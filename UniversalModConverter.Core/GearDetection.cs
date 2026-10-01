using System.Text.RegularExpressions;

namespace UniversalModConverter.Core;

/// <summary>Tells the gear a mod changes apart from files it only holds because another item loads them.</summary>
public static partial class GearDetection
{
    [GeneratedRegex(@"^chara/(?:equipment|accessory)/[ea]\d{4}/", RegexOptions.IgnoreCase | RegexOptions.CultureInvariant)]
    private static partial Regex RootRegex();

    /// <summary>
    /// Whether everything the mod holds for an item is loaded through another item's files, as a
    /// model that names a material under another set by its complete path does: the item is that
    /// model's dependency, not an item of its own, and converting the model brings its files
    /// along. <paramref name="keys"/> are the normalized game paths the mod redirects that carry
    /// the item's token. An item with a model of its own, or with any file nothing else loads (a
    /// retexture of a vanilla item, say), is the user's to convert.
    /// </summary>
    public static bool IsBorrowed(ModIndex index, IReadOnlyCollection<string> keys)
    {
        if (keys.Count == 0) return false;
        var roots = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (var key in keys)
        {
            if (!key.EndsWith(".mtrl", StringComparison.Ordinal) && !key.EndsWith(".tex", StringComparison.Ordinal)) return false;
            if (RootRegex().Match(key) is not { Success: true } root) return false;
            roots.Add(root.Value);
        }

        // What the rest of the mod loads: the materials its models name by complete path (a short
        // name stays in the model's own material folder, so it never reaches another item), and
        // the textures every one of its materials loads. The item's own root loads nothing for it.
        var loaded = new HashSet<string>(StringComparer.Ordinal);
        foreach (var redirect in index.Redirects)
        {
            if (redirect.Local == null || roots.Any(root => redirect.GamePath.StartsWith(root, StringComparison.OrdinalIgnoreCase))) continue;
            if (redirect.GamePath.EndsWith(".mdl", StringComparison.Ordinal))
            {
                foreach (var material in index.ModelMaterials(redirect.Local))
                {
                    if (!material.TrimStart('/').Contains('/')) continue;
                    var path = GamePath.Normalize(material);
                    loaded.Add(path);
                    if (index.LocalOf(path) is { } local) loaded.UnionWith(index.MaterialTextures(local));
                }
            }
            else if (redirect.GamePath.EndsWith(".mtrl", StringComparison.Ordinal))
                loaded.UnionWith(index.MaterialTextures(redirect.Local));
        }

        return keys.All(loaded.Contains);
    }
}
