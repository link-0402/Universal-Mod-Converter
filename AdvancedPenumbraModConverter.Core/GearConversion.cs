using System.Security.Cryptography;
using System.Text.Json.Nodes;
using System.Text.RegularExpressions;

namespace AdvancedPenumbraModConverter.Core;

public sealed record GearConversionRequest(GearItem Source, GearItem Target, ConversionOutputMode Mode);

public enum LocalFileOperation
{
    /// <summary>Copy <c>Source</c> to <c>Destination</c> unchanged (new mods only).</summary>
    Copy,

    /// <summary>Write <c>Content</c> to <c>Destination</c>, creating or replacing it.</summary>
    Write,

    /// <summary>Rename <c>Source</c> to <c>Destination</c> (in place only).</summary>
    Move,

    /// <summary>Remove <c>Source</c> (in place only, after its content was written elsewhere).</summary>
    Delete,
}

/// <summary>One mod-relative file operation. Paths use Penumbra's backslash separators.</summary>
public sealed record PlannedFileOperation(
    LocalFileOperation Operation, string? Source, string Destination, byte[]? Content, string Reason);

/// <summary>A human-readable line of the preview.</summary>
public sealed record GearPlanChange(string Category, string Scope, string From, string To);

public sealed class GearConversionPlan : IModFilePlan
{
    internal GearConversionPlan(GearConversionRequest request, PenumbraMod result)
    {
        Request = request;
        Result = result;
    }

    public GearConversionRequest Request { get; }

    /// <summary>The complete definition of the output mod (in place: the edited mod).</summary>
    public PenumbraMod Result { get; }

    public List<PlannedFileOperation> Files { get; } = [];

    IReadOnlyList<PlannedFileOperation> IModFilePlan.Files => Files;

    public List<GearPlanChange> Changes { get; } = [];

    public List<PlanDiagnostic> Diagnostics { get; } = [];

    /// <summary>Source game path → target game path for every converted resource.</summary>
    public Dictionary<string, string> GamePathMap { get; } = new(StringComparer.OrdinalIgnoreCase);

    /// <summary>Absolute paths of every source file the plan was derived from.</summary>
    public HashSet<string> InputFiles { get; } = new(StringComparer.OrdinalIgnoreCase);

    /// <summary>
    /// The mod file each converted model came from, by the container holding it and the model's
    /// target game path. Converting renames paths but never reorders meshes or parts, so the
    /// source file shows the same parts at the same indices; the Mesh groups tab previews them
    /// on a character wearing the source item.
    /// </summary>
    public Dictionary<(ContainerAddress Container, string TargetPath), string> ModelSources { get; } = [];

    public bool HasBlockers => Diagnostics.Any(d => d.IsBlocker);

    /// <summary>
    /// Deterministic hash of the planned output, used to prove Apply matches Preview. Covers the
    /// whole result definition, so in a run of several conversions every entry hashes the same
    /// shared definition; fingerprint the merged plan instead.
    /// </summary>
    public string Fingerprint() => ModFingerprint.ComputePlan(Files, Result);
}

/// <summary>
/// Plans gear (equipment, accessory and facewear) conversions on game paths, the only
/// thing Penumbra and the game resolve. Local file names are arbitrary and never used to
/// decide what belongs to an item.
///
/// The dependency walk mirrors the game: a model at <c>{root}/model/...</c> loads its
/// short material names from <c>{root}/material/v{IMC MaterialId}/</c>, materials load
/// full texture paths, and VFX load full texture paths. Every resource reached this way
/// that lives under the source root and is supplied by the mod is retargeted to the
/// target root. Only the models the mod ships are converted; vanilla resources those
/// models depend on (materials the mod does not ship) are copied from the game so the
/// target never references files that do not exist.
/// </summary>
public sealed partial class GearConversionPlanner(IGameFileProvider game)
{
    /// <summary>Plans one conversion on its own, finishing the mod definition as it goes.</summary>
    public GearConversionPlan Plan(string modDirectory, GearConversionRequest request)
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
    public GearConversionPlan Plan(ModPlanContext context, GearConversionRequest request)
    {
        if (context.Mode != request.Mode)
            throw new ArgumentException("The request and the planning context disagree about the output mode.",
                nameof(request));
        return new Session(game, context, request).Run();
    }

    [GeneratedRegex(@"/material/v(?<variant>\d{4})/(?<name>[^/]+)$", RegexOptions.CultureInvariant)]
    internal static partial Regex MaterialPathRegex();

    private sealed record FileProvider(ModContainer Container, string Key, string Local, string FullPath);

    private sealed record SwapProvider(ModContainer Container, string Key, string Target);

    private sealed class Session
    {
        private readonly IGameFileProvider _game;
        private readonly ModPlanContext _context;
        private readonly string _root;
        private readonly GearConversionRequest _request;
        private readonly GearItem _src;
        private readonly GearItem _tgt;
        private readonly GearPathEndpoint _srcEp;
        private readonly GearPathEndpoint _tgtEp;
        private readonly string _srcRoot;
        private readonly PenumbraMod _mod;
        private readonly GearConversionPlan _plan;

        private readonly Dictionary<string, List<FileProvider>> _files = new(StringComparer.Ordinal);
        private readonly Dictionary<string, List<SwapProvider>> _swaps = new(StringComparer.Ordinal);
        private readonly Dictionary<string, byte[]?> _localCache = new(StringComparer.OrdinalIgnoreCase);
        private readonly Dictionary<string, byte[]?> _gameCache = new(StringComparer.Ordinal);

        /// <summary>Source path → target path for resources under the source root.</summary>
        private readonly Dictionary<string, string> _map = new(StringComparer.Ordinal);

        /// <summary>Mod-supplied resources outside the source root the conversion needs unchanged.</summary>
        private readonly HashSet<string> _dependencies = new(StringComparer.Ordinal);

        /// <summary>Vanilla resources (source path → bytes) the target needs in the default container.</summary>
        private readonly SortedDictionary<string, byte[]> _generated = new(StringComparer.Ordinal);

        private readonly HashSet<string> _visited = new(StringComparer.Ordinal);
        private readonly Queue<string> _queue = new();

        /// <summary>Material file name → material folders (vNNNN) the mod supplies under the source root.</summary>
        private readonly Dictionary<string, SortedSet<int>> _materialFolders = new(StringComparer.Ordinal);

        private ushort _sourceVariant;
        private ImcEntry _sourceEntry;
        private readonly SortedDictionary<ushort, ImcEntry> _targetImc = new();

        public Session(IGameFileProvider game, ModPlanContext context, GearConversionRequest request)
        {
            _game = game;
            _context = context;
            _root = context.ModDirectory;
            _request = request;
            _src = request.Source;
            _tgt = request.Target;
            _srcEp = _src.PathEndpoint;
            _tgtEp = _tgt.PathEndpoint;
            _srcRoot = _src.Root;
            _mod = context.Source;
            _plan = new GearConversionPlan(request, context.Result);
            foreach (var file in context.InputFiles) _plan.InputFiles.Add(file);
        }

        private bool EditsSourceMod => _request.Mode.EditsSourceMod();

        /// <summary>
        /// Additive mode: the source item keeps working, so nothing is moved or removed and the
        /// converted paths are added to the containers the source paths already live in.
        /// </summary>
        private bool KeepsSource => _request.Mode.KeepsSource();

        public GearConversionPlan Run()
        {
            if (_src.SetId == _tgt.SetId && _src.Slot == _tgt.Slot && _src.IsAccessory == _tgt.IsAccessory)
            {
                Block("identical_roots", "Source and target are the same item slot.");
                return _plan;
            }

            if (_src.Slot == GearSlot.Glasses != (_tgt.Slot == GearSlot.Glasses) &&
                _src.SetId == _tgt.SetId && _src.Slot.Suffix() == _tgt.Slot.Suffix())
            {
                Block("identical_roots", "Head and facewear with the same model ID share every game path.");
                return _plan;
            }

            IndexContainers();
            ResolveVariants();
            SeedAndWalk();

            // Vanilla copies alone are not a conversion: the mod itself must change the item.
            if (!HasModContent())
            {
                Block("empty_plan", $"This mod does not change {Describe(_src)}; there is nothing to convert.");
                return _plan;
            }

            foreach (var (source, target) in _map) _plan.GamePathMap[source] = target;
            foreach (var (source, target) in _map)
                if (source.EndsWith(".mdl", StringComparison.Ordinal) && _files.TryGetValue(source, out var models))
                    foreach (var model in models)
                        _plan.ModelSources.TryAdd((model.Container.Address, target), model.FullPath);
            var rewritten = RewriteContents();
            if (_plan.HasBlockers) return _plan;

            if (EditsSourceMod) BuildInPlace(rewritten);
            else BuildNewMod(rewritten);
            return _plan;
        }

        private bool HasModContent()
            => _map.Keys.Any(k => _files.ContainsKey(k) || _swaps.ContainsKey(k)) ||
               _mod.Containers.Any(c => (c.Manipulations?.OfType<JsonObject>() ?? []).Any(m =>
                   GearManipulations.Retarget(m, _src, _tgt, _sourceVariant, TargetVariants).Kind != RetargetKind.Unrelated)) ||
               _mod.Groups.Any(g => g.IsImc && GearManipulations.ImcGroupMatches(g.Node, _src, _sourceVariant));

        // ── Indexing ────────────────────────────────────────────────────────

        private void IndexContainers()
        {
            foreach (var container in _mod.Containers)
            {
                foreach (var (key, local) in container.FileEntries())
                {
                    string full;
                    try
                    {
                        full = PathSafety.ResolveRelative(_root, GamePath.ToLocal(local));
                    }
                    catch (Exception ex)
                    {
                        Warn("unsafe_local_path", $"{container.Label}: '{local}' is not a valid mod-local path ({ex.Message}).");
                        continue;
                    }

                    // Penumbra ignores redirects to missing files and the game loads vanilla,
                    // so such entries do not provide anything and are left untouched.
                    if (!File.Exists(full))
                    {
                        Warn("missing_local_file", $"{container.Label}: {local} (for {key}) does not exist and is ignored.");
                        continue;
                    }

                    var path = GamePath.Normalize(key);
                    Add(_files, path, new FileProvider(container, key, local, full));
                    if (IsOwned(path) && MaterialPathRegex().Match(path) is { Success: true } material)
                        AddMaterialFolder(material);
                }

                foreach (var (key, target) in container.SwapEntries())
                {
                    var path = GamePath.Normalize(key);
                    Add(_swaps, path, new SwapProvider(container, key, target));
                    if (IsOwned(path) && MaterialPathRegex().Match(path) is { Success: true } material)
                        AddMaterialFolder(material);
                }
            }

            static void Add<T>(Dictionary<string, List<T>> index, string key, T value)
            {
                if (!index.TryGetValue(key, out var list)) index[key] = list = [];
                list.Add(value);
            }
        }

        private void AddMaterialFolder(Match match)
        {
            if (!_materialFolders.TryGetValue(match.Groups["name"].Value, out var folders))
                _materialFolders[match.Groups["name"].Value] = folders = [];
            folders.Add(int.Parse(match.Groups["variant"].Value));
        }

        // ── IMC variants ────────────────────────────────────────────────────

        /// <summary>
        /// Determines the source look (IMC entry of the chosen source variant, including the
        /// mod's own overrides) and every variant of the target set. All target variants are
        /// pointed at the source look so every item sharing the target model shows the
        /// converted model with a material set that exists.
        /// </summary>
        private void ResolveVariants()
        {
            _sourceVariant = _src.Variant > 0 ? _src.Variant : (ushort)1;
            var sourceRows = ReadImc(_src);
            if (sourceRows.TryGetValue(_sourceVariant, out var vanilla))
                _sourceEntry = vanilla;
            else
            {
                _sourceEntry = new ImcEntry(1, 0, 0x3FF, 0, 0, 0);
                Warn("source_imc_missing",
                    $"No IMC entry exists for {Describe(_src)}; assuming material folder v0001 with all attributes visible.");
            }

            var overridden = false;
            foreach (var manipulation in _mod.Default.Manipulations?.OfType<JsonObject>() ?? [])
                if (GearManipulations.IsImcFor(manipulation, _src, _sourceVariant) &&
                    ImcEntry.FromJson(manipulation["Manipulation"]?["Entry"]) is { } entry)
                {
                    _sourceEntry = entry;
                    overridden = true;
                }

            if (!overridden)
                foreach (var group in _mod.Groups.Where(g => g.IsImc))
                    if (GearManipulations.ImcGroupMatches(group.Node, _src, _sourceVariant) &&
                        !(Json.TryGetBool(group.Node["OnlyAttributes"], out var only) && only) &&
                        ImcEntry.FromJson(group.Node["DefaultEntry"]) is { MaterialId: > 0 } entry)
                    {
                        _sourceEntry = entry;
                        break;
                    }

            if (_sourceEntry.MaterialId == 0) _sourceEntry = _sourceEntry with { MaterialId = 1 };

            foreach (var (variant, entry) in ReadImc(_tgt)) _targetImc[variant] = entry;
            if (_targetImc.Count == 0)
                Warn("target_imc_missing", $"No IMC data could be read for {Describe(_tgt)}; only variant {TargetVariant} is redirected.");

            if (_sourceEntry.MaterialAnimationId != 0)
                Warn("material_animation",
                    $"The source variant uses material animation {_sourceEntry.MaterialAnimationId}; animation files are not relocated.");
        }

        private ushort TargetVariant => _tgt.Variant > 0 ? _tgt.Variant : (ushort)1;

        private IReadOnlyList<ushort> TargetVariants
            => _targetImc.Count > 0 ? _targetImc.Keys.ToArray() : [TargetVariant];

        private Dictionary<ushort, ImcEntry> ReadImc(GearItem item)
        {
            var bytes = Game(GearSlots.ImcFile(item));
            return bytes == null
                ? []
                : GameMetadata.ReadImc(bytes, item.Slot.ImcPartIndex()).ToDictionary(r => r.Variant, r => r.Entry);
        }

        // ── Dependency walk ─────────────────────────────────────────────────

        private void SeedAndWalk()
        {
            foreach (var race in GenderRaces.Playable)
            {
                // Only the models the mod ships are converted. A race it has no model for keeps
                // seeing the target's own model, rather than a vanilla copy of the source's.
                var model = GamePath.Normalize(GearSlots.ModelPath(_src, race));
                if (_files.ContainsKey(model) || _swaps.ContainsKey(model))
                    Enqueue(model);
            }

            // TexTools-style catch-all: anything under the source root that carries this slot's
            // item token belongs to the item even when nothing references it.
            var slotToken = $"{_srcEp.Token}_{_src.Slot.Suffix()}";
            foreach (var path in _files.Keys.Concat(_swaps.Keys))
                if (IsOwned(path) && ContainsToken(Path.GetFileName(path), slotToken))
                    Enqueue(path);

            if (_sourceEntry.VfxId != 0)
            {
                var vfx = GamePath.Normalize(GearSlots.VfxPath(_src, _sourceEntry.VfxId));
                if (!ProvidedIn(vfx, _mod.Default) && Game(vfx) is { } vanilla) Generate(vfx, vanilla);
                Enqueue(vfx);
            }

            while (_queue.Count > 0) Visit(_queue.Dequeue());
        }

        private static bool ContainsToken(string fileName, string token)
        {
            var index = fileName.IndexOf(token, StringComparison.Ordinal);
            return index >= 0 && (index + token.Length == fileName.Length || !char.IsLetterOrDigit(fileName[index + token.Length]));
        }

        /// <summary>Effective source EQDP entry: the mod's default container overrides vanilla.</summary>
        private ushort? SourceEqdp(ushort race)
        {
            foreach (var manipulation in _mod.Default.Manipulations?.OfType<JsonObject>() ?? [])
                if (GearManipulations.IsEqdpFor(manipulation, _src, race) &&
                    Json.TryGetInt(manipulation["Manipulation"]?["Entry"], out var entry))
                    return (ushort)entry;
            return Game(_src.Slot.EqdpFile(race)) is { } bytes ? GameMetadata.ReadEqdp(bytes, _src.SetId) : null;
        }

        private void Enqueue(string path)
        {
            if (_visited.Add(path)) _queue.Enqueue(path);
        }

        private void Visit(string path)
        {
            var owned = IsOwned(path);
            var provided = _files.ContainsKey(path) || _swaps.ContainsKey(path);
            if (owned && (provided || _generated.ContainsKey(path))) _map[path] = TargetPath(path);
            else if (!owned && provided) _dependencies.Add(path);
            else return; // Vanilla file the target can keep referencing unchanged.

            foreach (var (origin, bytes) in ContentSources(path))
            {
                IReadOnlyList<string> references;
                try
                {
                    references = ResourceReferences.Read(path, bytes);
                }
                catch (Exception ex)
                {
                    Block("unreadable_resource", $"{origin} ({path}) cannot be parsed: {ex.Message}");
                    continue;
                }

                if (path.EndsWith(".mdl", StringComparison.Ordinal))
                    foreach (var material in references)
                        VisitMaterial(path, material, origin);
                else
                    foreach (var reference in references)
                        Enqueue(GamePath.Normalize(reference));
            }

            if (_swaps.TryGetValue(path, out var swaps))
                foreach (var swap in swaps)
                    Enqueue(GamePath.Normalize(swap.Target));
        }

        private void VisitMaterial(string modelPath, string material, string origin)
        {
            if (ResourceReferences.IsSkinMaterial(material)) return;
            if (material.TrimStart('/').Contains('/'))
            {
                Enqueue(GamePath.Normalize(material));
                return;
            }

            var modelRoot = modelPath[..modelPath.IndexOf("/model/", StringComparison.Ordinal)];
            if (!string.Equals(modelRoot, _srcRoot, StringComparison.Ordinal)) return;
            var name = GamePath.Normalize(material);
            var folders = new SortedSet<int> { _sourceEntry.MaterialId };
            if (_materialFolders.TryGetValue(name, out var supplied)) folders.UnionWith(supplied);

            foreach (var folder in folders)
            {
                var path = $"{_srcRoot}/material/v{folder:D4}/{name}";
                if (folder == _sourceEntry.MaterialId && !ProvidedIn(path, _mod.Default) && !_generated.ContainsKey(path))
                {
                    if (Game(path) is { } vanilla) Generate(path, vanilla);
                    else if (!_files.ContainsKey(path) && !_swaps.ContainsKey(path))
                        Warn("missing_material",
                            $"{origin} uses '{material}', but {path} exists neither in the mod nor in the game." +
                            " If a separate modpack supplies it (a base mod this one builds on, for example), combine the two with Merge modpacks first.");
                }
                Enqueue(path);
            }
        }

        /// <summary>Every byte source the game could load for a path (all options, swaps, vanilla copies).</summary>
        private IEnumerable<(string Origin, byte[] Bytes)> ContentSources(string path)
        {
            if (!ResourceReferences.CanContainReferences(path)) yield break;
            if (_files.TryGetValue(path, out var files))
                foreach (var file in files.DistinctBy(f => f.FullPath, StringComparer.OrdinalIgnoreCase))
                    if (Local(file.FullPath) is { } bytes)
                        yield return ($"{file.Container.Label}: {file.Local}", bytes);
            if (_generated.TryGetValue(path, out var generated))
                yield return ($"game file {path}", generated);
            if (_swaps.TryGetValue(path, out var swaps))
                foreach (var swap in swaps)
                {
                    var target = GamePath.Normalize(swap.Target);
                    if (_files.TryGetValue(target, out var swapped))
                    {
                        foreach (var file in swapped)
                            if (Local(file.FullPath) is { } bytes)
                                yield return ($"{swap.Container.Label}: swap to {target}", bytes);
                    }
                    else if (Game(target) is { } vanilla)
                        yield return ($"{swap.Container.Label}: swap to game file {target}", vanilla);
                }
        }

        private void Generate(string path, byte[] bytes)
        {
            _generated[path] = bytes;
            _plan.Changes.Add(new GearPlanChange("Game dependency", "Default", path, "copied from the game"));
            // A path visited before its vanilla copy existed must be traced again.
            _visited.Remove(path);
            Enqueue(path);
        }

        private bool ProvidedIn(string path, ModContainer container)
            => (_files.TryGetValue(path, out var files) && files.Any(f => f.Container == container)) ||
               (_swaps.TryGetValue(path, out var swaps) && swaps.Any(s => s.Container == container));

        /// <summary>Whether <paramref name="container"/> of the output redirects the normalized game path.</summary>
        private static bool Redirects(ModContainer container, string path)
            => container.FileEntries().Any(e => GamePath.Normalize(e.Key) == path) ||
               container.SwapEntries().Any(e => GamePath.Normalize(e.Key) == path);

        /// <summary>
        /// Whether the output ships a material of the target named for <paramref name="race"/>,
        /// which is what the material half of an EQDP entry makes the game look for.
        /// </summary>
        private bool ShipsRaceMaterial(PenumbraMod result, ushort race)
        {
            var prefix = $"{_tgt.Root}/material/";
            var token = $"c{race:D4}";
            return result.Containers.Any(c => c.FileEntries().Select(e => e.Key).Concat(c.SwapEntries().Select(e => e.Key))
                .Select(GamePath.Normalize)
                .Any(k => k.StartsWith(prefix, StringComparison.Ordinal) && k.EndsWith(".mtrl", StringComparison.Ordinal) &&
                          Path.GetFileName(k).Contains(token, StringComparison.Ordinal)));
        }

        private bool IsOwned(string path) => path.StartsWith(_srcRoot + "/", StringComparison.Ordinal);

        private string TargetPath(string path)
        {
            var match = MaterialPathRegex().Match(path);
            if (match.Success && path.StartsWith(_srcRoot + "/material/", StringComparison.Ordinal))
                return $"{_tgt.Root}/material/v{match.Groups["variant"].Value}/" +
                       GamePath.Normalize(GearPaths.RewriteOwnedReference(match.Groups["name"].Value, _srcEp, _tgtEp));
            return GamePath.Normalize(GearPaths.RewriteGamePath(path, _srcEp, _tgtEp));
        }

        // ── Content rewriting ───────────────────────────────────────────────

        /// <summary>Rewritten bytes per source (absolute local path or game path of a vanilla copy).</summary>
        private Dictionary<string, byte[]> RewriteContents()
        {
            var result = new Dictionary<string, byte[]>(StringComparer.OrdinalIgnoreCase);
            foreach (var (path, target) in _map)
            {
                if (!ResourceReferences.CanContainReferences(path)) continue;
                if (_files.TryGetValue(path, out var files))
                    foreach (var file in files)
                        if (!result.ContainsKey(file.FullPath) && Local(file.FullPath) is { } bytes)
                            Rewrite(file.FullPath, $"{file.Container.Label}: {file.Local}", path, bytes);
                if (_generated.TryGetValue(path, out var generated))
                    Rewrite(path, $"game file {path}", path, generated);
            }
            return result;

            void Rewrite(string id, string origin, string path, byte[] bytes)
            {
                try
                {
                    var replacements = new Dictionary<string, string>(StringComparer.Ordinal);
                    foreach (var reference in ResourceReferences.Read(path, bytes))
                    {
                        var replacement = RewriteReference(path, reference);
                        if (!string.Equals(replacement, reference, StringComparison.Ordinal))
                            replacements[reference] = replacement;
                    }
                    var output = replacements.Count > 0 ? ResourceReferences.Rewrite(path, bytes, replacements) : bytes;
                    foreach (var (from, to) in replacements)
                        _plan.Changes.Add(new GearPlanChange("Reference", origin, from, to));
                    if (_tgt.IsAccessory && path.EndsWith(".mdl", StringComparison.Ordinal))
                        output = ForAccessory(origin, output);
                    if (!ReferenceEquals(output, bytes)) result[id] = output;
                }
                catch (Exception ex)
                {
                    Block("rewrite_failed", $"{origin} cannot be rewritten: {ex.Message}");
                }
            }
        }

        /// <summary>
        /// An accessory slot looks every material up in the item's own folder; only equipment
        /// slots send body materials (skin, bibo, pubes, piercings) to the character's body. So
        /// such a material in a model moved to an accessory is never found, and since the game
        /// loads every listed material before drawing a model, the whole model stays invisible.
        /// Unused ones are dropped here; mesh groups still using one are switched off by the
        /// session, which leaves the parts in place so the Mesh groups tab lines up with the
        /// source model.
        /// </summary>
        private byte[] ForAccessory(string origin, byte[] data)
        {
            MdlFile model;
            try { model = MdlFile.Read(data); }
            catch (InvalidDataException) { return data; }

            var skin = model.Meshes.Select(m => m.MaterialIndex < model.Materials.Length ? model.Materials[m.MaterialIndex] : null)
                .OfType<string>().Where(ResourceReferences.IsSkinMaterial).Distinct().ToList();
            if (skin.Count > 0)
                Warn("accessory_skin_material",
                    $"{origin} has parts with body materials ({string.Join(", ", skin)}): skin, bibo, pubes or piercings. " +
                    "An accessory cannot load these, and the game would not show the model at all, so those mesh groups " +
                    "are switched off in the Mesh groups tab. (In a run of several conversions there is no Mesh groups " +
                    "tab: convert this one on its own to have them left out.)");

            var unused = model.RemoveUnusedMaterials();
            if (unused.Count == 0) return data;
            try
            {
                var rebuilt = model.WriteRebuilt();
                _plan.Changes.Add(new GearPlanChange("Model", origin, string.Join(", ", unused), "unused material dropped"));
                return rebuilt;
            }
            catch (InvalidDataException ex)
            {
                Warn("unused_material_kept",
                    $"{origin} lists materials no part uses ({string.Join(", ", unused)}), which may keep it from showing, " +
                    $"but it cannot be rebuilt without them: {ex.Message}");
                return data;
            }
        }

        private string RewriteReference(string filePath, string reference)
        {
            var isShortMaterial = filePath.EndsWith(".mdl", StringComparison.Ordinal) &&
                                  !reference.TrimStart('/').Contains('/');
            if (isShortMaterial)
                return ResourceReferences.IsSkinMaterial(reference)
                    ? reference
                    : GearPaths.RewriteOwnedReference(reference, _srcEp, _tgtEp);
            return _map.TryGetValue(GamePath.Normalize(reference), out var mapped) ? mapped : reference;
        }

        // ── Output: in place ────────────────────────────────────────────────

        private void BuildInPlace(Dictionary<string, byte[]> rewritten)
        {
            var result = _plan.Result;
            // Additive mode moves nothing, so the (expensive) question of what may move is moot.
            var exclusive = KeepsSource ? [] : ExclusiveSourcePaths();
            var locals = _context.Locals;

            // Which (container, key) pairs reference each local file, and whether each pair moves.
            var usage = new Dictionary<string, List<bool>>(StringComparer.OrdinalIgnoreCase);
            foreach (var container in _mod.Containers)
            foreach (var (key, local) in container.FileEntries())
            {
                var path = GamePath.Normalize(key);
                var moves = !KeepsSource && _map.TryGetValue(path, out var target) && target != path && exclusive.Contains(path);
                if (!usage.TryGetValue(GamePath.NormalizeLocal(local), out var list))
                    usage[GamePath.NormalizeLocal(local)] = list = [];
                list.Add(moves);
            }

            var localTargets = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
            foreach (var original in _mod.Containers)
            {
                var container = result.GetContainer(original.Address);
                foreach (var (key, local) in original.FileEntries())
                {
                    var path = GamePath.Normalize(key);
                    if (!_map.TryGetValue(path, out var target)) continue;
                    var provider = Provider(path, original, key);
                    if (provider == null) continue;
                    if (target == path)
                    {
                        // Same root (cross-slot within one set): the key stays, only references change.
                        // There is no second key to add here, and rewriting the file would retarget
                        // the original's own references, so additive mode leaves it alone and says so.
                        if (KeepsSource)
                        {
                            if (rewritten.ContainsKey(provider.FullPath))
                                Warn("additive_shared_path",
                                    $"{path} is loaded under the same game path by both items, so it keeps the " +
                                    "original's references and the converted item reuses them.");
                            continue;
                        }

                        if (rewritten.TryGetValue(provider.FullPath, out var updated) && localTargets.TryAdd(provider.FullPath, local))
                            _plan.Files.Add(new PlannedFileOperation(LocalFileOperation.Write, null,
                                Path.GetRelativePath(_root, provider.FullPath), updated, "references retargeted"));
                        continue;
                    }

                    var newLocal = InPlaceLocal(provider, usage, rewritten, locals, localTargets);
                    var files = container.GetOrCreateFiles();
                    if (FindKey(files, target) is { } existing && !_map.ContainsKey(GamePath.Normalize(existing)))
                    {
                        Block("target_conflict", $"{original.Label} already redirects {target}.");
                        continue;
                    }

                    var moves = !KeepsSource && exclusive.Contains(path);
                    if (moves) files.Remove(key);
                    files[target] = GamePath.ToLocal(newLocal);
                    _plan.Changes.Add(new GearPlanChange("Game path", original.Label, key,
                        moves ? target
                        : KeepsSource ? $"{target} (added; the original stays)"
                        : $"{target} (source kept: still used elsewhere)"));
                }

                foreach (var (key, value) in original.SwapEntries())
                {
                    var path = GamePath.Normalize(key);
                    if (!_map.TryGetValue(path, out var target) || target == path) continue;
                    var swaps = container.GetOrCreateFileSwaps();
                    if (FindKey(swaps, target) is { } existing && !_map.ContainsKey(GamePath.Normalize(existing)))
                    {
                        Block("target_conflict", $"{original.Label} already swaps {target}.");
                        continue;
                    }

                    if (!KeepsSource && exclusive.Contains(path)) swaps.Remove(key);
                    swaps[target] = _map.GetValueOrDefault(GamePath.Normalize(value), value);
                    _plan.Changes.Add(new GearPlanChange("File swap", original.Label, key, target));
                }
            }

            AddGeneratedFiles(result.Default, rewritten, locals);
            RetargetManipulations(result, keepUnrelated: true, keepSource: KeepsSource);
            InjectDefaults(result);
            RetargetImcGroups(result, dropUnrelated: false);

            // Game files the target needs but the mod does not ship land in the default container,
            // so they apply even when the option holding the converted item is switched off.
            if (KeepsSource && _generated.Count > 0)
                Warn("additive_default_dependency",
                    $"{_generated.Count} file(s) the game provides were added to the mod's default files so the " +
                    "converted item is complete. They apply whenever the mod is enabled, not only when the " +
                    "option holding the converted item is on.");
        }

        private string InPlaceLocal(FileProvider provider, Dictionary<string, List<bool>> usage,
            Dictionary<string, byte[]> rewritten, LocalAllocator locals, Dictionary<string, string> assigned)
        {
            if (assigned.TryGetValue(provider.FullPath, out var known)) return known;
            var relative = Path.GetRelativePath(_root, provider.FullPath);
            var allMove = !KeepsSource &&
                          usage.TryGetValue(GamePath.NormalizeLocal(provider.Local), out var uses) && uses.All(u => u);
            var content = rewritten.GetValueOrDefault(provider.FullPath);
            var renamed = RewriteLocal(relative);
            string destination;

            if (allMove)
            {
                // Nothing else uses the file: move it to a target-named path and edit it there.
                destination = string.Equals(renamed, relative, StringComparison.OrdinalIgnoreCase)
                    ? relative
                    : locals.Reserve(renamed, relative);
                if (!string.Equals(destination, relative, StringComparison.OrdinalIgnoreCase))
                {
                    _plan.Files.Add(new PlannedFileOperation(LocalFileOperation.Move, relative, destination, null,
                        "renamed with its converted game path"));
                    locals.Release(relative);
                }
                if (content != null)
                    _plan.Files.Add(new PlannedFileOperation(LocalFileOperation.Write, null, destination, content,
                        "references retargeted"));
            }
            else if (content != null)
            {
                // Still used unchanged elsewhere: the converted copy gets its own file.
                destination = locals.Reserve(string.Equals(renamed, relative, StringComparison.OrdinalIgnoreCase)
                    ? AppendSuffix(relative, "_" + _tgtEp.Token) : renamed, null);
                _plan.Files.Add(new PlannedFileOperation(LocalFileOperation.Write, null, destination, content,
                    "converted copy of a shared file"));
            }
            else destination = relative;

            _plan.InputFiles.Add(provider.FullPath);
            assigned[provider.FullPath] = destination;
            return destination;
        }

        /// <summary>
        /// Source paths an in-place conversion may move away. Anything referenced by a mod
        /// resource that is not converted, or named for a different slot (deliberately shared
        /// part textures), stays in place so the rest of the mod keeps working.
        /// </summary>
        private HashSet<string> ExclusiveSourcePaths()
        {
            var external = new HashSet<string>(StringComparer.Ordinal);
            foreach (var (path, providers) in _files)
            {
                if (_map.ContainsKey(path) || !ResourceReferences.CanContainReferences(path)) continue;
                foreach (var provider in providers.DistinctBy(p => p.FullPath, StringComparer.OrdinalIgnoreCase))
                {
                    if (Local(provider.FullPath) is not { } bytes) continue;
                    IReadOnlyList<string> references;
                    try { references = ResourceReferences.Read(path, bytes); }
                    catch (Exception) { continue; }
                    foreach (var reference in references)
                    {
                        if (path.EndsWith(".mdl", StringComparison.Ordinal) && !reference.TrimStart('/').Contains('/'))
                        {
                            var root = path[..Math.Max(0, path.IndexOf("/model/", StringComparison.Ordinal))];
                            if (root == _srcRoot)
                                foreach (var candidate in _map.Keys.Where(k =>
                                             k.EndsWith("/" + GamePath.Normalize(reference), StringComparison.Ordinal)))
                                    external.Add(candidate);
                        }
                        else external.Add(GamePath.Normalize(reference));
                    }
                }
            }

            var slotToken = $"{_srcEp.Token}_{_src.Slot.Suffix()}";
            return _map.Keys.Where(p => !external.Contains(p) && ContainsToken(Path.GetFileName(p), slotToken))
                .ToHashSet(StringComparer.Ordinal);
        }

        // ── Output: new mod ─────────────────────────────────────────────────

        private void BuildNewMod(Dictionary<string, byte[]> rewritten)
        {
            var result = _plan.Result;
            var locals = _context.Locals;
            var assigned = new Dictionary<(string, bool), string>();

            foreach (var original in _mod.Containers)
            {
                var container = result.GetContainer(original.Address);
                foreach (var (key, _) in original.FileEntries())
                {
                    var path = GamePath.Normalize(key);
                    var owned = _map.TryGetValue(path, out var target);
                    var converted = owned && target != path;
                    if (!owned && !_dependencies.Contains(path)) continue;
                    if (Provider(path, original, key) is not { } provider) continue;

                    var newKey = converted ? target! : path;
                    var newLocal = NewModLocal(provider, rename: converted, useRewritten: owned);
                    var files = container.GetOrCreateFiles();
                    if (FindKey(files, newKey) is { } clash &&
                        !string.Equals(GamePath.NormalizeLocal(Json.GetString(files[clash]) ?? string.Empty),
                            GamePath.NormalizeLocal(newLocal), StringComparison.Ordinal))
                    {
                        Block("target_conflict", $"{original.Label}: two different files map to {newKey}.");
                        continue;
                    }

                    files[newKey] = GamePath.ToLocal(newLocal);
                    if (converted)
                        _plan.Changes.Add(new GearPlanChange("Game path", original.Label, key, newKey));
                }

                foreach (var (key, value) in original.SwapEntries())
                {
                    var path = GamePath.Normalize(key);
                    var converted = _map.TryGetValue(path, out var target) && target != path;
                    if (!converted && !_map.ContainsKey(path) && !_dependencies.Contains(path)) continue;
                    container.GetOrCreateFileSwaps()[converted ? target! : path] =
                        _map.GetValueOrDefault(GamePath.Normalize(value), value);
                    if (converted) _plan.Changes.Add(new GearPlanChange("File swap", original.Label, key, target!));
                }
            }

            AddGeneratedFiles(result.Default, rewritten, locals);
            RetargetManipulations(result, keepUnrelated: false);
            InjectDefaults(result);
            RetargetImcGroups(result, dropUnrelated: true);
            _context.AddFinalizerOnce("new-mod", mod =>
            {
                PruneGroups(mod);
                mod.Meta.Remove("DefaultPreferredItems");
                mod.Meta["Identifier"] = Guid.NewGuid().ToString();
            });
            return;

            string NewModLocal(FileProvider provider, bool rename, bool useRewritten)
            {
                var content = useRewritten ? rewritten.GetValueOrDefault(provider.FullPath) : null;
                var slot = (provider.FullPath, content != null);
                if (assigned.TryGetValue(slot, out var known)) return known;
                var relative = Path.GetRelativePath(_root, provider.FullPath);
                var destination = locals.Reserve(rename || content != null ? RewriteLocal(relative) : relative, null);
                _plan.InputFiles.Add(provider.FullPath);
                _plan.Files.Add(content != null
                    ? new PlannedFileOperation(LocalFileOperation.Write, relative, destination, content, "references retargeted")
                    : new PlannedFileOperation(LocalFileOperation.Copy, relative, destination, null,
                        rename ? "converted resource" : "shared dependency"));
                assigned[slot] = destination;
                return destination;
            }
        }

        private void AddGeneratedFiles(ModContainer defaults, Dictionary<string, byte[]> rewritten, LocalAllocator locals)
        {
            foreach (var (source, bytes) in _generated)
            {
                var target = _map[source];
                var files = defaults.GetOrCreateFiles();
                if (FindKey(files, target) != null)
                {
                    if (!EditsSourceMod) continue;
                    Block("target_conflict", $"The default option already redirects {target}.");
                    continue;
                }

                var destination = locals.Reserve(GamePath.ToLocal(target), null);
                _plan.Files.Add(new PlannedFileOperation(LocalFileOperation.Write, null, destination,
                    rewritten.GetValueOrDefault(source, bytes), $"game file {source}"));
                files[target] = destination;
            }
        }

        private string RewriteLocal(string relative)
        {
            var slashed = relative.Replace('\\', '/');
            var rewritten = GearPaths.RewriteGamePath(slashed, _srcEp, _tgtEp);
            return GamePath.ToLocal(rewritten);
        }

        private static string AppendSuffix(string relative, string suffix)
            => Path.Combine(Path.GetDirectoryName(relative) ?? string.Empty,
                Path.GetFileNameWithoutExtension(relative) + suffix + Path.GetExtension(relative));

        /// <summary>The provider of one Files entry, or null when its local file is missing (Penumbra ignores those too).</summary>
        private FileProvider? Provider(string path, ModContainer container, string key)
        {
            var provider = _files.TryGetValue(path, out var list)
                ? list.FirstOrDefault(f => f.Container == container && f.Key == key)
                : null;
            if (provider == null || File.Exists(provider.FullPath)) return provider;
            Warn("missing_local_file",
                $"{container.Label}: {provider.Local} (for {key}) does not exist and is skipped.");
            return null;
        }

        private static string? FindKey(JsonObject obj, string key)
            => obj.Select(p => p.Key).FirstOrDefault(k => string.Equals(GamePath.Normalize(k), key, StringComparison.Ordinal));

        // ── Metadata ────────────────────────────────────────────────────────

        /// <param name="keepSource">
        /// Also keep the source item's own entry next to the converted one. Their identities
        /// differ (a different set or slot), so the dedup below leaves both standing.
        /// </param>
        private void RetargetManipulations(PenumbraMod result, bool keepUnrelated, bool keepSource = false)
        {
            foreach (var original in _mod.Containers)
            {
                if (original.Manipulations is not { } manipulations) continue;
                var output = new JsonArray();
                var retargeted = new List<JsonObject>();
                foreach (var node in manipulations)
                {
                    if (node is not JsonObject manipulation) continue;
                    var outcome = GearManipulations.Retarget(manipulation, _src, _tgt, _sourceVariant, TargetVariants);
                    switch (outcome.Kind)
                    {
                        case RetargetKind.Unrelated:
                            if (keepUnrelated) output.Add(manipulation.DeepClone());
                            break;
                        case RetargetKind.OtherVariant:
                            if (keepUnrelated) output.Add(manipulation.DeepClone());
                            else _plan.Changes.Add(new GearPlanChange("Metadata", original.Label,
                                GearManipulations.Describe(manipulation), "dropped (other source variant)"));
                            break;
                        case RetargetKind.NotTransferable:
                            Warn("metadata_not_transferable", $"{original.Label}: {outcome.Reason}");
                            if (keepUnrelated) output.Add(manipulation.DeepClone());
                            break;
                        case RetargetKind.Retargeted:
                            if (keepSource) output.Add(manipulation.DeepClone());
                            retargeted.AddRange(outcome.Results);
                            foreach (var item in outcome.Results)
                                _plan.Changes.Add(new GearPlanChange("Metadata", original.Label,
                                    GearManipulations.Describe(manipulation), GearManipulations.Describe(item)));
                            break;
                    }
                }

                // A converted source entry wins over an existing entry for the same target identity.
                var identities = retargeted.Select(GearManipulations.Identity).ToHashSet(StringComparer.Ordinal);
                var merged = new JsonArray();
                foreach (var node in output)
                    if (node is JsonObject existing && identities.Contains(GearManipulations.Identity(existing)))
                        Warn("metadata_replaced", $"{original.Label}: {GearManipulations.Describe(existing)} is replaced by the converted item's entry.");
                    else merged.Add(node?.DeepClone());
                var seen = new HashSet<string>(StringComparer.Ordinal);
                foreach (var item in retargeted)
                    if (seen.Add(GearManipulations.Identity(item))) merged.Add(item);

                var container = result.GetContainer(original.Address);
                if (merged.Count > 0) container.Node["Manipulations"] = merged;
                else container.Node.Remove("Manipulations");
            }
        }

        /// <summary>
        /// Adds the source item's vanilla metadata for the target wherever it differs from
        /// the target's vanilla metadata and the mod does not define it explicitly.
        /// </summary>
        private void InjectDefaults(PenumbraMod result)
        {
            var defaults = result.Default;
            var existing = (defaults.Manipulations?.OfType<JsonObject>() ?? [])
                .Select(GearManipulations.Identity).ToHashSet(StringComparer.Ordinal);
            var added = new List<JsonObject>();

            void Add(JsonObject manipulation, string note)
            {
                if (!existing.Add(GearManipulations.Identity(manipulation))) return;
                added.Add(manipulation);
                _plan.Changes.Add(new GearPlanChange("Metadata", "Default", note, GearManipulations.Describe(manipulation)));
            }

            // EQDP: which races have their own model and material for the slot. Only races the
            // output ships a model for are touched: each is marked as having one, or the game never
            // asks for the file and loads the model of the race it falls back to instead (an item
            // with no female model shows the male one). Every other race keeps the target's own.
            foreach (var race in GenderRaces.Playable)
            {
                var model = GamePath.Normalize(GearSlots.ModelPath(_tgt, race));
                var holders = result.Containers.Where(c => Redirects(c, model)).ToList();
                if (holders.Count == 0) continue;

                var targetBits = Game(_tgt.Slot.EqdpFile(race)) is { } bytes
                    ? GameMetadata.EqdpBits(GameMetadata.ReadEqdp(bytes, _tgt.SetId), _tgt.Slot)
                    : (ushort)0;
                // The source's own entry says whether its materials are per race; without a
                // model bit it says nothing, so the shipped files decide.
                var bits = SourceEqdp(race) is { } source &&
                           GameMetadata.EqdpHasModel(GameMetadata.RepositionEqdp(source, _src.Slot, _tgt.Slot), _tgt.Slot)
                    ? GameMetadata.EqdpBits(GameMetadata.RepositionEqdp(source, _src.Slot, _tgt.Slot), _tgt.Slot)
                    : (ushort)(2 | (ShipsRaceMaterial(result, race) ? 1 : 0));
                if (bits == targetBits) continue;

                var entry = GearManipulations.Eqdp(_tgt, race, (ushort)(bits << _tgt.Slot.EqdpShift()));
                var note = $"{RaceNames.Name(race)} model shipped by the mod";
                if (holders.Any(c => c.Address.IsDefault))
                {
                    Add(entry, note);
                    continue;
                }

                // The model only exists while its option is on, and so must the entry: pointing the
                // game at a file nobody provides would make the item disappear for that race.
                foreach (var holder in holders)
                {
                    var own = holder.GetOrCreateManipulations();
                    var identity = GearManipulations.Identity(entry);
                    if (own.OfType<JsonObject>().Any(m => GearManipulations.Identity(m) == identity)) continue;
                    own.Add(entry.DeepClone());
                    _plan.Changes.Add(new GearPlanChange("Metadata", holder.Label, note, GearManipulations.Describe(entry)));
                }
            }

            // EQP / GMP: visibility of other slots and the visor, only meaningful for the same slot.
            if (_src.Slot.HasEqp() && _tgt.Slot == _src.Slot && Game(GameMetadata.EqpFile) is { } eqp)
            {
                var source = GameMetadata.ReadExpandedEntry(eqp, _src.SetId, GameMetadata.DefaultEqpEntry);
                if (source != GameMetadata.ReadExpandedEntry(eqp, _tgt.SetId, GameMetadata.DefaultEqpEntry))
                    Add(GearManipulations.Eqp(_tgt, source), "source visibility flags");
            }
            else if (_src.Slot.HasEqp() && _tgt.Slot != _src.Slot)
                Warn("eqp_not_transferable",
                    $"Visibility flags (EQP) of the {_src.Slot} slot do not apply to the {_tgt.Slot} slot; the target keeps its own.");

            if (_src.Slot.HasGmp() && _tgt.Slot.HasGmp() && Game(GameMetadata.GmpFile) is { } gmp)
            {
                var source = GameMetadata.ReadExpandedEntry(gmp, _src.SetId, 0);
                if (source != GameMetadata.ReadExpandedEntry(gmp, _tgt.SetId, 0))
                    Add(GearManipulations.Gmp(_tgt, source), "source visor settings");
            }

            // EST: extra skeletons (physics bones) for heads and bodies.
            if (_src.Slot.EstFile() is { } estFile && Game(estFile) is { } est)
            {
                var transferable = _tgt.Slot.EstType() == _src.Slot.EstType();
                foreach (var race in GenderRaces.Playable)
                {
                    ushort source = 0, target = 0;
                    try
                    {
                        ExtraSkeletonTable.TryGet(est, race, _src.SetId, out source);
                        if (transferable) ExtraSkeletonTable.TryGet(est, race, _tgt.SetId, out target);
                    }
                    catch (InvalidDataException) { break; }

                    if (source == target) continue;
                    if (!transferable)
                    {
                        Warn("est_not_transferable",
                            $"The source uses an extra skeleton that the {_tgt.Slot} slot cannot load; physics bones may not move.");
                        break;
                    }
                    Add(GearManipulations.Est(_tgt, race, source), "source extra skeleton");
                }
            }

            // IMC: every target variant shows the source variant's material set and attributes.
            foreach (var variant in TargetVariants)
            {
                var vanilla = _targetImc.TryGetValue(variant, out var entry) ? entry : (ImcEntry?)null;
                var wanted = _sourceEntry with { SoundId = vanilla?.SoundId ?? _sourceEntry.SoundId };
                if (vanilla == wanted) continue;
                Add(GearManipulations.Imc(_tgt, variant, wanted), $"source variant {_sourceVariant}");
            }

            if (added.Count == 0) return;
            var manipulations = defaults.GetOrCreateManipulations();
            foreach (var manipulation in added) manipulations.Add(manipulation);
        }

        private void RetargetImcGroups(PenumbraMod result, bool dropUnrelated)
        {
            var added = new List<ModGroup>();
            for (var i = result.Groups.Count - 1; i >= 0; i--)
            {
                var group = result.Groups[i];
                if (!group.IsImc) continue;
                if (GearManipulations.ImcGroupMatches(group.Node, _src, _sourceVariant))
                {
                    // A Penumbra IMC group carries exactly one identifier, so one group cannot
                    // drive two items. Keeping the source means the converted item needs its own
                    // copy, which is the one place additive mode cannot share a toggle.
                    var node = KeepsSource ? Duplicate(group, result) : group.Node;
                    var identifier = (JsonObject)node["Identifier"]!;
                    identifier["PrimaryId"] = Json.SameKindNumber(identifier["PrimaryId"], _tgt.SetId);
                    identifier["EquipSlot"] = _tgt.Slot.EquipSlotName();
                    identifier["ObjectType"] = _tgt.Slot.ImcObjectType();
                    identifier["Variant"] = Json.SameKindNumber(identifier["Variant"], TargetVariant);
                    if (TargetVariants.Count > 1) node["AllVariants"] = true;
                    _plan.Changes.Add(new GearPlanChange("IMC group", group.Name, Describe(_src), Describe(_tgt with { Variant = TargetVariant })));
                    if (KeepsSource)
                    {
                        added.Add(new ModGroup(node, result.Groups.Count + added.Count));
                        Warn("additive_imc_group_duplicated",
                            $"'{group.Name}' controls the IMC attributes of a single item, so the converted item " +
                            "got its own copy of it. Everything else stays under the original's options, but these " +
                            "attributes are toggled separately.");
                    }
                }
                else if (dropUnrelated) group.Node["__apmc_drop"] = true;
            }

            result.Groups.AddRange(added);
        }

        /// <summary>A copy of an IMC group with its own identity, ready to be retargeted.</summary>
        private static JsonObject Duplicate(ModGroup group, PenumbraMod result)
        {
            var node = (JsonObject)group.Node.DeepClone();
            node["Id"] = Guid.NewGuid().ToString();
            node["Name"] = UniqueGroupName(result, group.Name);
            foreach (var option in (node["Options"] as JsonArray)?.OfType<JsonObject>() ?? [])
                option["Id"] = Guid.NewGuid().ToString();
            return node;
        }

        /// <summary>Penumbra identifies a group by name in its UI, so the copy needs its own.</summary>
        private static string UniqueGroupName(PenumbraMod result, string name)
        {
            var taken = result.Groups.Select(g => g.Name).ToHashSet(StringComparer.OrdinalIgnoreCase);
            var candidate = $"{name} (converted)";
            for (var i = 2; taken.Contains(candidate); i++) candidate = $"{name} (converted {i})";
            return candidate;
        }

        private void PruneGroups(PenumbraMod result)
            => ModGroupPruning.Prune(result, group => _plan.Changes.Add(
                new GearPlanChange("Group", group.Name, "option group", "not included (no converted content)")));

        // ── Helpers ─────────────────────────────────────────────────────────

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
            else Warn("missing_local_file", $"{Path.GetRelativePath(_root, fullPath)} is referenced by the mod but does not exist.");
            _localCache[fullPath] = bytes;
            return bytes;
        }

        private byte[]? Game(string path)
        {
            path = GamePath.Normalize(path);
            if (_gameCache.TryGetValue(path, out var cached)) return cached;
            byte[]? bytes;
            try { bytes = _game.ReadFile(path); }
            catch (Exception) { bytes = null; }
            _gameCache[path] = bytes;
            return bytes;
        }

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

        private static string Describe(GearItem item) => $"{item.Slot} {item.PathEndpoint.Token} (variant {item.Variant})";
    }

    /// <summary>Hands out unique mod-relative destinations (case-insensitive, like Windows).</summary>
    internal sealed class LocalAllocator
    {
        private readonly HashSet<string> _taken = new(StringComparer.OrdinalIgnoreCase);
        private readonly bool _reuseReleased;

        /// <param name="reuseReleased">
        /// False when several conversions share the allocator. A name one of them vacates must
        /// not be handed to another, or the second would write over a file the first still moves.
        /// </param>
        public LocalAllocator(string? existingRoot, bool reuseReleased = true)
        {
            _reuseReleased = reuseReleased;
            if (existingRoot == null) return;
            foreach (var file in System.IO.Directory.EnumerateFiles(existingRoot, "*", SearchOption.AllDirectories))
                _taken.Add(Path.GetRelativePath(existingRoot, file));
        }

        /// <summary>Reserves <paramref name="wanted"/> (or a numbered variant). <paramref name="self"/> may be reused.</summary>
        public string Reserve(string wanted, string? self)
        {
            wanted = GamePath.ToLocal(wanted);
            var candidate = wanted;
            for (var i = 2; _taken.Contains(candidate) &&
                            !string.Equals(candidate, self, StringComparison.OrdinalIgnoreCase); i++)
                candidate = AppendNumber(wanted, i);
            _taken.Add(candidate);
            return candidate;
        }

        public void Release(string path)
        {
            if (_reuseReleased) _taken.Remove(path);
        }

        private static string AppendNumber(string path, int number)
            => Path.Combine(Path.GetDirectoryName(path) ?? string.Empty,
                $"{Path.GetFileNameWithoutExtension(path)}_{number}{Path.GetExtension(path)}");
    }
}
