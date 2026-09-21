using Dalamud.Configuration;
using System;
using System.Collections.Generic;
using UniversalModConverter.Core;

namespace UniversalModConverter;

[Serializable]
public class Configuration : IPluginConfiguration
{
    public const int CurrentVersion = 3;

    public int Version { get; set; } = CurrentVersion;

    /// <summary>Last-used mod directory path (the folder containing meta.json).</summary>
    public string LastModDirectory { get; set; } = string.Empty;

    /// <summary>Mesh groups tab: the player's character shows the model without the unticked groups and parts.</summary>
    public bool MeshPreviewOnCharacter { get; set; }

    /// <summary>Where Apply writes: a new mod, this mod in place, or added to this mod.</summary>
    public ConversionOutputMode OutputMode { get; set; } = ConversionOutputMode.NewMod;

    /// <summary>
    /// Replaced by <see cref="OutputMode"/> in version 3. Kept so an older configuration file
    /// still deserializes, and migrated once.
    /// </summary>
    [Obsolete("Use OutputMode.")]
    public bool CreateNewMod { get; set; } = true;

    /// <summary>Last-used new mod name (used as folder name and Penumbra display name).</summary>
    public string LastNewModName { get; set; } = string.Empty;

    /// <summary>Ask for confirmation before converting a mod in place.</summary>
    public bool ConfirmInPlace { get; set; } = true;

    /// <summary>Show fingerprints, bone resolutions and other diagnostics in the plan view.</summary>
    public bool ShowAdvancedDetails { get; set; }

    /// <summary>Width of the mod browser pane, in unscaled pixels.</summary>
    public float ModBrowserWidth { get; set; } = 250f;

    /// <summary>
    /// Where in-place originals and reverted outputs are kept. Empty means the default: the
    /// system temp folder, or a hidden .umc-backups folder beside the mods when temp is on
    /// another volume (publishing moves whole directories, which cannot cross volumes).
    /// </summary>
    public string BackupDirectory { get; set; } = string.Empty;

    /// <summary>How long a backup is kept before it is deleted automatically.</summary>
    public int BackupRetentionDays { get; set; } = 14;

    /// <summary>How many backups are kept at once, however recent they are.</summary>
    public int BackupRetentionCount { get; set; } = 10;

    /// <summary>Delete expired backups on startup and after each conversion.</summary>
    public bool PruneBackupsAutomatically { get; set; } = true;

    /// <summary>Most recent conversions first; used to revert them.</summary>
    public List<ConversionRecord> History { get; set; } = new();

    public void Migrate()
    {
        if (Version >= CurrentVersion) return;
        // v1 -> v2: new settings take their defaults; nothing to convert.
        // v2 -> v3: the two-valued CreateNewMod flag became a three-valued OutputMode. Backups
        // written under the old root are still found through the history records that point at
        // them, so they are pruned rather than stranded.
        if (Version < 3)
        {
#pragma warning disable CS0618
            OutputMode = CreateNewMod ? ConversionOutputMode.NewMod : ConversionOutputMode.InPlace;
#pragma warning restore CS0618
        }

        Version = CurrentVersion;
        Save();
    }

    public void Save()
    {
        Plugin.PluginInterface.SavePluginConfig(this);
    }
}

/// <summary>A published conversion that can be reverted.</summary>
[Serializable]
public class ConversionRecord
{
    public Guid Id { get; set; } = Guid.NewGuid();
    public DateTime TimestampUtc { get; set; } = DateTime.UtcNow;
    public ConversionOutputMode Mode { get; set; }

    /// <summary>Human-readable summary, e.g. "Body e0164 → e0200".</summary>
    public string Description { get; set; } = string.Empty;

    public string SourceModName { get; set; } = string.Empty;

    /// <summary>The mod that was converted (in place) or copied from (new mod).</summary>
    public string SourceModDirectory { get; set; } = string.Empty;

    /// <summary>The directory the conversion wrote: the new mod, or the source mod in place.</summary>
    public string PublishedPath { get; set; } = string.Empty;

    /// <summary>In place only: the untouched original, kept in the backup folder.</summary>
    public string? RecoveryPath { get; set; }

    public DateTime? RevertedUtc { get; set; }

    /// <summary>Where the reverted output was moved to, so a revert is never a hard delete.</summary>
    public string? RevertedOutputPath { get; set; }

    /// <summary>When this conversion's backup expired and was deleted, so revert can say so.</summary>
    public DateTime? BackupPrunedUtc { get; set; }

    /// <summary>
    /// One line per conversion when several were run together; empty for a single conversion.
    /// A run is reverted as a whole, so this is a record of what it contained, not a list of
    /// separately revertable things.
    /// </summary>
    public List<string> Entries { get; set; } = new();

    public bool IsReverted => RevertedUtc.HasValue;
}
