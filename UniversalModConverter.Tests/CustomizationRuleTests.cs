using System.Collections.Immutable;
using UniversalModConverter.Core;

/// <summary>Material-root rules for hair, face, tail and Viera ear conversion.</summary>
internal static class CustomizationRuleTests
{
    public static (string Name, Action Run)[] All =>
    [
        ("Shared material roots (hair 101-200, Hrothgar t0001)", SharedRoots),
        ("Midlander hair to another race's same ID keeps the shared material name", MidlanderHairSameId),
        ("Hair material names resolve the way the game resolves them", GameHairMaterialNames),
        ("Hair physics bones name their skeleton", HairSkeletonsFromBones),
        ("Hrothgar tail materials use t0001 and five variant folders", HrothgarMaterialPaths),
        ("A material named by complete game path is used as it is", CompleteMaterialPaths),
        ("Hrothgar tail to other race tail", HrothgarToOtherTail),
        ("Other race tail to Hrothgar tail", OtherToHrothgarTail),
        ("Hrothgar tail to Hrothgar tail keeps shared materials", HrothgarToHrothgarTail),
        ("Face, tail and ear materials are named as the game reads them", GameMaterialIdentity),
        ("Face and ear materials have no variant folder", FaceAndEarFolders),
        ("Au Ra tails own a Xaela material root at ID + 100", AuRaXaelaRoots),
        ("Au Ra tail to Au Ra tail moves both clans' materials", AuRaToAuRaTail),
        ("Au Ra tail to other race leaves the Xaela root behind", AuRaToOtherTail),
        ("Other race tail to Au Ra tail gives Xaela a material root", OtherToAuRaTail),
        ("Detection counts the Xaela root as part of its tail", AuRaXaelaDetection),
        ("A face's neck morphs move with its neck", NeckMorphsDeform),
        ("A face's neck is fitted to the target race's seam", NeckFitsTargetSeam),
        ("A neck that cannot be fitted says why and is only deformed", NeckNotFitted),
        ("A garbled neck table is left alone and the race's seam found near the vertices", GarbledNeckMorphs),
    ];

    private static RacialDeformationPlan Translate(float x, float y, float z) => new(
        [new RacialDeformationStep(201, false, new Dictionary<string, System.Numerics.Matrix4x4>
            { ["j_kubi"] = System.Numerics.Matrix4x4.CreateTranslation(x, y, z) }.ToImmutableDictionary())], []);

    private static NeckSeamPoint SeamPoint(float x, float y, float z, System.Numerics.Vector3 normal)
        => new(new(x, y, z), normal);

    private static void Near(System.Numerics.Vector3 expected, System.Numerics.Vector3 actual)
        => Assert.True(System.Numerics.Vector3.Distance(expected, actual) < 1e-4f, $"expected {expected}, got {actual}");

    private static System.Numerics.Vector3 ReadVector(byte[] bytes, int offset)
        => new(BitConverter.ToSingle(bytes, offset), BitConverter.ToSingle(bytes, offset + 4), BitConverter.ToSingle(bytes, offset + 8));

    private static void NeckMorphsDeform()
    {
        var model = MdlFile.Read(TestAssets.CreateMdl(boneName: "j_kubi", neckMorph: true));
        MdlRaceConverter.Convert(model, Translate(2, 3, 4));
        var parsed = MdlFile.Read(model.Write());
        Near(new(3, 5, 7), parsed.NeckMorphs.Single().Position);
        Assert.Equal(0xFFu, parsed.NeckMorphs.Single().Unknown);
    }

    private static void NeckFitsTargetSeam()
    {
        var model = MdlFile.Read(TestAssets.CreateMdl(boneName: "j_kubi", neckMorph: true));
        var normal = System.Numerics.Vector3.Normalize(new(0, 0.6f, 0.8f));
        // The deformation puts the seam at (3, 5, 7); the target race's bodies meet it 1 cm higher.
        var target = SeamPoint(3, 5.01f, 7, normal);
        var report = MdlRaceConverter.Convert(model, Translate(2, 3, 4),
            new NeckFit([SeamPoint(1, 2, 3, System.Numerics.Vector3.UnitZ)], [target]));
        Assert.Equal(null, report.NeckProblem);
        Assert.True(MathF.Abs(report.NeckMoved - 0.01f) < 1e-4f, $"moved {report.NeckMoved}");
        var output = model.Write();
        var parsed = MdlFile.Read(output);
        Near(target.Position, ReadVector(output, parsed.DataOffset));
        Near(normal, ReadVector(output, parsed.DataOffset + 20));
        Near(target.Position, parsed.NeckMorphs.Single().Position);
        Near(normal, parsed.NeckMorphs.Single().Normal);
        Near(target.Position, new System.Numerics.Vector3(parsed.BoundingBox.Maximum.X, parsed.BoundingBox.Maximum.Y, parsed.BoundingBox.Maximum.Z));
    }

    private static void GarbledNeckMorphs()
    {
        // The model's own neck table lists a seam nowhere near its vertices; its race's seam lists
        // the vertex 2 mm from where a sculptor left it.
        var model = MdlFile.Read(TestAssets.CreateMdl(boneName: "j_kubi", neckMorph: true, garbledNeckMorph: true));
        var normal = System.Numerics.Vector3.Normalize(new(0, 0.6f, 0.8f));
        var target = SeamPoint(3, 5.01f, 7, normal);
        var report = MdlRaceConverter.Convert(model, Translate(2, 3, 4),
            new NeckFit([SeamPoint(1.002f, 2, 3, System.Numerics.Vector3.UnitZ)], [target]));
        Assert.Equal(null, report.NeckProblem);
        var output = model.Write();
        var parsed = MdlFile.Read(output);
        Near(target.Position, ReadVector(output, parsed.DataOffset));
        Near(new(9, 9, 9), parsed.NeckMorphs.Single().Position);
    }

    private static void NeckNotFitted()
    {
        var model = MdlFile.Read(TestAssets.CreateMdl(boneName: "j_kubi", neckMorph: true));
        var report = MdlRaceConverter.Convert(model, Translate(2, 3, 4), new NeckFit([],
            [SeamPoint(3, 5, 7, System.Numerics.Vector3.UnitZ), SeamPoint(4, 5, 7, System.Numerics.Vector3.UnitX)]));
        Assert.True(report.NeckProblem?.Contains("1 vertices and the target race's 2") == true, report.NeckProblem ?? "no problem");
        var output = model.Write();
        Near(new(3, 5, 7), ReadVector(output, MdlFile.Read(output).DataOffset));
        Near(new(3, 5, 7), MdlFile.Read(output).NeckMorphs.Single().Position);
    }

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

    /// <summary>
    /// Midlander male hair 111 to Viera male hair 111: both load their materials from the
    /// c0101 h0111 root, and the game finds the folder from the c0101h0111 the material's name
    /// starts with. The owner suffix the conversion adds must not turn that into c1701h0111,
    /// a folder nothing is in (the hair would load no material and not be drawn).
    /// </summary>
    private static void MidlanderHairSameId()
    {
        var source = Hair(101, 111);
        var viera = Hair(1701, 111);
        Assert.Equal("chara/human/c0101/obj/hair/h0111/material/v0001/mt_c0101h0111_c1701_hir_b.mtrl",
            CustomizationPaths.Rewrite("chara/human/c0101/obj/hair/h0111/material/v0001/mt_c0101h0111_hir_b.mtrl", source, viera));
        Assert.Equal("chara/human/c0101/obj/hair/h0111/material/v0001/mt_c0101h0111_c1701_hir_b.mtrl",
            CustomizationPaths.RewriteOwnedReference("chara/human/c0101/obj/hair/h0111/material/v0001/mt_c0101h0111_hir_b.mtrl", source, viera));
        Assert.Equal("/mt_c0101h0111_c1701_hir_scalp.mtrl",
            CustomizationPaths.RewriteOwnedReference("/mt_c0101h0111_hir_scalp.mtrl", source, viera));
        Assert.Equal("/mt_c0101h0111_c0301_hir_a.mtrl",
            CustomizationPaths.RewriteOwnedReference("/mt_c0101h0111_hir_a.mtrl", source, Hair(301, 111)));
        // Another ID was never affected.
        Assert.Equal("/mt_c0101h0109_c1701_hir_scalp.mtrl",
            CustomizationPaths.RewriteOwnedReference("/mt_c0101h0111_hir_scalp.mtrl", source, Hair(1701, 109)));
        // The model's own textures still move to the target's root.
        Assert.Equal("chara/human/c1701/obj/hair/h0111/texture/c1701h0111_hir_b_mask.tex",
            CustomizationPaths.Rewrite("chara/human/c0101/obj/hair/h0111/texture/c0101h0111_hir_b_mask.tex", source, viera));
    }

    /// <summary>
    /// What the game's own material resolver returned for hair (Human.ResolveMtrlPath, 2026-10-17):
    /// on the shared Midlander root it writes that root's race over the name's, and keeps the
    /// name's ID; a hair with a root of its own loads the name as it is.
    /// </summary>
    private static void GameHairMaterialNames()
    {
        Assert.Equal("chara/human/c0101/obj/hair/h0111/material/v0001/mt_c0101h0111_c1701_hir_a.mtrl",
            CustomizationPaths.MaterialPath(Hair(1701, 111), "/mt_c1701h0111_c1701_hir_a.mtrl"));
        Assert.Equal("chara/human/c0101/obj/hair/h0111/material/v0001/mt_c0101h0111_c1701_hir_a.mtrl",
            CustomizationPaths.MaterialPath(Hair(1701, 111), "/mt_c0101h0111_c1701_hir_a.mtrl"));
        Assert.Equal("chara/human/c0101/obj/hair/h0109/material/v0001/mt_c0101h0111_c1701_hir_a.mtrl",
            CustomizationPaths.MaterialPath(Hair(1701, 109), "/mt_c1701h0111_c1701_hir_a.mtrl"));
        Assert.Equal("chara/human/c0201/obj/hair/h0120/material/v0001/mt_c0201h0120_hir_a.mtrl",
            CustomizationPaths.MaterialPath(Hair(801, 120), "/mt_c0801h0120_hir_a.mtrl"));
        // Roots of their own: Miqo'te 101-115, Hrothgar, and every ID above 200.
        Assert.Equal("chara/human/c0701/obj/hair/h0111/material/v0001/mt_c0101h0111_c1701_hir_a.mtrl",
            CustomizationPaths.MaterialPath(Hair(701, 111), "/mt_c0101h0111_c1701_hir_a.mtrl"));
        Assert.Equal("chara/human/c1501/obj/hair/h0111/material/v0001/mt_c0101h0111_hir_a.mtrl",
            CustomizationPaths.MaterialPath(Hair(1501, 111), "/mt_c0101h0111_hir_a.mtrl"));
        Assert.Equal("chara/human/c1701/obj/hair/h0201/material/v0001/mt_c0101h0201_hir_a.mtrl",
            CustomizationPaths.MaterialPath(Hair(1701, 201), "/mt_c0101h0201_hir_a.mtrl"));
    }

    private static void HairSkeletonsFromBones()
    {
        Assert.True(CustomizationPaths.HairSkeletons(["j_kao", "j_ex_h0108_ke_b", "J_EX_H0108_KE_L", "j_ex_h0112_ke_f",
                "j_ex_met_a", "/mt_c0101h0111_hir_a.mtrl"])
            .SequenceEqual(new ushort[] { 108, 112 }));
        Assert.Equal(0, CustomizationPaths.HairSkeletons(["j_kao", "j_kubi", "j_sebo_c"]).Count);
    }

    private static void HrothgarMaterialPaths()
    {
        var paths = CustomizationPaths.MaterialPaths(Tail(1501, 3), "/mt_c1501t0001_til_a.mtrl");
        Assert.Equal(5, paths.Length);
        Assert.Equal("chara/human/c1501/obj/tail/t0001/material/v0003/mt_c1501t0001_til_a.mtrl", paths[2]);
        Assert.Equal("chara/human/c0801/obj/tail/t0002/material/v0001/mt_c0801t0002_a.mtrl",
            CustomizationPaths.MaterialPath(Tail(801, 2), "/mt_c0801t0002_a.mtrl"));
    }

    /// <summary>
    /// A model can name its material by complete game path instead of a short /mt_….mtrl name.
    /// No folder is added to such a path, whichever kind of customization loads it.
    /// </summary>
    private static void CompleteMaterialPaths()
    {
        const string midlander = "chara/human/c0201/obj/hair/h0144/material/v0001/mt_c0201h0144_hir_b.mtrl";
        Assert.Equal(midlander, CustomizationPaths.MaterialPath(Hair(801, 144), midlander));
        Assert.Equal(midlander, CustomizationPaths.MaterialPath(Hair(801, 144), "/" + midlander));
        Assert.Equal(midlander, CustomizationPaths.MaterialPath(Hair(801, 144), midlander.Replace('/', '\\')));
        // The short name still resolves through the shared root.
        Assert.Equal(midlander, CustomizationPaths.MaterialPath(Hair(801, 144), "/mt_c0201h0144_hir_b.mtrl"));

        const string face = "chara/human/c0801/obj/face/f0002/material/mt_c0801f0002_fac_a.mtrl";
        Assert.Equal(face, CustomizationPaths.MaterialPath(new(AssetKind.Face, 801, 2), face));
        const string ear = "chara/human/c1801/obj/zear/z0003/material/mt_c1801z0003_a.mtrl";
        Assert.Equal(ear, CustomizationPaths.MaterialPath(new(AssetKind.VieraEar, 1801, 3), ear));

        // A Hrothgar tail may load any variant folder; the one the model names comes first.
        var hrothgar = CustomizationPaths.MaterialPaths(Tail(1501, 3),
            "chara/human/c1501/obj/tail/t0001/material/v0002/mt_c1501t0001_til_a.mtrl");
        Assert.Equal(5, hrothgar.Length);
        Assert.Equal("chara/human/c1501/obj/tail/t0001/material/v0002/mt_c1501t0001_til_a.mtrl", hrothgar[0]);
        Assert.True(hrothgar.Contains("chara/human/c1501/obj/tail/t0001/material/v0005/mt_c1501t0001_til_a.mtrl"));

        // An Au Ra tail: Raen load it as named, Xaela the same material from their own root.
        const string raen = "chara/human/c1401/obj/tail/t0003/material/v0001/mt_c1401t0003_a.mtrl";
        Assert.Equal(raen, CustomizationPaths.MaterialPath(Tail(1401, 3), raen));
        Assert.Equal("chara/human/c1401/obj/tail/t0103/material/v0001/mt_c1401t0103_a.mtrl",
            CustomizationPaths.MaterialPath(Tail(1401, 103), raen));
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
        // The target folder is the shared root. The game writes mt_c1601t0001 over the first 13
        // characters of the name, so it starts with that; the tail's own ID after it keeps the
        // materials every Hrothgar tail shares from being replaced.
        const string converted = "chara/human/c1601/obj/tail/t0001/material/v0001/mt_c1601t0001_t0003_a.mtrl";
        Assert.Equal(converted,
            CustomizationPaths.Rewrite("chara/human/c0801/obj/tail/t0005/material/v0001/mt_c0801t0005_a.mtrl", source, target));
        var reference = CustomizationPaths.RewriteOwnedReference("/mt_c0801t0005_a.mtrl", source, target);
        Assert.Equal("/mt_c1601t0001_t0003_a.mtrl", reference);
        Assert.Equal(converted, CustomizationPaths.MaterialPath(target, reference));
        // Tail 1 is a tail of its own as well.
        Assert.Equal("/mt_c1601t0001_t0001_a.mtrl", CustomizationPaths.RewriteOwnedReference("/mt_c0801t0005_a.mtrl", source, Tail(1601, 1)));
    }

    private static void HrothgarToHrothgarTail()
    {
        const string material = "chara/human/c1501/obj/tail/t0001/material/v0002/mt_c1501t0001_til_a.mtrl";
        Assert.Equal(material, CustomizationPaths.Rewrite(material, Tail(1501, 3), Tail(1501, 4)));
        Assert.Equal("/mt_c1501t0001_til_a.mtrl",
            CustomizationPaths.RewriteOwnedReference("/mt_c1501t0001_til_a.mtrl", Tail(1501, 3), Tail(1501, 4)));
        // Across genders the folder variant is kept and the name becomes the target's own, as the game reads it.
        Assert.Equal("chara/human/c1601/obj/tail/t0001/material/v0002/mt_c1601t0001_t0004_til_a.mtrl",
            CustomizationPaths.Rewrite(material, Tail(1501, 3), Tail(1601, 4)));
        Assert.Equal("/mt_c1601t0001_t0004_til_a.mtrl",
            CustomizationPaths.RewriteOwnedReference("/mt_c1501t0001_til_a.mtrl", Tail(1501, 3), Tail(1601, 4)));
    }

    /// <summary>
    /// The game writes the root's identity over the first 13 characters of a face, tail or ear
    /// material's name. A model naming another tail's material loads its own; converted, it loads
    /// the converted one. A second clan's face loads from its own root, but Veena take their face
    /// skin from the first clan's.
    /// </summary>
    private static void GameMaterialIdentity()
    {
        var tail = Tail(1401, 2);
        Assert.Equal("chara/human/c1401/obj/tail/t0002/material/v0001/mt_c1401t0002_a.mtrl",
            CustomizationPaths.MaterialPath(tail, "/mt_c1401t0001_a.mtrl"));
        Assert.Equal("/mt_c0801t0003_a.mtrl", CustomizationPaths.RewriteOwnedReference("/mt_c1401t0001_a.mtrl", tail, Tail(801, 3)));
        // A skin material keeps its own name.
        Assert.Equal("/mt_c1401b0001_a.mtrl", CustomizationPaths.RewriteOwnedReference("/mt_c1401b0001_a.mtrl", tail, Tail(801, 3)));

        var veena = new CustomizationPathEndpoint(AssetKind.Face, 1801, 101);
        Assert.Equal("chara/human/c1801/obj/face/f0101/material/mt_c1801f0101_iri_a.mtrl",
            CustomizationPaths.MaterialPath(veena, "/mt_c1801f0001_iri_a.mtrl"));
        Assert.Equal("chara/human/c1801/obj/face/f0001/material/mt_c1801f0001_fac_a.mtrl",
            CustomizationPaths.MaterialPath(veena, "/mt_c1801f0001_fac_a.mtrl"));
        var lost = new CustomizationPathEndpoint(AssetKind.Face, 1501, 102);
        Assert.Equal("chara/human/c1501/obj/face/f0002/material/mt_c1501f0002_etc_a.mtrl",
            CustomizationPaths.MaterialPath(lost, "/mt_c1501f0002_etc_a.mtrl"));
        Assert.Equal("chara/human/c1501/obj/face/f0102/material/mt_c1501f0102_fac_a.mtrl",
            CustomizationPaths.MaterialPath(lost, "/mt_c1501f0002_fac_a.mtrl"));
        var xaela = new CustomizationPathEndpoint(AssetKind.Face, 1401, 101);
        Assert.Equal("chara/human/c1401/obj/face/f0101/material/mt_c1401f0101_fac_a.mtrl",
            CustomizationPaths.MaterialPath(xaela, "/mt_c1401f0001_fac_a.mtrl"));

        // The model keeps naming the first clan's materials: each clan's root is read from the name by the game.
        Assert.Equal("/mt_c1401f0002_fac_a.mtrl", CustomizationPaths.RewriteOwnedReference("/mt_c1401f0001_fac_a.mtrl",
            new CustomizationPathEndpoint(AssetKind.Face, 1401, 1), new CustomizationPathEndpoint(AssetKind.Face, 1401, 2)));
        Assert.Equal("/mt_c1401t0002_a.mtrl", CustomizationPaths.RewriteOwnedReference("/mt_c1401t0001_a.mtrl", Tail(1401, 1), Tail(1401, 2)));

        // Face roots of a second clan belong to the first clan's face, and move with it.
        Assert.Equal(new CustomizationPathEndpoint(AssetKind.Face, 1401, 1), CustomizationPaths.Owner(xaela));
        Assert.Equal("chara/human/c1401/obj/face/f0102/material/mt_c1401f0102_fac_a.mtrl",
            CustomizationPaths.Rewrite("chara/human/c1401/obj/face/f0101/material/mt_c1401f0101_fac_a.mtrl",
                new CustomizationPathEndpoint(AssetKind.Face, 1401, 1), new CustomizationPathEndpoint(AssetKind.Face, 1401, 2)));
        Assert.Equal(new[] { 1, 101 }, CustomizationPaths.MovedRoots(new CustomizationPathEndpoint(AssetKind.Face, 1401, 1),
            new CustomizationPathEndpoint(AssetKind.Face, 1401, 2)).Select(r => (int)r.ModelId).ToArray());
        Assert.Equal(1, CustomizationPaths.MovedRoots(new CustomizationPathEndpoint(AssetKind.Face, 1401, 1),
            new CustomizationPathEndpoint(AssetKind.Face, 801, 2)).Count);
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
        Assert.Equal(Tail(1401, 103), CustomizationPaths.ClanMaterialEndpoint(Tail(1401, 3)));
        Assert.Equal(Tail(1301, 101), CustomizationPaths.ClanMaterialEndpoint(Tail(1301, 1)));
        Assert.Equal(null, CustomizationPaths.ClanMaterialEndpoint(Tail(801, 3)));
        Assert.Equal(null, CustomizationPaths.ClanMaterialEndpoint(Tail(1401, 103)));
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
