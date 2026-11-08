using System.Text.Json.Nodes;

namespace UniversalModConverter.Core;

/// <summary>
/// What converting one variant of a mod's gear item takes from it: the IMC entry the variant
/// shows (the mod's own in place of the game's), the entries the mod's options give it, and the
/// IMC groups that apply to it. Variants with the same look convert to the same thing, so the mod
/// scan lists them as one root.
/// </summary>
/// <param name="Entry">The variant's IMC entry, or null when neither the game nor the mod has one.</param>
/// <param name="Options">The entries the mod's options give the variant, by option.</param>
/// <param name="Groups">The indices of the mod's IMC groups that apply to the variant.</param>
public sealed record GearLook(ImcEntry? Entry, IReadOnlyList<(ContainerAddress Option, ImcEntry Entry)> Options, IReadOnlyList<int> Groups)
{
    /// <summary>
    /// Whether converting either variant makes the same: the same entry but for its sound, which a
    /// conversion takes from the target, the same option entries and the same IMC groups.
    /// </summary>
    public bool SameAs(GearLook other)
        => Silent(Entry) == Silent(other.Entry) && Options.SequenceEqual(other.Options) && Groups.SequenceEqual(other.Groups);

    private static ImcEntry? Silent(ImcEntry? entry) => entry is { } e ? e with { SoundId = 0 } : null;

    /// <summary>
    /// The look of <paramref name="item"/>'s variant (variant 0 counts as 1), resolved as
    /// <c>GearConversion</c> resolves the source variant's; <paramref name="vanilla"/> are the
    /// game's IMC rows of the item's set and slot.
    /// </summary>
    public static GearLook Of(PenumbraMod mod, IReadOnlyDictionary<ushort, ImcEntry> vanilla, GearItem item)
    {
        var variant = item.Variant > 0 ? item.Variant : (ushort)1;
        ImcEntry? entry = vanilla.TryGetValue(variant, out var row) ? row : null;

        var overridden = false;
        foreach (var manipulation in mod.Default.Manipulations?.OfType<JsonObject>() ?? [])
            if (GearManipulations.IsImcFor(manipulation, item, variant) &&
                ImcEntry.FromJson(manipulation["Manipulation"]?["Entry"]) is { } own)
            {
                entry = own;
                overridden = true;
            }

        var groups = Enumerable.Range(0, mod.Groups.Count)
            .Where(i => mod.Groups[i].IsImc && GearManipulations.ImcGroupMatches(mod.Groups[i].Node, item, variant))
            .ToList();
        // Without an entry of the mod's own, the first IMC group that sets a material sets the look.
        if (!overridden)
            foreach (var group in groups.Select(i => mod.Groups[i]))
                if (!(Json.TryGetBool(group.Node["OnlyAttributes"], out var only) && only) &&
                    ImcEntry.FromJson(group.Node["DefaultEntry"]) is { MaterialId: > 0 } groupEntry)
                {
                    entry = groupEntry;
                    break;
                }
        if (entry is { MaterialId: 0 } unset) entry = unset with { MaterialId = 1 };

        var options = new List<(ContainerAddress, ImcEntry)>();
        foreach (var container in mod.Containers.Where(c => !c.Address.IsDefault))
        foreach (var manipulation in container.Manipulations?.OfType<JsonObject>() ?? [])
            if (GearManipulations.IsImcFor(manipulation, item, variant) &&
                ImcEntry.FromJson(manipulation["Manipulation"]?["Entry"]) is { } optionEntry)
                options.Add((container.Address, optionEntry));

        return new GearLook(entry, options, groups);
    }
}
