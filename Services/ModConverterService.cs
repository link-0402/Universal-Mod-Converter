using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using System.Text.RegularExpressions;
using UniversalModConverter.Core;
using UniversalModConverter.Models;
using Dalamud.Plugin.Services;

namespace UniversalModConverter.Services;

/// <summary>
/// Plans, applies and verifies conversions of a Penumbra mod.
/// Gear (equipment, accessories, facewear) goes through the game-path based
/// <see cref="GearConversionPlanner"/>; hair, face, tail and ear roots go through the
/// <see cref="CustomizationPlanner"/>, except texture fan-outs, which go through the
/// <see cref="TextureFanOutPlanner"/>. All publish through a staged copy so a failure never
/// leaves a half-converted mod behind.
/// </summary>
public sealed class ModConverterService
{
    private const string BackupFolderName = ".umc-backups";

    /// <summary>Backups under the system temp folder live here, all Penumbra roots together.</summary>
    internal const string TempBackupFolderName = "UniversalModConverter-backups";

    private readonly IPluginLog           _log;
    private readonly IGameFileProvider    _gameFiles;
    private readonly GameDataService?     _gameData;
    private readonly Configuration?       _configuration;
    private readonly CustomizationPlanner _customizationPlanner;
    private readonly IAnimationRetargeter? _retargeter;

    /// <summary>The game's race tree, read once whichever thread asks first: planning and the window both do.</summary>
    private readonly Lazy<HumanPbd?> _pbd;

    public ModConverterService(IPluginLog log, GameDataService? gameData = null, IFramework? framework = null,
        IAnimationRetargeter? retargeter = null, Configuration? configuration = null)
    {
        _log           = log;
        _gameData      = gameData;
        _retargeter    = retargeter;
        _configuration = configuration;
        _gameFiles     = (IGameFileProvider?)gameData ?? NoGameFiles.Instance;
        _pbd           = new Lazy<HumanPbd?>(ReadRaceTree);
        var skeletons = gameData == null || framework == null
            ? null
            : new SkeletonHierarchyService(gameData, new HavokSkeletonHierarchyReader(framework), log);
        _customizationPlanner = new CustomizationPlanner(gameData, log, skeletons);
    }

    // ─────────────────────────────────────────────────────────────────────────
    // Planning
    // ─────────────────────────────────────────────────────────────────────────

    /// <summary>Validates that <paramref name="modDir"/> holds a readable Penumbra mod (any format).</summary>
    public (bool ok, string error) ValidateModDirectory(string modDir)
    {
        if (!PenumbraMod.IsModDirectory(modDir, out var error)) return (false, error);
        try
        {
            _ = PenumbraMod.Load(modDir);
        }
        catch (OutdatedModFormatException ex)
        {
            return (false, ex.Message);
        }
        catch (Exception ex)
        {
            return (false, $"The mod definition cannot be read: {ex.Message}");
        }

        return (true, string.Empty);
    }

    /// <summary>Plans all changes without touching disk and sets <c>task.IsPlanned</c>.</summary>
    public void PlanConversion(ConversionTask task)
    {
        task.PlannedRenames.Clear();
        task.PlannedJsonChanges.Clear();
        task.PlannedBinaryPatches.Clear();
        task.PlannedMdlChanges.Clear();
        task.PlannedGeneratedFiles.Clear();
        task.AllAssetFiles.Clear();
        task.OutputModels.Clear();
        task.MeshRemovals.Clear();
        task.MeshDefaultsApplied.Clear();
        task.Diagnostics.Clear();
        task.GearPlan = null;
        task.AnimationPlan = null;
        task.TexturePlan = null;
        task.MergedPlan = null;
        task.IsPlanned = false;
        task.IsApplied = false;
        task.ResultStatus = ConversionResultStatus.NotStarted;
        task.PublishedPath = null;
        task.RecoveryPath = null;
        task.ErrorMessage = null;

        try
        {
            if (task.IsQueue)
            {
                PlanQueue(task);
                return;
            }

            if (task.Kind == AssetKind.Animation)
            {
                PlanAnimation(task);
                return;
            }

            if (task.TextureRequest != null)
            {
                PlanTextureFanOut(task);
                return;
            }

            if (CustomizationKinds.IsCustomization(task.Kind))
            {
                // The window never offers this; refusing it here too keeps a stray request from
                // quietly converting the original away.
                if (task.OutputMode.KeepsSource()) throw new InvalidDataException(OutputModeRules.ReplacesOriginal(task.Kind));
                _customizationPlanner.Plan(task);
                ConversionPlanValidator.Finalize(task);
                return;
            }

            var request = BuildGearRequest(task);
            var plan = new GearConversionPlanner(_gameFiles).Plan(task.ModDirectory, request);
            task.GearPlan = plan;
            task.Diagnostics.AddRange(plan.Diagnostics);
            if (CrossSlotDiagnostic(request) is { } note) task.Diagnostics.Add(note);
            task.OutputModels.AddRange(GearOutputModels.Collect(plan, task.ModDirectory));
            Finish(task, plan);
            _log.Information("[UMC] Planned {0} -> {1} ({2}): {3} path(s), {4} file operation(s), {5} diagnostic(s).",
                request.Source, request.Target, request.Mode, plan.GamePathMap.Count, plan.Files.Count, plan.Diagnostics.Count);
        }
        catch (Exception ex)
        {
            task.ErrorMessage = ex.Message;
            task.Diagnostics.Add(new PlanDiagnostic("planning_failed", ex.Message, true));
            _log.Error(ex, "[UMC] PlanConversion failed");
        }
    }

    /// <summary>
    /// Plans every enabled conversion of a run into one mod. Each reads the mod as it is on
    /// disk; the merger rejects any that would undo another, and a rejected one blocks the run
    /// rather than quietly converting the rest.
    /// </summary>
    private void PlanQueue(ConversionTask task)
    {
        var context = new ModPlanContext(task.ModDirectory, task.PlanMode, shared: true);
        var merger  = new ModPlanMerger(context);

        foreach (var entry in task.Entries)
        {
            entry.ResetPlan();
            if (!entry.Enabled) continue;
            if (CustomizationKinds.IsCustomization(entry.Kind))
            {
                // Customization conversions patch their files on disk rather than producing a
                // file plan, so they cannot share a definition with the others yet.
                entry.Diagnostics.Add(new PlanDiagnostic("queue_unsupported_kind",
                    $"{entry.Description}: hair, face, tail, Viera-ear and skin conversions cannot be converted " +
                    "together with others yet. Convert this one on its own.", true));
                entry.Rejected = true;
                continue;
            }

            entry.Task.OutputMode = task.OutputMode;
            entry.Task.KeepsWholeMod = task.KeepsWholeMod;
            entry.Task.ModDirectory = task.ModDirectory;
            MergedPlanEntry merged;
            GearConversionRequest? gear = null;
            if (entry.Kind == AssetKind.Animation)
                merged = merger.Add(entry.Description, AnimationRoots(entry),
                    ctx => new AnimationConversionPlanner(_gameFiles, ParentRace, _retargeter)
                        .Plan(ctx, ForMode(entry.Task.AnimationRequest
                                  ?? throw new InvalidOperationException("Choose what to do with the animation."),
                              task.PlanMode)));
            else
            {
                var request = gear = BuildGearRequest(entry.Task);
                merged = merger.Add(entry.Description, GearRoots(request),
                    ctx => new GearConversionPlanner(_gameFiles).Plan(ctx, request));
            }
            entry.Plan = merged.Plan;
            entry.Rejected = merged.Rejected;
            entry.Diagnostics.AddRange(merged.Diagnostics);
            // The Mesh groups tab treats a run's conversions like single ones, so they say the same.
            if (gear != null && !merged.Rejected && CrossSlotDiagnostic(gear) is { } note) entry.Diagnostics.Add(note);
        }

        context.RunFinalizers();
        var plan = merger.Build();
        task.MergedPlan = plan;
        task.Diagnostics.AddRange(plan.Diagnostics);
        foreach (var entry in task.Entries)
            task.Diagnostics.AddRange(entry.Diagnostics
                .Where(d => !plan.Diagnostics.Contains(d))
                .Select(d => d with { Message = $"{entry.Description}: {d.Message}" }));

        // Mesh editing needs the models the run produced, which only exist once every entry has
        // planned and the finalizers have settled the definition.
        foreach (var entry in task.Entries.Where(e => !e.Rejected))
            if (entry.Plan is GearConversionPlan gear)
                task.OutputModels.AddRange(GearOutputModels.Collect(gear, task.ModDirectory));

        Finish(task, plan);
        _log.Information("[UMC] Planned a run of {0} conversion(s) ({1}): {2} file operation(s), {3} diagnostic(s).",
            plan.Entries.Count(e => !e.Rejected), task.OutputMode, plan.Files.Count, task.Diagnostics.Count);
    }

    /// <summary>
    /// What a queued gear conversion claims: the item it reads and the one it writes. A root
    /// such as e0728 holds every slot of a set, so the slot is part of the claim; body and
    /// hands of the same set are different items and may be converted together.
    /// </summary>
    private static IEnumerable<string> GearRoots(GearConversionRequest request)
        => [$"{request.Source.Root} ({request.Source.Slot})", $"{request.Target.Root} ({request.Target.Slot})"];

    /// <summary>What a gear conversion to another slot does and does not change, or null when it keeps its slot.</summary>
    private static PlanDiagnostic? CrossSlotDiagnostic(GearConversionRequest request)
        => GearSlots.CrossSlotNote(request.Source.Slot, request.Target.Slot) is { } note
            ? new PlanDiagnostic("cross_slot_geometry", note + " Untick anything else in the Mesh groups tab.", false)
            : null;

    /// <summary>
    /// The request for the output mode chosen at preview time; a plan entry is made before the
    /// mode may change. Adding to the mod always keeps the animation where it is, converting in
    /// place keeps it there when asked to, and a new mod holds only what the conversion writes.
    /// </summary>
    private static AnimationConversionRequest ForMode(AnimationConversionRequest request, ConversionOutputMode mode)
        => request with
        {
            Mode = mode,
            KeepOriginal = mode.KeepsSource() || mode == ConversionOutputMode.InPlace && request.KeepOriginal,
        };

    /// <summary>What a queued animation conversion claims: the animations it reads and writes.</summary>
    private static IEnumerable<string> AnimationRoots(QueuedConversion entry)
    {
        if (entry.Task.AnimationRequest is not { } request) return [];
        return request.SourceLocations
            .Concat(request.Variants.SelectMany(v => v.Locations.Values))
            .Distinct(StringComparer.OrdinalIgnoreCase);
    }

    /// <summary>Records a finished file plan on the task: its fingerprints, and whether it can run.</summary>
    private static void Finish(ConversionTask task, IModFilePlan plan)
    {
        task.SourceFingerprint = SourceFingerprint(task) ?? string.Empty;
        task.PlanFingerprint = plan.Fingerprint();
        task.IsPlanned = true;
        task.ErrorMessage = task.HasBlockers
            ? string.Join(" ", task.Diagnostics.Where(d => d.IsBlocker).Select(d => d.Message))
            : null;
    }

    /// <summary>
    /// A hash of every file the plan was made from, the mod's definition included, so that a
    /// change to any of them after the preview is caught before writing. Null once one of them
    /// no longer exists.
    /// </summary>
    private static string? SourceFingerprint(ConversionTask task)
    {
        if (task.FilePlan is not { } plan) return ConversionPlanValidator.RecomputeSourceFingerprint(task);
        try
        {
            return ModFingerprint.Compute(task.ModDirectory,
                plan.InputFiles.Concat(PenumbraMod.DefinitionFiles(task.ModDirectory).Where(File.Exists)));
        }
        catch (Exception)
        {
            return null; // A planned input no longer exists.
        }
    }

    private void PlanAnimation(ConversionTask task)
    {
        var request = ForMode(task.AnimationRequest ?? throw new InvalidOperationException("Choose what to do with the animation."),
            task.PlanMode);
        var plan = new AnimationConversionPlanner(_gameFiles, ParentRace, _retargeter)
            .Plan(task.ModDirectory, request);
        task.AnimationPlan = plan;
        task.Diagnostics.AddRange(plan.Diagnostics);
        Finish(task, plan);
        _log.Information("[UMC] Planned animation {0} ({1}): {2} file operation(s), {3} diagnostic(s).",
            request.Description, request.Mode, plan.Files.Count, plan.Diagnostics.Count);
    }

    private void PlanTextureFanOut(ConversionTask task)
    {
        // The request is made before the output mode may change, so the mode is applied here.
        var request = task.TextureRequest! with { Mode = task.OutputMode };
        var plan = new TextureFanOutPlanner(_gameFiles, _gameData == null
                ? null
                : target => _gameData.DescribeCustomization(target.Kind, target.GenderRace, target.ModelId))
            .Plan(task.ModDirectory, request);
        task.TexturePlan = plan;
        task.Diagnostics.AddRange(plan.Diagnostics);
        Finish(task, plan);
        _log.Information("[UMC] Planned texture fan-out {0} ({1}): {2} path(s), {3} file operation(s), {4} diagnostic(s).",
            request.Description, request.Mode, plan.Outputs.Count, plan.Files.Count, plan.Diagnostics.Count);
    }

    /// <summary>The skeleton parent of a race in the game's race tree (human.pbd).</summary>
    public ushort? ParentRace(ushort race) => _pbd.Value?.GetParentRace(race);

    private HumanPbd? ReadRaceTree()
    {
        try
        {
            if (_gameData?.GetHumanPbdBytes() is { } bytes) return new HumanPbd(bytes);
        }
        catch (Exception ex)
        {
            _log.Warning(ex, "[UMC] The race tree could not be read; animations will not inherit between races.");
        }
        return null;
    }

    public static GearConversionRequest BuildGearRequest(ConversionTask task)
    {
        if (!ushort.TryParse(task.OldIdPadded, out var sourceId) || !ushort.TryParse(task.NewIdPadded, out var targetId))
            throw new InvalidDataException("Source and target model IDs must be numbers between 0 and 65535.");
        var source = new GearItem(SlotInfo.ToGearSlot(task.Slot), sourceId, (ushort)Math.Max(0, task.SourceVariant));
        var target = new GearItem(SlotInfo.ToGearSlot(task.TargetSlot ?? task.Slot), targetId,
            (ushort)Math.Max(0, task.TargetVariant));
        return new GearConversionRequest(source, target, task.PlanMode);
    }

    /// <param name="newMod">
    /// Which of the two write paths is about to run. The plan must agree both on the exact mode
    /// and on which path it was built for, so a plan made for one cannot be written by the other.
    /// A new mod that keeps the whole mod is planned like converting in place (see
    /// <see cref="ConversionTask.PlanMode"/>), and only the new-mod path writes it.
    /// </param>
    private static void EnsurePlanIsCurrent(ConversionTask task, bool newMod)
    {
        if (!task.IsPlanned) throw new InvalidOperationException("Wait for the preview of the plan before applying it.");
        if (task.HasBlockers) throw new InvalidOperationException(task.ErrorMessage ?? "The conversion plan has blockers.");
        var planned = task.FilePlan?.Mode ?? task.PlanMode;
        if (planned != task.PlanMode || task.OutputMode.IsNewMod() != newMod)
            throw new InvalidOperationException("The output mode changed after the preview. Wait for the preview to update, then try again.");
        if (!string.Equals(SourceFingerprint(task), task.SourceFingerprint, StringComparison.Ordinal))
            throw new InvalidOperationException("The mod changed on disk after the preview. The preview is updated now; try again once it is ready.");
    }

    // ─────────────────────────────────────────────────────────────────────────
    // Applying in place
    // ─────────────────────────────────────────────────────────────────────────

    /// <summary>
    /// Applies the plan to a shadow copy of the mod, then swaps the copy in. The original
    /// is kept in the backup folder until Penumbra confirms the reload.
    /// </summary>
    public void ApplyConversion(ConversionTask task, Action<string>? onLog = null)
    {
        task.IsApplied = false;
        task.ResultStatus = ConversionResultStatus.NotStarted;
        task.ErrorMessage = null;

        void Log(string msg) { onLog?.Invoke(msg); _log.Information("[UMC] {0}", msg); }

        string? stageDir = null;
        string? backupDir = null;
        bool sourceMoved = false;
        try
        {
            EnsurePlanIsCurrent(task, newMod: false);

            var sourceDir = Path.GetFullPath(task.ModDirectory).TrimEnd('\\', '/');
            var parent = Path.GetDirectoryName(sourceDir) ?? throw new InvalidOperationException("The mod has no parent directory.");
            var name = Path.GetFileName(sourceDir);
            var id = Guid.NewGuid().ToString("N");
            stageDir = Path.Combine(parent, $".{name}.umc-stage-{id}");
            var backupRoot = BackupRoot(parent, _configuration?.BackupDirectory);
            // The swap moves whole folders, which cannot cross drives: say so now, not after copying the mod.
            if (!SameVolume(backupRoot, parent)) throw new InvalidOperationException(BackupOnOtherDrive(backupRoot));
            backupDir = Path.Combine(backupRoot, BackupRetention.FolderName(name, DateTime.UtcNow, id));
            task.JournalPath = Path.Combine(parent, $".{name}.umc-recovery-{id}.json");

            Log($"Staging complete mod shadow: {stageDir}");
            CopyDirectory(sourceDir, stageDir);
            if (task.FilePlan is { } plan)
                GearConversionExecutor.ApplyInPlace(plan, stageDir, Log);
            else
            {
                var stagedTask = RemapTask(task, stageDir);
                ApplyConversionCore(stagedTask, Log);
            }
            // Gear and customization models can lose parts; anything else has no removals.
            GearOutputModels.ApplyRemovals(stageDir, task.MeshRemovals, Log);
            ValidateModDefinition(stageDir);

            File.WriteAllText(task.JournalPath, JsonSerializer.Serialize(new
            {
                Version = 1,
                Source = sourceDir,
                Backup = backupDir,
                Stage = stageDir,
                task.SourceFingerprint,
                task.PlanFingerprint,
                CreatedUtc = DateTime.UtcNow,
            }, new JsonSerializerOptions { WriteIndented = true }), Encoding.UTF8);

            Directory.Move(sourceDir, backupDir);
            sourceMoved = true;
            Directory.Move(stageDir, sourceDir);
            stageDir = null;
            task.RecoveryPath = backupDir;
            task.PublishedPath = sourceDir;
            task.ResultStatus = ConversionResultStatus.PublishedButNotActivated;
            task.IsApplied = true;
            Log($"Conversion published atomically. Recovery backup: {backupDir}");
        }
        catch (Exception ex)
        {
            task.ErrorMessage = ex.Message;
            _log.Error(ex, "[UMC] ApplyConversion failed");
            if (sourceMoved && backupDir != null && Directory.Exists(backupDir) && !Directory.Exists(task.ModDirectory))
            {
                try
                {
                    Directory.Move(backupDir, task.ModDirectory);
                    task.ResultStatus = ConversionResultStatus.RolledBack;
                }
                catch (Exception rollback)
                {
                    // The mod folder is empty and the original is whole in the backup: say where it is.
                    task.ResultStatus = ConversionResultStatus.Failed;
                    task.ErrorMessage = $"{ex.Message} Putting the original back failed too ({rollback.Message}). " +
                                        $"The original mod is safe in {backupDir}.";
                    _log.Error(rollback, "[UMC] Rolling back ApplyConversion failed");
                }
            }
            else
                task.ResultStatus = ConversionResultStatus.Failed;
            if (stageDir != null && Directory.Exists(stageDir))
                try { Directory.Delete(stageDir, true); } catch { }
            onLog?.Invoke($"Error: {task.ErrorMessage}");
        }
    }

    /// <summary>Why a backup folder on another drive than the mods cannot be used.</summary>
    internal static string BackupOnOtherDrive(string backupFolder)
        => $"The backup folder '{backupFolder}' is on a different drive than the mods, so nothing can be moved into it. " +
           "Choose a folder on the same drive in Settings, or clear it to use the default.";

    /// <summary>
    /// Where backups are written, preferring the system temp folder so they are transient
    /// storage the machine can reclaim. Publishing a conversion swaps whole directories with
    /// <see cref="Directory.Move"/>, which cannot cross volumes, so a temp folder on another
    /// drive than the Penumbra root is no use: that case falls back to a hidden folder beside
    /// the mods, where Penumbra never discovers it as a duplicate mod. A configured
    /// <paramref name="customBackupDirectory"/> wins over both, and is the user's problem to
    /// keep on the right volume.
    /// </summary>
    /// <param name="create">False to resolve the path without creating anything on disk.</param>
    internal static string BackupRoot(string penumbraRoot, string? customBackupDirectory = null, bool create = true)
    {
        if (!string.IsNullOrWhiteSpace(customBackupDirectory))
        {
            if (create) Directory.CreateDirectory(customBackupDirectory);
            return customBackupDirectory;
        }

        var temp = Path.Combine(Path.GetTempPath(), TempBackupFolderName);
        if (SameVolume(temp, penumbraRoot))
        {
            if (create) Directory.CreateDirectory(temp);
            return temp;
        }

        var root = Path.Combine(penumbraRoot, BackupFolderName);
        if (!create) return root;
        var info = Directory.CreateDirectory(root);
        if ((info.Attributes & FileAttributes.Hidden) == 0)
            info.Attributes |= FileAttributes.Hidden;
        return root;
    }

    /// <summary>
    /// True when both paths sit on the same volume, so a directory move is atomic. Both the paths
    /// as written (a move between drive letters is refused outright) and the paths behind any
    /// junction or symbolic link on the way (a Penumbra folder linked to another drive) must agree.
    /// </summary>
    internal static bool SameVolume(string a, string b)
    {
        try
        {
            static bool SameRoot(string x, string y)
                => string.Equals(Path.GetPathRoot(x), Path.GetPathRoot(y), StringComparison.OrdinalIgnoreCase);
            return SameRoot(Path.GetFullPath(a), Path.GetFullPath(b)) && SameRoot(ResolvedPath(a), ResolvedPath(b));
        }
        catch (Exception)
        {
            return false;
        }
    }

    /// <summary><paramref name="path"/> with every junction and symbolic link on the way resolved.</summary>
    private static string ResolvedPath(string path)
    {
        var full = Path.GetFullPath(path).TrimEnd('\\', '/');
        for (var depth = 0; depth < 16; depth++)
        {
            string? resolved = null;
            for (var current = full; !string.IsNullOrEmpty(current); current = Path.GetDirectoryName(current))
            {
                var info = new DirectoryInfo(current);
                if (!info.Exists || info.LinkTarget == null) continue;
                if (info.ResolveLinkTarget(returnFinalTarget: true) is { } target)
                    resolved = Path.Join(target.FullName, Path.GetRelativePath(current, full));
                break;
            }
            if (resolved == null) return full;
            full = Path.GetFullPath(resolved).TrimEnd('\\', '/');
        }
        return full;
    }

    /// <summary>Applies a customization plan (hair, face, tail, ear) to a staged directory.</summary>
    private void ApplyConversionCore(ConversionTask task, Action<string> log)
    {
        foreach (var mdl in task.PlannedMdlChanges) ApplyMdlChange(mdl, log);
        foreach (var bp in task.PlannedBinaryPatches) ApplyBinaryPatch(bp, log);
        foreach (var jc in task.PlannedJsonChanges) ApplyJsonChange(jc, log);
        foreach (var rename in task.PlannedRenames.OrderByDescending(r => r.OldPath.Length))
            ApplyRename(rename, log);

        foreach (var generated in task.PlannedGeneratedFiles)
        {
            PathSafety.EnsureContained(task.ModDirectory, generated.FilePath);
            Directory.CreateDirectory(Path.GetDirectoryName(generated.FilePath)!);
            using var stream = new FileStream(generated.FilePath, FileMode.CreateNew, FileAccess.Write);
            stream.Write(generated.Data.AsSpan());
            log($"Included material dependency: {generated.GamePath}");
        }
        PruneEmptyCustomizationDirs(task, log);
    }

    /// <summary>A published mod must load: readable definition files and a non-empty name.</summary>
    private static void ValidateModDefinition(string directory)
    {
        var mod = PenumbraMod.Load(directory);
        if (string.IsNullOrWhiteSpace(mod.Name))
            throw new InvalidDataException("The mod definition has no name; Penumbra would reject it.");
        foreach (var container in mod.Containers)
        foreach (var (key, local) in container.FileEntries())
            _ = PathSafety.ResolveRelative(directory, GamePath.ToLocal(local));
    }

    public void ConfirmInPlace(ConversionTask task, Action<string>? onLog = null)
    {
        if (task.ResultStatus != ConversionResultStatus.PublishedButNotActivated) return;
        if (task.JournalPath is { } journal && File.Exists(journal)) File.Delete(journal);
        task.ResultStatus = ConversionResultStatus.Succeeded;
        onLog?.Invoke("Penumbra activation confirmed; recovery journal cleared.");
    }

    public bool RollbackInPlace(ConversionTask task, Action<string>? onLog = null)
    {
        try
        {
            if (task.RecoveryPath is not { } backup || !Directory.Exists(backup)) return false;
            var current = Path.GetFullPath(task.ModDirectory).TrimEnd('\\', '/');
            // Named like the staging folders: hidden beside the mods, where Penumbra never takes it
            // for one, and where the leftover sweep finds it if it cannot be deleted right away.
            var failed = Path.Combine(Path.GetDirectoryName(current) ?? current,
                $".{Path.GetFileName(current)}.umc-failed-{Guid.NewGuid():N}");
            if (Directory.Exists(current)) Directory.Move(current, failed);
            Directory.Move(backup, current);
            try { if (Directory.Exists(failed)) Directory.Delete(failed, true); } catch { }
            if (task.JournalPath is { } journal && File.Exists(journal)) File.Delete(journal);
            task.ResultStatus = ConversionResultStatus.RolledBack;
            task.IsApplied = false;
            onLog?.Invoke("Penumbra activation failed; the original mod was restored.");
            return true;
        }
        catch (Exception ex)
        {
            task.ErrorMessage = $"Automatic rollback failed: {ex.Message}";
            onLog?.Invoke(task.ErrorMessage);
            return false;
        }
    }

    // ─────────────────────────────────────────────────────────────────────────
    // New mods
    // ─────────────────────────────────────────────────────────────────────────

    /// <summary>
    /// Creates a new mod next to the source mod. Gear and animation conversions contain only the
    /// converted item (with the source's option structure) unless the task keeps the whole mod;
    /// customization conversions and texture fan-outs copy the mod.
    /// The source mod is never modified.
    /// </summary>
    public string? CreateNewModFromAssetChain(
        ConversionTask task, string newModDir, string modDisplayName, Action<string>? onLog = null)
    {
        task.IsApplied = false;
        task.ResultStatus = ConversionResultStatus.NotStarted;
        task.ErrorMessage = null;
        string? stageDir = null;
        void Log(string msg) { onLog?.Invoke(msg); _log.Information("[UMC] {0}", msg); }
        try
        {
            EnsurePlanIsCurrent(task, newMod: true);

            var finalDir = Path.GetFullPath(newModDir).TrimEnd('\\', '/');
            if (Directory.Exists(finalDir) || File.Exists(finalDir))
                throw new IOException($"The output path already exists: {finalDir}");
            var parent = Path.GetDirectoryName(finalDir) ?? throw new InvalidOperationException("The output path has no parent.");
            stageDir = Path.Combine(parent, $".{Path.GetFileName(finalDir)}.umc-stage-{Guid.NewGuid():N}");
            if (task.FilePlan is { } copied && (task.TexturePlan != null || task.KeepsWholeMod))
            {
                // A fan-out keeps its source, so the new mod is all of this one plus the added
                // paths; a new mod keeping the whole mod is this one converted as in place.
                CopyDirectory(task.ModDirectory, stageDir);
                GearConversionExecutor.ApplyInPlace(copied, stageDir, Log);
                UpdateMetaJsonName(stageDir, modDisplayName, Log);
            }
            else if (task.FilePlan is { } plan)
                GearConversionExecutor.WriteNewMod(plan, task.ModDirectory, stageDir, modDisplayName, Log);
            else
            {
                CopyDirectory(task.ModDirectory, stageDir);
                var stagedTask = RemapTask(task, stageDir);
                ApplyConversionCore(stagedTask, Log);
                UpdateMetaJsonName(stageDir, modDisplayName, Log);
            }
            // The copy of a customization's mod keeps its layout, so its models are where the plan says.
            GearOutputModels.ApplyRemovals(stageDir, task.MeshRemovals, Log);

            ValidateModDefinition(stageDir);
            Directory.Move(stageDir, finalDir);
            stageDir = null;
            task.PublishedPath = finalDir;
            task.ResultStatus = ConversionResultStatus.PublishedButNotActivated;
            task.IsApplied = true;
            Log($"New mod published atomically: {finalDir}");
            return finalDir;
        }
        catch (Exception ex)
        {
            if (stageDir != null && Directory.Exists(stageDir))
                try { Directory.Delete(stageDir, true); } catch { }
            task.ResultStatus = ConversionResultStatus.Failed;
            task.ErrorMessage = ex.Message;
            onLog?.Invoke($"Error: {ex.Message}");
            _log.Error(ex, "[UMC] Atomic new-mod publication failed");
            return null;
        }
    }

    /// <summary>
    /// Writes a merged modpack to <paramref name="newModDir"/>. Like any new mod it is staged
    /// beside its final place and moved there whole, so a failure never leaves half a mod.
    /// </summary>
    public string PublishMerge(ModMergePlan plan, string newModDir, Action<string>? onLog = null)
    {
        void Log(string msg) { onLog?.Invoke(msg); _log.Information("[UMC] {0}", msg); }

        var finalDir = Path.GetFullPath(newModDir).TrimEnd('\\', '/');
        if (Directory.Exists(finalDir) || File.Exists(finalDir))
            throw new IOException($"The output path already exists: {finalDir}");
        var parent = Path.GetDirectoryName(finalDir) ?? throw new InvalidOperationException("The output path has no parent.");
        var stageDir = Path.Combine(parent, $".{Path.GetFileName(finalDir)}.umc-stage-{Guid.NewGuid():N}");
        try
        {
            ModMerger.Write(plan, stageDir, Log);
            ValidateModDefinition(stageDir);
            Directory.Move(stageDir, finalDir);
            Log($"Merged mod published: {finalDir}");
            return finalDir;
        }
        catch
        {
            if (Directory.Exists(stageDir))
                try { Directory.Delete(stageDir, true); } catch { }
            throw;
        }
    }

    /// <summary>
    /// Sets the display name of a copied mod. A copy also needs its own stable identifier;
    /// two mods must not claim the same one.
    /// </summary>
    private void UpdateMetaJsonName(string modDir, string displayName, Action<string> log)
    {
        var mod = PenumbraMod.Load(modDir);
        mod.Meta["Name"] = displayName;
        mod.Meta["Identifier"] = Guid.NewGuid().ToString();
        mod.Save(modDir);
        log($"Set mod display name to: {displayName}");
    }

    private static ConversionTask RemapTask(ConversionTask original, string newBaseDir)
    {
        // The planners work with full paths, so both sides are compared in that form: a folder
        // typed as "E:/Mods/Hair" would otherwise match nothing, and every edit would land in the
        // source mod instead of the copy. A path outside the mod has no place in the copy at all.
        var oldBase = Path.GetFullPath(original.ModDirectory).TrimEnd('\\', '/');
        var newBase = Path.GetFullPath(newBaseDir).TrimEnd('\\', '/');

        string Remap(string p)
        {
            var full = Path.GetFullPath(p);
            if (string.Equals(full, oldBase, StringComparison.OrdinalIgnoreCase)) return newBase;
            if (full.StartsWith(oldBase + Path.DirectorySeparatorChar, StringComparison.OrdinalIgnoreCase))
                return newBase + full[oldBase.Length..];
            throw new InvalidDataException($"{p} is outside the mod folder {oldBase}.");
        }

        var remapped = new ConversionTask
        {
            Kind          = original.Kind,
            OutputMode    = original.OutputMode,
            TargetCustomizationKind = original.TargetCustomizationKind,
            ModDirectory  = newBaseDir,
            Slot          = original.Slot,
            OldIdPadded   = original.OldIdPadded,
            NewIdPadded   = original.NewIdPadded,
            TargetSlot    = original.TargetSlot,
            SourceVariant = original.SourceVariant,
            TargetVariant = original.TargetVariant,
            SourceGenderRace = original.SourceGenderRace,
            TargetGenderRace = original.TargetGenderRace,
            SourceFingerprint = original.SourceFingerprint,
            PlanFingerprint = original.PlanFingerprint,
            IsPlanned     = true,
        };

        foreach (var asset in original.AllAssetFiles) remapped.AllAssetFiles.Add(Remap(asset));
        foreach (var generated in original.PlannedGeneratedFiles)
            remapped.PlannedGeneratedFiles.Add(generated with { FilePath = Remap(generated.FilePath) });
        foreach (var diagnostic in original.Diagnostics) remapped.Diagnostics.Add(diagnostic);

        foreach (var r in original.PlannedRenames)
            remapped.PlannedRenames.Add(new PlannedRename
            {
                OldPath  = Remap(r.OldPath),
                NewPath  = Remap(r.NewPath),
            });

        foreach (var jc in original.PlannedJsonChanges)
        {
            var rc = new PlannedJsonChange { FilePath = Remap(jc.FilePath) };
            foreach (var c in jc.Changes)
                rc.Changes.Add(new JsonFieldChange
                {
                    JsonPath   = c.JsonPath,
                    OldValue   = c.OldValue,
                    NewValue   = c.NewValue,
                    ChangeType = c.ChangeType,
                });
            remapped.PlannedJsonChanges.Add(rc);
        }

        foreach (var bp in original.PlannedBinaryPatches)
        {
            var rb = new PlannedBinaryPatch { FilePath = Remap(bp.FilePath) };
            foreach (var p in bp.Patches)
                rb.Patches.Add(new BinaryStringPatch
                {
                    OldString = p.OldString,
                    NewString = p.NewString,
                });
            remapped.PlannedBinaryPatches.Add(rb);
        }

        foreach (var mdl in original.PlannedMdlChanges)
        {
            var remappedMdl = new PlannedMdlChange
            {
                FilePath = Remap(mdl.FilePath),
                SourceGenderRace = mdl.SourceGenderRace,
                TargetGenderRace = mdl.TargetGenderRace,
                InputHash = mdl.InputHash,
                OutputHash = mdl.OutputHash,
                Version = mdl.Version,
                LodCount = mdl.LodCount,
                MeshCount = mdl.MeshCount,
                VertexCount = mdl.VertexCount,
                ShapeVertexCount = mdl.ShapeVertexCount,
                DeformationPlan = mdl.DeformationPlan,
            };
            remappedMdl.BoneResolutions.AddRange(mdl.BoneResolutions);
            foreach (var patch in mdl.PathReplacements)
                remappedMdl.PathReplacements.Add(new BinaryStringPatch
                {
                    OldString = patch.OldString,
                    NewString = patch.NewString,
                });
            remapped.PlannedMdlChanges.Add(remappedMdl);
        }

        return remapped;
    }

    // ─────────────────────────────────────────────────────────────────────────
    // Verification
    // ─────────────────────────────────────────────────────────────────────────

    /// <summary>
    /// Checks the converted mod in <paramref name="directory"/> (default: the task's mod).
    /// Gear is verified the way the game loads it; customization roots by leftover paths.
    /// </summary>
    public List<LeftoverHit> VerifyConversion(ConversionTask task, string? directory = null)
    {
        var hits = new List<LeftoverHit>();
        var modDirectory = directory ?? task.ModDirectory;
        try
        {
            if (task.MergedPlan != null)
            {
                foreach (var entry in task.Entries.Where(e => !e.Rejected && e.Plan != null))
                foreach (var hit in VerifyPlan(entry.Plan!, modDirectory))
                {
                    // Which of the run's conversions a problem belongs to is the first thing to know.
                    hit.Detail = $"{entry.Description}: {hit.Detail}";
                    hits.Add(hit);
                }

                return hits;
            }

            if (task.FilePlan is { } plan)
            {
                hits.AddRange(VerifyPlan(plan, modDirectory));
                return hits;
            }

            if (CustomizationKinds.IsCustomization(task.Kind))
            {
                var remapped = directory == null ? task : RemapTask(task, directory);
                return VerifyCustomizationConversion(remapped, hits);
            }
        }
        catch (Exception ex)
        {
            _log.Error(ex, "[UMC] VerifyConversion failed");
            hits.Add(new LeftoverHit { FilePath = modDirectory, HitType = "error", Detail = $"Verification failed: {ex.Message}" });
        }

        return hits;
    }

    /// <summary>
    /// Verifies one written plan, a whole conversion or one of a run, with the verifier of its
    /// kind. A kind without one is a mistake to hear about, not a plan to wave through.
    /// </summary>
    private IEnumerable<LeftoverHit> VerifyPlan(IModFilePlan plan, string modDirectory)
    {
        LeftoverHit Hit(string type, string detail) => new() { FilePath = modDirectory, HitType = type, Detail = detail };

        return plan switch
        {
            // Retained source paths are expected when editing the mod (shared resources, and the
            // whole point of adding to it) but not in a new mod.
            GearConversionPlan gear => GearConversionVerifier.Verify(modDirectory, gear.Request.Target,
                    gear.Mode.IsNewMod() ? gear.Request.Source : (GearItem?)null, _gameFiles)
                .Select(issue => Hit(issue.IsError ? "missing" : "leftover", issue.Message)),
            AnimationConversionPlan animation => AnimationConversionVerifier.Verify(modDirectory, animation)
                .Select(issue => Hit(issue.IsError ? "missing" : "note", issue.Message)),
            // The source's paths stay on purpose; what matters is that every added one resolves.
            TextureFanOutPlan texture => texture.Verify(modDirectory).Select(problem => Hit("missing", problem)),
            _ => throw new InvalidOperationException($"There is no verifier for a {plan.GetType().Name}."),
        };
    }

    /// <summary>
    /// The roots a customization conversion moves away (an Au Ra tail's Xaela material root too,
    /// when the target has one), or none when the task does not name its source.
    /// </summary>
    private static IReadOnlyList<CustomizationPathEndpoint> MovedCustomizationRoots(ConversionTask task)
    {
        if (task.SourceGenderRace is not { } race ||
            !ushort.TryParse(task.OldIdPadded, out var modelId)) return [];
        var source = new CustomizationPathEndpoint(task.Kind, race, modelId);
        if (task.TargetGenderRace is not { } targetRace || !ushort.TryParse(task.NewIdPadded, out var targetId))
            return [source];
        return CustomizationPaths.MovedRoots(source,
            new CustomizationPathEndpoint(task.TargetCustomizationKind ?? task.Kind, targetRace, targetId));
    }

    private static List<LeftoverHit> VerifyCustomizationConversion(
        ConversionTask task, List<LeftoverHit> hits)
    {
        var moved = MovedCustomizationRoots(task);
        if (moved.Count == 0) return hits;
        var flagged = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

        foreach (var entry in Directory.EnumerateFileSystemEntries(task.ModDirectory, "*", SearchOption.AllDirectories))
        {
            if (!Moved(entry)) continue;
            flagged.Add(entry);
            hits.Add(new LeftoverHit
            {
                FilePath = entry,
                HitType = "filename",
                Detail = $"Path still contains the old customization root: {RelativePath(task.ModDirectory, entry)}",
            });
        }

        foreach (var jsonFile in PenumbraMod.DefinitionFiles(task.ModDirectory).Where(File.Exists))
        {
            if (!Moved(File.ReadAllText(jsonFile)) || !flagged.Add(jsonFile)) continue;
            hits.Add(new LeftoverHit
            {
                FilePath = jsonFile,
                HitType = "json",
                Detail = $"JSON still contains the old customization root: {RelativePath(task.ModDirectory, jsonFile)}",
            });
        }

        var structuredExtensions = new HashSet<string>(
            [".mdl", ".mtrl", ".avfx", ".atex", ".sklb", ".pap", ".tmb"],
            StringComparer.OrdinalIgnoreCase);
        foreach (var binaryFile in Directory.EnumerateFiles(task.ModDirectory, "*", SearchOption.AllDirectories)
                     .Where(file => structuredExtensions.Contains(Path.GetExtension(file))))
        {
            if (!BinaryPathRewriter.ExtractPaths(File.ReadAllBytes(binaryFile))
                    .Any(Moved) || !flagged.Add(binaryFile)) continue;
            hits.Add(new LeftoverHit
            {
                FilePath = binaryFile,
                HitType = "binary",
                Detail = $"Binary resource still contains the old customization root: {RelativePath(task.ModDirectory, binaryFile)}",
            });
        }

        return hits;

        bool Moved(string value) => moved.Any(root => CustomizationPaths.Contains(value, root));
    }

    // ─────────────────────────────────────────────────────────────────────────
    // Customization apply helpers
    // ─────────────────────────────────────────────────────────────────────────

    private void PruneEmptyCustomizationDirs(ConversionTask task, Action<string> log)
    {
        var moved = MovedCustomizationRoots(task);
        var candidates = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

        foreach (var rename in task.PlannedRenames)
        {
            var directory = Path.GetDirectoryName(rename.OldPath);
            while (directory != null && moved.Any(root => CustomizationPaths.Contains(directory, root)))
            {
                candidates.Add(directory);
                directory = Path.GetDirectoryName(directory);
            }
        }

        foreach (var directory in candidates.OrderByDescending(path => path.Length))
        {
            if (!Directory.Exists(directory) || Directory.EnumerateFileSystemEntries(directory).Any()) continue;
            try
            {
                Directory.Delete(directory);
                log($"Removed empty dir: {RelativePath(task.ModDirectory, directory)}");
            }
            catch (Exception ex)
            {
                _log.Warning(ex, "[UMC] Could not remove empty customization dir {0}", directory);
            }
        }
    }

    private void ApplyRename(PlannedRename rename, Action<string> log)
    {
        if (!File.Exists(rename.OldPath))
            throw new FileNotFoundException("Planned rename source is missing.", rename.OldPath);
        if (File.Exists(rename.NewPath))
            throw new IOException($"Planned rename destination exists: {rename.NewPath}");
        var destDir = Path.GetDirectoryName(rename.NewPath);
        if (!string.IsNullOrEmpty(destDir))
            Directory.CreateDirectory(destDir);
        var sameDir = string.Equals(
            Path.GetDirectoryName(rename.OldPath),
            Path.GetDirectoryName(rename.NewPath),
            StringComparison.OrdinalIgnoreCase);
        File.Move(rename.OldPath, rename.NewPath);
        log(sameDir
            ? $"Renamed: {Path.GetFileName(rename.OldPath)} → {Path.GetFileName(rename.NewPath)}"
            : $"Moved:   {rename.OldPath} → {rename.NewPath}");
    }

    private void ApplyJsonChange(PlannedJsonChange jc, Action<string> log)
    {
        if (jc.Changes.Count == 0) return;

        var raw  = File.ReadAllText(jc.FilePath);
        var node = JsonNode.Parse(raw, new JsonNodeOptions { PropertyNameCaseInsensitive = false },
                       new JsonDocumentOptions { AllowTrailingCommas = true, CommentHandling = JsonCommentHandling.Skip })
                   ?? throw new InvalidDataException($"JSON document is empty: {jc.FilePath}");

        int applied = 0;
        // Key renames and copies go last so value edits can still find their original key.
        foreach (var change in jc.Changes.OrderBy(c => c.ChangeType is "path_key" or "path_key_copy" or "path_key_remove" ? 1 : 0))
            if (ApplyJsonChangeAtPath(node, change)) applied++;

        if (applied != jc.Changes.Count)
            throw new InvalidDataException($"Only {applied} of {jc.Changes.Count} planned JSON changes applied in {jc.FilePath}.");
        var opts = new JsonSerializerOptions { WriteIndented = true };
        File.WriteAllText(jc.FilePath, node.ToJsonString(opts), Encoding.UTF8);
        log($"Updated JSON: {Path.GetFileName(jc.FilePath)} ({applied} changes)");
    }

    private static bool ApplyJsonChangeAtPath(JsonNode root, JsonFieldChange change)
    {
        try
        {
            if (change.ChangeType == "manipulation_insert")
            {
                // Appends a manipulation to a container, creating the container if needed
                // (Penumbra 1.7 omits DefaultData when the default option is empty).
                JsonNode? container = root;
                foreach (var segment in ParseJsonPath(change.JsonPath[6..]))
                {
                    if (segment.Kind == PathSegKind.Property && container is JsonObject parent)
                    {
                        if (parent[segment.Name] is not JsonObject child) parent[segment.Name] = child = new JsonObject();
                        container = child;
                    }
                    else container = segment.Kind == PathSegKind.Index ? (container as JsonArray)?[segment.Index] : null;
                }
                if (container is not JsonObject target) return false;
                if (target["Manipulations"] is not JsonArray manipulations)
                    target["Manipulations"] = manipulations = new JsonArray();
                var manipulation = (JsonObject)JsonNode.Parse(change.NewValue)!;
                var identity = GearManipulations.Identity(manipulation);
                if (!manipulations.OfType<JsonObject>().Any(m => GearManipulations.Identity(m) == identity))
                    manipulations.Add(manipulation);
                return true;
            }

            if (change.ChangeType == "dependency_files")
            {
                // This insertion is scoped to the exact option containing the model.
                JsonNode? option = root;
                foreach (var segment in ParseJsonPath(change.JsonPath[6..]))
                    option = segment.Kind switch
                    {
                        PathSegKind.Property => (option as JsonObject)?[segment.Name],
                        PathSegKind.Index => (option as JsonArray)?[segment.Index],
                        _ => null,
                    };
                if (option is not JsonObject obj) return false;
                var files = obj["Files"] as JsonObject;
                if (files == null) obj["Files"] = files = new JsonObject();
                foreach (var (key, value) in JsonNode.Parse(change.NewValue)!.AsObject())
                {
                    if (files.ContainsKey(key)) return false;
                    files[key] = value?.DeepClone();
                }
                return true;
            }

            var path = change.JsonPath;
            if (!path.StartsWith("<root>")) return false;
            path = path[6..]; // strip "<root>"

            bool isKeyChange = path.EndsWith("[key]");
            if (isKeyChange) path = path[..^5];

            var segments = ParseJsonPath(path);
            if (segments.Count == 0) return false;

            // Navigate to the parent
            JsonNode? current = root;
            for (int i = 0; i < segments.Count - 1; i++)
            {
                var seg = segments[i];
                current = seg.Kind switch
                {
                    PathSegKind.Property => (current as JsonObject)?[seg.Name],
                    PathSegKind.Index    => (current as JsonArray)?[seg.Index],
                    PathSegKind.Key      => (current as JsonObject)?[seg.Name],
                    _ => null,
                };
                if (current == null) return false;
            }

            var last = segments[^1];

            if (isKeyChange)
            {
                // Navigate into the last property to find the dict
                var dictNode = (current as JsonObject)?[last.Name] as JsonObject;
                if (dictNode == null) return false;
                // Find the key that equals old value
                foreach (var kv in dictNode.ToList())
                {
                    if (string.Equals(kv.Key, change.OldValue, StringComparison.OrdinalIgnoreCase))
                    {
                        // Keys nothing loads once the conversion is done go.
                        if (change.ChangeType == "path_key_remove")
                        {
                            dictNode.Remove(kv.Key);
                            return true;
                        }
                        var val = dictNode[kv.Key];
                        // Keys under shared material roots are copied: other customizations still load them.
                        if (change.ChangeType == "path_key_copy")
                        {
                            dictNode[change.NewValue] = val?.DeepClone();
                            return true;
                        }
                        dictNode.Remove(kv.Key);
                        dictNode[change.NewValue] = val;
                        return true;
                    }
                }
                return false;
            }

            switch (last.Kind)
            {
                case PathSegKind.Property:
                {
                    var obj = current as JsonObject;
                    if (obj == null) return false;
                    var val = obj[last.Name];
                    if (val == null) return false;
                    if (change.ChangeType == "numeric_id")
                    {
                        var intVal = val.GetValue<int>();
                        if (intVal == int.Parse(change.OldValue))
                        {
                            obj[last.Name] = int.Parse(change.NewValue);
                            return true;
                        }
                    }
                    else if (change.ChangeType == "numeric_id_string")
                    {
                        // SetId/PrimaryId serialised as a quoted string — preserve that format.
                        var strVal = val.GetValue<string>();
                        if (int.TryParse(strVal, out var intVal) && intVal == int.Parse(change.OldValue))
                        {
                            obj[last.Name] = change.NewValue;
                            return true;
                        }
                    }
                    else
                    {
                        var strVal = val.GetValue<string>();
                        var replaced = ReplaceInString(strVal, change.OldValue, change.NewValue);
                        if (replaced != strVal) { obj[last.Name] = replaced; return true; }
                    }
                    return false;
                }
                case PathSegKind.Key:
                {
                    var obj = current as JsonObject;
                    if (obj == null) return false;
                    var val = obj[last.Name];
                    if (val == null) return false;
                    var strVal = val.GetValue<string>();
                    var replaced = ReplaceInString(strVal, change.OldValue, change.NewValue);
                    if (replaced != strVal) { obj[last.Name] = replaced; return true; }
                    return false;
                }
                default:
                    return false;
            }
        }
        catch { return false; }
    }

    /// <summary>Rewrites the resource paths inside a model or material, rebuilding its string table.</summary>
    private void ApplyBinaryPatch(PlannedBinaryPatch bp, Action<string> log)
    {
        if (bp.Patches.Count == 0) return;

        var bytes = File.ReadAllBytes(bp.FilePath);
        var replacements = bp.Patches.ToDictionary(p => p.OldString, p => p.NewString, StringComparer.Ordinal);
        var rewritten = StructuredPathRewriter.Rewrite(bp.FilePath, bytes, replacements);
        if (rewritten.AsSpan().SequenceEqual(bytes))
            throw new InvalidDataException($"No planned structured paths were found in {bp.FilePath}.");
        File.WriteAllBytes(bp.FilePath, rewritten);
        log($"Rewrote structured binary paths: {Path.GetFileName(bp.FilePath)} ({bp.Patches.Count})");
    }

    private void ApplyMdlChange(PlannedMdlChange change, Action<string> log)
    {
        var input = File.ReadAllBytes(change.FilePath);
        var inputHash = MdlRaceConverter.Hash(input);
        if (!string.Equals(inputHash, change.InputHash, StringComparison.Ordinal))
            throw new InvalidOperationException($"MDL changed after preview: {change.FilePath}");
        var model = MdlFile.Read(input);
        var replacements = change.PathReplacements
            .ToDictionary(p => p.OldString, p => p.NewString, StringComparer.Ordinal);
        if (replacements.Count > 0) model.ReplacePaths(replacements);
        var deformationPlan = change.DeformationPlan
            ?? throw new InvalidOperationException("The previewed MDL deformation plan is missing.");
        var report = MdlRaceConverter.Convert(model, deformationPlan);
        var output = model.Write();
        var outputHash = MdlRaceConverter.Hash(output);
        if (!string.Equals(outputHash, change.OutputHash, StringComparison.Ordinal))
            throw new InvalidDataException($"MDL conversion output changed since preview: {change.FilePath}");
        _ = MdlFile.Read(output);
        File.WriteAllBytes(change.FilePath, output);
        log($"Deformed MDL geometry: {Path.GetFileName(change.FilePath)} " +
            $"({report.VertexCount} vertices, {report.ShapeVertexCount} shape vertices)");
    }

    /// <summary>Recursively copies <paramref name="sourceDir"/> to <paramref name="destDir"/>.</summary>
    private static void CopyDirectory(string sourceDir, string destDir)
    {
        Directory.CreateDirectory(destDir);
        foreach (var file in Directory.EnumerateFiles(sourceDir, "*", SearchOption.AllDirectories))
        {
            var rel      = Path.GetRelativePath(sourceDir, file);
            var destFile = Path.Combine(destDir, rel);
            Directory.CreateDirectory(Path.GetDirectoryName(destFile)!);
            File.Copy(file, destFile, overwrite: false);
        }
    }

    private static string ReplaceInString(string input, string oldVal, string newVal)
        => Regex.Replace(input, Regex.Escape(oldVal), newVal, RegexOptions.IgnoreCase);

    private enum PathSegKind { Property, Index, Key }

    private record PathSeg(PathSegKind Kind, string Name, int Index);

    private static List<PathSeg> ParseJsonPath(string path)
    {
        var segs = new List<PathSeg>();
        int i    = 0;
        while (i < path.Length)
        {
            if (path[i] == '.')
            {
                i++;
                int start = i;
                while (i < path.Length && path[i] != '.' && path[i] != '[') i++;
                if (i > start)
                    segs.Add(new PathSeg(PathSegKind.Property, path[start..i], 0));
            }
            else if (path[i] == '[')
            {
                i++;
                int start = i;
                while (i < path.Length && path[i] != ']') i++;
                var inner = path[start..i];
                i++; // skip ']'
                if (int.TryParse(inner, out int idx))
                    segs.Add(new PathSeg(PathSegKind.Index, string.Empty, idx));
                else
                    segs.Add(new PathSeg(PathSegKind.Key, inner, 0));
            }
            else i++;
        }
        return segs;
    }

    public static string RelativePath(string basePath, string fullPath)
    {
        try { return Path.GetRelativePath(basePath, fullPath).Replace('\\', '/'); }
        catch { return fullPath; }
    }

    // Asked for on every frame the new mod's name is shown; the framework copies the array each call.
    private static readonly char[] InvalidNameChars = Path.GetInvalidFileNameChars();

    /// <summary>
    /// Returns a version of <paramref name="name"/> safe for use as a directory name
    /// by replacing any characters forbidden in Windows file names with underscores.
    /// </summary>
    public static string SanitizeFolderName(string name)
    {
        var sb = new StringBuilder(name.Length);
        foreach (var c in name)
            sb.Append(Array.IndexOf(InvalidNameChars, c) >= 0 ? '_' : c);
        return sb.ToString().Trim(' ', '.').TrimEnd();
    }
}
