using System.Collections.Immutable;
using System.Security.Cryptography;
using System.Text.Json.Nodes;

namespace AdvancedPenumbraModConverter.Core;

public enum AnimationOperation
{
    /// <summary>Move the animation to another slot or emote of the same race.</summary>
    Swap,

    /// <summary>Rebuild the animation for the skeleton of other races.</summary>
    Retarget,
}

/// <summary>
/// One destination of a swap: which location (see <see cref="PapPath.Location"/>) each source
/// location moves to. A single variant is a plain replacement; several become the options of
/// one Penumbra group.
/// </summary>
public sealed record AnimationSwapVariant(string Label, ImmutableDictionary<string, string> Locations);

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

    /// <summary>Swap without a group: keep the source slot as well instead of moving it.</summary>
    public bool KeepOriginal { get; init; }

    /// <summary>Retarget: the race whose files are converted.</summary>
    public ushort SourceRace { get; init; }

    /// <summary>Retarget: the races to build the animation for.</summary>
    public ImmutableArray<ushort> TargetRaces { get; init; } = [];
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

public sealed class AnimationConversionPlan : IModFilePlan
{
    internal AnimationConversionPlan(AnimationConversionRequest request, PenumbraMod result)
    {
        Request = request;
        Result = result;
    }

    public AnimationConversionRequest Request { get; }

    public PenumbraMod Result { get; }

    public List<PlannedFileOperation> Files { get; } = [];

    IReadOnlyList<PlannedFileOperation> IModFilePlan.Files => Files;

    public List<GearPlanChange> Changes { get; } = [];

    public List<PlanDiagnostic> Diagnostics { get; } = [];

    public List<AnimationOutput> Outputs { get; } = [];

    public HashSet<string> InputFiles { get; } = new(StringComparer.OrdinalIgnoreCase);

    public bool HasBlockers => Diagnostics.Any(d => d.IsBlocker);

    public string Fingerprint()
    {
        var operations = Files.Select(f => new ConversionOperation(f.Operation.ToString(), f.Source ?? string.Empty,
                f.Destination + (f.Content == null ? string.Empty : "#" + Hash(f.Content))))
            .Append(new ConversionOperation("definition", "result", Hash(System.Text.Encoding.UTF8.GetBytes(
                PenumbraMod.Serialize(Result.Meta) + PenumbraMod.Serialize(Result.Default.Node) +
                string.Concat(Result.Groups.Select(g => PenumbraMod.Serialize(g.Node)))))));
        return ModFingerprint.ComputePlan(operations);
    }

    internal static string Hash(byte[] bytes) => Convert.ToHexString(SHA256.HashData(bytes));
}

/// <summary>
/// Plans animation swaps and race retargets on the game paths a mod redirects.
/// <para>
/// A swap moves a PAP to another location of the same race. The game finds an animation by
/// its entry name, which the destination's timeline expects to be its own, so the entry and
/// the embedded timeline's motion name are renamed to the destination's (read from the game
/// file, following the race's skeleton parents when the race has no file of its own).
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
    private readonly IGameFileProvider _game = game;
    private readonly Func<ushort, ushort?> _parentRace = parentRace;
    private readonly IAnimationRetargeter? _retargeter = retargeter;

    private sealed record Provider(ModContainer Container, string Key, string Local, string FullPath, PapPath Path);

    public AnimationConversionPlan Plan(string modDirectory, AnimationConversionRequest request)
        => new Session(this, Path.GetFullPath(modDirectory), request).Run();

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
        private int _races;

        public Session(AnimationConversionPlanner owner, string root, AnimationConversionRequest request)
        {
            _owner = owner;
            _root = root;
            _request = request;
            _mod = PenumbraMod.Load(root);
            var result = request.Mode == ConversionOutputMode.NewMod ? _mod.CloneStructure() : _mod.Clone();
            _plan = new AnimationConversionPlan(request, result);
            _locals = new GearConversionPlanner.LocalAllocator(request.Mode == ConversionOutputMode.InPlace ? root : null);
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
                                        ? $" for c{_request.SourceRace:D4}."
                                        : "."));
                return _plan;
            }

            if (_request.Operation == AnimationOperation.Swap) PlanSwap(providers);
            else PlanRetarget(providers);
            if (_plan.HasBlockers) return _plan;

            if (_request.Mode == ConversionOutputMode.InPlace) DeleteOrphans(providers);
            else
            {
                ModGroupPruning.Prune(Result, group => _plan.Changes.Add(
                    new GearPlanChange("Group", group.Name, "option group", "not included (no converted animation)")));
                Result.Meta.Remove("DefaultPreferredItems");
                if (Result.Format == PenumbraModFormat.Unified) Result.Meta["Identifier"] = Guid.NewGuid().ToString();
                else Result.RenumberLegacyGroupFiles();
            }
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
                var elsewhere = providers.Where(p => !p.Container.Address.IsDefault).Select(p => p.Container.Label).Distinct().ToList();
                if (elsewhere.Count > 0)
                {
                    Block("variant_group_options",
                        $"The animation is provided by the option(s) {string.Join(", ", elsewhere)}. An option group of slots " +
                        "can only be built from animations in the mod's default files; use a straight replacement instead.");
                    return;
                }
                PlanSwapGroup(providers, variants);
                return;
            }

            var variant = variants[0];
            foreach (var provider in providers)
            {
                if (!variant.Locations.TryGetValue(provider.Path.Location, out var destinationLocation))
                {
                    Warn("unpaired", _request.KeepOriginal
                        ? $"{provider.Path.Key} has no counterpart in {variant.Label} and is left unchanged."
                        : $"{provider.Path.Key} has no counterpart in {variant.Label}; it moves with the rest and is removed.");
                    continue;
                }
                var destination = FromLocation(provider.Path.Race, destinationLocation);
                if (destination == null) continue;
                if (SwapContent(provider, destination) is not { } local) continue;

                var container = Result.GetContainer(provider.Container.Address);
                var files = container.GetOrCreateFiles();
                if (FindKey(files, destination.GamePath) != null && !_sources.Contains(destination.Location))
                    Warn("destination_replaced", $"{container.Label}: the mod's own {destination.GamePath} is replaced.");
                if (FindKey(files, destination.GamePath) is { } stale) files.Remove(stale);
                files[destination.GamePath] = GamePath.ToLocal(local);
                _plan.Changes.Add(new GearPlanChange("Game path", container.Label, GamePath.Normalize(provider.Key), destination.GamePath));
            }

            if (!_request.KeepOriginal)
                foreach (var provider in providers)
                {
                    // Unpaired parts (a start without a counterpart) leave together with the rest.
                    var destinations = variant.Locations.GetValueOrDefault(provider.Path.Location);
                    if (destinations == provider.Path.Location) continue;
                    // A location that is also a destination was just rewritten; leave it.
                    if (variant.Locations.Values.Contains(provider.Path.Location)) continue;
                    var files = Result.GetContainer(provider.Container.Address).Files;
                    if (files != null && FindKey(files, provider.Key) is { } key) files.Remove(key);
                }
        }

        private void PlanSwapGroup(List<Provider> providers, ImmutableArray<AnimationSwapVariant> variants)
        {
            var unified = Result.Format == PenumbraModFormat.Unified;
            var options = new JsonArray();
            var labels = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            foreach (var variant in variants)
            {
                if (!labels.Add(variant.Label)) { Block("duplicate_option", $"Two options are named '{variant.Label}'."); return; }
                var files = new JsonObject();
                foreach (var provider in providers)
                {
                    if (!variant.Locations.TryGetValue(provider.Path.Location, out var destinationLocation)) continue;
                    var destination = FromLocation(provider.Path.Race, destinationLocation);
                    if (destination == null || SwapContent(provider, destination) is not { } local) continue;
                    if (FindKey(files, destination.GamePath) != null)
                    {
                        Block("target_conflict", $"{variant.Label}: two files map to {destination.GamePath}.");
                        continue;
                    }
                    files[destination.GamePath] = GamePath.ToLocal(local);
                    _plan.Changes.Add(new GearPlanChange("Option", $"{_request.GroupName} / {variant.Label}",
                        GamePath.Normalize(provider.Key), destination.GamePath));
                }
                if (files.Count == 0) Warn("empty_option", $"The option '{variant.Label}' has no animation for any race the mod provides.");
                var option = new JsonObject();
                if (unified) option["Id"] = Guid.NewGuid().ToString();
                option["Name"] = variant.Label;
                option["Description"] = string.Empty;
                option["Files"] = files;
                option["FileSwaps"] = new JsonObject();
                option["Manipulations"] = new JsonArray();
                options.Add(option);
            }

            // The group now provides the animation, so the default files stop doing so.
            var defaults = Result.Default.Files;
            foreach (var provider in providers)
                if (defaults != null && FindKey(defaults, provider.Key) is { } key)
                {
                    defaults.Remove(key);
                    _plan.Changes.Add(new GearPlanChange("Game path", "Default", GamePath.Normalize(provider.Key), "moved into the option group"));
                }

            var group = new JsonObject();
            if (unified) group["Id"] = Guid.NewGuid().ToString();
            group["Name"] = _request.GroupName;
            group["Description"] = "Choose which slot this animation replaces. Created by Advanced Penumbra Mod Converter.";
            group["Priority"] = Result.Groups.Select(g => Json.GetInt(g.Node["Priority"], 0)).DefaultIfEmpty(0).Max() + 1;
            group["Type"] = "Single";
            group["DefaultSettings"] = Math.Clamp(_request.DefaultVariant, 0, Math.Max(0, variants.Length - 1));
            group["Options"] = options;
            Result.Groups.Add(new ModGroup(group, Result.Groups.Count, null));
            _plan.Changes.Add(new GearPlanChange("Group", _request.GroupName!, "new single-select group",
                $"{variants.Length} option(s): {string.Join(", ", variants.Select(v => v.Label))}"));
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

            var wanted = SwapLocal(provider, destination);
            string local;
            if (_request.Mode == ConversionOutputMode.InPlace && content.AsSpan().SequenceEqual(bytes) &&
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
            var local = GamePath.ToLocal(provider.Local);
            var sourcePath = GamePath.ToLocal(provider.Path.GamePath);
            if (local.EndsWith(sourcePath, StringComparison.OrdinalIgnoreCase) &&
                (local.Length == sourcePath.Length || local[local.Length - sourcePath.Length - 1] == '\\'))
                return local[..^sourcePath.Length] + GamePath.ToLocal(destination.GamePath);

            var folder = Path.GetDirectoryName(local) ?? string.Empty;
            if (_races > 1) folder = Path.Combine(folder, $"c{destination.Race:D4}");
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
            var pap = new PapFile(bytes);
            var body = pap.BodyEntries.ToList();
            if (body.Count == 0) throw new InvalidDataException("The file has no body animation to move.");

            var resolved = ResolvingRace(destination.Race, race => Game(destination.WithRace(race).GamePath) != null, _owner._parentRace);
            if (resolved is not { } race)
                throw new InvalidDataException($"The game has no animation at {destination.Location} for c{destination.Race:D4} " +
                                               "or the races it inherits from, so its animation name is unknown.");
            if (race != destination.Race)
                Note("inherited_name", $"c{destination.Race:D4} has no own {destination.Key}; its name was taken from c{race:D4}, " +
                                       "whose animation the game plays for it.");
            var destinationBody = new PapFile(Game(destination.WithRace(race).GamePath)!).BodyEntries.ToList();

            var from = Played(source, body);
            var to = Played(destination, destinationBody);
            if (from.Count != to.Count)
                throw new InvalidDataException($"It plays {from.Count} body animation(s) but {destination.Key} plays {to.Count}; " +
                                               "the animations cannot be paired.");
            if (from.Count > 1)
                Warn("multiple_animations", $"{source.Key} plays {from.Count} body animations; they are paired with {destination.Key}'s in order.");

            var kept = body.Select(e => e.Entry.Name).ToHashSet(StringComparer.Ordinal);
            var lost = destinationBody.Where(e => !to.Contains(e) && !kept.Contains(e.Entry.Name)).Select(e => e.Entry.Name).ToList();
            if (lost.Count > 0)
                Warn("destination_extras", $"The game's {destination.Key} also holds {string.Join(", ", lost)}, which the moved file does not " +
                                           "have; the game cannot play it while this mod is enabled.");

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
                if (Skeleton(target) is not { } targetSkeleton) continue;
                foreach (var provider in providers)
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
                                Warn("retarget_note", $"{destination.Key} (c{authored:D4} → c{target:D4}): {note}");
                        }
                        catch (Exception ex) when (ex is InvalidDataException or InvalidOperationException)
                        {
                            Block("retarget_failed", $"{GamePath.Normalize(provider.Key)} → c{target:D4}: {ex.Message}");
                            continue;
                        }
                    }

                    var local = Write(content, RaceLocal(GamePath.ToLocal(provider.Local), provider.Path.Race, target), provider,
                        authored == target ? "copied, already made for this race" : $"retargeted c{authored:D4} → c{target:D4}");
                    _written[(provider.FullPath, destination.GamePath)] = local;
                    _hashes[local] = AnimationConversionPlan.Hash(content);
                    MapRetargeted(provider, destination, local);
                }
            }
            if (_plan.HasBlockers) return;
            DescribeInheritance(providers, targets);
        }

        private void MapRetargeted(Provider provider, PapPath destination, string local)
        {
            var container = Result.GetContainer(provider.Container.Address);
            var files = container.GetOrCreateFiles();
            if (FindKey(files, destination.GamePath) is { } existing)
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
        /// plays the one of the first race up its skeleton parents that has one.
        /// </summary>
        private void DescribeInheritance(List<Provider> providers, List<ushort> targets)
        {
            foreach (var location in providers.Select(p => p.Path).DistinctBy(p => p.Location))
            {
                bool Has(ushort race) => targets.Contains(race) || race == _request.SourceRace ||
                                         Game(location.WithRace(race).GamePath) != null;
                foreach (var target in targets)
                {
                    var users = GenderRaces.Playable.Where(r => r != target && !targets.Contains(r) &&
                                                                ResolvingRace(r, Has, _owner._parentRace) == target).ToList();
                    if (users.Count > 0)
                        Note("inherited_by", $"{location.Key}: {string.Join(", ", users.Select(r => $"c{r:D4}"))} have no file of their own " +
                                             $"and will also play the c{target:D4} version.");
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
                    Note("mod_skeleton", $"The skeleton for c{race:D4} is taken from the option {container.Label}.");
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
        private void DeleteOrphans(List<Provider> providers)
        {
            var referenced = Result.Containers.SelectMany(c => c.FileEntries())
                .Select(e => GamePath.NormalizeLocal(e.Local)).ToHashSet(StringComparer.Ordinal);
            foreach (var local in providers.Select(p => GamePath.ToLocal(p.Local)).Distinct(StringComparer.OrdinalIgnoreCase))
                if (!referenced.Contains(GamePath.NormalizeLocal(local)))
                    _plan.Files.Add(new PlannedFileOperation(LocalFileOperation.Delete, local, local, null, "no longer used"));
        }

        private PapPath? FromLocation(ushort race, string location)
        {
            if (PapPath.TryParse($"chara/human/c{race:D4}/animation/{location}.pap", out var path)) return path;
            Block("invalid_destination", $"'{location}' is not an animation location.");
            return null;
        }

        private static string? FindKey(JsonObject files, string gamePath)
            => files.Select(p => p.Key).FirstOrDefault(k => GamePath.Normalize(k) == gamePath);

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

        private void Warn(string code, string message)
        {
            if (!_plan.Diagnostics.Any(d => d.Code == code && d.Message == message))
                _plan.Diagnostics.Add(new PlanDiagnostic(code, message, false));
        }

        private void Block(string code, string message)
        {
            if (!_plan.Diagnostics.Any(d => d.Code == code && d.Message == message))
                _plan.Diagnostics.Add(new PlanDiagnostic(code, message, true));
        }
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
            try { _ = new PapFile(bytes); }
            catch (InvalidDataException ex) { issues.Add(new VerificationIssue(true, $"{output.Local} cannot be read: {ex.Message}")); }
        }
        return issues;
    }
}
