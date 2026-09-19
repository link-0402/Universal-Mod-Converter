using System.Security.Cryptography;
using System.Text;

namespace AdvancedPenumbraItemConverter.Core;

public static class ModFingerprint
{
    public static string Compute(string root, IEnumerable<string>? files = null)
    {
        var canonicalRoot = Path.GetFullPath(root)
            .TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar);
        var candidates = files ?? Directory.EnumerateFiles(canonicalRoot, "*", SearchOption.AllDirectories);

        using var hash = IncrementalHash.CreateHash(HashAlgorithmName.SHA256);
        foreach (var file in candidates.Select(Path.GetFullPath)
                     .Distinct(StringComparer.OrdinalIgnoreCase)
                     .OrderBy(p => p, StringComparer.OrdinalIgnoreCase))
        {
            PathSafety.EnsureContained(canonicalRoot, file, requireExisting: true);
            var relative = Path.GetRelativePath(canonicalRoot, file).Replace('\\', '/').ToLowerInvariant();
            hash.AppendData(Encoding.UTF8.GetBytes(relative));
            hash.AppendData([0]);
            using var stream = new FileStream(file, FileMode.Open, FileAccess.Read, FileShare.Read);
            var buffer = new byte[128 * 1024];
            int read;
            while ((read = stream.Read(buffer, 0, buffer.Length)) > 0)
                hash.AppendData(buffer.AsSpan(0, read));
            hash.AppendData([0xff]);
        }

        return Convert.ToHexString(hash.GetHashAndReset()).ToLowerInvariant();
    }

    public static string ComputePlan(IEnumerable<ConversionOperation> operations)
    {
        var lines = operations.OrderBy(o => o.Category, StringComparer.Ordinal)
            .ThenBy(o => o.SourcePath, StringComparer.OrdinalIgnoreCase)
            .ThenBy(o => o.TargetPath, StringComparer.OrdinalIgnoreCase)
            .Select(o => $"{o.Category}\0{o.SourcePath}\0{o.TargetPath}\0{o.Required}");
        return Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(string.Join('\n', lines))))
            .ToLowerInvariant();
    }
}
