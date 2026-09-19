using System.Buffers.Binary;
using System.Text;
using System.Text.Json.Nodes;
using AdvancedPenumbraModConverter.Core;

/// <summary>End-to-end tests of the game-path based gear conversion on synthetic mods.</summary>
internal static class GearConversionTests
{
    public static (string Name, Action Run)[] All =>
    [
        ("Penumbra v4 meta round trip keeps unknown data", MetaV4RoundTrip),
        ("Penumbra v3 multi-file round trip", MetaV3RoundTrip),
        ("Game metadata parsers (IMC, EQDP, EQP)", MetadataParsers),
        ("MDL v5 same-length material rewrite", MdlV5Rewrite),
        ("v4 new mod: cross-slot conversion with vanilla dependencies", NewModCrossSlotV4),
        ("v3 in place: shared resources are kept, exclusive ones move", InPlaceSameSlotV3),
        ("Accessory to equipment conversion", AccessoryToEquipment),
        ("Item the mod does not change is rejected", EmptyPlanWithoutModContent),
        ("v4 in place: IMC group, swaps, missing files", InPlaceV4ImcGroupAndSwaps),
        ("In place refuses to overwrite existing target paths", InPlaceTargetConflict),
        ("Customization detection skips roots that only hold borrowed textures", CustomizationBorrowedTextures),
        ("Cross-slot new mod: output models list and mesh-group removal", CrossSlotMeshRemoval),
    ];

    private static void CrossSlotMeshRemoval()
    {
        using var mod = new TempDir();
        const string top = "chara/equipment/e0100/model/c0201e0100_top.mdl";
        const string topMtrl = "chara/equipment/e0100/material/v0001/mt_c0201e0100_top_a.mtrl";
        const string topTex = "chara/equipment/e0100/texture/v01_c0201e0100_top_d.tex";
        mod.Json("meta.json", $$$"""
            {"FileVersion":4,"Identifier":"{{{G1}}}","Name":"Top","Author":"me",
             "DefaultData":{"Files":{"{{{top}}}":"m\\top.mdl","{{{topMtrl}}}":"m\\a.mtrl","{{{topTex}}}":"m\\d.tex"} } }
            """);
        mod.File("m/top.mdl", TestAssets.CreateMultiMeshMdl(["/mt_c0201e0100_top_a.mtrl", "/mt_c0201b0001_a.mtrl"]));
        mod.File("m/a.mtrl", Mtrl(topTex));
        mod.File("m/d.tex", [1]);

        var game = StandardGame();
        var request = new GearConversionRequest(new GearItem(GearSlot.Body, 100, 1), new GearItem(GearSlot.Hands, 300, 1),
            ConversionOutputMode.NewMod);
        var plan = new GearConversionPlanner(game).Plan(mod.Path, request);
        Assert.True(!plan.HasBlockers, string.Join("; ", plan.Diagnostics.Select(d => d.Message)));

        var models = GearOutputModels.Collect(plan, mod.Path);
        var converted = models.Single(m => m.GamePaths.Contains("chara/equipment/e0300/model/c0201e0300_glv.mdl"));
        Assert.True(converted.Editable, converted.EditError ?? "not editable");
        Assert.Equal(2, converted.Groups.Count);
        Assert.Equal("/mt_c0201e0300_glv_a.mtrl", converted.Groups[0].Material);
        Assert.True(converted.Groups[1].IsSkin);
        Assert.Equal((ushort?)201, converted.GenderRace);
        Assert.True(models.Any(m => m.GenderRace == 101), "the vanilla male model copied from the game is listed");

        using var output = new TempDir(create: false);
        GearConversionExecutor.WriteNewMod(plan, mod.Path, output.Path, "Converted");
        GearOutputModels.ApplyRemovals(output.Path,
            new Dictionary<string, MeshRemoval> { [converted.Local] = new([1], converted.Groups.Count) });

        var written = MdlFile.Read(File.ReadAllBytes(Path.Combine(output.Path, converted.Local)));
        Assert.Equal(1, written.Meshes.Length);
        Assert.Equal("/mt_c0201e0300_glv_a.mtrl", written.Materials[written.Meshes[0].MaterialIndex]);
        var issues = GearConversionVerifier.Verify(output.Path, request.Target, request.Source, game);
        Assert.True(issues.Count == 0, string.Join("; ", issues.Select(i => i.Message)));
        // The source mod is untouched.
        Assert.Equal(2, MdlFile.Read(File.ReadAllBytes(Path.Combine(mod.Path, "m", "top.mdl"))).Meshes.Length);
    }

    private static void CustomizationBorrowedTextures()
    {
        using var mod = new TempDir();
        const string borrowed = "chara/human/c0801/obj/face/f0001/texture/c0801f0001_fac_mask.tex";
        const string own = "chara/human/c0801/obj/face/f0002/texture/c0801f0002_fac_base.tex";
        const string retexture = "chara/human/c0801/obj/face/f0003/texture/c0801f0003_fac_base.tex";
        mod.Json("meta.json", $$$"""
            {"FileVersion":4,"Identifier":"{{{G1}}}","Name":"Face","Author":"me",
             "DefaultData":{"Files":{
               "chara/human/c0801/obj/face/f0002/model/c0801f0002_fac.mdl":"m\\face.mdl",
               "chara/human/c0801/obj/face/f0002/material/mt_c0801f0002_fac_a.mtrl":"m\\fac_a.mtrl",
               "{{{own}}}":"m\\base.tex","{{{borrowed}}}":"m\\mask.tex","{{{retexture}}}":"m\\other.tex"} } }
            """);
        mod.File("m/face.mdl", TestAssets.CreateMdl(material: "/mt_c0801f0002_fac_a.mtrl"));
        mod.File("m/fac_a.mtrl", Mtrl(own, borrowed));
        foreach (var texture in new[] { "m/base.tex", "m/mask.tex", "m/other.tex" })
            mod.File(texture, Encoding.ASCII.GetBytes(texture));

        var roots = CustomizationDetection.FindRoots(PenumbraMod.Load(mod.Path), mod.Path);
        // Face 1 only supplies a texture face 2's material loads; face 3's texture is its own retexture.
        Assert.Equal("Face c0801 #2, Face c0801 #3",
            string.Join(", ", roots.Select(r => $"{r.Kind} c{r.GenderRace:D4} #{r.ModelId}")));
    }

    private const string G1 = "11111111-1111-1111-1111-111111111111";
    private const string G2 = "22222222-2222-2222-2222-222222222222";
    private const string G3 = "33333333-3333-3333-3333-333333333333";
    private const string G4 = "44444444-4444-4444-4444-444444444444";
    private const string O1 = "a1111111-1111-1111-1111-111111111111";
    private const string O2 = "a2222222-2222-2222-2222-222222222222";
    private const string O3 = "a3333333-3333-3333-3333-333333333333";
    private const string O4 = "a4444444-4444-4444-4444-444444444444";
    private const string O5 = "a5555555-5555-5555-5555-555555555555";

    // ── Format ──────────────────────────────────────────────────────────────

    private static void MetaV4RoundTrip()
    {
        using var dir = new TempDir();
        dir.Json("meta.json", $$$"""
            {"FileVersion":4,"Identifier":"{{{G1}}}","Name":"Ünïcode + 'mod'","PageNames":{"1":"Second"},
             "DefaultData":{"Files":{"a/b.tex":"x\\b.tex"}},
             "Groups":[{"Type":"Single","Id":"{{{G2}}}","Name":"G","Page":1,"Layout":["DefaultClosed"],
                        "Options":[{"Id":"{{{O1}}}","Name":"A","Color":3,"Files":{"c/d.tex":"y\\d.tex"}}]},
                       {"Type":"Combining","Id":"{{{G3}}}","Name":"C","Options":[{"Id":"{{{O2}}}","Name":"X"}],
                        "Containers":[{"Name":"none"},{"Name":"x","Files":{"e/f.tex":"z\\f.tex"}}]}]}
            """);
        var mod = PenumbraMod.Load(dir.Path);
        Assert.Equal(PenumbraModFormat.Unified, mod.Format);
        Assert.Equal(1 + 1 + 2, mod.Containers.Count());
        Assert.Equal("z\\f.tex", mod.Groups[1].Containers[1].FileEntries().Single().Local);

        using var output = new TempDir();
        mod.Save(output.Path);
        Assert.True(!File.Exists(Path.Combine(output.Path, "default_mod.json")));
        var saved = Read(output.Path, "meta.json");
        Assert.Equal("Ünïcode + 'mod'", saved["Name"]!.GetValue<string>());
        Assert.True(File.ReadAllText(Path.Combine(output.Path, "meta.json")).Contains("Ünïcode + 'mod'"));
        Assert.Equal("Second", saved["PageNames"]!["1"]!.GetValue<string>());
        Assert.Equal("DefaultClosed", saved["Groups"]![0]!["Layout"]![0]!.GetValue<string>());
        Assert.Equal(3, saved["Groups"]![0]!["Options"]![0]!["Color"]!.GetValue<int>());
        Assert.Equal("x\\b.tex", saved["DefaultData"]!["Files"]!["a/b.tex"]!.GetValue<string>());
    }

    private static void MetaV3RoundTrip()
    {
        using var dir = new TempDir();
        dir.Json("meta.json", """{"FileVersion":3,"Name":"Old","Custom":1}""");
        dir.Json("default_mod.json", """{"Name":"","Priority":0,"Files":{"a/b.tex":"b.tex"},"FileSwaps":{},"Manipulations":[]}""");
        dir.Json("group_001_colors.json", """{"Name":"Colors","Type":"Multi","Options":[{"Name":"A","Files":{"c.tex":"c.tex"}}]}""");
        dir.Json("group_002_imc.json", """{"Name":"Imc","Type":"Imc","Identifier":{"PrimaryId":1},"DefaultEntry":{"MaterialId":1}}""");
        var mod = PenumbraMod.Load(dir.Path);
        Assert.Equal(PenumbraModFormat.Legacy, mod.Format);
        Assert.Equal(2, mod.Groups.Count);
        Assert.Equal(0, mod.Groups[1].Containers.Count);
        mod.Save(dir.Path);
        Assert.Equal(1, Read(dir.Path, "meta.json")["Custom"]!.GetValue<int>());
        Assert.True(File.Exists(Path.Combine(dir.Path, "group_001_colors.json")));
        Assert.Equal(PenumbraModFormat.Legacy, PenumbraMod.Load(dir.Path).Format);
    }

    private static void MetadataParsers()
    {
        var imc = Imc(3, 5, (v, part) => new ImcEntry((byte)v, 0, (ushort)(0x10 + part), 5, 0, 0));
        var rows = GameMetadata.ReadImc(imc, 1);
        Assert.Equal(3, rows.Count);
        Assert.Equal(new ImcEntry(2, 0, 0x11, 5, 0, 0), rows[1].Entry);

        var eqdp = Eqdp((300, 0b1100_0000), (7, 3));
        Assert.Equal((ushort)0b1100_0000, GameMetadata.ReadEqdp(eqdp, 300));
        Assert.Equal((ushort)3, GameMetadata.ReadEqdp(eqdp, 7));
        Assert.Equal((ushort)0, GameMetadata.ReadEqdp(eqdp, 8));
        Assert.True(GameMetadata.EqdpHasModel(0b1100_0000, GearSlot.Legs));
        Assert.Equal((ushort)0b11_0000, GameMetadata.RepositionEqdp(0b1100, GearSlot.Body, GearSlot.Hands));

        var eqp = Eqp((1, 0x1234), (300, 0xABCDEF));
        Assert.Equal(0x1234UL, GameMetadata.ReadExpandedEntry(eqp, 1, 0));
        Assert.Equal(0xABCDEFUL, GameMetadata.ReadExpandedEntry(eqp, 300, 0));
        Assert.Equal(0x1234UL, GameMetadata.ReadExpandedEntry(eqp, 0, 0));
        Assert.Equal(99UL, GameMetadata.ReadExpandedEntry(eqp, 9000, 99));

        var gmp = GameMetadata.GmpToJson(0b11 | (5UL << 2) | (6UL << 12) | (7UL << 22) | (0x9UL << 32) | (0x4UL << 36));
        Assert.Equal(5, gmp["RotationA"]!.GetValue<ushort>());
        Assert.Equal(7, gmp["RotationC"]!.GetValue<ushort>());
        Assert.Equal(4, gmp["UnknownB"]!.GetValue<byte>());
    }

    private static void MdlV5Rewrite()
    {
        var bytes = TestAssets.CreateMdl(material: "/mt_c0201e0100_top_a.mtrl");
        BinaryPrimitives.WriteUInt32LittleEndian(bytes, MdlFile.Version5);
        Assert.Equal("/mt_c0201e0100_top_a.mtrl", ResourceReferences.ReadMdlMaterials(bytes).Single());
        var rewritten = ResourceReferences.RewriteMdlStrings(bytes,
            new Dictionary<string, string> { ["/mt_c0201e0100_top_a.mtrl"] = "/mt_c0201e0300_glv_a.mtrl" });
        Assert.Equal(bytes.Length, rewritten.Length);
        Assert.Equal("/mt_c0201e0300_glv_a.mtrl", ResourceReferences.ReadMdlMaterials(rewritten).Single());
        // Length changes append the new name and repoint the material offset (v5 and v6).
        var longerV5 = ResourceReferences.RewriteMdlStrings(bytes,
            new Dictionary<string, string> { ["/mt_c0201e0100_top_a.mtrl"] = "/mt_c0201e0300_top_glv_a.mtrl" });
        Assert.Equal("/mt_c0201e0300_top_glv_a.mtrl", ResourceReferences.ReadMdlMaterials(longerV5).Single());
        Assert.Equal(0, (longerV5.Length - bytes.Length) % 16);

        var v6 = TestAssets.CreateMdl(faceData: true, material: "/mt_c0201e0100_top_a.mtrl");
        var longer = ResourceReferences.RewriteMdlStrings(v6,
            new Dictionary<string, string> { ["/mt_c0201e0100_top_a.mtrl"] = "/mt_c0201e0300_dwn_glv_a.mtrl" });
        // The strict parser validates every section and buffer offset of the result.
        var parsed = MdlFile.Read(longer);
        Assert.Equal("/mt_c0201e0300_dwn_glv_a.mtrl", parsed.Materials.Single());
        Assert.Equal("j_root", parsed.Bones.Single());
        var shift = longer.Length - v6.Length;
        Assert.True(longer.AsSpan(parsed.DataOffset).SequenceEqual(v6.AsSpan(MdlFile.Read(v6).DataOffset)),
            "vertex and index data are unchanged");
        Assert.Equal(MdlFile.Read(v6).DataOffset + shift, parsed.DataOffset);
    }

    // ── Conversions ─────────────────────────────────────────────────────────

    private static void NewModCrossSlotV4()
    {
        using var mod = new TempDir();
        const string top = "chara/equipment/e0100/model/c0201e0100_top.mdl";
        const string topMtrl = "chara/equipment/e0100/material/v0001/mt_c0201e0100_top_a.mtrl";
        const string topTex = "chara/equipment/e0100/texture/v01_c0201e0100_top_d.tex";
        mod.Json("meta.json", $$$"""
            {"FileVersion":4,"Identifier":"{{{G1}}}","Name":"Test Top","Author":"me","DefaultPreferredItems":[5],
             "DefaultData":{
               "Files":{"{{{top}}}":"stuff\\top.mdl","{{{topMtrl}}}":"stuff\\a.mtrl","{{{topTex}}}":"stuff\\d.tex",
                        "chara/equipment/e0200/model/c0201e0200_dwn.mdl":"stuff\\legs.mdl"},
               "Manipulations":[
                 {"Type":"Eqdp","Manipulation":{"Entry":12,"Gender":"Female","Race":"Midlander","SetId":"100","Slot":"Body"}},
                 {"Type":"Rsp","Manipulation":{"Entry":1.0,"SubRace":"Raen","Attribute":"MaleMinSize"}},
                 {"Type":"Shp","Manipulation":{"Entry":true,"Slot":"Body","Id":100,"Shape":"shpx_test"}}]},
             "Groups":[
               {"Type":"Single","Id":"{{{G1}}}","Name":"Color","DefaultSettings":1,"Options":[
                 {"Id":"{{{O1}}}","Name":"Red","Files":{"{{{topTex}}}":"red\\d.tex"}},
                 {"Id":"{{{O2}}}","Name":"Blue","Files":{"{{{topTex}}}":"blue\\d.tex"},
                  "Condition":{"Type":"AnySetting","Group":"{{{G2}}}","Options":["{{{O3}}}"]}}]},
               {"Type":"Multi","Id":"{{{G2}}}","Name":"Legs","Options":[
                 {"Id":"{{{O3}}}","Name":"Short","Files":{"chara/equipment/e0200/model/c0201e0200_dwn.mdl":"stuff\\short.mdl"}}]},
               {"Type":"Multi","Id":"{{{G3}}}","Name":"Other","Options":[
                 {"Id":"{{{O4}}}","Name":"x","Files":{"chara/equipment/e0200/texture/foo.tex":"stuff\\foo.tex"}}]},
               {"Type":"Combining","Id":"{{{G4}}}","Name":"Combo","Options":[{"Id":"{{{O5}}}","Name":"Glow"}],
                "Containers":[{},{"Files":{"{{{topTex}}}":"glow\\d.tex"}}]}]}
            """);
        mod.File("stuff/top.mdl", TestAssets.CreateMdl(material: "/mt_c0201e0100_top_a.mtrl"));
        mod.File("stuff/a.mtrl", Mtrl(topTex));
        foreach (var texture in new[] { "stuff/d.tex", "red/d.tex", "blue/d.tex", "glow/d.tex", "stuff/foo.tex" })
            mod.File(texture, Encoding.ASCII.GetBytes(texture));
        mod.File("stuff/legs.mdl", TestAssets.CreateMdl(material: "/mt_c0201e0200_dwn_a.mtrl"));
        mod.File("stuff/short.mdl", TestAssets.CreateMdl(material: "/mt_c0201e0200_dwn_a.mtrl"));

        var game = StandardGame();
        var request = new GearConversionRequest(new GearItem(GearSlot.Body, 100, 1), new GearItem(GearSlot.Hands, 300, 1),
            ConversionOutputMode.NewMod);
        var plan = new GearConversionPlanner(game).Plan(mod.Path, request);
        Assert.True(!plan.HasBlockers, string.Join("; ", plan.Diagnostics.Select(d => d.Message)));
        Assert.Equal(plan.Fingerprint(), plan.Fingerprint());

        using var output = new TempDir(create: false);
        GearConversionExecutor.WriteNewMod(plan, mod.Path, output.Path, "Converted");
        var meta = Read(output.Path, "meta.json");
        Assert.Equal(4, meta["FileVersion"]!.GetValue<int>());
        Assert.Equal("Converted", meta["Name"]!.GetValue<string>());
        Assert.True(meta["Identifier"]!.GetValue<string>() != G1, "a new mod must get a new identifier");
        Assert.True(meta["DefaultPreferredItems"] == null);

        var files = (JsonObject)meta["DefaultData"]!["Files"]!;
        var keys = files.Select(p => p.Key).ToHashSet();
        Assert.True(keys.Contains("chara/equipment/e0300/model/c0201e0300_glv.mdl"), string.Join(", ", keys));
        Assert.True(keys.Contains("chara/equipment/e0300/material/v0001/mt_c0201e0300_glv_a.mtrl"));
        Assert.True(keys.Contains("chara/equipment/e0300/texture/v01_c0201e0300_glv_d.tex"));
        Assert.True(keys.Contains("chara/equipment/e0300/model/c0101e0300_glv.mdl"), "vanilla male model is copied");
        Assert.True(keys.Contains("chara/equipment/e0300/material/v0001/mt_c0101e0300_glv_a.mtrl"), "vanilla material is copied");
        Assert.True(!keys.Any(k => k.Contains("e0100") || k.Contains("e0200")), string.Join(", ", keys));

        // Contents reference the retargeted resources.
        var model = File.ReadAllBytes(Path.Combine(output.Path, files["chara/equipment/e0300/model/c0201e0300_glv.mdl"]!.GetValue<string>()));
        Assert.Equal("/mt_c0201e0300_glv_a.mtrl", ResourceReferences.ReadMdlMaterials(model).Single());
        var material = File.ReadAllBytes(Path.Combine(output.Path,
            files["chara/equipment/e0300/material/v0001/mt_c0201e0300_glv_a.mtrl"]!.GetValue<string>()));
        Assert.Equal("chara/equipment/e0300/texture/v01_c0201e0300_glv_d.tex", MtrlFile.ReadTexturePaths(material).Single());
        var vanillaMaterial = File.ReadAllBytes(Path.Combine(output.Path,
            files["chara/equipment/e0300/material/v0001/mt_c0101e0300_glv_a.mtrl"]!.GetValue<string>()));
        Assert.Equal("chara/equipment/e0100/texture/v01_c0101e0100_top_n.tex",
            MtrlFile.ReadTexturePaths(vanillaMaterial).Single());

        // Metadata: explicit EQDP moved to the hands bits, unrelated entries dropped, IMC for every variant.
        var manipulations = meta["DefaultData"]!["Manipulations"]!.AsArray().OfType<JsonObject>().ToList();
        var eqdp = manipulations.Where(m => m["Type"]!.GetValue<string>() == "Eqdp").ToList();
        var female = eqdp.Single(m => m["Manipulation"]!["Gender"]!.GetValue<string>() == "Female")["Manipulation"]!;
        Assert.Equal("300", female["SetId"]!.GetValue<string>());
        Assert.Equal("Hands", female["Slot"]!.GetValue<string>());
        Assert.Equal(48, female["Entry"]!.GetValue<int>());
        var male = eqdp.Single(m => m["Manipulation"]!["Gender"]!.GetValue<string>() == "Male")["Manipulation"]!;
        Assert.Equal(48, Json.GetInt(male["Entry"], 0));
        Assert.True(!manipulations.Any(m => m["Type"]!.GetValue<string>() == "Rsp"));
        var shp = manipulations.Single(m => m["Type"]!.GetValue<string>() == "Shp")["Manipulation"]!;
        Assert.Equal("Hands", shp["Slot"]!.GetValue<string>());
        Assert.Equal(300, shp["Id"]!.GetValue<int>());
        var imc = manipulations.Where(m => m["Type"]!.GetValue<string>() == "Imc").Select(m => m["Manipulation"]!).ToList();
        Assert.Equal(2, imc.Count);
        Assert.True(imc.All(m => Json.GetInt(m["Entry"]!["MaterialId"], 0) == 1 && m["EquipSlot"]!.GetValue<string>() == "Hands"));
        Assert.Equal(0x3FF, Json.GetInt(imc[0]["Entry"]!["AttributeMask"], 0));

        // Groups: option IDs and conditions survive; the referenced (now empty) group is kept.
        var groups = meta["Groups"]!.AsArray().OfType<JsonObject>().ToList();
        Assert.Equal(new[] { "Color", "Legs", "Combo" }, groups.Select(g => g["Name"]!.GetValue<string>()).ToArray());
        Assert.Equal(1, groups[0]["DefaultSettings"]!.GetValue<int>());
        Assert.Equal(O2, groups[0]["Options"]![1]!["Id"]!.GetValue<string>());
        Assert.Equal("chara/equipment/e0300/texture/v01_c0201e0300_glv_d.tex",
            ((JsonObject)groups[0]["Options"]![1]!["Files"]!).Single().Key);
        Assert.True(groups[1]["Options"]![0]!["Files"] == null, "unrelated leg files are not carried over");
        Assert.Equal("chara/equipment/e0300/texture/v01_c0201e0300_glv_d.tex",
            ((JsonObject)groups[2]["Containers"]![1]!["Files"]!).Single().Key);

        var issues = GearConversionVerifier.Verify(output.Path, request.Target, request.Source, game);
        Assert.True(issues.Count == 0, string.Join("; ", issues.Select(i => i.Message)));
        Assert.True(!File.Exists(Path.Combine(output.Path, "stuff", "legs.mdl")));
    }

    private static void InPlaceSameSlotV3()
    {
        using var mod = new TempDir();
        const string root = "chara/equipment/e0100";
        mod.Json("meta.json", """{"FileVersion":3,"Name":"Legacy"}""");
        mod.Json("default_mod.json", $$$"""
            {"Name":"","Priority":0,
             "Files":{
               "{{{root}}}/model/c0201e0100_top.mdl":"chara\\equipment\\e0100\\model\\c0201e0100_top.mdl",
               "{{{root}}}/material/v0001/mt_c0201e0100_top_a.mtrl":"chara\\equipment\\e0100\\material\\v0001\\mt_c0201e0100_top_a.mtrl",
               "{{{root}}}/texture/v01_c0201e0100_top_d.tex":"chara\\equipment\\e0100\\texture\\v01_c0201e0100_top_d.tex",
               "{{{root}}}/texture/v01_c0201e0100_dwn_n.tex":"shared\\n.tex",
               "{{{root}}}/model/c0201e0100_dwn.mdl":"legs.mdl",
               "{{{root}}}/material/v0001/mt_c0201e0100_dwn_a.mtrl":"legs.mtrl"},
             "FileSwaps":{},
             "Manipulations":[
               {"Type":"Eqp","Manipulation":{"Entry":123,"SetId":100,"Slot":"Body"}},
               {"Type":"Imc","Manipulation":{"Entry":{"MaterialId":1,"DecalId":0,"VfxId":0,"MaterialAnimationId":0,"AttributeMask":63,"SoundId":0},
                 "PrimaryId":100,"SecondaryId":0,"Variant":1,"ObjectType":"Equipment","EquipSlot":"Body","BodySlot":"Unknown"}},
               {"Type":"Imc","Manipulation":{"Entry":{"MaterialId":2,"DecalId":0,"VfxId":0,"MaterialAnimationId":0,"AttributeMask":1,"SoundId":0},
                 "PrimaryId":100,"SecondaryId":0,"Variant":2,"ObjectType":"Equipment","EquipSlot":"Body","BodySlot":"Unknown"}}]}
            """);
        mod.Json("group_001_extra.json", $$$"""
            {"Name":"Extra","Type":"Multi","Options":[{"Name":"Detail","Files":{"{{{root}}}/texture/v01_c0201e0100_top_s.tex":"detail.tex"}}]}
            """);
        mod.File("chara/equipment/e0100/model/c0201e0100_top.mdl", TestAssets.CreateMdl(material: "/mt_c0201e0100_top_a.mtrl"));
        mod.File("chara/equipment/e0100/material/v0001/mt_c0201e0100_top_a.mtrl",
            Mtrl($"{root}/texture/v01_c0201e0100_top_d.tex", $"{root}/texture/v01_c0201e0100_dwn_n.tex"));
        mod.File("chara/equipment/e0100/texture/v01_c0201e0100_top_d.tex", [1]);
        mod.File("shared/n.tex", [2]);
        mod.File("detail.tex", [3]);
        mod.File("legs.mdl", TestAssets.CreateMdl(material: "/mt_c0201e0100_dwn_a.mtrl"));
        mod.File("legs.mtrl", Mtrl($"{root}/texture/v01_c0201e0100_dwn_n.tex"));

        var game = StandardGame();
        var request = new GearConversionRequest(new GearItem(GearSlot.Body, 100, 1), new GearItem(GearSlot.Body, 300, 2),
            ConversionOutputMode.InPlace);
        var plan = new GearConversionPlanner(game).Plan(mod.Path, request);
        Assert.True(!plan.HasBlockers, string.Join("; ", plan.Diagnostics.Select(d => d.Message)));
        GearConversionExecutor.ApplyInPlace(plan, mod.Path);

        var result = PenumbraMod.Load(mod.Path);
        Assert.Equal(PenumbraModFormat.Legacy, result.Format);
        var files = result.Default.FileEntries().ToDictionary(e => e.Key, e => e.Local);
        Assert.True(!files.ContainsKey($"{root}/model/c0201e0100_top.mdl"), "the exclusive model moved");
        Assert.Equal("chara\\equipment\\e0300\\model\\c0201e0300_top.mdl", files["chara/equipment/e0300/model/c0201e0300_top.mdl"]);
        Assert.True(File.Exists(Path.Combine(mod.Path, "chara", "equipment", "e0300", "model", "c0201e0300_top.mdl")));
        Assert.True(!Directory.Exists(Path.Combine(mod.Path, "chara", "equipment", "e0100", "model")), "empty folders are pruned");
        // The leg texture is shared with the unconverted legs: kept, and duplicated for the target.
        Assert.Equal("shared\\n.tex", files[$"{root}/texture/v01_c0201e0100_dwn_n.tex"]);
        Assert.Equal("shared\\n.tex", files["chara/equipment/e0300/texture/v01_c0201e0300_dwn_top_n.tex"]);
        Assert.True(files.ContainsKey($"{root}/model/c0201e0100_dwn.mdl"));
        var material = File.ReadAllBytes(Path.Combine(mod.Path,
            GamePath.ToLocal(files["chara/equipment/e0300/material/v0001/mt_c0201e0300_top_a.mtrl"])));
        Assert.Equal(new[] { "chara/equipment/e0300/texture/v01_c0201e0300_top_d.tex", "chara/equipment/e0300/texture/v01_c0201e0300_dwn_top_n.tex" },
            MtrlFile.ReadTexturePaths(material).ToArray());

        // Group files keep their names; option content is retargeted.
        Assert.Equal("group_001_extra.json", result.Groups[0].LegacyFileName);
        Assert.Equal("chara/equipment/e0300/texture/v01_c0201e0300_top_s.tex", result.Groups[0].Containers[0].FileEntries().Single().Key);

        var manipulations = result.Default.Manipulations!.OfType<JsonObject>().ToList();
        var eqp = manipulations.Single(m => m["Type"]!.GetValue<string>() == "Eqp")["Manipulation"]!;
        Assert.Equal(300, eqp["SetId"]!.GetValue<int>());
        var imc = manipulations.Where(m => m["Type"]!.GetValue<string>() == "Imc").Select(m => m["Manipulation"]!).ToList();
        // Variant 1 (the converted one) now covers both target variants; variant 2 stays with the source.
        Assert.Equal(2, imc.Count(m => m["PrimaryId"]!.GetValue<int>() == 300));
        Assert.True(imc.Where(m => m["PrimaryId"]!.GetValue<int>() == 300).All(m => m["Entry"]!["AttributeMask"]!.GetValue<int>() == 63));
        Assert.Equal(1, imc.Count(m => m["PrimaryId"]!.GetValue<int>() == 100));

        var issues = GearConversionVerifier.Verify(mod.Path, request.Target, null, game);
        Assert.True(issues.Count == 0, string.Join("; ", issues.Select(i => i.Message)));
    }

    private static void AccessoryToEquipment()
    {
        using var mod = new TempDir();
        mod.Json("meta.json", """
            {"FileVersion":4,"Name":"Ring",
             "DefaultData":{"Files":{
               "chara/accessory/a0050/model/c0101a0050_rir.mdl":"ring.mdl",
               "chara/accessory/a0050/material/v0001/mt_c0101a0050_rir_a.mtrl":"ring.mtrl"},
              "Manipulations":[{"Type":"GlobalEqp","Manipulation":{"Type":"DoNotHideRingR","Condition":50}}]}}
            """);
        mod.File("ring.mdl", TestAssets.CreateMdl(material: "/mt_c0101a0050_rir_a.mtrl"));
        mod.File("ring.mtrl", Mtrl("chara/accessory/a0050/texture/v01_c0101a0050_rir_d.tex"));
        var game = new FakeGame();
        game.Files["chara/accessory/a0050/texture/v01_c0101a0050_rir_d.tex"] = [1];
        game.Files["chara/accessory/a0050/a0050.imc"] = Imc(1, 3, (_, _) => new ImcEntry(1, 0, 0x3FF, 0, 0, 0));

        var request = new GearConversionRequest(new GearItem(GearSlot.RFinger, 50, 1), new GearItem(GearSlot.Head, 60, 1),
            ConversionOutputMode.NewMod);
        var plan = new GearConversionPlanner(game).Plan(mod.Path, request);
        Assert.True(!plan.HasBlockers, string.Join("; ", plan.Diagnostics.Select(d => d.Message)));
        Assert.True(plan.Diagnostics.Any(d => d.Code == "metadata_not_transferable"));
        Assert.Equal("chara/equipment/e0060/model/c0101e0060_met.mdl",
            plan.GamePathMap["chara/accessory/a0050/model/c0101a0050_rir.mdl"]);
        using var output = new TempDir(create: false);
        GearConversionExecutor.WriteNewMod(plan, mod.Path, output.Path, "Ring Hat");
        var converted = PenumbraMod.Load(output.Path);
        var imc = converted.Default.Manipulations!.OfType<JsonObject>().Single(m => m["Type"]!.GetValue<string>() == "Imc");
        Assert.Equal("Equipment", imc["Manipulation"]!["ObjectType"]!.GetValue<string>());
        Assert.True(!converted.Default.Manipulations!.OfType<JsonObject>().Any(m => m["Type"]!.GetValue<string>() == "GlobalEqp"));
        var issues = GearConversionVerifier.Verify(output.Path, request.Target, request.Source, game);
        Assert.True(issues.Count == 0, string.Join("; ", issues.Select(i => i.Message)));
    }

    private static void EmptyPlanWithoutModContent()
    {
        using var mod = new TempDir();
        mod.Json("meta.json", """
            {"FileVersion":4,"Name":"Legs","DefaultData":{"Files":{"chara/equipment/e0200/model/c0201e0200_dwn.mdl":"legs.mdl"}}}
            """);
        mod.File("legs.mdl", TestAssets.CreateMdl(material: "/mt_c0201e0200_dwn_a.mtrl"));
        // The game has e0100 body models, but the mod never touches them.
        var plan = new GearConversionPlanner(StandardGame()).Plan(mod.Path, new GearConversionRequest(
            new GearItem(GearSlot.Body, 100, 1), new GearItem(GearSlot.Body, 300, 1), ConversionOutputMode.NewMod));
        Assert.True(plan.Diagnostics.Any(d => d.Code == "empty_plan" && d.IsBlocker));
    }

    private static void InPlaceV4ImcGroupAndSwaps()
    {
        using var mod = new TempDir();
        const string root = "chara/equipment/e0100";
        mod.Json("meta.json", $$$"""
            {"FileVersion":4,"Identifier":"{{{G1}}}","Name":"Imc Top",
             "DefaultData":{
               "Files":{"{{{root}}}/model/c0201e0100_top.mdl":"top.mdl",
                        "{{{root}}}/material/v0001/mt_c0201e0100_top_a.mtrl":"top.mtrl",
                        "{{{root}}}/texture/v01_c0201e0100_top_m.tex":"gone.tex"},
               "FileSwaps":{"{{{root}}}/texture/v01_c0201e0100_top_d.tex":"chara/common/texture/white.tex"}},
             "Groups":[
               {"Type":"Imc","Id":"{{{G2}}}","Name":"Parts",
                "Identifier":{"PrimaryId":100,"SecondaryId":0,"Variant":1,"ObjectType":"Equipment","EquipSlot":"Body","BodySlot":"Unknown"},
                "DefaultEntry":{"MaterialId":1,"DecalId":0,"VfxId":0,"MaterialAnimationId":0,"AttributeMask":0,"SoundId":0},
                "Options":[{"Id":"{{{O1}}}","Name":"Hood","AttributeMask":1}]},
               {"Type":"Imc","Id":"{{{G3}}}","Name":"Unrelated",
                "Identifier":{"PrimaryId":555,"SecondaryId":0,"Variant":1,"ObjectType":"Equipment","EquipSlot":"Body","BodySlot":"Unknown"},
                "DefaultEntry":{"MaterialId":1,"DecalId":0,"VfxId":0,"MaterialAnimationId":0,"AttributeMask":0,"SoundId":0},
                "Options":[{"Id":"{{{O2}}}","Name":"x","AttributeMask":1}]}]}
            """);
        mod.File("top.mdl", TestAssets.CreateMdl(material: "/mt_c0201e0100_top_a.mtrl"));
        mod.File("top.mtrl", Mtrl($"{root}/texture/v01_c0201e0100_top_d.tex", $"{root}/texture/v01_c0201e0100_top_m.tex"));

        var game = StandardGame();
        game.Files["chara/common/texture/white.tex"] = [1];
        game.Files[$"{root}/texture/v01_c0201e0100_top_m.tex"] = [1];
        var request = new GearConversionRequest(new GearItem(GearSlot.Body, 100, 1), new GearItem(GearSlot.Body, 300, 1),
            ConversionOutputMode.InPlace);
        var plan = new GearConversionPlanner(game).Plan(mod.Path, request);
        Assert.True(!plan.HasBlockers, string.Join("; ", plan.Diagnostics.Select(d => d.Message)));
        Assert.True(plan.Diagnostics.Any(d => d.Code == "missing_local_file"), "missing files are reported");
        GearConversionExecutor.ApplyInPlace(plan, mod.Path);

        var meta = Read(mod.Path, "meta.json");
        Assert.Equal(4, meta["FileVersion"]!.GetValue<int>());
        Assert.Equal(G1, meta["Identifier"]!.GetValue<string>());
        Assert.True(!File.Exists(Path.Combine(mod.Path, "default_mod.json")));
        var files = (JsonObject)meta["DefaultData"]!["Files"]!;
        Assert.True(files.ContainsKey("chara/equipment/e0300/model/c0201e0300_top.mdl"));
        Assert.True(!files.ContainsKey($"{root}/model/c0201e0100_top.mdl"));
        Assert.True(files.ContainsKey($"{root}/texture/v01_c0201e0100_top_m.tex"), "missing-file entries are left alone");
        var swaps = (JsonObject)meta["DefaultData"]!["FileSwaps"]!;
        Assert.Equal("chara/common/texture/white.tex", swaps["chara/equipment/e0300/texture/v01_c0201e0300_top_d.tex"]!.GetValue<string>());

        var parts = meta["Groups"]![0]!;
        Assert.Equal(300, parts["Identifier"]!["PrimaryId"]!.GetValue<int>());
        Assert.Equal(1, parts["Identifier"]!["Variant"]!.GetValue<int>());
        Assert.True(parts["AllVariants"]!.GetValue<bool>(), "a two-variant target is covered by one group");
        Assert.Equal(555, meta["Groups"]![1]!["Identifier"]!["PrimaryId"]!.GetValue<int>());
        // The material now points at the retargeted swap key and the untouched vanilla mask.
        var material = File.ReadAllBytes(Path.Combine(mod.Path,
            GamePath.ToLocal(files["chara/equipment/e0300/material/v0001/mt_c0201e0300_top_a.mtrl"]!.GetValue<string>())));
        Assert.Equal(new[] { "chara/equipment/e0300/texture/v01_c0201e0300_top_d.tex", $"{root}/texture/v01_c0201e0100_top_m.tex" },
            MtrlFile.ReadTexturePaths(material).ToArray());
        var issues = GearConversionVerifier.Verify(mod.Path, request.Target, null, game);
        Assert.True(issues.Count == 0, string.Join("; ", issues.Select(i => i.Message)));
    }

    private static void InPlaceTargetConflict()
    {
        using var mod = new TempDir();
        mod.Json("meta.json", """
            {"FileVersion":4,"Name":"Two tops","DefaultData":{"Files":{
              "chara/equipment/e0100/model/c0201e0100_top.mdl":"a.mdl",
              "chara/equipment/e0300/model/c0201e0300_top.mdl":"b.mdl"}}}
            """);
        mod.File("a.mdl", TestAssets.CreateMdl(material: "/mt_c0201e0100_top_a.mtrl"));
        mod.File("b.mdl", TestAssets.CreateMdl(material: "/mt_c0201e0300_top_a.mtrl"));
        var plan = new GearConversionPlanner(StandardGame()).Plan(mod.Path, new GearConversionRequest(
            new GearItem(GearSlot.Body, 100, 1), new GearItem(GearSlot.Body, 300, 1), ConversionOutputMode.InPlace));
        Assert.True(plan.Diagnostics.Any(d => d.Code == "target_conflict" && d.IsBlocker));
    }

    // ── Fixtures ────────────────────────────────────────────────────────────

    /// <summary>Game data for e0100 (source body, 1 variant) and e0300 (target, 2 variants).</summary>
    private static FakeGame StandardGame()
    {
        var game = new FakeGame();
        game.Files["chara/equipment/e0100/e0100.imc"] = Imc(1, 5, (_, _) => new ImcEntry(1, 0, 0x3FF, 0, 0, 0));
        game.Files["chara/equipment/e0300/e0300.imc"] = Imc(2, 5, (v, _) => new ImcEntry((byte)(v + 1), 0, 0x001, 7, 0, 0));
        // Both playable Midlanders have their own e0100 body model; the target has nothing.
        game.Files["chara/xls/equipmentdeformerparameter/c0101.eqdp"] = Eqdp((100, 0b1100));
        game.Files["chara/xls/equipmentdeformerparameter/c0201.eqdp"] = Eqdp((100, 0b1100));
        game.Files["chara/equipment/e0100/model/c0101e0100_top.mdl"] = TestAssets.CreateMdl(material: "/mt_c0101e0100_top_a.mtrl");
        game.Files["chara/equipment/e0100/material/v0001/mt_c0101e0100_top_a.mtrl"] =
            Mtrl("chara/equipment/e0100/texture/v01_c0101e0100_top_n.tex");
        game.Files["chara/equipment/e0100/texture/v01_c0101e0100_top_n.tex"] = [9];
        return game;
    }

    private static byte[] Mtrl(params string[] textures)
        => BuildMultiTexture(textures, "uv", "cs", "character.shpk");

    private static byte[] BuildMultiTexture(string[] textures, string map, string colorSet, string shader)
    {
        using var strings = new MemoryStream();
        var all = textures.Append(map).Append(colorSet).ToArray();
        var offsets = new List<short>();
        foreach (var value in all)
        {
            offsets.Add((short)strings.Position);
            strings.Write(Encoding.UTF8.GetBytes(value + "\0"));
        }
        var shaderOffset = (ushort)strings.Position;
        strings.Write(Encoding.UTF8.GetBytes(shader + "\0"));
        while (strings.Length % 4 != 0) strings.WriteByte(0);

        using var output = new MemoryStream();
        using var writer = new BinaryWriter(output);
        writer.Write(0x01030000);
        writer.Write((ushort)0);
        writer.Write((ushort)0);
        writer.Write((ushort)strings.Length);
        writer.Write(shaderOffset);
        writer.Write((byte)textures.Length);
        writer.Write((byte)1);
        writer.Write((byte)1);
        writer.Write((byte)0);
        foreach (var offset in offsets) { writer.Write(offset); writer.Write((ushort)0); }
        writer.Write(strings.ToArray());
        writer.Write(new byte[] { 1, 2, 3, 4 });
        var bytes = output.ToArray();
        BinaryPrimitives.WriteUInt16LittleEndian(bytes.AsSpan(4), (ushort)bytes.Length);
        return bytes;
    }

    private static byte[] Imc(int variants, int parts, Func<int, int, ImcEntry> entry)
    {
        var bytes = new byte[4 + (variants + 1) * parts * 6];
        BinaryPrimitives.WriteUInt16LittleEndian(bytes, (ushort)variants);
        BinaryPrimitives.WriteUInt16LittleEndian(bytes.AsSpan(2), (ushort)((1 << parts) - 1));
        for (var row = 0; row <= variants; row++)
        for (var part = 0; part < parts; part++)
        {
            var e = entry(row, part);
            var offset = 4 + (row * parts + part) * 6;
            bytes[offset] = e.MaterialId;
            bytes[offset + 1] = e.DecalId;
            BinaryPrimitives.WriteUInt16LittleEndian(bytes.AsSpan(offset + 2), (ushort)(e.AttributeMask | (e.SoundId << 10)));
            bytes[offset + 4] = e.VfxId;
            bytes[offset + 5] = e.MaterialAnimationId;
        }
        return bytes;
    }

    private static byte[] Eqdp(params (ushort SetId, ushort Entry)[] entries)
    {
        const int blockSize = 160, blockCount = 4;
        var bytes = new byte[6 + blockCount * 2 + blockCount * blockSize * 2];
        BinaryPrimitives.WriteUInt16LittleEndian(bytes.AsSpan(2), blockSize);
        BinaryPrimitives.WriteUInt16LittleEndian(bytes.AsSpan(4), blockCount);
        for (var i = 0; i < blockCount; i++)
            BinaryPrimitives.WriteUInt16LittleEndian(bytes.AsSpan(6 + i * 2), (ushort)(i * blockSize));
        foreach (var (set, entry) in entries)
            BinaryPrimitives.WriteUInt16LittleEndian(bytes.AsSpan(6 + blockCount * 2 + set * 2), entry);
        return bytes;
    }

    private static byte[] Eqp(params (ushort SetId, ulong Entry)[] entries)
    {
        const int blockSize = 160, blocks = 4;
        var bytes = new byte[blocks * blockSize * 8];
        BinaryPrimitives.WriteUInt64LittleEndian(bytes, 0b1111);
        foreach (var (set, entry) in entries)
            BinaryPrimitives.WriteUInt64LittleEndian(bytes.AsSpan(set * 8), entry);
        return bytes;
    }

    private static JsonObject Read(string directory, string file)
        => (JsonObject)JsonNode.Parse(File.ReadAllText(Path.Combine(directory, file)))!;

    private sealed class FakeGame : IGameFileProvider
    {
        public Dictionary<string, byte[]> Files { get; } = new(StringComparer.OrdinalIgnoreCase);
        public byte[]? ReadFile(string gamePath) => Files.GetValueOrDefault(GamePath.Normalize(gamePath));
    }

    private sealed class TempDir : IDisposable
    {
        public TempDir(bool create = true)
        {
            Path = System.IO.Path.Combine(System.IO.Path.GetTempPath(), "apmc-gear-" + Guid.NewGuid().ToString("N"));
            if (create) Directory.CreateDirectory(Path);
        }

        public string Path { get; }

        public void Json(string relative, string text) => File(relative, Encoding.UTF8.GetBytes(text));

        public void File(string relative, byte[] bytes)
        {
            var full = System.IO.Path.Combine(Path, relative.Replace('/', System.IO.Path.DirectorySeparatorChar));
            Directory.CreateDirectory(System.IO.Path.GetDirectoryName(full)!);
            System.IO.File.WriteAllBytes(full, bytes);
        }

        public void Dispose()
        {
            try { if (Directory.Exists(Path)) Directory.Delete(Path, true); } catch (IOException) { }
        }
    }
}

internal static class Assert
{
    public static void Equal<T>(T expected, T actual)
    {
        if (expected is System.Collections.IEnumerable e && actual is System.Collections.IEnumerable a && expected is not string)
        {
            var left = e.Cast<object?>().ToArray();
            var right = a.Cast<object?>().ToArray();
            if (left.SequenceEqual(right)) return;
            throw new Exception($"Expected [{string.Join(", ", left)}], got [{string.Join(", ", right)}].");
        }
        if (!EqualityComparer<T>.Default.Equals(expected, actual))
            throw new Exception($"Expected {expected}, got {actual}.");
    }

    public static void True(bool value, string message = "Expected true.")
    {
        if (!value) throw new Exception(message);
    }

    public static void Throws<T>(Action action) where T : Exception
    {
        try { action(); }
        catch (T) { return; }
        throw new Exception($"Expected {typeof(T).Name}.");
    }
}
