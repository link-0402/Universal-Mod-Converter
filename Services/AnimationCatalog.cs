using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Collections.Immutable;
using System.Linq;
using UniversalModConverter.Core;
using Dalamud.Plugin.Services;
using Lumina.Excel.Sheets;

namespace UniversalModConverter.Services;

/// <summary>One body animation an emote plays: its position in the emote's timeline list and its PAP key.</summary>
public sealed record EmoteTimeline(int Index, string Key)
{
    public string Location => $"a0001/bt_common/{Key}";

    /// <summary>What the game uses this position of Emote.ActionTimeline for.</summary>
    public string Label => Index switch
    {
        0 => "main",
        1 => "start",
        2 => "ground sitting",
        3 => "chair sitting",
        4 => "upper body",
        _ => $"timeline {Index}",
    };
}

public sealed record EmoteInfo(uint Id, string Name, uint Icon, ImmutableArray<EmoteTimeline> Timelines);

/// <summary>
/// An emote of the game's Expressions list and the facial pose it plays (see <see cref="GameExpressions"/>).
/// <paramref name="OwnPack"/>: the pose has a pack of its own, rather than living in the shared
/// resident pack with every other face, so a face can be swapped onto it.
/// </summary>
public sealed record ExpressionInfo(uint Id, string Name, uint Icon, string Pose, bool OwnPack);

public enum AnimationSourceKind
{
    Idle,
    Emote,
    /// <summary>A facial expression's own packs (<c>f####/nonresident/{pose}.pap</c>).</summary>
    Expression,
    Other,
}

/// <summary>A container of the mod (Default or an option) that holds an animation.</summary>
public sealed record AnimationProvider(ContainerAddress Address, string Label);

/// <summary>
/// An animation a mod replaces, for every race it provides: an idle slot (loop and start), all
/// of an emote's animations, or any other single body animation.
/// </summary>
public sealed record AnimationSource(AnimationSourceKind Kind, string Label, ImmutableArray<string> Locations,
    ImmutableArray<ushort> Races, string? Family = null, int SlotIndex = -1, uint EmoteId = 0, uint Icon = 0)
{
    /// <summary>Every container that holds the animation, Default first.</summary>
    public ImmutableArray<AnimationProvider> Providers { get; init; } = [];

    /// <summary>
    /// Whether several containers hold different files for the same race and location, so that
    /// anything that can take only one of them (an option group per slot) must be told which.
    /// </summary>
    public bool HasVariants { get; init; }
}

/// <summary>
/// The game's player animations: emotes and their body animations from the Emote and
/// ActionTimeline sheets, and the idle slots each /cpose family has (probed in the game data,
/// because no sheet lists them).
/// </summary>
public sealed class AnimationCatalog(IDataManager data, IPluginLog log)
{
    /// <summary>The race whose files decide which idle slots and emote animations exist.</summary>
    private const ushort ProbeRace = 101;

    /// <summary>The EmoteCategory row of the emote list's Expressions tab.</summary>
    private const uint ExpressionsCategory = 3;

    private readonly object _lock = new();
    private IReadOnlyList<EmoteInfo>? _emotes;
    private IReadOnlyList<ExpressionInfo>? _expressions;
    private Dictionary<string, List<EmoteInfo>>? _emotesByLocation;
    private readonly ConcurrentDictionary<string, ImmutableArray<IdleSlot>> _idleSlots = new();

    public bool FileExists(string gamePath)
    {
        try { return data.FileExists(gamePath); }
        catch (Exception) { return false; }
    }

    /// <summary>Emotes with at least one body animation, by name. Built on first use; call off the framework thread.</summary>
    public IReadOnlyList<EmoteInfo> Emotes
    {
        get
        {
            EnsureEmotes();
            return _emotes!;
        }
    }

    public bool EmotesReady => _emotes != null;

    public EmoteInfo? FindEmote(uint id) => Emotes.FirstOrDefault(e => e.Id == id);

    /// <summary>
    /// The emotes of the Expressions list, by name: only a face, no body animation. Read with
    /// <see cref="Emotes"/>, so <see cref="EmotesReady"/> covers both.
    /// </summary>
    public IReadOnlyList<ExpressionInfo> Expressions
    {
        get
        {
            EnsureEmotes();
            return _expressions!;
        }
    }

    public ExpressionInfo? FindExpression(uint id) => Expressions.FirstOrDefault(e => e.Id == id);

    public ImmutableArray<IdleSlot> IdleSlots(string family)
        => _idleSlots.GetOrAdd(family, key => IdleSlotsFor(key));

    private ImmutableArray<IdleSlot> IdleSlotsFor(string family)
    {
        if (Core.IdleSlots.GetFamily(family) is not { } descriptor) return [];
        return Core.IdleSlots.Discover(descriptor,
            key => FileExists(new PapPath(ProbeRace, "a0001", "bt_common", key).GamePath));
    }

    private void EnsureEmotes()
    {
        if (_emotes != null) return;
        lock (_lock)
        {
            if (_emotes != null) return;
            var emotes = new List<EmoteInfo>();
            var expressions = new List<ExpressionInfo>();
            try
            {
                foreach (var emote in data.GetExcelSheet<Emote>())
                {
                    var name = emote.Name.ExtractText();
                    if (string.IsNullOrWhiteSpace(name)) continue;
                    if (emote.EmoteCategory.RowId == ExpressionsCategory)
                    {
                        if (ExpressionPose(emote) is { } pose)
                            expressions.Add(new ExpressionInfo(emote.RowId, name, emote.Icon, pose,
                                FileExists(GameExpressions.Candidates(ProbeRace, pose).First())));
                        continue;
                    }
                    var timelines = ImmutableArray.CreateBuilder<EmoteTimeline>();
                    var index = 0;
                    foreach (var reference in emote.ActionTimeline)
                    {
                        var position = index++;
                        if (reference.RowId == 0 || !reference.IsValid) continue;
                        var key = reference.Value.Key.ExtractText();
                        if (key.Length == 0) continue;
                        var papKey = AnimationKeys.PapKey(key);
                        if (!PapPath.TryParse(PapPath.ForTimelineKey(ProbeRace, key).GamePath, out var path) ||
                            !FileExists(path.GamePath) || timelines.Any(t => t.Key == papKey)) continue;
                        timelines.Add(new EmoteTimeline(position, papKey));
                    }
                    if (timelines.Count > 0)
                        emotes.Add(new EmoteInfo(emote.RowId, name, emote.Icon, timelines.ToImmutable()));
                }
            }
            catch (Exception ex)
            {
                log.Warning(ex, "[UMC] Could not read the emote list.");
            }

            var byLocation = new Dictionary<string, List<EmoteInfo>>(StringComparer.Ordinal);
            foreach (var emote in emotes)
            foreach (var timeline in emote.Timelines)
            {
                if (!byLocation.TryGetValue(timeline.Location, out var list)) byLocation[timeline.Location] = list = [];
                list.Add(emote);
            }
            _emotesByLocation = byLocation;
            _expressions = expressions.OrderBy(e => e.Name, StringComparer.OrdinalIgnoreCase).ThenBy(e => e.Id).ToList();
            _emotes = emotes.OrderBy(e => e.Name, StringComparer.OrdinalIgnoreCase).ThenBy(e => e.Id).ToList();
        }
    }

    /// <summary>The facial pose an expression plays, when the game has it for the probe race.</summary>
    private string? ExpressionPose(Emote emote)
    {
        foreach (var reference in emote.ActionTimeline)
        {
            if (reference.RowId == 0 || !reference.IsValid ||
                !GameExpressions.TryPose(reference.Value.Key.ExtractText(), out var pose)) continue;
            return GameExpressions.Candidates(ProbeRace, pose).Any(FileExists) ? pose : null;
        }
        return null;
    }

    /// <summary>Groups the body animations a mod replaces into swappable and retargetable sources.</summary>
    public List<AnimationSource> Scan(PenumbraMod mod)
    {
        EnsureEmotes();
        var races = new Dictionary<string, SortedSet<ushort>>(StringComparer.Ordinal);
        var held = new Dictionary<string, List<(ModContainer Container, ushort Race, string Local)>>(StringComparer.Ordinal);
        foreach (var container in mod.Containers)
        foreach (var (key, local) in container.FileEntries())
        {
            if (!PapPath.TryParse(key, out var path)) continue;
            if (!races.TryGetValue(path.Location, out var set)) races[path.Location] = set = [];
            set.Add(path.Race);
            if (!held.TryGetValue(path.Location, out var list)) held[path.Location] = list = [];
            list.Add((container, path.Race, GamePath.NormalizeLocal(local)));
        }

        var result = new List<AnimationSource>();
        var claimed = new HashSet<string>(StringComparer.Ordinal);
        ImmutableArray<ushort> Races(IEnumerable<string> locations)
            => [.. locations.SelectMany(l => races[l]).Distinct().Order()];
        // Which containers hold the animation, and whether they disagree about a file.
        AnimationSource WithProviders(AnimationSource source)
        {
            var entries = source.Locations.SelectMany(l => held[l].Select(h => (Location: l, h.Container, h.Race, h.Local))).ToList();
            return source with
            {
                Providers = [.. entries.Select(e => e.Container).DistinctBy(c => c.Address)
                    .Select(c => new AnimationProvider(c.Address, c.Label))],
                HasVariants = entries.GroupBy(e => (e.Location, e.Race))
                    .Any(g => g.Select(e => e.Local).Distinct(StringComparer.Ordinal).Count() > 1),
            };
        }

        // Idle slots: the loop and its start together.
        foreach (var slot in races.Keys
                     .Select(location => (Location: location, Ok: TryIdle(location, out var family, out var index), family, index))
                     .Where(s => s.Ok)
                     .GroupBy(s => (s.family, s.index))
                     .OrderBy(g => g.Key.family, StringComparer.Ordinal).ThenBy(g => g.Key.index))
        {
            var locations = slot.Select(s => s.Location).Order(StringComparer.Ordinal).ToImmutableArray();
            claimed.UnionWith(locations);
            result.Add(WithProviders(new AnimationSource(AnimationSourceKind.Idle, Core.IdleSlots.SlotLabel(slot.Key.family, slot.Key.index),
                locations, Races(locations), slot.Key.family, slot.Key.index)));
        }

        // Facial expressions: the packs of one pose, for every race and face animation set.
        foreach (var pose in races.Keys.Where(IsFacial).GroupBy(l => l[(l.LastIndexOf('/') + 1)..])
                     .OrderBy(g => g.Key, StringComparer.Ordinal))
        {
            var locations = pose.Order(StringComparer.Ordinal).ToImmutableArray();
            claimed.UnionWith(locations);
            var expression = _expressions!.FirstOrDefault(e => e.Pose == pose.Key);
            result.Add(WithProviders(new AnimationSource(AnimationSourceKind.Expression,
                expression != null ? $"/{expression.Name}" : $"{pose.Key} (face)", locations, Races(locations),
                EmoteId: expression?.Id ?? 0, Icon: expression?.Icon ?? 0)));
        }

        // Emotes: every animation of the emote the mod replaces. A shared animation goes to the
        // emote with the fewest animations, which is the one it most specifically belongs to.
        var emoteLocations = new Dictionary<uint, List<string>>();
        foreach (var location in races.Keys.Where(l => !claimed.Contains(l)))
        {
            if (_emotesByLocation!.GetValueOrDefault(location) is not { Count: > 0 } emotes) continue;
            var owner = emotes.OrderBy(e => e.Timelines.Length).ThenBy(e => e.Id).First();
            if (!emoteLocations.TryGetValue(owner.Id, out var list)) emoteLocations[owner.Id] = list = [];
            list.Add(location);
        }
        foreach (var (id, locations) in emoteLocations.OrderBy(e => FindEmote(e.Key)?.Name, StringComparer.OrdinalIgnoreCase))
        {
            var emote = FindEmote(id)!;
            var ordered = locations.Order(StringComparer.Ordinal).ToImmutableArray();
            claimed.UnionWith(ordered);
            var shared = ordered.Select(l => _emotesByLocation![l].Count).Max() > 1
                ? $" (shared with {string.Join(", ", ordered.SelectMany(l => _emotesByLocation![l]).Where(e => e.Id != id).Select(e => e.Name).Distinct().Take(3))})"
                : string.Empty;
            result.Add(WithProviders(new AnimationSource(AnimationSourceKind.Emote, $"/{emote.Name}{shared}", ordered, Races(ordered),
                EmoteId: id, Icon: emote.Icon)));
        }

        foreach (var location in races.Keys.Where(l => !claimed.Contains(l)).Order(StringComparer.Ordinal))
            result.Add(WithProviders(new AnimationSource(AnimationSourceKind.Other, location, [location], Races([location]))));
        return result;
    }

    /// <summary>A facial expression's pack, <c>f0002/nonresident/smile</c>; body animations live under <c>a####</c>.</summary>
    private static bool IsFacial(string location) => location.StartsWith('f');

    private static bool TryIdle(string location, out string family, out int index)
    {
        family = string.Empty;
        index = 0;
        const string prefix = "a0001/bt_common/";
        return location.StartsWith(prefix, StringComparison.Ordinal) &&
               Core.IdleSlots.TryDescribe(location[prefix.Length..], out family, out index, out _);
    }
}
