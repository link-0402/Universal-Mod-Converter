// Havok load/save and animation construction adapted from XIV Instant Edit's animation editor,
// which adapted them from VFXEditor (GPL-3.0). See THIRD_PARTY_NOTICES.md.
using System;
using System.Collections.Generic;
using System.Collections.Immutable;
using System.IO;
using System.Linq;
using System.Numerics;
using System.Runtime.InteropServices;
using System.Security.Cryptography;
using System.Text;
using AdvancedPenumbraModConverter.Core;
using Dalamud.Plugin.Services;
using FFXIVClientStructs.Havok.Animation;
using FFXIVClientStructs.Havok.Animation.Animation;
using FFXIVClientStructs.Havok.Animation.Playback;
using FFXIVClientStructs.Havok.Animation.Playback.Control;
using FFXIVClientStructs.Havok.Animation.Rig;
using FFXIVClientStructs.Havok.Common.Base.Container.Array;
using FFXIVClientStructs.Havok.Common.Base.Container.String;
using FFXIVClientStructs.Havok.Common.Base.Math.QsTransform;
using FFXIVClientStructs.Havok.Common.Base.System.IO.OStream;
using FFXIVClientStructs.Havok.Common.Base.Object;
using FFXIVClientStructs.Havok.Common.Base.Types;
using FFXIVClientStructs.Havok.Common.Serialize.Resource;
using FFXIVClientStructs.Havok.Common.Serialize.Util;

namespace AdvancedPenumbraModConverter.Services.Animations;

/// <summary>
/// The game's Havok runtime, used to decode, build and serialize animations. Every member
/// that touches native memory must run on the framework thread.
/// </summary>
internal sealed unsafe class HavokAnimation
{
    private readonly nint _interleavedVtbl;
    private readonly delegate* unmanaged<Spline*, Interleaved*, Spline*> _compress;

    public HavokAnimation(ISigScanner scanner)
    {
        try
        {
            var relative = scanner.ScanText("48 89 07 48 8B CD 48 89 77 38") - 4;
            _interleavedVtbl = relative + 4 + Marshal.ReadInt32(relative);
            if (_interleavedVtbl == 0) throw new InvalidOperationException("The interleaved animation type was not found.");
        }
        catch (Exception ex)
        {
            UnavailableReason = "This game version is not supported by animation retargeting: " + ex.Message;
        }

        try
        {
            _compress = (delegate* unmanaged<Spline*, Interleaved*, Spline*>)scanner.ScanText(
                "48 89 5C 24 ?? 57 48 83 EC 40 48 8B DA 48 8B F9 E8 ?? ?? ?? ?? 48 8D 05 ?? ?? ?? ??");
        }
        catch (Exception)
        {
            _compress = null;
        }
    }

    /// <summary>Why animations cannot be built in this game version, or null.</summary>
    public string? UnavailableReason { get; }

    /// <summary>Whether the game's spline compressor was found. Without it output stays uncompressed.</summary>
    public bool CanCompress => _compress != null;

    // ── Native layouts ──────────────────────────────────────────────────────

    [StructLayout(LayoutKind.Sequential)]
    internal struct Interleaved
    {
        public hkaAnimation Animation;
        public hkArray<hkQsTransformf> Transforms;
        public hkArray<float> Floats;
    }

    [StructLayout(LayoutKind.Sequential)]
    internal struct Spline
    {
        public hkaAnimation Animation;
        public int NumFrames, NumBlocks, MaxFramesPerBlock, MaskAndQuantizationSize;
        public float BlockDuration, BlockInverseDuration, FrameDuration;
        public uint Padding;
        public hkArray<uint> BlockOffsets, FloatBlockOffsets, TransformOffsets, FloatOffsets;
        public hkArray<byte> Data;
        public int Endian;
        public uint Padding2;
    }

    // Havok 2013 hkaQuantizedAnimation: the complete compressed stream follows the common
    // hkaAnimation base; the skeleton pointer is runtime-only.
    [StructLayout(LayoutKind.Sequential)]
    internal struct Quantized
    {
        public hkaAnimation Animation;
        public hkArray<byte> Data;
        public uint Endian;
        public uint Padding;
        public hkaSkeleton* Skeleton;
    }

    [StructLayout(LayoutKind.Sequential, Pack = 1)]
    internal struct QuantizedHeader
    {
        public ushort HeaderSize, NumBones, NumFloats, NumFrames;
        public float Duration;
    }

    // hkaPredictiveCompressedAnimation (Havok 2013, x64). The skeleton is runtime-only.
    [StructLayout(LayoutKind.Explicit, Size = 0xC0)]
    internal struct Predictive
    {
        [FieldOffset(0x00)] public hkaAnimation Animation;
        [FieldOffset(0x9C)] public int NumBones;
        [FieldOffset(0xA0)] public int NumFloatSlots;
        [FieldOffset(0xA4)] public int NumFrames;
        [FieldOffset(0xB0)] public hkaSkeleton* Skeleton;
    }

    // hkaSkeletonMapper: hkReferencedObject (0x10), then hkaSkeletonMapperData's two
    // skeleton references. Only the references are read.
    [StructLayout(LayoutKind.Explicit, Size = 0x20)]
    private struct MapperSkeletons
    {
        [FieldOffset(0x10)] public hkaSkeleton* A;
        [FieldOffset(0x18)] public hkaSkeleton* B;
    }

    // ── Memory ──────────────────────────────────────────────────────────────

    /// <summary>Plugin-owned native memory, freed together. Havok must never release it.</summary>
    internal sealed class Arena : IDisposable
    {
        private readonly List<nint> _allocations = [];

        public T* Alloc<T>(int count = 1) where T : unmanaged
        {
            if (count < 0 || (long)count * sizeof(T) > PapFile.MaxFileSize) throw new InvalidDataException("Native allocation limit exceeded.");
            var size = (nuint)Math.Max(16, checked(count * sizeof(T)));
            size = (size + 15) & ~(nuint)15;
            var pointer = NativeMemory.AlignedAlloc(size, 16);
            if (pointer == null) throw new OutOfMemoryException();
            NativeMemory.Clear(pointer, size);
            _allocations.Add((nint)pointer);
            return (T*)pointer;
        }

        public hkArray<T> Array<T>(int count) where T : unmanaged
            => new() { Data = Alloc<T>(count), Length = count, CapacityAndFlags = unchecked((int)0x80000000) | count };

        public hkArray<T> Copy<T>(ReadOnlySpan<T> values) where T : unmanaged
        {
            var array = Array<T>(values.Length);
            values.CopyTo(new Span<T>(array.Data, array.Length));
            return array;
        }

        public hkStringPtr String(string text)
            => new() { StringAndFlag = Copy<byte>(Encoding.UTF8.GetBytes(text + "\0")).Data };

        /// <summary>
        /// A plugin-owned copy of a binding. Havok keeps the reference count in the low 16 bits
        /// and the allocation size in the high 16 bits of <c>MemSizeAndRefCount</c>; zero turns
        /// reference management off, so a control's release can never free arena memory.
        /// </summary>
        public hkaAnimationBinding* CopyBinding(hkaAnimationBinding* source)
        {
            if (source == null) throw new InvalidDataException("Missing animation binding.");
            var binding = Alloc<hkaAnimationBinding>();
            *binding = *source;
            binding->MemSizeAndRefCount = 0;
            return binding;
        }

        public void Dispose()
        {
            foreach (var allocation in _allocations) NativeMemory.AlignedFree((void*)allocation);
            _allocations.Clear();
        }
    }

    /// <summary>A loaded Havok resource. The resource owns the object graph.</summary>
    internal sealed class Document : IDisposable
    {
        private hkResource* _resource;
        private GCHandle _input;

        public Document(byte[] bytes)
        {
            var registry = hkBuiltinTypeRegistry.Instance();
            if (registry == null) throw new InvalidOperationException("The Havok type registry is unavailable.");
            var options = new hkSerializeUtil.LoadOptions
            {
                TypeInfoRegistry = registry->GetTypeInfoRegistry(),
                ClassNameRegistry = registry->GetClassNameRegistry(),
            };
            // Loaders may keep pointers into the input, so it stays pinned for the resource's lifetime.
            _input = GCHandle.Alloc(bytes, GCHandleType.Pinned);
            try
            {
                _resource = hkSerializeUtil.LoadFromBuffer((byte*)_input.AddrOfPinnedObject(), bytes.Length, null, &options);
                if (_resource == null) throw new InvalidDataException("Havok could not load the resource.");
                Root = (hkRootLevelContainer*)_resource->GetContentsPointer("hkRootLevelContainer", registry->GetTypeInfoRegistry());
                if (Root == null) throw new InvalidDataException("The Havok resource has no root container.");
                // The skeleton reader finds the container by type; VFXEditor finds it by name.
                Container = (hkaAnimationContainer*)Root->findObjectByType("hkaAnimationContainer", null);
                if (Container == null) Container = (hkaAnimationContainer*)Root->findObjectByName("hkaAnimationContainer", null);
                if (Container == null) throw new InvalidDataException("The Havok resource has no animation container.");
                ValidateArray(Container->Bindings, 4096, "animation bindings");
                ValidateArray(Container->Animations, 4096, "animations");
                ValidateArray(Container->Skeletons, 16, "skeletons");
            }
            catch
            {
                Dispose();
                throw;
            }
        }

        public hkRootLevelContainer* Root { get; private set; }

        public hkaAnimationContainer* Container { get; private set; }

        public byte[] Save()
        {
            var path = Path.Combine(Path.GetTempPath(), $"apmc-{Guid.NewGuid():N}.hkx");
            try
            {
                SaveObject(Root, "hkRootLevelContainer", path);
                return File.ReadAllBytes(path);
            }
            finally
            {
                if (File.Exists(path)) File.Delete(path);
            }
        }

        private static void SaveObject(void* value, string type, string path)
        {
            var registry = hkBuiltinTypeRegistry.Instance();
            var klass = registry->GetClassNameRegistry()->GetClassByName(type);
            if (klass == null) throw new InvalidOperationException($"The Havok type {type} is unavailable.");
            hkOstream stream = default;
            stream.Ctor(path);
            try
            {
                if (stream.StreamWriter.ptr == null) throw new IOException("Could not open the Havok output file.");
                hkResult result = default;
                hkSerializeUtil.Save(&result, value, klass, stream.StreamWriter.ptr, new hkSerializeUtil.SaveOptions());
                if (result.Result != hkResult.hkResultEnum.Success) throw new IOException("Havok serialization failed.");
            }
            finally
            {
                stream.Dtor();
            }
        }

        public void Dispose()
        {
            try
            {
                if (_resource != null) ((hkReferencedObject*)_resource)->RemoveReference();
            }
            finally
            {
                _resource = null;
                Root = null;
                Container = null;
                if (_input.IsAllocated) _input.Free();
            }
        }
    }

    /// <summary>Samples one binding on one skeleton into a local-space pose and float values.</summary>
    internal sealed class Sampler : IDisposable
    {
        private readonly Arena _arena = new();
        private hkaAnimatedSkeleton* _animated;
        private hkaAnimationControl* _control;
        private bool _animatedConstructed, _controlConstructed, _controlAdded;

        public Sampler(hkaSkeleton* skeleton, hkaAnimationBinding* binding)
        {
            Skeleton = skeleton;
            Validate(skeleton, binding);
            try
            {
                _animated = _arena.Alloc<hkaAnimatedSkeleton>();
                _animated->Ctor1(skeleton);
                _animatedConstructed = true;
                _control = _arena.Alloc<hkaAnimationControl>();
                _control->Ctor1(binding);
                _controlConstructed = true;
                _control->Weight = 1;
                _animated->addAnimationControl(_control);
                _controlAdded = true;
                Transforms = _arena.Alloc<hkQsTransformf>(BoneCount);
                Floats = _arena.Alloc<float>(Math.Max(1, skeleton->FloatSlots.Length));
            }
            catch
            {
                Dispose();
                throw;
            }
        }

        public hkaSkeleton* Skeleton { get; }

        public hkQsTransformf* Transforms { get; }

        public float* Floats { get; }

        public int BoneCount => Skeleton->Bones.Length;

        public int FloatCount => Skeleton->FloatSlots.Length;

        public void Sample(float time)
        {
            _control->LocalTime = time;
            // Predictive and quantized channels read the skeleton's reference pose through a
            // runtime pointer that loaded files do not set. Borrow it only for this call.
            var animation = _control->Binding.ptr->Animation.ptr;
            var predictive = animation->Type == hkaAnimation.AnimationType.PredictiveCompressedAnimation ? (Predictive*)animation : null;
            var quantized = animation->Type == hkaAnimation.AnimationType.QuantizedCompressedAnimation ? (Quantized*)animation : null;
            var previousPredictive = predictive == null ? null : predictive->Skeleton;
            var previousQuantized = quantized == null ? null : quantized->Skeleton;
            try
            {
                if (predictive != null) predictive->Skeleton = Skeleton;
                if (quantized != null) quantized->Skeleton = Skeleton;
                _animated->sampleAndCombineAnimations(Transforms, Floats);
            }
            finally
            {
                if (predictive != null) predictive->Skeleton = previousPredictive;
                if (quantized != null) quantized->Skeleton = previousQuantized;
            }
        }

        public static void Validate(hkaSkeleton* skeleton, hkaAnimationBinding* binding)
        {
            if (skeleton == null || binding == null || binding->Animation.ptr == null ||
                skeleton->Bones.Length is < 1 or > 4096 || skeleton->ParentIndices.Length != skeleton->Bones.Length ||
                skeleton->ReferencePose.Length != skeleton->Bones.Length ||
                skeleton->ReferenceFloats.Length != skeleton->FloatSlots.Length)
                throw new InvalidDataException("The animation skeleton is invalid.");
            var animation = binding->Animation.ptr;
            ValidateCounts(animation);
            if (binding->TransformTrackToBoneIndices.Length != animation->NumberOfTransformTracks ||
                binding->FloatTrackToFloatSlotIndices.Length != animation->NumberOfFloatTracks)
                throw new InvalidDataException("Animations without explicit track bindings are not supported.");
            if (animation->Type == hkaAnimation.AnimationType.PredictiveCompressedAnimation &&
                (((Predictive*)animation)->NumBones != skeleton->Bones.Length ||
                 ((Predictive*)animation)->NumFloatSlots != skeleton->FloatSlots.Length))
                throw new InvalidDataException(
                    $"The animation was compressed for a skeleton with {((Predictive*)animation)->NumBones} bones, " +
                    $"but this skeleton has {skeleton->Bones.Length}. It was made for a modified skeleton; " +
                    "include that skeleton in the mod to retarget it.");
            if (animation->Type == hkaAnimation.AnimationType.QuantizedCompressedAnimation)
            {
                var header = (QuantizedHeader*)((Quantized*)animation)->Data.Data;
                if (header->NumBones != skeleton->Bones.Length || header->NumFloats != skeleton->FloatSlots.Length)
                    throw new InvalidDataException(
                        $"The animation was compressed for a skeleton with {header->NumBones} bones, but this skeleton has " +
                        $"{skeleton->Bones.Length}. It was made for a modified skeleton; include that skeleton in the mod to retarget it.");
            }

            var bound = new HashSet<short>();
            for (var i = 0; i < binding->TransformTrackToBoneIndices.Length; i++)
                if (binding->TransformTrackToBoneIndices[i] < 0 || binding->TransformTrackToBoneIndices[i] >= skeleton->Bones.Length ||
                    !bound.Add(binding->TransformTrackToBoneIndices[i]))
                    throw new InvalidDataException("An animation track does not fit the skeleton; the animation was made for a different one.");
            var floats = new HashSet<short>();
            for (var i = 0; i < binding->FloatTrackToFloatSlotIndices.Length; i++)
                if (binding->FloatTrackToFloatSlotIndices[i] < 0 || binding->FloatTrackToFloatSlotIndices[i] >= skeleton->FloatSlots.Length ||
                    !floats.Add(binding->FloatTrackToFloatSlotIndices[i]))
                    throw new InvalidDataException("An animation float track does not fit the skeleton.");
            ValidateArray(binding->PartitionIndices, 4096, "binding partitions");
            for (var i = 0; i < binding->PartitionIndices.Length; i++)
                if (binding->PartitionIndices[i] < 0 || binding->PartitionIndices[i] >= skeleton->Partitions.Length)
                    throw new InvalidDataException("An animation partition does not fit the skeleton.");
        }

        public void Dispose()
        {
            if (_controlAdded) _animated->removeAnimationControl(_control);
            if (_animatedConstructed) _animated->Dtor();
            if (_controlConstructed) _control->VirtDtor(0);
            _animatedConstructed = _controlConstructed = _controlAdded = false;
            _animated = null;
            _control = null;
            _arena.Dispose();
        }
    }

    // ── Skeletons ───────────────────────────────────────────────────────────

    /// <summary>Every distinct skeleton in an SKLB: its main skeletons and both ends of its skeleton mappers.</summary>
    public static List<SkeletonDescription> DescribeSkeletons(Document document)
    {
        var result = new List<SkeletonDescription>();
        var seen = new HashSet<string>(StringComparer.Ordinal);
        void Add(hkaSkeleton* skeleton)
        {
            if (skeleton == null) return;
            try
            {
                var description = Describe(skeleton);
                if (seen.Add(Fingerprint(description))) result.Add(description);
            }
            catch (InvalidDataException)
            {
                // A broken secondary skeleton must not hide valid ones.
            }
        }

        for (var i = 0; i < document.Container->Skeletons.Length; i++) Add(document.Container->Skeletons[i].ptr);
        ValidateArray(document.Root->NamedVariants, 256, "named variants");
        for (var i = 0; i < document.Root->NamedVariants.Length; i++)
        {
            var variant = document.Root->NamedVariants[i];
            if (variant.ClassName.String != "hkaSkeletonMapper" || variant.Variant.ptr == null) continue;
            var mapper = (MapperSkeletons*)variant.Variant.ptr;
            Add(mapper->A);
            Add(mapper->B);
        }
        if (result.Count == 0) throw new InvalidDataException("The skeleton file contains no valid skeleton.");
        return result;
    }

    public static SkeletonDescription Describe(hkaSkeleton* skeleton)
    {
        ValidateArray(skeleton->Bones, 4096, "bones");
        ValidateArray(skeleton->ParentIndices, 4096, "bone parents");
        ValidateArray(skeleton->ReferencePose, 4096, "reference pose");
        ValidateArray(skeleton->FloatSlots, 4096, "float slots");
        ValidateArray(skeleton->ReferenceFloats, 4096, "reference floats");
        ValidateArray(skeleton->Partitions, 4096, "partitions");
        if (skeleton->ParentIndices.Length != skeleton->Bones.Length || skeleton->ReferencePose.Length != skeleton->Bones.Length)
            throw new InvalidDataException("The skeleton's bone arrays disagree.");

        var bones = ImmutableArray.CreateBuilder<SkeletonBone>(skeleton->Bones.Length);
        for (var i = 0; i < skeleton->Bones.Length; i++)
            bones.Add(new SkeletonBone(skeleton->Bones[i].Name.String ?? string.Empty, skeleton->ParentIndices[i],
                Transform(skeleton->ReferencePose[i])));
        var floats = ImmutableArray.CreateBuilder<string>(skeleton->FloatSlots.Length);
        for (var i = 0; i < skeleton->FloatSlots.Length; i++) floats.Add(skeleton->FloatSlots[i].String ?? string.Empty);
        var partitions = ImmutableArray.CreateBuilder<SkeletonPartition>(skeleton->Partitions.Length);
        for (var i = 0; i < skeleton->Partitions.Length; i++)
            partitions.Add(new SkeletonPartition(skeleton->Partitions[i].Name.String ?? string.Empty,
                skeleton->Partitions[i].StartBoneIndex, skeleton->Partitions[i].NumBones));
        var description = new SkeletonDescription(skeleton->Name.String ?? string.Empty, bones.MoveToImmutable(),
            floats.MoveToImmutable(),
            new ReadOnlySpan<float>(skeleton->ReferenceFloats.Data, skeleton->ReferenceFloats.Length).ToArray().ToImmutableArray(),
            partitions.MoveToImmutable());
        description.Validate();
        return description;
    }

    /// <summary>Builds a native skeleton from a description, owned by <paramref name="arena"/>.</summary>
    public static hkaSkeleton* Materialize(SkeletonDescription description, Arena arena)
    {
        description.Validate();
        var skeleton = arena.Alloc<hkaSkeleton>();
        skeleton->Name = arena.String(description.Name);
        skeleton->Bones = arena.Array<hkaBone>(description.Bones.Length);
        skeleton->ParentIndices = arena.Array<short>(description.Bones.Length);
        skeleton->ReferencePose = arena.Array<hkQsTransformf>(description.Bones.Length);
        for (var i = 0; i < description.Bones.Length; i++)
        {
            skeleton->Bones.Data[i].Name = arena.String(description.Bones[i].Name);
            skeleton->ParentIndices[i] = description.Bones[i].Parent;
            skeleton->ReferencePose[i] = Transform(description.Bones[i].Reference);
        }
        skeleton->FloatSlots = arena.Array<hkStringPtr>(description.FloatNames.Length);
        for (var i = 0; i < description.FloatNames.Length; i++) skeleton->FloatSlots[i] = arena.String(description.FloatNames[i]);
        skeleton->ReferenceFloats = arena.Copy<float>(description.ReferenceFloats.AsSpan());
        skeleton->Partitions = arena.Array<hkaSkeleton.Partition>(description.Partitions.Length);
        for (var i = 0; i < description.Partitions.Length; i++)
        {
            skeleton->Partitions.Data[i].Name = arena.String(description.Partitions[i].Name);
            skeleton->Partitions.Data[i].StartBoneIndex = description.Partitions[i].Start;
            skeleton->Partitions.Data[i].NumBones = description.Partitions[i].Count;
        }
        return skeleton;
    }

    public static string Fingerprint(SkeletonDescription skeleton)
    {
        using var hash = IncrementalHash.CreateHash(HashAlgorithmName.SHA256);
        foreach (var bone in skeleton.Bones)
        {
            hash.AppendData(Encoding.UTF8.GetBytes(bone.Name + "\0"));
            hash.AppendData(BitConverter.GetBytes(bone.Parent));
            var r = bone.Reference;
            foreach (var value in new[] { r.Position.X, r.Position.Y, r.Position.Z, r.Rotation.X, r.Rotation.Y, r.Rotation.Z,
                         r.Rotation.W, r.Scale.X, r.Scale.Y, r.Scale.Z })
                hash.AppendData(BitConverter.GetBytes(value));
        }
        foreach (var name in skeleton.FloatNames) hash.AppendData(Encoding.UTF8.GetBytes(name + "\0"));
        return Convert.ToHexString(hash.GetHashAndReset());
    }

    // ── Animations ──────────────────────────────────────────────────────────

    /// <summary>A content hash of a binding and its animation data, to prove other animations were not changed.</summary>
    public static string Fingerprint(hkaAnimationBinding* binding)
    {
        if (binding == null || binding->Animation.ptr == null) throw new InvalidDataException("Missing animation binding.");
        var animation = binding->Animation.ptr;
        ValidateCounts(animation);
        using var hash = IncrementalHash.CreateHash(HashAlgorithmName.SHA256);
        hash.AppendData(BitConverter.GetBytes(animation->Duration));
        hash.AppendData(BitConverter.GetBytes((int)animation->Type));
        hash.AppendData(BitConverter.GetBytes(animation->NumberOfTransformTracks));
        hash.AppendData(BitConverter.GetBytes(animation->NumberOfFloatTracks));
        hash.AppendData(Encoding.UTF8.GetBytes(binding->OriginalSkeletonName.String ?? string.Empty));
        hash.AppendData([(byte)binding->BlendHint.Storage]);
        Append(binding->TransformTrackToBoneIndices);
        Append(binding->FloatTrackToFloatSlotIndices);
        Append(binding->PartitionIndices);
        switch (animation->Type)
        {
            case hkaAnimation.AnimationType.SplineCompressedAnimation:
                Append(((Spline*)animation)->Data);
                break;
            case hkaAnimation.AnimationType.InterleavedAnimation:
                Append(((Interleaved*)animation)->Transforms);
                Append(((Interleaved*)animation)->Floats);
                break;
            case hkaAnimation.AnimationType.QuantizedCompressedAnimation:
                Append(((Quantized*)animation)->Data);
                break;
        }
        return Convert.ToHexString(hash.GetHashAndReset());

        void Append<T>(hkArray<T> array) where T : unmanaged
        {
            if (array.Length < 0 || (long)array.Length * sizeof(T) > PapFile.MaxFileSize || (array.Length > 0 && array.Data == null))
                throw new InvalidDataException("Invalid animation array.");
            hash.AppendData(new ReadOnlySpan<byte>(array.Data, checked(array.Length * sizeof(T))));
        }
    }

    internal static void ValidateCounts(hkaAnimation* animation)
    {
        if (animation == null) throw new InvalidDataException("Missing animation.");
        if (!float.IsFinite(animation->Duration) || animation->Duration < 0 || animation->Duration > 3600 ||
            animation->NumberOfTransformTracks is < 0 or > 4096 || animation->NumberOfFloatTracks is < 0 or > 4096)
            throw new InvalidDataException("The animation's track counts or duration are invalid.");
        switch (animation->Type)
        {
            case hkaAnimation.AnimationType.InterleavedAnimation:
            {
                var value = (Interleaved*)animation;
                ValidateArray(value->Transforms, PapFile.MaxFileSize / sizeof(hkQsTransformf), "interleaved transforms");
                ValidateArray(value->Floats, PapFile.MaxFileSize / sizeof(float), "interleaved floats");
                if (animation->NumberOfTransformTracks > 0 && value->Transforms.Length % animation->NumberOfTransformTracks != 0 ||
                    animation->NumberOfFloatTracks > 0 && value->Floats.Length % animation->NumberOfFloatTracks != 0)
                    throw new InvalidDataException("The interleaved animation's frame counts disagree.");
                break;
            }
            case hkaAnimation.AnimationType.SplineCompressedAnimation:
            {
                var value = (Spline*)animation;
                ValidateArray(value->Data, PapFile.MaxFileSize, "spline data");
                if (value->NumFrames is < 1 or > 216001 || value->NumBlocks is < 1 or > 216001)
                    throw new InvalidDataException("The spline animation's frame data is invalid.");
                break;
            }
            case hkaAnimation.AnimationType.PredictiveCompressedAnimation:
            {
                var value = (Predictive*)animation;
                if (value->NumFrames is < 1 or > 216001 || value->NumBones is < 0 or > 4096 || value->NumFloatSlots is < 0 or > 4096)
                    throw new InvalidDataException("The predictive animation's frame data is invalid.");
                break;
            }
            case hkaAnimation.AnimationType.QuantizedCompressedAnimation:
            {
                var value = (Quantized*)animation;
                ValidateArray(value->Data, PapFile.MaxFileSize, "quantized data");
                if (value->Data.Length < sizeof(QuantizedHeader)) throw new InvalidDataException("The quantized animation header is missing.");
                var header = (QuantizedHeader*)value->Data.Data;
                if (header->NumBones > 4096 || header->NumFloats > 4096 || header->NumFrames < 2)
                    throw new InvalidDataException("The quantized animation's frame data is invalid.");
                break;
            }
            default:
                throw new InvalidDataException($"Unsupported animation encoding: {animation->Type}.");
        }
    }

    internal static int SourceFrameCount(hkaAnimation* animation)
    {
        ValidateCounts(animation);
        return animation->Type switch
        {
            hkaAnimation.AnimationType.SplineCompressedAnimation => ((Spline*)animation)->NumFrames,
            hkaAnimation.AnimationType.PredictiveCompressedAnimation => ((Predictive*)animation)->NumFrames,
            hkaAnimation.AnimationType.QuantizedCompressedAnimation => ((QuantizedHeader*)((Quantized*)animation)->Data.Data)->NumFrames,
            _ => Math.Max(((Interleaved*)animation)->Transforms.Length / Math.Max(1, animation->NumberOfTransformTracks),
                ((Interleaved*)animation)->Floats.Length / Math.Max(1, animation->NumberOfFloatTracks)),
        };
    }

    /// <summary>
    /// Evenly spaced samples that include every original frame: each source interval is
    /// divided into a whole number of steps of at most 1/30 s.
    /// </summary>
    internal static int SampleCount(float duration, int sourceFrames)
    {
        if (!float.IsFinite(duration) || duration < 0 || duration > 3600)
            throw new InvalidDataException("The animation's duration is invalid or exceeds one hour.");
        var intervals = Math.Max(1, sourceFrames - 1);
        var subdivision = Math.Max(1, checked((int)Math.Ceiling(duration * 30d / intervals)));
        var count = checked(intervals * subdivision + 1);
        if (count > 216001) throw new InvalidDataException("The animation has too many frames.");
        return count;
    }

    /// <summary>Makes <paramref name="animation"/> an arena-owned interleaved copy of <paramref name="original"/>'s header.</summary>
    public void InitializeInterleaved(Interleaved* animation, hkaAnimation* original)
    {
        if (UnavailableReason != null) throw new InvalidOperationException(UnavailableReason);
        animation->Animation = *original;
        *(nint*)animation = _interleavedVtbl;
        animation->Animation.MemSizeAndRefCount = 0;
        animation->Animation.Type = hkaAnimation.AnimationType.InterleavedAnimation;
    }

    /// <summary>Compresses an interleaved animation with the game's spline compressor.</summary>
    public hkaAnimation* Compress(Arena arena, Interleaved* animation)
    {
        if (_compress == null) throw new InvalidOperationException("The spline compressor is unavailable.");
        var spline = arena.Alloc<Spline>();
        if (_compress(spline, animation) != spline || *(nint*)spline == 0)
            throw new InvalidDataException("The spline compressor did not produce an animation.");
        return (hkaAnimation*)spline;
    }

    // ── Conversions ─────────────────────────────────────────────────────────

    public static BoneTransform Transform(hkQsTransformf t)
        => new(new Vector3(t.Translation.X, t.Translation.Y, t.Translation.Z),
            new Quaternion(t.Rotation.X, t.Rotation.Y, t.Rotation.Z, t.Rotation.W),
            new Vector3(t.Scale.X, t.Scale.Y, t.Scale.Z));

    public static hkQsTransformf Transform(BoneTransform t)
    {
        hkQsTransformf value = default;
        value.Translation.X = t.Position.X; value.Translation.Y = t.Position.Y; value.Translation.Z = t.Position.Z;
        value.Rotation.X = t.Rotation.X; value.Rotation.Y = t.Rotation.Y; value.Rotation.Z = t.Rotation.Z; value.Rotation.W = t.Rotation.W;
        value.Scale.X = t.Scale.X; value.Scale.Y = t.Scale.Y; value.Scale.Z = t.Scale.Z;
        return value;
    }

    internal static void ValidateArray<T>(hkArray<T> array, int maximum, string name) where T : unmanaged
    {
        if (array.Length < 0 || array.Length > maximum || array.Length > 0 && array.Data == null)
            throw new InvalidDataException($"The Havok {name} array is invalid.");
    }
}
