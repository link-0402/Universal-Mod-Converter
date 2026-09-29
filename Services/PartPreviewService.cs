using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using UniversalModConverter.Core;
using Dalamud.Plugin.Services;

namespace UniversalModConverter.Services;

/// <summary>
/// Shows the Mesh groups choices on the player's character: while the preview is on, the
/// character wearing the source item shows the model with every unticked mesh group and part
/// hidden, and it is redrawn whenever a checkbox changes. A copy of the source model with those
/// parts hidden is handed to Penumbra as a temporary mod; turning the preview off, or leaving
/// the tab, takes it away again.
///
/// The source model is used, not the converted one: the character wears the source item, and
/// converting never reorders meshes or parts, so the indices of both agree. The models are
/// rebuilt in the background, one build at a time; everything else, Penumbra included, runs on
/// the framework thread.
/// </summary>
public sealed class PartPreviewService(PenumbraIpcService penumbra, IPluginLog log) : IDisposable
{
    private const string Tag = "Universal Mod Converter part preview";

    /// <summary>High enough to win over any ordinary mod that replaces the same model.</summary>
    private const int Priority = 9999;

    /// <summary>Clicks in quick succession are drawn once, after the last of them.</summary>
    private static readonly TimeSpan SettleDelay = TimeSpan.FromMilliseconds(150);

    /// <summary>How long without a request (tab left, window closed, preview off) before the model is restored.</summary>
    private static readonly TimeSpan LeaveDelay = TimeSpan.FromMilliseconds(400);

    private readonly string _folder = Path.Combine(Path.GetTempPath(), "UniversalModConverter-preview");

    private (IReadOnlyList<GearOutputModel> Models, Dictionary<string, MeshRemoval> Removals)? _requested;
    private string? _requestedKey;
    private DateTime _requestedSince;
    private DateTime _lastRequest;

    private string? _shownKey;
    private bool _active;
    private List<string> _files = [];

    /// <summary>The choice being built in the background, or null.</summary>
    private string? _buildingKey;

    /// <summary>A finished build, handed from the background to <see cref="Tick"/>.</summary>
    private Build? _built;

    private volatile bool _disposed;

    /// <summary>
    /// Source models by file, so ticking one checkbox after another does not read them again.
    /// Let go when the preview ends.
    /// </summary>
    private readonly Dictionary<string, (DateTime Written, byte[] Bytes)> _sources = new(StringComparer.OrdinalIgnoreCase);

    private sealed record Build(string Key, List<string> Files, Dictionary<string, string> Paths);

    /// <summary>Why the preview cannot show these models, or null.</summary>
    public string? UnavailableReason(IReadOnlyList<GearOutputModel> models)
    {
        if (!penumbra.IsAvailable) return "Penumbra is not available.";
        if (models.All(m => m.SourceFile == null || m.SourceGamePaths.IsDefaultOrEmpty))
            return "These models do not come from a file of the mod, so there is nothing to show on your character.";
        return null;
    }

    /// <summary>Call every frame while the preview is on, with the current removals by output model.</summary>
    public void Request(IReadOnlyList<GearOutputModel> models, IReadOnlyDictionary<string, MeshRemoval> removals)
    {
        var relevant = models
            .Where(m => removals.ContainsKey(m.Local))
            .ToDictionary(m => m.Local, m => removals[m.Local], StringComparer.Ordinal);
        var key = string.Join(";", models.Select(m => m.Local)) + "#" +
                  string.Join(";", relevant.OrderBy(r => r.Key, StringComparer.Ordinal).Select(r =>
                      $"{r.Key}:{string.Join(",", r.Value.Groups)}/{string.Join(",", r.Value.PartsOrEmpty.Select(p => $"{p.Group}.{p.Part}"))}"));

        var now = DateTime.UtcNow;
        if (key != _requestedKey)
        {
            _requestedKey = key;
            _requested = (models, relevant);
            _requestedSince = now;
        }
        _lastRequest = now;
    }

    /// <summary>Called every framework tick: shows a finished build, starts one for a settled change, restores once requests stop.</summary>
    public void Tick()
    {
        var now = DateTime.UtcNow;
        if (_requested != null && now - _lastRequest > LeaveDelay)
        {
            _requested = null;
            _requestedKey = null;
        }

        if (Interlocked.Exchange(ref _built, null) is { } built)
        {
            _buildingKey = null;
            // Only the latest choice is shown; a build the user has moved on from is thrown away.
            if (_requested != null && built.Key == _requestedKey) Show(built);
            else Delete(built.Files);
        }

        if (_requested is not { } request)
        {
            if (_shownKey != null) Restore();
            return;
        }
        if (_buildingKey != null || _requestedKey == _shownKey || now - _requestedSince < SettleDelay) return;
        StartBuild(_requestedKey!, request.Models, request.Removals);
    }

    private void StartBuild(string key, IReadOnlyList<GearOutputModel> models, Dictionary<string, MeshRemoval> removals)
    {
        _buildingKey = key;
        Task.Run(() =>
        {
            var files = new List<string>();
            var paths = new Dictionary<string, string>(StringComparer.Ordinal);
            foreach (var model in models)
            {
                if (_disposed) break;
                if (model.SourceFile == null || model.SourceGamePaths.IsDefaultOrEmpty ||
                    !removals.TryGetValue(model.Local, out var removal)) continue;
                try
                {
                    var bytes = MdlMeshGroups.Hide(Source(model.SourceFile), removal);
                    Directory.CreateDirectory(_folder);
                    var file = Path.Combine(_folder, $"{Guid.NewGuid():N}.mdl");
                    File.WriteAllBytes(file, bytes);
                    files.Add(file);
                    foreach (var path in model.SourceGamePaths) paths[path] = file;
                }
                catch (Exception ex)
                {
                    log.Warning(ex, "[UMC] Could not build the mesh preview for {0}", model.SourceFile);
                }
            }
            if (_disposed) Delete(files);
            else Volatile.Write(ref _built, new Build(key, files, paths));
        });
    }

    /// <summary>The source model's bytes; <see cref="MdlMeshGroups.Hide"/> copies them, so they can be shared.</summary>
    private byte[] Source(string file)
    {
        var written = File.GetLastWriteTimeUtc(file);
        lock (_sources)
            if (_sources.TryGetValue(file, out var known) && known.Written == written) return known.Bytes;
        var bytes = File.ReadAllBytes(file);
        lock (_sources) _sources[file] = (written, bytes);
        return bytes;
    }

    private void Show(Build build)
    {
        _shownKey = build.Key;
        var previous = _files;
        _files = build.Files;

        if (build.Paths.Count > 0 && penumbra.AddTemporaryModAll(Tag, build.Paths, Priority))
        {
            _active = true;
            penumbra.RedrawObject(0);
        }
        else if (_active)
        {
            // Everything is kept again: the original model is the preview.
            penumbra.RemoveTemporaryModAll(Tag, Priority);
            penumbra.RedrawObject(0);
            _active = false;
        }
        Delete(previous);
    }

    private void Restore()
    {
        _shownKey = null;
        if (_active)
        {
            penumbra.RemoveTemporaryModAll(Tag, Priority);
            penumbra.RedrawObject(0);
            _active = false;
        }
        Delete(_files);
        _files = [];
        lock (_sources) _sources.Clear();
    }

    private static void Delete(List<string> files)
    {
        foreach (var file in files)
            try { File.Delete(file); }
            catch (IOException) { /* still loading; the folder is cleared on unload */ }
            catch (UnauthorizedAccessException) { }
    }

    public void Dispose()
    {
        _disposed = true;
        if (_shownKey != null) Restore();
        try
        {
            if (Directory.Exists(_folder)) Directory.Delete(_folder, true);
        }
        catch (Exception ex)
        {
            log.Debug(ex, "[UMC] Could not remove the mesh preview folder.");
        }
    }
}
