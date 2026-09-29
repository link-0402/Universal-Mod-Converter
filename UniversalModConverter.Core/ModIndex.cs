namespace UniversalModConverter.Core;

/// <summary>
/// What a mod redirects, gathered once for questions asked of many roots: every game path of
/// every container, normalized, with the local file behind it, and the textures each replaced
/// material loads, read from disk at most once. A scan asks the same things of every root it finds.
/// </summary>
public sealed class ModIndex
{
    private readonly string _modDirectory;
    private readonly Dictionary<string, IReadOnlyList<string>> _materialTextures = new(StringComparer.OrdinalIgnoreCase);

    public ModIndex(PenumbraMod mod, string modDirectory)
    {
        _modDirectory = modDirectory;
        Redirects = RedirectsOf(mod).ToList();
        Redirected = Redirects.Select(r => r.GamePath).ToHashSet(StringComparer.Ordinal);
    }

    /// <summary>One redirect of one container; <see cref="Local"/> is null for a file swap.</summary>
    public readonly record struct Redirect(string GamePath, string? Local);

    /// <summary>Every redirect of every container, in container order.</summary>
    public IReadOnlyList<Redirect> Redirects { get; }

    /// <summary>Every game path the mod redirects anywhere.</summary>
    public IReadOnlySet<string> Redirected { get; }

    /// <summary>The redirects of <paramref name="mod"/>, for questions that need no file of it.</summary>
    public static IEnumerable<Redirect> RedirectsOf(PenumbraMod mod)
    {
        foreach (var container in mod.Containers)
        {
            foreach (var (key, local) in container.FileEntries()) yield return new Redirect(GamePath.Normalize(key), local);
            foreach (var (key, _) in container.SwapEntries()) yield return new Redirect(GamePath.Normalize(key), null);
        }
    }

    /// <summary>
    /// The texture paths, normalized, that the material at <paramref name="local"/> loads; none
    /// when the file is missing, unreadable, or not inside the mod folder.
    /// </summary>
    public IReadOnlyList<string> MaterialTextures(string local)
    {
        if (_materialTextures.TryGetValue(local, out var known)) return known;
        IReadOnlyList<string> textures = [];
        try
        {
            var full = PathSafety.ResolveRelative(_modDirectory, GamePath.ToLocal(local));
            if (File.Exists(full))
                textures = MtrlFile.ReadTexturePaths(File.ReadAllBytes(full)).Select(GamePath.Normalize).ToList();
        }
        catch (Exception ex) when (ex is IOException or InvalidDataException or UnauthorizedAccessException)
        {
            // An unreadable material loads nothing we can name.
        }
        return _materialTextures[local] = textures;
    }
}
