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

    /// <summary>The mod <see cref="_sourceFilter"/> was typed for; another mod starts unfiltered.</summary>
    private string _sourceFilterMod = string.Empty;

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
        try { content(); }
        finally { Widgets.EndCard(); }
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
                Widgets.Muted("Scanning the mod");
            }
            else
                Widgets.MutedWrapped("No gear, facewear, hair, face, tail, Viera ear, skin or animation was found in this mod.");
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

        if (_sourceFilterMod != session.ModDirectory)
        {
            _sourceFilterMod = session.ModDirectory;
            _sourceFilter = string.Empty;
        }

        // The filter only applies while its box is there to see and clear.
        var filterable = items.Count > 6;
        if (filterable)
        {
            ImGui.SetNextItemWidth(-1);
            ImGui.InputTextWithHint("##SourceFilter", "Filter by name, type or ID", ref _sourceFilter, 128);
        }

        var filter = filterable ? _sourceFilter : string.Empty;
        var filtered = items
            .Select((item, index) => (Item: item, Index: index))
            .Where(e => filter.Length == 0 ||
                        e.Item.ItemName.Contains(filter, StringComparison.OrdinalIgnoreCase) ||
                        IdLabel(e.Item).Contains(filter, StringComparison.OrdinalIgnoreCase) ||
                        KindLabel(e.Item).Contains(filter, StringComparison.OrdinalIgnoreCase) ||
                        e.Item.Contents.Tags().Any(t => t.Name().Contains(filter, StringComparison.OrdinalIgnoreCase)))
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
            var idText  = $"{KindLabel(item)} · {IdLabel(item)}";
            var idWidth = ImGui.CalcTextSize(idText).X;
            var textY   = start.Y + (rowHeight - ImGui.GetTextLineHeight()) / 2;
            if (item.Contents.Tags().Any())
            {
                ImGui.SameLine(ImGui.GetContentRegionMax().X - idWidth - ImGui.GetStyle().ItemSpacing.X - ContentsTagsWidth(item));
                ImGui.SetCursorPosY(textY - Widgets.BadgePadding.Y);
                DrawContentsTags(item);
            }
            ImGui.SameLine(ImGui.GetContentRegionMax().X - idWidth);
            ImGui.SetCursorPosY(textY);
            Widgets.Muted(idText);
        });
    }

    /// <summary>The width <see cref="DrawContentsTags"/> takes, tags and the gaps between them.</summary>
    private static float ContentsTagsWidth(DetectedItem item)
    {
        var tags = item.Contents.Tags().ToList();
        return tags.Sum(t => Widgets.BadgeWidth(t.Name())) + Math.Max(0, tags.Count - 1) * ImGui.GetStyle().ItemSpacing.X;
    }

    /// <summary>
    /// What selecting the root actually works on, one tag per kind: a model, materials, textures
    /// (counting the ones its materials load), in any combination.
    /// </summary>
    private static void DrawContentsTags(DetectedItem item)
    {
        var first = true;
        foreach (var tag in item.Contents.Tags())
        {
            if (!first) ImGui.SameLine();
            first = false;
            var (color, tooltip) = tag switch
            {
                AssetContents.Model    => (Theme.Accent, "The mod replaces a model here; its materials and textures come along."),
                AssetContents.Material => (Theme.Info, "The mod replaces materials here."),
                _                      => (Theme.Success, "The mod replaces textures here, or textures its materials load."),
            };
            Widgets.Badge(tag.Name(), color);
            Widgets.Tooltip(tooltip);
        }
    }

    private static string KindLabel(DetectedItem item) => item switch
    {
        { Animation: { } animation } => animation.Kind switch
        {
            AnimationSourceKind.Idle  => "Idle",
            AnimationSourceKind.Emote => "Emote",
            AnimationSourceKind.Expression => "Expression",
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
            ImGui.Button(ConversionRow.Glyph(item.Kind).ToIconString() + "##kind", new Vector2(size));
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
            if (source.Contents.Tags().Any())
            {
                ImGui.SameLine();
                DrawContentsTags(source);
            }
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
        var hide = session.HideModdedTargets;
        if (Widgets.SectionTitle("To", FontAwesomeIcon.Bullseye, "Hide modded##HideModded", ref hide,
                "Leave out targets another mod already changes in your collection (the ones shown in red). " +
                "Whatever is selected or ticked always stays."))
            session.SetHideModdedTargets(hide);
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
        if (ImGui.InputTextWithHint("##TargetFilter", "Filter by name or model ID", ref _targetFilter, 128))
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
            Widgets.ModdedBadge(session.Modded.Item(chosen));
        }
        else
        {
            Widgets.Muted("No target selected.");
        }

        if (!session.GameData.ItemsReady)
        {
            Widgets.Spinner(Theme.Accent);
            ImGui.SameLine();
            Widgets.Muted("Loading the item list");
            return;
        }

        var items = session.ShownTargetCandidates;
        using var list = ImRaii.Child("##TargetList", new Vector2(-1, -1), true);
        if (!list.Success) return;
        if (items.Count == 0)
        {
            Widgets.Muted(session.TargetCandidates.Count > 0 ? "Every matching item is already modded." : "No items match.");
            return;
        }

        var spacing = ImGui.GetStyle().ItemSpacing.Y;
        Widgets.Clipped(items.Count, rowHeight + spacing, i =>
        {
            var item     = items[i];
            var selected = ReferenceEquals(item, session.TargetItem);
            var modded   = session.Modded.Item(item);
            using var id = ImRaii.PushId(i);
            var start    = ImGui.GetCursorPos();
            if (ImGui.Selectable("##row", selected, ImGuiSelectableFlags.None, new Vector2(0, rowHeight)))
                session.SelectTarget(item);
            Widgets.Tooltip(modded);
            ImGui.SetCursorPos(start);
            Widgets.GameIcon(item.Icon, rowHeight);
            ImGui.SameLine();
            ImGui.SetCursorPosY(start.Y + (rowHeight - ImGui.GetTextLineHeight()) / 2);
            if (modded != null) ImGui.TextColored(Theme.Danger, item.Name);
            else ImGui.TextUnformatted(item.Name);
            var idText = item.ModelIdDisplay;
            ImGui.SameLine(ImGui.GetContentRegionMax().X - ImGui.CalcTextSize(idText).X);
            Widgets.Muted(idText);
        });
    }

    /// <summary>
    /// Every allowed race/gender, in race-code order, each an expandable list of every ID it has
    /// (or a single toggle when there is only one, e.g. skins). The source's paths are simply
    /// added for every ticked (race, ID), pointing at the same files; the source itself is always
    /// kept, so it is shown ticked and locked.
    /// </summary>
    private void DrawTextureTargetList(DetectedItem source)
    {
        ImGui.TextUnformatted("Also add for");
        Widgets.Tooltip(session.TextureLayout == TextureFanOutLayout.NewGroups
            ? "Every race ticked below gets new option groups of its own holding its paths, so it can be " +
              "switched on or off separately. The source stays as it is."
            : "Everything ticked below gets the source's paths right beside them, in Default or whichever " +
              "options already hold them. The source stays as it is.");

        if (source.Contents.HasFlag(AssetContents.Material))
        {
            var retarget = session.RetargetMaterials;
            if (ImGui.Checkbox("Update texture paths inside materials", ref retarget)) session.SetRetargetMaterials(retarget);
            Widgets.Tooltip("On: every race or face gets its own copy of each material, pointing at its own " +
                            "textures (the mod's, added for it here, or the game's own where the mod has none). " +
                            "Off: every target shares the source's material as it is, source textures included.");
        }

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
            Widgets.Muted("Loading the options players can choose");
            return;
        }

        using var list = ImRaii.Child("##TextureTargets", new Vector2(-1, -1), true);
        if (!list.Success) return;
        var shown = false;
        foreach (var race in races)
            shown |= DrawTextureTargetRace(kind, race);
        if (!shown) Widgets.Muted("Every target is already modded.");
    }

    /// <summary>
    /// One race/gender row. A kind with at most one ID per race (skins, and any race/kind pair
    /// with a single option) is a single toggle; a kind with several (faces, mostly) expands into
    /// a checkbox per ID the players of that race can choose. A skin row names every race that
    /// wears that skin. Returns whether anything was drawn: a modded toggle may be hidden.
    /// </summary>
    private bool DrawTextureTargetRace(AssetKind kind, ushort race)
    {
        var options = kind == AssetKind.Body ? null : session.GameData.TryGetCustomizationOptions(kind, race);
        if (options is not { Count: > 1 })
        {
            // A skin keeps the source's body ID; there is one per race.
            var id = kind == AssetKind.Body && ushort.TryParse(session.Source?.ModelIdPadded, out var bodyId)
                ? bodyId
                : options is { Count: > 0 } list ? list[0].Id : (ushort)1;
            var label = kind == AssetKind.Body ? SkinLabel(race) : ConverterSession.RaceLabel(race);
            return DrawTextureTargetCheckbox(label, $"{race}", race, id);
        }

        var selected = session.TextureTargetCount(race);
        var modded = options.Count(o => !session.IsTextureSource(race, o.Id) && session.Modded.Customization(kind, race, o.Id) != null);
        var header = ConverterSession.RaceLabel(race) +
                     (options.Any(o => session.IsTextureSource(race, o.Id)) ? "  ·  source" : string.Empty) +
                     (selected > 0 ? $"  ·  {selected} selected" : string.Empty) +
                     (modded > 0 ? $"  ·  {modded} modded" : string.Empty);
        if (!ImGui.CollapsingHeader($"{header}###TextureRace{race}")) return true;

        // CollapsingHeader does not leave a lasting ID scope, so the race is folded into every
        // checkbox's own ID: two expanded races can otherwise share the same face ID.
        using var indent = ImRaii.PushIndent();
        var shown = false;
        foreach (var option in options)
            shown |= DrawTextureTargetCheckbox(option.Label, $"{race}_{option.Id}", race, option.Id,
                option.Clans != null ? $"Only {option.Clans} players can choose this." : null);
        if (!shown) Widgets.Muted("Every option is already modded.");
        return true;
    }

    /// <summary>
    /// A target toggle, red when another mod already changes that race or ID, and left out when
    /// those are hidden and it is not ticked; the source's own is always on and cannot be changed.
    /// Returns whether it was drawn.
    /// </summary>
    private bool DrawTextureTargetCheckbox(string label, string id, ushort race, ushort modelId, string? tooltip = null)
    {
        if (session.IsTextureSource(race, modelId))
        {
            var always = true;
            using (ImRaii.Disabled())
                ImGui.Checkbox($"{label} (source)##{id}", ref always);
            Widgets.Tooltip("The mod already has this; it is always kept as it is.");
            return true;
        }

        var modded = session.Modded.Customization(session.TargetCustomizationKind, race, modelId);
        var on = session.IsTextureTarget(race, modelId);
        if (!session.ShowsTarget(modded, on)) return false;
        using (ImRaii.PushColor(ImGuiCol.Text, Theme.Danger, modded != null))
        {
            if (ImGui.Checkbox($"{label}##{id}", ref on)) session.SetTextureTarget(race, modelId, on);
        }
        Widgets.Tooltip(tooltip, modded);
        return true;
    }

    /// <summary>"Midlander Female — also Elezen, Miqo'te": every race that wears this skin.</summary>
    private static string SkinLabel(ushort race)
    {
        var others = CustomizationPaths.SkinUsers(race).Where(r => r != race)
            .Select(r => CustomizationTargets.IsFemale(r) == CustomizationTargets.IsFemale(race)
                ? RaceNames.Race(r)
                : RaceNames.Name(r))
            .ToList();
        return others.Count == 0
            ? ConverterSession.RaceLabel(race)
            : $"{ConverterSession.RaceLabel(race)} — also {string.Join(", ", others)}";
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
            DrawTextureTargetList(source);
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
            Widgets.Muted("Reading the options players can choose");
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
        var shown = options.Where(o => session.ShowsTarget(
                session.Modded.Customization(session.TargetCustomizationKind, session.TargetRace, o.Id),
                o.Id == session.TargetCustomizationId))
            .ToList();
        Widgets.Badge(chosen?.Label ?? $"{session.TargetOptionLabel} (not available)", chosen != null ? Theme.Success : Theme.Danger);
        ImGui.SameLine();
        Widgets.Muted(shown.Count < options.Count
            ? $"{options.Count} available, {options.Count - shown.Count} modded hidden"
            : $"{options.Count} available");
        if (chosen != null)
            Widgets.ModdedBadge(session.Modded.Customization(session.TargetCustomizationKind, session.TargetRace, chosen.Id));

        var note = source.Kind is AssetKind.Tail or AssetKind.VieraEar
            ? "Tails and Viera ears can convert into each other." + XaelaNote(source)
            : null;
        var noteHeight = note == null ? 0 : ImGui.GetTextLineHeightWithSpacing() * 2;

        using (var grid = ImRaii.Child("##IdGrid", new Vector2(-1, -noteHeight), true))
        {
            if (grid.Success && shown.Count == 0) Widgets.Muted("Every option is already modded.");
            else if (grid.Success)
            {
                var padding = ImGui.GetStyle().FramePadding.X * 2;
                var spacing = ImGui.GetStyle().ItemSpacing.X;
                var avail   = ImGui.GetContentRegionAvail().X;
                var cell    = Math.Min(avail, options.Max(o => ImGui.CalcTextSize(o.Label).X) + padding);
                var columns = Math.Max(1, (int)((avail + spacing) / (cell + spacing)));
                for (var i = 0; i < shown.Count; i++)
                {
                    var option   = shown[i];
                    var selected = option.Id == session.TargetCustomizationId;
                    var modded   = session.Modded.Customization(session.TargetCustomizationKind, session.TargetRace, option.Id);
                    if (i % columns != 0) ImGui.SameLine();
                    using (ImRaii.PushColor(ImGuiCol.Button, Theme.Accent.WithAlpha(0.6f), selected)
                               .Push(ImGuiCol.Text, Theme.Danger, modded != null))
                    {
                        if (ImGui.Button(option.Label, new Vector2(cell, 0)))
                            session.SetCustomizationId(option.Id);
                    }
                    Widgets.Tooltip(option.Clans != null ? $"Only {option.Clans} players can choose this." : null, modded);
                }
            }
        }

        if (note != null) Widgets.MutedWrapped(note);
    }

    /// <summary>What happens to the Xaela material root of an Au Ra tail on either side, or nothing.</summary>
    private string XaelaNote(DetectedItem source)
    {
        if (source.GenderRace is not { } sourceRace || !ushort.TryParse(source.ModelIdPadded, out var sourceId)) return string.Empty;
        var sourceXaela = CustomizationPaths.XaelaMaterialEndpoint(new CustomizationPathEndpoint(source.Kind, sourceRace, sourceId));
        var targetXaela = CustomizationPaths.XaelaMaterialEndpoint(new CustomizationPathEndpoint(
            session.TargetCustomizationKind, session.TargetRace, (ushort)session.TargetCustomizationId));
        return (sourceXaela, targetXaela) switch
        {
            ({ } from, { } to) => $" Its Xaela material (tail {from.ModelId}) moves to tail {to.ModelId}.",
            ({ } from, null)   => $" Its Xaela material (tail {from.ModelId}) is left out: the target has none.",
            (null, { } to)     => $" Xaela get the same material, under tail {to.ModelId}.",
            _                  => string.Empty,
        };
    }
}
