using System.Buffers.Binary;
using System.Collections.Immutable;
using System.Numerics;
using System.Text;
using System.Text.Json.Nodes;
using AdvancedPenumbraModConverter.Core;

/// <summary>PAP editing, idle slots, retarget math and the animation planner on synthetic files.</summary>
internal static class AnimationTests
{
    public static (string Name, Action Run)[] All =>
    [
        ("PAP header, entries and Havok replacement", PapRoundTrip),
        ("PAP entry and timeline motion renames", PapRenames),
        ("Animation game paths and idle slots", PathsAndSlots),
        ("Retarget keeps rest poses and scales translation", RetargetRestAndScale),
        ("Retarget transfers rotation and folds dropped bones", RetargetRotationAndDroppedBones),
        ("Idle swap in place renames and moves the file", IdleSwapInPlace),
        ("Idle swap into an option group (new mod)", IdleSwapGroup),
        ("An animation inside an option gets its slots within that option's group", IdleSwapGroupInOption),
        ("Swapped files follow game-path layouts and reuse unchanged files", SwapLocalNames),
        ("Swap takes the name from the parent race", SwapInheritsName),
        ("Swap pairs the animation the action timeline plays", SwapFromDefaultIdle),
        ("An animation with no counterpart is explained in plain words", UnpairedIsExplained),
        ("Attaching an expression appends facial entries and keeps the body", ExpressionAttach),
        ("Retarget adds target races and reports inheritance", RetargetPlan),
    ];

    private const string Loop3 = "chara/human/c0101/animation/a0001/bt_common/emote/pose03_loop.pap";
    private const string Start3 = "chara/human/c0101/animation/a0001/bt_common/emote/pose03_start.pap";
    private const string Loop5 = "chara/human/c0101/animation/a0001/bt_common/emote/pose05_loop.pap";
    private const string Start5 = "chara/human/c0101/animation/a0001/bt_common/emote/pose05_start.pap";
    private const string Idle0 = "chara/human/c0101/animation/a0001/bt_common/resident/idle.pap";

    // ── Files ───────────────────────────────────────────────────────────────

    private static void PapRoundTrip()
    {
        var bytes = BuildPap([("cbem_pose03_1lp", 0)], model: 101, havokSize: 21);
        var pap = new PapFile(bytes);
        Assert.Equal((ushort)101, pap.ModelId);
        Assert.Equal(1, pap.Entries.Length);
        Assert.Equal("cbem_pose03_1lp", pap.Entries[0].Name);
        Assert.True(pap.Entries[0].IsBody);

        var replaced = new PapFile(pap.ReplaceHavok(new byte[50]));
        Assert.True(replaced.Havok.Length - 50 is >= 0 and < 4, "The Havok section keeps only alignment padding.");
        Assert.Equal(pap.TimelineOffset % 4, replaced.TimelineOffset % 4);
        Assert.Equal(["cbem_pose03_1lp"], PapTimeline.ReadStrings(replaced.ToArray()).Select(s => s.Value));

        var retargeted = new PapFile(pap.WithModel(1101, 0));
        Assert.Equal((ushort)1101, retargeted.ModelId);
        Assert.Throws<InvalidDataException>(() => _ = new PapFile(bytes[..30]));
    }

    private static void PapRenames()
    {
        var bytes = BuildPap([("cbem_pose03_1lp", 0), ("face_anim", 1)]);
        var pap = new PapFile(bytes);
        Assert.Equal(1, pap.BodyEntries.Count());

        // Shorter: written in place.
        var shorter = PapTimeline.RenameMotions(new PapFile(bytes).WithEntryNames(new Dictionary<int, string> { [0] = "jmn" }),
            new Dictionary<string, string> { ["cbem_pose03_1lp"] = "jmn" });
        Assert.Equal("jmn", new PapFile(shorter).Entries[0].Name);
        Assert.Equal(["jmn", "face_anim"], PapTimeline.ReadStrings(shorter).Select(s => s.Value));
        Assert.Equal(bytes.Length, shorter.Length);

        // Longer: appended to its own timeline, the following timeline stays readable.
        var name = "cbem_pose03_much_longer_name";
        var longer = PapTimeline.RenameMotions(new PapFile(bytes).WithEntryNames(new Dictionary<int, string> { [0] = name }),
            new Dictionary<string, string> { ["cbem_pose03_1lp"] = name });
        Assert.Equal([name, "face_anim"], PapTimeline.ReadStrings(longer).Select(s => s.Value));
        Assert.True(longer.Length > bytes.Length, "The longer name must grow the timeline.");

        Assert.Throws<InvalidDataException>(() => PapTimeline.RenameMotions(bytes, new Dictionary<string, string> { ["missing"] = "x" }));
        Assert.Throws<InvalidDataException>(() => new PapFile(bytes).WithEntryNames(new Dictionary<int, string> { [0] = "bad/name" }));
    }

    private static void PathsAndSlots()
    {
        Assert.True(PapPath.TryParse(Loop3, out var path));
        Assert.Equal((ushort)101, path.Race);
        Assert.Equal("a0001/bt_common/emote/pose03_loop", path.Location);
        Assert.Equal("chara/human/c1101/animation/a0001/bt_common/emote/pose03_loop.pap", path.WithRace(1101).GamePath);
        Assert.True(!PapPath.TryParse("chara/human/c0101/animation/f0001/nonresident/smile.pap", out _), "Facial animations are not body animations.");
        Assert.Equal("resident/idle", AnimationKeys.PapKey("normal/idle"));

        Assert.True(IdleSlots.TryDescribe("emote/j_pose02_start", out var family, out var index, out var start));
        Assert.Equal("ground", family);
        Assert.Equal(2, index);
        Assert.True(start);
        Assert.True(IdleSlots.TryDescribe("resident/idle", out family, out index, out _));
        Assert.Equal(("standing", 0), (family, index));
        Assert.True(!IdleSlots.TryDescribe("emote/b_pose01_loop", out _, out _, out _), "Unknown families are not idles.");

        var existing = new HashSet<string> { "resident/idle", "emote/pose01_loop", "emote/pose01_start", "emote/pose02_loop" };
        var slots = IdleSlots.Discover(IdleSlots.GetFamily("standing")!, existing.Contains);
        Assert.Equal([0, 1, 2], slots.Select(s => s.Index));
        Assert.Equal("emote/pose01_start", slots[1].StartKey);
        Assert.Equal(null, slots[2].StartKey);
    }

    // ── Retarget math ───────────────────────────────────────────────────────

    private static SkeletonDescription Chain(string name, float length, params string[] bones)
    {
        var list = ImmutableArray.CreateBuilder<SkeletonBone>();
        for (var i = 0; i < bones.Length; i++)
            list.Add(new SkeletonBone(bones[i], (short)(i - 1),
                BoneTransform.Identity with { Position = i == 0 ? Vector3.Zero : new Vector3(0, length, 0) }));
        return new SkeletonDescription(name, list.ToImmutable(), [], [], []);
    }

    private static void RetargetRestAndScale()
    {
        var source = Chain("src", 1f, "n_root", "j_kosi", "j_sebo");
        var target = Chain("tgt", 0.5f, "n_root", "j_kosi", "j_sebo");
        var retarget = new SkeletonRetarget(source, target, [], []);

        var rest = retarget.Map(source.Bones.Select(b => b.Reference).ToArray());
        for (var i = 0; i < rest.Length; i++) Assert.True(rest[i].Near(target.Bones[i].Reference, 1e-5f), $"Bone {i} left its rest pose.");

        // The waist moves up by 0.2 on a 1.0 bone: on a 0.5 bone that is 0.1.
        var pose = source.Bones.Select(b => b.Reference).ToArray();
        pose[1] = pose[1] with { Position = new Vector3(0, 1.2f, 0) };
        var mapped = retarget.Map(pose);
        Assert.True(Vector3.Distance(mapped[1].Position, new Vector3(0, 0.6f, 0)) < 1e-4f, $"Got {mapped[1].Position}.");
        Assert.Equal(0, retarget.DroppedBones.Count);
    }

    private static void RetargetRotationAndDroppedBones()
    {
        var source = Chain("src", 1f, "n_root", "j_kosi", "j_extra", "j_sebo");
        var target = Chain("tgt", 1f, "n_root", "j_kosi", "j_sebo");
        var retarget = new SkeletonRetarget(source, target, [], []);
        Assert.Equal([0, 1, -1, 2], retarget.BoneMap);
        Assert.Equal([(short)0, (short)2], retarget.MapTracks([0, 2, 3]));

        var turn = Quaternion.CreateFromAxisAngle(Vector3.UnitZ, 0.5f);
        var pose = source.Bones.Select(b => b.Reference).ToArray();
        pose[1] = pose[1] with { Rotation = turn };
        pose[2] = pose[2] with { Rotation = turn };
        var mapped = retarget.Map(pose);
        Assert.True(Math.Abs(Quaternion.Dot(mapped[1].Rotation, turn)) > 0.9999f, "The waist rotation must transfer.");
        // The dropped bone's rotation is carried by its child, measured from the shared ancestor.
        var expected = Quaternion.Normalize(turn);
        Assert.True(Math.Abs(Quaternion.Dot(mapped[2].Rotation, expected)) > 0.9999f, "The dropped bone's rotation must be folded in.");
        Assert.Equal(["j_extra"], retarget.DroppedBones);

        var encoded = SkeletonRetarget.Encode(mapped[1], target.Bones[1].Reference, 2);
        Assert.True(Vector3.Distance(encoded.Position, Vector3.Zero) < 1e-5f, "Additive translation is relative to the reference.");
        Assert.Throws<InvalidDataException>(() => SkeletonRetarget.Encode(mapped[1], target.Bones[1].Reference, 5));
    }

    // ── Planner ─────────────────────────────────────────────────────────────

    /// <summary>Appends marker bytes as the "merged" Havok, and reports one existing binding.</summary>
    private sealed class FakeMerger : IExpressionMerger
    {
        public string? UnavailableReason => null;
        public List<int> Requested { get; } = [];

        public (byte[] Havok, int OriginalBindings) Append(byte[] target, byte[] donor, IReadOnlyList<int> donorBindings)
        {
            Requested.AddRange(donorBindings);
            return ([.. target, .. Enumerable.Repeat((byte)0xEE, 12)], 1);
        }
    }

    /// <summary>
    /// The donor's facial entries land after the target's own, named after the target's body
    /// animation so they play with it, bound past the target's bindings, with their timelines
    /// renamed to match. The body entry and its timeline must come through byte for byte.
    /// </summary>
    private static void ExpressionAttach()
    {
        var target = BuildPap([("cbem_box_1lp", 0)]);
        var donor  = BuildPap([("emot_smile", 0), ("emot_smile", 1), ("emot_smile", 2)]);
        var merger = new FakeMerger();
        var notes  = new List<string>();

        var result = new PapFile(PapExpressions.Attach(target, donor, merger, notes));

        Assert.Equal(3, result.Entries.Length);
        Assert.Equal(new PapFile(target).Entries[0], result.Entries[0]);
        Assert.Equal(new PapFile(target).Timeline(0), result.Timeline(0));
        Assert.Equal(new[] { "cbem_box_1lp", "cbem_box_1lp" }, result.FaceEntries.Select(e => e.Entry.Name).ToArray());
        Assert.Equal(new[] { 1, 2 }, result.FaceEntries.Select(e => e.Entry.Face).ToArray());
        // The synthetic file binds entry i to binding i: the two faces bring bindings 1 and 2,
        // which land after the target's single binding.
        Assert.Equal(new[] { 1, 2 }, merger.Requested.ToArray());
        Assert.Equal(new[] { (short)1, (short)2 }, result.FaceEntries.Select(e => e.Entry.Binding).ToArray());
        Assert.Equal(new[] { "cbem_box_1lp", "cbem_box_1lp", "cbem_box_1lp" },
            PapTimeline.ReadStrings(result.ToArray()).Where(s => s.IsMotion).Select(s => s.Value).ToArray());
        Assert.True(result.Havok.Skip(result.Havok.Length - 12).All(b => b == 0xEE), "the merged Havok is used");

        // A face type the target already animates keeps its own.
        var withFace = BuildPap([("cbem_box_1lp", 0), ("cbem_box_1lp", 1)]);
        var partial  = new PapFile(PapExpressions.Attach(withFace, donor, new FakeMerger(), notes));
        Assert.Equal(new[] { 1, 2 }, partial.FaceEntries.Select(e => e.Entry.Face).ToArray());

        // A donor without a face is refused rather than silently doing nothing.
        Assert.Throws<InvalidDataException>(() => PapExpressions.Attach(target, target, new FakeMerger(), notes));
    }

    /// <summary>
    /// A destination with no start animation leaves the mod's start animation nowhere to go.
    /// The warning has to say that in those terms, not in terms of pap keys and "counterparts".
    /// </summary>
    private static void UnpairedIsExplained()
    {
        using var mod = new TempDir();
        Definition(mod, $$$"""{"Files":{"{{{Loop3}}}":"loop.pap","{{{Start3}}}":"start.pap"}}""");
        mod.File("loop.pap", BuildPap([("cbem_pose03_1lp", 0)]));
        mod.File("start.pap", BuildPap([("cbem_pose03_1st", 0)]));

        // Standing idle 5 as a destination with a loop but no start of its own.
        var plan = Planner(Game()).Plan(mod.Path, SlotRequest(ConversionOutputMode.InPlace, ("Standing idle 5", Loop5, null)));

        var unpaired = plan.Diagnostics.SingleOrDefault(d => d.Code == "unpaired");
        Assert.True(unpaired != null, "The unmatched start animation must be reported.");
        Assert.True(unpaired!.Message.Contains("Standing idle 5 has no start animation of its own"), unpaired.Message);
        Assert.True(unpaired.Message.Contains("removed from the mod"), unpaired.Message);
        Assert.True(!unpaired.Message.Contains("counterpart"), unpaired.Message);
    }

    /// <summary>Writes a Penumbra 1.7+ meta.json holding the given DefaultData and groups.</summary>
    private static void Definition(TempDir mod, string defaultData, string? groups = null)
        => mod.Json("meta.json", "{\"FileVersion\":4,\"Name\":\"Idle\",\"DefaultData\":" + defaultData +
                                 (groups == null ? "" : ",\"Groups\":" + groups) + "}");

    private static void IdleSwapInPlace()
    {
        using var mod = new TempDir();
        Definition(mod,
            $$$"""{"Files":{"{{{Loop3}}}":"anim\\loop.pap","{{{Start3}}}":"anim\\start.pap"},"FileSwaps":{},"Manipulations":[]}""");
        mod.File("anim/loop.pap", BuildPap([("cbem_pose03_1lp", 0)]));
        mod.File("anim/start.pap", BuildPap([("cbem_pose03_1st", 0)]));
        var game = Game();

        var request = SlotRequest(ConversionOutputMode.InPlace, ("Standing idle 5", Loop5, Start5));
        var plan = Planner(game).Plan(mod.Path, request);
        Assert.True(!plan.HasBlockers, string.Join(" ", plan.Diagnostics.Select(d => d.Message)));
        GearConversionExecutor.ApplyInPlace(plan, mod.Path);

        var files = PenumbraMod.Load(mod.Path).Default.FileEntries().ToDictionary(e => GamePath.Normalize(e.Key), e => e.Local);
        Assert.Equal(new[] { Loop5, Start5 }, files.Keys.Order().ToArray());
        var loop = new PapFile(File.ReadAllBytes(Path.Combine(mod.Path, files[Loop5])));
        Assert.Equal("cbem_pose05_1lp", loop.Entries[0].Name);
        Assert.Equal(["cbem_pose05_1lp"], PapTimeline.ReadStrings(loop.ToArray()).Select(s => s.Value));
        Assert.True(!File.Exists(Path.Combine(mod.Path, "anim", "loop.pap")), "The moved source file must be removed.");
        Assert.Equal(0, AnimationConversionVerifier.Verify(mod.Path, plan).Count);

        // The default idle has no start: the start leaves with the loop instead of staying behind in slot 5.
        var toIdle = SlotRequest(ConversionOutputMode.InPlace, ("Standing idle (default)", Idle0, null));
        toIdle = toIdle with { SourceLocations = [.. toIdle.SourceLocations.Select(l => l.Replace("pose03", "pose05"))],
            Variants = [new AnimationSwapVariant("Standing idle (default)", ImmutableDictionary<string, string>.Empty
                .Add(toIdle.SourceLocations[0].Replace("pose03", "pose05"), toIdle.Variants[0].Locations.Values.Single()))] };
        plan = Planner(game).Plan(mod.Path, toIdle);
        Assert.True(!plan.HasBlockers, string.Join(" ", plan.Diagnostics.Select(d => d.Message)));
        GearConversionExecutor.ApplyInPlace(plan, mod.Path);
        Assert.Equal(new[] { Idle0 }, PenumbraMod.Load(mod.Path).Default.FileEntries().Select(e => GamePath.Normalize(e.Key)).ToArray());
    }

    private static void IdleSwapGroup()
    {
        using var mod = new TempDir();
        using var output = new TempDir(create: false);
        mod.Json("meta.json", $$$"""
            {"FileVersion":4,"Identifier":"{{{Guid.NewGuid()}}}","Name":"Idle",
             "DefaultData":{"Files":{"{{{Loop3}}}":"anim\\loop.pap","chara/other.tex":"x.tex"} } }
            """);
        mod.File("anim/loop.pap", BuildPap([("cbem_pose03_1lp", 0)]));
        var game = Game();

        var request = SlotRequest(ConversionOutputMode.NewMod,
            ("Standing idle (default)", Idle0, null), ("Standing idle 3", Loop3, Start3), ("Standing idle 5", Loop5, Start5)) with
        {
            GroupName = "Idle slot",
            DefaultVariant = 1,
        };
        var plan = Planner(game).Plan(mod.Path, request);
        Assert.True(!plan.HasBlockers, string.Join(" ", plan.Diagnostics.Select(d => d.Message)));
        GearConversionExecutor.WriteNewMod(plan, mod.Path, output.Path, "Idle (slots)");

        var result = PenumbraMod.Load(output.Path);
        Assert.True(result.Default.FileEntries().All(e => !PapPath.TryParse(e.Key, out _)), "The group provides the animation.");
        var group = result.Groups.Single();
        Assert.Equal("Idle slot", group.Name);
        Assert.Equal(1, Json.GetInt(group.Node["DefaultSettings"], -1));
        Assert.Equal(["Standing idle (default)", "Standing idle 3", "Standing idle 5"],
            group.Options.Select(o => Json.GetString(o["Name"])));
        Assert.True(group.Options.All(o => Guid.TryParse(Json.GetString(o["Id"]), out _)), "Penumbra 1.7 options carry IDs.");
        var names = group.Containers.Select(c => c.FileEntries().Single())
            .Select(e => new PapFile(File.ReadAllBytes(Path.Combine(output.Path, e.Local))).Entries[0].Name).ToList();
        Assert.Equal(["cbnm_id0", "cbem_pose03_1lp", "cbem_pose05_1lp"], names);
        Assert.True(plan.Diagnostics.Any(d => d.Code == "destination_extras" && d.Message.Contains("cbna_add_dmg_f")),
            "Replacing the default idle must report the hit reaction the output lacks.");
        Assert.Equal(0, AnimationConversionVerifier.Verify(output.Path, plan).Count);
    }

    /// <summary>A local path laid out like the game path, as many mods do.</summary>
    private static string Mirrored(string gamePath) => GamePath.ToLocal("files/" + gamePath);

    private static void SwapLocalNames()
    {
        using var mod = new TempDir();
        var loop = Mirrored(Loop3);
        var start = Mirrored(Start3);
        Definition(mod, System.Text.Json.JsonSerializer.Serialize(new { Files = new Dictionary<string, string> { [Loop3] = loop, [Start3] = start } }));
        mod.File(loop, BuildPap([("cbem_pose03_1lp", 0)]));
        mod.File(start, BuildPap([("cbem_pose03_1st", 0)]));

        var request = SlotRequest(ConversionOutputMode.InPlace,
            ("Standing idle (default)", Idle0, null), ("Standing idle 3", Loop3, Start3), ("Standing idle 5", Loop5, Start5)) with
        {
            GroupName = "Idle slot",
        };
        var plan = Planner(Game()).Plan(mod.Path, request);
        Assert.True(!plan.HasBlockers, string.Join(" ", plan.Diagnostics.Select(d => d.Message)));
        var written = plan.Files.Where(f => f.Operation == LocalFileOperation.Write).Select(f => f.Destination).Order().ToArray();
        Assert.Equal(new[]
        {
            Mirrored(Start5),
            Mirrored(Loop5),
            Mirrored(Idle0),
        }.Order().ToArray(), written);
        Assert.True(plan.Files.All(f => f.Operation != LocalFileOperation.Delete), "The unchanged slot keeps using the source files.");
        GearConversionExecutor.ApplyInPlace(plan, mod.Path);
        Assert.Equal(0, AnimationConversionVerifier.Verify(mod.Path, plan).Count);
    }

    /// <summary>
    /// A slot group for an animation that lives in an option must not become a new group: that
    /// would make the animation play whether or not its option is selected. The option is split
    /// into one option per slot inside its own group instead, keeping its other files, and the
    /// group's default selection follows the option to its default slot.
    /// </summary>
    private static void IdleSwapGroupInOption()
    {
        using var mod = new TempDir();
        Definition(mod, """{"Files":{}}""",
            $$$"""[{"Name":"Style","Type":"Single","DefaultSettings":1,"Options":[{"Name":"Off"},{"Id":"keep-me","Name":"A","Files":{"{{{Loop3}}}":"a.pap","chara/other.tex":"other.tex"} } ] } ]""");
        mod.File("a.pap", BuildPap([("cbem_pose03_1lp", 0)]));
        mod.File("other.tex", [1]);
        var request = SlotRequest(ConversionOutputMode.InPlace,
            ("Standing idle 3", Loop3, null), ("Standing idle 5", Loop5, null)) with { GroupName = "Slot" };
        var plan = Planner(Game()).Plan(mod.Path, request);
        Assert.True(!plan.HasBlockers, string.Join(" ", plan.Diagnostics.Select(d => d.Message)));

        var group = plan.Result.Groups.Single();
        Assert.Equal("Style", group.Name);
        Assert.Equal(new[] { "Off", "A · Standing idle 3", "A · Standing idle 5" },
            group.Options.Select(o => o["Name"]!.GetValue<string>()).ToArray());
        // The option's identity stays with its default slot, and so does the group's default.
        Assert.Equal("keep-me", group.Options.ElementAt(1)["Id"]!.GetValue<string>());
        Assert.Equal(1, group.Node["DefaultSettings"]!.GetValue<int>());

        var slot5 = group.Containers[2].FileEntries().ToDictionary(e => GamePath.Normalize(e.Key), e => e.Local);
        Assert.True(slot5.ContainsKey(Loop5) && !slot5.ContainsKey(Loop3), "the copy for slot 5 plays in slot 5 only");
        Assert.True(slot5.ContainsKey("chara/other.tex"), "the option's other files come along");
        Assert.True(plan.Diagnostics.Any(d => d.Code == "slot_options_in_group"));

        // A plain replacement still works inside the option, and leaves the group's shape alone.
        plan = Planner(Game()).Plan(mod.Path,
            SlotRequest(ConversionOutputMode.InPlace, ("Standing idle 5", Loop5, null)));
        Assert.True(!plan.HasBlockers, string.Join(" ", plan.Diagnostics.Select(d => d.Message)));
        Assert.Equal(2, plan.Result.Groups.Single().Containers.Count);
        Assert.True(plan.Result.Groups.Single().Containers[1].FileEntries().Any(e => GamePath.Normalize(e.Key) == Loop5));
    }

    private static void SwapInheritsName()
    {
        using var mod = new TempDir();
        var loop = Loop3.Replace("c0101", "c0801");
        Definition(mod, $$$"""{"Files":{"{{{loop}}}":"loop.pap"}}""");
        mod.File("loop.pap", BuildPap([("cbem_pose03_1lp", 0)]));
        // c0801 has no own pose05; the game plays c0101's, whose name is used.
        var plan = new AnimationConversionPlanner(Game(), race => race == 801 ? (ushort)101 : null, null)
            .Plan(mod.Path, SlotRequest(ConversionOutputMode.NewMod, ("Standing idle 5", Loop5, Start5)));
        Assert.True(!plan.HasBlockers, string.Join(" ", plan.Diagnostics.Select(d => d.Message)));
        Assert.True(plan.Diagnostics.Any(d => d.Code == "inherited_name"), "The inherited name must be reported.");
        var write = plan.Files.Single(f => f.Operation == LocalFileOperation.Write);
        Assert.Equal("cbem_pose05_1lp", new PapFile(write.Content!).Entries[0].Name);

        plan = new AnimationConversionPlanner(Game(), _ => null, null)
            .Plan(mod.Path, SlotRequest(ConversionOutputMode.NewMod, ("Standing idle 5", Loop5, Start5)));
        Assert.True(plan.Diagnostics.Any(d => d.IsBlocker && d.Code == "swap_failed"), "Without a parent the name is unknown.");
    }

    private static void SwapFromDefaultIdle()
    {
        using var mod = new TempDir();
        Definition(mod, $$$"""{"Files":{"{{{Idle0}}}":"idle.pap"}}""");
        mod.File("idle.pap", BuildPap([("cbna_add_dmg_f", 0), ("cbnm_id0", 0)]));
        string Location(string path) => PapPath.TryParse(path, out var p) ? p.Location : throw new Exception(path);
        var request = new AnimationConversionRequest([Location(Idle0)], AnimationOperation.Swap, ConversionOutputMode.NewMod, "test")
        {
            Variants = [new AnimationSwapVariant("Standing idle 5", ImmutableDictionary<string, string>.Empty.Add(Location(Idle0), Location(Loop5)))],
        };
        var plan = Planner(Game()).Plan(mod.Path, request);
        Assert.True(!plan.HasBlockers, string.Join(" ", plan.Diagnostics.Select(d => d.Message)));
        var output = new PapFile(plan.Files.Single(f => f.Operation == LocalFileOperation.Write).Content!);
        Assert.Equal(["cbna_add_dmg_f", "cbem_pose05_1lp"], output.Entries.Select(e => e.Name));
        Assert.Equal(["cbna_add_dmg_f", "cbem_pose05_1lp"], PapTimeline.ReadStrings(output.ToArray()).Select(s => s.Value));
    }

    private static void RetargetPlan()
    {
        using var mod = new TempDir();
        Definition(mod, $$$"""{"Files":{"{{{Loop3}}}":"c0101\\loop.pap"}}""");
        mod.File("c0101/loop.pap", BuildPap([("cbem_pose03_1lp", 0)], model: 101));
        var game = Game();
        game.Files[PapPath.BaseSkeletonPath(101)] = [1];
        game.Files[PapPath.BaseSkeletonPath(1101)] = [2];
        var retargeter = new FakeRetargeter();
        var request = new AnimationConversionRequest([PapPath.TryParse(Loop3, out var p) ? p.Location : ""],
            AnimationOperation.Retarget, ConversionOutputMode.InPlace, "test")
        {
            SourceRace = 101,
            TargetRaces = [1101],
        };
        ushort? Parent(ushort race) => race switch { 1201 => 1101, 1101 => 101, 101 => null, _ => 101 };
        var plan = new AnimationConversionPlanner(game, Parent, retargeter).Plan(mod.Path, request);
        Assert.True(!plan.HasBlockers, string.Join(" ", plan.Diagnostics.Select(d => d.Message)));
        Assert.Equal(1, retargeter.Calls);
        // Named, not coded: the message is for someone reading the plan, not the file paths.
        Assert.True(plan.Diagnostics.Any(d => d.Code == "inherited_by" && d.Message.Contains("Lalafell Female")),
            "Lalafell female inherits the new Lalafell male file.");
        GearConversionExecutor.ApplyInPlace(plan, mod.Path);

        var files = PenumbraMod.Load(mod.Path).Default.FileEntries().ToDictionary(e => GamePath.Normalize(e.Key), e => e.Local);
        var target = Loop3.Replace("c0101", "c1101");
        Assert.Equal(new[] { Loop3, target }, files.Keys.Order().ToArray());
        Assert.Equal(@"c1101\loop.pap", files[target]);
        Assert.Equal((ushort)1101, new PapFile(File.ReadAllBytes(Path.Combine(mod.Path, files[target]))).ModelId);

        var unavailable = new AnimationConversionPlanner(game, Parent, new FakeRetargeter { Reason = "no" }).Plan(mod.Path, request);
        Assert.True(unavailable.Diagnostics.Any(d => d.IsBlocker && d.Code == "retarget_unavailable"));
    }

    // ── Helpers ─────────────────────────────────────────────────────────────

    private static AnimationConversionPlanner Planner(IGameFileProvider game) => new(game, _ => null, null);

    private static AnimationConversionRequest SlotRequest(ConversionOutputMode mode, params (string Label, string Loop, string? Start)[] slots)
    {
        string Location(string path) => PapPath.TryParse(path, out var p) ? p.Location : throw new Exception(path);
        var variants = slots.Select(s =>
        {
            var map = ImmutableDictionary.CreateBuilder<string, string>();
            map[Location(Loop3)] = Location(s.Loop);
            if (s.Start != null) map[Location(Start3)] = Location(s.Start);
            var roles = ImmutableDictionary.CreateBuilder<string, string>();
            roles[Location(Loop3)] = "looping";
            roles[Location(Start3)] = "start";
            return new AnimationSwapVariant(s.Label, map.ToImmutable()) { SourceRoles = roles.ToImmutable() };
        }).ToImmutableArray();
        return new AnimationConversionRequest([Location(Loop3), Location(Start3)], AnimationOperation.Swap, mode, "test")
        {
            Variants = variants,
        };
    }

    private static FakeGame Game()
    {
        var game = new FakeGame();
        // Like the game's: the default idle also holds an additive hit reaction.
        game.Files[Idle0] = BuildPap([("cbna_add_dmg_f", 0), ("cbnm_id0", 0)]);
        game.Files["chara/action/normal/idle.tmb"] = ActionTimeline("cbnm_id0");
        game.Files["chara/action/emote/pose03_loop.tmb"] = ActionTimeline("cbem_pose03_1lp");
        game.Files["chara/action/emote/pose05_loop.tmb"] = ActionTimeline("cbem_pose05_1lp");
        game.Files[Loop3] = BuildPap([("cbem_pose03_1lp", 0)]);
        game.Files[Start3] = BuildPap([("cbem_pose03_1st", 0)]);
        game.Files[Loop5] = BuildPap([("cbem_pose05_1lp", 0)]);
        game.Files[Start5] = BuildPap([("cbem_pose05_1st", 0)]);
        return game;
    }

    /// <summary>A PAP whose Havok section is opaque bytes and whose timelines each play their own entry.</summary>
    internal static byte[] BuildPap((string Name, int Face)[] entries, ushort model = 101, int havokSize = 16)
    {
        const int header = 26;
        var info = header;
        var havok = info + entries.Length * 40;
        var timelines = entries.Select(e => Timeline(e.Name)).ToList();
        var timelineOffset = havok + havokSize;
        var stream = new MemoryStream();
        var writer = new BinaryWriter(stream);
        writer.Write("pap "u8);
        writer.Write(0x00020001);
        writer.Write((short)entries.Length);
        writer.Write(model);
        writer.Write((byte)0);
        writer.Write((byte)0);
        writer.Write(info);
        writer.Write(havok);
        writer.Write(timelineOffset);
        for (var i = 0; i < entries.Length; i++)
        {
            var name = new byte[32];
            Encoding.ASCII.GetBytes(entries[i].Name).CopyTo(name, 0);
            writer.Write(name);
            writer.Write((short)0);
            writer.Write((short)i);
            writer.Write(entries[i].Face);
        }
        writer.Write(Enumerable.Range(0, havokSize).Select(i => (byte)i).ToArray());
        for (var i = 0; i < timelines.Count; i++)
        {
            writer.Write(timelines[i]);
            if (i + 1 < timelines.Count) writer.Write(new byte[(int)((timelineOffset - stream.Position) & 3)]);
        }
        return stream.ToArray();
    }

    /// <summary>TMLB with one C009 entry whose string (the motion name) follows the entries.</summary>
    private static byte[] Timeline(string motion)
    {
        var text = Encoding.ASCII.GetBytes(motion + "\0");
        var length = 12 + 28 + text.Length;
        var bytes = new byte[length];
        "TMLB"u8.CopyTo(bytes);
        BinaryPrimitives.WriteInt32LittleEndian(bytes.AsSpan(4), length);
        BinaryPrimitives.WriteInt32LittleEndian(bytes.AsSpan(8), 1);
        "C009"u8.CopyTo(bytes.AsSpan(12));
        BinaryPrimitives.WriteInt32LittleEndian(bytes.AsSpan(16), 28);
        // Displacement from the entry start + 8 to the string at offset 40.
        BinaryPrimitives.WriteInt32LittleEndian(bytes.AsSpan(12 + 20), 40 - (12 + 8));
        text.CopyTo(bytes, 40);
        return bytes;
    }

    /// <summary>A standalone action timeline (bare TMLB) whose C010 entry plays <paramref name="motion"/>.</summary>
    private static byte[] ActionTimeline(string motion)
    {
        var text = Encoding.ASCII.GetBytes(motion + "\0");
        var bytes = new byte[12 + 36 + text.Length];
        "TMLB"u8.CopyTo(bytes);
        BinaryPrimitives.WriteInt32LittleEndian(bytes.AsSpan(4), bytes.Length);
        BinaryPrimitives.WriteInt32LittleEndian(bytes.AsSpan(8), 1);
        "C010"u8.CopyTo(bytes.AsSpan(12));
        BinaryPrimitives.WriteInt32LittleEndian(bytes.AsSpan(16), 36);
        BinaryPrimitives.WriteInt32LittleEndian(bytes.AsSpan(12 + 32), 48 - (12 + 8));
        text.CopyTo(bytes, 48);
        return bytes;
    }

    private sealed class FakeRetargeter : IAnimationRetargeter
    {
        public string? Reason { get; init; }
        public int Calls { get; private set; }
        public string? UnavailableReason => Reason;

        public RetargetedPap Retarget(byte[] pap, byte[] sourceSkeleton, byte[] targetSkeleton, ushort targetRace)
        {
            Calls++;
            return new RetargetedPap(new PapFile(pap).WithModel(targetRace, 0), ["test note"]);
        }
    }

    private sealed class FakeGame : IGameFileProvider
    {
        public Dictionary<string, byte[]> Files { get; } = new(StringComparer.OrdinalIgnoreCase);
        public byte[]? ReadFile(string gamePath) => Files.GetValueOrDefault(GamePath.Normalize(gamePath));
    }

    private sealed class TempDir : IDisposable
    {
        public TempDir(bool create = true)
        {
            Path = System.IO.Path.Combine(System.IO.Path.GetTempPath(), "apmc-anim-" + Guid.NewGuid().ToString("N"));
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
