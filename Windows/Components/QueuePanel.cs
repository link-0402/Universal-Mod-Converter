using System;
using System.Linq;
using System.Numerics;
using UniversalModConverter.Models;
using UniversalModConverter.Session;
using UniversalModConverter.Windows.Ui;
using Dalamud.Bindings.ImGui;
using Dalamud.Interface;
using Dalamud.Interface.Utility.Raii;

namespace UniversalModConverter.Windows.Components;

/// <summary>
/// The conversion plan: what Preview and Apply work on, each entry shown as source icon →
/// target icon with its switch and remove button. Nothing converts until it is in here, so
/// even a single conversion is confirmed by adding it, and the cards above only ever choose
/// what to add next.
/// </summary>
internal sealed class QueuePanel(ConverterSession session)
{
    /// <summary>Rows shown before the card starts scrolling.</summary>
    private const int VisibleRows = 5;

    public void Draw()
    {
        DrawAddButton();
        ImGui.Spacing();

        var iconSize = ImGui.GetTextLineHeight() * 2.2f;
        var rowHeight = iconSize + ImGui.GetStyle().ItemSpacing.Y;
        var count = session.Queue.Count;
        var body = count == 0
            ? ImGui.GetTextLineHeightWithSpacing() * 2
            : Math.Min(count, VisibleRows) * rowHeight;
        var height = ImGui.GetTextLineHeightWithSpacing() * 1.6f + body + 12f * Theme.Scale;

        Widgets.BeginCard("##PlanCard", new Vector2(-1, height));
        try { DrawPlan(iconSize); }
        finally { Widgets.EndCard(); }
    }

    private void DrawPlan(float iconSize)
    {
        if (session.Queue.Count == 0)
        {
            Widgets.SectionTitle("Conversion plan", FontAwesomeIcon.ListUl);
            Widgets.MutedWrapped("Nothing is planned yet. Choose what to convert above, then add it with " +
                                 "\"Add selection to conversion plan\". Everything listed here is converted together.");
            return;
        }

        if (Widgets.SectionTitle($"Conversion plan ({session.Queue.Count})", FontAwesomeIcon.ListUl,
                FontAwesomeIcon.TrashAlt, "Clear the plan", "Remove every conversion from the plan."))
            session.ClearQueue();

        Guid? remove = null;
        foreach (var entry in session.Queue)
        {
            using var id = ImRaii.PushId(entry.Id.ToString());
            var rowStart = ImGui.GetCursorPosY();

            // The checkbox sits level with the icons rather than at the top of the row.
            ImGui.SetCursorPosY(rowStart + (iconSize - ImGui.GetFrameHeight()) / 2);
            var enabled = entry.Enabled;
            if (ImGui.Checkbox("##enabled", ref enabled)) session.SetQueueEntryEnabled(entry.Id, enabled);
            Widgets.Tooltip(enabled ? "Leave this one out when converting." : "Include this one when converting.");

            ImGui.SameLine();
            ImGui.SetCursorPosY(rowStart);
            using (ImRaii.PushStyle(ImGuiStyleVar.Alpha, enabled ? 1f : 0.45f))
                ConversionRow.Draw(entry.Source, entry.Target, iconSize);

            if (entry.Rejected)
            {
                ImGui.SameLine();
                ImGui.SetCursorPosY(rowStart + (iconSize - ImGui.GetFrameHeight()) / 2);
                Widgets.Badge("Overlaps another", Theme.Danger);
                if (entry.Diagnostics.FirstOrDefault(d => d.IsBlocker) is { } blocker)
                    Widgets.Tooltip(blocker.Message);
            }

            ImGui.SameLine(ImGui.GetContentRegionMax().X - ImGui.GetFrameHeight());
            ImGui.SetCursorPosY(rowStart + (iconSize - ImGui.GetFrameHeight()) / 2);
            if (Widgets.IconButton("##remove", FontAwesomeIcon.Times, "Remove from the plan"))
                remove = entry.Id;

            ImGui.SetCursorPosY(rowStart + iconSize + ImGui.GetStyle().ItemSpacing.Y);
        }

        if (remove is { } removed) session.RemoveFromQueue(removed);
    }

    private void DrawAddButton()
    {
        var reason = session.EnqueueBlockReason;
        if (Widgets.Button("Add selection to conversion plan##Enqueue", reason, new Vector2(260f * Theme.Scale, 0),
                primary: reason == null && session.Queue.Count == 0))
            session.EnqueueCurrent();
        if (reason == null)
            Widgets.Tooltip("Add what the cards above describe to the plan below. Only what is in the plan is converted.");
    }
}

/// <summary>One conversion as source icon → target icon, shared by the list and the plan summary.</summary>
internal static class ConversionRow
{
    public static void Draw(ConversionSide source, ConversionSide target, float iconSize)
    {
        using (ImRaii.Group())
        {
            DrawSide(source, iconSize, Theme.Muted);
            ImGui.SameLine();
            using (ImRaii.PushFont(UiBuilder.IconFont))
            {
                ImGui.SetCursorPosY(ImGui.GetCursorPosY() + (iconSize - ImGui.GetTextLineHeight()) / 2);
                ImGui.TextColored(Theme.Accent, FontAwesomeIcon.LongArrowAltRight.ToIconString());
            }
            ImGui.SameLine();
            DrawSide(target, iconSize, Theme.Success);
        }
    }

    private static void DrawSide(ConversionSide side, float iconSize, Vector4 detailColor)
    {
        if (side.Icon != 0)
            Widgets.GameIcon(side.Icon, iconSize);
        else
        {
            using (ImRaii.PushColor(ImGuiCol.Text, Theme.Muted))
            using (ImRaii.PushFont(UiBuilder.IconFont))
                ImGui.Button(FontAwesomeIcon.Running.ToIconString() + "##kind", new Vector2(iconSize));
        }

        ImGui.SameLine();
        using (ImRaii.Group())
        {
            ImGui.TextUnformatted(side.Name);
            if (side.Detail.Length > 0) ImGui.TextColored(detailColor, side.Detail);
        }
    }
}
