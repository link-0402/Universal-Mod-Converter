using System.Collections.Immutable;
using System.Numerics;
using System.Text;

namespace AdvancedPenumbraModConverter.Core;

public sealed record PbdDeformer(ushort GenderRace, short TreeIndex, float Scale,
    IReadOnlyDictionary<string, Matrix4x4> BoneMatrices);

public sealed record PbdTreeEntry(short ParentIndex, short FirstChildIndex, short NextSiblingIndex, short DeformerIndex);

/// <summary>Focused reader for chara/xls/boneDeformer/human.pbd.</summary>
public sealed class HumanPbd
{
    private readonly ImmutableArray<PbdDeformer> _deformers;
    private readonly ImmutableArray<PbdTreeEntry> _tree;
    private readonly Dictionary<ushort, int> _byRace;

    public HumanPbd(byte[] bytes) : this((bytes ?? throw new ArgumentNullException(nameof(bytes))).AsSpan()) { }

    public HumanPbd(ReadOnlySpan<byte> bytes)
    {
        if (bytes.Length < 4) throw new InvalidDataException("PBD header is truncated.");
        var count = ReadInt32(bytes, 0);
        if (count <= 0 || count > 256 || bytes.Length < 4 + count * 20)
            throw new InvalidDataException("PBD entry count is invalid.");

        var deformers = new PbdDeformer[count];
        var entryOffset = 4;
        for (var i = 0; i < count; i++, entryOffset += 12)
        {
            var race = ReadUInt16(bytes, entryOffset);
            var treeIndex = ReadInt16(bytes, entryOffset + 2);
            var dataOffset = ReadInt32(bytes, entryOffset + 4);
            var scale = ReadSingle(bytes, entryOffset + 8);
            if (race != ushort.MaxValue && (treeIndex < 0 || treeIndex >= count))
                throw new InvalidDataException($"PBD tree index {treeIndex} is invalid.");
            if (race == ushort.MaxValue && !IsOptionalIndex(treeIndex, count))
                throw new InvalidDataException($"Reserved PBD tree index {treeIndex} is invalid.");
            var matrices = race == ushort.MaxValue || dataOffset == 0
                ? new Dictionary<string, Matrix4x4>(StringComparer.Ordinal)
                : ReadRacialDeformer(bytes, dataOffset);
            deformers[i] = new PbdDeformer(race, treeIndex, scale, matrices);
        }

        var treeOffset = 4 + count * 12;
        var tree = new PbdTreeEntry[count];
        for (var i = 0; i < count; i++, treeOffset += 8)
            tree[i] = new PbdTreeEntry(ReadInt16(bytes, treeOffset), ReadInt16(bytes, treeOffset + 2),
                ReadInt16(bytes, treeOffset + 4), ReadInt16(bytes, treeOffset + 6));

        foreach (var entry in tree)
        {
            if (!IsOptionalIndex(entry.ParentIndex, count) ||
                !IsOptionalIndex(entry.FirstChildIndex, count) ||
                !IsOptionalIndex(entry.NextSiblingIndex, count))
                throw new InvalidDataException("PBD tree references an invalid tree entry.");
            if (!IsOptionalIndex(entry.DeformerIndex, count))
                throw new InvalidDataException("PBD tree references an invalid deformer.");
        }
        for (var i = 0; i < count; i++)
            if (deformers[i].GenderRace != ushort.MaxValue && tree[deformers[i].TreeIndex].DeformerIndex != i)
                throw new InvalidDataException("PBD deformer/tree mapping is inconsistent.");

        _deformers = [.. deformers];
        _tree = [.. tree];
        _byRace = new Dictionary<ushort, int>();
        for (var i = 0; i < deformers.Length; i++)
        {
            if (deformers[i].GenderRace == ushort.MaxValue) continue;
            if (!_byRace.TryAdd(deformers[i].GenderRace, i))
                throw new InvalidDataException($"PBD contains duplicate gender/race c{deformers[i].GenderRace:D4}.");
        }
        for (var i = 0; i < count; i++)
            if (deformers[i].GenderRace != ushort.MaxValue) _ = AncestorChain(i);
    }

    public bool Contains(ushort genderRace) => _byRace.ContainsKey(genderRace);

    public ushort? GetParentRace(ushort genderRace)
    {
        if (!_byRace.TryGetValue(genderRace, out var index)) return null;
        var parent = ParentDeformerIndex(index);
        return parent < 0 ? null : _deformers[parent].GenderRace;
    }

    /// <summary>
    /// Builds the ordered racial-deformation route from source model coordinates to
    /// target skeleton coordinates. Each edge remains a separate skinning pass; a
    /// weighted blend of composed per-bone matrices is not equivalent to sequential
    /// skinning when a vertex has more than one influence.
    /// </summary>
    public RacialDeformationPlan BuildPlan(ushort sourceRace, ushort targetRace)
    {
        if (!_byRace.TryGetValue(sourceRace, out var sourceIndex))
            throw new InvalidDataException($"Source gender/race c{sourceRace:D4} is absent from human.pbd.");
        if (!_byRace.TryGetValue(targetRace, out var targetIndex))
            throw new InvalidDataException($"Target gender/race c{targetRace:D4} is absent from human.pbd.");
        if (sourceRace == targetRace) return RacialDeformationPlan.Empty;

        var sourceAncestors = AncestorChain(sourceIndex);
        var targetAncestors = AncestorChain(targetIndex);
        var targetSet = targetAncestors.ToHashSet();
        var common = sourceAncestors.FirstOrDefault(targetSet.Contains, -1);
        if (common < 0) throw new InvalidDataException("PBD source and target do not share a root.");

        var steps = ImmutableArray.CreateBuilder<RacialDeformationStep>();

        // Each node's matrix maps its parent into that node. Going source -> common uses inverses.
        for (var current = sourceIndex; current != common; current = ParentDeformerIndex(current))
            steps.Add(CreateStep(_deformers[current], invert: true));

        // Going common -> target uses forward matrices in ancestor-to-child order.
        var down = new Stack<int>();
        for (var current = targetIndex; current != common; current = ParentDeformerIndex(current)) down.Push(current);
        while (down.Count > 0)
            steps.Add(CreateStep(_deformers[down.Pop()], invert: false));

        return new RacialDeformationPlan(steps.ToImmutable(), []);
    }

    private List<int> AncestorChain(int index)
    {
        var result = new List<int>();
        var seen = new HashSet<int>();
        while (index >= 0)
        {
            if (!seen.Add(index)) throw new InvalidDataException("PBD parent hierarchy contains a cycle.");
            result.Add(index);
            var parent = ParentDeformerIndex(index);
            index = parent;
        }
        return result;
    }

    private int ParentDeformerIndex(int deformerIndex)
    {
        var treeIndex = _deformers[deformerIndex].TreeIndex;
        if (treeIndex < 0) return -1;
        var parentTreeIndex = _tree[treeIndex].ParentIndex;
        return parentTreeIndex < 0 ? -1 : _tree[parentTreeIndex].DeformerIndex;
    }

    private static RacialDeformationStep CreateStep(PbdDeformer deformer, bool invert)
    {
        var matrices = new Dictionary<string, Matrix4x4>(deformer.BoneMatrices.Count, StringComparer.Ordinal);
        foreach (var (bone, sourceMatrix) in deformer.BoneMatrices)
        {
            var matrix = sourceMatrix;
            if (invert && !Matrix4x4.Invert(matrix, out matrix))
                throw new InvalidDataException($"PBD matrix for bone '{bone}' is singular.");
            matrices.Add(bone, matrix);
        }
        return new RacialDeformationStep(deformer.GenderRace, invert,
            matrices.ToImmutableDictionary(StringComparer.Ordinal));
    }

    private static Dictionary<string, Matrix4x4> ReadRacialDeformer(ReadOnlySpan<byte> bytes, int baseOffset)
    {
        if (baseOffset < 0 || baseOffset + 4 > bytes.Length)
            throw new InvalidDataException("PBD deformer offset is out of range.");
        var count = ReadInt32(bytes, baseOffset);
        if (count < 0 || count > 4096) throw new InvalidDataException("PBD bone count is invalid.");
        var offsetsStart = baseOffset + 4;
        // The matrix array is aligned from the beginning of the PBD, not relative to
        // the deformer block. Data offsets currently happen to be aligned in the game
        // file, but using the absolute position also validates synthetic/modded PBDs.
        var matrixStart = (offsetsStart + count * 2 + 3) & ~3;
        if (matrixStart + count * 48 > bytes.Length)
            throw new InvalidDataException("PBD matrix table is truncated.");

        var result = new Dictionary<string, Matrix4x4>(count, StringComparer.Ordinal);
        for (var i = 0; i < count; i++)
        {
            var nameOffset = ReadUInt16(bytes, offsetsStart + i * 2);
            var name = ReadCString(bytes, baseOffset + nameOffset);
            var p = matrixStart + i * 48;
            // PBD stores the first three rows of a column-vector affine matrix:
            //   [ r00 r01 r02 tx ]
            //   [ r10 r11 r12 ty ]
            //   [ r20 r21 r22 tz ]
            // System.Numerics uses row vectors and keeps translation in M41..M43,
            // so transpose the stored transform as it is decoded.
            var matrix = new Matrix4x4(
                ReadSingle(bytes, p),      ReadSingle(bytes, p + 16), ReadSingle(bytes, p + 32), 0,
                ReadSingle(bytes, p + 4),  ReadSingle(bytes, p + 20), ReadSingle(bytes, p + 36), 0,
                ReadSingle(bytes, p + 8),  ReadSingle(bytes, p + 24), ReadSingle(bytes, p + 40), 0,
                ReadSingle(bytes, p + 12), ReadSingle(bytes, p + 28), ReadSingle(bytes, p + 44), 1);
            result.Add(name, matrix);
        }
        return result;
    }

    private static string ReadCString(ReadOnlySpan<byte> bytes, int offset)
    {
        if (offset < 0 || offset >= bytes.Length) throw new InvalidDataException("PBD string offset is invalid.");
        var end = offset;
        while (end < bytes.Length && bytes[end] != 0) end++;
        if (end == bytes.Length) throw new InvalidDataException("PBD string is unterminated.");
        return Encoding.UTF8.GetString(bytes[offset..end]);
    }

    private static short ReadInt16(ReadOnlySpan<byte> b, int o) => BitConverter.ToInt16(b[o..(o + 2)]);
    private static ushort ReadUInt16(ReadOnlySpan<byte> b, int o) => BitConverter.ToUInt16(b[o..(o + 2)]);
    private static int ReadInt32(ReadOnlySpan<byte> b, int o) => BitConverter.ToInt32(b[o..(o + 4)]);
    private static float ReadSingle(ReadOnlySpan<byte> b, int o) => BitConverter.ToSingle(b[o..(o + 4)]);
    private static bool IsOptionalIndex(short index, int count) => index == -1 || index >= 0 && index < count;
}

public enum BoneResolutionStrategy
{
    StepSkeletonAncestor,
    SourceSkeletonAncestor,
    Heuristic,
    Identity,
}

/// <summary>A managed bone-name to parent-name hierarchy copied from one or more SKLB files.</summary>
public sealed record BoneHierarchy
{
    public BoneHierarchy(IReadOnlyDictionary<string, string?> parents)
        => Parents = parents.ToImmutableDictionary(StringComparer.Ordinal);

    public ImmutableDictionary<string, string?> Parents { get; }

    public static BoneHierarchy Empty { get; } = new(
        ImmutableDictionary<string, string?>.Empty.WithComparers(StringComparer.Ordinal));

    /// <summary>
    /// Keeps this hierarchy's entries and fills only missing bones from a fallback
    /// hierarchy. This mirrors TexTools' use of the Midlander skeleton for inherited
    /// base bones without replacing race-specific parent relationships.
    /// </summary>
    public BoneHierarchy MergeMissing(BoneHierarchy fallback)
    {
        ArgumentNullException.ThrowIfNull(fallback);
        var merged = fallback.Parents.ToDictionary(pair => pair.Key, pair => pair.Value, StringComparer.Ordinal);
        foreach (var (bone, parent) in Parents) merged[bone] = parent;
        return new BoneHierarchy(merged);
    }
}

public interface ISkeletonHierarchyReader
{
    BoneHierarchy Read(ReadOnlyMemory<byte> sklbBytes);
}

/// <summary>One parent/child edge in the human.pbd race tree.</summary>
public sealed record RacialDeformationStep(ushort GenderRace, bool Inverse,
    ImmutableDictionary<string, Matrix4x4> BoneMatrices);

/// <summary>How one non-exact weighted bone was resolved for one deformation step.</summary>
public sealed record BoneResolution(string Bone, ushort StepRace, bool Inverse,
    BoneResolutionStrategy Strategy, string? ResolvedBone);

/// <summary>An ordered set of racial-deformation skinning passes.</summary>
public sealed record RacialDeformationPlan(
    ImmutableArray<RacialDeformationStep> Steps,
    ImmutableArray<BoneResolution> BoneResolutions)
{
    public static RacialDeformationPlan Empty { get; } = new([], []);

    public static RacialDeformationPlan Create(HumanPbd pbd, ushort sourceRace, ushort targetRace)
    {
        ArgumentNullException.ThrowIfNull(pbd);
        return pbd.BuildPlan(sourceRace, targetRace);
    }

    /// <summary>
    /// Resolves every requested model bone for every step. Exact PBD transforms win,
    /// followed by the step skeleton, the source model skeleton, TexTools-compatible
    /// naming heuristics, and finally identity.
    /// </summary>
    public RacialDeformationPlan Materialize(IEnumerable<string> bones,
        IReadOnlyDictionary<ushort, BoneHierarchy>? stepSkeletons = null,
        BoneHierarchy? sourceSkeleton = null)
    {
        ArgumentNullException.ThrowIfNull(bones);
        var requested = bones.Distinct(StringComparer.Ordinal).ToArray();
        var resolvedSteps = ImmutableArray.CreateBuilder<RacialDeformationStep>(Steps.Length);
        var resolutions = ImmutableArray.CreateBuilder<BoneResolution>();
        sourceSkeleton ??= BoneHierarchy.Empty;

        foreach (var step in Steps)
        {
            var matrices = new Dictionary<string, Matrix4x4>(step.BoneMatrices, StringComparer.Ordinal);
            var stepSkeleton = stepSkeletons != null && stepSkeletons.TryGetValue(step.GenderRace, out var hierarchy)
                ? hierarchy
                : BoneHierarchy.Empty;

            foreach (var bone in requested)
            {
                if (matrices.ContainsKey(bone)) continue;

                var ancestor = FindDeformedAncestor(bone, stepSkeleton, matrices);
                var strategy = BoneResolutionStrategy.StepSkeletonAncestor;
                if (ancestor == null)
                {
                    ancestor = FindDeformedAncestor(bone, sourceSkeleton, matrices);
                    strategy = BoneResolutionStrategy.SourceSkeletonAncestor;
                }
                if (ancestor == null)
                {
                    ancestor = HeuristicAncestor(bone, matrices);
                    strategy = BoneResolutionStrategy.Heuristic;
                }

                if (ancestor != null)
                    matrices[bone] = matrices[ancestor];
                else
                {
                    matrices[bone] = Matrix4x4.Identity;
                    strategy = BoneResolutionStrategy.Identity;
                }
                resolutions.Add(new BoneResolution(bone, step.GenderRace, step.Inverse, strategy, ancestor));
            }
            resolvedSteps.Add(step with
                { BoneMatrices = matrices.ToImmutableDictionary(StringComparer.Ordinal) });
        }

        return new RacialDeformationPlan(resolvedSteps.ToImmutable(), resolutions.ToImmutable());
    }

    private static string? FindDeformedAncestor(string bone, BoneHierarchy hierarchy,
        IReadOnlyDictionary<string, Matrix4x4> matrices)
    {
        var current = bone;
        var seen = new HashSet<string>(StringComparer.Ordinal);
        while (seen.Add(current) && hierarchy.Parents.TryGetValue(current, out var parent) &&
               !string.IsNullOrEmpty(parent))
        {
            if (matrices.ContainsKey(parent)) return parent;
            current = parent;
        }
        return null;
    }

    private static string? HeuristicAncestor(string bone, IReadOnlyDictionary<string, Matrix4x4> matrices)
    {
        string? fallback = null;
        if (bone.StartsWith("j_ex_h", StringComparison.Ordinal) ||
            bone.StartsWith("j_ex_f", StringComparison.Ordinal))
            fallback = "j_kao";
        else if (bone.Contains("_ex_top_", StringComparison.Ordinal))
            fallback = "j_sebo_b";
        return fallback != null && matrices.ContainsKey(fallback) ? fallback : null;
    }
}

public sealed record VertexInfluence(string Bone, float Weight);

public sealed record DeformableVertex(
    Vector3 Position,
    Vector3 Normal,
    Vector3 Tangent,
    Vector3 Binormal,
    Vector3 Flow,
    ImmutableArray<VertexInfluence> Influences);

public static class RacialDeformation
{
    public static DeformableVertex Transform(DeformableVertex vertex,
        IReadOnlyDictionary<string, Matrix4x4> transforms,
        IReadOnlyDictionary<string, string>? skeletonParents = null)
    {
        if (vertex.Influences.IsDefaultOrEmpty) return vertex;
        var position = Vector3.Zero;
        var normal = Vector3.Zero;
        var tangent = Vector3.Zero;
        var binormal = Vector3.Zero;
        var flow = Vector3.Zero;
        var total = 0f;

        foreach (var influence in vertex.Influences.Where(i => i.Weight > 0))
        {
            var matrix = Resolve(influence.Bone, transforms, skeletonParents);
            if (!Matrix4x4.Invert(matrix, out var inverse))
                throw new InvalidDataException($"Deformation matrix for '{influence.Bone}' is singular.");
            var normalMatrix = Matrix4x4.Transpose(inverse);
            position += Vector3.Transform(vertex.Position, matrix) * influence.Weight;
            normal += Vector3.TransformNormal(vertex.Normal, normalMatrix) * influence.Weight;
            tangent += Vector3.TransformNormal(vertex.Tangent, matrix) * influence.Weight;
            binormal += Vector3.TransformNormal(vertex.Binormal, matrix) * influence.Weight;
            flow += Vector3.TransformNormal(vertex.Flow, matrix) * influence.Weight;
            total += influence.Weight;
        }

        if (total <= 0) return vertex;
        return vertex with
        {
            // TexTools applies the byte weights exactly as stored (weight / 255)
            // and does not renormalize the position after blending.
            Position = position,
            Normal = NormalizeOrZero(normal),
            Tangent = NormalizeOrZero(tangent),
            Binormal = NormalizeOrZero(binormal),
            Flow = NormalizeOrZero(flow),
        };
    }

    private static Matrix4x4 Resolve(string bone, IReadOnlyDictionary<string, Matrix4x4> transforms,
        IReadOnlyDictionary<string, string>? parents)
    {
        var current = bone;
        var seen = new HashSet<string>(StringComparer.Ordinal);
        while (seen.Add(current))
        {
            if (transforms.TryGetValue(current, out var matrix)) return matrix;
            if (parents == null || !parents.TryGetValue(current, out current!)) break;
        }

        if ((bone.StartsWith("j_ex_h", StringComparison.Ordinal) ||
             bone.StartsWith("j_ex_f", StringComparison.Ordinal)) &&
            transforms.TryGetValue("j_kao", out var face))
            return face;
        return Matrix4x4.Identity;
    }

    private static Vector3 NormalizeOrZero(Vector3 value)
        => value.LengthSquared() <= float.Epsilon ? Vector3.Zero : Vector3.Normalize(value);
}
