using System.Buffers.Binary;
using System.Text;
using UniversalModConverter.Core;

/// <summary>Synthetic game-file fixtures shared by the tests.</summary>
internal static class TestAssets
{
    public static byte[] CreateMdl(bool faceData = false, bool shape = false, string boneName = "j_root",
        bool twoBones = false, string material = "mt_test.mtrl", bool neckMorph = false, bool garbledNeckMorph = false)
    {
        var boneNames = twoBones ? new[] { "a", "b" } : new[] { boneName };
        var vertexCount = shape ? 2 : 1;
        var vertexBufferSize = vertexCount * 36;
        using var stream = new MemoryStream();
        using var writer = new BinaryWriter(stream, Encoding.UTF8, true);
        writer.Write(new byte[0x44]);
        // One 136-byte vertex declaration.
        WriteElement(0, 0, 2, 0);   // position, Single3
        WriteElement(0, 12, 5, 1);  // weights, UByte4
        WriteElement(0, 16, 5, 2);  // indices, UByte4
        WriteElement(1, 0, 2, 3);   // normal, Single3
        WriteElement(1, 12, 8, 6);  // tangent, NByte4
        WriteElement(255, 0, 0, 0);
        writer.Write(new byte[136 - 6 * 8]);

        var boneOffsets = new List<uint>();
        var boneText = new StringBuilder();
        foreach (var name in boneNames)
        {
            boneOffsets.Add((uint)Encoding.UTF8.GetByteCount(boneText.ToString()));
            boneText.Append(name).Append('\0');
        }
        var boneBytes = Encoding.UTF8.GetBytes(boneText.ToString());
        var materialBytes = Encoding.UTF8.GetBytes(material + "\0");
        var shapeBytes = shape ? Encoding.UTF8.GetBytes("shp_test\0") : Array.Empty<byte>();
        writer.Write((ushort)(boneNames.Length + 1 + (shape ? 1 : 0))); writer.Write((ushort)0);
        writer.Write((uint)(boneBytes.Length + materialBytes.Length + shapeBytes.Length));
        writer.Write(boneBytes); writer.Write(materialBytes); writer.Write(shapeBytes);

        var modelHeaderOffset = (int)stream.Position;
        writer.Write(1f);                  // radius
        writer.Write((ushort)1);           // meshes
        writer.Write((ushort)0);           // attributes
        writer.Write((ushort)1);           // submeshes
        writer.Write((ushort)1);           // materials
        writer.Write((ushort)boneNames.Length); // bones
        writer.Write((ushort)1);           // bone tables
        writer.Write((ushort)(shape ? 1 : 0));
        writer.Write((ushort)(shape ? 1 : 0));
        writer.Write((ushort)(shape ? 1 : 0));
        writer.Write((byte)1); writer.Write((byte)0); writer.Write((ushort)0); // lod, flags, elements
        writer.Write((byte)0); writer.Write((byte)0); // terrain, flags2
        writer.Write(0f); writer.Write(0f);
        writer.Write((ushort)0); writer.Write((ushort)0);
        writer.Write((byte)0); writer.Write((byte)0); writer.Write((byte)0); writer.Write((byte)(neckMorph ? 1 : 0));
        writer.Write((ushort)2); writer.Write((ushort)0);
        writer.Write(faceData ? (uint)vertexCount : 0u);
        writer.Write(0u);

        var lodTableOffset = (int)stream.Position;
        writer.Write(new byte[60 * 3]);
        // Mesh.
        writer.Write((ushort)vertexCount); writer.Write((ushort)0); writer.Write(3u);
        writer.Write((ushort)0); writer.Write((ushort)0); writer.Write((ushort)1); writer.Write((ushort)0);
        writer.Write(0u);
        writer.Write(0u); writer.Write((uint)(vertexCount * 20)); writer.Write(0u);
        writer.Write((byte)20); writer.Write((byte)16); writer.Write((byte)0); writer.Write((byte)2);
        // Submesh.
        writer.Write(0u); writer.Write(3u); writer.Write(0u); writer.Write((ushort)0); writer.Write((ushort)1);
        // Material then bone string offsets.
        writer.Write((uint)boneBytes.Length);
        foreach (var offset in boneOffsets) writer.Write(offset);
        // v6 bone table header and padded entries.
        writer.Write((ushort)1); writer.Write((ushort)boneNames.Length);
        for (ushort index = 0; index < boneNames.Length; index++) writer.Write(index);
        if ((boneNames.Length & 1) != 0) writer.Write((ushort)0);
        if (shape)
        {
            writer.Write((uint)(boneBytes.Length + materialBytes.Length));
            writer.Write((ushort)0); writer.Write((ushort)0); writer.Write((ushort)0);
            writer.Write((ushort)1); writer.Write((ushort)0); writer.Write((ushort)0);
            writer.Write(0u); writer.Write(1u); writer.Write(0u);
            writer.Write((ushort)0); writer.Write((ushort)1);
        }
        writer.Write((uint)(boneNames.Length * 2));
        for (ushort index = 0; index < boneNames.Length; index++) writer.Write(index); // submesh bone map
        if (neckMorph)
        {
            // The first vertex as a seam vertex: position, packed weights (all on bone 0), normal, bones.
            // A garbled one lists a place no vertex is.
            foreach (var value in garbledNeckMorph ? [9f, 9f, 9f] : new[] { 1f, 2f, 3f }) writer.Write(value);
            writer.Write(0xFFu);
            writer.Write(0f); writer.Write(0f); writer.Write(1f); writer.Write(0u);
        }
        if (faceData)
        {
            for (var i = 0; i < vertexCount; i++)
            {
                writer.Write(i + 1f); writer.Write(2f); writer.Write(3f); writer.Write(i == 0 ? 1u : 0u);
            }
        }
        var padding = (8 - (((int)stream.Position + 1) & 7)) & 7;
        writer.Write((byte)padding); writer.Write(new byte[padding]);
        // Four global boxes plus one box per bone.
        for (var i = 0; i < 4 + boneNames.Length; i++)
        {
            writer.Write(0f); writer.Write(0f); writer.Write(0f); writer.Write(1f);
            writer.Write(0f); writer.Write(0f); writer.Write(0f); writer.Write(1f);
        }
        var dataOffset = (int)stream.Position;
        // Stream 0.
        for (var i = 0; i < vertexCount; i++)
        {
            writer.Write(i + 1f); writer.Write(2f); writer.Write(3f);
            writer.Write((byte)(twoBones ? 128 : 255)); writer.Write((byte)(twoBones ? 127 : 0));
            writer.Write((byte)0); writer.Write((byte)0);
            writer.Write((byte)0); writer.Write((byte)(twoBones ? 1 : 0)); writer.Write((byte)0); writer.Write((byte)0);
        }
        // Stream 1.
        for (var i = 0; i < vertexCount; i++)
        {
            writer.Write(0f); writer.Write(0f); writer.Write(1f);
            writer.Write((byte)128); writer.Write((byte)255); writer.Write((byte)128); writer.Write((byte)77);
        }
        // 3 ushort indices and 10 bytes alignment.
        writer.Write((ushort)0); writer.Write((ushort)0); writer.Write((ushort)0); writer.Write(new byte[10]);

        var result = stream.ToArray();
        void U16(int offset, ushort value) => BitConverter.TryWriteBytes(result.AsSpan(offset, 2), value);
        void U32(int offset, uint value) => BitConverter.TryWriteBytes(result.AsSpan(offset, 4), value);
        U32(0, MdlFile.Version6);
        U32(4, 136);
        U32(8, (uint)(dataOffset - 0x44 - 136));
        U16(12, 1); U16(14, 1);
        U32(16, (uint)dataOffset); U32(28, (uint)(dataOffset + vertexBufferSize));
        U32(40, (uint)vertexBufferSize); U32(52, 16);
        result[64] = 1;
        // LOD0 record.
        U16(lodTableOffset, 0); U16(lodTableOffset + 2, 1);
        U32(lodTableOffset + 44, (uint)vertexBufferSize); U32(lodTableOffset + 48, 16);
        U32(lodTableOffset + 52, (uint)dataOffset); U32(lodTableOffset + 56, (uint)(dataOffset + vertexBufferSize));
        return result;

        void WriteElement(byte streamIndex, byte offset, byte type, byte usage)
        {
            writer.Write(streamIndex); writer.Write(offset); writer.Write(type); writer.Write(usage);
            writer.Write((byte)0); writer.Write(new byte[3]);
        }
    }


    /// <summary>
    /// An MDL v6 with one LOD and one single-triangle mesh per material (in order). Mesh 0's
    /// submesh carries the attribute "atr_test"; <paramref name="shapeMesh"/> gets a shape.
    /// <paramref name="attributes"/> replaces "atr_test", and <paramref name="partMasks"/> gives
    /// every part (submesh, mesh by mesh) its own attribute mask.
    /// </summary>
    public static byte[] CreateMultiMeshMdl(string[] materials, int? shapeMesh = null, int partsPerMesh = 1,
        string[]? attributes = null, uint[]? partMasks = null)
    {
        attributes ??= ["atr_test"];
        var meshCount = materials.Length;
        var parts = partsPerMesh;
        var indexCount = meshCount * parts * 3;
        const int vertexStride0 = 20, vertexStride1 = 16, vertexSize = vertexStride0 + vertexStride1;
        var vertexBufferSize = meshCount * vertexSize;
        var indexBufferSize = (indexCount * 2 + 15) & ~15;
        var hasShape = shapeMesh.HasValue;

        using var stream = new MemoryStream();
        using var writer = new BinaryWriter(stream, Encoding.UTF8, true);
        writer.Write(new byte[0x44]);
        for (var m = 0; m < meshCount; m++)
        {
            WriteElement(0, 0, 2, 0); WriteElement(0, 12, 5, 1); WriteElement(0, 16, 5, 2);
            WriteElement(1, 0, 2, 3); WriteElement(1, 12, 8, 6); WriteElement(255, 0, 0, 0);
            writer.Write(new byte[136 - 6 * 8]);
        }

        // Strings: bone, materials, attributes, optional shape name.
        var strings = new List<string> { "j_root" };
        strings.AddRange(materials);
        strings.AddRange(attributes);
        if (hasShape) strings.Add("shp_test");
        var offsets = new List<uint>();
        using (var text = new MemoryStream())
        {
            foreach (var value in strings)
            {
                offsets.Add((uint)text.Length);
                text.Write(Encoding.UTF8.GetBytes(value + "\0"));
            }
            writer.Write((ushort)strings.Count); writer.Write((ushort)0);
            writer.Write((uint)text.Length); writer.Write(text.ToArray());
        }
        uint Offset(string value) => offsets[strings.IndexOf(value)];

        writer.Write(1f);
        writer.Write((ushort)meshCount); writer.Write((ushort)attributes.Length); writer.Write((ushort)(meshCount * parts));
        writer.Write((ushort)meshCount); writer.Write((ushort)1); writer.Write((ushort)1);
        writer.Write((ushort)(hasShape ? 1 : 0)); writer.Write((ushort)(hasShape ? 1 : 0)); writer.Write((ushort)(hasShape ? 1 : 0));
        writer.Write((byte)1); writer.Write((byte)0); writer.Write((ushort)0);
        writer.Write((byte)0); writer.Write((byte)0);
        writer.Write(0f); writer.Write(0f);
        writer.Write((ushort)0); writer.Write((ushort)0);
        writer.Write((byte)0); writer.Write((byte)0); writer.Write((byte)0); writer.Write((byte)0);
        writer.Write((ushort)2); writer.Write((ushort)0);
        writer.Write(0u); writer.Write(0u);

        var lodTableOffset = (int)stream.Position;
        writer.Write(new byte[60 * 3]);
        for (var m = 0; m < meshCount; m++)
        {
            writer.Write((ushort)1); writer.Write((ushort)0); writer.Write((uint)(3 * parts));
            writer.Write((ushort)m); writer.Write((ushort)(m * parts)); writer.Write((ushort)parts); writer.Write((ushort)0);
            writer.Write((uint)(m * 3 * parts));
            writer.Write((uint)(m * vertexSize)); writer.Write((uint)(m * vertexSize + vertexStride0)); writer.Write(0u);
            writer.Write((byte)vertexStride0); writer.Write((byte)vertexStride1); writer.Write((byte)0); writer.Write((byte)2);
        }
        foreach (var attribute in attributes) writer.Write(Offset(attribute));
        for (var m = 0; m < meshCount; m++)
        for (var p = 0; p < parts; p++)
        {
            writer.Write((uint)((m * parts + p) * 3)); writer.Write(3u);
            writer.Write(partMasks?[m * parts + p] ?? (m == 0 && p == 0 ? 1u : 0u));
            writer.Write((ushort)0); writer.Write((ushort)1);
        }
        foreach (var material in materials) writer.Write(Offset(material));
        writer.Write(Offset("j_root"));
        writer.Write((ushort)1); writer.Write((ushort)1); writer.Write((ushort)0); writer.Write((ushort)0);
        if (hasShape)
        {
            writer.Write(Offset("shp_test"));
            writer.Write((ushort)0); writer.Write((ushort)0); writer.Write((ushort)0);
            writer.Write((ushort)1); writer.Write((ushort)0); writer.Write((ushort)0);
            writer.Write((uint)(shapeMesh!.Value * 3 * parts)); writer.Write(1u); writer.Write(0u);
            writer.Write((ushort)0); writer.Write((ushort)(shapeMesh.Value));
        }
        writer.Write(2u); writer.Write((ushort)0);
        var padding = (8 - (((int)stream.Position + 1) & 7)) & 7;
        writer.Write((byte)padding); writer.Write(new byte[padding]);
        for (var i = 0; i < 5; i++)
        {
            writer.Write(0f); writer.Write(0f); writer.Write(0f); writer.Write(1f);
            writer.Write(0f); writer.Write(0f); writer.Write(0f); writer.Write(1f);
        }

        var dataOffset = (int)stream.Position;
        for (var m = 0; m < meshCount; m++)
        {
            writer.Write(m + 1f); writer.Write(2f); writer.Write(3f);
            writer.Write((byte)255); writer.Write((byte)0); writer.Write((byte)0); writer.Write((byte)0);
            writer.Write(0u);
            writer.Write(0f); writer.Write(0f); writer.Write(1f);
            writer.Write((byte)128); writer.Write((byte)255); writer.Write((byte)128); writer.Write((byte)77);
        }
        var indexStart = (int)stream.Position;
        // Several parts get distinct indices, so a removed part's triangles are recognizable.
        for (var i = 0; i < indexCount; i++) writer.Write((ushort)(parts > 1 ? i % 3 : 0));
        writer.Write(new byte[indexBufferSize - indexCount * 2]);

        var result = stream.ToArray();
        void U16(int offset, ushort value) => BitConverter.TryWriteBytes(result.AsSpan(offset, 2), value);
        void U32(int offset, uint value) => BitConverter.TryWriteBytes(result.AsSpan(offset, 4), value);
        U32(0, MdlFile.Version6);
        U32(4, (uint)(136 * meshCount));
        U32(8, (uint)(dataOffset - 0x44 - 136 * meshCount));
        U16(12, (ushort)meshCount); U16(14, (ushort)meshCount);
        U32(16, (uint)dataOffset); U32(28, (uint)indexStart);
        U32(40, (uint)vertexBufferSize); U32(52, (uint)indexBufferSize);
        result[64] = 1;
        U16(lodTableOffset, 0); U16(lodTableOffset + 2, (ushort)meshCount);
        // Water, shadow and vertical-fog ranges start after the main meshes, empty.
        U16(lodTableOffset + 12, (ushort)meshCount); U16(lodTableOffset + 16, (ushort)meshCount);
        U16(lodTableOffset + 24, (ushort)meshCount);
        U32(lodTableOffset + 44, (uint)vertexBufferSize); U32(lodTableOffset + 48, (uint)indexBufferSize);
        U32(lodTableOffset + 52, (uint)dataOffset); U32(lodTableOffset + 56, (uint)indexStart);
        return result;

        void WriteElement(byte streamIndex, byte offset, byte type, byte usage)
        {
            writer.Write(streamIndex); writer.Write(offset); writer.Write(type); writer.Write(usage);
            writer.Write((byte)0); writer.Write(new byte[3]);
        }
    }

    public static byte[] BuildMtrl(string texture, string map, string colorSet, string shader, byte[] tail)
    {
        using var stringStream = new MemoryStream();
        using var stringWriter = new BinaryWriter(stringStream, Encoding.UTF8, leaveOpen: true);
        var strings = new[] { texture, map, colorSet };
        var offsets = new short[strings.Length];
        for (var index = 0; index < strings.Length; index++)
        {
            offsets[index] = checked((short)stringStream.Position);
            stringWriter.Write(Encoding.UTF8.GetBytes(strings[index]));
            stringWriter.Write((byte)0);
        }
        var shaderOffset = checked((ushort)stringStream.Position);
        stringWriter.Write(Encoding.UTF8.GetBytes(shader));
        stringWriter.Write((byte)0);
        while ((stringStream.Length & 3) != 0) stringWriter.Write((byte)0);

        using var output = new MemoryStream();
        using var writer = new BinaryWriter(output, Encoding.UTF8, leaveOpen: true);
        writer.Write(0x01030000);
        writer.Write((ushort)0);
        writer.Write((ushort)0);
        writer.Write(checked((ushort)stringStream.Length));
        writer.Write(shaderOffset);
        writer.Write((byte)1);
        writer.Write((byte)1);
        writer.Write((byte)1);
        writer.Write((byte)0);
        foreach (var offset in offsets)
        {
            writer.Write(offset);
            writer.Write((ushort)7);
        }
        writer.Write(stringStream.GetBuffer(), 0, checked((int)stringStream.Length));
        writer.Write(tail);
        var bytes = output.ToArray();
        BinaryPrimitives.WriteUInt16LittleEndian(bytes.AsSpan(4), checked((ushort)bytes.Length));
        return bytes;
    }

    /// <summary>A material that loads <paramref name="textures"/> and nothing else.</summary>
    public static byte[] BuildMtrl(params string[] textures)
    {
        using var stringStream = new MemoryStream();
        using var stringWriter = new BinaryWriter(stringStream, Encoding.UTF8, leaveOpen: true);
        var offsets = new short[textures.Length];
        for (var index = 0; index < textures.Length; index++)
        {
            offsets[index] = checked((short)stringStream.Position);
            stringWriter.Write(Encoding.UTF8.GetBytes(textures[index]));
            stringWriter.Write((byte)0);
        }
        var shaderOffset = checked((ushort)stringStream.Position);
        stringWriter.Write(Encoding.UTF8.GetBytes("skin.shpk"));
        stringWriter.Write((byte)0);
        while ((stringStream.Length & 3) != 0) stringWriter.Write((byte)0);

        using var output = new MemoryStream();
        using var writer = new BinaryWriter(output, Encoding.UTF8, leaveOpen: true);
        writer.Write(0x01030000);
        writer.Write((ushort)0);
        writer.Write((ushort)0);
        writer.Write(checked((ushort)stringStream.Length));
        writer.Write(shaderOffset);
        writer.Write(checked((byte)textures.Length));
        writer.Write((byte)0);
        writer.Write((byte)0);
        writer.Write((byte)0);
        foreach (var offset in offsets)
        {
            writer.Write(offset);
            writer.Write((ushort)0);
        }
        writer.Write(stringStream.GetBuffer(), 0, checked((int)stringStream.Length));
        writer.Write(new byte[] { 0xaa, 0xbb });
        var bytes = output.ToArray();
        BinaryPrimitives.WriteUInt16LittleEndian(bytes.AsSpan(4), checked((ushort)bytes.Length));
        return bytes;
    }

    /// <summary>
    /// A human.pbd with Midlander men at the root and <paramref name="child"/> below them, whose
    /// deformer moves <paramref name="bone"/> by <paramref name="matrix"/>.
    /// </summary>
    public static byte[] CreatePbd(ushort child, string bone, System.Numerics.Matrix4x4 matrix)
    {
        const int tree = 28, data = 48;
        var bytes = new byte[data + 56 + Encoding.UTF8.GetByteCount(bone) + 1];
        BinaryPrimitives.WriteInt32LittleEndian(bytes, 2);
        WriteEntry(0, 101, 0, 0);
        WriteEntry(1, child, 1, data);
        WriteTree(tree, -1, 1, -1, 0);
        WriteTree(tree + 8, 0, -1, -1, 1);

        BinaryPrimitives.WriteInt32LittleEndian(bytes.AsSpan(data), 1);
        BinaryPrimitives.WriteUInt16LittleEndian(bytes.AsSpan(data + 4), 56);
        // Three rows of a column-vector matrix: the transpose of System.Numerics' row vectors.
        float[] rows =
        [
            matrix.M11, matrix.M21, matrix.M31, matrix.M41,
            matrix.M12, matrix.M22, matrix.M32, matrix.M42,
            matrix.M13, matrix.M23, matrix.M33, matrix.M43,
        ];
        for (var i = 0; i < rows.Length; i++) BinaryPrimitives.WriteSingleLittleEndian(bytes.AsSpan(data + 8 + i * 4), rows[i]);
        Encoding.UTF8.GetBytes(bone).CopyTo(bytes, data + 56);
        return bytes;

        void WriteEntry(int index, ushort race, short treeIndex, int dataOffset)
        {
            var offset = 4 + index * 12;
            BinaryPrimitives.WriteUInt16LittleEndian(bytes.AsSpan(offset), race);
            BinaryPrimitives.WriteInt16LittleEndian(bytes.AsSpan(offset + 2), treeIndex);
            BinaryPrimitives.WriteInt32LittleEndian(bytes.AsSpan(offset + 4), dataOffset);
            BinaryPrimitives.WriteSingleLittleEndian(bytes.AsSpan(offset + 8), 1f);
        }

        void WriteTree(int offset, short parent, short firstChild, short sibling, short deformer)
        {
            BinaryPrimitives.WriteInt16LittleEndian(bytes.AsSpan(offset), parent);
            BinaryPrimitives.WriteInt16LittleEndian(bytes.AsSpan(offset + 2), firstChild);
            BinaryPrimitives.WriteInt16LittleEndian(bytes.AsSpan(offset + 4), sibling);
            BinaryPrimitives.WriteInt16LittleEndian(bytes.AsSpan(offset + 6), deformer);
        }
    }

    /// <summary>An extra-skeleton table (.est) with the given (set, race) → skeleton entries.</summary>
    public static byte[] CreateEst(params (ushort SetId, ushort Race, ushort Skeleton)[] entries)
    {
        var bytes = new byte[4 + entries.Length * 6];
        BinaryPrimitives.WriteUInt32LittleEndian(bytes, (uint)entries.Length);
        for (var i = 0; i < entries.Length; i++)
        {
            BinaryPrimitives.WriteUInt16LittleEndian(bytes.AsSpan(4 + i * 4), entries[i].SetId);
            BinaryPrimitives.WriteUInt16LittleEndian(bytes.AsSpan(6 + i * 4), entries[i].Race);
            BinaryPrimitives.WriteUInt16LittleEndian(bytes.AsSpan(4 + entries.Length * 4 + i * 2), entries[i].Skeleton);
        }
        return bytes;
    }
}
