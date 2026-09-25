using System;
using System.Linq;
using System.Numerics;
using UniversalModConverter.Core;
using UniversalModConverter.Services;
using UniversalModConverter.Session;
using UniversalModConverter.Windows.Ui;
using Dalamud.Bindings.ImGui;
using Dalamud.Interface.Utility.Raii;

namespace UniversalModConverter.Windows.Components;

/// <summary>The "To" card for animations: swap an idle or emote, or retarget to other races.</summary>
internal sealed class AnimationTargetPanel(ConverterSession session)
{
    private string _emoteFilter = string.Empty;
    private string _groupName = string.Empty;
    private string _expressionFilter = string.Empty;

    public void Draw(AnimationSource source)
    {
        var operation = session.AnimationOperation;
        var facial = source.Kind == AnimationSourceKind.Expression;
        using (ImRaii.Disabled(!session.CanSwapAnimation))
        {
            var label = source.Kind switch
            {
                AnimationSourceKind.Emote      => "Swap to another emote",
                AnimationSourceKind.Expression => "Swap to another expression",
                _                              => "Swap to another slot",
            };
            if (ImGui.RadioButton(label, operation == AnimationOperation.Swap))
                session.SetAnimationOperation(AnimationOperation.Swap);
        }
        Widgets.Tooltip(!session.CanSwapAnimation ? "Only idles, emotes and expressions can be swapped."
            : facial ? "Play this face for another expression of the emote list, with its pace and options."
            : "Play this animation from another slot or emote, for the same race.");
        using (ImRaii.Disabled(facial))
        {
            ImGui.SameLine(0, 20f * Theme.Scale);
            if (ImGui.RadioButton("Retarget to other races", operation == AnimationOperation.Retarget))
                session.SetAnimationOperation(AnimationOperation.Retarget);
            Widgets.Tooltip(facial
                ? "Faces play on each race's own face skeleton, so there is nothing to retarget."
                : "Rebuild the animation for other races' skeletons, rescaled to their proportions.");
            ImGui.SameLine(0, 20f * Theme.Scale);
            if (ImGui.RadioButton("Only add an expression", operation == AnimationOperation.Expression))
                session.SetAnimationOperation(AnimationOperation.Expression);
            Widgets.Tooltip(facial
                ? "This already is a facial expression."
                : "Keep the animation where it is and give it a facial expression.");
        }
        ImGui.Spacing();

        if (facial)
        {
            DrawExpressionSwap(source);
            return;
        }

        if (operation == AnimationOperation.Expression)
        {
            DrawExpressionPicker();
            return;
        }

        // Above the operation's own controls, whose lists fill the rest of the card.
        var attach = session.AttachExpression;
        if (ImGui.Checkbox("Also attach a facial expression", ref attach)) session.SetAttachExpression(attach);
        Widgets.Tooltip("The converted animation plays with the face of a game emote or of another mod.");
        if (attach) DrawExpressionPicker();
        ImGui.Spacing();

        if (operation == AnimationOperation.Retarget) DrawRetarget(source);
        else if (source.Kind == AnimationSourceKind.Idle) DrawIdleSwap(source);
        else DrawEmoteSwap(source);
    }

    // ── Expressions ─────────────────────────────────────────────────────────

    /// <summary>
    /// Where the face comes from. Compact on purpose: it sits above the swap and retarget
    /// controls, whose own lists need the height.
    /// </summary>
    private void DrawExpressionPicker()
    {
        if (ImGui.RadioButton("From the game", session.ExpressionSource == ExpressionSourceKind.Vanilla))
            session.SetExpressionSource(ExpressionSourceKind.Vanilla);
        ImGui.SameLine(0, 20f * Theme.Scale);
        if (ImGui.RadioButton("From another mod", session.ExpressionSource == ExpressionSourceKind.Mod))
            session.SetExpressionSource(ExpressionSourceKind.Mod);

        if (session.ExpressionSource == ExpressionSourceKind.Vanilla) DrawVanillaExpression();
        else DrawModExpression();
    }

    private void DrawVanillaExpression()
    {
        var expressions = session.AnimationExpressions;
        if (expressions == null)
        {
            Widgets.Spinner(Theme.Accent);
            ImGui.SameLine();
            Widgets.Muted("Reading the expression list…");
            return;
        }

        var chosen = expressions.FirstOrDefault(e => e.Id == session.ExpressionEmote);
        ImGui.SetNextItemWidth(-1);
        using var combo = ImRaii.Combo("##ExpressionEmote", chosen == null ? "Choose an expression…" : $"/{chosen.Name}",
            ImGuiComboFlags.HeightLarge);
        Widgets.Tooltip("The expressions of the game's emote list. Each race gets the face the game plays for it.");
        if (!combo.Success) return;

        ImGui.SetNextItemWidth(-1);
        ImGui.InputTextWithHint("##ExpressionFilter", "Filter expressions…", ref _expressionFilter, 64);
        var filter = _expressionFilter.Trim();
        foreach (var expression in expressions.Where(e => filter.Length == 0 || e.Name.Contains(filter, StringComparison.OrdinalIgnoreCase)))
        {
            using var id = ImRaii.PushId((int)expression.Id);
            if (ImGui.Selectable($"/{expression.Name}", expression.Id == session.ExpressionEmote))
                session.SetExpressionEmote(expression.Id);
        }
    }

    private void DrawModExpression()
    {
        var mods = session.Mods;
        var current = mods.FirstOrDefault(m => string.Equals(m.Directory, session.ExpressionModDirectory,
            StringComparison.OrdinalIgnoreCase));
        ImGui.SetNextItemWidth(-1);
        using (var combo = ImRaii.Combo("##ExpressionMod", current?.Name ?? "Choose a mod…", ImGuiComboFlags.HeightLarge))
        {
            if (combo.Success)
            {
                ImGui.SetNextItemWidth(-1);
                ImGui.InputTextWithHint("##ExpressionModFilter", "Filter mods…", ref _expressionFilter, 64);
                var filter = _expressionFilter.Trim();
                foreach (var mod in mods.Where(m => filter.Length == 0 || m.Name.Contains(filter, StringComparison.OrdinalIgnoreCase)))
                    if (ImGui.Selectable(mod.Name, ReferenceEquals(mod, current)))
                        session.SetExpressionMod(mod.Directory);
            }
        }
        if (mods.Count == 0) Widgets.MutedWrapped("Penumbra's mod list is not available.");
        if (session.ExpressionModDirectory == null) return;

        var expressions = session.ModExpressions;
        if (expressions == null)
        {
            Widgets.Spinner(Theme.Accent);
            ImGui.SameLine();
            Widgets.Muted("Looking for facial animations in that mod…");
            return;
        }
        if (expressions.Count == 0)
        {
            Widgets.MutedWrapped("That mod has no facial expression: no face animations, and no animation that plays one.");
            return;
        }

        ImGui.SetNextItemWidth(-1);
        using var files = ImRaii.Combo("##ExpressionFile", session.ExpressionModFile?.Label ?? "Choose an expression…");
        if (!files.Success) return;
        foreach (var expression in expressions)
            if (ImGui.Selectable(expression.Label, expression == session.ExpressionModFile))
                session.SetExpressionModFile(expression);
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
                        "so the slot can be picked in Penumbra at any time. Its first option, \"-\", switches it off.");

        if (group)
        {
            if (_groupName != session.AnimationGroupName) _groupName = session.AnimationGroupName;
            ImGui.SetNextItemWidth(-1);
            if (ImGui.InputTextWithHint("##GroupName", "Name of the option group", ref _groupName, 128))
                session.SetAnimationGroupName(_groupName);
            if (session.NeedsAnimationSourceContainer) DrawSourceContainer(source);
            if (ImGui.SmallButton("All")) session.SetAnimationGroupSlots(slots.Select(s => s.Index));
            ImGui.SameLine();
            if (ImGui.SmallButton("None")) session.SetAnimationGroupSlots([]);
            ImGui.SameLine();
            Widgets.Muted($"{session.AnimationGroupSlots.Count} of {slots.Count} slots");
        }
        else if (session.OutputMode == ConversionOutputMode.InPlace)  // Implied by AddToMod.
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

    /// <summary>
    /// The version the group uses, when several options of the mod have their own: one group can
    /// hold only one file per slot. Nothing is preselected, so the choice is always deliberate.
    /// </summary>
    private void DrawSourceContainer(AnimationSource source)
    {
        var chosen = source.Providers.FirstOrDefault(p => p.Address == session.AnimationSourceContainer);
        ImGui.AlignTextToFramePadding();
        Widgets.ColoredWrapped(Theme.Warning, "Take it from");
        ImGui.SameLine();
        ImGui.SetNextItemWidth(-1);
        using (var combo = ImRaii.Combo("##SourceContainer", chosen?.Label ?? "Choose an option…", ImGuiComboFlags.HeightLarge))
        {
            if (combo.Success)
                foreach (var provider in source.Providers)
                {
                    using var id = ImRaii.PushId($"{provider.Address.Group}/{provider.Address.Index}");
                    if (ImGui.Selectable(provider.Label, provider == chosen)) session.SetAnimationSourceContainer(provider.Address);
                }
        }
        Widgets.Tooltip("Several options of this mod have their own version of this idle, and an option group can " +
                        "hold only one of them per slot. Choose the one to use; the options themselves stay as they are.");
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

    // ── Facial expressions ──────────────────────────────────────────────────

    /// <summary>The expressions of the emote list the face can move to.</summary>
    private void DrawExpressionSwap(AnimationSource source)
    {
        var expressions = session.AnimationExpressions;
        if (expressions == null)
        {
            Widgets.Spinner(Theme.Accent);
            ImGui.SameLine();
            Widgets.Muted("Reading the expression list…");
            return;
        }

        ImGui.SetNextItemWidth(-1);
        ImGui.InputTextWithHint("##ExpressionSwapFilter", "Filter expressions…", ref _emoteFilter, 64);
        if (expressions.FirstOrDefault(e => e.Id == session.AnimationTargetEmote) is { } chosen)
            ImGui.TextColored(Theme.Success, $"/{chosen.Name}");
        else Widgets.Muted("No expression selected.");

        var filter = _emoteFilter.Trim();
        var shown = expressions.Where(e => e.Id != source.EmoteId &&
                                           (filter.Length == 0 || e.Name.Contains(filter, StringComparison.OrdinalIgnoreCase) ||
                                            e.Pose.Contains(filter, StringComparison.OrdinalIgnoreCase)))
            .ToList();
        using var list = ImRaii.Child("##Expressions", new Vector2(-1, -1), true);
        if (!list.Success) return;
        var rowHeight = ImGui.GetTextLineHeight() * 1.5f;
        Widgets.Clipped(shown.Count, rowHeight + ImGui.GetStyle().ItemSpacing.Y, i =>
        {
            var expression = shown[i];
            using var id = ImRaii.PushId((int)expression.Id);
            var start = ImGui.GetCursorPos();
            using (ImRaii.Disabled(!expression.OwnPack))
            {
                if (ImGui.Selectable("##row", expression.Id == session.AnimationTargetEmote, ImGuiSelectableFlags.None,
                        new Vector2(0, rowHeight)))
                    session.SetAnimationTargetEmote(expression.Id);
            }
            Widgets.Tooltip(expression.OwnPack
                ? $"facial/pose/{expression.Pose}"
                : "The game keeps this face in the shared face pack with every other face, so it cannot be replaced on its own.");
            ImGui.SetCursorPos(start);
            Widgets.GameIcon(expression.Icon, rowHeight);
            ImGui.SameLine();
            ImGui.SetCursorPosY(start.Y + (rowHeight - ImGui.GetTextLineHeight()) / 2);
            if (expression.OwnPack) ImGui.TextUnformatted($"/{expression.Name}");
            else Widgets.Muted($"/{expression.Name}");
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
                    if (ImGui.Selectable(RaceNames.Describe(race), race == session.AnimationSourceRace))
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
                : provided
                    ? $"{RaceNames.Describe(race)}. The mod already has this animation for this race; it would be replaced."
                    : RaceNames.Describe(race));
            if (provided && !isSource)
            {
                ImGui.SameLine();
                Widgets.Badge("in mod", Theme.Warning);
            }
        }
    }
}
