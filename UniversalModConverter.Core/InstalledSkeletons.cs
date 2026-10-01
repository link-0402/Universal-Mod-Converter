using System.Text.Json;
using System.Text.Json.Nodes;

namespace UniversalModConverter.Core;

/// <param name="Option">The option the file is in, when its mod offers several skeletons for the race.</param>
public sealed record InstalledSkeleton(ushort Race, string Path, string Mod, string? Option = null);

/// <summary>
/// The race base skeletons a mod folder maps, read from its definition in any of the layouts
/// Penumbra has used: meta.json holding every container, or default_mod.json and group_*.json.
/// </summary>
public static class InstalledSkeletons
{
    private const long MaxDefinitionSize = 64L * 1024 * 1024;

    private static readonly JsonDocumentOptions JsonOptions = new()
    {
        AllowTrailingCommas = true,
        CommentHandling = JsonCommentHandling.Skip,
        // A property written twice only fails when the object is first used; refuse it while parsing.
        AllowDuplicateProperties = false,
    };

    /// <summary>
    /// A mod folder's definition files, and a stamp of their names, sizes and write times: while
    /// the stamp is the same, so is what they map.
    /// </summary>
    public static (List<string> Files, string Stamp) Definitions(string folder)
    {
        var files = Directory.EnumerateFiles(folder, "*.json", SearchOption.TopDirectoryOnly)
            .Where(IsDefinition).Order(StringComparer.OrdinalIgnoreCase).ToList();
        var stamp = string.Join("|", files.Select(file =>
        {
            var info = new FileInfo(file);
            return $"{info.Name}:{info.Length}:{info.LastWriteTimeUtc.Ticks}";
        }));
        return (files, stamp);
    }

    private static bool IsDefinition(string file)
    {
        var name = Path.GetFileName(file);
        return name.Equals("meta.json", StringComparison.OrdinalIgnoreCase) ||
               name.Equals("default_mod.json", StringComparison.OrdinalIgnoreCase) ||
               name.StartsWith("group_", StringComparison.OrdinalIgnoreCase);
    }

    /// <summary>
    /// Every existing file inside <paramref name="folder"/> that the definition files map to a
    /// race's base skeleton path, each named by its option only where the mod maps several files
    /// for the race: those are the choices it offers, such as YAS's "Posing". Files that cannot be
    /// read are reported to <paramref name="rejected"/> and skipped.
    /// </summary>
    public static List<InstalledSkeleton> Read(string folder, IEnumerable<string> definitions, string mod,
        Action<string, Exception>? rejected = null)
    {
        var found = new List<InstalledSkeleton>();
        foreach (var file in definitions)
        {
            try { found.AddRange(ReadDefinition(folder, file, mod)); }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or JsonException or ArgumentException)
            {
                rejected?.Invoke(file, ex);
            }
        }
        var several = found.GroupBy(f => f.Race)
            .Where(g => g.Select(f => f.Path).Distinct(StringComparer.OrdinalIgnoreCase).Count() > 1)
            .Select(g => g.Key).ToHashSet();
        return [.. found.Select(f => several.Contains(f.Race) ? f : f with { Option = null })];
    }

    private static List<InstalledSkeleton> ReadDefinition(string folder, string file, string mod)
    {
        var result = new List<InstalledSkeleton>();
        if (new FileInfo(file).Length > MaxDefinitionSize) return result;
        var bytes = File.ReadAllBytes(file);
        // Most mods replace no skeleton; those are not parsed at all.
        if (bytes.AsSpan().IndexOf("skeleton"u8) < 0) return result;

        var prefix = Path.GetFullPath(folder).TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar) +
                     Path.DirectorySeparatorChar;
        // Containers keep their files under "Files", options with a "Name" of their own.
        void Visit(JsonNode? node)
        {
            if (node is JsonArray array)
            {
                foreach (var item in array) Visit(item);
                return;
            }
            if (node is not JsonObject obj) return;
            foreach (var (key, value) in obj)
            {
                if (key != "Files" || value is not JsonObject files)
                {
                    Visit(value);
                    continue;
                }
                var option = obj["Name"] is JsonValue name && name.TryGetValue<string>(out var label) && label.Length > 0 ? label : null;
                foreach (var (gamePath, local) in files)
                {
                    if (!PapPath.TryParseBaseSkeleton(gamePath, out var race) || local is not JsonValue entry ||
                        !entry.TryGetValue<string>(out var relative) || string.IsNullOrWhiteSpace(relative) || Path.IsPathRooted(relative))
                        continue;
                    try
                    {
                        var full = Path.GetFullPath(Path.Combine(prefix, relative.Replace('\\', Path.DirectorySeparatorChar)));
                        if (full.StartsWith(prefix, StringComparison.OrdinalIgnoreCase) && File.Exists(full))
                            result.Add(new InstalledSkeleton(race, full, mod, option));
                    }
                    catch (Exception ex) when (ex is ArgumentException or NotSupportedException or PathTooLongException)
                    {
                        // A path this cannot use is simply not a candidate.
                    }
                }
            }
        }
        // Penumbra and the mod loader read these as text, which accepts a UTF-8 byte-order mark; the JSON reader does not.
        var json = bytes.AsSpan();
        if (json.StartsWith(new byte[] { 0xEF, 0xBB, 0xBF })) json = json[3..];
        Visit(JsonNode.Parse(json, documentOptions: JsonOptions));
        return result;
    }
}
