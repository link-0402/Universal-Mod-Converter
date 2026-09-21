using System;
using System.Numerics;
using UniversalModConverter.Session;
using UniversalModConverter.Windows.Components;
using UniversalModConverter.Windows.Ui;
using Dalamud.Bindings.ImGui;
using Dalamud.Interface;
using Dalamud.Interface.Utility.Raii;
using Dalamud.Interface.Windowing;
using Dalamud.Utility;

namespace UniversalModConverter.Windows;

/// <summary>
/// Primary plugin window. A resizable mod browser on the left; on the right the conversion
/// workspace (source → target, output, actions, result) above Plan / Log / History tabs.
/// All state lives in <see cref="ConverterSession"/>; this class only lays things out.
/// </summary>
public sealed class MainWindow : Window, IDisposable
{
    private const float MinBrowserWidth = 160f;
    private const float MaxBrowserWidth = 520f;
    private const string KofiUrl = "https://ko-fi.com/luci_xiv";

    private readonly Plugin           _plugin;
    private readonly ConverterSession _session;
    private readonly ConfirmDialog    _confirm = new();
    private readonly ModBrowserPanel  _browser;
    private readonly ConversionCards  _cards;
    private readonly QueuePanel       _queue;
    private readonly ActionPanels     _actions;
    private readonly PlanView         _plan;
    private readonly LogView          _log;
    private readonly HistoryView      _history;

    private readonly MeshGroupsView   _meshes;

    private enum Tab { None, Plan, Meshes, Log }

    private int  _lastLogCount;
    private Tab  _selectTab = Tab.None;
    private object? _lastTask;

    public MainWindow(Plugin plugin) : base(
        "Universal Mod Converter###UMCMain",
        ImGuiWindowFlags.NoScrollbar | ImGuiWindowFlags.NoScrollWithMouse)
    {
        _plugin  = plugin;
        _session = plugin.Session;
        _browser = new ModBrowserPanel(_session);
        _cards   = new ConversionCards(_session);
        _queue   = new QueuePanel(_session);
        _actions = new ActionPanels(_session, plugin.Configuration, _confirm);
        _plan    = new PlanView(_session, plugin.Configuration);
        _log     = new LogView(_session.Log);
        _history = new HistoryView(_session, _actions);
        _meshes  = new MeshGroupsView(_session, plugin);

        Size          = new Vector2(1000, 720);
        SizeCondition = ImGuiCond.FirstUseEver;
        SizeConstraints = new WindowSizeConstraints
        {
            MinimumSize = new Vector2(640, 440),
            MaximumSize = new Vector2(float.MaxValue, float.MaxValue),
        };

        TitleBarButtons.Add(new TitleBarButton
        {
            Icon = FontAwesomeIcon.Heart,
            Click = _ => OpenKofiPage(),
            ShowTooltip = () => ImGui.SetTooltip("♥ Support me on Ko-fi"),
        });
    }

    public void Dispose() { }

    private void OpenKofiPage()
    {
        try
        {
            Util.OpenLink(KofiUrl);
        }
        catch (Exception e)
        {
            Plugin.Log.Error(e, "Could not open the Universal Mod Converter Ko-fi page.");
        }
    }

    public override void OnOpen()
    {
        _session.EnsureInitialized();
        _plugin.GameData.WarmUp();
    }

    public override void Draw()
    {
        _session.EnsureInitialized();

        DrawHeader();

        var bodyHeight   = ImGui.GetContentRegionAvail().Y;
        var config       = _plugin.Configuration;
        var maxWidth     = Math.Min(MaxBrowserWidth * Theme.Scale, ImGui.GetContentRegionAvail().X * 0.5f);
        var browserWidth = Math.Clamp(config.ModBrowserWidth * Theme.Scale, MinBrowserWidth * Theme.Scale, maxWidth);

        using (var left = ImRaii.Child("##Browser", new Vector2(browserWidth, bodyHeight)))
        {
            if (left.Success) _browser.Draw();
        }

        ImGui.SameLine(0, 0);
        DrawSplitter(bodyHeight, browserWidth);
        ImGui.SameLine(0, 0);

        using (var right = ImRaii.Child("##Workspace", new Vector2(-1, bodyHeight)))
        {
            if (right.Success) DrawWorkspace();
        }

        _confirm.Draw();
    }

    private void DrawHeader()
    {
        if (_session.PenumbraAvailable)
            Widgets.Badge("● Penumbra connected", Theme.Success);
        else
        {
            Widgets.Badge("● Penumbra not available", Theme.Danger);
            Widgets.Tooltip("Mods are opened by folder path, and new mods must be added to Penumbra manually.");
        }

        const string merge = "Merge modpacks";
        float iconWidth;
        using (ImRaii.PushFont(UiBuilder.IconFont))
            iconWidth = ImGui.CalcTextSize(FontAwesomeIcon.ObjectGroup.ToIconString()).X;
        var mergeWidth = iconWidth + ImGui.CalcTextSize(merge).X + ImGui.GetStyle().FramePadding.X * 2 + 5f * Theme.Scale;
        ImGui.SameLine(ImGui.GetWindowContentRegionMax().X - ImGui.GetFrameHeight() - ImGui.GetStyle().ItemSpacing.X - mergeWidth);
        if (Widgets.IconTextButton(FontAwesomeIcon.ObjectGroup, merge, null,
                "Combine two modpacks into one new mod, e.g. a base mod and a separate pack of upscaled models."))
            _plugin.ToggleMergeUi();
        ImGui.SameLine();
        if (Widgets.IconButton("##Settings", FontAwesomeIcon.Cog, "Settings"))
            _plugin.ToggleConfigUi();
        ImGui.Spacing();
    }

    /// <summary>A draggable divider between the mod browser and the workspace.</summary>
    private void DrawSplitter(float height, float browserWidth)
    {
        var width = 8f * Theme.Scale;
        ImGui.InvisibleButton("##Splitter", new Vector2(width, height));
        var active  = ImGui.IsItemActive();
        var hovered = ImGui.IsItemHovered();
        if (hovered || active) ImGui.SetMouseCursor(ImGuiMouseCursor.ResizeEw);

        var min  = ImGui.GetItemRectMin();
        var x    = min.X + width / 2;
        var line = hovered || active ? ImGuiCol.SeparatorHovered : ImGuiCol.Separator;
        ImGui.GetWindowDrawList().AddLine(new Vector2(x, min.Y), new Vector2(x, min.Y + height), ImGui.GetColorU32(line));

        if (active && ImGui.GetIO().MouseDelta.X != 0)
            _plugin.Configuration.ModBrowserWidth = (browserWidth + ImGui.GetIO().MouseDelta.X) / Theme.Scale;
        if (ImGui.IsItemDeactivated())
            _plugin.Configuration.Save();
    }

    private void DrawWorkspace()
    {
        if (!_session.HasMod)
        {
            DrawEmptyState();
            return;
        }

        DrawModHeader();
        ImGui.Spacing();
        _cards.Draw();
        ImGui.Spacing();
        _queue.Draw();
        ImGui.Spacing();
        Widgets.BeginAutoCard("##OutputCard");
        try
        {
            _actions.DrawOutput();
            _actions.DrawActions();
        }
        finally { Widgets.EndAutoCard(); }
        _actions.DrawResult();
        ImGui.Spacing();
        DrawTabs();
    }

    private void DrawEmptyState()
    {
        var text = _session.PenumbraAvailable
            ? "Select a mod on the left to start."
            : "Enter a mod folder on the left to start.";
        var size = ImGui.CalcTextSize(text);
        var avail = ImGui.GetContentRegionAvail();
        ImGui.SetCursorPos(ImGui.GetCursorPos() + new Vector2((avail.X - size.X) / 2, avail.Y / 2 - size.Y * 2));
        Widgets.Muted(text);
    }

    private void DrawModHeader()
    {
        ImGui.TextUnformatted(_session.ModName);

        var buttons = ImGui.GetFrameHeight() * 2 + ImGui.GetStyle().ItemSpacing.X;
        ImGui.SameLine(ImGui.GetContentRegionMax().X - buttons);
        if (Widgets.IconButton("##Rescan", FontAwesomeIcon.SyncAlt, "Scan the mod again",
                _session.IsBusy ? "Wait for the current operation to finish." : null))
            _session.Rescan();
        ImGui.SameLine();
        if (Widgets.IconButton("##OpenMod", FontAwesomeIcon.FolderOpen, "Open the mod folder"))
            ConverterSession.OpenFolder(_session.ModDirectory);

        Widgets.PathText(_session.ModDirectory, Theme.Muted);
    }

    private void DrawTabs()
    {
        // Jump to the log when a conversion or revert writes new entries.
        if (_session.Log.Entries.Count != _lastLogCount)
        {
            if (_session.Runner.CurrentLabel is "Converting…" or "Reverting…") _selectTab = Tab.Log;
            _lastLogCount = _session.Log.Entries.Count;
        }

        // A new cross-slot plan opens the mesh groups, since that is what needs a decision.
        if (!ReferenceEquals(_lastTask, _session.Task))
        {
            _lastTask = _session.Task;
            if (_session.PlanIsCrossSlot && _meshes.ModelCount > 0) _selectTab = Tab.Meshes;
        }

        using var bar = ImRaii.TabBar("##Tabs");
        if (!bar.Success) return;

        ImGuiTabItemFlags Flags(Tab tab) => _selectTab == tab ? ImGuiTabItemFlags.SetSelected : ImGuiTabItemFlags.None;
        var planFlags   = Flags(Tab.Plan);
        var meshFlags   = Flags(Tab.Meshes);
        var logFlags    = Flags(Tab.Log);
        _selectTab = Tab.None;

        var diagnostics = _session.Task.IsPlanned ? _session.Task.Diagnostics.Count : 0;
        using (var tab = ImRaii.TabItem(diagnostics > 0 ? $"Plan ({diagnostics})###Plan" : "Plan###Plan", planFlags))
        {
            if (tab.Success) _plan.Draw();
        }
        var models = _meshes.ModelCount;
        using (var tab = ImRaii.TabItem(models > 0 ? $"Mesh groups ({models})###Meshes" : "Mesh groups###Meshes", meshFlags))
        {
            if (tab.Success) _meshes.Draw();
        }
        using (var tab = ImRaii.TabItem("Log###Log", logFlags))
        {
            if (tab.Success) _log.Draw(240f * Theme.Scale);
        }
        using (var tab = ImRaii.TabItem($"History ({_session.History.Records.Count})###History"))
        {
            if (tab.Success) _history.Draw();
        }
    }
}
