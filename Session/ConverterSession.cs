using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using UniversalModConverter.Core;
using UniversalModConverter.Models;
using UniversalModConverter.Services;

namespace UniversalModConverter.Session;

public sealed record ModEntry(string Name, string Directory, string Folder);

public enum BannerKind
{
    Success,
    Info,
    Warning,
    Error,
}

/// <summary>What a plan converts, which decides what each output mode does with it.</summary>
[Flags]
public enum PlanContents
{
    None = 0,

    /// <summary>Gear or facewear.</summary>
    Gear = 1,

    /// <summary>Hair, a face, a tail, Viera ears or a skin, converted rather than fanned out.</summary>
    Customization = 2,

    AnimationSwap = 4,
    AnimationRetarget = 8,

    /// <summary>An expression attached where the animation is: "Only add an expression", or an idle kept in its slot.</summary>
    AnimationExpression = 16,

    /// <summary>An idle ticked in several slots: an option per slot, in a single-select group per race.</summary>
    AnimationSlotGroups = 32,

    Animation = AnimationSwap | AnimationRetarget | AnimationExpression | AnimationSlotGroups,
}

/// <summary>The outcome of the last conversion or revert, shown above the plan.</summary>
public sealed record ResultBanner(
    BannerKind Kind,
    string Title,
    string Message,
    string? Path = null,
    Guid? RecordId = null,
    string? RetryActivationFolder = null);

/// <summary>
/// All state and actions of the converter, independent of ImGui. The windows only read
/// from it and call its commands; everything here runs on the framework thread, and long
/// work goes through <see cref="Runner"/>.
/// </summary>
public sealed partial class ConverterSession
{
    private static readonly TimeSpan AutoPreviewDelay = TimeSpan.FromMilliseconds(600);
    private static readonly TimeSpan PenumbraPollInterval = TimeSpan.FromSeconds(1);

    public static readonly EquipSlot[] OutputSlots =
    [
        EquipSlot.Head, EquipSlot.Body, EquipSlot.Hands, EquipSlot.Legs, EquipSlot.Feet,
        EquipSlot.Earring, EquipSlot.Neck, EquipSlot.Wrists, EquipSlot.RingRight, EquipSlot.RingLeft,
        EquipSlot.Facewear,
    ];

    private readonly Plugin _plugin;
    private bool _initialized;
    private DateTime _lastPenumbraPoll = DateTime.MinValue;

    public ConverterSession(Plugin plugin)
    {
        _plugin         = plugin;
        OutputMode      = plugin.Configuration.OutputMode;
        TextureLayout   = plugin.Configuration.TextureLayout;
        TextureAsNewMod = plugin.Configuration.TextureAsNewMod;
        NewModOnlyConverted = plugin.Configuration.NewModOnlyConverted;
        HideModdedTargets = plugin.Configuration.HideModdedTargets;
        Modded          = new ModdedTargets(plugin.PenumbraIpc,
            folder => Mods.FirstOrDefault(m => string.Equals(m.Folder, folder, StringComparison.OrdinalIgnoreCase))?.Name,
            () => PenumbraModDirectory);
    }

    public BackgroundRunner Runner { get; } = new();
    public LogStore Log { get; } = new();

    /// <summary>Which targets other mods already change, for marking them in the target lists.</summary>
    public ModdedTargets Modded { get; }
    public ConversionHistoryService History => _plugin.History;
    public GameDataService GameData => _plugin.GameData;
    private Configuration Config => _plugin.Configuration;

    // ── Penumbra ─────────────────────────────────────────────────────────────

    public bool PenumbraAvailable { get; private set; }
    public IReadOnlyList<ModEntry> Mods { get; private set; } = [];

    // ── Selected mod ─────────────────────────────────────────────────────────

    public string ModDirectory { get; private set; } = string.Empty;
    public string? ModError { get; private set; }
    public IReadOnlyList<DetectedItem> DetectedItems { get; private set; } = [];
    public int SourceIndex { get; private set; } = -1;
    private bool _scanPending;

    public bool HasMod => ModDirectory.Length > 0;
    public DetectedItem? Source => SourceIndex >= 0 && SourceIndex < DetectedItems.Count ? DetectedItems[SourceIndex] : null;

    /// <summary>Penumbra display name of the selected mod, or its folder name.</summary>
    public string ModName { get; private set; } = string.Empty;

    private void UpdateModName()
    {
        var entry = Mods.FirstOrDefault(m => SamePath(m.Directory, ModDirectory));
        ModName = entry?.Name ?? Path.GetFileName(ModDirectory.TrimEnd('\\', '/'));
    }

    // ── Target ───────────────────────────────────────────────────────────────

    public EquipSlot TargetSlot { get; private set; } = EquipSlot.Body;
    public GameItem? TargetItem { get; private set; }
    public string TargetFilter { get; private set; } = string.Empty;
    public IReadOnlyList<GameItem> TargetCandidates { get; private set; } = [];
    private bool _candidatesWaitForItems;

    /// <summary>Leave targets other mods already change (see <see cref="Modded"/>) out of the target lists.</summary>
    public bool HideModdedTargets { get; private set; }

    public void SetHideModdedTargets(bool hide)
    {
        if (hide == HideModdedTargets) return;
        HideModdedTargets = hide;
        Config.HideModdedTargets = hide;
        Config.Save();
    }

    /// <summary>
    /// Whether a target list shows a target another mod may already change (<paramref name="modded"/>
    /// says who, or is null). A <paramref name="chosen"/> target (selected, ticked, or the source
    /// itself) always shows, so it can still be seen and unticked.
    /// </summary>
    public bool ShowsTarget(string? modded, bool chosen) => chosen || modded == null || !HideModdedTargets;

    private IReadOnlyList<GameItem>? _shownFrom;
    private GameItem? _shownChosen;
    private int _shownVersion;
    private IReadOnlyList<GameItem> _shownCandidates = [];

    /// <summary>
    /// The gear targets the list shows: <see cref="TargetCandidates"/>, less the ones other mods
    /// already change when <see cref="HideModdedTargets"/> is on. Kept until either changes.
    /// </summary>
    public IReadOnlyList<GameItem> ShownTargetCandidates
    {
        get
        {
            if (!HideModdedTargets) return TargetCandidates;
            var version = Modded.Version;
            if (!ReferenceEquals(_shownFrom, TargetCandidates) || !ReferenceEquals(_shownChosen, TargetItem) || _shownVersion != version)
            {
                _shownFrom       = TargetCandidates;
                _shownChosen     = TargetItem;
                _shownVersion    = version;
                _shownCandidates = TargetCandidates.Where(i => ShowsTarget(Modded.Item(i), ReferenceEquals(i, TargetItem))).ToList();
            }
            return _shownCandidates;
        }
    }

    public AssetKind TargetCustomizationKind { get; private set; } = AssetKind.Hair;
    public ushort TargetRace { get; private set; } = 101;
    public int TargetCustomizationId { get; private set; } = 1;

    /// <summary>
    /// Fan-out roots (textures, and face or skin materials): every further (race, ID) the source's
    /// paths are added for, e.g. a skin offered to Au Ra and Viera, or a face texture to several
    /// face IDs. The source itself is always kept and never part of this set.
    /// </summary>
    private readonly HashSet<(ushort Race, ushort Id)> _textureTargets = new();

    public IReadOnlyCollection<(ushort Race, ushort Id)> TextureTargets => _textureTargets;

    /// <summary>How many targets are checked for <paramref name="race"/>.</summary>
    public int TextureTargetCount(ushort race) => _textureTargets.Count(e => e.Race == race);

    public bool IsTextureTarget(ushort race, ushort id) => _textureTargets.Contains((race, id));

    /// <summary>Whether the selected root's paths can simply be added for further races or IDs.</summary>
    public bool CanFanOutTextures => Source is { IsCustomization: true, CanFanOut: true };

    /// <summary>Whether (race, ID) is the source itself, which a fan-out always keeps.</summary>
    public bool IsTextureSource(ushort race, ushort id)
        => Source is { GenderRace: { } sourceRace } source && TargetCustomizationKind == source.Kind &&
           race == sourceRace && ushort.TryParse(source.ModelIdPadded, out var sourceId) && id == sourceId;

    public void SetTextureTarget(ushort race, ushort id, bool on)
    {
        if (!AllowedTargetRaces.Contains(race) || IsTextureSource(race, id)) return;
        var key = (race, id);
        if (on ? !_textureTargets.Add(key) : !_textureTargets.Remove(key)) return;
        MarkDirty();
    }

    /// <summary>
    /// Fan-outs with materials: give each target its own copy of every material, with the texture
    /// paths inside moved to the target (so it gets its own face or skin textures where the mod has
    /// none), instead of sharing the source's material as it is.
    /// </summary>
    public bool RetargetMaterials { get; private set; } = true;

    public void SetRetargetMaterials(bool on)
    {
        if (on == RetargetMaterials) return;
        RetargetMaterials = on;
        MarkDirty();
    }

    private void ClearFanOut() => _textureTargets.Clear();

    // ── Output ───────────────────────────────────────────────────────────────

    /// <summary>Where gear, model and animation conversions write.</summary>
    public ConversionOutputMode OutputMode { get; private set; }

    /// <summary>How a texture fan-out adds its paths; see <see cref="UsesTextureOutput"/>.</summary>
    public TextureFanOutLayout TextureLayout { get; private set; }

    /// <summary>
    /// Whether a texture fan-out is written to a new mod, a copy of this one with the paths added,
    /// instead of this mod. Either way the source keeps working, so only these two apply to it.
    /// </summary>
    public bool TextureAsNewMod { get; private set; }

    /// <summary>
    /// Whether the plan is a texture fan-out, which has output choices of its own. It runs on its
    /// own like every customization conversion, so one fan-out entry decides; with nothing
    /// planned yet, the selected source does.
    /// </summary>
    public bool UsesTextureOutput => _queue.Count > 0 ? RunEntries.Any(e => e.CanFanOut) : CanFanOutTextures;

    /// <summary>The output mode the plan actually runs with.</summary>
    public ConversionOutputMode EffectiveOutputMode => UsesTextureOutput
        ? TextureAsNewMod ? ConversionOutputMode.NewMod : ConversionOutputMode.AddToMod
        : OutputMode;

    /// <summary>
    /// Whether a new mod holds only what the plan converts, rather than a copy of the whole mod
    /// with it converted in it.
    /// </summary>
    public bool NewModOnlyConverted { get; private set; }

    /// <summary>
    /// Whether the new mod is a copy of the whole mod, converted the way converting in place
    /// would. A fan-out has output choices of its own and always copies the whole mod.
    /// </summary>
    public bool NewModKeepsWholeMod
        => EffectiveOutputMode.IsNewMod() && !NewModOnlyConverted && !UsesTextureOutput;

    /// <summary>
    /// The mode the planners see: <see cref="EffectiveOutputMode"/>, except that a new mod
    /// keeping the whole mod is planned like converting in place (see <see cref="ConversionTask.PlanMode"/>).
    /// What a conversion does to the source follows this, not where it is written.
    /// </summary>
    public ConversionOutputMode PlanOutputMode => NewModKeepsWholeMod ? ConversionOutputMode.InPlace : EffectiveOutputMode;

    public string NewModName { get; private set; } = string.Empty;
    private bool _newModNameIsDefault = true;

    /// <summary>A failed conversion's banner stays up through the preview its failure starts.</summary>
    private bool _keepResultThroughPreview;

    public string? NewModPath => NewModPathFor(NewModName);

    /// <summary>Where a new mod named <paramref name="name"/> is created: beside this mod, in a folder of that name.</summary>
    private string? NewModPathFor(string name)
    {
        if (!HasMod || string.IsNullOrWhiteSpace(name)) return null;
        var parent = Path.GetDirectoryName(ModDirectory.TrimEnd('\\', '/'));
        return string.IsNullOrEmpty(parent) ? null : Path.Combine(parent, ModConverterService.SanitizeFolderName(name));
    }

    // ── Plan and result ──────────────────────────────────────────────────────

    public ConversionTask Task { get; private set; } = new();

    /// <summary>
    /// Bumped by what the plan is made of — its entries, the output mode, the mod. The From and
    /// To cards only choose what to add next, so changing them does not make a preview stale.
    /// </summary>
    private int _planVersion;
    private int _plannedVersion = -1;
    private int _autoPreviewVersion = -1;
    private DateTime _lastInputChange = DateTime.MinValue;

    /// <summary>A plan exists and was made from exactly the current inputs.</summary>
    public bool PlanIsCurrent => Task.IsPlanned && _plannedVersion == _planVersion;

    public ResultBanner? Result { get; set; }

    public bool IsBusy => Runner.IsBusy;

    // ─────────────────────────────────────────────────────────────────────────
    // Lifecycle
    // ─────────────────────────────────────────────────────────────────────────

    /// <summary>Restores the last mod the first time the window opens.</summary>
    public void EnsureInitialized()
    {
        if (_initialized) return;
        _initialized = true;
        RefreshPenumbraState();
        if (!string.IsNullOrEmpty(Config.LastModDirectory) && Directory.Exists(Config.LastModDirectory))
            SelectMod(Config.LastModDirectory);
    }

    /// <summary>Called every framework tick, whether the window is open or not.</summary>
    public void Tick()
    {
        Runner.Drain();
        if (_checkedFor != Runner.Finished)
        {
            // An operation of ours finished, so what the window remembered about the disk may be stale.
            _checkedFor = Runner.Finished;
            _checkedPaths.Clear();
            _checkedReverts.Clear();
            Modded.Invalidate();
        }
        if (!_initialized) return;

        if (DateTime.UtcNow - _lastPenumbraPoll > PenumbraPollInterval)
        {
            _lastPenumbraPoll = DateTime.UtcNow;
            if (_plugin.PenumbraIpc.IsAvailable != PenumbraAvailable) RefreshPenumbraState();
        }

        if (_candidatesWaitForItems && GameData.ItemsReady) ReloadCandidates();
        if (_fixTargetId) FixCustomizationTargetId();

        if (Runner.IsBusy) return;
        if (_scanPending)
        {
            StartScan();
            return;
        }

        // The preview follows the plan: only adding, removing or switching entries, the output
        // mode and the mod change it, so even a slow retarget is not redone on every click.
        if (!PlanIsCurrent && _autoPreviewVersion != _planVersion &&
            DateTime.UtcNow - _lastInputChange > AutoPreviewDelay && PreviewBlockReason == null)
        {
            _autoPreviewVersion = _planVersion;
            Preview();
        }
    }

    public void RefreshPenumbraState()
    {
        Modded.Invalidate();
        PenumbraAvailable = _plugin.PenumbraIpc.IsAvailable;
        if (PenumbraAvailable) RefreshMods();
        else
        {
            Mods = [];
            PenumbraModDirectory = null;
        }
    }

    /// <summary>
    /// Penumbra's mod folder as of the last refresh of its mod list, or null. The windows ask
    /// for it every frame, and asking Penumbra each time is an IPC call.
    /// </summary>
    public string? PenumbraModDirectory { get; private set; }

    public void RefreshMods()
    {
        var root = _plugin.PenumbraIpc.GetModDirectory()?.TrimEnd('/', '\\') ?? string.Empty;
        PenumbraModDirectory = root.Length > 0 ? root : null;
        var mods = _plugin.PenumbraIpc.GetModList();
        if (mods == null || mods.Count == 0)
        {
            Mods = [];
            UpdateModName();
            return;
        }

        Mods = mods
            .Select(kv => new ModEntry(kv.Value, string.IsNullOrEmpty(root) ? kv.Key : Path.Combine(root, kv.Key), kv.Key))
            .OrderBy(m => m.Name, StringComparer.OrdinalIgnoreCase)
            .ToList();
        UpdateModName();
    }

    // ─────────────────────────────────────────────────────────────────────────
    // Readiness
    // ─────────────────────────────────────────────────────────────────────────

    /// <summary>
    /// Why Preview cannot run right now, or null. Once conversions have been queued they are
    /// what gets previewed, so the cards only have to be complete when the queue is empty.
    /// </summary>
    public string? PreviewBlockReason
    {
        get
        {
            if (Runner.IsBusy) return "Wait for the current operation to finish.";
            if (!HasMod) return "Select a mod.";
            if (ModError != null) return ModError;
            if (_queue.Count == 0) return "Add a conversion to the plan first.";
            return _queue.Any(e => e.Enabled) ? null : "Enable at least one conversion in the plan.";
        }
    }

    /// <summary>Why the source and target chosen in the cards are not a complete conversion, or null.</summary>
    public string? SelectionBlockReason
    {
        get
        {
            if (Runner.IsBusy) return "Wait for the current operation to finish.";
            if (!HasMod) return "Select a mod.";
            if (ModError != null) return ModError;
            if (Source is not { } source) return DetectedItems.Count == 0 ? "No convertible item was found in this mod." : "Select a source item.";
            if (source.Animation is { } animation) return AnimationBlockReason(animation);
            if (!source.IsCustomization) return TargetItem == null ? "Select a target item." : null;
            if (source.CanFanOut) return _textureTargets.Count == 0 ? "Tick at least one race or ID to add the paths for." : null;
            if (TargetCustomizationId is < 1 or > 9999) return "Customization IDs must be between 1 and 9999.";
            if (CustomizationTargets.BlockReason(source.Kind, source.GenderRace ?? 0, TargetCustomizationKind, TargetRace) is { } blocked)
                return blocked;
            var options = GameData.TryGetCustomizationOptions(TargetCustomizationKind, TargetRace);
            if (options == null) return "Loading the options players can choose";
            if (options.Count > 0 && options.All(o => o.Id != TargetCustomizationId))
                return $"{GameDataService.OptionLabel(TargetCustomizationKind, (ushort)TargetCustomizationId)} is not available to {RaceLabel(TargetRace)} players.";
            return null;
        }
    }

    /// <summary>Why Apply cannot run right now, or null.</summary>
    public string? ApplyBlockReason
    {
        get
        {
            if (PreviewBlockReason is { } reason) return reason;
            if (!Task.IsPlanned || !PlanIsCurrent) return "Updating the preview";
            if (Task.HasBlockers) return "The plan has problems. Fix them first; they are listed in the Plan tab.";
            if (Task.IsApplied) return "This plan was already applied.";
            if (_queue.Count > 0 && _queue.All(e => !e.Enabled)) return "Enable at least one conversion in the plan.";
            if (EffectiveOutputMode.IsNewMod())
            {
                if (string.IsNullOrWhiteSpace(NewModName)) return "Enter a name for the new mod.";
                if (NewModPath is not { } path) return "Cannot determine where to create the new mod.";
                if (PathExists(path))
                    return $"A folder named '{Path.GetFileName(path)}' already exists.";
            }
            return null;
        }
    }

    // ─────────────────────────────────────────────────────────────────────────
    // Mod selection and scan
    // ─────────────────────────────────────────────────────────────────────────

    public void SelectMod(string directory)
    {
        directory = directory.Trim().Trim('"');
        // Use Penumbra's spelling of the path so the browser can highlight it.
        if (Mods.FirstOrDefault(m => SamePath(m.Directory, directory)) is { } known) directory = known.Directory;
        if (SamePath(directory, ModDirectory) && ModError == null && DetectedItems.Count > 0) return;

        ModDirectory  = directory;
        UpdateModName();
        ModError      = null;
        DetectedItems = [];
        SourceIndex   = -1;
        ClearGearTarget();
        _queue.Clear();
        Task          = new ConversionTask();
        Result        = null;
        // A name typed for the last mod is not this one's.
        _newModNameIsDefault = true;
        MarkPlanDirty();

        Config.LastModDirectory = directory;
        Config.Save();
        Rescan();
    }

    public void Rescan()
    {
        if (!HasMod) return;
        _scanPending = true;
        if (!Runner.IsBusy) StartScan();
    }

    private void StartScan()
    {
        _scanPending = false;
        var directory = ModDirectory;
        Runner.TryRun("Scanning mod", () =>
        {
            var (ok, error) = _plugin.Converter.ValidateModDirectory(directory);
            if (!ok) return (Error: error, Items: new List<DetectedItem>());
            return (Error: (string?)null, Items: GameData.ScanModForItems(directory));
        }, result =>
        {
            if (!SamePath(directory, ModDirectory)) return; // another mod was selected meanwhile
            ModError      = result.Error;
            DetectedItems = result.Items;
            SourceIndex   = -1;
            ClearGearTarget();
            MarkDirty();
            if (result.Error != null)
            {
                Log.Add(LogLevel.Error, result.Error);
                return;
            }

            Log.Add(result.Items.Count > 0
                ? $"Scan found {result.Items.Count} asset root(s) in {ModName}."
                : $"Scan found no gear, facewear, hair, face, tail, Viera ear, skin or animation in {ModName}.");
            if (result.Items.Count == 1) SelectSource(0);
        }, ex =>
        {
            if (!SamePath(directory, ModDirectory)) return;
            ModError = $"The mod could not be scanned: {ex.Message}";
            Log.Add(LogLevel.Error, ModError);
        });
    }

    public void SelectSource(int index)
    {
        if (index == SourceIndex || index < 0 || index >= DetectedItems.Count) return;
        SourceIndex = index;
        var source  = DetectedItems[index];
        TargetSlot  = source.Slot;
        ClearGearTarget();
        ClearFanOut();
        if (source.Animation is { } animation)
            ResetAnimationTarget(animation);
        else if (source.IsCustomization)
        {
            TargetCustomizationKind = source.Kind;
            TargetCustomizationId   = int.TryParse(source.ModelIdPadded, out var id) ? id : 1;
            var races = AllowedTargetRaces;
            TargetRace = source.GenderRace is { } race && races.Contains(race) ? race : races.FirstOrDefault();
            _fixTargetId = true;
        }
        else
            ReloadCandidates();
        MarkDirty();
    }

    // ─────────────────────────────────────────────────────────────────────────
    // Target selection
    // ─────────────────────────────────────────────────────────────────────────

    public void SetTargetSlot(EquipSlot slot)
    {
        if (slot == TargetSlot) return;
        TargetSlot = slot;
        ClearGearTarget();
        ReloadCandidates();
        MarkDirty();
    }

    public void SetTargetFilter(string filter)
    {
        TargetFilter = filter;
        ApplyTargetFilter();
    }

    public void SelectTarget(GameItem item)
    {
        if (TargetItem == item) return;
        TargetItem = item;
        MarkDirty();
    }

    /// <summary>Tails and Viera ears may convert into each other; other kinds stay the same.</summary>
    public IReadOnlyList<AssetKind> AllowedTargetKinds
        => Source is { Kind: AssetKind.Tail or AssetKind.VieraEar }
            ? [AssetKind.Tail, AssetKind.VieraEar]
            : Source is { IsCustomization: true } source ? [source.Kind] : [];

    /// <summary>Races the current source may be converted to (Lalafell only among Lalafell; faces keep their gender).</summary>
    public IReadOnlyList<ushort> AllowedTargetRaces
        => Source is { IsCustomization: true, GenderRace: { } race } source
            ? CustomizationTargets.AllowedRaces(source.Kind, race, TargetCustomizationKind)
            : [];

    public void SetCustomizationKind(AssetKind kind)
    {
        if (kind == TargetCustomizationKind || !AllowedTargetKinds.Contains(kind)) return;
        TargetCustomizationKind = kind;
        TargetRace = AllowedTargetRaces.FirstOrDefault();
        _fixTargetId = true;
        // A tail's and a Viera ear's IDs are unrelated numbering, so nothing here still applies.
        _textureTargets.Clear();
        MarkDirty();
    }

    public void SetTargetRace(ushort race)
    {
        if (race == TargetRace || !AllowedTargetRaces.Contains(race)) return;
        TargetRace = race;
        _fixTargetId = true;
        MarkDirty();
    }

    public void SetCustomizationId(int id)
    {
        id = Math.Clamp(id, 1, 9999);
        if (id == TargetCustomizationId) return;
        TargetCustomizationId = id;
        MarkDirty();
    }

    private bool _fixTargetId;

    /// <summary>Label of the chosen target, e.g. "Face 101 (Keeper of the Moon)".</summary>
    public string TargetOptionLabel
        => GameData.TryGetCustomizationOptions(TargetCustomizationKind, TargetRace)?
               .FirstOrDefault(o => o.Id == TargetCustomizationId)?.Label
           ?? GameDataService.OptionLabel(TargetCustomizationKind, (ushort)TargetCustomizationId);

    /// <summary>
    /// After the source, kind or race changes, keep the same ID when players of the target
    /// race can choose it, otherwise pick the first one they can.
    /// </summary>
    private void FixCustomizationTargetId()
    {
        if (Source is not { IsCustomization: true }) { _fixTargetId = false; return; }
        var options = GameData.TryGetCustomizationOptions(TargetCustomizationKind, TargetRace);
        if (options == null) return; // still loading
        _fixTargetId = false;
        if (options.Count == 0 || options.Any(o => o.Id == TargetCustomizationId)) return;
        TargetCustomizationId = options[0].Id;
        MarkDirty();
    }

    private void ClearGearTarget()
    {
        TargetItem       = null;
        TargetFilter     = string.Empty;
        TargetCandidates = [];
    }

    private List<GameItem> _slotItems = new();

    private void ReloadCandidates()
    {
        if (!GameData.ItemsReady)
        {
            // Never build the item cache on the framework thread; Tick retries once it is ready.
            _candidatesWaitForItems = true;
            GameData.WarmUp();
            _slotItems = new();
            TargetCandidates = [];
            return;
        }

        _candidatesWaitForItems = false;
        _slotItems = GameData.GetAllItemsForSlot(TargetSlot);
        ApplyTargetFilter();
    }

    private void ApplyTargetFilter()
    {
        var q = TargetFilter.Trim();
        if (q.Length == 0)
        {
            TargetCandidates = _slotItems;
            return;
        }

        bool Matches(GameItem i, Func<string, bool> test)
            => test(i.Name) || test(i.ModelIdPadded) || test(i.ModelIdDisplay);
        bool Starts(GameItem i) => Matches(i, s => s.StartsWith(q, StringComparison.OrdinalIgnoreCase));
        bool Contains(GameItem i) => Matches(i, s => s.Contains(q, StringComparison.OrdinalIgnoreCase));

        TargetCandidates = _slotItems.Where(Starts)
            .Concat(_slotItems.Where(i => !Starts(i) && Contains(i)))
            .ToList();
    }

    // ─────────────────────────────────────────────────────────────────────────
    // Output
    // ─────────────────────────────────────────────────────────────────────────

    /// <summary>
    /// What the plan converts, so the output options can say what each does with it. Like the
    /// output mode it follows the ticked entries, or the selection while nothing is planned.
    /// </summary>
    public PlanContents OutputContents
        => _queue.Count > 0
            ? RunEntries.Aggregate(PlanContents.None, (all, e) => all | ContentsOf(e.Kind, e.Task.AnimationRequest))
            : Source is { } source
                ? source.Animation is { } animation ? SelectionContents(animation) : ContentsOf(source.Kind, null)
                : PlanContents.None;

    private PlanContents ContentsOf(AssetKind kind, AnimationConversionRequest? request) => kind switch
    {
        AssetKind.Animation => AnimationContents(request),
        _ when CustomizationKinds.IsCustomization(kind) => PlanContents.Customization,
        _ => PlanContents.Gear,
    };

    /// <summary>The customization the plan converts, as a sentence names it ("hair", "Viera ear"), or null.</summary>
    public string? OutputCustomizationName
    {
        get
        {
            var kind = _queue.Count > 0
                ? RunEntries.FirstOrDefault(e => CustomizationKinds.IsCustomization(e.Kind))?.Kind
                : Source is { IsCustomization: true } source ? source.Kind : null;
            return kind switch
            {
                null                => null,
                AssetKind.VieraEar  => "Viera ear",
                { } other           => CustomizationKinds.Get(other).DisplayName.ToLowerInvariant(),
            };
        }
    }

    public void SetOutputMode(ConversionOutputMode mode)
    {
        if (mode == OutputMode) return;
        OutputMode = mode;
        Config.OutputMode = mode;
        Config.Save();
        MarkPlanDirty(); // The gear plan depends on the output mode.
    }

    public void SetNewModOnlyConverted(bool onlyConverted)
    {
        if (onlyConverted == NewModOnlyConverted) return;
        NewModOnlyConverted = onlyConverted;
        Config.NewModOnlyConverted = onlyConverted;
        Config.Save();
        MarkPlanDirty(); // Keeping the whole mod plans the conversions as in place.
    }

    public void SetTextureLayout(TextureFanOutLayout layout)
    {
        if (layout == TextureLayout) return;
        TextureLayout = layout;
        Config.TextureLayout = layout;
        Config.Save();
        MarkPlanDirty();
    }

    public void SetTextureAsNewMod(bool asNewMod)
    {
        if (asNewMod == TextureAsNewMod) return;
        TextureAsNewMod = asNewMod;
        Config.TextureAsNewMod = asNewMod;
        Config.Save();
        MarkPlanDirty();
    }

    public void SetNewModName(string name)
    {
        NewModName = name;
        _newModNameIsDefault = false;
    }

    public void ResetNewModName()
    {
        _newModNameIsDefault = true;
        RefreshDefaultNewModName();
    }

    /// <summary>Ends every suggested new mod name, so a converted copy never takes its source's name.</summary>
    private const string NewModNameSuffix = " - UMC";

    private void RefreshDefaultNewModName()
    {
        if (!_newModNameIsDefault) return;
        if (!HasMod)
        {
            NewModName = string.Empty;
            return;
        }

        var run = RunEntries.ToList();
        var label = run.Count switch
        {
            0 => string.Empty,
            1 => run[0].CanFanOut ? $"+ {run[0].Target.Detail}" : run[0].Target.Name,
            _ => $"{run.Count} conversions",
        };
        var name = (label.Length == 0 ? ModName : $"{ModName} ({label})") + NewModNameSuffix;

        // A suggestion is never a folder that already exists, such as the one the last conversion
        // created: it is numbered instead. Checked on disk, not through PathExists, whose answers
        // may predate a conversion that has only just finished.
        var suggested = name;
        for (var number = 2; number < 100 && NewModPathFor(suggested) is { } path && (Directory.Exists(path) || File.Exists(path)); number++)
            suggested = $"{name} ({number})";
        NewModName = suggested;
    }

    /// <summary>The plan itself changed: its entries, the output mode or the mod.</summary>
    private void MarkPlanDirty()
    {
        _planVersion++;
        MarkDirty();
    }

    private void MarkDirty()
    {
        _lastInputChange = DateTime.UtcNow;
        RefreshDefaultNewModName();
    }

    // ─────────────────────────────────────────────────────────────────────────
    // Preview
    // ─────────────────────────────────────────────────────────────────────────

    /// <summary>The conversion the current inputs describe, ready to plan.</summary>
    private ConversionTask BuildTask(DetectedItem source)
    {
        var target = TargetItem;
        if (source.Animation is { } animation)
            return new ConversionTask
            {
                Kind             = AssetKind.Animation,
                ModDirectory     = ModDirectory,
                OutputMode       = OutputMode,
                AnimationRequest = BuildAnimationRequest(animation),
            };

        // A fan-out has no single target: everything ticked in the list gets the source's paths.
        if (source is { IsCustomization: true, CanFanOut: true, GenderRace: { } sourceRace })
            return new ConversionTask
            {
                Kind                    = source.Kind,
                TargetCustomizationKind = TargetCustomizationKind,
                ModDirectory            = ModDirectory,
                OutputMode              = EffectiveOutputMode,
                SourceGenderRace        = sourceRace,
                OldIdPadded             = source.ModelIdPadded,
                TextureRequest          = new TextureFanOutRequest(
                    new CustomizationPathEndpoint(source.Kind, sourceRace, ushort.Parse(source.ModelIdPadded)),
                    [.. _textureTargets.OrderBy(t => t.Race).ThenBy(t => t.Id)
                        .Select(t => new CustomizationPathEndpoint(TargetCustomizationKind, t.Race, t.Id))],
                    EffectiveOutputMode, TextureLayout, RetargetMaterials, Describe(source)),
            };

        return new ConversionTask
        {
            Kind                    = source.Kind,
            TargetCustomizationKind = source.IsCustomization ? TargetCustomizationKind : null,
            ModDirectory            = ModDirectory,
            OutputMode              = OutputMode,
            Slot                    = source.Slot,
            OldIdPadded             = source.ModelIdPadded,
            NewIdPadded             = source.IsCustomization ? TargetCustomizationId.ToString("D4") : target!.ModelIdPadded,
            TargetVariant           = source.IsCustomization ? 1 : target!.Variant,
            SourceVariant           = source.Variant,
            SourceGenderRace        = source.GenderRace,
            TargetGenderRace        = source.IsCustomization ? TargetRace : null,
            TargetSlot              = !source.IsCustomization && TargetSlot != source.Slot ? TargetSlot : null,
        };
    }

    public void Preview()
    {
        if (PreviewBlockReason != null) return;

        ConversionTask task;
        string description;
        var enabled = _queue.Where(e => e.Enabled).ToList();
        if (enabled.Count == 1)
        {
            // One conversion needs no merging: it runs the way it always has, with everything
            // a single conversion supports, customization and mesh editing included.
            task = enabled[0].Task.CloneInputs();
            task.OutputMode = EffectiveOutputMode;
            task.KeepsWholeMod = NewModKeepsWholeMod;
            // Like the output mode, the layout is chosen for the plan, not per entry.
            if (task.TextureRequest is { } fanOut) task.TextureRequest = fanOut with { Layout = TextureLayout };
            description = enabled[0].Description;
        }
        else
        {
            task = new ConversionTask
            {
                ModDirectory  = ModDirectory,
                OutputMode    = EffectiveOutputMode,
                KeepsWholeMod = NewModKeepsWholeMod,
            };
            // Planning runs off the framework thread, so it works on copies; the queue's own
            // entries get the results once it is done.
            task.Entries.AddRange(enabled.Select(e => e.ForPlanning()));
            description = DescribeQueue();
        }

        var version = _planVersion;
        if (_keepResultThroughPreview) _keepResultThroughPreview = false;
        else Result = null;

        Runner.TryRun("Planning", () =>
        {
            _plugin.Converter.PlanConversion(task);
            return task;
        }, planned =>
        {
            CarryOverMeshRemovals(Task, planned);
            ApplyForcedMeshRemovals(planned);
            Task = planned;
            _plannedVersion = version;
            ShowEntryResults(planned);
            if (planned.IsPlanned)
            {
                var counts = planned.MergedPlan is { } merged
                    ? $"{merged.Entries.Count(e => !e.Rejected)} conversion(s), {merged.Files.Count} file operation(s)"
                    : planned.GearPlan is { } plan
                    ? $"{plan.Changes.Count} change(s), {plan.Files.Count} file operation(s)"
                    : planned.AnimationPlan is { } animationPlan
                    ? $"{animationPlan.Changes.Count} change(s), {animationPlan.Files.Count} file operation(s)"
                    : planned.TexturePlan is { } texturePlan
                    ? $"{texturePlan.Outputs.Count} path(s) added, {texturePlan.Files.Count} material file(s) written"
                    : $"{planned.PlannedRenames.Count(r => !r.KeepsOriginal)} rename(s), {planned.PlannedRenames.Count(r => r.KeepsOriginal)} file(s) copied, {planned.PlannedJsonChanges.Sum(j => j.Changes.Count)} metadata change(s), " +
                      $"{planned.PlannedBinaryPatches.Sum(b => b.Patches.Count)} binary patch(es), {planned.PlannedMdlChanges.Count} model rewrite(s)";
                Log.Add(planned.HasBlockers ? LogLevel.Warning : LogLevel.Info,
                    $"Preview {description}: {counts}{(planned.HasBlockers ? ", has problems" : string.Empty)}.");
            }
            else
                Log.Add(LogLevel.Error, $"Preview failed: {planned.ErrorMessage}");
        }, ex =>
        {
            task.ErrorMessage = ex.Message;
            task.Diagnostics.Add(new PlanDiagnostic("planning_failed", ex.Message, true));
            Task = task;
            _plannedVersion = version;
            ShowEntryResults(task);
            Log.Add(LogLevel.Error, $"Preview failed: {ex.Message}");
        });
    }

    /// <summary>
    /// Hands a preview's per-entry results (which entries were left out, and why) to the queue's
    /// own entries, on the framework thread the queue panel draws them on. An entry the preview
    /// did not plan as part of a run, such as the only one or an unticked one, shows none.
    /// </summary>
    private void ShowEntryResults(ConversionTask planned)
    {
        foreach (var entry in _queue)
        {
            if (planned.Entries.FirstOrDefault(e => e.Id == entry.Id) is { } copy) entry.TakeResults(copy);
            else entry.ResetPlan();
        }
    }

    // ─────────────────────────────────────────────────────────────────────────
    // Mesh groups
    // ─────────────────────────────────────────────────────────────────────────

    /// <summary>A planned gear conversion, the only one or one of a run, moves a model to a different slot.</summary>
    public bool PlanIsCrossSlot
        => Task.GearConversions().Any(c => c.Plan.Request.Source.Slot != c.Plan.Request.Target.Slot);

    /// <summary>Why mesh groups cannot be edited right now, or null.</summary>
    public string? MeshEditBlockReason
        => Runner.IsBusy ? "Wait for the current operation to finish."
            : Task.IsApplied ? "This plan was already applied. Add a conversion to the plan to make another."
            : null;

    /// <summary>
    /// Whether <paramref name="group"/> can never be kept (see <see cref="GearOutputModel.IsForcedOff"/>).
    /// Such groups are switched off when the plan is made and stay off.
    /// </summary>
    public bool IsMeshGroupForced(GearOutputModel model, int group) => model.IsForcedOff(group);

    /// <summary>
    /// Whether <paramref name="group"/> starts switched off (see <see cref="GearOutputModel.StartsOff"/>).
    /// The user may tick it back on.
    /// </summary>
    public bool IsMeshGroupOffByDefault(GearOutputModel model, int group) => model.StartsOff(group);

    /// <summary>
    /// Switches off every group that cannot be kept, and, the first time a model is planned,
    /// every group that starts off; never so many that the model would be left empty. Each model
    /// follows its own conversion, so a run treats every one of its conversions as a single
    /// conversion would.
    /// </summary>
    private static void ApplyForcedMeshRemovals(ConversionTask task)
    {
        foreach (var model in task.OutputModels.Where(m => m.Editable))
        {
            var firstTime = task.MeshDefaultsApplied.Add(model.Local);
            var forced = Enumerable.Range(0, model.Groups.Count)
                .Where(g => model.IsForcedOff(g) || firstTime && model.StartsOff(g))
                .ToList();
            if (forced.Count == 0) continue;
            var (groups, parts) = CurrentRemoval(task, model);
            groups.UnionWith(forced);
            parts.RemoveWhere(p => forced.Contains(p.Group));
            StoreRemoval(task, model, groups, parts);
        }
    }

    public bool IsMeshGroupRemoved(GearOutputModel model, int group)
        => Task.MeshRemovals.TryGetValue(model.Local, out var removal) && removal.Groups.Contains(group);

    public int RemovedMeshGroupCount(GearOutputModel model)
        => Task.MeshRemovals.TryGetValue(model.Local, out var removal) ? removal.Groups.Length : 0;

    public bool IsMeshPartRemoved(GearOutputModel model, int group, int part)
        => Task.MeshRemovals.TryGetValue(model.Local, out var removal) &&
           (removal.Groups.Contains(group) || removal.PartsOrEmpty.Contains(new MeshPartRef(group, part)));

    /// <summary>How many parts of <paramref name="group"/> are left out, counting a removed group as all of them.</summary>
    public int RemovedMeshPartCount(GearOutputModel model, int group)
    {
        if (!Task.MeshRemovals.TryGetValue(model.Local, out var removal) || group < 0 || group >= model.Groups.Count) return 0;
        return removal.Groups.Contains(group) ? model.Groups[group].Parts : removal.PartsOrEmpty.Count(p => p.Group == group);
    }

    /// <summary>Parts left out of groups that stay, over the whole model.</summary>
    public int RemovedMeshPartCount(GearOutputModel model)
        => Task.MeshRemovals.TryGetValue(model.Local, out var removal) ? removal.PartsOrEmpty.Length : 0;

    /// <summary>
    /// Keeps or removes mesh group <paramref name="group"/> in every given model. A model
    /// always keeps at least one group; requests that would empty it are ignored for it.
    /// </summary>
    public void SetMeshGroupRemoved(IEnumerable<GearOutputModel> models, int group, bool removed)
    {
        if (MeshEditBlockReason != null) return;
        foreach (var model in models)
        {
            if (!model.Editable || group < 0 || group >= model.Groups.Count || model.IsForcedOff(group)) continue;
            var (groups, parts) = CurrentRemoval(Task, model);
            parts.RemoveWhere(p => p.Group == group);
            if (removed) groups.Add(group);
            else groups.Remove(group);
            StoreRemoval(Task, model, groups, parts);
        }
    }

    /// <summary>
    /// Keeps or removes one part of a mesh group in every given model. Removing a group's last
    /// part removes the group; keeping a part of a removed group brings the group back with
    /// only that part.
    /// </summary>
    public void SetMeshPartRemoved(IEnumerable<GearOutputModel> models, int group, int part, bool removed)
    {
        if (MeshEditBlockReason != null) return;
        foreach (var model in models)
        {
            if (!model.Editable || group < 0 || group >= model.Groups.Count || part < 0 || part >= model.Groups[group].Parts ||
                model.IsForcedOff(group))
                continue;
            var (groups, parts) = CurrentRemoval(Task, model);
            if (groups.Remove(group))
                for (var p = 0; p < model.Groups[group].Parts; p++) parts.Add(new MeshPartRef(group, p));

            var target = new MeshPartRef(group, part);
            if (removed) parts.Add(target);
            else parts.Remove(target);

            if (parts.Count(p => p.Group == group) >= model.Groups[group].Parts)
            {
                parts.RemoveWhere(p => p.Group == group);
                groups.Add(group);
            }
            StoreRemoval(Task, model, groups, parts);
        }
    }

    private static (HashSet<int> Groups, HashSet<MeshPartRef> Parts) CurrentRemoval(ConversionTask task, GearOutputModel model)
        => task.MeshRemovals.TryGetValue(model.Local, out var current)
            ? (current.Groups.ToHashSet(), current.PartsOrEmpty.ToHashSet())
            : ([], []);

    private static void StoreRemoval(ConversionTask task, GearOutputModel model, HashSet<int> groups, HashSet<MeshPartRef> parts)
    {
        if (groups.Count >= model.Groups.Count) return; // A model keeps at least one group.
        if (groups.Count == 0 && parts.Count == 0) task.MeshRemovals.Remove(model.Local);
        else task.MeshRemovals[model.Local] = new MeshRemoval([.. groups.Order()], model.Groups.Count,
            [.. parts.OrderBy(p => p.Group).ThenBy(p => p.Part)]);
    }

    public void KeepAllMeshGroups(IEnumerable<GearOutputModel> models)
    {
        if (MeshEditBlockReason != null) return;
        foreach (var model in models) Task.MeshRemovals.Remove(model.Local);
        ApplyForcedMeshRemovals(Task);
    }

    /// <summary>Re-previewing keeps removals for output models whose layout did not change.</summary>
    private static void CarryOverMeshRemovals(ConversionTask previous, ConversionTask planned)
    {
        foreach (var (local, removal) in previous.MeshRemovals)
        {
            var before = previous.OutputModels.FirstOrDefault(m => string.Equals(m.Local, local, StringComparison.OrdinalIgnoreCase));
            var after  = planned.OutputModels.FirstOrDefault(m => string.Equals(m.Local, local, StringComparison.OrdinalIgnoreCase));
            if (before == null || after is not { Editable: true } || after.Groups.Count != removal.ExpectedGroupCount ||
                !before.Groups.Select(g => (g.Material, g.Parts)).SequenceEqual(after.Groups.Select(g => (g.Material, g.Parts))))
                continue;
            planned.MeshRemovals[after.Local] = removal;
        }
        // A model whose layout survived keeps what the user made of its defaults.
        foreach (var local in previous.MeshDefaultsApplied)
            if (planned.OutputModels.FirstOrDefault(m => string.Equals(m.Local, local, StringComparison.OrdinalIgnoreCase)) is { } after &&
                previous.OutputModels.FirstOrDefault(m => string.Equals(m.Local, local, StringComparison.OrdinalIgnoreCase)) is { } before &&
                before.Groups.Select(g => (g.Material, g.Parts)).SequenceEqual(after.Groups.Select(g => (g.Material, g.Parts))))
                planned.MeshDefaultsApplied.Add(after.Local);
    }

    /// <summary>Short description of the current conversion, e.g. "Body 0164-1 → Hands 0200-1".</summary>
    public string Describe(DetectedItem source)
    {
        if (source.Animation is { } animation) return DescribeAnimation(animation);
        if (source.IsCustomization)
            return source.CanFanOut
                ? $"{RaceLabel(source.GenderRace ?? 0)} {source.ItemName} → also {DescribeTextureTargets()}"
                : $"{RaceLabel(source.GenderRace ?? 0)} {source.ItemName} → {RaceLabel(TargetRace)} {TargetOptionLabel}";
        var target = TargetItem == null ? "?" : $"{TargetItem.Name} ({TargetItem.ModelIdDisplay})";
        return $"{source.ItemName} ({source.ModelIdDisplay}) → {target}" +
               (TargetSlot != source.Slot ? $" [{SlotInfo.DisplayLabelMap[source.Slot]} → {SlotInfo.DisplayLabelMap[TargetSlot]}]" : string.Empty);
    }

    /// <summary>The whole ticked (race, ID) set of a fan-out root, for summaries.</summary>
    private string DescribeTextureTargets()
    {
        if (_textureTargets.Count == 0) return "?";
        if (_textureTargets.Count == 1)
        {
            var (race, id) = _textureTargets.First();
            return TargetCustomizationKind == AssetKind.Body
                ? RaceLabel(race)
                : $"{RaceLabel(race)} {GameDataService.OptionLabel(TargetCustomizationKind, id)}";
        }
        var races = _textureTargets.Select(t => t.Race).Distinct().Count();
        return races == _textureTargets.Count
            ? $"{races} races"
            : $"{_textureTargets.Count} {CustomizationKinds.Get(TargetCustomizationKind).DisplayName.ToLowerInvariant()}s across {races} race(s)";
    }

    // ─────────────────────────────────────────────────────────────────────────
    // Apply
    // ─────────────────────────────────────────────────────────────────────────

    public void Apply()
    {
        if (ApplyBlockReason != null) return;
        var enabledEntries = _queue.Where(e => e.Enabled).ToList();
        if (enabledEntries.Count == 0) return;

        var task        = Task;
        var mode        = EffectiveOutputMode;
        var isNewMod    = mode.IsNewMod();
        var newModDir   = NewModPath;
        var newModName  = NewModName.Trim();
        var sourceName  = ModName;
        var description = enabledEntries.Count == 1 ? enabledEntries[0].Description : DescribeQueue();
        Result = null;
        Log.BeginOperation(isConversion: true);
        Log.Add($"Converting {description} ({OutputModeLabel(mode, newModName)})");
        if (task.MeshRemovals.Count > 0)
            Log.Add($"Removing {task.MeshRemovals.Values.Sum(r => r.Groups.Length)} mesh group(s) and " +
                    $"{task.MeshRemovals.Values.Sum(r => r.PartsOrEmpty.Length)} single part(s) from {task.MeshRemovals.Count} model(s).");

        void Post(string message) => Runner.Post(() => Log.Add(message));

        Runner.TryRun("Converting", () =>
        {
            string? outputDir;
            if (isNewMod)
                outputDir = _plugin.Converter.CreateNewModFromAssetChain(task, newModDir!, newModName, Post);
            else
            {
                _plugin.Converter.ApplyConversion(task, Post);
                outputDir = task.IsApplied ? task.ModDirectory : null;
            }

            if (outputDir == null) return (Output: (string?)null, Issues: new List<LeftoverHit>());
            Post("Verifying the converted item");
            return (Output: outputDir, Issues: _plugin.Converter.VerifyConversion(task, isNewMod ? outputDir : null));
        }, result =>
        {
            // Written or not, the plan is spent: it stays on screen, but the bytes it held go.
            task.ReleaseContents();
            if (result.Output == null)
            {
                // Whatever went wrong, the plan it was made from is spent; preview it afresh, and
                // keep saying what went wrong while that happens.
                MarkPlanDirty();
                _keepResultThroughPreview = true;
                Result = new ResultBanner(BannerKind.Error,
                    task.ResultStatus == ConversionResultStatus.RolledBack ? "Conversion rolled back" : "Conversion failed",
                    task.ErrorMessage ?? "See the log for details.");
                return;
            }

            var problems = ReportVerification(result.Issues);
            if (isNewMod) FinishNewMod(task, result.Output, newModName, sourceName, description, problems);
            else FinishInPlace(task, sourceName, description, problems);
        }, ex =>
        {
            // As above: the spent plan is previewed afresh rather than offered again without its contents.
            task.ReleaseContents();
            MarkPlanDirty();
            _keepResultThroughPreview = true;
            Log.Add(LogLevel.Error, $"Conversion failed: {ex.Message}");
            Result = new ResultBanner(BannerKind.Error, "Conversion failed", ex.Message);
        });
    }

    private void FinishNewMod(ConversionTask task, string outputDir, string name, string sourceName, string description, int problems)
    {
        Config.LastNewModName = name;
        var record = History.Record(task, description, sourceName);
        var folder = Path.GetFileName(outputDir);
        // The typed name is taken now; the next conversion suggests its own.
        _newModNameIsDefault = true;
        ClearQueue();

        if (!PenumbraAvailable)
        {
            Result = new ResultBanner(BannerKind.Info, "New mod created",
                $"'{name}' was written. Use 'Rediscover Mods' in Penumbra to load it." + ProblemSuffix(problems),
                outputDir, record.Id);
            return;
        }

        ActivateNewMod(folder, name, outputDir, record.Id, problems);
    }

    private void ActivateNewMod(string folder, string name, string outputDir, Guid recordId, int problems)
    {
        var added    = _plugin.PenumbraIpc.AddMod(folder);
        var reloaded = added && _plugin.PenumbraIpc.ReloadMod(folder);
        RefreshMods();
        if (reloaded)
        {
            Task.ResultStatus = ConversionResultStatus.Succeeded;
            Log.Add(LogLevel.Success, $"New mod '{name}' is now available in Penumbra.");
            Result = new ResultBanner(problems > 0 ? BannerKind.Warning : BannerKind.Success, "New mod created",
                $"'{name}' is now available in Penumbra." + ProblemSuffix(problems), outputDir, recordId);
        }
        else
        {
            Log.Add(LogLevel.Warning, $"'{name}' was created but Penumbra did not load it{(added ? " (reload failed)" : string.Empty)}.");
            Result = new ResultBanner(BannerKind.Warning, "Created, but not loaded by Penumbra",
                $"'{name}' was written but Penumbra did not pick it up. Retry, or use 'Rediscover Mods' in Penumbra." + ProblemSuffix(problems),
                outputDir, recordId, folder);
        }
    }

    public void RetryActivation()
    {
        if (Result is not { RetryActivationFolder: { } folder, Path: { } path } banner || !PenumbraAvailable) return;
        var record = banner.RecordId is { } id ? History.Find(id) : null;
        ActivateNewMod(folder, Path.GetFileName(path), path, record?.Id ?? Guid.Empty, 0);
    }

    private void FinishInPlace(ConversionTask task, string sourceName, string description, int problems)
    {
        var folder = Path.GetFileName(task.ModDirectory.TrimEnd('\\', '/'));
        // A folder Penumbra does not manage (opened through "Other folder") has nothing to reload,
        // and Penumbra failing to reload it says nothing about the conversion.
        var managed = PenumbraAvailable && Mods.Any(m => SamePath(m.Directory, task.ModDirectory));
        if (managed)
        {
            if (!_plugin.PenumbraIpc.ReloadMod(folder))
            {
                Log.Add(LogLevel.Error, $"Penumbra could not reload '{folder}'. Rolling back.");
                var restored = _plugin.Converter.RollbackInPlace(task, Log.Add);
                if (restored) _plugin.PenumbraIpc.ReloadMod(folder);
                Result = new ResultBanner(BannerKind.Error, "Conversion rolled back",
                    restored
                        ? "Penumbra could not load the converted mod, so the original was restored."
                        : $"Penumbra could not load the converted mod and the automatic rollback failed. The original is at {task.RecoveryPath}.",
                    restored ? task.ModDirectory : task.RecoveryPath);
                Rescan();
                return;
            }
        }

        _plugin.Converter.ConfirmInPlace(task, Log.Add, managed);
        var record = History.Record(task, description, sourceName);
        _plugin.RunBackupMaintenance();
        ClearQueue();
        // Adding to the mod keeps everything it had, so nothing was converted in place.
        var added = task.OutputMode == ConversionOutputMode.AddToMod;
        Log.Add(LogLevel.Success, added ? $"Added {description} to this mod." : $"Converted {description} in place.");
        Result = new ResultBanner(problems > 0 ? BannerKind.Warning : BannerKind.Success,
            added ? "Added to this mod" : "Mod converted in place",
            (managed ? "The mod was reloaded in Penumbra." : "Reload the mod in Penumbra to see the change.") +
            ProblemSuffix(problems), task.ModDirectory, record.Id);
        Rescan(); // What the mod holds has changed.
    }

    /// <summary>Writes verification results to the log; returns the number of real problems.</summary>
    private int ReportVerification(List<LeftoverHit> hits)
    {
        var errors = hits.Where(h => h.HitType is "missing" or "error").ToList();
        var notes  = hits.Except(errors).ToList();
        if (hits.Count == 0)
            Log.Add(LogLevel.Success, "Verification passed: every converted model, material and texture resolves.");
        if (errors.Count > 0)
        {
            Log.Add(LogLevel.Warning, $"{errors.Count} problem(s) found in the converted item:");
            foreach (var hit in errors) Log.Add(LogLevel.Error, $"  [{hit.HitType.ToUpperInvariant()}] {hit.Detail}");
        }
        if (notes.Count > 0)
        {
            Log.Add($"{notes.Count} note(s) (usually intentional, e.g. resources still shared with other items):");
            foreach (var hit in notes) Log.Add($"  [{hit.HitType.ToUpperInvariant()}] {hit.Detail}");
        }
        return errors.Count;
    }

    private static string ProblemSuffix(int problems)
        => problems == 0 ? string.Empty : $" Verification found {problems} problem(s); see the log.";

    // ─────────────────────────────────────────────────────────────────────────
    // Revert
    // ─────────────────────────────────────────────────────────────────────────

    public string? RevertBlockReason(ConversionRecord record)
        => Runner.IsBusy ? "Wait for the current operation to finish." : History.RevertBlockReason(record);

    // ─────────────────────────────────────────────────────────────────────────
    // Disk checks for drawing
    // ─────────────────────────────────────────────────────────────────────────

    // The windows ask these every frame. The answers only change when something is written:
    // they are forgotten when an operation of ours finishes, and otherwise rechecked after a
    // second, which is soon enough for changes made outside the game.
    private static readonly TimeSpan RecheckAfter = TimeSpan.FromSeconds(1);
    private readonly Dictionary<string, (DateTime At, bool Exists)> _checkedPaths = new(StringComparer.OrdinalIgnoreCase);
    private readonly Dictionary<Guid, (DateTime At, string? Reason)> _checkedReverts = new();
    private int _checkedFor;

    /// <summary>Whether a folder or file exists at <paramref name="path"/>, for drawing.</summary>
    public bool PathExists(string path)
    {
        var now = DateTime.UtcNow;
        if (!_checkedPaths.TryGetValue(path, out var known) || now - known.At >= RecheckAfter)
            _checkedPaths[path] = known = (now, Directory.Exists(path) || File.Exists(path));
        return known.Exists;
    }

    /// <summary><see cref="RevertBlockReason"/> for drawing; reverting itself always checks afresh.</summary>
    public string? DisplayedRevertBlockReason(ConversionRecord record)
    {
        if (Runner.IsBusy) return "Wait for the current operation to finish.";
        var now = DateTime.UtcNow;
        if (!_checkedReverts.TryGetValue(record.Id, out var known) || now - known.At >= RecheckAfter)
            _checkedReverts[record.Id] = known = (now, History.RevertBlockReason(record));
        return known.Reason;
    }

    public void Revert(Guid recordId)
    {
        if (History.Find(recordId) is not { } record) return;
        if (RevertBlockReason(record) is { } reason)
        {
            Result = new ResultBanner(BannerKind.Error, "Cannot revert", reason);
            return;
        }

        Log.BeginOperation(isConversion: false);
        Log.Add($"Reverting {record.Description}");
        Result = null;
        Runner.TryRun("Reverting", () => History.Revert(record, msg => Runner.Post(() => Log.Add(msg))), result =>
        {
            History.MarkReverted(record, result);
            _plugin.RunBackupMaintenance();
            if (!result.Success)
            {
                Result = new ResultBanner(BannerKind.Error, "Revert failed", result.Message);
                return;
            }

            if (PenumbraAvailable && result.PenumbraFolder is { } folder)
            {
                // The new mod's folder was already moved away, so this only unregisters it.
                // A restored in-place mod is reloaded so Penumbra picks up the original again.
                var updated = record.Mode.IsNewMod() && !Directory.Exists(record.PublishedPath)
                    ? _plugin.PenumbraIpc.DeleteMod(folder)
                    : _plugin.PenumbraIpc.ReloadMod(folder);
                if (!updated)
                    Log.Add(LogLevel.Warning, $"Penumbra could not update '{folder}'. Use 'Rediscover Mods' in Penumbra.");
                RefreshMods();
            }

            Log.Add(LogLevel.Success, result.Message);

            if (record.Mode.IsNewMod() && SamePath(record.PublishedPath, ModDirectory))
                SelectMod(record.SourceModDirectory);
            else if (SamePath(record.SourceModDirectory, ModDirectory))
            {
                // The mod changed under the plan, so the plan is previewed again after the rescan.
                Task = new ConversionTask();
                MarkPlanDirty();
                Rescan();
            }
            Result = new ResultBanner(BannerKind.Success, "Conversion reverted", result.Message);
        }, ex =>
        {
            Log.Add(LogLevel.Error, $"Revert failed: {ex.Message}");
            Result = new ResultBanner(BannerKind.Error, "Revert failed", ex.Message);
        });
    }

    // ─────────────────────────────────────────────────────────────────────────
    // Helpers
    // ─────────────────────────────────────────────────────────────────────────

    public static string RaceLabel(ushort genderRace) => RaceNames.Name(genderRace);

    /// <summary>How a conversion describes its destination in the log.</summary>
    public static string OutputModeLabel(ConversionOutputMode mode, string newModName) => mode switch
    {
        ConversionOutputMode.NewMod   => $"new mod '{newModName}'",
        ConversionOutputMode.AddToMod => "added to this mod",
        _                             => "in place",
    };

    public static void OpenFolder(string path)
    {
        try
        {
            if (Directory.Exists(path)) Process.Start(new ProcessStartInfo(path) { UseShellExecute = true });
        }
        catch (Exception ex)
        {
            Plugin.Log.Warning(ex, "[UMC] Could not open {0}", path);
        }
    }

    public static bool SamePath(string? a, string? b)
    {
        if (string.IsNullOrEmpty(a) || string.IsNullOrEmpty(b)) return false;
        try
        {
            return string.Equals(Path.GetFullPath(a).TrimEnd('\\', '/'), Path.GetFullPath(b).TrimEnd('\\', '/'),
                StringComparison.OrdinalIgnoreCase);
        }
        catch (Exception)
        {
            return string.Equals(a, b, StringComparison.OrdinalIgnoreCase);
        }
    }
}
