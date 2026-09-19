using System;
using System.Numerics;
using Dalamud.Bindings.ImGui;
using Dalamud.Interface;
using Dalamud.Interface.Utility.Raii;

namespace AdvancedPenumbraItemConverter.Windows.Ui;

/// <summary>One modal confirmation at a time, drawn by the owning window.</summary>
internal sealed class ConfirmDialog
{
    private const string PopupId = "Confirm###APICConfirm";

    private string _title = string.Empty;
    private string _message = string.Empty;
    private string _confirmLabel = string.Empty;
    private Action? _onConfirm;
    private bool _openRequested;

    public void Request(string title, string message, string confirmLabel, Action onConfirm)
    {
        _title         = title;
        _message       = message;
        _confirmLabel  = confirmLabel;
        _onConfirm     = onConfirm;
        _openRequested = true;
    }

    public void Draw()
    {
        if (_openRequested)
        {
            _openRequested = false;
            ImGui.OpenPopup(PopupId);
        }

        ImGui.SetNextWindowSizeConstraints(new Vector2(360, 0) * Theme.Scale, new Vector2(560, 800) * Theme.Scale);
        if (!ImGui.BeginPopupModal(PopupId, ImGuiWindowFlags.AlwaysAutoResize | ImGuiWindowFlags.NoTitleBar))
            return;

        Widgets.Icon(FontAwesomeIcon.ExclamationTriangle, Theme.Warning);
        ImGui.SameLine();
        ImGui.TextUnformatted(_title);
        ImGui.Separator();
        using (ImRaii.TextWrapPos(ImGui.GetFontSize() * 32f))
            ImGui.TextUnformatted(_message);
        ImGui.Spacing();

        var width = 120f * Theme.Scale;
        if (Widgets.Button(_confirmLabel, null, new Vector2(width, 0), primary: true))
        {
            ImGui.CloseCurrentPopup();
            var action = _onConfirm;
            _onConfirm = null;
            action?.Invoke();
        }
        ImGui.SameLine();
        if (ImGui.Button("Cancel", new Vector2(width, 0)) || ImGui.IsKeyPressed(ImGuiKey.Escape))
        {
            _onConfirm = null;
            ImGui.CloseCurrentPopup();
        }
        ImGui.EndPopup();
    }
}
