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
    ];

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
