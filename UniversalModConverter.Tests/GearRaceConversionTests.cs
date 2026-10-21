using System.Numerics;
using System.Text.Json.Nodes;
using UniversalModConverter.Core;

/// <summary>Gear race conversions: an item's model for one race becomes another race's.</summary>
internal static partial class GearConversionTests
{
    private static (string Name, Action Run)[] RaceTests =>
    [
        ("Race conversion: a hat becomes another race's in a new mod, with its EQDP and EST entries", RaceConversionNewModHead),
        ("Race conversion: a skeleton the target race has of its own needs no copy and no entry", RaceConversionOwnSkeleton),
        ("Race conversion: the mod's own extra skeleton and EST entry go to the target race too", RaceConversionModSkeleton),
        ("Race conversion: an earring moves to another race in place, its EQDP entry with it", RaceConversionInPlaceAccessory),
        ("Race conversion: added to the mod, both races keep a model and an entry", RaceConversionAddToMod),
        ("Race conversion: another item, a body piece or a race without a model is refused", RaceConversionRefusals),
    ];

    private const string HatRoot = "chara/equipment/e0350";

    /// <summary>
    /// The mod's Midlander man hat goes to Au Ra women: the model is reshaped the way human.pbd
    /// deforms one into the other and renamed for the race, its material stays where both load it,
    /// and the race gets the model bit in its EQDP entry and the extra skeleton (with a copy of the
    /// Midlander one, which the race does not have) the hat's EST entry gives Midlanders.
    /// </summary>
    private static void RaceConversionNewModHead()
    {
        using var mod = HatMod();
        var game = HatGame();
        game.Files["chara/xls/charadb/extra_met.est"] = TestAssets.CreateEst((350, 101, 7));
        game.Files["chara/human/c0101/skeleton/met/m0007/skl_c0101m0007.sklb"] = [5];
        game.Files["chara/human/c0101/skeleton/met/m0007/phy_c0101m0007.phyb"] = [6];

        var plan = PlanRace(mod.Path, game, GearSlot.Head, 350, 101, 1401, ConversionOutputMode.NewMod);
        Assert.True(!plan.HasBlockers, string.Join("; ", plan.Diagnostics.Select(d => d.Message)));

        var files = plan.Result.Default.FileEntries().ToDictionary(e => GamePath.Normalize(e.Key), e => e.Local);
        Assert.True(files.ContainsKey($"{HatRoot}/model/c1401e0350_met.mdl"), $"the model is the race's: {string.Join(", ", files.Keys)}");
        Assert.True(!files.ContainsKey($"{HatRoot}/model/c0101e0350_met.mdl"), "a new mod holds only the converted race's model");
        Assert.True(files.ContainsKey($"{HatRoot}/material/v0001/mt_c0101e0350_met_a.mtrl"), "the material it loads comes along unchanged");

        // The model is deformed exactly as the race tree says, and nothing else about it changes.
        var written = plan.Files.Single(f => f.Destination.EndsWith(".mdl", StringComparison.OrdinalIgnoreCase));
        var expected = MdlFile.Read(HatModel());
        MdlRaceConverter.Convert(expected, RacialDeformationPlan.Create(new HumanPbd(game.Files["chara/xls/boneDeformer/human.pbd"]), 101, 1401)
            .Materialize(expected.Bones));
        Assert.True(written.Content!.AsSpan().SequenceEqual(expected.Write()), "the model is reshaped for Au Ra women");
        Assert.True(!written.Content!.AsSpan().SequenceEqual(HatModel()), "the reshape moves the model");

        var manipulations = plan.Result.Default.Manipulations!.OfType<JsonObject>().ToList();
        var eqdp = manipulations.Where(m => Json.StringEquals(m["Type"], "Eqdp")).Select(m => m["Manipulation"]!).ToList();
        Assert.Equal("AuRa|Female|3", string.Join(";", eqdp.Select(e => $"{e["Race"]}|{e["Gender"]}|{e["Entry"]}")));
        var est = manipulations.Single(m => Json.StringEquals(m["Type"], "Est"))["Manipulation"]!;
        Assert.Equal("AuRa|Female|350|7", $"{est["Race"]}|{est["Gender"]}|{est["SetId"]}|{est["Entry"]}");
        Assert.True(manipulations.Any(m => Json.StringEquals(m["Type"], "Imc")), "the item's own IMC entry comes along");

        // Au Ra women have no m0007, so the Midlander one is copied to their folder.
        const string skeleton = "chara/human/c1401/skeleton/met/m0007/skl_c1401m0007.sklb";
        const string physics = "chara/human/c1401/skeleton/met/m0007/phy_c1401m0007.phyb";
        Assert.True(files.ContainsKey(skeleton) && files.ContainsKey(physics), $"the skeleton is copied: {string.Join(", ", files.Keys)}");
        Assert.True(plan.Files.Any(f => f.Content is [5]) && plan.Files.Any(f => f.Content is [6]), "the copies hold the game's files");
        Assert.True(plan.Diagnostics.Any(d => d.Code == "race_skeleton_copied"), "the copy is pointed out");
        Assert.Equal(0, plan.Diagnostics.Count(d => d.Code == "race_bones_unresolved"));
    }

    /// <summary>
    /// Where the game gives the target race the hat's skeleton itself, the target loads its own:
    /// no entry and no copy. Neither is needed for a hat whose source race has no skeleton.
    /// </summary>
    private static void RaceConversionOwnSkeleton()
    {
        using var mod = HatMod();
        var game = HatGame();
        game.Files["chara/xls/charadb/extra_met.est"] = TestAssets.CreateEst((350, 101, 7), (350, 1401, 7));
        game.Files["chara/human/c0101/skeleton/met/m0007/skl_c0101m0007.sklb"] = [5];
        game.Files["chara/human/c1401/skeleton/met/m0007/skl_c1401m0007.sklb"] = [7];

        var plan = PlanRace(mod.Path, game, GearSlot.Head, 350, 101, 1401, ConversionOutputMode.NewMod);
        Assert.True(!plan.HasBlockers, string.Join("; ", plan.Diagnostics.Select(d => d.Message)));
        Assert.True(!plan.Result.Default.Manipulations!.OfType<JsonObject>().Any(m => Json.StringEquals(m["Type"], "Est")),
            "the race's own entry already names the skeleton");
        Assert.True(!plan.Result.Default.FileEntries().Any(e => e.Key.Contains("/skeleton/")), "nothing is copied");
        Assert.True(plan.Diagnostics.All(d => d.Code != "race_skeleton_copied"), "nothing to point out");
    }

    /// <summary>
    /// A hat the mod gives physics of its own (an EST entry and a skeleton for Midlander men) keeps
    /// them on Au Ra women when added to the mod: the entry is given to the race, and the race's
    /// skeleton path loads the mod's file, which stays where the Midlander one loads it too.
    /// </summary>
    private static void RaceConversionModSkeleton()
    {
        using var mod = new TempDir();
        const string skeleton = "chara/human/c0101/skeleton/met/m0900/skl_c0101m0900.sklb";
        mod.Json("meta.json", $$$"""
            {"FileVersion":4,"Name":"Physics hat","DefaultData":{
              "Files":{"{{{HatRoot}}}/model/c0101e0350_met.mdl":"hat.mdl",
                       "{{{HatRoot}}}/material/v0001/mt_c0101e0350_met_a.mtrl":"hat.mtrl",
                       "{{{skeleton}}}":"skel.sklb"},
              "Manipulations":[{"Type":"Est","Manipulation":{"Entry":900,"Gender":"Male","Race":"Midlander","SetId":350,"Slot":"Head"}}]}}
            """);
        mod.File("hat.mdl", HatModel());
        mod.File("hat.mtrl", Mtrl("chara/common/texture/white.tex"));
        mod.File("skel.sklb", [8]);

        var plan = PlanRace(mod.Path, HatGame(), GearSlot.Head, 350, 101, 1401, ConversionOutputMode.AddToMod);
        Assert.True(!plan.HasBlockers, string.Join("; ", plan.Diagnostics.Select(d => d.Message)));

        var files = plan.Result.Default.FileEntries().ToDictionary(e => GamePath.Normalize(e.Key), e => e.Local);
        Assert.Equal("skel.sklb", files.GetValueOrDefault("chara/human/c1401/skeleton/met/m0900/skl_c1401m0900.sklb"));
        Assert.Equal("skel.sklb", files.GetValueOrDefault(skeleton));
        var est = plan.Result.Default.Manipulations!.OfType<JsonObject>().Where(m => Json.StringEquals(m["Type"], "Est"))
            .Select(m => m["Manipulation"]!).Select(e => $"{e["Race"]}|{e["Gender"]}|{e["Entry"]}").Order().ToList();
        Assert.Equal("AuRa|Female|900;Midlander|Male|900", string.Join(";", est));
        // Au Ra women get a model of their own to load through the EQDP entry, which the game does not give them.
        var eqdp = plan.Result.Default.Manipulations!.OfType<JsonObject>().Single(m => Json.StringEquals(m["Type"], "Eqdp"))["Manipulation"]!;
        Assert.Equal("AuRa|Female|2", $"{eqdp["Race"]}|{eqdp["Gender"]}|{eqdp["Entry"]}");
    }

    /// <summary>
    /// Converted in place, an earring an option holds moves from Midlander men to Au Ra women:
    /// the option's model and its EQDP entry go to the new race, and the Midlander ones are gone.
    /// Accessories have no extra skeletons.
    /// </summary>
    private static void RaceConversionInPlaceAccessory()
    {
        using var mod = EarringMod();
        var plan = PlanRace(mod.Path, EarringGame(), GearSlot.Ears, 120, 101, 1401, ConversionOutputMode.InPlace);
        Assert.True(!plan.HasBlockers, string.Join("; ", plan.Diagnostics.Select(d => d.Message)));

        var option = plan.Result.Groups.Single().Containers.Single();
        var keys = option.FileEntries().Select(e => GamePath.Normalize(e.Key)).ToList();
        Assert.True(keys.Contains("chara/accessory/a0120/model/c1401a0120_ear.mdl"), $"the option holds the race's model: {string.Join(", ", keys)}");
        Assert.True(!keys.Contains("chara/accessory/a0120/model/c0101a0120_ear.mdl"), "the Midlander model moved away");
        var eqdp = option.Manipulations!.OfType<JsonObject>().Select(m => m["Manipulation"]!).ToList();
        Assert.Equal("AuRa|Female|3", string.Join(";", eqdp.Select(e => $"{e["Race"]}|{e["Gender"]}|{e["Entry"]}")));
        Assert.True(plan.Result.Default.Manipulations == null || !plan.Result.Default.Manipulations.OfType<JsonObject>()
            .Any(m => Json.StringEquals(m["Type"], "Est")), "an accessory gets no extra skeleton");
        Assert.True(plan.Files.Any(f => f.Operation == LocalFileOperation.Move), "the model file is renamed for the race");
    }

    /// <summary>
    /// Added to the mod, the Midlander earring stays as it was and the Au Ra one is added beside it,
    /// in the same option, with an EQDP entry of its own; a target race the game has no earring
    /// model for gets one with the model bit even without an entry to copy.
    /// </summary>
    private static void RaceConversionAddToMod()
    {
        using var mod = EarringMod(withEntry: false);
        var plan = PlanRace(mod.Path, EarringGame(), GearSlot.Ears, 120, 101, 1401, ConversionOutputMode.AddToMod);
        Assert.True(!plan.HasBlockers, string.Join("; ", plan.Diagnostics.Select(d => d.Message)));

        var option = plan.Result.Groups.Single().Containers.Single();
        var keys = option.FileEntries().Select(e => GamePath.Normalize(e.Key)).ToList();
        Assert.True(keys.Contains("chara/accessory/a0120/model/c1401a0120_ear.mdl") &&
                    keys.Contains("chara/accessory/a0120/model/c0101a0120_ear.mdl"), $"both races have a model: {string.Join(", ", keys)}");
        var eqdp = option.Manipulations!.OfType<JsonObject>().Single()["Manipulation"]!;
        Assert.Equal("AuRa|Female|2", $"{eqdp["Race"]}|{eqdp["Gender"]}|{eqdp["Entry"]}");
        Assert.True(plan.Files.Any(f => f.Operation == LocalFileOperation.Write &&
                                         f.Destination.Contains("c1401", StringComparison.OrdinalIgnoreCase)),
            "the reshaped copy gets a file of its own");
    }

    private static void RaceConversionRefusals()
    {
        using var mod = HatMod();
        var game = HatGame();

        var other = new GearConversionPlanner(game).Plan(mod.Path, new GearConversionRequest(
            new GearItem(GearSlot.Head, 350, 1), new GearItem(GearSlot.Head, 351, 1), ConversionOutputMode.NewMod)
            { Race = new GearRaceChange(101, 1401) });
        Assert.True(other.Diagnostics.Any(d => d.IsBlocker && d.Code == "race_conversion"), "another item is refused");

        var body = PlanRace(mod.Path, game, GearSlot.Body, 350, 101, 1401, ConversionOutputMode.NewMod);
        Assert.True(body.Diagnostics.Any(d => d.IsBlocker && d.Code == "race_conversion"), "a body piece is refused");

        var missing = PlanRace(mod.Path, game, GearSlot.Head, 350, 201, 1401, ConversionOutputMode.NewMod);
        Assert.True(missing.Diagnostics.Any(d => d.IsBlocker && d.Code == "race_model_missing"), "a race without a model is refused");

        var lalafell = PlanRace(mod.Path, game, GearSlot.Head, 350, 101, 1101, ConversionOutputMode.NewMod);
        Assert.True(lalafell.Diagnostics.Any(d => d.IsBlocker && d.Code == "race_conversion"), "Lalafell stay among Lalafell");
    }

    // ── Fixtures ────────────────────────────────────────────────────────────

    private static GearConversionPlan PlanRace(string directory, FakeGame game, GearSlot slot, ushort set,
        ushort from, ushort to, ConversionOutputMode mode)
        => new GearConversionPlanner(game).Plan(directory, new GearConversionRequest(
            new GearItem(slot, set, 1), new GearItem(slot, set, 1), mode) { Race = new GearRaceChange(from, to) });

    private static byte[] HatModel() => TestAssets.CreateMdl(boneName: "j_kao", material: "/mt_c0101e0350_met_a.mtrl");

    /// <summary>A Midlander man hat with an IMC entry and EQDP entries for both Midlanders.</summary>
    private static TempDir HatMod()
    {
        var mod = new TempDir();
        mod.Json("meta.json", $$$"""
            {"FileVersion":4,"Name":"Hat","DefaultData":{
              "Files":{"{{{HatRoot}}}/model/c0101e0350_met.mdl":"hat.mdl",
                       "{{{HatRoot}}}/material/v0001/mt_c0101e0350_met_a.mtrl":"hat.mtrl"},
              "Manipulations":[
               {"Type":"Imc","Manipulation":{"ObjectType":"Equipment","PrimaryId":350,"SecondaryId":0,"Variant":1,"EquipSlot":"Head",
                "BodySlot":"Unknown","Entry":{"MaterialId":1,"DecalId":0,"VfxId":0,"MaterialAnimationId":0,"AttributeMask":1023,"SoundId":0} } },
               {"Type":"Eqdp","Manipulation":{"Entry":3,"Gender":"Male","Race":"Midlander","SetId":350,"Slot":"Head"}},
               {"Type":"Eqdp","Manipulation":{"Entry":3,"Gender":"Female","Race":"Midlander","SetId":350,"Slot":"Head"}}]}}
            """);
        mod.File("hat.mdl", HatModel());
        mod.File("hat.mtrl", Mtrl("chara/common/texture/white.tex"));
        return mod;
    }

    /// <summary>Game data for hat e0350: Au Ra women have no model of their own, and their race tree moves the head.</summary>
    private static FakeGame HatGame()
    {
        var game = new FakeGame();
        game.Files[$"{HatRoot}/e0350.imc"] = Imc(1, 5, (_, _) => new ImcEntry(1, 0, 0x3FF, 0, 0, 0));
        game.Files["chara/xls/charadb/equipmentdeformerparameter/c0101.eqdp"] = Eqdp((350, 3));
        game.Files["chara/xls/charadb/equipmentdeformerparameter/c1401.eqdp"] = Eqdp((350, 0));
        game.Files["chara/xls/boneDeformer/human.pbd"] = TestAssets.CreatePbd(1401, "j_kao", Matrix4x4.CreateTranslation(0, 1, 0));
        game.Files["chara/common/texture/white.tex"] = [1];
        return game;
    }

    /// <summary>A Midlander man earring in a single-select option, with (or without) its EQDP entry there.</summary>
    private static TempDir EarringMod(bool withEntry = true)
    {
        var mod = new TempDir();
        var manipulations = withEntry
            ? ""","Manipulations":[{"Type":"Eqdp","Manipulation":{"Entry":3,"Gender":"Male","Race":"Midlander","SetId":120,"Slot":"Ears"}}]"""
            : string.Empty;
        mod.Json("meta.json", $$$"""
            {"FileVersion":4,"Name":"Earring","DefaultData":{},
             "Groups":[{"Name":"Earring","Type":"Single","Options":[{"Name":"On"{{{manipulations}}},"Files":{
               "chara/accessory/a0120/model/c0101a0120_ear.mdl":"c0101a0120_ear.mdl",
               "chara/accessory/a0120/material/v0001/mt_c0101a0120_ear_a.mtrl":"ear.mtrl"}}]}]}
            """);
        mod.File("c0101a0120_ear.mdl", TestAssets.CreateMdl(boneName: "j_mimi_l", material: "/mt_c0101a0120_ear_a.mtrl"));
        mod.File("ear.mtrl", Mtrl("chara/common/texture/white.tex"));
        return mod;
    }

    private static FakeGame EarringGame()
    {
        var game = new FakeGame();
        game.Files["chara/accessory/a0120/a0120.imc"] = Imc(1, 5, (_, _) => new ImcEntry(1, 0, 0x3FF, 0, 0, 0));
        game.Files["chara/xls/charadb/accessorydeformerparameter/c1401.eqdp"] = Eqdp((120, 0));
        game.Files["chara/xls/boneDeformer/human.pbd"] = TestAssets.CreatePbd(1401, "j_mimi_l", Matrix4x4.CreateScale(1.1f));
        game.Files["chara/common/texture/white.tex"] = [1];
        return game;
    }
}
