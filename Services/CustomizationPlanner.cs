using System;
using System.Collections.Generic;
using System.Collections.Immutable;
using System.IO;
using System.Linq;
using System.Text.Json;
using System.Text.Json.Nodes;
using UniversalModConverter.Core;
using UniversalModConverter.Models;
using Dalamud.Plugin.Services;

namespace UniversalModConverter.Services;

internal sealed partial class CustomizationPlanner(GameDataService? gameData, IPluginLog log,
    SkeletonHierarchyService? skeletons = null)
{
    private static readonly HashSet<string> StructuredExtensions =
        new([".mdl", ".mtrl", ".avfx", ".atex", ".sklb", ".pap", ".tmb"],
            StringComparer.OrdinalIgnoreCase);

    public void Plan(ConversionTask task)
    {
        if (!CustomizationKinds.TryGet(task.Kind, out var descriptor))
            throw new InvalidDataException($"{task.Kind} is not a customization asset kind.");
        var targetKind = task.TargetCustomizationKind ?? task.Kind;
        if (!CustomizationKinds.TryGet(targetKind, out var targetDescriptor) ||
            !CustomizationKinds.CanConvert(task.Kind, targetKind))
            throw new InvalidDataException($"Unsupported customization conversion: {task.Kind} -> {targetKind}.");
        if (task.SourceGenderRace is not { } sourceRace || task.TargetGenderRace is not { } targetRace)
            throw new InvalidDataException("Source and target playable race/gender codes are required.");
        if (!descriptor.SupportsRace(sourceRace))
            throw new InvalidDataException($"{descriptor.DisplayName} is not valid for {RaceNames.Describe(sourceRace)}.");
        if (!targetDescriptor.SupportsRace(targetRace))
            throw new InvalidDataException($"{targetDescriptor.DisplayName} is not valid for {RaceNames.Describe(targetRace)}.");
        if (CustomizationTargets.BlockReason(task.Kind, sourceRace, targetKind, targetRace) is { } blocked)
            throw new InvalidDataException(blocked);

        var oldId = ParseId(task.OldIdPadded);
        var newId = ParseId(task.NewIdPadded);
        var root = Path.GetFullPath(task.ModDirectory);
        var source = new CustomizationPathEndpoint(task.Kind, sourceRace, oldId);

        // Textures (and face or skin materials) alone are offered to other races by the
        // TextureFanOutPlanner instead; anything with a model carries its race and ID inside it
        // and has exactly one target.
        var mod = PenumbraMod.Load(root);
        if (CustomizationDetection.CanFanOut(mod, source))
            throw new InvalidDataException("This root only holds textures or materials; choose where to add its paths instead.");

        if (oldId == newId && sourceRace == targetRace && task.Kind == targetKind)
            throw new InvalidDataException("Source and target customization roots are identical.");

        var target = new CustomizationPathEndpoint(targetKind, targetRace, newId);
        var resources = ReadResources(root);
        var keys = new KeyRules(source, target, resources);
        var references = new References();
        // The index keeps one file per game path, the last option's; every option's file is converted.
        var optionFiles = new List<(string GamePath, string Local)>();
        foreach (var container in mod.Containers)
        foreach (var (key, local) in container.FileEntries())
        {
            try { optionFiles.Add((Normalize(key), PathSafety.ResolveRelative(root, GamePath.ToLocal(local)))); }
            catch (InvalidDataException) { /* a path outside the mod folder is no file of the mod's */ }
        }
        var movedRoots = CustomizationPaths.MovedRoots(source, target);
        var missing = new List<string>();
        var assets = DiscoverAssets(root, resources.Files, optionFiles, movedRoots, references, missing);
        foreach (var file in missing.Distinct(StringComparer.OrdinalIgnoreCase))
            task.Diagnostics.Add(new PlanDiagnostic("missing_local_file",
                $"The mod redirects a game path under this root to '{Path.GetRelativePath(root, file)}', which does not exist; it is skipped.",
                false));
        if (assets.Count == 0)
            throw new InvalidDataException(resources.FileSwaps.Keys.Any(key => movedRoots.Any(moved => CustomizationPaths.Contains(key, moved)))
                ? $"This {descriptor.DisplayName.ToLowerInvariant()} root only holds file swaps, which cannot be converted. " +
                  "Convert the mod it swaps to instead."
                : $"No {descriptor.DisplayName.ToLowerInvariant()} root matched c{sourceRace:D4}/{descriptor.Token(oldId)}.");

        task.AllAssetFiles.Clear();
        task.AllAssetFiles.AddRange(assets);
        var keepInPlace = KeepInPlace(root, mod, keys);
        foreach (var file in assets)
        {
            // Files behind shared material roots stay where they are; the target gets
            // additional keys pointing at them instead (see KeyRules).
            var renamed = keepInPlace.Contains(file) || keys.IsSharedMaterialFile(file)
                ? file
                : CustomizationPaths.Rewrite(file, source, target);
            if (!string.Equals(file, renamed, StringComparison.OrdinalIgnoreCase))
                task.PlannedRenames.Add(new PlannedRename { OldPath = file, NewPath = renamed });

            if (!StructuredExtensions.Contains(Path.GetExtension(file))) continue;
            var planned = new PlannedBinaryPatch { FilePath = file };
            foreach (var path in references.Of(file))
            {
                var replacement = CustomizationPaths.RewriteOwnedReference(path, source, target);
                // An embedded texture is a dependency, not a destination declaration.
                // Only move it when a corresponding Files/FileSwaps mapping moves too.
                if (string.Equals(Path.GetExtension(file), ".mtrl", StringComparison.OrdinalIgnoreCase) &&
                    !resources.Files.ContainsKey(Normalize(path)) &&
                    !resources.FileSwaps.ContainsKey(Normalize(path)))
                    replacement = path;
                if (string.Equals(Path.GetExtension(file), ".mdl", StringComparison.OrdinalIgnoreCase) &&
                    sourceRace != targetRace)
                    replacement = CustomizationPaths.FixSkinMaterialReference(replacement, targetRace);
                if (!string.Equals(path, replacement, StringComparison.Ordinal))
                    planned.Patches.Add(new BinaryStringPatch { OldString = path, NewString = replacement });
            }
            if (planned.Patches.Count > 0)
            {
                // MTRL string offsets must be rebuilt when TexTools-style hair
                // ownership or fake-part suffixes make a path longer.
                if (string.Equals(Path.GetExtension(file), ".mtrl", StringComparison.OrdinalIgnoreCase))
                {
                    var replacements = planned.Patches.ToDictionary(p => p.OldString, p => p.NewString,
                        StringComparer.Ordinal);
                    try { _ = MtrlFile.RewritePaths(File.ReadAllBytes(file), replacements); }
                    catch (Exception ex) when (ex is InvalidDataException or IOException or ArgumentException or IndexOutOfRangeException)
                    {
                        task.Diagnostics.Add(new PlanDiagnostic("malformed_mtrl", $"{Path.GetFileName(file)}: {ex.Message}", true));
                    }
                }
                task.PlannedBinaryPatches.Add(planned);
            }
        }

        var materialDependencies = PlanMaterialDependencies(task, source, target, resources, keys);
        PlanDeformation(task, sourceRace, targetRace, source, resources);
        // Adding to the mod keeps the source working: what the conversion changes is changed in
        // copies, and the target's paths are added beside the source's rather than moved.
        var keepSource = task.PlanMode.KeepsSource();
        if (keepSource) KeepSourceFiles(task, root, mod, keys, target);
        PlanJson(task, root, descriptor, source, target, resources, materialDependencies, keys, keepSource);
        PlanExtraSkeleton(task, root, mod, descriptor, source, target, resources);
        var models = CollectOutputModels(task, root, mod, keys);
        if (keepSource && task.OwnOption is { } own && PlanConvertedOption(task, root, descriptor, own) is { Moved: true } moved)
        {
            // The models now load from the converted item's own option.
            var paths = moved.MovedFiles.Select(f => f.GamePath).ToHashSet(StringComparer.Ordinal);
            models = [.. models.Select(m => m.GamePaths.Any(paths.Contains) ? m with { Options = [moved.Label!] } : m)];
        }
        task.OutputModels.AddRange(models);
    }

    /// <summary>
    /// Adding to the mod with an option of the converted item's own: the files the conversion
    /// makes (the copies it changes, the materials it takes from the game) and the metadata it adds
    /// go into that option rather than beside the source's. Worked out on the definition as the
    /// other planned changes leave it, and made by a change of its own after them.
    /// </summary>
    private static ConvertedOptionOutcome PlanConvertedOption(ConversionTask task, string root,
        CustomizationKindDescriptor descriptor, ConvertedOptionRequest request)
    {
        var file = Path.Combine(root, PenumbraMod.MetaFileName);
        var newFiles = task.PlannedRenames.Where(r => r.KeepsOriginal).Select(r => r.NewPath)
            .Concat(task.PlannedGeneratedFiles.Select(g => g.FilePath))
            .Select(path => GamePath.NormalizeLocal(Path.GetRelativePath(root, path)))
            .Distinct(StringComparer.Ordinal)
            .Order(StringComparer.Ordinal)
            .ToArray();
        var planned = task.PlannedJsonChanges.FirstOrDefault(j => string.Equals(j.FilePath, file, StringComparison.OrdinalIgnoreCase));
        var raw = File.ReadAllText(file);
        if (ModConverterService.ApplyJsonChanges(raw, planned?.Changes ?? [], out _) is not JsonObject after ||
            Parse(file) is not JsonObject before)
            return ConvertedOptionOutcome.Nothing;

        var outcome = ConvertedOption.Move(PenumbraMod.FromJson(before), PenumbraMod.FromJson(after),
            newFiles.ToHashSet(StringComparer.Ordinal), request.Name, request.Description);
        if (outcome.Refusal is { } reason)
            task.Diagnostics.Add(new PlanDiagnostic("converted_option_refused",
                $"The converted {descriptor.DisplayName.ToLowerInvariant()} gets no option of its own: {reason} " +
                "Everything is added beside the original's.", false));
        if (outcome.Kept is { } kept) task.Diagnostics.Add(new PlanDiagnostic("converted_option_kept", kept, false));
        if (!outcome.Moved) return outcome;
        if (outcome.Placement is { } placement) task.ConvertedPlacements.Add(placement);

        if (planned == null) task.PlannedJsonChanges.Add(planned = new PlannedJsonChange { FilePath = file });
        var count = outcome.MovedFiles.Select(f => f.GamePath).Distinct().Count();
        planned.Changes.Add(new JsonFieldChange
        {
            JsonPath   = "<root>.Groups",
            ChangeType = ModConverterService.ConvertedOptionChange,
            OldValue   = (count == 1 ? "1 new file" : $"{count} new files") +
                         (outcome.Manipulations switch { 0 => string.Empty, 1 => ", 1 metadata entry", var n => $", {n} metadata entries" }),
            NewValue   = $"{outcome.Label} (new option, switched on" +
                         (outcome.Placement?.TurnedMulti == true ? "; the group becomes multi-select)" : ")"),
            Data       = JsonSerializer.Serialize(new ModConverterService.ConvertedOptionData(request.Name, request.Description, newFiles)),
        });
        return outcome;
    }

    /// <summary>
    /// Adding to the mod leaves the source's files as they are. A file the conversion changes is
    /// copied, under the name converting in place would give it, and the copy is changed; a file
    /// it leaves as it is (a texture, say) is not copied, and the target's paths load the same
    /// file. Models are always copied, so leaving out mesh groups never edits the source's.
    /// Only files the target's paths load are copied: another file the conversion would patch in
    /// place (a model of another hair loading the same material, say) stays the source's.
    /// </summary>
    private static void KeepSourceFiles(ConversionTask task, string root, PenumbraMod mod, KeyRules keys,
        CustomizationPathEndpoint target)
    {
        var loaded = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (var container in mod.Containers)
        foreach (var (key, local) in container.FileEntries())
        {
            if (keys.Target(key) == null) continue;
            try { loaded.Add(Path.GetFullPath(PathSafety.ResolveRelative(root, GamePath.ToLocal(local)))); }
            catch (InvalidDataException) { /* a path outside the mod folder is no file of the mod's */ }
        }
        task.PlannedBinaryPatches.RemoveAll(p => !loaded.Contains(Path.GetFullPath(p.FilePath)));
        task.PlannedMdlChanges.RemoveAll(m => !loaded.Contains(Path.GetFullPath(m.FilePath)));

        var changed = task.PlannedBinaryPatches.Select(p => p.FilePath)
            .Concat(task.PlannedMdlChanges.Select(m => m.FilePath))
            .Concat(task.AllAssetFiles.Where(f => f.EndsWith(".mdl", StringComparison.OrdinalIgnoreCase)))
            .Select(Path.GetFullPath)
            .Where(loaded.Contains)
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .Order(StringComparer.OrdinalIgnoreCase)
            .ToList();
        var renamed = task.PlannedRenames.ToDictionary(r => Path.GetFullPath(r.OldPath), r => Path.GetFullPath(r.NewPath),
            StringComparer.OrdinalIgnoreCase);
        task.PlannedRenames.Clear();

        var copies = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        var taken = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        var suffix = $"_c{target.GenderRace:D4}{CustomizationKinds.Get(target.Kind).Token(target.ModelId)}";
        foreach (var file in changed)
        {
            // A file that keeps its name in place (a shared material, or one whose name holds no
            // root) needs a name of its own beside the original.
            var copy = renamed.GetValueOrDefault(file, file);
            for (var number = 1; string.Equals(copy, file, StringComparison.OrdinalIgnoreCase) || File.Exists(copy) || taken.Contains(copy); number++)
                copy = Path.Combine(Path.GetDirectoryName(file)!,
                    Path.GetFileNameWithoutExtension(file) + suffix + (number > 1 ? $"_{number}" : string.Empty) + Path.GetExtension(file));
            taken.Add(copy);
            copies[file] = copy;
            task.PlannedRenames.Add(new PlannedRename { OldPath = file, NewPath = copy, KeepsOriginal = true });
        }

        foreach (var patch in task.PlannedBinaryPatches) patch.FilePath = copies[Path.GetFullPath(patch.FilePath)];
        foreach (var change in task.PlannedMdlChanges) change.FilePath = copies[Path.GetFullPath(change.FilePath)];
    }

    /// <summary>
    /// The models the converted customization ships, for the Mesh groups tab: every model of the
    /// source that the mod loads at a path the conversion moves, under the name it will have once
    /// converted. Converting never adds, removes or reorders meshes, so the file as it is now
    /// lists the same groups; only their material names change, as the planned patches say.
    /// </summary>
    private static List<GearOutputModel> CollectOutputModels(ConversionTask task, string root, PenumbraMod mod, KeyRules keys)
    {
        var models = task.AllAssetFiles.Where(f => f.EndsWith(".mdl", StringComparison.OrdinalIgnoreCase))
            .ToHashSet(StringComparer.OrdinalIgnoreCase);
        var targets = new Dictionary<string, (List<string> Keys, List<string> SourceKeys, List<string> Options)>(
            StringComparer.OrdinalIgnoreCase);
        var otherUses = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (var container in mod.Containers)
        foreach (var (key, local) in container.FileEntries())
        {
            string full;
            try { full = PathSafety.ResolveRelative(root, GamePath.ToLocal(local)); }
            catch (InvalidDataException) { continue; }
            if (!models.Contains(full)) continue;
            // A path the conversion leaves where it is keeps loading the same file, for another item.
            if (keys.Target(key) is not { } moved)
            {
                otherUses.Add(full);
                continue;
            }
            if (!targets.TryGetValue(full, out var entry)) targets[full] = entry = ([], [], []);
            entry.Keys.Add(GamePath.Normalize(moved));
            entry.SourceKeys.Add(GamePath.Normalize(key));
            if (!entry.Options.Contains(container.Label)) entry.Options.Add(container.Label);
        }

        var renames = task.PlannedRenames.ToDictionary(r => Path.GetFullPath(r.OldPath), r => Path.GetFullPath(r.NewPath),
            StringComparer.OrdinalIgnoreCase);
        var result = new List<GearOutputModel>();
        foreach (var (full, (gamePaths, sourcePaths, options)) in targets.OrderBy(t => t.Key, StringComparer.OrdinalIgnoreCase))
        {
            var replacements = MaterialReplacements(task, full, renames.GetValueOrDefault(full, full));
            IReadOnlyList<MdlMeshGroup> groups = [];
            string? error = null;
            try
            {
                groups = MdlMeshGroups.Describe(File.ReadAllBytes(full))
                    .Select(g => replacements.TryGetValue(g.Material, out var material)
                        ? g with { Material = material, IsSkin = ResourceReferences.IsSkinMaterial(material) }
                        : g)
                    .ToList();
                if (otherUses.Contains(full))
                    error = "This file is also used for other items in the mod, so it cannot be edited here.";
            }
            catch (UnsupportedMdlVersionException ex) when (ex.Version == MdlFile.Version5)
            {
                error = "MDL version 5 models cannot be edited. Re-export the model with a current tool.";
            }
            catch (Exception ex)
            {
                error = $"The model cannot be read: {ex.Message}";
            }
            var local = Path.GetRelativePath(root, renames.GetValueOrDefault(full, full));
            result.Add(new GearOutputModel(local, [.. gamePaths.Distinct(StringComparer.Ordinal)], [.. options], null, groups,
                error, full, [.. sourcePaths.Distinct(StringComparer.Ordinal)]));
        }
        return result;
    }

    /// <summary>
    /// The planned rewrites of the paths inside the model at <paramref name="file"/>. They are
    /// planned on the file itself when it is converted in place, and on <paramref name="copy"/>
    /// when the source keeps it.
    /// </summary>
    private static Dictionary<string, string> MaterialReplacements(ConversionTask task, string file, string copy)
    {
        bool Planned(string path) => string.Equals(path, file, StringComparison.OrdinalIgnoreCase) ||
                                     string.Equals(path, copy, StringComparison.OrdinalIgnoreCase);
        var patches = task.PlannedMdlChanges.FirstOrDefault(c => Planned(c.FilePath))?.PathReplacements
                      ?? task.PlannedBinaryPatches.FirstOrDefault(p => Planned(p.FilePath))?.Patches
                      ?? [];
        return patches.ToDictionary(p => p.OldString, p => p.NewString, StringComparer.Ordinal);
    }

    private Dictionary<string, Dictionary<string, string>> PlanMaterialDependencies(ConversionTask task,
        CustomizationPathEndpoint source, CustomizationPathEndpoint target, ModResourceIndex resources, KeyRules keys)
    {
        var result = new Dictionary<string, Dictionary<string, string>>(StringComparer.OrdinalIgnoreCase);
        foreach (var file in task.AllAssetFiles.Where(f => f.EndsWith(".mdl", StringComparison.OrdinalIgnoreCase)))
        {
            var additions = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
            result[file] = additions;
            // Material names are readable from MDL v5 and v6 alike.
            IReadOnlyList<string> materials;
            try { materials = ResourceReferences.ReadMdlMaterials(File.ReadAllBytes(file)); }
            catch (Exception ex) when (ex is InvalidDataException or IOException or ArgumentException or IndexOutOfRangeException)
            {
                continue; // The model's own planning step names the file and the problem.
            }
            foreach (var material in materials)
            {
                var renamed = CustomizationPaths.RewriteOwnedReference(material, source, target);
                if (renamed == material) continue;
                // Each clan of the target (Au Ra tails: Raen and Xaela) loads the material from
                // its own root, and gets what the source's same clan loads. Clans loading the
                // same source material share one copy.
                var copies = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
                foreach (var (sourceClan, targetClan) in CustomizationPaths.MaterialClans(source, target))
                {
                    // Prefer the folder the source actually loads (Hrothgar tails: one of five).
                    var sourcePaths = CustomizationPaths.MaterialPaths(sourceClan, material);
                    var ordered = keys.OrderSourceMaterialPaths(sourcePaths);
                    var sourcePath = ordered.FirstOrDefault(p => resources.Files.ContainsKey(p) || resources.FileSwaps.ContainsKey(p))
                                     ?? ordered[0];
                    // Modded materials are already discovered and rewritten with their textures.
                    if (resources.Files.ContainsKey(sourcePath)) continue;
                    var targetPaths = CustomizationPaths.MaterialPaths(targetClan, renamed);
                    if (!copies.TryGetValue(sourcePath, out var targetPath))
                    {
                        var resolved = sourcePath;
                        var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
                        while (resources.FileSwaps.TryGetValue(resolved, out var swapped))
                        {
                            if (!seen.Add(resolved)) throw new InvalidDataException($"Cyclic material swap: {sourcePath}");
                            resolved = Normalize(swapped);
                        }
                        var bytes = resources.Files.TryGetValue(resolved, out var local)
                            ? File.ReadAllBytes(local) : gameData?.GetRawFileBytes(resolved);
                        if (bytes == null)
                        {
                            task.Diagnostics.Add(new PlanDiagnostic("missing_material_dependency",
                                $"Material '{sourcePath}' required by '{Path.GetFileName(file)}' could not be read." +
                                GearConversionExecutor.MergeHint, true));
                            continue;
                        }
                        // Preserve vanilla texture references, shader constants and alpha behavior.
                        // Moving only the MDL reference would silently substitute a target game's
                        // material with the same name (or leave it missing altogether).
                        var replacements = BinaryPathRewriter.ExtractPaths(bytes)
                            .Where(p => resources.Files.ContainsKey(Normalize(p)) || resources.FileSwaps.ContainsKey(Normalize(p)))
                            .ToDictionary(p => p, p => CustomizationPaths.RewriteOwnedReference(p, source, target), StringComparer.Ordinal);
                        bytes = MtrlFile.RewritePaths(bytes, replacements);
                        // One copy on disk; every variant folder the target may use points at it.
                        targetPath = targetPaths[0];
                        var destination = PathSafety.ResolveRelative(task.ModDirectory, targetPath);
                        var prior = task.PlannedGeneratedFiles.FirstOrDefault(f => f.FilePath.Equals(destination, StringComparison.OrdinalIgnoreCase));
                        if (prior != null && !prior.Data.AsSpan().SequenceEqual(bytes))
                            throw new InvalidDataException($"Conflicting material dependencies for '{targetPath}'.");
                        if (prior == null)
                            task.PlannedGeneratedFiles.Add(new PlannedGeneratedFile(destination, targetPath, bytes.ToImmutableArray()));
                        copies[sourcePath] = targetPath;
                    }
                    foreach (var path in targetPaths) additions[path] = targetPath;
                }
            }
        }
        return result;
    }

    /// <param name="sources">The roots the conversion moves (see <see cref="CustomizationPaths.MovedRoots"/>).</param>
    private static HashSet<string> DiscoverAssets(string root,
        IReadOnlyDictionary<string, string> mappings, IReadOnlyList<(string GamePath, string Local)> optionFiles,
        IReadOnlyList<CustomizationPathEndpoint> sources, References references, ICollection<string> missing)
    {
        var assets = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        var queue = new Queue<string>();

        foreach (var (gamePath, localPath) in mappings)
            if (Moves(gamePath)) Add(localPath);
        // Another option may redirect the same game path to a file of its own.
        foreach (var (gamePath, localPath) in optionFiles)
            if (Moves(gamePath) && File.Exists(localPath)) Add(localPath);

        foreach (var file in Directory.EnumerateFiles(root, "*", SearchOption.AllDirectories))
        {
            if (Moves(file)) Add(file);
            if (!StructuredExtensions.Contains(Path.GetExtension(file))) continue;
            if (references.Of(file).Any(Moves)) Add(file);
        }

        while (queue.Count > 0)
        {
            var file = queue.Dequeue();
            if (!StructuredExtensions.Contains(Path.GetExtension(file))) continue;
            foreach (var dependency in references.Of(file))
                foreach (var local in ResolveMappedDependencies(mappings, dependency)) Add(local);
        }
        return assets;

        void Add(string path)
        {
            var full = Path.GetFullPath(path);
            PathSafety.EnsureContained(root, full);
            // A game path the mod redirects to a file it does not have is said, not a reason to refuse the plan.
            if (!File.Exists(full)) { missing.Add(full); return; }
            if (assets.Add(full)) queue.Enqueue(full);
        }

        bool Moves(string path) => sources.Any(source => CustomizationPaths.Contains(path, source));
    }

    /// <summary>
    /// The resource paths inside each structured file, read from disk once per plan: discovery
    /// scans the whole folder, and the assets it finds are asked again for their dependencies and
    /// again for the paths to rewrite.
    /// </summary>
    private sealed class References
    {
        private readonly Dictionary<string, IReadOnlyList<string>> _paths = new(StringComparer.OrdinalIgnoreCase);

        public IReadOnlyList<string> Of(string file)
        {
            if (!_paths.TryGetValue(file, out var paths))
                _paths[file] = paths = BinaryPathRewriter.ExtractPaths(File.ReadAllBytes(file));
            return paths;
        }
    }

    private static IEnumerable<string> ResolveMappedDependencies(
        IReadOnlyDictionary<string, string> mappings, string dependency)
    {
        var normalized = Normalize(dependency);
        if (mappings.TryGetValue(normalized, out var exact)) yield return exact;

        // MDL material references are usually short names (/mt_....mtrl), while
        // Penumbra Files keys contain the complete game path. TexTools resolves
        // those names against the model root; matching the path suffix gives us
        // the same dependency edge without assuming that local filenames mirror it.
        var suffix = "/" + normalized.TrimStart('/');
        foreach (var (gamePath, local) in mappings)
            if (!gamePath.Equals(normalized, StringComparison.OrdinalIgnoreCase) &&
                gamePath.EndsWith(suffix, StringComparison.OrdinalIgnoreCase))
                yield return local;
    }

    private static ModResourceIndex ReadResources(string root)
    {
        var files = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        var swaps = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        var estOverrides = new Dictionary<EstOverrideKey, ushort>();
        // Every container lives in meta.json; other JSON files inside option folders are not data.
        foreach (var jsonFile in PenumbraMod.DefinitionFiles(root).Where(File.Exists))
        {
            JsonNode? node;
            try { node = Parse(jsonFile); } catch { continue; }
            Walk(node);
        }
        return new ModResourceIndex(files, swaps, estOverrides);

        void Walk(JsonNode? node)
        {
            if (node is JsonObject obj)
            {
                if (obj["Files"] is JsonObject filesObject)
                    foreach (var (gamePath, value) in filesObject)
                        if (value is JsonValue v && v.TryGetValue<string>(out var local) && !Path.IsPathRooted(local))
                            files[Normalize(gamePath)] = PathSafety.ResolveRelative(root,
                                local.Replace('/', Path.DirectorySeparatorChar));
                if (obj["FileSwaps"] is JsonObject fileSwaps)
                    foreach (var (gamePath, value) in fileSwaps)
                        if (value is JsonValue v && v.TryGetValue<string>(out var target))
                            swaps[Normalize(gamePath)] = Normalize(target);
                if (string.Equals(Json.GetString(obj["Type"]), "Est", StringComparison.OrdinalIgnoreCase) &&
                    obj["Manipulation"] is JsonObject est)
                    AddEstOverride(est);
                foreach (var (_, child) in obj) Walk(child);
            }
            else if (node is JsonArray array)
                foreach (var child in array) Walk(child);
        }

        void AddEstOverride(JsonObject est)
        {
            if (!Json.TryGetInt(est["SetId"], out var setId) || setId is < 0 or > ushort.MaxValue ||
                !Json.TryGetInt(est["Entry"], out var skeletonId) || skeletonId is < 0 or > ushort.MaxValue ||
                !TryEstKind(Json.GetString(est["Slot"]), out var kind) ||
                !GenderRaces.TryParse(Json.GetString(est["Race"]), Json.GetString(est["Gender"]), out var race))
                return;
            estOverrides[new EstOverrideKey(kind, race, (ushort)setId)] = (ushort)skeletonId;
        }
    }

    /// <param name="keepSource">
    /// Add the target's paths beside the source's instead of moving them, and add the target's
    /// own metadata beside the source's instead of editing it.
    /// </param>
    private static void PlanJson(ConversionTask task, string root,
        CustomizationKindDescriptor descriptor, CustomizationPathEndpoint source,
        CustomizationPathEndpoint target, ModResourceIndex resources,
        IReadOnlyDictionary<string, Dictionary<string, string>> materialDependencies, KeyRules keys, bool keepSource)
    {
        var renameMap = task.PlannedRenames.ToDictionary(
            rename => Normalize(Path.GetRelativePath(root, rename.OldPath)),
            rename => Normalize(Path.GetRelativePath(root, rename.NewPath)),
            StringComparer.OrdinalIgnoreCase);
        var sourceNames = GenderRaces.Names(source.GenderRace);
        var targetNames = GenderRaces.Names(target.GenderRace);
        var occupied = new SortedSet<string>(StringComparer.OrdinalIgnoreCase);
        var dropped = new SortedSet<string>(StringComparer.OrdinalIgnoreCase);

        foreach (var jsonFile in PenumbraMod.DefinitionFiles(root).Where(File.Exists))
        {
            var node = Parse(jsonFile);
            if (node == null) continue;
            var changes = new List<JsonFieldChange>();
            Walk(node, "<root>", changes);
            if (changes.Count == 0) continue;
            var file = new PlannedJsonChange { FilePath = jsonFile };
            file.Changes.AddRange(changes);
            task.PlannedJsonChanges.Add(file);
        }

        // Renaming onto a path the mod already redirects replaces the mod's own file (another
        // hair of a pack at the target ID, say), which the gear planner refuses as well.
        if (occupied.Count > 0)
            task.Diagnostics.Add(new PlanDiagnostic("target_conflict",
                $"The mod already redirects {occupied.Count} of the target's path(s) itself, such as {occupied.First()}; " +
                "converting would replace them. Choose another target, or remove those from the mod first.", true));

        if (dropped.Count > 0)
            task.Diagnostics.Add(new PlanDiagnostic("xaela_material_dropped",
                $"The target has no Xaela material, so the source's {dropped.Count} Xaela path(s), such as {dropped.First()}, " +
                "are removed: nothing loads them once the tail's model has moved.", false));

        void Walk(JsonNode? node, string path, List<JsonFieldChange> changes)
        {
            if (node is JsonObject obj)
            {
                if (obj["Files"] is JsonObject modelFiles)
                {
                    var additions = new JsonObject();
                    foreach (var (_, value) in modelFiles)
                    {
                        if (value is not JsonValue v || !v.TryGetValue<string>(out var local)) continue;
                        var full = PathSafety.ResolveRelative(root, local.Replace('/', Path.DirectorySeparatorChar));
                        if (!materialDependencies.TryGetValue(full, out var dependencies)) continue;
                        foreach (var (gamePath, destination) in dependencies) additions[gamePath] = destination;
                    }
                    if (additions.Count > 0)
                    {
                        foreach (var (gamePath, _) in additions) task.CustomizationOutputKeys.Add(Normalize(gamePath));
                        changes.Add(new JsonFieldChange { JsonPath = path, ChangeType = "dependency_files",
                            NewValue = additions.ToJsonString() });
                    }
                }
                if (descriptor.SupportsEst &&
                    string.Equals(Json.GetString(obj["Type"]), "Est", StringComparison.OrdinalIgnoreCase) &&
                    obj["Manipulation"] is JsonObject est)
                    Retarget(obj, path, EstEdits(est), changes);
                if (Json.GetString(obj["Type"]) is { } shapeType &&
                    (shapeType.Equals("Shp", StringComparison.OrdinalIgnoreCase) || shapeType.Equals("Atr", StringComparison.OrdinalIgnoreCase)) &&
                    obj["Manipulation"] is JsonObject shape)
                    Retarget(obj, path, ShapeEdits(shape), changes);

                foreach (var (key, value) in obj.ToList())
                {
                    if (key is "Files" or "FileSwaps" && value is JsonObject dict)
                    {
                        var produced = new HashSet<string>(dict.Select(p => Normalize(p.Key)), StringComparer.OrdinalIgnoreCase);
                        // Keys this conversion leaves where they are, which a moved key must not land on.
                        // Adding beside the source leaves every key where it is.
                        var staying = dict.Where(p => keepSource || keys.Target(p.Key) == null && !keys.IsDropped(p.Key))
                            .Select(p => Normalize(p.Key))
                            .ToHashSet(StringComparer.OrdinalIgnoreCase);
                        var added = new JsonObject();
                        var variantAdditions = new JsonObject();
                        foreach (var (dictKey, dictValue) in dict.ToList())
                        {
                            if (keys.IsDropped(dictKey))
                            {
                                // Kept beside the source, they still load with the source's tail.
                                if (keepSource) continue;
                                dropped.Add(Normalize(dictKey));
                                changes.Add(new JsonFieldChange { JsonPath = $"{path}.{key}[key]", OldValue = dictKey, ChangeType = "path_key_remove" });
                                continue;
                            }

                            var newKey = keys.Target(dictKey);
                            if (newKey != null)
                            {
                                if (staying.Contains(Normalize(newKey))) occupied.Add(Normalize(newKey));
                                produced.Add(Normalize(newKey));
                                task.CustomizationOutputKeys.Add(Normalize(newKey));
                                if (!keepSource)
                                    changes.Add(new JsonFieldChange { JsonPath = $"{path}.{key}[key]", OldValue = dictKey, NewValue = newKey,
                                        ChangeType = keys.IsShared(dictKey) ? "path_key_copy" : "path_key" });
                            }

                            if (dictValue is not JsonValue dv || !dv.TryGetValue<string>(out var valueText)) continue;
                            var normalized = Normalize(valueText);
                            string replacement;
                            if (key == "Files")
                                replacement = renameMap.TryGetValue(normalized, out var mapped) ? mapped : valueText;
                            else
                                // Swap values point at existing resources, not hypothetical
                                // target resources. Retarget only when that resource is moved.
                                replacement = resources.Files.ContainsKey(normalized) || resources.FileSwaps.ContainsKey(normalized)
                                    ? CustomizationPaths.Rewrite(valueText, source, target) : valueText;
                            if (keepSource)
                            {
                                if (newKey != null) added[newKey] = replacement;
                            }
                            else if (replacement != valueText)
                                changes.Add(new JsonFieldChange { JsonPath = $"{path}.{key}[{dictKey}]", OldValue = valueText, NewValue = replacement, ChangeType = "path_value" });
                            if (key == "Files" && newKey != null)
                                foreach (var extra in keys.ExtraTargetVariants(newKey))
                                    variantAdditions[extra] = replacement;
                        }
                        foreach (var (extra, extraValue) in variantAdditions.ToList())
                            if (produced.Contains(Normalize(extra))) variantAdditions.Remove(extra);
                            else
                            {
                                task.CustomizationOutputKeys.Add(Normalize(extra));
                                variantAdditions.Remove(extra);
                                added[extra] = extraValue;
                            }
                        if (added.Count > 0)
                            changes.Add(new JsonFieldChange { JsonPath = path,
                                ChangeType = key == "Files" ? "dependency_files" : "dependency_swaps", NewValue = added.ToJsonString() });
                        continue;
                    }
                    Walk(value, path + "." + key, changes);
                }
            }
            else if (node is JsonArray array)
                for (var i = 0; i < array.Count; i++) Walk(array[i], $"{path}[{i}]", changes);
            // Beside the source, every other mention of it stays the source's.
            else if (!keepSource && node is JsonValue value && value.TryGetValue<string>(out var text))
            {
                var replacement = CustomizationPaths.Rewrite(text, source, target);
                if (replacement != text)
                    changes.Add(new JsonFieldChange { JsonPath = path, OldValue = text, NewValue = replacement, ChangeType = "path_string" });
            }
        }

        // A manipulation of the source's becomes the target's: edited where it is when converting in
        // place, or copied and the copy added to the same option when the source keeps its own.
        void Retarget(JsonObject manipulation, string path, List<JsonFieldChange> edits, List<JsonFieldChange> changes)
        {
            if (edits.Count == 0) return;
            if (!keepSource)
            {
                foreach (var edit in edits)
                {
                    edit.JsonPath = $"{path}.Manipulation.{edit.JsonPath}";
                    changes.Add(edit);
                }
                return;
            }

            var copy = (JsonObject)manipulation.DeepClone();
            var fields = (JsonObject)copy["Manipulation"]!;
            foreach (var edit in edits)
                fields[edit.JsonPath] = edit.ChangeType == "numeric_id"
                    ? JsonValue.Create(int.Parse(edit.NewValue))
                    : JsonValue.Create(edit.NewValue);
            changes.Add(new JsonFieldChange
            {
                JsonPath   = ManipulationsEntry().Replace(path, string.Empty),
                ChangeType = "manipulation_insert",
                OldValue   = GearManipulations.Describe(manipulation),
                NewValue   = copy.ToJsonString(),
            });
        }

        // Shape (Shp) and attribute (Atr) switches name a hair or face by slot and ID, and the race it is
        // for by GenderRaceCondition. One for this very ID is the converted item's own and moves with it;
        // one without an ID applies to every hair or face of its race, which this conversion is not about.
        // Each edit's JsonPath is the field it changes.
        List<JsonFieldChange> ShapeEdits(JsonObject shape)
        {
            var edits = new List<JsonFieldChange>();
            var slot = source.Kind switch { AssetKind.Hair => "Hair", AssetKind.Face => "Face", _ => null };
            if (slot == null || !string.Equals(Json.GetString(shape["Slot"]), slot, StringComparison.OrdinalIgnoreCase)) return edits;
            if (!Json.TryGetInt(shape["Id"], out var id) || id != source.ModelId) return edits;
            var hasRace = Json.TryGetInt(shape["GenderRaceCondition"], out var race) && race != 0;
            if (hasRace && race != source.GenderRace) return edits;

            if (source.ModelId != target.ModelId)
                edits.Add(new JsonFieldChange { JsonPath = "Id", OldValue = source.ModelId.ToString(), NewValue = target.ModelId.ToString(),
                    ChangeType = shape["Id"]?.GetValueKind() == JsonValueKind.String ? "numeric_id_string" : "numeric_id" });
            if (hasRace && source.GenderRace != target.GenderRace)
                edits.Add(new JsonFieldChange { JsonPath = "GenderRaceCondition", OldValue = source.GenderRace.ToString(), NewValue = target.GenderRace.ToString(),
                    ChangeType = shape["GenderRaceCondition"]?.GetValueKind() == JsonValueKind.String ? "numeric_id_string" : "numeric_id" });
            return edits;
        }

        List<JsonFieldChange> EstEdits(JsonObject est)
        {
            var edits = new List<JsonFieldChange>();
            if (!Json.TryGetInt(est["SetId"], out var setId) || setId != source.ModelId) return edits;
            // Hair and face share the same identifier space; the slot tells them apart.
            if (!string.Equals(Json.GetString(est["Slot"]), source.Kind.ToString(), StringComparison.OrdinalIgnoreCase)) return edits;
            if (Json.GetString(est["Race"]) is { } race && !race.Equals(sourceNames.Race, StringComparison.OrdinalIgnoreCase)) return edits;
            if (Json.GetString(est["Gender"]) is { } gender && !gender.Equals(sourceNames.Gender, StringComparison.OrdinalIgnoreCase)) return edits;
            edits.Add(new JsonFieldChange { JsonPath = "SetId", OldValue = source.ModelId.ToString("D4"), NewValue = target.ModelId.ToString("D4"),
                ChangeType = est["SetId"]?.GetValueKind() == JsonValueKind.String ? "numeric_id_string" : "numeric_id" });
            // Only real edits: an unchanged value would count as a failed change at apply time.
            if (est["Race"] is not null && sourceNames.Race != targetNames.Race)
                edits.Add(new JsonFieldChange { JsonPath = "Race", OldValue = sourceNames.Race, NewValue = targetNames.Race, ChangeType = "path_string" });
            if (est["Gender"] is not null && sourceNames.Gender != targetNames.Gender)
                edits.Add(new JsonFieldChange { JsonPath = "Gender", OldValue = sourceNames.Gender, NewValue = targetNames.Gender, ChangeType = "path_string" });
            return edits;
        }
    }

    /// <summary>The end of a manipulation's JSON path that names it within its option.</summary>
    [System.Text.RegularExpressions.GeneratedRegex(@"\.Manipulations\[\d+\]$")]
    private static partial System.Text.RegularExpressions.Regex ManipulationsEntry();

    /// <summary>
    /// Local files that must keep their name because a key that is copied or skipped
    /// (shared material root, unused Hrothgar variant) still points at them.
    /// </summary>
    private static HashSet<string> KeepInPlace(string root, PenumbraMod mod, KeyRules keys)
    {
        var result = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (var container in mod.Containers)
        foreach (var (key, local) in container.FileEntries())
        {
            if (!keys.IsShared(key) && !keys.IsSkipped(key)) continue;
            try { result.Add(PathSafety.ResolveRelative(root, GamePath.ToLocal(local))); }
            catch (InvalidDataException) { /* reported by path validation */ }
        }
        return result;
    }

    /// <summary>
    /// Hair and face physics bones come from an extra skeleton selected by the EST table
    /// for the exact race and ID. The converted model was built for the source's skeleton,
    /// so the target must load that skeleton rather than its own vanilla one. Unless the
    /// mod's default option already sets the source entry (retargeted with the rest of the
    /// metadata), the source's effective default is written explicitly for the target.
    /// Options that override it keep doing so, because Penumbra applies option
    /// manipulations over the default ones.
    /// </summary>
    private void PlanExtraSkeleton(ConversionTask task, string root, PenumbraMod mod, CustomizationKindDescriptor descriptor,
        CustomizationPathEndpoint source, CustomizationPathEndpoint target, ModResourceIndex resources)
    {
        if (!descriptor.SupportsEst || source.Kind != target.Kind) return;
        var estPath = source.Kind == AssetKind.Hair
            ? "chara/xls/charadb/hairskeletontemplate.est"
            : "chara/xls/charadb/faceskeletontemplate.est";

        var defaultSource = FindEst(mod.Default, source);
        if (defaultSource == null && FindEst(mod.Default, target) != null)
            return; // The mod's default option already defines the target explicitly.

        // The source's default: the mod's default option, else the table the mod supplies,
        // else the game's table.
        ushort defaultSkeleton = 0, targetVanilla = 0;
        if (defaultSource is { } explicitDefault)
            defaultSkeleton = explicitDefault;
        else
        {
            byte[]? estBytes;
            try
            {
                estBytes = resources.Files.TryGetValue(estPath, out var local) ? File.ReadAllBytes(local) : gameData?.GetRawFileBytes(estPath);
                if (estBytes == null)
                {
                    task.Diagnostics.Add(new PlanDiagnostic("est_unavailable",
                        $"{estPath} is unavailable, so the extra skeleton of {source.Kind} c{source.GenderRace:D4} #{source.ModelId} " +
                        "could not be copied to the target; physics may not transfer.", false));
                    return;
                }
                ExtraSkeletonTable.TryGet(estBytes, source.GenderRace, source.ModelId, out defaultSkeleton);
                ExtraSkeletonTable.TryGet(estBytes, target.GenderRace, target.ModelId, out targetVanilla);
            }
            catch (Exception ex) when (ex is InvalidDataException or IOException)
            {
                task.Diagnostics.Add(new PlanDiagnostic("est_invalid", $"{estPath} could not be read: {ex.Message}", false));
                return;
            }
        }

        // The skeleton an option sets for the source (the last one the mod defines, should several options
        // set different ones) and the default must exist for the target race.
        var used = resources.EstOverrides
            .Where(o => o.Key == new EstOverrideKey(source.Kind, source.GenderRace, source.ModelId))
            .Select(o => o.Value)
            .Append(defaultSkeleton)
            .Where(id => id != 0)
            .Distinct();
        var prefix = source.Kind == AssetKind.Hair ? 'h' : 'f';
        var directory = source.Kind == AssetKind.Hair ? "hair" : "face";
        var defaultAvailable = true;
        foreach (var skeleton in used)
        {
            var skeletonPath = $"chara/human/c{target.GenderRace:D4}/skeleton/{directory}/{prefix}{skeleton:D4}/" +
                               $"skl_c{target.GenderRace:D4}{prefix}{skeleton:D4}.sklb";
            if (resources.Files.ContainsKey(skeletonPath) || resources.FileSwaps.ContainsKey(skeletonPath) ||
                gameData is not IGameFileProvider files || files.FileExists(skeletonPath))
                continue;
            if (skeleton == defaultSkeleton) defaultAvailable = false;
            task.Diagnostics.Add(new PlanDiagnostic("extra_skeleton_missing",
                $"The source uses extra skeleton {prefix}{skeleton:D4}, which does not exist for {RaceNames.Describe(target.GenderRace)} " +
                $"({skeletonPath}); physics bones of the converted {descriptor.DisplayName.ToLowerInvariant()} may not move.", false));
        }

        // An explicit default entry is retargeted with the rest of the metadata, and a target whose own
        // skeleton is the one the source loads needs no entry to say so.
        if (defaultSource != null || !defaultAvailable || defaultSkeleton == targetVanilla) return;

        var file = Path.Combine(root, PenumbraMod.MetaFileName);
        const string jsonPath = "<root>.DefaultData";

        var (race, gender) = GenderRaces.Names(target.GenderRace);
        var manipulation = new JsonObject
        {
            ["Type"] = "Est",
            ["Manipulation"] = new JsonObject
            {
                ["Entry"] = defaultSkeleton,
                ["Gender"] = gender,
                ["Race"] = race,
                ["SetId"] = target.ModelId,
                ["Slot"] = source.Kind.ToString(),
            },
        };
        var planned = task.PlannedJsonChanges.FirstOrDefault(j => string.Equals(j.FilePath, file, StringComparison.OrdinalIgnoreCase));
        if (planned == null) task.PlannedJsonChanges.Add(planned = new PlannedJsonChange { FilePath = file });
        planned.Changes.Add(new JsonFieldChange
        {
            JsonPath = jsonPath,
            ChangeType = "manipulation_insert",
            OldValue = $"Est {source.Kind} c{target.GenderRace:D4} #{target.ModelId} " +
                       $"(target vanilla {prefix}{targetVanilla:D4}, source default {prefix}{defaultSkeleton:D4})",
            NewValue = manipulation.ToJsonString(),
        });
    }

    /// <summary>The extra skeleton a container sets for <paramref name="endpoint"/>, if any.</summary>
    private static ushort? FindEst(ModContainer container, CustomizationPathEndpoint endpoint)
    {
        var (race, gender) = GenderRaces.Names(endpoint.GenderRace);
        foreach (var node in container.Manipulations ?? [])
        {
            if (node is not JsonObject obj ||
                !string.Equals(Json.GetString(obj["Type"]), "Est", StringComparison.OrdinalIgnoreCase) ||
                obj["Manipulation"] is not JsonObject est) continue;
            if (!Json.TryGetInt(est["SetId"], out var setId) || setId != endpoint.ModelId ||
                !Json.TryGetInt(est["Entry"], out var entry) || entry is < 0 or > ushort.MaxValue ||
                !string.Equals(Json.GetString(est["Slot"]), endpoint.Kind.ToString(), StringComparison.OrdinalIgnoreCase) ||
                !string.Equals(Json.GetString(est["Race"]), race, StringComparison.OrdinalIgnoreCase) ||
                !string.Equals(Json.GetString(est["Gender"]), gender, StringComparison.OrdinalIgnoreCase))
                continue;
            return (ushort)entry;
        }
        return null;
    }

    /// <summary>Decides how each redirected game path of a customization is retargeted.</summary>
    private sealed partial class KeyRules
    {
        [System.Text.RegularExpressions.GeneratedRegex(@"^v(?<v>\d{4})/(?<name>[^/]+)$")]
        private static partial System.Text.RegularExpressions.Regex VariantFolder();

        [System.Text.RegularExpressions.GeneratedRegex(@"(?<pre>[/\\]material[/\\]v)(?<v>\d{4})(?=[/\\])",
            System.Text.RegularExpressions.RegexOptions.IgnoreCase)]
        private static partial System.Text.RegularExpressions.Regex MaterialVariant();

        private readonly CustomizationPathEndpoint _source;
        private readonly CustomizationPathEndpoint _target;
        private readonly HashSet<string> _skipped = new(StringComparer.OrdinalIgnoreCase);

        /// <summary>The source's Xaela material root when the target has none to move it to.</summary>
        private readonly CustomizationPathEndpoint? _droppedXaela;

        /// <summary>The target's Xaela material root when the source has none of its own, so Xaela share the target's material.</summary>
        private readonly CustomizationPathEndpoint? _sharedXaela;

        public KeyRules(CustomizationPathEndpoint source, CustomizationPathEndpoint target, ModResourceIndex resources)
        {
            _source = source;
            _target = target;
            var sourceXaela = CustomizationPaths.XaelaMaterialEndpoint(source);
            var targetXaela = CustomizationPaths.XaelaMaterialEndpoint(target);
            _droppedXaela = targetXaela == null ? sourceXaela : null;
            _sharedXaela = sourceXaela == null ? targetXaela : null;
            // Hrothgar tails keep the same material in up to five variant folders. A target
            // with a single folder receives one of them: the tail's own number when present.
            if (!CustomizationPaths.IsHrothgarTail(source) || CustomizationPaths.IsHrothgarTail(target)) return;
            var root = CustomizationPaths.GetMaterialEndpoint(source);
            var prefix = CustomizationKinds.Get(root.Kind).Root(root.GenderRace, root.ModelId) + "/material/";
            var candidates = resources.Files.Keys.Concat(resources.FileSwaps.Keys)
                .Where(k => k.StartsWith(prefix, StringComparison.OrdinalIgnoreCase))
                .Select(k => (Key: k, Match: VariantFolder().Match(k[prefix.Length..])))
                .Where(x => x.Match.Success)
                .GroupBy(x => x.Match.Groups["name"].Value, StringComparer.OrdinalIgnoreCase);
            foreach (var group in candidates)
            {
                var chosen = group.OrderBy(x => int.Parse(x.Match.Groups["v"].Value) == source.ModelId ? 0 : 1)
                    .ThenBy(x => x.Match.Groups["v"].Value, StringComparer.Ordinal).First();
                foreach (var other in group.Where(x => x.Key != chosen.Key)) _skipped.Add(other.Key);
            }
        }

        /// <summary>The retargeted key, or null when this key stays unchanged.</summary>
        public string? Target(string key)
        {
            if (IsSkipped(key)) return null;
            var rewritten = CustomizationPaths.Rewrite(key, _source, _target);
            return string.Equals(rewritten, key, StringComparison.Ordinal) ? null : rewritten;
        }

        public bool IsSkipped(string key) => _skipped.Contains(Normalize(key));

        /// <summary>
        /// Whether this key is removed: an Au Ra tail's Xaela material paths, when the target has
        /// no Xaela root. They only ever load with the tail's model, which moves away.
        /// </summary>
        public bool IsDropped(string key) => _droppedXaela is { } xaela && CustomizationPaths.Contains(key, xaela);

        /// <summary>
        /// Whether this key is added rather than moved: material keys under a root other
        /// customizations also load.
        /// </summary>
        public bool IsShared(string key)
            => key.EndsWith(".mtrl", StringComparison.OrdinalIgnoreCase) &&
               CustomizationPaths.FindEndpoints(key).Any(e => CustomizationPaths.IsSharedMaterialRoot(e) &&
                   (e == _source || e == CustomizationPaths.GetMaterialEndpoint(_source)));

        public bool IsSharedMaterialFile(string localPath) => IsShared(localPath);

        /// <summary>
        /// A Hrothgar tail target may load any of v0001-v0005; the other folders get the same file.
        /// Xaela load an Au Ra tail's material from a root of their own, which gets the same file
        /// too unless the source's own Xaela material moves there.
        /// </summary>
        public IEnumerable<string> ExtraTargetVariants(string newKey)
        {
            if (!newKey.EndsWith(".mtrl", StringComparison.OrdinalIgnoreCase)) yield break;
            if (_sharedXaela is { } xaela && CustomizationPaths.Contains(newKey, _target))
                yield return CustomizationPaths.Rewrite(newKey, _target, xaela);
            if (!CustomizationPaths.IsHrothgarTail(_target)) yield break;
            var match = MaterialVariant().Match(newKey);
            if (!match.Success) yield break;
            var current = int.Parse(match.Groups["v"].Value);
            foreach (var variant in CustomizationPaths.MaterialVariants(_target).Where(v => v != current))
                yield return MaterialVariant().Replace(newKey, m => m.Groups["pre"].Value + variant.ToString("D4"), 1);
        }

        /// <summary>Source material candidates with the folder the source most likely loads first.</summary>
        public IReadOnlyList<string> OrderSourceMaterialPaths(IReadOnlyList<string> paths)
            => paths.OrderBy(p => MaterialVariant().Match(p) is { Success: true } m &&
                                  int.Parse(m.Groups["v"].Value) == _source.ModelId ? 0 : 1).ToArray();
    }

    private void PlanDeformation(ConversionTask task, ushort sourceRace, ushort targetRace,
        CustomizationPathEndpoint source, ModResourceIndex resources)
    {
        RacialDeformationPlan? route = null;
        var hierarchy = new SkeletonResolution(new Dictionary<ushort, BoneHierarchy>(), BoneHierarchy.Empty, []);
        if (sourceRace != targetRace)
        {
            var bytes = gameData?.GetHumanPbdBytes();
            if (bytes == null)
            {
                task.Diagnostics.Add(new PlanDiagnostic("pbd_missing", "human.pbd could not be read; cross-race conversion is blocked.", true));
                return;
            }
            HumanPbd pbd;
            try
            {
                pbd = new HumanPbd(bytes);
                route = RacialDeformationPlan.Create(pbd, sourceRace, targetRace);
            }
            catch (Exception ex)
            {
                task.Diagnostics.Add(new PlanDiagnostic("pbd_invalid", ex.Message, true));
                return;
            }

            if (skeletons == null)
                hierarchy = new SkeletonResolution(new Dictionary<ushort, BoneHierarchy>(), BoneHierarchy.Empty,
                    ["Skeleton hierarchy service is unavailable; bones absent from human.pbd will remain unchanged."]);
            else
                hierarchy = skeletons.Resolve(pbd, route, source, resources);

            foreach (var warning in hierarchy.Warnings)
                task.Diagnostics.Add(new PlanDiagnostic("skeleton_warning", warning, false));
        }

        foreach (var filePath in task.AllAssetFiles
                     .Where(path => string.Equals(Path.GetExtension(path), ".mdl", StringComparison.OrdinalIgnoreCase))
                     .Distinct(StringComparer.OrdinalIgnoreCase))
        {
            try
            {
                var input = File.ReadAllBytes(filePath);
                if (route == null)
                {
                    // Same race: only material names change. That works for MDL v5 and v6 and
                    // does not need the full geometry parser, so the structured patch stays.
                    var patch = task.PlannedBinaryPatches.FirstOrDefault(p =>
                        string.Equals(p.FilePath, filePath, StringComparison.OrdinalIgnoreCase));
                    if (patch != null)
                        _ = ResourceReferences.RewriteMdlStrings(input,
                            patch.Patches.ToDictionary(x => x.OldString, x => x.NewString, StringComparer.Ordinal));
                    continue;
                }
                var model = MdlFile.Read(input);
                WarnAboutTailBones(task, filePath, model);
                var binary = task.PlannedBinaryPatches.FirstOrDefault(p =>
                    string.Equals(p.FilePath, filePath, StringComparison.OrdinalIgnoreCase));
                var replacements = binary?.Patches
                    .ToDictionary(p => p.OldString, p => p.NewString, StringComparer.Ordinal)
                    ?? new Dictionary<string, string>(StringComparer.Ordinal);
                if (replacements.Count > 0) model.ReplacePaths(replacements);
                var deformationPlan = route.Materialize(model.Bones, hierarchy.StepSkeletons, hierarchy.SourceSkeleton);
                var report = MdlRaceConverter.Convert(model, deformationPlan);
                var output = model.Write();
                _ = MdlFile.Read(output);

                var planned = new PlannedMdlChange
                {
                    FilePath = filePath,
                    SourceGenderRace = sourceRace,
                    TargetGenderRace = targetRace,
                    InputHash = MdlRaceConverter.Hash(input),
                    OutputHash = MdlRaceConverter.Hash(output),
                    LodCount = report.LodCount,
                    MeshCount = report.MeshCount,
                    VertexCount = report.VertexCount,
                    ShapeVertexCount = report.ShapeVertexCount,
                    DeformationPlan = deformationPlan,
                };
                planned.BoneResolutions.AddRange(report.BoneResolutions);
                if (binary != null)
                {
                    foreach (var patch in binary.Patches)
                        planned.PathReplacements.Add(new BinaryStringPatch
                        {
                            OldString = patch.OldString,
                            NewString = patch.NewString,
                        });
                    task.PlannedBinaryPatches.Remove(binary);
                }
                task.PlannedMdlChanges.Add(planned);
            }
            catch (UnsupportedMdlVersionException ex)
            {
                task.Diagnostics.Add(new PlanDiagnostic("unsupported_mdl_version",
                    $"{Path.GetFileName(filePath)}: {ex.Message}", true));
            }
            catch (MdlConversionException ex)
            {
                task.Diagnostics.Add(new PlanDiagnostic(ex.Code,
                    $"{Path.GetFileName(filePath)}: {ex.Message}", true));
            }
            catch (Exception ex)
            {
                task.Diagnostics.Add(new PlanDiagnostic("malformed_mdl",
                    $"{Path.GetFileName(filePath)}: {ex.Message}", true));
            }
        }

        if (task.PlannedMdlChanges.Count > 0)
            log.Information("[UMC] Planned racial deformation for {0} model(s).", task.PlannedMdlChanges.Count);
    }

    /// <summary>
    /// Real tails are skinned to the tail bones (n_sippo*), which Viera do not have. Ear-style
    /// models placed in the tail slot use head/ear bones and convert fine.
    /// </summary>
    private static void WarnAboutTailBones(ConversionTask task, string filePath, MdlFile model)
    {
        if (task.Kind != AssetKind.Tail || task.TargetCustomizationKind != AssetKind.VieraEar) return;
        if (!model.Bones.Any(b => b.StartsWith("n_sippo", StringComparison.OrdinalIgnoreCase))) return;
        task.Diagnostics.Add(new PlanDiagnostic("tail_bones_on_ear",
            $"{Path.GetFileName(filePath)} is skinned to tail bones (n_sippo), which Viera do not have; " +
            "it will not follow the ears.", false));
    }

    private static JsonNode? Parse(string path) => JsonNode.Parse(File.ReadAllText(path), new JsonNodeOptions(),
        new JsonDocumentOptions { AllowTrailingCommas = true, CommentHandling = JsonCommentHandling.Skip });
    private static string Normalize(string path) => path.Replace('\\', '/').TrimStart('/');
    private static ushort ParseId(string value) => ushort.TryParse(value, out var id)
        ? id
        : throw new InvalidDataException($"Invalid ID: {value}");
    private static bool TryEstKind(string? slot, out AssetKind kind)
    {
        if (string.Equals(slot, "Hair", StringComparison.OrdinalIgnoreCase))
        {
            kind = AssetKind.Hair;
            return true;
        }
        if (string.Equals(slot, "Face", StringComparison.OrdinalIgnoreCase))
        {
            kind = AssetKind.Face;
            return true;
        }
        kind = default;
        return false;
    }

}
