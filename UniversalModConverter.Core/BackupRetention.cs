namespace UniversalModConverter.Core;

/// <summary>One stored backup: where it is, and when it was taken.</summary>
public readonly record struct BackupFolder(string Path, DateTime WrittenUtc);

/// <summary>
/// Decides which backups have outlived their usefulness. Kept separate from the file system
/// so the rule can be tested: deleting the wrong folder here loses the only untouched copy
/// of somebody's mod.
/// </summary>
public static class BackupRetention
{
    /// <summary>
    /// The backups to delete, newest-first order preserved. Two independent caps apply: a
    /// backup older than <paramref name="keepDays"/> goes, and so does one beyond the newest
    /// <paramref name="keepCount"/>, so neither a long-idle install nor a busy afternoon can
    /// let the folder grow without bound. A path in <paramref name="protectedPaths"/> is never
    /// returned, however old or numerous — those are the backups a revert still depends on.
    /// </summary>
    public static List<string> Expired(IEnumerable<BackupFolder> folders, IReadOnlySet<string> protectedPaths,
        int keepDays, int keepCount, DateTime nowUtc)
    {
        var cutoff = nowUtc - TimeSpan.FromDays(Math.Max(1, keepDays));
        var limit  = Math.Max(1, keepCount);
        var expired = new List<string>();
        var kept = 0;

        foreach (var folder in folders.OrderByDescending(f => f.WrittenUtc).ThenBy(f => f.Path, StringComparer.OrdinalIgnoreCase))
        {
            if (protectedPaths.Contains(folder.Path))
            {
                // A protected backup still occupies one of the kept slots, so the cap counts
                // total backups rather than only the ones we are free to delete.
                kept++;
                continue;
            }

            if (kept < limit && folder.WrittenUtc >= cutoff)
            {
                kept++;
                continue;
            }

            expired.Add(folder.Path);
        }

        return expired;
    }
}
