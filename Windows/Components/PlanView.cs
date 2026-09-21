using System;
using System.Collections.Generic;
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

/// <summary>The previewed plan: diagnostics first, then one filterable table per change category.</summary>
internal sealed class PlanView(ConverterSession session, Configuration config)
{
    private enum Cell
    {
        Plain,
        Muted,
        From,
        To,
        Accent,
    }

    private sealed record Section(string Title, string[] Headers, Cell[] Styles, List<string[]> Rows);

    private static readonly (string Category, string Title)[] GearSections =
    [
        ("Game path", "Game paths"),
        ("File swap", "File swaps"),
        ("Reference", "References inside models, materials and effects"),
        ("Game dependency", "Game files copied into the mod"),
        ("Metadata", "Metadata (EQP, EQDP, IMC, EST, …)"),
        ("IMC group", "IMC option groups"),
        ("Group", "Option groups"),
    ];

    private string _filter = string.Empty;
    private ConversionTask? _builtFor;
    private List<Section> _sections = new();
    private string _filteredFor = string.Empty;
    private List<(Section Section, List<string[]> Rows)> _filtered = new();

    public void Draw()
    {
        var task = session.Task;
        if (!task.IsPlanned)
        {
            DrawEmpty(task);
            return;
        }

        DrawHeader(task);
        DrawDiagnostics(task, config);

        var advanced = config.ShowAdvancedDetails;
        if (ImGui.Checkbox("Advanced details", ref advanced))
        {
            config.ShowAdvancedDetails = advanced;
            config.Save();
        }
        Widgets.Tooltip("Every game path, metadata entry and file operation the conversion produces, " +
                        "plus fingerprints and bone resolution.");

        ImGui.Spacing();
        if (!config.ShowAdvancedDetails)
        {
            PlanSummaryView.Draw(session, task);
            return;
        }

        EnsureModel(task);
        var filterWidth = Math.Min(320f * Theme.Scale, ImGui.GetContentRegionAvail().X);
        ImGui.SetNextItemWidth(filterWidth);
        ImGui.InputTextWithHint("##PlanFilter", "Filter changes…", ref _filter, 256);

        Widgets.Muted($"Source fingerprint: {task.SourceFingerprint}");
        Widgets.CopyOnRightClick(task.SourceFingerprint);
        Widgets.Muted($"Plan fingerprint:   {task.PlanFingerprint}");
        Widgets.CopyOnRightClick(task.PlanFingerprint);
        ImGui.Spacing();

        var any = false;
        foreach (var (section, rows) in FilteredSections())
        {
            any = true;
            DrawSection(section, rows);
        }
        if (!any)
            Widgets.Muted(_filter.Length > 0 ? "No changes match the filter." : "This conversion changes nothing.");
    }

    private void DrawEmpty(ConversionTask task)
    {
        if (session.Runner.CurrentLabel == "Planning…")
        {
            Widgets.Spinner(Theme.Accent);
            ImGui.SameLine();
            Widgets.Muted("Planning the conversion…");
            return;
        }

        if (!string.IsNullOrEmpty(task.ErrorMessage))
        {
            Widgets.ColoredWrapped(Theme.Danger, task.ErrorMessage);
            ImGui.Spacing();
        }
        Widgets.MutedWrapped("Preview the conversion to see every path, reference and metadata entry it changes. " +
                             "Nothing is written until you apply it.");
    }

    private void DrawHeader(ConversionTask task)
    {
        if (task.IsQueue)
        {
            var accepted = task.Entries.Count(e => e.Enabled && !e.Rejected);
            ImGui.TextColored(Theme.Accent, accepted == 1
                ? "One conversion in this run"
                : $"{accepted} conversions in this run");
            DrawBadges(task);
            return;
        }

        if (task.AnimationPlan is { } animation)
        {
            ImGui.TextColored(Theme.Accent, animation.Request.Description);
            DrawBadges(task);
            return;
        }

        var kind = task.TargetCustomizationKind is { } targetKind && targetKind != task.Kind
            ? $"{task.Kind} → {targetKind}"
            : task.Kind.ToString();
        var race = task.SourceGenderRace.HasValue || task.TargetGenderRace.HasValue
            ? $"   {RaceNames.Describe(task.SourceGenderRace ?? 0)} → {RaceNames.Describe(task.TargetGenderRace ?? 0)}"
            : string.Empty;
        ImGui.TextColored(Theme.Accent, $"{kind}: {task.OldIdPadded} → {task.NewIdPadded}{race}");
        DrawBadges(task);
    }

    private void DrawBadges(ConversionTask task)
    {
        ImGui.SameLine();
        if (task.IsApplied)
            Widgets.Badge("Applied", Theme.Success);
        else if (session.PlanIsCurrent)
            Widgets.Badge("Up to date", Theme.Info);
        else
            Widgets.Badge("Outdated: preview again", Theme.Warning);

        ImGui.SameLine();
        Widgets.Badge(OutputModeBadge(task.OutputMode), Theme.Muted);
        ImGui.Spacing();
    }

    internal static string OutputModeBadge(ConversionOutputMode mode) => mode switch
    {
        ConversionOutputMode.NewMod   => "New mod",
        ConversionOutputMode.AddToMod => "Added to mod",
        _                             => "In place",
    };

    private static void DrawDiagnostics(ConversionTask task, Configuration config)
    {
        if (task.Diagnostics.Count == 0) return;

        var blockers = task.Diagnostics.Count(d => d.IsBlocker);
        var notes    = task.Diagnostics.Count - blockers;
        var title    = blockers > 0
            ? $"{blockers} problem(s) to fix" + (notes > 0 ? $", {notes} thing(s) to check" : string.Empty)
            : $"{notes} thing(s) to check";
        ImGui.SetNextItemOpen(true, ImGuiCond.Appearing);
        bool open;
        using (ImRaii.PushColor(ImGuiCol.Text, blockers > 0 ? Theme.Danger : Theme.Warning))
            open = ImGui.CollapsingHeader($"{title}###Diagnostics");
        if (!open) return;

        using var table = ImRaii.Table("##DiagnosticsTable", 2,
            ImGuiTableFlags.RowBg | ImGuiTableFlags.BordersInnerH | ImGuiTableFlags.SizingStretchProp);
        if (!table.Success) return;
        ImGui.TableSetupColumn("What", ImGuiTableColumnFlags.WidthFixed, 210f * Theme.Scale);
        ImGui.TableSetupColumn("Message", ImGuiTableColumnFlags.WidthStretch);
        foreach (var diagnostic in task.Diagnostics.OrderByDescending(d => d.IsBlocker))
        {
            var tint = diagnostic.IsBlocker ? Theme.Danger : Theme.Warning;
            ImGui.TableNextRow();
            ImGui.TableNextColumn();
            Widgets.Icon(diagnostic.IsBlocker ? FontAwesomeIcon.TimesCircle : FontAwesomeIcon.ExclamationTriangle, tint);
            ImGui.SameLine();
            // The raw code is what to quote in a bug report, so it stays one hover away.
            ImGui.TextColored(tint, config.ShowAdvancedDetails
                ? diagnostic.Code
                : DiagnosticText.Title(diagnostic.Code));
            if (!config.ShowAdvancedDetails && DiagnosticText.HasTitle(diagnostic.Code))
                Widgets.Tooltip(diagnostic.Code);
            Widgets.CopyOnRightClick($"[{diagnostic.Code}] {diagnostic.Message}", false);
            ImGui.TableNextColumn();
            ImGui.TextWrapped(diagnostic.Message);
        }
        ImGui.Spacing();
    }

    private static void DrawSection(Section section, List<string[]> rows)
    {
        ImGui.SetNextItemOpen(rows.Count <= 200, ImGuiCond.Appearing);
        if (!ImGui.CollapsingHeader($"{section.Title}  ({rows.Count})###{section.Title}"))
            return;

        var rowHeight = ImGui.GetTextLineHeightWithSpacing();
        var height    = Math.Min(rows.Count + 1, 14) * (rowHeight + 2f * Theme.Scale) + ImGui.GetStyle().ScrollbarSize;
        using var table = ImRaii.Table($"##{section.Title}", section.Headers.Length,
            ImGuiTableFlags.RowBg | ImGuiTableFlags.BordersInnerV | ImGuiTableFlags.Resizable |
            ImGuiTableFlags.ScrollY | ImGuiTableFlags.SizingStretchProp, new Vector2(-1, height));
        if (!table.Success) return;

        ImGui.TableSetupScrollFreeze(0, 1);
        for (var c = 0; c < section.Headers.Length; c++)
        {
            var narrow = section.Styles[c] is Cell.Muted or Cell.Accent;
            ImGui.TableSetupColumn(section.Headers[c], ImGuiTableColumnFlags.WidthStretch, narrow ? 0.4f : 1f);
        }
        ImGui.TableHeadersRow();

        Widgets.Clipped(rows.Count, rowHeight, i =>
        {
            var row = rows[i];
            ImGui.TableNextRow();
            for (var c = 0; c < row.Length; c++)
            {
                ImGui.TableNextColumn();
                var text = row[c];
                ImGui.TextColored(section.Styles[c] switch
                {
                    Cell.Muted  => Theme.Muted,
                    Cell.From   => Theme.From,
                    Cell.To     => Theme.To,
                    Cell.Accent => Theme.Accent,
                    _           => Theme.Text,
                }, text);
                if (text.Length > 0 && ImGui.IsItemHovered())
                    Widgets.CopyOnRightClick(text, ImGui.GetItemRectSize().X > ImGui.GetColumnWidth());
            }
        });
    }

    // ─────────────────────────────────────────────────────────────────────────
    // Model
    // ─────────────────────────────────────────────────────────────────────────

    private List<(Section Section, List<string[]> Rows)> FilteredSections()
    {
        if (_filteredFor == _filter) return _filtered;
        _filteredFor = _filter;
        var q = _filter.Trim();
        _filtered = _sections
            .Select(s => (s, q.Length == 0
                ? s.Rows
                : s.Rows.Where(r => r.Any(c => c.Contains(q, StringComparison.OrdinalIgnoreCase))).ToList()))
            .Where(s => s.Item2.Count > 0 || q.Length == 0)
            .ToList();
        return _filtered;
    }

    private void EnsureModel(ConversionTask task)
    {
        if (ReferenceEquals(task, _builtFor)) return;
        _builtFor    = task;
        _filteredFor = "\0"; // force refilter
        _filtered    = new();
        _sections    = task.IsQueue ? BuildRun(task)
            : task.GearPlan is { } plan ? BuildGear(plan.Changes, plan.Files)
            : task.AnimationPlan is { } animation ? BuildGear(animation.Changes, animation.Files, AnimationSections)
            : BuildCustomization(task);
        _sections.RemoveAll(s => s.Rows.Count == 0);
    }

    private static readonly (string Category, string Title)[] AnimationSections =
    [
        ("Game path", "Game paths"),
        ("Retarget", "Retargeted animations"),
        ("Option", "Option group contents"),
        ("Group", "Option groups"),
    ];

    /// <summary>
    /// The tables for a run of several conversions: the same categories, with every row saying
    /// which conversion produced it. Splitting them into one set of tables per conversion would
    /// bury the thing the advanced view is for — seeing the whole output at once.
    /// </summary>
    private static List<Section> BuildRun(ConversionTask task)
    {
        var changes = new List<GearPlanChange>();
        var files   = new List<PlannedFileOperation>();
        var gear    = false;
        foreach (var entry in task.Entries.Where(e => !e.Rejected && e.Plan != null))
        {
            switch (entry.Plan)
            {
                case GearConversionPlan plan:
                    gear = true;
                    changes.AddRange(plan.Changes.Select(c => c with { Scope = $"{entry.Description} · {c.Scope}" }));
                    files.AddRange(plan.Files);
                    break;
                case AnimationConversionPlan animation:
                    changes.AddRange(animation.Changes.Select(c => c with { Scope = $"{entry.Description} · {c.Scope}" }));
                    files.AddRange(animation.Files);
                    break;
            }
        }

        // Animation categories are a subset of the gear ones plus two of their own, so a mixed
        // run uses whichever set covers what it actually produced; unknown ones get their own table.
        return BuildGear(changes, files, gear ? GearSections : AnimationSections);
    }

    private static List<Section> BuildGear(IReadOnlyList<GearPlanChange> changes, IReadOnlyList<PlannedFileOperation> files,
        (string Category, string Title)[]? categories = null)
    {
        categories ??= GearSections;
        var sections = new List<Section>();
        var known    = categories.Select(s => s.Category).ToHashSet();
        var cols     = new[] { "Scope", "From", "To" };
        var styles   = new[] { Cell.Muted, Cell.From, Cell.To };

        foreach (var (category, title) in categories)
            sections.Add(new Section(title, cols, styles,
                changes.Where(c => c.Category == category).Select(c => new[] { c.Scope, c.From, c.To }).ToList()));
        foreach (var group in changes.Where(c => !known.Contains(c.Category)).GroupBy(c => c.Category))
            sections.Add(new Section(group.Key, cols, styles, group.Select(c => new[] { c.Scope, c.From, c.To }).ToList()));

        sections.Add(new Section("Files written or moved", ["Operation", "Source", "Destination", "Reason"],
            [Cell.Accent, Cell.From, Cell.To, Cell.Muted],
            files.Select(f => new[]
            {
                f.Operation.ToString(),
                f.Source != null && f.Operation != LocalFileOperation.Write ? f.Source : string.Empty,
                f.Destination,
                f.Reason,
            }).ToList()));
        return sections;
    }

    private static List<Section> BuildCustomization(ConversionTask task)
    {
        string Rel(string path) => ModConverterService.RelativePath(task.ModDirectory, path);

        return
        [
            new Section("File renames", ["Type", "From", "To"], [Cell.Muted, Cell.From, Cell.To],
                task.PlannedRenames.Select(r => new[] { r.IsDir ? "Folder" : "File", Rel(r.OldPath), Rel(r.NewPath) }).ToList()),
            new Section("Metadata", ["File", "Kind", "Field", "From", "To"], [Cell.Accent, Cell.Muted, Cell.Muted, Cell.From, Cell.To],
                task.PlannedJsonChanges.SelectMany(j => j.Changes.Select(c => new[]
                    { Rel(j.FilePath), ChangeTypeLabel(c.ChangeType), c.JsonPath, c.OldValue, c.NewValue })).ToList()),
            new Section("References inside models and materials", ["File", "From", "To"], [Cell.Accent, Cell.From, Cell.To],
                task.PlannedBinaryPatches.SelectMany(b => b.Patches.Select(p => new[] { Rel(b.FilePath), p.OldString, p.NewString })).ToList()),
            new Section("Model rewrites", ["File", "Change"], [Cell.Accent, Cell.Plain],
                task.PlannedMdlChanges.Select(m => new[] { Rel(m.FilePath), MdlSummary(m) }).ToList()),
            new Section("Game files copied into the mod", ["Game path", "File"], [Cell.To, Cell.Muted],
                task.PlannedGeneratedFiles.Select(g => new[] { g.GamePath, Rel(g.FilePath) }).ToList()),
            new Section("Bone resolution", ["File", "Step", "Bone", "Resolved to", "Strategy"],
                [Cell.Accent, Cell.Muted, Cell.From, Cell.To, Cell.Muted],
                task.PlannedMdlChanges.SelectMany(m => m.BoneResolutions.Select(r => new[]
                {
                    Rel(m.FilePath),
                    $"c{r.StepRace:D4} {(r.Inverse ? "inverse" : "forward")}",
                    r.Bone,
                    r.ResolvedBone ?? "identity",
                    r.Strategy.ToString(),
                })).ToList()),
        ];
    }

    private static string MdlSummary(PlannedMdlChange mdl)
    {
        var summary = mdl.GeometryConverted
            ? $"v{mdl.Version}  c{mdl.SourceGenderRace:D4} → c{mdl.TargetGenderRace:D4}  ·  {mdl.LodCount} LOD, " +
              $"{mdl.MeshCount} meshes, {mdl.VertexCount} vertices, {mdl.ShapeVertexCount} shape vertices"
            : $"v{mdl.Version}  string table rebuilt ({mdl.PathReplacements.Count} path replacements)";
        var heuristic = mdl.BoneResolutions.Count(r => r.Strategy != BoneResolutionStrategy.Identity);
        return heuristic > 0 ? $"{summary}  ·  {heuristic} bone(s) inherited/heuristic" : summary;
    }

    private static string ChangeTypeLabel(string changeType) => changeType switch
    {
        "path_key"            => "Key",
        "path_value"          => "Path",
        "path_string"         => "String",
        "numeric_id"          => "ID",
        "numeric_id_string"   => "ID",
        "path_key_copy"       => "Copy",
        "path_key_delete"     => "Remove",
        "dependency_files"    => "Add",
        "manipulation_insert" => "Meta",
        "group_insert"        => "New group",
        _                     => changeType,
    };
}
