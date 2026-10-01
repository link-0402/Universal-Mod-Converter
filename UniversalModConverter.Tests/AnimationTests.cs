using System.Buffers.Binary;
using System.Collections.Immutable;
using System.Numerics;
using System.Text;
using System.Text.Json.Nodes;
using UniversalModConverter.Core;

/// <summary>PAP editing, idle slots, retarget math and the animation planner on synthetic files.</summary>
internal static class AnimationTests
{
    public static (string Name, Action Run)[] All =>
    [
        ("PAP header, entries and Havok replacement", PapRoundTrip),
        ("PAP entry and timeline motion renames", PapRenames),
        ("Timelines are rebuilt in the game's layout, with faces added", TimelineEditing),
        ("A face played by a C009 counts, and a face pack may sit in a folder", TimelineFaceNames),
        ("Animation game paths and idle slots", PathsAndSlots),
        ("Retarget keeps rest poses and scales translation", RetargetRestAndScale),
        ("Retarget transfers rotation and folds dropped bones", RetargetRotationAndDroppedBones),
        ("Idle swap in place renames and moves the file", IdleSwapInPlace),
        ("An idle goes to several slots at once, moved or kept where it is", IdleSeveralSlots),
        ("A new mod holds the idle in every slot it plays in", IdleSeveralSlotsNewMod),
        ("An animation inside an option goes to its new slots in that option", IdleSlotsInOption),
        ("Adding slots to the mod keeps the original, and each option its own version", IdleSlotsKeepOriginal),
        ("A swap retargets on the way: the target races get it in every slot", IdleSlotsRetargeted),
        ("An expression where the idle is gets a group that switches it for every race", IdleExpressionAtSource),
        ("Swapped files follow game-path layouts and reuse unchanged files", SwapLocalNames),
        ("A swap writes nothing for a race without the destination of its own", SwapSkipsRaceWithoutFile),
        ("A swap that would write nothing is refused instead of removing the mod's animation", SwapWritingNothingIsRefused),
        ("Swap pairs the animation the action timeline plays", SwapFromDefaultIdle),
        ("An animation with no counterpart is explained in plain words", UnpairedIsExplained),
        ("A facial expression swaps to another, with its timeline in every option", FaceSwap),
        ("Attaching an expression names the face in the body's timeline", ExpressionAttach),
        ("Game expressions: one pose, read from the race's own face animations", GameExpressionDonor),
        ("An expression added for a new mod stays in every option that plays the file", ExpressionNewModKeepsEveryOption),
        ("An expression added to this mod goes into an option group beside the original", ExpressionAddedAsGroup),
        ("A new mod of an animation leaves the mod's IMC groups behind", AnimationNewModDropsImcGroups),
        ("A key written in any case moves", SwapKeyCase),
        ("Every animation conversion of a run removes its own unused files", RunRemovesEveryOrphan),
        ("A face from another mod is checked for every race it plays on", ModFacePerRace),
        ("Retarget adds target races, moves them in place and reports inheritance", RetargetPlan),
        ("A file of the mod's own that a retarget replaces is removed, adding to the mod or not", RetargetRemovesReplacedFile),
        ("Retarget hands the mod's skeletons over and names the source once", RetargetHandsOverSkeletons),
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
        var bytes = BuildPap([("cbem_pose03_1lp", 0), ("cfxf_smile", 1)]);
        var pap = new PapFile(bytes);
        Assert.Equal(1, pap.BodyEntries.Count());
        // The flag the game sets on facial entries is set on some body animations too.
        var dogeza = BuildPap([("cbem_dogeza", 0)]);
        // The face flag, set on a body animation as the game sometimes does.
        BinaryPrimitives.WriteInt32LittleEndian(dogeza.AsSpan(BinaryPrimitives.ReadInt32LittleEndian(dogeza.AsSpan(14)) + 36), 1);
        Assert.True(new PapFile(dogeza).Entries[0].IsBody, "the face flag alone does not make a facial animation");

        // Shorter: the timeline shrinks, keeping nothing of the old name.
        var shorter = PapTimeline.RenameMotions(new PapFile(bytes).WithEntryNames(new Dictionary<int, string> { [0] = "jmn" }),
            new Dictionary<string, string> { ["cbem_pose03_1lp"] = "jmn" });
        Assert.Equal("jmn", new PapFile(shorter).Entries[0].Name);
        Assert.Equal(["jmn", "cfxf_smile"], PapTimeline.ReadStrings(shorter).Select(s => s.Value));
        Assert.True(!Encoding.ASCII.GetString(new PapFile(shorter).Timeline(0)).Contains("pose03"), "no trace of the old name");
        Assert.Equal(new PapFile(bytes).Timeline(1), new PapFile(shorter).Timeline(1));

        // Longer: its timeline grows, the following timeline stays readable.
        var name = "cbem_pose03_much_longer_name";
        var longer = PapTimeline.RenameMotions(new PapFile(bytes).WithEntryNames(new Dictionary<int, string> { [0] = name }),
            new Dictionary<string, string> { ["cbem_pose03_1lp"] = name });
        Assert.Equal([name, "cfxf_smile"], PapTimeline.ReadStrings(longer).Select(s => s.Value));
        Assert.True(longer.Length > bytes.Length, "The longer name must grow the timeline.");
        foreach (var i in new[] { 0, 1 })
        {
            var timeline = new PapFile(longer).Timeline(i);
            Assert.Equal(timeline, TmbTimeline.Parse(timeline).ToArray());
        }

        // A timeline with an entry of unknown layout is patched in place instead.
        var unknown = Timeline("cbem_pose03_1lp");
        "C999"u8.CopyTo(unknown.AsSpan(12)); // over the TMDH header, which holds no pointer
        Assert.Throws<InvalidDataException>(() => TmbTimeline.Parse(unknown));
        var patched = PapTimeline.RenameMotions(pap.WithTimelines([unknown, pap.Timeline(1)]),
            new Dictionary<string, string> { ["cbem_pose03_1lp"] = "jmn" });
        Assert.Equal(["jmn", "cfxf_smile"], PapTimeline.ReadStrings(patched).Select(s => s.Value));

        Assert.Throws<InvalidDataException>(() => PapTimeline.RenameMotions(bytes, new Dictionary<string, string> { ["missing"] = "x" }));
        Assert.Throws<InvalidDataException>(() => new PapFile(bytes).WithEntryNames(new Dictionary<int, string> { [0] = "bad/name" }));
    }

    private static void PathsAndSlots()
    {
        Assert.True(PapPath.TryParse(Loop3, out var path));
        Assert.Equal((ushort)101, path.Race);
        Assert.Equal("a0001/bt_common/emote/pose03_loop", path.Location);
        Assert.Equal("chara/human/c1101/animation/a0001/bt_common/emote/pose03_loop.pap", path.WithRace(1101).GamePath);
        Assert.True(PapPath.TryParse("chara/human/c0101/animation/f0001/nonresident/smile.pap", out var smile) && smile.IsFacial,
            "A facial expression's pack parses, as facial.");
        Assert.True(!path.IsFacial, "Body animations are not facial.");
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

    /// <summary>
    /// Timelines taken apart and written back come out byte for byte, whether built here or
    /// taken from real animations: j_pose02 (a face and its pack) and a dance with a sound.
    /// Adding a face gives the body's track a C010 lasting as long as the body, and a pack
    /// header placed where the game keeps it.
    /// </summary>
    private static void TimelineFaceNames()
    {
        // The game plays a face from a C010; some timelines play it from a C009, and it is a face all the same.
        Assert.Equal(["cfxf_smile"], TmbTimeline.Parse(Timeline("cfxf_smile")).Faces);
        Assert.True(!TmbTimeline.Parse(Timeline("cbem_joy")).Faces.Any(), "a body animation plays no face");

        // The game's packs sit in folders too (f0002/nonresident/emot/upset.pap).
        var timeline = TmbTimeline.Parse(Timeline("cbem_joy"));
        timeline.FacePack = "emot/upset";
        Assert.Equal("emot/upset", TmbTimeline.Parse(timeline.ToArray()).FacePack);
        foreach (var bad in new[] { "", "/upset", "emot/", "emot//upset", "../upset", "emot/../upset", "a b" })
            Assert.Throws<InvalidDataException>(() => timeline.FacePack = bad);
    }

    private static void TimelineEditing()
    {
        const string jpose02 =
            "544d4c42d700000007000000544d4448100000000100000046000300544d50500c00000090000000544d414c100000007c00000001000000" +
            "544d41431c0000000200000000000000000000006e00000001000000544d54521800000003000000540000000200000000000000" +
            "433030391800000004000000a0000000000000004600000043303130280000000500" +
            "00000a000000000000000000000000000000000000004000000000000000" +
            "0200030004000500736d696c65006362656d5f6a5f706f736530325f326c7000636678665f736d696c6500";
        const string dance =
            "544d4c422901000009000000544d44481000000001000000b4000300544d414c10000000cc00000001000000" +
            "544d41431c000000020000000000000000000000be00000003000000544d54521800000003000000a80000000100000000000000" +
            "544d54521800000004000000920000000100000000000000544d545218000000050000007c0000000100000000000000" +
            "433030391800000006000000e80100000000000066000000" +
            "433031302800000007000000e80100000000000001000000000000000000803f5f00000000000000" +
            "433036332000000008000000ffffffff0000000042000000000000000300000002000300040005000600070008006362656d5f" +
            "64616e636531395f326c7000636678665f736d696c6500736f756e642f6c6f6c6f2e73636400";
        foreach (var hex in new[] { jpose02, dance })
        {
            var real = Convert.FromHexString(hex);
            Assert.Equal(real, TmbTimeline.Parse(real).ToArray());
            Assert.Equal(["cfxf_smile"], TmbTimeline.Parse(real).Faces);
        }
        Assert.Equal("smile", TmbTimeline.Parse(Convert.FromHexString(jpose02)).FacePack);

        var built = Timeline("cbem_joy", duration: 57);
        var timeline = TmbTimeline.Parse(built);
        Assert.Equal(built, timeline.ToArray());
        Assert.True(timeline.FacePack == null && !timeline.Faces.Any(), "a plain body animation");

        timeline.AddFace("cbem_joy", "cfxf_smile");
        timeline.FacePack = "smile";
        var edited = timeline.ToArray();
        var again = TmbTimeline.Parse(edited);
        Assert.Equal(edited, again.ToArray());
        Assert.Equal(["cbem_joy", "cfxf_smile"], again.Motions);
        Assert.Equal("smile", again.FacePack);
        // The pack header follows TMDH and its name comes first among the strings, as the game writes them.
        var text = Encoding.ASCII.GetString(edited);
        Assert.Equal("TMPP", text.Substring(28, 4));
        Assert.True(text.EndsWith("smile\0cbem_joy\0cfxf_smile\0", StringComparison.Ordinal), "strings in item order");

        // The C010: next id, on the body's track, as long as the body, time control from frame 0 to 1.
        var c010 = text.IndexOf("C010", StringComparison.Ordinal);
        Assert.Equal((short)5, BinaryPrimitives.ReadInt16LittleEndian(edited.AsSpan(c010 + 8)));
        Assert.Equal(57, BinaryPrimitives.ReadInt32LittleEndian(edited.AsSpan(c010 + 12)));
        Assert.Equal(1, BinaryPrimitives.ReadInt32LittleEndian(edited.AsSpan(c010 + 20)));
        Assert.Equal(1f, BinaryPrimitives.ReadSingleLittleEndian(edited.AsSpan(c010 + 28)));
        var track = text.IndexOf("TMTR", StringComparison.Ordinal);
        Assert.Equal(2, BinaryPrimitives.ReadInt32LittleEndian(edited.AsSpan(track + 16)));

        Assert.Throws<InvalidDataException>(() => TmbTimeline.Parse(built).AddFace("cbem_other", "cfxf_smile"));
    }

    /// <summary>
    /// Attaching a face only touches the timeline of the body animation the path plays: the
    /// entries and the Havok data stay exactly as they were, and nothing is added to the pack.
    /// </summary>
    private static void ExpressionAttach()
    {
        var target = BuildPap([("cbna_add_dmg_f", 0), ("cbnm_id0", 0)]);
        var notes = new List<string>();
        var smile = new FacialAnimation("cfxf_smile", "smile");

        var result = new PapFile(PapExpressions.Attach(target, smile, ["cbnm_id0"], notes));
        var before = new PapFile(target);
        Assert.Equal(before.Entries.ToArray(), result.Entries.ToArray());
        Assert.Equal(before.Havok, result.Havok);
        Assert.Equal(before.Timeline(0), result.Timeline(0));
        var face = TmbTimeline.Parse(result.Timeline(1));
        Assert.Equal(["cfxf_smile"], face.Faces);
        Assert.Equal("smile", face.FacePack);
        // Checking a pack reads every string an edit may touch, the face pack attaching names included.
        Assert.True(PapTimeline.ReadStrings(result.ToArray()).Any(s => s.Magic == "TMPP" && s.Value == "smile"),
            "reading a pack's strings includes the face pack it loads");

        // A face in the resident pack needs no pack loaded.
        var resident = new PapFile(PapExpressions.Attach(target, new FacialAnimation("cfxf_bow", null), ["cbnm_id0"], notes));
        Assert.True(TmbTimeline.Parse(resident.Timeline(1)).FacePack == null, "no pack for a resident face");

        // A moving face keeps its frames and pace, as its source plays it: here 600 frames against a
        // 40-frame body, which the notes say. A single-frame hold is stretched over the body instead.
        var panting = new FaceTiming(600, 0, 0x08000001, 0f, 600f, 0);
        notes.Clear();
        var moving = TmbTimeline.Parse(new PapFile(PapExpressions.Attach(target, new FacialAnimation("cfxf_comeon", "comeon", panting),
            ["cbnm_id0"], notes)).Timeline(1));
        Assert.Equal(panting, moving.TimingOf("cfxf_comeon"));
        Assert.True(notes.Any(n => n.Contains("starts over")), "a face longer than the body is explained");
        var held = TmbTimeline.Parse(new PapFile(PapExpressions.Attach(target,
            new FacialAnimation("cfxf_smile", "smile", new FaceTiming(3652, 0, 1, 0f, 1f, 0)), ["cbnm_id0"], notes)).Timeline(1));
        Assert.Equal(new FaceTiming(40, 0, 1, 0f, 1f, 0), held.TimingOf("cfxf_smile"));

        // An animation that already plays a face keeps it, and nothing to attach to is refused.
        Assert.Throws<InvalidDataException>(() => PapExpressions.Attach(result.ToArray(), smile, ["cbnm_id0"], notes));
        Assert.Throws<InvalidDataException>(() => PapExpressions.Attach(target, smile, ["cbem_missing"], notes));
    }

    /// <summary>
    /// A game expression is found in the race's standard face animations: the pose's own pack,
    /// which the timeline then has the game load, or the resident one. A race without face
    /// animations of its own plays its skeleton parent's.
    /// </summary>
    private static void GameExpressionDonor()
    {
        Assert.True(GameExpressions.TryPose("facial/pose/smile", out var pose) && pose == "smile", "an expression's timeline");
        Assert.True(!GameExpressions.TryPose("emote/joy", out _), "a body animation is no expression");

        var game = new FakeGame();
        game.Files["chara/human/c0101/animation/f0002/nonresident/smile.pap"] = BuildPap([("cfxf_smile", 1)]);
        game.Files["chara/human/c0101/animation/f0002/resident/face.pap"] =
            BuildPap([("cfxb_blink1", 1), ("cfxf_bow", 1), ("cfxf_base", 1), ("cfxl_lip_nor1", 1)]);
        Assert.Equal(new FacialAnimation("cfxf_smile", "smile"), GameExpressions.Find(101, "smile", game.ReadFile));
        Assert.Equal(new FacialAnimation("cfxf_bow", null), GameExpressions.Find(101, "bow", game.ReadFile));
        Assert.True(GameExpressions.Find(101, "wink", game.ReadFile) == null, "a pose the race does not have");
        Assert.True(GameExpressions.Find(501, "smile", game.ReadFile) == null, "a race without face animations");

        // Elezen males have no face animations of their own, so they get their skeleton parent's.
        using var mod = new TempDir();
        const string joy = "chara/human/c0501/animation/a0001/bt_common/emote/joy.pap";
        Definition(mod, $$$"""{"Files":{"{{{joy}}}":"joy.pap"}}""");
        mod.File("joy.pap", BuildPap([("cbem_joy", 0)], model: 501));
        var request = new AnimationConversionRequest([PapPath.TryParse(joy, out var path) ? path.Location : ""],
            AnimationOperation.Expression, ConversionOutputMode.InPlace, "test")
        {
            Expression = new ExpressionDonor("/Smile", Pose: "smile"),
        };
        ushort? Parent(ushort race) => race == 501 ? 101 : null;
        var plan = new AnimationConversionPlanner(game, Parent, null).Plan(mod.Path, request);
        Assert.True(!plan.HasBlockers, string.Join(" ", plan.Diagnostics.Select(d => d.Message)));
        var output = new PapFile(plan.Files.Single(f => f.Operation == LocalFileOperation.Write).Content!);
        Assert.Equal(new[] { "cbem_joy" }, output.Entries.Select(e => e.Name).ToArray());
        var timeline = TmbTimeline.Parse(output.Timeline(0));
        Assert.Equal(["cfxf_smile"], timeline.Faces);
        Assert.Equal("smile", timeline.FacePack);
        Assert.Equal(new FaceTiming(40, 0, 1, 0f, 1f, 0), timeline.TimingOf("cfxf_smile"));

        // An expression whose emote moves the face is played at the emote's pace.
        var emote = TmbTimeline.Parse(Timeline("cfxf_base"));
        var pace = new FaceTiming(90, 0, 1, 0f, 90f, 0);
        emote.AddFace("cfxf_base", "cfxf_smile", pace);
        game.Files["chara/action/facial/pose/smile.tmb"] = emote.ToArray();
        plan = new AnimationConversionPlanner(game, Parent, null).Plan(mod.Path, request);
        Assert.True(!plan.HasBlockers, string.Join(" ", plan.Diagnostics.Select(d => d.Message)));
        output = new PapFile(plan.Files.Single(f => f.Operation == LocalFileOperation.Write).Content!);
        Assert.Equal(pace, TmbTimeline.Parse(output.Timeline(0)).TimingOf("cfxf_smile"));
    }

    /// <summary>
    /// Attaching an expression for a new mod edits the file once and keeps it in every option
    /// that plays it: a new mod's options start out empty, so each has to be given it again.
    /// </summary>
    private static void ExpressionNewModKeepsEveryOption()
    {
        const string joy = "chara/human/c0101/animation/a0001/bt_common/emote/joy.pap";
        using var mod = new TempDir();
        Definition(mod, """{"Files":{}}""", $$$"""
            [{"Type":"Single","Name":"Mouth","DefaultSettings":0,"Options":[
              {"Name":"Open","Files":{"{{{joy}}}":"joy.pap"}},
              {"Name":"Closed","Files":{"{{{joy}}}":"joy.pap"}}]}]
            """);
        mod.File("joy.pap", BuildPap([("cbem_joy", 0)]));
        var game = new FakeGame();
        game.Files["chara/human/c0101/animation/f0002/nonresident/smile.pap"] = BuildPap([("cfxf_smile", 1)]);

        var request = new AnimationConversionRequest([PapPath.TryParse(joy, out var path) ? path.Location : ""],
            AnimationOperation.Expression, ConversionOutputMode.NewMod, "test")
        {
            Expression = new ExpressionDonor("/Smile", Pose: "smile"),
        };
        var plan = new AnimationConversionPlanner(game, _ => null, null).Plan(mod.Path, request);
        Assert.True(!plan.HasBlockers, string.Join(" ", plan.Diagnostics.Select(d => d.Message)));
        Assert.Equal(1, plan.Files.Count(f => f.Operation == LocalFileOperation.Write));

        var group = plan.Result.Groups.Single(g => g.Name == "Mouth");
        foreach (var container in group.Containers)
            Assert.True(container.FileEntries().Any(e => GamePath.Normalize(e.Key) == joy),
                $"'{container.Label}' plays the edited file too");
        Assert.Equal(1, group.Containers.SelectMany(c => c.FileEntries()).Select(e => GamePath.NormalizeLocal(e.Local)).Distinct().Count());
        Assert.Equal(2, plan.Outputs.Count);
    }

    /// <summary>
    /// Adding an expression to this mod keeps the animation as it was and adds the edited one as
    /// an option group that outranks the mod's own groups: "-" plays the original, the other option
    /// the one with the face. The group holds one file per game path, so where options have their
    /// own versions, the chosen one is used.
    /// </summary>
    private static void ExpressionAddedAsGroup()
    {
        const string joy = "chara/human/c0101/animation/a0001/bt_common/emote/joy.pap";
        using var mod = new TempDir();
        Definition(mod, $$$"""{"Files":{"{{{joy}}}":"joy.pap"}}""", $$$"""
            [{"Type":"Single","Name":"Style","Priority":4,"DefaultSettings":0,"Options":[
              {"Name":"Calm","Files":{}},
              {"Name":"Wild","Files":{"{{{joy}}}":"wild.pap"}}]}]
            """);
        mod.File("joy.pap", BuildPap([("cbem_joy", 0)]));
        mod.File("wild.pap", BuildPap([("cbem_joy", 0)], havokSize: 40));
        var game = new FakeGame();
        game.Files["chara/human/c0101/animation/f0002/nonresident/smile.pap"] = BuildPap([("cfxf_smile", 1)]);

        var request = new AnimationConversionRequest([PapPath.TryParse(joy, out var path) ? path.Location : ""],
            AnimationOperation.Expression, ConversionOutputMode.AddToMod, "test")
        {
            Expression = new ExpressionDonor("/Smile", Pose: "smile"),
        };
        var planner = new AnimationConversionPlanner(game, _ => null, null);
        var plan = planner.Plan(mod.Path, request);
        Assert.True(plan.Diagnostics.Any(d => d.Code == "several_sources" && d.IsBlocker),
            "Default and 'Wild' have their own version, and one option can hold only one");

        plan = planner.Plan(mod.Path, request with { SourceContainer = ContainerAddress.Default });
        Assert.True(!plan.HasBlockers, string.Join(" ", plan.Diagnostics.Select(d => d.Message)));
        Assert.Equal("joy.pap", plan.Result.Default.FileEntries().Single().Local);
        Assert.Equal("wild.pap", plan.Result.Groups[0].Containers[1].FileEntries().Single().Local);

        var group = plan.Result.Groups.Single(g => g.Name == AnimationConversionPlanner.ExpressionGroupName);
        Assert.Equal("Single", group.Type);
        Assert.Equal(new[] { "-", "/Smile" }, group.Options.Select(o => Json.GetString(o["Name"])).ToArray());
        Assert.Equal(1, Json.GetInt(group.Node["DefaultSettings"], -1));
        Assert.Equal(5, Json.GetInt(group.Node["Priority"], 0));
        Assert.Equal(0, group.Containers[0].FileEntries().Count());

        var edited = group.Containers[1].FileEntries().Single();
        Assert.Equal(joy, GamePath.Normalize(edited.Key));
        var write = plan.Files.Single(f => f.Operation == LocalFileOperation.Write);
        Assert.True(string.Equals(write.Destination, edited.Local, StringComparison.OrdinalIgnoreCase) && edited.Local != "joy.pap",
            $"the face goes into a copy of its own, not the original: {edited.Local}");
        Assert.Equal(["cfxf_smile"], TmbTimeline.Parse(new PapFile(write.Content!).Timeline(0)).Faces);

        GearConversionExecutor.ApplyInPlace(plan, mod.Path);
        Assert.Equal(0, AnimationConversionVerifier.Verify(mod.Path, plan).Count);
        Assert.True(File.Exists(Path.Combine(mod.Path, "joy.pap")), "the original stays for \"-\"");
    }

    /// <summary>A mod that writes its keys in another case still has the moved one removed.</summary>
    private static void SwapKeyCase()
    {
        using var mod = new TempDir();
        var shouting = "Chara/Human" + Loop3["chara/human".Length..];
        Definition(mod, $$$"""{"Files":{"{{{shouting}}}":"anim\\loop.pap","{{{Start3}}}":"anim\\start.pap"}}""");
        mod.File("anim/loop.pap", BuildPap([("cbem_pose03_1lp", 0)]));
        mod.File("anim/start.pap", BuildPap([("cbem_pose03_1st", 0)]));

        var moved = Planner(Game()).Plan(mod.Path, SlotRequest(ConversionOutputMode.InPlace, ("Standing idle 5", Loop5, Start5)));
        Assert.True(!moved.HasBlockers, string.Join(" ", moved.Diagnostics.Select(d => d.Message)));
        Assert.Equal(new[] { Loop5, Start5 }, moved.Result.Default.FileEntries().Select(e => GamePath.Normalize(e.Key)).Order().ToArray());
    }

    /// <summary>
    /// Converting in place removes the files nothing plays any more, for every conversion of a
    /// run and not only the first.
    /// </summary>
    private static void RunRemovesEveryOrphan()
    {
        const string loop7 = "chara/human/c0101/animation/a0001/bt_common/emote/pose07_loop.pap";
        using var mod = new TempDir();
        Definition(mod, $$$"""{"Files":{"{{{Loop3}}}":"anim\\loop.pap","{{{Start3}}}":"anim\\start.pap","{{{Idle0}}}":"anim\\idle.pap"}}""");
        mod.File("anim/loop.pap", BuildPap([("cbem_pose03_1lp", 0)]));
        mod.File("anim/start.pap", BuildPap([("cbem_pose03_1st", 0)]));
        mod.File("anim/idle.pap", BuildPap([("cbnm_id0", 0)]));
        var game = Game();
        game.Files["chara/action/emote/pose07_loop.tmb"] = ActionTimeline("cbem_pose07_1lp");
        game.Files[loop7] = BuildPap([("cbem_pose07_1lp", 0)]);

        string Location(string path) => PapPath.TryParse(path, out var p) ? p.Location : throw new Exception(path);
        var context = new ModPlanContext(mod.Path, ConversionOutputMode.InPlace, shared: true);
        var merger = new ModPlanMerger(context);
        var slot = merger.Add("pose 3 → 5", [Location(Loop3), Location(Start3)],
            ctx => Planner(game).Plan(ctx, SlotRequest(ConversionOutputMode.InPlace, ("Standing idle 5", Loop5, Start5))));
        var idle = merger.Add("idle → 7", [Location(Idle0)], ctx => Planner(game).Plan(ctx,
            new AnimationConversionRequest([Location(Idle0)], AnimationOperation.Swap, ConversionOutputMode.InPlace, "test")
            {
                Variants = [new AnimationSwapVariant("Standing idle 7", ImmutableDictionary<string, string>.Empty.Add(Location(Idle0), Location(loop7)))],
            }));
        Assert.True(!slot.Rejected && !idle.Rejected, string.Join(" ", slot.Diagnostics.Concat(idle.Diagnostics).Select(d => d.Message)));
        context.RunFinalizers();

        var deleted = merger.Build().Files.Where(f => f.Operation == LocalFileOperation.Delete)
            .Select(f => f.Destination.Replace('\\', '/')).Order().ToArray();
        Assert.Equal(new[] { "anim/idle.pap", "anim/loop.pap", "anim/start.pap" }, deleted);
    }

    /// <summary>
    /// A face from another mod is played by name from the character's own face pack, so a race
    /// neither that mod nor the game has one for shows no face, and the plan says so.
    /// </summary>
    private static void ModFacePerRace()
    {
        const string joy = "chara/human/c0101/animation/a0001/bt_common/emote/joy.pap";
        using var mod = new TempDir();
        Definition(mod, $$$"""{"Files":{"{{{joy}}}":"joy.pap"}}""");
        mod.File("joy.pap", BuildPap([("cbem_joy", 0)]));
        var game = new FakeGame();

        AnimationConversionPlan Plan(params ushort[] races) => new AnimationConversionPlanner(game, _ => null, null).Plan(mod.Path,
            new AnimationConversionRequest([PapPath.TryParse(joy, out var path) ? path.Location : ""],
                AnimationOperation.Expression, ConversionOutputMode.InPlace, "test")
            {
                Expression = new ExpressionDonor("Other mod: grin", Face: new FacialAnimation("cfxf_grin", "grin")) { PackRaces = races },
            });

        var missing = Plan(801);
        Assert.True(!missing.HasBlockers, string.Join(" ", missing.Diagnostics.Select(d => d.Message)));
        Assert.True(missing.Diagnostics.Any(d => d.Code == "expression_race_missing" && d.Message.Contains("c0101")),
            string.Join(" ", missing.Diagnostics.Select(d => d.Message)));
        Assert.True(Plan(101).Diagnostics.All(d => d.Code != "expression_race_missing"), "the mod ships the pack for the race");

        game.Files["chara/human/c0101/animation/f0002/nonresident/grin.pap"] = BuildPap([("cfxf_grin", 1)]);
        Assert.True(Plan(801).Diagnostics.All(d => d.Code != "expression_race_missing"), "the game has a face of that name");
    }

    /// <summary>
    /// A new mod made from an animation carries none of the mod's IMC groups: one changes an
    /// item's metadata even without files, and none of them is about the animation.
    /// </summary>
    private static void AnimationNewModDropsImcGroups()
    {
        using var mod = new TempDir();
        using var output = new TempDir(create: false);
        Definition(mod, $$$"""{"Files":{"{{{Loop3}}}":"anim\\loop.pap","{{{Start3}}}":"anim\\start.pap"}}""", """
            [{"Type":"Imc","Id":"33333333-3333-3333-3333-333333333333","Name":"Hood",
              "Identifier":{"PrimaryId":6001,"SecondaryId":0,"Variant":1,"ObjectType":"Equipment","EquipSlot":"Head","BodySlot":"Unknown"},
              "DefaultEntry":{"MaterialId":2,"DecalId":0,"VfxId":0,"MaterialAnimationId":0,"AttributeMask":0,"SoundId":0},
              "Options":[{"Id":"a3333333-3333-3333-3333-333333333333","Name":"Up","AttributeMask":1}]}]
            """);
        mod.File("anim/loop.pap", BuildPap([("cbem_pose03_1lp", 0)]));
        mod.File("anim/start.pap", BuildPap([("cbem_pose03_1st", 0)]));

        var plan = Planner(Game()).Plan(mod.Path, SlotRequest(ConversionOutputMode.NewMod, ("Standing idle 5", Loop5, Start5)));
        Assert.True(!plan.HasBlockers, string.Join(" ", plan.Diagnostics.Select(d => d.Message)));
        GearConversionExecutor.WriteNewMod(plan, mod.Path, output.Path, "Idle 5");

        var result = PenumbraMod.Load(output.Path);
        Assert.Equal(0, result.Groups.Count);
        Assert.Equal(new[] { Loop5, Start5 }, result.Default.FileEntries().Select(e => GamePath.Normalize(e.Key)).Order().ToArray());
    }

    /// <summary>
    /// A facial expression mod (the pose's packs per face animation set, and the expression's
    /// timeline in each speed option) swapped to another expression: every pack moves to the
    /// new pose with its face renamed, and each option's timeline follows with its pack and face
    /// renamed, keeping its pace. Faces only swap; a pose in the shared resident pack is refused.
    /// </summary>
    private static void FaceSwap()
    {
        const string smile2 = "chara/human/c0801/animation/f0002/nonresident/smile.pap";
        const string smile3 = "chara/human/c0801/animation/f0003/nonresident/smile.pap";
        const string grin2 = "chara/human/c0801/animation/f0002/nonresident/grin.pap";
        const string grin3 = "chara/human/c0801/animation/f0003/nonresident/grin.pap";
        const string smileTmb = "chara/action/facial/pose/smile.tmb";
        const string grinTmb = "chara/action/facial/pose/grin.tmb";
        Assert.True(PapPath.TryParse(smile2, out var face) && face.IsFacial && face.Key == "smile" && face.Location == "f0002/nonresident/smile",
            "a face pack parses as facial");
        Assert.True(!PapPath.TryParse("chara/human/c0801/animation/f0002/resident/face.pap", out _), "the shared pack holds every face");

        using var mod = new TempDir();
        Definition(mod, $$$"""{"Files":{"{{{smile2}}}":"face\\{{{smile2.Replace("/", "\\\\")}}}","{{{smile3}}}":"face\\{{{smile3.Replace("/", "\\\\")}}}"}}""",
            $$$"""[{"Name":"Speed","Type":"Single","Options":[{"Name":"Full","Files":{"{{{smileTmb}}}":"full\\smile.tmb"}},{"Name":"Half","Files":{"{{{smileTmb}}}":"half\\smile.tmb"}},{"Name":"None"}]}]""");
        mod.File("face/" + smile2, BuildPap([("cfxf_smile", 1)], model: 801));
        mod.File("face/" + smile3, BuildPap([("cfxf_smile", 1)], model: 801));
        byte[] Speed(int duration)
        {
            var timeline = TmbTimeline.Parse(Timeline("cfxf_base"));
            timeline.AddFace("cfxf_base", "cfxf_smile", new FaceTiming(duration, 0, 0x08000001, 0f, 600f, 0));
            timeline.FacePack = "smile";
            return timeline.ToArray();
        }
        mod.File("full/smile.tmb", Speed(600));
        mod.File("half/smile.tmb", Speed(1200));

        var game = new FakeGame();
        game.Files[grin2] = BuildPap([("cfxf_grin", 1)], model: 801);
        game.Files["chara/human/c0801/animation/f0002/resident/face.pap"] = BuildPap([("cfxf_bow", 1)], model: 801);
        AnimationConversionRequest Request(ConversionOutputMode mode, string pose, string label) =>
            new([face.Location, "f0003/nonresident/smile"], AnimationOperation.Swap, mode, "test")
            {
                Variants = [new AnimationSwapVariant(label, ImmutableDictionary<string, string>.Empty
                    .Add(face.Location, $"f0002/nonresident/{pose}").Add("f0003/nonresident/smile", $"f0003/nonresident/{pose}"))],
            };

        var plan = new AnimationConversionPlanner(game, _ => null, null).Plan(mod.Path, Request(ConversionOutputMode.InPlace, "grin", "/Grin"));
        Assert.True(!plan.HasBlockers, string.Join(" ", plan.Diagnostics.Select(d => d.Message)));
        // The game has no f0003 grin of its own for this race: said, not refused.
        Assert.True(plan.Diagnostics.Any(d => d.Code == "no_game_face"), "a destination the game lacks is explained");

        var files = plan.Result.Default.FileEntries().ToDictionary(e => GamePath.Normalize(e.Key), e => GamePath.NormalizeLocal(e.Local));
        Assert.Equal(new[] { grin2, grin3 }, files.Keys.Order().ToArray());
        foreach (var local in files.Values)
        {
            var pap = new PapFile(plan.Files.Single(f => f.Operation == LocalFileOperation.Write && GamePath.NormalizeLocal(f.Destination) == local).Content!);
            Assert.Equal(new[] { "cfxf_grin" }, pap.Entries.Select(e => e.Name).ToArray());
            Assert.Equal(["cfxf_grin"], PapTimeline.ReadStrings(pap.ToArray()).Select(s => s.Value));
        }

        var speed = plan.Result.Groups.Single();
        foreach (var (option, duration) in new[] { (0, 600), (1, 1200) })
        {
            var entry = speed.Containers[option].FileEntries().Single();
            Assert.Equal(grinTmb, GamePath.Normalize(entry.Key));
            var tmb = TmbTimeline.Parse(plan.Files.Single(f => f.Operation == LocalFileOperation.Write &&
                                                                GamePath.NormalizeLocal(f.Destination) == GamePath.NormalizeLocal(entry.Local)).Content!);
            Assert.Equal("grin", tmb.FacePack);
            Assert.Equal(new FaceTiming(duration, 0, 0x08000001, 0f, 600f, 0), tmb.TimingOf("cfxf_grin"));
        }
        Assert.True(!speed.Containers[2].FileEntries().Any(), "the empty option stays empty");
        // In place, the files left behind are removed.
        Assert.Equal(4, plan.Files.Count(f => f.Operation == LocalFileOperation.Delete));

        // Added to the mod, the smile stays too.
        var added = new AnimationConversionPlanner(game, _ => null, null).Plan(mod.Path,
            Request(ConversionOutputMode.AddToMod, "grin", "/Grin") with { KeepOriginal = true });
        Assert.True(!added.HasBlockers, string.Join(" ", added.Diagnostics.Select(d => d.Message)));
        Assert.Equal(new[] { grin2, smile2, grin3, smile3 },
            added.Result.Default.FileEntries().Select(e => GamePath.Normalize(e.Key)).Order().ToArray());
        Assert.Equal(new[] { grinTmb, smileTmb },
            added.Result.Groups.Single().Containers[0].FileEntries().Select(e => GamePath.Normalize(e.Key)).Order().ToArray());

        // The shared resident pack holds /Bow with every other face: it cannot be replaced alone.
        var bow = new AnimationConversionPlanner(game, _ => null, null).Plan(mod.Path, Request(ConversionOutputMode.InPlace, "bow", "/Bow"));
        Assert.True(bow.Diagnostics.Any(d => d.IsBlocker && d.Message.Contains("shared face pack")), "a resident pose is refused");

        // Faces are only swapped.
        var retarget = new AnimationConversionPlanner(game, _ => null, null).Plan(mod.Path,
            new AnimationConversionRequest([face.Location], AnimationOperation.Retarget, ConversionOutputMode.NewMod, "test")
                { SourceRace = 801, TargetRaces = [101] });
        Assert.True(retarget.Diagnostics.Any(d => d.Code == "facial_swap_only" && d.IsBlocker), "a face is not retargeted");
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

    /// <summary>
    /// Converting in place, an idle goes to every slot it is given and leaves its own, unless it
    /// stays there too. A part one slot has no counterpart for still goes where there is one.
    /// </summary>
    private static void IdleSeveralSlots()
    {
        using var mod = new TempDir();
        Definition(mod, $$$"""{"Files":{"{{{Loop3}}}":"anim\\loop.pap","{{{Start3}}}":"anim\\start.pap"}}""");
        mod.File("anim/loop.pap", BuildPap([("cbem_pose03_1lp", 0)]));
        mod.File("anim/start.pap", BuildPap([("cbem_pose03_1st", 0)]));
        var request = SlotRequest(ConversionOutputMode.InPlace, ("Standing idle (default)", Idle0, null), ("Standing idle 5", Loop5, Start5));

        var moved = Planner(Game()).Plan(mod.Path, request);
        Assert.True(!moved.HasBlockers, string.Join(" ", moved.Diagnostics.Select(d => d.Message)));
        var files = moved.Result.Default.FileEntries().ToDictionary(e => GamePath.Normalize(e.Key), e => GamePath.NormalizeLocal(e.Local));
        Assert.Equal(new[] { Idle0, Loop5, Start5 }.Order().ToArray(), files.Keys.Order().ToArray());
        string Played(string gamePath) => new PapFile(moved.Files.Single(f => f.Operation == LocalFileOperation.Write &&
            GamePath.NormalizeLocal(f.Destination) == files[gamePath]).Content!).Entries.Single().Name;
        Assert.Equal("cbnm_id0", Played(Idle0));
        Assert.Equal("cbem_pose05_1lp", Played(Loop5));
        Assert.Equal("cbem_pose05_1st", Played(Start5));
        var unpaired = moved.Diagnostics.Single(d => d.Code == "unpaired");
        Assert.True(unpaired.Message.Contains("Standing idle (default) has no start animation of its own") &&
                    unpaired.Message.Contains("left out there"), unpaired.Message);
        Assert.Equal(new[] { "anim/loop.pap", "anim/start.pap" }, moved.Files.Where(f => f.Operation == LocalFileOperation.Delete)
            .Select(f => f.Destination.Replace('\\', '/')).Order().ToArray());

        var kept = Planner(Game()).Plan(mod.Path, request with { StaysAtSource = true });
        Assert.True(!kept.HasBlockers, string.Join(" ", kept.Diagnostics.Select(d => d.Message)));
        Assert.Equal(new[] { Idle0, Loop3, Start3, Loop5, Start5 }.Order().ToArray(),
            kept.Result.Default.FileEntries().Select(e => GamePath.Normalize(e.Key)).Order().ToArray());
        Assert.True(kept.Files.All(f => f.Operation != LocalFileOperation.Delete), "the idle stays in its own slot");
        Assert.Equal(3, kept.Files.Count(f => f.Operation == LocalFileOperation.Write));
    }

    /// <summary>
    /// A new mod holds the idle in every slot it plays in: renamed for each, and a copy where it
    /// is when it stays there too. Nothing else of the mod comes along.
    /// </summary>
    private static void IdleSeveralSlotsNewMod()
    {
        using var mod = new TempDir();
        using var output = new TempDir(create: false);
        mod.Json("meta.json", $$$"""
            {"FileVersion":4,"Identifier":"{{{Guid.NewGuid()}}}","Name":"Idle",
             "DefaultData":{"Files":{"{{{Loop3}}}":"anim\\loop.pap","chara/other.tex":"x.tex"} } }
            """);
        mod.File("anim/loop.pap", BuildPap([("cbem_pose03_1lp", 0)]));

        var request = SlotRequest(ConversionOutputMode.NewMod, ("Standing idle (default)", Idle0, null), ("Standing idle 5", Loop5, Start5)) with
        {
            StaysAtSource = true,
        };
        var plan = Planner(Game()).Plan(mod.Path, request);
        Assert.True(!plan.HasBlockers, string.Join(" ", plan.Diagnostics.Select(d => d.Message)));
        GearConversionExecutor.WriteNewMod(plan, mod.Path, output.Path, "Idle (slots)");

        var result = PenumbraMod.Load(output.Path);
        Assert.Equal(0, result.Groups.Count);
        var files = result.Default.FileEntries().ToDictionary(e => GamePath.Normalize(e.Key), e => e.Local);
        Assert.Equal(new[] { Idle0, Loop3, Loop5 }.Order().ToArray(), files.Keys.Order().ToArray());
        string Played(string gamePath) => new PapFile(File.ReadAllBytes(Path.Combine(output.Path, files[gamePath]))).Entries.Single().Name;
        Assert.Equal("cbnm_id0", Played(Idle0));
        Assert.Equal("cbem_pose03_1lp", Played(Loop3));
        Assert.Equal("cbem_pose05_1lp", Played(Loop5));
        Assert.True(plan.Diagnostics.All(d => !d.Message.Contains("cbna_add_dmg_f")),
            "The hit reaction the game keeps beside its default idle is not worth a warning.");
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

        var request = SlotRequest(ConversionOutputMode.AddToMod,
            ("Standing idle (default)", Idle0, null), ("Standing idle 3", Loop3, Start3), ("Standing idle 5", Loop5, Start5)) with
        {
            KeepOriginal = true,
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
    /// An idle inside an option goes to its new slots in that option, beside everything else the
    /// option holds; the group keeps its shape and IDs. Converting in place moves it there.
    /// </summary>
    private static void IdleSlotsInOption()
    {
        using var mod = new TempDir();
        Definition(mod, """{"Files":{}}""",
            $$$"""[{"Name":"Style","Type":"Single","Priority":3,"DefaultSettings":1,"Options":[{"Name":"Off"},{"Id":"keep-me","Name":"A","Files":{"{{{Loop3}}}":"a.pap","chara/other.tex":"other.tex"} } ] } ]""");
        mod.File("a.pap", BuildPap([("cbem_pose03_1lp", 0)]));
        mod.File("other.tex", [1]);
        var request = SlotRequest(ConversionOutputMode.AddToMod, ("Standing idle (default)", Idle0, null), ("Standing idle 5", Loop5, null)) with
        {
            KeepOriginal = true,
        };
        var plan = Planner(Game()).Plan(mod.Path, request);
        Assert.True(!plan.HasBlockers, string.Join(" ", plan.Diagnostics.Select(d => d.Message)));

        var style = plan.Result.Groups.Single();
        Assert.Equal(new[] { "Off", "A" }, style.Options.Select(o => Json.GetString(o["Name"])).ToArray());
        Assert.Equal("keep-me", Json.GetString(style.Options.ElementAt(1)["Id"]));
        Assert.Equal(0, style.Containers[0].FileEntries().Count());
        Assert.Equal(new[] { Idle0, Loop3, Loop5, "chara/other.tex" }.Order().ToArray(),
            style.Containers[1].FileEntries().Select(e => GamePath.Normalize(e.Key)).Order().ToArray());

        plan = Planner(Game()).Plan(mod.Path, request with { Mode = ConversionOutputMode.InPlace, KeepOriginal = false });
        Assert.True(!plan.HasBlockers, string.Join(" ", plan.Diagnostics.Select(d => d.Message)));
        Assert.Equal(2, plan.Result.Groups.Single().Containers.Count);
        Assert.Equal(new[] { Idle0, Loop5, "chara/other.tex" }.Order().ToArray(),
            plan.Result.Groups.Single().Containers[1].FileEntries().Select(e => GamePath.Normalize(e.Key)).Order().ToArray());
    }

    /// <summary>
    /// Added to the mod, the idle plays in its new slot and keeps its own, in Default or in its
    /// options; options with a version of their own each place theirs, so they still choose.
    /// </summary>
    private static void IdleSlotsKeepOriginal()
    {
        using var mod = new TempDir();
        Definition(mod, $$$"""{"Files":{"{{{Loop3}}}":"a.pap"}}""");
        mod.File("a.pap", BuildPap([("cbem_pose03_1lp", 0)]));
        var request = SlotRequest(ConversionOutputMode.AddToMod, ("Standing idle 5", Loop5, null)) with { KeepOriginal = true };
        var plan = Planner(Game()).Plan(mod.Path, request);
        Assert.True(!plan.HasBlockers, string.Join(" ", plan.Diagnostics.Select(d => d.Message)));
        Assert.Equal(new[] { Loop3, Loop5 }.Order().ToArray(),
            plan.Result.Default.FileEntries().Select(e => GamePath.Normalize(e.Key)).Order().ToArray());
        GearConversionExecutor.ApplyInPlace(plan, mod.Path);
        Assert.True(File.Exists(Path.Combine(mod.Path, "a.pap")), "the original file stays");

        Definition(mod, """{"Files":{}}""",
            $$$"""[{"Name":"Style","Type":"Single","Options":[{"Name":"A","Files":{"{{{Loop3}}}":"a.pap"} },{"Name":"B","Files":{"{{{Loop3}}}":"b.pap"} } ] } ]""");
        mod.File("b.pap", BuildPap([("cbem_pose03_1lp", 0)], havokSize: 24)); // told apart from a.pap by its size
        plan = Planner(Game()).Plan(mod.Path, request);
        Assert.True(!plan.HasBlockers, string.Join(" ", plan.Diagnostics.Select(d => d.Message)));
        var style = plan.Result.Groups.Single();
        foreach (var (option, source) in new[] { (0, "a.pap"), (1, "b.pap") })
        {
            var files = style.Containers[option].FileEntries().ToDictionary(e => GamePath.Normalize(e.Key), e => e.Local);
            Assert.Equal(source, files[Loop3]);
            var placed = plan.Files.Single(f => f.Operation == LocalFileOperation.Write &&
                                                GamePath.NormalizeLocal(f.Destination) == GamePath.NormalizeLocal(files[Loop5]));
            Assert.Equal(source, placed.Source);
        }
    }

    /// <summary>
    /// Retargeting on the way, the target races get the source race's idle, rebuilt once for
    /// each, in every slot it goes to and where it stays; a race the game has no file of its own
    /// for in one of them gets none there. A new mod holds the source race's idle too unless told
    /// not to, and converting in place moves it from the source race to the target races.
    /// </summary>
    private static void IdleSlotsRetargeted()
    {
        using var mod = new TempDir();
        Definition(mod, $$$"""{"Files":{"{{{Loop3}}}":"c0101\\loop.pap"}}""");
        mod.File("c0101/loop.pap", BuildPap([("cbem_pose03_1lp", 0)], model: 101));
        var game = Game();
        game.Files[PapPath.BaseSkeletonPath(101)] = [1];
        game.Files[PapPath.BaseSkeletonPath(301)] = [3];
        game.Files[PapPath.BaseSkeletonPath(1101)] = [2];
        static string Race(string path, ushort race) => path.Replace("c0101", $"c{race:D4}");
        // Lalafell males have both slots of their own; Highlander males only slot 5.
        game.Files[Race(Loop3, 1101)] = BuildPap([("cbem_pose03_1lp", 0)], model: 1101);
        game.Files[Race(Loop5, 1101)] = BuildPap([("cbem_pose05_1lp", 0)], model: 1101);
        game.Files[Race(Loop5, 301)] = BuildPap([("cbem_pose05_1lp", 0)], model: 301);
        var retargeter = new FakeRetargeter();
        ushort? Parent(ushort race) => race switch { 1201 => 1101, 1101 => 101, 101 => null, _ => 101 };
        var request = SlotRequest(ConversionOutputMode.NewMod, ("Standing idle 5", Loop5, Start5)) with
        {
            StaysAtSource = true,
            SourceRace = 101,
            TargetRaces = [1101, 301],
        };
        var plan = new AnimationConversionPlanner(game, Parent, retargeter).Plan(mod.Path, request);
        Assert.True(!plan.HasBlockers, string.Join(" ", plan.Diagnostics.Select(d => d.Message)));
        Assert.Equal(2, retargeter.Calls);

        var files = plan.Result.Default.FileEntries().ToDictionary(e => GamePath.Normalize(e.Key), e => GamePath.NormalizeLocal(e.Local));
        Assert.Equal(new[] { Loop3, Loop5, Race(Loop3, 1101), Race(Loop5, 1101), Race(Loop5, 301) }.Order().ToArray(),
            files.Keys.Order().ToArray());
        Assert.Equal(@"c1101\loop.pap", files[Race(Loop3, 1101)]);
        PapFile Written(string gamePath) => new(plan.Files.Single(f => f.Operation == LocalFileOperation.Write &&
            GamePath.NormalizeLocal(f.Destination) == files[gamePath]).Content!);
        Assert.Equal((ushort)1101, Written(Race(Loop3, 1101)).ModelId);
        Assert.Equal((ushort)1101, Written(Race(Loop5, 1101)).ModelId);
        Assert.Equal("cbem_pose05_1lp", Written(Race(Loop5, 1101)).Entries.Single().Name);
        Assert.Equal((ushort)301, Written(Race(Loop5, 301)).ModelId);
        Assert.True(plan.Diagnostics.Any(d => d.Code == "no_race_file" && d.Message.Contains("Highlander")),
            "a race without the source slot of its own gets none there, and is told");
        Assert.True(plan.Diagnostics.Any(d => d.Code == "inherited_by" && d.Message.Contains("Lalafell Female")),
            "Lalafell female inherits the new Lalafell male files.");

        string[] Keys(AnimationConversionPlan result) => [.. result.Result.Default.FileEntries().Select(e => GamePath.Normalize(e.Key)).Order()];
        var others = new[] { Race(Loop3, 1101), Race(Loop5, 1101), Race(Loop5, 301) }.Order().ToArray();
        // Left out of the new mod, the source race gets nothing, wherever the others go.
        var without = new AnimationConversionPlanner(game, Parent, new FakeRetargeter()).Plan(mod.Path, request with { IncludeSourceRace = false });
        Assert.True(!without.HasBlockers, string.Join(" ", without.Diagnostics.Select(d => d.Message)));
        Assert.Equal(others, Keys(without));

        // Converting in place moves it to the target races, whatever the request says; the source file goes.
        var moved = new AnimationConversionPlanner(game, Parent, new FakeRetargeter()).Plan(mod.Path, request with { Mode = ConversionOutputMode.InPlace });
        Assert.True(!moved.HasBlockers, string.Join(" ", moved.Diagnostics.Select(d => d.Message)));
        Assert.Equal(others, Keys(moved));
        Assert.Equal(new[] { "c0101/loop.pap" },
            moved.Files.Where(f => f.Operation == LocalFileOperation.Delete).Select(f => f.Destination.Replace('\\', '/')).ToArray());

        // Added to this mod, it keeps its own and gets the new slot too, whatever the request says.
        var added = new AnimationConversionPlanner(game, Parent, new FakeRetargeter()).Plan(mod.Path,
            request with { Mode = ConversionOutputMode.AddToMod, KeepOriginal = true, IncludeSourceRace = false });
        Assert.True(!added.HasBlockers, string.Join(" ", added.Diagnostics.Select(d => d.Message)));
        Assert.Equal(new[] { Loop3, Loop5 }.Concat(others).Order().ToArray(), Keys(added));
    }

    /// <summary>
    /// Added to this mod, an expression where the idle is goes into an option group, which holds
    /// the target races of a retarget too; they also get their rebuilt idle without the face
    /// beside the source race's, so "-" plays all of it without. Its new slots get the face.
    /// </summary>
    private static void IdleExpressionAtSource()
    {
        using var mod = new TempDir();
        Definition(mod, $$$"""{"Files":{"{{{Loop3}}}":"c0101\\loop.pap"}}""");
        mod.File("c0101/loop.pap", BuildPap([("cbem_pose03_1lp", 0)], model: 101));
        var game = Game();
        game.Files["chara/human/c0101/animation/f0002/nonresident/smile.pap"] = BuildPap([("cfxf_smile", 1)]);
        game.Files[PapPath.BaseSkeletonPath(101)] = [1];
        game.Files[PapPath.BaseSkeletonPath(1101)] = [2];
        var lala3 = Loop3.Replace("c0101", "c1101");
        var lala5 = Loop5.Replace("c0101", "c1101");
        game.Files[lala3] = BuildPap([("cbem_pose03_1lp", 0)], model: 1101);
        game.Files[lala5] = BuildPap([("cbem_pose05_1lp", 0)], model: 1101);
        var request = SlotRequest(ConversionOutputMode.AddToMod, ("Standing idle 5", Loop5, Start5)) with
        {
            StaysAtSource = true,
            KeepOriginal = true,
            SourceRace = 101,
            TargetRaces = [1101],
            Expression = new ExpressionDonor("/Smile", Pose: "smile"),
        };
        var plan = new AnimationConversionPlanner(game, race => race == 1101 ? (ushort)101 : null, new FakeRetargeter())
            .Plan(mod.Path, request);
        Assert.True(!plan.HasBlockers, string.Join(" ", plan.Diagnostics.Select(d => d.Message)));
        string[] Faces(string local) => [.. TmbTimeline.Parse(new PapFile(plan.Files.Single(f => f.Operation == LocalFileOperation.Write &&
            GamePath.NormalizeLocal(f.Destination) == GamePath.NormalizeLocal(local)).Content!).Timeline(0)).Faces];

        var files = plan.Result.Default.FileEntries().ToDictionary(e => GamePath.Normalize(e.Key), e => e.Local);
        Assert.Equal(new[] { Loop3, Loop5, lala3, lala5 }.Order().ToArray(), files.Keys.Order().ToArray());
        Assert.Equal(@"c0101\loop.pap", files[Loop3]);
        Assert.Equal(0, Faces(files[lala3]).Length);
        Assert.Equal(["cfxf_smile"], Faces(files[Loop5]));
        Assert.Equal(["cfxf_smile"], Faces(files[lala5]));

        var group = plan.Result.Groups.Single(g => g.Name == AnimationConversionPlanner.ExpressionGroupName);
        Assert.Equal(0, group.Containers[0].FileEntries().Count());
        var faced = group.Containers[1].FileEntries().ToDictionary(e => GamePath.Normalize(e.Key), e => e.Local);
        Assert.Equal(new[] { Loop3, lala3 }.Order().ToArray(), faced.Keys.Order().ToArray());
        Assert.True(faced.Values.All(local => Faces(local).SequenceEqual(["cfxf_smile"])), "the group plays the face for both races");
        Assert.True(!string.Equals(GamePath.NormalizeLocal(faced[lala3]), GamePath.NormalizeLocal(files[lala3]), StringComparison.Ordinal),
            "the Lalafell idle with the face is a file of its own");
    }

    private static void SwapSkipsRaceWithoutFile()
    {
        using var mod = new TempDir();
        var loop = Loop3.Replace("c0101", "c0801");
        Definition(mod, $$$"""{"Files":{"{{{loop}}}":"loop.pap","{{{Loop3}}}":"loop.pap"}}""");
        mod.File("loop.pap", BuildPap([("cbem_pose03_1lp", 0)]));
        // c0801 has no pose05 of its own: the game plays c0101's and never asks for one, so only
        // c0101 gets the swapped file, and c0801's is removed with the rest.
        var plan = new AnimationConversionPlanner(Game(), race => race == 801 ? (ushort)101 : null, null)
            .Plan(mod.Path, SlotRequest(ConversionOutputMode.InPlace, ("Standing idle 5", Loop5, Start5)));
        Assert.True(!plan.HasBlockers, string.Join(" ", plan.Diagnostics.Select(d => d.Message)));
        Assert.True(plan.Diagnostics.Any(d => d.Code == "no_race_file" && d.Message.Contains("Miqo'te") &&
                                              d.Message.Contains("removed")), "the skipped race is explained");
        Assert.Equal(new[] { Loop5 }, plan.Result.Default.FileEntries().Select(e => GamePath.Normalize(e.Key)).ToArray());
        var write = plan.Files.Single(f => f.Operation == LocalFileOperation.Write);
        Assert.Equal("cbem_pose05_1lp", new PapFile(write.Content!).Entries[0].Name);

        // A new mod leaves the race out of every slot it has no file of its own in.
        var several = new AnimationConversionPlanner(Game(), _ => null, null).Plan(mod.Path,
            SlotRequest(ConversionOutputMode.NewMod, ("Standing idle (default)", Idle0, null), ("Standing idle 5", Loop5, Start5)));
        Assert.True(!several.HasBlockers, string.Join(" ", several.Diagnostics.Select(d => d.Message)));
        Assert.True(several.Diagnostics.Count(d => d.Code == "no_race_file" && d.Message.Contains("left out")) == 2,
            "explained for each slot");
        Assert.Equal(new[] { Idle0, Loop5 }.Order().ToArray(),
            several.Result.Default.FileEntries().Select(e => GamePath.Normalize(e.Key)).Order().ToArray());
    }

    private static void SwapWritingNothingIsRefused()
    {
        using var mod = new TempDir();
        Definition(mod, $$$"""{"Files":{"{{{Loop3}}}":"loop.pap"}}""");
        mod.File("loop.pap", BuildPap([("cbem_pose03_1lp", 0)]));
        // The game has no idle 5 files, so no race has anything to convert to.
        var game = Game();
        game.Files.Remove(Loop5);
        game.Files.Remove(Start5);
        var request = SlotRequest(ConversionOutputMode.InPlace, ("Standing idle 5", Loop5, Start5));

        // Converting in place would delete the mod's only idle and write none: refused.
        var plan = Planner(game).Plan(mod.Path, request);
        Assert.True(plan.Diagnostics.Any(d => d.Code == "nothing_written" && d.IsBlocker), "the empty swap is a blocker");

        // Keeping the original loses nothing, so the plan stands (with its warning).
        var kept = Planner(game).Plan(mod.Path, request with { KeepOriginal = true });
        Assert.True(!kept.Diagnostics.Any(d => d.Code == "nothing_written"), "nothing is removed, so nothing is refused");
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
        game.Files[PapPath.BaseSkeletonPath(301)] = [3];
        game.Files[PapPath.BaseSkeletonPath(1101)] = [2];
        // Lalafell males have the animation of their own; Highlander males play the Midlander one.
        game.Files[Loop3.Replace("c0101", "c1101")] = BuildPap([("cbem_pose03_1lp", 0)], model: 1101);
        var retargeter = new FakeRetargeter();
        var request = new AnimationConversionRequest([PapPath.TryParse(Loop3, out var p) ? p.Location : ""],
            AnimationOperation.Retarget, ConversionOutputMode.InPlace, "test")
        {
            SourceRace = 101,
            TargetRaces = [1101, 301],
        };
        ushort? Parent(ushort race) => race switch { 1201 => 1101, 1101 => 101, 101 => null, _ => 101 };
        var plan = new AnimationConversionPlanner(game, Parent, retargeter).Plan(mod.Path, request);
        Assert.True(!plan.HasBlockers, string.Join(" ", plan.Diagnostics.Select(d => d.Message)));
        Assert.Equal(1, retargeter.Calls);
        // The game never asks for a file of the Highlander male's own, so none is written.
        Assert.True(plan.Diagnostics.Any(d => d.Code == "no_race_file" && d.Message.Contains("Highlander")),
            "a race without a file of its own is skipped and told");
        // Named, not coded: the message is for someone reading the plan, not the file paths.
        Assert.True(plan.Diagnostics.Any(d => d.Code == "inherited_by" && d.Message.Contains("Lalafell Female")),
            "Lalafell female inherits the new Lalafell male file.");
        var unavailable = new AnimationConversionPlanner(game, Parent, new FakeRetargeter { Reason = "no" }).Plan(mod.Path, request);
        Assert.True(unavailable.Diagnostics.Any(d => d.IsBlocker && d.Code == "retarget_unavailable"));

        // A new mod holds the source race's animation too, unless told not to.
        var target = Loop3.Replace("c0101", "c1101");
        string[] Keys(AnimationConversionPlan result) => [.. result.Result.Default.FileEntries().Select(e => GamePath.Normalize(e.Key)).Order()];
        var copied = new AnimationConversionPlanner(game, Parent, new FakeRetargeter()).Plan(mod.Path, request with { Mode = ConversionOutputMode.NewMod });
        Assert.True(!copied.HasBlockers, string.Join(" ", copied.Diagnostics.Select(d => d.Message)));
        Assert.Equal(new[] { Loop3, target }, Keys(copied));
        var alone = new AnimationConversionPlanner(game, Parent, new FakeRetargeter()).Plan(mod.Path,
            request with { Mode = ConversionOutputMode.NewMod, IncludeSourceRace = false });
        Assert.Equal(new[] { target }, Keys(alone));

        // Added to this mod, the source race keeps its own whatever the request says.
        var added = new AnimationConversionPlanner(game, Parent, new FakeRetargeter()).Plan(mod.Path,
            request with { Mode = ConversionOutputMode.AddToMod, IncludeSourceRace = false });
        Assert.Equal(new[] { Loop3, target }, Keys(added));

        // Converting in place moves the animation from the source race to the target races.
        GearConversionExecutor.ApplyInPlace(plan, mod.Path);
        var files = PenumbraMod.Load(mod.Path).Default.FileEntries().ToDictionary(e => GamePath.Normalize(e.Key), e => e.Local);
        Assert.Equal(new[] { target }, files.Keys.ToArray());
        Assert.Equal(@"c1101\loop.pap", files[target]);
        Assert.Equal((ushort)1101, new PapFile(File.ReadAllBytes(Path.Combine(mod.Path, files[target]))).ModelId);
        Assert.True(!File.Exists(Path.Combine(mod.Path, "c0101", "loop.pap")), "the source race's file goes with it");
    }

    private static void RetargetRemovesReplacedFile()
    {
        using var mod = new TempDir();
        var target = Loop3.Replace("c0101", "c1101");
        Definition(mod, $$$"""{"Files":{"{{{Loop3}}}":"c0101\\loop.pap","{{{target}}}":"c1101\\mine.pap"}}""");
        mod.File("c0101/loop.pap", BuildPap([("cbem_pose03_1lp", 0)], model: 101));
        mod.File("c1101/mine.pap", BuildPap([("cbem_pose03_1lp", 0)], model: 1101));
        var game = Game();
        game.Files[PapPath.BaseSkeletonPath(101)] = [1];
        game.Files[PapPath.BaseSkeletonPath(1101)] = [2];
        game.Files[target] = BuildPap([("cbem_pose03_1lp", 0)], model: 1101);
        ushort? Parent(ushort race) => race switch { 1101 => 101, _ => null };
        var request = new AnimationConversionRequest([PapPath.TryParse(Loop3, out var p) ? p.Location : ""],
            AnimationOperation.Retarget, ConversionOutputMode.InPlace, "test")
        {
            SourceRace = 101,
            TargetRaces = [1101],
        };

        // The rebuilt file takes the Lalafell key over, so the mod's own file for it is dead weight,
        // whether the Midlander original stays (adding) or goes (in place).
        foreach (var mode in new[] { ConversionOutputMode.InPlace, ConversionOutputMode.AddToMod })
        {
            var plan = new AnimationConversionPlanner(game, Parent, new FakeRetargeter()).Plan(mod.Path, request with { Mode = mode });
            Assert.True(!plan.HasBlockers, string.Join(" ", plan.Diagnostics.Select(d => d.Message)));
            Assert.True(plan.Diagnostics.Any(d => d.Code == "destination_replaced"), $"{mode}: the replacement is reported");
            Assert.True(plan.Files.Any(f => f.Operation == LocalFileOperation.Delete && f.Destination == @"c1101\mine.pap"),
                $"{mode}: the replaced file is removed");
            Assert.True(plan.Files.Any(f => f.Operation == LocalFileOperation.Delete && f.Destination == @"c0101\loop.pap") == (mode == ConversionOutputMode.InPlace),
                $"{mode}: the source race's file goes only when converting in place");
        }
    }

    /// <summary>
    /// Finding skeletons is the retargeter's job: the planner needs none of the game's, hands over
    /// every base skeleton the mod replaces, in any option (one whose file is missing left out),
    /// and names the skeleton a file was made for once, however many races it is rebuilt for.
    /// </summary>
    private static void RetargetHandsOverSkeletons()
    {
        using var mod = new TempDir();
        var midlander = PapPath.BaseSkeletonPath(101);
        var lalafell = PapPath.BaseSkeletonPath(1101);
        var highlander = PapPath.BaseSkeletonPath(301);
        Definition(mod, $$$"""{"Files":{"{{{Loop3}}}":"c0101\\loop.pap","{{{midlander}}}":"skl\\c0101.sklb"}}""",
            $$$"""[{"Name":"Skeleton","Type":"Single","Options":[{"Name":"Small","Files":{"{{{lalafell}}}":"skl\\c1101.sklb","{{{highlander}}}":"skl\\gone.sklb"}}]}]""");
        mod.File("c0101/loop.pap", BuildPap([("cbem_pose03_1lp", 0)], model: 101));
        mod.File("skl/c0101.sklb", [1]);
        mod.File("skl/c1101.sklb", [11]);
        var game = Game();
        game.Files[Loop3.Replace("c0101", "c1101")] = BuildPap([("cbem_pose03_1lp", 0)], model: 1101);
        game.Files[Loop3.Replace("c0101", "c1201")] = BuildPap([("cbem_pose03_1lp", 0)], model: 1201);
        var retargeter = new FakeRetargeter { Source = "the Midlander Male skeleton from IVCS (168 bones)" };
        var request = new AnimationConversionRequest([PapPath.TryParse(Loop3, out var p) ? p.Location : ""],
            AnimationOperation.Retarget, ConversionOutputMode.NewMod, "test")
        {
            SourceRace = 101,
            TargetRaces = [1101, 1201],
        };
        ushort? Parent(ushort race) => race switch { 1201 => 1101, 1101 => 101, _ => null };
        var plan = new AnimationConversionPlanner(game, Parent, retargeter).Plan(mod.Path, request);
        Assert.True(!plan.HasBlockers, string.Join(" ", plan.Diagnostics.Select(d => d.Message)));

        Assert.Equal(new ushort[] { 1101, 1201 }, retargeter.Requests.Select(r => r.TargetRace).Order().ToArray());
        foreach (var sent in retargeter.Requests)
        {
            Assert.Equal((ushort)101, sent.SourceRace);
            Assert.Equal(new[] { ("Default", (ushort)101, (byte)1), ("Skeleton / Small", (ushort)1101, (byte)11) },
                sent.ModSkeletons.Select(s => (s.Label, s.Race, s.Bytes[0])).ToArray());
            Assert.Equal((ushort?)1101, sent.ParentRace(1201));
        }
        Assert.Equal(1, plan.Diagnostics.Count(d => d.Code == "retarget_source" && d.Message.Contains("from IVCS")));
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
            writer.Write((short)(entries[i].Face != 0 ? 17 : 0)); // facial animations are type 17
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

    /// <summary>
    /// A timeline as the game lays one out: TMDH, TMAL, one actor (TMAC) with one track (TMTR)
    /// playing <paramref name="motion"/> in a C009, then the id lists and the string.
    /// </summary>
    internal static byte[] Timeline(string motion, int duration = 40)
    {
        const int tmal = 28, tmac = 44, tmtr = 72, c009 = 96, lists = 120, text = 126;
        var name = Encoding.ASCII.GetBytes(motion + "\0");
        var bytes = new byte[text + name.Length];
        void Item(int at, string magic, int size, short id = -1)
        {
            Encoding.ASCII.GetBytes(magic).CopyTo(bytes, at);
            BinaryPrimitives.WriteInt32LittleEndian(bytes.AsSpan(at + 4), size);
            if (id >= 0) BinaryPrimitives.WriteInt16LittleEndian(bytes.AsSpan(at + 8), id);
        }
        // Pointers count from their item's start + 8; lists carry their length after the pointer.
        void Pointer(int item, int field, int target, int count = -1)
        {
            BinaryPrimitives.WriteInt32LittleEndian(bytes.AsSpan(item + field), target - (item + 8));
            if (count >= 0) BinaryPrimitives.WriteInt32LittleEndian(bytes.AsSpan(item + field + 4), count);
        }

        "TMLB"u8.CopyTo(bytes);
        BinaryPrimitives.WriteInt32LittleEndian(bytes.AsSpan(4), bytes.Length);
        BinaryPrimitives.WriteInt32LittleEndian(bytes.AsSpan(8), 5);
        Item(12, "TMDH", 16, 1);
        BinaryPrimitives.WriteInt16LittleEndian(bytes.AsSpan(12 + 12), (short)duration);
        BinaryPrimitives.WriteInt16LittleEndian(bytes.AsSpan(12 + 14), 3);
        Item(tmal, "TMAL", 16);
        Pointer(tmal, 8, lists, 1);
        Item(tmac, "TMAC", 28, 2);
        Pointer(tmac, 20, lists + 2, 1);
        Item(tmtr, "TMTR", 24, 3);
        Pointer(tmtr, 12, lists + 4, 1);
        Item(c009, "C009", 24, 4);
        BinaryPrimitives.WriteInt32LittleEndian(bytes.AsSpan(c009 + 12), duration);
        Pointer(c009, 20, text);
        BinaryPrimitives.WriteInt16LittleEndian(bytes.AsSpan(lists), 2);
        BinaryPrimitives.WriteInt16LittleEndian(bytes.AsSpan(lists + 2), 3);
        BinaryPrimitives.WriteInt16LittleEndian(bytes.AsSpan(lists + 4), 4);
        name.CopyTo(bytes, text);
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
        public string? Source { get; init; }
        public List<RetargetRequest> Requests { get; } = [];
        public int Calls => Requests.Count;
        public string? UnavailableReason => Reason;

        public RetargetedPap Retarget(RetargetRequest request)
        {
            Requests.Add(request);
            return new RetargetedPap(new PapFile(request.Pap).WithModel(request.TargetRace, 0), ["test note"], Source);
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
            Path = System.IO.Path.Combine(System.IO.Path.GetTempPath(), "umc-anim-" + Guid.NewGuid().ToString("N"));
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
