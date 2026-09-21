using System;
using System.Collections.Generic;
using AdvancedPenumbraModConverter.Core;

namespace AdvancedPenumbraModConverter.Models;

/// <summary>
/// One conversion waiting to be run together with the others. It carries its own
/// <see cref="ConversionTask"/> because that is already the shape the planners take their
/// inputs in, so queueing a conversion is exactly the same work as previewing one.
/// </summary>
public sealed class QueuedConversion
{
    public Guid Id { get; } = Guid.NewGuid();

    public AssetKind Kind { get; init; } = AssetKind.Gear;

    /// <summary>What the queue row says: "Body e0164 → e0200".</summary>
    public string Description { get; init; } = string.Empty;

    /// <summary>The icon and name of what is being converted, for the summary.</summary>
    public ConversionSide Source { get; init; } = new();

    public ConversionSide Target { get; init; } = new();

    /// <summary>Unchecked entries stay in the list but are left out of the run.</summary>
    public bool Enabled { get; set; } = true;

    public ConversionTask Task { get; init; } = new();

    /// <summary>This entry's own diagnostics, including why it was rejected.</summary>
    public List<PlanDiagnostic> Diagnostics { get; } = new();

    /// <summary>The plan this entry produced, or null when planning it threw.</summary>
    public IModFilePlan? Plan { get; set; }

    /// <summary>True when the entry overlapped another and was left out of the run.</summary>
    public bool Rejected { get; set; }

    public bool HasBlockers => Diagnostics.Exists(d => d.IsBlocker);

    /// <summary>Clears what the last preview produced, keeping what the user chose.</summary>
    public void ResetPlan()
    {
        Diagnostics.Clear();
        Plan = null;
        Rejected = false;
    }
}

/// <summary>One end of a queued conversion, as the summary shows it.</summary>
public sealed class ConversionSide
{
    /// <summary>Game icon ID, or 0 when the kind has no icon and a glyph stands in for it.</summary>
    public uint Icon { get; init; }

    public string Name { get; init; } = string.Empty;

    /// <summary>The short identifier under the name: "e0164", "Midlander Male", "/Lean".</summary>
    public string Detail { get; init; } = string.Empty;
}
