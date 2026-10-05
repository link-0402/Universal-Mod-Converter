using System.Buffers.Binary;
using System.Text;
using System.Text.RegularExpressions;

namespace UniversalModConverter.Core;

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

    /// <summary>Every string of an MDL's (v5 or v6) string table, in order: attribute, bone, material and shape names.</summary>
    public static IReadOnlyList<string> ReadMdlStrings(byte[] data) => ReadMdlStringTable(data).Strings;

    /// <summary>
    /// Rewrites MDL material names for MDL v5 and v6. Same-length edits are written in place.
    /// Length changes rebuild the string table: each replaced name takes the place of the name it
    /// replaces, so nothing of the old name is left behind, and every reference into the table
    /// (attribute, material, bone and shape names) is pointed at its string's new offset. Every
    /// absolute offset behind the string table moves by the same 16-byte aligned amount.
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

        if (!TryLocateMaterialOffsets(data, table, out _, out _) ||
            StringReferences(data, table) is not { } references)
            throw new InvalidDataException("The MDL layout cannot be followed for a length-changing material rename.");
        // Edge geometry (a LOD's size at +28, offset at +32; +36 is its polygon count) is not moved.
        var lodStart = LodTableStart(data, table);
        for (var l = 0; l < 3; l++)
            if (BinaryPrimitives.ReadUInt32LittleEndian(data.AsSpan(lodStart + l * LodSize + 28)) != 0)
                throw new InvalidDataException("MDL files with edge geometry only support same-length material renames.");

        // Every string in its order, the replaced ones under their new names.
        using var strings = new MemoryStream();
        var moved = new Dictionary<uint, uint>();
        for (var i = 0; i < table.Strings.Count; i++)
        {
            moved[(uint)(table.Offsets[i] - table.DataStart)] = (uint)strings.Length;
            strings.Write(Encoding.UTF8.GetBytes(replacements.GetValueOrDefault(table.Strings[i], table.Strings[i])));
            strings.WriteByte(0);
        }
        // The table keeps the padding it had after its strings, and grows or shrinks by whole
        // 16-byte steps so every later structure keeps its alignment.
        var padding = table.Size - (table.StringsEnd - table.DataStart);
        var grown = (int)strings.Length + padding - table.Size;
        var delta = (grown + 15) & ~15;
        var size = table.Size + delta;
        var oldDataOffset = MdlHeaderSize + (long)BinaryPrimitives.ReadUInt32LittleEndian(data.AsSpan(4)) +
                            BinaryPrimitives.ReadUInt32LittleEndian(data.AsSpan(8));

        var tableEnd = table.DataStart + table.Size;
        var result = new byte[data.Length + delta];
        data.AsSpan(0, table.DataStart).CopyTo(result);
        strings.ToArray().CopyTo(result, table.DataStart);
        data.AsSpan(tableEnd).CopyTo(result.AsSpan(tableEnd + delta));

        BinaryPrimitives.WriteUInt32LittleEndian(result.AsSpan(table.TableStart + 4), (uint)size);
        Add(8);                                                            // runtime size
        for (var i = 0; i < 6; i++) ShiftBufferOffset(16 + i * 4);          // vertex and index buffer offsets
        for (var l = 0; l < 3; l++)
        {
            ShiftBufferOffset(lodStart + delta + l * LodSize + 52);
            ShiftBufferOffset(lodStart + delta + l * LodSize + 56);
        }
        foreach (var position in references)
        {
            var at = position + delta;
            BinaryPrimitives.WriteUInt32LittleEndian(result.AsSpan(at), moved[BinaryPrimitives.ReadUInt32LittleEndian(result.AsSpan(at))]);
        }
        return result;

        void Add(int position)
            => BinaryPrimitives.WriteUInt32LittleEndian(result.AsSpan(position),
                (uint)(BinaryPrimitives.ReadUInt32LittleEndian(result.AsSpan(position)) + delta));

        void ShiftBufferOffset(int position)
        {
            var value = BinaryPrimitives.ReadUInt32LittleEndian(result.AsSpan(position));
            if (value != 0 && value >= oldDataOffset) Add(position);
        }
    }

    /// <summary>
    /// Where the model metadata names a string: the attribute, material and bone name offsets
    /// and each shape's name. Null when one of them does not land on a string, which means the
    /// layout was not followed correctly.
    /// </summary>
    private static List<int>? StringReferences(byte[] data, MdlStringTable table)
    {
        var model = table.DataStart + table.Size;
        if (model + ModelHeaderSize > data.Length) return null;
        ushort Count(int at) => BinaryPrimitives.ReadUInt16LittleEndian(data.AsSpan(model + at));
        var meshes = Count(4);
        var attributes = Count(6);
        var submeshes = Count(8);
        var materials = Count(10);
        var bones = Count(12);
        var boneTables = Count(14);
        var shapes = Count(16);
        var terrainMeshes = data[model + 26];
        var flags2 = data[model + 27];
        var terrainSubmeshes = Count(38);
        var boneTableWords = Count(44);

        var attributeStart = LodTableStart(data, table) + 3 * LodSize + ((flags2 & 0x10) != 0 ? 3 * 40 : 0) + meshes * 36;
        var materialStart = attributeStart + attributes * 4 + terrainMeshes * 20 + submeshes * 16 + terrainSubmeshes * 12;
        var boneStart = materialStart + materials * 4;
        // A v5 bone table is 64 bone indices, a count and padding; v6 packs the indices behind
        // an offset and count per table.
        var boneTableSize = BinaryPrimitives.ReadUInt32LittleEndian(data) == MdlFile.Version5
            ? boneTables * 132
            : boneTables * 4 + boneTableWords * 2;
        var shapeStart = boneStart + bones * 4 + boneTableSize;

        var positions = new List<int>();
        for (var i = 0; i < attributes; i++) positions.Add(attributeStart + i * 4);
        for (var i = 0; i < materials; i++) positions.Add(materialStart + i * 4);
        for (var i = 0; i < bones; i++) positions.Add(boneStart + i * 4);
        for (var i = 0; i < shapes; i++) positions.Add(shapeStart + i * 16);

        var starts = table.Offsets.Select(o => (long)(o - table.DataStart)).ToHashSet();
        foreach (var position in positions)
            if (position + 4 > data.Length || !starts.Contains(BinaryPrimitives.ReadUInt32LittleEndian(data.AsSpan(position))))
                return null;
        return positions;
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
