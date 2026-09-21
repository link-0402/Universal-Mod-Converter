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
        task.SourceRoot = descriptor.Root(sourceRace, oldId);

        // A texture has no paths inside it, so the very same file can serve every race and ID
        // chosen for it. Adding to this mod (or building a new one) turns every race into its own
        // new multi-select option group, the way an idle animation becomes one option per slot;
        // converting in place instead adds the same paths straight into whatever container the
        // source already lives in, governed by whatever toggle (if any) already governs it.
        // Anything else (a model or material) carries its race and ID inside it, so it can only
        // ever have a single target, in place or not.
        if (CustomizationDetection.IsTextureOnly(PenumbraMod.Load(root), source))
        {
            if (task.OutputMode == ConversionOutputMode.InPlace)
                PlanTextureInPlace(task, descriptor, targetKind, targetDescriptor, source, targetRace, newId, root);
            else
                PlanTextureGroups(task, descriptor, targetKind, targetDescriptor, source, targetRace, newId, root);
            return;
        }

        if (oldId == newId && sourceRace == targetRace && task.Kind == targetKind)
            throw new InvalidDataException("Source and target customization roots are identical.");

        var target = new CustomizationPathEndpoint(targetKind, targetRace, newId);
        var resources = ReadResources(root);
        var keys = new KeyRules(source, target, resources, [], false);
        var assets = DiscoverAssets(root, resources.Files, source);
        if (assets.Count == 0)
            throw new InvalidDataException($"No {descriptor.DisplayName.ToLowerInvariant()} root matched " +
                                           $"c{sourceRace:D4}/{descriptor.Token(oldId)}.");

        task.AllAssetFiles.Clear();
        task.AllAssetFiles.AddRange(assets);
        var keepInPlace = KeepInPlace(root, keys);
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
            var planned = new PlannedBinaryPatch { FilePath = file, IsStructured = true };
            foreach (var path in BinaryPathRewriter.ExtractPaths(File.ReadAllBytes(file)))
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
                    _ = MtrlFile.RewritePaths(File.ReadAllBytes(file), replacements);
                }
                task.PlannedBinaryPatches.Add(planned);
            }
        }

        var materialDependencies = PlanMaterialDependencies(task, source, target, resources, keys);
        PlanJson(task, root, descriptor, source, target, resources, materialDependencies, keys);
        PlanExtraSkeleton(task, root, descriptor, source, target, resources);
        PlanDeformation(task, sourceRace, targetRace, source, resources);
    }

    /// <summary>
    /// Validates every (race, ID) the conversion writes to: the primary target plus every extra,
    /// deduplicated. Throws the same way a single-target conversion would for an invalid one.
    /// </summary>
    private static List<CustomizationPathEndpoint> ResolveTargets(ConversionTask task, AssetKind targetKind,
        CustomizationKindDescriptor targetDescriptor, CustomizationPathEndpoint source,
        ushort primaryRace, ushort primaryId)
    {
        var targets = new List<CustomizationPathEndpoint>();
        void AddTarget(ushort race, ushort id)
        {
            var endpoint = new CustomizationPathEndpoint(targetKind, race, id);
            if (targets.Contains(endpoint)) return;
            if (!targetDescriptor.SupportsRace(race))
                throw new InvalidDataException($"{targetDescriptor.DisplayName} is not valid for {RaceNames.Describe(race)}.");
            if (CustomizationTargets.BlockReason(source.Kind, source.GenderRace, targetKind, race) is { } blocked)
                throw new InvalidDataException(blocked);
            targets.Add(endpoint);
        }
        AddTarget(primaryRace, primaryId);
        foreach (var extra in task.ExtraTargets)
            AddTarget(extra.GenderRace ?? primaryRace, extra.ModelId);
        if (targets.Count == 0)
            throw new InvalidDataException("Choose at least one race to convert to.");
        return targets;
    }

    /// <summary>The source root's own Files entries, wherever in the mod they live.</summary>
    private static List<KeyValuePair<string, string>> ResolveSourceFiles(ConversionTask task,
        ModResourceIndex resources, CustomizationKindDescriptor descriptor, CustomizationPathEndpoint source)
    {
        var sourceFiles = resources.Files.Where(f => CustomizationPaths.Contains(f.Key, source)).ToList();
        if (sourceFiles.Count == 0)
            throw new InvalidDataException($"No {descriptor.DisplayName.ToLowerInvariant()} root matched " +
                                           $"c{source.GenderRace:D4}/{descriptor.Token(source.ModelId)}.");
        foreach (var (gamePath, _) in resources.FileSwaps.Where(f => CustomizationPaths.Contains(f.Key, source)))
            task.Diagnostics.Add(new PlanDiagnostic("file_swap", $"The file swap for {gamePath} is not converted.", false));

        task.AllAssetFiles.Clear();
        task.AllAssetFiles.AddRange(sourceFiles.Select(f => f.Value).Distinct(StringComparer.OrdinalIgnoreCase));
        return sourceFiles;
    }

    /// <summary>
    /// Builds one new multi-select option group per target race, with one option per ID chosen
    /// for that race (most kinds ever offer one; a face can have several). The source's own keys
    /// are pulled out of wherever they currently live — Default, or any option — because the new
    /// groups provide them from here on, the same way an idle animation moved into a slot group
    /// stops being served from Default. The source root itself is just another target: including
    /// it keeps it working, as its own toggleable option beside the others; leaving it out moves
    /// it away entirely.
    /// </summary>
    private void PlanTextureGroups(ConversionTask task, CustomizationKindDescriptor descriptor,
        AssetKind targetKind, CustomizationKindDescriptor targetDescriptor, CustomizationPathEndpoint source,
        ushort primaryRace, ushort primaryId, string root)
    {
        var targets = ResolveTargets(task, targetKind, targetDescriptor, source, primaryRace, primaryId);
        var resources = ReadResources(root);
        var sourceFiles = ResolveSourceFiles(task, resources, descriptor, source);

        RemoveSourceKeys(task, root, source);

        var file = Path.Combine(root, PenumbraMod.MetaFileName);
        var planned = task.PlannedJsonChanges.FirstOrDefault(j => string.Equals(j.FilePath, file, StringComparison.OrdinalIgnoreCase));
        if (planned == null) task.PlannedJsonChanges.Add(planned = new PlannedJsonChange { FilePath = file });

        var mod = PenumbraMod.Load(root);
        var takenNames = mod.Groups.Select(g => g.Name).ToHashSet(StringComparer.OrdinalIgnoreCase);
        var priority = mod.Groups.Select(g => Json.GetInt(g.Node["Priority"], 0)).DefaultIfEmpty(0).Max();

        foreach (var raceGroup in targets.GroupBy(t => t.GenderRace).OrderBy(g => g.Key))
        {
            var race = raceGroup.Key;
            var ids = raceGroup.Select(e => e.ModelId).OrderBy(id => id).ToList();
            if (ids.Count > MaxMultiOptions)
                throw new InvalidDataException(
                    $"{RaceNames.Describe(race)} would need {ids.Count} options, more than the " +
                    $"{MaxMultiOptions} a multi-select group can have. Choose fewer IDs for it.");

            var options = new JsonArray();
            foreach (var id in ids)
            {
                var endpoint = new CustomizationPathEndpoint(targetKind, race, id);
                var files = new JsonObject();
                foreach (var (gamePath, local) in sourceFiles)
                    files[CustomizationPaths.Rewrite(gamePath, source, endpoint)] =
                        Path.GetRelativePath(root, local).Replace('/', '\\');
                options.Add(new JsonObject
                {
                    ["Id"] = Guid.NewGuid().ToString(),
                    ["Name"] = gameData?.DescribeCustomization(targetKind, race, id) ?? GameDataService.OptionLabel(targetKind, id),
                    ["Description"] = string.Empty,
                    ["Files"] = files,
                    ["FileSwaps"] = new JsonObject(),
                    ["Manipulations"] = new JsonArray(),
                });
            }

            var name = UniqueName(RaceNames.Name(race), takenNames);
            var group = new JsonObject
            {
                ["Id"] = Guid.NewGuid().ToString(),
                ["Name"] = name,
                ["Description"] = $"Which {descriptor.DisplayName.ToLowerInvariant()} texture{(ids.Count > 1 ? "s" : "")} " +
                                   $"to use for {RaceNames.Name(race)}. Created by Universal Mod Converter.",
                ["Priority"] = ++priority,
                ["Type"] = "Multi",
                ["DefaultSettings"] = (1UL << ids.Count) - 1, // every option here was ticked, so all start on
                ["Options"] = options,
            };
            planned.Changes.Add(new JsonFieldChange
            {
                JsonPath = "<root>",
                ChangeType = "group_insert",
                OldValue = $"New group '{name}'",
                NewValue = group.ToJsonString(),
            });
        }
    }

    /// <summary>
    /// Adds every extra target's rewritten keys straight into whatever container the source's own
    /// keys already live in — Default, or an existing option — right alongside them, governed by
    /// whatever toggle (if any) already governs that container. No new group is created. Unticking
    /// the source removes its own keys from there instead of leaving them in place.
    /// </summary>
    private void PlanTextureInPlace(ConversionTask task, CustomizationKindDescriptor descriptor,
        AssetKind targetKind, CustomizationKindDescriptor targetDescriptor, CustomizationPathEndpoint source,
        ushort primaryRace, ushort primaryId, string root)
    {
        var targets = ResolveTargets(task, targetKind, targetDescriptor, source, primaryRace, primaryId);
        var resources = ReadResources(root);
        ResolveSourceFiles(task, resources, descriptor, source);

        var keepsSource = targets.Contains(source);
        var extras = targets.Where(t => t != source).ToList();
        AddKeysInPlace(task, root, source, extras, keepsSource);
    }

    private static void AddKeysInPlace(ConversionTask task, string root, CustomizationPathEndpoint source,
        IReadOnlyList<CustomizationPathEndpoint> extras, bool keepsSource)
    {
        foreach (var jsonFile in PenumbraMod.DefinitionFiles(root).Where(File.Exists))
        {
            var node = Parse(jsonFile);
            if (node == null) continue;
            var changes = new List<JsonFieldChange>();
            Walk(node, "<root>", changes);
            if (changes.Count == 0) continue;
            var planned = task.PlannedJsonChanges.FirstOrDefault(j => string.Equals(j.FilePath, jsonFile, StringComparison.OrdinalIgnoreCase));
            if (planned == null) task.PlannedJsonChanges.Add(planned = new PlannedJsonChange { FilePath = jsonFile });
            planned.Changes.AddRange(changes);
        }

        void Walk(JsonNode? node, string path, List<JsonFieldChange> changes)
        {
            if (node is JsonObject obj)
            {
                foreach (var (key, value) in obj.ToList())
                {
                    if (key == "Files" && value is JsonObject dict)
                    {
                        var matches = dict.Where(kv => CustomizationPaths.Contains(kv.Key, source)).ToList();
                        if (matches.Count > 0)
                        {
                            var additions = new JsonObject();
                            foreach (var (dictKey, dictValue) in matches)
                            {
                                if (!keepsSource)
                                    changes.Add(new JsonFieldChange { JsonPath = $"{path}.{key}[key]", OldValue = dictKey, ChangeType = "path_key_delete" });
                                if (dictValue is not JsonValue v || !v.TryGetValue<string>(out var local)) continue;
                                foreach (var extra in extras)
                                {
                                    var rewritten = CustomizationPaths.Rewrite(dictKey, source, extra);
                                    if (!dict.ContainsKey(rewritten) && !additions.ContainsKey(rewritten)) additions[rewritten] = local;
                                }
                            }
                            if (additions.Count > 0)
                                changes.Add(new JsonFieldChange { JsonPath = path, ChangeType = "dependency_files", NewValue = additions.ToJsonString() });
                        }
                        continue;
                    }
                    if (key == "FileSwaps" && value is JsonObject swaps)
                    {
                        if (!keepsSource)
                            foreach (var (dictKey, _) in swaps.ToList())
                                if (CustomizationPaths.Contains(dictKey, source))
                                    changes.Add(new JsonFieldChange { JsonPath = $"{path}.{key}[key]", OldValue = dictKey, ChangeType = "path_key_delete" });
                        continue;
                    }
                    Walk(value, path + "." + key, changes);
                }
            }
            else if (node is JsonArray array)
                for (var i = 0; i < array.Count; i++) Walk(array[i], $"{path}[{i}]", changes);
        }
    }

    /// <summary>Penumbra identifies a group by name in its UI, so a new one needs its own.</summary>
    private static string UniqueName(string name, HashSet<string> taken)
    {
        var candidate = name;
        for (var i = 2; !taken.Add(candidate); i++) candidate = $"{name} ({i})";
        return candidate;
    }

    /// <summary>Penumbra stores a multi-select group's setting as a bit per option.</summary>
    private const int MaxMultiOptions = 32;

    /// <summary>
    /// Removes the source root's own keys from every container that has them, wherever in the mod
    /// that is: the new option groups provide those paths instead, and leaving the originals in
    /// place would keep them active no matter how the new group's option is toggled.
    /// </summary>
    private static void RemoveSourceKeys(ConversionTask task, string root, CustomizationPathEndpoint source)
    {
        foreach (var jsonFile in PenumbraMod.DefinitionFiles(root).Where(File.Exists))
        {
            var node = Parse(jsonFile);
            if (node == null) continue;
            var changes = new List<JsonFieldChange>();
            Walk(node, "<root>", changes);
            if (changes.Count == 0) continue;
            var planned = task.PlannedJsonChanges.FirstOrDefault(j => string.Equals(j.FilePath, jsonFile, StringComparison.OrdinalIgnoreCase));
            if (planned == null) task.PlannedJsonChanges.Add(planned = new PlannedJsonChange { FilePath = jsonFile });
            planned.Changes.AddRange(changes);
        }

        void Walk(JsonNode? node, string path, List<JsonFieldChange> changes)
        {
            if (node is JsonObject obj)
            {
                foreach (var (key, value) in obj.ToList())
                {
                    if (key is "Files" or "FileSwaps" && value is JsonObject dict)
                    {
                        foreach (var (dictKey, _) in dict.ToList())
                            if (CustomizationPaths.Contains(dictKey, source))
                                changes.Add(new JsonFieldChange
                                {
                                    JsonPath = $"{path}.{key}[key]",
                                    OldValue = dictKey,
                                    ChangeType = "path_key_delete",
                                });
                        continue;
                    }
                    Walk(value, path + "." + key, changes);
                }
            }
            else if (node is JsonArray array)
                for (var i = 0; i < array.Count; i++) Walk(array[i], $"{path}[{i}]", changes);
        }
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
            foreach (var material in ResourceReferences.ReadMdlMaterials(File.ReadAllBytes(file)))
            {
                var renamed = CustomizationPaths.RewriteOwnedReference(material, source, target);
                if (renamed == material) continue;
                // Prefer the folder the source actually loads (Hrothgar tails: one of five).
                var sourcePaths = CustomizationPaths.MaterialPaths(source, material);
                var ordered = keys.OrderSourceMaterialPaths(sourcePaths);
                var sourcePath = ordered.FirstOrDefault(p => resources.Files.ContainsKey(p) || resources.FileSwaps.ContainsKey(p))
                                 ?? ordered[0];
                // Modded materials are already discovered and rewritten with their textures.
                if (resources.Files.ContainsKey(sourcePath)) continue;
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
                var targetPaths = CustomizationPaths.MaterialPaths(target, renamed);
                var targetPath = targetPaths[0];
                var destination = PathSafety.ResolveRelative(task.ModDirectory, targetPath);
                var prior = task.PlannedGeneratedFiles.FirstOrDefault(f => f.FilePath.Equals(destination, StringComparison.OrdinalIgnoreCase));
                if (prior != null && !prior.Data.AsSpan().SequenceEqual(bytes))
                    throw new InvalidDataException($"Conflicting material dependencies for '{targetPath}'.");
                if (prior == null)
                    task.PlannedGeneratedFiles.Add(new PlannedGeneratedFile(destination, targetPath, bytes.ToImmutableArray()));
                foreach (var path in targetPaths) additions[path] = targetPath;
            }
        }
        return result;
    }

    private static HashSet<string> DiscoverAssets(string root,
        IReadOnlyDictionary<string, string> mappings, CustomizationPathEndpoint source)
    {
        var assets = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        var queue = new Queue<string>();

        foreach (var (gamePath, localPath) in mappings)
            if (CustomizationPaths.Contains(gamePath, source)) Add(localPath);

        foreach (var file in Directory.EnumerateFiles(root, "*", SearchOption.AllDirectories))
        {
            if (CustomizationPaths.Contains(file, source)) Add(file);
            if (!StructuredExtensions.Contains(Path.GetExtension(file))) continue;
            if (BinaryPathRewriter.ExtractPaths(File.ReadAllBytes(file))
                    .Any(path => CustomizationPaths.Contains(path, source))) Add(file);
        }

        while (queue.Count > 0)
        {
            var file = queue.Dequeue();
            if (!StructuredExtensions.Contains(Path.GetExtension(file))) continue;
            foreach (var dependency in BinaryPathRewriter.ExtractPaths(File.ReadAllBytes(file)))
                foreach (var local in ResolveMappedDependencies(mappings, dependency)) Add(local);
        }
        return assets;

        void Add(string path)
        {
            var full = Path.GetFullPath(path);
            PathSafety.EnsureContained(root, full, true);
            if (assets.Add(full)) queue.Enqueue(full);
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
                if (string.Equals(obj["Type"]?.GetValue<string>(), "Est", StringComparison.OrdinalIgnoreCase) &&
                    obj["Manipulation"] is JsonObject est)
                    AddEstOverride(est);
                foreach (var (_, child) in obj) Walk(child);
            }
            else if (node is JsonArray array)
                foreach (var child in array) Walk(child);
        }

        void AddEstOverride(JsonObject est)
        {
            if (!TryInt(est["SetId"], out var setId) || setId is < 0 or > ushort.MaxValue ||
                !TryInt(est["Entry"], out var skeletonId) || skeletonId is < 0 or > ushort.MaxValue ||
                !TryEstKind(est["Slot"]?.GetValue<string>(), out var kind) ||
                !GenderRaces.TryParse(est["Race"]?.GetValue<string>(), est["Gender"]?.GetValue<string>(), out var race))
                return;
            estOverrides[new EstOverrideKey(kind, race, (ushort)setId)] = (ushort)skeletonId;
        }
    }

    private static void PlanJson(ConversionTask task, string root,
        CustomizationKindDescriptor descriptor, CustomizationPathEndpoint source,
        CustomizationPathEndpoint target, ModResourceIndex resources,
        IReadOnlyDictionary<string, Dictionary<string, string>> materialDependencies, KeyRules keys)
    {
        var renameMap = task.PlannedRenames.ToDictionary(
            rename => Normalize(Path.GetRelativePath(root, rename.OldPath)),
            rename => Normalize(Path.GetRelativePath(root, rename.NewPath)),
            StringComparer.OrdinalIgnoreCase);
        var sourceNames = GenderRaces.Names(source.GenderRace);
        var targetNames = GenderRaces.Names(target.GenderRace);

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
                        changes.Add(new JsonFieldChange { JsonPath = path, ChangeType = "dependency_files",
                            NewValue = additions.ToJsonString() });
                }
                if (descriptor.SupportsEst &&
                    string.Equals(obj["Type"]?.GetValue<string>(), "Est", StringComparison.OrdinalIgnoreCase) &&
                    obj["Manipulation"] is JsonObject est)
                    AddEstChanges(est, path + ".Manipulation", changes);

                foreach (var (key, value) in obj.ToList())
                {
                    if (key is "Files" or "FileSwaps" && value is JsonObject dict)
                    {
                        var produced = new HashSet<string>(dict.Select(p => Normalize(p.Key)), StringComparer.OrdinalIgnoreCase);
                        var variantAdditions = new JsonObject();
                        foreach (var (dictKey, dictValue) in dict.ToList())
                        {
                            var newKey = keys.Target(dictKey);
                            if (newKey != null)
                            {
                                produced.Add(Normalize(newKey));
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
                            if (replacement != valueText)
                                changes.Add(new JsonFieldChange { JsonPath = $"{path}.{key}[{dictKey}]", OldValue = valueText, NewValue = replacement, ChangeType = "path_value" });
                            if (key == "Files" && newKey != null)
                                foreach (var extra in keys.ExtraTargetVariants(newKey))
                                    variantAdditions[extra] = replacement;
                            if (key == "Files")
                                foreach (var extra in keys.ExtraTargetKeys(dictKey))
                                    variantAdditions[extra] = replacement;
                        }
                        foreach (var (extra, extraValue) in variantAdditions.ToList())
                            if (produced.Contains(Normalize(extra))) variantAdditions.Remove(extra);
                        if (variantAdditions.Count > 0)
                            changes.Add(new JsonFieldChange { JsonPath = path, ChangeType = "dependency_files",
                                NewValue = variantAdditions.ToJsonString() });
                        continue;
                    }
                    Walk(value, path + "." + key, changes);
                }
            }
            else if (node is JsonArray array)
                for (var i = 0; i < array.Count; i++) Walk(array[i], $"{path}[{i}]", changes);
            else if (node is JsonValue value && value.TryGetValue<string>(out var text))
            {
                var replacement = CustomizationPaths.Rewrite(text, source, target);
                if (replacement != text)
                    changes.Add(new JsonFieldChange { JsonPath = path, OldValue = text, NewValue = replacement, ChangeType = "path_string" });
            }
        }

        void AddEstChanges(JsonObject est, string path, List<JsonFieldChange> changes)
        {
            if (!TryInt(est["SetId"], out var setId) || setId != source.ModelId) return;
            // Hair and face share the same identifier space; the slot tells them apart.
            if (!string.Equals(est["Slot"]?.GetValue<string>(), source.Kind.ToString(), StringComparison.OrdinalIgnoreCase)) return;
            if (est["Race"]?.GetValue<string>() is { } race && !race.Equals(sourceNames.Race, StringComparison.OrdinalIgnoreCase)) return;
            if (est["Gender"]?.GetValue<string>() is { } gender && !gender.Equals(sourceNames.Gender, StringComparison.OrdinalIgnoreCase)) return;
            changes.Add(new JsonFieldChange { JsonPath = path + ".SetId", OldValue = source.ModelId.ToString("D4"), NewValue = target.ModelId.ToString("D4"),
                ChangeType = est["SetId"]?.GetValueKind() == JsonValueKind.String ? "numeric_id_string" : "numeric_id" });
            // Only real edits: an unchanged value would count as a failed change at apply time.
            if (est["Race"] is not null && sourceNames.Race != targetNames.Race)
                changes.Add(new JsonFieldChange { JsonPath = path + ".Race", OldValue = sourceNames.Race, NewValue = targetNames.Race, ChangeType = "path_string" });
            if (est["Gender"] is not null && sourceNames.Gender != targetNames.Gender)
                changes.Add(new JsonFieldChange { JsonPath = path + ".Gender", OldValue = sourceNames.Gender, NewValue = targetNames.Gender, ChangeType = "path_string" });
        }
    }

    /// <summary>
    /// Local files that must keep their name because a key that is copied or skipped
    /// (shared material root, unused Hrothgar variant) still points at them.
    /// </summary>
    private static HashSet<string> KeepInPlace(string root, KeyRules keys)
    {
        var result = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (var container in PenumbraMod.Load(root).Containers)
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
    private void PlanExtraSkeleton(ConversionTask task, string root, CustomizationKindDescriptor descriptor,
        CustomizationPathEndpoint source, CustomizationPathEndpoint target, ModResourceIndex resources)
    {
        if (!descriptor.SupportsEst || source.Kind != target.Kind) return;
        var estPath = source.Kind == AssetKind.Hair
            ? "chara/xls/charadb/hairskeletontemplate.est"
            : "chara/xls/charadb/faceskeletontemplate.est";

        var mod = PenumbraMod.Load(root);
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

        // Every skeleton the converted part can load, in any option, must exist for the target race.
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

        // An explicit default entry is retargeted with the rest of the metadata.
        if (defaultSource != null || !defaultAvailable) return;

        var file = Path.Combine(root, PenumbraMod.MetaFileName);
        const string jsonPath = "<root>.DefaultData";
        if (!File.Exists(file))
        {
            task.Diagnostics.Add(new PlanDiagnostic("extra_skeleton_not_added",
                $"The mod has no default option to receive the extra-skeleton entry for {prefix}{defaultSkeleton:D4}.", false));
            return;
        }

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
                !string.Equals(obj["Type"]?.GetValue<string>(), "Est", StringComparison.OrdinalIgnoreCase) ||
                obj["Manipulation"] is not JsonObject est) continue;
            if (!TryInt(est["SetId"], out var setId) || setId != endpoint.ModelId ||
                !TryInt(est["Entry"], out var entry) || entry is < 0 or > ushort.MaxValue ||
                !string.Equals(est["Slot"]?.GetValue<string>(), endpoint.Kind.ToString(), StringComparison.OrdinalIgnoreCase) ||
                !string.Equals(est["Race"]?.GetValue<string>(), race, StringComparison.OrdinalIgnoreCase) ||
                !string.Equals(est["Gender"]?.GetValue<string>(), gender, StringComparison.OrdinalIgnoreCase))
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
        private readonly IReadOnlyList<CustomizationPathEndpoint> _extraTargets;
        private readonly bool _keepSource;
        private readonly HashSet<string> _skipped = new(StringComparer.OrdinalIgnoreCase);

        public KeyRules(CustomizationPathEndpoint source, CustomizationPathEndpoint target, ModResourceIndex resources,
            IReadOnlyList<CustomizationPathEndpoint>? extraTargets = null, bool keepSource = false)
        {
            _source = source;
            _target = target;
            _extraTargets = extraTargets ?? [];
            _keepSource = keepSource;
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
        /// Whether this key is added rather than moved: material keys under a root other
        /// customizations also load, and every key of a conversion that keeps its source.
        /// </summary>
        public bool IsShared(string key)
            => _keepSource ||
               key.EndsWith(".mtrl", StringComparison.OrdinalIgnoreCase) &&
               CustomizationPaths.FindEndpoints(key).Any(e => CustomizationPaths.IsSharedMaterialRoot(e) &&
                   (e == _source || e == CustomizationPaths.GetMaterialEndpoint(_source)));

        /// <summary>The same file's key for every extra target, so one texture serves them all.</summary>
        public IEnumerable<string> ExtraTargetKeys(string key)
        {
            if (IsSkipped(key)) yield break;
            foreach (var extra in _extraTargets)
            {
                var rewritten = CustomizationPaths.Rewrite(key, _source, extra);
                if (!string.Equals(rewritten, key, StringComparison.Ordinal)) yield return rewritten;
            }
        }

        public bool IsSharedMaterialFile(string localPath) => IsShared(localPath);

        /// <summary>A Hrothgar tail target may load any of v0001-v0005; the other folders get the same file.</summary>
        public IEnumerable<string> ExtraTargetVariants(string newKey)
        {
            if (!CustomizationPaths.IsHrothgarTail(_target) || !newKey.EndsWith(".mtrl", StringComparison.OrdinalIgnoreCase))
                yield break;
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
                var replacements = binary?.Patches.Where(p => p.Selected)
                    .ToDictionary(p => p.OldString, p => p.NewString, StringComparer.Ordinal)
                    ?? new Dictionary<string, string>(StringComparer.Ordinal);
                if (replacements.Count > 0) model.ReplacePaths(replacements);
                RacialDeformationPlan? deformationPlan = null;
                MdlConversionReport? report = null;
                if (route != null)
                {
                    deformationPlan = route.Materialize(model.Bones, hierarchy.StepSkeletons,
                        hierarchy.SourceSkeleton);
                    report = MdlRaceConverter.Convert(model, deformationPlan);
                }
                var output = model.Write();
                _ = MdlFile.Read(output);

                var planned = new PlannedMdlChange
                {
                    FilePath = filePath,
                    SourceGenderRace = sourceRace,
                    TargetGenderRace = targetRace,
                    InputHash = MdlRaceConverter.Hash(input),
                    OutputHash = MdlRaceConverter.Hash(output),
                    LodCount = report?.LodCount ?? model.Header.LodCount,
                    MeshCount = report?.MeshCount ?? model.Meshes.Length,
                    VertexCount = report?.VertexCount ?? 0,
                    ShapeVertexCount = report?.ShapeVertexCount ?? 0,
                    GeometryConverted = deformationPlan != null,
                    DeformationPlan = deformationPlan,
                };
                if (report != null) planned.BoneResolutions.AddRange(report.BoneResolutions);
                if (binary != null)
                {
                    foreach (var patch in binary.Patches)
                        planned.PathReplacements.Add(new BinaryStringPatch
                        {
                            OldString = patch.OldString,
                            NewString = patch.NewString,
                            Selected = true,
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
            log.Information("[UMC] Planned parsed MDL rewrite for {0} file(s), including {1} racial deformation(s).",
                task.PlannedMdlChanges.Count, task.PlannedMdlChanges.Count(change => change.GeometryConverted));
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
    private static bool TryInt(JsonNode? node, out int value)
    {
        value = 0;
        if (node is not JsonValue jsonValue) return false;
        if (jsonValue.TryGetValue<int>(out value)) return true;
        return jsonValue.TryGetValue<string>(out var text) && int.TryParse(text, out value);
    }

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
