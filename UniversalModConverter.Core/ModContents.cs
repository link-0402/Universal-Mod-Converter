namespace UniversalModConverter.Core;

/// <summary>What a mod replaces for one root, for telling the user what converting it touches.</summary>
public static class ModContents
{
    /// <summary>
    /// Every kind of file among the game paths <paramref name="owns"/> accepts, plus textures a
    /// replaced material loads that the mod also replaces. Converting or fanning out the material
    /// brings those along, even when they live outside the root (a skin material loading
    /// <c>chara/bibo_*.tex</c>, say).
    /// </summary>
    /// <param name="owns">Whether a normalized game path belongs to the root.</param>
    public static AssetContents Of(PenumbraMod mod, string modDirectory, Func<string, bool> owns)
    {
        var contents = AssetContents.None;
        var materials = new HashSet<string>(StringComparer.Ordinal);
        foreach (var container in mod.Containers)
        foreach (var key in container.FileEntries().Select(e => e.Key).Concat(container.SwapEntries().Select(e => e.Key)))
        {
            var normalized = GamePath.Normalize(key);
            if (!owns(normalized)) continue;
            contents |= AssetContentsExtensions.Of(normalized);
            if (normalized.EndsWith(".mtrl", StringComparison.Ordinal)) materials.Add(normalized);
        }
        if (materials.Count == 0 || contents.HasFlag(AssetContents.Texture)) return contents;

        var redirected = mod.Containers
            .SelectMany(c => c.FileEntries().Select(e => e.Key).Concat(c.SwapEntries().Select(e => e.Key)))
            .Select(GamePath.Normalize)
            .ToHashSet(StringComparer.Ordinal);
        foreach (var container in mod.Containers)
        foreach (var (key, local) in container.FileEntries())
        {
            if (!materials.Contains(GamePath.Normalize(key))) continue;
            try
            {
                var full = PathSafety.ResolveRelative(modDirectory, GamePath.ToLocal(local));
                if (File.Exists(full) &&
                    MtrlFile.ReadTexturePaths(File.ReadAllBytes(full)).Any(t => redirected.Contains(GamePath.Normalize(t))))
                    return contents | AssetContents.Texture;
            }
            catch (Exception ex) when (ex is IOException or InvalidDataException or UnauthorizedAccessException)
            {
                // An unreadable material loads nothing we can name.
            }
        }
        return contents;
    }
}
