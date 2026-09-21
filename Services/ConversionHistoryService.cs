using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using UniversalModConverter.Core;
using UniversalModConverter.Models;

namespace UniversalModConverter.Services;

/// <summary>
/// Remembers published conversions and reverts them. A revert itself never deletes anything:
/// the converted output is moved into the backup folder, and for in-place conversions the
/// original is moved back from there. Backups do expire afterwards, which
/// <see cref="BackupMaintenanceService"/> handles and <see cref="RevertBlockReason"/> reports.
/// </summary>
public sealed class ConversionHistoryService(Configuration configuration)
{
    private const int MaxRecords = 30;

    public IReadOnlyList<ConversionRecord> Records => configuration.History;

    public ConversionRecord Record(ConversionTask task, string description, string sourceModName)
    {
        var record = new ConversionRecord
        {
            Entries            = task.Entries.Where(e => !e.Rejected).Select(e => e.Description).ToList(),
            Mode               = task.OutputMode,
            Description        = description,
            SourceModName      = sourceModName,
            SourceModDirectory = Normalize(task.ModDirectory),
            PublishedPath      = Normalize(task.PublishedPath ?? task.ModDirectory),
            RecoveryPath       = task.OutputMode.EditsSourceMod() ? task.RecoveryPath : null,
        };
        configuration.History.Insert(0, record);
        if (configuration.History.Count > MaxRecords)
            configuration.History.RemoveRange(MaxRecords, configuration.History.Count - MaxRecords);
        configuration.Save();
        return record;
    }

    public ConversionRecord? Find(Guid id) => configuration.History.FirstOrDefault(r => r.Id == id);

    /// <summary>Returns null when <paramref name="record"/> can be reverted, otherwise why not.</summary>
    public string? RevertBlockReason(ConversionRecord record)
    {
        if (record.IsReverted) return "Already reverted.";
        if (!Directory.Exists(record.PublishedPath))
            return $"The converted mod no longer exists: {record.PublishedPath}";
        if (record.Mode.IsNewMod()) return null;

        if (string.IsNullOrEmpty(record.RecoveryPath) || !Directory.Exists(record.RecoveryPath))
            return record.BackupPrunedUtc.HasValue
                ? $"The backup of the original was cleaned up on {record.BackupPrunedUtc:yyyy-MM-dd}, " +
                  "so this conversion can no longer be undone."
                : "The backup of the original is missing, so this conversion can no longer be undone.";
        // Reverting an older conversion of this mod would silently discard every later one.
        var latest = configuration.History.FirstOrDefault(r =>
            r.Mode.EditsSourceMod() && !r.IsReverted &&
            string.Equals(r.SourceModDirectory, record.SourceModDirectory, StringComparison.OrdinalIgnoreCase));
        return latest == record ? null : "A later in-place conversion of this mod must be reverted first.";
    }

    /// <summary>
    /// Performs the file moves for a revert. Safe to call off the framework thread; the caller
    /// then calls <see cref="MarkReverted"/> and asks Penumbra to reload
    /// <see cref="RevertResult.PenumbraFolder"/> on the framework thread.
    /// </summary>
    public RevertResult Revert(ConversionRecord record, Action<string> log)
    {
        if (RevertBlockReason(record) is { } reason) return new RevertResult(false, reason, null, null);

        var published = Normalize(record.PublishedPath);
        var parent    = Path.GetDirectoryName(published);
        if (string.IsNullOrEmpty(parent)) return new RevertResult(false, "The converted mod has no parent directory.", null, null);

        var name     = Path.GetFileName(published);
        var parked   = Path.Combine(ModConverterService.BackupRoot(parent, configuration.BackupDirectory),
            $"{name}-reverted-{DateTime.UtcNow:yyyyMMdd-HHmmss}-{Guid.NewGuid().ToString("N")[..8]}");
        var moved    = false;
        try
        {
            Directory.Move(published, parked);
            moved = true;
            log($"Moved converted output to {parked}");

            if (record.Mode.EditsSourceMod())
            {
                Directory.Move(record.RecoveryPath!, published);
                log($"Restored the original mod from {record.RecoveryPath}");
            }

            var message = record.Mode.IsNewMod()
                ? $"Removed '{name}'. A copy was kept at {parked}."
                : $"Restored the original '{name}'. The converted version was kept at {parked}.";
            return new RevertResult(true, message, name, parked);
        }
        catch (Exception ex)
        {
            // Put the converted output back if the original could not take its place.
            if (moved && !Directory.Exists(published))
                try { Directory.Move(parked, published); } catch { /* reported below */ }
            log($"[ERROR] Revert failed: {ex.Message}");
            return new RevertResult(false, $"Revert failed: {ex.Message}", null, null);
        }
    }

    public void MarkReverted(ConversionRecord record, RevertResult result)
    {
        if (!result.Success) return;
        record.RevertedUtc        = DateTime.UtcNow;
        record.RevertedOutputPath = result.ParkedPath;
        configuration.Save();
    }

    /// <summary>Notes that the backups of these conversions expired, so Revert explains itself.</summary>
    public void MarkBackupsPruned(IReadOnlyList<Guid> ids)
    {
        if (ids.Count == 0) return;
        var now = DateTime.UtcNow;
        foreach (var record in configuration.History.Where(r => ids.Contains(r.Id) && !r.BackupPrunedUtc.HasValue))
            record.BackupPrunedUtc = now;
        configuration.Save();
    }

    private static string Normalize(string path)
        => string.IsNullOrEmpty(path) ? path : Path.GetFullPath(path).TrimEnd('\\', '/');
}

/// <param name="PenumbraFolder">Folder name under the Penumbra root to reload, when the revert succeeded.</param>
/// <param name="ParkedPath">Where the converted output was moved to.</param>
public sealed record RevertResult(bool Success, string Message, string? PenumbraFolder, string? ParkedPath);
