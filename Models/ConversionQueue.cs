using System;
using System.Collections.Generic;
using System.Collections.Immutable;
using UniversalModConverter.Core;
using UniversalModConverter.Services;

namespace UniversalModConverter.Models;

/// <summary>
/// One conversion waiting to be run together with the others. It carries its own
/// <see cref="ConversionTask"/> because that is already the shape the planners take their
/// inputs in, so queueing a conversion is exactly the same work as previewing one.
/// </summary>
public sealed class QueuedConversion
{
    public QueuedConversion() { }

    private QueuedConversion(Guid id) => Id = id;

    public Guid Id { get; } = Guid.NewGuid();

    public AssetKind Kind { get; init; } = AssetKind.Gear;

    /// <summary>
    /// Whether this is a fan-out: the source root replaces nothing but textures (or face or skin
    /// materials), whose paths are simply added for further races. Such a plan has output
    /// choices of its own (see <see cref="TextureFanOutLayout"/>).
    /// </summary>
    public bool CanFanOut => Task.TextureRequest != null;

    /// <summary>
    /// What the queue row says: "Body e0164 → e0200". Grows a " (the version in …)" when the
    /// version of an expression's animation is chosen on the row (see <see cref="SourceChoices"/>).
    /// </summary>
    public string Description { get; set; } = string.Empty;

    /// <summary>
    /// "Only add an expression" on an animation several options of the mod have their own version
    /// of: those versions. Added to this mod, the expression goes into an option group, which can
    /// hold only one of them; when none was chosen before the entry was added (it was then going
    /// into a new mod or converted in place), the plan row offers these.
    /// </summary>
    public ImmutableArray<AnimationProvider> SourceChoices { get; init; } = [];

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

    /// <summary>
    /// A copy for one preview to plan: the same choices and <see cref="Id"/>, with a task of its
    /// own. Planning fills in the copy off the framework thread, so it never changes the entry the
    /// queue panel is drawing.
    /// </summary>
    public QueuedConversion ForPlanning() => new(Id)
    {
        Kind          = Kind,
        Description   = Description,
        SourceChoices = SourceChoices,
        Source        = Source,
        Target        = Target,
        Enabled       = Enabled,
        Task          = Task.CloneInputs(),
    };

    /// <summary>Takes over what a preview found for this entry's copy. Framework thread only.</summary>
    public void TakeResults(QueuedConversion planned)
    {
        ResetPlan();
        Diagnostics.AddRange(planned.Diagnostics);
        Plan = planned.Plan;
        Rejected = planned.Rejected;
    }
}

/// <summary>One end of a queued conversion, as the summary shows it.</summary>
public sealed class ConversionSide
{
    /// <summary>Game icon ID, or 0 when the kind has no icon and a glyph stands in for it.</summary>
    public uint Icon { get; init; }

    /// <summary>What this end is, which picks the glyph when there is no icon.</summary>
    public AssetKind Kind { get; init; } = AssetKind.Gear;

    public string Name { get; init; } = string.Empty;

    /// <summary>The short identifier under the name: "e0164", "Midlander Male", "/Lean".</summary>
    public string Detail { get; init; } = string.Empty;
}
