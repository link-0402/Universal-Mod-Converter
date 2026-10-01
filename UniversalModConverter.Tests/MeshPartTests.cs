using System.Buffers.Binary;
using System.Collections.Immutable;
using UniversalModConverter.Core;

/// <summary>Mesh group parts (submeshes): listing, removing one, and hiding removed ones for the preview.</summary>
internal static class MeshPartTests
{
    public static (string Name, Action Run)[] All =>
    [
        ("Mesh groups list their parts", DescribeParts),
        ("Removing a part hides only its triangles and moves nothing", RemovePart),
        ("The preview hides removed groups and parts in place", HideRemoved),
        ("A removed part stays hidden while a shape is on", ShapesStayHidden),
        ("A model whose LOD struct is a few bytes off the header still reads", HeaderBufferOffsetsWin),
    ];

    /// <summary>
    /// The header's buffer offsets match the file; some exporters leave the LOD struct's copies a
    /// few bytes off, and the game ignores them. Reading such a model must not refuse it.
    /// </summary>
    private static void HeaderBufferOffsetsWin()
    {
        var bytes = TestAssets.CreateMdl();
        var model = MdlFile.Read(bytes);
        var vertex = model.Lods[0].VertexDataOffset;
        var index = model.Lods[0].IndexDataOffset;
        var pair = new byte[8];
        BinaryPrimitives.WriteUInt32LittleEndian(pair, vertex);
        BinaryPrimitives.WriteUInt32LittleEndian(pair.AsSpan(4), index);
        var at = bytes.AsSpan().IndexOf(pair);
        Assert.True(at >= 0, "the LOD struct holds the buffer locations");
        BinaryPrimitives.WriteUInt32LittleEndian(bytes.AsSpan(at + 4), index + 12);

        var read = MdlFile.Read(bytes);   // was refused: the index buffer ran past the end of the file
        Assert.Equal(index, read.Lods[0].IndexDataOffset);
        Assert.Equal(vertex, read.Lods[0].VertexDataOffset);
    }

    /// <summary>
    /// A shape replaces index-buffer entries with vertices of its own. Aimed at a removed part,
    /// it is pointed at the part's collapsed vertex, so the part cannot come back with the shape.
    /// The test model's one shape value replaces the first entry of group 1, which its part 0 holds.
    /// </summary>
    private static void ShapesStayHidden()
    {
        var input = TestAssets.CreateMultiMeshMdl(["/mt_a.mtrl", "/mt_b.mtrl"], shapeMesh: 1, partsPerMesh: 2);
        Assert.Equal((ushort)1, MdlFile.Read(input).ShapeValues.Single().ReplacementVertexIndex);

        var other = MdlMeshGroups.Remove(input, new MeshRemoval([], 2, [new MeshPartRef(1, 1)]));
        Assert.Equal((ushort)1, MdlFile.Read(other).ShapeValues.Single().ReplacementVertexIndex);

        foreach (var output in new[]
                 {
                     MdlMeshGroups.Remove(input, new MeshRemoval([], 2, [new MeshPartRef(1, 0)])),
                     MdlMeshGroups.Hide(input, new MeshRemoval([], 2, [new MeshPartRef(1, 0)])),
                 })
        {
            Assert.Equal(input.Length, output.Length);
            Assert.Equal(Indices(output)[6], MdlFile.Read(output).ShapeValues.Single().ReplacementVertexIndex);
        }
    }

    private static byte[] Model() => TestAssets.CreateMultiMeshMdl(["/mt_a.mtrl", "/mt_b.mtrl"], partsPerMesh: 2);

    private static void DescribeParts()
    {
        var groups = MdlMeshGroups.Describe(Model());
        Assert.Equal(2, groups.Count);
        Assert.Equal(2, groups[0].PartList.Length);
        Assert.Equal(1, groups[0].PartList[1].Triangles);
        Assert.Equal("atr_test", groups[0].PartList[0].Attributes.Single());
        Assert.True(groups[0].PartList[1].Attributes.IsEmpty, "Only the first part carries the attribute.");
    }

    private static void RemovePart()
    {
        var input = Model();
        var output = MdlMeshGroups.Remove(input, new MeshRemoval([], 2, [new MeshPartRef(0, 1)]));
        Assert.Equal(input.Length, output.Length);

        var before = Indices(input);
        var after = Indices(output);
        // Submesh 1 (group 0, part 1) is indices 3..5: all the same now, a zero-area triangle.
        Assert.True(after[3] == after[4] && after[4] == after[5], "The removed part is degenerate.");
        Assert.True(before[3..6].Distinct().Count() == 3, "The test model starts with a real triangle there.");
        Assert.True(before[..3].SequenceEqual(after[..3]) && before[6..].SequenceEqual(after[6..]), "Other parts are untouched.");
        Assert.Equal(2, MdlFile.Read(output).Meshes.Length);
    }

    private static void HideRemoved()
    {
        var input = Model();
        var output = MdlMeshGroups.Hide(input, new MeshRemoval([1], 2, [new MeshPartRef(0, 0)]));
        Assert.Equal(input.Length, output.Length);
        Assert.Equal(2, MdlFile.Read(output).Meshes.Length);
        var indices = Indices(output);
        for (var submesh = 0; submesh < 4; submesh++)
        {
            var degenerate = indices[(submesh * 3)..(submesh * 3 + 3)].Distinct().Count() == 1;
            // Part 0 of group 0 and all of group 1 are hidden; group 0's part 1 (submesh 1) stays.
            Assert.True(degenerate == (submesh != 1), $"Submesh {submesh} {(degenerate ? "is" : "is not")} hidden.");
        }
    }

    private static ushort[] Indices(byte[] data)
    {
        var model = MdlFile.Read(data);
        var lod = model.Lods[0];
        var count = (int)model.Submeshes.Sum(s => s.IndexCount);
        var result = new ushort[count];
        for (var i = 0; i < count; i++)
            result[i] = BinaryPrimitives.ReadUInt16LittleEndian(data.AsSpan((int)lod.IndexDataOffset + i * 2));
        return result;
    }
}
