using System.Text.Json.Nodes;

namespace UniversalModConverter.Core;

/// <summary>Option groups and options the way the converter creates them.</summary>
public static class ModGroupBuilder
{
    public static JsonObject Option(string name, string description, JsonObject files, JsonObject? swaps = null,
        JsonNode? priority = null)
    {
        var option = new JsonObject
        {
            ["Id"] = Guid.NewGuid().ToString(),
            ["Name"] = name,
            ["Description"] = description,
        };
        if (priority != null) option["Priority"] = priority.DeepClone();
        option["Files"] = files;
        option["FileSwaps"] = swaps ?? new JsonObject();
        option["Manipulations"] = new JsonArray();
        return option;
    }

    /// <summary>Adds a new group to <paramref name="mod"/>; returns the line the preview shows for it.</summary>
    public static GearPlanChange Add(PenumbraMod mod, string name, string description, int priority, string type,
        JsonNode defaults, JsonArray options)
    {
        var node = new JsonObject
        {
            ["Id"] = Guid.NewGuid().ToString(),
            ["Name"] = name,
            ["Description"] = description,
            ["Priority"] = priority,
            ["Type"] = type,
            ["DefaultSettings"] = defaults,
            ["Options"] = options,
        };
        mod.Groups.Add(new ModGroup(node, mod.Groups.Count));
        return new GearPlanChange("Group", name, $"new {type.ToLowerInvariant()}-select group",
            $"{options.Count} option(s): " +
            string.Join(", ", options.OfType<JsonObject>().Select(o => Json.GetString(o["Name"]))));
    }

    /// <summary>The highest group priority of <paramref name="mod"/>; 0 when it has no groups.</summary>
    public static int TopPriority(PenumbraMod mod)
        => mod.Groups.Select(g => Json.GetInt(g.Node["Priority"], 0)).DefaultIfEmpty(0).Max();

    /// <summary>Penumbra identifies a group by name, so a new one needs its own: the name, then "name (2)" and so on.</summary>
    public static string UniqueName(PenumbraMod mod, string name)
    {
        var taken = mod.Groups.Select(g => g.Name).ToHashSet(StringComparer.OrdinalIgnoreCase);
        var candidate = name;
        for (var i = 2; taken.Contains(candidate); i++) candidate = $"{name} ({i})";
        return candidate;
    }
}
