using System.Diagnostics;
using System.IO;
using System.Net.Http;
using System.Text.Json;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using ProJict.Core.Updates;

namespace ProJict.App.Services;

/// <summary>Looks for a newer published installer, and downloads and runs it on request.</summary>
public sealed partial class UpdateService(AppState state) : ObservableObject
{
    private static readonly HttpClient Http = new() { Timeout = TimeSpan.FromMinutes(5) };

    public static Version CurrentVersion { get; } = typeof(UpdateService).Assembly.GetName().Version ?? new Version(0, 0, 0);
    public static string CurrentVersionText => CurrentVersion.ToString(3);

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(IsAvailable), nameof(BannerText))]
    private UpdateInfo? _available;

    [ObservableProperty] private bool _isDownloading;
    [ObservableProperty] private double _progress;

    /// <summary>How often ProJict looks again while it stays open (it also looks at every start).</summary>
    public static readonly TimeSpan PeriodicInterval = TimeSpan.FromHours(4);
    private System.Windows.Threading.DispatcherTimer? _timer;

    /// <summary>
    /// Keeps looking while ProJict runs, so a long-open ProJict learns of new versions too. A tick does nothing while
    /// checks are turned off, the banner already shows or an update is downloading; "Later" means until the next look.
    /// </summary>
    public void StartPeriodicChecks()
    {
        if (_timer != null) return;
        _timer = new System.Windows.Threading.DispatcherTimer { Interval = PeriodicInterval };
        _timer.Tick += async (_, _) =>
        {
            if (state.AppSettings.CheckForUpdates && !IsAvailable && !IsDownloading) await CheckAsync(manual: false);
        };
        _timer.Start();
    }

    public bool IsPeriodicCheckRunning => _timer?.IsEnabled == true;

    public bool IsAvailable => Available != null;
    public string BannerText => Available == null ? ""
        : $"ProJict {Available.Version.ToString(3)} is available (you have {CurrentVersionText}).";

    /// <summary>Returns a short result message; a found update also shows the banner unless that version was skipped.</summary>
    public async Task<string> CheckAsync(bool manual)
    {
        try
        {
            var info = await UpdateChecker.CheckAsync(Http, CurrentVersion);
            state.AppSettings.LastUpdateCheck = DateTime.Now;
            state.SaveAppSettings();
            if (info == null) return $"You have the latest version ({CurrentVersionText}).";
            if (manual || !string.Equals(info.Tag, state.AppSettings.SkippedVersion, StringComparison.OrdinalIgnoreCase))
                Available = info;
            return $"ProJict {info.Version.ToString(3)} is available.";
        }
        catch (Exception ex) when (ex is HttpRequestException or TaskCanceledException or JsonException)
        {
            return "Could not check for updates: " + ex.Message;
        }
    }

    [RelayCommand]
    private async Task InstallAsync()
    {
        if (Available is not { } info || IsDownloading) return;
        IsDownloading = true;
        Progress = 0;
        try
        {
            var path = Path.Combine(Path.GetTempPath(), "ProJict-update", info.AssetName);
            await UpdateChecker.DownloadAsync(Http, info, path, new Progress<double>(p => Progress = p * 100));
            state.SaveNow();
            // The installer replaces the program files and starts ProJict again when it is done. It waits for this ProJict
            // to exit before it touches anything (/WAITPID), and keeps a log next to the download, so a failed update
            // can be told apart (#2a293aff); a Setup that doesn't finish puts the old ProJict back and starts it.
            var log = Path.ChangeExtension(path, ".log");
            Process.Start(new ProcessStartInfo(path,
                $"/SILENT /SUPPRESSMSGBOXES /NORESTART /CLOSEAPPLICATIONS /WAITPID={Environment.ProcessId} /LOG=\"{log}\"") { UseShellExecute = true });
            // Quit once Setup holds its lock: from then on nothing starts ProJict again until the update is done.
            for (var waited = 0; waited < 15_000 && !SetupLock.IsHeld(); waited += 250) await Task.Delay(250);
            App.Current.ExitApp();
        }
        catch (Exception ex)
        {
            state.StatusText = "Update failed: " + ex.Message;
            IsDownloading = false;
        }
    }

    [RelayCommand]
    private void Skip()
    {
        if (Available == null) return;
        state.AppSettings.SkippedVersion = Available.Tag;
        state.SaveAppSettings();
        Available = null;
    }

    [RelayCommand] private void Later() => Available = null;

    [RelayCommand]
    private void ShowNotes() => ShellLauncher.OpenUrl(Available?.ReleaseUrl ?? UpdateChecker.ReleasesPage);
}
