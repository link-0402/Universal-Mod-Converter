using System;
using System.Linq;
using System.Numerics;
using AdvancedPenumbraModConverter.Core;
using AdvancedPenumbraModConverter.Models;
using AdvancedPenumbraModConverter.Services;
using AdvancedPenumbraModConverter.Session;
using AdvancedPenumbraModConverter.Windows.Ui;
using Dalamud.Bindings.ImGui;
using Dalamud.Interface;
using Dalamud.Interface.Utility.Raii;

namespace AdvancedPenumbraModConverter.Windows.Components;

/// <summary>The "From" and "To" cards: source root selection and target picker.</summary>
internal sealed class ConversionCards(ConverterSession session)
{
    private const float WideLayoutWidth = 640f;

    private string _targetFilter = string.Empty;

    public void Draw()
    {
        var scale  = Theme.Scale;
        var height = 280f * scale;
        var wide   = ImGui.GetContentRegionAvail().X >= WideLayoutWidth * scale;

        if (!wide)
        {
            DrawCard("##FromCard", new Vector2(-1, 150f * scale), DrawSource);
            DrawCard("##ToCard", new Vector2(-1, height), DrawTarget);
            return;
        }

        using var table = ImRaii.Table("##Cards", 3, ImGuiTableFlags.None);
        if (!table.Success) return;
        var arrowWidth = ImGui.GetFrameHeight();
        ImGui.TableSetupColumn("From", ImGuiTableColumnFlags.WidthStretch, 0.42f);
        ImGui.TableSetupColumn("Arrow", ImGuiTableColumnFlags.WidthFixed, arrowWidth);
        ImGui.TableSetupColumn("To", ImGuiTableColumnFlags.WidthStretch, 0.58f);

        ImGui.TableNextColumn();
        DrawCard("##FromCard", new Vector2(-1, height), DrawSource);
        ImGui.TableNextColumn();
        ImGui.SetCursorPosY(ImGui.GetCursorPosY() + height / 2 - ImGui.GetTextLineHeight() / 2);
        Widgets.Icon(FontAwesomeIcon.ArrowRight, Theme.Muted);
        ImGui.TableNextColumn();
        DrawCard("##ToCard", new Vector2(-1, height), DrawTarget);
    }

    private static void DrawCard(string id, Vector2 size, Action content)
    {
        Widgets.BeginCard(id, size);
        content();
        Widgets.EndCard();
    }

    // ─────────────────────────────────────────────────────────────────────────
    // Source
    // ─────────────────────────────────────────────────────────────────────────

    private void DrawSource()
    {
        Widgets.SectionTitle("From", FontAwesomeIcon.Tshirt);

        if (!session.HasMod)
        {
            Widgets.MutedWrapped("Select a mod first.");
            return;
        }
        if (session.ModError is { } error)
        {
            Widgets.ColoredWrapped(Theme.Danger, error);
            return;
        }
        if (session.DetectedItems.Count == 0)
        {
            if (session.Runner.CurrentLabel?.StartsWith("Scanning") == true)
            {
                Widgets.Spinner(Theme.Accent);
                ImGui.SameLine();
                Widgets.Muted("Scanning the mod…");
            }
            else
                Widgets.MutedWrapped("No gear, facewear, hair, face, tail or Viera-ear root was found in this mod.");
            return;
        }

        if (session.DetectedItems.Count > 1)
        {
            Widgets.Muted($"{session.DetectedItems.Count} convertible roots in this mod:");
            ImGui.SetNextItemWidth(-1);
            var preview = session.Source is { } current ? SourceLabel(current) : "Select a source…";
            using (var combo = ImRaii.Combo("##Source", preview, ImGuiComboFlags.HeightLarge))
            {
                if (combo.Success)
                {
                    for (var i = 0; i < session.DetectedItems.Count; i++)
                    {
                        var item = session.DetectedItems[i];
                        using var id = ImRaii.PushId(i);
                        if (ImGui.Selectable(SourceLabel(item), i == session.SourceIndex))
                            session.SelectSource(i);
                        if (i == session.SourceIndex) ImGui.SetItemDefaultFocus();
                    }
                }
            }
            ImGui.Spacing();
        }

        if (session.Source is not { } source)
        {
            Widgets.MutedWrapped("Pick the item this mod replaces that you want to move.");
            return;
        }

        DrawItemSummary(source);
    }

    private static string SourceLabel(DetectedItem item)
        => $"[{KindLabel(item)}] {item.ItemName}  ·  {IdLabel(item)}";

    private static string KindLabel(DetectedItem item)
        => item.IsCustomization ? CustomizationKinds.Get(item.Kind).DisplayName : SlotInfo.DisplayLabelMap[item.Slot];

    private static string IdLabel(DetectedItem item)
        => item.IsCustomization ? ConverterSession.RaceLabel(item.GenderRace ?? 0) : $"{(item.IsAccessory ? 'a' : 'e')}{item.ModelIdDisplay}";

    private static void DrawItemSummary(DetectedItem source)
    {
        var iconSize = ImGui.GetTextLineHeight() * 2.6f;
        if (source.IsCustomization)
        {
            using (ImRaii.PushColor(ImGuiCol.Text, Theme.Muted))
            using (ImRaii.PushFont(UiBuilder.IconFont))
            {
                var glyph = source.Kind switch
                {
                    AssetKind.Hair => FontAwesomeIcon.Cut,
                    AssetKind.Face => FontAwesomeIcon.UserCircle,
                    _              => FontAwesomeIcon.Paw,
                };
                ImGui.Button(glyph.ToIconString() + "##kind", new Vector2(iconSize));
            }
        }
        else
            Widgets.GameIcon(source.Icon, iconSize);

        ImGui.SameLine();
        using (ImRaii.Group())
        {
            ImGui.TextWrapped(source.ItemName);
            Widgets.Badge(KindLabel(source), Theme.Accent);
            ImGui.SameLine();
            Widgets.Badge(IdLabel(source), Theme.Info);
        }

        if (source.IsAmbiguous)
        {
            ImGui.Spacing();
            Widgets.ColoredWrapped(Theme.Warning,
                "Several game items share this model; the conversion applies to all of them.");
        }
    }

    // ─────────────────────────────────────────────────────────────────────────
    // Target
    // ─────────────────────────────────────────────────────────────────────────

    private void DrawTarget()
    {
        Widgets.SectionTitle("To", FontAwesomeIcon.Bullseye);
        if (session.Source is not { } source)
        {
            Widgets.MutedWrapped("Select a source item first.");
            return;
        }

        if (source.IsCustomization) DrawCustomizationTarget(source);
        else DrawGearTarget();
    }

    private void DrawGearTarget()
    {
        var slotWidth = 120f * Theme.Scale;
        ImGui.SetNextItemWidth(slotWidth);
        using (var combo = ImRaii.Combo("##TargetSlot", SlotInfo.DisplayLabelMap[session.TargetSlot]))
        {
            if (combo.Success)
                foreach (var slot in ConverterSession.OutputSlots)
                    if (ImGui.Selectable(SlotInfo.DisplayLabelMap[slot], slot == session.TargetSlot))
                        session.SetTargetSlot(slot);
        }
        Widgets.Tooltip("Destination slot. Any wearable slot is allowed.");

        ImGui.SameLine();
        ImGui.SetNextItemWidth(-1);
        if (_targetFilter != session.TargetFilter) _targetFilter = session.TargetFilter;
        if (ImGui.InputTextWithHint("##TargetFilter", "Filter by name or model ID…", ref _targetFilter, 128))
            session.SetTargetFilter(_targetFilter);

        if (session.Source is { } source && session.TargetSlot != source.Slot)
        {
            Widgets.Icon(FontAwesomeIcon.ExclamationTriangle, Theme.Warning);
            ImGui.SameLine();
            Widgets.ColoredWrapped(Theme.Warning,
                $"The model keeps its {SlotInfo.DisplayLabelMap[source.Slot].ToLowerInvariant()} mesh: changing slots does not reshape it. " +
                "After previewing, remove the parts you don't want in the Mesh groups tab.");
        }

        // The chosen item stays visible even when the filter hides it.
        var rowHeight = ImGui.GetTextLineHeight() * 1.5f;
        if (session.TargetItem is { } chosen)
        {
            Widgets.GameIcon(chosen.Icon, rowHeight);
            ImGui.SameLine();
            ImGui.AlignTextToFramePadding();
            ImGui.TextColored(Theme.Success, chosen.Name);
            ImGui.SameLine();
            Widgets.Muted(chosen.ModelIdDisplay);
        }
        else
        {
            Widgets.Muted("No target selected.");
        }

        if (!session.GameData.ItemsReady)
        {
            Widgets.Spinner(Theme.Accent);
            ImGui.SameLine();
            Widgets.Muted("Loading the item list…");
            return;
        }

        var items = session.TargetCandidates;
        using var list = ImRaii.Child("##TargetList", new Vector2(-1, -1), true);
        if (!list.Success) return;
        if (items.Count == 0)
        {
            Widgets.Muted("No items match.");
            return;
        }

        var spacing = ImGui.GetStyle().ItemSpacing.Y;
        Widgets.Clipped(items.Count, rowHeight + spacing, i =>
        {
            var item     = items[i];
            var selected = ReferenceEquals(item, session.TargetItem);
            using var id = ImRaii.PushId(i);
            var start    = ImGui.GetCursorPos();
            if (ImGui.Selectable("##row", selected, ImGuiSelectableFlags.None, new Vector2(0, rowHeight)))
                session.SelectTarget(item);
            ImGui.SetCursorPos(start);
            Widgets.GameIcon(item.Icon, rowHeight);
            ImGui.SameLine();
            ImGui.SetCursorPosY(start.Y + (rowHeight - ImGui.GetTextLineHeight()) / 2);
            ImGui.TextUnformatted(item.Name);
            var idText = item.ModelIdDisplay;
            ImGui.SameLine(ImGui.GetContentRegionMax().X - ImGui.CalcTextSize(idText).X);
            Widgets.Muted(idText);
        });
    }

    private void DrawCustomizationTarget(DetectedItem source)
    {
        var labelWidth = 70f * Theme.Scale;

        var kinds = session.AllowedTargetKinds;
        if (kinds.Count > 1)
        {
            ImGui.AlignTextToFramePadding();
            ImGui.TextUnformatted("Type");
            ImGui.SameLine(labelWidth);
            foreach (var kind in kinds)
            {
                if (ImGui.RadioButton(CustomizationKinds.Get(kind).DisplayName, session.TargetCustomizationKind == kind))
                    session.SetCustomizationKind(kind);
                ImGui.SameLine();
            }
            ImGui.NewLine();
        }

        ImGui.AlignTextToFramePadding();
        ImGui.TextUnformatted("Race");
        ImGui.SameLine(labelWidth);
        ImGui.SetNextItemWidth(-1);
        using (var combo = ImRaii.Combo("##TargetRace", ConverterSession.RaceLabel(session.TargetRace)))
        {
            if (combo.Success)
                foreach (var race in session.AllowedTargetRaces)
                    if (ImGui.Selectable($"{ConverterSession.RaceLabel(race)}  (c{race:D4})", race == session.TargetRace))
                        session.SetTargetRace(race);
        }
        Widgets.Tooltip(source.Kind == AssetKind.Face
            ? "Races with this kind of asset. Lalafell convert only among Lalafell, and faces keep their gender."
            : "Races with this kind of asset. Lalafell convert only among Lalafell.");

        var kindName = session.TargetCustomizationKind == AssetKind.VieraEar
            ? "Ears"
            : CustomizationKinds.Get(session.TargetCustomizationKind).DisplayName;
        ImGui.AlignTextToFramePadding();
        ImGui.TextUnformatted(kindName);
        ImGui.SameLine(labelWidth);
        var options = session.GameData.TryGetCustomizationOptions(session.TargetCustomizationKind, session.TargetRace);
        if (options == null)
        {
            Widgets.Spinner(Theme.Accent);
            ImGui.SameLine();
            Widgets.Muted("Reading the options players can choose…");
            return;
        }

        if (options.Count == 0)
        {
            // Game data could not be read; fall back to free input.
            var value = session.TargetCustomizationId;
            ImGui.SetNextItemWidth(120f * Theme.Scale);
            if (ImGui.InputInt("##CustomizationId", ref value)) session.SetCustomizationId(value);
            Widgets.MutedWrapped("The options could not be read from the game; enter an ID manually.");
            return;
        }

        var chosen = options.FirstOrDefault(o => o.Id == session.TargetCustomizationId);
        Widgets.Badge(chosen?.Label ?? $"{session.TargetOptionLabel} (not available)", chosen != null ? Theme.Success : Theme.Danger);
        ImGui.SameLine();
        Widgets.Muted($"{options.Count} available");

        var note = source.Kind is AssetKind.Tail or AssetKind.VieraEar
            ? "Tails and Viera ears can convert into each other."
            : null;
        var noteHeight = note == null ? 0 : ImGui.GetTextLineHeightWithSpacing() * 2;

        using (var grid = ImRaii.Child("##IdGrid", new Vector2(-1, -noteHeight), true))
        {
            if (grid.Success)
            {
                var padding = ImGui.GetStyle().FramePadding.X * 2;
                var spacing = ImGui.GetStyle().ItemSpacing.X;
                var avail   = ImGui.GetContentRegionAvail().X;
                var cell    = Math.Min(avail, options.Max(o => ImGui.CalcTextSize(o.Label).X) + padding);
                var columns = Math.Max(1, (int)((avail + spacing) / (cell + spacing)));
                for (var i = 0; i < options.Count; i++)
                {
                    var option   = options[i];
                    var selected = option.Id == session.TargetCustomizationId;
                    if (i % columns != 0) ImGui.SameLine();
                    using var color = ImRaii.PushColor(ImGuiCol.Button, Theme.Accent.WithAlpha(0.6f), selected);
                    if (ImGui.Button(option.Label, new Vector2(cell, 0)))
                        session.SetCustomizationId(option.Id);
                    if (option.Clans != null) Widgets.Tooltip($"Only {option.Clans} players can choose this.");
                }
            }
        }

        if (note != null) Widgets.MutedWrapped(note);
    }
}
