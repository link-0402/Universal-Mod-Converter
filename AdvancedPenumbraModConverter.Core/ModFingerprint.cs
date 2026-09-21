using System.Security.Cryptography;
using System.Text;

namespace AdvancedPenumbraModConverter.Core;

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

    /// <param name="salt">
    /// Extra text mixed into the hash. Operations are sorted so the hash does not depend on the
    /// order they were planned in; a run made of several conversions uses this to say which
    /// conversions, in which order, produced them.
    /// </param>
    public static string ComputePlan(IEnumerable<ConversionOperation> operations, string? salt = null)
    {
        var lines = operations.OrderBy(o => o.Category, StringComparer.Ordinal)
            .ThenBy(o => o.SourcePath, StringComparer.OrdinalIgnoreCase)
            .ThenBy(o => o.TargetPath, StringComparer.OrdinalIgnoreCase)
            .Select(o => $"{o.Category}\0{o.SourcePath}\0{o.TargetPath}\0{o.Required}");
        if (salt != null) lines = lines.Append("salt\0" + salt);
        return Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(string.Join('\n', lines))))
            .ToLowerInvariant();
    }

    /// <summary>
    /// The fingerprint of a planned mod: every file operation plus the definition it produces.
    /// Shared so a gear plan, an animation plan and a merged run cannot drift apart.
    /// </summary>
    public static string ComputePlan(IReadOnlyList<PlannedFileOperation> files, PenumbraMod result, string? salt = null)
    {
        var operations = files
            .Select(f => new ConversionOperation(f.Operation.ToString(), f.Source ?? string.Empty,
                f.Destination + (f.Content == null ? string.Empty : "#" + Convert.ToHexString(SHA256.HashData(f.Content)))))
            .Append(new ConversionOperation("definition", "result", DefinitionHash(result)));
        return ComputePlan(operations, salt);
    }

    /// <summary>A hash of the mod definition a plan would write.</summary>
    public static string DefinitionHash(PenumbraMod mod)
    {
        var text = PenumbraMod.Serialize(mod.Meta) + PenumbraMod.Serialize(mod.Default.Node) +
                   string.Concat(mod.Groups.Select(g => PenumbraMod.Serialize(g.Node)));
        return Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(text)));
    }
}
