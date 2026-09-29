using System;
using System.Collections.Generic;
using System.Linq;
using UniversalModConverter.Core;
using UniversalModConverter.Models;
using UniversalModConverter.Session;
using UniversalModConverter.Windows.Ui;
using Dalamud.Bindings.ImGui;

namespace UniversalModConverter.Windows.Components;

/// <summary>
/// The Plan tab without Advanced details. What gets converted is listed in the conversion plan
/// under the item pickers, with its icons and switches, so this only adds what changes the
/// shape of the mod: the option groups it gains, and those a new mod leaves out. The tables of
/// game paths, metadata entries and file operations live behind Advanced details.
/// </summary>
internal static class PlanSummaryView
{
    public static void Draw(ConverterSession session, ConversionTask task)
    {
        var groups = Changes(task).Where(c => c.Category == "Group").ToList();
        var created = groups.Where(c => c.From.StartsWith("new ", StringComparison.Ordinal))
            .DistinctBy(c => c.Scope).ToList();
        var dropped = groups.Where(c => c.To.StartsWith("not included", StringComparison.Ordinal))
            .Select(c => c.Scope).Distinct().ToList();

        if (created.Count > 0)
        {
            ImGui.TextUnformatted(created.Count == 1 ? "Adds an option group:" : $"Adds {created.Count} option groups:");
            foreach (var group in created)
                Widgets.MutedWrapped($"• '{group.Scope}': {group.To}");
            ImGui.Spacing();
        }
        if (dropped.Count > 0)
        {
            Widgets.MutedWrapped($"Left out of the new mod, since they hold nothing it converts: " +
                                 string.Join(", ", dropped.Select(name => $"'{name}'")) + ".");
            ImGui.Spacing();
        }

        Widgets.MutedWrapped("What gets converted is listed in the conversion plan under the item pickers. " +
                             "Turn on Advanced details to see every path, metadata entry and file it changes.");
    }

    /// <summary>The preview lines of every plan the task holds: the one conversion's, or each accepted one of a run.</summary>
    private static IEnumerable<GearPlanChange> Changes(ConversionTask task)
        => task.IsQueue
            ? task.Entries.Where(e => !e.Rejected).Select(e => e.Plan).OfType<ModFilePlan>().SelectMany(p => p.Changes)
            : task.FilePlan is ModFilePlan plan ? plan.Changes : [];
}
