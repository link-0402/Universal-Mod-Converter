using System.Collections.Immutable;
using System.Numerics;

namespace UniversalModConverter.Core;

/// <summary>A bone's local transform: translation, rotation and scale, applied scale first.</summary>
public sealed record BoneTransform(Vector3 Position, Quaternion Rotation, Vector3 Scale)
{
    public static BoneTransform Identity { get; } = new(Vector3.Zero, Quaternion.Identity, Vector3.One);

    public Matrix4x4 ToMatrix()
        => Matrix4x4.CreateScale(Scale) * Matrix4x4.CreateFromQuaternion(Rotation) * Matrix4x4.CreateTranslation(Position);

    public bool IsFinite
        => float.IsFinite(Position.LengthSquared()) && float.IsFinite(Scale.LengthSquared()) &&
           float.IsFinite(Rotation.LengthSquared()) && Math.Abs(Rotation.LengthSquared() - 1) <= 0.01f;

    public bool Near(BoneTransform other, float tolerance)
        => Vector3.Distance(Position, other.Position) <= tolerance && Vector3.Distance(Scale, other.Scale) <= tolerance &&
           1 - Math.Abs(Quaternion.Dot(Quaternion.Normalize(Rotation), Quaternion.Normalize(other.Rotation))) <= tolerance;
}

public sealed record SkeletonBone(string Name, short Parent, BoneTransform Reference);

public sealed record SkeletonPartition(string Name, short Start, short Count);

/// <summary>A Havok skeleton as plain managed data: bones in parent-first order, float slots and partitions.</summary>
public sealed record SkeletonDescription(string Name, ImmutableArray<SkeletonBone> Bones,
    ImmutableArray<string> FloatNames, ImmutableArray<float> ReferenceFloats, ImmutableArray<SkeletonPartition> Partitions)
{
    public void Validate()
    {
        if (Bones.IsDefault || FloatNames.IsDefault || ReferenceFloats.IsDefault || Partitions.IsDefault ||
            Bones.Length is < 1 or > 4096 || FloatNames.Length > 4096 || FloatNames.Length != ReferenceFloats.Length ||
            Partitions.Length > 4096)
            throw new InvalidDataException("The skeleton's channel counts are invalid.");
        var names = new HashSet<string>(StringComparer.Ordinal);
        for (var i = 0; i < Bones.Length; i++)
        {
            var bone = Bones[i];
            if (string.IsNullOrEmpty(bone.Name) || !names.Add(bone.Name) || bone.Parent < -1 || bone.Parent >= i)
                throw new InvalidDataException("The skeleton's bone names or hierarchy are ambiguous.");
            if (!bone.Reference.IsFinite) throw new InvalidDataException($"Bone '{bone.Name}' has an invalid reference pose.");
        }
        if (ReferenceFloats.Any(f => !float.IsFinite(f)) ||
            Partitions.Any(p => p.Start < 0 || p.Count < 0 || p.Start + p.Count > Bones.Length))
            throw new InvalidDataException("The skeleton's float slots or partitions are invalid.");
    }

    public int IndexOf(string bone)
    {
        for (var i = 0; i < Bones.Length; i++)
            if (Bones[i].Name == bone) return i;
        return -1;
    }
}

/// <summary>
/// Moves a pose from one skeleton onto another with the same bone names but different
/// proportions (another race). Bones are matched by exact name.
/// <list type="bullet">
/// <item>Rotation and scale are transferred relative to each skeleton's reference pose, so a
/// bone at rest stays at the target's rest.</item>
/// <item>Translation offsets from the rest pose are scaled by the ratio of the target's bone
/// length to the source's (the skeleton-wide ratio for bones without length, such as the
/// root), which is what rescales motion like hip sway and jumps for a smaller race.</item>
/// <item>Bones the target does not have are dropped; their motion is folded into their
/// children, which are measured from their nearest shared ancestor. Target bones the source
/// does not have stay at their reference pose.</item>
/// </list>
/// </summary>
public sealed class SkeletonRetarget
{
    private const float Epsilon = 1e-4f;

    private readonly SkeletonDescription _source;
    private readonly SkeletonDescription _target;
    private readonly int[] _sourceAncestors;
    private readonly int[] _targetAncestors;
    private readonly BoneTransform[] _sourceRest;
    private readonly BoneTransform[] _targetRest;
    private readonly Matrix4x4[] _targetHelpers;
    private readonly float[] _translationScale;
    private readonly BoneTransform[] _sourceReferences;
    private readonly BoneTransform[] _targetReferences;

    /// <param name="floatSlots">Source float slot of each float track of the animation.</param>
    /// <param name="partitions">Source partitions the animation binding uses.</param>
    public SkeletonRetarget(SkeletonDescription source, SkeletonDescription target,
        IReadOnlyList<short> floatSlots, IReadOnlyList<short> partitions)
    {
        source.Validate();
        target.Validate();
        _source = source;
        _target = target;

        var targetNames = new Dictionary<string, int>(StringComparer.Ordinal);
        for (var i = 0; i < target.Bones.Length; i++) targetNames[target.Bones[i].Name] = i;
        BoneMap = source.Bones.Select(b => targetNames.GetValueOrDefault(b.Name, -1)).ToArray();
        FloatMap = floatSlots.Select(slot => Unique(target.FloatNames, SourceName(source.FloatNames, slot), "float channel")).ToArray();
        PartitionMap = partitions.Select(p => Unique(target.Partitions.Select(t => t.Name).ToImmutableArray(),
            SourceName(source.Partitions.Select(s => s.Name).ToImmutableArray(), p), "partition")).ToArray();

        var count = source.Bones.Length;
        _sourceAncestors = new int[count];
        _targetAncestors = new int[count];
        _sourceRest = new BoneTransform[count];
        _targetRest = new BoneTransform[count];
        _targetHelpers = new Matrix4x4[count];
        _translationScale = new float[count];
        _sourceReferences = source.Bones.Select(b => b.Reference).ToArray();
        _targetReferences = target.Bones.Select(b => b.Reference).ToArray();

        var shared = BoneMap.Where(i => i >= 0).ToHashSet();
        var globalScale = GlobalScale(source, target, BoneMap);
        for (var i = 0; i < count; i++)
        {
            if (BoneMap[i] < 0) continue;
            var a = source.Bones[i].Parent;
            while (a >= 0 && BoneMap[a] < 0) a = source.Bones[a].Parent;
            var b = target.Bones[BoneMap[i]].Parent;
            while (b >= 0 && !shared.Contains(b)) b = target.Bones[b].Parent;
            _sourceAncestors[i] = a;
            _targetAncestors[i] = b;
            _sourceRest[i] = Collapse(source, _sourceReferences, i, a);
            _targetRest[i] = Collapse(target, _targetReferences, BoneMap[i], b);

            var sourceLength = _sourceRest[i].Position.Length();
            var targetLength = _targetRest[i].Position.Length();
            _translationScale[i] = sourceLength > Epsilon && targetLength > Epsilon ? targetLength / sourceLength : globalScale;

            // Target bones between this bone and its shared ancestor stay at rest, so their
            // combined reference transform is divided out of the retargeted result.
            var helper = Matrix4x4.Identity;
            for (var p = target.Bones[BoneMap[i]].Parent; p != b; p = target.Bones[p].Parent)
                helper *= _targetReferences[p].ToMatrix();
            if (!Matrix4x4.Invert(helper, out _targetHelpers[i]))
                throw new InvalidDataException($"A target bone above '{source.Bones[i].Name}' has a singular scale.");
        }

        for (var p = 0; p < partitions.Count; p++)
        {
            var from = source.Partitions[partitions[p]];
            var to = target.Partitions[PartitionMap[p]];
            for (var i = from.Start; i < from.Start + from.Count; i++)
                if (BoneMap[i] >= 0 && (BoneMap[i] < to.Start || BoneMap[i] >= to.Start + to.Count))
                    throw new InvalidDataException($"Partition '{from.Name}' contains different bones on the target skeleton.");
        }
    }

    /// <summary>Target bone index of each source bone, or -1 when the target has no such bone.</summary>
    public int[] BoneMap { get; }

    /// <summary>Target float slot of each float track.</summary>
    public short[] FloatMap { get; }

    /// <summary>Target partition of each binding partition.</summary>
    public short[] PartitionMap { get; }

    /// <summary>Names of source bones that moved at some point but do not exist on the target.</summary>
    public SortedSet<string> DroppedBones { get; } = new(StringComparer.Ordinal);

    /// <summary>Maps the source bones of an animation's transform tracks to the target, dropping missing bones.</summary>
    public short[] MapTracks(IEnumerable<short> tracks)
    {
        var result = new List<short>();
        foreach (var sourceBone in tracks)
        {
            if (sourceBone < 0 || sourceBone >= BoneMap.Length)
                throw new InvalidDataException("An animation track is outside its source skeleton.");
            var targetBone = BoneMap[sourceBone];
            if (targetBone >= 0 && !result.Contains((short)targetBone)) result.Add(checked((short)targetBone));
        }
        return result.ToArray();
    }

    /// <summary>Maps one complete local-space source pose to a complete local-space target pose.</summary>
    public BoneTransform[] Map(IReadOnlyList<BoneTransform> values)
    {
        if (values.Count != _source.Bones.Length) throw new InvalidDataException("The source pose does not fit the source skeleton.");
        for (var i = 0; i < values.Count; i++)
            if (BoneMap[i] < 0 && !values[i].Near(_sourceReferences[i], 1e-6f))
                DroppedBones.Add(_source.Bones[i].Name);

        var result = _targetReferences.ToArray();
        var sourceForTarget = Enumerable.Repeat(-1, _target.Bones.Length).ToArray();
        for (var i = 0; i < BoneMap.Length; i++)
            if (BoneMap[i] >= 0) sourceForTarget[BoneMap[i]] = i;

        // Target order guarantees that a reparented bone's target parent is final before its
        // global transform is made local.
        for (var dest = 0; dest < sourceForTarget.Length; dest++)
        {
            var i = sourceForTarget[dest];
            if (i < 0) continue;
            var sample = Collapse(_source, values, i, _sourceAncestors[i]);
            if (sample.Near(_sourceRest[i], 1e-6f)) continue;

            var mappedAncestor = _sourceAncestors[i] < 0 ? -1 : BoneMap[_sourceAncestors[i]];
            if (mappedAncestor != _targetAncestors[i])
            {
                // The bone hangs off a different ancestor on the target: transfer in model space.
                var sourceGlobal = Collapse(_source, values, i, -1);
                var sourceGlobalRest = Collapse(_source, _sourceReferences, i, -1);
                var targetGlobalRest = Collapse(_target, _targetReferences, dest, -1);
                var targetGlobal = Transfer(sourceGlobal, sourceGlobalRest, targetGlobalRest, _translationScale[i]);
                var parent = _target.Bones[dest].Parent;
                if (parent < 0)
                {
                    result[dest] = targetGlobal;
                    continue;
                }
                var parentGlobal = Collapse(_target, result, parent, -1);
                if (!Matrix4x4.Invert(parentGlobal.ToMatrix(), out var inverseParent))
                    throw new InvalidDataException($"Bone '{_source.Bones[i].Name}' has a singular parent on the target skeleton.");
                result[dest] = Decompose(targetGlobal.ToMatrix() * inverseParent);
                continue;
            }

            var retargeted = Transfer(sample, _sourceRest[i], _targetRest[i], _translationScale[i]);
            result[dest] = Decompose(retargeted.ToMatrix() * _targetHelpers[i]);
        }
        return result;
    }

    /// <summary>
    /// Applies the source's offset from its rest pose to the target's rest pose. Rotation is
    /// the rest-relative rotation in the bone's own frame; translation offsets are scaled.
    /// </summary>
    public static BoneTransform Transfer(BoneTransform value, BoneTransform sourceRest, BoneTransform targetRest,
        float translationScale)
    {
        if (Math.Abs(sourceRest.Scale.X * sourceRest.Scale.Y * sourceRest.Scale.Z) < 1e-12f)
            throw new InvalidDataException("Cannot retarget from a reference pose with zero scale.");
        return new BoneTransform(
            targetRest.Position + (value.Position - sourceRest.Position) * translationScale,
            Quaternion.Normalize(targetRest.Rotation * Quaternion.Inverse(sourceRest.Rotation) * value.Rotation),
            targetRest.Scale * (value.Scale / sourceRest.Scale));
    }

    /// <summary>Encodes a complete pose for an additive binding (Havok blend hint 1 or 2).</summary>
    public static BoneTransform Encode(BoneTransform pose, BoneTransform reference, sbyte blendHint)
    {
        if (blendHint == 0) return pose;
        if (blendHint is not (1 or 2)) throw new InvalidDataException($"Unsupported animation blend hint {blendHint}.");
        if (Math.Abs(reference.Scale.X * reference.Scale.Y * reference.Scale.Z) < 1e-12f)
            throw new InvalidDataException("Cannot encode an additive animation against a reference pose with zero scale.");
        var inverse = Quaternion.Inverse(reference.Rotation);
        return new BoneTransform(pose.Position - reference.Position,
            Quaternion.Normalize(blendHint == 2 ? inverse * pose.Rotation : pose.Rotation * inverse),
            pose.Scale / reference.Scale);
    }

    public static float MapFloat(float value, float sourceReference, float targetReference, sbyte blendHint)
        => blendHint == 0 ? value : value - sourceReference + targetReference;

    /// <summary>Average model-space distance ratio of the shared bones: how much larger the target is.</summary>
    private static float GlobalScale(SkeletonDescription source, SkeletonDescription target, int[] map)
    {
        var sourceModel = ModelPositions(source);
        var targetModel = ModelPositions(target);
        double sourceSum = 0, targetSum = 0;
        for (var i = 0; i < map.Length; i++)
        {
            if (map[i] < 0) continue;
            sourceSum += sourceModel[i].Length();
            targetSum += targetModel[map[i]].Length();
        }
        return sourceSum > Epsilon && targetSum > Epsilon ? (float)(targetSum / sourceSum) : 1f;
    }

    private static Vector3[] ModelPositions(SkeletonDescription skeleton)
    {
        var matrices = new Matrix4x4[skeleton.Bones.Length];
        for (var i = 0; i < matrices.Length; i++)
        {
            var local = skeleton.Bones[i].Reference.ToMatrix();
            matrices[i] = skeleton.Bones[i].Parent < 0 ? local : local * matrices[skeleton.Bones[i].Parent];
        }
        return matrices.Select(m => m.Translation).ToArray();
    }

    private static string SourceName(ImmutableArray<string> names, short index)
        => index >= 0 && index < names.Length ? names[index] : throw new InvalidDataException("An animation channel is outside its source skeleton.");

    private static short Unique(ImmutableArray<string> names, string name, string kind)
    {
        var matches = names.Select((n, i) => (n, i)).Where(v => v.n == name && name.Length > 0).ToArray();
        if (matches.Length != 1) throw new InvalidDataException($"The target skeleton has no unique {kind} '{name}'.");
        return checked((short)matches[0].i);
    }

    /// <summary>The transform of <paramref name="bone"/> relative to <paramref name="ancestor"/> (-1: model space).</summary>
    private static BoneTransform Collapse(SkeletonDescription skeleton, IReadOnlyList<BoneTransform> values, int bone, int ancestor)
    {
        var matrix = values[bone].ToMatrix();
        for (var p = skeleton.Bones[bone].Parent; p != ancestor; p = skeleton.Bones[p].Parent)
            matrix *= values[p].ToMatrix();
        return Decompose(matrix);
    }

    private static BoneTransform Decompose(Matrix4x4 m)
    {
        if (!Matrix4x4.Decompose(m, out var scale, out var rotation, out var position))
            throw new InvalidDataException("Retargeting produced a transform that cannot be decomposed.");
        var value = new BoneTransform(position, Quaternion.Normalize(rotation), scale);
        var rebuilt = value.ToMatrix();
        var error = Math.Abs(m.M11 - rebuilt.M11) + Math.Abs(m.M12 - rebuilt.M12) + Math.Abs(m.M13 - rebuilt.M13) +
                    Math.Abs(m.M21 - rebuilt.M21) + Math.Abs(m.M22 - rebuilt.M22) + Math.Abs(m.M23 - rebuilt.M23) +
                    Math.Abs(m.M31 - rebuilt.M31) + Math.Abs(m.M32 - rebuilt.M32) + Math.Abs(m.M33 - rebuilt.M33);
        if (error > 0.0001f)
            throw new InvalidDataException("Retargeting would need shear, which Havok transforms cannot store.");
        return value;
    }
}
