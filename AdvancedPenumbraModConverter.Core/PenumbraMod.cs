using System.Text;
using System.Text.Encodings.Web;
using System.Text.Json;
using System.Text.Json.Nodes;

namespace AdvancedPenumbraModConverter.Core;

public enum PenumbraModFormat
{
    /// <summary>FileVersion 3 and older: meta.json, default_mod.json and group_*.json files.</summary>
    Legacy,

    /// <summary>
    /// FileVersion 4 (Penumbra 1.7+): every container lives in meta.json under
    /// <c>DefaultData</c> and <c>Groups</c>; groups and options carry GUIDs.
    /// </summary>
    Unified,
}

/// <summary>Stable address of a data container: group index (-1 = default) and container index.</summary>
public readonly record struct ContainerAddress(int Group, int Index)
{
    public static ContainerAddress Default { get; } = new(-1, -1);
    public bool IsDefault => Group < 0;
}

/// <summary>
/// An editable, lossless view of a Penumbra mod definition. Unknown properties are kept
/// untouched; only the Files, FileSwaps and Manipulations of containers are interpreted.
/// </summary>
public sealed class PenumbraMod
{
    public const int UnifiedFileVersion = 4;
    public const string MetaFileName = "meta.json";
    public const string LegacyDefaultFileName = "default_mod.json";

    internal static readonly JsonDocumentOptions ReadOptions = new()
    {
        AllowTrailingCommas = true,
        CommentHandling = JsonCommentHandling.Skip,
    };

    private static readonly JsonWriterOptions WriteOptions = new()
    {
        Indented = true,
        Encoder = JavaScriptEncoder.UnsafeRelaxedJsonEscaping,
    };

    private PenumbraMod(PenumbraModFormat format, JsonObject meta, JsonObject defaultNode, List<ModGroup> groups)
    {
        Format = format;
        Meta = meta;
        Groups = groups;
        Default = new ModContainer(defaultNode, null, ContainerAddress.Default);
    }

    public PenumbraModFormat Format { get; }

    /// <summary>meta.json without the DefaultData and Groups properties.</summary>
    public JsonObject Meta { get; }

    public ModContainer Default { get; }

    public List<ModGroup> Groups { get; }

    public int FileVersion => Json.GetInt(Meta["FileVersion"], 0);

    public string Name => Json.GetString(Meta["Name"]) ?? string.Empty;

    /// <summary>Default container followed by every option/combination container.</summary>
    public IEnumerable<ModContainer> Containers => Groups.SelectMany(g => g.Containers).Prepend(Default);

    public ModContainer GetContainer(ContainerAddress address)
        => address.IsDefault ? Default : Groups[address.Group].Containers[address.Index];

    // ── Loading ──────────────────────────────────────────────────────────────

    public static bool IsModDirectory(string directory, out string error)
    {
        error = string.Empty;
        if (!System.IO.Directory.Exists(directory))
        {
            error = $"Directory not found: {directory}";
            return false;
        }

        if (!File.Exists(Path.Combine(directory, MetaFileName)))
        {
            error = $"No {MetaFileName} found in {directory}; this is not a Penumbra mod folder.";
            return false;
        }

        return true;
    }

    public static PenumbraMod Load(string directory)
    {
        var metaPath = Path.Combine(directory, MetaFileName);
        var meta = ParseObject(metaPath);
        var version = Json.GetInt(meta["FileVersion"], 0);
        if (version > UnifiedFileVersion)
            throw new InvalidDataException(
                $"Mod metadata version {version} is newer than the supported version {UnifiedFileVersion}.");

        if (version >= UnifiedFileVersion)
        {
            var defaultNode = Detach(meta, "DefaultData") as JsonObject ?? new JsonObject();
            var groups = new List<ModGroup>();
            if (Detach(meta, "Groups") is JsonArray array)
            {
                var nodes = array.ToList();
                array.Clear();
                foreach (var node in nodes)
                    if (node is JsonObject group)
                        groups.Add(new ModGroup(group, groups.Count, null));
            }

            return new PenumbraMod(PenumbraModFormat.Unified, meta, defaultNode, groups);
        }

        var defaultPath = Path.Combine(directory, LegacyDefaultFileName);
        var legacyDefault = File.Exists(defaultPath) ? ParseObject(defaultPath) : new JsonObject();
        var legacyGroups = new List<ModGroup>();
        foreach (var file in LegacyGroupFiles(directory))
            legacyGroups.Add(new ModGroup(ParseObject(file), legacyGroups.Count, Path.GetFileName(file)));
        return new PenumbraMod(PenumbraModFormat.Legacy, meta, legacyDefault, legacyGroups);
    }

    /// <summary>The JSON files that define this mod's data (used for fingerprinting).</summary>
    public static IReadOnlyList<string> DefinitionFiles(string directory)
    {
        var meta = Path.Combine(directory, MetaFileName);
        var files = new List<string> { meta };
        if (File.Exists(meta))
        {
            try
            {
                if (Json.GetInt(ParseObject(meta)["FileVersion"], 0) >= UnifiedFileVersion) return files;
            }
            catch (Exception) { /* malformed meta: include the legacy files as well */ }
        }

        var defaultPath = Path.Combine(directory, LegacyDefaultFileName);
        if (File.Exists(defaultPath)) files.Add(defaultPath);
        files.AddRange(LegacyGroupFiles(directory));
        return files;
    }

    private static IEnumerable<string> LegacyGroupFiles(string directory)
        => System.IO.Directory.EnumerateFiles(directory, "group_*.json", SearchOption.TopDirectoryOnly)
            .OrderBy(Path.GetFileName, StringComparer.OrdinalIgnoreCase);

    internal static JsonObject ParseObject(string path)
    {
        JsonNode? node;
        try
        {
            node = JsonNode.Parse(File.ReadAllText(path), new JsonNodeOptions(), ReadOptions);
        }
        catch (JsonException ex)
        {
            throw new InvalidDataException($"{Path.GetFileName(path)} is not valid JSON: {ex.Message}", ex);
        }

        return node as JsonObject ?? throw new InvalidDataException($"{Path.GetFileName(path)} must contain a JSON object.");
    }

    private static JsonNode? Detach(JsonObject obj, string property)
    {
        if (!obj.TryGetPropertyValue(property, out var value)) return null;
        obj.Remove(property);
        return value;
    }

    // ── Construction / cloning ──────────────────────────────────────────────

    /// <summary>A deep copy whose containers have the same addresses as this mod's.</summary>
    public PenumbraMod Clone()
    {
        var groups = Groups.Select((g, i) => new ModGroup((JsonObject)g.Node.DeepClone(), i, g.LegacyFileName)).ToList();
        return new PenumbraMod(Format, (JsonObject)Meta.DeepClone(), (JsonObject)Default.Node.DeepClone(), groups);
    }

    /// <summary>
    /// A copy of this mod with the same groups and options but empty containers. Used as
    /// the skeleton for a new mod so option IDs, conditions and default settings survive.
    /// </summary>
    public PenumbraMod CloneStructure()
    {
        var clone = Clone();
        foreach (var container in clone.Containers) container.Clear();
        return clone;
    }

    // ── Saving ──────────────────────────────────────────────────────────────

    /// <summary>Writes the definition files for this mod into <paramref name="directory"/>.</summary>
    public void Save(string directory)
    {
        if (Format == PenumbraModFormat.Unified)
        {
            WriteJson(Path.Combine(directory, MetaFileName), writer =>
            {
                writer.WriteStartObject();
                foreach (var (key, value) in Meta)
                {
                    writer.WritePropertyName(key);
                    WriteNode(writer, value);
                }

                if (!Default.IsEmpty)
                {
                    writer.WritePropertyName("DefaultData");
                    Default.Node.WriteTo(writer);
                }

                if (Groups.Count > 0)
                {
                    writer.WriteStartArray("Groups");
                    foreach (var group in Groups) group.Node.WriteTo(writer);
                    writer.WriteEndArray();
                }

                writer.WriteEndObject();
            });
            return;
        }

        WriteJson(Path.Combine(directory, MetaFileName), w => Meta.WriteTo(w));
        WriteJson(Path.Combine(directory, LegacyDefaultFileName), w => Default.Node.WriteTo(w));

        var written = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        for (var i = 0; i < Groups.Count; i++)
        {
            var group = Groups[i];
            group.LegacyFileName ??= LegacyGroupFileName(i, group.Name);
            if (!written.Add(group.LegacyFileName))
                group.LegacyFileName = LegacyGroupFileName(i, group.Name + "_" + i);
            written.Add(group.LegacyFileName);
            WriteJson(Path.Combine(directory, group.LegacyFileName), w => group.Node.WriteTo(w));
        }

        // Penumbra loads every group_*.json file, so stale ones must not survive.
        foreach (var stale in LegacyGroupFiles(directory).Where(f => !written.Contains(Path.GetFileName(f))).ToList())
            File.Delete(stale);
    }

    /// <summary>Renumbers legacy group files sequentially (Penumbra orders groups by file name).</summary>
    public void RenumberLegacyGroupFiles()
    {
        for (var i = 0; i < Groups.Count; i++)
            Groups[i].LegacyFileName = LegacyGroupFileName(i, Groups[i].Name);
    }

    public static string LegacyGroupFileName(int index, string name)
    {
        var invalid = Path.GetInvalidFileNameChars();
        var builder = new StringBuilder();
        foreach (var c in name.ToLowerInvariant())
            builder.Append(Array.IndexOf(invalid, c) >= 0 || char.IsControl(c) ? '_' : c);
        var safe = builder.ToString().Trim(' ', '.');
        if (safe.Length == 0) safe = "group";
        return $"group_{index + 1:D3}_{safe}.json";
    }

    private static void WriteNode(Utf8JsonWriter writer, JsonNode? value)
    {
        if (value == null) writer.WriteNullValue();
        else value.WriteTo(writer);
    }

    private static void WriteJson(string path, Action<Utf8JsonWriter> write)
    {
        using var stream = new MemoryStream();
        using (var writer = new Utf8JsonWriter(stream, WriteOptions))
            write(writer);
        File.WriteAllBytes(path, stream.ToArray());
    }

    public static string Serialize(JsonNode node)
    {
        using var stream = new MemoryStream();
        using (var writer = new Utf8JsonWriter(stream, WriteOptions))
            node.WriteTo(writer);
        return Encoding.UTF8.GetString(stream.ToArray());
    }
}

public sealed class ModGroup
{
    internal ModGroup(JsonObject node, int index, string? legacyFileName)
    {
        Node = node;
        Index = index;
        LegacyFileName = legacyFileName;
        var list = new List<ModContainer>();
        var array = IsCombining ? node["Containers"] as JsonArray
            : IsImc ? null
            : node["Options"] as JsonArray;
        if (array != null)
            for (var i = 0; i < array.Count; i++)
                if (array[i] is JsonObject container)
                    list.Add(new ModContainer(container, this, new ContainerAddress(index, list.Count)));
        Containers = list;
    }

    public JsonObject Node { get; }

    public int Index { get; }

    public string? LegacyFileName { get; set; }

    public string Type => Json.GetString(Node["Type"]) ?? "Single";

    public string Name => Json.GetString(Node["Name"]) ?? string.Empty;

    public bool IsImc => Type.Equals("Imc", StringComparison.OrdinalIgnoreCase);

    public bool IsCombining => Type.Equals("Combining", StringComparison.OrdinalIgnoreCase);

    /// <summary>
    /// The data containers of this group: options for Single/Multi groups, the 2^n
    /// combination containers for Combining groups, and none for IMC groups.
    /// </summary>
    public IReadOnlyList<ModContainer> Containers { get; }

    public IEnumerable<JsonObject> Options
        => (Node["Options"] as JsonArray)?.OfType<JsonObject>() ?? [];
}

public sealed class ModContainer
{
    internal ModContainer(JsonObject node, ModGroup? group, ContainerAddress address)
    {
        Node = node;
        Group = group;
        Address = address;
    }

    public JsonObject Node { get; }

    public ModGroup? Group { get; }

    public ContainerAddress Address { get; }

    public string Label
    {
        get
        {
            if (Group == null) return "Default";
            var name = Json.GetString(Node["Name"]);
            return $"{Group.Name} / {(string.IsNullOrEmpty(name) ? $"#{Address.Index + 1}" : name)}";
        }
    }

    public JsonObject? Files => Node["Files"] as JsonObject;

    public JsonObject? FileSwaps => Node["FileSwaps"] as JsonObject;

    public JsonArray? Manipulations => Node["Manipulations"] as JsonArray;

    public bool IsEmpty => (Files?.Count ?? 0) == 0 && (FileSwaps?.Count ?? 0) == 0 && (Manipulations?.Count ?? 0) == 0;

    public JsonObject GetOrCreateFiles() => GetOrCreate("Files", () => new JsonObject());

    public JsonObject GetOrCreateFileSwaps() => GetOrCreate("FileSwaps", () => new JsonObject());

    public JsonArray GetOrCreateManipulations() => GetOrCreate("Manipulations", () => new JsonArray());

    public void Clear()
    {
        Node.Remove("Files");
        Node.Remove("FileSwaps");
        Node.Remove("Manipulations");
    }

    /// <summary>Iterates Files entries as (key, relative local path).</summary>
    public IEnumerable<(string Key, string Local)> FileEntries()
    {
        if (Files is not { } files) yield break;
        foreach (var (key, value) in files)
            if (Json.GetString(value) is { Length: > 0 } local)
                yield return (key, local);
    }

    public IEnumerable<(string Key, string Target)> SwapEntries()
    {
        if (FileSwaps is not { } swaps) yield break;
        foreach (var (key, value) in swaps)
            if (Json.GetString(value) is { Length: > 0 } target)
                yield return (key, target);
    }

    private T GetOrCreate<T>(string property, Func<T> create) where T : JsonNode
    {
        if (Node[property] is T existing) return existing;
        var created = create();
        Node[property] = created;
        return created;
    }
}

/// <summary>Normalization shared by every game-path comparison.</summary>
public static class GamePath
{
    public static string Normalize(string path)
        => path.Replace('\\', '/').Trim().TrimStart('/').ToLowerInvariant();

    /// <summary>Normalizes a mod-relative local path for case-insensitive comparisons.</summary>
    public static string NormalizeLocal(string path)
    {
        var normalized = path.Replace('/', '\\').Trim();
        while (normalized.StartsWith(".\\", StringComparison.Ordinal)) normalized = normalized[2..];
        return normalized.TrimStart('\\').ToLowerInvariant();
    }

    /// <summary>Local paths are written the way Penumbra writes them on Windows.</summary>
    public static string ToLocal(string path) => path.Replace('/', '\\').TrimStart('\\');
}
