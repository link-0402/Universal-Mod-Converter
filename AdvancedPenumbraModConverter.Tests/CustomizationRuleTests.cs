using AdvancedPenumbraModConverter.Core;

/// <summary>Material-root rules for hair, face, tail and Viera-ear conversion.</summary>
internal static class CustomizationRuleTests
{
    public static (string Name, Action Run)[] All =>
    [
        ("Shared material roots (hair 101-200, Hrothgar t0001)", SharedRoots),
        ("Hrothgar tail materials use t0001 and five variant folders", HrothgarMaterialPaths),
        ("Hrothgar tail to other race tail", HrothgarToOtherTail),
        ("Other race tail to Hrothgar tail", OtherToHrothgarTail),
        ("Hrothgar tail to Hrothgar tail keeps shared materials", HrothgarToHrothgarTail),
        ("Face and ear materials have no variant folder", FaceAndEarFolders),
    ];

    private static CustomizationPathEndpoint Hair(ushort race, ushort id) => new(AssetKind.Hair, race, id);
    private static CustomizationPathEndpoint Tail(ushort race, ushort id) => new(AssetKind.Tail, race, id);

    private static void SharedRoots()
    {
        Assert.True(CustomizationPaths.IsSharedMaterialRoot(Hair(101, 120)));
        Assert.True(CustomizationPaths.IsSharedMaterialRoot(Hair(201, 101)));
        Assert.True(!CustomizationPaths.IsSharedMaterialRoot(Hair(101, 201)), "hair above 200 is per race");
        Assert.True(!CustomizationPaths.IsSharedMaterialRoot(Hair(501, 120)), "only the Midlander root is shared");
        Assert.True(!CustomizationPaths.IsSharedMaterialRoot(Hair(701, 105)), "Miqo'te 101-115 roots are their own");
        Assert.True(CustomizationPaths.IsSharedMaterialRoot(Tail(1501, 1)));
        Assert.True(!CustomizationPaths.IsSharedMaterialRoot(Tail(1501, 3)));
        Assert.True(!CustomizationPaths.IsSharedMaterialRoot(Tail(801, 1)));
        Assert.Equal(Tail(1601, 1), CustomizationPaths.GetMaterialEndpoint(Tail(1601, 4)));
        Assert.Equal(Hair(101, 130), CustomizationPaths.GetMaterialEndpoint(Hair(501, 130)));
    }

    private static void HrothgarMaterialPaths()
    {
        var paths = CustomizationPaths.MaterialPaths(Tail(1501, 3), "/mt_c1501t0001_til_a.mtrl");
        Assert.Equal(5, paths.Length);
        Assert.Equal("chara/human/c1501/obj/tail/t0001/material/v0003/mt_c1501t0001_til_a.mtrl", paths[2]);
        Assert.Equal("chara/human/c0801/obj/tail/t0002/material/v0001/mt_c0801t0002_a.mtrl",
            CustomizationPaths.MaterialPath(Tail(801, 2), "/mt_c0801t0002_a.mtrl"));
    }

    private static void HrothgarToOtherTail()
    {
        var source = Tail(1501, 3);
        var target = Tail(801, 2);
        // Materials live in the shared root and are named after it.
        Assert.Equal("chara/human/c0801/obj/tail/t0002/material/v0001/mt_c0801t0002_til_egof.mtrl",
            CustomizationPaths.Rewrite("chara/human/c1501/obj/tail/t0001/material/v0004/mt_c1501t0001_til_egof.mtrl", source, target));
        Assert.Equal("/mt_c0801t0002_til_egof.mtrl",
            CustomizationPaths.RewriteOwnedReference("/mt_c1501t0001_til_egof.mtrl", source, target));
        Assert.Equal("chara/human/c0801/obj/tail/t0002/model/c0801t0002_til.mdl",
            CustomizationPaths.Rewrite("chara/human/c1501/obj/tail/t0003/model/c1501t0003_til.mdl", source, target));
        // Shared-root textures are referenced by full path and stay where they are.
        const string texture = "chara/human/c1501/obj/tail/t0001/texture/v01_c1501t0001_etc_norm.tex";
        Assert.Equal(texture, CustomizationPaths.Rewrite(texture, source, target));
    }

    private static void OtherToHrothgarTail()
    {
        var source = Tail(801, 5);
        var target = Tail(1601, 3);
        // The target folder is the shared root, but the name is the target's own so the
        // materials every Hrothgar tail shares are never replaced.
        Assert.Equal("chara/human/c1601/obj/tail/t0001/material/v0001/mt_c1601t0003_a.mtrl",
            CustomizationPaths.Rewrite("chara/human/c0801/obj/tail/t0005/material/v0001/mt_c0801t0005_a.mtrl", source, target));
        Assert.Equal("/mt_c1601t0003_a.mtrl", CustomizationPaths.RewriteOwnedReference("/mt_c0801t0005_a.mtrl", source, target));
    }

    private static void HrothgarToHrothgarTail()
    {
        const string material = "chara/human/c1501/obj/tail/t0001/material/v0002/mt_c1501t0001_til_a.mtrl";
        Assert.Equal(material, CustomizationPaths.Rewrite(material, Tail(1501, 3), Tail(1501, 4)));
        Assert.Equal("/mt_c1501t0001_til_a.mtrl",
            CustomizationPaths.RewriteOwnedReference("/mt_c1501t0001_til_a.mtrl", Tail(1501, 3), Tail(1501, 4)));
        // Across genders the folder variant is kept and the name becomes the target's own.
        Assert.Equal("chara/human/c1601/obj/tail/t0001/material/v0002/mt_c1601t0004_til_a.mtrl",
            CustomizationPaths.Rewrite(material, Tail(1501, 3), Tail(1601, 4)));
    }

    private static void FaceAndEarFolders()
    {
        Assert.Equal("chara/human/c0101/obj/face/f0002/material/mt_c0101f0002_fac_a.mtrl",
            CustomizationPaths.MaterialPath(new CustomizationPathEndpoint(AssetKind.Face, 101, 2), "/mt_c0101f0002_fac_a.mtrl"));
        Assert.Equal("chara/human/c1801/obj/zear/z0003/material/mt_c1801z0003_a.mtrl",
            CustomizationPaths.MaterialPath(new CustomizationPathEndpoint(AssetKind.VieraEar, 1801, 3), "/mt_c1801z0003_a.mtrl"));
        Assert.Equal(0, CustomizationPaths.MaterialVariants(new CustomizationPathEndpoint(AssetKind.VieraEar, 1801, 3)).Length);
    }
}
