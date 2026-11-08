namespace UniversalModConverter.Core;

/// <summary>
/// The gear target filter "Free set": item sets whose free slots are exactly the ones a mod
/// fills. Gear and accessories are separate (their set ids are counted apart), facewear has no
/// sets, and both rings are one slot, as they share their models.
/// </summary>
public static class FreeSets
{
    /// <summary>The filter's slot for an item's: the left ring counts as the right one.</summary>
    public static GearSlot SlotOf(GearSlot slot) => slot == GearSlot.LFinger ? GearSlot.RFinger : slot;

    /// <summary>
    /// The slots a target's set must have free: those of the mod's gear roots
    /// (<paramref name="modSlots"/>, one per root) of the target's kind, with the source's slot
    /// moved to <paramref name="target"/>, where the conversion puts it.
    /// </summary>
    public static HashSet<GearSlot> Wanted(IEnumerable<GearSlot> modSlots, GearSlot source, GearSlot target)
    {
        var slots = modSlots
            .Where(s => s != GearSlot.Glasses && s.IsAccessory() == target.IsAccessory())
            .Select(SlotOf)
            .ToList();
        slots.Remove(SlotOf(source));
        slots.Add(SlotOf(target));
        return slots.ToHashSet();
    }

    /// <summary>
    /// The sets among <paramref name="items"/> (all of one kind) whose free slots are exactly
    /// <paramref name="wanted"/>: the set has an item in each of them and none of those is taken,
    /// and every other slot it has is taken. A slot is taken when any of its items is.
    /// </summary>
    public static HashSet<ushort> Find(IEnumerable<(GearSlot Slot, ushort SetId, bool Taken)> items, IReadOnlySet<GearSlot> wanted)
    {
        var found = new HashSet<ushort>();
        foreach (var set in items.GroupBy(i => i.SetId))
        {
            var taken = set.GroupBy(i => SlotOf(i.Slot)).ToDictionary(g => g.Key, g => g.Any(i => i.Taken));
            if (wanted.All(s => taken.TryGetValue(s, out var t) && !t) &&
                taken.All(s => wanted.Contains(s.Key) || s.Value))
                found.Add(set.Key);
        }
        return found;
    }
}
