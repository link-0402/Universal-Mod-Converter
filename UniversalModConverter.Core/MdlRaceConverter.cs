using System.Buffers.Binary;
using System.Collections.Immutable;
using System.Numerics;
using System.Security.Cryptography;

namespace UniversalModConverter.Core;

/// <param name="NeckMoved">How far, in metres, fitting the neck to the target race moved its seam (0: not fitted).</param>
/// <param name="NeckProblem">Why a neck that was to be fitted was not, or null.</param>
public sealed record MdlConversionReport(int LodCount, int MeshCount, int VertexCount,
    int ShapeVertexCount, ImmutableArray<BoneResolution> BoneResolutions, float NeckMoved = 0, string? NeckProblem = null);

/// <summary>
/// One vertex of a face's neck seam, the ring where it meets the body, as a face model's neck
/// morphs list them: its position and normal in the model.
/// </summary>
public sealed record NeckSeamPoint(Vector3 Position, Vector3 Normal);

/// <summary>
/// The neck seams a converted face is fitted between: the source race's (where to find the seam
/// in a model without neck morphs of its own) and the target race's, which its bodies meet.
/// Every face of a race shares one seam.
/// </summary>
public sealed record NeckFit(ImmutableArray<NeckSeamPoint> Source, ImmutableArray<NeckSeamPoint> Target);

/// <summary>Applies PBD racial deformation directly to every skinned v6 MDL vertex stream.</summary>
public static class MdlRaceConverter
{
    /// <summary>
    /// The neck seam a face model's neck morphs describe, or none when it has none. Another tool
    /// may have garbled them; a conversion only trusts them where the model's vertices agree.
    /// </summary>
    public static ImmutableArray<NeckSeamPoint> NeckSeam(MdlFile model)
    {
        ArgumentNullException.ThrowIfNull(model);
        return model.NeckMorphs.IsDefault ? [] : [.. model.NeckMorphs.Select(m => new NeckSeamPoint(m.Position, m.Normal))];
    }

    /// <param name="neck">
    /// Fits a face's neck to the target race's seam, which the deformation alone misses by up to
    /// 2.5 cm for some races (Roegadyn, Hrothgar, Lalafell); null leaves the neck as deformed.
    /// </param>
    public static MdlConversionReport Convert(MdlFile model, RacialDeformationPlan plan, NeckFit? neck = null)
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
        // Written once the neck is fitted, which needs every vertex deformed first.
        var deformed = new List<DeformedVertex>();

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

                    var original = new DeformableVertex(position, normal, tangent, tangent, flow,
                        influences.ToImmutable());
                    var converted = Deform(original, plan);
                    deformed.Add(new DeformedVertex(lodIndex, meshIndex, vertexIndex, positionElement, normalElement,
                        tangentElement, flowElement, position, converted, influencedBones.Distinct().ToArray()));
                }
            }
        }

        var (fitted, neckMoved, neckProblem) = neck == null ? (false, 0f, null) : FitNeck(model, neck, deformed);
        // The neck morphs list the seam vertices, so they move with them.
        if (!fitted) DeformNeckMorphs(model, deformed);
        foreach (var vertex in deformed)
        {
            var (lodIndex, meshIndex, vertexIndex) = (vertex.Lod, vertex.Mesh, vertex.Index);
            var converted = vertex.Converted;
            WriteVector(model, lodIndex, meshIndex, vertexIndex, vertex.PositionElement, converted.Position);
            WriteVector(model, lodIndex, meshIndex, vertexIndex, vertex.NormalElement, converted.Normal);
            if (vertex.TangentElement != null)
                WriteVector(model, lodIndex, meshIndex, vertexIndex, vertex.TangentElement, converted.Tangent);
            if (vertex.FlowElement != null)
                WriteVector(model, lodIndex, meshIndex, vertexIndex, vertex.FlowElement, converted.Flow);

            transformedPositions.Add(converted.Position);
            allPositions.Add(converted.Position);
            foreach (var boneIndex in vertex.Bones) bonePositions[boneIndex].Add(converted.Position);
            vertexCount++;
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
            CountShapeVertices(model), resolutions, neckMoved, neckProblem);
    }

    private sealed record DeformedVertex(int Lod, int Mesh, int Index, MdlVertexElement PositionElement,
        MdlVertexElement NormalElement, MdlVertexElement? TangentElement, MdlVertexElement? FlowElement,
        Vector3 OriginalPosition, DeformableVertex Converted, int[] Bones)
    {
        public DeformableVertex Converted { get; set; } = Converted;
    }

    private static DeformableVertex Deform(DeformableVertex vertex, RacialDeformationPlan plan)
    {
        foreach (var step in plan.Steps)
            vertex = RacialDeformation.Transform(vertex, step.BoneMatrices);
        return vertex;
    }

    /// <summary>Copies of one seam vertex (split by normals or UVs) share its position to this distance (metres).</summary>
    private const float SeamTolerance = 0.0005f;

    /// <summary>How far (metres) a sculpted seam vertex may sit from the place its morph or its race lists.</summary>
    private const float SeamSearch = 0.003f;

    /// <summary>
    /// Moves the deformed neck onto the target race's seam: each seam vertex exactly, with the
    /// target's normal so the shading meets the body's too, and the rest of the neck after it in
    /// proportion to how much it hangs on the neck bones and how close it is to the seam. The seam
    /// is found among the model's own vertices, so where they end up decides the fit, not the
    /// morphs' bone numbers. The model's own neck morphs, when they list that seam, are set to the target's.
    /// </summary>
    private static (bool Fitted, float Moved, string? Problem) FitNeck(MdlFile model, NeckFit neck,
        List<DeformedVertex> vertices)
    {
        if (neck.Target.IsDefaultOrEmpty) return (false, 0, "the target race's neck seam could not be read");
        // The model's own seam, or, for one whose neck morphs another tool dropped or garbled, its race's.
        var own = NeckSeam(model);
        var ownFound = Locate(own, vertices);
        var ownMatches = Matches(ownFound);
        var seam = ownMatches ? own : neck.Source;
        var found = ownMatches ? ownFound : Locate(neck.Source, vertices);
        if (seam.IsDefaultOrEmpty) return (false, 0, "the source race's neck seam could not be read");
        if (seam.Length != neck.Target.Length)
            return (false, 0, $"its neck seam has {seam.Length} vertices and the target race's {neck.Target.Length}");
        if (!Matches(found)) return (false, 0, "its neck does not end where the source race's does");

        var points = Enumerable.Range(0, seam.Length).Where(i => found[i] >= 0).ToArray();
        var converted = points.Select(i => vertices[found[i]].Converted).ToArray();
        var targets = Pair(converted, neck.Target);
        if (targets == null) return (false, 0, "its neck seam does not match the target race's");
        var offsets = converted.Select((v, k) => targets[k].Position - v.Position).ToArray();
        var moved = offsets.Max(o => o.Length());
        // The neck blends into the seam over a few centimetres, more when the seam moves far.
        var reach = MathF.Max(0.03f, 3 * moved);
        var seamOriginals = points.Select(i => vertices[found[i]].OriginalPosition).ToArray();

        foreach (var vertex in vertices)
        {
            var neckWeight = NeckWeight(vertex);
            if (neckWeight <= 0) continue;
            var snapped = Array.FindIndex(seamOriginals, p => Vector3.Distance(p, vertex.OriginalPosition) < SeamTolerance);
            if (snapped >= 0)
            {
                vertex.Converted = vertex.Converted with
                    { Position = targets[snapped].Position, Normal = Vector3.Normalize(targets[snapped].Normal) };
                continue;
            }
            var position = vertex.Converted.Position;
            var distance = converted.Min(v => Vector3.Distance(position, v.Position));
            if (distance >= reach) continue;
            // Inverse-distance blend of the seam's offsets, fading out with distance from the seam.
            var blend = Vector3.Zero;
            var total = 0f;
            for (var k = 0; k < converted.Length; k++)
            {
                var weight = 1 / MathF.Max(1e-12f, Vector3.DistanceSquared(position, converted[k].Position));
                blend += offsets[k] * weight;
                total += weight;
            }
            var fade = 1 - distance / reach;
            fade = fade * fade * (3 - 2 * fade);
            vertex.Converted = vertex.Converted with
                { Position = position + blend / total * (fade * MathF.Min(1, neckWeight)) };
        }

        if (ownMatches)
        {
            var morphs = model.NeckMorphs.ToArray();
            for (var k = 0; k < points.Length; k++)
                morphs[points[k]] = morphs[points[k]] with { Position = targets[k].Position, Normal = targets[k].Normal };
            model.SetNeckMorphs(morphs);
        }
        return (true, moved, null);
    }

    /// <summary>
    /// Moves the model's neck morphs to where their seam vertices went. Morphs that do not list
    /// the seam the vertices make (another tool garbled them) are left as they are.
    /// </summary>
    private static void DeformNeckMorphs(MdlFile model, List<DeformedVertex> vertices)
    {
        var found = Locate(NeckSeam(model), vertices);
        if (!Matches(found)) return;
        model.SetNeckMorphs(model.NeckMorphs.Select((m, i) => found[i] < 0 ? m
            : m with { Position = vertices[found[i]].Converted.Position, Normal = vertices[found[i]].Converted.Normal }).ToList());
    }

    /// <summary>For each seam point, the neck vertex nearest it within <see cref="SeamSearch"/>, or -1.</summary>
    private static int[] Locate(ImmutableArray<NeckSeamPoint> seam, List<DeformedVertex> vertices)
    {
        if (seam.IsDefaultOrEmpty) return [];
        var neck = Enumerable.Range(0, vertices.Count).Where(i => NeckWeight(vertices[i]) > 0).ToArray();
        return seam.Select(point =>
        {
            var best = -1;
            var bestDistance = SeamSearch;
            foreach (var i in neck)
            {
                var distance = Vector3.Distance(vertices[i].OriginalPosition, point.Position);
                if (distance < bestDistance) (best, bestDistance) = (i, distance);
            }
            return best;
        }).ToArray();
    }

    /// <summary>Whether at least half of a seam was found among the vertices.</summary>
    private static bool Matches(int[] found) => found.Length > 0 && found.Count(i => i >= 0) * 2 >= found.Length;

    private static float NeckWeight(DeformedVertex vertex)
        => vertex.Converted.Influences.Where(i => IsNeckBone(i.Bone)).Sum(i => i.Weight);

    private static bool IsNeckBone(string bone) => bone == "j_kubi" || bone.StartsWith("j_sebo_", StringComparison.Ordinal);

    /// <summary>
    /// The target seam vertex for each deformed one: races list their seam in different orders, so
    /// they are paired by the way each faces (its normal), nearest first. Null when they do not
    /// pair up as the same ring.
    /// </summary>
    private static NeckSeamPoint[]? Pair(DeformableVertex[] seam, ImmutableArray<NeckSeamPoint> target)
    {
        var result = new NeckSeamPoint?[seam.Length];
        var taken = new bool[target.Length];
        var candidates =
            from i in Enumerable.Range(0, seam.Length)
            from j in Enumerable.Range(0, target.Length)
            let agreement = Vector3.Dot(Vector3.Normalize(seam[i].Normal), Vector3.Normalize(target[j].Normal))
            orderby agreement descending
            select (i, j, agreement);
        foreach (var (i, j, agreement) in candidates)
        {
            if (result[i] != null || taken[j]) continue;
            if (agreement < 0.7f) return null;
            result[i] = target[j];
            taken[j] = true;
        }
        return result.All(p => p != null) ? result.Select(p => p!).ToArray() : null;
    }

    public static string Hash(ReadOnlySpan<byte> bytes)
        => System.Convert.ToHexString(SHA256.HashData(bytes)).ToLowerInvariant();

    private static int CountShapeVertices(MdlFile model)
    {
        var values = new HashSet<(int Mesh, int Vertex)>();
        foreach (var shapeMesh in model.ShapeMeshes)
        {
            // Meshes with nothing in them share the start index of the next real one, and a LOD's
            // shape meshes can share one with another LOD's. The shape belongs to the mesh at that
            // start that holds the vertices it replaces; this only fills in a statistic, so a shape
            // that fits none is counted against the largest rather than refused.
            var candidates = Enumerable.Range(0, model.Meshes.Length)
                .Where(i => model.Meshes[i].StartIndex == shapeMesh.MeshIndexOffset).ToList();
            if (candidates.Count == 0 || (ulong)shapeMesh.ShapeValueOffset + shapeMesh.ShapeValueCount > (ulong)model.ShapeValues.Length)
                throw new MdlConversionException("malformed_shape_data", "A shape mesh references invalid values or mesh data.");
            var replaced = new List<uint>((int)shapeMesh.ShapeValueCount);
            for (var i = 0u; i < shapeMesh.ShapeValueCount; i++)
                replaced.Add(model.ShapeValues[checked((int)(shapeMesh.ShapeValueOffset + i))].ReplacementVertexIndex);
            var highest = replaced.Count == 0 ? 0u : replaced.Max();
            var meshIndex = candidates.FirstOrDefault(i => model.Meshes[i].VertexCount > highest,
                candidates.OrderByDescending(i => model.Meshes[i].VertexCount).First());
            foreach (var vertex in replaced) values.Add((meshIndex, (int)vertex));
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
