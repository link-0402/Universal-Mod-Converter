using System;
using System.Collections.Generic;
using System.Collections.Immutable;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Numerics;
using UniversalModConverter.Core;
using Dalamud.Plugin.Services;
using FFXIVClientStructs.Havok.Animation.Animation;
using FFXIVClientStructs.Havok.Animation.Rig;
using FFXIVClientStructs.Havok.Common.Base.Math.QsTransform;
using FFXIVClientStructs.Havok.Common.Base.Types;

namespace UniversalModConverter.Services.Animations;

/// <summary>
/// Retargets a PAP with the game's own Havok runtime, the way XIV Instant Edit does:
/// <list type="number">
/// <item>The skeleton the animation was made for is found among the game's, the mod's own and
/// the installed skeleton mods' (see <see cref="SkeletonMatcher.ChooseSource"/>): animations bind
/// bones by index, and most are made for a skeleton mod with far more bones than the game's.</item>
/// <item>Every body animation is sampled on it, which shows the bones it really moves.</item>
/// <item>The target race gets the smallest standard skeleton with those bones, or the mod's own
/// skeleton for it (see <see cref="SkeletonMatcher.ChooseTarget"/>).</item>
/// <item>Each frame is moved onto it with <see cref="SkeletonRetarget"/>, rebuilt, spline-compressed
/// when the original was compressed (in any encoding) and left uncompressed when it was
/// interleaved, serialized, and then decoded again and compared frame by frame.</item>
/// </list>
/// Facial animations bind to face skeletons and are kept unchanged.
/// <para>
/// Called from a background thread. Native work runs on the framework thread in batches of a
/// few milliseconds so the game keeps running.
/// </para>
/// </summary>
internal sealed class HavokAnimationRetargeter(HavokAnimation havok, SkeletonLibrary skeletons, IFramework framework) : IAnimationRetargeter
{
    public string? UnavailableReason => havok.UnavailableReason;

    public RetargetedPap Retarget(RetargetRequest request)
    {
        if (framework.IsInFrameworkUpdateThread)
            throw new InvalidOperationException("Animations cannot be retargeted on the framework thread.");
        if (UnavailableReason is { } reason) throw new InvalidOperationException(reason);

        var pap = new PapFile(request.Pap);
        var bindings = pap.BodyEntries.Select(e => (int)e.Entry.Binding).Distinct().Order().ToList();
        if (bindings.Count == 0) throw new InvalidDataException("The file has no body animation.");
        var channels = Run(() => HavokAnimation.ReadChannels(pap.Havok, bindings));
        var live = skeletons.LivePaths(GenderRaces.Playable);
        var source = FindSource(request, channels, live);

        var job = Run(() => new Job(havok, pap, bindings, source.Candidate.Skeleton));
        try
        {
            Run(job.CheckRoundTrip);
            while (!Tick(job.ScanBatch)) { }
            var (target, standards) = FindTarget(request, job.MovingBones(), live);
            Run(() => job.Target(target.Candidate.Skeleton));
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

            var output = new PapFile(pap.ReplaceHavok(saved)).WithModel(request.TargetRace, pap.ModelType);
            return new RetargetedPap(output, [.. RivalNotes(source, request.SourceRace), .. TargetNotes(target, standards, request.TargetRace),
                .. job.Notes], SourceText(source.Candidate, request.SourceRace));
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

    // ── Skeletons ───────────────────────────────────────────────────────────

    /// <summary>
    /// The skeleton the animation was made for: first among the mod's own, and the game's and the
    /// installed ones of the race it was made for and the races it inherits from; only when none
    /// of those fits, among every other race's too.
    /// </summary>
    private SourceChoice FindSource(RetargetRequest request, List<AnimationChannels> channels, IReadOnlyDictionary<ushort, string> live)
    {
        var race = request.SourceRace;
        var lineage = new List<ushort>();
        for (ushort? current = race; current is { } r && !lineage.Contains(r); current = request.ParentRace(r)) lineage.Add(r);

        var game = skeletons.Game(race, live);
        var candidates = skeletons.Mod(request.ModSkeletons);
        candidates.AddRange(lineage.Select(r => r == race ? game : skeletons.Game(r, live)).OfType<SkeletonCandidate>());
        candidates.AddRange(skeletons.Installed(lineage, live));
        if (SkeletonMatcher.ChooseSource(channels, candidates, race, game?.Skeleton, request.ParentRace) is { } near) return near;

        var others = GenderRaces.Playable.Where(r => !lineage.Contains(r)).ToList();
        candidates.AddRange(others.Select(r => skeletons.Game(r, live)).OfType<SkeletonCandidate>());
        candidates.AddRange(skeletons.Installed(others, live));
        return SkeletonMatcher.ChooseSource(channels, candidates, race, game?.Skeleton, request.ParentRace)
               ?? throw new InvalidDataException(NoSource(channels, candidates.Count));
    }

    /// <summary>Why no skeleton fits, and what to do about it.</summary>
    private string NoSource(IReadOnlyList<AnimationChannels> channels, int searched)
    {
        var twice = channels.SelectMany(c => c.Bones.GroupBy(b => b).Where(g => g.Count() > 1)).Count();
        if (twice > 0)
            return $"No skeleton fits it: it binds {twice} bone(s) to more than one track each, so it was exported wrongly and cannot be rebuilt.";
        var bones = channels.Max(c => c.ReferenceBones ?? (c.Bones.IsDefaultOrEmpty ? 0 : c.Bones.Max() + 1));
        var where = skeletons.SearchedMods
            ? "the game's, this mod's and those your installed mods add"
            : "the game's and this mod's (Penumbra is not available, so other mods were not searched)";
        var made = channels.Any(c => c.Quantized)
            ? $"It was compressed for a skeleton with exactly {bones} bones"
            : $"It was made for a skeleton with {bones} bones or more";
        return $"No skeleton fits it. {made}, and none of the {searched} skeletons found among {where} has that many. " +
               "Install the skeleton mod (and version) it was made for, then preview again.";
    }

    /// <summary>
    /// The skeleton the target race gets, and the standards it had to choose from: the one the
    /// mod itself has for the race, which is what the race plays the result on; otherwise the
    /// smallest standard skeleton of the race with every bone the animation moves.
    /// </summary>
    private (TargetChoice Choice, ImmutableArray<SkeletonStandard> Standards) FindTarget(RetargetRequest request,
        IReadOnlySet<string> moving, IReadOnlyDictionary<ushort, string> live)
    {
        var race = request.TargetRace;
        if (skeletons.Mod(request.ModSkeletons.Where(s => s.Race == race)).FirstOrDefault() is { } own)
            return (new TargetChoice(own, null, SkeletonMatcher.Missing(own.Skeleton, moving)), []);
        var game = skeletons.Game(race, live) ?? throw new InvalidDataException($"The game has no skeleton for {RaceNames.Describe(race)}.");
        var standards = SkeletonMatcher.Standards(game, skeletons.Installed([race], live));
        return (SkeletonMatcher.ChooseTarget(standards, moving), [.. standards.Select(s => s.Standard)]);
    }

    /// <summary>The source skeleton in words, when it is not the game's own skeleton of the race the animation was made for.</summary>
    private static string? SourceText(SkeletonCandidate source, ushort race)
    {
        if (source.Origin == SkeletonOrigin.Game && source.Races.Contains(race) && source.LeadingBones == 0) return null;
        return Describe(source, race) +
               (source.LeadingBones > 0 ? $" (its first {source.LeadingBones} bones)" : $" ({source.Skeleton.Bones.Length} bones)");
    }

    /// <summary>Other skeletons that fit the animation just as well but would move it differently.</summary>
    private static IEnumerable<string> RivalNotes(SourceChoice choice, ushort race)
    {
        if (choice.Rivals.IsEmpty) yield break;
        // Rivals from the same mods are other options of theirs, which are named then.
        var options = choice.Rivals.Any(r => r.Label == choice.Candidate.Label);
        string Name(SkeletonCandidate c) => Describe(c, race) + (options && c.Variant is { } variant ? $" ({variant})" : string.Empty);
        yield return $"{Capitalized(List(choice.Rivals.Select(Name), 3))} would fit it just as well but rest differently; " +
                     $"it is rebuilt from {Name(choice.Candidate)}.";
    }

    /// <summary>A skeleton in words, as one of <paramref name="race"/>'s when it is mapped to that race.</summary>
    private static string Describe(SkeletonCandidate skeleton, ushort race)
    {
        var name = RaceNames.Name(skeleton.Races.Contains(race) || skeleton.Races.IsDefaultOrEmpty ? race : skeleton.Races[0]);
        return skeleton.Origin switch
        {
            SkeletonOrigin.Game    => $"the game's {name} skeleton",
            SkeletonOrigin.ThisMod => $"this mod's own {name} skeleton ({skeleton.Label})",
            _                      => $"the {name} skeleton from {skeleton.Label}",
        };
    }

    /// <summary>What the target race's skeleton means for the result, when it is not simply the game's own.</summary>
    private static IEnumerable<string> TargetNotes(TargetChoice target, ImmutableArray<SkeletonStandard> standards, ushort race)
    {
        var name = RaceNames.Name(race);
        var skeleton = target.Standard switch
        {
            null                     => Describe(target.Candidate, race),
            SkeletonStandard.Vanilla => $"the game's {name} skeleton",
            _                        => $"the {StandardName(target.Standard.Value)} {name} skeleton from {target.Candidate.Label}",
        };
        if (target.Missing.IsEmpty)
        {
            if (target.Standard == null) yield return $"It is rebuilt for {skeleton}.";
            else if (target.Standard != SkeletonStandard.Vanilla)
                yield return $"It moves bones the game's skeleton does not have, so it is rebuilt for {skeleton}.";
            yield break;
        }

        var missing = List(target.Missing, 6);
        const string leftOut = "that motion is left out, carried by the bones below them where possible.";
        if (target.Standard == null)
        {
            yield return $"It is rebuilt for {skeleton}, which has no {missing}; {leftOut}";
            yield break;
        }
        var hint = Installable(target.Missing, standards, name);
        if (standards.Length <= 1)
        {
            yield return $"{Capitalized(skeleton)} has no {missing}, which it moves; {leftOut}{hint}";
            yield break;
        }
        yield return $"None of the standard {name} skeletons you have ({List(standards.Select(StandardName), 3)}) has {missing}, " +
                     $"which it moves; {leftOut} It is rebuilt for {skeleton}.{hint}";
    }

    /// <summary>Which standard skeleton to install to keep missing IVCS or YAS bones, when it is not installed.</summary>
    private static string Installable(ImmutableArray<string> missing, ImmutableArray<SkeletonStandard> standards, string race)
    {
        var iv = missing.Any(b => b.StartsWith("iv_", StringComparison.Ordinal));
        var ya = missing.Any(b => b.StartsWith("ya_", StringComparison.Ordinal));
        if (ya && !standards.Contains(SkeletonStandard.IvcsYas))
            return $" Install YAS for {race} to keep the {(iv ? "iv_ and ya_" : "ya_")} bones.";
        return iv && !standards.Contains(SkeletonStandard.Ivcs) && !standards.Contains(SkeletonStandard.IvcsYas)
            ? $" Install IVCS for {race} to keep the iv_ bones."
            : string.Empty;
    }

    private static string StandardName(SkeletonStandard standard) => standard switch
    {
        SkeletonStandard.Ivcs    => "IVCS",
        SkeletonStandard.IvcsYas => "IVCS + YAS",
        _                        => "the game's",
    };

    private static string Capitalized(string text) => text.Length == 0 ? text : char.ToUpperInvariant(text[0]) + text[1..];

    /// <summary>"a, b and c", or "a, b, c and 4 more" beyond <paramref name="shown"/> items.</summary>
    private static string List(IEnumerable<string> items, int shown)
    {
        var list = items.ToList();
        if (list.Count <= 1) return string.Join(string.Empty, list);
        if (list.Count <= shown) return $"{string.Join(", ", list.Take(list.Count - 1))} and {list[^1]}";
        return $"{string.Join(", ", list.Take(shown))} and {list.Count - shown} more";
    }

    /// <summary>All native state of one retarget. Every member runs on the framework thread.</summary>
    private sealed unsafe class Job : IDisposable
    {
        private const long BatchMilliseconds = 3;

        private readonly HavokAnimation _havok;
        private readonly HavokAnimation.Arena _arena = new();
        private readonly HavokAnimation.Document _document;
        private readonly SkeletonDescription _source;
        private readonly hkaSkeleton* _sourceSkeleton;
        private SkeletonDescription? _target;
        private hkaSkeleton* _targetSkeleton;
        private readonly List<Clip> _clips = [];
        private readonly string[] _originalPrints;
        private readonly bool[] _moving;
        private HavokAnimation.Document? _verification;
        private int _clip;

        public Job(HavokAnimation havok, PapFile pap, IReadOnlyList<int> bindings, SkeletonDescription source)
        {
            _havok = havok;
            _source = source;
            _moving = new bool[source.Bones.Length];
            try
            {
                _document = new HavokAnimation.Document(pap.Havok);
                var container = _document.Container;
                _originalPrints = Enumerable.Range(0, container->Bindings.Length)
                    .Select(i => HavokAnimation.Fingerprint(container->Bindings[i].ptr)).ToArray();
                foreach (var index in bindings)
                    if (index >= container->Bindings.Length || index >= container->Animations.Length ||
                        container->Animations[index].ptr != container->Bindings[index].ptr->Animation.ptr)
                        throw new InvalidDataException("The file's animation list and binding list disagree.");
                if (pap.Entries.Any(e => !e.IsBody))
                    Notes.Add($"{pap.Entries.Count(e => !e.IsBody)} facial animation(s) are kept unchanged; they use the face skeleton.");

                _sourceSkeleton = HavokAnimation.Materialize(source, _arena);
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

        /// <summary>The source bones some frame moves away from their reference pose, by name (see <see cref="ScanBatch"/>).</summary>
        public IReadOnlySet<string> MovingBones()
            => Enumerable.Range(0, _moving.Length).Where(i => _moving[i]).Select(i => _source.Bones[i].Name).ToHashSet(StringComparer.Ordinal);

        /// <summary>Samples the source frames to find the bones that move; true once every clip is done.</summary>
        public bool ScanBatch()
        {
            var watch = Stopwatch.StartNew();
            while (_clip < _clips.Count && watch.ElapsedMilliseconds < BatchMilliseconds)
            {
                if (_clips[_clip].ScanNext(_moving)) continue;
                _clip++;
            }
            if (_clip < _clips.Count) return false;
            _clip = 0;
            return true;
        }

        /// <summary>Sets the skeleton to rebuild for; sampling (see <see cref="SampleBatch"/>) moves every frame onto it.</summary>
        public void Target(SkeletonDescription target)
        {
            _target = target;
            _targetSkeleton = HavokAnimation.Materialize(target, _arena);
            foreach (var clip in _clips) clip.Target();
            _clip = 0;
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
            private readonly short[] _partitions;
            private readonly int _frames;
            private readonly float _duration;
            private readonly List<BoneTransform[]> _expected = [];
            private readonly List<float[]> _expectedFloats = [];
            private readonly List<float[]> _rawFloats = [];
            private readonly HashSet<int> _affected = [];
            private HavokAnimation.Sampler? _sampler, _rawSampler, _verifier;
            private hkaAnimation* _compressed;
            private bool _replaced, _outputCompressed;
            private int _scanned, _frame;

            public Clip(Job job, int index, hkaAnimationBinding* binding)
            {
                _job = job;
                Index = index;
                _binding = binding;
                _animation = binding->Animation.ptr;
                _blendHint = binding->BlendHint.Storage;
                _tracks = new ReadOnlySpan<short>(binding->TransformTrackToBoneIndices.Data, binding->TransformTrackToBoneIndices.Length).ToArray();
                _floatSlots = new ReadOnlySpan<short>(binding->FloatTrackToFloatSlotIndices.Data, binding->FloatTrackToFloatSlotIndices.Length).ToArray();
                _partitions = new ReadOnlySpan<short>(binding->PartitionIndices.Data, binding->PartitionIndices.Length).ToArray();
                Compressed = _animation->Type != hkaAnimation.AnimationType.InterleavedAnimation;
                _duration = _animation->Duration;
                _frames = HavokAnimation.SampleCount(_duration, HavokAnimation.SourceFrameCount(_animation));

                _sampler = new HavokAnimation.Sampler(job._sourceSkeleton, binding);
                // Additive animations sample onto the reference pose; their float tracks are
                // stored raw, so they are also read without blending.
                var raw = job._arena.CopyBinding(binding);
                raw->BlendHint.Storage = 0;
                _rawSampler = new HavokAnimation.Sampler(job._sourceSkeleton, raw);
            }

            public int Index { get; }

            public bool Compressed { get; }

            public SkeletonRetarget? Retarget { get; private set; }

            private float Time(int frame) => frame == _frames - 1 ? _duration : _duration * frame / Math.Max(1, _frames - 1);

            /// <summary>Samples the next frame and marks the bones it moves; false when every frame is done.</summary>
            public bool ScanNext(bool[] moving)
            {
                if (_scanned >= _frames) return false;
                _sampler!.Sample(Time(_scanned));
                var bones = _job._source.Bones;
                for (var i = 0; i < _sampler.BoneCount; i++)
                    if (!moving[i] && SkeletonMatcher.Moves(HavokAnimation.Transform(_sampler.Transforms[i]), bones[i].Reference))
                        moving[i] = true;
                _scanned++;
                return true;
            }

            /// <summary>Prepares moving the frames onto the job's target skeleton.</summary>
            public void Target()
            {
                var target = _job._target!;
                Retarget = new SkeletonRetarget(_job._source, target, _floatSlots, _partitions);
                var perFrame = (long)target.Bones.Length * sizeof(hkQsTransformf) + (target.FloatNames.Length + _floatSlots.Length) * 4L;
                if (_frames * perFrame > PapFile.MaxFileSize) throw new InvalidDataException("The animation is too long to rebuild.");
            }

            /// <summary>Samples the next frame and moves it onto the target skeleton; false when every frame is done.</summary>
            public bool SampleNext()
            {
                if (_frame >= _frames) return false;
                var time = Time(_frame);
                _sampler!.Sample(time);
                _rawSampler!.Sample(time);
                var source = new BoneTransform[_sampler.BoneCount];
                for (var i = 0; i < source.Length; i++) source[i] = HavokAnimation.Transform(_sampler.Transforms[i]);
                var pose = Retarget!.Map(source);
                var target = _job._target!;
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
                var target = _job._target!;
                var tracks = Retarget!.MapTracks(_tracks).ToList();
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
                var target = _job._target!;
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
