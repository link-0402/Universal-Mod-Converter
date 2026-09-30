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
    private string _expressionModFilter = string.Empty;
    private string _expressionSwapFilter = string.Empty;

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
            Loading("Reading the expression list…");
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
                ImGui.InputTextWithHint("##ExpressionModFilter", "Filter mods…", ref _expressionModFilter, 64);
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
            Loading("Looking for facial animations in that mod…");
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
        for (var index = 0; index < expressions.Count; index++)
        {
            using var id = ImRaii.PushId(index);
            if (ImGui.Selectable(expressions[index].Label, expressions[index] == session.ExpressionModFile))
                session.SetExpressionModFile(expressions[index]);
        }
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
                        "so the slot can be picked in Penumbra at any time. Its first option, \"-\", places it nowhere. " +
                        "The mod's own copy keeps playing in the current slot as long as this mod is enabled.");

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
            Widgets.Muted($"{session.AnimationGroupSlots.Count} of {slots.Count(s => session.CanSwapToIdleSlot(source, s))} slots");
        }
        else if (session.EffectiveOutputMode == ConversionOutputMode.InPlace)  // Implied by AddToMod; not kept in a new mod.
        {
            var keep = session.AnimationKeepOriginal;
            if (ImGui.Checkbox("Keep it in the current slot too", ref keep)) session.SetAnimationKeepOriginal(keep);
            Widgets.Tooltip("Copy instead of move: both slots play this animation.");
        }

        var missing = slots.Count(s => s.Index != source.SlotIndex && !session.CanSwapToIdleSlot(source, s));
        if (missing > 0) NotListed(source, missing, missing == 1 ? "slot is" : "slots are");

        using var grid = ImRaii.Child("##Slots", new Vector2(-1, -1), true);
        if (!grid.Success) return;
        var shown = false;
        foreach (var slot in slots)
        {
            var modded = session.ModdedIdleSlot(source, slot);
            // The current slot stays for orientation, whoever changes it.
            var chosen = slot.Index == source.SlotIndex ||
                         (group ? session.AnimationGroupSlots.Contains(slot.Index) : slot.Index == session.AnimationTargetSlot);
            if (!chosen && !session.CanSwapToIdleSlot(source, slot)) continue;
            if (!session.ShowsTarget(modded, chosen)) continue;
            shown = true;
            using var id = ImRaii.PushId(slot.Index);
            var label = slot.Index == source.SlotIndex ? $"{slot.Label} (current)" : slot.Label;
            using (ImRaii.PushColor(ImGuiCol.Text, Theme.Danger, modded != null))
            {
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
            }
            Widgets.Tooltip(slot.StartKey == null ? $"{slot.LoopKey} (no start animation)" : $"{slot.LoopKey} and {slot.StartKey}", modded);
        }
        if (!shown) Widgets.Muted("Every slot is already modded.");
    }

    /// <summary>
    /// The version a new option group uses (a slot group, or an expression added to this mod), when
    /// several options of the mod have their own: one group can hold only one file per animation.
    /// Nothing is preselected, so the choice is always deliberate.
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
        Widgets.Tooltip("Several options of this mod have their own version of this animation, and the new option group " +
                        "can hold only one of them. Choose the one to use; the options themselves stay as they are.");
    }

    // ── Emotes ──────────────────────────────────────────────────────────────

    private void DrawEmoteSwap(AnimationSource source)
    {
        var emotes = session.AnimationEmotes;
        if (emotes == null)
        {
            Loading("Reading the emote list…");
            return;
        }

        ImGui.SetNextItemWidth(-1);
        ImGui.InputTextWithHint("##EmoteFilter", "Filter emotes…", ref _emoteFilter, 64);
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
            Loading("Reading the expression list…");
            return;
        }

        ImGui.SetNextItemWidth(-1);
        ImGui.InputTextWithHint("##ExpressionSwapFilter", "Filter expressions…", ref _expressionSwapFilter, 64);
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

        Widgets.MutedWrapped("To: only races the game has this animation for are listed. The others play their parent race's " +
                             "(the game's race tree) and never load one of their own; the preview lists which of them each new " +
                             "file also covers.");
        // Only races with a file of their own can be given one; ticked ones stay so they can be unticked.
        var retargetable = session.RetargetRaces(source);
        var races = GenderRaces.Playable
            .Where(r => r == session.AnimationSourceRace || retargetable.Contains(r) || session.AnimationTargetRaces.Contains(r))
            .ToList();
        using var grid = ImRaii.Child("##Races", new Vector2(-1, -1), true);
        if (!grid.Success) return;
        if (races.All(r => r == session.AnimationSourceRace))
        {
            Widgets.MutedWrapped("The game has this animation for no other race: every other race plays the one of a race " +
                                 "it inherits from, so there is no race to retarget it to.");
            return;
        }
        using var table = ImRaii.Table("##RaceTable", 2, ImGuiTableFlags.SizingStretchSame);
        if (!table.Success) return;
        foreach (var race in races)
        {
            var included = session.AnimationTargetRaces.Contains(race);
            var isSource = race == session.AnimationSourceRace;
            var provided = source.Races.Contains(race);
            var modded   = isSource ? null : session.ModdedRetargetRace(source, race);
            if (!session.ShowsTarget(modded, included)) continue;
            ImGui.TableNextColumn();
            using var id = ImRaii.PushId(race);
            using (ImRaii.Disabled(isSource))
            using (ImRaii.PushColor(ImGuiCol.Text, Theme.Danger, modded != null))
            {
                if (ImGui.Checkbox(ConverterSession.RaceLabel(race), ref included)) session.SetAnimationTargetRace(race, included);
            }
            Widgets.Tooltip(isSource ? "This is the source race."
                : provided
                    ? $"{RaceNames.Describe(race)}. The mod already has this animation for this race; it would be replaced."
                    : RaceNames.Describe(race), modded);
            if (provided && !isSource)
            {
                ImGui.SameLine();
                Widgets.Badge("in mod", Theme.Warning);
            }
        }
    }
}
