using System.IO;
using System.Net;
using System.Runtime.InteropServices;
using System.Windows.Threading;
using ProJict.Core.Api;

namespace ProJict.App.Services.Api;

/// <summary>
/// Built-in Tailscale (#a0d11f05): this computer joins your tailnet itself, through Tailscale's own code in
/// projict-tailscale.dll (no Tailscale app, no VPN adapter), and connections to it there go to ProJict's API, so your
/// phone reaches ProJict at home or away. It runs while it's turned on and the API serves the network over HTTPS (the
/// phone pins ProJict's certificate, which goes through untouched). You sign in once, in your browser; the node keeps
/// its identity in %LocalAppData%\ProJict\Tailscale, never in the data folder (not backed up, not mirrored).
/// </summary>
public sealed class TailscaleService
{
    private readonly ApiServer _api;
    private readonly Dispatcher _dispatcher;
    private readonly object _sync = new();
    private Timer? _poll;
    private bool _started;
    private int _startedPort;

    public TailscaleService(ApiServer api, Dispatcher? dispatcher = null)
    {
        _api = api;
        _dispatcher = dispatcher ?? Dispatcher.CurrentDispatcher;
        _api.Changed += Sync;
    }

    /// <summary>Where the node keeps its identity; tests point it elsewhere.</summary>
    public string StateFolder { get; set; } = Path.Combine(Path.GetDirectoryName(ApiData.DefaultPath)!, "Tailscale");
    /// <summary>Its name on the tailnet.</summary>
    public string HostName { get; set; } = Tailscale.HostName(Environment.MachineName);
    /// <summary>Tests start an ephemeral node, which leaves the tailnet by itself.</summary>
    public bool Ephemeral { get; set; }

    /// <summary>projict-tailscale.dll is next to ProJict.exe (the build includes it when Go was there to build it).</summary>
    public static bool IsAvailable => File.Exists(Path.Combine(AppContext.BaseDirectory, Native.Dll));

    public TailscaleStatus Status { get; private set; } = TailscaleStatus.Stopped;
    public bool IsOn => _api.Data.Tailscale;
    /// <summary>The API serves the network over HTTPS: the phone's only way in.</summary>
    public bool ApiServesNetwork => _api is { IsRunning: true, Https: true };
    public string Description => Tailscale.Describe(Status, IsOn, IsAvailable, ApiServesNetwork, _api.Port);

    /// <summary>Its tailnet address while it's in, for the pairing QR code.</summary>
    public IPAddress? Address => _started ? Status.IPv4 : null;

    /// <summary>Raised on the UI thread when it starts, stops, or its state changes.</summary>
    public event Action? Changed;

    /// <summary>Turns it on or off (kept in api.json), and starts or stops it to match.</summary>
    public void SetOn(bool on)
    {
        if (on == IsOn) return;
        lock (_api.Store.Sync) _api.Data.Tailscale = on;
        _api.Store.Save();
        Sync();
    }

    /// <summary>Opens Tailscale's sign-in page in your browser (only Tailscale's own, and only when you click).</summary>
    public bool SignIn()
    {
        if (Status is not { NeedsSignIn: true, SignInUrl: { } url } || !Tailscale.IsSignInUrl(url)) return false;
        ShellLauncher.OpenUrl(url);
        return true;
    }

    /// <summary>Signs this computer out of your tailnet and turns it off; turning it on again needs a sign-in.</summary>
    public void SignOut()
    {
        if (_started)
        {
            try { Native.Take(Native.ProJictTsSignOut()); }
            catch (Exception) { }
        }
        lock (_sync) _started = false;
        SetOn(false);
        Update(TailscaleStatus.Stopped);
    }

    /// <summary>Starts or stops it to match: on, available, and the API serving the network over HTTPS.</summary>
    public void Sync()
    {
        var wanted = IsOn && IsAvailable && ApiServesNetwork;
        if (wanted && _started && _startedPort != _api.Port) Stop();
        if (wanted && !_started) Start();
        else if (!wanted && _started) Stop();
        else RaiseChanged();
    }

    private void Start()
    {
        lock (_sync)
        {
            if (_started) return;
            _started = true;
            _startedPort = _api.Port;
        }
        var (folder, host, port, ephemeral) = (StateFolder, HostName, _api.Port, Ephemeral);
        Update(new TailscaleStatus("Starting", null, [], null));
        Task.Run(() =>
        {
            TailscaleStatus result;
            try
            {
                // The node listens on the tailnet at the API's port and hands each connection to the API here.
                var started = TailscaleStatus.Parse(Native.Take(Native.ProJictTsStart(folder, host, "", port, port, ephemeral ? 1 : 0)));
                result = started.Error != null ? started : new TailscaleStatus("Starting", null, [], null);
            }
            catch (Exception ex) when (ex is DllNotFoundException or EntryPointNotFoundException or BadImageFormatException)
            {
                result = new TailscaleStatus("Failed", null, [], null, "its part of ProJict (projict-tailscale.dll) couldn't be loaded: " + ex.Message);
            }
            if (result.Error != null) lock (_sync) _started = false;
            Update(result);
            if (result.Error == null) _poll = new Timer(_ => Poll(), null, TimeSpan.FromSeconds(1), TimeSpan.FromSeconds(2));
        });
    }

    /// <summary>Asks the bridge where it is: every 2 seconds until it's in, then every 30.</summary>
    private void Poll()
    {
        if (!_started) return;
        TailscaleStatus status;
        try { status = TailscaleStatus.Parse(Native.Take(Native.ProJictTsStatus())); }
        catch (Exception ex) { status = new TailscaleStatus("Failed", null, [], null, ex.Message); }
        if (!_started) return;
        _poll?.Change(status.IsRunning ? TimeSpan.FromSeconds(30) : TimeSpan.FromSeconds(2), status.IsRunning ? TimeSpan.FromSeconds(30) : TimeSpan.FromSeconds(2));
        if (!Same(status, Status)) Update(status);
    }

    private static bool Same(TailscaleStatus a, TailscaleStatus b) =>
        a.State == b.State && a.SignInUrl == b.SignInUrl && a.Name == b.Name && a.Error == b.Error && a.Addresses.SequenceEqual(b.Addresses);

    /// <summary>Takes this computer off the tailnet for now (it stays signed in for next time).</summary>
    public void Stop()
    {
        lock (_sync)
        {
            if (!_started) return;
            _started = false;
        }
        _poll?.Dispose();
        _poll = null;
        try { Native.ProJictTsStop(); }
        catch (Exception) { }
        Update(TailscaleStatus.Stopped);
    }

    private void Update(TailscaleStatus status)
    {
        Status = status;
        RaiseChanged();
    }

    private void RaiseChanged()
    {
        if (_dispatcher.HasShutdownStarted) return;
        if (_dispatcher.CheckAccess()) Changed?.Invoke();
        else _dispatcher.BeginInvoke(() => Changed?.Invoke());
    }

    /// <summary>The bridge's functions (native\tailscale\bridge.go); strings come back as JSON it allocated.</summary>
    private static class Native
    {
        public const string Dll = "projict-tailscale.dll";

        [DllImport(Dll, CallingConvention = CallingConvention.Cdecl)]
        public static extern nint ProJictTsStart([MarshalAs(UnmanagedType.LPUTF8Str)] string dir, [MarshalAs(UnmanagedType.LPUTF8Str)] string hostname,
            [MarshalAs(UnmanagedType.LPUTF8Str)] string authKey, int listenPort, int targetPort, int ephemeral);

        [DllImport(Dll, CallingConvention = CallingConvention.Cdecl)]
        public static extern nint ProJictTsStatus();

        [DllImport(Dll, CallingConvention = CallingConvention.Cdecl)]
        public static extern nint ProJictTsCheck(int port);

        [DllImport(Dll, CallingConvention = CallingConvention.Cdecl)]
        public static extern nint ProJictTsSignOut();

        [DllImport(Dll, CallingConvention = CallingConvention.Cdecl)]
        public static extern void ProJictTsStop();

        [DllImport(Dll, CallingConvention = CallingConvention.Cdecl)]
        private static extern void ProJictTsFree(nint text);

        public static string Take(nint text)
        {
            if (text == 0) return "";
            try { return Marshal.PtrToStringUTF8(text) ?? ""; }
            finally { ProJictTsFree(text); }
        }
    }

    /// <summary>For tests: connects to its own tailnet address at the API's port, as a phone would, and says what came back.</summary>
    public string Check() => _started ? Native.Take(Native.ProJictTsCheck(_api.Port)) : "{\"error\":\"not started\"}";
}
