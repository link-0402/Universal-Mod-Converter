using System;
using System.IO;
using System.Linq;
using UniversalModConverter.Core;
using UniversalModConverter.Services;
using UniversalModConverter.Session;
using UniversalModConverter.Windows.Ui;
using Dalamud.Bindings.ImGui;
using Dalamud.Interface;
using Dalamud.Interface.Utility.Raii;
using Dalamud.Interface.Windowing;

namespace UniversalModConverter.Windows;

public sealed class ConfigWindow : Window, IDisposable
{
    private readonly Plugin _plugin;

    private BackupMaintenanceService.Usage? _usage;
    private string? _sweepMessage;
    private bool _measuring;

    public ConfigWindow(Plugin plugin) : base(
        "Universal Mod Converter — Settings###UMCConfig",
        ImGuiWindowFlags.AlwaysAutoResize | ImGuiWindowFlags.NoCollapse)
    {
        _plugin = plugin;
    }

    public void Dispose() { }

    public override void Draw()
    {
        var cfg = _plugin.Configuration;

        Widgets.SectionTitle("Plan");
        var advanced = cfg.ShowAdvancedDetails;
        if (ImGui.Checkbox("Show advanced plan details (fingerprints, bone resolution)", ref advanced))
        {
            cfg.ShowAdvancedDetails = advanced;
            cfg.Save();
        }

        ImGui.Spacing();
        Widgets.SectionTitle("Safety");
        var confirm = cfg.ConfirmInPlace;
        if (ImGui.Checkbox("Ask before converting a mod in place", ref confirm))
        {
            cfg.ConfirmInPlace = confirm;
            cfg.Save();
        }

        ImGui.Spacing();
        Widgets.SectionTitle("Backups");
        DrawBackupDirectory(cfg);
        ImGui.Spacing();
        DrawRetention(cfg);
    }

    private void DrawBackupDirectory(Configuration cfg)
    {
        using (ImRaii.TextWrapPos(ImGui.GetFontSize() * 28f))
            Widgets.Muted("Converting a mod in place keeps the untouched original here, and so does " +
                          "reverting, so a conversion can always be undone. Leave this blank to use the " +
                          "default location.");
        ImGui.Spacing();

        var buttonWidth = ImGui.GetFrameHeight();
        var backupDir   = cfg.BackupDirectory;
        // A fixed width: the window sizes itself to its content, so a field that fills the
        // available width would make it grow a little every frame.
        ImGui.SetNextItemWidth(ImGui.GetFontSize() * 28f - buttonWidth * 2 - ImGui.GetStyle().ItemSpacing.X * 2);
        if (ImGui.InputTextWithHint("##BackupDirectory", DefaultLocationHint(), ref backupDir, 512))
        {
            cfg.BackupDirectory = backupDir;
            cfg.Save();
            Invalidate();
        }

        ImGui.SameLine();
        var folder     = CurrentRoot();
        var openReason = folder != null && Directory.Exists(folder) ? null : "This folder does not exist yet.";
        if (Widgets.IconButton("##OpenBackupDir", FontAwesomeIcon.FolderOpen, "Open this folder", openReason))
            ConverterSession.OpenFolder(folder!);

        ImGui.SameLine();
        var resetReason = string.IsNullOrEmpty(cfg.BackupDirectory) ? "Already using the default." : null;
        if (Widgets.IconButton("##ResetBackupDir", FontAwesomeIcon.Undo, "Reset to the default location", resetReason))
        {
            cfg.BackupDirectory = string.Empty;
            cfg.Save();
            Invalidate();
        }

        if (string.IsNullOrEmpty(cfg.BackupDirectory) && CurrentRoot() is { } resolved)
            Widgets.Muted(resolved);
    }

    private void DrawRetention(Configuration cfg)
    {
        var prune = cfg.PruneBackupsAutomatically;
        if (ImGui.Checkbox("Delete old backups automatically", ref prune))
        {
            cfg.PruneBackupsAutomatically = prune;
            cfg.Save();
        }
        Widgets.Tooltip("Runs when the plugin starts and after each conversion. A backup you could " +
                        "still revert to is never deleted, however old it is.");

        using (ImRaii.Disabled(!prune))
        {
            var width = ImGui.GetFontSize() * 6f;
            var days  = cfg.BackupRetentionDays;
            ImGui.SetNextItemWidth(width);
            if (ImGui.InputInt("Delete backups older than (days)", ref days))
            {
                cfg.BackupRetentionDays = Math.Clamp(days, 1, 3650);
                cfg.Save();
            }

            var count = cfg.BackupRetentionCount;
            ImGui.SetNextItemWidth(width);
            if (ImGui.InputInt("Keep at most", ref count))
            {
                cfg.BackupRetentionCount = Math.Clamp(count, 1, 500);
                cfg.Save();
            }
            Widgets.Tooltip("Both limits apply: a backup goes when it is too old, and also when " +
                            "newer conversions have pushed it past this count.");
        }

        ImGui.Spacing();
        EnsureMeasured();
        Widgets.Muted(_usage is { } usage
            ? usage.Folders == 0
                ? "No backups are stored right now."
                : $"{usage.Folders} backup(s) using {BackupMaintenanceService.Describe(usage.Bytes)}."
            : "Measuring…");

        ImGui.SameLine();
        if (Widgets.IconTextButton(FontAwesomeIcon.Broom, "Clean up now"))
            CleanUpNow();

        if (_sweepMessage is { } message)
        {
            ImGui.SameLine();
            Widgets.Muted(message);
        }
    }

    // ── Backup folder state ──────────────────────────────────────────────────

    private void Invalidate()
    {
        _usage = null;
        _sweepMessage = null;
    }

    private string DefaultLocationHint()
        => ModConverterService.SameVolume(Path.GetTempPath(), PenumbraRoot() ?? Path.GetTempPath())
            ? "Default: the system temp folder"
            : "Default: a hidden .umc-backups folder next to the mods";

    private string? PenumbraRoot()
        => _plugin.PenumbraIpc.IsAvailable ? _plugin.PenumbraIpc.GetModDirectory() : null;

    private string? CurrentRoot()
    {
        if (!string.IsNullOrWhiteSpace(_plugin.Configuration.BackupDirectory))
            return _plugin.Configuration.BackupDirectory;
        return _plugin.BackupMaintenance.Roots(PenumbraRoot()).FirstOrDefault()
               ?? (PenumbraRoot() is { } root
                   ? ModConverterService.BackupRoot(root, null, create: false)
                   : null);
    }

    private void EnsureMeasured()
    {
        if (_usage != null || _measuring) return;
        _measuring = true;
        var root = PenumbraRoot();
        System.Threading.Tasks.Task.Run(() =>
        {
            var measured = _plugin.BackupMaintenance.Measure(root);
            _plugin.Session.Runner.Post(() => { _usage = measured; _measuring = false; });
        });
    }

    private void CleanUpNow()
    {
        var root = PenumbraRoot();
        _sweepMessage = "Cleaning up…";
        System.Threading.Tasks.Task.Run(() =>
        {
            var result = _plugin.BackupMaintenance.Sweep(root);
            _plugin.Session.Runner.Post(() =>
            {
                if (result.PrunedRecords.Count > 0) _plugin.History.MarkBackupsPruned(result.PrunedRecords);
                _sweepMessage = result.Folders == 0
                    ? "Nothing to clean up."
                    : $"Removed {result.Folders} backup(s), freeing {BackupMaintenanceService.Describe(result.Bytes)}.";
                _usage = null;
            });
        });
    }
}
