using System.Text;
using System.Text.Json.Nodes;
using AdvancedPenumbraModConverter.Core;

/// <summary>Merging two modpacks that were split from one mod.</summary>
internal static class ModMergerTests
{
    public static (string Name, Action Run)[] All =>
    [
        ("Merging modpacks: the top pack wins every overlap", TopPackWins),
        ("Merging modpacks: a group of each pack keeps its own IDs", DuplicateIdsAreReplaced),
    ];

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
            Path = System.IO.Path.Combine(System.IO.Path.GetTempPath(), "apmc-merge-" + Guid.NewGuid().ToString("N"));
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
