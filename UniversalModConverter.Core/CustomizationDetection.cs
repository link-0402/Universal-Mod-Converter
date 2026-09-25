namespace UniversalModConverter.Core;

/// <summary>Finds the hair, face, tail, Viera-ear and skin roots a mod changes.</summary>
public static class CustomizationDetection
{
    /// <summary>
    /// Returns every customization root the mod redirects game paths under. A root whose
    /// only mod content is textures that the mod's materials of another root load (a face 2
    /// material using a replaced face 1 mask, for example) is a dependency of that root,
    /// not a part of its own, and is left out.
    /// </summary>
    public static IReadOnlyList<CustomizationPathEndpoint> FindRoots(PenumbraMod mod, string modDirectory)
    {
        var keys = new Dictionary<CustomizationPathEndpoint, HashSet<string>>();
        var materials = new List<(string Key, string FullPath)>();
        foreach (var container in mod.Containers)
        {
            foreach (var (gamePath, local) in container.FileEntries().Select(e => (e.Key, (string?)e.Local))
                         .Concat(container.SwapEntries().Select(e => (e.Key, (string?)null))))
            {
                var normalized = GamePath.Normalize(gamePath);
                foreach (var endpoint in CustomizationPaths.FindEndpoints(normalized))
                {
                    if (!CustomizationKinds.Get(endpoint.Kind).SupportsRace(endpoint.GenderRace)) continue;
                    if (!keys.TryGetValue(endpoint, out var set)) keys[endpoint] = set = new(StringComparer.Ordinal);
                    set.Add(normalized);
                }
                if (local != null && normalized.EndsWith(".mtrl", StringComparison.Ordinal))
                    materials.Add((normalized, Path.Combine(modDirectory, GamePath.ToLocal(local))));
            }
        }

        return keys.Where(root => !IsBorrowedTextureRoot(root.Key, root.Value, materials))
            .Select(root => root.Key)
            .OrderBy(root => root.Kind).ThenBy(root => root.GenderRace).ThenBy(root => root.ModelId)
            .ToList();
    }

    /// <summary>
    /// True when the same files can simply be offered under other races or IDs: the mod replaces
    /// only textures under this root, or, for faces and skins, textures and materials. A texture
    /// has no paths inside it to retarget, and a face or skin material only points at textures,
    /// which a fan-out can follow (see <see cref="TextureFanOutPlanner"/>). Anything with a model
    /// has to be converted properly instead.
    /// </summary>
    public static bool CanFanOut(PenumbraMod mod, CustomizationPathEndpoint endpoint)
    {
        var allowed = endpoint.Kind is AssetKind.Face or AssetKind.Body
            ? AssetContents.Texture | AssetContents.Material
            : AssetContents.Texture;
        var contents = Contents(mod, endpoint);
        return contents != AssetContents.None && (contents & ~allowed) == AssetContents.None;
    }

    /// <summary>
    /// What converting this root touches, for the user: the kinds of files the mod redirects under
    /// it, plus textures its materials load that the mod also replaces (see <see cref="ModContents.Of"/>).
    /// </summary>
    public static AssetContents Affected(PenumbraMod mod, string modDirectory, CustomizationPathEndpoint endpoint)
        => ModContents.Of(mod, modDirectory, path => CustomizationPaths.Contains(path, endpoint));

    /// <summary>What kinds of files the mod redirects under this root, and nothing else.</summary>
    public static AssetContents Contents(PenumbraMod mod, CustomizationPathEndpoint endpoint)
    {
        var contents = AssetContents.None;
        foreach (var container in mod.Containers)
        foreach (var (gamePath, _) in container.FileEntries().Concat(container.SwapEntries()))
        {
            var normalized = GamePath.Normalize(gamePath);
            if (CustomizationPaths.Contains(normalized, endpoint)) contents |= AssetContentsExtensions.Of(normalized);
        }

        return contents;
    }

    private static bool IsBorrowedTextureRoot(CustomizationPathEndpoint endpoint, HashSet<string> keys,
        List<(string Key, string FullPath)> materials)
    {
        if (keys.Any(k => !k.EndsWith(".tex", StringComparison.Ordinal))) return false;
        var borrowed = new HashSet<string>(StringComparer.Ordinal);
        foreach (var (key, fullPath) in materials)
        {
            if (CustomizationPaths.Contains(key, endpoint)) continue;
            try
            {
                if (!File.Exists(fullPath)) continue;
                foreach (var texture in MtrlFile.ReadTexturePaths(File.ReadAllBytes(fullPath)))
                    borrowed.Add(GamePath.Normalize(texture));
            }
            catch (Exception ex) when (ex is IOException or InvalidDataException or UnauthorizedAccessException)
            {
                // An unreadable material cannot borrow anything.
            }
        }
        return keys.All(borrowed.Contains);
    }
}
