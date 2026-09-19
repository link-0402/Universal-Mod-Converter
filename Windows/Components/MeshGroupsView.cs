using System;
using System.Collections.Generic;
using System.Linq;
using System.Numerics;
using System.Text.RegularExpressions;
using AdvancedPenumbraModConverter.Core;
using AdvancedPenumbraModConverter.Models;
using AdvancedPenumbraModConverter.Session;
using AdvancedPenumbraModConverter.Windows.Ui;
using Dalamud.Bindings.ImGui;
using Dalamud.Interface;
using Dalamud.Interface.Utility.Raii;

namespace AdvancedPenumbraModConverter.Windows.Components;

/// <summary>
/// The mesh groups of every model the output ships for the target item, with a Keep
/// checkbox per group. Models with the same layout (normally the race versions of one
/// model) form a family and are edited together.
/// </summary>
internal sealed partial class MeshGroupsView(ConverterSession session)
{
    private sealed record Family(string Title, List<GearOutputModel> Models);

    [GeneratedRegex(@"c\d{4}", RegexOptions.CultureInvariant)]
    private static partial Regex RaceCodeRegex();

    private ConversionTask? _builtFor;
    private List<Family> _families = new();
    private List<GearOutputModel> _readOnly = new();

    /// <summary>Number of models the tab lists for the current plan (0 when there is nothing to show).</summary>
    public int ModelCount => session.Task.IsPlanned ? session.Task.OutputModels.Count : 0;

    public void Draw()
    {
        var task = session.Task;
        if (!task.IsPlanned)
        {
            Widgets.MutedWrapped("Preview the conversion to see the mesh groups of the converted models.");
            return;
        }
        if (task.GearPlan is not { } plan)
        {
            Widgets.MutedWrapped("Mesh groups can be edited for gear and facewear conversions.");
            return;
        }

        if (session.PlanIsCrossSlot)
            DrawCrossSlotNotice(SlotName(plan.Request.Source.Slot), SlotName(plan.Request.Target.Slot));

        if (task.OutputModels.Count == 0)
        {
            Widgets.MutedWrapped("The output contains no model for the target item.");
            return;
        }

        EnsureFamilies(task);
        Widgets.MutedWrapped("Untick a mesh group to leave it out of the converted model. Models with the same layout " +
                             "(usually the race versions of one model) are edited together. The source mod is never changed.");
        if (session.MeshEditBlockReason is { } reason)
            Widgets.ColoredWrapped(Theme.Warning, reason);
        ImGui.Spacing();

        for (var i = 0; i < _families.Count; i++)
            DrawFamily(i, _families[i]);

        foreach (var model in _readOnly)
        {
            Widgets.Icon(FontAwesomeIcon.Lock, Theme.Muted);
            ImGui.SameLine();
            Widgets.PathText(model.Local, Theme.Muted, ImGui.GetContentRegionAvail().X * 0.5f);
            ImGui.SameLine();
            Widgets.ColoredWrapped(Theme.Warning, model.EditError ?? "No mesh groups.");
        }
    }

    public static void DrawCrossSlotNotice(string from, string to)
    {
        using var table = ImRaii.Table("##CrossSlotNotice", 1, ImGuiTableFlags.BordersOuter | ImGuiTableFlags.PadOuterX);
        if (!table.Success) return;
        ImGui.TableNextRow();
        ImGui.TableSetBgColor(ImGuiTableBgTarget.RowBg0, ImGui.GetColorU32(Theme.Warning.WithAlpha(0.12f)));
        ImGui.TableNextColumn();
        Widgets.Icon(FontAwesomeIcon.ExclamationTriangle, Theme.Warning);
        ImGui.SameLine();
        ImGui.TextColored(Theme.Warning, $"{from} → {to}: the model itself is not changed");
        ImGui.TextWrapped($"Converting between slots only changes which item loads the model. The {from.ToLowerInvariant()} " +
                          $"geometry stays in it: a body model converted to hands still contains the body mesh and is drawn " +
                          $"whenever the {to.ToLowerInvariant()} item is worn. Untick the mesh groups that do not belong on the " +
                          "new slot below.");
        ImGui.Spacing();
    }

    private void DrawFamily(int index, Family family)
    {
        var first   = family.Models[0];
        var removed = session.RemovedMeshGroupCount(first);
        var header  = removed > 0
            ? $"{family.Title}  ·  {first.Groups.Count - removed} of {first.Groups.Count} mesh groups kept###Family{index}"
            : $"{family.Title}  ·  {first.Groups.Count} mesh groups###Family{index}";
        ImGui.SetNextItemOpen(true, ImGuiCond.Appearing);
        if (!ImGui.CollapsingHeader(header)) return;
        DrawFamilyDetails(family);

        var blocked = session.MeshEditBlockReason;
        using var id = ImRaii.PushId(index);
        using (var table = ImRaii.Table("##Groups", 6,
                   ImGuiTableFlags.RowBg | ImGuiTableFlags.BordersInnerV | ImGuiTableFlags.SizingStretchProp))
        {
            if (table.Success)
            {
                ImGui.TableSetupColumn("Keep", ImGuiTableColumnFlags.WidthFixed, ImGui.GetFrameHeight() + 4f * Theme.Scale);
                ImGui.TableSetupColumn("#", ImGuiTableColumnFlags.WidthFixed, 24f * Theme.Scale);
                ImGui.TableSetupColumn("Material", ImGuiTableColumnFlags.WidthStretch, 1.4f);
                ImGui.TableSetupColumn("Triangles", ImGuiTableColumnFlags.WidthFixed, 80f * Theme.Scale);
                ImGui.TableSetupColumn("Parts", ImGuiTableColumnFlags.WidthFixed, 40f * Theme.Scale);
                ImGui.TableSetupColumn("Attributes", ImGuiTableColumnFlags.WidthStretch, 1f);
                ImGui.TableHeadersRow();

                for (var g = 0; g < first.Groups.Count; g++)
                    DrawGroupRow(family, g, removed, blocked);
            }
        }

        if (removed > 0 && Widgets.IconTextButton(FontAwesomeIcon.Undo, "Keep all", blocked))
            session.KeepAllMeshGroups(family.Models);
        ImGui.Spacing();
    }

    private void DrawGroupRow(Family family, int g, int removedInFamily, string? blocked)
    {
        var group     = family.Models[0].Groups[g];
        var isRemoved = session.IsMeshGroupRemoved(family.Models[0], g);
        var lastKept  = !isRemoved && removedInFamily == family.Models[0].Groups.Count - 1;
        var text      = isRemoved ? Theme.Muted : Theme.Text;

        ImGui.TableNextRow();
        ImGui.TableNextColumn();
        var keep = !isRemoved;
        using (ImRaii.Disabled(blocked != null || lastKept))
        {
            if (ImGui.Checkbox($"##Keep{g}", ref keep))
                session.SetMeshGroupRemoved(family.Models, g, !keep);
        }
        Widgets.Tooltip(blocked ?? (lastKept ? "A model must keep at least one mesh group." : keep ? "Untick to remove this mesh group." : "Tick to keep this mesh group."));

        ImGui.TableNextColumn();
        ImGui.TextColored(text, g.ToString());

        ImGui.TableNextColumn();
        if (group.IsSkin)
        {
            Widgets.Badge("Skin", Theme.Info);
            Widgets.Tooltip("Uses the character's skin material: exposed body parts such as arms, legs or the torso.");
            ImGui.SameLine();
        }
        ImGui.TextColored(text, Widgets.Ellipsize(group.Material, ImGui.GetContentRegionAvail().X));
        Widgets.CopyOnRightClick(group.Material);

        ImGui.TableNextColumn();
        var triangles = family.Models.Where(m => g < m.Groups.Count).Select(m => m.Groups[g].Triangles).ToList();
        var min = triangles.Min();
        var max = triangles.Max();
        ImGui.TextColored(text, min == max ? $"{min:N0}" : $"{min:N0}–{max:N0}");
        if (min != max) Widgets.Tooltip("The race versions of this model differ in detail.");

        ImGui.TableNextColumn();
        ImGui.TextColored(text, group.Parts.ToString());

        ImGui.TableNextColumn();
        var attributes = group.Attributes.IsDefaultOrEmpty ? "–" : string.Join(", ", group.Attributes);
        ImGui.TextColored(Theme.Muted, Widgets.Ellipsize(attributes, ImGui.GetContentRegionAvail().X));
        if (!group.Attributes.IsDefaultOrEmpty)
            Widgets.Tooltip(string.Join("\n", group.Attributes));
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

    private void EnsureFamilies(ConversionTask task)
    {
        if (ReferenceEquals(task, _builtFor)) return;
        _builtFor = task;
        _readOnly = task.OutputModels.Where(m => !m.Editable).ToList();
        _families = task.OutputModels
            .Where(m => m.Editable)
            .GroupBy(m => string.Join("|", m.Groups.Select(g => RaceCodeRegex().Replace(g.Material, "c####"))))
            .Select(g => new Family(FamilyTitle(g.ToList()), g.OrderBy(m => m.GenderRace ?? 0).ToList()))
            .ToList();
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
