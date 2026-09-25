using System.Collections.Immutable;
using System.Text;
using System.Text.Json.Nodes;
using UniversalModConverter.Core;

/// <summary>Offering a skin's or face's textures and materials to further races and IDs.</summary>
internal static class TextureFanOutTests
{
    public static (string Name, Action Run)[] All =>
    [
        ("Fan-out roots: textures, plus materials for faces and skins", Detection),
        ("Skin targets are the races that load a skin of their own", SkinTargets),
        ("Add paths: every target beside the source, in the same containers", AddPaths),
        ("New groups: a toggle per ID for Default, a copy of each option group", NewGroups),
        ("New groups: a combining group cannot be copied", NewGroupsRejectCombining),
        ("Materials: each target gets its own copy pointing at its own textures", RetargetMaterials),
        ("Materials: shared as they are when not retargeted", ShareMaterials),
        ("As a new mod: planned as the whole mod with the paths added", AsNewMod),
        ("Tags count the textures a root's materials load", MaterialTextureTags),
    ];

    private static string[] Names(AssetContents contents) => contents.Tags().Select(t => t.Name()).ToArray();

    private const string SkinBase = "chara/human/c0201/obj/body/b0001/texture/c0201b0001_base.tex";
    private const string SkinNorm = "chara/human/c0201/obj/body/b0001/texture/c0201b0001_norm.tex";
    private const string SkinMtrl = "chara/human/c0201/obj/body/b0001/material/v0001/mt_c0201b0001_a.mtrl";
    private const string FaceBase = "chara/human/c1401/obj/face/f0001/texture/c1401f0001_fac_base.tex";
    private const string FaceMask = "chara/human/c1401/obj/face/f0001/texture/c1401f0001_fac_mask.tex";
    private const string FaceEtc  = "chara/human/c1401/obj/face/f0001/texture/c1401f0001_etc_base.tex";
    private const string FaceMtrl = "chara/human/c1401/obj/face/f0001/material/mt_c1401f0001_fac_a.mtrl";

    private static CustomizationPathEndpoint Skin(ushort race) => new(AssetKind.Body, race, 1);
    private static CustomizationPathEndpoint Face(ushort race, ushort id) => new(AssetKind.Face, race, id);

    private static TextureFanOutRequest Request(CustomizationPathEndpoint source, TextureFanOutLayout layout,
        bool retarget, params CustomizationPathEndpoint[] targets)
        => new(source, [.. targets], ConversionOutputMode.AddToMod, layout, retarget, "test");

    private static void Detection()
    {
        using var mod = new TempDir();
        const string hairTex = "chara/human/c0101/obj/hair/h0101/texture/c0101h0101_hir_norm.tex";
        const string hairMtrl = "chara/human/c0101/obj/hair/h0101/material/v0001/mt_c0101h0101_hir_a.mtrl";
        const string faceMdl = "chara/human/c0801/obj/face/f0002/model/c0801f0002_fac.mdl";
        const string faceTex = "chara/human/c0801/obj/face/f0002/texture/c0801f0002_fac_base.tex";
        mod.Json("meta.json", $$$"""
            {"FileVersion":4,"Name":"Mixed","DefaultData":{"Files":{
              "{{{SkinBase}}}":"a.tex","{{{FaceBase}}}":"b.tex","{{{FaceMtrl}}}":"b.mtrl",
              "{{{hairTex}}}":"c.tex","{{{faceMdl}}}":"d.mdl","{{{faceTex}}}":"d.tex"} } }
            """);
        var loaded = PenumbraMod.Load(mod.Path);

        Assert.True(CustomizationDetection.CanFanOut(loaded, Skin(201)), "a skin retexture fans out");
        Assert.Equal(AssetContents.Texture, CustomizationDetection.Contents(loaded, Skin(201)));
        Assert.True(CustomizationDetection.CanFanOut(loaded, Face(1401, 1)), "a face material and texture fan out");
        Assert.Equal(new[] { "Material", "Texture" }, Names(CustomizationDetection.Contents(loaded, Face(1401, 1))));
        Assert.True(CustomizationDetection.CanFanOut(loaded, new(AssetKind.Hair, 101, 101)), "hair textures alone fan out");
        Assert.True(!CustomizationDetection.CanFanOut(loaded, Face(801, 2)), "a model never fans out");
        Assert.Equal(new[] { "Model", "Texture" }, Names(CustomizationDetection.Contents(loaded, Face(801, 2))));
        Assert.True(!(AssetContents.None | AssetContents.Other).Tags().Any(), "only models, materials and textures are tagged");

        mod.Json("meta.json", $$$"""
            {"FileVersion":4,"Name":"Hair","DefaultData":{"Files":{"{{{hairTex}}}":"c.tex","{{{hairMtrl}}}":"c.mtrl"} } }
            """);
        Assert.True(!CustomizationDetection.CanFanOut(PenumbraMod.Load(mod.Path), new(AssetKind.Hair, 101, 101)),
            "hair materials are routed through shared roots and are converted instead");
    }

    private static void SkinTargets()
    {
        Assert.Equal(new ushort[] { 201, 401, 1401, 1601, 1801 },
            CustomizationTargets.AllowedRaces(AssetKind.Body, 201, AssetKind.Body).ToArray());
        Assert.Equal(new ushort[] { 101, 301, 901, 1301, 1501, 1701 },
            CustomizationTargets.AllowedRaces(AssetKind.Body, 101, AssetKind.Body).ToArray());
        // A skin mod written for a race that borrows another's skin still lists that race itself.
        var miqote = CustomizationTargets.AllowedRaces(AssetKind.Body, 801, AssetKind.Body);
        Assert.True(miqote.Contains((ushort)801) && !miqote.Contains((ushort)601), "the source race, not other borrowers");
        Assert.True(CustomizationTargets.BlockReason(AssetKind.Body, 201, AssetKind.Body, 801)?.Contains("Midlander Female") == true,
            "a borrower is pointed at the skin it wears");
        Assert.Equal(new ushort[] { 201, 601, 801 }, CustomizationPaths.SkinUsers(201).ToArray());
        Assert.Equal(new ushort[] { 401, 1001 }, CustomizationPaths.SkinUsers(401).ToArray());
        Assert.Equal(new ushort[] { 1101, 1201 }, CustomizationPaths.SkinUsers(1101).ToArray());
        Assert.Equal("Miqo'te", RaceNames.Race(801));
    }

    private static void AddPaths()
    {
        using var mod = new TempDir();
        const string auRaBase = "chara/human/c1401/obj/body/b0001/texture/c1401b0001_base.tex";
        mod.Json("meta.json", $$$"""
            {"FileVersion":4,"Name":"Skin",
             "DefaultData":{"Files":{"{{{SkinBase}}}":"skin/base.tex","{{{auRaBase}}}":"au/base.tex"} },
             "Groups":[{"Name":"Body type","Type":"Single","Priority":3,"DefaultSettings":0,"Options":[
               {"Name":"Bibo","Files":{"{{{SkinNorm}}}":"bibo/norm.tex"} },
               {"Name":"Gen3","Files":{"{{{SkinNorm}}}":"gen3/norm.tex"} } ] } ] }
            """);
        foreach (var file in new[] { "skin/base.tex", "au/base.tex", "bibo/norm.tex", "gen3/norm.tex" }) mod.File(file, [1]);

        var plan = new TextureFanOutPlanner(new FakeGame()).Plan(mod.Path,
            Request(Skin(201), TextureFanOutLayout.AddPathsToOptions, true, Skin(1401), Skin(1801), Skin(201)));
        Assert.True(!plan.HasBlockers, string.Join("; ", plan.Diagnostics.Select(d => d.Message)));
        Assert.Equal(0, plan.Files.Count);
        Assert.Equal(1, plan.Result.Groups.Count);

        var defaults = plan.Result.Default.Files!;
        Assert.Equal("skin/base.tex", Json.GetString(defaults[SkinBase]));
        Assert.Equal("skin/base.tex", Json.GetString(defaults[SkinBase.Replace("c0201", "c1801")]));
        Assert.Equal("au/base.tex", Json.GetString(defaults[auRaBase]));
        Assert.True(plan.Diagnostics.Any(d => d.Code == "path_exists" && !d.IsBlocker), "an existing path is kept with a note");

        var options = plan.Result.Groups[0].Containers;
        Assert.Equal("bibo/norm.tex", Json.GetString(options[0].Files![SkinNorm.Replace("c0201", "c1401")]));
        Assert.Equal("gen3/norm.tex", Json.GetString(options[1].Files![SkinNorm.Replace("c0201", "c1801")]));
        Assert.Equal("gen3/norm.tex", Json.GetString(options[1].Files![SkinNorm]));

        GearConversionExecutor.ApplyInPlace(plan, mod.Path);
        var applied = PenumbraMod.Load(mod.Path);
        Assert.Equal(3, applied.Groups[0].Containers[0].Files!.Count);
        Assert.Equal(0, plan.Verify(mod.Path).Count);
    }

    private static void NewGroups()
    {
        using var mod = new TempDir();
        mod.Json("meta.json", $$$"""
            {"FileVersion":4,"Name":"Face",
             "DefaultData":{"Files":{"{{{FaceBase}}}":"base.tex"} },
             "Groups":[
               {"Name":"Makeup","Type":"Single","Priority":5,"DefaultSettings":2,"Options":[
                 {"Name":"None"},
                 {"Name":"Red","Description":"Red lips","Files":{"{{{FaceMask}}}":"red.tex"} },
                 {"Name":"Blue","Files":{"{{{FaceMask}}}":"blue.tex"} } ] },
               {"Name":"Extras","Type":"Multi","Priority":2,"DefaultSettings":2,"Options":[
                 {"Name":"Freckles","Files":{"chara/common/texture/freckles.tex":"freckles.tex"} },
                 {"Name":"Scar","Priority":4,"Files":{"{{{FaceEtc}}}":"scar.tex"} } ] } ] }
            """);
        foreach (var file in new[] { "base.tex", "red.tex", "blue.tex", "freckles.tex", "scar.tex" }) mod.File(file, [1]);

        var plan = new TextureFanOutPlanner(new FakeGame()).Plan(mod.Path,
            Request(Face(1401, 1), TextureFanOutLayout.NewGroups, true, Face(1401, 3), Face(1401, 2), Face(801, 1)));
        Assert.True(!plan.HasBlockers, string.Join("; ", plan.Diagnostics.Select(d => d.Message)));

        var added = plan.Result.Groups.Skip(2).ToList();
        Assert.Equal(new[]
            {
                "Miqo'te Female", "Makeup · Miqo'te Female", "Extras · Miqo'te Female",
                "Au Ra Female", "Makeup · Au Ra Female", "Extras · Au Ra Female",
            },
            added.Select(g => g.Name).ToArray());
        Assert.Equal(new[] { 6, 7, 8, 9, 10, 11 }, added.Select(g => Json.GetInt(g.Node["Priority"], 0)).ToArray());

        // Default's paths: one toggle per ID, all on.
        var toggle = added[3];
        Assert.Equal("Multi", toggle.Type);
        Assert.Equal(new[] { "Face 2", "Face 3" }, toggle.Options.Select(o => Json.GetString(o["Name"])).ToArray());
        Assert.Equal(3, Json.GetInt(toggle.Node["DefaultSettings"], 0));
        Assert.Equal("base.tex", Json.GetString(toggle.Containers[0].Files![FaceBase.Replace("f0001", "f0002")]));

        // An option group: copied with the options that hold the source's paths, and its default.
        var makeup = added[4];
        Assert.Equal("Single", makeup.Type);
        Assert.Equal(new[] { "-", "Red", "Blue" }, makeup.Options.Select(o => Json.GetString(o["Name"])).ToArray());
        Assert.Equal(2, Json.GetInt(makeup.Node["DefaultSettings"], -1));
        Assert.Equal("Red lips", Json.GetString(makeup.Containers[1].Node["Description"]));
        Assert.Equal(2, makeup.Containers[1].Files!.Count); // Face 2 and Face 3 alike
        Assert.Equal(0, makeup.Containers[0].Files!.Count);

        var extras = added[2];
        Assert.Equal(new[] { "Scar" }, extras.Options.Select(o => Json.GetString(o["Name"])).ToArray());
        Assert.Equal(1, Json.GetInt(extras.Node["DefaultSettings"], 0));
        Assert.Equal(4, Json.GetInt(extras.Containers[0].Node["Priority"], 0));
        Assert.Equal("scar.tex", Json.GetString(extras.Containers[0].Files![
            "chara/human/c0801/obj/face/f0001/texture/c0801f0001_etc_base.tex"]));

        // The source is untouched.
        Assert.Equal(1, plan.Result.Default.Files!.Count);
        Assert.Equal(3, plan.Result.Groups[0].Containers.Count);

        GearConversionExecutor.ApplyInPlace(plan, mod.Path);
        Assert.Equal(8, PenumbraMod.Load(mod.Path).Groups.Count);
        Assert.Equal(0, plan.Verify(mod.Path).Count);
    }

    private static void NewGroupsRejectCombining()
    {
        using var mod = new TempDir();
        mod.Json("meta.json", $$$"""
            {"FileVersion":4,"Name":"Face","Groups":[
              {"Name":"Combo","Type":"Combining","Options":[{"Name":"A"}],
               "Containers":[{"Files":{} },{"Files":{"{{{FaceBase}}}":"base.tex"} }] } ] }
            """);
        mod.File("base.tex", [1]);

        var plan = new TextureFanOutPlanner(new FakeGame()).Plan(mod.Path,
            Request(Face(1401, 1), TextureFanOutLayout.NewGroups, true, Face(1401, 2)));
        Assert.True(plan.Diagnostics.Any(d => d.Code == "uncopyable_group" && d.IsBlocker), "a combining group blocks");

        var inPlace = new TextureFanOutPlanner(new FakeGame()).Plan(mod.Path,
            Request(Face(1401, 1), TextureFanOutLayout.AddPathsToOptions, true, Face(1401, 2)));
        Assert.True(!inPlace.HasBlockers, "adding beside the source works for any group");
    }

    private static void RetargetMaterials()
    {
        using var mod = SkinWithMaterial();
        var game = new FakeGame();
        game.Files[SkinNorm.Replace("c0201", "c1401")] = [1];

        var plan = new TextureFanOutPlanner(game).Plan(mod.Path,
            Request(Skin(201), TextureFanOutLayout.AddPathsToOptions, true, Skin(1401), Skin(1801)));
        Assert.True(!plan.HasBlockers, string.Join("; ", plan.Diagnostics.Select(d => d.Message)));
        Assert.Equal(2, plan.Files.Count);

        var auRaKey = SkinMtrl.Replace("c0201", "c1401");
        var auRaLocal = Json.GetString(plan.Result.Default.Files![auRaKey])!;
        Assert.Equal(GamePath.ToLocal(auRaKey), auRaLocal);
        var auRa = plan.Files.Single(f => f.Destination == auRaLocal);
        Assert.Equal(new[] { SkinBase.Replace("c0201", "c1401"), SkinNorm.Replace("c0201", "c1401"), "chara/common/texture/skin_m.tex" },
            MtrlFile.ReadTexturePaths(auRa.Content!).ToArray());

        // Viera have no such normal map in the game, so theirs keeps the source's.
        var vieraLocal = Json.GetString(plan.Result.Default.Files![SkinMtrl.Replace("c0201", "c1801")])!;
        Assert.Equal(new[] { SkinBase.Replace("c0201", "c1801"), SkinNorm, "chara/common/texture/skin_m.tex" },
            MtrlFile.ReadTexturePaths(plan.Files.Single(f => f.Destination == vieraLocal).Content!).ToArray());
        Assert.True(plan.Diagnostics.Any(d => d.Code == "material_texture_missing" && !d.IsBlocker), "the kept path is noted");

        GearConversionExecutor.ApplyInPlace(plan, mod.Path);
        Assert.Equal(0, plan.Verify(mod.Path).Count);
        Assert.True(File.Exists(Path.Combine(mod.Path, auRaLocal)), "the material copy is written");
    }

    private static void ShareMaterials()
    {
        using var mod = SkinWithMaterial();
        var plan = new TextureFanOutPlanner(new FakeGame()).Plan(mod.Path,
            Request(Skin(201), TextureFanOutLayout.AddPathsToOptions, false, Skin(1401)));
        Assert.True(!plan.HasBlockers, string.Join("; ", plan.Diagnostics.Select(d => d.Message)));
        Assert.Equal(0, plan.Files.Count);
        Assert.Equal("m/skin.mtrl", Json.GetString(plan.Result.Default.Files![SkinMtrl.Replace("c0201", "c1401")]));
    }

    /// <summary>
    /// A new mod starts as a copy of the whole mod, so it is planned exactly like adding to this one:
    /// the source's paths stay, and the definition keeps everything else the mod has.
    /// </summary>
    private static void AsNewMod()
    {
        using var mod = SkinWithMaterial();
        var inPlace = Request(Skin(201), TextureFanOutLayout.NewGroups, true, Skin(1401));
        var planner = new TextureFanOutPlanner(new FakeGame());
        var plan = planner.Plan(mod.Path, inPlace with { Mode = ConversionOutputMode.NewMod });
        Assert.True(!plan.HasBlockers, string.Join("; ", plan.Diagnostics.Select(d => d.Message)));
        Assert.Equal(2, plan.Result.Default.Files!.Count);
        Assert.Equal("Au Ra Female", plan.Result.Groups.Single().Name);
        Assert.Equal(1, plan.Files.Count);
        Assert.Equal(planner.Plan(mod.Path, inPlace).Outputs.Count, plan.Outputs.Count);

        Assert.Throws<ArgumentException>(() => planner.Plan(mod.Path, inPlace with { Mode = ConversionOutputMode.InPlace }));
    }

    /// <summary>
    /// A skin material that loads textures the mod keeps outside the body root still brings them
    /// to every race it is added for, so the root is tagged with both.
    /// </summary>
    private static void MaterialTextureTags()
    {
        using var mod = new TempDir();
        mod.Json("meta.json", $$$"""
            {"FileVersion":4,"Name":"Skin","DefaultData":{"Files":{
              "{{{SkinMtrl}}}":"m/skin.mtrl","chara/bibo_mid_base.tex":"m/base.tex"} } }
            """);
        mod.File("m/skin.mtrl", TestAssets.BuildMtrl("chara/bibo_mid_base.tex", "chara/common/texture/skin_m.tex"));
        mod.File("m/base.tex", [1]);
        var loaded = PenumbraMod.Load(mod.Path);

        Assert.Equal(new[] { "Material" }, Names(CustomizationDetection.Contents(loaded, Skin(201))));
        Assert.Equal(new[] { "Material", "Texture" }, Names(CustomizationDetection.Affected(loaded, mod.Path, Skin(201))));
        Assert.True(CustomizationDetection.CanFanOut(loaded, Skin(201)), "a borrowed texture does not change what fans out");

        // A material that only loads the game's own textures brings nothing of the mod's along.
        mod.Json("meta.json", $$$"""
            {"FileVersion":4,"Name":"Skin","DefaultData":{"Files":{"{{{SkinMtrl}}}":"m/skin.mtrl"} } }
            """);
        Assert.Equal(new[] { "Material" }, Names(CustomizationDetection.Affected(PenumbraMod.Load(mod.Path), mod.Path, Skin(201))));
    }

    private static TempDir SkinWithMaterial()
    {
        var mod = new TempDir();
        mod.Json("meta.json", $$$"""
            {"FileVersion":4,"Name":"Skin","DefaultData":{"Files":{"{{{SkinMtrl}}}":"m/skin.mtrl","{{{SkinBase}}}":"m/base.tex"} } }
            """);
        mod.File("m/skin.mtrl", TestAssets.BuildMtrl(SkinBase, SkinNorm, "chara/common/texture/skin_m.tex"));
        mod.File("m/base.tex", [1]);
        return mod;
    }

    private sealed class FakeGame : IGameFileProvider
    {
        public Dictionary<string, byte[]> Files { get; } = new(StringComparer.OrdinalIgnoreCase);
        public byte[]? ReadFile(string gamePath) => Files.GetValueOrDefault(GamePath.Normalize(gamePath));
    }

    private sealed class TempDir : IDisposable
    {
        public TempDir()
        {
            Path = System.IO.Path.Combine(System.IO.Path.GetTempPath(), "umc-fanout-" + Guid.NewGuid().ToString("N"));
            Directory.CreateDirectory(Path);
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
