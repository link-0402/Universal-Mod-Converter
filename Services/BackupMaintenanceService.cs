using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text.Json;
using UniversalModConverter.Core;
using Dalamud.Plugin.Services;

namespace UniversalModConverter.Services;

/// <summary>
/// Keeps the backup folder from growing without bound, and finishes conversions the plugin
/// died in the middle of.
/// <para>
/// Every in-place conversion parks a full copy of the mod, and so does every revert. Nothing
/// used to remove them, and the history list caps at thirty records, so the thirty-first
/// backup lost its only pointer and stayed on disk forever. Backups now expire by age and by
/// count, and a backup that is still the one thing standing between the user and a lost mod
/// is never touched.
/// </para>
/// </summary>
public sealed class BackupMaintenanceService(
    Configuration configuration,
    ConversionHistoryService history,
    IPluginLog log)
{
    /// <summary>
    /// A staging folder younger than this may belong to a conversion running right now, in
    /// this session or another one, so it is left alone.
    /// </summary>
    private static readonly TimeSpan OrphanGrace = TimeSpan.FromHours(1);

    public sealed record Usage(long Bytes, int Folders);

    /// <param name="PrunedRecords">Records whose backup was deleted; the caller marks them.</param>
    public sealed record SweepResult(int Folders, long Bytes, IReadOnlyList<Guid> PrunedRecords)
    {
        public static SweepResult Empty { get; } = new(0, 0, []);
    }

    /// <summary>Every folder backups may live in: the configured root plus roots history points at.</summary>
    public IReadOnlyList<string> Roots(string? penumbraRoot)
    {
        var roots = new List<string>();
        void Add(string? path)
        {
            if (string.IsNullOrWhiteSpace(path)) return;
            var full = Path.GetFullPath(path).TrimEnd('\\', '/');
            if (Directory.Exists(full) && !roots.Contains(full, StringComparer.OrdinalIgnoreCase)) roots.Add(full);
        }

        if (!string.IsNullOrWhiteSpace(penumbraRoot))
            try { Add(ModConverterService.BackupRoot(penumbraRoot!, configuration.BackupDirectory, create: false)); }
            catch (Exception ex) { log.Warning(ex, "[UMC] Could not resolve the backup root."); }

        // A backup written before the root moved, or under a Penumbra directory that is not
        // the current one, is still ours to clean up.
        foreach (var record in configuration.History)
        {
            Add(Parent(record.RecoveryPath));
            Add(Parent(record.RevertedOutputPath));
        }

        return roots;
    }

    public Usage Measure(string? penumbraRoot)
    {
        long bytes = 0;
        var folders = 0;
        foreach (var folder in Roots(penumbraRoot).SelectMany(Backups))
        {
            folders++;
            bytes += SizeOf(folder);
        }

        return new Usage(bytes, folders);
    }

    /// <summary>
    /// Deletes backups past the age and count limits. Safe to call off the framework thread;
    /// the caller applies <see cref="SweepResult.PrunedRecords"/> and saves the configuration.
    /// </summary>
    public SweepResult Sweep(string? penumbraRoot)
    {
        // A backup the user could still revert to outlives every limit: losing it would lose
        // the only untouched copy of their mod.
        var protectedPaths = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (var record in configuration.History.Where(r => history.RevertBlockReason(r) == null))
            if (!string.IsNullOrEmpty(record.RecoveryPath))
                protectedPaths.Add(Path.GetFullPath(record.RecoveryPath).TrimEnd('\\', '/'));

        var deleted = new List<string>();
        long bytes = 0;
        foreach (var root in Roots(penumbraRoot))
        {
            var folders = Backups(root).Select(folder => new BackupFolder(folder, WrittenUtc(folder)));
            foreach (var folder in BackupRetention.Expired(folders, protectedPaths,
                         configuration.BackupRetentionDays, configuration.BackupRetentionCount, DateTime.UtcNow))
            {
                var size = SizeOf(folder);
                if (!TryDelete(folder)) continue;
                deleted.Add(folder);
                bytes += size;
            }
        }

        if (deleted.Count == 0) return SweepResult.Empty;
        log.Information("[UMC] Removed {0} expired backup folder(s), {1}.", deleted.Count, Describe(bytes));

        var pruned = configuration.History
            .Where(record => Matches(record.RecoveryPath, deleted) || Matches(record.RevertedOutputPath, deleted))
            .Select(record => record.Id)
            .ToList();
        return new SweepResult(deleted.Count, bytes, pruned);
    }

    /// <summary>
    /// Cleans up what a crash left beside the mods: staging copies that were never swapped in,
    /// and recovery journals. A journal whose mod folder is gone means the plugin died between
    /// the two moves of an in-place conversion, so the original is put back.
    /// </summary>
    public int SweepOrphans(string penumbraRoot)
    {
        if (string.IsNullOrWhiteSpace(penumbraRoot) || !Directory.Exists(penumbraRoot)) return 0;
        var handled = 0;
        var now = DateTime.UtcNow;

        foreach (var journal in SafeEnumerateFiles(penumbraRoot, ".*.umc-recovery-*.json"))
        {
            try
            {
                if (now - File.GetLastWriteTimeUtc(journal) < OrphanGrace) continue;
                using var document = JsonDocument.Parse(File.ReadAllText(journal));
                var root = document.RootElement;
                var source = root.TryGetProperty("Source", out var s) ? s.GetString() : null;
                var backup = root.TryGetProperty("Backup", out var b) ? b.GetString() : null;

                if (!string.IsNullOrEmpty(source) && !Directory.Exists(source) &&
                    !string.IsNullOrEmpty(backup) && Directory.Exists(backup))
                {
                    Directory.Move(backup, source);
                    log.Warning("[UMC] An interrupted conversion was rolled back: restored {0}.", source);
                }

                File.Delete(journal);
                handled++;
            }
            catch (Exception ex)
            {
                log.Warning(ex, "[UMC] Could not process the recovery journal {0}.", journal);
            }
        }

        foreach (var stage in SafeEnumerateDirectories(penumbraRoot, ".*.umc-stage-*")
                     .Concat(SafeEnumerateDirectories(penumbraRoot, ".*.umc-failed-*")))
        {
            if (now - Directory.GetLastWriteTimeUtc(stage) < OrphanGrace) continue;
            if (!TryDelete(stage)) continue;
            log.Information("[UMC] Removed the leftover staging folder {0}.", stage);
            handled++;
        }

        return handled;
    }

    public static string Describe(long bytes) => bytes switch
    {
        >= 1L << 30 => $"{bytes / (double)(1L << 30):0.0} GB",
        >= 1L << 20 => $"{bytes / (double)(1L << 20):0.0} MB",
        >= 1L << 10 => $"{bytes / (double)(1L << 10):0.0} KB",
        _           => $"{bytes} bytes",
    };

    // ── Helpers ──────────────────────────────────────────────────────────────

    private static IEnumerable<string> Backups(string root)
        => SafeEnumerateDirectories(root, "*").Select(folder => folder.TrimEnd('\\', '/'));

    private static bool Matches(string? path, List<string> deleted)
        => !string.IsNullOrEmpty(path) &&
           deleted.Contains(Path.GetFullPath(path).TrimEnd('\\', '/'), StringComparer.OrdinalIgnoreCase);

    private static string? Parent(string? path)
        => string.IsNullOrWhiteSpace(path) ? null : Path.GetDirectoryName(Path.GetFullPath(path).TrimEnd('\\', '/'));

    /// <summary>When the backup was taken. The folder timestamp is what a move preserves.</summary>
    private static DateTime WrittenUtc(string folder)
    {
        try { return Directory.GetLastWriteTimeUtc(folder); }
        catch (Exception) { return DateTime.MinValue; }
    }

    private static long SizeOf(string folder)
    {
        try
        {
            return new DirectoryInfo(folder)
                .EnumerateFiles("*", SearchOption.AllDirectories)
                .Sum(file => file.Length);
        }
        catch (Exception) { return 0; }
    }

    private bool TryDelete(string folder)
    {
        try
        {
            Directory.Delete(folder, true);
            return true;
        }
        catch (Exception ex)
        {
            log.Warning(ex, "[UMC] Could not delete {0}.", folder);
            return false;
        }
    }

    private static IEnumerable<string> SafeEnumerateDirectories(string root, string pattern)
    {
        try { return Directory.EnumerateDirectories(root, pattern, SearchOption.TopDirectoryOnly).ToList(); }
        catch (Exception) { return []; }
    }

    private static IEnumerable<string> SafeEnumerateFiles(string root, string pattern)
    {
        try { return Directory.EnumerateFiles(root, pattern, SearchOption.TopDirectoryOnly).ToList(); }
        catch (Exception) { return []; }
    }
}
