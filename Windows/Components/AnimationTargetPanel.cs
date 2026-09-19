using System;
using System.Linq;
using System.Numerics;
using AdvancedPenumbraModConverter.Core;
using AdvancedPenumbraModConverter.Services;
using AdvancedPenumbraModConverter.Session;
using AdvancedPenumbraModConverter.Windows.Ui;
using Dalamud.Bindings.ImGui;
using Dalamud.Interface.Utility.Raii;

namespace AdvancedPenumbraModConverter.Windows.Components;

/// <summary>The "To" card for animations: swap an idle or emote, or retarget to other races.</summary>
internal sealed class AnimationTargetPanel(ConverterSession session)
{
    private string _emoteFilter = string.Empty;
    private string _groupName = string.Empty;

    public void Draw(AnimationSource source)
    {
        var swap = session.AnimationOperation == AnimationOperation.Swap;
        using (ImRaii.Disabled(!session.CanSwapAnimation))
        {
            if (ImGui.RadioButton(source.Kind == AnimationSourceKind.Emote ? "Swap to another emote" : "Swap to another slot", swap))
                session.SetAnimationOperation(AnimationOperation.Swap);
        }
        Widgets.Tooltip(session.CanSwapAnimation
            ? "Play this animation from another slot or emote, for the same race."
            : "Only idles and emotes can be swapped.");
        ImGui.SameLine(0, 20f * Theme.Scale);
        if (ImGui.RadioButton("Retarget to other races", !swap))
            session.SetAnimationOperation(AnimationOperation.Retarget);
        Widgets.Tooltip("Rebuild the animation for other races' skeletons, rescaled to their proportions.");
        ImGui.Spacing();

        if (!swap) DrawRetarget(source);
        else if (source.Kind == AnimationSourceKind.Idle) DrawIdleSwap(source);
        else DrawEmoteSwap(source);
    }

    // ── Idle slots ──────────────────────────────────────────────────────────

    private void DrawIdleSwap(AnimationSource source)
    {
        var slots = session.AnimationSlots;
        if (slots.Count == 0)
        {
            Widgets.MutedWrapped("The game's slots for this idle could not be found.");
            return;
        }

        var group = session.AnimationAsGroup;
        if (ImGui.RadioButton("Replace one slot", !group)) session.SetAnimationAsGroup(false);
        Widgets.Tooltip("Move the animation into the chosen slot.");
        ImGui.SameLine(0, 20f * Theme.Scale);
        if (ImGui.RadioButton("Option group with a variant per slot", group)) session.SetAnimationAsGroup(true);
        Widgets.Tooltip("Create a Penumbra option group whose options place the animation in each chosen slot, " +
                        "so the slot can be picked in Penumbra at any time.");

        if (group)
        {
            if (_groupName != session.AnimationGroupName) _groupName = session.AnimationGroupName;
            ImGui.SetNextItemWidth(-1);
            if (ImGui.InputTextWithHint("##GroupName", "Name of the option group", ref _groupName, 128))
                session.SetAnimationGroupName(_groupName);
            if (ImGui.SmallButton("All")) session.SetAnimationGroupSlots(slots.Select(s => s.Index));
            ImGui.SameLine();
            if (ImGui.SmallButton("None")) session.SetAnimationGroupSlots([]);
            ImGui.SameLine();
            Widgets.Muted($"{session.AnimationGroupSlots.Count} of {slots.Count} slots");
        }
        else if (session.OutputMode == ConversionOutputMode.InPlace)
        {
            var keep = session.AnimationKeepOriginal;
            if (ImGui.Checkbox("Keep it in the current slot too", ref keep)) session.SetAnimationKeepOriginal(keep);
            Widgets.Tooltip("Copy instead of move: both slots play this animation.");
        }

        using var grid = ImRaii.Child("##Slots", new Vector2(-1, -1), true);
        if (!grid.Success) return;
        foreach (var slot in slots)
        {
            using var id = ImRaii.PushId(slot.Index);
            var label = slot.Index == source.SlotIndex ? $"{slot.Label} (current)" : slot.Label;
            if (group)
            {
                var included = session.AnimationGroupSlots.Contains(slot.Index);
                if (ImGui.Checkbox(label, ref included)) session.SetAnimationGroupSlot(slot.Index, included);
            }
            else
            {
                using var disabled = ImRaii.Disabled(slot.Index == source.SlotIndex);
                if (ImGui.Selectable(label, slot.Index == session.AnimationTargetSlot)) session.SetAnimationTargetSlot(slot.Index);
            }
            if (slot.StartKey == null) Widgets.Tooltip($"{slot.LoopKey} (no start animation)");
            else Widgets.Tooltip($"{slot.LoopKey} and {slot.StartKey}");
        }
    }

    // ── Emotes ──────────────────────────────────────────────────────────────

    private void DrawEmoteSwap(AnimationSource source)
    {
        var emotes = session.AnimationEmotes;
        if (emotes == null)
        {
            Widgets.Spinner(Theme.Accent);
            ImGui.SameLine();
            Widgets.Muted("Reading the emote list…");
            return;
        }

        ImGui.SetNextItemWidth(-1);
        ImGui.InputTextWithHint("##EmoteFilter", "Filter emotes…", ref _emoteFilter, 64);
        if (emotes.FirstOrDefault(e => e.Id == session.AnimationTargetEmote) is { } chosen)
        {
            ImGui.TextColored(Theme.Success, $"/{chosen.Name}");
            ImGui.SameLine();
            Widgets.Muted(string.Join(", ", chosen.Timelines.Select(t => t.Label)));
        }
        else Widgets.Muted("No emote selected.");

        var filter = _emoteFilter.Trim();
        var shown = emotes.Where(e => e.Id != source.EmoteId &&
                                      (filter.Length == 0 || e.Name.Contains(filter, StringComparison.OrdinalIgnoreCase) ||
                                       e.Timelines.Any(t => t.Key.Contains(filter, StringComparison.OrdinalIgnoreCase))))
            .ToList();
        using var list = ImRaii.Child("##Emotes", new Vector2(-1, -1), true);
        if (!list.Success) return;
        var rowHeight = ImGui.GetTextLineHeight() * 1.5f;
        Widgets.Clipped(shown.Count, rowHeight + ImGui.GetStyle().ItemSpacing.Y, i =>
        {
            var emote = shown[i];
            using var id = ImRaii.PushId((int)emote.Id);
            var start = ImGui.GetCursorPos();
            if (ImGui.Selectable("##row", emote.Id == session.AnimationTargetEmote, ImGuiSelectableFlags.None, new Vector2(0, rowHeight)))
                session.SetAnimationTargetEmote(emote.Id);
            Widgets.Tooltip(string.Join("\n", emote.Timelines.Select(t => $"{t.Label}: {t.Key}")));
            ImGui.SetCursorPos(start);
            Widgets.GameIcon(emote.Icon, rowHeight);
            ImGui.SameLine();
            ImGui.SetCursorPosY(start.Y + (rowHeight - ImGui.GetTextLineHeight()) / 2);
            ImGui.TextUnformatted($"/{emote.Name}");
        });
    }

    // ── Races ───────────────────────────────────────────────────────────────

    private void DrawRetarget(AnimationSource source)
    {
        ImGui.AlignTextToFramePadding();
        ImGui.TextUnformatted("From");
        ImGui.SameLine(60f * Theme.Scale);
        ImGui.SetNextItemWidth(-1);
        using (var combo = ImRaii.Combo("##SourceRace", ConverterSession.RaceLabel(session.AnimationSourceRace)))
        {
            if (combo.Success)
                foreach (var race in source.Races)
                    if (ImGui.Selectable($"{ConverterSession.RaceLabel(race)}  (c{race:D4})", race == session.AnimationSourceRace))
                        session.SetAnimationSourceRace(race);
        }
        Widgets.Tooltip("The race whose files are rebuilt. Only races this mod provides the animation for are listed.");

        Widgets.MutedWrapped("To: races without their own file play their parent race's animation (the game's race tree), " +
                             "so the preview lists which other races each new file also covers.");
        using var grid = ImRaii.Child("##Races", new Vector2(-1, -1), true);
        if (!grid.Success) return;
        using var table = ImRaii.Table("##RaceTable", 2, ImGuiTableFlags.SizingStretchSame);
        if (!table.Success) return;
        foreach (var race in GenderRaces.Playable)
        {
            ImGui.TableNextColumn();
            using var id = ImRaii.PushId(race);
            var included = session.AnimationTargetRaces.Contains(race);
            var isSource = race == session.AnimationSourceRace;
            var provided = source.Races.Contains(race);
            using (ImRaii.Disabled(isSource))
            {
                if (ImGui.Checkbox(ConverterSession.RaceLabel(race), ref included)) session.SetAnimationTargetRace(race, included);
            }
            Widgets.Tooltip(isSource ? "This is the source race."
                : provided ? $"c{race:D4}. The mod already has this animation for this race; it would be replaced."
                : $"c{race:D4}");
            if (provided && !isSource)
            {
                ImGui.SameLine();
                Widgets.Badge("in mod", Theme.Warning);
            }
        }
    }
}
