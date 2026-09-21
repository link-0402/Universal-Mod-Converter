using System;
using System.Threading;
using System.Threading.Tasks;
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
    [PluginService] internal static IObjectTable            ObjectTable     { get; private set; } = null!;

    // ── Plugin internals ──────────────────────────────────────────────────────
    internal Configuration            Configuration  { get; }
    internal PenumbraIpcService       PenumbraIpc    { get; }
    internal ModConverterService      Converter      { get; }
    internal GameDataService          GameData       { get; }
    internal ConversionHistoryService History        { get; }
    internal BackupMaintenanceService BackupMaintenance { get; }
    internal ConverterSession         Session        { get; }
    internal MergeSession             Merge          { get; }
    internal PartPreviewService       PartPreview    { get; }
    internal GlamourerIpcService      Glamourer      { get; }
    internal WornGearService          WornGear       { get; }

    private int _maintenanceRunning;

    public   readonly WindowSystem WindowSystem = new("AdvancedPenumbraModConverter");
    private  ConfigWindow          ConfigWindow  { get; }
    private  MainWindow            MainWindow    { get; }
    private  MergeWindow           MergeWindow   { get; }

    private const string CommandName    = "/apmc";
    private const string CommandConfig  = "/apmcconfig";

    public Plugin()
    {
        Configuration = PluginInterface.GetPluginConfig() as Configuration ?? new Configuration();
        Configuration.Migrate();

        PenumbraIpc = new PenumbraIpcService(PluginInterface, Log);
        GameData    = new GameDataService(DataManager, Log);
        Converter   = new ModConverterService(Log, GameData, Framework,
            new HavokAnimationRetargeter(new HavokAnimation(SigScanner), Framework), Configuration,
            new HavokExpressionMerger(Framework));
        History     = new ConversionHistoryService(Configuration);
        BackupMaintenance = new BackupMaintenanceService(Configuration, History, Log);
        Session     = new ConverterSession(this);
        Merge       = new MergeSession(this);
        PartPreview = new PartPreviewService(PenumbraIpc, Log);
        Glamourer   = new GlamourerIpcService(PluginInterface, Log);
        WornGear    = new WornGearService(ObjectTable);

        // ── Windows ───────────────────────────────────────────────────────────
        ConfigWindow = new ConfigWindow(this);
        MainWindow   = new MainWindow(this);
        MergeWindow  = new MergeWindow(this);

        WindowSystem.AddWindow(ConfigWindow);
        WindowSystem.AddWindow(MainWindow);
        WindowSystem.AddWindow(MergeWindow);

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

        // Anything a previous session left behind is cleaned up once, at startup.
        RunBackupMaintenance(includeOrphans: true);

        Log.Information("[APMC] Advanced Penumbra Mod Converter loaded.");
    }

    /// <summary>
    /// Deletes expired backups off the framework thread. Cheap to call after any conversion;
    /// overlapping calls are dropped rather than queued.
    /// </summary>
    /// <param name="includeOrphans">
    /// Also clean up staging folders and recovery journals a crash left beside the mods. Only
    /// safe when no conversion of ours is in flight, so this is a startup-only concern.
    /// </param>
    internal void RunBackupMaintenance(bool includeOrphans = false)
    {
        if (!Configuration.PruneBackupsAutomatically) return;
        if (Interlocked.Exchange(ref _maintenanceRunning, 1) == 1) return;

        // Penumbra IPC is a framework-thread concern, so the root is read here, not in the task.
        var root = PenumbraIpc.IsAvailable ? PenumbraIpc.GetModDirectory() : null;
        Task.Run(() =>
        {
            try
            {
                if (includeOrphans && !string.IsNullOrWhiteSpace(root)) BackupMaintenance.SweepOrphans(root!);
                var result = BackupMaintenance.Sweep(root);
                if (result.PrunedRecords.Count > 0)
                    Session.Runner.Post(() => History.MarkBackupsPruned(result.PrunedRecords));
            }
            catch (Exception ex)
            {
                Log.Warning(ex, "[APMC] Backup maintenance failed.");
            }
            finally
            {
                Volatile.Write(ref _maintenanceRunning, 0);
            }
        });
    }

    public void Dispose()
    {
        Framework.Update -= OnFrameworkUpdate;

        PluginInterface.UiBuilder.Draw         -= WindowSystem.Draw;
        PluginInterface.UiBuilder.OpenConfigUi -= ToggleConfigUi;
        PluginInterface.UiBuilder.OpenMainUi   -= ToggleMainUi;

        PenumbraIpc.PenumbraInitialized -= OnPenumbraStateChanged;
        PenumbraIpc.PenumbraDisposed    -= OnPenumbraStateChanged;

        PartPreview.Dispose(); // Takes its temporary mod out of Penumbra, so before the IPC goes.
        PenumbraIpc.Dispose();

        WindowSystem.RemoveAllWindows();
        ConfigWindow.Dispose();
        MainWindow.Dispose();
        MergeWindow.Dispose();

        CommandManager.RemoveHandler(CommandName);
        CommandManager.RemoveHandler(CommandConfig);
    }

    // ── Command handlers ──────────────────────────────────────────────────────

    private void OnMainCommand   (string cmd, string args) => MainWindow.Toggle();
    private void OnConfigCommand (string cmd, string args) => ConfigWindow.Toggle();

    public void ToggleMainUi()   => MainWindow.Toggle();
    public void ToggleConfigUi() => ConfigWindow.Toggle();
    public void ToggleMergeUi()  => MergeWindow.Toggle();

    private void OnFrameworkUpdate(IFramework framework)
    {
        Session.Tick();
        if (MergeWindow.IsOpen) Merge.Tick();
        PartPreview.Tick();
    }

    // ── Penumbra lifecycle callbacks ──────────────────────────────────────────

    private void OnPenumbraStateChanged()
    {
        Log.Information("[APMC] Penumbra availability changed.");
        Session.Runner.Post(() =>
        {
            Session.RefreshPenumbraState();
            // The mod root is only knowable once Penumbra is up, and startup may have missed it.
            RunBackupMaintenance(includeOrphans: true);
        });
    }
}
