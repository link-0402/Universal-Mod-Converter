using System;
using System.Collections.Generic;
using System.Collections.Immutable;
using System.Linq;
using AdvancedPenumbraModConverter.Core;
using AdvancedPenumbraModConverter.Services;

namespace AdvancedPenumbraModConverter.Session;

/// <summary>Target selection for animations: swapping idles and emotes, and retargeting between races.</summary>
public sealed partial class ConverterSession
{
    public AnimationOperation AnimationOperation { get; private set; } = AnimationOperation.Swap;

    /// <summary>Idle swap: build a single-select group with one option per chosen slot.</summary>
    public bool AnimationAsGroup { get; private set; }

    /// <summary>Idle swap, plain replacement: the destination slot, or -1.</summary>
    public int AnimationTargetSlot { get; private set; } = -1;

    /// <summary>Idle swap into a group: the slots to offer.</summary>
    public IReadOnlySet<int> AnimationGroupSlots => _animationGroupSlots;
    private readonly SortedSet<int> _animationGroupSlots = [];

    public string AnimationGroupName { get; private set; } = string.Empty;

    /// <summary>In place, plain replacement: keep the source slot as well.</summary>
    public bool AnimationKeepOriginal { get; private set; }

    /// <summary>Emote swap: the destination emote, or 0.</summary>
    public uint AnimationTargetEmote { get; private set; }

    public ushort AnimationSourceRace { get; private set; }

    public IReadOnlySet<ushort> AnimationTargetRaces => _animationTargetRaces;
    private readonly SortedSet<ushort> _animationTargetRaces = [];

    private bool _emotesLoading;

    /// <summary>The slots of the selected idle's family, in slot order.</summary>
    public IReadOnlyList<IdleSlot> AnimationSlots
        => Source?.Animation is { Kind: AnimationSourceKind.Idle, Family: { } family }
            ? GameData.Animations.IdleSlots(family)
            : [];

    /// <summary>All emotes with body animations, or null while they are being read.</summary>
    public IReadOnlyList<EmoteInfo>? AnimationEmotes
    {
        get
        {
            if (GameData.Animations.EmotesReady) return GameData.Animations.Emotes;
            if (!_emotesLoading)
            {
                _emotesLoading = true;
                System.Threading.Tasks.Task.Run(() => _ = GameData.Animations.Emotes);
            }
            return null;
        }
    }

    public bool CanSwapAnimation => Source?.Animation is { Kind: AnimationSourceKind.Idle or AnimationSourceKind.Emote };

    private void ResetAnimationTarget(AnimationSource source)
    {
        AnimationOperation = source.Kind == AnimationSourceKind.Other ? AnimationOperation.Retarget : AnimationOperation.Swap;
        AnimationAsGroup = false;
        AnimationTargetSlot = -1;
        AnimationKeepOriginal = false;
        AnimationTargetEmote = 0;
        _animationGroupSlots.Clear();
        _animationTargetRaces.Clear();
        AnimationSourceRace = source.Races.Contains((ushort)101) ? (ushort)101 : source.Races.FirstOrDefault();
        AnimationGroupName = source.Family is { } family
            ? $"{IdleSlots.GetFamily(family)?.Label ?? "Idle"} slot"
            : "Animation slot";
        if (source.Family != null)
            foreach (var slot in GameData.Animations.IdleSlots(source.Family)) _animationGroupSlots.Add(slot.Index);
    }

    public void SetAnimationOperation(AnimationOperation operation)
    {
        if (operation == AnimationOperation || operation == AnimationOperation.Swap && !CanSwapAnimation) return;
        AnimationOperation = operation;
        MarkDirty();
    }

    public void SetAnimationAsGroup(bool value)
    {
        if (value == AnimationAsGroup) return;
        AnimationAsGroup = value;
        MarkDirty();
    }

    public void SetAnimationTargetSlot(int index)
    {
        if (index == AnimationTargetSlot) return;
        AnimationTargetSlot = index;
        MarkDirty();
    }

    public void SetAnimationGroupSlot(int index, bool included)
    {
        if (included ? !_animationGroupSlots.Add(index) : !_animationGroupSlots.Remove(index)) return;
        MarkDirty();
    }

    public void SetAnimationGroupSlots(IEnumerable<int> slots)
    {
        _animationGroupSlots.Clear();
        _animationGroupSlots.UnionWith(slots);
        MarkDirty();
    }

    public void SetAnimationGroupName(string name)
    {
        if (name == AnimationGroupName) return;
        AnimationGroupName = name;
        MarkDirty();
    }

    public void SetAnimationKeepOriginal(bool keep)
    {
        if (keep == AnimationKeepOriginal) return;
        AnimationKeepOriginal = keep;
        MarkDirty();
    }

    public void SetAnimationTargetEmote(uint id)
    {
        if (id == AnimationTargetEmote) return;
        AnimationTargetEmote = id;
        MarkDirty();
    }

    public void SetAnimationSourceRace(ushort race)
    {
        if (race == AnimationSourceRace || Source?.Animation is not { } source || !source.Races.Contains(race)) return;
        AnimationSourceRace = race;
        _animationTargetRaces.Remove(race);
        MarkDirty();
    }

    public void SetAnimationTargetRace(ushort race, bool included)
    {
        if (race == AnimationSourceRace && included) return;
        if (included ? !_animationTargetRaces.Add(race) : !_animationTargetRaces.Remove(race)) return;
        MarkDirty();
    }

    /// <summary>Why the animation inputs are incomplete, or null.</summary>
    private string? AnimationBlockReason(AnimationSource source)
    {
        if (AnimationOperation == AnimationOperation.Retarget)
        {
            if (!source.Races.Contains(AnimationSourceRace)) return "Choose the race to retarget from.";
            return _animationTargetRaces.Count == 0 ? "Choose at least one race to retarget to." : null;
        }

        switch (source.Kind)
        {
            case AnimationSourceKind.Idle when AnimationAsGroup:
                if (string.IsNullOrWhiteSpace(AnimationGroupName)) return "Enter a name for the option group.";
                return _animationGroupSlots.Count == 0 ? "Choose the slots the option group offers." : null;
            case AnimationSourceKind.Idle:
                if (AnimationTargetSlot < 0) return "Choose the slot the animation moves to.";
                return AnimationTargetSlot == source.SlotIndex ? "Choose a different slot than the current one." : null;
            case AnimationSourceKind.Emote:
                if (AnimationEmotes == null) return "Reading the emote list…";
                if (AnimationTargetEmote == 0) return "Choose the emote the animation moves to.";
                return AnimationTargetEmote == source.EmoteId ? "Choose a different emote than the current one." : null;
            default:
                return "Only idles and emotes can be swapped. Retarget this animation instead.";
        }
    }

    /// <summary>Builds the planner request from the current inputs; null when they are incomplete.</summary>
    private AnimationConversionRequest? BuildAnimationRequest(AnimationSource source)
    {
        if (AnimationBlockReason(source) != null) return null;
        var request = new AnimationConversionRequest(source.Locations, AnimationOperation, OutputMode, DescribeAnimation(source));
        if (AnimationOperation == AnimationOperation.Retarget)
            return request with { SourceRace = AnimationSourceRace, TargetRaces = [.. _animationTargetRaces] };

        if (source.Kind == AnimationSourceKind.Emote)
            return request with { Variants = [EmoteVariant(source)] };

        var slots = AnimationSlots;
        if (!AnimationAsGroup)
            return slots.FirstOrDefault(s => s.Index == AnimationTargetSlot) is { } target
                ? request with { Variants = [SlotVariant(source, target)], KeepOriginal = AnimationKeepOriginal }
                : null;

        var chosen = slots.Where(s => _animationGroupSlots.Contains(s.Index)).ToList();
        return request with
        {
            Variants = [.. chosen.Select(slot => SlotVariant(source, slot))],
            GroupName = AnimationGroupName.Trim(),
            DefaultVariant = Math.Max(0, chosen.FindIndex(s => s.Index == source.SlotIndex)),
        };
    }

    /// <summary>The source idle's loop moves to the slot's loop, and its start to the slot's start when both have one.</summary>
    private static AnimationSwapVariant SlotVariant(AnimationSource source, IdleSlot slot)
    {
        const string prefix = "a0001/bt_common/";
        var map = ImmutableDictionary.CreateBuilder<string, string>(StringComparer.Ordinal);
        foreach (var location in source.Locations)
        {
            if (!IdleSlots.TryDescribe(location[prefix.Length..], out _, out _, out var isStart)) continue;
            if (!isStart) map[location] = prefix + slot.LoopKey;
            else if (slot.StartKey != null) map[location] = prefix + slot.StartKey;
        }
        return new AnimationSwapVariant(slot.Label, map.ToImmutable());
    }

    /// <summary>An emote's animations pair with the destination's by their position in the emote.</summary>
    private AnimationSwapVariant EmoteVariant(AnimationSource source)
    {
        var from = GameData.Animations.FindEmote(source.EmoteId);
        var to = GameData.Animations.FindEmote(AnimationTargetEmote);
        var map = ImmutableDictionary.CreateBuilder<string, string>(StringComparer.Ordinal);
        if (from != null && to != null)
        {
            var provided = from.Timelines.Where(t => source.Locations.Contains(t.Location)).ToList();
            foreach (var timeline in provided)
                if (to.Timelines.FirstOrDefault(t => t.Index == timeline.Index) is { } match)
                    map[timeline.Location] = match.Location;
            // Emotes with one animation each pair them regardless of position.
            if (map.Count == 0 && provided.Count == 1 && to.Timelines.Length == 1)
                map[provided[0].Location] = to.Timelines[0].Location;
        }
        return new AnimationSwapVariant(to == null ? "Emote" : $"/{to.Name}", map.ToImmutable());
    }

    private string DescribeAnimation(AnimationSource source)
    {
        if (AnimationOperation == AnimationOperation.Retarget)
            return $"{source.Label}: {RaceLabel(AnimationSourceRace)} → " +
                   string.Join(", ", _animationTargetRaces.Select(RaceLabel));
        if (source.Kind == AnimationSourceKind.Emote)
            return $"{source.Label} → /{GameData.Animations.FindEmote(AnimationTargetEmote)?.Name ?? "?"}";
        if (AnimationAsGroup)
            return $"{source.Label} → option group '{AnimationGroupName.Trim()}' ({_animationGroupSlots.Count} slots)";
        return $"{source.Label} → {IdleSlots.SlotLabel(source.Family ?? string.Empty, AnimationTargetSlot)}";
    }

    /// <summary>Short label for the default new mod name.</summary>
    private string AnimationNameLabel(AnimationSource source)
    {
        if (AnimationOperation == AnimationOperation.Retarget)
            return _animationTargetRaces.Count == 1 ? RaceLabel(_animationTargetRaces.First()) : "Retargeted";
        if (source.Kind == AnimationSourceKind.Emote)
            return GameData.Animations.EmotesReady && GameData.Animations.FindEmote(AnimationTargetEmote) is { } emote
                ? $"as /{emote.Name}"
                : "Swapped";
        if (AnimationAsGroup) return "Slot options";
        return AnimationTargetSlot < 0 ? "Swapped" : IdleSlots.SlotLabel(source.Family ?? string.Empty, AnimationTargetSlot);
    }
}
