using System;
using System.Numerics;
using Dalamud.Bindings.ImGui;
using Dalamud.Interface;
using Dalamud.Interface.Components;
using Dalamud.Interface.Textures;
using Dalamud.Interface.Utility.Raii;

namespace AdvancedPenumbraItemConverter.Windows.Ui;

/// <summary>Small reusable drawing helpers shared by the window components.</summary>
internal static class Widgets
{
    public static void Icon(FontAwesomeIcon icon, Vector4? color = null)
    {
        using var font = ImRaii.PushFont(UiBuilder.IconFont);
        if (color is { } c) ImGui.TextColored(c, icon.ToIconString());
        else ImGui.TextUnformatted(icon.ToIconString());
    }

    /// <summary>Draws a game icon, or an empty square of the same size while it loads.</summary>
    public static void GameIcon(uint iconId, float size)
    {
        if (iconId != 0 &&
            Plugin.TextureProvider.GetFromGameIcon(new GameIconLookup(iconId)).TryGetWrap(out var wrap, out _))
            ImGui.Image(wrap.Handle, new Vector2(size));
        else
            ImGui.Dummy(new Vector2(size));
    }

    public static void Muted(string text) => ImGui.TextColored(Theme.Muted, text);

    public static void MutedWrapped(string text)
    {
        using var color = ImRaii.PushColor(ImGuiCol.Text, Theme.Muted);
        ImGui.TextWrapped(text);
    }

    public static void ColoredWrapped(Vector4 color, string text)
    {
        using var c = ImRaii.PushColor(ImGuiCol.Text, color);
        ImGui.TextWrapped(text);
    }

    /// <summary>Tooltip for the last item that also shows while it is disabled.</summary>
    public static void Tooltip(string? text)
    {
        if (string.IsNullOrEmpty(text) || !ImGui.IsItemHovered(ImGuiHoveredFlags.AllowWhenDisabled)) return;
        using var tooltip = ImRaii.Tooltip();
        using var wrap = ImRaii.TextWrapPos(ImGui.GetFontSize() * 30f);
        ImGui.TextUnformatted(text);
    }

    /// <summary>A button that is disabled with an explanation instead of silently doing nothing.</summary>
    public static bool Button(string label, string? disabledReason, Vector2 size = default, bool primary = false, string? tooltip = null)
    {
        var enabled = disabledReason == null;
        bool clicked;
        using (ImRaii.Disabled(!enabled))
        {
            using var color = ImRaii.PushColor(ImGuiCol.Button, Theme.Accent.WithAlpha(0.55f), primary && enabled)
                .Push(ImGuiCol.ButtonHovered, Theme.Accent.WithAlpha(0.75f), primary && enabled)
                .Push(ImGuiCol.ButtonActive, Theme.Accent.WithAlpha(0.95f), primary && enabled);
            clicked = ImGui.Button(label, size);
        }
        Tooltip(enabled ? tooltip : disabledReason);
        return clicked && enabled;
    }

    public static bool IconButton(string id, FontAwesomeIcon icon, string tooltip, string? disabledReason = null)
    {
        var enabled = disabledReason == null;
        bool clicked;
        using (ImRaii.Disabled(!enabled))
            clicked = ImGuiComponents.IconButton(id, icon);
        Tooltip(enabled ? tooltip : disabledReason);
        return clicked && enabled;
    }

    public static bool IconTextButton(FontAwesomeIcon icon, string text, string? disabledReason = null, string? tooltip = null)
    {
        var enabled = disabledReason == null;
        bool clicked;
        using (ImRaii.Disabled(!enabled))
            clicked = ImGuiComponents.IconButtonWithText(icon, text);
        Tooltip(enabled ? tooltip : disabledReason);
        return clicked && enabled;
    }

    /// <summary>A small rounded label, e.g. a slot or status tag.</summary>
    public static void Badge(string text, Vector4 color)
    {
        var padding = new Vector2(5f, 1f) * Theme.Scale;
        var size    = ImGui.CalcTextSize(text) + padding * 2;
        var pos     = ImGui.GetCursorScreenPos();
        var draw    = ImGui.GetWindowDrawList();
        draw.AddRectFilled(pos, pos + size, ImGui.GetColorU32(color.WithAlpha(0.18f)), Theme.Rounding);
        draw.AddRect(pos, pos + size, ImGui.GetColorU32(color.WithAlpha(0.55f)), Theme.Rounding);
        draw.AddText(pos + padding, ImGui.GetColorU32(color), text);
        ImGui.Dummy(size);
    }

    /// <summary>Header text followed by a thin separator line.</summary>
    public static void SectionTitle(string text, FontAwesomeIcon? icon = null)
    {
        if (icon is { } i)
        {
            Icon(i, Theme.Muted);
            ImGui.SameLine();
        }
        ImGui.TextColored(Theme.Muted, text.ToUpperInvariant());
        var pos = ImGui.GetCursorScreenPos();
        ImGui.GetWindowDrawList().AddLine(pos, pos + new Vector2(ImGui.GetContentRegionAvail().X, 0),
            ImGui.GetColorU32(ImGuiCol.Separator));
        ImGui.Dummy(new Vector2(0, 3f * Theme.Scale));
    }

    /// <summary>
    /// A path shortened in the middle to fit <paramref name="maxWidth"/>; hovering shows the
    /// full path and right-click copies it.
    /// </summary>
    public static void PathText(string path, Vector4 color, float maxWidth = 0)
    {
        if (maxWidth <= 0) maxWidth = ImGui.GetContentRegionAvail().X;
        var shown = Ellipsize(path, maxWidth);
        ImGui.TextColored(color, shown);
        CopyOnRightClick(path, shown != path);
    }

    /// <summary>Right-click copies <paramref name="value"/>; optionally shows it as a tooltip.</summary>
    public static void CopyOnRightClick(string value, bool showTooltip = true)
    {
        if (!ImGui.IsItemHovered()) return;
        if (ImGui.IsMouseClicked(ImGuiMouseButton.Right)) ImGui.SetClipboardText(value);
        using var tooltip = ImRaii.Tooltip();
        if (showTooltip)
        {
            using var wrap = ImRaii.TextWrapPos(ImGui.GetFontSize() * 45f);
            ImGui.TextUnformatted(value);
        }
        ImGui.TextColored(Theme.Muted, "Right-click to copy");
    }

    public static string Ellipsize(string text, float maxWidth)
    {
        if (ImGui.CalcTextSize(text).X <= maxWidth || text.Length < 8) return text;
        int keep = text.Length;
        while (keep > 6)
        {
            keep = (int)(keep * 0.85f);
            var head = keep / 3;
            var tail = keep - head;
            var candidate = string.Concat(text.AsSpan(0, head), "…", text.AsSpan(text.Length - tail));
            if (ImGui.CalcTextSize(candidate).X <= maxWidth) return candidate;
        }
        return string.Concat("…", text.AsSpan(Math.Max(0, text.Length - 6)));
    }

    /// <summary>An indeterminate spinner the height of a text line.</summary>
    public static void Spinner(Vector4 color)
    {
        var size   = ImGui.GetTextLineHeight();
        var pos    = ImGui.GetCursorScreenPos();
        var center = pos + new Vector2(size / 2, size / 2);
        var start  = (float)(ImGui.GetTime() * 6.0 % (Math.PI * 2));
        var draw   = ImGui.GetWindowDrawList();
        draw.PathArcTo(center, size * 0.38f, start, start + MathF.PI * 1.4f, 20);
        draw.PathStroke(ImGui.GetColorU32(color), ImDrawFlags.None, 2f * Theme.Scale);
        ImGui.Dummy(new Vector2(size, size));
    }

    /// <summary>Draws only the visible rows of a uniform-height list or table.</summary>
    public static void Clipped(int count, float rowHeight, Action<int> drawRow)
    {
        var clipper = ImGui.ImGuiListClipper();
        clipper.Begin(count, rowHeight);
        while (clipper.Step())
            for (var i = clipper.DisplayStart; i < clipper.DisplayEnd; i++)
                drawRow(i);
        clipper.End();
        clipper.Destroy();
    }

    /// <summary>Begins a bordered, padded panel. Always pair with <see cref="EndCard"/>.</summary>
    public static void BeginCard(string id, Vector2 size)
    {
        ImGui.PushStyleColor(ImGuiCol.ChildBg, Theme.CardBackground);
        ImGui.PushStyleVar(ImGuiStyleVar.ChildRounding, Theme.Rounding);
        ImGui.PushStyleVar(ImGuiStyleVar.WindowPadding, new Vector2(8f, 6f) * Theme.Scale);
        ImGui.BeginChild(id, size, true, ImGuiWindowFlags.AlwaysUseWindowPadding);
    }

    public static void EndCard()
    {
        ImGui.EndChild();
        ImGui.PopStyleVar(2);
        ImGui.PopStyleColor();
    }
}
