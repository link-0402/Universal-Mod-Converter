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

/// <summary>Filterable operation log.</summary>
internal sealed class LogView(LogStore log)
{
    private bool _showInfo = true;
    private bool _showWarnings = true;
    private bool _showErrors = true;
    private bool _autoScroll = true;
    private string _filter = string.Empty;

    private int _builtRevision = -1;
    private (bool, bool, bool, string) _builtFilter;
    private List<LogEntry> _visible = new();
    private float _widthFontSize = -1f;
    private float _contentWidth;

    public void Draw(float minHeight)
    {
        ImGui.Checkbox("Info", ref _showInfo);
        ImGui.SameLine();
        using (ImRaii.PushColor(ImGuiCol.Text, Theme.Warning))
            ImGui.Checkbox("Warnings", ref _showWarnings);
        ImGui.SameLine();
        using (ImRaii.PushColor(ImGuiCol.Text, Theme.Danger))
            ImGui.Checkbox("Errors", ref _showErrors);
        ImGui.SameLine();
        ImGui.SetNextItemWidth(160f * Theme.Scale);
        ImGui.InputTextWithHint("##LogFilter", "Search", ref _filter, 128);
        ImGui.SameLine();
        ImGui.Checkbox("Follow", ref _autoScroll);
        Widgets.Tooltip("Keep the newest entry in view.");

        ImGui.SameLine();
        var lastReason = log.LastConversionOperation == 0 ? "No conversion has run yet." : null;
        if (Widgets.IconButton("##CopyLast", FontAwesomeIcon.Clipboard, "Copy the log of the last conversion", lastReason))
            ImGui.SetClipboardText(log.Format(log.LastConversion()));
        ImGui.SameLine();
        if (Widgets.IconButton("##CopyAll", FontAwesomeIcon.Copy, "Copy the visible entries"))
            ImGui.SetClipboardText(log.Format(Visible()));
        ImGui.SameLine();
        if (Widgets.IconButton("##ClearLog", FontAwesomeIcon.Trash, "Clear the log"))
            log.Clear();

        var entries = Visible();
        var height  = Math.Max(minHeight, ImGui.GetContentRegionAvail().Y);
        // The clipper only submits visible rows, so without a fixed width the horizontal scrollbar
        // comes and goes as long rows scroll in and out, and Follow chases the changing bottom.
        ImGui.SetNextWindowContentSize(new Vector2(ContentWidth(entries), 0f));
        using var child = ImRaii.Child("##LogScroll", new Vector2(-1, height), true, ImGuiWindowFlags.HorizontalScrollbar);
        if (!child.Success) return;
        if (entries.Count == 0)
        {
            Widgets.Muted(log.Entries.Count == 0 ? "Nothing logged yet." : "No entries match the filter.");
            return;
        }

        var atBottom = ImGui.GetScrollY() >= ImGui.GetScrollMaxY() - 1f;
        Widgets.Clipped(entries.Count, ImGui.GetTextLineHeightWithSpacing(), i =>
        {
            var entry = entries[i];
            ImGui.TextColored(Theme.Muted, entry.Time.ToString("HH:mm:ss"));
            ImGui.SameLine();
            ImGui.TextColored(Theme.For(entry.Level), entry.Text);
            Widgets.CopyOnRightClick(entry.Text, false);
        });
        if (_autoScroll && atBottom) ImGui.SetScrollHereY(1f);
    }

    /// <summary>Width of the widest visible row, recomputed when the rows or the font size change.</summary>
    private float ContentWidth(List<LogEntry> entries)
    {
        var fontSize = ImGui.GetFontSize();
        if (_widthFontSize == fontSize) return _contentWidth;
        _widthFontSize = fontSize;
        var spacing = ImGui.GetStyle().ItemSpacing.X;
        _contentWidth = 0f;
        foreach (var e in entries)
            _contentWidth = Math.Max(_contentWidth,
                ImGui.CalcTextSize(e.Time.ToString("HH:mm:ss")).X + spacing + ImGui.CalcTextSize(e.Text).X);
        return _contentWidth;
    }

    private List<LogEntry> Visible()
    {
        var filter = (_showInfo, _showWarnings, _showErrors, _filter);
        if (_builtRevision == log.Revision && _builtFilter == filter) return _visible;
        _widthFontSize = -1f;
        _builtRevision = log.Revision;
        _builtFilter   = filter;
        var q = _filter.Trim();
        _visible = log.Entries.Where(e => e.Level switch
            {
                LogLevel.Warning => _showWarnings,
                LogLevel.Error   => _showErrors,
                _                => _showInfo,
            })
            .Where(e => q.Length == 0 || e.Text.Contains(q, StringComparison.OrdinalIgnoreCase))
            .ToList();
        return _visible;
    }
}
