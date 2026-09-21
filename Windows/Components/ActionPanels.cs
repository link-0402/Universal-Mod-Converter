using System;
using System.IO;
using System.Linq;
using System.Numerics;
using UniversalModConverter.Core;
using UniversalModConverter.Session;
using UniversalModConverter.Windows.Ui;
using Dalamud.Bindings.ImGui;
using Dalamud.Interface;
using Dalamud.Interface.Utility.Raii;

namespace UniversalModConverter.Windows.Components;

/// <summary>Output options, readiness, Preview/Apply buttons and the result banner.</summary>
internal sealed class ActionPanels(ConverterSession session, Configuration config, ConfirmDialog confirm)
{
    private string _newModName = string.Empty;

    // ─────────────────────────────────────────────────────────────────────────
    // Output
    // ─────────────────────────────────────────────────────────────────────────

    public void DrawOutput()
    {
        Widgets.SectionTitle("Output", FontAwesomeIcon.FileExport);

        var mode = session.OutputMode;
        if (ImGui.RadioButton("Create a new mod", mode.IsNewMod()))
            session.SetOutputMode(ConversionOutputMode.NewMod);
        Widgets.Tooltip("Safest: the source mod is never modified.");

        var additiveBlock = session.AddToModBlockReason;
        using (ImRaii.Disabled(additiveBlock != null))
        {
            if (ImGui.RadioButton("Add to this mod", mode == ConversionOutputMode.AddToMod))
                session.SetOutputMode(ConversionOutputMode.AddToMod);
        }
        ImGui.SameLine();
        Widgets.Badge("Keeps the original", Theme.Info);
        if (additiveBlock != null) Widgets.Tooltip(additiveBlock);

        if (ImGui.RadioButton("Convert in place", mode == ConversionOutputMode.InPlace))
            session.SetOutputMode(ConversionOutputMode.InPlace);
        ImGui.SameLine();
        Widgets.Badge("Advanced", Theme.Warning);

        if (mode == ConversionOutputMode.AddToMod)
        {
            Widgets.MutedWrapped("The original item keeps working. The converted paths are added to the same " +
                                 "options, so the toggles this mod already has control both. The mod as it is " +
                                 "now is kept as a backup.");
            return;
        }

        if (!mode.IsNewMod())
        {
            Widgets.MutedWrapped("Edits this mod directly. The original is kept as a backup, so the " +
                                 "conversion can be reverted from the result or the History tab until " +
                                 "that backup expires.");
            return;
        }

        if (_newModName != session.NewModName) _newModName = session.NewModName;
        ImGui.Spacing();
        ImGui.TextUnformatted("Name of the new mod");
        ImGui.SameLine();
        Widgets.Muted("(how it appears in Penumbra)");
        var buttonWidth = ImGui.GetFrameHeight();
        ImGui.SetNextItemWidth(ImGui.GetContentRegionAvail().X - buttonWidth - ImGui.GetStyle().ItemSpacing.X);
        if (ImGui.InputTextWithHint("##NewModName", "Name of the new mod", ref _newModName, 256))
            session.SetNewModName(_newModName);
        ImGui.SameLine();
        if (Widgets.IconButton("##ResetName", FontAwesomeIcon.Undo, "Use the suggested name"))
            session.ResetNewModName();

        if (session.NewModPath is { } path)
        {
            // The folder existing is a problem only until we are the ones who created it.
            var published = session.Task.IsApplied &&
                            string.Equals(session.Task.PublishedPath, path, StringComparison.OrdinalIgnoreCase);
            var conflict = !published && (Directory.Exists(path) || File.Exists(path));
            var color = conflict ? Theme.Danger : published ? Theme.Success : Theme.Muted;
            Widgets.Icon(conflict ? FontAwesomeIcon.ExclamationCircle
                : published ? FontAwesomeIcon.CheckCircle : FontAwesomeIcon.FolderPlus, color);
            ImGui.SameLine();
            var label = conflict ? "Already exists:" : published ? "Created at:" : "Will be created at:";
            ImGui.TextColored(color, label);
            ImGui.SameLine();
            Widgets.PathText(path, color);
        }
    }

    // ─────────────────────────────────────────────────────────────────────────
    // Readiness and actions
    // ─────────────────────────────────────────────────────────────────────────

    public void DrawActions()
    {
        ImGui.Spacing();
        DrawReadiness();
        ImGui.Spacing();

        var width      = 150f * Theme.Scale;
        var applyBlock = session.ApplyBlockReason;
        var mode       = session.OutputMode;
        var applyLabel = mode switch
        {
            ConversionOutputMode.NewMod   => "Create new mod",
            ConversionOutputMode.AddToMod => "Add to this mod",
            _                             => "Convert in place",
        };
        if (Widgets.Button($"{applyLabel}##Apply", applyBlock, new Vector2(width, 0), primary: applyBlock == null))
            RequestApply(mode);
    }

    private void RequestApply(ConversionOutputMode mode)
    {
        if (mode.IsNewMod() || !config.ConfirmInPlace)
        {
            session.Apply();
            return;
        }

        var (title, what, verb) = mode == ConversionOutputMode.AddToMod
            ? ("Add the converted item to this mod?",
               $"'{session.ModName}' will be modified, but the original item keeps working. The mod as it is " +
               "now is kept as a backup and can be restored with Revert until that backup expires.",
               "Add")
            : ("Convert this mod in place?",
               $"'{session.ModName}' will be modified directly. The original is kept as a backup and can be " +
               "restored with Revert until the backup expires; see Settings for how long they are kept.",
               "Convert");
        confirm.Request(title, what, verb, session.Apply);
    }

    private void DrawReadiness()
    {
        var task = session.Task;
        if (session.IsBusy)
        {
            Widgets.Spinner(Theme.Accent);
            ImGui.SameLine();
            ImGui.TextUnformatted($"{session.Runner.CurrentLabel} {session.Runner.Elapsed.TotalSeconds:0}s");
            return;
        }

        if (session.ApplyBlockReason == null)
        {
            Widgets.Icon(FontAwesomeIcon.CheckCircle, Theme.Success);
            ImGui.SameLine();
            var notes = task.Diagnostics.Count(d => !d.IsBlocker);
            ImGui.TextColored(Theme.Success, notes switch
            {
                0 => "Ready to convert.",
                1 => "Ready to convert. One thing to check in the plan below.",
                _ => $"Ready to convert. {notes} things to check in the plan below.",
            });
            return;
        }

        if (session.PlanIsCurrent && task.HasBlockers)
        {
            Widgets.Icon(FontAwesomeIcon.TimesCircle, Theme.Danger);
            ImGui.SameLine();
            var blockers = task.Diagnostics.Count(d => d.IsBlocker);
            ImGui.TextColored(Theme.Danger, blockers == 1
                ? "One problem stops this conversion. See the plan below."
                : $"{blockers} problems stop this conversion. See the plan below.");
            return;
        }

        var reason = session.PreviewBlockReason ?? session.ApplyBlockReason;
        var stale  = task.IsPlanned && !session.PlanIsCurrent && !task.IsApplied;
        Widgets.Icon(stale ? FontAwesomeIcon.ExclamationTriangle : FontAwesomeIcon.InfoCircle, stale ? Theme.Warning : Theme.Muted);
        ImGui.SameLine();
        ImGui.TextColored(stale ? Theme.Warning : Theme.Muted, reason ?? string.Empty);
    }

    // ─────────────────────────────────────────────────────────────────────────
    // Result banner
    // ─────────────────────────────────────────────────────────────────────────

    public void DrawResult()
    {
        if (session.Result is not { } result) return;

        var color = Theme.For(result.Kind);
        ImGui.Spacing();
        using var table = ImRaii.Table("##Result", 1, ImGuiTableFlags.BordersOuter | ImGuiTableFlags.PadOuterX);
        if (!table.Success) return;
        ImGui.TableNextRow();
        ImGui.TableSetBgColor(ImGuiTableBgTarget.RowBg0, ImGui.GetColorU32(color.WithAlpha(0.12f)));
        ImGui.TableNextColumn();
        ImGui.Dummy(new Vector2(0, 2f * Theme.Scale));

        Widgets.Icon(result.Kind switch
        {
            BannerKind.Success => FontAwesomeIcon.CheckCircle,
            BannerKind.Warning => FontAwesomeIcon.ExclamationTriangle,
            BannerKind.Error   => FontAwesomeIcon.TimesCircle,
            _                  => FontAwesomeIcon.InfoCircle,
        }, color);
        ImGui.SameLine();
        ImGui.TextColored(color, result.Title);
        ImGui.SameLine();
        ImGui.SetCursorPosX(ImGui.GetCursorPosX() + ImGui.GetContentRegionAvail().X - ImGui.GetFrameHeight());
        if (Widgets.IconButton("##DismissResult", FontAwesomeIcon.Times, "Dismiss"))
            session.Result = null;

        ImGui.TextWrapped(result.Message);

        if (result.Path is { } path && Directory.Exists(path))
        {
            if (Widgets.IconTextButton(FontAwesomeIcon.FolderOpen, "Open folder"))
                ConverterSession.OpenFolder(path);
            ImGui.SameLine();
            if (Widgets.IconTextButton(FontAwesomeIcon.Copy, "Copy path"))
                ImGui.SetClipboardText(path);
            ImGui.SameLine();
        }

        if (result.RetryActivationFolder != null)
        {
            var reason = session.PenumbraAvailable ? null : "Penumbra is not available.";
            if (Widgets.IconTextButton(FontAwesomeIcon.Redo, "Load in Penumbra", reason))
                session.RetryActivation();
            ImGui.SameLine();
        }

        if (result.RecordId is { } recordId && session.History.Find(recordId) is { } record)
        {
            var reason = session.RevertBlockReason(record);
            if (reason == null || !record.IsReverted)
                if (Widgets.IconTextButton(FontAwesomeIcon.Undo, "Revert", reason, RevertTooltip(record)))
                    RequestRevert(record);
        }

        ImGui.NewLine();
    }

    public static string RevertTooltip(ConversionRecord record)
        => record.Mode == ConversionOutputMode.NewMod
            ? "Remove the new mod. It is moved to the backup folder, not deleted."
            : "Restore the mod as it was before this conversion. The converted version is moved to the backup folder.";

    public void RequestRevert(ConversionRecord record)
    {
        var what = record.Mode == ConversionOutputMode.NewMod
            ? $"The new mod '{Path.GetFileName(record.PublishedPath)}' will be removed from Penumbra. " +
              "Its folder is moved into the backup folder, not deleted."
            : $"'{record.SourceModName}' will be restored to how it was before this conversion. " +
              "Any changes made to it since then are lost; the converted version is moved into the backup folder.";
        confirm.Request($"Revert: {record.Description}?", what, "Revert", () => session.Revert(record.Id));
    }
}
