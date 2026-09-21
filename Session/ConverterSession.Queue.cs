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

    /// <summary>Why the current selection cannot be queued, or null.</summary>
    public string? EnqueueBlockReason
    {
        get
        {
            if (SelectionBlockReason is { } reason) return reason;
            if (Source is not { } source) return "Select a source item first.";
            // Hair, face, tail and ear conversions patch files on disk instead of producing a file
            // plan, so they cannot be merged with anything: they run as the plan's only entry.
            if (_queue.FirstOrDefault(e => CustomizationKinds.IsCustomization(e.Kind)) is { } lone)
                return $"The plan holds a {CustomizationKinds.Get(lone.Kind).DisplayName.ToLowerInvariant()} conversion, " +
                       "which has to run on its own. Convert it first, or remove it.";
            if (source.IsCustomization && _queue.Count > 0)
                return $"{CustomizationKinds.Get(source.Kind).DisplayName} conversions have to run on their own. " +
                       "Clear the plan first.";
            if (_queue.Any(e => e.Description == Describe(source)))
                return "This conversion is already in the plan.";
            return null;
        }
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
            IsTextureOnly = source.IsTextureOnly,
            Description   = Describe(source),
            Source        = SideOf(source),
            Target        = TargetSide(source),
            Task          = task,
        });
        if (OutputMode == ConversionOutputMode.AddToMod && AddToModBlockReason != null)
            SetOutputMode(ConversionOutputMode.NewMod);
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
                Name   = AnimationNameLabel(animation),
                Detail = AnimationOperation switch
                {
                    AnimationOperation.Retarget   => "Race retarget",
                    AnimationOperation.Expression => "Expression added",
                    _                             => "Animation swap",
                } + (AttachExpression && AnimationOperation != AnimationOperation.Expression ? " + expression" : ""),
            };
        if (source.IsCustomization)
            return source.IsTextureOnly
                ? new ConversionSide { Name = CustomizationKinds.Get(TargetCustomizationKind).DisplayName, Detail = DescribeTextureTargets() }
                : new ConversionSide
                {
                    Name   = $"{CustomizationKinds.Get(TargetCustomizationKind).DisplayName} {TargetCustomizationId:D4}",
                    Detail = RaceLabel(TargetRace),
                };
        return TargetItem is { } item
            ? new ConversionSide { Icon = item.Icon, Name = item.Name, Detail = $"{(item.IsAccessory ? 'a' : 'e')}{item.ModelIdDisplay}" }
            : new ConversionSide { Name = "?" };
    }
}
