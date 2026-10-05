using System;
using System.Collections.Generic;
using System.Linq;
using UniversalModConverter.Core;
using UniversalModConverter.Models;
using UniversalModConverter.Services;

namespace UniversalModConverter.Session;

/// <summary>
/// Converting several things in one pass. The queue holds conversions the user has locked in;
/// while it is non-empty, Preview and Apply work on it and the From/To cards only decide what
/// gets added next. That keeps "what will happen" in one place instead of splitting it between
/// the queue and a half-configured live selection.
/// </summary>
public sealed partial class ConverterSession
{
    private readonly List<QueuedConversion> _queue = [];

    public IReadOnlyList<QueuedConversion> Queue => _queue;

    /// <summary>
    /// The queued conversions the next run converts: the ticked ones. Whatever belongs to the run
    /// as a whole (its output mode, the option group it adds, its default name) follows these
    /// alone, or an unticked entry would change how the ticked ones are written.
    /// </summary>
    private IEnumerable<QueuedConversion> RunEntries => _queue.Where(e => e.Enabled);

    /// <summary>Why the current selection cannot be queued, or null.</summary>
    public string? EnqueueBlockReason
    {
        get
        {
            if (SelectionBlockReason is { } reason) return reason;
            if (Source is not { } source) return "Select a source item first.";
            // Hair, face, tail, ear and skin conversions (retextures included) run on their own:
            // most patch files on disk instead of producing a file plan, so they cannot be merged.
            if (_queue.FirstOrDefault(e => CustomizationKinds.IsCustomization(e.Kind)) is { } lone)
                return $"The plan holds a {CustomizationKinds.Get(lone.Kind).DisplayName.ToLowerInvariant()} conversion, " +
                       "which has to run on its own. Convert it first, or remove it.";
            if (source.IsCustomization && _queue.Count > 0)
                return $"{CustomizationKinds.Get(source.Kind).DisplayName} conversions have to run on their own. " +
                       "Clear the plan first.";
            // Choosing the version here would add the same expression a second time, overlapping the first.
            if (source.Animation is { } animation && ExpressionAtSource(animation) &&
                _queue.Any(e => NeedsSourceChoice(e) && e.Task.AnimationRequest!.SourceLocations.SequenceEqual(animation.Locations)))
                return "This expression is already in the plan without a version chosen; choose it there, under \"Take it from\".";
            var description = Describe(source);
            if (_queue.Any(e => e.Description == description))
                return "This conversion is already in the plan.";
            return null;
        }
    }

    /// <summary>
    /// Whether a queued animation still needs the version a new option group uses, which none was
    /// chosen for when it was added to the plan because the output mode was another then: an
    /// expression attached where the animation is, added to this mod, or an idle that now plays
    /// in more than one slot (slot groups), while several options have their own version of it.
    /// </summary>
    public bool NeedsSourceChoice(QueuedConversion entry)
        => entry.SourceChoices.Length > 1 && entry.Task.AnimationRequest is { SourceContainer: null } request &&
           (EffectiveOutputMode == ConversionOutputMode.AddToMod && request.ExpressionAtSource ||
            request.ForMode(PlanOutputMode).InSlotGroups);

    /// <summary>Chooses the version a queued expression's option group uses; see <see cref="NeedsSourceChoice"/>.</summary>
    public void ChooseQueueEntrySource(Guid id, AnimationProvider provider)
    {
        if (_queue.FirstOrDefault(e => e.Id == id) is not { } entry || !NeedsSourceChoice(entry) ||
            !entry.SourceChoices.Contains(provider) || entry.Task.AnimationRequest is not { } request)
            return;
        var suffix = VersionSuffix(provider.Label);
        entry.Task.AnimationRequest = request with
        {
            SourceContainer = provider.Address,
            Description     = request.Description + suffix,
        };
        entry.Description += suffix;
        MarkPlanDirty();
    }

    /// <summary>Locks the current selection in and leaves the cards free for the next one.</summary>
    public void EnqueueCurrent()
    {
        if (EnqueueBlockReason != null || Source is not { } source) return;
        var task = BuildTask(source);
        if (task.Kind == AssetKind.Animation && task.AnimationRequest == null) return;

        _queue.Add(new QueuedConversion
        {
            Kind          = task.Kind,
            Description   = Describe(source),
            Source        = SideOf(source),
            Target        = TargetSide(source),
            Task          = task,
            // The output mode may still change to one that puts the expression, or the idle's slots,
            // into an option group.
            SourceChoices = source.Animation is { HasVariants: true } animation &&
                            (ExpressionAtSource(animation) || animation.Kind == AnimationSourceKind.Idle)
                ? animation.Providers
                : [],
        });
        MarkPlanDirty();
    }

    public void RemoveFromQueue(Guid id)
    {
        if (_queue.RemoveAll(e => e.Id == id) > 0) MarkPlanDirty();
    }

    public void SetQueueEntryEnabled(Guid id, bool enabled)
    {
        if (_queue.FirstOrDefault(e => e.Id == id) is not { } entry || entry.Enabled == enabled) return;
        entry.Enabled = enabled;
        MarkPlanDirty();
    }

    public void ClearQueue()
    {
        if (_queue.Count == 0) return;
        _queue.Clear();
        MarkPlanDirty();
    }

    /// <summary>The whole run in one line, for the log and the history record.</summary>
    public string DescribeQueue()
    {
        var enabled = _queue.Where(e => e.Enabled).Select(e => e.Description).ToList();
        return enabled.Count switch
        {
            0 => "nothing",
            <= 3 => string.Join(" + ", enabled),
            _ => string.Join(" + ", enabled.Take(3)) + $" + {enabled.Count - 3} more",
        };
    }

    /// <summary>The source item as the summary shows it.</summary>
    private static ConversionSide SideOf(DetectedItem source) => new()
    {
        Icon   = source.Icon,
        Kind   = source.Kind,
        Name   = source.ItemName,
        Detail = source.Animation is { } animation
            ? animation.Races.Length == 1 ? RaceLabel(animation.Races[0]) : $"{animation.Races.Length} races"
            : source.IsCustomization
                ? RaceLabel(source.GenderRace ?? 0)
                : $"{(source.IsAccessory ? 'a' : 'e')}{source.ModelIdDisplay}",
    };

    /// <summary>What the current inputs would convert the source into, as the summary shows it.</summary>
    private ConversionSide TargetSide(DetectedItem source)
    {
        if (source.Animation is { } animation)
            return new ConversionSide
            {
                Kind   = AssetKind.Animation,
                Name   = AnimationNameLabel(animation),
                Detail = AnimationTargetDetail(animation),
            };
        if (source.IsCustomization)
            return source.CanFanOut
                ? new ConversionSide
                {
                    Kind   = TargetCustomizationKind,
                    Name   = CustomizationKinds.Get(TargetCustomizationKind).DisplayName,
                    Detail = DescribeTextureTargets(),
                }
                : new ConversionSide
                {
                    Kind   = TargetCustomizationKind,
                    Name   = $"{CustomizationKinds.Get(TargetCustomizationKind).DisplayName} {TargetCustomizationId:D4}",
                    Detail = RaceLabel(TargetRace),
                };
        return TargetItem is { } item
            ? new ConversionSide
            {
                Icon   = item.Icon,
                Kind   = source.Kind,
                Name   = item.Name,
                Detail = $"{(item.IsAccessory ? 'a' : 'e')}{item.ModelIdDisplay}",
            }
            : new ConversionSide { Kind = source.Kind, Name = "?" };
    }
}
