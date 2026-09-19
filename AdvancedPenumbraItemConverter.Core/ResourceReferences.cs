using System.Buffers.Binary;
using System.Text;
using System.Text.RegularExpressions;

namespace AdvancedPenumbraItemConverter.Core;

/// <summary>
/// Reads and rewrites the resource references stored inside game files. The game only
/// follows three kinds of edges for gear: MDL → material names, MTRL → texture paths, and
/// AVFX → texture/model paths.
/// </summary>
public static partial class ResourceReferences
{
    private const int MdlHeaderSize = 0x44;

    [GeneratedRegex(@"^/?mt_c\d{4}b\d{4}", RegexOptions.IgnoreCase | RegexOptions.CultureInvariant)]
    private static partial Regex SkinMaterialRegex();

    /// <summary>Skin materials (mt_cXXXXbYYYY) resolve to the character body, never to the gear root.</summary>
    public static bool IsSkinMaterial(string materialName) => SkinMaterialRegex().IsMatch(materialName);

    public static bool CanContainReferences(string path)
    {
        var extension = Path.GetExtension(path);
        return extension.Equals(".mdl", StringComparison.OrdinalIgnoreCase) ||
               extension.Equals(".mtrl", StringComparison.OrdinalIgnoreCase) ||
               extension.Equals(".avfx", StringComparison.OrdinalIgnoreCase);
    }

    /// <summary>Returns the references a file of the given type holds.</summary>
    public static IReadOnlyList<string> Read(string pathOrExtension, byte[] data)
    {
        var extension = Path.GetExtension(pathOrExtension);
        if (extension.Equals(".mdl", StringComparison.OrdinalIgnoreCase)) return ReadMdlMaterials(data);
        if (extension.Equals(".mtrl", StringComparison.OrdinalIgnoreCase)) return MtrlFile.ReadTexturePaths(data);
        if (extension.Equals(".avfx", StringComparison.OrdinalIgnoreCase)) return BinaryPathRewriter.ExtractPaths(data);
        return [];
    }

    /// <summary>Replaces exact references; returns the input array when nothing changed.</summary>
    public static byte[] Rewrite(string pathOrExtension, byte[] data, IReadOnlyDictionary<string, string> replacements)
    {
        var relevant = Read(pathOrExtension, data)
            .Where(r => replacements.TryGetValue(r, out var target) && !string.Equals(r, target, StringComparison.Ordinal))
            .Distinct(StringComparer.Ordinal)
            .ToDictionary(r => r, r => replacements[r], StringComparer.Ordinal);
        if (relevant.Count == 0) return data;

        var extension = Path.GetExtension(pathOrExtension);
        if (extension.Equals(".mdl", StringComparison.OrdinalIgnoreCase)) return RewriteMdlStrings(data, relevant);
        if (extension.Equals(".mtrl", StringComparison.OrdinalIgnoreCase)) return MtrlFile.RewritePaths(data, relevant);
        return BinaryPathRewriter.Rewrite(data, relevant);
    }

    /// <summary>
    /// Material names an MDL (v5 or v6) references, read through its material offset table.
    /// Falls back to every *.mtrl string when the layout cannot be followed.
    /// </summary>
    public static IReadOnlyList<string> ReadMdlMaterials(byte[] data)
    {
        var table = ReadMdlStringTable(data);
        if (TryLocateMaterialOffsets(data, table, out var position, out var count))
        {
            var byOffset = StringsByOffset(table);
            var result = new List<string>(count);
            for (var i = 0; i < count; i++)
            {
                var offset = table.DataStart + (int)BinaryPrimitives.ReadUInt32LittleEndian(data.AsSpan(position + i * 4));
                if (byOffset.TryGetValue(offset, out var name)) result.Add(name);
            }
            return result.Distinct(StringComparer.Ordinal).ToArray();
        }

        return table.Strings
            .Where(s => s.EndsWith(".mtrl", StringComparison.OrdinalIgnoreCase))
            .Distinct(StringComparer.Ordinal)
            .ToArray();
    }

    /// <summary>
    /// Rewrites MDL material names for MDL v5 and v6. Same-length edits are written in place.
    /// Length changes keep every existing string where it is, append the new names to the
    /// string table and repoint only the material offsets; every absolute offset behind the
    /// string table moves by the same 16-byte aligned amount.
    /// </summary>
    public static byte[] RewriteMdlStrings(byte[] data, IReadOnlyDictionary<string, string> replacements)
    {
        var table = ReadMdlStringTable(data);
        var sameLength = replacements.All(r =>
            Encoding.UTF8.GetByteCount(r.Key) == Encoding.UTF8.GetByteCount(r.Value));
        if (sameLength)
        {
            var output = data.ToArray();
            for (var i = 0; i < table.Strings.Count; i++)
            {
                if (!replacements.TryGetValue(table.Strings[i], out var replacement)) continue;
                Encoding.UTF8.GetBytes(replacement).CopyTo(output, table.Offsets[i]);
            }
            return output;
        }

        if (!TryLocateMaterialOffsets(data, table, out var materialPosition, out var materialCount))
            throw new InvalidDataException("The MDL layout cannot be followed for a length-changing material rename.");
        var lodStart = LodTableStart(data, table);
        for (var l = 0; l < 3; l++)
            if (BinaryPrimitives.ReadUInt32LittleEndian(data.AsSpan(lodStart + l * LodSize + 36)) != 0)
                throw new InvalidDataException("MDL files with edge geometry only support same-length material renames.");

        // Append each new name once, then pad so every later structure keeps its alignment.
        using var appended = new MemoryStream();
        var newOffsets = new Dictionary<string, uint>(StringComparer.Ordinal);
        foreach (var value in replacements.Values.Distinct(StringComparer.Ordinal))
        {
            newOffsets[value] = (uint)(table.StringsEnd - table.DataStart + appended.Length);
            appended.Write(Encoding.UTF8.GetBytes(value));
            appended.WriteByte(0);
        }
        while (appended.Length % 16 != 0) appended.WriteByte(0);
        var delta = (int)appended.Length;
        var oldDataOffset = MdlHeaderSize + (long)BinaryPrimitives.ReadUInt32LittleEndian(data.AsSpan(4)) +
                            BinaryPrimitives.ReadUInt32LittleEndian(data.AsSpan(8));
        // Insert directly after the last string so count-based readers see the new names
        // before any original padding.
        var tableEnd = table.StringsEnd;

        var result = new byte[data.Length + delta];
        data.AsSpan(0, tableEnd).CopyTo(result);
        appended.ToArray().CopyTo(result, tableEnd);
        data.AsSpan(tableEnd).CopyTo(result.AsSpan(tableEnd + delta));

        BinaryPrimitives.WriteUInt16LittleEndian(result.AsSpan(table.TableStart),
            checked((ushort)(table.Strings.Count + newOffsets.Count)));
        BinaryPrimitives.WriteUInt32LittleEndian(result.AsSpan(table.TableStart + 4), (uint)(table.Size + delta));
        Add(8);                                                            // runtime size
        for (var i = 0; i < 6; i++) ShiftBufferOffset(16 + i * 4);          // vertex and index buffer offsets
        for (var l = 0; l < 3; l++)
        {
            ShiftBufferOffset(lodStart + delta + l * LodSize + 52);
            ShiftBufferOffset(lodStart + delta + l * LodSize + 56);
        }

        var byOffset = StringsByOffset(table);
        for (var i = 0; i < materialCount; i++)
        {
            var at = materialPosition + delta + i * 4;
            var relative = BinaryPrimitives.ReadUInt32LittleEndian(result.AsSpan(at));
            if (byOffset.TryGetValue(table.DataStart + (int)relative, out var name) &&
                replacements.TryGetValue(name, out var replacement))
                BinaryPrimitives.WriteUInt32LittleEndian(result.AsSpan(at), newOffsets[replacement]);
        }
        return result;

        void Add(int position)
            => BinaryPrimitives.WriteUInt32LittleEndian(result.AsSpan(position),
                BinaryPrimitives.ReadUInt32LittleEndian(result.AsSpan(position)) + (uint)delta);

        void ShiftBufferOffset(int position)
        {
            var value = BinaryPrimitives.ReadUInt32LittleEndian(result.AsSpan(position));
            if (value != 0 && value >= oldDataOffset) Add(position);
        }
    }

    private const int ModelHeaderSize = 56;
    private const int LodSize = 60;

    private static Dictionary<int, string> StringsByOffset(MdlStringTable table)
    {
        var result = new Dictionary<int, string>();
        for (var i = 0; i < table.Offsets.Count; i++) result.TryAdd(table.Offsets[i], table.Strings[i]);
        return result;
    }

    private static int LodTableStart(byte[] data, MdlStringTable table)
    {
        var model = table.DataStart + table.Size;
        var elementIds = BinaryPrimitives.ReadUInt16LittleEndian(data.AsSpan(model + 24));
        return model + ModelHeaderSize + elementIds * 32;
    }

    /// <summary>Follows the model header to the material-name offset table (same layout in v5 and v6).</summary>
    private static bool TryLocateMaterialOffsets(byte[] data, MdlStringTable table, out int position, out int count)
    {
        position = count = 0;
        var model = table.DataStart + table.Size;
        if (model + ModelHeaderSize > data.Length) return false;
        var meshes = BinaryPrimitives.ReadUInt16LittleEndian(data.AsSpan(model + 4));
        var attributes = BinaryPrimitives.ReadUInt16LittleEndian(data.AsSpan(model + 6));
        var submeshes = BinaryPrimitives.ReadUInt16LittleEndian(data.AsSpan(model + 8));
        count = BinaryPrimitives.ReadUInt16LittleEndian(data.AsSpan(model + 10));
        var terrainMeshes = data[model + 26];
        var flags2 = data[model + 27];
        var terrainSubmeshes = BinaryPrimitives.ReadUInt16LittleEndian(data.AsSpan(model + 38));
        position = LodTableStart(data, table) + 3 * LodSize + ((flags2 & 0x10) != 0 ? 3 * 40 : 0) +
                   meshes * 36 + attributes * 4 + terrainMeshes * 20 + submeshes * 16 + terrainSubmeshes * 12;
        if (count == 0 || position + (long)count * 4 > data.Length) return false;

        // Every entry must name a material string, or the layout guess is wrong.
        var byOffset = StringsByOffset(table);
        for (var i = 0; i < count; i++)
        {
            var offset = table.DataStart + (long)BinaryPrimitives.ReadUInt32LittleEndian(data.AsSpan(position + i * 4));
            if (offset > int.MaxValue || !byOffset.TryGetValue((int)offset, out var name) ||
                !name.EndsWith(".mtrl", StringComparison.OrdinalIgnoreCase))
                return false;
        }
        return true;
    }

    private sealed record MdlStringTable(IReadOnlyList<string> Strings, IReadOnlyList<int> Offsets,
        int TableStart, int DataStart, int Size, int StringsEnd);

    private static MdlStringTable ReadMdlStringTable(byte[] data)
    {
        if (data.Length < MdlHeaderSize) throw new InvalidDataException("MDL header is truncated.");
        var version = BinaryPrimitives.ReadUInt32LittleEndian(data);
        if (version is not (MdlFile.Version5 or MdlFile.Version6))
            throw new InvalidDataException($"Unsupported MDL version 0x{version:X8}.");
        var stackSize = BinaryPrimitives.ReadUInt32LittleEndian(data.AsSpan(4));
        var tableStart = MdlHeaderSize + (long)stackSize;
        if (tableStart + 8 > data.Length) throw new InvalidDataException("MDL string table is outside the file.");
        var count = BinaryPrimitives.ReadUInt16LittleEndian(data.AsSpan((int)tableStart));
        var size = BinaryPrimitives.ReadUInt32LittleEndian(data.AsSpan((int)tableStart + 4));
        var dataStart = (int)tableStart + 8;
        if (dataStart + (long)size > data.Length) throw new InvalidDataException("MDL string table is truncated.");

        var strings = new List<string>(count);
        var offsets = new List<int>(count);
        var position = dataStart;
        var end = dataStart + (int)size;
        for (var i = 0; i < count && position < end; i++)
        {
            var terminator = Array.IndexOf(data, (byte)0, position, end - position);
            if (terminator < 0) throw new InvalidDataException("MDL string table is unterminated.");
            strings.Add(Encoding.UTF8.GetString(data, position, terminator - position));
            offsets.Add(position);
            position = terminator + 1;
        }
        return new MdlStringTable(strings, offsets, (int)tableStart, dataStart, (int)size, position);
    }
}
