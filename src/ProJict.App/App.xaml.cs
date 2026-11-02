using System.IO;
using System.Windows;
using System.Windows.Threading;
using ProJict.App.Services;
using ProJict.App.ViewModels;
using ProJict.App.Views;
using ProJict.Core.Export;
using ProJict.Core.Storage;
using ProJict.Core.Updates;

namespace ProJict.App;

public partial class App : Application
{
    private SingleInstance? _single;
    private TrayService? _tray;
    private HotkeyService? _hotkey;
    private AgentPipeServer? _agents;
    private NotificationService? _notifications;
    private QuickCaptureWindow? _capture;

    public static new App Current => (App)Application.Current;
    public AppState State { get; private set; } = null!;
    public MainViewModel MainVm { get; private set; } = null!;
    public bool IsExiting { get; private set; }

    protected override void OnStartup(StartupEventArgs e)
    {
        base.OnStartup(e);

        if (HasFlag(e.Args, "--mcp") || HasFlag(e.Args, "--watch-chat"))
        {
            RunBridge(e.Args);
            return;
        }

        // ProJict is being updated: Setup starts it when it's done. Started now (by an agent's bridge, say), it would keep
        // the install folder's files in use and the update would fail. So it quits first thing: quietly (a message box would
        // keep them in use too), and before the single-instance lock, which would make Setup's own start think it runs.
        if (SetupLock.IsHeld() && !HasFlag(e.Args, SetupLock.AfterSetupFlag))
        {
            Shutdown();
            return;
        }

        var dataArg = ArgValue(e.Args, "--data");
        var appSettings = AppSettings.Load();
        if (dataArg == null && appSettings.DataRoot == null)
        {
            // First start: let the user pick the data folder, and remember it.
            ThemeMode = ThemeMode.System;
            appSettings.DataRoot = FirstRunWindow.Ask(AppSettings.DefaultDataRoot()) ?? AppSettings.DefaultDataRoot();
            try { appSettings.Save(); }
            catch (Exception) { /* asked again next time */ }
        }
        var dataRoot = Path.GetFullPath(dataArg ?? appSettings.DataRoot ?? AppSettings.DefaultDataRoot());

        _single = new SingleInstance(dataRoot);
        if (!_single.IsFirst)
        {
            _single.SignalFirst();
            Shutdown();
            return;
        }

        DispatcherUnhandledException += OnUnhandledException;

        try
        {
            State = new AppState(new ProjectStore(dataRoot), appSettings, persistAppSettings: dataArg == null);
        }
        catch (Exception ex)
        {
            MessageBox.Show($"Could not open the data folder:\n{dataRoot}\n\n{ex.Message}", "ProJict", MessageBoxButton.OK, MessageBoxImage.Error);
            Shutdown(1);
            return;
        }

        ApplyTheme(State.Settings.Theme);
        // One snapshot per launch, before anything is written.
        State.BackupNow();
        State.CleanUpAttachments();

        MainVm = new MainViewModel(State);
        var main = new MainWindow(MainVm);
        MainWindow = main;
        _single.Activated += () => Dispatcher.BeginInvoke(ShowMainWindow);

        _tray = new TrayService(ShowMainWindow, ShowQuickCapture, ExportAllNow, ExitApp);
        _hotkey = new HotkeyService(main);
        _hotkey.Pressed += ShowQuickCapture;
        RegisterHotkey(State.Settings.Hotkey, out _);

        // Windows notifications for what needs you, from the tray icon; a click brings ProJict up at the thing.
        _notifications = new NotificationService(State, MainVm,
            notice => _tray?.ShowNotice(notice.Title, notice.Text, () =>
            {
                ShowMainWindow();
                notice.Open?.Invoke();
            }),
            () => MainWindow is { IsVisible: true, IsActive: true } window && window.WindowState != WindowState.Minimized);

        _agents = new AgentPipeServer(AgentPipeServer.PipeName(dataRoot), MainVm.AgentTools, UpdateService.CurrentVersionText, MainVm.AgentTools.WatchOverPipeAsync);
        // The bridge agents start, outside the install folder so updates don't cut them off. Not from a ProJict on another
        // data folder (a test or second copy): the bridge would start that one for every agent.
        if (dataArg == null) Task.Run(BridgeInstaller.Refresh);
        if (appSettings.AllowAgents) _agents.Start();

        // Started for an AI agent (by the --mcp bridge): stay in the tray until the user opens the window.
        var background = HasFlag(e.Args, "--background");
        if (!background) main.Show();

        if (State.Store.LoadErrors.Count > 0)
        {
            var text = "Some files could not be loaded and were left untouched:\n\n" + string.Join("\n", State.Store.LoadErrors);
            if (background) State.StatusText = text.Replace("\n\n", " ").Replace('\n', ' ');
            else MessageBox.Show(main, text, "ProJict", MessageBoxButton.OK, MessageBoxImage.Warning);
        }

        if (appSettings.CheckForUpdates)
            _ = MainVm.Updates.CheckAsync(manual: false);
        MainVm.Updates.StartPeriodicChecks();
        if (MainVm.GitHub.HasWork)
            _ = MainVm.GitHub.RefreshAsync(force: false);
        _ = MainVm.Issues.SyncAllAsync();
        if (MainVm.Sharing.Data.Enabled) MainVm.Host.Start();
        if (MainVm.ApiStore.Data.Enabled) _ = MainVm.Api.StartAsync();
        MainVm.Guest.ConnectKnownHosts();
    }

    /// <summary>
    /// Bridge mode for MCP clients: no window and no single-instance lock; relays stdin/stdout to the running ProJict
    /// for the same data folder until either side closes.
    /// </summary>
    private void RunBridge(string[] args)
    {
        // Running from the install folder while it's being updated would keep its files in use: quit at once.
        if (SetupLock.IsHeld())
        {
            try { Console.Error.WriteLine("ProJict: ProJict is being updated. Try again in a minute."); }
            catch (IOException) { }
            Shutdown(1);
            return;
        }
        var dataArg = ArgValue(args, "--data");
        var dataRoot = Path.GetFullPath(dataArg ?? AppSettings.Load().DataRoot ?? AppSettings.DefaultDataRoot());
        var launch = !HasFlag(args, "--no-launch");
        var watch = HasFlag(args, "--watch-chat");
        Task.Run(async () =>
        {
            int code;
            try
            {
                code = watch
                    ? await McpBridge.WatchAsync(dataRoot, launch, dataArg != null ? dataRoot : null, ArgValue(args, "--as"))
                    : await McpBridge.RunAsync(dataRoot, launch, dataArg != null ? dataRoot : null);
            }
            catch (Exception) { code = 1; }
            await Dispatcher.InvokeAsync(() => Shutdown(code));
        });
    }

    /// <summary>Turns agent access on or off right away (Settings › AI agents).</summary>
    public void SetAgentAccess(bool allowed)
    {
        if (allowed) _agents?.Start();
        else _agents?.Stop();
    }

    public bool RegisterHotkey(string gesture, out string? error)
    {
        error = null;
        if (_hotkey == null) return false;
        var ok = _hotkey.Register(gesture, out error);
        _tray?.SetHotkeyHint(ok ? gesture : null);
        if (!ok) State.StatusText = "Quick capture hotkey not registered: " + error;
        return ok;
    }

    public static void ApplyTheme(string theme)
    {
        Current.ThemeMode = theme switch
        {
            "Light" => ThemeMode.Light,
            "Dark" => ThemeMode.Dark,
            _ => ThemeMode.System,
        };
    }

    public void ShowMainWindow()
    {
        if (MainWindow is not { } window) return;
        if (!window.IsVisible) window.Show();
        if (window.WindowState == WindowState.Minimized) window.WindowState = WindowState.Normal;
        window.Activate();
        window.Topmost = true;
        window.Topmost = false;
        window.Focus();
    }

    public void ShowQuickCapture()
    {
        if (_capture is { IsLoaded: true })
        {
            _capture.Activate();
            return;
        }
        _capture = new QuickCaptureWindow(State, MainVm);
        _capture.Closed += (_, _) => _capture = null;
        _capture.Show();
        _capture.Activate();
    }

    private void ExportAllNow()
    {
        State.SaveNow();
        var folder = State.Settings.MirrorEnabled && !string.IsNullOrWhiteSpace(State.Settings.MirrorFolder)
            ? State.Settings.MirrorFolder
            : State.Settings.DefaultExportFolder(State.Store.DataRoot);
        try
        {
            var result = ExportService.Export(State.Projects, folder, State.Settings.Export, overwriteConflicts: false);
            var text = $"{result.Written.Count} written, {result.Unchanged.Count} unchanged";
            if (result.Conflicts.Count > 0) text += $", {result.Conflicts.Count} skipped (not written by ProJict)";
            _tray?.ShowBalloon($"Exported to {folder}", text);
        }
        catch (Exception ex)
        {
            _tray?.ShowBalloon("Export failed", ex.Message);
        }
    }

    public void ExitApp()
    {
        IsExiting = true;
        // Dialogs first (an update can be installed from Settings), then the main window.
        foreach (var window in Windows.OfType<Window>().Where(w => w != MainWindow).ToList()) window.Close();
        if (MainWindow != null) MainWindow.Close();
        else Shutdown();
    }

    public void OnMainWindowClosed()
    {
        IsExiting = true;
        Shutdown();
    }

    protected override void OnExit(ExitEventArgs e)
    {
        try
        {
            MainVm?.Wiki.Page?.Leave();
            MainVm?.Diagrams.Close();
            State?.SaveNow();
        }
        catch (Exception) { }
        State?.SaveAppSettings();
        _agents?.Dispose();
        if (MainVm != null)
        {
            MainVm.Host.Stop();
            // Off the UI thread, so stopping doesn't wait for it.
            try { Task.Run(MainVm.Api.StopAsync).Wait(TimeSpan.FromSeconds(3)); }
            catch (Exception) { }
        }
        _hotkey?.Dispose();
        _tray?.Dispose();
        _single?.Dispose();
        base.OnExit(e);
    }

    private void OnUnhandledException(object sender, DispatcherUnhandledExceptionEventArgs e)
    {
        e.Handled = true;
        try
        {
            var log = Path.Combine(State?.Store.DataRoot ?? Path.GetTempPath(), "error.log");
            File.AppendAllText(log, $"[{DateTime.Now:u}] {e.Exception}\r\n\r\n");
        }
        catch (Exception) { }
        MessageBox.Show(e.Exception.Message, "ProJict — unexpected error", MessageBoxButton.OK, MessageBoxImage.Error);
    }

    private static bool HasFlag(string[] args, string name) => args.Any(a => string.Equals(a, name, StringComparison.OrdinalIgnoreCase));

    private static string? ArgValue(string[] args, string name)
    {
        var i = Array.FindIndex(args, a => string.Equals(a, name, StringComparison.OrdinalIgnoreCase));
        return i >= 0 && i + 1 < args.Length ? args[i + 1] : null;
    }
}
