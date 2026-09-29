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

    /// <summary>
    /// Where backups go, whether that folder exists, and the hint for the folder field. Working
    /// them out touches the disk (every history record's folder, links on the way to the temp
    /// folder), so it happens at most once a second rather than every frame.
    /// </summary>
    private (string? Folder, bool Exists, string Hint, DateTime At)? _location;

    public ConfigWindow(Plugin plugin) : base(
        "Universal Mod Converter — Settings###UMCConfig",
        ImGuiWindowFlags.AlwaysAutoResize | ImGuiWindowFlags.NoCollapse)
    {
        _plugin = plugin;
    }

    public void Dispose() { }

    /// <summary>Backups come and go while the window is closed, so their usage is measured afresh.</summary>
    public override void OnOpen() => Invalidate();

    public override void Draw()
    {
        var cfg = _plugin.Configuration;

        Widgets.SectionTitle("Plan");
        var advanced = cfg.ShowAdvancedDetails;
        if (ImGui.Checkbox("Show advanced plan details", ref advanced))
        {
            cfg.ShowAdvancedDetails = advanced;
            cfg.Save();
        }
        Widgets.Tooltip("The same switch as Advanced details in the Plan tab: every game path, metadata entry and file " +
                        "operation a conversion produces, fingerprints, bone resolution, and the raw codes of the " +
                        "things to check.");

        ImGui.Spacing();
        Widgets.SectionTitle("Safety");
        var confirm = cfg.ConfirmInPlace;
        if (ImGui.Checkbox("Ask before changing an existing mod", ref confirm))
        {
            cfg.ConfirmInPlace = confirm;
            cfg.Save();
        }
        Widgets.Tooltip("Asks before \"Convert in place\" and \"Add to this mod\", retextures added to the mod included. " +
                        "Creating a new mod never asks, since it changes nothing that exists.");

        ImGui.Spacing();
        Widgets.SectionTitle("Backups");
        DrawBackupDirectory(cfg);
        ImGui.Spacing();
        DrawRetention(cfg);
    }

    private void DrawBackupDirectory(Configuration cfg)
    {
        using (ImRaii.TextWrapPos(ImGui.GetFontSize() * 28f))
            Widgets.Muted("Converting a mod in place or adding to it keeps the untouched original here, and " +
                          "so does reverting, so a conversion can always be undone. Leave this blank to use " +
                          "the default location.");
        ImGui.Spacing();

        var buttonWidth = ImGui.GetFrameHeight();
        var backupDir   = cfg.BackupDirectory;
        var location    = Location();
        // A fixed width: the window sizes itself to its content, so a field that fills the
        // available width would make it grow a little every frame.
        ImGui.SetNextItemWidth(ImGui.GetFontSize() * 28f - buttonWidth * 2 - ImGui.GetStyle().ItemSpacing.X * 2);
        if (ImGui.InputTextWithHint("##BackupDirectory", location.Hint, ref backupDir, 512))
        {
            cfg.BackupDirectory = backupDir;
            cfg.Save();
            Invalidate();
        }

        ImGui.SameLine();
        var folder     = location.Folder;
        var openReason = folder != null && location.Exists ? null : "This folder does not exist yet.";
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

        if (string.IsNullOrEmpty(cfg.BackupDirectory) && location.Folder is { } resolved)
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
        Widgets.Tooltip("Runs when the plugin starts and after each conversion or revert that keeps a backup. " +
                        "A backup you could still revert to is never deleted, however old it is. What an " +
                        "interrupted conversion left behind is recovered at startup either way.");

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
        _location = null;
    }

    private (string? Folder, bool Exists, string Hint) Location()
    {
        var now = DateTime.UtcNow;
        if (_location is { } known && now - known.At < TimeSpan.FromSeconds(1)) return (known.Folder, known.Exists, known.Hint);

        var root = PenumbraRoot();
        var hint = ModConverterService.SameVolume(Path.GetTempPath(), root ?? Path.GetTempPath())
            ? "Default: the system temp folder"
            : "Default: a hidden .umc-backups folder next to the mods";
        var folder = !string.IsNullOrWhiteSpace(_plugin.Configuration.BackupDirectory)
            ? _plugin.Configuration.BackupDirectory
            : _plugin.BackupMaintenance.Roots(root).FirstOrDefault()
              ?? (root != null ? ModConverterService.BackupRoot(root, null, create: false) : null);
        _location = (folder, folder != null && Directory.Exists(folder), hint, now);
        return (folder, _location.Value.Exists, hint);
    }

    /// <summary>
    /// Penumbra's mod folder: as the main window last read it, or from Penumbra itself when that
    /// window has not been opened yet. Asked for at most once a second (see <see cref="Location"/>).
    /// </summary>
    private string? PenumbraRoot()
        => _plugin.Session.PenumbraAvailable && _plugin.Session.PenumbraModDirectory is { } known ? known
            : _plugin.PenumbraIpc.IsAvailable ? _plugin.PenumbraIpc.GetModDirectory()
            : null;

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

    /// <summary>Runs the same sweep as the automatic one, so the two never run at once.</summary>
    private void CleanUpNow()
    {
        var started = _plugin.RunBackupMaintenance(manual: true, done: result =>
        {
            _sweepMessage = result == null ? "Cleaning up failed; see the log."
                : result.Folders == 0 ? "Nothing to clean up."
                : $"Removed {result.Folders} backup(s), freeing {BackupMaintenanceService.Describe(result.Bytes)}.";
            _usage = null;
            _location = null;
        });
        _sweepMessage = started ? "Cleaning up…" : "A cleanup is already running.";
    }
}
