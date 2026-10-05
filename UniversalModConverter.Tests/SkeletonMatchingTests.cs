using System.Collections.Immutable;
using System.Numerics;
using UniversalModConverter.Core;

/// <summary>Finding the skeleton an animation was made for, and the one to rebuild it for, on synthetic skeletons.</summary>
internal static class SkeletonMatchingTests
{
    public static (string Name, Action Run)[] All =>
    [
        ("A source skeleton holds every bone the animation binds", SourceMustFit),
        ("The animation's own race wins over its skeleton parents, and those over others", SourcePrefersRace),
        ("Among fitting skeletons, the game's layout and then the tightest wins", SourcePrefersGameLayout),
        ("Skeletons that fit alike are no rivals; differently resting ones are", SourceRivals),
        ("A predictive animation fits the leading bones of a longer skeleton", SourceLeadingBones),
        ("A quantized animation only fits its own race's whole skeleton", SourceQuantized),
        ("Standard layouts: the game's bones first, then IVCS, then YAS", StandardLayouts),
        ("The target is the smallest standard with every bone that moves", TargetSmallestStandard),
        ("Game bones skeleton mods keep at other indices get no track", UnportableBones),
        ("Float noise does not count as motion", MotionThreshold),
        ("Installed skeletons are read from every definition layout", InstalledDefinitions),
    ];

    private const ushort Miqote = 801, Midlander = 201, AuRa = 1401;

    private static readonly string[] Game = ["n_root", "j_kosi", "j_sebo_a", "j_kubi", "n_hara_noanim_trans"];

    // IVCS and YAS keep the game's bones first; the game added its last bone after they fixed
    // their layouts, so IVCS has it at the end and YAS lacks it.
    private static readonly string[] Ivcs = [.. Game[..4], "iv_a", "iv_b", "n_hara_noanim_trans"];
    private static readonly string[] Yas = [.. Game[..4], "iv_a", "iv_b", "ya_a"];
    private static readonly string[] Big = [.. Game[..4], "iv_a", "iv_b", "nf_a", "nf_b", "nf_c", "n_hara_noanim_trans"];

    private static ushort? Parent(ushort race) => race switch { Miqote or AuRa => Midlander, Midlander => 101, _ => null };

    private static SkeletonDescription Skeleton(string name, string[] bones, float length = 0.1f)
        => new(name, [.. bones.Select((bone, i) => new SkeletonBone(bone, (short)(i - 1),
            BoneTransform.Identity with { Position = new Vector3(0, i == 0 ? 0 : length, 0) }))], [], [], []);

    private static SkeletonCandidate Candidate(string[] bones, SkeletonOrigin origin, ushort race, string label = "mod", float length = 0.1f)
        => new(Skeleton("skeleton", bones, length), origin, [race], label);

    private static AnimationChannels Binding(params short[] bones) => new("skeleton", [.. bones], [], []);

    private static SkeletonDescription GameSkeleton => Skeleton("skeleton", Game);

    /// <summary>
    /// The reported case: an idle made for a large skeleton mod binds bones far beyond the game's
    /// skeleton. Only a skeleton with that many bones is its source, wherever it is installed.
    /// </summary>
    private static void SourceMustFit()
    {
        AnimationChannels[] channels = [Binding(0, 1, 2, 3, 6, 9)];
        var game = new SkeletonCandidate(GameSkeleton, SkeletonOrigin.Game, [Miqote], "game");
        var ivcs = Candidate(Ivcs, SkeletonOrigin.Installed, Miqote, "IVCS");
        var big = Candidate(Big, SkeletonOrigin.Installed, Miqote, "Big");
        Assert.True(!SkeletonMatcher.Fits(channels, GameSkeleton), "bone 9 is not on the game's skeleton");
        Assert.Equal(null, SkeletonMatcher.ChooseSource(channels, [game, ivcs], Miqote, GameSkeleton, Parent));
        Assert.Equal("Big", SkeletonMatcher.ChooseSource(channels, [game, ivcs, big], Miqote, GameSkeleton, Parent)!.Candidate.Label);

        // A bone bound twice, a float slot or a partition the skeleton lacks: made for another one.
        Assert.True(!SkeletonMatcher.Fits([Binding(0, 1, 1)], GameSkeleton));
        Assert.True(!SkeletonMatcher.Fits([new AnimationChannels("skeleton", [0], [0], [])], GameSkeleton));
        Assert.True(!SkeletonMatcher.Fits([new AnimationChannels("skeleton", [0], [], [0])], GameSkeleton));
    }

    private static void SourcePrefersRace()
    {
        AnimationChannels[] channels = [Binding(0, 1, 6)];
        var own = Candidate(Ivcs, SkeletonOrigin.Installed, Miqote, "own", 0.2f);
        var parent = Candidate(Ivcs, SkeletonOrigin.Installed, Midlander, "parent", 0.3f);
        var root = Candidate(Ivcs, SkeletonOrigin.Installed, 101, "root", 0.35f);
        var unrelated = Candidate(Ivcs, SkeletonOrigin.Installed, 1101, "unrelated", 0.4f);
        var child = Candidate(Ivcs, SkeletonOrigin.Installed, Miqote, "child", 0.45f);
        Assert.Equal("own", SkeletonMatcher.ChooseSource(channels, [unrelated, root, parent, own], Miqote, null, Parent)!.Candidate.Label);
        Assert.Equal("parent", SkeletonMatcher.ChooseSource(channels, [unrelated, root, parent], Miqote, null, Parent)!.Candidate.Label);
        Assert.Equal("root", SkeletonMatcher.ChooseSource(channels, [unrelated, root], Miqote, null, Parent)!.Candidate.Label);
        // Made for a parent race, a child's skeleton still beats an unrelated one.
        Assert.Equal("child", SkeletonMatcher.ChooseSource(channels, [unrelated, child], Midlander, null, Parent)!.Candidate.Label);
        // The mod's own skeleton comes first among the race's.
        var mods = Candidate(Ivcs, SkeletonOrigin.ThisMod, Miqote, "this mod", 0.5f);
        Assert.Equal("this mod", SkeletonMatcher.ChooseSource(channels, [own, mods], Miqote, null, Parent)!.Candidate.Label);
    }

    private static void SourcePrefersGameLayout()
    {
        // Only game bones animated: the game's skeleton fits and keeps all of them in place.
        AnimationChannels[] channels = [Binding(0, 1, 2, 3)];
        var game = new SkeletonCandidate(GameSkeleton, SkeletonOrigin.Game, [Miqote], "game");
        var ivcs = Candidate(Ivcs, SkeletonOrigin.Installed, Miqote, "IVCS");
        var big = Candidate(Big, SkeletonOrigin.Installed, Miqote, "Big");
        Assert.Equal("game", SkeletonMatcher.ChooseSource(channels, [big, ivcs, game], Miqote, GameSkeleton, Parent)!.Candidate.Label);
        // Without it the one with the fewest bones the animation leaves alone fits it best, even
        // when the player uses the larger one...
        Assert.Equal("IVCS", SkeletonMatcher.ChooseSource(channels, [big, ivcs], Miqote, GameSkeleton, Parent)!.Candidate.Label);
        Assert.Equal("IVCS", SkeletonMatcher.ChooseSource(channels, [big with { Live = true }, ivcs], Miqote, GameSkeleton, Parent)!.Candidate.Label);
        // ...which only decides between skeletons that fit equally tightly, such as a mod's options.
        var option = ivcs with { Label = "IVCS option", Skeleton = Skeleton("skeleton", Ivcs, 0.2f) };
        var live = SkeletonMatcher.ChooseSource(channels, [ivcs, option with { Live = true }], Miqote, GameSkeleton, Parent)!;
        Assert.Equal("IVCS option", live.Candidate.Label);
        Assert.Equal(0, live.Rivals.Length);
        // The skeleton the animation names wins over the game's layout.
        var named = new AnimationChannels("c0801", [0, 1, 2, 3], [], []);
        var bigNamed = big with { Skeleton = big.Skeleton with { Name = "c0801" } };
        Assert.Equal("Big", SkeletonMatcher.ChooseSource([named], [game, bigNamed], Miqote, GameSkeleton, Parent)!.Candidate.Label);
    }

    private static void SourceRivals()
    {
        AnimationChannels[] channels = [Binding(0, 1, 2)];
        var first = Candidate(Ivcs, SkeletonOrigin.Installed, Miqote, "a");
        // Different only where the animation has no track and no tracked bone hangs below.
        var alike = first with
        {
            Label = "b",
            Skeleton = first.Skeleton with { Bones = first.Skeleton.Bones.SetItem(5, first.Skeleton.Bones[5] with { Reference = BoneTransform.Identity }) },
        };
        var choice = SkeletonMatcher.ChooseSource(channels, [first, alike], Miqote, null, Parent)!;
        Assert.Equal("a", choice.Candidate.Label);
        Assert.Equal(0, choice.Rivals.Length);

        // Resting differently under a tracked bone changes the result: a rival.
        var different = first with
        {
            Label = "c",
            Skeleton = first.Skeleton with { Bones = first.Skeleton.Bones.SetItem(1, first.Skeleton.Bones[1] with { Reference = BoneTransform.Identity }) },
        };
        choice = SkeletonMatcher.ChooseSource(channels, [different, first], Miqote, null, Parent)!;
        Assert.Equal("a", choice.Candidate.Label);
        Assert.Equal(["c"], choice.Rivals.Select(r => r.Label));
    }

    private static void SourceLeadingBones()
    {
        // Compressed for five bones; the only skeleton installed since got more appended.
        var channels = new[] { new AnimationChannels("skeleton", [0, 1, 2, 3, 4], [], [], ReferenceBones: 5, ReferenceFloats: 0) };
        var longer = Candidate([.. Game, "iv_a", "iv_b"], SkeletonOrigin.Installed, Miqote, "IVCS");
        var choice = SkeletonMatcher.ChooseSource(channels, [longer], Miqote, null, Parent)!;
        Assert.Equal(5, choice.Candidate.LeadingBones);
        Assert.Equal(Game, choice.Candidate.Skeleton.Bones.Select(b => b.Name));
        // A whole skeleton of the right size needs no cut.
        var game = new SkeletonCandidate(GameSkeleton, SkeletonOrigin.Game, [Miqote], "game");
        Assert.Equal(0, SkeletonMatcher.ChooseSource(channels, [longer, game], Miqote, null, Parent)!.Candidate.LeadingBones);
    }

    private static void SourceQuantized()
    {
        var channels = new[] { new AnimationChannels("skeleton", [0, 1, 6], [], [], ReferenceBones: 7, ReferenceFloats: 0, Quantized: true) };
        var parent = Candidate(Ivcs, SkeletonOrigin.Installed, Midlander, "parent");
        var own = Candidate(Ivcs, SkeletonOrigin.Installed, Miqote, "own");
        Assert.Equal(null, SkeletonMatcher.ChooseSource(channels, [parent], Miqote, null, Parent));
        Assert.Equal("own", SkeletonMatcher.ChooseSource(channels, [parent, own], Miqote, null, Parent)!.Candidate.Label);
        // Never cut to size: the data was encoded against the whole skeleton.
        var shorter = new[] { channels[0] with { Bones = [0, 1], ReferenceBones = 5 } };
        Assert.Equal(null, SkeletonMatcher.ChooseSource(shorter, [own], Miqote, null, Parent));
    }

    private static void StandardLayouts()
    {
        var game = GameSkeleton;
        Assert.True(SkeletonMatcher.IsStandardLayout(game, game, SkeletonStandard.Vanilla));
        Assert.True(SkeletonMatcher.IsStandardLayout(Skeleton("s", Ivcs), game, SkeletonStandard.Ivcs), "the late game bone may follow later");
        Assert.True(!SkeletonMatcher.IsStandardLayout(Skeleton("s", Ivcs), game, SkeletonStandard.Vanilla));
        Assert.True(SkeletonMatcher.IsStandardLayout(Skeleton("s", Yas), game, SkeletonStandard.IvcsYas), "the late game bone may be missing");
        Assert.True(!SkeletonMatcher.IsStandardLayout(Skeleton("s", Yas), game, SkeletonStandard.Ivcs), "YAS bones are not IVCS");
        Assert.True(!SkeletonMatcher.IsStandardLayout(Skeleton("s", Ivcs), game, SkeletonStandard.IvcsYas), "IVCS + YAS needs YAS bones");
        Assert.True(!SkeletonMatcher.IsStandardLayout(Skeleton("s", Big), game, SkeletonStandard.IvcsYas), "other groups are no standard");
        string[] shuffled = ["n_root", "j_sebo_a", "j_kosi", "j_kubi", "iv_a", "n_hara_noanim_trans"];
        Assert.True(!SkeletonMatcher.IsStandardLayout(Skeleton("s", shuffled), game, SkeletonStandard.Ivcs), "game bones moved to other indices");

        // Seven races have no tail in the game's skeleton; IVCS and YAS add its bones to them.
        string[] tailedIvcs = [.. Game[..4], "iv_a", "iv_b", "n_sippo_a", "n_sippo_b", "n_hara_noanim_trans"];
        string[] tailedYas = [.. Game[..4], "iv_a", "n_sippo_a", "n_sippo_b", "ya_a"];
        Assert.True(SkeletonMatcher.IsStandardLayout(Skeleton("s", tailedIvcs), game, SkeletonStandard.Ivcs), "IVCS adds the tail bones the game lacks");
        Assert.True(SkeletonMatcher.IsStandardLayout(Skeleton("s", tailedYas), game, SkeletonStandard.IvcsYas), "so does IVCS + YAS");
        Assert.True(!SkeletonMatcher.IsStandardLayout(Skeleton("s", tailedIvcs), game, SkeletonStandard.Vanilla), "the game's own layout has no tail here");
        string[] tailOnly = [.. Game[..4], "n_sippo_a", "n_hara_noanim_trans"];
        Assert.True(!SkeletonMatcher.IsStandardLayout(Skeleton("s", tailOnly), game, SkeletonStandard.Ivcs), "a tail alone is no IVCS");
    }

    private static void TargetSmallestStandard()
    {
        var game = new SkeletonCandidate(GameSkeleton, SkeletonOrigin.Game, [AuRa], "game");
        SkeletonCandidate[] installed =
        [
            Candidate(Big, SkeletonOrigin.Installed, AuRa, "Big"),
            Candidate(Yas, SkeletonOrigin.Installed, AuRa, "YAS"),
            Candidate(Ivcs, SkeletonOrigin.Installed, AuRa, "IVCS"),
        ];
        var standards = SkeletonMatcher.Standards(game, installed);
        Assert.Equal(new[] { SkeletonStandard.Vanilla, SkeletonStandard.Ivcs, SkeletonStandard.IvcsYas }, standards.Select(s => s.Standard));
        Assert.Equal(new[] { "game", "IVCS", "YAS" }, standards.Select(s => s.Candidate.Label));

        TargetChoice Choose(IReadOnlyList<(SkeletonStandard, SkeletonCandidate)> from, params string[] moving)
            => SkeletonMatcher.ChooseTarget(from, moving.ToHashSet(StringComparer.Ordinal));
        Assert.Equal(SkeletonStandard.Vanilla, Choose(standards, "j_kosi").Standard);
        Assert.Equal(SkeletonStandard.Ivcs, Choose(standards, "j_kosi", "iv_a").Standard);
        Assert.Equal(SkeletonStandard.IvcsYas, Choose(standards, "ya_a").Standard);
        // Bones no standard has are left out; the smallest one losing no more is chosen.
        var partly = Choose(standards, "iv_a", "nf_a", "nf_b");
        Assert.Equal(SkeletonStandard.Ivcs, partly.Standard);
        Assert.Equal(["nf_a", "nf_b"], partly.Missing);
        // Without IVCS installed, YAS keeps its bones; with neither, only the game's is left.
        Assert.Equal(SkeletonStandard.IvcsYas, Choose(SkeletonMatcher.Standards(game, installed[..2]), "iv_a").Standard);
        var bare = Choose(SkeletonMatcher.Standards(game, []), "iv_a", "j_kosi");
        Assert.Equal(SkeletonStandard.Vanilla, bare.Standard);
        Assert.Equal(["iv_a"], bare.Missing);
    }

    /// <summary>
    /// The reported case: a retarget rebuilt for the game's skeleton tracked its last bone,
    /// n_hara_noanim_trans, whose index holds a fingertip on IVCS and YAS, so the finger
    /// stretched to the floor. Such bones are found by name and in the installed standard
    /// layouts; a layout that is no standard says nothing about where the game's bones are.
    /// </summary>
    private static void UnportableBones()
    {
        string[] Unportable(SkeletonDescription game, params SkeletonDescription[] layouts)
            => [.. SkeletonMatcher.UnportableBones(game, layouts).Order(StringComparer.Ordinal)];
        Assert.Equal(new[] { "n_hara_noanim_trans" }, Unportable(GameSkeleton));
        Assert.Equal(new[] { "n_hara_noanim_trans" }, Unportable(GameSkeleton, Skeleton("s", Ivcs), Skeleton("s", Yas)));

        // A later game bone the mods also put elsewhere, which its name does not give away.
        string[] newer = [.. Game, "j_new"];
        string[] newerIvcs = [.. Game[..4], "iv_a", "iv_b", "n_hara_noanim_trans", "j_new"];
        Assert.Equal(new[] { "j_new", "n_hara_noanim_trans" }, Unportable(Skeleton("s", newer), Skeleton("s", newerIvcs)));
        Assert.Equal(new[] { "n_hara_noanim_trans" }, Unportable(Skeleton("s", newer), Skeleton("s", newer), Skeleton("s", Big)));
    }

    /// <summary>
    /// A skeleton mod's base skeletons, in meta.json or the older default_mod.json and group files:
    /// options are named only where the mod offers several skeletons for a race, and files that
    /// are missing, outside the mod or not base skeletons are left out.
    /// </summary>
    private static void InstalledDefinitions()
    {
        var folder = Path.Combine(Path.GetTempPath(), "umc-skeletons-" + Guid.NewGuid().ToString("N"));
        try
        {
            Directory.CreateDirectory(Path.Combine(folder, "posing"));
            Directory.CreateDirectory(Path.Combine(folder, "anim"));
            foreach (var file in new[] { "base.sklb", "posing/c0801.sklb", "anim/c0801.sklb", "posing/c1401.sklb", "face.sklb" })
                File.WriteAllBytes(Path.Combine(folder, file), [1]);
            File.WriteAllBytes(Path.Combine(Path.GetTempPath(), "umc-outside.sklb"), [1]);
            string Skeleton(ushort race) => PapPath.BaseSkeletonPath(race);
            File.WriteAllText(Path.Combine(folder, "meta.json"), $$$"""
                {"FileVersion":4,"Name":"Skeletons","Description":"skeleton mod",
                 "DefaultData":{"Files":{"{{{Skeleton(101)}}}":"base.sklb","chara/human/c0801/skeleton/face/f0001/skl_c0801f0001.sklb":"face.sklb",
                   "{{{Skeleton(201)}}}":"..\\umc-outside.sklb","{{{Skeleton(301)}}}":"missing.sklb"}},
                 "Groups":[{"Name":"Use","Type":"Single","Options":[
                   {"Name":"Posing","Files":{"{{{Skeleton(801)}}}":"posing\\c0801.sklb","{{{Skeleton(1401)}}}":"posing\\c1401.sklb"}},
                   {"Name":"Animation","Files":{"{{{Skeleton(801)}}}":"anim\\c0801.sklb","{{{Skeleton(1401)}}}":"posing\\c1401.sklb"}}]}]}
                """);
            // The layout before meta.json held every container.
            File.WriteAllText(Path.Combine(folder, "default_mod.json"), $$$"""{"Files":{"{{{Skeleton(1101)}}}":"base.sklb"}}""");
            File.WriteAllText(Path.Combine(folder, "group_001_use.json"), """{"Name":"Old","Options":[{"Name":"Only","Files":{}}]}""");
            File.WriteAllText(Path.Combine(folder, "notes.json"), $$$"""{"Files":{"{{{Skeleton(1201)}}}":"base.sklb"}}""");

            var (definitions, stamp) = InstalledSkeletons.Definitions(folder);
            Assert.Equal(new[] { "default_mod.json", "group_001_use.json", "meta.json" }, definitions.Select(Path.GetFileName));
            var found = InstalledSkeletons.Read(folder, definitions, "Mod");
            string Local(InstalledSkeleton s) => Path.GetRelativePath(folder, s.Path).Replace('\\', '/');
            Assert.Equal(new[] { "101 base.sklb -", "1101 base.sklb -", "1401 posing/c1401.sklb -", "1401 posing/c1401.sklb -",
                    "801 anim/c0801.sklb Animation", "801 posing/c0801.sklb Posing" },
                found.Select(s => $"{s.Race} {Local(s)} {s.Option ?? "-"}").Order(StringComparer.Ordinal).ToArray());
            Assert.True(found.All(s => s.Mod == "Mod"));

            // Unchanged definitions keep their stamp; an edit changes it.
            Assert.Equal(stamp, InstalledSkeletons.Definitions(folder).Stamp);
            File.AppendAllText(Path.Combine(folder, "default_mod.json"), " ");
            Assert.True(InstalledSkeletons.Definitions(folder).Stamp != stamp, "an edited definition is read again");

            // A definition that cannot be parsed is reported and the rest are still read.
            File.WriteAllText(Path.Combine(folder, "group_002_broken.json"), "{ \"Files\": { \"skeleton\": ");
            var rejected = new List<string>();
            var again = InstalledSkeletons.Read(folder, InstalledSkeletons.Definitions(folder).Files, "Mod", (file, _) => rejected.Add(Path.GetFileName(file)));
            Assert.Equal(["group_002_broken.json"], rejected);
            Assert.Equal(found.Count, again.Count);
        }
        finally
        {
            Directory.Delete(folder, true);
            File.Delete(Path.Combine(Path.GetTempPath(), "umc-outside.sklb"));
        }
    }

    private static void MotionThreshold()
    {
        var rest = BoneTransform.Identity with { Position = new Vector3(0, 0.1f, 0) };
        Assert.True(!SkeletonMatcher.Moves(rest with { Position = new Vector3(0, 0.1f + 1e-6f, 0) }, rest), "float noise");
        Assert.True(SkeletonMatcher.Moves(rest with { Position = new Vector3(0, 0.101f, 0) }, rest), "a millimetre");
        Assert.True(SkeletonMatcher.Moves(rest with { Rotation = Quaternion.CreateFromAxisAngle(Vector3.UnitX, MathF.PI / 180) }, rest), "a degree");
        Assert.True(SkeletonMatcher.Moves(rest with { Scale = new Vector3(1.01f) }, rest), "a percent of scale");
    }
}
