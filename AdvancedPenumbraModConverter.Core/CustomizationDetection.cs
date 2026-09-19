namespace AdvancedPenumbraModConverter.Core;

/// <summary>Finds the hair, face, tail and Viera-ear roots a mod changes.</summary>
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
