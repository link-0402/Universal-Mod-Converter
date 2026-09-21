using System;
using System.IO;
using System.Linq;
using System.Numerics;
using AdvancedPenumbraModConverter.Core;
using AdvancedPenumbraModConverter.Session;
using AdvancedPenumbraModConverter.Windows.Ui;
using Dalamud.Bindings.ImGui;
using Dalamud.Interface;
using Dalamud.Interface.Utility.Raii;
using Dalamud.Interface.Windowing;

namespace AdvancedPenumbraModConverter.Windows;

/// <summary>
/// Merges two modpacks into one new mod. Some mods ship split — a base pack with the materials
/// and textures and a second one with only upscaled models — and neither works on its own.
/// State lives in <see cref="MergeSession"/>; this window only lays it out.
/// </summary>
public sealed class MergeWindow : Window, IDisposable
{
    private readonly MergeSession _merge;
    private readonly ConverterSession _session;

    private string _firstFilter = string.Empty;
    private string _secondFilter = string.Empty;
    private string _firstPath = string.Empty;
    private string _secondPath = string.Empty;
    private string _name = string.Empty;

    public MergeWindow(Plugin plugin) : base("Merge modpacks###APMCMerge", ImGuiWindowFlags.NoCollapse)
    {
        _merge = plugin.Merge;
        _session = plugin.Session;
        Size = new Vector2(620, 640);
        SizeCondition = ImGuiCond.FirstUseEver;
        SizeConstraints = new WindowSizeConstraints
        {
            MinimumSize = new Vector2(460, 360),
            MaximumSize = new Vector2(float.MaxValue, float.MaxValue),
        };
    }

    public void Dispose() { }

    public override void OnOpen() => _session.EnsureInitialized();

    public override void Draw()
    {
        Widgets.MutedWrapped("Some mods come as several modpacks, for example a base mod with the materials and " +
                             "textures and a second pack with only upscaled models. Merging puts both into one new " +
                             "mod. The two modpacks themselves are not changed.");
        ImGui.Spacing();

        Widgets.BeginAutoCard("##MergeInputs");
        try { DrawInputs(); }
        finally { Widgets.EndAutoCard(); }

        ImGui.Spacing();
        Widgets.BeginAutoCard("##MergeOutput");
        try { DrawOutput(); }
        finally { Widgets.EndAutoCard(); }

        DrawResult();

        if (_merge.Plan is { } plan && _merge.PlanIsCurrent)
        {
            ImGui.Spacing();
            DrawPlan(plan);
        }
    }

    // ── Inputs ───────────────────────────────────────────────────────────────

    private void DrawInputs()
    {
        Widgets.SectionTitle("Modpacks", FontAwesomeIcon.LayerGroup);

        DrawPicker("First modpack", "##First", _merge.FirstDirectory, _merge.SecondDirectory,
            ref _firstFilter, ref _firstPath, _merge.SetFirst);
        var swapReason = _merge.FirstDirectory.Length == 0 && _merge.SecondDirectory.Length == 0 ? "Choose the modpacks first." : null;
        if (Widgets.IconButton("##Swap", FontAwesomeIcon.ExchangeAlt, "Swap the two modpacks", swapReason))
            _merge.Swap();
        DrawPicker("Second modpack", "##Second", _merge.SecondDirectory, _merge.FirstDirectory,
            ref _secondFilter, ref _secondPath, _merge.SetSecond);

        ImGui.Spacing();
        ImGui.TextUnformatted("When both change the same file, keep the version from:");
        using (ImRaii.Disabled(_merge.FirstDirectory.Length == 0 || _merge.SecondDirectory.Length == 0))
        {
            if (ImGui.RadioButton($"{Label(_merge.FirstName, "the first modpack")}##WinFirst", _merge.Winner == 0))
                _merge.SetWinner(0);
            if (ImGui.RadioButton($"{Label(_merge.SecondName, "the second modpack")}##WinSecond", _merge.Winner == 1))
                _merge.SetWinner(1);
        }

        if (_merge.Winner is { } winner)
        {
            var (kept, other) = winner == 0 ? (_merge.FirstName, _merge.SecondName) : (_merge.SecondName, _merge.FirstName);
            Widgets.MutedWrapped($"Where both modpacks change the same game file or metadata entry, '{kept}' replaces " +
                                 $"'{other}'. Its options also take precedence when options of both are enabled. " +
                                 "Usually the pack with the upscaled or newer files should win.");
        }
        else
            Widgets.MutedWrapped("There is no default: choose which modpack wins before the merge is planned.");
    }

    private static string Label(string name, string fallback) => name.Length > 0 ? name : fallback;

    private void DrawPicker(string label, string id, string current, string other, ref string filter, ref string path,
        Action<string> set)
    {
        ImGui.TextUnformatted(label);

        if (!_session.PenumbraAvailable)
        {
            // Without Penumbra there is no mod list; a folder path works just as well.
            if (path.Length == 0 && current.Length > 0) path = current;
            ImGui.SetNextItemWidth(-1);
            if (ImGui.InputTextWithHint($"{id}Path", "Folder of the modpack (the one with meta.json)", ref path, 512))
                set(path);
            return;
        }

        ImGui.SetNextItemWidth(-1);
        using var combo = ImRaii.Combo(id, current.Length == 0 ? "Choose a modpack…" : _merge.ModName(current),
            ImGuiComboFlags.HeightLarge);
        if (!combo.Success) return;

        if (ImGui.IsWindowAppearing()) ImGui.SetKeyboardFocusHere();
        ImGui.SetNextItemWidth(-1);
        ImGui.InputTextWithHint($"{id}Filter", "Filter", ref filter, 128);

        var text = filter;
        var mods = _session.Mods
            .Where(m => text.Length == 0 || m.Name.Contains(text, StringComparison.OrdinalIgnoreCase))
            .ToList();
        if (mods.Count == 0) Widgets.Muted("No modpack matches.");
        Widgets.Clipped(mods.Count, ImGui.GetTextLineHeightWithSpacing(), i =>
        {
            var mod = mods[i];
            var taken = ConverterSession.SamePath(mod.Directory, other);
            if (ImGui.Selectable($"{mod.Name}##{mod.Directory}", ConverterSession.SamePath(mod.Directory, current),
                    taken ? ImGuiSelectableFlags.Disabled : ImGuiSelectableFlags.None))
                set(mod.Directory);
        });
    }

    // ── Output ───────────────────────────────────────────────────────────────

    private void DrawOutput()
    {
        Widgets.SectionTitle("Merged mod", FontAwesomeIcon.FileExport);

        if (_name != _merge.Name) _name = _merge.Name;
        ImGui.TextUnformatted("Name of the merged mod");
        ImGui.SameLine();
        Widgets.Muted("(how it appears in Penumbra)");
        var buttonWidth = ImGui.GetFrameHeight();
        ImGui.SetNextItemWidth(ImGui.GetContentRegionAvail().X - buttonWidth - ImGui.GetStyle().ItemSpacing.X);
        if (ImGui.InputTextWithHint("##MergeName", "Name of the merged mod", ref _name, 256))
            _merge.SetName(_name);
        ImGui.SameLine();
        if (Widgets.IconButton("##ResetMergeName", FontAwesomeIcon.Undo, "Use the suggested name"))
            _merge.ResetName();

        if (_merge.OutputPath is { } path)
        {
            var exists = Directory.Exists(path) || File.Exists(path);
            var color = exists ? Theme.Danger : Theme.Muted;
            Widgets.Icon(exists ? FontAwesomeIcon.ExclamationCircle : FontAwesomeIcon.FolderPlus, color);
            ImGui.SameLine();
            ImGui.TextColored(color, exists ? "Already exists:" : "Will be created at:");
            ImGui.SameLine();
            Widgets.PathText(path, color);
        }

        ImGui.Spacing();
        DrawStatus();
        ImGui.Spacing();

        var reason = _merge.CreateBlockReason;
        if (Widgets.Button("Create merged mod##CreateMerge", reason, new Vector2(180f * Theme.Scale, 0), primary: reason == null))
            _merge.Create();
    }

    private void DrawStatus()
    {
        if (_merge.IsBusy)
        {
            Widgets.Spinner(Theme.Accent);
            ImGui.SameLine();
            ImGui.TextUnformatted($"{_session.Runner.CurrentLabel} {_session.Runner.Elapsed.TotalSeconds:0}s");
            return;
        }

        if (_merge.PlanError is { } error && _merge.PlanIsCurrent)
        {
            Widgets.Icon(FontAwesomeIcon.TimesCircle, Theme.Danger);
            ImGui.SameLine();
            Widgets.ColoredWrapped(Theme.Danger, $"These modpacks cannot be merged: {error}");
            return;
        }

        if (_merge.CreateBlockReason is not { } reason)
        {
            var conflicts = _merge.Plan?.Conflicts.Count ?? 0;
            Widgets.Icon(FontAwesomeIcon.CheckCircle, Theme.Success);
            ImGui.SameLine();
            ImGui.TextColored(Theme.Success, conflicts switch
            {
                0 => "Ready to merge. The two modpacks change nothing in common.",
                1 => "Ready to merge. One thing both modpacks change is listed below.",
                _ => $"Ready to merge. {conflicts} things both modpacks change are listed below.",
            });
            return;
        }

        Widgets.Icon(FontAwesomeIcon.InfoCircle, Theme.Muted);
        ImGui.SameLine();
        Widgets.MutedWrapped(reason);
    }

    private void DrawResult()
    {
        if (_merge.Result is not { } result) return;

        var color = Theme.For(result.Kind);
        ImGui.Spacing();
        using var table = ImRaii.Table("##MergeResult", 1, ImGuiTableFlags.BordersOuter | ImGuiTableFlags.PadOuterX);
        if (!table.Success) return;
        ImGui.TableNextRow();
        ImGui.TableSetBgColor(ImGuiTableBgTarget.RowBg0, ImGui.GetColorU32(color.WithAlpha(0.12f)));
        ImGui.TableNextColumn();
        ImGui.Dummy(new Vector2(0, 2f * Theme.Scale));

        Widgets.Icon(result.Kind switch
        {
            BannerKind.Success => FontAwesomeIcon.CheckCircle,
            BannerKind.Warning => FontAwesomeIcon.ExclamationTriangle,
            BannerKind.Error   => FontAwesomeIcon.TimesCircle,
            _                  => FontAwesomeIcon.InfoCircle,
        }, color);
        ImGui.SameLine();
        ImGui.TextColored(color, result.Title);
        ImGui.SameLine();
        ImGui.SetCursorPosX(ImGui.GetCursorPosX() + ImGui.GetContentRegionAvail().X - ImGui.GetFrameHeight());
        if (Widgets.IconButton("##DismissMergeResult", FontAwesomeIcon.Times, "Dismiss"))
            _merge.Result = null;

        ImGui.TextWrapped(result.Message);
        if (result.Path is { } path && Directory.Exists(path))
        {
            if (Widgets.IconTextButton(FontAwesomeIcon.FolderOpen, "Open folder"))
                ConverterSession.OpenFolder(path);
            ImGui.SameLine();
            Widgets.Muted("It can be removed again from the History tab of the converter.");
        }
        ImGui.Dummy(new Vector2(0, 2f * Theme.Scale));
    }

    // ── Plan ─────────────────────────────────────────────────────────────────

    private static void DrawPlan(ModMergePlan plan)
    {
        Widgets.SectionTitle("What the merged mod contains", FontAwesomeIcon.ListUl);

        ImGui.TextUnformatted($"{plan.Copies.Count} file(s) from both modpacks.");
        if (plan.GroupsMerged > 0 || plan.GroupsAdded > 0)
            ImGui.TextUnformatted($"{plan.GroupsMerged} option group(s) merged, {plan.GroupsAdded} added from '{plan.OverlayName}'.");
        if (plan.SharedFiles > 0)
            Widgets.Muted($"{plan.SharedFiles} identical file(s) are stored once.");
        if (plan.RenamedFiles > 0)
            Widgets.Muted($"{plan.RenamedFiles} file(s) were renamed inside the mod folder, since both modpacks kept a " +
                          "different file under the same name. The game never sees these names.");

        foreach (var note in plan.Notes)
            Widgets.MutedWrapped("• " + note);

        if (plan.MissingFiles.Count > 0)
        {
            ImGui.Spacing();
            Widgets.ColoredWrapped(Theme.Warning, $"{plan.MissingFiles.Count} file(s) the modpacks point at are missing " +
                                                  "from their folders and are left out:");
            foreach (var missing in plan.MissingFiles.Take(20))
                Widgets.MutedWrapped("  " + missing);
            if (plan.MissingFiles.Count > 20) Widgets.Muted($"  …and {plan.MissingFiles.Count - 20} more.");
        }

        ImGui.Spacing();
        if (plan.Conflicts.Count == 0)
        {
            Widgets.Muted("Nothing overlaps: every file and entry of both modpacks is kept.");
            return;
        }

        ImGui.TextUnformatted($"Changed by both, kept from '{plan.OverlayName}' ({plan.Conflicts.Count}):");
        var rowHeight = ImGui.GetTextLineHeightWithSpacing();
        var height = Math.Min(plan.Conflicts.Count + 1, 12) * rowHeight + 8f * Theme.Scale;
        using var table = ImRaii.Table("##MergeConflicts", 2,
            ImGuiTableFlags.RowBg | ImGuiTableFlags.BordersInnerV | ImGuiTableFlags.ScrollY | ImGuiTableFlags.Resizable,
            new Vector2(-1, height));
        if (!table.Success) return;
        ImGui.TableSetupScrollFreeze(0, 1);
        ImGui.TableSetupColumn("Game file or entry", ImGuiTableColumnFlags.WidthStretch, 0.65f);
        ImGui.TableSetupColumn("Where", ImGuiTableColumnFlags.WidthStretch, 0.35f);
        ImGui.TableHeadersRow();
        Widgets.Clipped(plan.Conflicts.Count, rowHeight, i =>
        {
            var conflict = plan.Conflicts[i];
            ImGui.TableNextRow();
            ImGui.TableNextColumn();
            Widgets.PathText(conflict.What, Theme.Text);
            ImGui.TableNextColumn();
            ImGui.TextUnformatted(conflict.Where);
        });
    }
}
