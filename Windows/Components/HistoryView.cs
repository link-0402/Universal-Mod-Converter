using System.IO;
using UniversalModConverter.Core;
using UniversalModConverter.Session;
using UniversalModConverter.Windows.Ui;
using Dalamud.Bindings.ImGui;
using Dalamud.Interface;
using Dalamud.Interface.Utility.Raii;

namespace UniversalModConverter.Windows.Components;

/// <summary>Past conversions with a Revert action.</summary>
internal sealed class HistoryView(ConverterSession session, ActionPanels actions)
{
    public void Draw()
    {
        var records = session.History.Records;
        if (records.Count == 0)
        {
            Widgets.MutedWrapped("Conversions you apply appear here, and can be reverted from here.");
            return;
        }

        using var table = ImRaii.Table("##History", 4,
            ImGuiTableFlags.RowBg | ImGuiTableFlags.BordersInnerH | ImGuiTableFlags.SizingStretchProp);
        if (!table.Success) return;
        ImGui.TableSetupColumn("When", ImGuiTableColumnFlags.WidthFixed, 110f * Theme.Scale);
        ImGui.TableSetupColumn("Conversion", ImGuiTableColumnFlags.WidthStretch);
        ImGui.TableSetupColumn("Status", ImGuiTableColumnFlags.WidthFixed, 90f * Theme.Scale);
        ImGui.TableSetupColumn("##Actions", ImGuiTableColumnFlags.WidthFixed,
            ImGui.GetFrameHeight() * 2 + ImGui.GetStyle().ItemSpacing.X + ImGui.GetStyle().CellPadding.X * 2);
        ImGui.TableHeadersRow();

        for (var i = 0; i < records.Count; i++)
        {
            var record = records[i];
            using var id = ImRaii.PushId(record.Id.ToString());
            ImGui.TableNextRow();

            ImGui.TableNextColumn();
            Widgets.Muted(record.TimestampUtc.ToLocalTime().ToString("MMM d, HH:mm"));

            ImGui.TableNextColumn();
            ImGui.TextWrapped(record.Description);
            Widgets.Badge(PlanView.OutputModeBadge(record.Mode), Theme.Muted);
            ImGui.SameLine();
            Widgets.Muted(record.Mode.IsNewMod()
                ? $"{record.SourceModName} → {Path.GetFileName(record.PublishedPath)}"
                : record.SourceModName);
            if (record.Entries.Count > 1)
            {
                ImGui.SameLine();
                Widgets.Badge($"{record.Entries.Count} conversions", Theme.Info);
                Widgets.Tooltip(string.Join("\n", record.Entries));
            }

            ImGui.TableNextColumn();
            // Both touch the disk, so the session remembers them for a moment.
            var reason = session.DisplayedRevertBlockReason(record);
            if (record.IsReverted)
                Widgets.Badge("Reverted", Theme.Muted);
            else if (reason == null || session.IsBusy)
                Widgets.Badge("Active", Theme.Success);
            else
            {
                Widgets.Badge("Unavailable", Theme.Warning);
                Widgets.Tooltip(reason);
            }
            if (record.IsReverted && record.RevertedOutputPath is { } parked)
                Widgets.Tooltip($"The converted output was moved to:\n{parked}");

            ImGui.TableNextColumn();
            var folder = record.IsReverted ? record.RevertedOutputPath : record.PublishedPath;
            var folderReason = folder != null && session.PathExists(folder) ? null : "The folder no longer exists.";
            if (Widgets.IconButton("##Open", FontAwesomeIcon.FolderOpen,
                    record.IsReverted ? "Open the folder the reverted output was moved to" : "Open the converted mod's folder",
                    folderReason))
                ConverterSession.OpenFolder(folder!);
            ImGui.SameLine();
            if (!record.IsReverted &&
                Widgets.IconButton("##Revert", FontAwesomeIcon.Undo, ActionPanels.RevertTooltip(record), reason))
                actions.RequestRevert(record);
        }
    }
}
