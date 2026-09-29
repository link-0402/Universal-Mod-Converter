using System.Globalization;
using System.Text.RegularExpressions;

namespace UniversalModConverter.Core;

/// <summary>One stored backup: where it is, and when it was taken.</summary>
public readonly record struct BackupFolder(string Path, DateTime WrittenUtc);

/// <summary>
/// Decides which backups have outlived their usefulness. Kept separate from the file system
/// so the rule can be tested: deleting the wrong folder here loses the only untouched copy
/// of somebody's mod.
/// </summary>
public static partial class BackupRetention
{
    private const string TakenFormat = "yyyyMMdd-HHmmss";

    /// <summary>
    /// The name of a backup of <paramref name="folder"/> taken at <paramref name="takenUtc"/>:
    /// "MyMod-20260925-135557-db2702d3". The name is the backup's only reliable date, which
    /// <see cref="TakenUtc"/> reads back.
    /// </summary>
    /// <param name="id">Hex digits that tell backups taken in the same second apart; the first eight are used.</param>
    public static string FolderName(string folder, DateTime takenUtc, string id)
        => $"{folder}-{takenUtc.ToString(TakenFormat, CultureInfo.InvariantCulture)}-{id[..8]}";

    /// <summary>
    /// When the backup named <paramref name="folderName"/> was taken, or null when the folder is
    /// not one of ours. Backups are made by moving the mod folder, and a move keeps the folder's
    /// own timestamp however old the mod is, so the file system cannot say; and a custom backup
    /// directory may hold anything else besides.
    /// </summary>
    public static DateTime? TakenUtc(string folderName)
    {
        var match = BackupNameRegex().Match(folderName);
        return match.Success && DateTime.TryParseExact(match.Groups["taken"].Value, TakenFormat, CultureInfo.InvariantCulture,
            DateTimeStyles.AssumeUniversal | DateTimeStyles.AdjustToUniversal, out var taken)
            ? taken
            : null;
    }

    [GeneratedRegex(@".-(?<taken>\d{8}-\d{6})-[0-9a-f]{8}$", RegexOptions.CultureInvariant)]
    private static partial Regex BackupNameRegex();

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
