using System;
using System.IO;
using System.Linq;
using UniversalModConverter.Core;
using UniversalModConverter.Models;
using UniversalModConverter.Services;

namespace UniversalModConverter.Session;

/// <summary>
/// State of the Merge modpacks window: two modpacks, which one wins on a conflict, and the name
/// of the merged mod. The merge is planned as soon as that is complete, like a conversion
/// preview, and shares the converter's runner so it never writes while a conversion does.
/// </summary>
public sealed class MergeSession(Plugin plugin)
{
    private ConverterSession Main => plugin.Session;
    private BackgroundRunner Runner => Main.Runner;

    public string FirstDirectory { get; private set; } = string.Empty;
    public string SecondDirectory { get; private set; } = string.Empty;

    /// <summary>Which modpack is kept where both change the same thing: 0 the first, 1 the second, null not chosen yet.</summary>
    public int? Winner { get; private set; }

    public string Name { get; private set; } = string.Empty;
    private bool _nameIsDefault = true;

    public ModMergePlan? Plan { get; private set; }
    public string? PlanError { get; private set; }
    public ResultBanner? Result { get; set; }

    private int _version;
    private int _plannedVersion = -1;
    private bool _planning;

    public bool IsBusy => Runner.IsBusy;
    public bool PlanIsCurrent => _plannedVersion == _version && !_planning;

    public string FirstName => ModName(FirstDirectory);
    public string SecondName => ModName(SecondDirectory);

    public string ModName(string directory)
        => directory.Length == 0 ? string.Empty
            : Main.Mods.FirstOrDefault(m => ConverterSession.SamePath(m.Directory, directory))?.Name
              ?? Path.GetFileName(directory.TrimEnd('\\', '/'));

    // ── Inputs ───────────────────────────────────────────────────────────────

    public void SetFirst(string directory)
    {
        directory = directory.Trim().Trim('"');
        if (ConverterSession.SamePath(FirstDirectory, directory)) return;
        FirstDirectory = directory;
        Changed();
        UpdateDefaultName();
    }

    public void SetSecond(string directory)
    {
        directory = directory.Trim().Trim('"');
        if (ConverterSession.SamePath(SecondDirectory, directory)) return;
        SecondDirectory = directory;
        Changed();
        UpdateDefaultName();
    }

    public void Swap()
    {
        (FirstDirectory, SecondDirectory) = (SecondDirectory, FirstDirectory);
        if (Winner is { } winner) Winner = 1 - winner;
        Changed();
        UpdateDefaultName();
    }

    public void SetWinner(int winner)
    {
        if (Winner == winner) return;
        Winner = winner;
        Changed();
    }

    public void SetName(string name)
    {
        Name = name;
        _nameIsDefault = false;
    }

    public void ResetName()
    {
        _nameIsDefault = true;
        UpdateDefaultName();
    }

    private void UpdateDefaultName()
    {
        if (!_nameIsDefault) return;
        Name = FirstDirectory.Length > 0 && SecondDirectory.Length > 0 ? $"{FirstName} + {SecondName}" : string.Empty;
    }

    private void Changed()
    {
        _version++;
        Plan = null;
        PlanError = null;
    }

    /// <summary>Where the merged mod is created: beside the other mods.</summary>
    public string? OutputPath
    {
        get
        {
            if (string.IsNullOrWhiteSpace(Name)) return null;
            var root = Main.PenumbraAvailable ? plugin.PenumbraIpc.GetModDirectory() : null;
            if (string.IsNullOrWhiteSpace(root) && FirstDirectory.Length > 0)
                root = Path.GetDirectoryName(FirstDirectory.TrimEnd('\\', '/'));
            return string.IsNullOrWhiteSpace(root) ? null : Path.Combine(root, ModConverterService.SanitizeFolderName(Name));
        }
    }

    // ── Readiness ────────────────────────────────────────────────────────────

    public string? PlanBlockReason
    {
        get
        {
            if (FirstDirectory.Length == 0 || SecondDirectory.Length == 0) return "Choose the two modpacks to merge.";
            if (ConverterSession.SamePath(FirstDirectory, SecondDirectory)) return "Choose two different modpacks.";
            foreach (var directory in new[] { FirstDirectory, SecondDirectory })
                if (!PenumbraMod.IsModDirectory(directory, out var error)) return error;
            if (Winner == null) return "Choose which modpack wins when both change the same file.";
            return null;
        }
    }

    public string? CreateBlockReason
    {
        get
        {
            if (Runner.IsBusy) return "Wait for the current operation to finish.";
            if (PlanBlockReason is { } reason) return reason;
            if (!PlanIsCurrent) return "Planning the merge…";
            if (PlanError != null) return PlanError;
            if (string.IsNullOrWhiteSpace(Name)) return "Enter a name for the merged mod.";
            if (OutputPath is not { } path) return "Cannot determine where to create the merged mod.";
            if (Directory.Exists(path) || File.Exists(path)) return $"A folder named '{Path.GetFileName(path)}' already exists.";
            return null;
        }
    }

    // ── Planning and creating ────────────────────────────────────────────────

    /// <summary>Called every framework tick: plans the merge once the inputs are complete.</summary>
    public void Tick()
    {
        if (_planning || Runner.IsBusy || _plannedVersion == _version || PlanBlockReason != null) return;

        var version = _version;
        var (baseDir, overlayDir) = Winner == 0 ? (SecondDirectory, FirstDirectory) : (FirstDirectory, SecondDirectory);
        var name = Name;
        _planning = Runner.TryRun("Planning the merge…", () => ModMerger.Plan(baseDir, overlayDir, name), plan =>
        {
            _planning = false;
            if (version != _version) return;
            Plan = plan;
            PlanError = null;
            _plannedVersion = version;
        }, ex =>
        {
            _planning = false;
            if (version != _version) return;
            Plan = null;
            PlanError = ex.Message;
            _plannedVersion = version;
        });
    }

    public void Create()
    {
        if (CreateBlockReason != null || Plan is not { } plan || OutputPath is not { } path) return;

        var name = Name.Trim();
        var description = $"Merged '{FirstName}' and '{SecondName}'";
        var baseDir = Winner == 0 ? SecondDirectory : FirstDirectory;
        plan.Result.Meta["Name"] = name;
        Result = null;
        Main.Log.BeginOperation(isConversion: true);
        Main.Log.Add($"{description} into '{name}', keeping '{plan.OverlayName}' where both change the same thing…");

        void Post(string message) => Runner.Post(() => Main.Log.Add(message));

        Runner.TryRun("Merging…", () => plugin.Converter.PublishMerge(plan, path, Post), output =>
        {
            var task = new ConversionTask
            {
                ModDirectory  = baseDir,
                OutputMode    = ConversionOutputMode.NewMod,
                PublishedPath = output,
                IsApplied     = true,
            };
            var record = plugin.History.Record(task, description, plan.BaseName);
            Changed(); // The folder exists now; creating again needs a new name.

            if (!Main.PenumbraAvailable)
            {
                Result = new ResultBanner(BannerKind.Info, "Merged mod created",
                    $"'{name}' was written. Use 'Rediscover Mods' in Penumbra to load it.", output, record.Id);
                return;
            }

            var folder = Path.GetFileName(output);
            var loaded = plugin.PenumbraIpc.AddMod(folder) && plugin.PenumbraIpc.ReloadMod(folder);
            Main.RefreshMods();
            Main.Log.Add(loaded ? LogLevel.Success : LogLevel.Warning,
                loaded ? $"'{name}' is now available in Penumbra." : $"'{name}' was created but Penumbra did not load it.");
            Result = loaded
                ? new ResultBanner(BannerKind.Success, "Merged mod created",
                    $"'{name}' is now available in Penumbra. The two original modpacks are unchanged; " +
                    "disable them so they do not override the merged mod.", output, record.Id)
                : new ResultBanner(BannerKind.Warning, "Created, but not loaded by Penumbra",
                    $"'{name}' was written but Penumbra did not pick it up. Use 'Rediscover Mods' in Penumbra.",
                    output, record.Id);
        }, ex =>
        {
            Main.Log.Add(LogLevel.Error, $"Merging failed: {ex.Message}");
            Result = new ResultBanner(BannerKind.Error, "Merging failed", ex.Message);
        });
    }
}
