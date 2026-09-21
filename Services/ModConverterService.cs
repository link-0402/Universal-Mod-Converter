using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using System.Text.RegularExpressions;
using AdvancedPenumbraModConverter.Core;
using AdvancedPenumbraModConverter.Models;
using Dalamud.Plugin.Services;

namespace AdvancedPenumbraModConverter.Services;

/// <summary>
/// Plans, applies and verifies conversions of a Penumbra mod.
/// Gear (equipment, accessories, facewear) goes through the game-path based
/// <see cref="GearConversionPlanner"/>; hair, face, tail and ear roots go through the
/// <see cref="CustomizationPlanner"/>. Both publish through a staged copy so a failure
/// never leaves a half-converted mod behind.
/// </summary>
public sealed class ModConverterService
{
    private const string BackupFolderName = ".apmc-backups";

    /// <summary>Backups under the system temp folder live here, all Penumbra roots together.</summary>
    internal const string TempBackupFolderName = "AdvancedPenumbraModConverter-backups";

    private readonly IPluginLog           _log;
    private readonly IGameFileProvider    _gameFiles;
    private readonly GameDataService?     _gameData;
    private readonly Configuration?       _configuration;
    private readonly CustomizationPlanner _customizationPlanner;
    private readonly IAnimationRetargeter? _retargeter;
    private readonly IExpressionMerger? _expressions;
    private HumanPbd? _pbd;
    private bool _pbdLoaded;

    public ModConverterService(IPluginLog log, GameDataService? gameData = null, IFramework? framework = null,
        IAnimationRetargeter? retargeter = null, Configuration? configuration = null,
        IExpressionMerger? expressions = null)
    {
        _log           = log;
        _gameData      = gameData;
        _retargeter    = retargeter;
        _expressions   = expressions;
        _configuration = configuration;
        _gameFiles     = (IGameFileProvider?)gameData ?? NoGameFiles.Instance;
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

    /// <summary>Why expressions cannot be attached in this session, or null.</summary>
    public string? ExpressionUnavailableReason
        => _expressions == null ? "Attaching expressions is not available." : _expressions.UnavailableReason;

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

            if (CustomizationKinds.IsCustomization(task.Kind))
            {
                _customizationPlanner.Plan(task);
                ConversionPlanValidator.Finalize(task);
                return;
            }

            var request = BuildGearRequest(task);
            var plan = new GearConversionPlanner(_gameFiles).Plan(task.ModDirectory, request);
            task.GearPlan = plan;
            task.SourceRoot = request.Source.Root;
            task.Diagnostics.AddRange(plan.Diagnostics);
            if (GearSlots.CrossSlotNote(request.Source.Slot, request.Target.Slot) is { } note)
                task.Diagnostics.Add(new PlanDiagnostic("cross_slot_geometry",
                    note + " Untick anything else in the Mesh groups tab.", false));
            task.OutputModels.AddRange(GearOutputModels.Collect(plan, task.ModDirectory));
            task.SourceFingerprint = GearSourceFingerprint(task) ?? string.Empty;
            task.PlanFingerprint = plan.Fingerprint();
            task.IsPlanned = true;
            task.ErrorMessage = task.HasBlockers
                ? string.Join(" ", task.Diagnostics.Where(d => d.IsBlocker).Select(d => d.Message))
                : null;
            _log.Information("[APMC] Planned {0} -> {1} ({2}): {3} path(s), {4} file operation(s), {5} diagnostic(s).",
                request.Source, request.Target, request.Mode, plan.GamePathMap.Count, plan.Files.Count, plan.Diagnostics.Count);
        }
        catch (Exception ex)
        {
            task.ErrorMessage = ex.Message;
            task.Diagnostics.Add(new PlanDiagnostic("planning_failed", ex.Message, true));
            _log.Error(ex, "[APMC] PlanConversion failed");
        }
    }

    /// <summary>
    /// Plans every enabled conversion of a run into one mod. Each reads the mod as it is on
    /// disk; the merger rejects any that would undo another, and a rejected one blocks the run
    /// rather than quietly converting the rest.
    /// </summary>
    private void PlanQueue(ConversionTask task)
    {
        var context = new ModPlanContext(task.ModDirectory, task.OutputMode, shared: true);
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
                    $"{entry.Description}: hair, face, tail and Viera-ear conversions cannot be converted " +
                    "together with others yet. Convert this one on its own.", true));
                entry.Rejected = true;
                continue;
            }

            entry.Task.OutputMode = task.OutputMode;
            entry.Task.ModDirectory = task.ModDirectory;
            var merged = entry.Kind == AssetKind.Animation
                ? merger.Add(entry.Description, AnimationRoots(entry),
                    ctx => new AnimationConversionPlanner(_gameFiles, ParentRace, _retargeter, _expressions)
                        .Plan(ctx, ForMode(entry.Task.AnimationRequest
                                  ?? throw new InvalidOperationException("Choose what to do with the animation."),
                              task.OutputMode)))
                : merger.Add(entry.Description, GearRoots(entry),
                    ctx => new GearConversionPlanner(_gameFiles).Plan(ctx, BuildGearRequest(entry.Task)));
            entry.Plan = merged.Plan;
            entry.Rejected = merged.Rejected;
            entry.Diagnostics.AddRange(merged.Diagnostics);
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

        task.SourceFingerprint = MergedSourceFingerprint(task) ?? string.Empty;
        task.PlanFingerprint = plan.Fingerprint();
        task.IsPlanned = true;
        task.ErrorMessage = task.HasBlockers
            ? string.Join(" ", task.Diagnostics.Where(d => d.IsBlocker).Select(d => d.Message))
            : null;
        _log.Information("[APMC] Planned a run of {0} conversion(s) ({1}): {2} file operation(s), {3} diagnostic(s).",
            plan.Entries.Count(e => !e.Rejected), task.OutputMode, plan.Files.Count, task.Diagnostics.Count);
    }

    /// <summary>
    /// What a queued gear conversion claims: the item it reads and the one it writes. A root
    /// such as e0728 holds every slot of a set, so the slot is part of the claim; body and
    /// hands of the same set are different items and may be converted together.
    /// </summary>
    private static IEnumerable<string> GearRoots(QueuedConversion entry)
    {
        var request = BuildGearRequest(entry.Task);
        return [$"{request.Source.Root} ({request.Source.Slot})", $"{request.Target.Root} ({request.Target.Slot})"];
    }

    /// <summary>
    /// The request for the output mode chosen at preview time. A plan entry is made before the
    /// mode may change, so the mode is applied here; adding to the mod always keeps the source.
    /// </summary>
    private static AnimationConversionRequest ForMode(AnimationConversionRequest request, ConversionOutputMode mode)
        => request with { Mode = mode, KeepOriginal = request.KeepOriginal || mode.KeepsSource() };

    /// <summary>What a queued animation conversion claims: the animations it reads and writes.</summary>
    private static IEnumerable<string> AnimationRoots(QueuedConversion entry)
    {
        if (entry.Task.AnimationRequest is not { } request) return [];
        return request.SourceLocations
            .Concat(request.Variants.SelectMany(v => v.Locations.Values))
            .Distinct(StringComparer.OrdinalIgnoreCase);
    }

    private static string? MergedSourceFingerprint(ConversionTask task)
    {
        try
        {
            var inputs = task.MergedPlan!.InputFiles
                .Concat(task.Entries.Where(e => !e.Rejected).SelectMany(PlanInputs))
                .Concat(PenumbraMod.DefinitionFiles(task.ModDirectory).Where(File.Exists));
            return ModFingerprint.Compute(task.ModDirectory, inputs);
        }
        catch (Exception)
        {
            return null; // A planned input no longer exists.
        }
    }

    private static IEnumerable<string> PlanInputs(QueuedConversion entry) => entry.Plan switch
    {
        GearConversionPlan gear           => gear.InputFiles,
        AnimationConversionPlan animation => animation.InputFiles,
        _                                 => [],
    };

    private void PlanAnimation(ConversionTask task)
    {
        var request = ForMode(task.AnimationRequest ?? throw new InvalidOperationException("Choose what to do with the animation."),
            task.OutputMode);
        var plan = new AnimationConversionPlanner(_gameFiles, ParentRace, _retargeter, _expressions)
            .Plan(task.ModDirectory, request);
        task.AnimationPlan = plan;
        task.Diagnostics.AddRange(plan.Diagnostics);
        task.SourceFingerprint = AnimationSourceFingerprint(task) ?? string.Empty;
        task.PlanFingerprint = plan.Fingerprint();
        task.IsPlanned = true;
        task.ErrorMessage = task.HasBlockers
            ? string.Join(" ", task.Diagnostics.Where(d => d.IsBlocker).Select(d => d.Message))
            : null;
        _log.Information("[APMC] Planned animation {0} ({1}): {2} file operation(s), {3} diagnostic(s).",
            request.Description, request.Mode, plan.Files.Count, plan.Diagnostics.Count);
    }

    /// <summary>The skeleton parent of a race in the game's race tree (human.pbd).</summary>
    private ushort? ParentRace(ushort race)
    {
        if (!_pbdLoaded)
        {
            _pbdLoaded = true;
            try
            {
                if (_gameData?.GetHumanPbdBytes() is { } bytes) _pbd = new HumanPbd(bytes);
            }
            catch (Exception ex)
            {
                _log.Warning(ex, "[APMC] The race tree could not be read; animations will not inherit between races.");
            }
        }
        return _pbd?.GetParentRace(race);
    }

    private static string? AnimationSourceFingerprint(ConversionTask task)
    {
        try
        {
            return ModFingerprint.Compute(task.ModDirectory,
                task.AnimationPlan!.InputFiles.Concat(PenumbraMod.DefinitionFiles(task.ModDirectory).Where(File.Exists)));
        }
        catch (Exception)
        {
            return null; // A planned input no longer exists.
        }
    }

    public static GearConversionRequest BuildGearRequest(ConversionTask task)
    {
        if (!ushort.TryParse(task.OldIdPadded, out var sourceId) || !ushort.TryParse(task.NewIdPadded, out var targetId))
            throw new InvalidDataException("Source and target model IDs must be numbers between 0 and 65535.");
        var source = new GearItem(SlotInfo.ToGearSlot(task.Slot), sourceId, (ushort)Math.Max(0, task.SourceVariant));
        var target = new GearItem(SlotInfo.ToGearSlot(task.TargetSlot ?? task.Slot), targetId,
            (ushort)Math.Max(0, task.TargetVariant));
        return new GearConversionRequest(source, target, task.OutputMode);
    }

    private static string? GearSourceFingerprint(ConversionTask task)
    {
        try
        {
            return ModFingerprint.Compute(task.ModDirectory, task.GearPlan!.InputFiles);
        }
        catch (Exception)
        {
            return null; // A planned input no longer exists.
        }
    }

    /// <param name="newMod">
    /// Which of the two write paths is about to run. The plan must agree both on the exact mode
    /// and on which path it was built for, so a plan made for one cannot be written by the other.
    /// </param>
    private static void EnsurePlanIsCurrent(ConversionTask task, bool newMod)
    {
        if (!task.IsPlanned) throw new InvalidOperationException("Preview the conversion before applying it.");
        if (task.HasBlockers) throw new InvalidOperationException(task.ErrorMessage ?? "The conversion plan has blockers.");
        var planned = task.MergedPlan?.Mode ?? task.GearPlan?.Request.Mode
                      ?? task.AnimationPlan?.Request.Mode ?? task.OutputMode;
        if (planned != task.OutputMode || planned.IsNewMod() != newMod)
            throw new InvalidOperationException("The output mode changed after the preview. Wait for the preview to update, then try again.");
        var current = task.MergedPlan != null ? MergedSourceFingerprint(task)
            : task.GearPlan != null ? GearSourceFingerprint(task)
            : task.AnimationPlan != null ? AnimationSourceFingerprint(task)
            : ConversionPlanValidator.RecomputeSourceFingerprint(task);
        if (!string.Equals(current, task.SourceFingerprint, StringComparison.Ordinal))
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

        void Log(string msg) { onLog?.Invoke(msg); _log.Information("[APMC] {0}", msg); }

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
            stageDir = Path.Combine(parent, $".{name}.apmc-stage-{id}");
            backupDir = Path.Combine(BackupRoot(parent, _configuration?.BackupDirectory), $"{name}-{DateTime.UtcNow:yyyyMMdd-HHmmss}-{id[..8]}");
            task.JournalPath = Path.Combine(parent, $".{name}.apmc-recovery-{id}.json");

            Log($"Staging complete mod shadow: {stageDir}");
            CopyDirectory(sourceDir, stageDir);
            if (task.MergedPlan is { } merged)
            {
                GearConversionExecutor.ApplyInPlace(merged, stageDir, Log);
                GearOutputModels.ApplyRemovals(stageDir, task.MeshRemovals, Log);
            }
            else if (task.GearPlan is { } plan)
            {
                GearConversionExecutor.ApplyInPlace(plan, stageDir, Log);
                GearOutputModels.ApplyRemovals(stageDir, task.MeshRemovals, Log);
            }
            else if (task.AnimationPlan is { } animation)
                GearConversionExecutor.ApplyInPlace(animation, stageDir, Log);
            else
            {
                var stagedTask = RemapTask(task, stageDir);
                ApplyConversionCore(stagedTask, Log);
            }
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
            if (sourceMoved && backupDir != null && Directory.Exists(backupDir) && !Directory.Exists(task.ModDirectory))
            {
                Directory.Move(backupDir, task.ModDirectory);
                task.ResultStatus = ConversionResultStatus.RolledBack;
            }
            else
                task.ResultStatus = ConversionResultStatus.Failed;
            if (stageDir != null && Directory.Exists(stageDir))
                try { Directory.Delete(stageDir, true); } catch { }
            task.ErrorMessage = ex.Message;
            _log.Error(ex, "[APMC] ApplyConversion failed");
            onLog?.Invoke($"Error: {ex.Message}");
        }
    }

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

    /// <summary>True when both paths sit on the same volume, so a directory move is atomic.</summary>
    internal static bool SameVolume(string a, string b)
    {
        try
        {
            return string.Equals(Path.GetPathRoot(Path.GetFullPath(a)), Path.GetPathRoot(Path.GetFullPath(b)),
                StringComparison.OrdinalIgnoreCase);
        }
        catch (Exception)
        {
            return false;
        }
    }

    /// <summary>Applies a customization plan (hair, face, tail, ear) to a staged directory.</summary>
    private void ApplyConversionCore(ConversionTask task, Action<string> log)
    {
        foreach (var mdl in task.PlannedMdlChanges.Where(p => p.Selected)) ApplyMdlChange(mdl, log);
        foreach (var bp in task.PlannedBinaryPatches.Where(p => p.Selected)) ApplyBinaryPatch(bp, log);
        foreach (var jc in task.PlannedJsonChanges.Where(j => j.Selected)) ApplyJsonChange(jc, log);
        foreach (var rename in task.PlannedRenames.Where(r => r.Selected).OrderByDescending(r => r.OldPath.Length))
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
            var failed = current + $".apmc-failed-{Guid.NewGuid():N}";
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

    public string? ApplyConversionAsNewMod(
        ConversionTask task,
        string         newModDir,
        string         modDisplayName,
        Action<string>? onLog = null)
        => CreateNewModFromAssetChain(task, newModDir, modDisplayName, onLog);

    /// <summary>
    /// Creates a new mod next to the source mod. Gear conversions contain only the converted
    /// item (with the source's option structure); customization conversions copy the mod.
    /// The source mod is never modified.
    /// </summary>
    public string? CreateNewModFromAssetChain(
        ConversionTask task, string newModDir, string modDisplayName, Action<string>? onLog = null)
    {
        task.IsApplied = false;
        task.ResultStatus = ConversionResultStatus.NotStarted;
        task.ErrorMessage = null;
        string? stageDir = null;
        void Log(string msg) { onLog?.Invoke(msg); _log.Information("[APMC] {0}", msg); }
        try
        {
            EnsurePlanIsCurrent(task, newMod: true);

            var finalDir = Path.GetFullPath(newModDir).TrimEnd('\\', '/');
            if (Directory.Exists(finalDir) || File.Exists(finalDir))
                throw new IOException($"The output path already exists: {finalDir}");
            var parent = Path.GetDirectoryName(finalDir) ?? throw new InvalidOperationException("The output path has no parent.");
            stageDir = Path.Combine(parent, $".{Path.GetFileName(finalDir)}.apmc-stage-{Guid.NewGuid():N}");
            if (task.MergedPlan is { } merged)
            {
                GearConversionExecutor.WriteNewMod(merged, task.ModDirectory, stageDir, modDisplayName, Log);
                GearOutputModels.ApplyRemovals(stageDir, task.MeshRemovals, Log);
            }
            else if (task.GearPlan is { } plan)
            {
                GearConversionExecutor.WriteNewMod(plan, task.ModDirectory, stageDir, modDisplayName, Log);
                GearOutputModels.ApplyRemovals(stageDir, task.MeshRemovals, Log);
            }
            else if (task.AnimationPlan is { } animation)
                GearConversionExecutor.WriteNewMod(animation, task.ModDirectory, stageDir, modDisplayName, Log);
            else
            {
                CopyDirectory(task.ModDirectory, stageDir);
                var stagedTask = RemapTask(task, stageDir);
                ApplyConversionCore(stagedTask, Log);
                UpdateMetaJsonName(stageDir, modDisplayName, Log);
            }

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
            _log.Error(ex, "[APMC] Atomic new-mod publication failed");
            return null;
        }
    }

    /// <summary>
    /// Writes a merged modpack to <paramref name="newModDir"/>. Like any new mod it is staged
    /// beside its final place and moved there whole, so a failure never leaves half a mod.
    /// </summary>
    public string PublishMerge(ModMergePlan plan, string newModDir, Action<string>? onLog = null)
    {
        void Log(string msg) { onLog?.Invoke(msg); _log.Information("[APMC] {0}", msg); }

        var finalDir = Path.GetFullPath(newModDir).TrimEnd('\\', '/');
        if (Directory.Exists(finalDir) || File.Exists(finalDir))
            throw new IOException($"The output path already exists: {finalDir}");
        var parent = Path.GetDirectoryName(finalDir) ?? throw new InvalidOperationException("The output path has no parent.");
        var stageDir = Path.Combine(parent, $".{Path.GetFileName(finalDir)}.apmc-stage-{Guid.NewGuid():N}");
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
        var oldBase = original.ModDirectory.TrimEnd('\\', '/');
        var newBase = newBaseDir.TrimEnd('\\', '/');

        string Remap(string p) =>
            p.StartsWith(oldBase, StringComparison.OrdinalIgnoreCase)
                ? newBase + p[oldBase.Length..]
                : p;

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
            IgnoreSlot    = original.IgnoreSlot,
            SourceVariant = original.SourceVariant,
            TargetVariant = original.TargetVariant,
            SourceGenderRace = original.SourceGenderRace,
            TargetGenderRace = original.TargetGenderRace,
            SourceRoot = original.SourceRoot,
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
                IsDir    = r.IsDir,
                Selected = r.Selected,
            });

        foreach (var jc in original.PlannedJsonChanges)
        {
            var rc = new PlannedJsonChange { FilePath = Remap(jc.FilePath), Selected = jc.Selected };
            foreach (var c in jc.Changes)
                rc.Changes.Add(new JsonFieldChange
                {
                    JsonPath   = c.JsonPath,
                    OldValue   = c.OldValue,
                    NewValue   = c.NewValue,
                    ChangeType = c.ChangeType,
                    Selected   = c.Selected,
                });
            remapped.PlannedJsonChanges.Add(rc);
        }

        foreach (var bp in original.PlannedBinaryPatches)
        {
            var rb = new PlannedBinaryPatch { FilePath = Remap(bp.FilePath), Selected = bp.Selected, IsStructured = bp.IsStructured };
            foreach (var p in bp.Patches)
                rb.Patches.Add(new BinaryStringPatch
                {
                    OldString = p.OldString,
                    NewString = p.NewString,
                    Selected  = p.Selected,
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
                GeometryConverted = mdl.GeometryConverted,
                DeformationPlan = mdl.DeformationPlan,
                Selected = mdl.Selected,
            };
            remappedMdl.BoneResolutions.AddRange(mdl.BoneResolutions);
            foreach (var patch in mdl.PathReplacements)
                remappedMdl.PathReplacements.Add(new BinaryStringPatch
                {
                    OldString = patch.OldString,
                    NewString = patch.NewString,
                    Selected = patch.Selected,
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
                foreach (var hit in VerifyEntry(entry, modDirectory))
                {
                    // Which of the run's conversions a problem belongs to is the first thing to know.
                    hit.Detail = $"{entry.Description}: {hit.Detail}";
                    hits.Add(hit);
                }

                return hits;
            }

            if (task.GearPlan is { } plan)
            {
                // Retained source paths are expected when editing the mod (shared resources, and
                // the whole point of adding to it) but not in a new mod.
                var source = plan.Request.Mode.IsNewMod() ? plan.Request.Source : (GearItem?)null;
                foreach (var issue in GearConversionVerifier.Verify(modDirectory, plan.Request.Target, source, _gameFiles))
                    hits.Add(new LeftoverHit
                    {
                        FilePath = modDirectory,
                        HitType  = issue.IsError ? "missing" : "leftover",
                        Detail   = issue.Message,
                    });
                return hits;
            }

            if (task.AnimationPlan is { } animation)
            {
                foreach (var issue in AnimationConversionVerifier.Verify(modDirectory, animation))
                    hits.Add(new LeftoverHit
                    {
                        FilePath = modDirectory,
                        HitType  = issue.IsError ? "missing" : "note",
                        Detail   = issue.Message,
                    });
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
            _log.Error(ex, "[APMC] VerifyConversion failed");
            hits.Add(new LeftoverHit { FilePath = modDirectory, HitType = "error", Detail = $"Verification failed: {ex.Message}" });
        }

        return hits;
    }

    /// <summary>Verifies one conversion of a run with the verifier its kind already has.</summary>
    private IEnumerable<LeftoverHit> VerifyEntry(QueuedConversion entry, string modDirectory)
    {
        switch (entry.Plan)
        {
            case GearConversionPlan gear:
                // Retained source paths are expected when editing the mod (shared resources, and
                // the whole point of adding to it) but not in a new mod.
                var source = gear.Request.Mode.IsNewMod() ? gear.Request.Source : (GearItem?)null;
                return GearConversionVerifier.Verify(modDirectory, gear.Request.Target, source, _gameFiles)
                    .Select(issue => new LeftoverHit
                    {
                        FilePath = modDirectory,
                        HitType  = issue.IsError ? "missing" : "leftover",
                        Detail   = issue.Message,
                    });
            case AnimationConversionPlan animation:
                return AnimationConversionVerifier.Verify(modDirectory, animation)
                    .Select(issue => new LeftoverHit
                    {
                        FilePath = modDirectory,
                        HitType  = issue.IsError ? "missing" : "note",
                        Detail   = issue.Message,
                    });
            default:
                return [];
        }
    }

    private static List<LeftoverHit> VerifyCustomizationConversion(
        ConversionTask task, List<LeftoverHit> hits)
    {
        if (task.SourceGenderRace is not { } race ||
            !ushort.TryParse(task.OldIdPadded, out var modelId)) return hits;
        var source = new CustomizationPathEndpoint(task.Kind, race, modelId);
        var flagged = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

        foreach (var entry in EnumerateAll(task.ModDirectory))
        {
            if (!CustomizationPaths.Contains(entry.FullName, source)) continue;
            flagged.Add(entry.FullName);
            hits.Add(new LeftoverHit
            {
                FilePath = entry.FullName,
                HitType = "filename",
                Detail = $"Path still contains the old customization root: {RelativePath(task.ModDirectory, entry.FullName)}",
            });
        }

        foreach (var jsonFile in PenumbraMod.DefinitionFiles(task.ModDirectory).Where(File.Exists))
        {
            if (!CustomizationPaths.Contains(File.ReadAllText(jsonFile), source) || !flagged.Add(jsonFile)) continue;
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
                    .Any(path => CustomizationPaths.Contains(path, source)) || !flagged.Add(binaryFile)) continue;
            hits.Add(new LeftoverHit
            {
                FilePath = binaryFile,
                HitType = "binary",
                Detail = $"Binary resource still contains the old customization root: {RelativePath(task.ModDirectory, binaryFile)}",
            });
        }

        return hits;
    }

    // ─────────────────────────────────────────────────────────────────────────
    // Customization apply helpers
    // ─────────────────────────────────────────────────────────────────────────

    private void PruneEmptyCustomizationDirs(ConversionTask task, Action<string> log)
    {
        if (task.SourceGenderRace is not { } race ||
            !ushort.TryParse(task.OldIdPadded, out var modelId)) return;
        var source = new CustomizationPathEndpoint(task.Kind, race, modelId);
        var candidates = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

        foreach (var rename in task.PlannedRenames.Where(rename => rename.Selected))
        {
            var directory = Path.GetDirectoryName(rename.OldPath);
            while (directory != null && CustomizationPaths.Contains(directory, source))
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
                _log.Warning(ex, "[APMC] Could not remove empty customization dir {0}", directory);
            }
        }
    }

    private void ApplyRename(PlannedRename rename, Action<string> log)
    {
        if (!rename.IsDir)
        {
            if (!File.Exists(rename.OldPath))
                throw new FileNotFoundException("Planned rename source is missing.", rename.OldPath);
            if (File.Exists(rename.NewPath))
                throw new IOException($"Planned rename destination exists: {rename.NewPath}");
            // Ensure the destination directory exists — mirrors Python's
            // os.makedirs(os.path.dirname(new_path), exist_ok=True) before shutil.move.
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
        else
        {
            if (!Directory.Exists(rename.OldPath))
                throw new DirectoryNotFoundException($"Planned rename directory is missing: {rename.OldPath}");
            if (Directory.Exists(rename.NewPath))
                throw new IOException($"Planned rename directory destination exists: {rename.NewPath}");
            Directory.Move(rename.OldPath, rename.NewPath);
            log($"Renamed dir: {Path.GetFileName(rename.OldPath)} → {Path.GetFileName(rename.NewPath)}");
        }
    }

    private void ApplyJsonChange(PlannedJsonChange jc, Action<string> log)
    {
        var selectedChanges = jc.Changes.Where(c => c.Selected).ToList();
        if (selectedChanges.Count == 0) return;

        var raw  = File.ReadAllText(jc.FilePath);
        var node = JsonNode.Parse(raw, new JsonNodeOptions { PropertyNameCaseInsensitive = false },
                       new JsonDocumentOptions { AllowTrailingCommas = true, CommentHandling = JsonCommentHandling.Skip })
                   ?? throw new InvalidDataException($"JSON document is empty: {jc.FilePath}");

        int applied = 0;
        // Key renames and copies go last so value edits can still find their original key.
        foreach (var change in selectedChanges.OrderBy(c => c.ChangeType is "path_key" or "path_key_copy" ? 1 : 0))
            if (ApplyJsonChangeAtPath(node, change)) applied++;

        if (applied != selectedChanges.Count)
            throw new InvalidDataException($"Only {applied} of {selectedChanges.Count} planned JSON changes applied in {jc.FilePath}.");
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

    private void ApplyBinaryPatch(PlannedBinaryPatch bp, Action<string> log)
    {
        var selectedPatches = bp.Patches.Where(p => p.Selected).ToList();
        if (selectedPatches.Count == 0) return;

        var bytes = File.ReadAllBytes(bp.FilePath);
        if (bp.IsStructured)
        {
            var replacements = selectedPatches.ToDictionary(p => p.OldString, p => p.NewString, StringComparer.Ordinal);
            var rewritten = StructuredPathRewriter.Rewrite(bp.FilePath, bytes, replacements);
            if (rewritten.AsSpan().SequenceEqual(bytes))
                throw new InvalidDataException($"No planned structured paths were found in {bp.FilePath}.");
            File.WriteAllBytes(bp.FilePath, rewritten);
            log($"Rewrote structured binary paths: {Path.GetFileName(bp.FilePath)} ({selectedPatches.Count})");
            return;
        }

        int count = 0;
        foreach (var patch in selectedPatches)
        {
            var oldBytes = Encoding.Latin1.GetBytes(patch.OldString);
            var newBytes = Encoding.Latin1.GetBytes(patch.NewString);
            if (oldBytes.Length != newBytes.Length)
                throw new InvalidDataException($"Unsafe length-changing binary patch: '{patch.OldString}' -> '{patch.NewString}'.");
            var replaced = ReplaceBytesInArray(ref bytes, oldBytes, newBytes);
            if (replaced == 0) throw new InvalidDataException($"Planned binary string was not found: {patch.OldString}");
            count += replaced;
        }
        File.WriteAllBytes(bp.FilePath, bytes);
        log($"Patched binary: {Path.GetFileName(bp.FilePath)} ({count} replacement(s))");
    }

    private void ApplyMdlChange(PlannedMdlChange change, Action<string> log)
    {
        var input = File.ReadAllBytes(change.FilePath);
        var inputHash = MdlRaceConverter.Hash(input);
        if (!string.Equals(inputHash, change.InputHash, StringComparison.Ordinal))
            throw new InvalidOperationException($"MDL changed after preview: {change.FilePath}");
        var model = MdlFile.Read(input);
        var replacements = change.PathReplacements.Where(p => p.Selected)
            .ToDictionary(p => p.OldString, p => p.NewString, StringComparer.Ordinal);
        if (replacements.Count > 0) model.ReplacePaths(replacements);
        MdlConversionReport? report = null;
        if (change.GeometryConverted)
        {
            var deformationPlan = change.DeformationPlan
                ?? throw new InvalidOperationException("The previewed MDL deformation plan is missing.");
            report = MdlRaceConverter.Convert(model, deformationPlan);
        }
        var output = model.Write();
        var outputHash = MdlRaceConverter.Hash(output);
        if (!string.Equals(outputHash, change.OutputHash, StringComparison.Ordinal))
            throw new InvalidDataException($"MDL conversion output changed since preview: {change.FilePath}");
        _ = MdlFile.Read(output);
        File.WriteAllBytes(change.FilePath, output);
        if (report != null)
            log($"Deformed MDL geometry: {Path.GetFileName(change.FilePath)} " +
                $"({report.VertexCount} vertices, {report.ShapeVertexCount} shape vertices)");
        else
            log($"Rebuilt MDL string table: {Path.GetFileName(change.FilePath)} " +
                $"({replacements.Count} path replacement(s))");
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

    private static int ReplaceBytesInArray(ref byte[] data, byte[] oldBytes, byte[] newBytes)
    {
        int count = 0;
        int idx   = 0;
        while (true)
        {
            int pos = IndexOf(data, oldBytes, idx);
            if (pos < 0) break;
            Buffer.BlockCopy(newBytes, 0, data, pos, Math.Min(newBytes.Length, data.Length - pos));
            idx = pos + newBytes.Length;
            count++;
        }
        return count;
    }

    private static int IndexOf(byte[] data, byte[] pattern, int start = 0)
    {
        for (int i = start; i <= data.Length - pattern.Length; i++)
        {
            bool match = true;
            for (int j = 0; j < pattern.Length; j++)
            {
                if (data[i + j] != pattern[j]) { match = false; break; }
            }
            if (match) return i;
        }
        return -1;
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

    private record FsEntry(string FullName, bool IsDirectory);

    private static IEnumerable<FsEntry> EnumerateAll(string root)
    {
        // Yield files first, then dirs (deepest first handled by caller ordering)
        var files = Directory.EnumerateFiles(root, "*", SearchOption.AllDirectories);
        var dirs  = Directory.EnumerateDirectories(root, "*", SearchOption.AllDirectories);

        foreach (var f in files) yield return new FsEntry(f, false);
        foreach (var d in dirs)  yield return new FsEntry(d, true);
    }

    public static string RelativePath(string basePath, string fullPath)
    {
        try { return Path.GetRelativePath(basePath, fullPath).Replace('\\', '/'); }
        catch { return fullPath; }
    }

    /// <summary>
    /// Returns a version of <paramref name="name"/> safe for use as a directory name
    /// by replacing any characters forbidden in Windows file names with underscores.
    /// </summary>
    public static string SanitizeFolderName(string name)
    {
        var invalid = Path.GetInvalidFileNameChars();
        var sb = new StringBuilder(name.Length);
        foreach (var c in name)
            sb.Append(Array.IndexOf(invalid, c) >= 0 ? '_' : c);
        return sb.ToString().Trim(' ', '.').TrimEnd();
    }
}
