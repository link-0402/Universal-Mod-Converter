using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text.RegularExpressions;
using AdvancedPenumbraItemConverter.Core;
using AdvancedPenumbraItemConverter.Models;
using Dalamud.Plugin.Services;
using Lumina.Excel.Sheets;

namespace AdvancedPenumbraItemConverter.Services;

// ─────────────────────────────────────────────────────────────────────────────
// DTOs
// ─────────────────────────────────────────────────────────────────────────────

/// <summary>An equipment/accessory item ID + slot detected inside a mod directory.</summary>
public sealed class DetectedItem
{
    public AssetKind Kind         { get; init; } = AssetKind.Gear;
    /// <summary>The equipment slot this item occupies.</summary>
    public EquipSlot Slot         { get; init; }

    /// <summary>Four-digit zero-padded model ID found in the mod paths (e.g. "0164").</summary>
    public string ModelIdPadded   { get; init; } = string.Empty;

    /// <summary>Variant index resolved from the Item sheet (bits 16-31 of ModelMain).</summary>
    public ushort Variant         { get; init; }

    /// <summary>Full display ID including variant suffix, e.g. "0585-2".</summary>
    public string ModelIdDisplay  => $"{ModelIdPadded}-{Variant}";

    /// <summary>Whether this is an accessory (prefix 'a') rather than equipment (prefix 'e').</summary>
    public bool   IsAccessory     { get; init; }

    public bool   IsFacewear      { get; init; }

    public ushort? GenderRace     { get; init; }

    public bool IsCustomization => CustomizationKinds.IsCustomization(Kind);

    public bool IsAmbiguous      { get; init; }

    /// <summary>Game item name resolved from the Item sheet, or "Unknown (ID {n})" if not found.</summary>
    public string ItemName        { get; init; } = string.Empty;

    /// <summary>Game icon of the resolved item, 0 when unknown or for customization roots.</summary>
    public uint   Icon            { get; init; }
}

/// <summary>A face, hair, tail or ear ID a player can choose.</summary>
/// <param name="Label">Display name, e.g. "Face 101 (Keeper of the Moon)".</param>
/// <param name="Clans">The clans that have it, when not all clans of the race do; otherwise null.</param>
public sealed record CustomizationOption(ushort Id, string Label, string? Clans);

/// <summary>A searchable game item from the Item excel sheet.</summary>
public sealed class GameItem
{
    public uint      RowId        { get; init; }
    public string    Name         { get; init; } = string.Empty;

    /// <summary>Four-digit zero-padded model ID (lower 16 bits of ModelMain).</summary>
    public ushort    ModelId      { get; init; }

    /// <summary>Variant index (bits 16-31 of ModelMain, 1-based as stored by the game).</summary>
    public ushort    Variant      { get; init; }

    /// <summary>Four-digit zero-padded string for display / comparison.</summary>
    public string    ModelIdPadded => ModelId.ToString("D4");

    /// <summary>Full display ID including variant suffix, e.g. "0585-2".</summary>
    public string    ModelIdDisplay => $"{ModelIdPadded}-{Variant}";

    public EquipSlot Slot         { get; init; }
    public bool      IsAccessory  { get; init; }
    public bool      IsFacewear   { get; init; }

    /// <summary>Game icon ID, 0 when the item has none.</summary>
    public uint      Icon         { get; init; }
}

// ─────────────────────────────────────────────────────────────────────────────
// Service
// ─────────────────────────────────────────────────────────────────────────────

/// <summary>
/// Provides two capabilities:
/// <list type="bullet">
/// <item>Scan a Penumbra mod directory and detect which game items (slot + model ID) it
///       already replaces, resolved to human-readable names from game data.</item>
/// <item>Search the game's Item sheet by name to let the user pick a target item.</item>
/// </list>
/// </summary>
public sealed class GameDataService : IGameFileProvider
{
    private readonly IDataManager _data;
    private readonly IPluginLog   _log;

    // Lazily-built searchable list of equipment/accessory items. Built once, possibly
    // off the framework thread (see WarmUp), then only read.
    private List<GameItem>? _itemCache;
    private readonly object _itemCacheLock = new();

    // Matches tokens like e0164_top, a0123_ear — anywhere in a file path or JSON text.
    // Negative lookbehind only excludes preceding *letters* (not digits) so that
    // race-coded filenames like c0201e0164_top.mdl are matched correctly.
    private static readonly Regex TokenRx = new(
        @"(?<![a-zA-Z])([ea])(\d{4})_(met|top|glv|dwn|sho|ear|nek|wrs|rir|ril)(?![a-zA-Z0-9])",
        RegexOptions.IgnoreCase | RegexOptions.Compiled);

    public GameDataService(IDataManager data, IPluginLog log)
    {
        _data = data;
        _log  = log;
    }

    public byte[]? GetHumanPbdBytes() => GetRawFileBytes("chara/xls/boneDeformer/human.pbd");

    public byte[]? GetRawFileBytes(string gamePath)
    {
        try { return _data.GetFile(gamePath)?.Data; }
        catch (Exception ex)
        {
            _log.Warning(ex, "[APIC] Could not read game file {0}", gamePath);
            return null;
        }
    }

    byte[]? IGameFileProvider.ReadFile(string gamePath) => GetRawFileBytes(gamePath);

    bool IGameFileProvider.FileExists(string gamePath)
    {
        try { return _data.FileExists(gamePath); }
        catch (Exception) { return false; }
    }

    private readonly System.Collections.Concurrent.ConcurrentDictionary<(AssetKind, ushort), ushort[]> _customizationIds = new();

    /// <summary>Builds the item cache in the background so the first target list opens instantly.</summary>
    public void WarmUp() => Task.Run(() => _ = GetItemCache());

    /// <summary>True once the item list has been built; until then target lists are empty.</summary>
    public bool ItemsReady => Volatile.Read(ref _itemCache) != null;

    // ─────────────────────────────────────────────────────────────────────────
    // Player customization options
    // ─────────────────────────────────────────────────────────────────────────

    private Dictionary<(AssetKind, ushort), CustomizationOption[]>? _playerOptions;
    private readonly object _playerOptionsLock = new();
    private int _playerOptionsStarted;

    /// <summary>"Face 101", "Hair 12", "Tail 3", "Ears 2".</summary>
    public static string OptionLabel(AssetKind kind, ushort id) => kind switch
    {
        AssetKind.VieraEar => $"Ears {id}",
        _                  => $"{CustomizationKinds.Get(kind).DisplayName} {id}",
    };

    /// <summary>
    /// Non-blocking variant of <see cref="GetCustomizationOptions"/> for the UI: returns null
    /// and builds the option table in the background when it is not ready yet.
    /// </summary>
    public IReadOnlyList<CustomizationOption>? TryGetCustomizationOptions(AssetKind kind, ushort genderRace)
    {
        if (Volatile.Read(ref _playerOptions) is { } table) return Lookup(table, kind, genderRace);
        if (Interlocked.Exchange(ref _playerOptionsStarted, 1) == 0) Task.Run(() => _ = PlayerOptions());
        return null;
    }

    /// <summary>
    /// The IDs of <paramref name="kind"/> a player can choose for <paramref name="genderRace"/>,
    /// taken from the character creation data (including unlockable hairstyles), so NPC-only
    /// faces, hairs and tails are excluded. Falls back to every model that exists when the
    /// sheets cannot be read.
    /// </summary>
    public IReadOnlyList<CustomizationOption> GetCustomizationOptions(AssetKind kind, ushort genderRace)
        => Lookup(PlayerOptions(), kind, genderRace);

    /// <summary>Label for any root, e.g. "Face 101 (Keeper of the Moon)" or "Face 91 (NPC)".</summary>
    public string DescribeCustomization(AssetKind kind, ushort genderRace, ushort id)
    {
        var option = GetCustomizationOptions(kind, genderRace).FirstOrDefault(o => o.Id == id);
        return option?.Label ?? $"{OptionLabel(kind, id)} (NPC)";
    }

    private IReadOnlyList<CustomizationOption> Lookup(Dictionary<(AssetKind, ushort), CustomizationOption[]> table,
        AssetKind kind, ushort genderRace)
    {
        if (table.Count > 0) return table.GetValueOrDefault((kind, genderRace)) ?? [];
        return GetCustomizationIds(kind, genderRace).Select(id => new CustomizationOption(id, OptionLabel(kind, id), null)).ToArray();
    }

    private Dictionary<(AssetKind, ushort), CustomizationOption[]> PlayerOptions()
    {
        if (Volatile.Read(ref _playerOptions) is { } ready) return ready;
        lock (_playerOptionsLock)
        {
            if (_playerOptions != null) return _playerOptions;
            var built = BuildPlayerOptions();
            Volatile.Write(ref _playerOptions, built);
            return built;
        }
    }

    // Character creation menus, by the customize index they edit.
    private const uint CustomizeFace = 5;
    private const uint CustomizeHair = 6;
    private const uint CustomizeTail = 22;

    private Dictionary<(AssetKind, ushort), CustomizationOption[]> BuildPlayerOptions()
    {
        var ids   = new Dictionary<(AssetKind, ushort), SortedDictionary<ushort, SortedSet<string>>>();
        var clans = new Dictionary<ushort, SortedSet<string>>();

        void Add(AssetKind kind, ushort code, ushort id, string clan)
        {
            var descriptor = CustomizationKinds.Get(kind);
            if (id == 0 || !descriptor.SupportsRace(code) || !((IGameFileProvider)this).FileExists(descriptor.ModelPath(code, id)))
                return;
            if (!ids.TryGetValue((kind, code), out var byId)) ids[(kind, code)] = byId = new();
            if (!byId.TryGetValue(id, out var owners)) byId[id] = owners = new(StringComparer.Ordinal);
            owners.Add(clan);
        }

        try
        {
            foreach (var row in _data.GetExcelSheet<CharaMakeType>())
            {
                if (!TryRaceCode(row.Race.RowId, row.Tribe.RowId, row.Gender, out var code)) continue;
                var clan = ClanName(row.Tribe.RowId, row.Gender);
                if (!clans.TryGetValue(code, out var known)) clans[code] = known = new(StringComparer.Ordinal);
                known.Add(clan);

                foreach (var menu in row.CharaMakeStruct)
                {
                    var values = menu.SubMenuGraphic.Take(menu.SubMenuNum).Where(v => v != 0).Select(v => (ushort)v);
                    if (menu.Customize == CustomizeFace && menu.SubMenuType == 1)
                    {
                        // The second clan of a race uses faces 101+ unless both clans share one set.
                        var secondClan = row.Tribe.RowId % 2 == 0;
                        foreach (var value in values)
                        {
                            var shifted = (ushort)(value + 100);
                            var own = secondClan && ((IGameFileProvider)this).FileExists(
                                CustomizationKinds.Get(AssetKind.Face).ModelPath(code, shifted));
                            Add(AssetKind.Face, code, own ? shifted : value, clan);
                        }
                    }
                    else if (menu.Customize == CustomizeTail && menu.SubMenuType == 1)
                    {
                        var kind = CustomizationKinds.Get(AssetKind.VieraEar).SupportsRace(code) ? AssetKind.VieraEar : AssetKind.Tail;
                        foreach (var value in values) Add(kind, code, value, clan);
                    }
                }
            }

            // Hairstyles, including unlockable ones. Lists longer than 100 continue in the
            // struct's overflow column.
            var customize = _data.GetExcelSheet<CharaMakeCustomize>();
            foreach (var row in _data.GetExcelSheet<HairMakeType>())
            {
                if (!TryRaceCode(row.Race.RowId, row.Tribe.RowId, row.Gender, out var code)) continue;
                var clan = ClanName(row.Tribe.RowId, row.Gender);
                foreach (var menu in row.CharaMakeStruct.Where(m => m.Customize == CustomizeHair))
                    foreach (var rowId in menu.SubMenuParam.Concat(menu.Unknown0).Take(menu.SubMenuNum).Where(r => r != 0))
                        if (customize.GetRowOrDefault(rowId) is { } entry)
                            Add(AssetKind.Hair, code, entry.FeatureID, clan);
            }
        }
        catch (Exception ex)
        {
            _log.Warning(ex, "[APIC] Could not read the character creation sheets; all existing customization models are offered.");
            return new();
        }

        var table = new Dictionary<(AssetKind, ushort), CustomizationOption[]>();
        foreach (var ((kind, code), byId) in ids)
        {
            var all = clans.GetValueOrDefault(code) ?? new SortedSet<string>();
            table[(kind, code)] = byId
                .Select(entry =>
                {
                    // Name the clans only when not every clan of this race/gender has the option.
                    var owners = entry.Value.Count < all.Count ? string.Join(", ", entry.Value) : null;
                    var label  = OptionLabel(kind, entry.Key) + (owners == null ? string.Empty : $" ({owners})");
                    return new CustomizationOption(entry.Key, label, owners);
                })
                .ToArray();
        }
        _log.Information("[APIC] Player customization options: {0} race/kind combination(s).", table.Count);
        return table;
    }

    /// <summary>
    /// Maps a Race/Tribe/gender row to its model race code. Hyur clans have separate codes
    /// (Midlander c01/c02, Highlander c03/c04); the other races share one code per gender.
    /// </summary>
    private static bool TryRaceCode(uint race, uint tribe, sbyte gender, out ushort code)
    {
        code = 0;
        int? baseCode = (race, tribe) switch
        {
            (1, 1) => 1, (1, 2) => 3, (2, _) => 5, (3, _) => 11, (4, _) => 7,
            (5, _) => 9, (6, _) => 13, (7, _) => 15, (8, _) => 17, _ => null,
        };
        if (baseCode is not { } b || gender is not (0 or 1)) return false;
        code = (ushort)((b + gender) * 100 + 1);
        return true;
    }

    private string ClanName(uint tribe, sbyte gender)
    {
        try
        {
            if (_data.GetExcelSheet<Tribe>().GetRowOrDefault(tribe) is { } row)
            {
                var name = (gender == 1 ? row.Feminine : row.Masculine).ExtractText();
                if (!string.IsNullOrWhiteSpace(name)) return name;
            }
        }
        catch (Exception)
        {
            // Fall through to the numeric name.
        }
        return $"Clan {tribe}";
    }

    /// <summary>
    /// Returns the IDs of <paramref name="kind"/> that exist in the game for
    /// <paramref name="genderRace"/>, found by probing the model each ID loads. This
    /// includes NPC-only models; prefer <see cref="GetCustomizationOptions"/>.
    /// </summary>
    public IReadOnlyList<ushort> GetCustomizationIds(AssetKind kind, ushort genderRace)
    {
        if (_customizationIds.TryGetValue((kind, genderRace), out var cached)) return cached;
        var descriptor = CustomizationKinds.Get(kind);
        var ids = new List<ushort>();
        if (descriptor.SupportsRace(genderRace))
            for (ushort id = 1; id <= MaxCustomizationId; id++)
            {
                try
                {
                    if (_data.FileExists(descriptor.ModelPath(genderRace, id))) ids.Add(id);
                }
                catch (Exception)
                {
                    // Treated as missing.
                }
            }
        return _customizationIds[(kind, genderRace)] = ids.ToArray();
    }

    private const ushort MaxCustomizationId = 999;

    // ─────────────────────────────────────────────────────────────────────────
    // Item search
    // ─────────────────────────────────────────────────────────────────────────

    /// <summary>
    /// Returns up to <paramref name="max"/> equipment/accessory game items whose name contains
    /// <paramref name="query"/>. Prefix matches are listed before substring matches.
    /// </summary>
    public List<GameItem> SearchItems(string query, int max = 100)
    {
        if (string.IsNullOrWhiteSpace(query)) return new();

        var q     = query.Trim();
        var cache = GetItemCache();

        var starts   = cache
            .Where(i => i.Name.StartsWith(q, StringComparison.OrdinalIgnoreCase))
            .OrderBy(i => i.Name, StringComparer.OrdinalIgnoreCase);

        var contains = cache
            .Where(i => !i.Name.StartsWith(q, StringComparison.OrdinalIgnoreCase)
                     &&  i.Name.Contains(q,  StringComparison.OrdinalIgnoreCase))
            .OrderBy(i => i.Name, StringComparer.OrdinalIgnoreCase);

        return starts.Concat(contains).Take(max).ToList();
    }

    /// <summary>
    /// Like <see cref="SearchItems(string,int)"/> but restricted to a specific slot.
    /// </summary>
    public List<GameItem> SearchItems(string query, EquipSlot slot, int max = 100)
    {
        if (string.IsNullOrWhiteSpace(query)) return new();

        var q     = query.Trim();
        var cache = GetItemCache().Where(i => i.Slot == slot);

        var starts   = cache
            .Where(i => i.Name.StartsWith(q, StringComparison.OrdinalIgnoreCase))
            .OrderBy(i => i.Name, StringComparer.OrdinalIgnoreCase);

        var contains = cache
            .Where(i => !i.Name.StartsWith(q, StringComparison.OrdinalIgnoreCase)
                     &&  i.Name.Contains(q,  StringComparison.OrdinalIgnoreCase))
            .OrderBy(i => i.Name, StringComparer.OrdinalIgnoreCase);

        return starts.Concat(contains).Take(max).ToList();
    }

    /// <summary>
    /// Returns the number of items in the cache (useful for diagnostics / status display).
    /// Triggers a cache build if needed.
    /// </summary>
    public int CacheCount => GetItemCache().Count;

    /// <summary>
    /// Returns all equipment/accessory items for <paramref name="slot"/>, sorted by name.
    /// This is the full unfiltered list used to populate the target item picker.
    /// </summary>
    public List<GameItem> GetAllItemsForSlot(EquipSlot slot)
        => GetItemCache()
            .Where(i => i.Slot == slot)
            .OrderBy(i => i.Name, StringComparer.OrdinalIgnoreCase)
            .ToList();

    // ─────────────────────────────────────────────────────────────────────────
    // Mod scan
    // ─────────────────────────────────────────────────────────────────────────

    /// <summary>
    /// Scans <paramref name="modDir"/> recursively for file paths and JSON content that
    /// contain equipment/accessory item tokens (e.g. <c>e0164_top</c>, <c>a0123_ear</c>).
    /// Each distinct (slot, modelId) combination is resolved to a game item name and
    /// returned as a <see cref="DetectedItem"/>.
    /// </summary>
    public List<DetectedItem> ScanModForItems(string modDir)
    {
        if (!Directory.Exists(modDir)) return new();

        // Key: (slot, 4-digit-id), Value: isAccessory
        var found = new Dictionary<(EquipSlot, string), bool>();
        var custom = new HashSet<(AssetKind Kind, ushort Race, string Id)>();

        try
        {
            // Only the game paths a mod redirects decide what it changes; local file names are
            // arbitrary. This reads both the legacy multi-file and the Penumbra 1.7+ layout.
            var mod = PenumbraMod.Load(modDir);
            foreach (var container in mod.Containers)
            {
                foreach (var gamePath in container.FileEntries().Select(e => e.Key)
                             .Concat(container.SwapEntries().Select(e => e.Key)))
                {
                    var normalized = GamePath.Normalize(gamePath);
                    if (normalized.StartsWith("chara/equipment/", StringComparison.Ordinal) ||
                        normalized.StartsWith("chara/accessory/", StringComparison.Ordinal))
                        foreach (Match m in TokenRx.Matches(Path.GetFileName(normalized)))
                            RecordToken(m, found);
                }
            }

            foreach (var root in CustomizationDetection.FindRoots(mod, modDir))
                custom.Add((root.Kind, root.GenderRace, root.ModelId.ToString("D4")));
        }
        catch (Exception ex)
        {
            _log.Warning(ex, "[APIC] ScanModForItems failed for {0}", modDir);
        }

        // Resolve names from game data
        var cache  = GetItemCache();
        var result = new List<DetectedItem>();

        foreach (var ((slot, id), isAcc) in found
                     .OrderBy(k => (int)k.Key.Item1)
                     .ThenBy(k => k.Key.Item2))
        {
            if (!ushort.TryParse(id, out var modelId)) continue;

            var logicalSlots = slot == EquipSlot.Head
                ? new[] { EquipSlot.Head, EquipSlot.Facewear }
                : new[] { slot };
            foreach (var logicalSlot in logicalSlots)
            {
                var matches = cache.Where(i => i.ModelId == modelId && i.Slot == logicalSlot).ToList();
                if (logicalSlot == EquipSlot.Facewear && matches.Count == 0) continue;
                if (matches.Count == 0)
                {
                    result.Add(new DetectedItem
                    {
                        Kind          = AssetKind.Gear,
                        Slot          = logicalSlot,
                        ModelIdPadded = id,
                        IsAccessory   = isAcc,
                        ItemName      = $"Unknown (ID {modelId})",
                    });
                    continue;
                }

                foreach (var variantGroup in matches.GroupBy(i => i.Variant))
                {
                    var match = variantGroup.First();
                    var ambiguous = variantGroup.Skip(1).Any();
                    result.Add(new DetectedItem
                    {
                        Kind          = logicalSlot == EquipSlot.Facewear ? AssetKind.Facewear : AssetKind.Gear,
                        Slot          = logicalSlot,
                        ModelIdPadded = id,
                        Variant       = match.Variant,
                        IsAccessory   = match.IsAccessory,
                        IsFacewear    = match.IsFacewear,
                        IsAmbiguous   = ambiguous,
                        Icon          = match.Icon,
                        ItemName      = ambiguous ? $"{match.Name} (+{variantGroup.Count() - 1} shared items)" : match.Name,
                    });
                }
            }
        }

        foreach (var entry in custom.OrderBy(c => c.Kind).ThenBy(c => c.Race).ThenBy(c => c.Id))
            result.Add(new DetectedItem
            {
                Kind          = entry.Kind,
                Slot          = EquipSlot.Head,
                ModelIdPadded = entry.Id,
                GenderRace    = entry.Race,
                ItemName      = DescribeCustomization(entry.Kind, entry.Race, ushort.Parse(entry.Id)),
            });

        return result;
    }

    // ─────────────────────────────────────────────────────────────────────────
    // Internal helpers
    // ─────────────────────────────────────────────────────────────────────────

    private static void RecordToken(Match m, Dictionary<(EquipSlot, string), bool> found)
    {
        var prefix  = char.ToLower(m.Groups[1].Value[0]); // 'e' or 'a'
        var id      = m.Groups[2].Value;                  // "0164"
        var slotKey = m.Groups[3].Value.ToLower();        // "top" etc.

        if (!SlotInfo.ReverseMap.TryGetValue(slotKey, out var slot)) return;

        bool isAcc = prefix == 'a';
        found.TryAdd((slot, id), isAcc);
    }

    private List<GameItem> GetItemCache()
    {
        if (Volatile.Read(ref _itemCache) is { } ready) return ready;
        lock (_itemCacheLock)
        {
            if (_itemCache != null) return _itemCache;
            var built = BuildItemCache();
            Volatile.Write(ref _itemCache, built);
            return built;
        }
    }

    private List<GameItem> BuildItemCache()
    {

        var list = new List<GameItem>(8192);
        try
        {
            var sheet = _data.GetExcelSheet<Item>();
            if (sheet == null) goto done;

            foreach (var row in sheet)
            {
                // Skip items with no name
                var name = row.Name.ToString();
                if (string.IsNullOrWhiteSpace(name)) continue;

                // Skip items with no model
                var modelMain = row.ModelMain;
                if (modelMain == 0) continue;

                var primaryId = (ushort)(modelMain & 0xFFFF);
                if (primaryId == 0) continue;

                // Skip if we cannot map to an equipment/accessory slot we support
                if (!TryGetEquipSlot(row, out var slot, out var isAcc)) continue;

                var variant = (ushort)((modelMain >> 16) & 0xFFFF);

                list.Add(new GameItem
                {
                    RowId       = row.RowId,
                    Name        = name,
                    ModelId     = primaryId,
                    Variant     = variant,
                    Slot        = slot,
                    IsAccessory = isAcc,
                    Icon        = row.Icon,
                });
            }

            var glassesSheet = _data.GetExcelSheet<Glasses>();
            if (glassesSheet != null)
            {
                foreach (var row in glassesSheet)
                {
                    var name = row.Name.ToString();
                    if (string.IsNullOrWhiteSpace(name) || row.Model == 0) continue;
                    list.Add(new GameItem
                    {
                        RowId       = row.RowId,
                        Name        = name,
                        ModelId     = (ushort)(row.Model & 0xFFFF),
                        Variant     = (ushort)((row.Model >> 16) & 0xFFFF),
                        Slot        = EquipSlot.Facewear,
                        IsFacewear  = true,
                        Icon        = (uint)row.Icon,
                    });
                }
            }
        }
        catch (Exception ex)
        {
            _log.Warning(ex, "[APIC] Failed to build item name cache");
        }

        done:
        _log.Information("[APIC] Item cache built: {0} equipment/accessory items", list.Count);
        return list;
    }

    /// <summary>
    /// Inspects the item's <c>EquipSlotCategory</c> sub-row to determine which
    /// equipment slot it occupies. Returns false for weapons, offhands and anything
    /// outside the ten slots we support.
    /// </summary>
    private static bool TryGetEquipSlot(Item row, out EquipSlot slot, out bool isAcc)
    {
        slot  = EquipSlot.Body;
        isAcc = false;

        try
        {
            var catRowId = row.EquipSlotCategory.RowId;
            if (catRowId == 0) return false;

            var cat = row.EquipSlotCategory.Value;

            // Equipment (prefix 'e')
            if (cat.Head   != 0) { slot = EquipSlot.Head;      return true; }
            if (cat.Body   != 0) { slot = EquipSlot.Body;      return true; }
            if (cat.Gloves != 0) { slot = EquipSlot.Hands;     return true; }
            if (cat.Legs   != 0) { slot = EquipSlot.Legs;      return true; }
            if (cat.Feet   != 0) { slot = EquipSlot.Feet;      return true; }

            // Accessories (prefix 'a')
            if (cat.Ears     != 0) { slot = EquipSlot.Earring;   isAcc = true; return true; }
            if (cat.Neck     != 0) { slot = EquipSlot.Neck;      isAcc = true; return true; }
            if (cat.Wrists   != 0) { slot = EquipSlot.Wrists;    isAcc = true; return true; }
            if (cat.FingerR  != 0) { slot = EquipSlot.RingRight; isAcc = true; return true; }
            if (cat.FingerL  != 0) { slot = EquipSlot.RingLeft;  isAcc = true; return true; }
        }
        catch
        {
            // Sub-row might not resolve for some items — skip silently
        }

        return false;
    }
}
