using System.Text.Json.Nodes;

namespace AdvancedPenumbraModConverter.Core;

public static class ModGroupPruning
{
    /// <summary>
    /// Removes groups that ended up without data. Groups referenced by the conditions or
    /// parent links of kept groups stay, because Penumbra refuses mods with dangling GUIDs.
    /// </summary>
    public static void Prune(PenumbraMod result, Action<ModGroup>? dropped = null)
    {
        var keep = result.Groups.Where(g => !g.Node.ContainsKey("__apmc_drop") &&
                                            (g.IsImc || g.Containers.Any(c => !c.IsEmpty))).ToHashSet();
        var changed = true;
        while (changed)
        {
            changed = false;
            var referenced = new HashSet<Guid>();
            foreach (var group in keep) CollectReferences(group.Node, referenced, topLevel: true);
            foreach (var group in result.Groups)
            {
                if (keep.Contains(group)) continue;
                var ids = group.Options.Select(o => Json.GetString(o["Id"])).Append(Json.GetString(group.Node["Id"]));
                if (!ids.Any(id => Guid.TryParse(id, out var guid) && referenced.Contains(guid))) continue;
                keep.Add(group);
                changed = true;
            }
        }

        foreach (var group in result.Groups.Where(g => !keep.Contains(g)))
            dropped?.Invoke(group);
        result.Groups.RemoveAll(g => !keep.Contains(g));
        foreach (var group in result.Groups) group.Node.Remove("__apmc_drop");
    }

    private static void CollectReferences(JsonNode? node, HashSet<Guid> output, bool topLevel)
    {
        if (node is not JsonObject obj) return;
        if (Guid.TryParse(Json.GetString(obj["ParentSetting"]), out var parent)) output.Add(parent);
        CollectGuids(obj["Condition"], output);
        if (topLevel && obj["Options"] is JsonArray options)
            foreach (var option in options) CollectReferences(option, output, false);
    }

    private static void CollectGuids(JsonNode? node, HashSet<Guid> output)
    {
        switch (node)
        {
            case JsonObject obj:
                foreach (var (_, value) in obj) CollectGuids(value, output);
                break;
            case JsonArray array:
                foreach (var value in array) CollectGuids(value, output);
                break;
            case JsonValue value when Guid.TryParse(Json.GetString(value), out var guid):
                output.Add(guid);
                break;
        }
    }
}
