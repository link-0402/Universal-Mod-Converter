using System.Text.Json.Nodes;

namespace UniversalModConverter.Core;

/// <summary>
/// A new mod holding only a converted hair, face, tail or ear: a copy of its mod, converted in
/// place, with everything else taken out again.
/// </summary>
public static class CustomizationOutput
{
    /// <summary>
    /// Keeps only what loads the converted customization in the mod at <paramref name="directory"/>:
    /// the redirects of <paramref name="keys"/> (the game paths the conversion wrote) and of what
    /// their files load in turn, the metadata about <paramref name="target"/> itself, the groups
    /// and options still holding any of it, and the files those redirects load. Everything else
    /// is removed.
    /// </summary>
    public static void KeepOnly(string directory, IReadOnlyCollection<string> keys, CustomizationPathEndpoint target,
        Action<string>? log = null)
    {
        var mod = PenumbraMod.Load(directory);
        var before = mod.Clone();
        var kept = WithDependencies(directory, mod, keys);
        var removed = 0;
        foreach (var container in mod.Containers)
        {
            removed += Filter(container.Files, kept);
            removed += Filter(container.FileSwaps, kept);
            if (container.Manipulations is { } manipulations)
                foreach (var manipulation in manipulations.ToList())
                    if (manipulation is not JsonObject obj || !IsAbout(obj, target))
                    {
                        manipulations.Remove(manipulation);
                        removed++;
                    }
            if ((container.Files?.Count ?? 1) == 0) container.Node.Remove("Files");
            if ((container.FileSwaps?.Count ?? 1) == 0) container.Node.Remove("FileSwaps");
            if ((container.Manipulations?.Count ?? 1) == 0) container.Node.Remove("Manipulations");
        }
        ModGroupPruning.Prune(mod, before, group => log?.Invoke($"Left out group '{group.Name}': nothing of the conversion is in it."));
        mod.Save(directory);
        log?.Invoke($"Left out {removed} redirect(s) and manipulation(s) that are not part of the conversion.");

        // The files the remaining redirects load, and the definition, are all the new mod holds.
        var used = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (var file in PenumbraMod.DefinitionFiles(directory)) used.Add(Path.GetFullPath(file));
        foreach (var container in mod.Containers)
        foreach (var (_, local) in container.FileEntries())
            used.Add(Path.GetFullPath(PathSafety.ResolveRelative(directory, GamePath.ToLocal(local))));
        var deleted = 0;
        foreach (var file in Directory.EnumerateFiles(directory, "*", SearchOption.AllDirectories).ToList())
        {
            if (used.Contains(Path.GetFullPath(file))) continue;
            File.Delete(file);
            deleted++;
        }
        foreach (var folder in Directory.EnumerateDirectories(directory, "*", SearchOption.AllDirectories)
                     .OrderByDescending(d => d.Length))
            if (!Directory.EnumerateFileSystemEntries(folder).Any()) Directory.Delete(folder);
        if (deleted > 0) log?.Invoke($"Left out {deleted} file(s) nothing in the new mod loads.");
    }

    private static readonly HashSet<string> StructuredExtensions =
        new([".mdl", ".mtrl", ".avfx", ".atex", ".sklb", ".pap", ".tmb"], StringComparer.OrdinalIgnoreCase);

    /// <summary>
    /// <paramref name="keys"/> and every redirect of the mod they load in turn. A file the
    /// conversion did not have to change stays where it is: a shared material keeps loading its
    /// textures from the source's paths, in every option that supplies them. A model's short
    /// material name (/mt_….mtrl) matches the redirect ending in it.
    /// </summary>
    private static HashSet<string> WithDependencies(string directory, PenumbraMod mod, IReadOnlyCollection<string> keys)
    {
        var files = new Dictionary<string, List<string>>(StringComparer.Ordinal);
        var swaps = new Dictionary<string, List<string>>(StringComparer.Ordinal);
        foreach (var container in mod.Containers)
        {
            foreach (var (key, local) in container.FileEntries())
            {
                string full;
                try { full = PathSafety.ResolveRelative(directory, GamePath.ToLocal(local)); }
                catch (InvalidDataException) { continue; }
                Add(files, GamePath.Normalize(key), full);
            }
            foreach (var (key, swapped) in container.SwapEntries()) Add(swaps, GamePath.Normalize(key), GamePath.Normalize(swapped));
        }
        var known = files.Keys.Concat(swaps.Keys).ToHashSet(StringComparer.Ordinal);

        var kept = keys.Select(GamePath.Normalize).ToHashSet(StringComparer.Ordinal);
        var pending = new Queue<string>(kept);
        var read = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        while (pending.Count > 0)
        {
            var key = pending.Dequeue();
            var references = swaps.GetValueOrDefault(key, []).ToList();
            foreach (var file in files.GetValueOrDefault(key, []))
                if (StructuredExtensions.Contains(Path.GetExtension(file)) && File.Exists(file) && read.Add(file))
                    references.AddRange(References(file).Select(GamePath.Normalize));
            foreach (var reference in references)
            foreach (var match in reference.Contains('/')
                         ? known.Where(k => k == reference)
                         : known.Where(k => k.EndsWith("/" + reference, StringComparison.Ordinal)))
                if (kept.Add(match)) pending.Enqueue(match);
        }
        return kept;

        // A model loads what its material list names, not every string it holds (bone and
        // attribute names among them).
        static IReadOnlyList<string> References(string file)
        {
            var bytes = File.ReadAllBytes(file);
            if (string.Equals(Path.GetExtension(file), ".mdl", StringComparison.OrdinalIgnoreCase))
                try { return ResourceReferences.ReadMdlMaterials(bytes); }
                catch (Exception ex) when (ex is InvalidDataException or IOException or ArgumentException or IndexOutOfRangeException) { }
            return BinaryPathRewriter.ExtractPaths(bytes);
        }

        static void Add(Dictionary<string, List<string>> map, string key, string value)
        {
            if (!map.TryGetValue(key, out var list)) map[key] = list = [];
            list.Add(value);
        }
    }

    private static int Filter(JsonObject? redirects, HashSet<string> kept)
    {
        if (redirects == null) return 0;
        var removed = 0;
        foreach (var (key, _) in redirects.ToList())
        {
            if (kept.Contains(GamePath.Normalize(key))) continue;
            redirects.Remove(key);
            removed++;
        }
        return removed;
    }

    /// <summary>
    /// Whether a manipulation is about the converted customization itself: its extra skeleton
    /// (Est), or a shape or attribute switch (Shp, Atr) for its ID and race. Anything about other
    /// items, or every hair or face of a race, is not part of it.
    /// </summary>
    public static bool IsAbout(JsonObject manipulation, CustomizationPathEndpoint target)
    {
        if (manipulation["Manipulation"] is not JsonObject fields) return false;
        var slot = target.Kind switch { AssetKind.Hair => "Hair", AssetKind.Face => "Face", _ => null };
        if (slot == null || !string.Equals(Json.GetString(fields["Slot"]), slot, StringComparison.OrdinalIgnoreCase)) return false;
        var type = Json.GetString(manipulation["Type"]) ?? string.Empty;
        if (type.Equals("Est", StringComparison.OrdinalIgnoreCase))
        {
            var (race, gender) = GenderRaces.Names(target.GenderRace);
            return Json.TryGetInt(fields["SetId"], out var setId) && setId == target.ModelId &&
                   string.Equals(Json.GetString(fields["Race"]), race, StringComparison.OrdinalIgnoreCase) &&
                   string.Equals(Json.GetString(fields["Gender"]), gender, StringComparison.OrdinalIgnoreCase);
        }
        if (type.Equals("Shp", StringComparison.OrdinalIgnoreCase) || type.Equals("Atr", StringComparison.OrdinalIgnoreCase))
            return Json.TryGetInt(fields["Id"], out var id) && id == target.ModelId &&
                   (!Json.TryGetInt(fields["GenderRaceCondition"], out var condition) || condition == 0 ||
                    condition == target.GenderRace);
        return false;
    }
}
