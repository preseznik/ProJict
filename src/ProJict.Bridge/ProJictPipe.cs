using System.Diagnostics;
using System.IO.Pipes;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json.Nodes;

namespace ProJict.Bridge;

/// <summary>
/// What the bridge was asked: <c>--mcp</c> or <c>--watch-chat --as Ada</c>, which data folder's ProJict (<c>--data</c>,
/// else the one ProJict remembers), and whether it may start ProJict when it isn't running (not with <c>--no-launch</c>).
/// </summary>
internal sealed record Options(bool Watch, string? Agent, string DataRoot, string? DataArgument, bool Launch)
{
    public static Options From(string[] args)
    {
        string? Value(string name) => Array.IndexOf(args, name) is >= 0 and var i && i + 1 < args.Length ? args[i + 1] : null;
        var data = Value("--data");
        var root = Path.GetFullPath(data ?? RememberedDataRoot() ?? Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.MyDocuments), "ProJict"));
        return new Options(args.Contains("--watch-chat"), Value("--as"), root, data != null ? root : null, !args.Contains("--no-launch"));
    }

    /// <summary>ProJict's own folder under AppData: its settings, and this bridge's copy.</summary>
    public static string ProJictFolder => Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "ProJict");

    /// <summary>The data folder ProJict remembers (its settings.json), like ProJict.exe does.</summary>
    private static string? RememberedDataRoot()
    {
        try
        {
            var path = Path.Combine(ProJictFolder, "settings.json");
            return File.Exists(path) && JsonNode.Parse(File.ReadAllText(path))?["dataRoot"]?.GetValue<string>() is { Length: > 0 } root ? root : null;
        }
        catch (Exception) { return null; }
    }
}

/// <summary>The running ProJict's agent pipe for a data folder (named as ProJict names it), and starting ProJict.</summary>
internal static class ProJictPipe
{
    private static string Id(string dataRoot) => Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(dataRoot.ToLowerInvariant())))[..16];

    /// <summary>
    /// ProJict is being updated: its installer holds this mutex while it runs (SetupMutex in installer\ProJict.iss, and
    /// SetupLock in ProJict.Core). Nothing starts ProJict meanwhile; Setup starts it itself when it's done.
    /// </summary>
    public static bool SetupRunning()
    {
        if (!Mutex.TryOpenExisting("ProJictSetupRunning", out var mutex)) return false;
        mutex.Dispose();
        return true;
    }

    public static bool IsRunning(string dataRoot)
    {
        if (!Mutex.TryOpenExisting($"ProJict-{Id(dataRoot)}-mutex", out var mutex)) return false;
        mutex.Dispose();
        return true;
    }

    public static async Task<NamedPipeClientStream?> ConnectAsync(string dataRoot, TimeSpan timeout)
    {
        var pipe = new NamedPipeClientStream(".", $"ProJict-{Id(dataRoot)}-agents", PipeDirection.InOut, PipeOptions.Asynchronous | PipeOptions.CurrentUserOnly);
        try
        {
            await pipe.ConnectAsync((int)timeout.TotalMilliseconds);
            return pipe;
        }
        catch (Exception ex) when (ex is TimeoutException or IOException or UnauthorizedAccessException)
        {
            await pipe.DisposeAsync();
            return null;
        }
    }

    /// <summary>
    /// The pipe, starting ProJict in the tray first if it isn't running and that's allowed; else why not. While ProJict is
    /// being updated it waits for Setup (at most <paramref name="waitForSetup"/>), which starts ProJict when it's done.
    /// </summary>
    public static async Task<(NamedPipeClientStream? Pipe, string? Problem)> OpenAsync(Options options, bool launch, TimeSpan waitForSetup = default)
    {
        if (await ConnectAsync(options.DataRoot, TimeSpan.FromSeconds(1)) is { } pipe) return (pipe, null);
        if (SetupRunning())
        {
            var until = DateTime.Now + waitForSetup;
            while (SetupRunning())
            {
                if (DateTime.Now >= until) return (null, "ProJict is being updated. Try again in a minute.");
                await Task.Delay(1000);
            }
            if (await ConnectAsync(options.DataRoot, TimeSpan.FromSeconds(30)) is { } after) return (after, null);
        }
        if (IsRunning(options.DataRoot)) return (null, "ProJict is running but doesn't accept AI agents. Turn on Settings > AI agents in ProJict.");
        if (!launch) return (null, "ProJict isn't running.");
        if (!Launch(options)) return (null, "ProJict isn't running, and the bridge doesn't know where it's installed. Start ProJict once.");
        return await ConnectAsync(options.DataRoot, TimeSpan.FromSeconds(30)) is { } started ? (started, null) : (null, "ProJict didn't start in time.");
    }

    /// <summary>Starts ProJict in the tray: the ProJict.exe that last set this bridge up (bridge.json beside it).</summary>
    public static bool Launch(Options options)
    {
        if (SetupRunning()) return false;
        try
        {
            var settings = Path.Combine(AppContext.BaseDirectory, "bridge.json");
            if (!File.Exists(settings) || JsonNode.Parse(File.ReadAllText(settings))?["projict"]?.GetValue<string>() is not { } exe || !File.Exists(exe)) return false;
            var args = "--background" + (options.DataArgument != null ? $" --data \"{options.DataArgument}\"" : "");
            // Shell execute, so ProJict doesn't inherit (and keep open) the client's stdio pipes.
            Process.Start(new ProcessStartInfo(exe, args) { UseShellExecute = true });
            return true;
        }
        catch (Exception) { return false; }
    }
}
