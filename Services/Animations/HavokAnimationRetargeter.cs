using System;
using System.Collections.Generic;
using System.Collections.Immutable;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Numerics;
using AdvancedPenumbraModConverter.Core;
using Dalamud.Plugin.Services;
using FFXIVClientStructs.Havok.Animation.Animation;
using FFXIVClientStructs.Havok.Animation.Rig;
using FFXIVClientStructs.Havok.Common.Base.Math.QsTransform;
using FFXIVClientStructs.Havok.Common.Base.Types;

namespace AdvancedPenumbraModConverter.Services.Animations;

/// <summary>
/// Retargets a PAP with the game's own Havok runtime: every body animation is sampled on the
/// skeleton it was made for, moved onto the target skeleton with <see cref="SkeletonRetarget"/>,
/// rebuilt, compressed like the original, serialized, and then decoded again and compared
/// frame by frame. Facial animations bind to face skeletons and are kept unchanged.
/// <para>
/// Called from a background thread. Native work runs on the framework thread in batches of a
/// few milliseconds so the game keeps running.
/// </para>
/// </summary>
internal sealed class HavokAnimationRetargeter(HavokAnimation havok, IFramework framework) : IAnimationRetargeter
{
    public string? UnavailableReason => havok.UnavailableReason;

    public RetargetedPap Retarget(byte[] papBytes, byte[] sourceSkeleton, byte[] targetSkeleton, ushort targetRace)
    {
        if (framework.IsInFrameworkUpdateThread)
            throw new InvalidOperationException("Animations cannot be retargeted on the framework thread.");
        if (UnavailableReason is { } reason) throw new InvalidOperationException(reason);

        var pap = new PapFile(papBytes);
        var sourceHavok = SklbEnvelope.ExtractHavok(sourceSkeleton);
        var targetHavok = SklbEnvelope.ExtractHavok(targetSkeleton);
        var job = Run(() => new Job(havok, pap, sourceHavok, targetHavok));
        try
        {
            Run(job.CheckRoundTrip);
            while (!Tick(job.SampleBatch)) { }

            byte[] saved;
            var compress = havok.CanCompress;
            while (true)
            {
                saved = Run(() => job.Build(compress));
                Run(() => job.BeginValidation(saved));
                try
                {
                    while (!Tick(job.ValidateBatch)) { }
                    break;
                }
                catch (InvalidDataException ex) when (compress)
                {
                    // Spline compression is lossy; if it strays too far, keep the exact frames.
                    job.Notes.Add($"Compression was not accurate enough ({ex.Message}); the animation is stored uncompressed.");
                    compress = false;
                }
            }
            if (!havok.CanCompress && job.HadCompressedSource)
                job.Notes.Add("The game's animation compressor was not found; the animation is stored uncompressed and is larger.");

            var output = new PapFile(pap.ReplaceHavok(saved)).WithModel(targetRace, pap.ModelType);
            return new RetargetedPap(output, [.. job.Notes.Concat(job.DroppedBoneNotes())]);
        }
        finally
        {
            Run(() =>
            {
                job.Dispose();
                return true;
            });
        }
    }

    private T Run<T>(Func<T> action) => framework.RunOnFrameworkThread(action).GetAwaiter().GetResult();

    private void Run(Action action) => framework.RunOnFrameworkThread(action).GetAwaiter().GetResult();

    private bool Tick(Func<bool> batch) => framework.RunOnTick(batch, delayTicks: 1).GetAwaiter().GetResult();

    /// <summary>All native state of one retarget. Every member runs on the framework thread.</summary>
    private sealed unsafe class Job : IDisposable
    {
        private const long BatchMilliseconds = 3;

        private readonly HavokAnimation _havok;
        private readonly HavokAnimation.Arena _arena = new();
        private readonly HavokAnimation.Document _document;
        private readonly HavokAnimation.Document _sourceDocument;
        private readonly HavokAnimation.Document _targetDocument;
        private readonly SkeletonDescription _source;
        private readonly SkeletonDescription _target;
        private readonly hkaSkeleton* _sourceSkeleton;
        private readonly hkaSkeleton* _targetSkeleton;
        private readonly List<Clip> _clips = [];
        private readonly string[] _originalPrints;
        private HavokAnimation.Document? _verification;
        private int _clip;

        public Job(HavokAnimation havok, PapFile pap, byte[] sourceHavok, byte[] targetHavok)
        {
            _havok = havok;
            try
            {
                _document = new HavokAnimation.Document(pap.Havok);
                _sourceDocument = new HavokAnimation.Document(sourceHavok);
                _targetDocument = new HavokAnimation.Document(targetHavok);
                var container = _document.Container;
                _originalPrints = Enumerable.Range(0, container->Bindings.Length)
                    .Select(i => HavokAnimation.Fingerprint(container->Bindings[i].ptr)).ToArray();

                var bindings = pap.BodyEntries.Select(e => (int)e.Entry.Binding).Distinct().Order().ToList();
                if (bindings.Count == 0) throw new InvalidDataException("The file has no body animation.");
                foreach (var index in bindings)
                    if (index >= container->Bindings.Length || index >= container->Animations.Length ||
                        container->Animations[index].ptr != container->Bindings[index].ptr->Animation.ptr)
                        throw new InvalidDataException("The file's animation list and binding list disagree.");
                if (pap.Entries.Any(e => !e.IsBody))
                    Notes.Add($"{pap.Entries.Count(e => !e.IsBody)} facial animation(s) are kept unchanged; they use the face skeleton.");

                // The skeleton that fits every body binding: SKLBs can carry several (mapper
                // ends), and compressed animations only decode on the exact one they were made for.
                var candidates = HavokAnimation.DescribeSkeletons(_sourceDocument);
                string? misfit = null;
                foreach (var candidate in candidates)
                {
                    misfit = bindings.Select(i => Misfit(candidate, container->Bindings[i].ptr)).FirstOrDefault(m => m != null);
                    if (misfit != null) continue;
                    _source = candidate;
                    break;
                }
                if (_source == null) throw new InvalidDataException(misfit ?? "No source skeleton fits the animation.");
                _target = HavokAnimation.DescribeSkeletons(_targetDocument)[0];
                _sourceSkeleton = HavokAnimation.Materialize(_source, _arena);
                _targetSkeleton = HavokAnimation.Materialize(_target, _arena);

                foreach (var index in bindings)
                    _clips.Add(new Clip(this, index, container->Bindings[index].ptr));
            }
            catch
            {
                Dispose();
                throw;
            }
        }

        public List<string> Notes { get; } = [];

        public bool HadCompressedSource => _clips.Any(c => c.Compressed);

        public IEnumerable<string> DroppedBoneNotes()
        {
            var dropped = _clips.SelectMany(c => c.Retarget.DroppedBones).ToHashSet(StringComparer.Ordinal);
            if (dropped.Count > 0)
                yield return $"The target skeleton has no {string.Join(", ", dropped.Order(StringComparer.Ordinal))}; " +
                             "their motion is carried by the bones below them where possible.";
        }

        /// <summary>Why a skeleton cannot play a binding, or null when it can.</summary>
        private static string? Misfit(SkeletonDescription skeleton, hkaAnimationBinding* binding)
        {
            var animation = binding->Animation.ptr;
            HavokAnimation.ValidateCounts(animation);
            for (var i = 0; i < binding->TransformTrackToBoneIndices.Length; i++)
                if (binding->TransformTrackToBoneIndices[i] < 0 || binding->TransformTrackToBoneIndices[i] >= skeleton.Bones.Length)
                    return "The animation uses bones the skeleton does not have; it was made for a different or modified skeleton. " +
                           "Include that skeleton in the mod to retarget it.";
            for (var i = 0; i < binding->FloatTrackToFloatSlotIndices.Length; i++)
                if (binding->FloatTrackToFloatSlotIndices[i] < 0 || binding->FloatTrackToFloatSlotIndices[i] >= skeleton.FloatNames.Length)
                    return "The animation uses float channels the skeleton does not have.";
            var bones = animation->Type switch
            {
                hkaAnimation.AnimationType.PredictiveCompressedAnimation => ((HavokAnimation.Predictive*)animation)->NumBones,
                hkaAnimation.AnimationType.QuantizedCompressedAnimation =>
                    ((HavokAnimation.QuantizedHeader*)((HavokAnimation.Quantized*)animation)->Data.Data)->NumBones,
                _ => -1,
            };
            return bones >= 0 && bones != skeleton.Bones.Length
                ? $"The animation was compressed for a skeleton with {bones} bones, but the skeleton has {skeleton.Bones.Length}. " +
                  "It was made for a modified skeleton; include that skeleton in the mod to retarget it."
                : null;
        }

        /// <summary>Saving the unchanged file must reproduce every animation exactly, or the output could not be trusted.</summary>
        public void CheckRoundTrip()
        {
            using var check = new HavokAnimation.Document(_document.Save());
            if (check.Container->Bindings.Length != _originalPrints.Length)
                throw new InvalidDataException("Saving the unchanged animation changed its animation count.");
            for (var i = 0; i < _originalPrints.Length; i++)
                if (HavokAnimation.Fingerprint(check.Container->Bindings[i].ptr) != _originalPrints[i])
                    throw new InvalidDataException("Saving the unchanged animation changed it; it cannot be rebuilt safely.");
        }

        public bool SampleBatch()
        {
            var watch = Stopwatch.StartNew();
            while (_clip < _clips.Count && watch.ElapsedMilliseconds < BatchMilliseconds)
            {
                if (_clips[_clip].SampleNext()) continue;
                _clip++;
            }
            return _clip >= _clips.Count;
        }

        public byte[] Build(bool compress)
        {
            foreach (var clip in _clips) clip.Build(compress);
            return _document.Save();
        }

        public void BeginValidation(byte[] saved)
        {
            _verification?.Dispose();
            _verification = new HavokAnimation.Document(saved);
            var container = _verification.Container;
            if (container->Bindings.Length != _originalPrints.Length)
                throw new InvalidDataException("Rebuilding changed the number of animations.");
            var rebuilt = _clips.Select(c => c.Index).ToHashSet();
            for (var i = 0; i < _originalPrints.Length; i++)
                if (!rebuilt.Contains(i) && HavokAnimation.Fingerprint(container->Bindings[i].ptr) != _originalPrints[i])
                    throw new InvalidDataException("Rebuilding changed a facial animation.");
            foreach (var clip in _clips) clip.BeginValidation(container->Bindings[clip.Index].ptr);
            _clip = 0;
        }

        public bool ValidateBatch()
        {
            var watch = Stopwatch.StartNew();
            while (_clip < _clips.Count && watch.ElapsedMilliseconds < BatchMilliseconds)
            {
                if (_clips[_clip].ValidateNext()) continue;
                _clip++;
            }
            return _clip >= _clips.Count;
        }

        public void Dispose()
        {
            foreach (var clip in _clips) clip.Dispose();
            _clips.Clear();
            _verification?.Dispose();
            _verification = null;
            _document?.Dispose();
            _sourceDocument?.Dispose();
            _targetDocument?.Dispose();
            _arena.Dispose();
        }

        /// <summary>One body animation: its samples on the source skeleton and its rebuilt replacement.</summary>
        private sealed class Clip : IDisposable
        {
            private readonly Job _job;
            private readonly hkaAnimationBinding* _binding;
            private readonly hkaAnimation* _animation;
            private readonly sbyte _blendHint;
            private readonly short[] _tracks;
            private readonly short[] _floatSlots;
            private readonly int _frames;
            private readonly float _duration;
            private readonly List<BoneTransform[]> _expected = [];
            private readonly List<float[]> _expectedFloats = [];
            private readonly List<float[]> _rawFloats = [];
            private readonly HashSet<int> _affected = [];
            private HavokAnimation.Sampler? _sampler, _rawSampler, _verifier;
            private hkaAnimation* _compressed;
            private bool _replaced, _outputCompressed;
            private int _frame;

            public Clip(Job job, int index, hkaAnimationBinding* binding)
            {
                _job = job;
                Index = index;
                _binding = binding;
                _animation = binding->Animation.ptr;
                _blendHint = binding->BlendHint.Storage;
                _tracks = new ReadOnlySpan<short>(binding->TransformTrackToBoneIndices.Data, binding->TransformTrackToBoneIndices.Length).ToArray();
                _floatSlots = new ReadOnlySpan<short>(binding->FloatTrackToFloatSlotIndices.Data, binding->FloatTrackToFloatSlotIndices.Length).ToArray();
                var partitions = new ReadOnlySpan<short>(binding->PartitionIndices.Data, binding->PartitionIndices.Length).ToArray();
                Retarget = new SkeletonRetarget(job._source, job._target, _floatSlots, partitions);
                Compressed = _animation->Type != hkaAnimation.AnimationType.InterleavedAnimation;
                _duration = _animation->Duration;
                _frames = HavokAnimation.SampleCount(_duration, HavokAnimation.SourceFrameCount(_animation));
                var perFrame = (long)job._target.Bones.Length * sizeof(hkQsTransformf) + (job._target.FloatNames.Length + _floatSlots.Length) * 4L;
                if (_frames * perFrame > PapFile.MaxFileSize) throw new InvalidDataException("The animation is too long to rebuild.");

                _sampler = new HavokAnimation.Sampler(job._sourceSkeleton, binding);
                // Additive animations sample onto the reference pose; their float tracks are
                // stored raw, so they are also read without blending.
                var raw = job._arena.CopyBinding(binding);
                raw->BlendHint.Storage = 0;
                _rawSampler = new HavokAnimation.Sampler(job._sourceSkeleton, raw);
            }

            public int Index { get; }

            public bool Compressed { get; }

            public SkeletonRetarget Retarget { get; }

            private float Time(int frame) => frame == _frames - 1 ? _duration : _duration * frame / Math.Max(1, _frames - 1);

            /// <summary>Samples the next frame; false when every frame is done.</summary>
            public bool SampleNext()
            {
                if (_frame >= _frames) return false;
                var time = Time(_frame);
                _sampler!.Sample(time);
                _rawSampler!.Sample(time);
                var source = new BoneTransform[_sampler.BoneCount];
                for (var i = 0; i < source.Length; i++) source[i] = HavokAnimation.Transform(_sampler.Transforms[i]);
                var pose = Retarget.Map(source);
                var target = _job._target;
                for (var i = 0; i < pose.Length; i++)
                {
                    if (!pose[i].IsFinite) throw new InvalidDataException($"Retargeting produced an invalid pose for {target.Bones[i].Name}.");
                    // Keep quaternions on one hemisphere so interpolation takes the short way.
                    if (_frame > 0 && Quaternion.Dot(_expected[^1][i].Rotation, pose[i].Rotation) < 0)
                        pose[i] = pose[i] with { Rotation = Quaternion.Negate(pose[i].Rotation) };
                    if (!pose[i].Near(target.Bones[i].Reference, 1e-6f)) _affected.Add(i);
                }
                _expected.Add(pose);

                var floats = target.ReferenceFloats.ToArray();
                for (var t = 0; t < _floatSlots.Length; t++)
                    floats[Retarget.FloatMap[t]] = SkeletonRetarget.MapFloat(_sampler.Floats[_floatSlots[t]],
                        _job._source.ReferenceFloats[_floatSlots[t]], target.ReferenceFloats[Retarget.FloatMap[t]], _blendHint);
                _expectedFloats.Add(floats);
                _rawFloats.Add(_floatSlots.Select(slot => _rawSampler.Floats[slot]).ToArray());
                _frame++;
                return true;
            }

            public void Build(bool compress)
            {
                Restore();
                var target = _job._target;
                var tracks = Retarget.MapTracks(_tracks).ToList();
                tracks.AddRange(_affected.Order().Where(i => !tracks.Contains((short)i)).Select(i => (short)i));
                if (tracks.Count == 0) tracks.Add(0);

                var arena = _job._arena;
                var interleaved = arena.Alloc<HavokAnimation.Interleaved>();
                _job._havok.InitializeInterleaved(interleaved, _animation);
                interleaved->Animation.Duration = _duration;
                interleaved->Animation.NumberOfTransformTracks = tracks.Count;
                interleaved->Animation.NumberOfFloatTracks = _floatSlots.Length;
                interleaved->Transforms = arena.Array<hkQsTransformf>(checked(_frames * tracks.Count));
                interleaved->Floats = arena.Array<float>(checked(_frames * _floatSlots.Length));
                for (var f = 0; f < _frames; f++)
                {
                    for (var t = 0; t < tracks.Count; t++)
                        interleaved->Transforms[f * tracks.Count + t] = HavokAnimation.Transform(
                            SkeletonRetarget.Encode(_expected[f][tracks[t]], target.Bones[tracks[t]].Reference, _blendHint));
                    for (var t = 0; t < _floatSlots.Length; t++)
                        interleaved->Floats[f * _floatSlots.Length + t] = _rawFloats[f][t];
                }

                var binding = arena.CopyBinding(_binding);
                binding->OriginalSkeletonName = arena.String(target.Name);
                binding->TransformTrackToBoneIndices = arena.Copy<short>(tracks.ToArray());
                binding->FloatTrackToFloatSlotIndices = arena.Copy<short>(Retarget.FloatMap);
                binding->PartitionIndices = arena.Copy<short>(Retarget.PartitionMap);

                hkaAnimation* final = &interleaved->Animation;
                _outputCompressed = compress && Compressed;
                if (_outputCompressed)
                {
                    // Compress only the tracks; the original's annotations and root motion are
                    // attached afterwards and stay owned by the loaded file.
                    interleaved->Animation.ExtractedMotion = default;
                    interleaved->Animation.AnnotationTracks = default;
                    final = _compressed = _job._havok.Compress(arena, interleaved);
                }
                final->ExtractedMotion = _animation->ExtractedMotion;
                final->AnnotationTracks = _animation->AnnotationTracks;
                binding->Animation = new hkRefPtr<hkaAnimation> { ptr = final };

                var container = _job._document.Container;
                container->Animations[Index] = new hkRefPtr<hkaAnimation> { ptr = final };
                container->Bindings[Index] = new hkRefPtr<hkaAnimationBinding> { ptr = binding };
                _replaced = true;
            }

            public void BeginValidation(hkaAnimationBinding* rebuilt)
            {
                if (rebuilt->Animation.ptr->Duration != _duration || rebuilt->BlendHint.Storage != _blendHint ||
                    rebuilt->Animation.ptr->NumberOfFloatTracks != _floatSlots.Length ||
                    rebuilt->Animation.ptr->AnnotationTracks.Length != _animation->AnnotationTracks.Length)
                    throw new InvalidDataException("Rebuilding changed the animation's length, blending or annotations.");
                _verifier?.Dispose();
                _verifier = new HavokAnimation.Sampler(_job._targetSkeleton, rebuilt);
                _frame = 0;
            }

            /// <summary>Decodes the next frame of the rebuilt animation and compares it; false when done.</summary>
            public bool ValidateNext()
            {
                if (_frame >= _frames) return false;
                _verifier!.Sample(Time(_frame));
                var positionTolerance = _outputCompressed ? 0.01f : 0.002f;
                var rotationTolerance = _outputCompressed ? 0.001f : 0.0002f;
                var target = _job._target;
                for (var i = 0; i < _verifier.BoneCount; i++)
                {
                    var actual = HavokAnimation.Transform(_verifier.Transforms[i]);
                    var expected = _expected[_frame][i];
                    if (Vector3.Distance(actual.Position, expected.Position) > positionTolerance ||
                        Vector3.Distance(actual.Scale, expected.Scale) > positionTolerance ||
                        1 - Math.Abs(Quaternion.Dot(Quaternion.Normalize(actual.Rotation), Quaternion.Normalize(expected.Rotation))) > rotationTolerance)
                        throw new InvalidDataException($"frame {_frame}, bone {target.Bones[i].Name} differs after rebuilding");
                }
                for (var i = 0; i < _verifier.FloatCount; i++)
                    if (!float.IsFinite(_verifier.Floats[i]) || Math.Abs(_verifier.Floats[i] - _expectedFloats[_frame][i]) > positionTolerance)
                        throw new InvalidDataException($"frame {_frame}, float channel {target.FloatNames[i]} differs after rebuilding");
                _frame++;
                return true;
            }

            /// <summary>Puts the original animation back so the loaded file owns only its own objects.</summary>
            private void Restore()
            {
                _verifier?.Dispose();
                _verifier = null;
                if (_replaced)
                {
                    var container = _job._document.Container;
                    container->Animations[Index] = new hkRefPtr<hkaAnimation> { ptr = _animation };
                    container->Bindings[Index] = new hkRefPtr<hkaAnimationBinding> { ptr = _binding };
                    _replaced = false;
                }
                if (_compressed != null)
                {
                    // These point into the original file, which keeps ownership.
                    _compressed->ExtractedMotion = default;
                    _compressed->AnnotationTracks = default;
                    _compressed->VirtDtor(0);
                    _compressed = null;
                }
            }

            public void Dispose()
            {
                Restore();
                _rawSampler?.Dispose();
                _rawSampler = null;
                _sampler?.Dispose();
                _sampler = null;
            }
        }
    }
}
