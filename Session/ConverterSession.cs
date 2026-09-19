using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using AdvancedPenumbraItemConverter.Core;
using AdvancedPenumbraItemConverter.Models;
using AdvancedPenumbraItemConverter.Services;

namespace AdvancedPenumbraItemConverter.Session;

public sealed record ModEntry(string Name, string Directory, string Folder);

public enum BannerKind
{
    Success,
    Info,
    Warning,
    Error,
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
public sealed class ConverterSession
{
    private static readonly TimeSpan AutoPreviewDelay = TimeSpan.FromMilliseconds(600);
    private static readonly TimeSpan PenumbraPollInterval = TimeSpan.FromSeconds(1);

    public static readonly EquipSlot[] OutputSlots =
    [
        EquipSlot.Head, EquipSlot.Body, EquipSlot.Hands, EquipSlot.Legs, EquipSlot.Feet,
        EquipSlot.Earring, EquipSlot.Neck, EquipSlot.Wrists, EquipSlot.RingRight, EquipSlot.RingLeft,
        EquipSlot.Facewear,
    ];

    private static readonly string[] RaceNames =
    [
        "Midlander", "Highlander", "Elezen", "Miqo'te", "Roegadyn",
        "Lalafell", "Au Ra", "Hrothgar", "Viera",
    ];

    private readonly Plugin _plugin;
    private bool _initialized;
    private DateTime _lastPenumbraPoll = DateTime.MinValue;

    public ConverterSession(Plugin plugin)
    {
        _plugin    = plugin;
        OutputMode = plugin.Configuration.CreateNewMod ? ConversionOutputMode.NewMod : ConversionOutputMode.InPlace;
    }

    public BackgroundRunner Runner { get; } = new();
    public LogStore Log { get; } = new();
    public ConversionHistoryService History => _plugin.History;
    public GameDataService GameData => _plugin.GameData;
    private Configuration Config => _plugin.Configuration;

    // ── Penumbra ─────────────────────────────────────────────────────────────

    public bool PenumbraAvailable { get; private set; }
    public IReadOnlyList<ModEntry> Mods { get; private set; } = [];

    // ── Selected mod ─────────────────────────────────────────────────────────

    public string ModDirectory { get; private set; } = string.Empty;
    public string? ModError { get; private set; }
    public PenumbraModFormat? ModFormat { get; private set; }
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

    public AssetKind TargetCustomizationKind { get; private set; } = AssetKind.Hair;
    public ushort TargetRace { get; private set; } = 101;
    public int TargetCustomizationId { get; private set; } = 1;

    // ── Output ───────────────────────────────────────────────────────────────

    public ConversionOutputMode OutputMode { get; private set; }
    public string NewModName { get; private set; } = string.Empty;
    private bool _newModNameIsDefault = true;

    public string? NewModPath
    {
        get
        {
            if (!HasMod || string.IsNullOrWhiteSpace(NewModName)) return null;
            var parent = Path.GetDirectoryName(ModDirectory.TrimEnd('\\', '/'));
            return string.IsNullOrEmpty(parent) ? null : Path.Combine(parent, ModConverterService.SanitizeFolderName(NewModName));
        }
    }

    // ── Plan and result ──────────────────────────────────────────────────────

    public ConversionTask Task { get; private set; } = new();
    private int _inputsVersion;
    private int _plannedVersion = -1;
    private int _autoPreviewVersion = -1;
    private DateTime _lastInputChange = DateTime.MinValue;

    /// <summary>A plan exists and was made from exactly the current inputs.</summary>
    public bool PlanIsCurrent => Task.IsPlanned && _plannedVersion == _inputsVersion;

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

        if (Config.AutoRefreshPreview && !PlanIsCurrent && _autoPreviewVersion != _inputsVersion &&
            DateTime.UtcNow - _lastInputChange > AutoPreviewDelay && PreviewBlockReason == null)
        {
            _autoPreviewVersion = _inputsVersion;
            Preview();
        }
    }

    public void RefreshPenumbraState()
    {
        PenumbraAvailable = _plugin.PenumbraIpc.IsAvailable;
        if (PenumbraAvailable) RefreshMods();
        else Mods = [];
    }

    public void RefreshMods()
    {
        var mods = _plugin.PenumbraIpc.GetModList();
        if (mods == null || mods.Count == 0)
        {
            Mods = [];
            UpdateModName();
            return;
        }

        var root = _plugin.PenumbraIpc.GetModDirectory()?.TrimEnd('/', '\\') ?? string.Empty;
        Mods = mods
            .Select(kv => new ModEntry(kv.Value, string.IsNullOrEmpty(root) ? kv.Key : Path.Combine(root, kv.Key), kv.Key))
            .OrderBy(m => m.Name, StringComparer.OrdinalIgnoreCase)
            .ToList();
        UpdateModName();
    }

    // ─────────────────────────────────────────────────────────────────────────
    // Readiness
    // ─────────────────────────────────────────────────────────────────────────

    /// <summary>Why Preview cannot run right now, or null.</summary>
    public string? PreviewBlockReason
    {
        get
        {
            if (Runner.IsBusy) return "Wait for the current operation to finish.";
            if (!HasMod) return "Select a mod.";
            if (ModError != null) return ModError;
            if (Source is not { } source) return DetectedItems.Count == 0 ? "No convertible item was found in this mod." : "Select a source item.";
            if (!source.IsCustomization) return TargetItem == null ? "Select a target item." : null;
            if (TargetCustomizationId is < 1 or > 9999) return "Customization IDs must be between 1 and 9999.";
            if (CustomizationTargets.BlockReason(source.Kind, source.GenderRace ?? 0, TargetCustomizationKind, TargetRace) is { } blocked)
                return blocked;
            var options = GameData.TryGetCustomizationOptions(TargetCustomizationKind, TargetRace);
            if (options == null) return "Loading the options players can choose…";
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
            if (!Task.IsPlanned) return "Preview the conversion first.";
            if (!PlanIsCurrent) return "The inputs changed since the preview. Preview again.";
            if (Task.HasBlockers) return "The plan has blockers. Resolve them first.";
            if (Task.IsApplied) return "This plan was already applied.";
            if (OutputMode == ConversionOutputMode.NewMod)
            {
                if (string.IsNullOrWhiteSpace(NewModName)) return "Enter a name for the new mod.";
                if (NewModPath is not { } path) return "Cannot determine where to create the new mod.";
                if (Directory.Exists(path) || File.Exists(path))
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
        ModFormat     = null;
        DetectedItems = [];
        SourceIndex   = -1;
        ClearGearTarget();
        Task          = new ConversionTask();
        Result        = null;
        MarkDirty();

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
        Runner.TryRun("Scanning mod…", () =>
        {
            var (ok, error) = _plugin.Converter.ValidateModDirectory(directory);
            if (!ok) return (Error: error, Format: (PenumbraModFormat?)null, Items: new List<DetectedItem>());
            var format = PenumbraMod.Load(directory).Format;
            return (Error: (string?)null, Format: (PenumbraModFormat?)format, Items: GameData.ScanModForItems(directory));
        }, result =>
        {
            if (!SamePath(directory, ModDirectory)) return; // another mod was selected meanwhile
            ModError      = result.Error;
            ModFormat     = result.Format;
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
                : $"Scan found no gear, facewear, hair, face, tail, or Viera-ear roots in {ModName}.");
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
        if (source.IsCustomization)
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

    public void SetOutputMode(ConversionOutputMode mode)
    {
        if (mode == OutputMode) return;
        OutputMode = mode;
        Config.CreateNewMod = mode == ConversionOutputMode.NewMod;
        Config.Save();
        MarkDirty(); // The gear plan depends on the output mode.
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

    private void RefreshDefaultNewModName()
    {
        if (!_newModNameIsDefault) return;
        var label = Source switch
        {
            null => string.Empty,
            { IsCustomization: true } => $"{RaceLabel(TargetRace)} {TargetOptionLabel}",
            _ when TargetItem != null => $"{SlotInfo.DisplayLabelMap[TargetSlot]} {TargetItem.Name}",
            _ => SlotInfo.DisplayLabelMap[TargetSlot],
        };
        NewModName = HasMod ? (label.Length == 0 ? ModName : $"{ModName} ({label})") : string.Empty;
    }

    private void MarkDirty()
    {
        _inputsVersion++;
        _lastInputChange = DateTime.UtcNow;
        RefreshDefaultNewModName();
    }

    // ─────────────────────────────────────────────────────────────────────────
    // Preview
    // ─────────────────────────────────────────────────────────────────────────

    public void Preview()
    {
        if (PreviewBlockReason != null || Source is not { } source) return;

        var target = TargetItem;
        var task = new ConversionTask
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
        var version     = _inputsVersion;
        var description = Describe(source);
        Result = null;

        Runner.TryRun("Planning…", () =>
        {
            _plugin.Converter.PlanConversion(task);
            return task;
        }, planned =>
        {
            CarryOverMeshRemovals(Task, planned);
            Task = planned;
            _plannedVersion = version;
            if (planned.IsPlanned)
            {
                var counts = planned.GearPlan is { } plan
                    ? $"{plan.Changes.Count} change(s), {plan.Files.Count} file operation(s)"
                    : $"{planned.PlannedRenames.Count} rename(s), {planned.PlannedJsonChanges.Sum(j => j.Changes.Count)} metadata change(s), " +
                      $"{planned.PlannedBinaryPatches.Sum(b => b.Patches.Count)} binary patch(es), {planned.PlannedMdlChanges.Count} model rewrite(s)";
                Log.Add(planned.HasBlockers ? LogLevel.Warning : LogLevel.Info,
                    $"Preview {description}: {counts}{(planned.HasBlockers ? ", has blockers" : string.Empty)}.");
            }
            else
                Log.Add(LogLevel.Error, $"Preview failed: {planned.ErrorMessage}");
        }, ex =>
        {
            task.ErrorMessage = ex.Message;
            task.Diagnostics.Add(new PlanDiagnostic("planning_failed", ex.Message, true));
            Task = task;
            _plannedVersion = version;
            Log.Add(LogLevel.Error, $"Preview failed: {ex.Message}");
        });
    }

    // ─────────────────────────────────────────────────────────────────────────
    // Mesh groups
    // ─────────────────────────────────────────────────────────────────────────

    /// <summary>The planned gear conversion moves the model to a different slot.</summary>
    public bool PlanIsCrossSlot => Task.GearPlan is { } plan && plan.Request.Source.Slot != plan.Request.Target.Slot;

    /// <summary>Why mesh groups cannot be edited right now, or null.</summary>
    public string? MeshEditBlockReason
        => Runner.IsBusy ? "Wait for the current operation to finish."
            : Task.IsApplied ? "This plan was already applied. Preview again to make another conversion."
            : null;

    public bool IsMeshGroupRemoved(GearOutputModel model, int group)
        => Task.MeshRemovals.TryGetValue(model.Local, out var removal) && removal.Groups.Contains(group);

    public int RemovedMeshGroupCount(GearOutputModel model)
        => Task.MeshRemovals.TryGetValue(model.Local, out var removal) ? removal.Groups.Length : 0;

    /// <summary>
    /// Keeps or removes mesh group <paramref name="group"/> in every given model. A model
    /// always keeps at least one group; requests that would empty it are ignored for it.
    /// </summary>
    public void SetMeshGroupRemoved(IEnumerable<GearOutputModel> models, int group, bool removed)
    {
        if (MeshEditBlockReason != null) return;
        foreach (var model in models)
        {
            if (!model.Editable || group < 0 || group >= model.Groups.Count) continue;
            var groups = Task.MeshRemovals.TryGetValue(model.Local, out var current) ? current.Groups.ToHashSet() : new HashSet<int>();
            if (removed) groups.Add(group);
            else groups.Remove(group);
            if (groups.Count >= model.Groups.Count) continue;
            if (groups.Count == 0) Task.MeshRemovals.Remove(model.Local);
            else Task.MeshRemovals[model.Local] = new MeshRemoval([.. groups.Order()], model.Groups.Count);
        }
    }

    public void KeepAllMeshGroups(IEnumerable<GearOutputModel> models)
    {
        if (MeshEditBlockReason != null) return;
        foreach (var model in models) Task.MeshRemovals.Remove(model.Local);
    }

    /// <summary>Re-previewing keeps removals for output models whose layout did not change.</summary>
    private static void CarryOverMeshRemovals(ConversionTask previous, ConversionTask planned)
    {
        foreach (var (local, removal) in previous.MeshRemovals)
        {
            var before = previous.OutputModels.FirstOrDefault(m => string.Equals(m.Local, local, StringComparison.OrdinalIgnoreCase));
            var after  = planned.OutputModels.FirstOrDefault(m => string.Equals(m.Local, local, StringComparison.OrdinalIgnoreCase));
            if (before == null || after is not { Editable: true } || after.Groups.Count != removal.ExpectedGroupCount ||
                !before.Groups.Select(g => g.Material).SequenceEqual(after.Groups.Select(g => g.Material)))
                continue;
            planned.MeshRemovals[after.Local] = removal;
        }
    }

    /// <summary>Short description of the current conversion, e.g. "Body 0164-1 → Hands 0200-1".</summary>
    public string Describe(DetectedItem source)
    {
        if (source.IsCustomization)
            return $"{RaceLabel(source.GenderRace ?? 0)} {source.ItemName} → {RaceLabel(TargetRace)} {TargetOptionLabel}";
        var target = TargetItem == null ? "?" : $"{TargetItem.Name} ({TargetItem.ModelIdDisplay})";
        return $"{source.ItemName} ({source.ModelIdDisplay}) → {target}" +
               (TargetSlot != source.Slot ? $" [{SlotInfo.DisplayLabelMap[source.Slot]} → {SlotInfo.DisplayLabelMap[TargetSlot]}]" : string.Empty);
    }

    // ─────────────────────────────────────────────────────────────────────────
    // Apply
    // ─────────────────────────────────────────────────────────────────────────

    public void Apply()
    {
        if (ApplyBlockReason != null || Source is not { } source) return;

        var task        = Task;
        var isNewMod    = OutputMode == ConversionOutputMode.NewMod;
        var newModDir   = NewModPath;
        var newModName  = NewModName.Trim();
        var sourceName  = ModName;
        var description = Describe(source);
        Result = null;
        Log.BeginOperation(isConversion: true);
        Log.Add($"Converting {description} ({(isNewMod ? $"new mod '{newModName}'" : "in place")})…");
        if (task.MeshRemovals.Count > 0)
            Log.Add($"Removing {task.MeshRemovals.Values.Sum(r => r.Groups.Length)} mesh group(s) from {task.MeshRemovals.Count} model(s).");

        void Post(string message) => Runner.Post(() => Log.Add(message));

        Runner.TryRun("Converting…", () =>
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
            Post("Verifying the converted item…");
            return (Output: outputDir, Issues: _plugin.Converter.VerifyConversion(task, isNewMod ? outputDir : null));
        }, result =>
        {
            if (result.Output == null)
            {
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
            Log.Add(LogLevel.Error, $"Conversion failed: {ex.Message}");
            Result = new ResultBanner(BannerKind.Error, "Conversion failed", ex.Message);
        });
    }

    private void FinishNewMod(ConversionTask task, string outputDir, string name, string sourceName, string description, int problems)
    {
        Config.LastNewModName = name;
        var record = History.Record(task, description, sourceName);
        var folder = Path.GetFileName(outputDir);

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
        if (PenumbraAvailable)
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

        _plugin.Converter.ConfirmInPlace(task, Log.Add);
        var record = History.Record(task, description, sourceName);
        Log.Add(LogLevel.Success, $"Converted {description} in place.");
        Result = new ResultBanner(problems > 0 ? BannerKind.Warning : BannerKind.Success, "Mod converted in place",
            (PenumbraAvailable ? "The mod was reloaded in Penumbra." : "Reload the mod in Penumbra to see the change.") +
            ProblemSuffix(problems), task.ModDirectory, record.Id);
        Rescan(); // The source item no longer exists in this mod.
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

    public void Revert(Guid recordId)
    {
        if (History.Find(recordId) is not { } record) return;
        if (RevertBlockReason(record) is { } reason)
        {
            Result = new ResultBanner(BannerKind.Error, "Cannot revert", reason);
            return;
        }

        Log.BeginOperation(isConversion: false);
        Log.Add($"Reverting {record.Description}…");
        Result = null;
        Runner.TryRun("Reverting…", () => History.Revert(record, msg => Runner.Post(() => Log.Add(msg))), result =>
        {
            History.MarkReverted(record, result);
            if (!result.Success)
            {
                Result = new ResultBanner(BannerKind.Error, "Revert failed", result.Message);
                return;
            }

            if (PenumbraAvailable && result.PenumbraFolder is { } folder)
            {
                // The new mod's folder was already moved away, so this only unregisters it.
                // A restored in-place mod is reloaded so Penumbra picks up the original again.
                var updated = record.Mode == ConversionOutputMode.NewMod && !Directory.Exists(record.PublishedPath)
                    ? _plugin.PenumbraIpc.DeleteMod(folder)
                    : _plugin.PenumbraIpc.ReloadMod(folder);
                if (!updated)
                    Log.Add(LogLevel.Warning, $"Penumbra could not update '{folder}'. Use 'Rediscover Mods' in Penumbra.");
                RefreshMods();
            }

            Log.Add(LogLevel.Success, result.Message);

            if (record.Mode == ConversionOutputMode.NewMod && SamePath(record.PublishedPath, ModDirectory))
                SelectMod(record.SourceModDirectory);
            else if (SamePath(record.SourceModDirectory, ModDirectory))
            {
                Task = new ConversionTask();
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

    public static string RaceLabel(ushort genderRace)
    {
        var index = genderRace / 100 - 1;
        if (genderRace % 100 != 1 || index < 0 || index >= RaceNames.Length * 2) return $"c{genderRace:D4}";
        return $"{RaceNames[index / 2]} {(index % 2 == 0 ? "Male" : "Female")}";
    }

    public static void OpenFolder(string path)
    {
        try
        {
            if (Directory.Exists(path)) Process.Start(new ProcessStartInfo(path) { UseShellExecute = true });
        }
        catch (Exception ex)
        {
            Plugin.Log.Warning(ex, "[APIC] Could not open {0}", path);
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
