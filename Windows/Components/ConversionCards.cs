using System;
using System.Linq;
using System.Numerics;
using UniversalModConverter.Core;
using UniversalModConverter.Models;
using UniversalModConverter.Services;
using UniversalModConverter.Session;
using UniversalModConverter.Windows.Ui;
using Dalamud.Bindings.ImGui;
using Dalamud.Interface;
using Dalamud.Interface.Utility.Raii;

namespace UniversalModConverter.Windows.Components;

/// <summary>The "From" and "To" cards: source root selection and target picker.</summary>
internal sealed class ConversionCards(ConverterSession session)
{
    private const float WideLayoutWidth = 640f;

    private readonly AnimationTargetPanel _animation = new(session);

    private string _targetFilter = string.Empty;
    private string _sourceFilter = string.Empty;

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
                Widgets.MutedWrapped("No gear, facewear, hair, face, tail, Viera-ear or animation was found in this mod.");
            return;
        }

        if (session.DetectedItems.Count == 1)
        {
            DrawItemSummary(session.DetectedItems[0]);
            DrawSourceDetails(session.DetectedItems[0]);
            return;
        }

        DrawSourceList();
        if (session.Source is { } selected)
        {
            ImGui.Spacing();
            DrawSourceDetails(selected);
        }
        else
            Widgets.MutedWrapped("Pick the item this mod replaces that you want to move.");
    }

    /// <summary>
    /// Every convertible root in the mod, shown the way the selected one is: icon, name and
    /// model ID. A dropdown hid what the mod actually contains behind a click.
    /// </summary>
    private void DrawSourceList()
    {
        var items = session.DetectedItems;
        Widgets.Muted($"{items.Count} convertible roots in this mod:");

        if (items.Count > 6)
        {
            ImGui.SetNextItemWidth(-1);
            ImGui.InputTextWithHint("##SourceFilter", "Filter by name, type or ID…", ref _sourceFilter, 128);
        }

        var filter = _sourceFilter;
        var filtered = items
            .Select((item, index) => (Item: item, Index: index))
            .Where(e => filter.Length == 0 ||
                        e.Item.ItemName.Contains(filter, StringComparison.OrdinalIgnoreCase) ||
                        IdLabel(e.Item).Contains(filter, StringComparison.OrdinalIgnoreCase) ||
                        KindLabel(e.Item).Contains(filter, StringComparison.OrdinalIgnoreCase))
            .ToList();

        // Leave room for the detail lines below without letting the list collapse.
        var rowHeight = ImGui.GetTextLineHeight() * 1.5f;
        var spacing   = ImGui.GetStyle().ItemSpacing.Y;
        var available = ImGui.GetContentRegionAvail().Y - ImGui.GetTextLineHeightWithSpacing() * 3;
        var height    = Math.Clamp(filtered.Count * (rowHeight + spacing) + ImGui.GetStyle().FramePadding.Y * 2,
            rowHeight * 2, Math.Max(rowHeight * 3, available));

        using var list = ImRaii.Child("##SourceList", new Vector2(-1, height), true);
        if (!list.Success) return;
        if (filtered.Count == 0)
        {
            Widgets.Muted("No root matches.");
            return;
        }

        Widgets.Clipped(filtered.Count, rowHeight + spacing, row =>
        {
            var (item, index) = filtered[row];
            using var id = ImRaii.PushId(index);
            var start = ImGui.GetCursorPos();
            if (ImGui.Selectable("##row", index == session.SourceIndex, ImGuiSelectableFlags.None, new Vector2(0, rowHeight)))
                session.SelectSource(index);
            ImGui.SetCursorPos(start);
            DrawKindIcon(item, rowHeight);
            ImGui.SameLine();
            ImGui.SetCursorPosY(start.Y + (rowHeight - ImGui.GetTextLineHeight()) / 2);
            ImGui.TextUnformatted(item.ItemName);
            var idText = $"{KindLabel(item)} · {IdLabel(item)}";
            ImGui.SameLine(ImGui.GetContentRegionMax().X - ImGui.CalcTextSize(idText).X);
            Widgets.Muted(idText);
        });
    }

    private static string KindLabel(DetectedItem item) => item switch
    {
        { Animation: { } animation } => animation.Kind switch
        {
            AnimationSourceKind.Idle  => "Idle",
            AnimationSourceKind.Emote => "Emote",
            _                         => "Animation",
        },
        { IsCustomization: true } => CustomizationKinds.Get(item.Kind).DisplayName,
        _ => SlotInfo.DisplayLabelMap[item.Slot],
    };

    private static string IdLabel(DetectedItem item) => item switch
    {
        { Animation: { } animation } => animation.Races.Length == 1
            ? ConverterSession.RaceLabel(animation.Races[0])
            : $"{animation.Races.Length} races",
        { IsCustomization: true } => ConverterSession.RaceLabel(item.GenderRace ?? 0),
        _ => $"{(item.IsAccessory ? 'a' : 'e')}{item.ModelIdDisplay}",
    };

    /// <summary>The game icon, or a glyph standing in for a root the game has no icon for.</summary>
    private static void DrawKindIcon(DetectedItem item, float size)
    {
        if (!item.IsCustomization && (item.Animation == null || item.Icon != 0))
        {
            Widgets.GameIcon(item.Icon, size);
            return;
        }

        using (ImRaii.PushColor(ImGuiCol.Text, Theme.Muted))
        using (ImRaii.PushFont(UiBuilder.IconFont))
        {
            var glyph = item.Kind switch
            {
                AssetKind.Hair      => FontAwesomeIcon.Cut,
                AssetKind.Face      => FontAwesomeIcon.UserCircle,
                AssetKind.Animation => FontAwesomeIcon.Running,
                _                   => FontAwesomeIcon.Paw,
            };
            ImGui.Button(glyph.ToIconString() + "##kind", new Vector2(size));
        }
    }

    private static void DrawItemSummary(DetectedItem source)
    {
        DrawKindIcon(source, ImGui.GetTextLineHeight() * 2.6f);

        ImGui.SameLine();
        using (ImRaii.Group())
        {
            ImGui.TextWrapped(source.ItemName);
            Widgets.Badge(KindLabel(source), Theme.Accent);
            ImGui.SameLine();
            Widgets.Badge(IdLabel(source), Theme.Info);
        }
    }

    private static void DrawSourceDetails(DetectedItem source)
    {
        if (source.Animation is { } animation)
        {
            Widgets.MutedWrapped(string.Join("\n", animation.Locations));
            Widgets.MutedWrapped("Races in the mod: " +
                                 string.Join(", ", animation.Races.Select(ConverterSession.RaceLabel)));
        }

        if (source.IsAmbiguous)
            Widgets.ColoredWrapped(Theme.Warning,
                "Several game items share this model; the conversion applies to all of them.");
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

        if (source.Animation is { } animation) _animation.Draw(animation);
        else if (source.IsCustomization) DrawCustomizationTarget(source);
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

        if (session.Source is { } source &&
            GearSlots.CrossSlotNote(SlotInfo.ToGearSlot(source.Slot), SlotInfo.ToGearSlot(session.TargetSlot)) is { } note)
        {
            Widgets.Icon(FontAwesomeIcon.InfoCircle, Theme.Info);
            ImGui.SameLine();
            Widgets.ColoredWrapped(Theme.Info, note);
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

    /// <summary>
    /// Every allowed race/gender, in race-code order, each an expandable list of every ID it has
    /// (or a single toggle when there is only one, e.g. skins). A texture has no paths inside it,
    /// so the very same file can serve every ticked (race, ID); each becomes its own toggleable
    /// option in a new group built for its race, the same way an idle animation becomes one option
    /// per slot. The source's own race starts ticked, since that is what the mod already has.
    /// </summary>
    private void DrawTextureTargetList()
    {
        ImGui.TextUnformatted("Convert to");
        Widgets.Tooltip(session.OutputMode == ConversionOutputMode.InPlace
            ? "Everything ticked below is added straight into whatever option already has the source's own " +
              "paths, right alongside them. Untick the source's own race to remove it from there instead of " +
              "leaving it."
            : "Everything ticked below becomes its own toggleable option in a new group built for its race. " +
              "Untick the source's own race to move it away instead of keeping it.");

        var kind  = session.TargetCustomizationKind;
        var races = session.AllowedTargetRaces.OrderBy(r => r).ToList();

        if (races.Count == 0)
        {
            Widgets.MutedWrapped("No race accepts this kind.");
            return;
        }

        if (kind != AssetKind.Body && races.Any(r => session.GameData.TryGetCustomizationOptions(kind, r) == null))
        {
            Widgets.Spinner(Theme.Accent);
            ImGui.SameLine();
            Widgets.Muted("Loading the options players can choose…");
            return;
        }

        using var list = ImRaii.Child("##TextureTargets", new Vector2(-1, -1), true);
        if (list.Success)
            foreach (var race in races)
                DrawTextureTargetRace(kind, race);
    }

    /// <summary>
    /// One race/gender row. A kind with at most one ID per race (skins, and any race/kind pair
    /// with a single option) is a single toggle; a kind with several (faces, mostly) expands into
    /// a checkbox per ID the players of that race can choose.
    /// </summary>
    private void DrawTextureTargetRace(AssetKind kind, ushort race)
    {
        var options = kind == AssetKind.Body ? null : session.GameData.TryGetCustomizationOptions(kind, race);
        if (options is not { Count: > 1 })
        {
            var id = options is { Count: > 0 } list ? list[0].Id : (ushort)1;
            var on = session.IsTextureTarget(race, id);
            if (ImGui.Checkbox(ConverterSession.RaceLabel(race), ref on)) session.SetTextureTarget(race, id, on);
            return;
        }

        var selected = session.TextureTargetCount(race);
        var header   = selected > 0 ? $"{ConverterSession.RaceLabel(race)}  ·  {selected} selected" : ConverterSession.RaceLabel(race);
        if (!ImGui.CollapsingHeader($"{header}###TextureRace{race}")) return;

        // CollapsingHeader does not leave a lasting ID scope, so the race is folded into every
        // checkbox's own ID: two expanded races can otherwise share the same face ID.
        using var indent = ImRaii.PushIndent();
        foreach (var option in options)
        {
            var on = session.IsTextureTarget(race, option.Id);
            if (ImGui.Checkbox($"{option.Label}##{race}_{option.Id}", ref on)) session.SetTextureTarget(race, option.Id, on);
            if (option.Clans != null) Widgets.Tooltip($"Only {option.Clans} players can choose this.");
        }
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

        if (session.CanFanOutTextures)
        {
            DrawTextureTargetList();
            return;
        }

        ImGui.AlignTextToFramePadding();
        ImGui.TextUnformatted("Race");
        ImGui.SameLine(labelWidth);
        ImGui.SetNextItemWidth(-1);
        using (var combo = ImRaii.Combo("##TargetRace", ConverterSession.RaceLabel(session.TargetRace)))
        {
            if (combo.Success)
                foreach (var race in session.AllowedTargetRaces)
                    if (ImGui.Selectable(RaceNames.Describe(race), race == session.TargetRace))
                        session.SetTargetRace(race);
        }
        Widgets.Tooltip(source.Kind is AssetKind.Face or AssetKind.Body
            ? "Races with this kind of asset. Lalafell convert only among Lalafell, and faces and skins keep their gender."
            : "Races with this kind of asset. Lalafell convert only among Lalafell.");

        // A skin has exactly one ID per race; there is nothing for the player to pick.
        if (session.TargetCustomizationKind == AssetKind.Body) return;

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
