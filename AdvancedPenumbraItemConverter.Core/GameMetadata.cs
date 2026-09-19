using System.Buffers.Binary;
using System.Numerics;
using System.Text.Json.Nodes;

namespace AdvancedPenumbraItemConverter.Core;

/// <summary>Read access to vanilla game files (by game path).</summary>
public interface IGameFileProvider
{
    byte[]? ReadFile(string gamePath);

    bool FileExists(string gamePath) => ReadFile(gamePath) != null;
}

/// <summary>A game file provider with no data, used when game data is unavailable.</summary>
public sealed class NoGameFiles : IGameFileProvider
{
    public static NoGameFiles Instance { get; } = new();
    public byte[]? ReadFile(string gamePath) => null;
}

/// <summary>One IMC entry, with the same field split Penumbra uses in its JSON.</summary>
public readonly record struct ImcEntry(
    byte MaterialId, byte DecalId, ushort AttributeMask, byte SoundId, byte VfxId, byte MaterialAnimationId)
{
    public JsonObject ToJson() => new()
    {
        ["MaterialId"] = MaterialId,
        ["DecalId"] = DecalId,
        ["VfxId"] = VfxId,
        ["MaterialAnimationId"] = MaterialAnimationId,
        ["AttributeMask"] = AttributeMask,
        ["SoundId"] = SoundId,
    };

    public static ImcEntry? FromJson(JsonNode? node)
    {
        if (node is not JsonObject obj) return null;
        return new ImcEntry(
            (byte)Json.GetInt(obj["MaterialId"], 0),
            (byte)Json.GetInt(obj["DecalId"], 0),
            (ushort)(Json.GetInt(obj["AttributeMask"], 0) & 0x3FF),
            (byte)(Json.GetInt(obj["SoundId"], 0) & 0x3F),
            (byte)Json.GetInt(obj["VfxId"], 0),
            (byte)Json.GetInt(obj["MaterialAnimationId"], 0));
    }
}

/// <summary>Parsers for the vanilla metadata tables Penumbra manipulates.</summary>
public static class GameMetadata
{
    public const string EqpFile = "chara/xls/equipmentparameter/equipmentparameter.eqp";
    public const string GmpFile = "chara/xls/equipmentparameter/gimmickparameter.gmp";

    /// <summary>Penumbra's default EQP entry for collapsed blocks.</summary>
    public const ulong DefaultEqpEntry = 0x3fe00070603f00UL;

    /// <summary>
    /// Returns IMC rows 1..N for one part. Row 0 is the variant-0 fallback row and is
    /// not a selectable item variant.
    /// </summary>
    public static IReadOnlyList<(ushort Variant, ImcEntry Entry)> ReadImc(ReadOnlySpan<byte> data, int partIndex)
    {
        var result = new List<(ushort, ImcEntry)>();
        if (data.Length < 4) return result;
        int count = BinaryPrimitives.ReadUInt16LittleEndian(data);
        int parts = BitOperations.PopCount(BinaryPrimitives.ReadUInt16LittleEndian(data[2..]));
        if (parts == 0 || partIndex < 0 || partIndex >= parts) return result;
        for (var row = 1; row <= count; row++)
        {
            var offset = 4 + (row * parts + partIndex) * 6;
            if (offset + 6 > data.Length) break;
            var packed = BinaryPrimitives.ReadUInt16LittleEndian(data[(offset + 2)..]);
            result.Add(((ushort)row, new ImcEntry(data[offset], data[offset + 1], (ushort)(packed & 0x3FF),
                (byte)(packed >> 10), data[offset + 4], data[offset + 5])));
        }
        return result;
    }

    /// <summary>
    /// Reads an entry from the block-compressed EQP/GMP layout: a 64-bit control word
    /// marks which 160-entry blocks are stored; missing blocks use <paramref name="fallback"/>.
    /// </summary>
    public static ulong ReadExpandedEntry(ReadOnlySpan<byte> data, ushort setId, ulong fallback)
    {
        const int blockSize = 160;
        if (data.Length < 8) return fallback;
        if (setId == 0) setId = 1;
        var block = setId / blockSize;
        if (block >= 64) return fallback;
        var control = BinaryPrimitives.ReadUInt64LittleEndian(data);
        var bit = 1UL << block;
        if ((control & bit) == 0) return fallback;
        var stored = BitOperations.PopCount(control & (bit - 1));
        var offset = (stored * blockSize + setId % blockSize) * 8;
        return offset + 8 <= data.Length ? BinaryPrimitives.ReadUInt64LittleEndian(data[offset..]) : fallback;
    }

    /// <summary>Reads the complete packed 16-bit EQDP entry for a set.</summary>
    public static ushort ReadEqdp(ReadOnlySpan<byte> data, ushort setId)
    {
        if (data.Length < 6) return 0;
        int blockSize = BinaryPrimitives.ReadUInt16LittleEndian(data[2..]);
        int blockCount = BinaryPrimitives.ReadUInt16LittleEndian(data[4..]);
        if (blockSize == 0) return 0;
        var block = setId / blockSize;
        if (block >= blockCount) return 0;
        var header = BinaryPrimitives.ReadUInt16LittleEndian(data[(6 + block * 2)..]);
        if (header == ushort.MaxValue) return 0;
        var offset = 6 + blockCount * 2 + header * 2 + setId % blockSize * 2;
        return offset + 2 <= data.Length ? BinaryPrimitives.ReadUInt16LittleEndian(data[offset..]) : (ushort)0;
    }

    /// <summary>Moves the two EQDP bits of one slot to another slot's position.</summary>
    public static ushort RepositionEqdp(ushort entry, GearSlot source, GearSlot target)
        => MetadataTransforms.RepositionEqdp(entry, source.EqdpShift(), target.EqdpShift());

    public static bool EqdpHasModel(ushort entry, GearSlot slot) => ((entry >> slot.EqdpShift()) & 2) != 0;

    public static ushort EqdpBits(ushort entry, GearSlot slot) => (ushort)((entry >> slot.EqdpShift()) & 3);

    /// <summary>Penumbra's JSON object form of a packed GMP entry.</summary>
    public static JsonObject GmpToJson(ulong value) => new()
    {
        ["Enabled"] = (value & 1) != 0,
        ["Animated"] = (value & 2) != 0,
        ["RotationA"] = (ushort)((value >> 2) & 0x3FF),
        ["RotationB"] = (ushort)((value >> 12) & 0x3FF),
        ["RotationC"] = (ushort)((value >> 22) & 0x3FF),
        ["UnknownA"] = (byte)((value >> 32) & 0xF),
        ["UnknownB"] = (byte)((value >> 36) & 0xF),
    };
}

/// <summary>Lenient helpers for Penumbra JSON, which may store numbers as strings.</summary>
public static class Json
{
    // JsonValue.TryGetValue<T> only succeeds for the exact CLR type a value was created
    // with, so numbers are read through their JSON text instead.
    private static string? NumberText(JsonNode? node) => node switch
    {
        JsonValue v when v.GetValueKind() == System.Text.Json.JsonValueKind.Number => v.ToJsonString(),
        JsonValue v when v.GetValueKind() == System.Text.Json.JsonValueKind.String => v.GetValue<string>(),
        _ => null,
    };

    public static bool TryGetInt(JsonNode? node, out int value)
    {
        value = 0;
        var text = NumberText(node);
        if (text == null) return false;
        if (int.TryParse(text, System.Globalization.NumberStyles.Integer,
                System.Globalization.CultureInfo.InvariantCulture, out value)) return true;
        return double.TryParse(text, System.Globalization.NumberStyles.Float,
                   System.Globalization.CultureInfo.InvariantCulture, out var d) &&
               d == Math.Floor(d) && d is >= int.MinValue and <= int.MaxValue &&
               (value = (int)d) == value;
    }

    public static int GetInt(JsonNode? node, int fallback) => TryGetInt(node, out var value) ? value : fallback;

    public static bool TryGetULong(JsonNode? node, out ulong value)
    {
        value = 0;
        var text = NumberText(node);
        return text != null && ulong.TryParse(text, System.Globalization.NumberStyles.Integer,
            System.Globalization.CultureInfo.InvariantCulture, out value);
    }

    public static string? GetString(JsonNode? node)
        => node is JsonValue v && v.GetValueKind() == System.Text.Json.JsonValueKind.String
            ? v.GetValue<string>()
            : null;

    public static bool TryGetBool(JsonNode? node, out bool value)
    {
        value = false;
        if (node is not JsonValue v) return false;
        switch (v.GetValueKind())
        {
            case System.Text.Json.JsonValueKind.True: value = true; return true;
            case System.Text.Json.JsonValueKind.False: return true;
            default: return false;
        }
    }

    public static bool StringEquals(JsonNode? node, string expected)
        => string.Equals(GetString(node), expected, StringComparison.OrdinalIgnoreCase);

    /// <summary>Writes a number back in the representation (number or string) the original used.</summary>
    public static JsonNode SameKindNumber(JsonNode? original, long value)
        => original is JsonValue v && v.GetValueKind() == System.Text.Json.JsonValueKind.String
            ? JsonValue.Create(value.ToString(System.Globalization.CultureInfo.InvariantCulture))
            : JsonValue.Create(value);
}
