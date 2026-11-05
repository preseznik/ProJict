using System.IO;
using System.Runtime.InteropServices;
using System.Text.Json;
using System.Text.Json.Nodes;
using System.Windows.Interop;
using System.Windows.Threading;
using Microsoft.Web.WebView2.Core;
using ProJict.Core.Agents;
using ProJict.Core.Tools;

namespace ProJict.App.Services;

/// <summary>
/// ProJict's browser for agents (Settings › Tools, a tool in the browser such as Suno or Tripo3D): for each tool a
/// browser nobody sees, with the tool's own profile, where you signed in yourself (Sign in…). One agent at a time
/// uses a tool's browser, by its connection (so a name it picks meanwhile keeps it); it's let go when the agent closes it
/// or disconnects, after <see cref="IdleFor"/> without use, or when you Stop it. Used on the UI thread.
/// </summary>
public sealed class BrowserService(ToolService tools, Dispatcher dispatcher)
{
    /// <summary>A tool's browser nobody has used for this long is closed, and another agent may have it.</summary>
    public static readonly TimeSpan IdleFor = TimeSpan.FromMinutes(10);

    private readonly Dictionary<Guid, BrowserSession> _sessions = [];
    /// <summary>Agents whose browser you stopped, by tool: told once, on their next call.</summary>
    private readonly Dictionary<Guid, McpSession> _stopped = [];

    public IReadOnlyCollection<BrowserSession> Sessions => _sessions.Values;
    /// <summary>A browser opened, closed, changed hands or finished a download.</summary>
    public event Action? Changed;
    /// <summary>A download finished (or failed): who it was for and where it is.</summary>
    public event Action<BrowserSession, BrowserDownload>? Downloaded;

    public BrowserSession? SessionOf(ExternalTool tool) => _sessions.GetValueOrDefault(tool.Id);

    /// <summary>The agent has it: the same connection, or the same name (it connected again since).</summary>
    public static bool IsHeldBy(BrowserSession session, McpSession agent) =>
        session.Holder == agent || string.Equals(session.Agent, agent.AgentName, StringComparison.OrdinalIgnoreCase);

    /// <summary>
    /// The tool's browser for this agent, started if it isn't running. Refused while another agent has used it within
    /// <see cref="IdleFor"/>; an agent whose browser you stopped hears so once.
    /// </summary>
    public async Task<BrowserSession> TakeAsync(ExternalTool tool, McpSession agent, DateTime now)
    {
        if (_stopped.TryGetValue(tool.Id, out var stoppedFor) && (stoppedFor == agent || string.Equals(stoppedFor.AgentName, agent.AgentName, StringComparison.OrdinalIgnoreCase)))
        {
            _stopped.Remove(tool.Id);
            throw new BrowserException($"The user stopped your use of {tool.Name}'s browser. Ask them before using it again.");
        }
        if (_sessions.TryGetValue(tool.Id, out var session))
        {
            if (!IsHeldBy(session, agent) && now - session.LastUsed < IdleFor)
                throw new BrowserException($"{session.Agent} is using {tool.Name}'s browser right now. Try again later, or ask {session.Agent} in Chat when it's done.");
            session.Holder = agent;
            session.LastUsed = now;
            Changed?.Invoke();
            return session;
        }
        session = new BrowserSession(tool, agent, Path.Combine(tools.ProfileFolder(tool), "Downloads"), now);
        session.Downloaded += download =>
        {
            Downloaded?.Invoke(session, download);
            Changed?.Invoke();
        };
        await session.StartAsync(tools.ProfileFolder(tool));
        _sessions[tool.Id] = session;
        Changed?.Invoke();
        return session;
    }

    /// <summary>The agent is done with it (browser_close): it's closed.</summary>
    public void Release(BrowserSession session)
    {
        if (!_sessions.Remove(session.Tool.Id)) return;
        session.Dispose();
        Changed?.Invoke();
    }

    /// <summary>You stopped it (Stop on the bar): it's closed, and the agent is told on its next call.</summary>
    public void Stop(BrowserSession session)
    {
        _stopped[session.Tool.Id] = session.Holder;
        Release(session);
    }

    /// <summary>The agent disconnected: the browsers it had are closed, for others to use.</summary>
    public void ReleaseHeldBy(McpSession agent)
    {
        foreach (var session in _sessions.Values.Where(s => s.Holder == agent).ToList()) Release(session);
    }

    /// <summary>Closes the browsers nobody has used for <see cref="IdleFor"/>.</summary>
    public void CloseIdle(DateTime now)
    {
        foreach (var session in _sessions.Values.Where(s => now - s.LastUsed >= IdleFor && s.Watching == null).ToList()) Release(session);
    }

    /// <summary>ProJict is quitting.</summary>
    public void CloseAll()
    {
        foreach (var session in _sessions.Values.ToList()) Release(session);
    }

    internal Dispatcher Dispatcher => dispatcher;
}

/// <summary>Something an agent may not do in a tool's browser, said to it in words.</summary>
public sealed class BrowserException(string message) : Exception(message);

/// <summary>A file downloaded in a tool's browser: where it was saved, and whether it finished.</summary>
public sealed record BrowserDownload(string Path, string FileName, bool Finished, string? Problem);

/// <summary>A link, button or field on the page, numbered for the agent (its ref).</summary>
public sealed record BrowserElement(int Ref, string Kind, string Text, string Label, string Type, string Name, string Autocomplete, string Href, string Value, bool Checked, bool Disabled)
{
    /// <summary>"[3] button “Create”", "[5] field “Song description”", "[8] link “Library” → https://suno.com/me".</summary>
    public string Line => $"[{Ref}] {Kind} “{(Text.Length > 0 ? Text : Label)}”" + (Kind == "link" && Href.Length > 0 ? $" → {Href}" : "") +
                          (Value.Length > 0 ? $" = “{Value}”" : "") + (Checked ? " (ticked)" : "") + (Disabled ? " (disabled)" : "");
}

/// <summary>A page as an agent reads it: its title, address, text and numbered links, buttons and fields.</summary>
public sealed record BrowserPage(string Title, string Url, string Text, IReadOnlyList<BrowserElement> Elements);

/// <summary>
/// A tool's browser: Edge WebView2 in a window nobody sees, with the tool's profile. It stays on the tool's website,
/// takes downloads itself (no dialog) and is driven with real clicks and typing (DevTools input, as a person would).
/// </summary>
public sealed class BrowserSession : IDisposable
{
    private const int PageWidth = 1400, PageHeight = 900;
    private HwndSource? _window;
    private CoreWebView2Controller? _controller;
    private TaskCompletionSource<bool>? _loading;

    internal BrowserSession(ExternalTool tool, McpSession holder, string downloads, DateTime now)
    {
        Tool = tool;
        Holder = holder;
        DownloadFolder = downloads;
        LastUsed = now;
    }

    public ExternalTool Tool { get; }
    /// <summary>The agent using it, by its connection: a name it picks meanwhile keeps the browser.</summary>
    public McpSession Holder { get; internal set; }
    /// <summary>The name that agent goes by now.</summary>
    public string Agent => Holder.AgentName;
    public DateTime LastUsed { get; set; }
    public string DownloadFolder { get; }
    /// <summary>Where what's downloaded goes: the item it's for (set by browser_open), else the downloads folder.</summary>
    public (Guid ProjectId, Guid ItemId)? Item { get; set; }
    /// <summary>The page as last read, whose refs clicks and typing go by.</summary>
    public BrowserPage? Page { get; private set; }
    /// <summary>Somewhere it was kept from going (off the tool's website), for the agent's next answer.</summary>
    public string? Blocked { get; set; }
    public List<BrowserDownload> Downloads { get; } = [];
    /// <summary>What the agent hasn't been told yet (a download attached, or one that failed), for its next answer.</summary>
    public List<string> News { get; } = [];
    /// <summary>The window you watch it in, while you do (the bar's Watch).</summary>
    public System.Windows.Window? Watching { get; set; }
    public CoreWebView2? Web => _controller?.CoreWebView2;
    internal CoreWebView2Controller? Controller => _controller;
    internal IntPtr HiddenWindow => _window?.Handle ?? IntPtr.Zero;

    internal event Action<BrowserDownload>? Downloaded;

    internal async Task StartAsync(string profile)
    {
        try
        {
            var environment = await CoreWebView2Environment.CreateAsync(null, profile);
            // A window of its own that is never shown or activated: WebView2 needs one to live in.
            _window = new HwndSource(new HwndSourceParameters($"ProJict browser: {Tool.Name}")
            {
                WindowStyle = unchecked((int)0x80000000), Width = PageWidth, Height = PageHeight, PositionX = -32000, PositionY = -32000,
            });
            _controller = await environment.CreateCoreWebView2ControllerAsync(_window.Handle);
            _controller.Bounds = new System.Drawing.Rectangle(0, 0, PageWidth, PageHeight);
            _controller.IsVisible = true;
            var web = _controller.CoreWebView2;
            web.Settings.AreDevToolsEnabled = false;
            web.Settings.IsPasswordAutosaveEnabled = false;
            web.Settings.IsGeneralAutofillEnabled = false;
            web.NavigationStarting += (_, e) =>
            {
                if (BrowserRules.IsOnSite(e.Uri, Tool.Website) || e.Uri.StartsWith("about:", StringComparison.Ordinal)) return;
                e.Cancel = true;
                Blocked = e.Uri;
            };
            web.NewWindowRequested += (_, e) =>
            {
                // A link that opens a window opens here instead, if it's on the website.
                e.Handled = true;
                if (BrowserRules.IsOnSite(e.Uri, Tool.Website)) web.Navigate(e.Uri);
                else Blocked = e.Uri;
            };
            web.NavigationCompleted += (_, e) => _loading?.TrySetResult(e.IsSuccess);
            web.DownloadStarting += (_, e) =>
            {
                Directory.CreateDirectory(DownloadFolder);
                var name = Path.GetFileName(e.ResultFilePath);
                var path = Unique(Path.Combine(DownloadFolder, string.IsNullOrWhiteSpace(name) ? "download" : name));
                e.ResultFilePath = path;
                e.Handled = true; // no download bar or dialog
                var operation = e.DownloadOperation;
                operation.StateChanged += (_, _) =>
                {
                    if (operation.State == CoreWebView2DownloadState.InProgress) return;
                    var download = new BrowserDownload(path, Path.GetFileName(path), operation.State == CoreWebView2DownloadState.Completed,
                        operation.State == CoreWebView2DownloadState.Interrupted ? $"It stopped: {operation.InterruptReason}" : null);
                    Downloads.Add(download);
                    Downloaded?.Invoke(download);
                };
            };
        }
        catch (Exception ex) when (ex is WebView2RuntimeNotFoundException or COMException or IOException or UnauthorizedAccessException or InvalidOperationException)
        {
            Dispose();
            throw new BrowserException(ex is WebView2RuntimeNotFoundException
                ? "ProJict's browser needs Microsoft Edge WebView2, which isn't on the user's computer."
                : $"ProJict's browser didn't start: {ex.Message}");
        }
    }

    private static string Unique(string path)
    {
        if (!File.Exists(path)) return path;
        var folder = Path.GetDirectoryName(path)!;
        var stem = Path.GetFileNameWithoutExtension(path);
        var extension = Path.GetExtension(path);
        for (var n = 2; ; n++)
        {
            var candidate = Path.Combine(folder, $"{stem} ({n}){extension}");
            if (!File.Exists(candidate)) return candidate;
        }
    }

    private CoreWebView2 Live => Web ?? throw new BrowserException($"{Tool.Name}'s browser isn't open.");

    /// <summary>Goes to an address on the tool's website and waits for it to load (up to <paramref name="seconds"/>).</summary>
    public async Task GoAsync(string address, double seconds = 30)
    {
        if (!BrowserRules.IsOnSite(address, Tool.Website))
            throw new BrowserException($"Agents stay on {Tool.Name}'s website ({Tool.Website}) in ProJict's browser; “{address}” isn't on it.");
        _loading = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
        Live.Navigate(address);
        await Task.WhenAny(_loading.Task, Task.Delay(TimeSpan.FromSeconds(seconds)));
        await Settle();
    }

    /// <summary>A moment for the page to react (scripts, a page that follows), after a click, typing or loading.</summary>
    public static Task Settle(int milliseconds = 1200) => Task.Delay(milliseconds);

    private const string ReadScript = """
        (() => {
          const visible = el => { const r = el.getBoundingClientRect(); const s = getComputedStyle(el);
            return r.width > 0 && r.height > 0 && s.visibility !== 'hidden' && s.display !== 'none' && s.opacity !== '0'; };
          document.querySelectorAll('[data-projict-ref]').forEach(e => e.removeAttribute('data-projict-ref'));
          const tidy = t => (t || '').trim().replace(/\s+/g, ' ').slice(0, 120);
          const labelFor = el => {
            if (el.id) { const l = document.querySelector('label[for="' + CSS.escape(el.id) + '"]'); if (l) return tidy(l.innerText); }
            const p = el.closest('label'); return p ? tidy(p.innerText) : tidy(el.getAttribute('aria-label') || el.getAttribute('placeholder'));
          };
          const picks = document.querySelectorAll('a[href], button, input:not([type=hidden]), textarea, select, [role=button], [role=link], [role=tab], [role=menuitem], [role=checkbox], [role=switch], [contenteditable=true]');
          const elements = []; let n = 0;
          for (const el of picks) {
            if (elements.length >= 150) break;
            if (!visible(el) || el.closest('[aria-hidden=true]')) continue;
            n++; el.setAttribute('data-projict-ref', String(n));
            const tag = el.tagName.toLowerCase(); const type = (el.getAttribute('type') || '').toLowerCase();
            const kind = tag === 'a' || el.getAttribute('role') === 'link' ? 'link' : tag === 'select' ? 'choice'
              : (tag === 'input' || tag === 'textarea' || el.isContentEditable) ? (type === 'checkbox' || type === 'radio' ? type : type === 'file' ? 'file' : 'field') : 'button';
            const field = kind === 'field';
            elements.push({ ref: n, kind, text: tidy(field ? '' : (el.getAttribute('aria-label') || el.innerText || el.value || el.getAttribute('title') || el.getAttribute('alt'))),
              label: labelFor(el), type, name: el.getAttribute('name') || '', autocomplete: el.getAttribute('autocomplete') || '',
              href: tag === 'a' ? el.href : '', value: field && type !== 'password' ? tidy(el.value !== undefined ? el.value : el.innerText) : '',
              checked: !!el.checked, disabled: !!el.disabled || el.getAttribute('aria-disabled') === 'true' });
          }
          const text = (document.body ? document.body.innerText : '').replace(/\n{3,}/g, '\n\n').slice(0, 6000);
          return JSON.stringify({ title: document.title, url: location.href, text, elements });
        })()
        """;

    /// <summary>Reads the page: its title, address, text and numbered links, buttons and fields (the refs to click and type in).</summary>
    public async Task<BrowserPage> ReadAsync()
    {
        var json = await Live.ExecuteScriptAsync(ReadScript);
        var inner = JsonSerializer.Deserialize<string>(json) ?? "{}";
        var page = JsonNode.Parse(inner)!.AsObject();
        var elements = (page["elements"]?.AsArray() ?? []).Select(e => new BrowserElement(
            (int)e!["ref"]!, (string)e["kind"]!, (string?)e["text"] ?? "", (string?)e["label"] ?? "", (string?)e["type"] ?? "", (string?)e["name"] ?? "",
            (string?)e["autocomplete"] ?? "", (string?)e["href"] ?? "", (string?)e["value"] ?? "", (bool?)e["checked"] ?? false, (bool?)e["disabled"] ?? false)).ToList();
        Page = new BrowserPage((string?)page["title"] ?? "", (string?)page["url"] ?? "", (string?)page["text"] ?? "", elements);
        return Page;
    }

    /// <summary>The element a ref names, from the page as last read.</summary>
    public BrowserElement Element(int reference) =>
        Page?.Elements.FirstOrDefault(e => e.Ref == reference)
        ?? throw new BrowserException($"There's no [{reference}] on the page as you last read it: read it again (browser_read) for its links, buttons and fields.");

    /// <summary>Where an element is now (scrolled into view first), in the page's pixels; null when it's gone.</summary>
    private async Task<(double X, double Y)?> Locate(int reference)
    {
        var json = await Live.ExecuteScriptAsync($$"""
            (() => { const el = document.querySelector('[data-projict-ref="{{reference}}"]'); if (!el) return null;
              el.scrollIntoView({ block: 'center', inline: 'center' }); const r = el.getBoundingClientRect();
              return JSON.stringify({ x: r.left + r.width / 2, y: r.top + r.height / 2 }); })()
            """);
        if (json == "null") return null;
        var at = JsonNode.Parse(JsonSerializer.Deserialize<string>(json)!)!;
        return ((double)at["x"]!, (double)at["y"]!);
    }

    private Task<string> Input(string method, object parameters) => Live.CallDevToolsProtocolMethodAsync(method, JsonSerializer.Serialize(parameters));

    /// <summary>Clicks an element as a person would (the mouse moved there, pressed and let go).</summary>
    public async Task ClickAsync(int reference)
    {
        if (await Locate(reference) is not { } at)
            throw new BrowserException($"[{reference}] isn't on the page any more: read it again (browser_read).");
        var (x, y) = at;
        await Input("Input.dispatchMouseEvent", new { type = "mouseMoved", x, y });
        await Input("Input.dispatchMouseEvent", new { type = "mousePressed", x, y, button = "left", clickCount = 1 });
        await Input("Input.dispatchMouseEvent", new { type = "mouseReleased", x, y, button = "left", clickCount = 1 });
        await Settle();
    }

    /// <summary>Types into a field as a person would (what was in it is replaced); Enter afterwards when <paramref name="submit"/>.</summary>
    public async Task TypeAsync(int reference, string text, bool submit)
    {
        if (await Locate(reference) is null)
            throw new BrowserException($"[{reference}] isn't on the page any more: read it again (browser_read).");
        await Live.ExecuteScriptAsync($$"""
            (() => { const el = document.querySelector('[data-projict-ref="{{reference}}"]'); if (!el) return;
              el.focus(); if (el.select) el.select(); else if (el.isContentEditable) document.execCommand('selectAll'); })()
            """);
        await Input("Input.insertText", new { text });
        if (submit)
        {
            await Input("Input.dispatchKeyEvent", new { type = "keyDown", key = "Enter", code = "Enter", windowsVirtualKeyCode = 13, nativeVirtualKeyCode = 13, text = "\r" });
            await Input("Input.dispatchKeyEvent", new { type = "keyUp", key = "Enter", code = "Enter", windowsVirtualKeyCode = 13, nativeVirtualKeyCode = 13 });
        }
        await Settle();
    }

    /// <summary>
    /// Puts files into a page's file field (#ae854b1c): straight into it when [ref] is one; otherwise [ref] (a "Select files"
    /// button, say) is clicked and the file picker it opens gets them instead of showing. False when no picker opened.
    /// </summary>
    public async Task<bool> UploadAsync(int reference, IReadOnlyList<string> files)
    {
        if (await Locate(reference) is null)
            throw new BrowserException($"[{reference}] isn't on the page any more: read it again (browser_read).");
        if (Element(reference).Kind == "file")
        {
            var document = JsonNode.Parse(await Input("DOM.getDocument", new { depth = 0 }))!;
            var found = JsonNode.Parse(await Input("DOM.querySelector", new { nodeId = (int)document["root"]!["nodeId"]!, selector = $"[data-projict-ref=\"{reference}\"]" }))!;
            if ((int?)found["nodeId"] is not (> 0 and var nodeId))
                throw new BrowserException($"[{reference}] isn't on the page any more: read it again (browser_read).");
            await Input("DOM.setFileInputFiles", new { files, nodeId });
            await Settle();
            return true;
        }
        var opened = new TaskCompletionSource<int?>(TaskCreationOptions.RunContinuationsAsynchronously);
        var receiver = Live.GetDevToolsProtocolEventReceiver("Page.fileChooserOpened");
        void Chosen(object? sender, CoreWebView2DevToolsProtocolEventReceivedEventArgs e) =>
            opened.TrySetResult(JsonNode.Parse(e.ParameterObjectAsJson)?["backendNodeId"] is JsonValue id && id.TryGetValue<int>(out var node) ? node : null);
        receiver.DevToolsProtocolEventReceived += Chosen;
        await Input("Page.enable", new { });
        await Input("Page.setInterceptFileChooserDialog", new { enabled = true });
        try
        {
            await ClickAsync(reference);
            if (await Task.WhenAny(opened.Task, Task.Delay(4000)) != opened.Task || await opened.Task is not { } picked) return false;
            await Input("DOM.setFileInputFiles", new { files, backendNodeId = picked });
            await Settle();
            return true;
        }
        finally
        {
            receiver.DevToolsProtocolEventReceived -= Chosen;
            // The file picker is the user's again (when they watch, say).
            await Input("Page.setInterceptFileChooserDialog", new { enabled = false });
        }
    }

    /// <summary>Waits until the page's text has <paramref name="text"/> (or until the time's up); true when it came.</summary>
    public async Task<bool> WaitForAsync(string text, TimeSpan within)
    {
        var until = DateTime.Now + within;
        while (true)
        {
            var json = await Live.ExecuteScriptAsync("document.body ? document.body.innerText : ''");
            if ((JsonSerializer.Deserialize<string>(json) ?? "").Contains(text, StringComparison.OrdinalIgnoreCase)) return true;
            if (DateTime.Now >= until) return false;
            await Task.Delay(2000);
        }
    }

    public void Dispose()
    {
        Watching?.Close();
        Watching = null;
        _controller?.Close();
        _controller = null;
        _window?.Dispose();
        _window = null;
    }
}
