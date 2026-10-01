using System.Text;
using System.Text.Json.Nodes;
using UniversalModConverter.Core;

/// <summary>Merging two modpacks that were split from one mod.</summary>
internal static class ModMergerTests
{
    public static (string Name, Action Run)[] All =>
    [
        ("Merging modpacks: the top pack wins every overlap", TopPackWins),
        ("Merging modpacks: a group of each pack keeps its own IDs", DuplicateIdsAreReplaced),
        ("Merging modpacks: a renamed file never stands in for one of the same new name", RenamedFileKeepsItsSource),
        ("Merging modpacks: merged groups keep IDs every condition can find, and the top pack's options win", MergedGroupIds),
        ("Merging modpacks: IMC options pair by name and the lower pack's others are kept", ImcOptionsPairByName),
    ];

    /// <summary>
    /// Options of an IMC group pair by name, not by position: a top pack with fewer or reordered
    /// options must not delete the lower pack's, nor give one option another's ID, or the
    /// lower pack's conditions end up pointing at nothing, or at the wrong option.
    /// </summary>
    private static void ImcOptionsPairByName()
    {
        using var baseMod = new TempDir();
        using var overlay = new TempDir();
        const string baseImc = "aaaaaaaa-0000-0000-0000-000000000001", topImc = "bbbbbbbb-0000-0000-0000-000000000001";
        const string baseHood = "aaaaaaaa-0000-0000-0000-000000000002", baseCape = "aaaaaaaa-0000-0000-0000-000000000003";
        const string topCape = "bbbbbbbb-0000-0000-0000-000000000002";
        const string imc = """
            "Identifier":{"PrimaryId":100,"SecondaryId":0,"Variant":1,"ObjectType":"Equipment","EquipSlot":"Body","BodySlot":"Unknown"},
            "DefaultEntry":{"MaterialId":1,"DecalId":0,"VfxId":0,"MaterialAnimationId":0,"AttributeMask":0,"SoundId":0}
            """;

        baseMod.Json("meta.json", $$$"""
            {"FileVersion":4,"Name":"Base","Groups":[
              {"Type":"Imc","Id":"{{{baseImc}}}","Name":"Parts",{{{imc}}},
               "Options":[{"Id":"{{{baseHood}}}","Name":"Hood","AttributeMask":1},{"Id":"{{{baseCape}}}","Name":"Cape","AttributeMask":2}]},
              {"Type":"Multi","Id":"aaaaaaaa-0000-0000-0000-000000000004","Name":"Extras","Options":[
                {"Name":"Glow","Files":{},"Condition":{"Group":"{{{baseImc}}}","Options":["{{{baseHood}}}"]} } ] } ] }
            """);
        overlay.Json("meta.json", $$$"""
            {"FileVersion":4,"Name":"Top","Groups":[
              {"Type":"Imc","Id":"{{{topImc}}}","Name":"Parts",{{{imc.Replace("\"MaterialId\":1", "\"MaterialId\":2")}}},
               "Options":[{"Id":"{{{topCape}}}","Name":"Cape","AttributeMask":2}]} ] }
            """);

        var result = ModMerger.Plan(baseMod.Path, overlay.Path, "Merged").Result;
        var parts = result.Groups.Single(g => g.Name == "Parts");
        var byName = parts.Options.ToDictionary(o => Json.GetString(o["Name"])!, o => Json.GetString(o["Id"]));
        Assert.Equal(new[] { "Cape", "Hood" }, byName.Keys.Order().ToArray());
        Assert.Equal(baseCape, byName["Cape"]);   // not Hood's ID, which sits first in the lower pack
        Assert.Equal(baseHood, byName["Hood"]);   // not dropped: the lower pack's condition names it
        Assert.Equal(2, Json.GetInt(parts.Node["DefaultEntry"]?["MaterialId"], 0));

        var ids = result.Groups.SelectMany(g => g.Options.Select(o => Json.GetString(o["Id"]))).OfType<string>()
            .ToHashSet(StringComparer.OrdinalIgnoreCase);
        var glow = result.Groups.Single(g => g.Name == "Extras").Options.Single();
        Assert.True(glow["Condition"]?["Options"]?.AsArray().All(x => ids.Contains(Json.GetString(x) ?? "")) == true,
            "the lower pack's condition still finds its option");
    }

    /// <summary>
    /// A merged group keeps the lower pack's IDs, which its conditions refer to, and a condition
    /// of the top pack that named its own copy is pointed at the merged one: every reference must
    /// still resolve, or Penumbra refuses the mod. Options the top pack adds to a multi-select
    /// group win where both packs' are on, and start on when they did in their own pack.
    /// </summary>
    private static void MergedGroupIds()
    {
        using var baseMod = new TempDir();
        using var overlay = new TempDir();
        const string baseImc = "aaaaaaaa-0000-0000-0000-000000000001", baseImcOption = "aaaaaaaa-0000-0000-0000-000000000002";
        const string topImc = "bbbbbbbb-0000-0000-0000-000000000001", topImcOption = "bbbbbbbb-0000-0000-0000-000000000002";
        const string baseExtras = "aaaaaaaa-0000-0000-0000-000000000003", topExtras = "bbbbbbbb-0000-0000-0000-000000000003";
        const string imc = """
            "Identifier":{"PrimaryId":100,"SecondaryId":0,"Variant":1,"ObjectType":"Equipment","EquipSlot":"Body","BodySlot":"Unknown"}
            """;

        baseMod.Json("meta.json", $$$"""
            {"FileVersion":4,"Name":"Base","Groups":[
              {"Type":"Imc","Id":"{{{baseImc}}}","Name":"Parts",{{{imc}}},
               "DefaultEntry":{"MaterialId":1,"DecalId":0,"VfxId":0,"MaterialAnimationId":0,"AttributeMask":0,"SoundId":0},
               "Options":[{"Id":"{{{baseImcOption}}}","Name":"Hood","AttributeMask":1}]},
              {"Type":"Multi","Id":"{{{baseExtras}}}","Name":"Extras","DefaultSettings":1,"Options":[
                {"Name":"Glow","Files":{"{{{Texture}}}":"t\\glow.tex"},
                 "Condition":{"Group":"{{{baseImc}}}","Options":["{{{baseImcOption}}}"]} } ] } ] }
            """);
        baseMod.File("t/glow.tex", "glow"u8.ToArray());
        overlay.Json("meta.json", $$$"""
            {"FileVersion":4,"Name":"Top","Groups":[
              {"Type":"Imc","Id":"{{{topImc}}}","Name":"Parts",{{{imc}}},
               "DefaultEntry":{"MaterialId":2,"DecalId":0,"VfxId":0,"MaterialAnimationId":0,"AttributeMask":0,"SoundId":0},
               "Options":[{"Id":"{{{topImcOption}}}","Name":"Hood","AttributeMask":1}]},
              {"Type":"Multi","Id":"{{{topExtras}}}","Name":"Extras","DefaultSettings":2,"Options":[
                {"Name":"Glow","Files":{} },
                {"Name":"Shine","Files":{"{{{Texture}}}":"t\\shine.tex"},
                 "Condition":{"Group":"{{{topImc}}}","Options":["{{{topImcOption}}}"],"Parent":"{{{topExtras}}}"} } ] } ] }
            """);
        overlay.File("t/shine.tex", "shine"u8.ToArray());

        var result = ModMerger.Plan(baseMod.Path, overlay.Path, "Merged").Result;
        var parts = result.Groups.Single(g => g.Name == "Parts");
        Assert.Equal(baseImc, Json.GetString(parts.Node["Id"]));
        Assert.Equal(baseImcOption, Json.GetString(parts.Options.First()["Id"]));
        Assert.Equal(2, Json.GetInt(parts.Node["DefaultEntry"]?["MaterialId"], 0));

        var ids = result.Groups.SelectMany(g => g.Options.Select(o => Json.GetString(o["Id"])).Append(Json.GetString(g.Node["Id"])))
            .OfType<string>().ToHashSet(StringComparer.OrdinalIgnoreCase);
        var extras = result.Groups.Single(g => g.Name == "Extras");
        foreach (var option in extras.Options)
            foreach (var reference in option["Condition"]?.AsObject().Select(p => p.Value)
                         .SelectMany(v => v is JsonArray a ? a.Select(x => Json.GetString(x)) : [Json.GetString(v)]) ?? [])
                Assert.True(reference != null && ids.Contains(reference), $"'{Json.GetString(option["Name"])}' refers to {reference}, which is gone");

        var names = extras.Options.Select(o => Json.GetString(o["Name"])).ToArray();
        Assert.Equal(new[] { "Glow", "Shine" }, names);
        var priorities = extras.Options.Select(o => Json.GetInt(o["Priority"], 0)).ToArray();
        Assert.True(priorities[1] > priorities[0],
            "the top pack's option wins where both are on");
        Assert.True(Json.TryGetULong(extras.Node["DefaultSettings"], out var defaults) && defaults == 0b11,
            $"Glow stays on and Shine starts on as it did in its own pack: {extras.Node["DefaultSettings"]}");
    }

    /// <summary>
    /// A file of the top pack that clashes with the base's is renamed, here to <c>a_2.tex</c>.
    /// When the top pack has a file of that name as well, it must be compared with the renamed
    /// file's own bytes, not with itself, or it is taken for a copy and never written.
    /// </summary>
    private static void RenamedFileKeepsItsSource()
    {
        using var baseMod = new TempDir();
        using var overlay = new TempDir();
        const string first = "chara/equipment/e0100/texture/v01_c0101e0100_top_d.tex";
        const string second = "chara/equipment/e0100/texture/v01_c0101e0100_top_n.tex";

        baseMod.Json("meta.json", $$$"""
            {"FileVersion":4,"Name":"Base","DefaultData":{"Files":{"{{{first}}}":"t\\a.tex"} } }
            """);
        baseMod.File("t/a.tex", "base a"u8.ToArray());
        overlay.Json("meta.json", $$$"""
            {"FileVersion":4,"Name":"Top","DefaultData":{"Files":{"{{{first}}}":"t\\a.tex"}},
             "Groups":[{"Name":"Normals","Type":"Single","Options":[{"Name":"On","Files":{"{{{second}}}":"t\\a_2.tex"}}]}]}
            """);
        overlay.File("t/a.tex", "top a"u8.ToArray());
        overlay.File("t/a_2.tex", "top a_2"u8.ToArray());

        var plan = ModMerger.Plan(baseMod.Path, overlay.Path, "Merged");
        var firstLocal = Json.GetString(plan.Result.Default.Files![first])!;
        var secondLocal = Json.GetString(plan.Result.Groups.Single(g => g.Name == "Normals").Containers[0].Files![second])!;
        Assert.True(!string.Equals(firstLocal, secondLocal, StringComparison.OrdinalIgnoreCase),
            $"two different files share {firstLocal}");
        Assert.Equal(0, plan.SharedFiles);

        string CopiedFrom(string local) => plan.Copies
            .Single(c => string.Equals(c.Destination, local, StringComparison.OrdinalIgnoreCase)).SourceLocal;
        Assert.Equal("t\\a.tex", CopiedFrom(firstLocal));
        Assert.Equal("t\\a_2.tex", CopiedFrom(secondLocal));
    }

    private const string Model = "chara/equipment/e0100/model/c0101e0100_top.mdl";
    private const string Material = "chara/equipment/e0100/material/v0001/mt_c0101e0100_top_a.mtrl";
    private const string Texture = "chara/equipment/e0100/texture/v01_c0101e0100_top_d.tex";
    private const string GroupId = "22222222-2222-2222-2222-222222222222";

    private static void TopPackWins()
    {
        using var baseMod = new TempDir();
        using var overlay = new TempDir();
        using var output = new TempDir(create: false);

        baseMod.Json("meta.json", $$$"""
            {"FileVersion":4,"Name":"Base","Author":"Alice","ModTags":["gear"],
             "DefaultData":{"Files":{"{{{Model}}}":"a\\x.mdl","{{{Material}}}":"a\\m.mtrl"}},
             "Groups":[{"Name":"Colour","Type":"Single","Priority":3,"Options":[
                {"Name":"Red","Files":{"{{{Texture}}}":"t\\red.tex","{{{Model}}}":"a\\red.mdl"}},
                {"Name":"Blue","Files":{"{{{Texture}}}":"t\\blue.tex"}}]}]}
            """);
        baseMod.File("a/x.mdl", "base model"u8.ToArray());
        baseMod.File("a/red.mdl", "red model"u8.ToArray());
        baseMod.File("a/m.mtrl", "material"u8.ToArray());
        baseMod.File("t/red.tex", "red"u8.ToArray());
        baseMod.File("t/blue.tex", "blue"u8.ToArray());

        overlay.Json("meta.json", $$$"""
            {"FileVersion":4,"Name":"Upscale","Author":"Bob","ModTags":["upscale"],
             "DefaultData":{"Files":{"{{{Model}}}":"a\\x.mdl"}},
             "Groups":[
               {"Name":"Colour","Type":"Single","Priority":0,"Options":[
                 {"Name":"red","Files":{"{{{Texture}}}":"t\\red.tex"}},
                 {"Name":"Green","Files":{"{{{Texture}}}":"t\\green.tex"}}]},
               {"Name":"Extras","Type":"Multi","Priority":0,"Options":[{"Name":"Glow","Files":{}}]}]}
            """);
        overlay.File("a/x.mdl", "upscaled model"u8.ToArray());
        overlay.File("t/red.tex", "red"u8.ToArray());
        overlay.File("t/green.tex", "green"u8.ToArray());

        var plan = ModMerger.Plan(baseMod.Path, overlay.Path, "Merged");
        var result = plan.Result;

        Assert.Equal("Merged", result.Name);
        Assert.Equal("Alice, Bob", Json.GetString(result.Meta["Author"]));
        Assert.Equal(2, ((JsonArray)result.Meta["ModTags"]!).Count);

        // The upscaled model replaces the base's, under a new name since both used a\x.mdl.
        var modelLocal = Json.GetString(result.Default.Files![Model])!;
        Assert.Equal("a\\x_2.mdl", modelLocal);
        Assert.True(Json.GetString(result.Default.Files[Material]) == "a\\m.mtrl", "The base's material stays.");
        Assert.Equal(1, plan.RenamedFiles);

        // The base's Red option set the model too, and would have outranked the new default.
        var colour = result.Groups.Single(g => g.Name == "Colour");
        Assert.Equal(3, colour.Containers.Count);
        Assert.True(!colour.Containers[0].Files!.ContainsKey(Model), "The base option no longer overrides the model.");
        Assert.True(plan.Conflicts.Any(c => c.What == Model && c.Where.Contains("Red")), "The dropped entry is reported.");

        // Identical files are shared, different ones added.
        Assert.Equal(1, plan.SharedFiles);
        Assert.Equal("t\\green.tex", Json.GetString(colour.Containers[2].Files![Texture]));

        // Groups only the top pack has go above the base's.
        var extras = result.Groups.Single(g => g.Name == "Extras");
        Assert.Equal(4, Json.GetInt(extras.Node["Priority"], 0));
        Assert.Equal(1, plan.GroupsMerged);
        Assert.Equal(1, plan.GroupsAdded);

        ModMerger.Write(plan, output.Path);
        var written = PenumbraMod.Load(output.Path);
        Assert.Equal(2, written.Groups.Count);
        Assert.Equal("upscaled model", File.ReadAllText(Path.Combine(output.Path, "a", "x_2.mdl")));
        Assert.Equal("base model", File.ReadAllText(Path.Combine(output.Path, "a", "x.mdl")));
        Assert.True(File.Exists(Path.Combine(output.Path, "t", "green.tex")), "The top pack's own files are copied.");
    }

    private static void DuplicateIdsAreReplaced()
    {
        using var baseMod = new TempDir();
        using var overlay = new TempDir();

        baseMod.Json("meta.json", $$$"""
            {"FileVersion":4,"Name":"Base",
             "Groups":[{"Id":"{{{GroupId}}}","Name":"Style","Type":"Single","Options":[{"Name":"A"}]}]}
            """);
        overlay.Json("meta.json", $$$"""
            {"FileVersion":4,"Name":"Other",
             "Groups":[{"Id":"{{{GroupId}}}","Name":"Style","Type":"Multi","Options":[{"Name":"B"}]},
                       {"Name":"Child","Type":"Single","Options":[{"Name":"C","Condition":{"Group":"{{{GroupId}}}"}}]}]}
            """);

        var plan = ModMerger.Plan(baseMod.Path, overlay.Path, "Merged");
        var groups = plan.Result.Groups;
        Assert.Equal(3, groups.Count);
        var added = groups.Single(g => g.Type == "Multi");
        var id = Json.GetString(added.Node["Id"])!;
        Assert.True(id != GroupId, "The appended group gets a GUID of its own.");
        Assert.True(added.Name != "Style", "The appended group is renamed beside the base's group of that name.");

        var condition = groups.Single(g => g.Name == "Child").Options.Single()["Condition"]!;
        Assert.Equal(id, Json.GetString(condition["Group"]));
    }

    private sealed class TempDir : IDisposable
    {
        public TempDir(bool create = true)
        {
            Path = System.IO.Path.Combine(System.IO.Path.GetTempPath(), "umc-merge-" + Guid.NewGuid().ToString("N"));
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
