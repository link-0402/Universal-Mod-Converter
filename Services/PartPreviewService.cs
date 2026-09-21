using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using AdvancedPenumbraModConverter.Core;
using Dalamud.Plugin.Services;

namespace AdvancedPenumbraModConverter.Services;

/// <summary>
/// Shows the Mesh groups choices on the player's character: while the preview is on, the
/// character wearing the source item shows the model with every unticked mesh group and part
/// hidden, and it is redrawn whenever a checkbox changes. A copy of the source model with those
/// parts hidden is handed to Penumbra as a temporary mod; turning the preview off, or leaving
/// the tab, takes it away again.
///
/// The source model is used, not the converted one: the character wears the source item, and
/// converting never reorders meshes or parts, so the indices of both agree. Everything here
/// runs on the framework thread.
/// </summary>
public sealed class PartPreviewService(PenumbraIpcService penumbra, IPluginLog log) : IDisposable
{
    private const string Tag = "Advanced Penumbra Mod Converter part preview";

    /// <summary>High enough to win over any ordinary mod that replaces the same model.</summary>
    private const int Priority = 9999;

    /// <summary>Clicks in quick succession are drawn once, after the last of them.</summary>
    private static readonly TimeSpan SettleDelay = TimeSpan.FromMilliseconds(150);

    /// <summary>How long without a request (tab left, window closed, preview off) before the model is restored.</summary>
    private static readonly TimeSpan LeaveDelay = TimeSpan.FromMilliseconds(400);

    private readonly string _folder = Path.Combine(Path.GetTempPath(), "AdvancedPenumbraModConverter-preview");

    private (IReadOnlyList<GearOutputModel> Models, Dictionary<string, MeshRemoval> Removals)? _requested;
    private string? _requestedKey;
    private DateTime _requestedSince;
    private DateTime _lastRequest;

    private string? _shownKey;
    private bool _active;
    private List<string> _files = [];

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

    /// <summary>Called every framework tick: draws a settled change, restores once requests stop.</summary>
    public void Tick()
    {
        var now = DateTime.UtcNow;
        if (_requested != null && now - _lastRequest > LeaveDelay)
        {
            _requested = null;
            _requestedKey = null;
        }

        if (_requested is not { } request)
        {
            if (_shownKey != null) Restore();
            return;
        }
        if (_requestedKey == _shownKey || now - _requestedSince < SettleDelay) return;
        Show(request.Models, request.Removals);
    }

    private void Show(IReadOnlyList<GearOutputModel> models, Dictionary<string, MeshRemoval> removals)
    {
        _shownKey = _requestedKey;
        var previous = _files;
        _files = [];

        var paths = new Dictionary<string, string>(StringComparer.Ordinal);
        foreach (var model in models)
        {
            if (model.SourceFile == null || model.SourceGamePaths.IsDefaultOrEmpty ||
                !removals.TryGetValue(model.Local, out var removal)) continue;
            try
            {
                var bytes = MdlMeshGroups.Hide(File.ReadAllBytes(model.SourceFile), removal);
                Directory.CreateDirectory(_folder);
                var file = Path.Combine(_folder, $"{Guid.NewGuid():N}.mdl");
                File.WriteAllBytes(file, bytes);
                _files.Add(file);
                foreach (var path in model.SourceGamePaths) paths[path] = file;
            }
            catch (Exception ex)
            {
                log.Warning(ex, "[APMC] Could not build the mesh preview for {0}", model.SourceFile);
            }
        }

        if (paths.Count > 0 && penumbra.AddTemporaryModAll(Tag, paths, Priority))
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
    }

    private void Delete(List<string> files)
    {
        foreach (var file in files)
            try { File.Delete(file); }
            catch (IOException) { /* still loading; the folder is cleared on unload */ }
            catch (UnauthorizedAccessException) { }
    }

    public void Dispose()
    {
        if (_shownKey != null) Restore();
        try
        {
            if (Directory.Exists(_folder)) Directory.Delete(_folder, true);
        }
        catch (Exception ex)
        {
            log.Debug(ex, "[APMC] Could not remove the mesh preview folder.");
        }
    }
}
