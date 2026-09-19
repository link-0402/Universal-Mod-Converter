namespace AdvancedPenumbraModConverter.Core;

public static class PathSafety
{
    public static string EnsureContained(string root, string candidate, bool requireExisting = false)
    {
        if (string.IsNullOrWhiteSpace(root) || string.IsNullOrWhiteSpace(candidate))
            throw new InvalidDataException("Root and candidate paths are required.");

        var canonicalRoot = Path.GetFullPath(root)
            .TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar);
        var canonical = Path.GetFullPath(candidate);
        var prefix = canonicalRoot + Path.DirectorySeparatorChar;
        if (!canonical.Equals(canonicalRoot, StringComparison.OrdinalIgnoreCase) &&
            !canonical.StartsWith(prefix, StringComparison.OrdinalIgnoreCase))
            throw new InvalidDataException($"Path escapes the authorized root: {candidate}");

        RejectReparsePoints(canonicalRoot, canonical);
        if (requireExisting && !File.Exists(canonical) && !Directory.Exists(canonical))
            throw new FileNotFoundException("Required path does not exist.", canonical);
        return canonical;
    }

    public static string ResolveRelative(string root, string relative)
    {
        if (Path.IsPathRooted(relative))
            throw new InvalidDataException($"Absolute mod-local path is not allowed: {relative}");
        return EnsureContained(root, Path.Combine(root, relative));
    }

    public static void ValidateNoCaseCollisions(IEnumerable<string> paths)
    {
        var seen = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        foreach (var path in paths.Select(Path.GetFullPath))
        {
            if (seen.TryGetValue(path, out var prior) && !string.Equals(prior, path, StringComparison.Ordinal))
                throw new IOException($"Case-insensitive destination collision: '{prior}' and '{path}'.");
            seen[path] = path;
        }
    }

    private static void RejectReparsePoints(string root, string candidate)
    {
        var relative = Path.GetRelativePath(root, candidate);
        var current = root;
        Check(current);
        foreach (var segment in relative.Split([Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar],
                     StringSplitOptions.RemoveEmptyEntries))
        {
            if (segment == "..")
                throw new InvalidDataException($"Path traversal is not allowed: {candidate}");
            current = Path.Combine(current, segment);
            if (File.Exists(current) || Directory.Exists(current))
                Check(current);
        }

        static void Check(string path)
        {
            if ((File.GetAttributes(path) & FileAttributes.ReparsePoint) != 0)
                throw new InvalidDataException($"Reparse points are not allowed in mod paths: {path}");
        }
    }
}
