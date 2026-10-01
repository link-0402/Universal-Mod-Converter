using System;
using System.IO;
using System.Linq;
using System.Numerics;
using UniversalModConverter.Core;
using UniversalModConverter.Session;
using UniversalModConverter.Windows.Ui;
using Dalamud.Bindings.ImGui;
using Dalamud.Interface;
using Dalamud.Interface.Utility.Raii;

namespace UniversalModConverter.Windows.Components;

/// <summary>Output options, readiness, Preview/Apply buttons and the result banner.</summary>
internal sealed class ActionPanels(ConverterSession session, Configuration config, ConfirmDialog confirm)
{
    private string _newModName = string.Empty;

    // ─────────────────────────────────────────────────────────────────────────
    // Output
    // ─────────────────────────────────────────────────────────────────────────

    public void DrawOutput()
    {
        Widgets.SectionTitle("Output", FontAwesomeIcon.FileExport);

        if (session.UsesTextureOutput)
        {
            DrawTextureOutput();
            return;
        }

        var mode = session.EffectiveOutputMode;
        // Each option's tooltip sits on the option itself, so it also says why a disabled one is off.
        if (ImGui.RadioButton("Create a new mod", mode.IsNewMod()))
            session.SetOutputMode(ConversionOutputMode.NewMod);
        Widgets.Tooltip("Safest: the result goes into a separate new mod, and this mod is never modified.");

        var additiveBlock = session.AddToModBlockReason;
        using (ImRaii.Disabled(additiveBlock != null))
        {
            if (ImGui.RadioButton("Add to this mod", mode == ConversionOutputMode.AddToMod))
                session.SetOutputMode(ConversionOutputMode.AddToMod);
        }
        Widgets.Tooltip(additiveBlock ?? "The original keeps working, and the converted result is added beside it in this " +
                                         "mod. The mod as it is now is kept as a backup.");
        ImGui.SameLine();
        Widgets.Badge("Keeps the original", additiveBlock == null ? Theme.Info : Theme.Muted);

        if (ImGui.RadioButton("Convert in place", mode == ConversionOutputMode.InPlace))
            session.SetOutputMode(ConversionOutputMode.InPlace);
        Widgets.Tooltip("The converted result replaces the original inside this mod. The mod as it is now is kept as a " +
                        "backup, so this can be reverted.");
        ImGui.SameLine();
        Widgets.Badge("Advanced", Theme.Warning);

        if (mode == ConversionOutputMode.AddToMod)
        {
            Widgets.MutedWrapped(AddToModDescription());
            return;
        }

        if (!mode.IsNewMod())
        {
            Widgets.MutedWrapped(InPlaceDescription());
            return;
        }

        Widgets.MutedWrapped(NewModDescription());
        DrawNewModName();
    }

    // What each output mode does depends on what the plan converts. A run can mix gear and
    // animations, so each kind it holds says its own sentence.

    private string NewModDescription()
    {
        var contents = session.OutputContents;
        if (contents.HasFlag(PlanContents.Customization))
            return $"Creates a copy of this whole mod with the {session.OutputCustomizationName ?? "customization"} " +
                   "converted; everything else in the mod comes along. This mod is not modified.";

        var text = "Creates a new mod holding only what the plan converts, in the options it is in; everything else " +
                   "in this mod is left out. This mod is not modified.";
        if (contents.HasFlag(PlanContents.AnimationSwap))
            text += " While it stays enabled, the animation also keeps playing where it was.";
        if (contents.HasFlag(PlanContents.AnimationRetarget))
            text += " A retarget brings the source race's animation along, unless that race is unticked.";
        if (contents.HasFlag(PlanContents.AnimationExpression))
            text += " Both mods then replace the same animation, so disable this one (or give the new one the higher " +
                    "priority) to see the expression.";
        return text;
    }

    private string AddToModDescription()
    {
        var contents = session.OutputContents;
        var parts = new List<string>();
        if (contents.HasFlag(PlanContents.Gear))
            parts.Add("The original item keeps working: the converted one is added beside it, in the same options, so " +
                      "the toggles this mod already has control both.");
        if (contents.HasFlag(PlanContents.AnimationSwap))
            parts.Add("The animation keeps playing where it is and also plays at every destination, switched by the " +
                      "same options.");
        if (contents.HasFlag(PlanContents.AnimationRetarget))
            parts.Add("The source race keeps its animation, and the other races' versions are added in the same options.");
        if (session.AddsExpressionGroup)
            parts.Add($"Adds an option group, '{AnimationConversionPlanner.ExpressionGroupName}', that plays the " +
                      "animation with the expression; its first option, \"-\", plays it without. The animation itself " +
                      "stays as it is.");
        parts.Add("The mod as it is now is kept as a backup.");
        return string.Join(" ", parts);
    }

    private string InPlaceDescription()
    {
        var contents = session.OutputContents;
        var parts = new List<string>();
        if (contents.HasFlag(PlanContents.Gear))
            parts.Add("The item moves to the target: this mod stops changing the original item, apart from files other " +
                      "items still use.");
        if (contents.HasFlag(PlanContents.Customization) && session.OutputCustomizationName is { } kind)
            parts.Add($"The {kind} is converted inside this mod, which then no longer changes the original {kind}.");
        if (contents.HasFlag(PlanContents.AnimationSwap))
            parts.Add("The animation moves to its destination and stops playing where it was; an idle stays in its " +
                      "current slot too while that slot is ticked.");
        if (contents.HasFlag(PlanContents.AnimationRetarget))
            parts.Add("Retargeting moves the animation from the source race to the ticked races; add to this mod to keep " +
                      "the source race's too.");
        if (contents.HasFlag(PlanContents.AnimationExpression))
            parts.Add("The expression is attached to the animation itself, which then always plays with it.");
        parts.Add("The original is kept as a backup, so the conversion can be reverted from the result or the History " +
                  "tab until that backup expires.");
        return string.Join(" ", parts);
    }

    /// <summary>The new mod's name and where its folder goes.</summary>
    private void DrawNewModName()
    {
        if (_newModName != session.NewModName) _newModName = session.NewModName;
        ImGui.Spacing();
        ImGui.TextUnformatted("Name of the new mod");
        ImGui.SameLine();
        Widgets.Muted("(how it appears in Penumbra)");
        var buttonWidth = ImGui.GetFrameHeight();
        ImGui.SetNextItemWidth(ImGui.GetContentRegionAvail().X - buttonWidth - ImGui.GetStyle().ItemSpacing.X);
        if (ImGui.InputTextWithHint("##NewModName", "Name of the new mod", ref _newModName, 256))
            session.SetNewModName(_newModName);
        ImGui.SameLine();
        if (Widgets.IconButton("##ResetName", FontAwesomeIcon.Undo, "Use the suggested name"))
            session.ResetNewModName();

        if (session.NewModPath is { } path)
        {
            // The folder existing is a problem only until we are the ones who created it.
            var published = session.Task.IsApplied &&
                            string.Equals(session.Task.PublishedPath, path, StringComparison.OrdinalIgnoreCase);
            var conflict = !published && session.PathExists(path);
            // With nothing planned there is nothing to create yet, so a folder of that name is no
            // conflict until the plan would write there.
            if (conflict && session.Queue.Count == 0) return;
            var color = conflict ? Theme.Danger : published ? Theme.Success : Theme.Muted;
            Widgets.Icon(conflict ? FontAwesomeIcon.ExclamationCircle
                : published ? FontAwesomeIcon.CheckCircle : FontAwesomeIcon.FolderPlus, color);
            ImGui.SameLine();
            var label = conflict ? "Already exists:" : published ? "Created at:" : "Will be created at:";
            ImGui.TextColored(color, label);
            ImGui.SameLine();
            Widgets.PathText(path, color);
        }
    }

    /// <summary>
    /// A fan-out keeps the source as it is and only adds paths, so the question is mostly how the
    /// added paths are switched on; whether they go into this mod or a copy of it comes on top.
    /// </summary>
    private void DrawTextureOutput()
    {
        var asNewMod = session.TextureAsNewMod;
        if (ImGui.Checkbox("Create as a new mod", ref asNewMod)) session.SetTextureAsNewMod(asNewMod);
        Widgets.Tooltip("Write the result to a copy of this mod, with the paths added, and leave this mod " +
                        "untouched.");

        var layout = session.TextureLayout;
        if (ImGui.RadioButton("Add paths on existing options", layout == TextureFanOutLayout.AddPathsToOptions))
            session.SetTextureLayout(TextureFanOutLayout.AddPathsToOptions);
        Widgets.Tooltip("Each ticked race or face gets the source's paths right beside them, in Default or " +
                        "whichever options already hold them, so the mod's existing toggles govern them too.");

        if (ImGui.RadioButton("Create new groups for new paths", layout == TextureFanOutLayout.NewGroups))
            session.SetTextureLayout(TextureFanOutLayout.NewGroups);
        Widgets.Tooltip("Each ticked race gets option groups of its own: a toggle per face or skin for what " +
                        "Default holds, and a copy of every group whose options hold the source's paths, so " +
                        "each race can be switched on, or pick its variant, separately.");

        var where = asNewMod ? "a copy of this whole mod" : "this mod";
        Widgets.MutedWrapped((layout == TextureFanOutLayout.NewGroups
                                 ? $"Every ticked race gets new groups in {where}. "
                                 : $"The paths are added to {where} beside the source's. ") +
                             "The source keeps working as it is. " +
                             (asNewMod
                                 ? "This mod is not modified."
                                 : "The mod as it is now is kept as a backup, so this can be reverted from the " +
                                   "result or the History tab until that backup expires."));

        if (asNewMod) DrawNewModName();
    }

    // ─────────────────────────────────────────────────────────────────────────
    // Readiness and actions
    // ─────────────────────────────────────────────────────────────────────────

    public void DrawActions()
    {
        ImGui.Spacing();
        DrawReadiness();
        ImGui.Spacing();

        var width      = 150f * Theme.Scale;
        var applyBlock = session.ApplyBlockReason;
        var mode       = session.EffectiveOutputMode;
        var applyLabel = mode switch
        {
            ConversionOutputMode.NewMod   => "Create new mod",
            ConversionOutputMode.AddToMod => "Add to this mod",
            _                             => "Convert in place",
        };
        if (Widgets.Button($"{applyLabel}##Apply", applyBlock, new Vector2(width, 0), primary: applyBlock == null))
            RequestApply(mode);
    }

    private void RequestApply(ConversionOutputMode mode)
    {
        if (mode.IsNewMod() || !config.ConfirmInPlace)
        {
            session.Apply();
            return;
        }

        var (title, what, verb) = mode switch
        {
            ConversionOutputMode.AddToMod when session.UsesTextureOutput && session.TextureLayout == TextureFanOutLayout.NewGroups
                => ("Add new option groups to this mod?",
                    $"'{session.ModName}' will get new option groups for every ticked race. The mod as it is now is " +
                    "kept as a backup and can be restored with Revert until that backup expires.",
                    "Add"),
            ConversionOutputMode.AddToMod when session.UsesTextureOutput
                => ("Add the paths to this mod?",
                    $"'{session.ModName}' will get the paths for every ticked race or face, beside the source's. The " +
                    "mod as it is now is kept as a backup and can be restored with Revert until that backup expires.",
                    "Add"),
            ConversionOutputMode.AddToMod when session.AddsExpressionGroup
                => ("Add the expression to this mod?",
                    $"'{session.ModName}' gets the option group '{AnimationConversionPlanner.ExpressionGroupName}', which " +
                    "plays the animation with the expression; its \"-\" option plays it without. The mod as it is now " +
                    "is kept as a backup and can be restored with Revert until that backup expires.",
                    "Add"),
            ConversionOutputMode.AddToMod => ("Add the converted item to this mod?",
                $"'{session.ModName}' will be modified, but the original item keeps working. The mod as it is " +
                "now is kept as a backup and can be restored with Revert until that backup expires.",
                "Add"),
            _ => ("Convert this mod in place?",
                $"'{session.ModName}' will be modified directly. The original is kept as a backup and can be " +
                "restored with Revert until the backup expires; see Settings for how long they are kept.",
                "Convert"),
        };
        confirm.Request(title, what, verb, session.Apply);
    }

    private void DrawReadiness()
    {
        var task = session.Task;
        if (session.IsBusy)
        {
            Widgets.Spinner(Theme.Accent);
            ImGui.SameLine();
            ImGui.TextUnformatted($"{session.Runner.CurrentLabel} {session.Runner.Elapsed.TotalSeconds:0}s");
            return;
        }

        if (session.ApplyBlockReason == null)
        {
            Widgets.Icon(FontAwesomeIcon.CheckCircle, Theme.Success);
            ImGui.SameLine();
            var notes = task.Diagnostics.Count(d => !d.IsBlocker);
            ImGui.TextColored(Theme.Success, notes switch
            {
                0 => "Ready to convert.",
                1 => "Ready to convert. One thing to check in the plan below.",
                _ => $"Ready to convert. {notes} things to check in the plan below.",
            });
            return;
        }

        if (session.PlanIsCurrent && task.HasBlockers)
        {
            Widgets.Icon(FontAwesomeIcon.TimesCircle, Theme.Danger);
            ImGui.SameLine();
            var blockers = task.Diagnostics.Count(d => d.IsBlocker);
            ImGui.TextColored(Theme.Danger, blockers == 1
                ? "One problem stops this conversion. See the plan below."
                : $"{blockers} problems stop this conversion. See the plan below.");
            return;
        }

        var reason = session.PreviewBlockReason ?? session.ApplyBlockReason;
        var stale  = task.IsPlanned && !session.PlanIsCurrent && !task.IsApplied;
        Widgets.Icon(stale ? FontAwesomeIcon.ExclamationTriangle : FontAwesomeIcon.InfoCircle, stale ? Theme.Warning : Theme.Muted);
        ImGui.SameLine();
        ImGui.TextColored(stale ? Theme.Warning : Theme.Muted, reason ?? string.Empty);
    }

    // ─────────────────────────────────────────────────────────────────────────
    // Result banner
    // ─────────────────────────────────────────────────────────────────────────

    public void DrawResult()
    {
        if (session.Result is not { } result) return;

        var color = Theme.For(result.Kind);
        ImGui.Spacing();
        using var table = ImRaii.Table("##Result", 1, ImGuiTableFlags.BordersOuter | ImGuiTableFlags.PadOuterX);
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
        if (Widgets.IconButton("##DismissResult", FontAwesomeIcon.Times, "Dismiss"))
            session.Result = null;

        ImGui.TextWrapped(result.Message);

        if (result.Path is { } path && session.PathExists(path))
        {
            if (Widgets.IconTextButton(FontAwesomeIcon.FolderOpen, "Open folder"))
                ConverterSession.OpenFolder(path);
            ImGui.SameLine();
            if (Widgets.IconTextButton(FontAwesomeIcon.Copy, "Copy path"))
                ImGui.SetClipboardText(path);
            ImGui.SameLine();
        }

        if (result.RetryActivationFolder != null)
        {
            var reason = session.PenumbraAvailable ? null : "Penumbra is not available.";
            if (Widgets.IconTextButton(FontAwesomeIcon.Redo, "Load in Penumbra", reason))
                session.RetryActivation();
            ImGui.SameLine();
        }

        if (result.RecordId is { } recordId && session.History.Find(recordId) is { } record)
        {
            var reason = session.DisplayedRevertBlockReason(record);
            if (reason == null || !record.IsReverted)
                if (Widgets.IconTextButton(FontAwesomeIcon.Undo, "Revert", reason, RevertTooltip(record)))
                    RequestRevert(record);
        }

        ImGui.NewLine();
    }

    public static string RevertTooltip(ConversionRecord record)
        => record.Mode == ConversionOutputMode.NewMod
            ? "Remove the new mod. It is moved to the backup folder, not deleted."
            : "Restore the mod as it was before this conversion. The converted version is moved to the backup folder.";

    public void RequestRevert(ConversionRecord record)
    {
        var what = record.Mode == ConversionOutputMode.NewMod
            ? $"The new mod '{Path.GetFileName(record.PublishedPath)}' will be removed from Penumbra. " +
              "Its folder is moved into the backup folder, not deleted."
            : $"'{record.SourceModName}' will be restored to how it was before this conversion. " +
              "Any changes made to it since then are lost; the converted version is moved into the backup folder.";
        confirm.Request($"Revert: {record.Description}?", what, "Revert", () => session.Revert(record.Id));
    }
}
