using UniversalModConverter.Core;

/// <summary>Penumbra's changed-item names for hair, faces, tails, ears and skins.</summary>
internal static class ChangedItemKeyTests
{
    public static (string Name, Action Run)[] All =>
    [
        ("Changed-item customization names parse", CustomizationNames),
        ("Changed-item names that are not player customizations are rejected", OtherNames),
    ];

    private static ChangedCustomization Parse(string key)
    {
        Assert.True(ChangedItemKeys.TryParseCustomization(key, out var customization), key);
        return customization;
    }

    private static void CustomizationNames()
    {
        Assert.Equal(new ChangedCustomization(AssetKind.Hair, 101, 5), Parse("Customization: Midlander Male Hair 5"));
        Assert.Equal(new ChangedCustomization(AssetKind.Face, 1401, 1), Parse("Customization: Au Ra Female Face 1"));
        Assert.Equal(new ChangedCustomization(AssetKind.Face, 1401, 1), Parse("Customization: Au Ra Female Face (Iris) 1"));
        Assert.Equal(new ChangedCustomization(AssetKind.Tail, 801, 3), Parse("Customization: Miqo'te Female Tail 3"));
        Assert.Equal(new ChangedCustomization(AssetKind.VieraEar, 1801, 1), Parse("Customization: Viera Female Ear 1"));
        Assert.Equal(new ChangedCustomization(AssetKind.Hair, 1501, 201), Parse("Customization: Hrothgar Male Hair (Skeleton) 201"));
        Assert.Equal(new ChangedCustomization(AssetKind.Hair, 1101, 12), Parse("Customization: Lalafell Male Hair 12"));
        Assert.Equal(new ChangedCustomization(AssetKind.Body, 201, 1), Parse("Customization: Midlander Female Body 1"));
        Assert.Equal(new ChangedCustomization(AssetKind.Body, 1601, 0), Parse("Customization: Hrothgar Female Skin Textures"));
    }

    private static void OtherNames()
    {
        foreach (var key in new[]
                 {
                     "Customization: Midlander Male (Child) Hair 5",
                     "Customization: Unknown",
                     "Customization: Face Decal 5",
                     "Customization: All Eyes (Catchlight)",
                     "Customization: Hyur Male Hair 5",
                     "Emote: Dance",
                     "Augmented Crystarium Coat",
                 })
            Assert.True(!ChangedItemKeys.TryParseCustomization(key, out _), key);
    }
}
