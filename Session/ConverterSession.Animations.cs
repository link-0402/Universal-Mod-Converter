using System;
using System.Collections.Generic;
using System.Collections.Immutable;
using System.Linq;
using UniversalModConverter.Core;
using UniversalModConverter.Services;

namespace UniversalModConverter.Session;

/// <summary>
/// Target selection for animations: the slots an idle plays in, swapping emotes and expressions,
/// and retargeting between races.
/// </summary>
public sealed partial class ConverterSession
{
    /// <summary>What an emote, expression or other animation does. Idles choose with <see cref="AnimationOutputSlots"/> instead.</summary>
    public AnimationOperation AnimationOperation { get; private set; } = AnimationOperation.Swap;

    /// <summary>
    /// Idles: the slots the animation plays in once converted. Its current slot is one of them
    /// only while ticked: unticked, converting in place moves the animation away from it.
    /// </summary>
    public IReadOnlySet<int> AnimationOutputSlots => _animationOutputSlots;
    private readonly SortedSet<int> _animationOutputSlots = [];

    /// <summary>Idles: also retarget the animation to other races, in every slot it plays in.</summary>
    public bool AnimationAlsoRetargets { get; private set; }

    /// <summary>
    /// An expression added to this mod where the animation is (it goes into an option group),
    /// when several options hold their own version of the animation: the one whose version the
    /// group uses, or null until the user chooses.
    /// </summary>
    public ContainerAddress? AnimationSourceContainer { get; private set; }

    /// <summary>Whether the user has to choose <see cref="AnimationSourceContainer"/> for the selection.</summary>
    public bool NeedsAnimationSourceContainer
        => Source?.Animation is { HasVariants: true } animation && ExpressionAtSource(animation) &&
           EffectiveOutputMode == ConversionOutputMode.AddToMod;

    /// <summary>
    /// Whether the plan adds an expression to this mod where the animation is. That goes into an
    /// option group beside the original (see <see cref="AnimationConversionPlanner.ExpressionGroupName"/>).
    /// </summary>
    public bool AddsExpressionGroup
        => EffectiveOutputMode == ConversionOutputMode.AddToMod &&
           (_queue.Count > 0
               ? RunEntries.Any(e => e.Task.AnimationRequest?.ExpressionAtSource == true)
               : Source?.Animation is { } animation && ExpressionAtSource(animation));

    /// <summary>Emote swap: the destination emote, or 0.</summary>
    public uint AnimationTargetEmote { get; private set; }

    public ushort AnimationSourceRace { get; private set; }

    public IReadOnlySet<ushort> AnimationTargetRaces => _animationTargetRaces;
    private readonly SortedSet<ushort> _animationTargetRaces = [];

    /// <summary>
    /// Retargeting into a new mod: whether it holds the source race's animation too, as the
    /// source race's tick in the race list says. Only a new mod asks (see <see cref="AnimationSourceRaceStays"/>).
    /// </summary>
    public bool AnimationIncludesSourceRace { get; private set; } = true;

    /// <summary>
    /// Whether the source race keeps its animation, which is what its tick shows: adding to this
    /// mod always keeps it, converting in place moves it to the target races, and a new mod
    /// follows <see cref="AnimationIncludesSourceRace"/>.
    /// </summary>
    public bool AnimationSourceRaceStays => EffectiveOutputMode.KeepsSourceRace(AnimationIncludesSourceRace);

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

    /// <summary>The game's Expressions list, or null while it is being read (with the emotes).</summary>
    public IReadOnlyList<ExpressionInfo>? AnimationExpressions
        => AnimationEmotes == null ? null : GameData.Animations.Expressions;

    public bool CanSwapAnimation
        => Source?.Animation is { Kind: AnimationSourceKind.Idle or AnimationSourceKind.Emote or AnimationSourceKind.Expression };

    /// <summary>A facial expression: it can only be swapped to another expression.</summary>
    public bool IsFacialAnimation => Source?.Animation is { Kind: AnimationSourceKind.Expression };

    // ── What the selection does ─────────────────────────────────────────────

    /// <summary>Whether the idle stays in its current slot.</summary>
    private bool StaysInSlot(AnimationSource source) => _animationOutputSlots.Contains(source.SlotIndex);

    /// <summary>The ticked slots other than the idle's current one, in slot order.</summary>
    private List<IdleSlot> OtherOutputSlots(AnimationSource source)
        => [.. AnimationSlots.Where(s => s.Index != source.SlotIndex && _animationOutputSlots.Contains(s.Index))];

    /// <summary>Whether the animation goes somewhere else: an idle to another slot, or any other swap.</summary>
    private bool Moves(AnimationSource source)
        => source.Kind == AnimationSourceKind.Idle ? OtherOutputSlots(source).Count > 0 : AnimationOperation == AnimationOperation.Swap;

    private bool Retargets(AnimationSource source)
        => source.Kind == AnimationSourceKind.Idle ? AnimationAlsoRetargets : AnimationOperation == AnimationOperation.Retarget;

    /// <summary>
    /// Whether the expression is attached where the animation already is: "Only add an
    /// expression", or an idle that stays in its current slot. Added to this mod, that goes into
    /// an option group.
    /// </summary>
    private bool ExpressionAtSource(AnimationSource source)
        => source.Kind == AnimationSourceKind.Idle
            ? AttachExpression && StaysInSlot(source)
            : AnimationOperation == AnimationOperation.Expression;

    /// <summary>What the selected animation's conversion would do, for the output options (see <see cref="AnimationContents"/>).</summary>
    private PlanContents SelectionContents(AnimationSource source)
    {
        var contents = PlanContents.None;
        if (Moves(source)) contents |= PlanContents.AnimationSwap;
        if (Retargets(source)) contents |= PlanContents.AnimationRetarget;
        if (ExpressionAtSource(source)) contents |= PlanContents.AnimationExpression;
        return contents == PlanContents.None ? PlanContents.AnimationSwap : contents;
    }

    /// <summary>What an animation conversion does, for the output options' sentences.</summary>
    private static PlanContents AnimationContents(AnimationConversionRequest? request)
    {
        if (request == null) return PlanContents.AnimationSwap;
        var contents = PlanContents.None;
        if (request.Operation == AnimationOperation.Swap && !request.Variants.IsDefaultOrEmpty) contents |= PlanContents.AnimationSwap;
        if (!request.TargetRaces.IsDefaultOrEmpty) contents |= PlanContents.AnimationRetarget;
        if (request.ExpressionAtSource) contents |= PlanContents.AnimationExpression;
        return contents == PlanContents.None ? PlanContents.AnimationSwap : contents;
    }

    // ── Choices ─────────────────────────────────────────────────────────────

    private void ResetAnimationTarget(AnimationSource source)
    {
        AnimationOperation = source.Kind == AnimationSourceKind.Other ? AnimationOperation.Retarget : AnimationOperation.Swap;
        AnimationAlsoRetargets = false;
        AnimationTargetEmote = 0;
        AnimationSourceContainer = null;
        AttachExpression = false;
        AnimationIncludesSourceRace = true;
        _animationOutputSlots.Clear();
        _animationTargetRaces.Clear();
        AnimationSourceRace = source.Races.Contains((ushort)101) ? (ushort)101 : source.Races.FirstOrDefault();
    }

    public void SetAnimationOperation(AnimationOperation operation)
    {
        if (operation == AnimationOperation || operation == AnimationOperation.Swap && !CanSwapAnimation) return;
        if (IsFacialAnimation && operation != AnimationOperation.Swap) return;
        AnimationOperation = operation;
        MarkDirty();
    }

    /// <summary>Ticks or unticks one of the idle's slots: its current one, or one a swap writes something for.</summary>
    public void SetAnimationOutputSlot(int index, bool ticked)
    {
        if (ticked && (Source?.Animation is not { } source || AnimationSlots.FirstOrDefault(s => s.Index == index) is not { } slot ||
                       index != source.SlotIndex && !CanSwapToIdleSlot(source, slot))) return;
        if (ticked ? !_animationOutputSlots.Add(index) : !_animationOutputSlots.Remove(index)) return;
        MarkDirty();
    }

    public void SetAnimationAlsoRetargets(bool retargets)
    {
        if (retargets == AnimationAlsoRetargets) return;
        AnimationAlsoRetargets = retargets;
        MarkDirty();
    }

    public void SetAnimationSourceContainer(ContainerAddress? address)
    {
        if (address == AnimationSourceContainer) return;
        AnimationSourceContainer = address;
        MarkDirty();
    }

    public void SetAnimationTargetEmote(uint id)
    {
        if (id == AnimationTargetEmote) return;
        // Shared with expression swaps, which place the face either way.
        if (Source?.Animation is { Kind: AnimationSourceKind.Emote } source && GameData.Animations.EmotesReady &&
            GameData.Animations.FindEmote(id) is { } emote && !CanSwapToEmote(source, emote)) return;
        AnimationTargetEmote = id;
        MarkDirty();
    }

    private AnimationSource? _swapTargetsFor;
    private readonly Dictionary<object, bool> _swapTargets = new();

    /// <summary>
    /// Whether a swap to the emote writes anything: the game has an animation of its own at one of
    /// the destinations for one of the source's races at least. Every other race plays the file of
    /// a race it inherits from, and the game never asks for one of its own, so nothing is written
    /// for it.
    /// </summary>
    public bool CanSwapToEmote(AnimationSource source, EmoteInfo emote)
        => GameHasForSourceRaces(source, emote, () => EmoteVariant(source, emote).Locations.Values);

    /// <summary>Whether a swap into the idle slot writes anything; see <see cref="CanSwapToEmote"/>.</summary>
    public bool CanSwapToIdleSlot(AnimationSource source, IdleSlot slot)
        => GameHasForSourceRaces(source, slot, () => SlotVariant(source, slot).Locations.Values);

    private bool GameHasForSourceRaces(AnimationSource source, object target, Func<IEnumerable<string>> destinations)
    {
        if (!ReferenceEquals(source, _swapTargetsFor))
        {
            _swapTargetsFor = source;
            _swapTargets.Clear();
        }
        if (_swapTargets.TryGetValue(target, out var known)) return known;
        var locations = destinations().Distinct(StringComparer.Ordinal).ToList();
        return _swapTargets[target] = source.Races.Any(race =>
            locations.Any(location => GameData.Animations.FileExists(PapGamePath(race, location))));
    }

    public void SetAnimationSourceRace(ushort race)
    {
        if (race == AnimationSourceRace || Source?.Animation is not { } source || !source.Races.Contains(race)) return;
        AnimationSourceRace = race;
        _animationTargetRaces.Remove(race);
        MarkDirty();
    }

    private AnimationSource? _idleOutputsFor;
    private string _idleOutputsKey = string.Empty;
    private IReadOnlyList<string> _idleOutputs = [];

    /// <summary>The locations an idle conversion writes, for any race: each ticked slot's, the current one's being the idle's own.</summary>
    private IReadOnlyList<string> IdleOutputLocations(AnimationSource source)
    {
        var key = string.Join(",", _animationOutputSlots);
        if (!ReferenceEquals(source, _idleOutputsFor) || key != _idleOutputsKey)
        {
            _idleOutputsFor = source;
            _idleOutputsKey = key;
            _idleOutputs = AnimationSlots.Where(s => _animationOutputSlots.Contains(s.Index))
                .SelectMany(s => s.Index == source.SlotIndex ? source.Locations : SlotVariant(source, s).Locations.Values)
                .Distinct(StringComparer.Ordinal)
                .ToList();
        }
        return _idleOutputs;
    }

    /// <summary>
    /// Where a retarget writes the animation: an idle's ticked slots (its own until one is
    /// ticked), or where the animation is.
    /// </summary>
    private IReadOnlyList<string> RetargetLocations(AnimationSource source)
        => source.Kind == AnimationSourceKind.Idle && IdleOutputLocations(source) is { Count: > 0 } outputs ? outputs : source.Locations;

    private AnimationSource? _retargetRacesFor;
    private string _retargetRacesKey = string.Empty;
    private IReadOnlyList<ushort> _retargetRaces = [];

    /// <summary>
    /// The races a retarget can give the source's animation: those the game has a file of their
    /// own for, at one of the locations it writes at least (see <see cref="RetargetLocations"/>).
    /// Every other race plays the file of a race it inherits from, and the game never asks for one
    /// of its own, so a file written for it would never load.
    /// </summary>
    public IReadOnlyList<ushort> RetargetRaces(AnimationSource source)
    {
        var locations = RetargetLocations(source);
        var key = string.Join("|", locations);
        if (!ReferenceEquals(source, _retargetRacesFor) || key != _retargetRacesKey)
        {
            _retargetRacesFor = source;
            _retargetRacesKey = key;
            _retargetRaces = GenderRaces.Playable
                .Where(race => locations.Any(location => GameData.Animations.FileExists(PapGamePath(race, location))))
                .ToList();
        }
        return _retargetRaces;
    }

    public void SetAnimationTargetRace(ushort race, bool included)
    {
        if (race == AnimationSourceRace && included) return;
        if (included && (Source?.Animation is not { } source || !RetargetRaces(source).Contains(race))) return;
        if (included ? !_animationTargetRaces.Add(race) : !_animationTargetRaces.Remove(race)) return;
        MarkDirty();
    }

    /// <summary>Ticks or unticks the source race; only a new mod leaves that choice to the user.</summary>
    public void SetAnimationIncludesSourceRace(bool include)
    {
        if (include == AnimationIncludesSourceRace || !EffectiveOutputMode.IsNewMod()) return;
        AnimationIncludesSourceRace = include;
        MarkDirty();
    }

    // ── Readiness and the request ───────────────────────────────────────────

    /// <summary>Why the animation inputs are incomplete, or null.</summary>
    private string? AnimationBlockReason(AnimationSource source) => source.Kind switch
    {
        AnimationSourceKind.Idle       => IdleBlockReason(source) ?? ExpressionBlockReason(),
        AnimationSourceKind.Expression => OperationBlockReason(source),
        _                              => OperationBlockReason(source) ?? ExpressionBlockReason(),
    };

    private string? IdleBlockReason(AnimationSource source)
    {
        if (_animationOutputSlots.Count == 0)
            return "Tick the slots the animation should play in (its current one to keep or convert it there).";
        if (_animationOutputSlots.All(index => index == source.SlotIndex) && !AnimationAlsoRetargets && !AttachExpression)
            return "It already plays in its current slot. Tick another slot, or retarget it or attach an expression.";
        if (AnimationAlsoRetargets && RetargetBlockReason(source) is { } reason) return reason;
        return NeedsAnimationSourceContainer && source.Providers.All(p => p.Address != AnimationSourceContainer)
            ? "Several options have their own version of this animation; choose the one the expression's option group uses."
            : null;
    }

    private string? RetargetBlockReason(AnimationSource source)
    {
        if (!source.Races.Contains(AnimationSourceRace)) return "Choose the race to retarget from.";
        return _animationTargetRaces.Count == 0 ? "Choose at least one race to retarget to." : null;
    }

    private string? OperationBlockReason(AnimationSource source)
    {
        if (AnimationOperation == AnimationOperation.Expression)
            return NeedsAnimationSourceContainer && source.Providers.All(p => p.Address != AnimationSourceContainer)
                ? "Several options have their own version of this animation; choose the one the expression's option group uses."
                : null;
        if (AnimationOperation == AnimationOperation.Retarget) return RetargetBlockReason(source);

        switch (source.Kind)
        {
            case AnimationSourceKind.Expression:
                if (AnimationExpressions == null) return "Reading the expression list";
                if (GameData.Animations.FindExpression(AnimationTargetEmote) is not { } target) return "Choose the expression the face moves to.";
                if (target.Id == source.EmoteId || FacePose(source) == target.Pose) return "Choose a different expression than the current one.";
                return target.OwnPack ? null : $"/{target.Name} is kept in the shared face pack with every other face, so it cannot be replaced.";
            case AnimationSourceKind.Emote:
                if (AnimationEmotes == null) return "Reading the emote list";
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
        var request = new AnimationConversionRequest(source.Locations, AnimationOperation, OutputMode, DescribeAnimation(source))
        {
            Expression      = source.Kind == AnimationSourceKind.Expression ? null : CurrentExpression(source),
            SourceContainer = NeedsAnimationSourceContainer ? AnimationSourceContainer : null,
        };
        // An idle goes to every ticked slot at once, and is retargeted wherever it goes.
        if (source.Kind == AnimationSourceKind.Idle)
            return request with
            {
                Variants          = [.. OtherOutputSlots(source).Select(slot => SlotVariant(source, slot))],
                StaysAtSource     = StaysInSlot(source),
                SourceRace        = AnimationAlsoRetargets ? AnimationSourceRace : (ushort)0,
                TargetRaces       = AnimationAlsoRetargets ? [.. _animationTargetRaces] : [],
                IncludeSourceRace = AnimationIncludesSourceRace,
            };
        if (AnimationOperation == AnimationOperation.Expression) return request;
        if (AnimationOperation == AnimationOperation.Retarget)
            return request with
            {
                SourceRace        = AnimationSourceRace,
                TargetRaces       = [.. _animationTargetRaces],
                IncludeSourceRace = AnimationIncludesSourceRace,
            };

        if (source.Kind == AnimationSourceKind.Emote)
            return request with { Variants = [EmoteVariant(source)] };
        return GameData.Animations.FindExpression(AnimationTargetEmote) is { } expression
            ? request with { Variants = [FaceVariant(source, expression)] }
            : null;
    }

    /// <summary>The source idle's loop moves to the slot's loop, and its start to the slot's start when both have one.</summary>
    private static AnimationSwapVariant SlotVariant(AnimationSource source, IdleSlot slot)
    {
        const string prefix = "a0001/bt_common/";
        var map   = ImmutableDictionary.CreateBuilder<string, string>(StringComparer.Ordinal);
        var roles = ImmutableDictionary.CreateBuilder<string, string>(StringComparer.Ordinal);
        foreach (var location in source.Locations)
        {
            if (!IdleSlots.TryDescribe(location[prefix.Length..], out _, out _, out var isStart)) continue;
            roles[location] = isStart ? "start" : "looping";
            if (!isStart) map[location] = prefix + slot.LoopKey;
            else if (slot.StartKey != null) map[location] = prefix + slot.StartKey;
        }
        return new AnimationSwapVariant(slot.Label, map.ToImmutable()) { SourceRoles = roles.ToImmutable() };
    }

    /// <summary>The pose a facial expression source's packs hold, e.g. <c>smile</c>.</summary>
    private static string FacePose(AnimationSource source)
        => source.Locations.Select(l => l[(l.LastIndexOf('/') + 1)..]).FirstOrDefault() ?? string.Empty;

    /// <summary>Every pack of the face moves to the destination pose, in the same face animation set.</summary>
    private static AnimationSwapVariant FaceVariant(AnimationSource source, ExpressionInfo target)
    {
        var map = ImmutableDictionary.CreateBuilder<string, string>(StringComparer.Ordinal);
        foreach (var location in source.Locations)
            map[location] = location[..(location.LastIndexOf('/') + 1)] + target.Pose;
        return new AnimationSwapVariant($"/{target.Name}", map.ToImmutable());
    }

    private AnimationSwapVariant EmoteVariant(AnimationSource source)
        => EmoteVariant(source, GameData.Animations.FindEmote(AnimationTargetEmote));

    /// <summary>An emote's animations pair with the destination's by their position in the emote.</summary>
    private AnimationSwapVariant EmoteVariant(AnimationSource source, EmoteInfo? to)
    {
        var from = GameData.Animations.FindEmote(source.EmoteId);
        var map   = ImmutableDictionary.CreateBuilder<string, string>(StringComparer.Ordinal);
        var roles = ImmutableDictionary.CreateBuilder<string, string>(StringComparer.Ordinal);
        if (from != null && to != null)
        {
            var provided = from.Timelines.Where(t => source.Locations.Contains(t.Location)).ToList();
            foreach (var timeline in provided)
            {
                roles[timeline.Location] = timeline.Label;
                if (to.Timelines.FirstOrDefault(t => t.Index == timeline.Index) is { } match)
                    map[timeline.Location] = match.Location;
            }
            // Emotes with one animation each pair them regardless of position.
            if (map.Count == 0 && provided.Count == 1 && to.Timelines.Length == 1)
                map[provided[0].Location] = to.Timelines[0].Location;
        }
        return new AnimationSwapVariant(to == null ? "Emote" : $"/{to.Name}", map.ToImmutable())
        {
            SourceRoles = roles.ToImmutable(),
        };
    }

    // ── Descriptions ────────────────────────────────────────────────────────

    private string DescribeAnimation(AnimationSource source)
        => source.Kind == AnimationSourceKind.Idle
            ? DescribeIdle(source)
            : DescribeOperation(source) + (AttachExpression && AnimationOperation != AnimationOperation.Expression
                ? $", with the {ExpressionLabel}"
                : string.Empty);

    /// <summary>
    /// How an expression's description names the version its option group uses. It goes last, so
    /// a version chosen later on the plan row reads the same as one chosen on the card.
    /// </summary>
    private static string VersionSuffix(string label) => $" (the version in '{label}')";

    /// <summary><see cref="VersionSuffix"/> for the version chosen on the card, when one has to be.</summary>
    private string ChosenVersionSuffix(AnimationSource source)
        => NeedsAnimationSourceContainer && source.Providers.FirstOrDefault(p => p.Address == AnimationSourceContainer) is { } chosen
            ? VersionSuffix(chosen.Label)
            : string.Empty;

    /// <summary>"Standing idle 3 → Standing idle 5 and its own slot, retargeted (races), with the /Smile".</summary>
    private string DescribeIdle(AnimationSource source)
    {
        var others = OtherOutputSlots(source);
        var text = source.Label;
        if (others.Count > 0)
            text += " → " + string.Join(", ", others.Select(s => s.Label)) + (StaysInSlot(source) ? " and its own slot" : string.Empty);
        if (AnimationAlsoRetargets)
            text += (others.Count > 0 ? ", retargeted " : ": ") + DescribeRetarget();
        if (AttachExpression)
            text += others.Count > 0 || AnimationAlsoRetargets ? $", with the {ExpressionLabel}" : $" + {ExpressionLabel}";
        return text + ChosenVersionSuffix(source);
    }

    private string DescribeOperation(AnimationSource source)
    {
        if (AnimationOperation == AnimationOperation.Expression)
            return $"{source.Label} + {ExpressionLabel}" + ChosenVersionSuffix(source);
        if (AnimationOperation == AnimationOperation.Retarget)
            return $"{source.Label}: {DescribeRetarget()}";
        if (source.Kind == AnimationSourceKind.Emote)
            return $"{source.Label} → /{GameData.Animations.FindEmote(AnimationTargetEmote)?.Name ?? "?"}";
        return $"{source.Label} → /{GameData.Animations.FindExpression(AnimationTargetEmote)?.Name ?? "?"}";
    }

    /// <summary>"Miqo'te Female → Midlander Female", saying when a new mod leaves the source race out.</summary>
    private string DescribeRetarget()
        => $"{RaceLabel(AnimationSourceRace)} → {string.Join(", ", _animationTargetRaces.Select(RaceLabel))}" +
           (AnimationIncludesSourceRace ? string.Empty : $" (without {RaceLabel(AnimationSourceRace)} in a new mod)");

    /// <summary>What the selection does, as the plan's summary names it: "Animation swap + expression".</summary>
    private string AnimationTargetDetail(AnimationSource source)
    {
        var moves = Moves(source);
        var retargets = Retargets(source);
        return (moves ? "Animation swap" : retargets ? "Race retarget" : "Expression added") +
               (moves && retargets ? " + retarget" : string.Empty) +
               (WantsExpression && (moves || retargets) ? " + expression" : string.Empty);
    }

    // ── Targets other mods already change ───────────────────────────────────
    // A swap writes the target's animations for every race the source has, and a retarget the
    // source's for the target race. What is looked up is what those races play there: their own
    // file where the game has one, or else the one of the race they inherit it from.

    /// <summary>
    /// Who already changes the slot as the races the conversion writes for play it, or null. Its
    /// loop and its start both count, the loop the most, so the note says which is changed.
    /// </summary>
    public string? ModdedIdleSlot(AnimationSource source, IdleSlot slot)
    {
        var races = IdleRaces(source);
        var scope = (source, string.Join(",", races));
        IReadOnlyList<string> Part(string? key)
            => key == null ? [] : Modded.PathMods(scope, key, () => PlayedPaths(races, [$"a0001/bt_common/{key}"]));
        var loop  = Part(slot.LoopKey);
        var start = Part(slot.StartKey);
        if (loop.Count == 0 && start.Count == 0) return null;
        var what = slot.StartKey == null ? "Already"
            : start.Count == 0 ? "Its loop is already"
            : loop.Count == 0 ? "Only its start is already"
            : "Its loop and start are already";
        return Modded.DescribeMods([.. loop.Union(start, StringComparer.OrdinalIgnoreCase)], what);
    }

    /// <summary>The races an idle conversion writes for: the mod's, and those it retargets to, the source race while it stays.</summary>
    private IReadOnlyList<ushort> IdleRaces(AnimationSource source)
        => AnimationAlsoRetargets
            ? [.. source.Races.Union(_animationTargetRaces).Where(r => r != AnimationSourceRace || AnimationSourceRaceStays).Order()]
            : source.Races;

    /// <summary>Who already changes any of the emote's animations, as the source's races play them, or null.</summary>
    public string? ModdedEmote(AnimationSource source, EmoteInfo emote)
        => Modded.Paths(source, emote, () => PlayedPaths(source.Races, [.. emote.Timelines.Select(t => t.Location)]));

    /// <summary>Who already changes the expression's face packs the source's faces would move to, or null.</summary>
    public string? ModdedExpression(AnimationSource source, ExpressionInfo expression)
        => expression.OwnPack
            ? Modded.Paths(source, expression, () =>
            {
                var locations = FaceVariant(source, expression).Locations.Values.ToList();
                return source.Races.SelectMany(race => locations.Select(location => PapGamePath(race, location)));
            })
            : null;

    /// <summary>Who already changes the animation for <paramref name="race"/> where a retarget writes it, or null.</summary>
    public string? ModdedRetargetRace(AnimationSource source, ushort race)
    {
        var locations = RetargetLocations(source);
        return Modded.Paths((source, string.Join("|", locations)), race, () => locations
            .Select(location => PapGamePath(race, location))
            .Where(GameData.Animations.FileExists));
    }

    /// <summary>
    /// What <paramref name="races"/> play at <paramref name="locations"/>: each race's own file
    /// where the game has one, or else the one of the first race up its skeleton parents that has.
    /// </summary>
    private IEnumerable<string> PlayedPaths(IReadOnlyList<ushort> races, IReadOnlyList<string> locations)
        => races.SelectMany(race => locations.Select(location =>
                AnimationConversionPlanner.ResolvingRace(race, r => GameData.Animations.FileExists(PapGamePath(r, location)),
                    _plugin.Converter.ParentRace) is { } owner
                    ? PapGamePath(owner, location)
                    : null))
            .OfType<string>();

    private static string PapGamePath(ushort race, string location) => $"chara/human/c{race:D4}/animation/{location}.pap";

    /// <summary>Short label for the default new mod name.</summary>
    private string AnimationNameLabel(AnimationSource source)
    {
        if (source.Kind == AnimationSourceKind.Idle)
        {
            var others = OtherOutputSlots(source);
            if (others.Count > 0) return others.Count == 1 ? others[0].Label : $"{others.Count} slots";
            if (AnimationAlsoRetargets) return RetargetNameLabel();
            return AttachExpression ? $"with {ExpressionLabel}" : "Swapped";
        }
        if (AnimationOperation == AnimationOperation.Expression) return $"with {ExpressionLabel}";
        if (AnimationOperation == AnimationOperation.Retarget) return RetargetNameLabel();
        if (source.Kind == AnimationSourceKind.Emote)
            return GameData.Animations.EmotesReady && GameData.Animations.FindEmote(AnimationTargetEmote) is { } emote
                ? $"as /{emote.Name}"
                : "Swapped";
        return GameData.Animations.EmotesReady && GameData.Animations.FindExpression(AnimationTargetEmote) is { } face
            ? $"as /{face.Name}"
            : "Swapped";
    }

    private string RetargetNameLabel()
        => _animationTargetRaces.Count == 1 ? RaceLabel(_animationTargetRaces.First()) : "Retargeted";
}
