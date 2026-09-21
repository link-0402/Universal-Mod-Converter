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

public enum AnimationSourceKind
{
    Idle,
    Emote,
    Other,
}

/// <summary>
/// An animation a mod replaces, for every race it provides: an idle slot (loop and start), all
/// of an emote's animations, or any other single body animation.
/// </summary>
public sealed record AnimationSource(AnimationSourceKind Kind, string Label, ImmutableArray<string> Locations,
    ImmutableArray<ushort> Races, string? Family = null, int SlotIndex = -1, uint EmoteId = 0, uint Icon = 0);

/// <summary>
/// The game's player animations: emotes and their body animations from the Emote and
/// ActionTimeline sheets, and the idle slots each /cpose family has (probed in the game data,
/// because no sheet lists them).
/// </summary>
public sealed class AnimationCatalog(IDataManager data, IPluginLog log)
{
    /// <summary>The race whose files decide which idle slots and emote animations exist.</summary>
    private const ushort ProbeRace = 101;

    private readonly object _lock = new();
    private IReadOnlyList<EmoteInfo>? _emotes;
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
            try
            {
                foreach (var emote in data.GetExcelSheet<Emote>())
                {
                    var name = emote.Name.ExtractText();
                    if (string.IsNullOrWhiteSpace(name)) continue;
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
            _emotes = emotes.OrderBy(e => e.Name, StringComparer.OrdinalIgnoreCase).ThenBy(e => e.Id).ToList();
        }
    }

    /// <summary>Groups the body animations a mod replaces into swappable and retargetable sources.</summary>
    public List<AnimationSource> Scan(PenumbraMod mod)
    {
        EnsureEmotes();
        var races = new Dictionary<string, SortedSet<ushort>>(StringComparer.Ordinal);
        foreach (var container in mod.Containers)
        foreach (var (key, _) in container.FileEntries())
        {
            if (!PapPath.TryParse(key, out var path)) continue;
            if (!races.TryGetValue(path.Location, out var set)) races[path.Location] = set = [];
            set.Add(path.Race);
        }

        var result = new List<AnimationSource>();
        var claimed = new HashSet<string>(StringComparer.Ordinal);
        ImmutableArray<ushort> Races(IEnumerable<string> locations)
            => [.. locations.SelectMany(l => races[l]).Distinct().Order()];

        // Idle slots: the loop and its start together.
        foreach (var slot in races.Keys
                     .Select(location => (Location: location, Ok: TryIdle(location, out var family, out var index), family, index))
                     .Where(s => s.Ok)
                     .GroupBy(s => (s.family, s.index))
                     .OrderBy(g => g.Key.family, StringComparer.Ordinal).ThenBy(g => g.Key.index))
        {
            var locations = slot.Select(s => s.Location).Order(StringComparer.Ordinal).ToImmutableArray();
            claimed.UnionWith(locations);
            result.Add(new AnimationSource(AnimationSourceKind.Idle, Core.IdleSlots.SlotLabel(slot.Key.family, slot.Key.index),
                locations, Races(locations), slot.Key.family, slot.Key.index));
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
            result.Add(new AnimationSource(AnimationSourceKind.Emote, $"/{emote.Name}{shared}", ordered, Races(ordered),
                EmoteId: id, Icon: emote.Icon));
        }

        foreach (var location in races.Keys.Where(l => !claimed.Contains(l)).Order(StringComparer.Ordinal))
            result.Add(new AnimationSource(AnimationSourceKind.Other, location, [location], Races([location])));
        return result;
    }

    private static bool TryIdle(string location, out string family, out int index)
    {
        family = string.Empty;
        index = 0;
        const string prefix = "a0001/bt_common/";
        return location.StartsWith(prefix, StringComparison.Ordinal) &&
               Core.IdleSlots.TryDescribe(location[prefix.Length..], out family, out index, out _);
    }
}
