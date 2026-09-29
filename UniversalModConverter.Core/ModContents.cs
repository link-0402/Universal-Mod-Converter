namespace UniversalModConverter.Core;

/// <summary>What a mod replaces for one root, for telling the user what converting it touches.</summary>
public static class ModContents
{
    /// <inheritdoc cref="Of(ModIndex, Func{string, bool})"/>
    public static AssetContents Of(PenumbraMod mod, string modDirectory, Func<string, bool> owns)
        => Of(new ModIndex(mod, modDirectory), owns);

    /// <summary>
    /// Every kind of file among the game paths <paramref name="owns"/> accepts, plus textures a
    /// replaced material loads that the mod also replaces. Converting or fanning out the material
    /// brings those along, even when they live outside the root (a skin material loading
    /// <c>chara/bibo_*.tex</c>, say).
    /// </summary>
    /// <param name="owns">Whether a normalized game path belongs to the root.</param>
    public static AssetContents Of(ModIndex index, Func<string, bool> owns)
    {
        var contents = AssetContents.None;
        var materials = new HashSet<string>(StringComparer.Ordinal);
        foreach (var redirect in index.Redirects)
        {
            if (!owns(redirect.GamePath)) continue;
            contents |= AssetContentsExtensions.Of(redirect.GamePath);
            if (redirect.GamePath.EndsWith(".mtrl", StringComparison.Ordinal)) materials.Add(redirect.GamePath);
        }
        if (materials.Count == 0 || contents.HasFlag(AssetContents.Texture)) return contents;

        foreach (var redirect in index.Redirects)
            if (redirect.Local != null && materials.Contains(redirect.GamePath) &&
                index.MaterialTextures(redirect.Local).Any(index.Redirected.Contains))
                return contents | AssetContents.Texture;
        return contents;
    }
}
