using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Threading;
using UniversalModConverter.Core;
using UniversalModConverter.Services;

namespace UniversalModConverter.Session;

/// <summary>
/// Which targets other mods already change, the way Penumbra's Item Swap tab marks them, in the
/// collection the player's character uses (Penumbra's current collection when there is no
/// character). Items, hair, faces, tails, ears and skins are looked up among the collection's
/// changed items by the names Penumbra gives them. Animations are looked up by resolving the
/// game paths a swap writes, because Penumbra names only emotes, and those by file name alone.
/// <para>
/// Every query returns why the target is marked (who changes it), or null. Everything is read
/// from Penumbra when first asked for and kept until Penumbra reports a change, or until a
/// routine re-read finds the collection changing something else; a closed window costs nothing.
/// </para>
/// </summary>
public sealed class ModdedTargets
{
    /// <summary>How long a reported change settles before it is read: Penumbra may still be rebuilding the collection.</summary>
    private const long SettleMs = 300;

    /// <summary>How often the player's collection assignment is checked.</summary>
    private const long PollMs = 1000;

    /// <summary>How often the changed items are read again anyway, for changes Penumbra sends no event for (inheritance, for one).</summary>
    private const long RefreshMs = 10_000;

    private readonly PenumbraIpcService _ipc;
    private readonly Func<string, string?> _modName;
    private readonly Func<string?> _modRoot;

    private (Guid Id, string Name)? _collection;

    /// <summary>Whether the collection is the one Penumbra's window edits, the only one Penumbra names the mods behind a changed item for.</summary>
    private bool _isCurrent;

    /// <summary>Every changed item of the collection; item names are among them as they are.</summary>
    private HashSet<string> _keys = new(StringComparer.Ordinal);
    private readonly Dictionary<ChangedCustomization, List<string>> _customizations = new();

    // What each query answered, so a marked row does not ask Penumbra again every frame.
    private readonly Dictionary<string, string> _itemNotes = new(StringComparer.Ordinal);
    private readonly Dictionary<ChangedCustomization, string?> _customizationNotes = new();
    private readonly Dictionary<(object Scope, object Target), string?> _paths = new();

    private bool _loaded;
    private long _loadedAt;
    private long _polledAt;
    private long _changedAt;

    /// <summary>The last reported change a reload has taken in.</summary>
    private long _handledChangeAt;

    private int _version;

    /// <param name="modName">A mod's display name by its folder under Penumbra's mod root, or null.</param>
    /// <param name="modRoot">Penumbra's mod root, or null.</param>
    public ModdedTargets(PenumbraIpcService ipc, Func<string, string?> modName, Func<string?> modRoot)
    {
        _ipc     = ipc;
        _modName = modName;
        _modRoot = modRoot;
        ipc.ModsChanged += Invalidate;
    }

    /// <summary>Reads everything again once it settles. Safe to call from any thread.</summary>
    public void Invalidate() => Interlocked.Exchange(ref _changedAt, Environment.TickCount64);

    /// <summary>
    /// Changes whenever the answers may have: lists filtered by them can be kept until it does.
    /// </summary>
    public int Version
    {
        get
        {
            Update();
            return _version;
        }
    }

    /// <summary>Who already changes the item, or null.</summary>
    public string? Item(GameItem item)
    {
        Update();
        var key = item.ChangedItemName;
        if (!_keys.Contains(key)) return null;
        if (!_itemNotes.TryGetValue(key, out var note)) _itemNotes[key] = note = Describe([key]);
        return note;
    }

    /// <summary>
    /// Who already changes a hair, face, tail, ear or body (<paramref name="kind"/> of
    /// <paramref name="genderRace"/>, numbered <paramref name="id"/>), or null. A body counts
    /// its race's skin textures too.
    /// </summary>
    public string? Customization(AssetKind kind, ushort genderRace, ushort id)
    {
        Update();
        var target = new ChangedCustomization(kind, genderRace, id);
        if (_customizationNotes.TryGetValue(target, out var note)) return note;
        var keys = _customizations.GetValueOrDefault(target);
        if (kind == AssetKind.Body && _customizations.GetValueOrDefault(target with { Id = 0 }) is { } skin)
            keys = keys == null ? skin : [.. keys, .. skin];
        _customizationNotes[target] = note = keys == null ? null : Describe(keys);
        return note;
    }

    /// <summary>
    /// Who already changes any of a target's game paths, or null. <paramref name="scope"/> and
    /// <paramref name="target"/> identify the paths, which are only built and resolved the first
    /// time they are asked for.
    /// </summary>
    public string? Paths(object scope, object target, Func<IEnumerable<string>> gamePaths)
    {
        Update();
        if (_collection is not { } collection || collection.Id == Guid.Empty) return null;
        if (_paths.TryGetValue((scope, target), out var cached)) return cached;

        string? note = null;
        var paths = gamePaths().Distinct(StringComparer.Ordinal).ToArray();
        if (paths.Length > 0 && _ipc.ResolvePaths(collection.Id, paths) is { } resolved)
        {
            // Unchanged paths come back as they were asked for.
            var mods = paths.Zip(resolved)
                .Where(p => !string.Equals(p.First, p.Second, StringComparison.OrdinalIgnoreCase))
                .Select(p => ModBehind(p.Second))
                .Distinct(StringComparer.OrdinalIgnoreCase)
                .ToList();
            if (mods.Count > 0) note = Describe(collection.Name, mods);
        }
        _paths[(scope, target)] = note;
        return note;
    }

    private void Update()
    {
        var now = Environment.TickCount64;
        var changedAt = Interlocked.Read(ref _changedAt);
        var reported = changedAt > _handledChangeAt && now - changedAt >= SettleMs;
        var due = !_loaded || reported || now - _loadedAt >= RefreshMs;
        if (!due && now - _polledAt < PollMs) return;

        _polledAt = now;
        (Guid Id, string Name)? collection = null, current = null;
        if (_ipc.IsAvailable)
        {
            current = _ipc.GetCurrentCollection();
            collection = _ipc.GetCollectionForObject(0) ?? current;
        }
        if (!due && collection == _collection) return;

        // Penumbra's empty collection ("None") changes nothing, and asking about it logs a warning there.
        var keys = collection is { } c && c.Id != Guid.Empty
            ? new HashSet<string>(_ipc.GetChangedItems(c.Id) ?? [], StringComparer.Ordinal)
            : new HashSet<string>(StringComparer.Ordinal);
        var isCurrent = collection != null && collection.Value.Id == current?.Id;
        var unchanged = _loaded && !reported && collection == _collection && isCurrent == _isCurrent && keys.SetEquals(_keys);
        _loaded = true;
        _loadedAt = now;
        if (reported) _handledChangeAt = changedAt;
        // A routine re-read that finds nothing new keeps every answer, so a filtered list does
        // not look all of its targets up again.
        if (unchanged) return;

        _version++;
        _collection = collection;
        _isCurrent = isCurrent;
        _keys = keys;
        _customizations.Clear();
        _itemNotes.Clear();
        _customizationNotes.Clear();
        _paths.Clear();
        foreach (var key in keys)
        {
            if (!ChangedItemKeys.TryParseCustomization(key, out var customization)) continue;
            if (!_customizations.TryGetValue(customization, out var named)) _customizations[customization] = named = [];
            named.Add(key);
        }
    }

    /// <summary>The mods behind changed items, when Penumbra can name them.</summary>
    private string Describe(IReadOnlyList<string> keys)
    {
        var name = _collection?.Name ?? string.Empty;
        if (!_isCurrent) return $"Already changed by a mod in the collection '{name}'.";
        var mods = keys.SelectMany(key => _ipc.GetCurrentChangedItemMods(key) ?? [])
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .ToList();
        return mods.Count == 0 ? $"Already changed by a mod in the collection '{name}'." : Describe(name, mods);
    }

    private static string Describe(string collection, IReadOnlyList<string> mods)
        => $"Already changed in the collection '{collection}' by:\n{string.Join("\n", mods.Select(m => $"• {m}"))}";

    /// <summary>The mod a resolved path belongs to, by the folder it lies in under Penumbra's mod root.</summary>
    private string ModBehind(string resolved)
    {
        // A file swap redirects to another game file and does not say whose it is.
        if (!Path.IsPathRooted(resolved)) return "a file swap";
        if (_modRoot() is { } root)
        {
            var relative = Path.GetRelativePath(root, resolved);
            if (!Path.IsPathRooted(relative) && !relative.StartsWith("..", StringComparison.Ordinal))
            {
                var folder = relative.Split(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar)[0];
                return _modName(folder) ?? folder;
            }
        }
        // Temporary mods, such as this plugin's own part preview, keep their files elsewhere.
        return "a temporary mod";
    }
}
