using Dalamud.Configuration;
using System;
using System.Collections.Generic;
using AdvancedPenumbraItemConverter.Core;

namespace AdvancedPenumbraItemConverter;

[Serializable]
public class Configuration : IPluginConfiguration
{
    public const int CurrentVersion = 2;

    public int Version { get; set; } = CurrentVersion;

    /// <summary>Last-used mod directory path (the folder containing meta.json).</summary>
    public string LastModDirectory { get; set; } = string.Empty;

    /// <summary>Preview automatically once the inputs are complete and stop changing.</summary>
    public bool AutoRefreshPreview { get; set; } = true;

    /// <summary>When true, Apply creates a new mod instead of modifying in place.</summary>
    public bool CreateNewMod { get; set; } = true;

    /// <summary>Last-used new mod name (used as folder name and Penumbra display name).</summary>
    public string LastNewModName { get; set; } = string.Empty;

    /// <summary>Ask for confirmation before converting a mod in place.</summary>
    public bool ConfirmInPlace { get; set; } = true;

    /// <summary>Show fingerprints, bone resolutions and other diagnostics in the plan view.</summary>
    public bool ShowAdvancedDetails { get; set; }

    /// <summary>Width of the mod browser pane, in unscaled pixels.</summary>
    public float ModBrowserWidth { get; set; } = 250f;

    /// <summary>Most recent conversions first; used to revert them.</summary>
    public List<ConversionRecord> History { get; set; } = new();

    public void Migrate()
    {
        if (Version >= CurrentVersion) return;
        // v1 -> v2: new settings take their defaults; nothing to convert.
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

    /// <summary>In place only: the untouched original, kept in the hidden backup folder.</summary>
    public string? RecoveryPath { get; set; }

    public DateTime? RevertedUtc { get; set; }

    /// <summary>Where the reverted output was moved to, so a revert is never a hard delete.</summary>
    public string? RevertedOutputPath { get; set; }

    public bool IsReverted => RevertedUtc.HasValue;
}
