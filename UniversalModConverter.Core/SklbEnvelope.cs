using System.Buffers.Binary;

namespace UniversalModConverter.Core;

/// <summary>Validates an FFXIV SKLB container and extracts its embedded Havok payload.</summary>
public static class SklbEnvelope
{
    private const uint Magic = 0x736B6C62;
    private const ushort Version12 = 0x3132;
    private const ushort Version13 = 0x3133;

    public static int GetHavokOffset(ReadOnlySpan<byte> bytes)
    {
        if (bytes.Length < 8 || BinaryPrimitives.ReadUInt32LittleEndian(bytes) != Magic)
            throw new InvalidDataException("Invalid SKLB magic or truncated header.");

        var version = BinaryPrimitives.ReadUInt16LittleEndian(bytes[6..]);
        var offset = version switch
        {
            Version12 when bytes.Length >= 12 => BinaryPrimitives.ReadUInt16LittleEndian(bytes[10..]),
            Version13 when bytes.Length >= 16 => BinaryPrimitives.ReadInt32LittleEndian(bytes[12..]),
            Version12 or Version13 => throw new InvalidDataException("SKLB header is truncated."),
            _ => throw new InvalidDataException($"Unsupported SKLB container version 0x{version:X4}."),
        };
        if (offset <= 0 || offset >= bytes.Length)
            throw new InvalidDataException("SKLB Havok offset is outside the file.");
        return offset;
    }

    public static byte[] ExtractHavok(ReadOnlySpan<byte> bytes)
        => bytes[GetHavokOffset(bytes)..].ToArray();
}

/// <summary>Reader for the game's compact extra-skeleton template tables.</summary>
public static class ExtraSkeletonTable
{
    public static bool TryGet(ReadOnlySpan<byte> bytes, ushort race, ushort setId, out ushort skeletonId)
    {
        skeletonId = 0;
        if (bytes.Length < 4) throw new InvalidDataException("EST header is truncated.");
        var count = BinaryPrimitives.ReadUInt32LittleEndian(bytes);
        if (count > 1_000_000 || 4UL + count * 6UL > (ulong)bytes.Length)
            throw new InvalidDataException("EST entry table is truncated or invalid.");
        var skeletonBase = checked(4 + (int)count * 4);
        for (var index = 0; index < count; index++)
        {
            var keyOffset = checked(4 + (int)index * 4);
            if (BinaryPrimitives.ReadUInt16LittleEndian(bytes[keyOffset..]) != setId ||
                BinaryPrimitives.ReadUInt16LittleEndian(bytes[(keyOffset + 2)..]) != race)
                continue;
            skeletonId = BinaryPrimitives.ReadUInt16LittleEndian(bytes[(skeletonBase + (int)index * 2)..]);
            return true;
        }
        return false;
    }
}
