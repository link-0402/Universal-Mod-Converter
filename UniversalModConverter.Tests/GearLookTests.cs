using System.Text.Json.Nodes;
using UniversalModConverter.Core;

/// <summary>Which variants of a mod's gear item convert to the same thing, and so are one root.</summary>
internal static class GearLookTests
{
    public static (string Name, Action Run)[] All =>
    [
        ("A variant the mod points at another's look is one root with it", ModMakesSame),
        ("Variants that look different stay apart", DifferentStayApart),
        ("An option that changes one variant keeps it apart", OptionKeepsApart),
    ];

    private static readonly GearItem Legs = new(GearSlot.Legs, 245, 1);

    // Variant 1 shows material 1, variant 2 material 2, and variant 2 has a sound of its own.
    private static readonly Dictionary<ushort, ImcEntry> Vanilla = new()
    {
        [1] = new ImcEntry(1, 0, 0x3FF, 0, 0, 0),
        [2] = new ImcEntry(2, 0, 0x3FF, 5, 0, 0),
    };

    private static string Imc(int variant, int material)
        => $$$"""{"Type":"Imc","Manipulation":{"Entry":{"MaterialId":{{{material}}},"DecalId":0,"VfxId":0,"MaterialAnimationId":0,"AttributeMask":1023,"SoundId":0},"PrimaryId":245,"SecondaryId":0,"Variant":{{{variant}}},"ObjectType":"Equipment","EquipSlot":"Legs","BodySlot":"Unknown"}}""";

    private static PenumbraMod Mod(string defaultManipulations, string groups = "[]")
        => PenumbraMod.FromJson(JsonNode.Parse(
            $$$"""{"FileVersion":4,"Name":"Shorts","DefaultData":{"Files":{},"FileSwaps":{},"Manipulations":[{{{defaultManipulations}}}]},"Groups":{{{groups}}}}""")!.AsObject());

    private static bool Same(PenumbraMod mod)
        => GearLook.Of(mod, Vanilla, Legs).SameAs(GearLook.Of(mod, Vanilla, Legs with { Variant = 2 }));

    private static void ModMakesSame()
        // The sound stays the target's in a conversion, so it does not tell variants apart.
        => Assert.True(Same(Mod(Imc(2, 1))), "variant 2 shows material 1 like variant 1");

    private static void DifferentStayApart()
    {
        Assert.True(!Same(Mod(string.Empty)), "the game's own looks differ");
        Assert.True(!Same(Mod(Imc(2, 3))), "the mod gives variant 2 a third look");
    }

    private static void OptionKeepsApart()
    {
        var groups = $$$"""[{"Name":"Colour","Type":"Single","DefaultSettings":0,"Options":[{"Name":"Red","Files":{},"Manipulations":[{{{Imc(2, 4)}}}]}]}]""";
        Assert.True(!Same(Mod(Imc(2, 1), groups)), "an option gives variant 2 material 4");
    }
}
