using System.Buffers.Binary;
using System.Collections.Immutable;
using System.Numerics;
using System.Security.Cryptography;

namespace AdvancedPenumbraItemConverter.Core;

public sealed record MdlConversionReport(int LodCount, int MeshCount, int VertexCount,
    int ShapeVertexCount, ImmutableArray<BoneResolution> BoneResolutions);

/// <summary>Applies PBD racial deformation directly to every skinned v6 MDL vertex stream.</summary>
public static class MdlRaceConverter
{
    public static MdlConversionReport Convert(MdlFile model, RacialDeformationPlan plan)
    {
        ArgumentNullException.ThrowIfNull(model);
        ArgumentNullException.ThrowIfNull(plan);
        if (plan.Steps.IsDefaultOrEmpty)
            throw new ArgumentException("A racial deformation plan must contain at least one step.", nameof(plan));
        if (model.Lods.Take(model.Header.LodCount).Any(l => l.EdgeGeometrySize != 0))
            throw new MdlConversionException("unsupported_edge_geometry",
                "The MDL contains edge geometry whose skinning layout cannot be proven.");

        var transformedPositions = new List<Vector3>();
        var allPositions = new List<Vector3>();
        var bonePositions = Enumerable.Range(0, model.Bones.Length).Select(_ => new List<Vector3>()).ToArray();
        var usedBones = new HashSet<string>(StringComparer.Ordinal);
        var vertexCount = 0;

        for (var lodIndex = 0; lodIndex < model.Header.LodCount; lodIndex++)
        {
            var lod = model.Lods[lodIndex];
            for (var meshIndex = lod.MeshIndex; meshIndex < lod.MeshIndex + lod.MeshCount; meshIndex++)
            {
                var mesh = model.Meshes[meshIndex];
                // Unskinned meshes (no bone table) have no bones to deform and stay as they are.
                if (mesh.BoneTableIndex == ushort.MaxValue) continue;
                if (mesh.BoneTableIndex >= model.BoneTables.Length)
                    throw new MdlConversionException("malformed_bone_table", $"Mesh {meshIndex} references an invalid bone table.");
                var declaration = model.VertexDeclarations[meshIndex];
                var positionElement = Required(declaration, MdlVertexUsage.Position, meshIndex);
                var normalElement = Required(declaration, MdlVertexUsage.Normal, meshIndex);
                var weightElement = Required(declaration, MdlVertexUsage.BlendWeights, meshIndex);
                var indexElement = Required(declaration, MdlVertexUsage.BlendIndices, meshIndex);
                var tangentElement = Optional(declaration, MdlVertexUsage.Tangent);
                var flowElement = Optional(declaration, MdlVertexUsage.Flow);
                var influenceCount = InfluenceCount(weightElement, indexElement, meshIndex);
                var boneTable = model.BoneTables[mesh.BoneTableIndex].BoneIndices;

                for (var vertexIndex = 0; vertexIndex < mesh.VertexCount; vertexIndex++)
                {
                    var position = ReadVector(model, lodIndex, meshIndex, vertexIndex, positionElement);
                    var normal = ReadVector(model, lodIndex, meshIndex, vertexIndex, normalElement);
                    var tangent = tangentElement == null ? Vector3.Zero
                        : ReadVector(model, lodIndex, meshIndex, vertexIndex, tangentElement);
                    var flow = flowElement == null ? Vector3.Zero
                        : ReadVector(model, lodIndex, meshIndex, vertexIndex, flowElement);
                    var influences = ImmutableArray.CreateBuilder<VertexInfluence>(influenceCount);
                    var influencedBones = new List<int>(influenceCount);
                    var totalWeight = 0f;
                    for (var influence = 0; influence < influenceCount; influence++)
                    {
                        var weight = ReadWeight(model, lodIndex, meshIndex, vertexIndex, weightElement, influence);
                        if (weight <= 0) continue;
                        var tableIndex = ReadIndex(model, lodIndex, meshIndex, vertexIndex, indexElement, influence);
                        if (tableIndex >= boneTable.Length)
                            throw new MdlConversionException("malformed_bone_index",
                                $"Mesh {meshIndex} vertex {vertexIndex} references bone-table index {tableIndex}.");
                        var globalBoneIndex = boneTable[tableIndex];
                        if (globalBoneIndex >= model.Bones.Length)
                            throw new MdlConversionException("malformed_bone_index",
                                $"Mesh {meshIndex} references global bone index {globalBoneIndex}.");
                        var bone = model.Bones[globalBoneIndex];
                        influences.Add(new VertexInfluence(bone, weight));
                        influencedBones.Add(globalBoneIndex);
                        totalWeight += weight;
                        usedBones.Add(bone);
                    }
                    if (totalWeight <= 0)
                        throw new MdlConversionException("unweighted_vertex",
                            $"Mesh {meshIndex} vertex {vertexIndex} has no positive blend weight.");

                    var converted = new DeformableVertex(position, normal, tangent, tangent, flow,
                        influences.ToImmutable());
                    foreach (var step in plan.Steps)
                        converted = RacialDeformation.Transform(converted, step.BoneMatrices);
                    WriteVector(model, lodIndex, meshIndex, vertexIndex, positionElement, converted.Position);
                    WriteVector(model, lodIndex, meshIndex, vertexIndex, normalElement, converted.Normal);
                    if (tangentElement != null)
                        WriteVector(model, lodIndex, meshIndex, vertexIndex, tangentElement, converted.Tangent);
                    if (flowElement != null)
                        WriteVector(model, lodIndex, meshIndex, vertexIndex, flowElement, converted.Flow);

                    transformedPositions.Add(converted.Position);
                    allPositions.Add(converted.Position);
                    foreach (var boneIndex in influencedBones.Distinct()) bonePositions[boneIndex].Add(converted.Position);
                    vertexCount++;
                }
            }
        }

        if (allPositions.Count == 0)
            throw new MdlConversionException("empty_mdl", "The MDL contains no transformable vertices.");
        if (model.FaceData.Length != 0)
        {
            if (model.FaceData.Length != transformedPositions.Count)
                throw new MdlConversionException("ambiguous_face_data",
                    $"Face-data count {model.FaceData.Length} does not match vertex count {transformedPositions.Count}.");
            model.SetFaceData(transformedPositions);
        }

        var modelBounds = Bounds(allPositions);
        var boneBounds = bonePositions.Select(points => points.Count == 0
            ? new MdlBoundingBox(Vector4.Zero, Vector4.Zero)
            : Bounds(points)).ToArray();
        model.SetBounds(modelBounds, boneBounds);

        var resolutions = plan.BoneResolutions
            .Where(resolution => usedBones.Contains(resolution.Bone))
            .ToImmutableArray();
        return new MdlConversionReport(model.Header.LodCount, model.Meshes.Length, vertexCount,
            CountShapeVertices(model), resolutions);
    }

    public static string Hash(ReadOnlySpan<byte> bytes)
        => System.Convert.ToHexString(SHA256.HashData(bytes)).ToLowerInvariant();

    private static int CountShapeVertices(MdlFile model)
    {
        var values = new HashSet<(int Mesh, int Vertex)>();
        foreach (var shapeMesh in model.ShapeMeshes)
        {
            var meshIndex = -1;
            for (var i = 0; i < model.Meshes.Length; i++)
                if (model.Meshes[i].StartIndex == shapeMesh.MeshIndexOffset) { meshIndex = i; break; }
            if (meshIndex < 0 || (ulong)shapeMesh.ShapeValueOffset + shapeMesh.ShapeValueCount > (ulong)model.ShapeValues.Length)
                throw new MdlConversionException("malformed_shape_data", "A shape mesh references invalid values or mesh data.");
            for (var i = 0u; i < shapeMesh.ShapeValueCount; i++)
            {
                var value = model.ShapeValues[checked((int)(shapeMesh.ShapeValueOffset + i))];
                if (value.ReplacementVertexIndex >= model.Meshes[meshIndex].VertexCount)
                    throw new MdlConversionException("malformed_shape_data", "A shape replacement vertex is outside its mesh.");
                values.Add((meshIndex, value.ReplacementVertexIndex));
            }
        }
        return values.Count;
    }

    private static MdlVertexElement Required(MdlVertexDeclaration declaration, MdlVertexUsage usage, int mesh)
        => Optional(declaration, usage) ?? throw new MdlConversionException("missing_vertex_channel",
            $"Mesh {mesh} has no {usage} vertex channel.");

    private static MdlVertexElement? Optional(MdlVertexDeclaration declaration, MdlVertexUsage usage)
    {
        var matches = declaration.Elements.Where(e => e.Usage == usage && e.UsageIndex == 0).ToArray();
        if (matches.Length > 1)
            throw new MdlConversionException("ambiguous_vertex_channel", $"The MDL has multiple {usage} channels.");
        return matches.SingleOrDefault();
    }

    private static int InfluenceCount(MdlVertexElement weights, MdlVertexElement indices, int mesh)
    {
        static int Count(MdlVertexElement element) => element.Type switch
        {
            // The game stores four-byte blend weights as NByte4 in character
            // models, even though the values are unsigned normalized weights
            // (0..255), not signed normalized vectors like normals/tangents.
            MdlVertexType.UByte4 => 4,
            MdlVertexType.NByte4 when element.Usage == MdlVertexUsage.BlendWeights => 4,
            MdlVertexType.UShort4 => 8,
            _ => 0,
        };
        var weightCount = Count(weights);
        var indexCount = Count(indices);
        if (weightCount == 0 || weightCount != indexCount)
            throw new MdlConversionException("unsupported_blend_encoding",
                $"Mesh {mesh} uses unsupported or mismatched blend encodings.");
        return weightCount;
    }

    private static float ReadWeight(MdlFile model, int lod, int mesh, int vertex,
        MdlVertexElement element, int influence)
    {
        var offset = model.VertexOffset(lod, mesh, element.Stream, vertex) + element.Offset + influence;
        return model.MutableBytes[offset] / 255f;
    }

    private static int ReadIndex(MdlFile model, int lod, int mesh, int vertex,
        MdlVertexElement element, int influence)
    {
        var offset = model.VertexOffset(lod, mesh, element.Stream, vertex) + element.Offset + influence;
        return model.MutableBytes[offset];
    }

    private static Vector3 ReadVector(MdlFile model, int lod, int mesh, int vertex, MdlVertexElement element)
    {
        var offset = model.VertexOffset(lod, mesh, element.Stream, vertex) + element.Offset;
        var bytes = model.MutableBytes;
        return element.Type switch
        {
            MdlVertexType.Single3 or MdlVertexType.Single4 => new(ReadSingle(bytes, offset),
                ReadSingle(bytes, offset + 4), ReadSingle(bytes, offset + 8)),
            MdlVertexType.Half4 => new((float)ReadHalf(bytes, offset), (float)ReadHalf(bytes, offset + 2),
                (float)ReadHalf(bytes, offset + 4)),
            MdlVertexType.NByte4 => new(DecodeByte(bytes[offset]), DecodeByte(bytes[offset + 1]),
                DecodeByte(bytes[offset + 2])),
            MdlVertexType.NShort4 => new(DecodeShort(ReadInt16(bytes, offset)),
                DecodeShort(ReadInt16(bytes, offset + 2)), DecodeShort(ReadInt16(bytes, offset + 4))),
            _ => throw new MdlConversionException("unsupported_vertex_encoding",
                $"Vertex channel {element.Usage} uses unsupported encoding {element.Type}."),
        };
    }

    private static void WriteVector(MdlFile model, int lod, int mesh, int vertex,
        MdlVertexElement element, Vector3 value)
    {
        var offset = model.VertexOffset(lod, mesh, element.Stream, vertex) + element.Offset;
        var bytes = model.MutableBytes;
        switch (element.Type)
        {
            case MdlVertexType.Single3:
            case MdlVertexType.Single4:
                WriteSingle(bytes, offset, value.X); WriteSingle(bytes, offset + 4, value.Y);
                WriteSingle(bytes, offset + 8, value.Z); break;
            case MdlVertexType.Half4:
                WriteHalf(bytes, offset, (Half)value.X); WriteHalf(bytes, offset + 2, (Half)value.Y);
                WriteHalf(bytes, offset + 4, (Half)value.Z); break;
            case MdlVertexType.NByte4:
                bytes[offset] = EncodeByte(value.X); bytes[offset + 1] = EncodeByte(value.Y);
                bytes[offset + 2] = EncodeByte(value.Z); break;
            case MdlVertexType.NShort4:
                WriteInt16(bytes, offset, EncodeShort(value.X)); WriteInt16(bytes, offset + 2, EncodeShort(value.Y));
                WriteInt16(bytes, offset + 4, EncodeShort(value.Z)); break;
            default:
                throw new MdlConversionException("unsupported_vertex_encoding",
                    $"Vertex channel {element.Usage} uses unsupported encoding {element.Type}.");
        }
    }

    private static MdlBoundingBox Bounds(IReadOnlyList<Vector3> positions)
    {
        var min = positions[0];
        var max = positions[0];
        for (var i = 1; i < positions.Count; i++)
        {
            min = Vector3.Min(min, positions[i]);
            max = Vector3.Max(max, positions[i]);
        }
        return new MdlBoundingBox(new Vector4(min, 1), new Vector4(max, 1));
    }

    private static float DecodeByte(byte value) => value / 127.5f - 1f;
    private static byte EncodeByte(float value) => (byte)Math.Clamp((int)MathF.Round((Math.Clamp(value, -1f, 1f) + 1f) * 127.5f), 0, 255);
    private static float DecodeShort(short value) => Math.Max(value / 32767f, -1f);
    private static short EncodeShort(float value) => (short)Math.Clamp((int)MathF.Round(Math.Clamp(value, -1f, 1f) * 32767f), short.MinValue, short.MaxValue);
    private static float ReadSingle(Span<byte> bytes, int offset) => BitConverter.Int32BitsToSingle(BinaryPrimitives.ReadInt32LittleEndian(bytes.Slice(offset, 4)));
    private static void WriteSingle(Span<byte> bytes, int offset, float value) => BinaryPrimitives.WriteInt32LittleEndian(bytes.Slice(offset, 4), BitConverter.SingleToInt32Bits(value));
    private static Half ReadHalf(Span<byte> bytes, int offset) => BitConverter.UInt16BitsToHalf(BinaryPrimitives.ReadUInt16LittleEndian(bytes.Slice(offset, 2)));
    private static void WriteHalf(Span<byte> bytes, int offset, Half value) => BinaryPrimitives.WriteUInt16LittleEndian(bytes.Slice(offset, 2), BitConverter.HalfToUInt16Bits(value));
    private static short ReadInt16(Span<byte> bytes, int offset) => BinaryPrimitives.ReadInt16LittleEndian(bytes.Slice(offset, 2));
    private static void WriteInt16(Span<byte> bytes, int offset, short value) => BinaryPrimitives.WriteInt16LittleEndian(bytes.Slice(offset, 2), value);
}

public sealed class MdlConversionException(string code, string message) : Exception(message)
{
    public string Code { get; } = code;
}
