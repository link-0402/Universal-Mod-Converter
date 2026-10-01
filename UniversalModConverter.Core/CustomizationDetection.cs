namespace UniversalModConverter.Core;

/// <summary>Finds the hair, face, tail, Viera ear and skin roots a mod changes.</summary>
public static class CustomizationDetection
{
    /// <inheritdoc cref="FindRoots(ModIndex)"/>
    public static IReadOnlyList<CustomizationPathEndpoint> FindRoots(PenumbraMod mod, string modDirectory)
        => FindRoots(new ModIndex(mod, modDirectory));

    /// <summary>
    /// Returns every customization root the mod redirects game paths under, an Au Ra tail's
    /// Xaela material root counting as part of its tail (see <see cref="CustomizationPaths.Owner"/>). A root whose
    /// only mod content is textures that the mod's materials of another root load (a face 2
    /// material using a replaced face 1 mask, for example) is a dependency of that root,
    /// not a part of its own, and is left out.
    /// </summary>
    public static IReadOnlyList<CustomizationPathEndpoint> FindRoots(ModIndex index)
    {
        var keys = new Dictionary<CustomizationPathEndpoint, HashSet<string>>();
        foreach (var redirect in index.Redirects)
        foreach (var found in CustomizationPaths.FindEndpoints(redirect.GamePath))
        {
            if (!CustomizationKinds.Get(found.Kind).SupportsRace(found.GenderRace)) continue;
            var endpoint = CustomizationPaths.Owner(found);
            if (!keys.TryGetValue(endpoint, out var set)) keys[endpoint] = set = new(StringComparer.Ordinal);
            set.Add(redirect.GamePath);
        }

        return keys.Where(root => !IsBorrowedRoot(index, root.Key, root.Value))
            .Select(root => root.Key)
            .OrderBy(root => root.Kind).ThenBy(root => root.GenderRace).ThenBy(root => root.ModelId)
            .ToList();
    }

    /// <inheritdoc cref="CanFanOut(ModIndex, CustomizationPathEndpoint)"/>
    public static bool CanFanOut(PenumbraMod mod, CustomizationPathEndpoint endpoint)
        => CanFanOut(Contents(mod, endpoint), endpoint);

    /// <summary>
    /// True when the same files can simply be offered under other races or IDs: the mod replaces
    /// only textures under this root, or, for faces and skins, textures and materials. A texture
    /// has no paths inside it to retarget, and a face or skin material only points at textures,
    /// which a fan-out can follow (see <see cref="TextureFanOutPlanner"/>). Anything with a model
    /// has to be converted properly instead.
    /// </summary>
    public static bool CanFanOut(ModIndex index, CustomizationPathEndpoint endpoint)
        => CanFanOut(Contents(index, endpoint), endpoint);

    private static bool CanFanOut(AssetContents contents, CustomizationPathEndpoint endpoint)
    {
        var allowed = endpoint.Kind is AssetKind.Face or AssetKind.Body
            ? AssetContents.Texture | AssetContents.Material
            : AssetContents.Texture;
        return contents != AssetContents.None && (contents & ~allowed) == AssetContents.None;
    }

    /// <inheritdoc cref="Affected(ModIndex, CustomizationPathEndpoint)"/>
    public static AssetContents Affected(PenumbraMod mod, string modDirectory, CustomizationPathEndpoint endpoint)
        => Affected(new ModIndex(mod, modDirectory), endpoint);

    /// <summary>
    /// What converting this root touches, for the user: the kinds of files the mod redirects under
    /// it, the materials its models load that the mod keeps under another root (a hair's shared
    /// material root), and the textures those materials load (see <see cref="ModContents.Of(ModIndex, Func{string, bool})"/>).
    /// </summary>
    public static AssetContents Affected(ModIndex index, CustomizationPathEndpoint endpoint)
    {
        var loaded = endpoint.Kind == AssetKind.Body
            ? new HashSet<string>(StringComparer.Ordinal)
            : MaterialsOfModels(index, path => CustomizationPaths.Owns(path, endpoint)).ToHashSet(StringComparer.Ordinal);
        return ModContents.Of(index, path => CustomizationPaths.Owns(path, endpoint) || loaded.Contains(path));
    }

    /// <summary>What kinds of files the mod redirects under this root, and nothing else.</summary>
    public static AssetContents Contents(PenumbraMod mod, CustomizationPathEndpoint endpoint)
        => Contents(ModIndex.RedirectsOf(mod), endpoint);

    /// <inheritdoc cref="Contents(PenumbraMod, CustomizationPathEndpoint)"/>
    public static AssetContents Contents(ModIndex index, CustomizationPathEndpoint endpoint)
        => Contents(index.Redirects, endpoint);

    private static AssetContents Contents(IEnumerable<ModIndex.Redirect> redirects, CustomizationPathEndpoint endpoint)
    {
        var contents = AssetContents.None;
        foreach (var redirect in redirects)
            if (CustomizationPaths.Owns(redirect.GamePath, endpoint))
                contents |= AssetContentsExtensions.Of(redirect.GamePath);
        return contents;
    }

    /// <summary>
    /// Whether everything the mod holds under this root is loaded through another root: textures
    /// that other roots' materials load, and materials (with their textures) that other roots'
    /// models load, such as the shared material root a Miqo'te hair loads from the Midlander one.
    /// A root holding anything else, or anything nothing else loads, is the user's to convert.
    /// </summary>
    private static bool IsBorrowedRoot(ModIndex index, CustomizationPathEndpoint endpoint, HashSet<string> keys)
    {
        if (keys.Any(k => !k.EndsWith(".tex", StringComparison.Ordinal) && !k.EndsWith(".mtrl", StringComparison.Ordinal)))
            return false;
        var borrowed = new HashSet<string>(StringComparer.Ordinal);
        foreach (var redirect in index.Redirects)
        {
            if (redirect.Local == null || !redirect.GamePath.EndsWith(".mtrl", StringComparison.Ordinal) ||
                CustomizationPaths.Owns(redirect.GamePath, endpoint)) continue;
            borrowed.UnionWith(index.MaterialTextures(redirect.Local));
        }
        // Gear models load skin materials by short name; only a customization's own models count.
        if (endpoint.Kind != AssetKind.Body)
            foreach (var material in MaterialsOfModels(index, path => !CustomizationPaths.Owns(path, endpoint)))
            {
                borrowed.Add(material);
                if (index.LocalOf(material) is { } local) borrowed.UnionWith(index.MaterialTextures(local));
            }
        return keys.All(borrowed.Contains);
    }

    /// <summary>
    /// The materials the mod redirects that the customization models <paramref name="models"/>
    /// accepts load, resolved as the game does: a complete path as it is, a short name in the
    /// model's own material root (see <see cref="CustomizationPaths.MaterialPaths"/>).
    /// </summary>
    private static IEnumerable<string> MaterialsOfModels(ModIndex index, Func<string, bool> models)
    {
        foreach (var redirect in index.Redirects)
        {
            if (redirect.Local == null || !redirect.GamePath.EndsWith(".mdl", StringComparison.Ordinal) ||
                !models(redirect.GamePath)) continue;
            var owners = CustomizationPaths.FindEndpoints(redirect.GamePath)
                .Select(CustomizationPaths.Owner).Where(e => e.Kind != AssetKind.Body).Distinct().ToList();
            if (owners.Count == 0) continue;
            foreach (var reference in index.ModelMaterials(redirect.Local))
            foreach (var owner in owners)
            foreach (var path in CustomizationPaths.MaterialPaths(owner, reference))
            {
                var key = GamePath.Normalize(path);
                if (index.Redirected.Contains(key)) yield return key;
            }
        }
    }
}
