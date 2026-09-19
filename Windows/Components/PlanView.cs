using System;
using System.Collections.Generic;
using System.Linq;
using System.Numerics;
using AdvancedPenumbraModConverter.Core;
using AdvancedPenumbraModConverter.Models;
using AdvancedPenumbraModConverter.Services;
using AdvancedPenumbraModConverter.Session;
using AdvancedPenumbraModConverter.Windows.Ui;
using Dalamud.Bindings.ImGui;
using Dalamud.Interface;
using Dalamud.Interface.Utility.Raii;

namespace AdvancedPenumbraModConverter.Windows.Components;

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

    private sealed record Section(string Title, string[] Headers, Cell[] Styles, List<string[]> Rows, bool Advanced = false);

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
        DrawDiagnostics(task);

        EnsureModel(task);
        var filterWidth = Math.Min(320f * Theme.Scale, ImGui.GetContentRegionAvail().X);
        ImGui.SetNextItemWidth(filterWidth);
        ImGui.InputTextWithHint("##PlanFilter", "Filter changes…", ref _filter, 256);
        ImGui.SameLine();
        var advanced = config.ShowAdvancedDetails;
        if (ImGui.Checkbox("Advanced details", ref advanced))
        {
            config.ShowAdvancedDetails = advanced;
            config.Save();
        }
        Widgets.Tooltip("Show fingerprints and bone resolution details.");

        if (config.ShowAdvancedDetails)
        {
            Widgets.Muted($"Source fingerprint: {task.SourceFingerprint}");
            Widgets.CopyOnRightClick(task.SourceFingerprint);
            Widgets.Muted($"Plan fingerprint:   {task.PlanFingerprint}");
            Widgets.CopyOnRightClick(task.PlanFingerprint);
        }
        ImGui.Spacing();

        var any = false;
        foreach (var (section, rows) in FilteredSections())
        {
            if (section.Advanced && !config.ShowAdvancedDetails) continue;
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
        var kind = task.TargetCustomizationKind is { } targetKind && targetKind != task.Kind
            ? $"{task.Kind} → {targetKind}"
            : task.Kind.ToString();
        var race = task.SourceGenderRace.HasValue || task.TargetGenderRace.HasValue
            ? $"   c{task.SourceGenderRace:D4} → c{task.TargetGenderRace:D4}"
            : string.Empty;
        ImGui.TextColored(Theme.Accent, $"{kind}: {task.OldIdPadded} → {task.NewIdPadded}{race}");

        ImGui.SameLine();
        if (task.IsApplied)
            Widgets.Badge("Applied", Theme.Success);
        else if (session.PlanIsCurrent)
            Widgets.Badge("Up to date", Theme.Info);
        else
            Widgets.Badge("Outdated: preview again", Theme.Warning);

        ImGui.SameLine();
        Widgets.Badge(task.OutputMode == ConversionOutputMode.NewMod ? "New mod" : "In place", Theme.Muted);
        if (task.GearPlan is { } plan)
        {
            ImGui.SameLine();
            Widgets.Badge(plan.Result.Format == PenumbraModFormat.Unified ? "Penumbra 1.7+ format" : "Legacy format", Theme.Muted);
        }
        ImGui.Spacing();
    }

    private static void DrawDiagnostics(ConversionTask task)
    {
        if (task.Diagnostics.Count == 0) return;

        var blockers = task.Diagnostics.Count(d => d.IsBlocker);
        var title    = blockers > 0
            ? $"{blockers} blocker(s), {task.Diagnostics.Count - blockers} warning(s)"
            : $"{task.Diagnostics.Count} warning(s)";
        ImGui.SetNextItemOpen(true, ImGuiCond.Appearing);
        bool open;
        using (ImRaii.PushColor(ImGuiCol.Text, blockers > 0 ? Theme.Danger : Theme.Warning))
            open = ImGui.CollapsingHeader($"{title}###Diagnostics");
        if (!open) return;

        using var table = ImRaii.Table("##DiagnosticsTable", 2,
            ImGuiTableFlags.RowBg | ImGuiTableFlags.BordersInnerH | ImGuiTableFlags.SizingStretchProp);
        if (!table.Success) return;
        ImGui.TableSetupColumn("Code", ImGuiTableColumnFlags.WidthFixed, 150f * Theme.Scale);
        ImGui.TableSetupColumn("Message", ImGuiTableColumnFlags.WidthStretch);
        foreach (var diagnostic in task.Diagnostics.OrderByDescending(d => d.IsBlocker))
        {
            var tint = diagnostic.IsBlocker ? Theme.Danger : Theme.Warning;
            ImGui.TableNextRow();
            ImGui.TableNextColumn();
            Widgets.Icon(diagnostic.IsBlocker ? FontAwesomeIcon.TimesCircle : FontAwesomeIcon.ExclamationTriangle, tint);
            ImGui.SameLine();
            ImGui.TextColored(tint, diagnostic.Code);
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
        _sections    = task.GearPlan is { } plan ? BuildGear(plan) : BuildCustomization(task);
        _sections.RemoveAll(s => s.Rows.Count == 0);
    }

    private static List<Section> BuildGear(GearConversionPlan plan)
    {
        var sections = new List<Section>();
        var known    = GearSections.Select(s => s.Category).ToHashSet();
        var cols     = new[] { "Scope", "From", "To" };
        var styles   = new[] { Cell.Muted, Cell.From, Cell.To };

        foreach (var (category, title) in GearSections)
            sections.Add(new Section(title, cols, styles,
                plan.Changes.Where(c => c.Category == category).Select(c => new[] { c.Scope, c.From, c.To }).ToList()));
        foreach (var group in plan.Changes.Where(c => !known.Contains(c.Category)).GroupBy(c => c.Category))
            sections.Add(new Section(group.Key, cols, styles, group.Select(c => new[] { c.Scope, c.From, c.To }).ToList()));

        sections.Add(new Section("Files written or moved", ["Operation", "Source", "Destination", "Reason"],
            [Cell.Accent, Cell.From, Cell.To, Cell.Muted],
            plan.Files.Select(f => new[]
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
                })).ToList(), Advanced: true),
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
        "dependency_files"    => "Add",
        "manipulation_insert" => "Meta",
        _                     => changeType,
    };
}
