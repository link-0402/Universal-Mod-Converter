namespace UniversalModConverter.Core;

/// <summary>
/// A facial animation for a body animation to play: its name, e.g. <c>cfxf_smile</c>, and the
/// face pack it lives in. <paramref name="Pack"/> names a pack of the character's own face
/// animations, <c>f000x/nonresident/{Pack}.pap</c>, which the timeline asks the game to load;
/// null for one in the resident pack the game always has loaded. <paramref name="Timing"/> is
/// how a timeline that plays it does so, when known.
/// </summary>
public sealed record FacialAnimation(string Entry, string? Pack, FaceTiming? Timing = null);

/// <summary>
/// How a timeline plays a facial animation: a C010's fields after its id and start time.
/// With time control (flag 1) the face runs from frame <paramref name="Start"/> to
/// <paramref name="End"/> over <paramref name="Duration"/> frames; without it, the face plays
/// at its own pace. Unknown fields and flags are carried as they are.
/// </summary>
public sealed record FaceTiming(int Duration, int Unknown1, int Flags, float Start, float End, int Unknown2)
{
    /// <summary>
    /// Held on one frame for as long as the timeline says: how single-frame poses such as
    /// <c>cfxf_smile</c> are played, whose length only fits the animation it was made for.
    /// </summary>
    public bool HoldsOneFrame => (Flags & 1) != 0 && End - Start <= 1;

    /// <summary>
    /// The timeline a facial expression plays as an emote, <c>chara/action/facial/pose/{name}.tmb</c>
    /// for <c>cfxf_{name}</c>; null for a name that is not an expression.
    /// </summary>
    public static string? ExpressionTimeline(string face)
        => face.StartsWith("cfxf_", StringComparison.Ordinal) && face.Length > 5 && PapTimeline.IsSafeMotionName(face)
            ? GameExpressions.TimelinePath(face[5..])
            : null;
}

/// <summary>Where the facial expression to attach comes from.</summary>
/// <param name="Label">What the user picked, for messages: "/Smile", "Some Mod: smile".</param>
/// <param name="Pose">
/// A game expression by its pose, e.g. <c>smile</c> for /Smile (see <see cref="GameExpressions"/>),
/// looked up for each race in that race's own face animations.
/// </param>
/// <param name="Face">A facial animation another mod provides or plays.</param>
public sealed record ExpressionDonor(string Label, string? Pose = null, FacialAnimation? Face = null)
{
    /// <summary>
    /// With <see cref="Face"/>: the races the other mod ships the face's pack for. The face is
    /// played by name from the character's own pack, so a race outside these shows it only when
    /// the game has a face of that name in the pack itself.
    /// </summary>
    public IReadOnlyCollection<ushort> PackRaces { get; init; } = [];
}

/// <summary>
/// The expressions the game's emote list offers under Expressions (/Smile, /Sad, /Wink…). Each
/// plays the timeline <c>facial/pose/{pose}</c>: a facial animation named <c>cfxf_{pose}</c>,
/// which a race keeps for its standard faces in <c>animation/f0002</c>, either in a pack of its
/// own or, for the ones every face needs at hand, in the shared <c>resident/face.pap</c>.
/// </summary>
public static class GameExpressions
{
    private const string PosePrefix = "facial/pose/";

    /// <summary>The pose an expression emote's timeline key plays, e.g. <c>smile</c> for <c>facial/pose/smile</c>.</summary>
    public static bool TryPose(string timelineKey, out string pose)
    {
        pose = timelineKey.StartsWith(PosePrefix, StringComparison.Ordinal) ? timelineKey[PosePrefix.Length..] : string.Empty;
        return pose.Length > 0 && !pose.Contains('/');
    }

    /// <summary>The name of the facial animation that plays <paramref name="pose"/>.</summary>
    public static string EntryName(string pose) => $"cfxf_{pose}";

    /// <summary>
    /// The timeline the expression emote plays, <c>chara/action/facial/pose/{pose}.tmb</c>: it names
    /// the pose's pack (TMPP) and plays <c>cfxf_{pose}</c> at the pace the face moves.
    /// </summary>
    public static string TimelinePath(string pose) => $"chara/action/facial/pose/{pose}.tmb";

    /// <summary>The shared pack of a race's face animation set, holding the faces every face needs at hand.</summary>
    public static string ResidentPack(ushort race, string set) => $"chara/human/c{race:D4}/animation/{set}/resident/face.pap";

    /// <summary>Where <paramref name="race"/> may keep <paramref name="pose"/>, the pose's own pack first.</summary>
    public static IEnumerable<string> Candidates(ushort race, string pose)
    {
        yield return NonresidentPack(race, pose);
        yield return ResidentPack(race, "f0002");
    }

    private static string NonresidentPack(ushort race, string pose) => $"chara/human/c{race:D4}/animation/f0002/nonresident/{pose}.pap";

    /// <summary>
    /// Whether the game has <paramref name="face"/> for <paramref name="race"/> itself: in the pack
    /// the face names, or in the resident one for a face that names none.
    /// </summary>
    public static bool Has(ushort race, FacialAnimation face, Func<string, byte[]?> read)
    {
        var path = face.Pack is { } pack ? NonresidentPack(race, pack) : ResidentPack(race, "f0002");
        if (read(path) is not { } bytes) return false;
        try { return new PapFile(bytes).FaceEntries.Any(e => e.Entry.Name == face.Entry); }
        catch (InvalidDataException) { return false; }
    }

    /// <summary>
    /// How <paramref name="race"/> plays <paramref name="pose"/>: from the pose's own pack or the
    /// resident one. Null when the race has none (Elezen males, for one, play their skeleton
    /// parent's faces).
    /// </summary>
    public static FacialAnimation? Find(ushort race, string pose, Func<string, byte[]?> read)
    {
        var entry = EntryName(pose);
        foreach (var path in Candidates(race, pose))
        {
            if (read(path) is not { } bytes) continue;
            try
            {
                if (new PapFile(bytes).FaceEntries.Any(e => e.Entry.Name == entry))
                    return new FacialAnimation(entry, path == NonresidentPack(race, pose) ? pose : null);
            }
            catch (InvalidDataException)
            {
                // Not a readable pack; try the next place.
            }
        }
        return null;
    }
}

/// <summary>
/// Attaches a facial expression to an animation. A body animation does not carry its face:
/// its timeline names one (a C010 entry playing <c>cfxf_smile</c>) and, for a face outside the
/// resident pack, the pack to load (the TMPP header, <c>smile</c>). The game then plays the
/// face from the character's own face animations, so one attached face fits every face shape
/// and race. The animation's own entries and Havok data are left exactly as they are.
/// </summary>
public static class PapExpressions
{
    /// <param name="motions">
    /// The body animations to give the face, by name: those the animation's game path plays.
    /// Other entries of the pack, such as the hit reaction beside an idle, are left alone.
    /// </param>
    public static byte[] Attach(byte[] pap, FacialAnimation face, IReadOnlyCollection<string> motions, List<string> notes)
    {
        var file = new PapFile(pap);
        var timelines = new List<byte[]>();
        var attached = new List<string>();
        var existing = new List<string>();
        for (var i = 0; i < file.Entries.Length; i++)
        {
            var entry = file.Entries[i];
            var original = file.Timeline(i);
            if (!entry.IsBody || !motions.Contains(entry.Name))
            {
                timelines.Add(original);
                continue;
            }

            var timeline = TmbTimeline.Parse(original);
            if (!timeline.Motions.Contains(entry.Name))
            {
                timelines.Add(original);
                continue;
            }
            // An animation that already has a face keeps it: two would fight over the face.
            if (timeline.Faces.FirstOrDefault() is { } own)
            {
                existing.Add($"{entry.Name} ({own})");
                timelines.Add(original);
                continue;
            }

            // A pose held on one frame is held for as long as this animation plays; a moving face
            // keeps its own frames and pace, as its source plays it.
            var timing = face.Timing is { HoldsOneFrame: false } ? face.Timing : null;
            timeline.AddFace(entry.Name, face.Entry, timing);
            // Without faces of its own, any pack the timeline named was loaded for nothing.
            if (face.Pack != null) timeline.FacePack = face.Pack;
            timelines.Add(timeline.ToArray());
            attached.Add(entry.Name);
            if (timing is { Flags: var flags } && (flags & 1) != 0 && timeline.DurationOf(entry.Name) is { } length &&
                timing.Duration > length)
                notes.Add($"{face.Entry} runs {timing.Duration} frames and {entry.Name} {length}: " +
                          "the face starts over whenever the animation does, before it finishes.");
        }

        if (attached.Count == 0)
            throw new InvalidDataException(existing.Count > 0
                ? $"The animation already plays a facial expression: {string.Join(", ", existing)}."
                : "The animation has no body animation to attach a face to.");
        if (existing.Count > 0)
            notes.Add($"Already playing a facial expression, so left as they are: {string.Join(", ", existing)}.");
        notes.Add($"{string.Join(", ", attached)} now play{(attached.Count == 1 ? "s" : string.Empty)} {face.Entry}" +
                  (face.Pack == null ? "." : $" from the face pack '{face.Pack}'."));
        return file.WithTimelines(timelines);
    }
}
