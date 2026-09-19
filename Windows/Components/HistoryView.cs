using System;
using System.Collections.Generic;
using System.IO;
using AdvancedPenumbraModConverter.Core;
using AdvancedPenumbraModConverter.Session;
using AdvancedPenumbraModConverter.Windows.Ui;
using Dalamud.Bindings.ImGui;
using Dalamud.Interface;
using Dalamud.Interface.Utility.Raii;

namespace AdvancedPenumbraModConverter.Windows.Components;

/// <summary>Past conversions with a Revert action.</summary>
internal sealed class HistoryView(ConverterSession session, ActionPanels actions)
{
    private static readonly TimeSpan RefreshInterval = TimeSpan.FromSeconds(1);

    // Revert eligibility touches the filesystem; don't re-check it every frame.
    private readonly Dictionary<Guid, (string? RevertReason, bool FolderExists)> _status = new();
    private DateTime _statusTime = DateTime.MinValue;
    private bool _statusBusy;

    public void Draw()
    {
        var records = session.History.Records;
        if (records.Count == 0)
        {
            Widgets.MutedWrapped("Conversions you apply appear here, and can be reverted from here.");
            return;
        }

        if (DateTime.UtcNow - _statusTime > RefreshInterval || _statusBusy != session.IsBusy || _status.Count != records.Count)
        {
            _status.Clear();
            foreach (var record in records)
            {
                var folder = record.IsReverted ? record.RevertedOutputPath : record.PublishedPath;
                _status[record.Id] = (session.RevertBlockReason(record), folder != null && Directory.Exists(folder));
            }
            _statusTime = DateTime.UtcNow;
            _statusBusy = session.IsBusy;
        }

        using var table = ImRaii.Table("##History", 4,
            ImGuiTableFlags.RowBg | ImGuiTableFlags.BordersInnerH | ImGuiTableFlags.SizingStretchProp);
        if (!table.Success) return;
        ImGui.TableSetupColumn("When", ImGuiTableColumnFlags.WidthFixed, 110f * Theme.Scale);
        ImGui.TableSetupColumn("Conversion", ImGuiTableColumnFlags.WidthStretch);
        ImGui.TableSetupColumn("Status", ImGuiTableColumnFlags.WidthFixed, 90f * Theme.Scale);
        ImGui.TableSetupColumn("##Actions", ImGuiTableColumnFlags.WidthFixed, ImGui.GetFrameHeight() * 2 + ImGui.GetStyle().ItemSpacing.X);
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
            Widgets.Badge(record.Mode == ConversionOutputMode.NewMod ? "New mod" : "In place", Theme.Muted);
            ImGui.SameLine();
            Widgets.Muted(record.Mode == ConversionOutputMode.NewMod
                ? $"{record.SourceModName} → {Path.GetFileName(record.PublishedPath)}"
                : record.SourceModName);

            ImGui.TableNextColumn();
            var (reason, folderExists) = _status.TryGetValue(record.Id, out var status) ? status : (null, false);
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
            var folderReason = folderExists ? null : "The folder no longer exists.";
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
