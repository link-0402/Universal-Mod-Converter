using System;
using System.Collections.Generic;
using System.Linq;
using System.Numerics;
using UniversalModConverter.Session;
using UniversalModConverter.Windows.Ui;
using Dalamud.Bindings.ImGui;
using Dalamud.Interface;
using Dalamud.Interface.Utility.Raii;

namespace UniversalModConverter.Windows.Components;

/// <summary>Left pane: Penumbra's mod list, or a folder path when Penumbra is unavailable.</summary>
internal sealed class ModBrowserPanel(ConverterSession session)
{
    private const string BusyNote = "Wait for the current operation to finish.";

    private string _filter = string.Empty;
    private string _manualPath = string.Empty;
    private IReadOnlyList<ModEntry>? _filteredSource;
    private string _filteredFor = string.Empty;
    private List<ModEntry> _filtered = new();

    public void Draw()
    {
        if (session.PenumbraAvailable)
            DrawModList();
        else
            DrawManualOnly();
    }

    private void DrawModList()
    {
        var buttonWidth = ImGui.GetFrameHeight();
        ImGui.SetNextItemWidth(ImGui.GetContentRegionAvail().X - buttonWidth - ImGui.GetStyle().ItemSpacing.X);
        ImGui.InputTextWithHint("##ModFilter", $"Filter {session.Mods.Count} mods", ref _filter, 128);
        ImGui.SameLine();
        if (Widgets.IconButton("##RefreshMods", FontAwesomeIcon.SyncAlt, "Reload the mod list from Penumbra"))
            session.RefreshMods();

        var mods = FilteredMods();
        var footer = ImGui.GetFrameHeightWithSpacing() * 2 + ImGui.GetStyle().ItemSpacing.Y;
        using (var list = ImRaii.Child("##ModList", new Vector2(-1, -footer), true))
        {
            if (list.Success)
            {
                if (mods.Count == 0)
                    Widgets.Muted(session.Mods.Count == 0 ? "Penumbra reported no mods." : "No mods match the filter.");

                var busy = session.IsBusy;
                var clears = PlanClearedNote();
                Widgets.Clipped(mods.Count, ImGui.GetTextLineHeightWithSpacing(), i =>
                {
                    var mod = mods[i];
                    var selected = string.Equals(mod.Directory, session.ModDirectory, StringComparison.OrdinalIgnoreCase);
                    using var id = ImRaii.PushId(i);
                    if (ImGui.Selectable(mod.Name, selected) && !selected && !busy)
                        session.SelectMod(mod.Directory);
                    var tip = string.Equals(mod.Folder, mod.Name, StringComparison.Ordinal) ? null : mod.Folder;
                    if (!selected && (busy ? BusyNote : clears) is { } note) tip = tip == null ? note : $"{tip}\n{note}";
                    Widgets.Tooltip(tip);
                    if (selected && ImGui.IsWindowAppearing()) ImGui.SetScrollHereY();
                });
            }
        }

        Widgets.Muted("Other folder:");
        DrawPathInput();
    }

    private void DrawManualOnly()
    {
        Widgets.Icon(FontAwesomeIcon.ExclamationTriangle, Theme.Warning);
        ImGui.SameLine();
        ImGui.TextColored(Theme.Warning, "Penumbra is not available");
        Widgets.MutedWrapped("Enter the path of a mod folder (the folder that contains meta.json). " +
                             "New mods will not be registered with Penumbra automatically.");
        ImGui.Spacing();
        DrawPathInput();
    }

    private void DrawPathInput()
    {
        var buttonWidth = ImGui.GetFrameHeight();
        ImGui.SetNextItemWidth(ImGui.GetContentRegionAvail().X - buttonWidth - ImGui.GetStyle().ItemSpacing.X);
        var submitted = ImGui.InputTextWithHint("##ManualPath", "Path to a mod folder", ref _manualPath, 512,
            ImGuiInputTextFlags.EnterReturnsTrue);
        ImGui.SameLine();
        var reason = session.IsBusy ? "Wait for the current operation to finish."
            : string.IsNullOrWhiteSpace(_manualPath) ? "Enter a folder path first." : null;
        var tooltip = PlanClearedNote() is { } note ? $"Load this folder\n{note}" : "Load this folder";
        if ((Widgets.IconButton("##LoadPath", FontAwesomeIcon.FolderOpen, tooltip, reason) || submitted) && reason == null)
            session.SelectMod(_manualPath);
    }

    /// <summary>A plan belongs to its mod, so choosing another one starts afresh; null when nothing is planned.</summary>
    private string? PlanClearedNote()
        => session.Queue.Count switch
        {
            0 => null,
            1 => "Choosing another mod clears the conversion plan (1 conversion).",
            var count => $"Choosing another mod clears the conversion plan ({count} conversions).",
        };

    private List<ModEntry> FilteredMods()
    {
        if (ReferenceEquals(_filteredSource, session.Mods) && _filteredFor == _filter) return _filtered;
        _filteredSource = session.Mods;
        _filteredFor    = _filter;
        var q = _filter.Trim();
        _filtered = q.Length == 0
            ? session.Mods.ToList()
            : session.Mods.Where(m => m.Name.Contains(q, StringComparison.OrdinalIgnoreCase) ||
                                      m.Folder.Contains(q, StringComparison.OrdinalIgnoreCase)).ToList();
        return _filtered;
    }
}
