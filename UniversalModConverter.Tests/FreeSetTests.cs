using UniversalModConverter.Core;

/// <summary>The gear target filter that lists only sets with exactly the mod's slots free.</summary>
internal static class FreeSetTests
{
    public static (string Name, Action Run)[] All =>
    [
        ("Free set wants the mod's slots of the target's kind", WantedSlots),
        ("Free set finds sets with exactly those slots free", FindSets),
    ];

    private static void WantedSlots()
    {
        GearSlot[] mod = [GearSlot.Body, GearSlot.Legs, GearSlot.Ears, GearSlot.Glasses];
        Assert.True(FreeSets.Wanted(mod, GearSlot.Body, GearSlot.Body).SetEquals([GearSlot.Body, GearSlot.Legs]), "same slot");
        // The source's slot moves to the one it is converted to.
        Assert.True(FreeSets.Wanted(mod, GearSlot.Body, GearSlot.Hands).SetEquals([GearSlot.Hands, GearSlot.Legs]), "other slot");
        // A second root in the source's slot still wants it.
        Assert.True(FreeSets.Wanted([GearSlot.Body, GearSlot.Body], GearSlot.Body, GearSlot.Hands).SetEquals([GearSlot.Body, GearSlot.Hands]),
            "second root");
        // Accessories count apart from gear, and both rings are one slot.
        Assert.True(FreeSets.Wanted(mod, GearSlot.Ears, GearSlot.LFinger).SetEquals([GearSlot.RFinger]), "accessory");
        Assert.True(FreeSets.Wanted(mod, GearSlot.Body, GearSlot.Neck).SetEquals([GearSlot.Ears, GearSlot.Neck]), "gear to accessory");
    }

    private static void FindSets()
    {
        (GearSlot, ushort, bool)[] items =
        [
            // 1: body and legs free, the rest modded.
            (GearSlot.Body, 1, false), (GearSlot.Legs, 1, false), (GearSlot.Head, 1, true), (GearSlot.Feet, 1, true),
            // 2: hands free too.
            (GearSlot.Body, 2, false), (GearSlot.Legs, 2, false), (GearSlot.Hands, 2, false),
            // 3: one legs variant modded.
            (GearSlot.Body, 3, false), (GearSlot.Legs, 3, false), (GearSlot.Legs, 3, true), (GearSlot.Head, 3, true),
            // 4: no legs.
            (GearSlot.Body, 4, false), (GearSlot.Head, 4, true),
            // 5: nothing but body and legs, both free.
            (GearSlot.Body, 5, false), (GearSlot.Legs, 5, false),
        ];
        var found = FreeSets.Find(items, new HashSet<GearSlot> { GearSlot.Body, GearSlot.Legs });
        Assert.True(found.SetEquals(new ushort[] { 1, 5 }), string.Join(", ", found));

        (GearSlot, ushort, bool)[] rings = [(GearSlot.RFinger, 7, false), (GearSlot.LFinger, 7, false), (GearSlot.Ears, 7, true)];
        Assert.True(FreeSets.Find(rings, new HashSet<GearSlot> { GearSlot.RFinger }).SetEquals(new ushort[] { 7 }), "rings");
    }
}
