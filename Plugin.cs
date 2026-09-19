using System;
using Dalamud.Game.Command;
using Dalamud.IoC;
using Dalamud.Plugin;
using Dalamud.Plugin.Services;
using Dalamud.Interface.Windowing;
using AdvancedPenumbraModConverter.Services;
using AdvancedPenumbraModConverter.Services.Animations;
using AdvancedPenumbraModConverter.Session;
using AdvancedPenumbraModConverter.Windows;

namespace AdvancedPenumbraModConverter;

public sealed class Plugin : IDalamudPlugin
{
    // ── Dalamud services ──────────────────────────────────────────────────────
    [PluginService] internal static IDalamudPluginInterface PluginInterface { get; private set; } = null!;
    [PluginService] internal static ICommandManager         CommandManager  { get; private set; } = null!;
    [PluginService] internal static IPluginLog              Log             { get; private set; } = null!;
    [PluginService] internal static IDataManager            DataManager     { get; private set; } = null!;
    [PluginService] internal static IFramework              Framework       { get; private set; } = null!;
    [PluginService] internal static ITextureProvider        TextureProvider { get; private set; } = null!;
    [PluginService] internal static ISigScanner             SigScanner      { get; private set; } = null!;

    // ── Plugin internals ──────────────────────────────────────────────────────
    internal Configuration            Configuration  { get; }
    internal PenumbraIpcService       PenumbraIpc    { get; }
    internal ModConverterService      Converter      { get; }
    internal GameDataService          GameData       { get; }
    internal ConversionHistoryService History        { get; }
    internal ConverterSession         Session        { get; }

    public   readonly WindowSystem WindowSystem = new("AdvancedPenumbraModConverter");
    private  ConfigWindow          ConfigWindow  { get; }
    private  MainWindow            MainWindow    { get; }

    private const string CommandName    = "/apmc";
    private const string CommandConfig  = "/apmcconfig";

    public Plugin()
    {
        Configuration = PluginInterface.GetPluginConfig() as Configuration ?? new Configuration();
        Configuration.Migrate();

        PenumbraIpc = new PenumbraIpcService(PluginInterface, Log);
        GameData    = new GameDataService(DataManager, Log);
        Converter   = new ModConverterService(Log, GameData, Framework,
            new HavokAnimationRetargeter(new HavokAnimation(SigScanner), Framework));
        History     = new ConversionHistoryService(Configuration);
        Session     = new ConverterSession(this);

        // ── Windows ───────────────────────────────────────────────────────────
        ConfigWindow = new ConfigWindow(this);
        MainWindow   = new MainWindow(this);

        WindowSystem.AddWindow(ConfigWindow);
        WindowSystem.AddWindow(MainWindow);

        // ── Commands ──────────────────────────────────────────────────────────
        CommandManager.AddHandler(CommandName, new CommandInfo(OnMainCommand)
        {
            HelpMessage = "Open the Advanced Penumbra Mod Converter window."
        });
        CommandManager.AddHandler(CommandConfig, new CommandInfo(OnConfigCommand)
        {
            HelpMessage = "Open the Advanced Penumbra Mod Converter configuration."
        });

        // ── UI hooks ──────────────────────────────────────────────────────────
        PluginInterface.UiBuilder.Draw          += WindowSystem.Draw;
        PluginInterface.UiBuilder.OpenConfigUi  += ToggleConfigUi;
        PluginInterface.UiBuilder.OpenMainUi    += ToggleMainUi;

        // Background results are applied on the framework thread even while the window is closed.
        Framework.Update += OnFrameworkUpdate;

        // ── Penumbra lifecycle ────────────────────────────────────────────────
        PenumbraIpc.PenumbraInitialized += OnPenumbraStateChanged;
        PenumbraIpc.PenumbraDisposed    += OnPenumbraStateChanged;

        Log.Information("[APMC] Advanced Penumbra Mod Converter loaded.");
    }

    public void Dispose()
    {
        Framework.Update -= OnFrameworkUpdate;

        PluginInterface.UiBuilder.Draw         -= WindowSystem.Draw;
        PluginInterface.UiBuilder.OpenConfigUi -= ToggleConfigUi;
        PluginInterface.UiBuilder.OpenMainUi   -= ToggleMainUi;

        PenumbraIpc.PenumbraInitialized -= OnPenumbraStateChanged;
        PenumbraIpc.PenumbraDisposed    -= OnPenumbraStateChanged;

        PenumbraIpc.Dispose();

        WindowSystem.RemoveAllWindows();
        ConfigWindow.Dispose();
        MainWindow.Dispose();

        CommandManager.RemoveHandler(CommandName);
        CommandManager.RemoveHandler(CommandConfig);
    }

    // ── Command handlers ──────────────────────────────────────────────────────

    private void OnMainCommand   (string cmd, string args) => MainWindow.Toggle();
    private void OnConfigCommand (string cmd, string args) => ConfigWindow.Toggle();

    public void ToggleMainUi()   => MainWindow.Toggle();
    public void ToggleConfigUi() => ConfigWindow.Toggle();

    private void OnFrameworkUpdate(IFramework framework) => Session.Tick();

    // ── Penumbra lifecycle callbacks ──────────────────────────────────────────

    private void OnPenumbraStateChanged()
    {
        Log.Information("[APMC] Penumbra availability changed.");
        Session.Runner.Post(Session.RefreshPenumbraState);
    }
}
