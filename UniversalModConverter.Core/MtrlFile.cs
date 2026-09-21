using System.Buffers.Binary;
using System.Text;

namespace UniversalModConverter.Core;

/// <summary>
/// Minimal lossless MTRL string-block rewriter. The data following the string
/// block is preserved byte-for-byte; only string offsets, sizes, and requested
/// strings are rebuilt, matching the part of TexTools' MTRL save path needed by
/// dependency-root cloning.
/// </summary>
public static class MtrlFile
{
    private const int HeaderSize = 16;
    private const int Signature = 0x01030000;

    /// <summary>Returns the texture paths (the first string group) of a material.</summary>
    public static IReadOnlyList<string> ReadTexturePaths(byte[] input)
    {
        if (input.Length < HeaderSize) throw new InvalidDataException("MTRL header is truncated.");
        if (BinaryPrimitives.ReadInt32LittleEndian(input) != Signature)
            throw new InvalidDataException("Unsupported MTRL signature.");
        var stringBlockSize = BinaryPrimitives.ReadUInt16LittleEndian(input.AsSpan(8));
        var textureCount = input[12];
        var stringCount = textureCount + input[13] + input[14];
        var stringBlockStart = HeaderSize + stringCount * 4;
        var stringBlockEnd = stringBlockStart + stringBlockSize;
        if (stringBlockEnd > input.Length)
            throw new InvalidDataException("MTRL string block extends beyond the file.");
        var result = new List<string>(textureCount);
        for (var index = 0; index < textureCount; index++)
        {
            var offset = BinaryPrimitives.ReadInt16LittleEndian(input.AsSpan(HeaderSize + index * 4));
            if (offset < 0 || offset >= stringBlockSize)
                throw new InvalidDataException("MTRL string offset is outside the string block.");
            result.Add(ReadString(input, stringBlockStart + offset, stringBlockEnd));
        }
        return result;
    }

    public static byte[] RewritePaths(byte[] input, IReadOnlyDictionary<string, string> replacements)
    {
        if (input.Length < HeaderSize) throw new InvalidDataException("MTRL header is truncated.");
        if (BinaryPrimitives.ReadInt32LittleEndian(input) != Signature)
            throw new InvalidDataException("Unsupported MTRL signature.");

        var stringBlockSize = BinaryPrimitives.ReadUInt16LittleEndian(input.AsSpan(8));
        var shaderNameOffset = BinaryPrimitives.ReadUInt16LittleEndian(input.AsSpan(10));
        var textureCount = input[12];
        var mapCount = input[13];
        var colorSetCount = input[14];
        var stringCount = checked(textureCount + mapCount + colorSetCount);
        var stringBlockStart = checked(HeaderSize + stringCount * 4);
        var stringBlockEnd = checked(stringBlockStart + stringBlockSize);
        if (stringBlockEnd > input.Length)
            throw new InvalidDataException("MTRL string block extends beyond the file.");
        if (shaderNameOffset >= stringBlockSize)
            throw new InvalidDataException("MTRL shader-name offset is outside the string block.");

        var strings = new string[stringCount];
        for (var index = 0; index < stringCount; index++)
        {
            var offset = BinaryPrimitives.ReadInt16LittleEndian(input.AsSpan(HeaderSize + index * 4));
            if (offset < 0 || offset >= stringBlockSize)
                throw new InvalidDataException("MTRL string offset is outside the string block.");
            strings[index] = ReadString(input, stringBlockStart + offset, stringBlockEnd);
        }
        var shaderName = ReadString(input, stringBlockStart + shaderNameOffset, stringBlockEnd);

        using var stringStream = new MemoryStream();
        using (var writer = new BinaryWriter(stringStream, Encoding.UTF8, leaveOpen: true))
        {
            var offsets = new short[stringCount];
            for (var index = 0; index < strings.Length; index++)
            {
                offsets[index] = checked((short)stringStream.Position);
                WriteString(writer, Replace(strings[index], replacements));
            }
            var newShaderNameOffset = checked((ushort)stringStream.Position);
            WriteString(writer, Replace(shaderName, replacements));
            while ((stringStream.Length & 3) != 0) writer.Write((byte)0);

            if (stringStream.Length > ushort.MaxValue)
                throw new InvalidDataException("Rewritten MTRL string block is too large.");

            var newLength = checked(stringBlockStart + (int)stringStream.Length + input.Length - stringBlockEnd);
            if (newLength > ushort.MaxValue)
                throw new InvalidDataException("Rewritten MTRL exceeds the format's file-size limit.");

            var output = new byte[newLength];
            input.AsSpan(0, stringBlockStart).CopyTo(output);
            stringStream.GetBuffer().AsSpan(0, (int)stringStream.Length).CopyTo(output.AsSpan(stringBlockStart));
            input.AsSpan(stringBlockEnd).CopyTo(output.AsSpan(stringBlockStart + (int)stringStream.Length));

            BinaryPrimitives.WriteUInt16LittleEndian(output.AsSpan(4), checked((ushort)newLength));
            BinaryPrimitives.WriteUInt16LittleEndian(output.AsSpan(8), checked((ushort)stringStream.Length));
            BinaryPrimitives.WriteUInt16LittleEndian(output.AsSpan(10), newShaderNameOffset);
            for (var index = 0; index < offsets.Length; index++)
                BinaryPrimitives.WriteInt16LittleEndian(output.AsSpan(HeaderSize + index * 4), offsets[index]);
            return output;
        }
    }

    private static string ReadString(byte[] input, int start, int end)
    {
        var terminator = start;
        while (terminator < end && input[terminator] != 0) terminator++;
        if (terminator == end) throw new InvalidDataException("MTRL string is unterminated.");
        return Encoding.UTF8.GetString(input, start, terminator - start);
    }

    private static void WriteString(BinaryWriter writer, string value)
    {
        writer.Write(Encoding.UTF8.GetBytes(value));
        writer.Write((byte)0);
    }

    private static string Replace(string value, IReadOnlyDictionary<string, string> replacements)
        => replacements.TryGetValue(value, out var replacement) ? replacement : value;
}
