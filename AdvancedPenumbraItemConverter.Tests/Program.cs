using System.Collections.Immutable;
using System.Buffers.Binary;
using System.Numerics;
using System.Text;
using AdvancedPenumbraItemConverter.Core;

if (args is ["--inspect-mdl", ..])
{
    foreach (var path in args.Skip(1))
    {
        var input = File.ReadAllBytes(path);
        var model = MdlFile.Read(input);
        Console.WriteLine(path);
        Console.WriteLine(System.Text.Json.JsonSerializer.Serialize(new
        {
            model.Header, model.ModelHeader, model.Materials, model.Attributes, model.Bones,
            model.Meshes, model.Submeshes, model.BoneTables, model.Shapes, model.Lods,
            model.ModelBoundingBox,
            Lossless = input.SequenceEqual(model.Write()),
            RebuiltDataUnchanged = input.AsSpan(model.DataOffset).SequenceEqual(
                model.WriteRebuilt().AsSpan(MdlFile.Read(model.WriteRebuilt()).DataOffset)),
        }, new System.Text.Json.JsonSerializerOptions { WriteIndented = true, IncludeFields = true }));
    }
    return 0;
}

var tests = new (string Name, Action Run)[]
{
    ("EQDP packed entry relocation", TestEqdp),
    ("Binary path extraction and rewrite", TestBinaryPaths),
    ("Binary rewrite rejects length changes", TestBinaryLength),
    ("Path containment", TestContainment),
    ("Case-folded collision rejection", TestCaseCollision),
    ("Source fingerprint detects changes", TestSourceFingerprint),
    ("Weighted racial deformation", TestDeformation),
    ("Missing bone fallback behavior", TestBoneFallback),
    ("Malformed PBD is rejected", TestMalformedPbd),
    ("PBD hierarchy validation", TestPbdHierarchy),
    ("PBD reserved race entries", TestPbdReservedRace),
    ("PBD cross-branch sequential route", TestPbdRoute),
    ("Sequential multi-influence skinning", TestSequentialSkinning),
    ("Hierarchy-aware bone materialization", TestBoneMaterialization),
    ("Cyclic hierarchy falls back to identity", TestCyclicBoneHierarchy),
    ("SKLB 0x3132 and 0x3133 envelopes", TestSklbEnvelope),
    ("EST extra-skeleton lookup", TestExtraSkeletonTable),
    ("MDL v6 lossless round trip", TestMdlRoundTrip),
    ("MDL v6 changed-string rebuild", TestMdlRebuild),
    ("MTRL length-changing string rebuild", TestMtrlRebuild),
    ("MDL v6 racial deformation", TestMdlDeformation),
    ("MDL sequential multi-influence deformation", TestMdlSequentialDeformation),
    ("MDL v6 NByte4 blend weights", TestMdlNByteBlendWeights),
    ("MDL shape and face vertices", TestMdlShapeVertices),
    ("Viera ear bone inherits head deformation", TestMdlVieraEarBone),
    ("MDL v5 rejection", TestMdlV5),
    ("MDL with an over-reported stream count deforms", TestMdlStreamCountByte),
    ("MDL neck-morph count without data (Penumbra export)", TestMdlNeckMorphCountWithoutData),
    ("MDL unresolved bone identity warning", TestMdlUnresolvedBone),
    ("MDL mesh groups are described", TestMeshGroupsDescribe),
    ("MDL mesh removal renumbers metadata and keeps data", TestMeshRemoval),
    ("MDL mesh removal drops the removed mesh's shape", TestMeshRemovalDropsShape),
    ("MDL mesh removal guards", TestMeshRemovalGuards),
    ("Customization descriptors restrict races", TestCustomizationDescriptors),
    ("Customization targets exclude Lalafell and cross-gender faces", TestCustomizationTargets),
    ("Canonical customization path rewriting", TestCustomizationPaths),
    ("TexTools-style equipment/accessory path retargeting", TestGearPaths),
    ("Hair material sharing and skin references", TestCharacterMaterialPaths),
    ("Plan fingerprint is deterministic", TestFingerprint),
}.Concat(GearConversionTests.All).Concat(CustomizationRuleTests.All).ToArray();

var failures = 0;
foreach (var test in tests)
{
    try { test.Run(); Console.WriteLine($"PASS {test.Name}"); }
    catch (Exception ex) { failures++; Console.Error.WriteLine($"FAIL {test.Name}: {ex.Message}"); }
}
Console.WriteLine($"{tests.Length - failures}/{tests.Length} tests passed.");
return failures == 0 ? 0 : 1;

static void TestEqdp()
{
    const ushort entry = 0b10_01_11_00_10;
    var moved = MetadataTransforms.RepositionEqdp(entry, 0, 8);
    Equal(2, (moved >> 8) & 3);
    Equal(0, moved & 3);
    Equal((entry >> 2) & 0x3f, (moved >> 2) & 0x3f);
    Equal((ushort)0, MetadataTransforms.RepositionEqdp(0, 2, 6));
}

static void TestBinaryPaths()
{
    var source = "chara/equipment/e0123/model/c0101e0123_top.mdl";
    var target = "chara/accessory/a0456/model/c0101a0456_ear.mdl";
    var bytes = Encoding.ASCII.GetBytes($"noise\0{source}\0tail");
    var paths = BinaryPathRewriter.ExtractPaths(bytes);
    Equal(source, paths.Single());
    if (source.Length != target.Length) throw new Exception("Test paths must be equal length.");
    var rewritten = BinaryPathRewriter.Rewrite(bytes, new Dictionary<string, string> { [source] = target });
    True(Encoding.ASCII.GetString(rewritten).Contains(target, StringComparison.Ordinal));
}

static void TestBinaryLength()
{
    var source = "chara/x/a0001_ear.tex";
    var bytes = Encoding.ASCII.GetBytes(source + "\0");
    Throws<InvalidDataException>(() => BinaryPathRewriter.Rewrite(bytes,
        new Dictionary<string, string> { [source] = source + "x" }));
}

static void TestMtrlRebuild()
{
    const string source = "chara/equipment/e0123/texture/c0101e0123_dwn_d.tex";
    const string target = "chara/equipment/e0456/texture/c0101e0456_dwn_top_d.tex";
    var bytes = BuildMtrl(source, "uv1", "colorset", "character.shpk", [0xaa, 0xbb, 0xcc]);
    var rewritten = StructuredPathRewriter.Rewrite("test.mtrl", bytes,
        new Dictionary<string, string> { [source] = target });

    Equal((ushort)rewritten.Length, BinaryPrimitives.ReadUInt16LittleEndian(rewritten.AsSpan(4)));
    Equal(target, BinaryPathRewriter.ExtractPaths(rewritten).Single());
    True(Encoding.UTF8.GetString(rewritten).Contains("uv1\0colorset\0character.shpk\0",
        StringComparison.Ordinal));
    True(rewritten.AsSpan(rewritten.Length - 3).SequenceEqual(new byte[] { 0xaa, 0xbb, 0xcc }));
}

static void TestContainment()
{
    var root = Path.Combine(Path.GetTempPath(), "apic-core-path-test");
    Directory.CreateDirectory(root);
    Equal(Path.GetFullPath(Path.Combine(root, "a", "b")), PathSafety.ResolveRelative(root, "a/b"));
    Throws<InvalidDataException>(() => PathSafety.ResolveRelative(root, "../escape"));
    Throws<InvalidDataException>(() => PathSafety.ResolveRelative(root, Path.GetFullPath(Path.Combine(root, "absolute"))));
}

static void TestCaseCollision()
{
    var root = Path.Combine(Path.GetTempPath(), "apic-case-test");
    Throws<IOException>(() => PathSafety.ValidateNoCaseCollisions(
        [Path.Combine(root, "A.mdl"), Path.Combine(root, "a.mdl")]));
}

static void TestSourceFingerprint()
{
    var root = Path.Combine(Path.GetTempPath(), "apic-fingerprint-" + Guid.NewGuid().ToString("N"));
    Directory.CreateDirectory(root);
    try
    {
        var path = Path.Combine(root, "asset.bin");
        File.WriteAllBytes(path, [1, 2, 3]);
        var first = ModFingerprint.Compute(root);
        File.WriteAllBytes(path, [1, 2, 4]);
        var second = ModFingerprint.Compute(root);
        True(!string.Equals(first, second, StringComparison.Ordinal));
    }
    finally
    {
        Directory.Delete(root, true);
    }
}

static void TestDeformation()
{
    var transforms = new Dictionary<string, Matrix4x4>
    {
        ["a"] = Matrix4x4.CreateTranslation(2, 0, 0),
        ["b"] = Matrix4x4.CreateTranslation(0, 2, 0),
    };
    var vertex = new DeformableVertex(new Vector3(1, 1, 1), Vector3.UnitZ, Vector3.UnitX,
        Vector3.UnitY, Vector3.UnitX,
        [new VertexInfluence("a", .5f), new VertexInfluence("b", .5f)]);
    var result = RacialDeformation.Transform(vertex, transforms);
    Near(new Vector3(2, 2, 1), result.Position);
    Near(Vector3.UnitZ, result.Normal);
}

static void TestBoneFallback()
{
    var transforms = new Dictionary<string, Matrix4x4>
    {
        ["parent"] = Matrix4x4.CreateTranslation(3, 0, 0),
        ["j_kao"] = Matrix4x4.CreateTranslation(0, 4, 0),
    };
    var parents = new Dictionary<string, string> { ["child"] = "parent" };
    var vertex = new DeformableVertex(Vector3.Zero, Vector3.UnitZ, Vector3.UnitX,
        Vector3.UnitY, Vector3.UnitX, [new VertexInfluence("child", 1)]);
    Near(new Vector3(3, 0, 0), RacialDeformation.Transform(vertex, transforms, parents).Position);
    vertex = vertex with { Influences = [new VertexInfluence("j_ex_h_test", 1)] };
    Near(new Vector3(0, 4, 0), RacialDeformation.Transform(vertex, transforms).Position);
}

static void TestMalformedPbd()
{
    Throws<InvalidDataException>(() => _ = new HumanPbd([1, 0, 0]));
    Throws<InvalidDataException>(() => _ = new HumanPbd([0, 0, 0, 0]));
}

static void TestPbdHierarchy()
{
    True(new HumanPbd(CreatePbd(-1)).Contains(101));
    Throws<InvalidDataException>(() => _ = new HumanPbd(CreatePbd(1)));
    Throws<InvalidDataException>(() => _ = new HumanPbd(CreatePbd(0)));
}

static void TestPbdReservedRace()
{
    var bytes = new byte[44];
    BitConverter.TryWriteBytes(bytes.AsSpan(0, 4), 2);
    BitConverter.TryWriteBytes(bytes.AsSpan(4, 2), (ushort)101);
    BitConverter.TryWriteBytes(bytes.AsSpan(6, 2), (short)0);
    BitConverter.TryWriteBytes(bytes.AsSpan(12, 4), 1f);
    BitConverter.TryWriteBytes(bytes.AsSpan(16, 2), ushort.MaxValue);
    BitConverter.TryWriteBytes(bytes.AsSpan(18, 2), (short)-1);
    BitConverter.TryWriteBytes(bytes.AsSpan(24, 4), 1f);
    // Tree 0 -> active deformer 0; tree 1 -> reserved deformer 1.
    BitConverter.TryWriteBytes(bytes.AsSpan(28, 2), (short)-1);
    BitConverter.TryWriteBytes(bytes.AsSpan(30, 2), (short)1);
    BitConverter.TryWriteBytes(bytes.AsSpan(32, 2), (short)-1);
    BitConverter.TryWriteBytes(bytes.AsSpan(34, 2), (short)0);
    BitConverter.TryWriteBytes(bytes.AsSpan(36, 2), (short)0);
    BitConverter.TryWriteBytes(bytes.AsSpan(38, 2), (short)-1);
    BitConverter.TryWriteBytes(bytes.AsSpan(40, 2), (short)-1);
    BitConverter.TryWriteBytes(bytes.AsSpan(42, 2), (short)1);
    var pbd = new HumanPbd(bytes);
    True(pbd.Contains(101));
    True(!pbd.Contains(ushort.MaxValue));
}

static void TestPbdRoute()
{
    var pbd = new HumanPbd(CreatePbdChain());
    var plan = pbd.BuildPlan(1801, 801);
    Equal(2, plan.Steps.Length);
    Equal((ushort)1801, plan.Steps[0].GenderRace);
    True(plan.Steps[0].Inverse);
    Equal((ushort)801, plan.Steps[1].GenderRace);
    True(!plan.Steps[1].Inverse);
    var converted = new Vector3(4, 0, 0);
    foreach (var step in plan.Steps)
        converted = Vector3.Transform(converted, step.BoneMatrices["j_root"]);
    // Source -> parent uses inverse scale, then parent -> target applies translation.
    Near(new Vector3(12, 0, 0), converted);

    var reverse = pbd.BuildPlan(801, 1801);
    Equal(2, reverse.Steps.Length);
    Equal((ushort)801, reverse.Steps[0].GenderRace);
    True(reverse.Steps[0].Inverse);
    Equal((ushort)1801, reverse.Steps[1].GenderRace);
    True(!reverse.Steps[1].Inverse);
}

static void TestSequentialSkinning()
{
    var vertex = new DeformableVertex(Vector3.UnitX, Vector3.UnitZ, Vector3.UnitX,
        Vector3.UnitY, Vector3.UnitX,
        [new VertexInfluence("a", .5f), new VertexInfluence("b", .5f)]);
    var first = new Dictionary<string, Matrix4x4>
    {
        ["a"] = Matrix4x4.CreateTranslation(2, 0, 0),
        ["b"] = Matrix4x4.Identity,
    };
    var second = new Dictionary<string, Matrix4x4>
    {
        ["a"] = Matrix4x4.Identity,
        ["b"] = Matrix4x4.CreateRotationZ(MathF.PI / 2),
    };
    var sequential = RacialDeformation.Transform(RacialDeformation.Transform(vertex, first), second);
    var composed = new Dictionary<string, Matrix4x4>
    {
        ["a"] = first["a"] * second["a"],
        ["b"] = first["b"] * second["b"],
    };
    var onePass = RacialDeformation.Transform(vertex, composed);
    Near(new Vector3(1, 1, 0), sequential.Position);
    True(Vector3.Distance(sequential.Position, onePass.Position) > .1f);
}

static void TestBoneMaterialization()
{
    var route = OneStep(new Dictionary<string, Matrix4x4>
        { ["j_kao"] = Matrix4x4.CreateTranslation(0, 4, 0) }, 1801);
    var source = new BoneHierarchy(new Dictionary<string, string?>
    {
        ["j_zera_b_l"] = "j_zera_a_l",
        ["j_zera_a_l"] = "j_kao",
        ["j_kao"] = "j_kubi",
    });
    var materialized = route.Materialize(["j_zera_b_l"], sourceSkeleton: source);
    Near(new Vector3(0, 4, 0), Vector3.Transform(Vector3.Zero,
        materialized.Steps.Single().BoneMatrices["j_zera_b_l"]));
    var resolution = materialized.BoneResolutions.Single();
    Equal(BoneResolutionStrategy.SourceSkeletonAncestor, resolution.Strategy);
    Equal("j_kao", resolution.ResolvedBone!);

    var stepHierarchy = new Dictionary<ushort, BoneHierarchy>
    {
        [1801] = new BoneHierarchy(new Dictionary<string, string?>
            { ["ordinary_child"] = "j_kao" }),
    };
    var fromStep = route.Materialize(["ordinary_child"], stepHierarchy, source);
    Equal(BoneResolutionStrategy.StepSkeletonAncestor, fromStep.BoneResolutions.Single().Strategy);
    Equal("j_kao", fromStep.BoneResolutions.Single().ResolvedBone!);

    var heuristic = route.Materialize(["j_ex_h_custom"]);
    Equal(BoneResolutionStrategy.Heuristic, heuristic.BoneResolutions.Single().Strategy);
    Equal("j_kao", heuristic.BoneResolutions.Single().ResolvedBone!);
}

static void TestCyclicBoneHierarchy()
{
    var route = OneStep(new Dictionary<string, Matrix4x4>(), 801);
    var hierarchy = new BoneHierarchy(new Dictionary<string, string?>
        { ["a"] = "b", ["b"] = "a" });
    var materialized = route.Materialize(["a"], sourceSkeleton: hierarchy);
    Equal(BoneResolutionStrategy.Identity, materialized.BoneResolutions.Single().Strategy);
    Equal(Matrix4x4.Identity, materialized.Steps.Single().BoneMatrices["a"]);
}

static void TestSklbEnvelope()
{
    var old = new byte[15];
    BitConverter.TryWriteBytes(old.AsSpan(0, 4), 0x736B6C62u);
    BitConverter.TryWriteBytes(old.AsSpan(6, 2), (ushort)0x3132);
    BitConverter.TryWriteBytes(old.AsSpan(10, 2), (ushort)12);
    old[12] = 1; old[13] = 2; old[14] = 3;
    Equal(12, SklbEnvelope.GetHavokOffset(old));
    True(SklbEnvelope.ExtractHavok(old).SequenceEqual(new byte[] { 1, 2, 3 }));

    var current = new byte[18];
    BitConverter.TryWriteBytes(current.AsSpan(0, 4), 0x736B6C62u);
    BitConverter.TryWriteBytes(current.AsSpan(6, 2), (ushort)0x3133);
    BitConverter.TryWriteBytes(current.AsSpan(12, 4), 16);
    current[16] = 4; current[17] = 5;
    Equal(16, SklbEnvelope.GetHavokOffset(current));
    True(SklbEnvelope.ExtractHavok(current).SequenceEqual(new byte[] { 4, 5 }));
    Throws<InvalidDataException>(() => SklbEnvelope.GetHavokOffset([0, 1, 2, 3]));
}

static void TestExtraSkeletonTable()
{
    var bytes = new byte[16];
    BitConverter.TryWriteBytes(bytes.AsSpan(0, 4), 2u);
    BitConverter.TryWriteBytes(bytes.AsSpan(4, 2), (ushort)1);
    BitConverter.TryWriteBytes(bytes.AsSpan(6, 2), (ushort)1801);
    BitConverter.TryWriteBytes(bytes.AsSpan(8, 2), (ushort)7);
    BitConverter.TryWriteBytes(bytes.AsSpan(10, 2), (ushort)801);
    BitConverter.TryWriteBytes(bytes.AsSpan(12, 2), (ushort)42);
    BitConverter.TryWriteBytes(bytes.AsSpan(14, 2), (ushort)9);
    True(ExtraSkeletonTable.TryGet(bytes, 1801, 1, out var first));
    Equal((ushort)42, first);
    True(ExtraSkeletonTable.TryGet(bytes, 801, 7, out var second));
    Equal((ushort)9, second);
    True(!ExtraSkeletonTable.TryGet(bytes, 101, 1, out _));
}

static void TestMdlRoundTrip()
{
    var bytes = CreateMdl(faceData: true);
    var model = MdlFile.Read(bytes);
    Equal((uint)MdlFile.Version6, model.Header.Version);
    Equal(1, model.Meshes.Length);
    Equal("j_root", model.Bones.Single());
    True(bytes.AsSpan().SequenceEqual(model.Write()));
}

static void TestMdlRebuild()
{
    var model = MdlFile.Read(CreateMdl(faceData: true));
    model.ReplacePaths(new Dictionary<string, string>
        { ["mt_test.mtrl"] = "mt_a_much_longer_test_name.mtrl" });
    var rebuilt = model.Write();
    var parsed = MdlFile.Read(rebuilt);
    Equal("mt_a_much_longer_test_name.mtrl", parsed.Materials.Single());
    Equal(1, parsed.FaceData.Length);
    Equal((ushort)0, parsed.BoneTables.Single().BoneIndices.Single());
}

static void TestMdlDeformation()
{
    var bytes = CreateMdl(faceData: true);
    var model = MdlFile.Read(bytes);
    var report = MdlRaceConverter.Convert(model, OneStep(
        new Dictionary<string, Matrix4x4> { ["j_root"] = Matrix4x4.CreateTranslation(2, 3, 4) }));
    Equal(1, report.VertexCount);
    var output = model.Write();
    var parsed = MdlFile.Read(output);
    var position = new Vector3(BitConverter.ToSingle(output, parsed.DataOffset),
        BitConverter.ToSingle(output, parsed.DataOffset + 4), BitConverter.ToSingle(output, parsed.DataOffset + 8));
    Near(new Vector3(3, 5, 7), position);
    Near(new Vector3(3, 5, 7), parsed.FaceData.Single().Position);
    Near(Vector3.UnitZ, new Vector3(BitConverter.ToSingle(output, parsed.DataOffset + 20),
        BitConverter.ToSingle(output, parsed.DataOffset + 24), BitConverter.ToSingle(output, parsed.DataOffset + 28)));
    Equal((byte)77, output[parsed.DataOffset + 35]);
    Equal((uint)1, parsed.FaceData.Single().Sign);
    Near(new Vector3(3, 5, 7), new Vector3(parsed.ModelBoundingBox.Minimum.X,
        parsed.ModelBoundingBox.Minimum.Y, parsed.ModelBoundingBox.Minimum.Z));
}

static void TestMdlSequentialDeformation()
{
    var model = MdlFile.Read(CreateMdl(twoBones: true));
    var first = new Dictionary<string, Matrix4x4>
    {
        ["a"] = Matrix4x4.CreateTranslation(2, 0, 0),
        ["b"] = Matrix4x4.Identity,
    }.ToImmutableDictionary(StringComparer.Ordinal);
    var second = new Dictionary<string, Matrix4x4>
    {
        ["a"] = Matrix4x4.Identity,
        ["b"] = Matrix4x4.CreateRotationZ(MathF.PI / 2),
    }.ToImmutableDictionary(StringComparer.Ordinal);
    var plan = new RacialDeformationPlan(
        [new RacialDeformationStep(1801, true, first), new RacialDeformationStep(801, false, second)], []);
    _ = MdlRaceConverter.Convert(model, plan);
    var output = model.Write();
    var parsed = MdlFile.Read(output);
    var actual = new Vector3(BitConverter.ToSingle(output, parsed.DataOffset),
        BitConverter.ToSingle(output, parsed.DataOffset + 4), BitConverter.ToSingle(output, parsed.DataOffset + 8));
    var expected = new DeformableVertex(new Vector3(1, 2, 3), Vector3.UnitZ, Vector3.UnitX,
        Vector3.UnitY, Vector3.UnitX,
        [new VertexInfluence("a", 128f / 255f), new VertexInfluence("b", 127f / 255f)]);
    expected = RacialDeformation.Transform(expected, first);
    expected = RacialDeformation.Transform(expected, second);
    Near(expected.Position, actual);
}

static void TestMdlNByteBlendWeights()
{
    var bytes = CreateMdl();
    // The first declaration's BlendWeights element is the second 8-byte
    // declaration entry; change only its vertex type. The payload remains four
    // bytes, matching the game's NByte4 character-model encoding.
    bytes[0x44 + 8 + 2] = (byte)MdlVertexType.NByte4;
    var model = MdlFile.Read(bytes);
    var report = MdlRaceConverter.Convert(model, OneStep(
        new Dictionary<string, Matrix4x4> { ["j_root"] = Matrix4x4.Identity }));
    Equal(1, report.VertexCount);
}

static void TestMdlShapeVertices()
{
    var model = MdlFile.Read(CreateMdl(faceData: true, shape: true));
    var report = MdlRaceConverter.Convert(model, OneStep(
        new Dictionary<string, Matrix4x4> { ["j_root"] = Matrix4x4.CreateScale(2) }));
    Equal(2, report.VertexCount);
    Equal(1, report.ShapeVertexCount);
    var parsed = MdlFile.Read(model.Write());
    Equal(2, parsed.FaceData.Length);
    Near(new Vector3(4, 4, 6), parsed.FaceData[1].Position);
}

static void TestMdlVieraEarBone()
{
    var model = MdlFile.Read(CreateMdl(boneName: "j_zera_b_l"));
    var source = new BoneHierarchy(new Dictionary<string, string?>
    {
        ["j_zera_b_l"] = "j_zera_a_l",
        ["j_zera_a_l"] = "j_kao",
    });
    var plan = OneStep(new Dictionary<string, Matrix4x4>
        { ["j_kao"] = Matrix4x4.CreateTranslation(0, 2, 0) }, 1801)
        .Materialize(model.Bones, sourceSkeleton: source);
    var report = MdlRaceConverter.Convert(model, plan);
    Equal(BoneResolutionStrategy.SourceSkeletonAncestor, report.BoneResolutions.Single().Strategy);
    var output = model.Write();
    var parsed = MdlFile.Read(output);
    Near(new Vector3(1, 4, 3), new Vector3(BitConverter.ToSingle(output, parsed.DataOffset),
        BitConverter.ToSingle(output, parsed.DataOffset + 4), BitConverter.ToSingle(output, parsed.DataOffset + 8)));
}

static byte[] CreatePbdChain()
{
    const int sourceData = 64;
    const int targetData = 128;
    var bytes = new byte[192];
    BitConverter.TryWriteBytes(bytes.AsSpan(0, 4), 3);
    WriteEntry(0, 201, 0, 0, 1f);
    WriteEntry(1, 1801, 1, sourceData, 99f);
    WriteEntry(2, 801, 2, targetData, .01f);
    var tree = 40;
    WriteTree(tree, -1, 1, -1, 0);
    WriteTree(tree + 8, 0, -1, 2, 1);
    WriteTree(tree + 16, 0, -1, -1, 2);
    WriteDeformer(sourceData, Matrix4x4.CreateScale(2));
    WriteDeformer(targetData, Matrix4x4.CreateTranslation(10, 0, 0));
    return bytes;

    void WriteEntry(int index, ushort race, short treeIndex, int dataOffset, float scale)
    {
        var offset = 4 + index * 12;
        BitConverter.TryWriteBytes(bytes.AsSpan(offset, 2), race);
        BitConverter.TryWriteBytes(bytes.AsSpan(offset + 2, 2), treeIndex);
        BitConverter.TryWriteBytes(bytes.AsSpan(offset + 4, 4), dataOffset);
        BitConverter.TryWriteBytes(bytes.AsSpan(offset + 8, 4), scale);
    }
    void WriteTree(int offset, short parent, short child, short sibling, short deformer)
    {
        BitConverter.TryWriteBytes(bytes.AsSpan(offset, 2), parent);
        BitConverter.TryWriteBytes(bytes.AsSpan(offset + 2, 2), child);
        BitConverter.TryWriteBytes(bytes.AsSpan(offset + 4, 2), sibling);
        BitConverter.TryWriteBytes(bytes.AsSpan(offset + 6, 2), deformer);
    }
    void WriteDeformer(int offset, Matrix4x4 matrix)
    {
        BitConverter.TryWriteBytes(bytes.AsSpan(offset, 4), 1);
        BitConverter.TryWriteBytes(bytes.AsSpan(offset + 4, 2), (ushort)56);
        // PBD stores three rows of a column-vector matrix. This is the transpose
        // of the row-vector representation used by System.Numerics.
        WriteFloat(offset + 8, matrix.M11); WriteFloat(offset + 12, matrix.M21); WriteFloat(offset + 16, matrix.M31); WriteFloat(offset + 20, matrix.M41);
        WriteFloat(offset + 24, matrix.M12); WriteFloat(offset + 28, matrix.M22); WriteFloat(offset + 32, matrix.M32); WriteFloat(offset + 36, matrix.M42);
        WriteFloat(offset + 40, matrix.M13); WriteFloat(offset + 44, matrix.M23); WriteFloat(offset + 48, matrix.M33); WriteFloat(offset + 52, matrix.M43);
        Encoding.UTF8.GetBytes("j_root\0").CopyTo(bytes, offset + 56);
    }
    void WriteFloat(int offset, float value) => BitConverter.TryWriteBytes(bytes.AsSpan(offset, 4), value);
}

static void TestMdlV5()
{
    var bytes = CreateMdl();
    BitConverter.TryWriteBytes(bytes.AsSpan(0, 4), MdlFile.Version5);
    Throws<UnsupportedMdlVersionException>(() => MdlFile.Read(bytes));
}

static void TestMdlUnresolvedBone()
{
    var model = MdlFile.Read(CreateMdl());
    var plan = OneStep(new Dictionary<string, Matrix4x4>(), 801).Materialize(model.Bones);
    var report = MdlRaceConverter.Convert(model, plan);
    Equal(BoneResolutionStrategy.Identity, report.BoneResolutions.Single().Strategy);
    var output = model.Write();
    var parsed = MdlFile.Read(output);
    var position = new Vector3(BitConverter.ToSingle(output, parsed.DataOffset),
        BitConverter.ToSingle(output, parsed.DataOffset + 4),
        BitConverter.ToSingle(output, parsed.DataOffset + 8));
    Near(new Vector3(1, 2, 3), position);
}

static void TestMdlStreamCountByte()
{
    // Exported models can store a stream count above 3 (seen: 6 with two real streams);
    // the vertex declaration is authoritative.
    var bytes = CreateMdl();
    var table = 0x44 + (int)BitConverter.ToUInt32(bytes, 4);
    var modelHeader = table + 8 + (int)BitConverter.ToUInt32(bytes, table + 4);
    var mesh = modelHeader + 56 + 3 * 60;
    Equal((byte)2, bytes[mesh + 35]);
    bytes[mesh + 35] = 6;
    var model = MdlFile.Read(bytes);
    MdlRaceConverter.Convert(model, OneStep(
        new Dictionary<string, Matrix4x4> { ["j_root"] = Matrix4x4.CreateTranslation(2, 3, 4) }));
    var output = model.Write();
    var parsed = MdlFile.Read(output);
    Near(new Vector3(3, 5, 7), new Vector3(BitConverter.ToSingle(output, parsed.DataOffset),
        BitConverter.ToSingle(output, parsed.DataOffset + 4), BitConverter.ToSingle(output, parsed.DataOffset + 8)));
    Equal((byte)6, parsed.Meshes.Single().VertexStreamCount);
}

static void TestMdlNeckMorphCountWithoutData()
{
    // Penumbra writes the neck-morph count but no neck-morph data.
    var bytes = CreateMdl();
    var table = 0x44 + (int)BitConverter.ToUInt32(bytes, 4);
    var modelHeader = table + 8 + (int)BitConverter.ToUInt32(bytes, table + 4);
    bytes[modelHeader + 43] = 3;
    var model = MdlFile.Read(bytes);
    Equal(0, model.NeckMorphs.Length);
    Equal("j_root", model.Bones.Single());
    True(bytes.AsSpan().SequenceEqual(model.Write()));
    model.ReplacePaths(new Dictionary<string, string> { ["mt_test.mtrl"] = "mt_a_longer_test.mtrl" });
    var rebuilt = MdlFile.Read(model.Write());
    Equal((byte)3, rebuilt.ModelHeader.NeckMorphCount);
    Equal("mt_a_longer_test.mtrl", rebuilt.Materials.Single());
}

static RacialDeformationPlan OneStep(IReadOnlyDictionary<string, Matrix4x4> matrices, ushort race = 101)
    => new([new RacialDeformationStep(race, false,
        matrices.ToImmutableDictionary(StringComparer.Ordinal))], []);

static byte[] CreateMdl(bool faceData = false, bool shape = false, string boneName = "j_root",
    bool twoBones = false)
    => TestAssets.CreateMdl(faceData, shape, boneName, twoBones);

static byte[] CreatePbd(short parent)
{
    var bytes = new byte[24];
    BitConverter.TryWriteBytes(bytes.AsSpan(0, 4), 1);
    BitConverter.TryWriteBytes(bytes.AsSpan(4, 2), (ushort)101);
    BitConverter.TryWriteBytes(bytes.AsSpan(6, 2), (short)0);
    BitConverter.TryWriteBytes(bytes.AsSpan(8, 4), 0);
    BitConverter.TryWriteBytes(bytes.AsSpan(12, 4), 1f);
    BitConverter.TryWriteBytes(bytes.AsSpan(16, 2), parent);
    BitConverter.TryWriteBytes(bytes.AsSpan(18, 2), (short)-1);
    BitConverter.TryWriteBytes(bytes.AsSpan(20, 2), (short)-1);
    BitConverter.TryWriteBytes(bytes.AsSpan(22, 2), (short)0);
    return bytes;
}

static void TestCustomizationDescriptors()
{
    var tail = CustomizationKinds.Get(AssetKind.Tail);
    True(tail.SupportsRace(701));
    True(tail.SupportsRace(1601));
    True(!tail.SupportsRace(101));
    True(!tail.SupportsEst);

    var ear = CustomizationKinds.Get(AssetKind.VieraEar);
    Equal("zear", ear.Directory);
    True(ear.SupportsRace(1701));
    True(ear.SupportsRace(1801));
    True(!ear.SupportsRace(1601));
}

static void TestCustomizationPaths()
{
    var source = new CustomizationPathEndpoint(AssetKind.VieraEar, 1801, 1);
    var target = new CustomizationPathEndpoint(AssetKind.VieraEar, 1801, 2);
    var path = "chara/human/c1801/obj/zear/z0001/material/v0001/mt_c1801z0001_a.mtrl";
    Equal("chara/human/c1801/obj/zear/z0002/material/v0001/mt_c1801z0002_a.mtrl",
        CustomizationPaths.Rewrite(path, source, target));
    Equal("/mt_c0801t0002_til_test.mtrl",
        CustomizationPaths.RewriteOwnedReference("/mt_c1801z0001_zer_test.mtrl", source,
            new CustomizationPathEndpoint(AssetKind.Tail, 801, 2)));

    var mixed = "keep chara/human/c1701/obj/zear/z0001/a.mdl; change " + path;
    var rewritten = CustomizationPaths.Rewrite(mixed, source, target);
    True(rewritten.Contains("c1701/obj/zear/z0001", StringComparison.Ordinal));
    True(rewritten.Contains("c1801/obj/zear/z0002", StringComparison.Ordinal));
    True(CustomizationPaths.FindEndpoints(@"c0701\obj\tail\t0003\model.mdl")
        .Contains(new CustomizationPathEndpoint(AssetKind.Tail, 701, 3)));
    Equal(0, CustomizationPaths.FindEndpoints("c0701/obj/tail/z0003/model.mdl").Length);
    var tailTarget = new CustomizationPathEndpoint(AssetKind.Tail, 701, 2);
    Equal("chara/human/c0701/obj/tail/t0002/material/v0001/mt_c0701t0002_a.mtrl",
        CustomizationPaths.Rewrite(path, source, tailTarget));
    // Ear (and face) materials are unversioned; Penumbra's own item swap drops the
    // variant folder for them while tails keep v0001.
    Equal("chara/human/c1801/obj/zear/z0001/material/mt_c1801z0001_a.mtrl",
        CustomizationPaths.Rewrite(
            "chara/human/c0701/obj/tail/t0002/material/v0001/mt_c0701t0002_a.mtrl",
            tailTarget, source));
    True(CustomizationKinds.CanConvert(AssetKind.Tail, AssetKind.VieraEar));
    True(CustomizationKinds.CanConvert(AssetKind.VieraEar, AssetKind.Tail));
    True(!CustomizationKinds.CanConvert(AssetKind.Hair, AssetKind.Tail));
    Throws<ArgumentException>(() => CustomizationPaths.Rewrite(
        "chara/human/c0101/obj/hair/h0001/model.mdl",
        new CustomizationPathEndpoint(AssetKind.Hair, 101, 1), tailTarget));
}

static void TestGearPaths()
{
    var source = new GearPathEndpoint(false, 123, "top");
    var target = new GearPathEndpoint(true, 456, "ear");
    Equal("chara/accessory/a0456/model/c0101a0456_ear.mdl",
        GearPaths.RewriteGamePath(
            "chara/equipment/e0123/model/c0101e0123_top.mdl", source, target));
    Equal("chara/accessory/a0456/material/v0001/mt_c0101a0456_ear_a.mtrl",
        GearPaths.RewriteGamePath(
            "chara/equipment/e0123/material/v0001/mt_c0101e0123_top_a.mtrl", source, target));
    Equal("/mt_c0101a0456_dwn_ear_a.mtrl",
        GearPaths.RewriteOwnedReference("/mt_c0101e0123_dwn_a.mtrl", source, target));
    Equal("chara/accessory/a0456/common/texture/shared.tex",
        GearPaths.RewriteGamePath("chara/common/texture/shared.tex", source, target, relocateCommon: true));
    Equal(@"C:\mod\chara\accessory\a0456\common\texture\shared.tex",
        GearPaths.RewriteGamePath(@"C:\mod\chara\common\texture\shared.tex", source, target,
            relocateCommon: true));

    var sameRootTarget = new GearPathEndpoint(false, 123, "glv");
    Equal("chara/equipment/e0123/model/c0101e0123_glv.mdl",
        GearPaths.RewriteGamePath(
            "chara/equipment/e0123/model/c0101e0123_top.mdl", source, sameRootTarget));
    True(!GearPaths.RewriteGamePath("chara/equipment/e0123/e0123.imc", source, sameRootTarget)
        .Contains("e0123_glv.imc", StringComparison.Ordinal));
}

static void TestCharacterMaterialPaths()
{
    var source = new CustomizationPathEndpoint(AssetKind.Hair, 501, 120);
    var target = new CustomizationPathEndpoint(AssetKind.Hair, 1801, 120);
    Equal(new CustomizationPathEndpoint(AssetKind.Hair, 101, 120),
        CustomizationPaths.GetHairMaterialEndpoint(source));
    Equal(new CustomizationPathEndpoint(AssetKind.Hair, 201, 120),
        CustomizationPaths.GetHairMaterialEndpoint(target));
    Equal(
        "chara/human/c0201/obj/hair/h0120/material/v0001/mt_c0201h0120_c1801_a.mtrl",
        CustomizationPaths.Rewrite(
            "chara/human/c0101/obj/hair/h0120/material/v0001/mt_c0101h0120_c0501_a.mtrl",
            source, target));
    Equal("/mt_c0201h0120_c1801_a.mtrl",
        CustomizationPaths.RewriteOwnedReference("/mt_c0101h0120_c0501_a.mtrl", source, target));
    Equal("/mt_c0401b0001_a.mtrl",
        CustomizationPaths.FixSkinMaterialReference("/mt_c0101b0099_a.mtrl", 1001));
    Equal("/mt_c0101b0099_a.mtrl",
        CustomizationPaths.FixSkinMaterialReference("/mt_c0101b0099_a.mtrl", 701));
    Equal("/mt_c1801b0001_b.mtrl",
        CustomizationPaths.FixSkinMaterialReference("/mt_c0201b0007_b.mtrl", 1801));
}

static void TestFingerprint()
{
    var operations = new[]
    {
        new ConversionOperation("asset", "b", "d"),
        new ConversionOperation("asset", "a", "c"),
    };
    Equal(ModFingerprint.ComputePlan(operations), ModFingerprint.ComputePlan(operations.Reverse()));
}

static byte[] BuildMtrl(string texture, string map, string colorSet, string shader, byte[] tail)
    => TestAssets.BuildMtrl(texture, map, colorSet, shader, tail);

static void TestCustomizationTargets()
{
    var hair = CustomizationTargets.AllowedRaces(AssetKind.Hair, 801, AssetKind.Hair);
    True(hair.Contains((ushort)101) && hair.Contains((ushort)1801));
    True(!hair.Contains((ushort)1101) && !hair.Contains((ushort)1201));
    Equal(16, hair.Length);

    var femaleFace = CustomizationTargets.AllowedRaces(AssetKind.Face, 801, AssetKind.Face);
    True(femaleFace.All(CustomizationTargets.IsFemale));
    Equal(8, femaleFace.Length);
    var maleFace = CustomizationTargets.AllowedRaces(AssetKind.Face, 1501, AssetKind.Face);
    True(maleFace.All(r => !CustomizationTargets.IsFemale(r)) && maleFace.Contains((ushort)1501));

    True(CustomizationTargets.BlockReason(AssetKind.Face, 801, AssetKind.Face, 701) != null);
    True(CustomizationTargets.BlockReason(AssetKind.Hair, 801, AssetKind.Hair, 1101) != null);
    True(CustomizationTargets.BlockReason(AssetKind.Hair, 1101, AssetKind.Hair, 701) != null);
    True(CustomizationTargets.BlockReason(AssetKind.Hair, 1101, AssetKind.Hair, 1201) == null);
    True(CustomizationTargets.BlockReason(AssetKind.Face, 1101, AssetKind.Face, 1201) != null);
    True(CustomizationTargets.AllowedRaces(AssetKind.Hair, 1201, AssetKind.Hair).SequenceEqual(new ushort[] { 1101, 1201 }));
    Equal((ushort)1101, CustomizationTargets.AllowedRaces(AssetKind.Face, 1101, AssetKind.Face).Single());
    True(CustomizationTargets.BlockReason(AssetKind.Tail, 701, AssetKind.VieraEar, 1801) == null);
}

static string[] ThreeMaterials() =>
    ["/mt_c0201e0100_top_a.mtrl", "/mt_c0201b0001_a.mtrl", "/mt_c0201e0100_top_b.mtrl"];

static void TestMeshGroupsDescribe()
{
    var groups = MdlMeshGroups.Describe(TestAssets.CreateMultiMeshMdl(ThreeMaterials()));
    Equal(3, groups.Count);
    Equal("atr_test", groups[0].Attributes.Single());
    True(groups[1].Attributes.IsEmpty);
    True(!groups[0].IsSkin && groups[1].IsSkin && !groups[2].IsSkin);
    Equal(1, groups[2].Triangles);
    Equal("/mt_c0201e0100_top_b.mtrl", groups[2].Material);
}

static void TestMeshRemoval()
{
    var bytes = TestAssets.CreateMultiMeshMdl(ThreeMaterials(), shapeMesh: 2);
    var original = MdlFile.Read(bytes);
    var parsed = MdlFile.Read(MdlMeshGroups.Remove(bytes, new MeshRemoval([1], 3)));

    Equal(2, parsed.Meshes.Length);
    Equal(2, parsed.VertexDeclarations.Length);
    Equal("/mt_c0201e0100_top_a.mtrl", parsed.Materials[parsed.Meshes[0].MaterialIndex]);
    Equal("/mt_c0201e0100_top_b.mtrl", parsed.Materials[parsed.Meshes[1].MaterialIndex]);
    Equal(2, parsed.Submeshes.Length);
    Equal((ushort)1, parsed.Meshes[1].SubmeshIndex);
    Equal(1u, parsed.Submeshes[0].AttributeMask);
    Equal((ushort)2, parsed.Lods[0].MeshCount);
    Equal((ushort)2, parsed.Lods[0].WaterMeshIndex);
    Equal((ushort)2, parsed.Lods[0].ShadowMeshIndex);
    // The kept mesh's shape still points at its first index.
    Equal((ushort)1, parsed.Shapes[0].MeshCounts[0]);
    Equal(parsed.Meshes[1].StartIndex, parsed.ShapeMeshes[parsed.Shapes[0].MeshStarts[0]].MeshIndexOffset);
    // Vertex and index data are carried over byte for byte; only metadata changed.
    True(bytes.AsSpan(original.DataOffset).SequenceEqual(MdlMeshGroups.Remove(bytes, new MeshRemoval([1], 3))
        .AsSpan(parsed.DataOffset)));
    Equal(2, MdlMeshGroups.Describe(MdlMeshGroups.Remove(bytes, new MeshRemoval([1], 3))).Count);
}

static void TestMeshRemovalDropsShape()
{
    var bytes = TestAssets.CreateMultiMeshMdl(ThreeMaterials(), shapeMesh: 2);
    var parsed = MdlFile.Read(MdlMeshGroups.Remove(bytes, new MeshRemoval([0, 2], 3)));
    Equal(1, parsed.Meshes.Length);
    Equal("/mt_c0201b0001_a.mtrl", parsed.Materials[parsed.Meshes[0].MaterialIndex]);
    Equal((ushort)0, parsed.Meshes[0].SubmeshIndex);
    Equal("shp_test", parsed.Shapes.Single().Name);
    Equal((ushort)0, parsed.Shapes[0].MeshCounts[0]);
    Equal(0, parsed.ShapeMeshes.Length);
}

static void TestMeshRemovalGuards()
{
    var bytes = TestAssets.CreateMultiMeshMdl(ThreeMaterials());
    Throws<InvalidDataException>(() => MdlMeshGroups.Remove(bytes, new MeshRemoval([0, 1, 2], 3)));
    Throws<InvalidDataException>(() => MdlMeshGroups.Remove(bytes, new MeshRemoval([1], 4)));
    Throws<InvalidDataException>(() => MdlMeshGroups.Remove(bytes, new MeshRemoval([3], 3)));
    True(bytes.AsSpan().SequenceEqual(MdlFile.Read(bytes).Write()));
}

static void Equal<T>(T expected, T actual) where T : notnull
{
    if (!EqualityComparer<T>.Default.Equals(expected, actual))
        throw new Exception($"Expected {expected}, got {actual}.");
}

static void True(bool value)
{
    if (!value) throw new Exception("Expected true.");
}

static void Near(Vector3 expected, Vector3 actual)
{
    if (Vector3.Distance(expected, actual) > 0.0001f)
        throw new Exception($"Expected {expected}, got {actual}.");
}

static void Throws<T>(Action action) where T : Exception
{
    try { action(); }
    catch (T) { return; }
    throw new Exception($"Expected {typeof(T).Name}.");
}
