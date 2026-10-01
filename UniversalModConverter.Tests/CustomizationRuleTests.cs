using UniversalModConverter.Core;

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
        ("Au Ra tails own a Xaela material root at ID + 100", AuRaXaelaRoots),
        ("Au Ra tail to Au Ra tail moves both clans' materials", AuRaToAuRaTail),
        ("Au Ra tail to other race leaves the Xaela root behind", AuRaToOtherTail),
        ("Other race tail to Au Ra tail gives Xaela a material root", OtherToAuRaTail),
        ("Detection counts the Xaela root as part of its tail", AuRaXaelaDetection),
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

    private const string RaenMaterial = "chara/human/c1401/obj/tail/t0003/material/v0001/mt_c1401t0003_a.mtrl";
    private const string XaelaMaterial = "chara/human/c1401/obj/tail/t0103/material/v0001/mt_c1401t0103_a.mtrl";
    private const string XaelaTexture = "chara/human/c1401/obj/tail/t0103/texture/v01_c1401t0103_etc_n.tex";

    private static void AuRaXaelaRoots()
    {
        Assert.Equal(Tail(1401, 103), CustomizationPaths.XaelaMaterialEndpoint(Tail(1401, 3)));
        Assert.Equal(Tail(1301, 101), CustomizationPaths.XaelaMaterialEndpoint(Tail(1301, 1)));
        Assert.Equal(null, CustomizationPaths.XaelaMaterialEndpoint(Tail(801, 3)));
        Assert.Equal(null, CustomizationPaths.XaelaMaterialEndpoint(Tail(1401, 103)));
        Assert.Equal(Tail(1401, 3), CustomizationPaths.Owner(Tail(1401, 103)));
        Assert.Equal(Tail(801, 103), CustomizationPaths.Owner(Tail(801, 103)));
        Assert.True(CustomizationPaths.Owns(XaelaTexture, Tail(1401, 3)));
        Assert.True(!CustomizationPaths.Contains(XaelaTexture, Tail(1401, 3)));
        // The model's material name loads under the Xaela root, named after it.
        Assert.Equal(XaelaMaterial, CustomizationPaths.MaterialPath(Tail(1401, 103), "/mt_c1401t0003_a.mtrl"));
        Assert.Equal(RaenMaterial, CustomizationPaths.MaterialPath(Tail(1401, 3), "/mt_c1401t0003_a.mtrl"));
    }

    private static void AuRaToAuRaTail()
    {
        var source = Tail(1401, 3);
        var target = Tail(1401, 2);
        Assert.Equal(new[] { source, Tail(1401, 103) }, CustomizationPaths.MovedRoots(source, target).ToArray());
        Assert.Equal("chara/human/c1401/obj/tail/t0102/material/v0001/mt_c1401t0102_a.mtrl",
            CustomizationPaths.Rewrite(XaelaMaterial, source, target));
        Assert.Equal("chara/human/c1401/obj/tail/t0102/texture/v01_c1401t0102_etc_n.tex",
            CustomizationPaths.Rewrite(XaelaTexture, source, target));
        Assert.Equal("chara/human/c1401/obj/tail/t0002/material/v0001/mt_c1401t0002_a.mtrl",
            CustomizationPaths.Rewrite(RaenMaterial, source, target));
        // Local files laid out like the game paths follow along.
        Assert.Equal(@"tail\aura\chara\human\c1301\obj\tail\t0103\material\v0001\mt_c1301t0103_a.mtrl",
            CustomizationPaths.Rewrite(@"tail\aura\chara\human\c1401\obj\tail\t0103\material\v0001\mt_c1401t0103_a.mtrl",
                source, Tail(1301, 3)));
        Assert.Equal(new[] { (source, target), (Tail(1401, 103), Tail(1401, 102)) },
            CustomizationPaths.MaterialClans(source, target).ToArray());
    }

    private static void AuRaToOtherTail()
    {
        var source = Tail(1401, 3);
        var target = Tail(801, 2);
        Assert.Equal(new[] { source }, CustomizationPaths.MovedRoots(source, target).ToArray());
        Assert.Equal(XaelaMaterial, CustomizationPaths.Rewrite(XaelaMaterial, source, target));
        Assert.Equal("chara/human/c0801/obj/tail/t0002/material/v0001/mt_c0801t0002_a.mtrl",
            CustomizationPaths.Rewrite(RaenMaterial, source, target));
        Assert.Equal(new[] { (source, target) }, CustomizationPaths.MaterialClans(source, target).ToArray());
        Assert.Equal(new[] { source }, CustomizationPaths.MovedRoots(source, new CustomizationPathEndpoint(AssetKind.VieraEar, 1801, 1)).ToArray());
    }

    private static void OtherToAuRaTail()
    {
        var source = Tail(801, 5);
        var target = Tail(1401, 2);
        Assert.Equal(new[] { source }, CustomizationPaths.MovedRoots(source, target).ToArray());
        // Xaela load the source's only material too, from their own root.
        Assert.Equal(new[] { (source, target), (source, Tail(1401, 102)) },
            CustomizationPaths.MaterialClans(source, target).ToArray());
        Assert.Equal("chara/human/c1401/obj/tail/t0102/material/v0001/mt_c1401t0102_a.mtrl",
            CustomizationPaths.MaterialPath(Tail(1401, 102), "/mt_c1401t0002_a.mtrl"));
        Assert.Equal("chara/human/c1401/obj/tail/t0102/material/v0001/mt_c1401t0102_a.mtrl",
            CustomizationPaths.Rewrite("chara/human/c1401/obj/tail/t0002/material/v0001/mt_c1401t0002_a.mtrl", target, Tail(1401, 102)));
    }

    private static void AuRaXaelaDetection()
    {
        var root = Path.Combine(Path.GetTempPath(), "umc-xaela-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(root);
        try
        {
            File.WriteAllText(Path.Combine(root, "meta.json"), $$"""
                {"FileVersion":4,"Name":"Xaela","DefaultData":{"Files":{
                  "chara/human/c1401/obj/tail/t0003/model/c1401t0003_til.mdl":"a.mdl",
                  "{{RaenMaterial}}":"a.mtrl","{{XaelaMaterial}}":"b.mtrl","{{XaelaTexture}}":"b.tex"} } }
                """);
            var mod = PenumbraMod.Load(root);
            Assert.Equal(new[] { Tail(1401, 3) }, CustomizationDetection.FindRoots(mod, root).ToArray());
            Assert.Equal(AssetContents.Model | AssetContents.Material | AssetContents.Texture,
                CustomizationDetection.Contents(mod, Tail(1401, 3)));

            // A mod that only retextures Xaela still lists the tail itself.
            File.WriteAllText(Path.Combine(root, "meta.json"), $$"""
                {"FileVersion":4,"Name":"Xaela","DefaultData":{"Files":{"{{XaelaTexture}}":"b.tex"} } }
                """);
            mod = PenumbraMod.Load(root);
            Assert.Equal(new[] { Tail(1401, 3) }, CustomizationDetection.FindRoots(mod, root).ToArray());
            Assert.True(CustomizationDetection.CanFanOut(mod, Tail(1401, 3)));
        }
        finally
        {
            Directory.Delete(root, true);
        }
    }
}
