// Source-skeleton ranking and the standard skeleton layouts adapted from XIV Instant Edit's
// animation editor, which adapted the ranking from VFXEditor's SkeletonMatcher (GPL-3.0).
// See THIRD_PARTY_NOTICES.md.
using System.Collections.Immutable;
using System.Numerics;

namespace UniversalModConverter.Core;

/// <summary>
/// What one body animation binds, by index into the skeleton it was made for: the bone of each
/// transform track, the float slot of each float track, and its partitions. Predictive and
/// quantized animations also record how many bones and float slots that skeleton had; quantized
/// ones only decode on the whole skeleton of the race they were made for.
/// </summary>
public sealed record AnimationChannels(string OriginalSkeleton, ImmutableArray<short> Bones, ImmutableArray<short> Floats,
    ImmutableArray<short> Partitions, int? ReferenceBones = null, int? ReferenceFloats = null, bool Quantized = false);

public enum SkeletonOrigin
{
    /// <summary>The game's own base skeleton of a race.</summary>
    Game,

    /// <summary>A base skeleton the converted mod itself replaces.</summary>
    ThisMod,

    /// <summary>A base skeleton another installed mod replaces, such as IVCS or YAS.</summary>
    Installed,
}

/// <summary>
/// The body skeleton layouts other players' skeletons share, each extending the one before it:
/// the game's own, then IVCS's <c>iv_</c> bones, then YAS's <c>ya_</c> bones. Animations bind
/// bones by index, and these are the indices other players' skeletons and sync plugins expect.
/// </summary>
public enum SkeletonStandard { Vanilla, Ivcs, IvcsYas }

/// <summary>
/// A skeleton an animation may have been made for, or may be rebuilt for: the races whose base
/// skeleton path it replaces, and where it comes from, named for messages.
/// </summary>
public sealed record SkeletonCandidate(SkeletonDescription Skeleton, SkeletonOrigin Origin, ImmutableArray<ushort> Races, string Label)
{
    /// <summary>How many files hold it: skeleton mods that are widely used come first among equals.</summary>
    public int Files { get; init; } = 1;

    /// <summary>The player's collection loads it for one of its races.</summary>
    public bool Live { get; init; }

    /// <summary>
    /// The options it is in, where its mods offer several skeletons for a race (such as YAS's
    /// "Posing"), to tell it from the others; otherwise null.
    /// </summary>
    public string? Variant { get; init; }

    /// <summary>
    /// Set when only the first bones of a longer skeleton are meant: a predictive animation made
    /// before a skeleton mod appended bones was made for exactly those.
    /// </summary>
    public int LeadingBones { get; init; }
}

/// <summary>
/// The skeleton an animation was made for, and the others that fit it just as well but would
/// move it differently (see <see cref="SkeletonMatcher.ChooseSource"/>).
/// </summary>
public sealed record SourceChoice(SkeletonCandidate Candidate, ImmutableArray<SkeletonCandidate> Rivals);

/// <summary>The skeleton an animation is rebuilt for, the standard it is, and the bones it moves that the skeleton lacks.</summary>
public sealed record TargetChoice(SkeletonCandidate Candidate, SkeletonStandard? Standard, ImmutableArray<string> Missing);

/// <summary>
/// Finds the skeleton an animation was made for, and the one to rebuild it for on another race.
/// <para>
/// Animations bind bones by index, so an animation made for a skeleton mod (IVCS, YAS or a larger
/// one) only fits skeletons with at least as many bones, while the game's own skeleton is often
/// far smaller. The source is the fitting skeleton that best matches the race the animation was
/// made for; the target is a standard layout, so the result plays for other players too.
/// </para>
/// </summary>
public static class SkeletonMatcher
{
    /// <summary>
    /// How many of the game's last bones a mod layout may lack at the game's indices. The game
    /// appended <c>n_hara_noanim_trans</c> after IVCS and YAS fixed their layouts; a small
    /// allowance covers such patch additions without accepting a rig that reshuffles the game's bones.
    /// </summary>
    private const int LateGameBones = 4;

    /// <summary>How far a sample may stray from the reference pose before a bone counts as moving (see <see cref="Moves"/>).</summary>
    private const float MotionTolerance = 1e-4f;
    private const float RotationTolerance = 1e-6f;

    /// <summary>Whether every binding's tracks, float tracks and partitions exist on the skeleton, as many as compressed data expects.</summary>
    public static bool Fits(IReadOnlyList<AnimationChannels> channels, SkeletonDescription skeleton)
    {
        foreach (var channel in channels)
        {
            if (!Indices(channel.Bones, skeleton.Bones.Length) || !Indices(channel.Floats, skeleton.FloatNames.Length) ||
                !Indices(channel.Partitions, skeleton.Partitions.Length))
                return false;
            if (channel.ReferenceBones is { } bones && bones != skeleton.Bones.Length) return false;
            if (channel.ReferenceFloats is { } floats && floats != skeleton.FloatNames.Length) return false;
        }
        return true;

        static bool Indices(ImmutableArray<short> indices, int count)
            => indices.All(i => i >= 0 && i < count) && indices.Distinct().Count() == indices.Length;
    }

    /// <summary>
    /// The skeleton the animation was made for among <paramref name="candidates"/>, or null when
    /// none fits. Among those that fit, it prefers, in order: one mapped to
    /// <paramref name="race"/>, the race it was made for, then one of its skeleton parents, then
    /// a child; one the converted mod replaces; one named as the animation says; one keeping more
    /// of the game's bones at the game's indices (<paramref name="game"/> is that race's own);
    /// one with the fewest bones the animation does not bind, as the animation's author would
    /// hardly use a larger skeleton than needed; and one the player's collection loads. Rivals
    /// are equally good candidates that rest differently where it matters.
    /// <para>
    /// Quantized animations only decode on the race's own whole skeleton. When nothing fits, a
    /// predictive animation is tried on the first bones of longer skeletons: skeleton mods add
    /// bones at the end, and one made before they did was made for exactly those.
    /// </para>
    /// </summary>
    public static SourceChoice? ChooseSource(IReadOnlyList<AnimationChannels> channels, IEnumerable<SkeletonCandidate> candidates,
        ushort race, SkeletonDescription? game, Func<ushort, ushort?> parentRace)
    {
        var quantized = channels.Any(c => c.Quantized);
        var allowed = candidates.Where(c => !quantized || c.Races.Contains(race)).ToList();
        var fitting = allowed.Where(c => Fits(channels, c.Skeleton)).ToList();
        if (fitting.Count == 0 && !quantized && channels.Select(c => c.ReferenceBones).FirstOrDefault(b => b != null) is { } leading)
            fitting = [.. allowed.Select(c => Leading(c, leading)).OfType<SkeletonCandidate>().Where(c => Fits(channels, c.Skeleton))];
        if (fitting.Count == 0) return null;

        var ranked = fitting.Select(c => (Candidate: c, Rank: new SourceRank(ModelScore(c.Races, race, parentRace),
                c.Origin == SkeletonOrigin.ThisMod, NameMatches(channels, c.Skeleton), Overlap(game, c.Skeleton),
                -ReferenceOnly(channels, c.Skeleton), c.Live)))
            .OrderByDescending(r => r.Rank)
            .ThenByDescending(r => r.Candidate.Files)
            .ThenBy(r => r.Candidate.Origin)
            .ThenBy(r => r.Candidate.Label, StringComparer.Ordinal)
            .ToList();
        var best = ranked[0];
        return new SourceChoice(best.Candidate, [.. ranked.Skip(1)
            .Where(r => r.Rank.CompareTo(best.Rank) == 0 && !Alike(best.Candidate.Skeleton, r.Candidate.Skeleton, channels))
            .Select(r => r.Candidate)]);
    }

    /// <summary>
    /// The skeleton to rebuild an animation for on another race: the standard layout, of
    /// <paramref name="standards"/> (see <see cref="Standards"/>), that has every bone the
    /// animation moves (<paramref name="moving"/>, by name), the smallest first; when none has
    /// all of them, the one lacking the fewest. Lacking bones are left out: their motion is
    /// carried by the bones below them where possible.
    /// </summary>
    public static TargetChoice ChooseTarget(IReadOnlyList<(SkeletonStandard Standard, SkeletonCandidate Candidate)> standards,
        IReadOnlySet<string> moving)
    {
        if (standards.Count == 0) throw new InvalidDataException("There is no skeleton to rebuild the animation for.");
        return standards
            .Select(s => new TargetChoice(s.Candidate, s.Standard, Missing(s.Candidate.Skeleton, moving)))
            .OrderBy(c => c.Missing.Length)
            .ThenBy(c => c.Standard)
            .First();
    }

    /// <summary>
    /// The game's bones an animation rebuilt for a standard layout leaves without a track: those
    /// skeleton mods do not keep at the game's index. The game appended them after IVCS and YAS
    /// fixed their layouts (<c>n_hara_noanim_trans</c>), so on those skeletons another bone sits
    /// at that index (a fingertip, or the first tail bone), and a track bound there moves it with
    /// this bone's motion instead: a finger stretched to the floor, for most players. The game's
    /// own animations never track these bones (hence "noanim"), so at rest they change nothing on
    /// its skeleton. Found in <paramref name="layouts"/>, the race's installed skeletons laid out
    /// as a standard (see <see cref="IsStandardLayout"/>), and by name for when none is installed.
    /// </summary>
    public static ImmutableHashSet<string> UnportableBones(SkeletonDescription game, IEnumerable<SkeletonDescription> layouts)
    {
        var names = game.Bones.Select(b => b.Name).ToArray();
        var result = names.Where(name => name.Contains("_noanim", StringComparison.Ordinal)).ToHashSet(StringComparer.Ordinal);
        foreach (var layout in layouts.Where(l => IsStandardLayout(l, game, SkeletonStandard.Ivcs) ||
                                                  IsStandardLayout(l, game, SkeletonStandard.IvcsYas)))
            for (var i = 0; i < names.Length; i++)
                if (i >= layout.Bones.Length || layout.Bones[i].Name != names[i])
                    result.Add(names[i]);
        return result.ToImmutableHashSet(StringComparer.Ordinal);
    }

    /// <summary>The bones of <paramref name="moving"/> that <paramref name="skeleton"/> lacks, by name.</summary>
    public static ImmutableArray<string> Missing(SkeletonDescription skeleton, IReadOnlySet<string> moving)
    {
        var names = skeleton.Bones.Select(b => b.Name).ToHashSet(StringComparer.Ordinal);
        return [.. moving.Where(name => !names.Contains(name)).Order(StringComparer.Ordinal)];
    }

    /// <summary>
    /// The standard skeletons of a race: its game skeleton, then the best IVCS and IVCS + YAS
    /// layouts among <paramref name="installed"/> (skeletons mapped to the race's base path).
    /// Among several of one standard, the one keeping the most game bones at the game's indices
    /// wins, then the one with the most game bones, then the one whose game bones rest most like
    /// the game's, then one the player uses, then the one the most files share.
    /// </summary>
    public static ImmutableArray<(SkeletonStandard Standard, SkeletonCandidate Candidate)> Standards(SkeletonCandidate game,
        IEnumerable<SkeletonCandidate> installed)
    {
        var reference = game.Skeleton.Bones.ToDictionary(b => b.Name, b => b.Reference, StringComparer.Ordinal);
        int Leading(SkeletonDescription s) => s.Bones.Zip(game.Skeleton.Bones).TakeWhile(p => p.First.Name == p.Second.Name).Count();
        var usable = installed.ToList();
        var result = ImmutableArray.CreateBuilder<(SkeletonStandard, SkeletonCandidate)>();
        result.Add((SkeletonStandard.Vanilla, game));
        foreach (var standard in new[] { SkeletonStandard.Ivcs, SkeletonStandard.IvcsYas })
        {
            var best = usable.Where(c => IsStandardLayout(c.Skeleton, game.Skeleton, standard))
                .OrderByDescending(c => Leading(c.Skeleton))
                .ThenByDescending(c => c.Skeleton.Bones.Count(b => reference.ContainsKey(b.Name)))
                .ThenByDescending(c => c.Skeleton.Bones.Count(b => reference.TryGetValue(b.Name, out var rest) && b.Reference.Near(rest, 1e-6f)))
                .ThenByDescending(c => c.Live)
                .ThenByDescending(c => c.Files)
                .ThenBy(c => c.Label, StringComparer.Ordinal)
                .FirstOrDefault();
            if (best != null) result.Add((standard, best));
        }
        return result.ToImmutable();
    }

    /// <summary>
    /// Whether a bone belongs to a standard: the game's own bones, then IVCS's iv_ bones and the
    /// tail bones (n_sippo_) it gives every race, then YAS's ya_ bones. Seven races have no tail
    /// in the game's skeleton (Highlander, female Roegadyn, Lalafell, Viera); IVCS adds one.
    /// </summary>
    public static bool InStandard(string name, SkeletonStandard standard, IReadOnlySet<string> vanilla)
        => vanilla.Contains(name) ||
           standard >= SkeletonStandard.Ivcs && (name.StartsWith("iv_", StringComparison.Ordinal) ||
                                                  name.StartsWith("n_sippo_", StringComparison.Ordinal)) ||
           standard == SkeletonStandard.IvcsYas && name.StartsWith("ya_", StringComparison.Ordinal);

    /// <summary>
    /// Whether a skeleton is laid out as the standard: only bones of the standard's groups, with
    /// IVCS's (and for IVCS + YAS, YAS's) among them, and the game's bones leading at the game's
    /// own indices, except that up to <see cref="LateGameBones"/> of the game's last bones may be
    /// missing there or follow later.
    /// </summary>
    public static bool IsStandardLayout(SkeletonDescription skeleton, SkeletonDescription game, SkeletonStandard standard)
    {
        var gameNames = game.Bones.Select(b => b.Name).ToArray();
        var names = skeleton.Bones.Select(b => b.Name).ToArray();
        if (standard == SkeletonStandard.Vanilla) return names.SequenceEqual(gameNames, StringComparer.Ordinal);
        var vanilla = gameNames.ToHashSet(StringComparer.Ordinal);
        if (!names.All(name => InStandard(name, standard, vanilla)) ||
            !names.Any(name => name.StartsWith("iv_", StringComparison.Ordinal)) ||
            standard == SkeletonStandard.IvcsYas && !names.Any(name => name.StartsWith("ya_", StringComparison.Ordinal)))
            return false;
        var leading = names.TakeWhile(vanilla.Contains).Count();
        return leading >= gameNames.Length - LateGameBones && leading <= gameNames.Length &&
               names.Take(leading).SequenceEqual(gameNames.Take(leading), StringComparer.Ordinal);
    }

    /// <summary>
    /// Whether a sampled local transform differs from the bone's reference pose by more than
    /// float noise: 0.1 mm, or about a sixth of a degree.
    /// </summary>
    public static bool Moves(BoneTransform sample, BoneTransform reference)
        => Vector3.Distance(sample.Position, reference.Position) > MotionTolerance ||
           Vector3.Distance(sample.Scale, reference.Scale) > MotionTolerance ||
           1 - Math.Abs(Quaternion.Dot(Quaternion.Normalize(sample.Rotation), Quaternion.Normalize(reference.Rotation))) > RotationTolerance;

    /// <summary>
    /// <paramref name="candidate"/>'s first <paramref name="bones"/> bones as a candidate of their
    /// own, or null when it has no more bones than that or a partition spans the cut. Parents come
    /// before their children, so the leading bones keep a whole hierarchy.
    /// </summary>
    public static SkeletonCandidate? Leading(SkeletonCandidate candidate, int bones)
    {
        var skeleton = candidate.Skeleton;
        if (candidate.LeadingBones != 0 || bones < 1 || bones >= skeleton.Bones.Length ||
            skeleton.Partitions.Any(p => p.Start + p.Count > bones))
            return null;
        return candidate with { Skeleton = skeleton with { Bones = [.. skeleton.Bones.Take(bones)] }, LeadingBones = bones };
    }

    /// <summary>
    /// How well the races a candidate is mapped to match the race the animation was made for:
    /// that race, then its skeleton parents, the nearest first, then its children, the nearest first.
    /// </summary>
    private static int ModelScore(ImmutableArray<ushort> races, ushort race, Func<ushort, ushort?> parentRace)
        => races.IsDefaultOrEmpty ? 0 : races.Max(r =>
            r == race ? 300
            : Steps(race, r, parentRace) is { } up ? 200 - up
            : Steps(r, race, parentRace) is { } down ? 100 - down
            : 0);

    /// <summary>How many skeleton parents up from <paramref name="race"/> <paramref name="ancestor"/> is; null when it is none of them.</summary>
    private static int? Steps(ushort race, ushort ancestor, Func<ushort, ushort?> parentRace)
    {
        var seen = new HashSet<ushort> { race };
        var steps = 0;
        for (var current = parentRace(race); current is { } r && seen.Add(r); current = parentRace(r))
        {
            steps++;
            if (r == ancestor) return steps;
        }
        return null;
    }

    private static bool NameMatches(IReadOnlyList<AnimationChannels> channels, SkeletonDescription skeleton)
        => channels.All(c => c.OriginalSkeleton.Length > 0 && c.OriginalSkeleton == skeleton.Name);

    /// <summary>How many bones sit where the game's skeleton has the same bone under the same parent.</summary>
    private static int Overlap(SkeletonDescription? game, SkeletonDescription skeleton)
    {
        if (game == null) return 0;
        var count = 0;
        for (var i = 0; i < Math.Min(game.Bones.Length, skeleton.Bones.Length); i++)
            if (game.Bones[i].Name == skeleton.Bones[i].Name && game.Bones[i].Parent == skeleton.Bones[i].Parent) count++;
        return count;
    }

    private static int ReferenceOnly(IReadOnlyList<AnimationChannels> channels, SkeletonDescription skeleton)
        => skeleton.Bones.Length - channels.SelectMany(c => c.Bones).Distinct().Count();

    /// <summary>
    /// Whether two fitting skeletons move the animation alike: the same bones, under the same
    /// parents and at the same rest, wherever a track or one of its ancestors is, and the same
    /// float channels.
    /// </summary>
    private static bool Alike(SkeletonDescription a, SkeletonDescription b, IReadOnlyList<AnimationChannels> channels)
    {
        var bones = new HashSet<int>();
        foreach (var bone in channels.SelectMany(c => c.Bones))
            for (int i = bone; i >= 0 && bones.Add(i); i = a.Bones[i].Parent) { }
        return bones.All(i => a.Bones[i].Name == b.Bones[i].Name && a.Bones[i].Parent == b.Bones[i].Parent &&
                              a.Bones[i].Reference.Near(b.Bones[i].Reference, 1e-5f)) &&
               channels.SelectMany(c => c.Floats).Distinct().All(i => a.FloatNames[i] == b.FloatNames[i] &&
                                                                     Math.Abs(a.ReferenceFloats[i] - b.ReferenceFloats[i]) <= 1e-5f);
    }

    /// <summary>A candidate's standing as a source, compared field by field, the first deciding.</summary>
    private readonly record struct SourceRank(int Model, bool ThisMod, bool Name, int Overlap, int FewestUnbound, bool Live)
        : IComparable<SourceRank>
    {
        public int CompareTo(SourceRank other)
        {
            var result = Model.CompareTo(other.Model);
            if (result == 0) result = ThisMod.CompareTo(other.ThisMod);
            if (result == 0) result = Name.CompareTo(other.Name);
            if (result == 0) result = Overlap.CompareTo(other.Overlap);
            if (result == 0) result = FewestUnbound.CompareTo(other.FewestUnbound);
            if (result == 0) result = Live.CompareTo(other.Live);
            return result;
        }
    }
}
