using System.Text.Json.Nodes;
using UniversalModConverter.Core;

/// <summary>
/// Several conversions added to one mod with options of their own, one after another: a hair
/// converted to several races in one run. Each later conversion sees the mod as the earlier ones
/// left it, and its option goes beside theirs.
/// </summary>
internal static class ConvertedOptionRunTests
{
    public static (string Name, Action Run)[] All =>
    [
        ("Own options of a run share the Converted group", RunSharesConvertedGroup),
        ("Own options of a run go beside a lone option, which turns multi-select once", RunGoesBesideLoneOption),
    ];

    private const string SourceModel = "chara/human/c0101/obj/hair/h0005/model/c0101h0005_hir.mdl";
    private const string SourceTexture = "chara/human/c0101/obj/hair/h0005/texture/--c0101h0005_hir_n.tex";

    private static string TargetModel(ushort race) => $"chara/human/c{race:D4}/obj/hair/h0005/model/c{race:D4}h0005_hir.mdl";
    private static string TargetLocal(ushort race) => $"hair/c0101h0005_hir_c{race:D4}h0005.mdl";

    /// <summary>The model is in Default, so every converted race gets a toggle in one "Converted" group.</summary>
    private static void RunSharesConvertedGroup()
    {
        var mod = Mod($$$"""
            {"FileVersion":4,"Name":"Hair","DefaultData":{"Files":{
              "{{{SourceModel}}}":"hair/c0101h0005_hir.mdl","{{{SourceTexture}}}":"hair/n.tex"} } }
            """);

        var miqote = Add(mod, 701, "Miqo'te Female", (m, key, local) => m.Default.Files![key] = local);
        Assert.True(miqote.Placement is { NewGroup: true, TurnedMulti: false }, "the first conversion makes the group");
        var auRa = Add(miqote.Mod, 1401, "Au Ra Female", (m, key, local) => m.Default.Files![key] = local);
        Assert.True(auRa.Placement is { NewGroup: false, TurnedMulti: false }, "the second joins it");
        Assert.Equal(ConvertedOption.GroupName, auRa.Placement!.Group);

        var result = auRa.Mod;
        var group = result.Groups.Single();
        Assert.Equal("Multi", group.Type);
        Assert.Equal(2, group.Containers.Count);
        Assert.Equal(TargetLocal(701), Local(group.Containers[0], TargetModel(701)));
        Assert.Equal(TargetLocal(1401), Local(group.Containers[1], TargetModel(1401)));
        Assert.Equal(3UL, Json.TryGetULong(group.Node["DefaultSettings"], out var defaults) ? defaults : 0UL);
        Assert.Equal(new[] { SourceModel, SourceTexture },
            result.Default.FileEntries().Select(e => GamePath.Normalize(e.Key)).Order().ToArray());
    }

    /// <summary>
    /// The model is switched on by a single-select group with only its option: the first conversion
    /// turns it multi-select, the second only adds its option, and the original stays chosen.
    /// </summary>
    private static void RunGoesBesideLoneOption()
    {
        var mod = Mod($$$"""
            {"FileVersion":4,"Name":"Hair","Groups":[{"Name":"Hair","Type":"Single","DefaultSettings":0,"Options":[
              {"Name":"On","Files":{"{{{SourceModel}}}":"hair/c0101h0005_hir.mdl"}}]}]}
            """);

        void InOption(PenumbraMod m, string key, string local) => m.Groups[0].Containers[0].Files![key] = local;
        var miqote = Add(mod, 701, "Miqo'te Female", InOption);
        Assert.True(miqote.Placement is { Group: "Hair", NewGroup: false, TurnedMulti: true }, "the first conversion turns the group multi-select");
        var viera = Add(miqote.Mod, 1801, "Viera Female", InOption);
        Assert.True(viera.Placement is { Group: "Hair", NewGroup: false, TurnedMulti: false }, "the second finds it multi-select already");

        var group = viera.Mod.Groups.Single();
        Assert.Equal("Multi", group.Type);
        Assert.Equal(new[] { "On", "Miqo'te Female", "Viera Female" },
            group.Options.Select(o => Json.GetString(o["Name"])).ToArray());
        Assert.Equal(TargetLocal(1801), Local(group.Containers[2], TargetModel(1801)));
        Assert.True(Local(group.Containers[0], TargetModel(1801)) == null, "the original's option keeps only its own model");
        Assert.Equal(7UL, Json.TryGetULong(group.Node["DefaultSettings"], out var defaults) ? defaults : 0UL);
    }

    /// <summary>
    /// One conversion added to <paramref name="before"/> the way a customization plan adds it: the
    /// target's model path beside the source's, loading a copy it makes, then moved into its option.
    /// </summary>
    private static (PenumbraMod Mod, ConvertedOptionPlacement? Placement) Add(PenumbraMod before, ushort race, string name,
        Action<PenumbraMod, string, string> addPath)
    {
        var after = before.Clone();
        addPath(after, TargetModel(race), TargetLocal(race));
        var outcome = ConvertedOption.Move(before, after, new HashSet<string>([GamePath.NormalizeLocal(TargetLocal(race))], StringComparer.Ordinal),
            name, $"Hair, converted to {name}.");
        Assert.True(outcome.Moved, outcome.Refusal ?? "nothing moved");
        // Written and read back, as the next conversion of the run reads the definition.
        return (PenumbraMod.FromJson((JsonObject)after.ToJson()), outcome.Placement);
    }

    private static PenumbraMod Mod(string json) => PenumbraMod.FromJson(JsonNode.Parse(json)!.AsObject());

    private static string? Local(ModContainer container, string gamePath)
        => container.FileEntries().FirstOrDefault(e => GamePath.Normalize(e.Key) == gamePath).Local;
}
