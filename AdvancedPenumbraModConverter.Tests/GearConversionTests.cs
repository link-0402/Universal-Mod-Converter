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
        ("Pre-1.7 mods are rejected with a readable message", OutdatedFormatRejected),
        ("Backup retention expires by age and count, never a revertable one", BackupRetentionRules),
        ("Race codes are described by name", RaceNaming),
        ("Game metadata parsers (IMC, EQDP, EQP)", MetadataParsers),
        ("MDL v5 same-length material rewrite", MdlV5Rewrite),
        ("v4 new mod: cross-slot conversion with vanilla dependencies", NewModCrossSlotV4),
        ("In place: shared resources are kept, exclusive ones move", InPlaceSameSlot),
        ("Add to mod: the original keeps working beside the converted item", AddToModKeepsSource),
        ("Two conversions merge into one mod", MergeTwoConversions),
        ("A conversion that overlaps another is rejected and rolled back", MergeRejectsOverlap),
        ("Accessory to equipment conversion", AccessoryToEquipment),
        ("Races with a model get an EQDP entry on a target without one", EqdpForRaceModels),
        ("Item the mod does not change is rejected", EmptyPlanWithoutModContent),
        ("v4 in place: IMC group, swaps, missing files", InPlaceV4ImcGroupAndSwaps),
        ("In place refuses to overwrite existing target paths", InPlaceTargetConflict),
        ("Customization detection skips roots that only hold borrowed textures", CustomizationBorrowedTextures),
        ("Skin textures are a root of their own and fan out to other races", SkinTextureRoots),
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
        Assert.True(converted.SourceFile?.EndsWith("top.mdl", StringComparison.OrdinalIgnoreCase) == true,
            "The model knows the mod file it came from, for the preview on a character.");
        Assert.Equal(top, converted.SourceGamePaths.Single());
        Assert.Equal((ushort?)201, converted.GenderRace);
        Assert.True(models.All(m => m.GenderRace != 101), "no vanilla model is added for a race the mod does not cover");

        using var output = new TempDir(create: false);
        GearConversionExecutor.WriteNewMod(plan, mod.Path, output.Path, "Converted");
        GearOutputModels.ApplyRemovals(output.Path,
            new Dictionary<string, MeshRemoval> { [converted.Local] = new([1], converted.Groups.Count) });

        var written = MdlFile.Read(File.ReadAllBytes(Path.Combine(output.Path, converted.Local)));
        Assert.Equal(1, written.Meshes.Length);
        Assert.Equal("/mt_c0201e0300_glv_a.mtrl", written.Materials[written.Meshes[0].MaterialIndex]);
        // The removed part's skin material goes too: the game would still try to load it.
        Assert.Equal(1, written.Materials.Length);
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

    /// <summary>
    /// A skin mod replaces only textures under obj/body. It must be detected, recognised as
    /// texture-only (the condition for writing one file under several races), retargeted by
    /// path and file name, and kept within its gender.
    /// </summary>
    private static void SkinTextureRoots()
    {
        using var mod = new TempDir();
        const string skin = "chara/human/c0201/obj/body/b0001/texture/--c0201b0001_base.tex";
        mod.Json("meta.json", $$$"""
            {"FileVersion":4,"Name":"Skin","DefaultData":{"Files":{"{{{skin}}}":"skin.tex"} } }
            """);
        mod.File("skin.tex", [1]);

        var loaded = PenumbraMod.Load(mod.Path);
        var root = CustomizationDetection.FindRoots(loaded, mod.Path).Single();
        Assert.Equal(new CustomizationPathEndpoint(AssetKind.Body, 201, 1), root);
        Assert.True(CustomizationDetection.IsTextureOnly(loaded, root), "a skin retexture is texture-only");

        // Each extra race gets the same file under its own path; the file name follows the race.
        var highlander = CustomizationPaths.Rewrite(skin, root, root with { GenderRace = 401 });
        Assert.Equal("chara/human/c0401/obj/body/b0001/texture/--c0401b0001_base.tex", highlander);

        // Bodies are shaped per gender, so skins stay on their side of it.
        Assert.True(CustomizationTargets.BlockReason(AssetKind.Body, 201, AssetKind.Body, 101) != null);
        Assert.True(CustomizationTargets.BlockReason(AssetKind.Body, 201, AssetKind.Body, 401) == null);

        // A root with a model is not texture-only, so it never offers the fan-out.
        mod.Json("meta.json", $$$"""
            {"FileVersion":4,"Name":"Skin","DefaultData":{"Files":{"{{{skin}}}":"skin.tex",
              "chara/human/c0201/obj/body/b0001/model/c0201b0001_top.mdl":"body.mdl"} } }
            """);
        Assert.True(!CustomizationDetection.IsTextureOnly(PenumbraMod.Load(mod.Path), root));
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

    /// <summary>
    /// Plan messages name races; mod JSON spells them Penumbra's way. Mixing the two would
    /// either produce unreadable warnings or JSON that does not round-trip.
    /// </summary>
    private static void RaceNaming()
    {
        Assert.Equal("Midlander Female", RaceNames.Name(201));
        Assert.Equal("Midlander Female (c0201)", RaceNames.Describe(201));
        Assert.Equal("Au Ra Male (c1301)", RaceNames.Describe(1301));
        Assert.Equal("Miqo'te Female (c0801)", RaceNames.Describe(801));

        // Not a playable code: the description collapses to the code rather than inventing a name.
        Assert.Equal("c0202", RaceNames.Name(202));
        Assert.Equal("c0202", RaceNames.Describe(202));

        // Penumbra's own spellings stay as Penumbra writes them.
        Assert.Equal("Miqote", GenderRaces.Names(801).Race);
        Assert.Equal("AuRa", GenderRaces.Names(1301).Race);
    }

    /// <summary>
    /// Deleting the wrong backup loses the only untouched copy of somebody's mod, so both caps
    /// and the protection rule are pinned here.
    /// </summary>
    private static void BackupRetentionRules()
    {
        var now = new DateTime(2026, 6, 1, 12, 0, 0, DateTimeKind.Utc);
        BackupFolder At(string name, int daysAgo) => new(name, now.AddDays(-daysAgo));

        // Newest three are inside both caps; the fourth is only pushed out by the count cap.
        var folders = new[] { At("a", 0), At("b", 1), At("c", 2), At("d", 3) };
        Assert.Equal(new[] { "d" },
            BackupRetention.Expired(folders, new HashSet<string>(), keepDays: 14, keepCount: 3, now).ToArray());

        // Age alone expires a backup even when the count has room to spare.
        Assert.Equal(new[] { "old" },
            BackupRetention.Expired([At("new", 1), At("old", 20)], new HashSet<string>(), 14, 10, now).ToArray());

        // A backup a revert still depends on survives both caps however old it is, and the
        // newest two still survive the count cap: a pinned old backup must not cost the user
        // their most recent ones.
        var kept = new HashSet<string>(StringComparer.OrdinalIgnoreCase) { "old" };
        Assert.Equal(new[] { "c" },
            BackupRetention.Expired([At("a", 0), At("b", 1), At("c", 2), At("old", 99)], kept, 14, 2, now).ToArray());

        // Inside the newest N, a protected backup does take one of the slots, so the folder
        // holds at most N backups plus whatever protection forces it to keep.
        var pinnedNewest = new HashSet<string>(StringComparer.OrdinalIgnoreCase) { "a" };
        Assert.Equal(new[] { "c" },
            BackupRetention.Expired([At("a", 0), At("b", 1), At("c", 2)], pinnedNewest, 14, 2, now).ToArray());

        // Nonsense limits are clamped rather than deleting everything.
        Assert.Equal(new[] { "b" },
            BackupRetention.Expired([At("a", 0), At("b", 0)], new HashSet<string>(), keepDays: 0, keepCount: 0, now).ToArray());
    }

    /// <summary>Penumbra migrated every mod to FileVersion 4 on the 1.7 release, so the
    /// converter reads only that layout and says so in words the user can act on.</summary>
    private static void OutdatedFormatRejected()
    {
        using var dir = new TempDir();
        dir.Json("meta.json", """{"FileVersion":3,"Name":"Old","Custom":1}""");
        dir.Json("default_mod.json", """{"Name":"","Priority":0,"Files":{"a/b.tex":"b.tex"},"FileSwaps":{},"Manipulations":[]}""");
        dir.Json("group_001_colors.json", """{"Name":"Colors","Type":"Multi","Options":[{"Name":"A","Files":{"c.tex":"c.tex"}}]}""");

        var thrown = Assert.Throws<OutdatedModFormatException>(() => PenumbraMod.Load(dir.Path));
        Assert.True(thrown.Message.Contains("Open it in Penumbra"), thrown.Message);

        // A version newer than we understand is a different failure, and must not claim to be old.
        dir.Json("meta.json", """{"FileVersion":5,"Name":"New"}""");
        Assert.True(Assert.Throws<InvalidDataException>(() => PenumbraMod.Load(dir.Path)).Message.Contains("newer"));
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
        // Only the models the mod ships: the male race keeps the target's own model.
        Assert.True(!keys.Contains("chara/equipment/e0300/model/c0101e0300_glv.mdl"), "no vanilla model is added");
        Assert.True(!keys.Contains("chara/equipment/e0300/material/v0001/mt_c0101e0300_glv_a.mtrl"), "no vanilla material is added");
        Assert.True(!keys.Any(k => k.Contains("e0100") || k.Contains("e0200")), string.Join(", ", keys));

        // Contents reference the retargeted resources.
        var model = File.ReadAllBytes(Path.Combine(output.Path, files["chara/equipment/e0300/model/c0201e0300_glv.mdl"]!.GetValue<string>()));
        Assert.Equal("/mt_c0201e0300_glv_a.mtrl", ResourceReferences.ReadMdlMaterials(model).Single());
        var material = File.ReadAllBytes(Path.Combine(output.Path,
            files["chara/equipment/e0300/material/v0001/mt_c0201e0300_glv_a.mtrl"]!.GetValue<string>()));
        Assert.Equal("chara/equipment/e0300/texture/v01_c0201e0300_glv_d.tex", MtrlFile.ReadTexturePaths(material).Single());

        // Metadata: explicit EQDP moved to the hands bits, unrelated entries dropped, IMC for every variant.
        var manipulations = meta["DefaultData"]!["Manipulations"]!.AsArray().OfType<JsonObject>().ToList();
        var eqdp = manipulations.Where(m => m["Type"]!.GetValue<string>() == "Eqdp").ToList();
        var female = eqdp.Single(m => m["Manipulation"]!["Gender"]!.GetValue<string>() == "Female")["Manipulation"]!;
        Assert.Equal("300", female["SetId"]!.GetValue<string>());
        Assert.Equal("Hands", female["Slot"]!.GetValue<string>());
        Assert.Equal(48, female["Entry"]!.GetValue<int>());
        Assert.True(eqdp.All(m => m["Manipulation"]!["Gender"]!.GetValue<string>() == "Female"),
            "races the mod ships no model for keep the target's EQDP");
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

    private static void InPlaceSameSlot()
    {
        using var mod = new TempDir();
        const string root = "chara/equipment/e0100";
        mod.Json("meta.json", $$$"""
            {"FileVersion":4,"Name":"Shared",
             "DefaultData":{
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
                 "PrimaryId":100,"SecondaryId":0,"Variant":2,"ObjectType":"Equipment","EquipSlot":"Body","BodySlot":"Unknown"}}]},
             "Groups":[{"Name":"Extra","Type":"Multi","Id":"{{{G2}}}",
                        "Options":[{"Id":"{{{O1}}}","Name":"Detail",
                                    "Files":{"{{{root}}}/texture/v01_c0201e0100_top_s.tex":"detail.tex"}}]}]}
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

        // Groups keep their identity; option content is retargeted.
        Assert.Equal("Extra", result.Groups[0].Name);
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

    /// <summary>A mod with two independent items, used to test running conversions together.</summary>
    private static TempDir TwoItemMod()
    {
        var mod = new TempDir();
        mod.Json("meta.json", """
            {"FileVersion":4,"Name":"Two",
             "DefaultData":{"Files":{
               "chara/equipment/e0100/model/c0201e0100_top.mdl":"top.mdl",
               "chara/equipment/e0100/material/v0001/mt_c0201e0100_top_a.mtrl":"top.mtrl",
               "chara/equipment/e0200/model/c0201e0200_glv.mdl":"glv.mdl",
               "chara/equipment/e0200/material/v0001/mt_c0201e0200_glv_a.mtrl":"glv.mtrl"}}}
            """);
        mod.File("top.mdl", TestAssets.CreateMdl(material: "/mt_c0201e0100_top_a.mtrl"));
        mod.File("top.mtrl", Mtrl("chara/equipment/e0100/texture/v01_c0201e0100_top_d.tex"));
        mod.File("glv.mdl", TestAssets.CreateMdl(material: "/mt_c0201e0200_glv_a.mtrl"));
        mod.File("glv.mtrl", Mtrl("chara/equipment/e0200/texture/v01_c0201e0200_glv_d.tex"));
        return mod;
    }

    private static GearConversionRequest Swap(GearSlot slot, ushort from, ushort to)
        => new(new GearItem(slot, from, 1), new GearItem(slot, to, 1), ConversionOutputMode.InPlace);

    /// <summary>
    /// Two conversions planned together share one mod definition and one pool of file names.
    /// Planned apart they would each rewrite the whole definition, and the second would undo
    /// the first.
    /// </summary>
    private static void MergeTwoConversions()
    {
        using var mod = TwoItemMod();
        var game = StandardGame();

        MergedModPlan Plan(bool bodyFirst)
        {
            var context = new ModPlanContext(mod.Path, ConversionOutputMode.InPlace, shared: true);
            var merger  = new ModPlanMerger(context);
            var order = bodyFirst
                ? new[] { ("body", "chara/equipment/e0100", GearSlot.Body, (ushort)100, (ushort)300),
                          ("hands", "chara/equipment/e0200", GearSlot.Hands, (ushort)200, (ushort)400) }
                : [("hands", "chara/equipment/e0200", GearSlot.Hands, (ushort)200, (ushort)400),
                   ("body", "chara/equipment/e0100", GearSlot.Body, (ushort)100, (ushort)300)];
            foreach (var (name, root, slot, from, to) in order)
            {
                var entry = merger.Add(name, [root], ctx => new GearConversionPlanner(game).Plan(ctx, Swap(slot, from, to)));
                Assert.True(!entry.Rejected, string.Join("; ", entry.Diagnostics.Select(d => d.Message)));
            }

            context.RunFinalizers();
            return merger.Build();
        }

        var merged = Plan(bodyFirst: true);
        Assert.True(!merged.HasBlockers, string.Join("; ", merged.Diagnostics.Select(d => d.Message)));

        // One pool of names: no two operations may land on the same file.
        var destinations = merged.Files.Select(f => f.Destination.ToLowerInvariant()).ToList();
        Assert.Equal(destinations.Count, destinations.Distinct().Count());

        // The same conversions in the other order are a different run, and must not be mistaken
        // for a preview of this one.
        Assert.True(merged.Fingerprint() != Plan(bodyFirst: false).Fingerprint(),
            "reordering the run must invalidate its fingerprint");

        GearConversionExecutor.ApplyInPlace(merged, mod.Path);
        var files = PenumbraMod.Load(mod.Path).Default.FileEntries()
            .ToDictionary(e => GamePath.Normalize(e.Key), e => e.Local);
        Assert.True(files.ContainsKey("chara/equipment/e0300/model/c0201e0300_top.mdl"), "the first conversion survived");
        Assert.True(files.ContainsKey("chara/equipment/e0400/model/c0201e0400_glv.mdl"), "the second conversion survived");
    }

    /// <summary>
    /// Two conversions of the same item cannot both happen. The second is rejected, and the
    /// definition must come back to exactly what the first left behind — a half-applied second
    /// conversion would be worse than either outcome.
    /// </summary>
    private static void MergeRejectsOverlap()
    {
        using var mod = TwoItemMod();
        var game = StandardGame();

        string Run(bool withOverlap)
        {
            var context = new ModPlanContext(mod.Path, ConversionOutputMode.InPlace, shared: true);
            var merger  = new ModPlanMerger(context);
            merger.Add("body → 300", ["chara/equipment/e0100"],
                ctx => new GearConversionPlanner(game).Plan(ctx, Swap(GearSlot.Body, 100, 300)));
            if (withOverlap)
            {
                var second = merger.Add("body → 500", ["chara/equipment/e0100"],
                    ctx => new GearConversionPlanner(game).Plan(ctx, Swap(GearSlot.Body, 100, 500)));
                Assert.True(second.Rejected, "converting the same item twice must be rejected");
                Assert.True(second.Diagnostics.Any(d => d.Code == "queue_conflict" && d.IsBlocker),
                    string.Join("; ", second.Diagnostics.Select(d => d.Message)));
            }

            context.RunFinalizers();
            var merged = merger.Build();
            Assert.Equal(withOverlap, merged.HasBlockers);
            return ModFingerprint.DefinitionHash(merged.Result);
        }

        // The rejected conversion left nothing behind: both runs produce the same definition.
        Assert.Equal(Run(withOverlap: false), Run(withOverlap: true));
    }

    /// <summary>
    /// The promise of the additive mode is that the source item still works afterwards and the
    /// mod's existing toggles govern both. That means: no file is moved or deleted, every source
    /// key survives, the target keys land in the same containers, a file that needs no rewrite is
    /// shared by both keys, one that does gets a second copy, and the source metadata stays.
    /// </summary>
    private static void AddToModKeepsSource()
    {
        using var mod = new TempDir();
        const string root = "chara/equipment/e0100";
        mod.Json("meta.json", $$$"""
            {"FileVersion":4,"Name":"Shared",
             "DefaultData":{
              "Files":{
               "{{{root}}}/model/c0201e0100_top.mdl":"top.mdl",
               "{{{root}}}/material/v0001/mt_c0201e0100_top_a.mtrl":"top.mtrl",
               "{{{root}}}/texture/v01_c0201e0100_top_d.tex":"top_d.tex"},
              "Manipulations":[
               {"Type":"Eqp","Manipulation":{"Entry":123,"SetId":100,"Slot":"Body"}}]},
             "Groups":[{"Name":"Imc","Type":"Imc","Id":"{{{G2}}}",
                        "Identifier":{"PrimaryId":100,"SecondaryId":0,"Variant":1,
                                      "ObjectType":"Equipment","EquipSlot":"Body","BodySlot":"Unknown"},
                        "DefaultEntry":{"MaterialId":1,"DecalId":0,"VfxId":0,"MaterialAnimationId":0,
                                        "AttributeMask":63,"SoundId":0},
                        "Options":[{"Id":"{{{O1}}}","Name":"Bow","AttributeMask":1}]}]}
            """);
        mod.File("top.mdl", TestAssets.CreateMdl(material: "/mt_c0201e0100_top_a.mtrl"));
        mod.File("top.mtrl", Mtrl($"{root}/texture/v01_c0201e0100_top_d.tex"));
        mod.File("top_d.tex", [1]);

        var game = StandardGame();
        var request = new GearConversionRequest(new GearItem(GearSlot.Body, 100, 1), new GearItem(GearSlot.Body, 300, 1),
            ConversionOutputMode.AddToMod);
        var plan = new GearConversionPlanner(game).Plan(mod.Path, request);
        Assert.True(!plan.HasBlockers, string.Join("; ", plan.Diagnostics.Select(d => d.Message)));

        // Nothing leaves: an additive plan only ever writes.
        Assert.True(plan.Files.All(f => f.Operation == LocalFileOperation.Write),
            string.Join("; ", plan.Files.Select(f => $"{f.Operation} {f.Destination}")));

        GearConversionExecutor.ApplyInPlace(plan, mod.Path);
        var result = PenumbraMod.Load(mod.Path);
        var files  = result.Default.FileEntries().ToDictionary(e => GamePath.Normalize(e.Key), e => e.Local);

        // Both items are present, in the same container.
        Assert.True(files.ContainsKey($"{root}/model/c0201e0100_top.mdl"), "the original model keeps its key");
        Assert.True(files.ContainsKey("chara/equipment/e0300/model/c0201e0300_top.mdl"), "the converted model is added");

        // The texture has no embedded paths, so one file serves both keys.
        Assert.Equal("top_d.tex", files[$"{root}/texture/v01_c0201e0100_top_d.tex"]);
        Assert.Equal("top_d.tex", files["chara/equipment/e0300/texture/v01_c0201e0300_top_d.tex"]);

        // The model does, so the converted copy is a separate file and the original is untouched.
        var convertedModel = files["chara/equipment/e0300/model/c0201e0300_top.mdl"];
        Assert.True(!string.Equals(convertedModel, "top.mdl", StringComparison.OrdinalIgnoreCase),
            "the converted model must not overwrite the original");
        Assert.Equal("/mt_c0201e0100_top_a.mtrl",
            MdlFile.Read(File.ReadAllBytes(Path.Combine(mod.Path, "top.mdl"))).Strings.First(s => s.EndsWith(".mtrl")));

        // The source's own metadata survives next to the converted item's.
        var eqp = result.Default.Manipulations!.OfType<JsonObject>()
            .Where(m => m["Type"]!.GetValue<string>() == "Eqp")
            .Select(m => m["Manipulation"]!["SetId"]!.GetValue<int>())
            .Order().ToArray();
        Assert.Equal(new[] { 100, 300 }, eqp);

        // One IMC group cannot drive two items, so the converted one got its own copy and said so.
        Assert.Equal(2, result.Groups.Count(g => g.IsImc));
        Assert.Equal(100, result.Groups[0].Node["Identifier"]!["PrimaryId"]!.GetValue<int>());
        Assert.Equal(300, result.Groups[1].Node["Identifier"]!["PrimaryId"]!.GetValue<int>());
        Assert.True(!string.Equals(result.Groups[0].Node["Id"]!.GetValue<string>(),
                                   result.Groups[1].Node["Id"]!.GetValue<string>(), StringComparison.Ordinal),
            "the duplicated group needs its own identity");
        Assert.True(plan.Diagnostics.Any(d => d.Code == "additive_imc_group_duplicated"),
            "the one thing additive mode cannot share has to be called out");
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

    private static void EqdpForRaceModels()
    {
        using var mod = new TempDir();
        const string src = "chara/equipment/e0349";
        mod.Json("meta.json", $$$"""
            {"FileVersion":4,"Name":"Skirt",
             "DefaultData":{"Files":{"{{{src}}}/model/c0201e0349_dwn.mdl":"female.mdl"}},
             "Groups":[{"Name":"meow","Type":"Single","Options":[{"Name":"on","Files":{
               "{{{src}}}/model/c1801e0349_dwn.mdl":"viera.mdl",
               "{{{src}}}/material/v0001/mt_c1801e0349_dwn_a.mtrl":"viera.mtrl"}}]}]}
            """);
        mod.File("viera.mdl", TestAssets.CreateMdl(material: "/mt_c1801e0349_dwn_a.mtrl"));
        mod.File("viera.mtrl", Mtrl("chara/common/texture/white.tex"));
        mod.File("female.mdl", TestAssets.CreateMdl(material: "/mt_c0201e0349_dwn_a.mtrl"));

        var game = new FakeGame();
        game.Files[$"{src}/e0349.imc"] = Imc(1, 5, (_, _) => new ImcEntry(1, 0, 0x3FF, 0, 0, 0));
        // Midlander women have their own vanilla skirt; the bracelet has no female model at all.
        game.Files["chara/xls/charadb/equipmentdeformerparameter/c0201.eqdp"] = Eqdp((349, 0b11 << 6));
        game.Files["chara/xls/charadb/accessorydeformerparameter/c0201.eqdp"] = Eqdp((130, 0));
        game.Files[$"{src}/material/v0001/mt_c0201e0349_dwn_a.mtrl"] = Mtrl("chara/common/texture/white.tex");
        game.Files["chara/common/texture/white.tex"] = [1];

        var request = new GearConversionRequest(new GearItem(GearSlot.Legs, 349, 1), new GearItem(GearSlot.Wrists, 130, 1),
            ConversionOutputMode.NewMod);
        var plan = new GearConversionPlanner(game).Plan(mod.Path, request);
        Assert.True(!plan.HasBlockers, string.Join("; ", plan.Diagnostics.Select(d => d.Message)));

        static JsonObject? EqdpFor(ModContainer container, ushort race)
            => container.Manipulations?.OfType<JsonObject>().SingleOrDefault(m =>
                m["Type"]!.GetValue<string>() == "Eqdp" && m["Manipulation"] is JsonObject e &&
                Json.GetInt(e["SetId"], 0) == 130 &&
                GenderRaces.TryParse(Json.GetString(e["Race"]), Json.GetString(e["Gender"]), out var code) && code == race);

        // The mod's female skirt is told to the game as the bracelet's female model; the source's
        // entry (model and material) carries over.
        var defaults = plan.Result.Default;
        Assert.True(defaults.Files!.ContainsKey("chara/accessory/a0130/model/c0201a0130_wrs.mdl"), "The model is converted.");
        Assert.True(!defaults.Files.Any(p => p.Key.Contains("/c0101")), "No model is added for a race the mod does not cover.");
        var female = EqdpFor(defaults, 201) ?? throw new Exception("No EQDP entry for Midlander women.");
        Assert.Equal(3, Json.GetInt(female["Manipulation"]!["Entry"], 0) >> GearSlot.Wrists.EqdpShift() & 3);

        // A model only an option ships gets its entry in that option, material bit included.
        var option = plan.Result.Groups.Single().Containers.Single();
        Assert.True(EqdpFor(defaults, 1801) == null, "The option-only model does not change the default.");
        var viera = EqdpFor(option, 1801) ?? throw new Exception("No EQDP entry for Viera women in the option.");
        Assert.Equal(3, Json.GetInt(viera["Manipulation"]!["Entry"], 0) >> GearSlot.Wrists.EqdpShift() & 3);
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
        game.Files["chara/xls/charadb/equipmentdeformerparameter/c0101.eqdp"] = Eqdp((100, 0b1100));
        game.Files["chara/xls/charadb/equipmentdeformerparameter/c0201.eqdp"] = Eqdp((100, 0b1100));
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

    /// <summary>Returns the exception so callers can assert on its message or exact type.</summary>
    public static T Throws<T>(Action action) where T : Exception
    {
        try { action(); }
        catch (T caught) { return caught; }
        throw new Exception($"Expected {typeof(T).Name}.");
    }
}
