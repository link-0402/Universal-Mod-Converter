using System;
using System.Collections.Generic;
using System.Collections.Immutable;
using System.Linq;
using System.Numerics;
using System.Text.RegularExpressions;
using UniversalModConverter.Core;
using UniversalModConverter.Models;
using UniversalModConverter.Services;
using UniversalModConverter.Session;
using UniversalModConverter.Windows.Ui;
using Dalamud.Bindings.ImGui;
using Dalamud.Interface;
using Dalamud.Interface.Utility.Raii;

namespace UniversalModConverter.Windows.Components;

/// <summary>
/// The mesh groups of every model the output ships for the target items, with a Keep
/// checkbox per group. Models with the same layout (normally the race versions of one
/// model) form a family and are edited together. A run of several gear conversions lists
/// each under its own heading, with the same rules a single conversion follows. Hair, face, tail and
/// ear conversions list their models the same way, one family per file.
/// </summary>
internal sealed partial class MeshGroupsView(ConverterSession session, Plugin plugin)
{
    private PartPreviewService preview => plugin.PartPreview;

    private sealed record Family(string Title, List<GearOutputModel> Models);

    /// <summary>
    /// One conversion of the plan: its editable models as families, and those it cannot edit.
    /// <paramref name="Conversion"/> is null for a hair, face, tail or ear conversion.
    /// </summary>
    private sealed record Section(string Title, GearConversionRequest? Conversion, List<Family> Families,
        List<GearOutputModel> ReadOnly);

    [GeneratedRegex(@"c\d{4}", RegexOptions.CultureInvariant)]
    private static partial Regex RaceCodeRegex();

    private ConversionTask? _builtFor;
    private List<Section> _sections = new();

    /// <summary>Mesh groups whose parts are listed, by family (counted over every section) and group index.</summary>
    private readonly HashSet<(int Family, int Group)> _expanded = [];


    /// <summary>Number of models the tab lists for the current plan (0 when there is nothing to show).</summary>
    public int ModelCount => session.Task.IsPlanned ? session.Task.OutputModels.Count : 0;

    public void Draw()
    {
        var task = session.Task;
        if (!task.IsPlanned)
        {
            Widgets.MutedWrapped("Add a gear, facewear, hair, face, tail or ear conversion to the plan to see the mesh groups " +
                                 "of the converted models.");
            return;
        }
        var conversions = task.GearConversions().ToList();
        // Hair, face, tail and ear conversions patch the mod's files instead of producing a file plan.
        var customization = task.FilePlan == null && CustomizationKinds.IsCustomization(task.Kind);
        if (conversions.Count == 0 && !customization)
        {
            Widgets.MutedWrapped("Mesh groups can be edited for gear, facewear, hair, face, tail and ear conversions.");
            return;
        }

        // A single conversion says what changing slots does up front; a run says it under each conversion.
        var single = conversions.Count <= 1;
        if (conversions.Count == 1 && CrossSlotNote(conversions[0].Plan.Request) is { } note)
            DrawCrossSlotNotice(conversions[0].Plan.Request.Source.Slot, conversions[0].Plan.Request.Target.Slot, note);

        if (task.OutputModels.Count == 0)
        {
            Widgets.MutedWrapped(customization
                ? $"The output contains no model for the converted {CustomizationKinds.Get(task.Kind).DisplayName.ToLowerInvariant()}."
                : single
                ? "The output contains no model for the target item."
                : "The output contains no model for the target items.");
            return;
        }

        EnsureSections(task, conversions, customization);
        Widgets.MutedWrapped("Untick a mesh group to leave it out of the converted model, or open it with the arrow to " +
                             "keep or remove its parts one by one. " +
                             (customization ? string.Empty
                                 : "Models with the same layout (usually the race versions of one model) are edited together. ") +
                             "Only the converted models change.");
        DrawPreviewControls();
        if (session.MeshEditBlockReason is { } reason)
            Widgets.ColoredWrapped(Theme.Warning, reason);
        ImGui.Spacing();

        var family = 0;
        for (var s = 0; s < _sections.Count; s++)
        {
            var section = _sections[s];
            using var id = ImRaii.PushId(s);
            if (!single)
            {
                ImGui.Spacing();
                ImGui.TextColored(Theme.Accent, section.Title);
                if (section.Conversion is { } conversion && CrossSlotNote(conversion) is { } sectionNote)
                    DrawCrossSlotNotice(conversion.Source.Slot, conversion.Target.Slot, sectionNote);
                if (section.Families.Count == 0 && section.ReadOnly.Count == 0)
                    Widgets.MutedWrapped("This conversion ships no model for its target item.");
            }

            foreach (var entry in section.Families) DrawFamily(family++, entry);
            DrawReadOnly(section.ReadOnly);
        }
    }

    private static string? CrossSlotNote(GearConversionRequest conversion)
        => GearSlots.CrossSlotNote(conversion.Source.Slot, conversion.Target.Slot);

    private static void DrawReadOnly(List<GearOutputModel> models)
    {
        foreach (var model in models)
        {
            Widgets.Icon(FontAwesomeIcon.Lock, Theme.Muted);
            ImGui.SameLine();
            Widgets.PathText(model.Local, Theme.Muted, ImGui.GetContentRegionAvail().X * 0.5f);
            ImGui.SameLine();
            Widgets.ColoredWrapped(Theme.Warning, model.EditError ?? "No mesh groups.");
        }
    }

    public static void DrawCrossSlotNotice(GearSlot from, GearSlot to, string note)
    {
        using var table = ImRaii.Table("##CrossSlotNotice", 1, ImGuiTableFlags.BordersOuter | ImGuiTableFlags.PadOuterX);
        if (!table.Success) return;
        ImGui.TableNextRow();
        ImGui.TableSetBgColor(ImGuiTableBgTarget.RowBg0, ImGui.GetColorU32(Theme.Info.WithAlpha(0.12f)));
        ImGui.TableNextColumn();
        Widgets.Icon(FontAwesomeIcon.InfoCircle, Theme.Info);
        ImGui.SameLine();
        ImGui.TextColored(Theme.Info, $"{SlotName(from)} → {SlotName(to)}");
        ImGui.TextWrapped($"{note} Untick anything else that should not show.");
        ImGui.Spacing();
    }

    private void DrawFamily(int index, Family family)
    {
        var first   = family.Models[0];
        var removed = session.RemovedMeshGroupCount(first);
        var parts   = session.RemovedMeshPartCount(first);
        var summary = removed > 0
            ? $"{first.Groups.Count - removed} of {first.Groups.Count} mesh groups kept"
            : $"{first.Groups.Count} mesh groups";
        if (parts > 0) summary += $", {parts} part(s) removed";
        ImGui.SetNextItemOpen(true, ImGuiCond.Appearing);
        if (!ImGui.CollapsingHeader($"{family.Title}  ·  {summary}###Family{index}")) return;
        DrawFamilyDetails(family);

        var blocked = session.MeshEditBlockReason;
        using var id = ImRaii.PushId(index);
        using (var table = ImRaii.Table("##Groups", 6,
                   ImGuiTableFlags.RowBg | ImGuiTableFlags.BordersInnerV | ImGuiTableFlags.SizingStretchProp))
        {
            if (table.Success)
            {
                ImGui.TableSetupColumn("Keep", ImGuiTableColumnFlags.WidthFixed, ImGui.GetFrameHeight() + 4f * Theme.Scale);
                ImGui.TableSetupColumn("#", ImGuiTableColumnFlags.WidthFixed, ImGui.GetFrameHeight() + 34f * Theme.Scale);
                ImGui.TableSetupColumn("Material", ImGuiTableColumnFlags.WidthStretch, 1.4f);
                ImGui.TableSetupColumn("Triangles", ImGuiTableColumnFlags.WidthFixed, 80f * Theme.Scale);
                ImGui.TableSetupColumn("Parts", ImGuiTableColumnFlags.WidthFixed, 40f * Theme.Scale);
                ImGui.TableSetupColumn("Attributes", ImGuiTableColumnFlags.WidthStretch, 1f);
                ImGui.TableHeadersRow();

                for (var g = 0; g < first.Groups.Count; g++)
                {
                    DrawGroupRow(index, family, g, removed, blocked);
                    if (!_expanded.Contains((index, g))) continue;
                    for (var p = 0; p < first.Groups[g].Parts; p++)
                        DrawPartRow(family, g, p, removed, blocked);
                }
            }
        }

        if ((removed > 0 || parts > 0) && Widgets.IconTextButton(FontAwesomeIcon.Undo, "Keep all", blocked))
            session.KeepAllMeshGroups(family.Models);
        ImGui.Spacing();
    }

    private void DrawGroupRow(int familyIndex, Family family, int g, int removedInFamily, string? blocked)
    {
        var model     = family.Models[0];
        var group     = model.Groups[g];
        var isRemoved = session.IsMeshGroupRemoved(model, g);
        var partsOut  = session.RemovedMeshPartCount(model, g);
        var mixed     = !isRemoved && partsOut > 0;
        var lastKept  = !isRemoved && removedInFamily == model.Groups.Count - 1;
        var forced    = session.IsMeshGroupForced(model, g);
        var byDefault = !forced && session.IsMeshGroupOffByDefault(model, g);
        var text      = isRemoved ? Theme.Muted : Theme.Text;

        ImGui.TableNextRow();
        ImGui.TableNextColumn();
        var keep = !isRemoved;
        using (ImRaii.Disabled(blocked != null || forced || lastKept && !mixed))
        {
            if (mixed) ImGuiP.PushItemFlag(ImGuiItemFlags.MixedValue, true);
            var clicked = ImGui.Checkbox($"##Keep{g}", ref keep);
            if (mixed) ImGuiP.PopItemFlag();
            // A partly kept group comes back whole on the first click.
            if (clicked) session.SetMeshGroupRemoved(family.Models, g, !(keep || mixed));
        }
        Widgets.Tooltip(forced ? ForcedReason : blocked ?? (lastKept && !mixed ? "A model must keep at least one mesh group."
            : mixed ? $"{partsOut} of {group.Parts} parts are removed. Click to keep the whole mesh group."
            : keep ? "Untick to remove this mesh group."
            : byDefault ? $"{DefaultOffReason} Tick to keep it anyway."
            : "Tick to keep this mesh group."));

        ImGui.TableNextColumn();
        if (group.Parts > 1)
        {
            var open = _expanded.Contains((familyIndex, g));
            if (ImGui.ArrowButton($"##Expand{g}", open ? ImGuiDir.Down : ImGuiDir.Right))
            {
                if (open) _expanded.Remove((familyIndex, g));
                else _expanded.Add((familyIndex, g));
            }
            Widgets.Tooltip(open ? "Hide the parts of this mesh group." : "Show the parts of this mesh group, to keep or remove them one by one.");
            ImGui.SameLine();
        }
        else
            ImGui.Dummy(new Vector2(ImGui.GetFrameHeight(), 0));
        if (group.Parts <= 1) ImGui.SameLine();
        ImGui.AlignTextToFramePadding();
        ImGui.TextColored(text, g.ToString());

        ImGui.TableNextColumn();
        ImGui.AlignTextToFramePadding();
        if (group.IsSkin)
        {
            Widgets.Badge("Body", forced ? Theme.Warning : Theme.Info);
            Widgets.Tooltip(forced ? ForcedReason
                : byDefault ? DefaultOffReason
                : "Uses one of the character's body materials: skin (exposed arms, legs or torso), bibo, pubes or piercings.");
            ImGui.SameLine();
        }
        ImGui.TextColored(text, Widgets.Ellipsize(group.Material, ImGui.GetContentRegionAvail().X));
        Widgets.CopyOnRightClick(group.Material);

        ImGui.TableNextColumn();
        ImGui.AlignTextToFramePadding();
        DrawTriangles(family.Models.Where(m => g < m.Groups.Count).Select(m => m.Groups[g].Triangles).ToList(), text);

        ImGui.TableNextColumn();
        ImGui.AlignTextToFramePadding();
        ImGui.TextColored(text, mixed ? $"{group.Parts - partsOut}/{group.Parts}" : group.Parts.ToString());

        ImGui.TableNextColumn();
        ImGui.AlignTextToFramePadding();
        DrawAttributes(group.Attributes);
    }

    private const string DefaultOffReason =
        "Uses a body material (skin, bibo, pubes or piercings): a body part of the old slot, which would show wherever " +
        "the new item is worn, so it was switched off automatically.";

    private const string ForcedReason =
        "Uses a body material (skin, bibo, pubes or piercings). An accessory cannot load those, and the game would not " +
        "draw the model at all, so this mesh group is always left out.";

    private void DrawPartRow(Family family, int g, int p, int removedInFamily, string? blocked)
    {
        var model     = family.Models[0];
        var group     = model.Groups[g];
        var part      = group.PartList[p];
        var isRemoved = session.IsMeshPartRemoved(model, g, p);
        var keptParts = group.Parts - session.RemovedMeshPartCount(model, g);
        var lastKept  = !isRemoved && keptParts == 1 &&
                        !session.IsMeshGroupRemoved(model, g) && removedInFamily == model.Groups.Count - 1;
        var forced    = session.IsMeshGroupForced(model, g);
        var text      = isRemoved ? Theme.Muted : Theme.Text;

        ImGui.TableNextRow();
        ImGui.TableNextColumn();
        var indent = 10f * Theme.Scale;
        ImGui.SetCursorPosX(ImGui.GetCursorPosX() + indent);
        var keep = !isRemoved;
        using (ImRaii.Disabled(blocked != null || forced || lastKept))
        using (ImRaii.PushStyle(ImGuiStyleVar.FramePadding, ImGui.GetStyle().FramePadding * 0.6f))
        {
            if (ImGui.Checkbox($"##Keep{g}.{p}", ref keep))
                session.SetMeshPartRemoved(family.Models, g, p, !keep);
        }
        Widgets.Tooltip(forced ? ForcedReason : blocked ?? (lastKept ? "A model must keep at least one part."
            : keep ? "Untick to remove this part." : "Tick to keep this part."));

        ImGui.TableNextColumn();
        ImGui.SetCursorPosX(ImGui.GetCursorPosX() + ImGui.GetFrameHeight() + ImGui.GetStyle().ItemSpacing.X);
        ImGui.TextColored(Theme.Muted, $"{g}.{p}");

        ImGui.TableNextColumn();
        ImGui.TextColored(text, $"Part {p + 1} of {group.Parts}");

        ImGui.TableNextColumn();
        DrawTriangles(family.Models
            .Where(m => g < m.Groups.Count && p < m.Groups[g].PartList.Length)
            .Select(m => m.Groups[g].PartList[p].Triangles).ToList(), text);

        ImGui.TableNextColumn();
        ImGui.TableNextColumn();
        DrawAttributes(part.Attributes);
    }

    private static void DrawTriangles(List<int> triangles, Vector4 color)
    {
        if (triangles.Count == 0)
        {
            ImGui.TextColored(Theme.Muted, "–");
            return;
        }
        var min = triangles.Min();
        var max = triangles.Max();
        ImGui.TextColored(color, min == max ? $"{min:N0}" : $"{min:N0}–{max:N0}");
        if (min != max) Widgets.Tooltip("The race versions of this model differ in detail.");
    }

    private static void DrawAttributes(ImmutableArray<string> attributes)
    {
        var text = attributes.IsDefaultOrEmpty ? "–" : string.Join(", ", attributes);
        ImGui.TextColored(Theme.Muted, Widgets.Ellipsize(text, ImGui.GetContentRegionAvail().X));
        if (!attributes.IsDefaultOrEmpty) Widgets.Tooltip(string.Join("\n", attributes));
    }

    /// <summary>
    /// The switch for the on-character preview and, for each conversion's original item, a
    /// button that puts it on the character. The preview shows the models of the items that are
    /// worn: on anything else the redrawn model would not be the one the rows describe.
    /// </summary>
    private void DrawPreviewControls()
    {
        var config      = plugin.Configuration;
        var enabled     = config.MeshPreviewOnCharacter;
        var unavailable = preview.UnavailableReason(_sections.SelectMany(EditableModels).ToList());

        using (ImRaii.Disabled(unavailable != null))
        {
            if (ImGui.Checkbox("Show my choices on my character", ref enabled))
            {
                config.MeshPreviewOnCharacter = enabled;
                config.Save();
            }
        }
        Widgets.Tooltip(unavailable ?? "While this is on, your character shows the model without the mesh groups and parts " +
                        "you untick, and is redrawn each time you change one.");

        // A run converts several items; each gets its own button, on a line of its own. A hair,
        // face, tail or ear is not worn, so it has none.
        var sources = _sections.Where(s => s.Conversion != null).Select(s => s.Conversion!.Source).Distinct().ToList();
        var worn = sources.ToDictionary(s => s, s => plugin.WornGear.Wears(s));
        for (var i = 0; i < sources.Count; i++)
        {
            using var id = ImRaii.PushId(i);
            if (i == 0) ImGui.SameLine();
            DrawEquipButton(sources[i], worn[sources[i]]);
        }

        if (unavailable != null)
        {
            Widgets.MutedWrapped($"The preview is not available: {unavailable}");
            return;
        }
        if (!enabled) return;

        var shown = _sections.Where(s => s.Conversion is not { } conversion || worn[conversion.Source] != false).ToList();
        if (_sections.Any(s => s.Conversion == null) && CustomizationSource() is { } original)
            Widgets.MutedWrapped($"Your character shows your choices while it has the original {original}.");
        foreach (var source in sources.Where(s => worn[s] == false))
        {
            var name = plugin.GameData.FindItem(source)?.Name ?? "the original item";
            var showing = plugin.WornGear.WornSet(source.Slot) is { } set and not 0
                ? $" Your character currently shows model {(source.IsAccessory ? 'a' : 'e')}{set:D4} there, not " +
                  $"{(source.IsAccessory ? 'a' : 'e')}{source.SetId:D4}."
                : " Your character currently shows nothing there.";
            Widgets.ColoredWrapped(Theme.Warning, shown.Count == 0
                ? $"Wear {name} to use the preview. It stays off until you do.{showing}"
                : $"Wear {name} to see its choices as well.{showing}");
        }
        if (shown.Count == 0) return;

        preview.Request(shown.SelectMany(EditableModels).ToList(), session.Task.MeshRemovals);
        if (preview.Problem is { } problem) Widgets.ColoredWrapped(Theme.Warning, problem);
        else
            Widgets.MutedWrapped("Your character shows the model without what you untick below, and is redrawn each time " +
                                 "you change a checkbox. Keep the source mod enabled.");
    }

    /// <summary>The hair, face, tail or ear being converted, as "Hyur Midlander Male Hair 12"; null when unknown.</summary>
    private string? CustomizationSource()
    {
        var task = session.Task;
        if (task.SourceGenderRace is not { } race || !ushort.TryParse(task.OldIdPadded, out var id)) return null;
        return $"{ConverterSession.RaceLabel(race)} {GameDataService.OptionLabel(task.Kind, id)}";
    }

    private static IEnumerable<GearOutputModel> EditableModels(Section section)
        => section.Families.SelectMany(f => f.Models);

    private void DrawEquipButton(GearItem source, bool? worn)
    {
        var item = plugin.GameData.FindItem(source);
        var name = item?.Name ?? "the original item";
        var equipBlock = !plugin.Glamourer.IsAvailable ? "Equipping the item needs Glamourer."
            : item == null ? "No game item uses this model."
            : worn == true ? "You are already wearing it."
            : null;
        if (Widgets.IconTextButton(FontAwesomeIcon.Tshirt, $"Equip {name}", equipBlock,
                "Put the original item on your character with Glamourer. It stays until you change gear."))
            plugin.Glamourer.EquipOnPlayer(source.Slot, item!.RowId, plugin.WornGear.Stains(source.Slot));
    }

    private static void DrawFamilyDetails(Family family)
    {
        var options = family.Models.SelectMany(m => m.Options).Distinct().ToList();
        if (options.Count > 1 || options.FirstOrDefault() is { } only && only != "Default")
        {
            Widgets.Muted($"Options: {string.Join(", ", options)}");
        }
        Widgets.Muted($"{family.Models.Count} model file(s)");
        Widgets.Tooltip(string.Join("\n", family.Models.SelectMany(m => m.GamePaths.Select(p => $"{p}  ←  {m.Local}"))));
    }

    private void EnsureSections(ConversionTask task, List<(string Description, GearConversionPlan Plan)> conversions,
        bool customization)
    {
        if (ReferenceEquals(task, _builtFor)) return;
        _builtFor = task;
        _expanded.Clear();
        if (customization)
        {
            // Every model is for the one target race, so each file is its own family, named after itself.
            _sections =
            [
                new Section(string.Empty, null,
                    task.OutputModels.Where(m => m.Editable).Select(m => new Family(FamilyTitle([m]), [m])).ToList(),
                    task.OutputModels.Where(m => !m.Editable).ToList()),
            ];
            return;
        }
        _sections = conversions.Select(conversion =>
        {
            // Families stay within a conversion: another item's models are not the race versions of these.
            var models = task.OutputModels.Where(m => m.Conversion == conversion.Plan.Request).ToList();
            var families = models
                .Where(m => m.Editable)
                .GroupBy(m => string.Join("|", m.Groups.Select(g => $"{RaceCodeRegex().Replace(g.Material, "c####")}:{g.Parts}")))
                .Select(g => new Family(FamilyTitle(g.ToList()), g.OrderBy(m => m.GenderRace ?? 0).ToList()))
                .ToList();
            return new Section(conversion.Description, conversion.Plan.Request, families,
                models.Where(m => !m.Editable).ToList());
        }).ToList();
    }

    private static string FamilyTitle(List<GearOutputModel> models)
    {
        var races = models.Select(m => m.GenderRace).Where(r => r.HasValue).Select(r => ConverterSession.RaceLabel(r!.Value))
            .Distinct().ToList();
        if (races.Count == 0) return System.IO.Path.GetFileName(models[0].Local);
        return races.Count <= 3 ? string.Join(", ", races) : $"{string.Join(", ", races.Take(2))} and {races.Count - 2} more";
    }

    private static string SlotName(GearSlot slot) => slot switch
    {
        GearSlot.Ears    => "Earring",
        GearSlot.RFinger => "Ring Right",
        GearSlot.LFinger => "Ring Left",
        GearSlot.Glasses => "Facewear",
        _                => slot.ToString(),
    };
}
