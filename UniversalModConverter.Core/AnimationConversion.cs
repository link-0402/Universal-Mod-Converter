using System.Collections.Immutable;
using System.Security.Cryptography;
using System.Text.Json.Nodes;

namespace UniversalModConverter.Core;

public enum AnimationOperation
{
    /// <summary>Move the animation to another slot or emote of the same race.</summary>
    Swap,

    /// <summary>Rebuild the animation for the skeleton of other races.</summary>
    Retarget,

    /// <summary>Only attach a facial expression; the animation stays where it is.</summary>
    Expression,
}

/// <summary>
/// One destination of a swap: which location (see <see cref="PapPath.Location"/>) each source
/// location moves to. A single variant is a plain replacement; several become the options of
/// one Penumbra group.
/// </summary>
public sealed record AnimationSwapVariant(string Label, ImmutableDictionary<string, string> Locations)
{
    /// <summary>
    /// What each source location is for, in the words the game uses: "start", "looping",
    /// "ground sitting". Only used to make messages readable, so it may be incomplete.
    /// </summary>
    public ImmutableDictionary<string, string> SourceRoles { get; init; } =
        ImmutableDictionary<string, string>.Empty;
}

public sealed record AnimationConversionRequest(
    ImmutableArray<string> SourceLocations,
    AnimationOperation Operation,
    ConversionOutputMode Mode,
    string Description)
{
    /// <summary>Swap: the destinations. Exactly one unless <see cref="GroupName"/> is set.</summary>
    public ImmutableArray<AnimationSwapVariant> Variants { get; init; } = [];

    /// <summary>Swap: create a single-select group with one option per variant.</summary>
    public string? GroupName { get; init; }

    /// <summary>Swap into a group: the option selected by default.</summary>
    public int DefaultVariant { get; init; }

    /// <summary>
    /// Swap into a group: where the animation comes from when several containers of the mod hold
    /// their own version of it. One group can hold only one version per slot.
    /// </summary>
    public ContainerAddress? SourceContainer { get; init; }

    /// <summary>Swap without a group: keep the source slot as well instead of moving it.</summary>
    public bool KeepOriginal { get; init; }

    /// <summary>Retarget: the race whose files are converted.</summary>
    public ushort SourceRace { get; init; }

    /// <summary>Retarget: the races to build the animation for.</summary>
    public ImmutableArray<ushort> TargetRaces { get; init; } = [];

    /// <summary>
    /// Any operation: a facial expression to attach to every animation the conversion writes.
    /// With <see cref="AnimationOperation.Expression"/> it is the whole conversion.
    /// </summary>
    public ExpressionDonor? Expression { get; init; }
}

/// <summary>Rebuilds a PAP's body animations for another race's skeleton.</summary>
public interface IAnimationRetargeter
{
    /// <summary>Why retargeting cannot run in this game build, or null.</summary>
    string? UnavailableReason { get; }

    RetargetedPap Retarget(byte[] pap, byte[] sourceSkeleton, byte[] targetSkeleton, ushort targetRace);
}

public sealed record RetargetedPap(byte[] Bytes, ImmutableArray<string> Notes);

/// <summary>A file the plan produces, used to verify the published mod.</summary>
public sealed record AnimationOutput(string Scope, string GamePath, string Local, string Hash);

public sealed class AnimationConversionPlan : ModFilePlan
{
    internal AnimationConversionPlan(AnimationConversionRequest request, PenumbraMod result) : base(result)
    {
        Request = request;
    }

    public AnimationConversionRequest Request { get; }

    public override ConversionOutputMode Mode => Request.Mode;

    public List<AnimationOutput> Outputs { get; } = [];

    internal static string Hash(byte[] bytes) => Convert.ToHexString(SHA256.HashData(bytes));
}

/// <summary>
/// Plans animation swaps and race retargets on the game paths a mod redirects.
/// <para>
/// A swap moves a PAP to another location of the same race. The game finds an animation by
/// its entry name, which the destination's timeline expects to be its own, so the entry and
/// the embedded timeline's motion name are renamed to the destination's, read from the game
/// file. A race with no file of its own there gets none: the game plays the file of a race it
/// inherits from and never asks for one of its own. The same holds for retargets.
/// </para>
/// <para>
/// A retarget rebuilds the PAP's body animations for each target race's base skeleton and
/// writes them to that race's path. The source skeleton is the one the PAP declares it was
/// authored for; skeletons the mod itself replaces are preferred over the game's.
/// </para>
/// </summary>
public sealed class AnimationConversionPlanner(
    IGameFileProvider game, Func<ushort, ushort?> parentRace, IAnimationRetargeter? retargeter)
{
    /// <summary>
    /// The option group an expression added to this mod goes into (numbered when the mod already
    /// has one): "-" plays the animation without the face, the other option with it.
    /// </summary>
    public const string ExpressionGroupName = "Facial expression";

    private readonly IGameFileProvider _game = game;
    private readonly Func<ushort, ushort?> _parentRace = parentRace;
    private readonly IAnimationRetargeter? _retargeter = retargeter;

    private sealed record Provider(ModContainer Container, string Key, string Local, string FullPath, PapPath Path);

    /// <summary>Plans one conversion on its own, finishing the mod definition as it goes.</summary>
    public AnimationConversionPlan Plan(string modDirectory, AnimationConversionRequest request)
    {
        var context = new ModPlanContext(modDirectory, request.Mode);
        var plan = Plan(context, request);
        context.RunFinalizers();
        return plan;
    }

    /// <summary>
    /// Plans one conversion into a shared context. The caller runs the finalizers once every
    /// conversion of the run has been planned.
    /// </summary>
    public AnimationConversionPlan Plan(ModPlanContext context, AnimationConversionRequest request)
    {
        if (context.Mode != request.Mode)
            throw new ArgumentException("The request and the planning context disagree about the output mode.",
                nameof(request));
        return new Session(this, context, request).Run();
    }

    /// <summary>
    /// The race whose animation a race plays for <paramref name="location"/>: the first race up
    /// its skeleton parents that has a file, according to <paramref name="has"/>.
    /// </summary>
    public static ushort? ResolvingRace(ushort race, Func<ushort, bool> has, Func<ushort, ushort?> parentRace)
    {
        var seen = new HashSet<ushort>();
        for (ushort? current = race; current is { } r && seen.Add(r); current = parentRace(r))
            if (has(r)) return r;
        return null;
    }

    private sealed class Session
    {
        private readonly AnimationConversionPlanner _owner;
        private readonly ModPlanContext _context;
        private readonly string _root;
        private readonly AnimationConversionRequest _request;
        private readonly PenumbraMod _mod;
        private readonly AnimationConversionPlan _plan;
        private readonly GearConversionPlanner.LocalAllocator _locals;
        private readonly HashSet<string> _sources;
        private readonly Dictionary<string, byte[]?> _gameCache = new(StringComparer.Ordinal);
        private readonly Dictionary<string, byte[]?> _localCache = new(StringComparer.OrdinalIgnoreCase);
        private readonly Dictionary<(string File, string Destination), string> _written = new();
        private readonly Dictionary<string, string> _hashes = new(StringComparer.OrdinalIgnoreCase);

        /// <summary>Expression timelines a face swap moved away from, for the orphan check.</summary>
        private readonly List<string> _movedTimelines = [];

        /// <summary>What the current swap is aiming at ("/Box"), for messages. Null while retargeting.</summary>
        private string? _destinationLabel;
        private int _races;

        public Session(AnimationConversionPlanner owner, ModPlanContext context, AnimationConversionRequest request)
        {
            _owner = owner;
            _context = context;
            _root = context.ModDirectory;
            _request = request;
            _mod = context.Source;
            _plan = new AnimationConversionPlan(request, context.Result);
            _locals = context.Locals;
            _sources = request.SourceLocations.ToHashSet(StringComparer.Ordinal);
        }

        private PenumbraMod Result => _plan.Result;

        public AnimationConversionPlan Run()
        {
            var providers = CollectProviders();
            _races = providers.Select(p => p.Path.Race).Distinct().Count();
            if (providers.Count == 0)
            {
                Block("empty_plan", "This mod does not replace the selected animation" +
                                    (_request.Operation == AnimationOperation.Retarget
                                        ? $" for {RaceNames.Describe(_request.SourceRace)}."
                                        : "."));
                return _plan;
            }

            // A face pack plays on the face skeleton and is itself the face: it can only move.
            if (providers.Any(p => p.Path.IsFacial) &&
                (_request.Operation != AnimationOperation.Swap || _request.Expression != null || _request.GroupName != null))
            {
                Block("facial_swap_only", "A facial expression can only be swapped to another expression, as a plain replacement.");
                return _plan;
            }

            switch (_request.Operation)
            {
                case AnimationOperation.Swap:     PlanSwap(providers); break;
                case AnimationOperation.Retarget: PlanRetarget(providers); break;
                default:                          PlanExpression(providers); break;
            }
            if (_plan.HasBlockers) return _plan;

            // Additive mode keeps every source key, so nothing is orphaned to begin with. Each
            // conversion of a run checks its own sources, once all of them have planned: a file
            // another conversion still uses stays.
            if (_request.Mode.EditsSourceMod())
                _context.AddFinalizer(_ => DeleteOrphans(providers.Select(p => p.Local).Concat(_movedTimelines)));
            else
                _context.AddFinalizerOnce("new-mod", mod =>
                {
                    ModGroupPruning.Prune(mod, _mod, group => _plan.Changes.Add(
                        new GearPlanChange("Group", group.Name, "option group", "not included (no converted animation)")));
                    mod.Meta.Remove("DefaultPreferredItems");
                    mod.Meta["Identifier"] = Guid.NewGuid().ToString();
                });
            return _plan;
        }

        private List<Provider> CollectProviders()
        {
            var providers = new List<Provider>();
            foreach (var container in _mod.Containers)
            {
                foreach (var (key, local) in container.FileEntries())
                {
                    if (!PapPath.TryParse(key, out var path) || !_sources.Contains(path.Location)) continue;
                    if (_request.Operation == AnimationOperation.Retarget && path.Race != _request.SourceRace) continue;
                    var full = PathSafety.ResolveRelative(_root, GamePath.ToLocal(local));
                    providers.Add(new Provider(container, key, local, full, path));
                }
                foreach (var (key, _) in container.SwapEntries())
                    if (PapPath.TryParse(key, out var swapped) && _sources.Contains(swapped.Location))
                        Warn("file_swap", $"{container.Label}: the file swap for {GamePath.Normalize(key)} is not converted.");
            }
            return providers;
        }

        // ── Swaps ───────────────────────────────────────────────────────────

        private void PlanSwap(List<Provider> providers)
        {
            var variants = _request.Variants;
            var grouped = _request.GroupName != null;
            if (variants.IsDefaultOrEmpty) { Block("no_destination", "Choose where the animation goes."); return; }
            if (!grouped && variants.Length != 1) { Block("no_destination", "A replacement needs exactly one destination."); return; }
            if (grouped && string.IsNullOrWhiteSpace(_request.GroupName)) { Block("group_name", "The option group needs a name."); return; }

            if (grouped)
            {
                PlanSwapGroup(providers, variants);
                return;
            }

            var variant = variants[0];
            _destinationLabel = variant.Label;
            foreach (var provider in providers)
            {
                if (!variant.Locations.TryGetValue(provider.Path.Location, out var destinationLocation))
                {
                    Warn("unpaired", Unpaired(variant, provider));
                    continue;
                }
                var destination = FromLocation(provider.Path.Race, destinationLocation);
                if (destination == null) continue;
                if (!GameHas(destination, variant.Label, provider,
                        _request.KeepOriginal ? "stays where it is." : "is removed from the mod with the rest.")) continue;
                if (SwapContent(provider, destination) is not { } local) continue;

                var container = Result.GetContainer(provider.Container.Address);
                var files = container.GetOrCreateFiles();
                if (GamePath.FindKey(files, destination.GamePath) != null && !_sources.Contains(destination.Location))
                    Warn("destination_replaced", $"{container.Label}: the mod's own {destination.GamePath} is replaced.");
                if (GamePath.FindKey(files, destination.GamePath) is { } stale) files.Remove(stale);
                files[destination.GamePath] = GamePath.ToLocal(local);
                _plan.Changes.Add(new GearPlanChange("Game path", container.Label, GamePath.Normalize(provider.Key), destination.GamePath));
            }
            if (providers.Any(p => p.Path.IsFacial)) SwapFaceTimelines(variant);

            if (!_request.KeepOriginal)
                foreach (var provider in providers)
                {
                    // Unpaired parts (a start without a counterpart) leave together with the rest.
                    var destinations = variant.Locations.GetValueOrDefault(provider.Path.Location);
                    if (destinations == provider.Path.Location) continue;
                    // A location that is also a destination was just rewritten; leave it.
                    if (variant.Locations.Values.Contains(provider.Path.Location)) continue;
                    var files = Result.GetContainer(provider.Container.Address).Files;
                    if (files != null && GamePath.FindKey(files, provider.Key) is { } key) files.Remove(key);
                }
        }

        /// <summary>
        /// Explains a source animation the destination has no equivalent of: /Box may simply
        /// have no start animation, in which case the mod's start animation has nowhere to go.
        /// </summary>
        private string Unpaired(AnimationSwapVariant variant, Provider provider)
        {
            var role    = variant.SourceRoles.GetValueOrDefault(provider.Path.Location);
            var missing = role == null ? $"counterpart for {provider.Path.Key}" : $"{role} animation of its own";
            var subject = role == null ? $"this mod's {provider.Path.Key}" : $"this mod's {role} animation";
            var outcome = _request.KeepOriginal
                ? "stays where it is instead of moving."
                : "has nothing to convert to and is removed from the mod. Everything else converts normally.";
            return $"{variant.Label} has no {missing}, so {subject} {outcome}";
        }

        /// <summary>
        /// A new single-select group, named as asked, with one option per slot that places the
        /// animation there, after an empty "-" that switches the group off. It outranks the mod's
        /// other groups, so its choice wins where they overlap. The group is added beside what the
        /// mod already has, which stays as it is: in this mod, or as the whole of a new one.
        /// </summary>
        private void PlanSwapGroup(List<Provider> providers, ImmutableArray<AnimationSwapVariant> variants)
        {
            if (_request.Mode == ConversionOutputMode.InPlace)
            {
                Block("group_in_place", OutputModeRules.GroupInPlace(_request.GroupName));
                return;
            }

            var sources = OneSourcePerPath(providers);
            if (_plan.HasBlockers) return;

            var name = ModGroupBuilder.UniqueName(Result, _request.GroupName!);
            var options = new JsonArray { ModGroupBuilder.Option(ModGroup.OffOptionName, string.Empty, new JsonObject()) };

            var labels = new HashSet<string>(StringComparer.OrdinalIgnoreCase) { ModGroup.OffOptionName };
            foreach (var variant in variants)
            {
                if (!labels.Add(variant.Label)) { Block("duplicate_option", $"Two options are named '{variant.Label}'."); return; }
                _destinationLabel = variant.Label;
                var files = new JsonObject();
                foreach (var provider in sources)
                {
                    if (!variant.Locations.TryGetValue(provider.Path.Location, out var destinationLocation)) continue;
                    var destination = FromLocation(provider.Path.Race, destinationLocation);
                    if (destination == null || !GameHas(destination, variant.Label, provider, "is left out of that option.")) continue;
                    if (SwapContent(provider, destination) is not { } local) continue;
                    if (GamePath.FindKey(files, destination.GamePath) != null)
                    {
                        Block("target_conflict", $"{variant.Label}: two files map to {destination.GamePath}.");
                        continue;
                    }
                    files[destination.GamePath] = GamePath.ToLocal(local);
                    _plan.Changes.Add(new GearPlanChange("Option", $"{name} / {variant.Label}",
                        GamePath.Normalize(provider.Key), destination.GamePath));
                }
                if (files.Count == 0) Warn("empty_option", $"The option '{variant.Label}' has no animation for any race the mod provides.");
                options.Add(ModGroupBuilder.Option(variant.Label, string.Empty, files));
            }

            var defaultVariant = Math.Clamp(_request.DefaultVariant, 0, Math.Max(0, variants.Length - 1));
            AddSingleGroup(name, "Choose which slot this animation replaces. Created by Universal Mod Converter.",
                defaultVariant + 1, options); // past the "-" option
        }

        /// <summary>
        /// One provider per game path, for an option group, whose options hold one file per game
        /// path. Where the mod fills a location with a different file in each of several
        /// containers, the chosen container's version is used; without a choice the plan asks for one.
        /// </summary>
        private List<Provider> OneSourcePerPath(List<Provider> providers)
        {
            var sources = new List<Provider>();
            foreach (var same in providers.GroupBy(p => p.Path.GamePath))
            {
                if (same.Select(p => GamePath.NormalizeLocal(p.Local)).Distinct(StringComparer.Ordinal).Count() == 1)
                {
                    sources.Add(same.First());
                    continue;
                }
                if (same.FirstOrDefault(p => p.Container.Address == _request.SourceContainer) is { } chosen)
                {
                    sources.Add(chosen);
                    continue;
                }
                Block("several_sources", $"{string.Join(", ", same.Select(p => $"'{p.Container.Label}'").Distinct())} each have " +
                                         $"their own {same.Key}, and one option group can hold only one of them. " +
                                         "Choose which one it uses under \"Take it from\".");
            }
            return sources;
        }

        /// <summary>
        /// Adds a single-select group that outranks the mod's other groups, so its choice wins where
        /// they overlap. <paramref name="options"/> starts with the empty "-" that switches it off.
        /// </summary>
        private void AddSingleGroup(string name, string description, int selected, JsonArray options)
            => _plan.Changes.Add(ModGroupBuilder.Add(Result, name, description, ModGroupBuilder.TopPriority(Result) + 1,
                "Single", selected, options));

        private readonly HashSet<string> _noRaceFile = new(StringComparer.Ordinal);

        /// <summary>
        /// Whether the game has a body animation of its own at <paramref name="destination"/>. It asks
        /// for a race's own file only where it has one; a race without one plays the file of a race it
        /// inherits from, so a file written for it would never load, and none is. Reported once per
        /// destination, with what becomes of the source's file (<paramref name="outcome"/>). Face packs
        /// are placed either way (see <see cref="RenameFace"/>).
        /// </summary>
        private bool GameHas(PapPath destination, string label, Provider provider, string outcome)
        {
            if (destination.IsFacial || Game(destination.GamePath) != null) return true;
            if (_noRaceFile.Add(destination.GamePath))
                Warn("no_race_file",
                    $"{RaceNames.Describe(destination.Race)} has no {label} animation of its own ({destination.Key}) in the game " +
                    $"and plays the one of a race it inherits from, so nothing is written there for it; this mod's " +
                    $"{provider.Path.Key} for it {outcome}");
            return false;
        }

        /// <summary>Writes the source PAP renamed for <paramref name="destination"/>; returns its local path.</summary>
        private string? SwapContent(Provider provider, PapPath destination)
        {
            if (_written.TryGetValue((provider.FullPath, destination.GamePath), out var known)) return known;
            if (Local(provider.FullPath) is not { } bytes) return null;

            byte[] content;
            try
            {
                content = RenameFor(bytes, provider.Path, destination);
            }
            catch (InvalidDataException ex)
            {
                Block("swap_failed", $"{GamePath.Normalize(provider.Key)}: {ex.Message}");
                return null;
            }
            if (WithExpression(content, destination) is not { } expressed) return null;
            content = expressed;

            var wanted = SwapLocal(provider, destination);
            string local;
            if (_request.Mode.EditsSourceMod() && content.AsSpan().SequenceEqual(bytes) &&
                string.Equals(GamePath.NormalizeLocal(wanted), GamePath.NormalizeLocal(provider.Local), StringComparison.Ordinal))
                local = GamePath.ToLocal(provider.Local); // The slot keeps its own, unchanged file.
            else
                local = Write(content, wanted, provider, $"renamed for {destination.Key}");
            _written[(provider.FullPath, destination.GamePath)] = local;
            _plan.Outputs.Add(new AnimationOutput(provider.Container.Label, destination.GamePath, local, AnimationConversionPlan.Hash(content)));
            return local;
        }

        /// <summary>
        /// Where a swapped file goes. A mod that lays its files out like the game paths
        /// (<c>…/chara/human/c0101/animation/…/emote/pose03_loop.pap</c>) gets the destination's
        /// game path under the same prefix; otherwise the file stays next to the source, named
        /// after the destination, in a race folder when several races would share that name.
        /// </summary>
        private string SwapLocal(Provider provider, PapPath destination)
        {
            if (Mirrored(provider.Local, provider.Path.GamePath, destination.GamePath) is { } mirrored) return mirrored;

            var folder = Path.GetDirectoryName(GamePath.ToLocal(provider.Local)) ?? string.Empty;
            if (_races > 1 || destination.IsFacial) folder = Path.Combine(folder, $"c{destination.Race:D4}");
            // A race has a pack per face animation set, all named after the pose.
            if (destination.IsFacial) folder = Path.Combine(folder, destination.Set);
            return Path.Combine(folder, Path.GetFileName(destination.Key) + ".pap");
        }

        /// <summary>
        /// Renames the animations the source location plays to the names the destination
        /// location plays. Which entry a location plays is named by its action timeline
        /// (<c>chara/action/{key}.tmb</c>); a PAP may hold more, such as the hit reaction
        /// <c>resident/idle.pap</c> carries next to the idle. Those are left alone.
        /// </summary>
        private byte[] RenameFor(byte[] bytes, PapPath source, PapPath destination)
        {
            if (source.IsFacial) return RenameFace(bytes, source, destination);
            var pap = new PapFile(bytes);
            var body = pap.BodyEntries.ToList();
            if (body.Count == 0) throw new InvalidDataException("The file has no body animation to move.");

            // Swaps only write where the game has a file of its own (see GameHas), which names the animations.
            if (Game(destination.GamePath) is not { } game)
                throw new InvalidDataException(
                    $"The game has no animation at {destination.GamePath}, so there is no name to give the converted animation.");
            var destinationBody = new PapFile(game).BodyEntries.ToList();

            var from = Played(source, body);
            var to = Played(destination, destinationBody);
            if (from.Count != to.Count)
                throw new InvalidDataException($"It plays {from.Count} body animation(s) but {destination.Key} plays {to.Count}; " +
                                               "the animations cannot be paired.");
            if (from.Count > 1)
                Warn("multiple_animations", $"{source.Key} plays {from.Count} body animations; they are paired with {destination.Key}'s in order.");

            var entries = new Dictionary<int, string>();
            var motions = new Dictionary<string, string>(StringComparer.Ordinal);
            for (var i = 0; i < from.Count; i++)
            {
                if (from[i].Entry.Name == to[i].Entry.Name) continue;
                entries[from[i].Index] = to[i].Entry.Name;
                if (motions.TryGetValue(from[i].Entry.Name, out var previous) && previous != to[i].Entry.Name)
                    throw new InvalidDataException($"The animation name '{from[i].Entry.Name}' is used twice with different destinations.");
                motions[from[i].Entry.Name] = to[i].Entry.Name;
            }
            if (entries.Count == 0) return bytes;
            var names = pap.Entries.Select((e, i) => entries.GetValueOrDefault(i, e.Name)).ToList();
            if (names.Distinct(StringComparer.Ordinal).Count() != names.Count)
                throw new InvalidDataException($"Renaming would give two animations in the file the same name ({string.Join(", ", names)}).");

            var renamed = new PapFile(bytes).WithEntryNames(entries);
            // Timelines that do not reference their own animation are left as they are.
            var referenced = PapTimeline.ReadStrings(renamed).Where(s => s.IsMotion).Select(s => s.Value).ToHashSet(StringComparer.Ordinal);
            var applicable = motions.Where(m => referenced.Contains(m.Key)).ToDictionary(m => m.Key, m => m.Value, StringComparer.Ordinal);
            return applicable.Count == 0 ? renamed : PapTimeline.RenameMotions(renamed, applicable);
        }

        /// <summary>
        /// A facial expression's pack moved to another pose: the game plays <c>cfxf_{pose}</c> from
        /// <c>nonresident/{pose}.pap</c>, so the face and its timeline are renamed to the
        /// destination pose. Other faces the pack holds are left alone. A pose the game keeps in
        /// the shared resident pack cannot be a destination: that pack holds every face at once.
        /// </summary>
        private byte[] RenameFace(byte[] bytes, PapPath source, PapPath destination)
        {
            var from = GameExpressions.EntryName(source.Key);
            var to = GameExpressions.EntryName(destination.Key);
            var pap = new PapFile(bytes);
            var index = pap.Entries.ToList().FindIndex(e => e.Name == from);
            if (index < 0)
                throw new InvalidDataException($"It holds no {from}, the face the game plays for {source.Key}, so there is no face to move.");

            if (Game(destination.GamePath) == null)
            {
                var resident = Game(GameExpressions.ResidentPack(destination.Race, destination.Set));
                var shared = false;
                try { shared = resident != null && new PapFile(resident).Entries.Any(e => e.Name == to); }
                catch (InvalidDataException) { /* unreadable: not known to be shared */ }
                if (shared)
                    throw new InvalidDataException(
                        $"{_destinationLabel ?? destination.Key} is kept in {RaceNames.Describe(destination.Race)}'s shared face pack " +
                        $"({destination.Set}/resident/face.pap) with every other face, so it cannot be replaced on its own.");
                Note("no_game_face",
                    $"The game has no {_destinationLabel ?? destination.Key} of its own for {RaceNames.Describe(destination.Race)} " +
                    $"in {destination.Set}; the face is placed there anyway.");
            }

            if (from == to) return bytes;
            if (pap.Entries.Any(e => e.Name == to))
                throw new InvalidDataException($"It already holds {to} beside {from}, so renaming would give both the same name.");
            var renamed = pap.WithEntryNames(new Dictionary<int, string> { [index] = to });
            return PapTimeline.ReadStrings(renamed).Any(s => s.IsMotion && s.Value == from)
                ? PapTimeline.RenameMotions(renamed, new Dictionary<string, string> { [from] = to })
                : renamed;
        }

        /// <summary>
        /// <paramref name="local"/> moved along with its file: when it spells out the game path it
        /// replaces, as whole folders (<c>mymod\chara\...</c>), that part becomes <paramref name="to"/>.
        /// Null when it does not, and the caller picks a name of its own.
        /// </summary>
        private static string? Mirrored(string local, string from, string to)
        {
            local = GamePath.ToLocal(local);
            var suffix = GamePath.ToLocal(from);
            return local.EndsWith(suffix, StringComparison.OrdinalIgnoreCase) &&
                   (local.Length == suffix.Length || local[local.Length - suffix.Length - 1] == '\\')
                ? local[..^suffix.Length] + GamePath.ToLocal(to)
                : null;
        }

        /// <summary>
        /// The expression's own timeline (<c>chara/action/facial/pose/{pose}.tmb</c>), which names
        /// the face's pack and sets its pace, moves with the face: in every container that has
        /// one, so options such as speed choices keep working at the new pose.
        /// </summary>
        private void SwapFaceTimelines(AnimationSwapVariant variant)
        {
            var poses = new Dictionary<string, string>(StringComparer.Ordinal);
            foreach (var (from, to) in variant.Locations)
                if (PapPath.TryParse($"chara/human/c0101/animation/{from}.pap", out var source) && source.IsFacial &&
                    PapPath.TryParse($"chara/human/c0101/animation/{to}.pap", out var destination) && destination.IsFacial)
                    poses.TryAdd(source.Key, destination.Key);

            foreach (var (fromPose, toPose) in poses.Where(p => p.Key != p.Value))
            {
                var sourcePath = GameExpressions.TimelinePath(fromPose);
                var destinationPath = GameExpressions.TimelinePath(toPose);
                var moved = false;
                foreach (var container in _mod.Containers)
                foreach (var (key, local) in container.FileEntries())
                {
                    if (GamePath.Normalize(key) != sourcePath) continue;
                    moved = true;
                    var full = PathSafety.ResolveRelative(_root, GamePath.ToLocal(local));
                    if (Local(full) is not { } bytes) continue;

                    byte[] content;
                    try
                    {
                        var timeline = TmbTimeline.Parse(bytes);
                        timeline.RenameMotions(new Dictionary<string, string>
                            { [GameExpressions.EntryName(fromPose)] = GameExpressions.EntryName(toPose) });
                        if (timeline.FacePack == fromPose) timeline.FacePack = toPose;
                        content = timeline.ToArray();
                    }
                    catch (InvalidDataException ex)
                    {
                        Block("timeline_swap_failed", $"{container.Label}: {sourcePath}: {ex.Message}");
                        continue;
                    }

                    var sourceLocal = GamePath.ToLocal(local);
                    var wanted = Mirrored(sourceLocal, sourcePath, destinationPath)
                                 ?? Path.Combine(Path.GetDirectoryName(sourceLocal) ?? string.Empty, toPose + ".tmb");
                    var written = _locals.Reserve(wanted, null);
                    _plan.Files.Add(new PlannedFileOperation(LocalFileOperation.Write, Path.GetRelativePath(_root, full), written, content,
                        $"moved to {destinationPath}"));
                    _movedTimelines.Add(sourceLocal);

                    var files = Result.GetContainer(container.Address).GetOrCreateFiles();
                    if (GamePath.FindKey(files, destinationPath) is { } stale)
                    {
                        Warn("destination_replaced", $"{container.Label}: the mod's own {destinationPath} is replaced.");
                        files.Remove(stale);
                    }
                    files[destinationPath] = GamePath.ToLocal(written);
                    if (!_request.KeepOriginal && GamePath.FindKey(files, sourcePath) is { } old) files.Remove(old);
                    _plan.Outputs.Add(new AnimationOutput(container.Label, destinationPath, written, AnimationConversionPlan.Hash(content)));
                    _plan.Changes.Add(new GearPlanChange("Game path", container.Label, sourcePath, destinationPath));
                }
                if (!moved)
                    Note("no_face_timeline",
                        $"The mod has no timeline of its own for {fromPose}, so {_destinationLabel ?? toPose} plays the face " +
                        "at the pace the game plays that expression.");
            }
        }

        /// <summary>
        /// The entries of <paramref name="body"/> the action timeline of <paramref name="path"/>
        /// plays, in file order; every body entry when the timeline is unknown or names none.
        /// </summary>
        private List<(PapFile.Entry Entry, int Index)> Played(PapPath path, List<(PapFile.Entry Entry, int Index)> body)
        {
            var timeline = $"chara/action/{AnimationKeys.TimelineKey(path.Key)}.tmb";
            var bytes = ModFile(timeline) ?? Game(timeline);
            if (bytes == null) return body;
            try
            {
                var motions = PapTimeline.ReadActionTimeline(bytes).Where(s => s.IsMotion).Select(s => s.Value).ToHashSet(StringComparer.Ordinal);
                var played = body.Where(e => motions.Contains(e.Entry.Name)).ToList();
                return played.Count > 0 ? played : body;
            }
            catch (InvalidDataException)
            {
                return body;
            }
        }

        /// <summary>A file the mod itself provides for <paramref name="gamePath"/>, default files first.</summary>
        private byte[]? ModFile(string gamePath)
        {
            foreach (var container in _mod.Containers)
            foreach (var (key, local) in container.FileEntries())
                if (GamePath.Normalize(key) == gamePath)
                    return Local(PathSafety.ResolveRelative(_root, GamePath.ToLocal(local)));
            return null;
        }

        // ── Retargets ───────────────────────────────────────────────────────

        private void PlanRetarget(List<Provider> providers)
        {
            var targets = _request.TargetRaces.IsDefault ? [] : _request.TargetRaces.Where(r => r != _request.SourceRace).Distinct().ToList();
            if (targets.Count == 0) { Block("no_target_race", "Choose at least one race other than the source race."); return; }
            if (_owner._retargeter is not { } retargeter) { Block("retarget_unavailable", "Retargeting is not available."); return; }
            if (retargeter.UnavailableReason is { } reason) { Block("retarget_unavailable", reason); return; }

            foreach (var target in targets)
            {
                // The game asks for a race's own file only where it has one; a race without one
                // plays the file of a race it inherits from, so a file written for it never loads.
                var own = providers.Where(p => Game(p.Path.WithRace(target).GamePath) != null).ToList();
                foreach (var missing in providers.Except(own).Select(p => p.Path.Key).Distinct())
                    Warn("no_race_file",
                        $"{RaceNames.Describe(target)} has no {missing} of its own in the game and plays the one of a race it " +
                        "inherits from, so none is written for it.");
                if (own.Count == 0 || Skeleton(target) is not { } targetSkeleton) continue;
                foreach (var provider in own)
                {
                    if (Local(provider.FullPath) is not { } bytes) continue;
                    var destination = provider.Path.WithRace(target);
                    PapFile pap;
                    try { pap = new PapFile(bytes); }
                    catch (InvalidDataException ex) { Block("invalid_pap", $"{GamePath.Normalize(provider.Key)}: {ex.Message}"); continue; }

                    // Bindings index the skeleton the file was authored for, which is not always
                    // the race of the path it is placed at.
                    var authored = pap.ModelType == 0 && GenderRaces.Playable.Contains(pap.ModelId) ? pap.ModelId : provider.Path.Race;
                    if (authored != provider.Path.Race)
                        Note("authored_race", $"{GamePath.Normalize(provider.Key)} was made for c{authored:D4}; that skeleton is used as the source.");
                    if (Skeleton(authored) is not { } sourceSkeleton) continue;

                    if (_written.TryGetValue((provider.FullPath, destination.GamePath), out var done))
                    {
                        MapRetargeted(provider, destination, done);
                        continue;
                    }

                    byte[] content;
                    if (authored == target)
                        content = bytes;
                    else
                    {
                        try
                        {
                            var retargeted = retargeter.Retarget(bytes, sourceSkeleton, targetSkeleton, target);
                            content = retargeted.Bytes;
                            foreach (var note in retargeted.Notes)
                                Warn("retarget_note",
                                    $"{destination.Key} ({RaceNames.Name(authored)} to {RaceNames.Name(target)}): {note}");
                        }
                        catch (Exception ex) when (ex is InvalidDataException or InvalidOperationException)
                        {
                            Block("retarget_failed", $"{GamePath.Normalize(provider.Key)} → c{target:D4}: {ex.Message}");
                            continue;
                        }
                    }

                    if (WithExpression(content, destination) is not { } expressed) continue;
                    content = expressed;
                    var local = Write(content, RaceLocal(GamePath.ToLocal(provider.Local), provider.Path.Race, target), provider,
                        authored == target
                            ? "copied, already made for this race"
                            : $"rebuilt for {RaceNames.Name(target)} from {RaceNames.Name(authored)}");
                    _written[(provider.FullPath, destination.GamePath)] = local;
                    _hashes[local] = AnimationConversionPlan.Hash(content);
                    MapRetargeted(provider, destination, local);
                }
            }
            if (_plan.HasBlockers) return;
            DescribeInheritance(providers, targets);
        }

        /// <summary>
        /// Attaching a face on its own: every animation keeps its game path and its timeline gets
        /// the face. In place and in a new mod the file is edited; added to this mod, the edited
        /// animations go into an option group beside the originals.
        /// </summary>
        private void PlanExpression(List<Provider> providers)
        {
            if (_request.Expression == null) { Block("no_expression", "Choose the expression to attach."); return; }
            if (_request.Mode.KeepsSource())
            {
                PlanExpressionGroup(providers, _request.Expression.Label);
                return;
            }

            // One output file per distinct result. Every container, race and location that plays a
            // file with the same face shares its edited version; one that needs the file edited
            // differently (another race's face pack, another animation in it) gets its own copy,
            // since a file can only hold one of them.
            var edited = new Dictionary<(string File, string Hash), string>();
            var editedInPlace = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            foreach (var provider in providers)
            {
                if (Local(provider.FullPath) is not { } bytes) continue;
                if (WithExpression(bytes, provider.Path) is not { } content) continue;

                var hash = AnimationConversionPlan.Hash(content);
                var relative = GamePath.ToLocal(provider.Local);
                if (!edited.TryGetValue((provider.FullPath, hash), out var local))
                {
                    if (_request.Mode.EditsSourceMod() && editedInPlace.Add(provider.FullPath))
                    {
                        // The same file, edited: the key keeps pointing at it.
                        local = _locals.Reserve(relative, relative);
                        _plan.Files.Add(new PlannedFileOperation(LocalFileOperation.Write, null, local, content,
                            $"expression {_request.Expression.Label} attached"));
                    }
                    else local = Write(content, relative, provider, $"expression {_request.Expression.Label} attached");
                    edited[(provider.FullPath, hash)] = local;
                }

                // A new mod's containers start out empty, so each maps its key again; in place only
                // a key whose file became a copy of its own changes.
                var files = Result.GetContainer(provider.Container.Address).GetOrCreateFiles();
                if (!_request.Mode.EditsSourceMod() ||
                    !string.Equals(GamePath.NormalizeLocal(local), GamePath.NormalizeLocal(provider.Local), StringComparison.Ordinal))
                    files[GamePath.FindKey(files, provider.Path.GamePath) ?? provider.Path.GamePath] = GamePath.ToLocal(local);

                _written[(provider.FullPath, provider.Path.GamePath)] = local;
                _plan.Outputs.Add(new AnimationOutput(provider.Container.Label, provider.Path.GamePath, local, hash));
                _plan.Changes.Add(new GearPlanChange("Expression", provider.Container.Label,
                    GamePath.Normalize(provider.Key), _request.Expression.Label));
            }
        }

        /// <summary>
        /// Attaching a face beside the original: a new single-select group whose option plays the
        /// edited animations, after an empty "-" that plays the originals. The group outranks the
        /// mod's other groups, so its choice wins over the containers the originals live in, which
        /// stay as they are; it starts on the face, since that is what was asked for.
        /// </summary>
        private void PlanExpressionGroup(List<Provider> providers, string label)
        {
            var sources = OneSourcePerPath(providers);
            if (_plan.HasBlockers) return;

            var name = ModGroupBuilder.UniqueName(Result, ExpressionGroupName);
            var scope = $"{name} / {label}";
            var files = new JsonObject();
            var edited = new Dictionary<(string File, string Hash), string>();
            foreach (var provider in sources)
            {
                if (Local(provider.FullPath) is not { } bytes) continue;
                if (WithExpression(bytes, provider.Path) is not { } content) continue;

                var hash = AnimationConversionPlan.Hash(content);
                if (!edited.TryGetValue((provider.FullPath, hash), out var local))
                    edited[(provider.FullPath, hash)] = local =
                        Write(content, GamePath.ToLocal(provider.Local), provider, $"expression {label} attached");
                files[provider.Path.GamePath] = GamePath.ToLocal(local);
                _plan.Outputs.Add(new AnimationOutput(scope, provider.Path.GamePath, local, hash));
                _plan.Changes.Add(new GearPlanChange("Expression", scope, GamePath.Normalize(provider.Key), label));
            }
            if (files.Count == 0) return;

            AddSingleGroup(name, "Plays the animation with the expression; \"-\" plays it without. Created by Universal Mod Converter.",
                1, new JsonArray
                {
                    ModGroupBuilder.Option(ModGroup.OffOptionName, string.Empty, new JsonObject()),
                    ModGroupBuilder.Option(label, string.Empty, files),
                });
        }

        private readonly Dictionary<ushort, FacialAnimation?> _faces = new();

        /// <summary>
        /// The face <paramref name="race"/> plays, found once. A game expression is looked up in
        /// that race's own face animations, or the nearest skeleton parent's when the race has
        /// none; another mod's face is played by name. Null (and reported) when there is none.
        /// </summary>
        private FacialAnimation? Face(ushort race)
        {
            if (_faces.TryGetValue(race, out var known)) return known;
            var expression = _request.Expression!;
            var face = expression.Face;
            if (expression.Pose is { } pose)
                face = ResolvingRace(race, r => GameExpressions.Find(r, pose, Game) != null, _owner._parentRace) is { } owner
                    ? GameExpressions.Find(owner, pose, Game)
                    : null;

            // A face with no timing of its own plays as the game plays it as an emote, if it does.
            if (face is { Timing: null } && FaceTiming.ExpressionTimeline(face.Entry) is { } emote && Game(emote) is { } tmb)
            {
                try
                {
                    face = face with { Timing = TmbTimeline.Parse(tmb).TimingOf(face.Entry) };
                }
                catch (InvalidDataException)
                {
                    // Unreadable: the face holds its first frame instead.
                }
            }

            if (face == null)
                Block("expression_missing", $"The expression {expression.Label} could not be found for {RaceNames.Describe(race)}.");
            else if (expression.Pose == null)
            {
                if (_faces.Count == 0)
                    Note("expression_from_mod",
                        $"{expression.Label} plays from the face animations of the mod it comes from" +
                        (face.Pack == null ? string.Empty : $" (the face pack '{face.Pack}')") +
                        ": keep that mod enabled, or the face falls back to the game's own of that name, if there is one.");
                // Played by name from the character's own pack, or its skeleton parent's.
                var found = face;
                if (ResolvingRace(race, r => expression.PackRaces.Contains(r) || GameExpressions.Has(r, found, Game),
                        _owner._parentRace) == null)
                    Warn("expression_race_missing",
                        $"{expression.Label}: neither that mod nor the game has this face for {RaceNames.Describe(race)}, " +
                        "so that race shows no face.");
            }
            return _faces[race] = face;
        }

        /// <summary>
        /// The animation for <paramref name="path"/> with the chosen expression attached to what
        /// that path plays, or unchanged when none was chosen. Null when attaching failed, which
        /// blocks the plan.
        /// </summary>
        private byte[]? WithExpression(byte[] content, PapPath path)
        {
            if (_request.Expression is not { } expression) return content;
            if (Face(path.Race) is not { } face) return null;

            try
            {
                var notes = new List<string>();
                var played = Played(path, new PapFile(content).BodyEntries.ToList()).Select(e => e.Entry.Name).ToHashSet(StringComparer.Ordinal);
                var result = PapExpressions.Attach(content, face, played, notes);
                foreach (var note in notes) Note("expression_note", $"{path.Key}: {note}");
                return result;
            }
            catch (InvalidDataException ex)
            {
                Block("expression_failed", $"{path.Key}: attaching {expression.Label} failed: {ex.Message}");
                return null;
            }
        }

        private void MapRetargeted(Provider provider, PapPath destination, string local)
        {
            var container = Result.GetContainer(provider.Container.Address);
            var files = container.GetOrCreateFiles();
            if (GamePath.FindKey(files, destination.GamePath) is { } existing)
            {
                Warn("destination_replaced", $"{container.Label}: the mod's own {destination.GamePath} is replaced.");
                files.Remove(existing);
            }
            files[destination.GamePath] = GamePath.ToLocal(local);
            _plan.Outputs.Add(new AnimationOutput(container.Label, destination.GamePath, local, _hashes[local]));
            _plan.Changes.Add(new GearPlanChange("Retarget", container.Label, GamePath.Normalize(provider.Key), destination.GamePath));
        }

        /// <summary>
        /// Reports which other races will play a retargeted file: a race without its own file
        /// plays the one of the first race up its skeleton parents that has one. Only races the
        /// game has a file for get one written, so the game's files alone decide.
        /// </summary>
        private void DescribeInheritance(List<Provider> providers, List<ushort> targets)
        {
            foreach (var location in providers.Select(p => p.Path).DistinctBy(p => p.Location))
            {
                bool Has(ushort race) => Game(location.WithRace(race).GamePath) != null;
                foreach (var target in targets)
                {
                    var users = GenderRaces.Playable.Where(r => r != target && !targets.Contains(r) &&
                                                                ResolvingRace(r, Has, _owner._parentRace) == target).ToList();
                    if (users.Count > 0)
                        Note("inherited_by",
                            $"{location.Key}: {string.Join(", ", users.Select(RaceNames.Name))} have no animation of their own, " +
                            $"so the game plays the {RaceNames.Name(target)} version for them too.");
                }
            }
        }

        private byte[]? Skeleton(ushort race)
        {
            var path = PapPath.BaseSkeletonPath(race);
            foreach (var container in _mod.Containers)
            foreach (var (key, local) in container.FileEntries())
            {
                if (GamePath.Normalize(key) != path) continue;
                if (!container.Address.IsDefault)
                    Note("mod_skeleton",
                        $"The skeleton for {RaceNames.Describe(race)} is taken from the option {container.Label}.");
                return Local(PathSafety.ResolveRelative(_root, GamePath.ToLocal(local)));
            }
            if (Game(path) is { } bytes) return bytes;
            Block("missing_skeleton", $"The skeleton {path} was not found.");
            return null;
        }

        private static string RaceLocal(string local, ushort from, ushort to)
        {
            var token = $"c{from:D4}";
            var index = local.IndexOf(token, StringComparison.OrdinalIgnoreCase);
            if (index >= 0) return local[..index] + $"c{to:D4}" + local[(index + token.Length)..];
            return Path.Combine(Path.GetDirectoryName(local) ?? string.Empty, $"c{to:D4}", Path.GetFileName(local));
        }

        // ── Files ───────────────────────────────────────────────────────────

        private string Write(byte[] content, string wanted, Provider provider, string reason)
        {
            var local = _locals.Reserve(wanted, null);
            _plan.Files.Add(new PlannedFileOperation(LocalFileOperation.Write,
                Path.GetRelativePath(_root, provider.FullPath), local, content, reason));
            return local;
        }

        /// <summary>In place, source files no container references any more are removed.</summary>
        private void DeleteOrphans(IEnumerable<string> sources)
        {
            var referenced = Result.Containers.SelectMany(c => c.FileEntries())
                .Select(e => GamePath.NormalizeLocal(e.Local)).ToHashSet(StringComparer.Ordinal);
            foreach (var local in sources.Select(GamePath.ToLocal).Distinct(StringComparer.OrdinalIgnoreCase))
                if (!referenced.Contains(GamePath.NormalizeLocal(local)))
                    _plan.Files.Add(new PlannedFileOperation(LocalFileOperation.Delete, local, local, null, "no longer used"));
        }

        private PapPath? FromLocation(ushort race, string location)
        {
            if (PapPath.TryParse($"chara/human/c{race:D4}/animation/{location}.pap", out var path)) return path;
            Block("invalid_destination", $"'{location}' is not an animation location.");
            return null;
        }

        private byte[]? Local(string fullPath)
        {
            if (_localCache.TryGetValue(fullPath, out var cached)) return cached;
            byte[]? bytes = null;
            if (File.Exists(fullPath))
            {
                PathSafety.EnsureContained(_root, fullPath, requireExisting: true);
                bytes = File.ReadAllBytes(fullPath);
                _plan.InputFiles.Add(fullPath);
            }
            else Block("missing_local_file", $"{Path.GetRelativePath(_root, fullPath)} is referenced by the mod but does not exist.");
            _localCache[fullPath] = bytes;
            return bytes;
        }

        private byte[]? Game(string path)
        {
            if (_gameCache.TryGetValue(path, out var cached)) return cached;
            byte[]? bytes;
            try { bytes = _owner._game.ReadFile(path); }
            catch (Exception) { bytes = null; }
            return _gameCache[path] = bytes;
        }

        private void Note(string code, string message) => Warn(code, message);

        private void Warn(string code, string message) => _plan.Report(code, message, false);

        private void Block(string code, string message) => _plan.Report(code, message, true);
    }
}

/// <summary>Checks a published animation conversion: every planned output resolves to the planned bytes.</summary>
public static class AnimationConversionVerifier
{
    public static List<VerificationIssue> Verify(string modDirectory, AnimationConversionPlan plan)
    {
        var issues = new List<VerificationIssue>();
        var mod = PenumbraMod.Load(modDirectory);
        var mapped = mod.Containers.SelectMany(c => c.FileEntries().Select(e => (Path: GamePath.Normalize(e.Key), Local: GamePath.NormalizeLocal(e.Local))))
            .ToHashSet();
        foreach (var output in plan.Outputs)
        {
            if (!mapped.Contains((output.GamePath, GamePath.NormalizeLocal(output.Local))))
            {
                issues.Add(new VerificationIssue(true, $"{output.Scope}: {output.GamePath} does not point to {output.Local}."));
                continue;
            }
            var full = PathSafety.ResolveRelative(modDirectory, GamePath.ToLocal(output.Local));
            if (!File.Exists(full))
            {
                issues.Add(new VerificationIssue(true, $"{output.Local} (for {output.GamePath}) does not exist."));
                continue;
            }
            var bytes = File.ReadAllBytes(full);
            if (AnimationConversionPlan.Hash(bytes) != output.Hash)
                issues.Add(new VerificationIssue(true, $"{output.Local} differs from the previewed output."));
            try
            {
                if (output.GamePath.EndsWith(".tmb", StringComparison.Ordinal)) _ = TmbTimeline.Parse(bytes);
                else _ = new PapFile(bytes);
            }
            catch (InvalidDataException ex) { issues.Add(new VerificationIssue(true, $"{output.Local} cannot be read: {ex.Message}")); }
        }
        return issues;
    }
}
