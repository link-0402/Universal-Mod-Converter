using System.Text.Json.Nodes;

namespace UniversalModConverter.Core;

public static class ModGroupPruning
{
    /// <summary>
    /// Removes groups that ended up without data. An IMC group holds no files but changes an
    /// item's metadata, so it stays only when a conversion retargeted it: one still exactly as
    /// it is in <paramref name="source"/> is about an item the new mod does not carry. Deciding
    /// that here, once every conversion of a run has planned, leaves the groups untouched while
    /// they plan, so no conversion appears to change a group it has no part in. Groups
    /// referenced by the conditions or parent links of kept groups stay, because Penumbra
    /// refuses mods with dangling GUIDs.
    /// </summary>
    public static void Prune(PenumbraMod result, PenumbraMod source, Action<ModGroup>? dropped = null)
    {
        var untouched = source.Groups.Where(g => g.IsImc).Select(g => PenumbraMod.Serialize(g.Node))
            .ToHashSet(StringComparer.Ordinal);
        var keep = result.Groups.Where(g => g.IsImc
            ? !untouched.Contains(PenumbraMod.Serialize(g.Node))
            : g.Containers.Any(c => !c.IsEmpty)).ToHashSet();
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
