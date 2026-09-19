using System.Text;

namespace AdvancedPenumbraModConverter.Core;

/// <summary>
/// Rewrites only bounded, NUL-terminated ASCII resource-path strings. It deliberately
/// refuses length-changing edits because shifting an arbitrary binary section is unsafe.
/// MDL and MTRL callers use their format-aware writers instead.
/// </summary>
public static class BinaryPathRewriter
{
    private static readonly string[] SupportedExtensions =
        [".mdl", ".mtrl", ".tex", ".avfx", ".atex", ".sklb", ".pap", ".tmb"];

    public static IReadOnlyList<string> ExtractPaths(ReadOnlySpan<byte> bytes)
    {
        var result = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        var start = 0;
        while (start < bytes.Length)
        {
            while (start < bytes.Length && !IsPathByte(bytes[start])) start++;
            var end = start;
            while (end < bytes.Length && IsPathByte(bytes[end])) end++;
            if (end - start >= 6 && end < bytes.Length && bytes[end] == 0)
            {
                var value = Encoding.ASCII.GetString(bytes[start..end]);
                if (LooksLikeGamePath(value)) result.Add(value);
            }
            start = Math.Max(end + 1, start + 1);
        }
        return result.OrderBy(p => p, StringComparer.OrdinalIgnoreCase).ToArray();
    }

    public static byte[] Rewrite(byte[] input, IReadOnlyDictionary<string, string> replacements)
    {
        var output = input.ToArray();
        foreach (var source in ExtractPaths(input))
        {
            if (!replacements.TryGetValue(source, out var target) || source == target) continue;
            if (Encoding.ASCII.GetByteCount(source) != Encoding.ASCII.GetByteCount(target))
                throw new InvalidDataException($"Length-changing binary path rewrite is unsupported: '{source}' -> '{target}'.");
            ReplaceExact(output, Encoding.ASCII.GetBytes(source), Encoding.ASCII.GetBytes(target));
        }
        return output;
    }

    private static bool LooksLikeGamePath(string value)
        => value.Contains('/') && SupportedExtensions.Any(e => value.EndsWith(e, StringComparison.OrdinalIgnoreCase));

    private static bool IsPathByte(byte value)
        => value is >= 0x21 and <= 0x7e && value != (byte)'"' && value != (byte)'\'';

    private static void ReplaceExact(byte[] bytes, byte[] source, byte[] target)
    {
        for (var i = 0; i <= bytes.Length - source.Length; i++)
        {
            if (!bytes.AsSpan(i, source.Length).SequenceEqual(source)) continue;
            target.CopyTo(bytes, i);
            i += source.Length - 1;
        }
    }
}
