using System;
using System.IO;
using System.Linq;
using System.Numerics;
using AdvancedPenumbraModConverter.Core;
using AdvancedPenumbraModConverter.Session;
using AdvancedPenumbraModConverter.Windows.Ui;
using Dalamud.Bindings.ImGui;
using Dalamud.Interface;
using Dalamud.Interface.Utility.Raii;

namespace AdvancedPenumbraModConverter.Windows.Components;

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

        var newMod = session.OutputMode == ConversionOutputMode.NewMod;
        if (ImGui.RadioButton("Create a new mod", newMod))
            session.SetOutputMode(ConversionOutputMode.NewMod);
        Widgets.Tooltip("Safest: the source mod is never modified.");
        ImGui.SameLine(0, 20f * Theme.Scale);
        if (ImGui.RadioButton("Convert in place", !newMod))
            session.SetOutputMode(ConversionOutputMode.InPlace);
        ImGui.SameLine();
        Widgets.Badge("Advanced", Theme.Warning);

        if (!newMod)
        {
            Widgets.MutedWrapped("Edits this mod directly. The original is kept in a hidden backup, " +
                                 "so the conversion can be reverted from the result or the History tab.");
            return;
        }

        if (_newModName != session.NewModName) _newModName = session.NewModName;
        var buttonWidth = ImGui.GetFrameHeight();
        ImGui.SetNextItemWidth(ImGui.GetContentRegionAvail().X - buttonWidth - ImGui.GetStyle().ItemSpacing.X);
        if (ImGui.InputTextWithHint("##NewModName", "Name of the new mod", ref _newModName, 256))
            session.SetNewModName(_newModName);
        ImGui.SameLine();
        if (Widgets.IconButton("##ResetName", FontAwesomeIcon.Undo, "Use the suggested name"))
            session.ResetNewModName();

        if (session.NewModPath is { } path)
        {
            var exists = Directory.Exists(path) || File.Exists(path);
            Widgets.Icon(exists ? FontAwesomeIcon.ExclamationCircle : FontAwesomeIcon.FolderPlus,
                exists ? Theme.Danger : Theme.Muted);
            ImGui.SameLine();
            Widgets.PathText(path, exists ? Theme.Danger : Theme.Muted);
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

        var width        = 150f * Theme.Scale;
        var previewBlock = session.PreviewBlockReason;
        var applyBlock   = session.ApplyBlockReason;

        var previewLabel = session.PlanIsCurrent ? "Preview again" : "Preview";
        if (Widgets.Button($"{previewLabel}##Preview", previewBlock, new Vector2(width, 0),
                primary: !session.PlanIsCurrent && previewBlock == null,
                tooltip: "Plan the conversion without writing anything."))
            session.Preview();

        ImGui.SameLine();
        var newMod     = session.OutputMode == ConversionOutputMode.NewMod;
        var applyLabel = newMod ? "Create new mod" : "Convert in place";
        if (Widgets.Button($"{applyLabel}##Apply", applyBlock, new Vector2(width, 0), primary: applyBlock == null))
            RequestApply(newMod);

        ImGui.SameLine();
        var auto = config.AutoRefreshPreview;
        if (ImGui.Checkbox("Auto-preview", ref auto))
        {
            config.AutoRefreshPreview = auto;
            config.Save();
        }
        Widgets.Tooltip("Preview automatically once the inputs are complete.");
    }

    private void RequestApply(bool newMod)
    {
        if (newMod || !config.ConfirmInPlace)
        {
            session.Apply();
            return;
        }

        confirm.Request("Convert this mod in place?",
            $"'{session.ModName}' will be modified directly. The original is kept in a hidden backup " +
            "and can be restored with Revert, as long as the backup folder is not deleted.",
            "Convert", session.Apply);
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
            var warnings = task.Diagnostics.Count(d => !d.IsBlocker);
            ImGui.TextColored(Theme.Success, warnings == 0
                ? "Ready to convert."
                : $"Ready to convert, with {warnings} warning(s). Review them in the plan below.");
            return;
        }

        if (session.PlanIsCurrent && task.HasBlockers)
        {
            Widgets.Icon(FontAwesomeIcon.TimesCircle, Theme.Danger);
            ImGui.SameLine();
            ImGui.TextColored(Theme.Danger, $"{task.Diagnostics.Count(d => d.IsBlocker)} blocker(s) prevent this conversion. See the plan below.");
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
            ? "Remove the new mod. It is moved to the hidden backup folder, not deleted."
            : "Restore the mod as it was before this conversion. The converted version is moved to the hidden backup folder.";

    public void RequestRevert(ConversionRecord record)
    {
        var what = record.Mode == ConversionOutputMode.NewMod
            ? $"The new mod '{Path.GetFileName(record.PublishedPath)}' will be removed from Penumbra. " +
              "Its folder is moved into the hidden .apmc-backups folder, not deleted."
            : $"'{record.SourceModName}' will be restored to how it was before this conversion. " +
              "Any changes made to it since then are lost; the converted version is moved into the hidden .apmc-backups folder.";
        confirm.Request($"Revert: {record.Description}?", what, "Revert", () => session.Revert(record.Id));
    }
}
