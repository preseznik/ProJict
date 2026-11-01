using System.IO;
using System.Runtime.InteropServices;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Windows.Interop;
using System.Windows.Threading;
using Microsoft.Web.WebView2.Core;

namespace ProJict.App.Services;

/// <summary>
/// Draws wiki diagrams: mermaid.js in Windows' Edge WebView2, in a window nobody sees, turns a diagram's text into a
/// picture, which the page then shows like any other. Each picture is drawn once and kept on disk (by its text and
/// light or dark), so pages open at once after that; a diagram that doesn't draw keeps its error, for the page and for
/// agents. Nothing runs until a page first shows a diagram.
/// </summary>
public sealed class DiagramService(string cacheDir, string userDataDir, Dispatcher dispatcher)
{
    private const string Host = "projict-diagrams.local";
    private static readonly TimeSpan GiveUpAfter = TimeSpan.FromSeconds(20);

    private readonly Dictionary<string, string> _errors = [];
    private readonly Dictionary<string, (string Source, bool Dark)> _waiting = [];
    private readonly HashSet<string> _drawing = [];
    private HwndSource? _window;
    private CoreWebView2Controller? _controller;
    private Task<bool>? _starting;
    private string? _unavailable;

    /// <summary>A picture was drawn (or failed to): pages showing it draw again (UI thread).</summary>
    public event Action? Drawn;

    /// <summary>Why diagrams can't be drawn on this computer (no WebView2 runtime, …); null while they can, or before the first try.</summary>
    public string? Unavailable => _unavailable;

    /// <summary>Which picture a diagram's text makes: the same text and theme always give the same one.</summary>
    public static string Key(string language, string source, bool dark) =>
        Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes($"{language}\n{(dark ? "dark" : "light")}\n{source.Replace("\r\n", "\n").Trim()}")))[..32].ToLowerInvariant();

    /// <summary>The picture's file, once drawn; null until then.</summary>
    public string? PictureFor(string key)
    {
        var path = Path.Combine(cacheDir, key + ".png");
        return File.Exists(path) ? path : null;
    }

    /// <summary>Why the diagram doesn't draw (mermaid's words), if it was tried and didn't.</summary>
    public string? ErrorFor(string key) => _errors.GetValueOrDefault(key) ?? _unavailable;

    /// <summary>Asks for a diagram's picture; <see cref="Drawn"/> says when it's there (or why not).</summary>
    public void Request(string language, string source, bool dark)
    {
        var key = Key(language, source, dark);
        if (PictureFor(key) != null || _errors.ContainsKey(key) || _drawing.Contains(key) || _waiting.ContainsKey(key) || _unavailable != null) return;
        _waiting[key] = (source, dark);
        _ = DrawWaitingAsync();
    }

    private async Task DrawWaitingAsync()
    {
        if (!await StartAsync()) return;
        foreach (var (key, (source, dark)) in _waiting.ToList())
        {
            _waiting.Remove(key);
            if (!_drawing.Add(key)) continue;
            var script = $"draw({JsonSerializer.Serialize(key)}, {JsonSerializer.Serialize(source)}, {(dark ? "true" : "false")})";
            try { await _controller!.CoreWebView2.ExecuteScriptAsync(script); }
            catch (Exception ex) when (ex is InvalidOperationException or COMException)
            {
                Finish(key, null, ex.Message);
                continue;
            }
            // An answer comes as a web message; one that never comes doesn't keep the diagram waiting for ever.
            var timer = new DispatcherTimer(GiveUpAfter, DispatcherPriority.Background, (s, _) =>
            {
                ((DispatcherTimer)s!).Stop();
                if (_drawing.Contains(key)) Finish(key, null, "Drawing it took too long.");
            }, dispatcher);
            timer.Start();
        }
    }

    /// <summary>Starts the hidden WebView2 the first time a diagram is needed; false (and <see cref="Unavailable"/> says why) when it can't.</summary>
    private Task<bool> StartAsync() => _starting ??= StartCoreAsync();

    private async Task<bool> StartCoreAsync()
    {
        try
        {
            var assets = Path.Combine(AppContext.BaseDirectory, "Assets", "Diagrams");
            if (!File.Exists(Path.Combine(assets, "mermaid.min.js"))) throw new FileNotFoundException("mermaid.min.js isn't next to ProJict.");
            Directory.CreateDirectory(userDataDir);
            var environment = await CoreWebView2Environment.CreateAsync(null, userDataDir);
            // A window of its own that is never shown or activated: WebView2 needs one to live in.
            _window = new HwndSource(new HwndSourceParameters("ProJict diagrams") { WindowStyle = unchecked((int)0x80000000), Width = 1600, Height = 1200, PositionX = -32000, PositionY = -32000 });
            _controller = await environment.CreateCoreWebView2ControllerAsync(_window.Handle);
            _controller.Bounds = new System.Drawing.Rectangle(0, 0, 1600, 1200);
            var web = _controller.CoreWebView2;
            web.Settings.AreDevToolsEnabled = false;
            web.Settings.AreDefaultContextMenusEnabled = false;
            web.Settings.AreDefaultScriptDialogsEnabled = false;
            web.Settings.IsStatusBarEnabled = false;
            web.Settings.IsZoomControlEnabled = false;
            web.SetVirtualHostNameToFolderMapping(Host, assets, CoreWebView2HostResourceAccessKind.Deny);
            // Only the drawing page: no links followed, no windows opened, nothing fetched from elsewhere.
            web.NavigationStarting += (_, e) => e.Cancel = !e.Uri.StartsWith($"https://{Host}/", StringComparison.OrdinalIgnoreCase);
            web.NewWindowRequested += (_, e) => e.Handled = true;
            web.AddWebResourceRequestedFilter("*", CoreWebView2WebResourceContext.All);
            web.WebResourceRequested += (_, e) =>
            {
                if (!e.Request.Uri.StartsWith($"https://{Host}/", StringComparison.OrdinalIgnoreCase) && !e.Request.Uri.StartsWith("data:", StringComparison.OrdinalIgnoreCase))
                    e.Response = environment.CreateWebResourceResponse(null, 403, "Forbidden", "");
            };
            var ready = new TaskCompletionSource<bool>();
            web.WebMessageReceived += (_, e) => OnMessage(e.WebMessageAsJson, ready);
            web.Navigate($"https://{Host}/diagram.html");
            if (await Task.WhenAny(ready.Task, Task.Delay(GiveUpAfter)) != ready.Task || !ready.Task.Result)
                throw new InvalidOperationException("The diagram page didn't load.");
            return true;
        }
        catch (Exception ex) when (ex is WebView2RuntimeNotFoundException or COMException or IOException or UnauthorizedAccessException or InvalidOperationException)
        {
            _unavailable = ex is WebView2RuntimeNotFoundException
                ? "Diagrams need Microsoft Edge WebView2, which isn't on this computer (it comes with Windows 11; for Windows 10 get it from microsoft.com)."
                : $"Diagrams can't be drawn here: {ex.Message}";
            foreach (var key in _waiting.Keys.ToList()) _waiting.Remove(key);
            Drawn?.Invoke();
            return false;
        }
    }

    private void OnMessage(string json, TaskCompletionSource<bool> ready)
    {
        using var document = JsonDocument.Parse(json);
        var message = document.RootElement;
        if (message.TryGetProperty("ready", out var isReady))
        {
            ready.TrySetResult(isReady.GetBoolean());
            return;
        }
        if (!message.TryGetProperty("id", out var id) || id.GetString() is not { } key || !_drawing.Contains(key)) return;
        if (message.TryGetProperty("error", out var error)) Finish(key, null, Core.Wiki.WikiText.DiagramError(error.GetString()));
        else if (message.TryGetProperty("png", out var png)) Finish(key, Convert.FromBase64String(png.GetString() ?? ""), null);
    }

    private void Finish(string key, byte[]? png, string? error)
    {
        _drawing.Remove(key);
        if (png != null)
        {
            try
            {
                Directory.CreateDirectory(cacheDir);
                File.WriteAllBytes(Path.Combine(cacheDir, key + ".png"), png);
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { error = ex.Message; }
        }
        if (error != null) _errors[key] = error;
        Drawn?.Invoke();
    }

    /// <summary>Closes the hidden WebView2 (ProJict is quitting).</summary>
    public void Close()
    {
        _controller?.Close();
        _controller = null;
        _window?.Dispose();
        _window = null;
    }
}
