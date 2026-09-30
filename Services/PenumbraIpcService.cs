using System;
using System.Collections.Generic;
using Dalamud.Plugin;
using Dalamud.Plugin.Ipc;
using Dalamud.Plugin.Services;

namespace UniversalModConverter.Services;

/// <summary>
/// Thin wrapper around Penumbra's IPC channel.
/// All calls gracefully return null/false when Penumbra is unavailable.
/// </summary>
public sealed class PenumbraIpcService : IDisposable
{
    private readonly IDalamudPluginInterface _pi;
    private readonly IPluginLog              _log;

    // ── IPC subscribers ───────────────────────────────────────────────────────
    // We cache the subscribers to avoid creating new objects on every call.

    private readonly ICallGateSubscriber<(int Breaking, int Features)>                                 _apiVersion;
    private readonly ICallGateSubscriber<string>                                                        _getModDirectory;
    private readonly ICallGateSubscriber<Dictionary<string, string>>                                    _getModList;
    private readonly ICallGateSubscriber<string, string, int>                                           _reloadMod;
    private readonly ICallGateSubscriber<string, int>                                                   _addMod;
    private readonly ICallGateSubscriber<string, string, int>                                           _deleteMod;
    private readonly ICallGateSubscriber<string, string, (int, string, bool, bool)>                    _getModPath;
    private readonly ICallGateSubscriber<Dictionary<Guid, string>>                                            _getCollections;
    private readonly ICallGateSubscriber<int, (bool ObjectValid, bool IndividualSet, (Guid Id, string Name))> _getCollectionForObject;
    private readonly ICallGateSubscriber<string, Dictionary<string, string>, string, int, int>            _addTemporaryModAll;
    private readonly ICallGateSubscriber<string, int, int>                                                _removeTemporaryModAll;
    private readonly ICallGateSubscriber<int, int, object>                                                _redrawObject;
    private readonly ICallGateSubscriber<byte, (Guid Id, string Name)?>                                   _getCollection;
    private readonly ICallGateSubscriber<Guid, Dictionary<string, object?>>                               _getChangedItemsForCollection;
    private readonly ICallGateSubscriber<Func<string, (string, string)[]>>                                _checkCurrentChangedItemFunc;
    private readonly ICallGateSubscriber<Guid, string[], string[], (int, string[], string[][])>           _resolvePaths;

    /// <summary>Penumbra's ApiCollectionType.Current: the collection its own window edits.</summary>
    private const byte CurrentCollectionType = 226;

    /// <summary>
    /// Lists the mods changing a changed item in the current collection. Penumbra hands out the
    /// function once; it throws <see cref="ObjectDisposedException"/> after Penumbra reloads.
    /// </summary>
    private Func<string, (string, string)[]>? _currentChangedItemMods;

    // ── Events ────────────────────────────────────────────────────────────────
    /// <summary>Raised when Penumbra signals it has fully initialised.</summary>
    public event Action? PenumbraInitialized;
    /// <summary>Raised when Penumbra is disposing.</summary>
    public event Action? PenumbraDisposed;

    /// <summary>
    /// Raised when what a collection changes may have changed: a mod setting changed in any
    /// collection, or a mod was added, deleted or renamed. May be raised off the framework thread.
    /// </summary>
    public event Action? ModsChanged;

    private ICallGateSubscriber<object>? _initializedSub;
    private ICallGateSubscriber<object>? _disposedSub;
    private ICallGateSubscriber<int, Guid, string, bool, object>? _modSettingChangedSub;
    private ICallGateSubscriber<string, object>? _modAddedSub;
    private ICallGateSubscriber<string, object>? _modDeletedSub;
    private ICallGateSubscriber<string, string, object>? _modMovedSub;

    public PenumbraIpcService(IDalamudPluginInterface pi, IPluginLog log)
    {
        _pi  = pi;
        _log = log;

        _apiVersion      = pi.GetIpcSubscriber<(int, int)>             ("Penumbra.ApiVersion.V5");
        _getModDirectory = pi.GetIpcSubscriber<string>                 ("Penumbra.GetModDirectory");
        _getModList      = pi.GetIpcSubscriber<Dictionary<string,string>>("Penumbra.GetModList");
        _reloadMod       = pi.GetIpcSubscriber<string, string, int>    ("Penumbra.ReloadMod.V5");
        _addMod          = pi.GetIpcSubscriber<string, int>            ("Penumbra.AddMod.V5");
        _deleteMod       = pi.GetIpcSubscriber<string, string, int>    ("Penumbra.DeleteMod.V5");
        _getModPath      = pi.GetIpcSubscriber<string, string, (int, string, bool, bool)>("Penumbra.GetModPath.V5");
        _getCollections         = pi.GetIpcSubscriber<Dictionary<Guid, string>>("Penumbra.GetCollections.V5");
        _getCollectionForObject = pi.GetIpcSubscriber<int, (bool, bool, (Guid, string))>("Penumbra.GetCollectionForObject.V5");
        _addTemporaryModAll     = pi.GetIpcSubscriber<string, Dictionary<string, string>, string, int, int>("Penumbra.AddTemporaryModAll.V5");
        _removeTemporaryModAll  = pi.GetIpcSubscriber<string, int, int>("Penumbra.RemoveTemporaryModAll.V5");
        _redrawObject           = pi.GetIpcSubscriber<int, int, object>("Penumbra.RedrawObject.V5");
        _getCollection          = pi.GetIpcSubscriber<byte, (Guid, string)?>("Penumbra.GetCollection");
        _getChangedItemsForCollection = pi.GetIpcSubscriber<Guid, Dictionary<string, object?>>("Penumbra.GetChangedItemsForCollection");
        _checkCurrentChangedItemFunc  = pi.GetIpcSubscriber<Func<string, (string, string)[]>>("Penumbra.CheckCurrentChangedItemFunc");
        _resolvePaths           = pi.GetIpcSubscriber<Guid, string[], string[], (int, string[], string[][])>("Penumbra.ResolvePaths");

        // Subscribe to lifecycle events
        try
        {
            _initializedSub = pi.GetIpcSubscriber<object>("Penumbra.Initialized");
            _initializedSub.Subscribe(OnPenumbraInitialized);
        }
        catch (Exception ex) { _log.Debug(ex, "[UMC] Could not subscribe to Penumbra.Initialized"); }

        try
        {
            _disposedSub = pi.GetIpcSubscriber<object>("Penumbra.Disposed");
            _disposedSub.Subscribe(OnPenumbraDisposed);
        }
        catch (Exception ex) { _log.Debug(ex, "[UMC] Could not subscribe to Penumbra.Disposed"); }

        try
        {
            _modSettingChangedSub = pi.GetIpcSubscriber<int, Guid, string, bool, object>("Penumbra.ModSettingChanged.V5");
            _modSettingChangedSub.Subscribe(OnModSettingChanged);
            _modAddedSub = pi.GetIpcSubscriber<string, object>("Penumbra.ModAdded");
            _modAddedSub.Subscribe(OnModAddedOrDeleted);
            _modDeletedSub = pi.GetIpcSubscriber<string, object>("Penumbra.ModDeleted");
            _modDeletedSub.Subscribe(OnModAddedOrDeleted);
            _modMovedSub = pi.GetIpcSubscriber<string, string, object>("Penumbra.ModMoved");
            _modMovedSub.Subscribe(OnModMoved);
        }
        catch (Exception ex) { _log.Debug(ex, "[UMC] Could not subscribe to Penumbra's mod change events"); }
    }

    // ── Lifecycle ─────────────────────────────────────────────────────────────

    private void OnPenumbraInitialized()
    {
        _currentChangedItemMods = null;
        PenumbraInitialized?.Invoke();
    }

    private void OnPenumbraDisposed()
    {
        _currentChangedItemMods = null;
        PenumbraDisposed?.Invoke();
    }

    private void OnModSettingChanged(int change, Guid collection, string modDirectory, bool inherited) => ModsChanged?.Invoke();
    private void OnModAddedOrDeleted(string modDirectory) => ModsChanged?.Invoke();
    private void OnModMoved(string oldDirectory, string newDirectory) => ModsChanged?.Invoke();

    public void Dispose()
    {
        try { _initializedSub?.Unsubscribe(OnPenumbraInitialized); } catch { /* ignore */ }
        try { _disposedSub?.Unsubscribe(OnPenumbraDisposed); }       catch { /* ignore */ }
        try { _modSettingChangedSub?.Unsubscribe(OnModSettingChanged); } catch { /* ignore */ }
        try { _modAddedSub?.Unsubscribe(OnModAddedOrDeleted); }          catch { /* ignore */ }
        try { _modDeletedSub?.Unsubscribe(OnModAddedOrDeleted); }        catch { /* ignore */ }
        try { _modMovedSub?.Unsubscribe(OnModMoved); }                   catch { /* ignore */ }
    }

    // ── Availability check ────────────────────────────────────────────────────

    /// <summary>Returns true when Penumbra is loaded and its IPC is reachable.</summary>
    public bool IsAvailable
    {
        get
        {
            try
            {
                var (breaking, _) = _apiVersion.InvokeFunc();
                return breaking == 5;
            }
            catch { return false; }
        }
    }

    // ── API calls ─────────────────────────────────────────────────────────────

    /// <summary>Returns Penumbra's configured mod root directory, or null on failure.</summary>
    public string? GetModDirectory()
    {
        try   { return _getModDirectory.InvokeFunc(); }
        catch (Exception ex) { _log.Warning(ex, "[UMC] GetModDirectory failed"); return null; }
    }

    /// <summary>Returns a dict of modDirectory → modName for all known mods.</summary>
    public Dictionary<string, string>? GetModList()
    {
        try   { return _getModList.InvokeFunc(); }
        catch (Exception ex) { _log.Warning(ex, "[UMC] GetModList failed"); return null; }
    }

    /// <summary>
    /// Asks Penumbra to reload a mod from disk.
    /// <paramref name="modDirectory"/> is the folder name under the Penumbra root (not a full path).
    /// Returns true on success.
    /// </summary>
    public bool ReloadMod(string modDirectory, string modName = "")
    {
        try
        {
            var rc = (PenumbraApiEc)_reloadMod.InvokeFunc(modDirectory, modName);
            if (rc != PenumbraApiEc.Success)
                _log.Warning($"[UMC] ReloadMod returned {rc} for '{modDirectory}'");
            return rc == PenumbraApiEc.Success;
        }
        catch (Exception ex) { _log.Warning(ex, "[UMC] ReloadMod failed"); return false; }
    }

    /// <summary>
    /// Adds a temporary mod to every collection: <paramref name="paths"/> maps game paths to
    /// files on disk. Replaces an earlier temporary mod with the same tag and priority.
    /// </summary>
    public bool AddTemporaryModAll(string tag, Dictionary<string, string> paths, int priority)
    {
        try
        {
            var rc = (PenumbraApiEc)_addTemporaryModAll.InvokeFunc(tag, paths, string.Empty, priority);
            if (rc != PenumbraApiEc.Success) _log.Warning($"[UMC] AddTemporaryModAll returned {rc}");
            return rc == PenumbraApiEc.Success;
        }
        catch (Exception ex) { _log.Warning(ex, "[UMC] AddTemporaryModAll failed"); return false; }
    }

    public void RemoveTemporaryModAll(string tag, int priority)
    {
        try { _removeTemporaryModAll.InvokeFunc(tag, priority); }
        catch (Exception ex) { _log.Warning(ex, "[UMC] RemoveTemporaryModAll failed"); }
    }

    /// <summary>Redraws a game object (0 is the local player) so file changes show.</summary>
    public void RedrawObject(int objectIndex)
    {
        try { _redrawObject.InvokeAction(objectIndex, 0); }
        catch (Exception ex) { _log.Warning(ex, "[UMC] RedrawObject failed"); }
    }

    /// <summary>
    /// Registers a new mod folder (already created inside the Penumbra mod root) in
    /// Penumbra's mod list so it is immediately visible without a full rediscover.
    /// <paramref name="modDirectory"/> is the folder name only (not a full path).
    /// Returns true on success.
    /// </summary>
    public bool AddMod(string modDirectory)
    {
        try
        {
            var rc = (PenumbraApiEc)_addMod.InvokeFunc(modDirectory);
            if (rc != PenumbraApiEc.Success && rc != PenumbraApiEc.NothingDone)
                _log.Warning($"[UMC] AddMod returned {rc} for '{modDirectory}'");
            return rc == PenumbraApiEc.Success || rc == PenumbraApiEc.NothingDone;
        }
        catch (Exception ex) { _log.Warning(ex, "[UMC] AddMod failed"); return false; }
    }

    /// <summary>
    /// Removes a mod from Penumbra's mod list. Penumbra also deletes the folder if it still
    /// exists, so callers move the folder away first when they want to keep it.
    /// <paramref name="modDirectory"/> is the folder name only (not a full path).
    /// </summary>
    public bool DeleteMod(string modDirectory, string modName = "")
    {
        try
        {
            var rc = (PenumbraApiEc)_deleteMod.InvokeFunc(modDirectory, modName);
            if (rc != PenumbraApiEc.Success && rc != PenumbraApiEc.NothingDone)
                _log.Warning($"[UMC] DeleteMod returned {rc} for '{modDirectory}'");
            return rc == PenumbraApiEc.Success || rc == PenumbraApiEc.NothingDone;
        }
        catch (Exception ex) { _log.Warning(ex, "[UMC] DeleteMod failed"); return false; }
    }

    /// <summary>
    /// Retrieves the full on-disk path for a specific mod.
    /// Returns (success, fullPath).
    /// </summary>
    public (bool Success, string FullPath) GetModPath(string modDirectory, string modName = "")
    {
        try
        {
            var (ret, fullPath, _, _) = _getModPath.InvokeFunc(modDirectory, modName);
            return ((PenumbraApiEc)ret == PenumbraApiEc.Success, fullPath);
        }
        catch (Exception ex) { _log.Warning(ex, "[UMC] GetModPath failed"); return (false, string.Empty); }
    }

    /// <summary>
    /// Returns all Penumbra collections as (Guid Id, string Name) pairs, or null on failure.
    /// </summary>
    public Dictionary<Guid, string>? GetCollections()
    {
        try   { return _getCollections.InvokeFunc(); }
        catch (Exception ex) { _log.Debug(ex, "[UMC] GetCollections failed"); return null; }
    }

    /// <summary>
    /// Returns the collection currently assigned to a game object by table index.
    /// Index 0 is always the player character.
    /// Returns null when there is no valid object at that index or on failure.
    /// </summary>
    public (Guid Id, string Name)? GetCollectionForObject(int gameObjectIndex)
    {
        try
        {
            var (objectValid, _, collection) = _getCollectionForObject.InvokeFunc(gameObjectIndex);
            return objectValid ? collection : null;
        }
        catch (Exception ex)
        {
            _log.Debug(ex, "[UMC] GetCollectionForObject failed for index {0}", gameObjectIndex);
            return null;
        }
    }

    /// <summary>The collection Penumbra's own window currently edits, or null.</summary>
    public (Guid Id, string Name)? GetCurrentCollection()
    {
        try   { return _getCollection.InvokeFunc(CurrentCollectionType); }
        catch (Exception ex) { _log.Debug(ex, "[UMC] GetCollection failed"); return null; }
    }

    /// <summary>
    /// The names of everything the mods enabled in a collection change, as Penumbra lists them:
    /// item names, <c>Customization: …</c>, <c>Emote: …</c> and so on. Only the mod that wins a
    /// file counts. Empty for a collection nothing uses; null on failure.
    /// </summary>
    public IReadOnlyCollection<string>? GetChangedItems(Guid collection)
    {
        try   { return _getChangedItemsForCollection.InvokeFunc(collection).Keys; }
        catch (Exception ex) { _log.Debug(ex, "[UMC] GetChangedItemsForCollection failed"); return null; }
    }

    /// <summary>
    /// The names of the mods that change <paramref name="changedItem"/> (a name from
    /// <see cref="GetChangedItems"/>) in the collection Penumbra's window edits. Empty when none
    /// does; null on failure.
    /// </summary>
    public string[]? GetCurrentChangedItemMods(string changedItem)
    {
        // A function kept from before Penumbra reloaded throws; the second attempt asks for a new one.
        for (var attempt = 0; attempt < 2; attempt++)
        {
            try
            {
                _currentChangedItemMods ??= _checkCurrentChangedItemFunc.InvokeFunc();
                return _currentChangedItemMods(changedItem).Select(m => m.Item2).ToArray();
            }
            catch (ObjectDisposedException) { _currentChangedItemMods = null; }
            catch (Exception ex) { _log.Debug(ex, "[UMC] CheckCurrentChangedItemFunc failed"); return null; }
        }
        return null;
    }

    /// <summary>
    /// What each game path loads in <paramref name="collection"/>, in the same order: a file on
    /// disk, another game path for a file swap, or the path itself when no mod changes it.
    /// Null on failure.
    /// </summary>
    public string[]? ResolvePaths(Guid collection, string[] gamePaths)
    {
        try
        {
            var (ret, resolved, _) = _resolvePaths.InvokeFunc(collection, gamePaths, []);
            if ((PenumbraApiEc)ret == PenumbraApiEc.Success) return resolved;
            _log.Debug($"[UMC] ResolvePaths returned {(PenumbraApiEc)ret}");
            return null;
        }
        catch (Exception ex) { _log.Debug(ex, "[UMC] ResolvePaths failed"); return null; }
    }
}

/// <summary>Mirrors Penumbra's PenumbraApiEc enum (int values).</summary>
public enum PenumbraApiEc : int
{
    Success            = 0,
    NothingDone        = 1,
    InvalidArgument    = 2,
    ModMissing         = 3,
    CollectionMissing  = 4,
    OptionGroupMissing = 5,
    OptionMissing      = 6,
    PathMissing        = 7,
    Default            = 8,
    SystemCollection   = 9,
    Disposed           = 10,
    UnknownError       = 11,
}
