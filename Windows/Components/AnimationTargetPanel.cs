using System;
using System.Collections.Generic;
using System.Linq;
using System.Numerics;
using UniversalModConverter.Core;
using UniversalModConverter.Services;
using UniversalModConverter.Session;
using UniversalModConverter.Windows.Ui;
using Dalamud.Bindings.ImGui;
using Dalamud.Interface.Utility.Raii;

namespace UniversalModConverter.Windows.Components;

/// <summary>
/// The "To" card for animations: the slots an idle plays in, swapping an emote or expression, and
/// retargeting to other races.
/// </summary>
internal sealed class AnimationTargetPanel(ConverterSession session)
{
    private string _emoteFilter = string.Empty;
    private string _expressionFilter = string.Empty;
    private string _expressionModFilter = string.Empty;
    private string _expressionSwapFilter = string.Empty;

    public void Draw(AnimationSource source)
    {
        if (source.Kind == AnimationSourceKind.Idle)
        {
            DrawIdle(source);
            return;
        }

        var operation = session.AnimationOperation;
        var facial = source.Kind == AnimationSourceKind.Expression;
        using (ImRaii.Disabled(!session.CanSwapAnimation))
        {
            if (ImGui.RadioButton(facial ? "Swap to another expression" : "Swap to another emote", operation == AnimationOperation.Swap))
                session.SetAnimationOperation(AnimationOperation.Swap);
        }
        Widgets.Tooltip(!session.CanSwapAnimation ? "Only idles, emotes and expressions can be swapped."
            : facial ? "Play this face for another expression of the emote list, with its pace and options."
            : "Play this animation for another emote, for the same race.");
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
            if (session.NeedsAnimationSourceContainer) DrawSourceContainer(source);
            return;
        }

        // Above the operation's own controls, whose lists fill the rest of the card.
        var attach = session.AttachExpression;
        if (ImGui.Checkbox("Also attach a facial expression", ref attach)) session.SetAttachExpression(attach);
        Widgets.Tooltip("The converted animation plays with the face of a game emote or of another mod.");
        if (attach) DrawExpressionPicker();
        ImGui.Spacing();

        if (operation == AnimationOperation.Retarget) DrawRetarget(source);
        else DrawEmoteSwap(source);
    }

    // ── Idles ───────────────────────────────────────────────────────────────

    /// <summary>
    /// An idle plays in every ticked slot, its current one only while that stays ticked.
    /// Retargeting and a facial expression apply wherever it plays, so they are boxes of their own.
    /// </summary>
    private void DrawIdle(AnimationSource source)
    {
        var retarget = session.AnimationAlsoRetargets;
        if (ImGui.Checkbox("Retarget to other races", ref retarget)) session.SetAnimationAlsoRetargets(retarget);
        Widgets.Tooltip("Also rebuild the animation for other races' skeletons, rescaled to their proportions, in every slot " +
                        "it plays in. Only races the game has a file of their own for there are listed: the others play " +
                        "their parent race's (the game's race tree) and never load one of their own.");
        ImGui.SameLine(0, 20f * Theme.Scale);
        var attach = session.AttachExpression;
        if (ImGui.Checkbox("Attach a facial expression", ref attach)) session.SetAttachExpression(attach);
        Widgets.Tooltip("The animation plays with the face of a game emote or of another mod, in every slot it plays in.");
        if (attach) DrawExpressionPicker();
        if (session.NeedsAnimationSourceContainer) DrawSourceContainer(source);
        ImGui.Spacing();

        var slots = session.AnimationSlots;
        if (slots.Count == 0)
        {
            Widgets.MutedWrapped("The game's slots for this idle could not be found.");
            return;
        }

        // Side by side, both lists get the height the card has left.
        using var table = ImRaii.Table("##IdleTargets", retarget ? 2 : 1, ImGuiTableFlags.SizingStretchSame);
        if (!table.Success) return;
        ImGui.TableNextColumn();
        DrawIdleSlots(source, slots);
        if (!retarget) return;
        ImGui.TableNextColumn();
        DrawRetargetSource(source);
        DrawTargetRaces(source, 1);
    }

    private void DrawIdleSlots(AnimationSource source, IReadOnlyList<IdleSlot> slots)
    {
        ImGui.AlignTextToFramePadding();
        ImGui.TextUnformatted("Output slots");
        Widgets.Tooltip("Tick every slot the animation should play in. Its current slot keeps it only while ticked: " +
                        "unticked, converting in place moves it away, while adding to this mod leaves it there as it is.");

        var missing = slots.Count(s => s.Index != source.SlotIndex && !session.CanSwapToIdleSlot(source, s));
        using var list = ImRaii.Child("##Slots", new Vector2(-1, -1), true);
        if (!list.Success) return;
        var hidden = 0;
        foreach (var slot in slots)
        {
            var current = slot.Index == source.SlotIndex;
            var ticked = session.AnimationOutputSlots.Contains(slot.Index);
            if (!current && !ticked && !session.CanSwapToIdleSlot(source, slot)) continue;
            var modded = session.ModdedIdleSlot(source, slot);
            // The current slot stays for orientation, whoever changes it.
            if (!current && !session.ShowsTarget(modded, ticked))
            {
                hidden++;
                continue;
            }
            using var id = ImRaii.PushId(slot.Index);
            using (ImRaii.PushColor(ImGuiCol.Text, Theme.Danger, modded != null))
            {
                if (ImGui.Checkbox(current ? $"{slot.Label} (current)" : slot.Label, ref ticked))
                    session.SetAnimationOutputSlot(slot.Index, ticked);
            }
            var keys = slot.StartKey == null ? $"{slot.LoopKey} (no start animation)" : $"{slot.LoopKey} and {slot.StartKey}";
            Widgets.Tooltip(current ? $"{keys}\nWhere the animation plays now." : keys, modded);
        }
        if (hidden > 0) Widgets.Muted($"{hidden} modded {(hidden == 1 ? "slot" : "slots")} hidden.");
        if (missing > 0) NotListed(source, missing, missing == 1 ? "slot is" : "slots are");
    }

    // ── Expressions ─────────────────────────────────────────────────────────

    /// <summary>
    /// Where the face comes from. Compact on purpose: it sits above the slot, emote and race
    /// lists, which need the height.
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
            Loading("Reading the expression list");
            return;
        }

        var chosen = expressions.FirstOrDefault(e => e.Id == session.ExpressionEmote);
        ImGui.SetNextItemWidth(-1);
        using var combo = ImRaii.Combo("##ExpressionEmote", chosen == null ? "Choose an expression" : $"/{chosen.Name}",
            ImGuiComboFlags.HeightLarge);
        Widgets.Tooltip("The expressions of the game's emote list. Each race gets the face the game plays for it.");
        if (!combo.Success) return;

        ImGui.SetNextItemWidth(-1);
        ImGui.InputTextWithHint("##ExpressionFilter", "Filter expressions", ref _expressionFilter, 64);
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
        using (var combo = ImRaii.Combo("##ExpressionMod", current?.Name ?? "Choose a mod", ImGuiComboFlags.HeightLarge))
        {
            if (combo.Success)
            {
                ImGui.SetNextItemWidth(-1);
                ImGui.InputTextWithHint("##ExpressionModFilter", "Filter mods", ref _expressionModFilter, 64);
                var filter = _expressionModFilter.Trim();
                // Two mods may share a name; the folder tells their rows apart.
                foreach (var mod in mods.Where(m => filter.Length == 0 || m.Name.Contains(filter, StringComparison.OrdinalIgnoreCase)))
                {
                    using var id = ImRaii.PushId(mod.Directory);
                    if (ImGui.Selectable(mod.Name, ReferenceEquals(mod, current)))
                        session.SetExpressionMod(mod.Directory);
                }
            }
        }
        if (mods.Count == 0) Widgets.MutedWrapped("Penumbra's mod list is not available.");
        if (session.ExpressionModDirectory == null) return;

        var expressions = session.ModExpressions;
        if (expressions == null)
        {
            Loading("Looking for facial animations in that mod");
            return;
        }
        if (expressions.Count == 0)
        {
            Widgets.MutedWrapped("That mod has no facial expression: no face animations, and no animation that plays one.");
            return;
        }

        ImGui.SetNextItemWidth(-1);
        using var files = ImRaii.Combo("##ExpressionFile", session.ExpressionModFile?.Label ?? "Choose an expression");
        if (!files.Success) return;
        for (var index = 0; index < expressions.Count; index++)
        {
            using var id = ImRaii.PushId(index);
            if (ImGui.Selectable(expressions[index].Label, expressions[index] == session.ExpressionModFile))
                session.SetExpressionModFile(expressions[index]);
        }
    }

    /// <summary>
    /// The version the option group of an expression added to this mod uses, when several options
    /// of the mod have their own: one group can hold only one file per animation. Nothing is
    /// preselected, so the choice is always deliberate.
    /// </summary>
    private void DrawSourceContainer(AnimationSource source)
    {
        var chosen = source.Providers.FirstOrDefault(p => p.Address == session.AnimationSourceContainer);
        ImGui.AlignTextToFramePadding();
        Widgets.ColoredWrapped(Theme.Warning, "Take it from");
        ImGui.SameLine();
        ImGui.SetNextItemWidth(-1);
        using (var combo = ImRaii.Combo("##SourceContainer", chosen?.Label ?? "Choose an option", ImGuiComboFlags.HeightLarge))
        {
            if (combo.Success)
                foreach (var provider in source.Providers)
                {
                    using var id = ImRaii.PushId($"{provider.Address.Group}/{provider.Address.Index}");
                    if (ImGui.Selectable(provider.Label, provider == chosen)) session.SetAnimationSourceContainer(provider.Address);
                }
        }
        Widgets.Tooltip("Several options of this mod have their own version of this animation, and the new option group " +
                        "can hold only one of them. Choose the one to use; the options themselves stay as they are.");
    }

    // ── Emotes ──────────────────────────────────────────────────────────────

    private void DrawEmoteSwap(AnimationSource source)
    {
        var emotes = session.AnimationEmotes;
        if (emotes == null)
        {
            Loading("Reading the emote list");
            return;
        }

        ImGui.SetNextItemWidth(-1);
        ImGui.InputTextWithHint("##EmoteFilter", "Filter emotes", ref _emoteFilter, 64);
        if (emotes.FirstOrDefault(e => e.Id == session.AnimationTargetEmote) is { } chosen)
        {
            ImGui.TextColored(Theme.Success, $"/{chosen.Name}");
            ImGui.SameLine();
            Widgets.Muted(string.Join(", ", chosen.Timelines.Select(t => t.Label)));
            Widgets.ModdedBadge(session.ModdedEmote(source, chosen));
        }
        else Widgets.Muted("No emote selected.");

        var filter = _emoteFilter.Trim();
        var candidates = emotes.Where(e => e.Id != source.EmoteId).ToList();
        var missing = candidates.Count(e => e.Id != session.AnimationTargetEmote && !session.CanSwapToEmote(source, e));
        if (missing > 0) NotListed(source, missing, missing == 1 ? "emote is" : "emotes are");
        var matching = candidates.Where(e => (e.Id == session.AnimationTargetEmote || session.CanSwapToEmote(source, e)) &&
                                             (filter.Length == 0 || e.Name.Contains(filter, StringComparison.OrdinalIgnoreCase) ||
                                              e.Timelines.Any(t => t.Key.Contains(filter, StringComparison.OrdinalIgnoreCase))))
            .ToList();
        var shown = matching.Where(e => session.ShowsTarget(session.HideModdedTargets ? session.ModdedEmote(source, e) : null,
                e.Id == session.AnimationTargetEmote))
            .ToList();
        using var list = ImRaii.Child("##Emotes", new Vector2(-1, -1), true);
        if (!list.Success) return;
        if (shown.Count == 0 && matching.Count > 0)
        {
            Widgets.Muted("Every matching emote is already modded.");
            return;
        }
        var rowHeight = ImGui.GetTextLineHeight() * 1.5f;
        Widgets.Clipped(shown.Count, rowHeight + ImGui.GetStyle().ItemSpacing.Y, i =>
        {
            var emote = shown[i];
            using var id = ImRaii.PushId((int)emote.Id);
            if (EmoteRow(emote.Icon, emote.Name, emote.Id == session.AnimationTargetEmote, true,
                    string.Join("\n", emote.Timelines.Select(t => $"{t.Label}: {t.Key}")), session.ModdedEmote(source, emote), rowHeight))
                session.SetAnimationTargetEmote(emote.Id);
        });
    }

    // ── Facial expressions ──────────────────────────────────────────────────

    /// <summary>The expressions of the emote list the face can move to.</summary>
    private void DrawExpressionSwap(AnimationSource source)
    {
        var expressions = session.AnimationExpressions;
        if (expressions == null)
        {
            Loading("Reading the expression list");
            return;
        }

        ImGui.SetNextItemWidth(-1);
        ImGui.InputTextWithHint("##ExpressionSwapFilter", "Filter expressions", ref _expressionSwapFilter, 64);
        if (expressions.FirstOrDefault(e => e.Id == session.AnimationTargetEmote) is { } chosen)
        {
            ImGui.TextColored(Theme.Success, $"/{chosen.Name}");
            Widgets.ModdedBadge(session.ModdedExpression(source, chosen));
        }
        else Widgets.Muted("No expression selected.");

        var filter = _expressionSwapFilter.Trim();
        var matching = expressions.Where(e => e.Id != source.EmoteId &&
                                              (filter.Length == 0 || e.Name.Contains(filter, StringComparison.OrdinalIgnoreCase) ||
                                               e.Pose.Contains(filter, StringComparison.OrdinalIgnoreCase)))
            .ToList();
        var shown = matching.Where(e => session.ShowsTarget(session.HideModdedTargets ? session.ModdedExpression(source, e) : null,
                e.Id == session.AnimationTargetEmote))
            .ToList();
        using var list = ImRaii.Child("##Expressions", new Vector2(-1, -1), true);
        if (!list.Success) return;
        if (shown.Count == 0 && matching.Count > 0)
        {
            Widgets.Muted("Every matching expression is already modded.");
            return;
        }
        var rowHeight = ImGui.GetTextLineHeight() * 1.5f;
        Widgets.Clipped(shown.Count, rowHeight + ImGui.GetStyle().ItemSpacing.Y, i =>
        {
            var expression = shown[i];
            using var id = ImRaii.PushId((int)expression.Id);
            if (EmoteRow(expression.Icon, expression.Name, expression.Id == session.AnimationTargetEmote, expression.OwnPack,
                    expression.OwnPack
                        ? $"facial/pose/{expression.Pose}"
                        : "The game keeps this face in the shared face pack with every other face, so it cannot be replaced on its own.",
                    session.ModdedExpression(source, expression), rowHeight))
                session.SetAnimationTargetEmote(expression.Id);
        });
    }

    /// <summary>
    /// A row of the game's emote list: its icon and /name, selectable unless <paramref name="enabled"/>
    /// is false, and red when <paramref name="modded"/> says another mod already changes it.
    /// </summary>
    private static bool EmoteRow(uint icon, string name, bool selected, bool enabled, string tooltip, string? modded, float rowHeight)
    {
        var start = ImGui.GetCursorPos();
        bool clicked;
        using (ImRaii.Disabled(!enabled))
            clicked = ImGui.Selectable("##row", selected, ImGuiSelectableFlags.None, new Vector2(0, rowHeight));
        Widgets.Tooltip(tooltip, modded);
        ImGui.SetCursorPos(start);
        Widgets.GameIcon(icon, rowHeight);
        ImGui.SameLine();
        ImGui.SetCursorPosY(start.Y + (rowHeight - ImGui.GetTextLineHeight()) / 2);
        if (!enabled) Widgets.Muted($"/{name}");
        else if (modded != null) ImGui.TextColored(Theme.Danger, $"/{name}");
        else ImGui.TextUnformatted($"/{name}");
        return clicked;
    }

    /// <summary>
    /// Says how many targets a swap would write nothing for, and so are not listed: the game has
    /// no animation of their own there for any race the mod provides, and never asks for one.
    /// </summary>
    private static void NotListed(AnimationSource source, int count, string what)
        => Widgets.MutedWrapped($"{count} {what} not listed: the game has no animation of its own there for " +
                                $"{string.Join(", ", source.Races.Select(RaceNames.Name))}, which play another race's " +
                                "instead, so a swapped file would never load.");

    private static void Loading(string message)
    {
        Widgets.Spinner(Theme.Accent);
        ImGui.SameLine();
        Widgets.Muted(message);
    }

    // ── Races ───────────────────────────────────────────────────────────────

    private void DrawRetarget(AnimationSource source)
    {
        DrawRetargetSource(source);
        Widgets.MutedWrapped("To: only races the game has this animation for are listed. The others play their parent race's " +
                             "(the game's race tree) and never load one of their own; the preview lists which of them each new " +
                             "file also covers.");
        DrawTargetRaces(source, 2);
    }

    /// <summary>The race whose files are rebuilt, on one line.</summary>
    private void DrawRetargetSource(AnimationSource source)
    {
        ImGui.AlignTextToFramePadding();
        ImGui.TextUnformatted("From");
        ImGui.SameLine();
        ImGui.SetNextItemWidth(-1);
        using (var combo = ImRaii.Combo("##SourceRace", ConverterSession.RaceLabel(session.AnimationSourceRace)))
        {
            if (combo.Success)
                foreach (var race in source.Races)
                    if (ImGui.Selectable(RaceNames.Describe(race), race == session.AnimationSourceRace))
                        session.SetAnimationSourceRace(race);
        }
        Widgets.Tooltip("The race whose files are rebuilt. Only races this mod provides the animation for are listed.");
    }

    /// <summary>
    /// The races the retarget writes, in <paramref name="columns"/> columns filling the rest of the
    /// card: the source race first, then those to rebuild the animation for.
    /// </summary>
    private void DrawTargetRaces(AnimationSource source, int columns)
    {
        // Only races with a file of their own can be given one; ticked ones stay so they can be unticked.
        var sourceRace = session.AnimationSourceRace;
        var retargetable = session.RetargetRaces(source);
        var targets = GenderRaces.Playable
            .Where(r => r != sourceRace && (retargetable.Contains(r) || session.AnimationTargetRaces.Contains(r)))
            .ToList();
        using var grid = ImRaii.Child("##Races", new Vector2(-1, -1), true);
        if (!grid.Success) return;
        if (targets.Count == 0)
        {
            Widgets.MutedWrapped("The game has this animation for no other race: every other race plays the one of a race " +
                                 "it inherits from, so there is no race to retarget it to.");
            return;
        }
        using var table = ImRaii.Table("##RaceTable", columns, ImGuiTableFlags.SizingStretchSame);
        if (!table.Success) return;
        DrawSourceRace(sourceRace);
        foreach (var race in targets)
        {
            var included = session.AnimationTargetRaces.Contains(race);
            var provided = source.Races.Contains(race);
            var modded   = session.ModdedRetargetRace(source, race);
            if (!session.ShowsTarget(modded, included)) continue;
            ImGui.TableNextColumn();
            using var id = ImRaii.PushId(race);
            using (ImRaii.PushColor(ImGuiCol.Text, Theme.Danger, modded != null))
            {
                if (ImGui.Checkbox(ConverterSession.RaceLabel(race), ref included)) session.SetAnimationTargetRace(race, included);
            }
            Widgets.Tooltip(provided
                ? $"{RaceNames.Describe(race)}. The mod already has this animation for this race; it would be replaced."
                : RaceNames.Describe(race), modded);
            if (provided)
            {
                ImGui.SameLine();
                Widgets.Badge("in mod", Theme.Warning);
            }
        }
    }

    /// <summary>
    /// The source race, ticked while the output keeps its animation. Only a new mod leaves that
    /// to the user: adding to this mod always keeps it, and converting in place always moves it
    /// to the ticked races.
    /// </summary>
    private void DrawSourceRace(ushort race)
    {
        ImGui.TableNextColumn();
        using var id = ImRaii.PushId("source");
        var mode = session.EffectiveOutputMode;
        var kept = session.AnimationSourceRaceStays;
        using (ImRaii.Disabled(!mode.IsNewMod()))
        {
            if (ImGui.Checkbox($"{ConverterSession.RaceLabel(race)} (source)", ref kept)) session.SetAnimationIncludesSourceRace(kept);
        }
        Widgets.Tooltip(mode switch
        {
            ConversionOutputMode.AddToMod => $"{RaceNames.Describe(race)}, the race it is retargeted from. Adding to this mod always " +
                                             "keeps its animation.",
            ConversionOutputMode.InPlace  => $"{RaceNames.Describe(race)}, the race it is retargeted from. Converting in place moves " +
                                             "the animation from it to the ticked races; add to this mod to keep it too.",
            _                             => $"{RaceNames.Describe(race)}, the race it is retargeted from. Ticked, the new mod holds " +
                                             "its animation too; untick it to leave it out.",
        });
    }
}
