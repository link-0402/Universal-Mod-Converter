// Discovery of installed skeletons adapted from XIV Instant Edit's skeleton library.
// See THIRD_PARTY_NOTICES.md.
using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Security.Cryptography;
using UniversalModConverter.Core;
using Dalamud.Plugin.Services;

namespace UniversalModConverter.Services.Animations;

/// <summary>
/// The skeletons animations are matched against (see <see cref="SkeletonMatcher"/>): the game's
/// base skeletons, the ones the converted mod replaces, and every base skeleton the installed
/// Penumbra mods replace, such as IVCS, YAS or larger rigs. Animation mods rarely include the
/// skeleton they were made for: players install it as a mod of its own.
/// <para>
/// Installed skeletons are found by reading every mod's definition the first time they are
/// needed, and again, for the mods whose definition files changed, once Penumbra reports a
/// change. Each file is described once per content with the game's Havok runtime, a few per
/// framework tick. Called from background threads.
/// </para>
/// </summary>
internal sealed class SkeletonLibrary
{
    private const long BatchMilliseconds = 3;
    private const long MaxSkeletonSize = 64L * 1024 * 1024;

    private readonly PenumbraIpcService? _penumbra;
    private readonly IGameFileProvider _game;
    private readonly IFramework _framework;
    private readonly IPluginLog _log;

    private readonly object _scanLock = new();
    private readonly object _cacheLock = new();
    private volatile bool _stale = true;
    private List<InstalledSkeleton> _installed = [];

    /// <summary>The skeletons each mod folder maps, as of the stamp of its definition files.</summary>
    private readonly Dictionary<string, (string Stamp, List<InstalledSkeleton> Files)> _mods = new(StringComparer.OrdinalIgnoreCase);

    /// <summary>The content hash of each skeleton file, while its size and write time stay the same.</summary>
    private readonly Dictionary<string, (long Length, DateTime Written, string Hash)> _hashes = new(StringComparer.OrdinalIgnoreCase);

    /// <summary>Described skeletons by content hash; null for content that holds no usable skeleton.</summary>
    private readonly Dictionary<string, Described?> _descriptions = new(StringComparer.Ordinal);

    private sealed record Described(SkeletonDescription Skeleton, string Fingerprint);

    public SkeletonLibrary(PenumbraIpcService? penumbra, IGameFileProvider game, IFramework framework, IPluginLog log)
    {
        _penumbra = penumbra;
        _game = game;
        _framework = framework;
        _log = log;
        if (penumbra == null) return;
        penumbra.ModsChanged += MarkStale;
        penumbra.PenumbraInitialized += MarkStale;
    }

    /// <summary>Whether the last search could read the installed mods: false while Penumbra is unavailable.</summary>
    public bool SearchedMods { get; private set; }

    private void MarkStale() => _stale = true;

    // ── Candidates ──────────────────────────────────────────────────────────

    /// <summary>
    /// What the player's collection loads for each race's base skeleton: a file on disk, or the
    /// game path itself where no mod replaces it. Empty when Penumbra cannot tell.
    /// </summary>
    public IReadOnlyDictionary<ushort, string> LivePaths(IReadOnlyList<ushort> races)
    {
        var result = new Dictionary<ushort, string>();
        if (_penumbra == null || races.Count == 0) return result;
        var paths = races.Select(PapPath.BaseSkeletonPath).ToArray();
        string[]? resolved = null;
        try
        {
            resolved = Run(() =>
            {
                if (!_penumbra.IsAvailable) return null;
                var collection = _penumbra.GetCollectionForObject(0) ?? _penumbra.GetCurrentCollection();
                return collection is { } c && c.Id != Guid.Empty ? _penumbra.ResolvePaths(c.Id, paths) : null;
            });
        }
        catch (Exception ex)
        {
            _log.Debug(ex, "[UMC] The player's skeletons could not be resolved.");
        }
        if (resolved == null || resolved.Length != races.Count) return result;
        for (var i = 0; i < races.Count; i++)
        {
            if (string.IsNullOrEmpty(resolved[i])) continue;
            try
            {
                result[races[i]] = Path.IsPathRooted(resolved[i]) ? Path.GetFullPath(resolved[i]) : GamePath.Normalize(resolved[i]);
            }
            catch (Exception ex) when (ex is ArgumentException or NotSupportedException or PathTooLongException)
            {
                // Not a path this can compare; the race simply has no live skeleton.
            }
        }
        return result;
    }

    /// <summary>The game's own base skeleton of <paramref name="race"/>, or null when the game has none.</summary>
    public SkeletonCandidate? Game(ushort race, IReadOnlyDictionary<ushort, string> live)
    {
        var path = PapPath.BaseSkeletonPath(race);
        byte[]? bytes;
        try { bytes = _game.ReadFile(path); }
        catch (Exception) { bytes = null; }
        if (bytes == null || Describe([(Hash(bytes), () => bytes)]).FirstOrDefault() is not { } described) return null;
        return new SkeletonCandidate(described.Skeleton, SkeletonOrigin.Game, [race], $"the game's {RaceNames.Name(race)} skeleton")
        {
            Live = live.TryGetValue(race, out var loaded) && loaded == path,
        };
    }

    /// <summary>The base skeletons the converted mod replaces, alike ones merged, in the order given.</summary>
    public List<SkeletonCandidate> Mod(IEnumerable<ModSkeleton> skeletons)
    {
        var list = skeletons.ToList();
        var described = Describe(list.Select(s => (Hash(s.Bytes), (Func<byte[]>)(() => s.Bytes))));
        return list.Zip(described)
            .Where(p => p.Second != null)
            .GroupBy(p => p.Second!.Fingerprint)
            .Select(g => new SkeletonCandidate(g.First().Second!.Skeleton, SkeletonOrigin.ThisMod,
                [.. g.Select(p => p.First.Race).Distinct().Order()], g.First().First.Label) { Files = g.Count() })
            .ToList();
    }

    /// <summary>
    /// The skeletons installed mods map to the base skeleton path of any of
    /// <paramref name="races"/>, files holding the same skeleton merged into one candidate.
    /// </summary>
    public List<SkeletonCandidate> Installed(IReadOnlyCollection<ushort> races, IReadOnlyDictionary<ushort, string> live)
    {
        var files = Scan().Where(f => races.Contains(f.Race)).ToList();
        var hashes = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        foreach (var path in files.Select(f => f.Path).Distinct(StringComparer.OrdinalIgnoreCase))
            if (FileHash(path) is { } hash) hashes[path] = hash;
        var readable = files.Where(f => hashes.ContainsKey(f.Path)).ToList();
        var unique = readable.Select(f => (Hash: hashes[f.Path], f.Path)).DistinctBy(f => f.Hash).ToList();
        var described = unique.Zip(Describe(unique.Select(f => (f.Hash, (Func<byte[]>)(() => File.ReadAllBytes(f.Path))))))
            .Where(p => p.Second != null)
            .ToDictionary(p => p.First.Hash, p => p.Second!, StringComparer.Ordinal);

        return readable
            .Where(f => described.ContainsKey(hashes[f.Path]))
            .GroupBy(f => described[hashes[f.Path]].Fingerprint)
            .Select(g => new SkeletonCandidate(described[hashes[g.First().Path]].Skeleton, SkeletonOrigin.Installed,
                [.. g.Select(f => f.Race).Distinct().Order()], Origin(g.Select(f => f.Mod)))
            {
                Files = g.Select(f => f.Path).Distinct(StringComparer.OrdinalIgnoreCase).Count(),
                Live = g.Any(f => live.TryGetValue(f.Race, out var loaded) && string.Equals(loaded, f.Path, StringComparison.OrdinalIgnoreCase)),
                Variant = g.Select(f => f.Option).OfType<string>().Distinct(StringComparer.Ordinal).ToList() is { Count: > 0 } options
                    ? Origin(options)
                    : null,
            })
            .ToList();
    }

    /// <summary>
    /// Names the mods a skeleton comes from, a few at most. Characters downloaded by sync
    /// plugins, whose folders are named Name@World, come after the mods themselves.
    /// </summary>
    private static string Origin(IEnumerable<string> mods)
    {
        var names = mods.Distinct(StringComparer.Ordinal)
            .OrderBy(name => name.Contains('@')).ThenBy(name => name, StringComparer.OrdinalIgnoreCase).ToArray();
        return names.Length <= 3 ? string.Join(", ", names) : $"{string.Join(", ", names.Take(3))} and {names.Length - 3} more";
    }

    // ── Descriptions ────────────────────────────────────────────────────────

    private static string Hash(byte[] bytes) => Convert.ToHexString(SHA256.HashData(bytes));

    /// <summary>The content hash of a skeleton file, read again only when it changed; null when it cannot be read.</summary>
    private string? FileHash(string path)
    {
        try
        {
            var info = new FileInfo(path);
            if (!info.Exists || info.Length > MaxSkeletonSize) return null;
            lock (_cacheLock)
                if (_hashes.TryGetValue(path, out var known) && known.Length == info.Length && known.Written == info.LastWriteTimeUtc)
                    return known.Hash;
            var hash = Hash(File.ReadAllBytes(path));
            lock (_cacheLock) _hashes[path] = (info.Length, info.LastWriteTimeUtc, hash);
            return hash;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            _log.Debug(ex, "[UMC] Skeleton {0} could not be read.", path);
            return null;
        }
    }

    /// <summary>
    /// Describes each skeleton, in order: the main skeleton of the file, or null when it holds
    /// none that is usable. Contents seen before are not read again; the others are read with
    /// <c>Read</c> and described on the framework thread, a few milliseconds' worth per tick.
    /// </summary>
    private List<Described?> Describe(IEnumerable<(string Hash, Func<byte[]> Read)> skeletons)
    {
        var list = skeletons.ToList();
        List<(string Hash, Func<byte[]> Read)> unknown;
        lock (_cacheLock) unknown = [.. list.DistinctBy(s => s.Hash).Where(s => !_descriptions.ContainsKey(s.Hash))];
        var pending = new List<(string Hash, byte[] Bytes)>();
        foreach (var (hash, read) in unknown)
        {
            try { pending.Add((hash, read())); }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
            {
                // Not remembered: a file in use now may read fine next time.
                _log.Debug(ex, "[UMC] A skeleton could not be read.");
            }
        }

        var next = 0;
        while (next < pending.Count)
            _framework.RunOnTick(() =>
            {
                var watch = Stopwatch.StartNew();
                do
                {
                    var (hash, bytes) = pending[next++];
                    Described? described = null;
                    var remember = true;
                    try
                    {
                        var skeleton = HavokAnimation.DescribeMain(bytes);
                        described = new Described(skeleton, skeleton.Fingerprint());
                    }
                    catch (InvalidDataException ex)
                    {
                        _log.Debug("[UMC] A skeleton was skipped: {0}", ex.Message);
                    }
                    catch (InvalidOperationException ex)
                    {
                        // The runtime was not ready, which says nothing about the file: ask again next time.
                        _log.Debug("[UMC] A skeleton could not be described yet: {0}", ex.Message);
                        remember = false;
                    }
                    if (remember) lock (_cacheLock) _descriptions[hash] = described;
                } while (next < pending.Count && watch.ElapsedMilliseconds < BatchMilliseconds);
            }, delayTicks: 1).GetAwaiter().GetResult();

        lock (_cacheLock) return list.Select(s => _descriptions.GetValueOrDefault(s.Hash)).ToList();
    }

    // ── Installed mods ──────────────────────────────────────────────────────

    /// <summary>
    /// Every base skeleton file the installed mods map, from the last scan; scanned again after
    /// Penumbra reported a change. Mods whose definition files are unchanged keep what was read.
    /// </summary>
    private List<InstalledSkeleton> Scan()
    {
        lock (_scanLock)
        {
            if (!_stale) return _installed;
            // Cleared first, so a change reported while scanning scans again next time.
            _stale = false;
            (string? Root, Dictionary<string, string>? Mods) listed = (null, null);
            try
            {
                if (_penumbra != null)
                    listed = Run(() => _penumbra.IsAvailable ? (_penumbra.GetModDirectory(), _penumbra.GetModList()) : (null, null));
            }
            catch (Exception ex)
            {
                _log.Debug(ex, "[UMC] Penumbra's mod list could not be read.");
            }
            if (string.IsNullOrWhiteSpace(listed.Root) || listed.Mods == null)
            {
                SearchedMods = false;
                _stale = true;
                return _installed = [];
            }

            var result = new List<InstalledSkeleton>();
            var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            foreach (var (directory, name) in listed.Mods)
            {
                try
                {
                    var folder = Path.GetFullPath(Path.Combine(listed.Root, directory));
                    if (!seen.Add(folder) || !Directory.Exists(folder)) continue;
                    var (definitions, stamp) = InstalledSkeletons.Definitions(folder);
                    if (!_mods.TryGetValue(folder, out var known) || known.Stamp != stamp)
                        _mods[folder] = known = (stamp, InstalledSkeletons.Read(folder, definitions,
                            string.IsNullOrWhiteSpace(name) ? directory : name,
                            (file, ex) => _log.Debug("[UMC] {0} could not be read for skeletons: {1}", file, ex.Message)));
                    result.AddRange(known.Files);
                }
                catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or ArgumentException or NotSupportedException)
                {
                    _log.Debug(ex, "[UMC] The mod {0} could not be searched for skeletons.", directory);
                }
            }
            foreach (var gone in _mods.Keys.Where(folder => !seen.Contains(folder)).ToList()) _mods.Remove(gone);
            SearchedMods = true;
            return _installed = result;
        }
    }

    private T Run<T>(Func<T> action) => _framework.RunOnFrameworkThread(action).GetAwaiter().GetResult();
}
