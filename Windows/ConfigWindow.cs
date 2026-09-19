using System;
using AdvancedPenumbraItemConverter.Windows.Ui;
using Dalamud.Bindings.ImGui;
using Dalamud.Interface.Utility.Raii;
using Dalamud.Interface.Windowing;

namespace AdvancedPenumbraItemConverter.Windows;

public sealed class ConfigWindow : Window, IDisposable
{
    private readonly Plugin _plugin;

    public ConfigWindow(Plugin plugin) : base(
        "Advanced Penumbra Item Converter — Settings###APICConfig",
        ImGuiWindowFlags.AlwaysAutoResize | ImGuiWindowFlags.NoCollapse)
    {
        _plugin = plugin;
    }

    public void Dispose() { }

    public override void Draw()
    {
        var cfg = _plugin.Configuration;

        Widgets.SectionTitle("Preview");
        var autoRefresh = cfg.AutoRefreshPreview;
        if (ImGui.Checkbox("Preview automatically when the inputs are complete", ref autoRefresh))
        {
            cfg.AutoRefreshPreview = autoRefresh;
            cfg.Save();
        }

        var advanced = cfg.ShowAdvancedDetails;
        if (ImGui.Checkbox("Show advanced plan details (fingerprints, bone resolution)", ref advanced))
        {
            cfg.ShowAdvancedDetails = advanced;
            cfg.Save();
        }

        ImGui.Spacing();
        Widgets.SectionTitle("Safety");
        var confirm = cfg.ConfirmInPlace;
        if (ImGui.Checkbox("Ask before converting a mod in place", ref confirm))
        {
            cfg.ConfirmInPlace = confirm;
            cfg.Save();
        }

        ImGui.Spacing();
        using (ImRaii.TextWrapPos(ImGui.GetFontSize() * 28f))
            Widgets.Muted("Reverted and in-place originals are kept in the hidden .apic-backups folder " +
                          "inside your Penumbra mod directory. Delete it manually to reclaim space.");
    }
}
