using System.Collections.Immutable;
using System.Text.Json.Nodes;

namespace UniversalModConverter.Core;

/// <summary>How a texture fan-out adds the paths of its targets.</summary>
public enum TextureFanOutLayout
{
    /// <summary>
    /// Every target's paths go right beside the source's, in Default or whichever options already
    /// hold them, so the toggles that govern the source govern the targets too.
    /// </summary>
    AddPathsToOptions,

    /// <summary>
    /// Every target race gets new option groups of its own holding its paths, copying the options
    /// the source's paths live in, so it can be switched on or off separately.
    /// </summary>
    NewGroups,
}

/// <summary>
/// Offers the textures (and, for faces and skins, the materials) a mod replaces under one
/// customization root to further races or IDs as well. Nothing is converted: every target simply
/// gets the source's game paths under its own root, pointing at the very same files.
/// </summary>
/// <param name="Targets">The races/IDs to add; the source itself is always kept and is ignored here.</param>
/// <param name="Mode">
/// Where the result goes: <see cref="ConversionOutputMode.AddToMod"/> edits this mod, and
/// <see cref="ConversionOutputMode.NewMod"/> writes a copy of it. The plan is the same either way,
/// since the new mod starts out as the whole of this one.
/// </param>
/// <param name="RetargetMaterials">
/// Give every target its own copy of each material, with the texture paths inside moved to the
/// target's root, instead of sharing the source's material as it is.
/// </param>
public sealed record TextureFanOutRequest(
    CustomizationPathEndpoint Source,
    ImmutableArray<CustomizationPathEndpoint> Targets,
    ConversionOutputMode Mode,
    TextureFanOutLayout Layout,
    bool RetargetMaterials,
    string Description);

/// <summary>A redirect the plan adds; <paramref name="Local"/> is null for a file swap.</summary>
public sealed record TextureFanOutOutput(string Scope, string GamePath, string? Local);

public sealed class TextureFanOutPlan : ModFilePlan
{
    internal TextureFanOutPlan(TextureFanOutRequest request, PenumbraMod result) : base(result)
    {
        Request = request;
    }

    public TextureFanOutRequest Request { get; }

    public override ConversionOutputMode Mode => Request.Mode;

    public List<TextureFanOutOutput> Outputs { get; } = [];

    /// <summary>Every added path whose file is missing from <paramref name="modDirectory"/>.</summary>
    public IReadOnlyList<string> Verify(string modDirectory)
    {
        var missing = new List<string>();
        foreach (var output in Outputs.Where(o => o.Local != null))
        {
            string full;
            try { full = PathSafety.ResolveRelative(modDirectory, GamePath.ToLocal(output.Local!)); }
            catch (InvalidDataException ex)
            {
                missing.Add($"{output.Scope}: {output.GamePath} → {output.Local}: {ex.Message}");
                continue;
            }
            if (!File.Exists(full)) missing.Add($"{output.Scope}: {output.GamePath} → {output.Local} does not exist.");
        }
        return missing;
    }
}

/// <summary>
/// Plans a <see cref="TextureFanOutRequest"/>. The source's own paths are never touched.
/// <para>
/// <see cref="TextureFanOutLayout.AddPathsToOptions"/> adds each target's paths into every
/// container (Default or an option) that holds the source's, so the toggles that already govern
/// the source govern the targets too.
/// </para>
/// <para>
/// <see cref="TextureFanOutLayout.NewGroups"/> gives each target race groups of its own: the
/// source's paths in Default become a multi-select group with one option per ID, and the paths
/// in an existing group's options become a copy of that group (same type, option names and
/// default choice), so each race can be switched, or pick its variant, on its own.
/// </para>
/// </summary>
public sealed class TextureFanOutPlanner(IGameFileProvider game, Func<CustomizationPathEndpoint, string>? optionLabel = null)
{
    /// <summary>Penumbra stores a multi-select group's setting as a bit per option.</summary>
    private const int MaxMultiOptions = 32;

    private readonly IGameFileProvider _game = game;

    private sealed record Entry(ModContainer Container, string Key, string Value, bool IsSwap);

    public TextureFanOutPlan Plan(string modDirectory, TextureFanOutRequest request)
    {
        if (request.Mode == ConversionOutputMode.InPlace)
            throw new ArgumentException(OutputModeRules.FanOutInPlace, nameof(request));
        // A new mod is a copy of the whole of this one, so either way the plan edits all of it.
        return new Session(this, new ModPlanContext(modDirectory, ConversionOutputMode.AddToMod), request).Run();
    }

    private string OptionLabel(CustomizationPathEndpoint endpoint)
        => optionLabel?.Invoke(endpoint) ?? (endpoint.Kind == AssetKind.Body
            ? CustomizationKinds.Get(endpoint.Kind).DisplayName
            : $"{CustomizationKinds.Get(endpoint.Kind).DisplayName} {endpoint.ModelId}");

    private sealed class Session
    {
        private readonly TextureFanOutPlanner _owner;
        private readonly string _root;
        private readonly TextureFanOutRequest _request;
        private readonly CustomizationPathEndpoint _source;
        private readonly PenumbraMod _mod;
        private readonly TextureFanOutPlan _plan;
        private readonly GearConversionPlanner.LocalAllocator _locals;
        private readonly Dictionary<CustomizationPathEndpoint, HashSet<string>> _targetKeys = new();
        private readonly Dictionary<(string Local, CustomizationPathEndpoint Target), string?> _materials = new();

        /// <summary>Every game path the mod already redirects, with the first place that does.</summary>
        private readonly Dictionary<string, string> _redirected = new(StringComparer.Ordinal);

        /// <summary>Target paths left alone because the mod has them already, by where and for which race.</summary>
        private readonly Dictionary<(string Scope, ushort Race), HashSet<string>> _kept = new();
        private int _priority;

        public Session(TextureFanOutPlanner owner, ModPlanContext context, TextureFanOutRequest request)
        {
            _owner = owner;
            _root = context.ModDirectory;
            _request = request;
            _source = request.Source;
            _mod = context.Source;
            _plan = new TextureFanOutPlan(request, context.Result);
            _locals = context.Locals;
            _plan.InputFiles.UnionWith(context.InputFiles);
            _priority = ModGroupBuilder.TopPriority(Result);
            foreach (var container in _mod.Containers)
            foreach (var key in container.FileEntries().Select(e => e.Key).Concat(container.SwapEntries().Select(e => e.Key)))
                _redirected.TryAdd(GamePath.Normalize(key), container.Label);
        }

        private PenumbraMod Result => _plan.Result;

        public TextureFanOutPlan Run()
        {
            var descriptor = CustomizationKinds.Get(_source.Kind);
            if (!CustomizationDetection.CanFanOut(_mod, _source))
            {
                Block("not_fan_out", $"The mod no longer replaces only " +
                                     (_source.Kind is AssetKind.Face or AssetKind.Body ? "textures and materials" : "textures") +
                                     $" under {descriptor.Root(_source.GenderRace, _source.ModelId)}. Scan the mod again.");
                return _plan;
            }

            var targets = ResolveTargets();
            if (_plan.HasBlockers) return _plan;

            var entries = CollectEntries();
            foreach (var target in targets)
                _targetKeys[target] = entries
                    .Select(e => GamePath.Normalize(Retarget(e.Key, target)))
                    .ToHashSet(StringComparer.Ordinal);

            if (_request.Layout == TextureFanOutLayout.AddPathsToOptions) AddPaths(entries, targets);
            else AddGroups(entries, targets);

            foreach (var ((scope, race), keys) in _kept)
                Warn("path_exists", $"{scope} already redirects {keys.Count} of {RaceNames.Name(race)}'s paths; " +
                                    "those are left as they are.");
            if (!_plan.HasBlockers && _plan.Outputs.Count == 0)
                Block("empty_plan", "Every target already has these paths; there is nothing to add.");
            return _plan;
        }

        private List<CustomizationPathEndpoint> ResolveTargets()
        {
            var targets = new List<CustomizationPathEndpoint>();
            foreach (var target in _request.Targets.Distinct().Where(t => t != _source))
            {
                if (!CustomizationKinds.CanConvert(_source.Kind, target.Kind))
                    Block("invalid_target", $"{CustomizationKinds.Get(_source.Kind).DisplayName} cannot be offered as " +
                                            $"{CustomizationKinds.Get(target.Kind).DisplayName}.");
                else if (!CustomizationKinds.Get(target.Kind).SupportsRace(target.GenderRace))
                    Block("invalid_target", $"{CustomizationKinds.Get(target.Kind).DisplayName} is not valid for " +
                                            $"{RaceNames.Describe(target.GenderRace)}.");
                else if (CustomizationTargets.BlockReason(_source.Kind, _source.GenderRace, target.Kind, target.GenderRace) is { } reason)
                    Block("invalid_target", reason);
                else if (_source.Kind == AssetKind.Body && target.Kind == AssetKind.Body && _source.ModelId != target.ModelId)
                    // A race's body variants (the Xaela skin is b0101) are its own: another race has no such body.
                    Block("invalid_target", $"This skin is body {_source.ModelId:D4}, which only its own race has, so it cannot be " +
                                            $"offered to {RaceNames.Describe(target.GenderRace)}.");
                else
                    targets.Add(target);
            }
            if (targets.Count == 0 && !_plan.HasBlockers)
                Block("no_targets", "Tick at least one race or ID to add the paths for.");
            var ordered = targets.OrderBy(t => t.GenderRace).ThenBy(t => t.Kind).ThenBy(t => t.ModelId).ToList();

            // Hair and Hrothgar tails load their files from a shared root, so a target that loads from
            // the source's own root, or from the same root as another target, is already served.
            var settled = new HashSet<CustomizationPathEndpoint> { _source };
            var distinct = new List<CustomizationPathEndpoint>();
            foreach (var target in ordered)
            {
                var root = PathsOf(target);
                if (settled.Add(root)) distinct.Add(target);
                else
                    Warn("shared_paths", $"{Describe(target)} loads the same files as another root of this run " +
                                         $"({Describe(root)}), so nothing is added for it.");
            }
            return distinct;
        }

        /// <summary>The root whose files <paramref name="target"/> loads: its own, or the shared one hair and Hrothgar tails use.</summary>
        private static CustomizationPathEndpoint PathsOf(CustomizationPathEndpoint target) => CustomizationPaths.GetMaterialEndpoint(target);

        /// <summary><paramref name="value"/> moved from the source's root to the root <paramref name="target"/> loads its files from.</summary>
        private string Retarget(string value, CustomizationPathEndpoint target)
            => CustomizationPaths.Rewrite(value, _source, PathsOf(target));

        private static string Describe(CustomizationPathEndpoint endpoint)
            => $"{RaceNames.Name(endpoint.GenderRace)} {CustomizationKinds.Get(endpoint.Kind).DisplayName.ToLowerInvariant()} {endpoint.ModelId}";

        /// <summary>
        /// Every redirect under the source's roots (an Au Ra tail's Xaela material root too),
        /// wherever in the mod it lives.
        /// </summary>
        private List<Entry> CollectEntries()
        {
            var entries = new List<Entry>();
            foreach (var container in _mod.Containers)
            {
                foreach (var (key, local) in container.FileEntries())
                    if (CustomizationPaths.Owns(GamePath.Normalize(key), _source))
                        entries.Add(new Entry(container, key, local, false));
                foreach (var (key, target) in container.SwapEntries())
                    if (CustomizationPaths.Owns(GamePath.Normalize(key), _source))
                        entries.Add(new Entry(container, key, target, true));
            }
            return entries;
        }

        // ── Add paths on existing options ────────────────────────────────────

        private void AddPaths(List<Entry> entries, List<CustomizationPathEndpoint> targets)
        {
            foreach (var entry in entries)
            {
                var container = Result.GetContainer(entry.Container.Address);
                var dictionary = entry.IsSwap ? container.GetOrCreateFileSwaps() : container.GetOrCreateFiles();
                foreach (var target in targets)
                {
                    var key = Retarget(entry.Key, target);
                    if (string.Equals(key, entry.Key, StringComparison.Ordinal)) continue;
                    if (Kept(key, target) || Holds(dictionary, key)) continue;
                    Add(dictionary, entry, target, key, container.Label, "Game path");
                }
            }
        }

        /// <summary>
        /// Whether the mod already has <paramref name="key"/> of its own, anywhere. The target then
        /// keeps it: the added path would win over it wherever it lands (an option over Default, a
        /// new group over the groups before it) and silently replace the mod's own file.
        /// </summary>
        private bool Kept(string key, CustomizationPathEndpoint target)
        {
            var normalized = GamePath.Normalize(key);
            if (!_redirected.TryGetValue(normalized, out var where)) return false;
            if (!_kept.TryGetValue((where, target.GenderRace), out var keys))
                _kept[(where, target.GenderRace)] = keys = new HashSet<string>(StringComparer.Ordinal);
            keys.Add(normalized);
            return true;
        }

        // ── Create new groups for new paths ──────────────────────────────────

        private void AddGroups(List<Entry> entries, List<CustomizationPathEndpoint> targets)
        {
            var defaults = entries.Where(e => e.Container.Group == null).ToList();
            // Copied in order of the originals' priority, so each race's copies outrank one another
            // the way the originals do (a tattoo group over a skin tone group, say).
            var groups = entries.Where(e => e.Container.Group != null)
                .GroupBy(e => e.Container.Group!.Index)
                .OrderBy(g => Json.GetInt(_mod.Groups[g.Key].Node["Priority"], 0))
                .ThenBy(g => g.Key)
                .Select(g => (Group: _mod.Groups[g.Key], Entries: g.ToList()))
                .ToList();

            foreach (var (group, _) in groups.Where(g => !IsCopyable(g.Group)))
                Block("uncopyable_group", $"The source's paths live in '{group.Name}', a {group.Type.ToLowerInvariant()} group, " +
                                          "which cannot be copied for another race. Use \"Add paths on existing options\" instead.");
            if (_plan.HasBlockers) return;

            foreach (var race in targets.GroupBy(t => t.GenderRace).OrderBy(g => g.Key))
            {
                var raceTargets = race.ToList();
                if (defaults.Count > 0) AddToggleGroup(race.Key, raceTargets, defaults);
                foreach (var (group, groupEntries) in groups) AddMirroredGroup(group, race.Key, raceTargets, groupEntries);
            }
        }

        private static bool IsCopyable(ModGroup group)
            => group.Type.Equals("Single", StringComparison.OrdinalIgnoreCase) ||
               group.Type.Equals("Multi", StringComparison.OrdinalIgnoreCase);

        /// <summary>The source's paths in Default: one option per ID of the race, all switched on.</summary>
        private void AddToggleGroup(ushort race, List<CustomizationPathEndpoint> targets, List<Entry> entries)
        {
            var raceName = RaceNames.Name(race);
            if (targets.Count > MaxMultiOptions)
            {
                Block("too_many_options", $"{raceName} would need {targets.Count} options, more than the " +
                                          $"{MaxMultiOptions} a multi-select group can have. Tick fewer for it.");
                return;
            }

            var name = ModGroupBuilder.UniqueName(Result, raceName);
            var options = new JsonArray();
            foreach (var target in targets)
            {
                var label = _owner.OptionLabel(target);
                var (files, swaps) = Fill(entries, [target], $"{name} / {label}");
                // An ID the mod already has all of these paths for needs no option.
                if (files.Count > 0 || swaps.Count > 0) options.Add(ModGroupBuilder.Option(label, string.Empty, files, swaps));
            }
            if (options.Count == 0) return;

            var kind = CustomizationKinds.Get(targets[0].Kind).DisplayName.ToLowerInvariant();
            AddGroup(name, $"The {kind} paths for {raceName}. Created by Universal Mod Converter.",
                "Multi", (1UL << options.Count) - 1, options);
        }

        /// <summary>
        /// The source's paths in an existing group's options: a copy of that group for the race,
        /// holding only the options that have any, with the same default choice. A single-select
        /// copy starts with an empty "-" so the race can be left out entirely; a multi-select one
        /// is switched off by unticking its options.
        /// </summary>
        private void AddMirroredGroup(ModGroup source, ushort race, List<CustomizationPathEndpoint> targets, List<Entry> entries)
        {
            var raceName = RaceNames.Name(race);
            var isMulti = source.Type.Equals("Multi", StringComparison.OrdinalIgnoreCase);
            var name = ModGroupBuilder.UniqueName(Result, $"{source.Name} · {raceName}");
            var options = new JsonArray();
            if (!isMulti)
                options.Add(ModGroupBuilder.Option(ModGroup.OffOptionName, string.Empty, new JsonObject()));

            var positions = new Dictionary<int, int>();
            foreach (var option in entries.GroupBy(e => e.Container.Address.Index).OrderBy(g => g.Key))
            {
                var node = source.Containers[option.Key].Node;
                var label = Json.GetString(node["Name"]) is { Length: > 0 } text ? text : $"#{option.Key + 1}";
                var (files, swaps) = Fill(option.ToList(), targets, $"{name} / {label}");
                if (files.Count == 0 && swaps.Count == 0) continue;
                positions[option.Key] = options.Count;
                options.Add(ModGroupBuilder.Option(label, Json.GetString(node["Description"]) ?? string.Empty, files, swaps, node["Priority"]));
            }
            if (positions.Count == 0) return;

            if (isMulti && options.Count > MaxMultiOptions)
            {
                Block("too_many_options", $"'{name}' would need {options.Count} options, more than the " +
                                          $"{MaxMultiOptions} a multi-select group can have.");
                return;
            }

            JsonNode defaults;
            if (isMulti)
            {
                Json.TryGetULong(source.Node["DefaultSettings"], out var mask);
                var mapped = 0UL;
                foreach (var (old, position) in positions)
                    if (old < 64 && (mask >> old & 1) != 0) mapped |= 1UL << position;
                defaults = mapped;
            }
            else
                defaults = positions.TryGetValue(Json.GetInt(source.Node["DefaultSettings"], 0), out var position) ? position : 0;

            AddGroup(name, $"'{source.Name}' for {raceName}. Created by Universal Mod Converter.",
                isMulti ? "Multi" : "Single", defaults, options);
        }

        private (JsonObject Files, JsonObject Swaps) Fill(List<Entry> entries, List<CustomizationPathEndpoint> targets, string scope)
        {
            var files = new JsonObject();
            var swaps = new JsonObject();
            foreach (var entry in entries)
            foreach (var target in targets)
            {
                var key = Retarget(entry.Key, target);
                var dictionary = entry.IsSwap ? swaps : files;
                if (string.Equals(key, entry.Key, StringComparison.Ordinal) || Kept(key, target) || Holds(dictionary, key))
                    continue;
                Add(dictionary, entry, target, key, scope, "Option");
            }
            return (files, swaps);
        }

        private void AddGroup(string name, string description, string type, JsonNode defaults, JsonArray options)
            => _plan.Changes.Add(ModGroupBuilder.Add(Result, name, description, ++_priority, type, defaults, options));

        // ── Shared ───────────────────────────────────────────────────────────

        /// <summary>
        /// The keys of each Files or FileSwaps object written to, normalized, so asking whether one
        /// holds a path is not a walk over all of it for every entry and target.
        /// </summary>
        private readonly Dictionary<JsonObject, HashSet<string>> _held = new(ReferenceEqualityComparer.Instance);

        private HashSet<string> Held(JsonObject dictionary)
        {
            if (!_held.TryGetValue(dictionary, out var keys))
                _held[dictionary] = keys = dictionary.Select(p => GamePath.Normalize(p.Key)).ToHashSet(StringComparer.Ordinal);
            return keys;
        }

        private bool Holds(JsonObject dictionary, string key) => Held(dictionary).Contains(GamePath.Normalize(key));

        private void Add(JsonObject dictionary, Entry entry, CustomizationPathEndpoint target, string key,
            string scope, string category)
        {
            var value = entry.IsSwap ? entry.Value : MaterialFor(entry, target) ?? entry.Value;
            dictionary[key] = value;
            Held(dictionary).Add(GamePath.Normalize(key));
            _plan.Outputs.Add(new TextureFanOutOutput(scope, GamePath.Normalize(key), entry.IsSwap ? null : value));
            _plan.Changes.Add(new GearPlanChange(category, scope, GamePath.Normalize(entry.Key), GamePath.Normalize(key)));
        }

        /// <summary>
        /// The target's own copy of a material, with every texture path inside that belongs to the
        /// source moved to the target, or null to share the source's material as it is. A moved
        /// path must exist: either the fan-out provides it or the game has it.
        /// </summary>
        private string? MaterialFor(Entry entry, CustomizationPathEndpoint target)
        {
            if (!_request.RetargetMaterials || !entry.Key.EndsWith(".mtrl", StringComparison.OrdinalIgnoreCase)) return null;
            var cacheKey = (GamePath.NormalizeLocal(entry.Value), target);
            if (_materials.TryGetValue(cacheKey, out var cached)) return cached;

            string? result = null;
            try
            {
                var full = PathSafety.ResolveRelative(_root, GamePath.ToLocal(entry.Value));
                if (!File.Exists(full))
                    Warn("material_missing", $"{entry.Value} does not exist, so its texture paths could not be moved for " +
                                             $"{RaceNames.Name(target.GenderRace)}.");
                else
                {
                    _plan.InputFiles.Add(full);
                    var bytes = File.ReadAllBytes(full);
                    var replacements = new Dictionary<string, string>(StringComparer.Ordinal);
                    foreach (var texture in MtrlFile.ReadTexturePaths(bytes))
                    {
                        var moved = Retarget(texture, target);
                        if (string.Equals(moved, texture, StringComparison.Ordinal) || replacements.ContainsKey(texture)) continue;
                        var normalized = GamePath.Normalize(moved);
                        if (_targetKeys[target].Contains(normalized) || _owner._game.FileExists(normalized))
                            replacements[texture] = moved;
                        else
                            Warn("material_texture_missing", $"{Path.GetFileName(entry.Key)} for {RaceNames.Name(target.GenderRace)}: " +
                                                             $"{normalized} is in neither the mod nor the game, so it keeps using {texture}.");
                    }

                    if (replacements.Count > 0)
                    {
                        var content = MtrlFile.RewritePaths(bytes, replacements);
                        var destination = _locals.Reserve(GamePath.Normalize(Retarget(entry.Key, target)), null);
                        _plan.Files.Add(new PlannedFileOperation(LocalFileOperation.Write, GamePath.ToLocal(entry.Value), destination,
                            content, $"texture paths moved to {RaceNames.Name(target.GenderRace)}"));
                        result = destination;
                    }
                }
            }
            catch (Exception ex) when (ex is InvalidDataException or IOException)
            {
                Warn("material_unreadable", $"{entry.Value}: {ex.Message} It is shared unchanged instead.");
            }

            _materials[cacheKey] = result;
            return result;
        }

        private void Block(string code, string message) => _plan.Report(code, message, true);

        private void Warn(string code, string message) => _plan.Report(code, message, false);
    }
}
