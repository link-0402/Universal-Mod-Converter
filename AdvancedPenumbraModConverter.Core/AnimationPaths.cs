using System.Collections.Immutable;
using System.Text.RegularExpressions;

namespace AdvancedPenumbraModConverter.Core;

/// <summary>
/// A player body animation pack path:
/// <c>chara/human/c{race}/animation/{set}/{directory}/{key}.pap</c>, for example
/// <c>chara/human/c0101/animation/a0001/bt_common/emote/pose01_loop.pap</c>. The race is the
/// only race-specific part; <see cref="Location"/> identifies the animation for every race.
/// Facial animations live under <c>animation/f####</c> and bind to face skeletons, so they
/// are not body animations and never parse.
/// </summary>
public sealed partial record PapPath(ushort Race, string Set, string Directory, string Key)
{
    [GeneratedRegex(@"^chara/human/c(?<race>\d{4})/animation/(?<set>a\d{4})/(?<dir>[a-z0-9_]+)/(?<key>[a-z0-9_./-]+)\.pap$",
        RegexOptions.CultureInvariant)]
    private static partial Regex PathRegex();

    public string GamePath => $"chara/human/c{Race:D4}/animation/{Set}/{Directory}/{Key}.pap";

    /// <summary>The race-independent part, e.g. <c>a0001/bt_common/emote/pose01_loop</c>.</summary>
    public string Location => $"{Set}/{Directory}/{Key}";

    public PapPath WithRace(ushort race) => this with { Race = race };

    public PapPath WithKey(string key) => this with { Key = key };

    public static bool TryParse(string gamePath, out PapPath path)
    {
        path = null!;
        var match = PathRegex().Match(Core.GamePath.Normalize(gamePath));
        if (!match.Success || match.Groups["key"].Value.Split('/').Any(s => s is "" or "." or "..")) return false;
        path = new PapPath(ushort.Parse(match.Groups["race"].Value), match.Groups["set"].Value,
            match.Groups["dir"].Value, match.Groups["key"].Value);
        return true;
    }

    /// <summary>The standard location of an ActionTimeline key's body animation.</summary>
    public static PapPath ForTimelineKey(ushort race, string timelineKey)
        => new(race, "a0001", "bt_common", AnimationKeys.PapKey(timelineKey));

    public static string BaseSkeletonPath(ushort race)
        => $"chara/human/c{race:D4}/skeleton/base/b0001/skl_c{race:D4}b0001.sklb";

    public override string ToString() => GamePath;
}

public static class AnimationKeys
{
    /// <summary>
    /// ActionTimeline keys and PAP keys differ for the resident idle: timeline
    /// <c>normal/idle</c> plays <c>resident/idle.pap</c>. Every other key is its own path.
    /// </summary>
    public static string PapKey(string timelineKey) => timelineKey switch
    {
        "normal/idle" => "resident/idle",
        _ => timelineKey,
    };

    /// <summary>The ActionTimeline key that plays a PAP key; the inverse of <see cref="PapKey"/>.</summary>
    public static string TimelineKey(string papKey) => papKey switch
    {
        "resident/idle" => "normal/idle",
        _ => papKey,
    };
}

/// <summary>A member of an idle family: the loop, and the start that plays before it if there is one.</summary>
public sealed record IdleSlot(string Family, int Index, string LoopKey, string? StartKey)
{
    public IEnumerable<string> Keys => StartKey == null ? [LoopKey] : [LoopKey, StartKey];

    public string Label => IdleSlots.SlotLabel(Family, Index);
}

/// <summary>
/// Idle families the player switches between with /cpose. Each family has numbered members
/// under <c>emote/</c> (<c>pose03_loop.pap</c> with an optional <c>pose03_start.pap</c>) and one
/// unnumbered base member elsewhere: <c>resident/idle.pap</c> for standing, <c>sit</c> for
/// chair sitting and <c>jmn</c> for ground sitting. The base member is slot 0 and swaps like
/// any other. Swaps never cross families: a standing idle cannot become a sitting one.
/// </summary>
public static partial class IdleSlots
{
    public sealed record Family(string Key, string Label, string Prefix, string BaseName);

    public static ImmutableArray<Family> Families { get; } =
    [
        new("standing", "Standing idle", "pose", "idle"),
        new("chair", "Chair sitting idle", "s_pose", "sit"),
        new("ground", "Ground sitting idle", "j_pose", "jmn"),
    ];

    /// <summary>Numbered members are probed up to here.</summary>
    public const int MaxIndex = 30;

    [GeneratedRegex(@"^emote/(?<prefix>[sj]_pose|pose)(?<slot>\d{2})_(?<state>loop|start)$", RegexOptions.CultureInvariant)]
    private static partial Regex NumberedKeyRegex();

    public static Family? GetFamily(string key) => Families.FirstOrDefault(f => f.Key == key);

    /// <summary>The family and slot a PAP key belongs to, if it is an idle.</summary>
    public static bool TryDescribe(string key, out string family, out int index, out bool isStart)
    {
        family = string.Empty;
        index = 0;
        isStart = false;
        if (NumberedKeyRegex().Match(key) is { Success: true } numbered)
        {
            family = Families.First(f => f.Prefix == numbered.Groups["prefix"].Value).Key;
            index = int.Parse(numbered.Groups["slot"].Value);
            isStart = numbered.Groups["state"].Value == "start";
            return index is > 0 and <= MaxIndex;
        }
        foreach (var candidate in Families)
            if (BaseKeys(candidate).Contains(key))
            {
                family = candidate.Key;
                return true;
            }
        return false;
    }

    /// <summary>Where a family's base member may live; the game data decides which one exists.</summary>
    public static IEnumerable<string> BaseKeys(Family family)
        => [$"resident/{family.BaseName}", $"emote/{family.BaseName}"];

    public static string NumberedKey(Family family, int index, bool start)
        => $"emote/{family.Prefix}{index:D2}_{(start ? "start" : "loop")}";

    /// <summary>
    /// Every slot of a family that exists for <paramref name="exists"/> (a key test, normally the
    /// game data of one race). A start is only paired when it exists too.
    /// </summary>
    public static ImmutableArray<IdleSlot> Discover(Family family, Func<string, bool> exists)
    {
        var slots = ImmutableArray.CreateBuilder<IdleSlot>();
        if (BaseKeys(family).FirstOrDefault(exists) is { } baseKey)
            slots.Add(new IdleSlot(family.Key, 0, baseKey, null));
        for (var index = 1; index <= MaxIndex; index++)
        {
            var loop = NumberedKey(family, index, false);
            if (!exists(loop)) continue;
            var start = NumberedKey(family, index, true);
            slots.Add(new IdleSlot(family.Key, index, loop, exists(start) ? start : null));
        }
        return slots.ToImmutable();
    }

    public static string SlotLabel(string family, int index)
    {
        var label = GetFamily(family)?.Label ?? family;
        return index == 0 ? $"{label} (default)" : $"{label} {index}";
    }
}
