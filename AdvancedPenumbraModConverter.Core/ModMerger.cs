using System.Text.Json.Nodes;

namespace AdvancedPenumbraModConverter.Core;

/// <summary>One thing both modpacks change, and which of them the merged mod takes it from.</summary>
/// <param name="What">The game path or metadata entry.</param>
/// <param name="Where">The option it lives in, e.g. "Default" or "Colour / Red".</param>
/// <param name="Kept">Display name of the modpack whose version is kept.</param>
public sealed record MergeConflict(string What, string Where, string Kept);

/// <summary>A file the merged mod needs, copied from one of the two modpacks.</summary>
public sealed record MergeCopy(string SourceDirectory, string SourceLocal, string Destination);

/// <summary>What merging two modpacks produces: a definition, the files it needs, and what overlapped.</summary>
public sealed class ModMergePlan
{
    public required PenumbraMod Result { get; init; }
    public required IReadOnlyList<MergeCopy> Copies { get; init; }
    public required IReadOnlyList<MergeConflict> Conflicts { get; init; }
    public required IReadOnlyList<string> Notes { get; init; }

    /// <summary>Files an option points at that are not in the modpack's folder.</summary>
    public required IReadOnlyList<string> MissingFiles { get; init; }

    /// <summary>The modpack whose content gives way on a conflict.</summary>
    public required string BaseName { get; init; }

    /// <summary>The modpack whose content is kept on a conflict.</summary>
    public required string OverlayName { get; init; }

    public int GroupsMerged { get; init; }
    public int GroupsAdded { get; init; }
    public int SharedFiles { get; init; }
    public int RenamedFiles { get; init; }
}

/// <summary>
/// Merges two modpacks into one. Some authors split a mod across packs — a base with the
/// materials and textures, and a second pack with only upscaled models — and each is broken on
/// its own. The merged mod holds both; where they change the same thing, the pack the user put
/// on top wins everywhere:
/// <list type="bullet">
/// <item>Options of the same group (matched by name and type) are merged, same-named options
/// into one container; the rest of the options and groups are appended.</item>
/// <item>A path or metadata entry the top pack sets by default is removed from the lower pack's
/// options, which would otherwise override it, since groups outrank the default.</item>
/// <item>Groups taken from the top pack are raised above the lower pack's groups, so when an
/// option of each is on, the top pack's wins in Penumbra too.</item>
/// </list>
/// </summary>
public static class ModMerger
{
    /// <summary>Penumbra's limit for a multi-selection group.</summary>
    private const int MaxMultiOptions = 32;

    /// <param name="baseDirectory">The modpack that gives way on a conflict.</param>
    /// <param name="overlayDirectory">The modpack that is kept on a conflict.</param>
    /// <param name="name">Display name of the merged mod.</param>
    public static ModMergePlan Plan(string baseDirectory, string overlayDirectory, string name)
    {
        if (string.Equals(Path.GetFullPath(baseDirectory).TrimEnd('\\', '/'),
                Path.GetFullPath(overlayDirectory).TrimEnd('\\', '/'), StringComparison.OrdinalIgnoreCase))
            throw new InvalidOperationException("Choose two different modpacks.");
        return new Builder(baseDirectory, PenumbraMod.Load(baseDirectory),
            overlayDirectory, PenumbraMod.Load(overlayDirectory), name).Build();
    }

    /// <summary>Writes the merged mod into <paramref name="outputDirectory"/>, which must be empty or absent.</summary>
    public static void Write(ModMergePlan plan, string outputDirectory, Action<string>? log = null)
    {
        Directory.CreateDirectory(outputDirectory);
        if (Directory.EnumerateFileSystemEntries(outputDirectory).Any())
            throw new IOException($"The output directory is not empty: {outputDirectory}");

        PathSafety.ValidateNoCaseCollisions(plan.Copies.Select(c => Path.Combine(outputDirectory, c.Destination)));
        foreach (var copy in plan.Copies)
        {
            var destination = PathSafety.ResolveRelative(outputDirectory, copy.Destination);
            Directory.CreateDirectory(Path.GetDirectoryName(destination)!);
            File.Copy(PathSafety.ResolveRelative(copy.SourceDirectory, copy.SourceLocal), destination, overwrite: false);
        }

        plan.Result.Save(outputDirectory);
        log?.Invoke($"Copied {plan.Copies.Count} file(s) and wrote the merged mod definition.");
    }

    private sealed class Builder(string baseDir, PenumbraMod baseMod, string overlayDir, PenumbraMod overlay, string name)
    {
        private readonly PenumbraMod _result = baseMod.Clone();
        private readonly List<MergeCopy> _copies = [];
        private readonly List<MergeConflict> _conflicts = [];
        private readonly List<string> _notes = [];
        private readonly List<string> _missing = [];

        /// <summary>Destination local paths taken so far, by normalized path.</summary>
        private readonly Dictionary<string, (string Directory, string Local)> _taken = new(StringComparer.Ordinal);

        /// <summary>Where each of the top pack's files ends up in the merged mod.</summary>
        private readonly Dictionary<string, string> _overlayLocals = new(StringComparer.Ordinal);

        /// <summary>The top pack's group and option IDs that had to change, old → new.</summary>
        private readonly Dictionary<string, string> _idRemap = new(StringComparer.OrdinalIgnoreCase);

        private readonly HashSet<string> _ids = new(StringComparer.OrdinalIgnoreCase);
        private readonly List<JsonObject> _appended = [];

        private string BaseName => DisplayName(baseMod, baseDir);
        private string OverlayName => DisplayName(overlay, overlayDir);

        private int _groupsMerged, _groupsAdded, _shared, _renamed;

        public ModMergePlan Build()
        {
            RegisterBaseFiles();
            CollectIds();
            MergeMeta();
            MergeDefault();
            MergeGroups();
            RemapReferences();

            return new ModMergePlan
            {
                Result       = _result,
                Copies       = _copies,
                Conflicts    = _conflicts,
                Notes        = _notes,
                MissingFiles = _missing,
                BaseName     = BaseName,
                OverlayName  = OverlayName,
                GroupsMerged = _groupsMerged,
                GroupsAdded  = _groupsAdded,
                SharedFiles  = _shared,
                RenamedFiles = _renamed,
            };
        }

        private static string DisplayName(PenumbraMod mod, string directory)
            => mod.Name.Length > 0 ? mod.Name : Path.GetFileName(directory.TrimEnd('\\', '/'));

        // ── Files ───────────────────────────────────────────────────────────

        /// <summary>The lower pack's files keep their place; the top pack's are fitted around them.</summary>
        private void RegisterBaseFiles()
        {
            foreach (var container in _result.Containers)
            foreach (var (_, local) in container.FileEntries())
            {
                var key = GamePath.NormalizeLocal(local);
                if (_taken.ContainsKey(key)) continue;
                if (!File.Exists(Path.Combine(baseDir, GamePath.ToLocal(local))))
                {
                    _missing.Add($"{BaseName}: {local}");
                    _taken[key] = (baseDir, local);
                    continue;
                }
                _taken[key] = (baseDir, local);
                _copies.Add(new MergeCopy(baseDir, GamePath.ToLocal(local), GamePath.ToLocal(local)));
            }
        }

        /// <summary>
        /// Where a file of the top pack goes. The same file at the same place is shared; a
        /// different file at a place the lower pack already uses is renamed, which is safe
        /// because game files refer to each other by game path, never by their place in the mod.
        /// </summary>
        private string OverlayLocal(string local)
        {
            var key = GamePath.NormalizeLocal(local);
            if (_overlayLocals.TryGetValue(key, out var mapped)) return mapped;

            var source = Path.Combine(overlayDir, GamePath.ToLocal(local));
            if (!File.Exists(source))
            {
                _missing.Add($"{OverlayName}: {local}");
                return _overlayLocals[key] = local;
            }

            if (_taken.TryGetValue(key, out var existing))
            {
                if (SameContent(Path.Combine(existing.Directory, GamePath.ToLocal(existing.Local)), source))
                {
                    _shared++;
                    return _overlayLocals[key] = existing.Local;
                }

                var renamed = Unique(GamePath.ToLocal(local));
                _renamed++;
                _taken[GamePath.NormalizeLocal(renamed)] = (overlayDir, renamed);
                _copies.Add(new MergeCopy(overlayDir, GamePath.ToLocal(local), renamed));
                return _overlayLocals[key] = renamed;
            }

            _taken[key] = (overlayDir, local);
            _copies.Add(new MergeCopy(overlayDir, GamePath.ToLocal(local), GamePath.ToLocal(local)));
            return _overlayLocals[key] = local;
        }

        private string Unique(string local)
        {
            var directory = Path.GetDirectoryName(local) ?? string.Empty;
            var stem = Path.GetFileNameWithoutExtension(local);
            var extension = Path.GetExtension(local);
            for (var i = 2; ; i++)
            {
                var candidate = Path.Combine(directory, $"{stem}_{i}{extension}");
                if (!_taken.ContainsKey(GamePath.NormalizeLocal(candidate))) return candidate;
            }
        }

        private static bool SameContent(string a, string b)
        {
            if (!File.Exists(a)) return false;
            if (new FileInfo(a).Length != new FileInfo(b).Length) return false;
            return File.ReadAllBytes(a).AsSpan().SequenceEqual(File.ReadAllBytes(b));
        }

        /// <summary>A copy of one of the top pack's containers with its files pointing at their merged place.</summary>
        private JsonObject Remapped(JsonObject container)
        {
            var clone = (JsonObject)container.DeepClone();
            if (clone["Files"] is JsonObject files)
                foreach (var key in files.Select(p => p.Key).ToList())
                    if (Json.GetString(files[key]) is { Length: > 0 } local)
                        files[key] = OverlayLocal(local);
            return clone;
        }

        // ── Meta ────────────────────────────────────────────────────────────

        private void MergeMeta()
        {
            var meta = _result.Meta;
            meta["Name"] = name;
            meta["Identifier"] = Guid.NewGuid().ToString();

            var authors = new[] { Json.GetString(baseMod.Meta["Author"]), Json.GetString(overlay.Meta["Author"]) }
                .Where(a => !string.IsNullOrWhiteSpace(a))
                .Distinct(StringComparer.OrdinalIgnoreCase)
                .ToList();
            if (authors.Count > 0) meta["Author"] = string.Join(", ", authors);

            var baseDescription = Json.GetString(baseMod.Meta["Description"]) ?? string.Empty;
            var overlayDescription = Json.GetString(overlay.Meta["Description"]) ?? string.Empty;
            if (overlayDescription.Length > 0 && !string.Equals(baseDescription, overlayDescription, StringComparison.Ordinal))
                meta["Description"] = baseDescription.Length == 0
                    ? overlayDescription
                    : $"{baseDescription}\n\n{OverlayName}:\n{overlayDescription}";

            foreach (var list in new[] { "ModTags", "DefaultPreferredItems" })
                UnionArray(meta, overlay.Meta, list);
        }

        private static void UnionArray(JsonObject target, JsonObject other, string property)
        {
            if (other[property] is not JsonArray additions || additions.Count == 0) return;
            if (target[property] is not JsonArray existing)
            {
                target[property] = additions.DeepClone();
                return;
            }
            var seen = existing.Select(n => n?.ToJsonString()).ToHashSet(StringComparer.OrdinalIgnoreCase);
            foreach (var item in additions)
                if (seen.Add(item?.ToJsonString()))
                    existing.Add(item?.DeepClone());
        }

        // ── Containers ──────────────────────────────────────────────────────

        private void MergeDefault()
        {
            var incoming = Remapped(overlay.Default.Node);
            MergeContainer(_result.Default, incoming);

            // Every group outranks the default, so a lower-pack option that sets what the top
            // pack sets by default would still win in game. The top pack is meant to win.
            var paths = Redirections(incoming).Select(r => r.Normalized).ToHashSet(StringComparer.Ordinal);
            var metadata = (incoming["Manipulations"] as JsonArray)?.OfType<JsonObject>()
                .Select(GearManipulations.Identity).ToHashSet(StringComparer.Ordinal) ?? [];
            if (paths.Count == 0 && metadata.Count == 0) return;

            foreach (var container in _result.Groups.SelectMany(g => g.Containers))
            {
                foreach (var (key, normalized) in Redirections(container.Node).ToList())
                {
                    if (!paths.Contains(normalized)) continue;
                    RemoveRedirection(container.Node, normalized);
                    _conflicts.Add(new MergeConflict(key, $"{container.Label} ({BaseName})", OverlayName));
                }

                if (container.Manipulations is not { } manipulations) continue;
                for (var i = manipulations.Count - 1; i >= 0; i--)
                {
                    if (manipulations[i] is not JsonObject manipulation ||
                        !metadata.Contains(GearManipulations.Identity(manipulation))) continue;
                    manipulations.RemoveAt(i);
                    _conflicts.Add(new MergeConflict(GearManipulations.Describe(manipulation),
                        $"{container.Label} ({BaseName})", OverlayName));
                }
            }
        }

        /// <summary>Adds the top pack's <paramref name="incoming"/> container to <paramref name="target"/>; it wins every overlap.</summary>
        private void MergeContainer(ModContainer target, JsonObject incoming)
        {
            var where = target.Label;
            foreach (var property in new[] { "Files", "FileSwaps" })
            {
                if (incoming[property] is not JsonObject entries) continue;
                foreach (var (key, value) in entries)
                {
                    var normalized = GamePath.Normalize(key);
                    var existing = Find(target.Node, normalized);
                    if (existing is { } found)
                    {
                        if (found.Property == property && JsonNode.DeepEquals(found.Value, value)) continue;
                        RemoveRedirection(target.Node, normalized);
                        _conflicts.Add(new MergeConflict(key, where, OverlayName));
                    }

                    var destination = property == "Files" ? target.GetOrCreateFiles() : target.GetOrCreateFileSwaps();
                    destination[key] = value?.DeepClone();
                }
            }

            if (incoming["Manipulations"] is not JsonArray additions || additions.Count == 0) return;
            var manipulations = target.GetOrCreateManipulations();
            foreach (var addition in additions.OfType<JsonObject>())
            {
                var identity = GearManipulations.Identity(addition);
                var index = IndexOf(manipulations, identity);
                if (index >= 0)
                {
                    if (JsonNode.DeepEquals(manipulations[index], addition)) continue;
                    manipulations.RemoveAt(index);
                    _conflicts.Add(new MergeConflict(GearManipulations.Describe(addition), where, OverlayName));
                }
                manipulations.Add(addition.DeepClone());
            }
        }

        private static int IndexOf(JsonArray manipulations, string identity)
        {
            for (var i = 0; i < manipulations.Count; i++)
                if (manipulations[i] is JsonObject m && GearManipulations.Identity(m) == identity)
                    return i;
            return -1;
        }

        /// <summary>Files and file swaps share one namespace: each game path resolves to one thing.</summary>
        private static IEnumerable<(string Key, string Normalized)> Redirections(JsonObject container)
        {
            foreach (var property in new[] { "Files", "FileSwaps" })
                if (container[property] is JsonObject entries)
                    foreach (var (key, _) in entries)
                        yield return (key, GamePath.Normalize(key));
        }

        private static (string Property, JsonNode? Value)? Find(JsonObject container, string normalized)
        {
            foreach (var property in new[] { "Files", "FileSwaps" })
                if (container[property] is JsonObject entries)
                    foreach (var (key, value) in entries)
                        if (GamePath.Normalize(key) == normalized)
                            return (property, value);
            return null;
        }

        private static void RemoveRedirection(JsonObject container, string normalized)
        {
            foreach (var property in new[] { "Files", "FileSwaps" })
                if (container[property] is JsonObject entries)
                    foreach (var key in entries.Select(p => p.Key).Where(k => GamePath.Normalize(k) == normalized).ToList())
                        entries.Remove(key);
        }

        // ── Groups ──────────────────────────────────────────────────────────

        private void MergeGroups()
        {
            var baseGroups = _result.Groups.Count;
            var basePriority = _result.Groups.Select(g => Json.GetInt(g.Node["Priority"], 0)).DefaultIfEmpty(0).Max();
            var added = new List<JsonObject>();

            foreach (var group in overlay.Groups)
            {
                var match = _result.Groups.Take(baseGroups).FirstOrDefault(g =>
                    string.Equals(g.Name, group.Name, StringComparison.OrdinalIgnoreCase) &&
                    string.Equals(g.Type, group.Type, StringComparison.OrdinalIgnoreCase));
                if (match != null && TryMerge(match, group))
                {
                    _groupsMerged++;
                    continue;
                }
                added.Add(Append(group));
            }

            if (added.Count == 0 || baseGroups == 0) return;

            // The top pack's groups go above the lower pack's, keeping their own order.
            var lowest = added.Min(g => Json.GetInt(g["Priority"], 0));
            var offset = Math.Max(0, basePriority + 1 - lowest);
            if (offset == 0) return;
            foreach (var node in added) node["Priority"] = Json.GetInt(node["Priority"], 0) + offset;
            _notes.Add($"Groups from '{OverlayName}' were given a higher priority than those of '{BaseName}', " +
                       "so its options win when options of both are enabled.");
        }

        private bool TryMerge(ModGroup target, ModGroup group)
        {
            if (group.IsImc) return TryMergeImc(target, group);
            if (group.IsCombining) return TryMergeCombining(target, group);

            var options = target.Options.ToList();
            var incoming = group.Options.ToList();
            var unmatched = incoming.Count(o => FindOption(options, o) == null);
            if (group.Type.Equals("Multi", StringComparison.OrdinalIgnoreCase) && options.Count + unmatched > MaxMultiOptions)
            {
                _notes.Add($"'{group.Name}' could not be merged: together the two would have more than " +
                           $"{MaxMultiOptions} options, so the one from '{OverlayName}' was added as its own group.");
                return false;
            }

            if (target.Node["Options"] is not JsonArray array) target.Node["Options"] = array = [];
            foreach (var option in incoming)
            {
                if (FindOption(options, option) is { } existing)
                {
                    var index = options.IndexOf(existing);
                    MergeContainer(target.Containers[index], Remapped(option));
                    MapId(option, existing);
                    continue;
                }

                var clone = Remapped(option);
                FreshId(clone);
                array.Add(clone);
                _appended.Add(clone);
            }

            Rebuild(target);
            _notes.Add($"Merged the '{group.Name}' options of both modpacks.");
            return true;
        }

        private bool TryMergeImc(ModGroup target, ModGroup group)
        {
            if (!JsonNode.DeepEquals(target.Node["Identifier"], group.Node["Identifier"])) return false;
            if (JsonNode.DeepEquals(target.Node, group.Node)) return true;

            var clone = (JsonObject)group.Node.DeepClone();
            _result.Groups[target.Index] = new ModGroup(clone, target.Index);
            MapId(group.Node, target.Node);
            foreach (var (option, existing) in group.Options.Zip(target.Options)) MapId(option, existing);
            _conflicts.Add(new MergeConflict($"IMC attributes of '{group.Name}'", group.Name, OverlayName));
            return true;
        }

        private bool TryMergeCombining(ModGroup target, ModGroup group)
        {
            var names = target.Options.Select(o => Json.GetString(o["Name"]) ?? string.Empty).ToList();
            var incoming = group.Options.Select(o => Json.GetString(o["Name"]) ?? string.Empty).ToList();
            if (!names.SequenceEqual(incoming, StringComparer.OrdinalIgnoreCase) ||
                target.Containers.Count != group.Containers.Count)
            {
                _notes.Add($"'{group.Name}' combines different options in each modpack, so the one from " +
                           $"'{OverlayName}' was added as its own group.");
                return false;
            }

            for (var i = 0; i < group.Containers.Count; i++)
                MergeContainer(target.Containers[i], Remapped(group.Containers[i].Node));
            foreach (var (option, existing) in group.Options.Zip(target.Options)) MapId(option, existing);
            _notes.Add($"Merged the '{group.Name}' combinations of both modpacks.");
            return true;
        }

        private static JsonObject? FindOption(IEnumerable<JsonObject> options, JsonObject option)
        {
            var name = Json.GetString(option["Name"]) ?? string.Empty;
            return options.FirstOrDefault(o => string.Equals(Json.GetString(o["Name"]), name, StringComparison.OrdinalIgnoreCase));
        }

        /// <summary>Adds one of the top pack's groups as it is, with its files moved to their merged place.</summary>
        private JsonObject Append(ModGroup group)
        {
            var node = (JsonObject)group.Node.DeepClone();
            var containers = group.IsCombining ? node["Containers"] as JsonArray
                : group.IsImc ? null
                : node["Options"] as JsonArray;
            if (containers != null)
                for (var i = 0; i < containers.Count; i++)
                    if (containers[i] is JsonObject container)
                        containers[i] = Remapped(container);

            var groupName = group.Name;
            if (_result.Groups.Any(g => string.Equals(g.Name, groupName, StringComparison.OrdinalIgnoreCase)))
            {
                groupName = UniqueGroupName($"{group.Name} ({OverlayName})");
                node["Name"] = groupName;
                _notes.Add($"'{group.Name}' from '{OverlayName}' was added as '{groupName}', since '{BaseName}' " +
                           "has a different group of that name.");
            }

            FreshId(node);
            if (node["Options"] is JsonArray options)
                foreach (var option in options.OfType<JsonObject>())
                    FreshId(option);

            _result.Groups.Add(new ModGroup(node, _result.Groups.Count));
            _appended.Add(node);
            _groupsAdded++;
            return node;
        }

        private string UniqueGroupName(string candidate)
        {
            var result = candidate;
            for (var i = 2; _result.Groups.Any(g => string.Equals(g.Name, result, StringComparison.OrdinalIgnoreCase)); i++)
                result = $"{candidate} {i}";
            return result;
        }

        private void Rebuild(ModGroup group) => _result.Groups[group.Index] = new ModGroup(group.Node, group.Index);

        // ── IDs ─────────────────────────────────────────────────────────────
        // Groups and options carry GUIDs that conditions and parent links refer to. Two packs
        // cut from the same mod share them, so an appended copy may need new ones, and whatever
        // referred to a merged option must now refer to the option it was merged into.

        private void CollectIds()
        {
            foreach (var group in _result.Groups)
            {
                if (Json.GetString(group.Node["Id"]) is { } id) _ids.Add(id);
                foreach (var option in group.Options)
                    if (Json.GetString(option["Id"]) is { } optionId) _ids.Add(optionId);
            }
        }

        private void FreshId(JsonObject node)
        {
            if (Json.GetString(node["Id"]) is not { } id) return;
            if (_ids.Add(id)) return;
            var fresh = Guid.NewGuid().ToString();
            _idRemap[id] = fresh;
            node["Id"] = fresh;
            _ids.Add(fresh);
        }

        private void MapId(JsonObject from, JsonObject to)
        {
            if (Json.GetString(from["Id"]) is { } source && Json.GetString(to["Id"]) is { } target &&
                !string.Equals(source, target, StringComparison.OrdinalIgnoreCase))
                _idRemap[source] = target;
        }

        private void RemapReferences()
        {
            if (_idRemap.Count == 0) return;
            foreach (var node in _appended)
            {
                Remap(node["ParentSetting"], v => node["ParentSetting"] = v);
                RemapTree(node["Condition"]);
                if (node["Options"] is JsonArray options)
                    foreach (var option in options.OfType<JsonObject>())
                    {
                        Remap(option["ParentSetting"], v => option["ParentSetting"] = v);
                        RemapTree(option["Condition"]);
                    }
            }
        }

        private void RemapTree(JsonNode? node)
        {
            switch (node)
            {
                case JsonObject obj:
                    foreach (var key in obj.Select(p => p.Key).ToList())
                    {
                        if (obj[key] is JsonValue) Remap(obj[key], v => obj[key] = v);
                        else RemapTree(obj[key]);
                    }
                    break;
                case JsonArray array:
                    for (var i = 0; i < array.Count; i++)
                    {
                        var index = i;
                        if (array[i] is JsonValue) Remap(array[i], v => array[index] = v);
                        else RemapTree(array[i]);
                    }
                    break;
            }
        }

        private void Remap(JsonNode? value, Action<string> set)
        {
            if (Json.GetString(value) is { } text && _idRemap.TryGetValue(text, out var mapped)) set(mapped);
        }
    }
}
