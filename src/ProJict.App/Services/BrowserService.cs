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
/// ProJict's browser for agents. A tool's browser (Settings › Tools, a tool in the browser such as Suno or Tripo3D) has
/// the tool's own profile, where you signed in yourself (Sign in…), and stays on its website; one agent at a time uses
/// it, by its connection (so a name it picks meanwhile keeps it). The web browser (no tool) is for any agent that has no
/// browser of its own: each gets a private one that keeps nothing (no sign-ins, cookies or history), goes where
/// <see cref="WebAllowed"/> lets it, and at most <see cref="MaxWeb"/> are open at once. Either is let go when the agent
/// closes it or disconnects, after <see cref="IdleFor"/> without use, or when you Stop it. Used on the UI thread.
/// </summary>
public sealed class BrowserService(ToolService tools, Dispatcher dispatcher)
{
    /// <summary>A browser nobody has used for this long is closed, and another agent may have it.</summary>
    public static readonly TimeSpan IdleFor = TimeSpan.FromMinutes(10);
    /// <summary>Web browsers open at once (each takes a few hundred MB).</summary>
    public const int MaxWeb = 3;

    private readonly Dictionary<Guid, BrowserSession> _sessions = [];
    private readonly List<BrowserSession> _web = [];
    /// <summary>Agents whose browser you stopped, by tool: told once, on their next call.</summary>
    private readonly Dictionary<Guid, McpSession> _stopped = [];
    /// <summary>Agents whose web browser you stopped: told once, on their next call.</summary>
    private readonly List<McpSession> _stoppedWeb = [];

    /// <summary>How far agents may go in the web browser (Settings › Tools), asked at every step.</summary>
    public Func<WebBrowsing> WebAllowed { get; set; } = () => WebBrowsing.Off;

    public IReadOnlyCollection<BrowserSession> Sessions => [.. _sessions.Values, .. _web];
    /// <summary>A browser opened, closed, changed hands or finished a download.</summary>
    public event Action? Changed;
    /// <summary>A download finished (or failed): who it was for and where it is.</summary>
    public event Action<BrowserSession, BrowserDownload>? Downloaded;

    public BrowserSession? SessionOf(ExternalTool tool) => _sessions.GetValueOrDefault(tool.Id);

    /// <summary>The agent's web browser, if it has one open.</summary>
    public BrowserSession? WebOf(McpSession agent) => _web.FirstOrDefault(s => IsHeldBy(s, agent));

    /// <summary>The agent has it: the same connection, or the same name (it connected again since).</summary>
    public static bool IsHeldBy(BrowserSession session, McpSession agent) =>
        session.Holder == agent || string.Equals(session.Agent, agent.AgentName, StringComparison.OrdinalIgnoreCase);

    private static bool Same(McpSession a, McpSession b) => a == b || string.Equals(a.AgentName, b.AgentName, StringComparison.OrdinalIgnoreCase);

    /// <summary>Where screenshots and image search pictures go for agents to look at (kept a day).</summary>
    public string PicturesFolder
    {
        get
        {
            var folder = tools.PicturesFolder;
            Directory.CreateDirectory(folder);
            return folder;
        }
    }

    /// <summary>
    /// The tool's browser for this agent, started if it isn't running. Refused while another agent has used it within
    /// <see cref="IdleFor"/>; an agent whose browser you stopped hears so once.
    /// </summary>
    public async Task<BrowserSession> TakeAsync(ExternalTool tool, McpSession agent, DateTime now)
    {
        if (_stopped.TryGetValue(tool.Id, out var stoppedFor) && Same(stoppedFor, agent))
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
        session = Track(new BrowserSession(tool, agent, Path.Combine(tools.ProfileFolder(tool), "Downloads"), now));
        await session.StartAsync(tools.ProfileFolder(tool), privately: false);
        _sessions[tool.Id] = session;
        Changed?.Invoke();
        return session;
    }

    /// <summary>
    /// The agent's own web browser, started (private, keeping nothing) if it hasn't one. Refused when browsing is off, while
    /// <see cref="MaxWeb"/> others are in use, and once after you stopped the agent's.
    /// </summary>
    public async Task<BrowserSession> TakeWebAsync(McpSession agent, DateTime now)
    {
        if (_stoppedWeb.FirstOrDefault(s => Same(s, agent)) is { } stopped)
        {
            _stoppedWeb.Remove(stopped);
            throw new BrowserException("The user stopped your use of ProJict's web browser. Ask them before using it again.");
        }
        if (WebAllowed() == WebBrowsing.Off)
            throw new BrowserException(BrowserRules.WebProblem(null, WebBrowsing.Off, false)!);
        if (WebOf(agent) is { } session)
        {
            session.Holder = agent;
            session.LastUsed = now;
            Changed?.Invoke();
            return session;
        }
        if (_web.Count >= MaxWeb)
        {
            // One nobody used for a while makes room.
            if (_web.Where(s => now - s.LastUsed >= IdleFor && s.Watching == null).MinBy(s => s.LastUsed) is { } idle) Release(idle);
            else throw new BrowserException($"{MaxWeb} agents ({string.Join(", ", _web.Select(s => s.Agent))}) are using ProJict's web browser; " +
                                            "it takes one more when one of them closes theirs (browser_close). Try again later.");
        }
        session = Track(new BrowserSession(null, agent, Path.Combine(tools.WebFolder, "Downloads"), now) { WebAllowed = WebAllowed });
        await session.StartAsync(tools.WebFolder, privately: true);
        _web.Add(session);
        Changed?.Invoke();
        return session;
    }

    private BrowserSession Track(BrowserSession session)
    {
        session.Downloaded += download =>
        {
            Downloaded?.Invoke(session, download);
            Changed?.Invoke();
        };
        return session;
    }

    /// <summary>The agent is done with it (browser_close): it's closed.</summary>
    public void Release(BrowserSession session)
    {
        if (!(session.Tool is { } tool ? _sessions.Remove(tool.Id) : _web.Remove(session))) return;
        session.Dispose();
        Changed?.Invoke();
    }

    /// <summary>You stopped it (Stop on the bar): it's closed, and the agent is told on its next call.</summary>
    public void Stop(BrowserSession session)
    {
        if (session.Tool is { } tool) _stopped[tool.Id] = session.Holder;
        else _stoppedWeb.Add(session.Holder);
        Release(session);
    }

    /// <summary>The agent disconnected: the browsers it had are closed, for others to use.</summary>
    public void ReleaseHeldBy(McpSession agent)
    {
        foreach (var session in Sessions.Where(s => s.Holder == agent).ToList()) Release(session);
    }

    /// <summary>Closes the browsers nobody has used for <see cref="IdleFor"/>, and forgets pictures older than a day.</summary>
    public void CloseIdle(DateTime now)
    {
        foreach (var session in Sessions.Where(s => now - s.LastUsed >= IdleFor && s.Watching == null).ToList()) Release(session);
        try
        {
            if (Directory.Exists(tools.PicturesFolder))
                foreach (var file in new DirectoryInfo(tools.PicturesFolder).EnumerateFiles().Where(f => now - f.LastWriteTime > TimeSpan.FromDays(1)))
                    file.Delete();
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { }
    }

    /// <summary>ProJict is quitting.</summary>
    public void CloseAll()
    {
        foreach (var session in Sessions.ToList()) Release(session);
    }

    internal Dispatcher Dispatcher => dispatcher;
}

/// <summary>Something an agent may not do in ProJict's browser, said to it in words.</summary>
public sealed class BrowserException(string message) : Exception(message);

/// <summary>A file downloaded in ProJict's browser: where it was saved, and whether it finished.</summary>
public sealed record BrowserDownload(string Path, string FileName, bool Finished, string? Problem);

/// <summary>A link, button or field on the page, numbered for the agent (its ref).</summary>
public sealed record BrowserElement(int Ref, string Kind, string Text, string Label, string Type, string Name, string Autocomplete, string Href, string Value, bool Checked, bool Disabled,
    IReadOnlyList<string>? Options = null)
{
    /// <summary>"[3] button “Create”", "[5] field “Song description”", "[8] link “Library” → https://suno.com/me", "[9] choice “Size” = “M” (S | M | L)".</summary>
    public string Line => $"[{Ref}] {Kind} “{(Text.Length > 0 ? Text : Label)}”" + (Kind == "link" && Href.Length > 0 ? $" → {Href}" : "") +
                          (Value.Length > 0 ? $" = “{Value}”" : "") + (Options is { Count: > 0 } o ? $" ({string.Join(" | ", o)})" : "") +
                          (Checked ? " (ticked)" : "") + (Disabled ? " (disabled)" : "");
}

/// <summary>A picture on the page: its address, what it says it shows, and its size.</summary>
public sealed record BrowserImage(int Number, string Url, string Alt, int Width, int Height)
{
    public string Line => $"({Number}) {Width}×{Height}" + (Alt.Length > 0 ? $" “{Alt}”" : "") + $" {Url}";
}

/// <summary>A page as an agent reads it: its title, address, text (a part of it) and numbered links, buttons and fields.</summary>
public sealed record BrowserPage(string Title, string Url, string Text, IReadOnlyList<BrowserElement> Elements, int Part = 1, int Parts = 1,
    IReadOnlyList<BrowserImage>? Images = null);

/// <summary>A line from the page's console: a log, a warning, an error or an uncaught exception.</summary>
public sealed record BrowserConsoleLine(DateTime At, string Level, string Text)
{
    public string Line => $"{At:HH:mm:ss} {Level}: {Text}";
    public bool IsError => Level is "error" or "exception";
}

/// <summary>
/// ProJict's browser: Edge WebView2 in a window nobody sees. A tool's has the tool's profile and stays on its website; a
/// web one is private and goes where the user lets agents go. It takes downloads itself (no dialog), is driven with real
/// clicks and typing (DevTools input, as a person would) and keeps what the page's console says.
/// </summary>
public sealed class BrowserSession : IDisposable
{
    public const int PageWidth = 1400, PageHeight = 900;
    /// <summary>Characters of a page's text in one read; browser_read's part reads the next.</summary>
    public const int PartLength = 6000;
    private const int ConsoleKept = 300;
    private static int _next;
    private HwndSource? _window;
    private CoreWebView2Environment? _environment;
    private CoreWebView2Controller? _controller;
    private TaskCompletionSource<bool>? _loading;
    private readonly List<BrowserConsoleLine> _console = [];
    private int _consoleRead;

    internal BrowserSession(ExternalTool? tool, McpSession holder, string downloads, DateTime now)
    {
        Tool = tool;
        Holder = holder;
        DownloadFolder = downloads;
        LastUsed = now;
        Number = Interlocked.Increment(ref _next);
    }

    /// <summary>The tool it's for, or null for the web browser.</summary>
    public ExternalTool? Tool { get; }
    public bool IsWeb => Tool == null;
    public int Number { get; }
    /// <summary>"Suno", or "the web".</summary>
    public string Name => Tool?.Name ?? "the web";
    /// <summary>How far the web browser may go, asked at every step (the user may change it meanwhile).</summary>
    internal Func<WebBrowsing> WebAllowed { get; init; } = () => WebBrowsing.Off;
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
    /// <summary>Somewhere it was kept from going, and why, for the agent's next answer.</summary>
    public string? Blocked { get; set; }
    public string? BlockedWhy { get; set; }
    public List<BrowserDownload> Downloads { get; } = [];
    /// <summary>What the agent hasn't been told yet (a download attached, or one that failed), for its next answer.</summary>
    public List<string> News { get; } = [];
    /// <summary>The window you watch it in, while you do (the bar's Watch).</summary>
    public System.Windows.Window? Watching { get; set; }
    /// <summary>The page's size when the agent asked for another ("390x844", phone), or null for the usual 1400×900.</summary>
    public string? Size { get; private set; }
    public CoreWebView2? Web => _controller?.CoreWebView2;
    internal CoreWebView2Controller? Controller => _controller;
    internal IntPtr HiddenWindow => _window?.Handle ?? IntPtr.Zero;

    /// <summary>The site it's on now, for the bar ("localhost:5173", "en.wikipedia.org").</summary>
    public string Where => Uri.TryCreate(Web?.Source, UriKind.Absolute, out var u) && u.Scheme is "http" or "https"
        ? (u.IsDefaultPort ? u.Host : $"{u.Host}:{u.Port}") : Name;

    internal event Action<BrowserDownload>? Downloaded;

    /// <summary>Why the agent may not go to this address in this browser, or null when it may.</summary>
    public string? Problem(string? address) => Tool is { } tool
        ? BrowserRules.IsOnSite(address, tool.Website) ? null : $"Agents stay on {tool.Name}'s website ({tool.Website}) in ProJict's browser; “{address}” isn't on it."
        : BrowserRules.WebProblem(address, WebAllowed(), Holder.Remote);

    internal async Task StartAsync(string profile, bool privately)
    {
        try
        {
            _environment = await CoreWebView2Environment.CreateAsync(null, profile);
            // A window of its own that is never shown or activated: WebView2 needs one to live in.
            _window = new HwndSource(new HwndSourceParameters($"ProJict browser: {Name}")
            {
                WindowStyle = unchecked((int)0x80000000), Width = PageWidth, Height = PageHeight, PositionX = -32000, PositionY = -32000,
            });
            if (privately)
            {
                // InPrivate, a profile of its own: nothing is kept, and agents never share cookies or sign-ins.
                var options = _environment.CreateCoreWebView2ControllerOptions();
                options.IsInPrivateModeEnabled = true;
                options.ProfileName = $"agent{Number}";
                _controller = await _environment.CreateCoreWebView2ControllerAsync(_window.Handle, options);
            }
            else _controller = await _environment.CreateCoreWebView2ControllerAsync(_window.Handle);
            _controller.Bounds = new System.Drawing.Rectangle(0, 0, PageWidth, PageHeight);
            _controller.IsVisible = true;
            var web = _controller.CoreWebView2;
            web.Settings.AreDevToolsEnabled = false;
            web.Settings.IsPasswordAutosaveEnabled = false;
            web.Settings.IsGeneralAutofillEnabled = false;
            web.NavigationStarting += (_, e) =>
            {
                if (e.Uri.StartsWith("about:", StringComparison.Ordinal) || Problem(e.Uri) is not { } why) return;
                e.Cancel = true;
                (Blocked, BlockedWhy) = (e.Uri, why);
            };
            web.NewWindowRequested += (_, e) =>
            {
                // A link that opens a window opens here instead, if it may.
                e.Handled = true;
                if (Problem(e.Uri) is { } why) (Blocked, BlockedWhy) = (e.Uri, why);
                else web.Navigate(e.Uri);
            };
            if (privately) GuardTheNetwork(web);
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
            await ListenToConsole(web);
        }
        catch (Exception ex) when (ex is WebView2RuntimeNotFoundException or COMException or IOException or UnauthorizedAccessException or InvalidOperationException or ArgumentException)
        {
            Dispose();
            throw new BrowserException(ex is WebView2RuntimeNotFoundException
                ? "ProJict's browser needs Microsoft Edge WebView2, which isn't on the user's computer."
                : $"ProJict's browser didn't start: {ex.Message}");
        }
    }

    /// <summary>
    /// A web page never reaches other devices on your network, nor this computer's own sites unless it's one of them:
    /// frames are kept from going there, and anything it fetches from there (a picture, a script, a call) is refused.
    /// </summary>
    private void GuardTheNetwork(CoreWebView2 web)
    {
        web.FrameNavigationStarting += (_, e) =>
        {
            if (!BrowserRules.MayFetch(e.Uri, web.Source)) e.Cancel = true;
        };
        web.AddWebResourceRequestedFilter("*", CoreWebView2WebResourceContext.All, CoreWebView2WebResourceRequestSourceKinds.All);
        web.WebResourceRequested += (_, e) =>
        {
            // Pages and frames are checked as they're opened (NavigationStarting, FrameNavigationStarting).
            if (e.ResourceContext == CoreWebView2WebResourceContext.Document) return;
            if (!BrowserRules.MayFetch(e.Request.Uri, web.Source))
                e.Response = _environment!.CreateWebResourceResponse(null, 403, "Refused by ProJict", "");
        };
    }

    /// <summary>Keeps what the page's console says (logs, warnings, errors, uncaught exceptions) for browser_console.</summary>
    private async Task ListenToConsole(CoreWebView2 web)
    {
        void Keep(string level, string text)
        {
            if (text.Length > 2000) text = text[..2000] + "…";
            _console.Add(new BrowserConsoleLine(DateTime.Now, level, text));
            if (_console.Count > ConsoleKept)
            {
                var drop = _console.Count - ConsoleKept;
                _console.RemoveRange(0, drop);
                _consoleRead = Math.Max(0, _consoleRead - drop);
            }
        }
        web.GetDevToolsProtocolEventReceiver("Runtime.consoleAPICalled").DevToolsProtocolEventReceived += (_, e) =>
        {
            var call = JsonNode.Parse(e.ParameterObjectAsJson);
            var level = (string?)call?["type"] switch { "warning" => "warning", "error" or "assert" => "error", "debug" => "debug", "info" => "info", _ => "log" };
            var parts = (call?["args"] as JsonArray ?? []).Select(a => a?["value"] is JsonValue v ? v.ToString() : (string?)a?["description"] ?? (string?)a?["type"] ?? "");
            Keep(level, string.Join(" ", parts));
        };
        web.GetDevToolsProtocolEventReceiver("Runtime.exceptionThrown").DevToolsProtocolEventReceived += (_, e) =>
        {
            var details = JsonNode.Parse(e.ParameterObjectAsJson)?["exceptionDetails"];
            var text = (string?)details?["exception"]?["description"] ?? (string?)details?["text"] ?? "An error";
            if ((string?)details?["url"] is { Length: > 0 } url) text += $" ({url}:{(int?)details?["lineNumber"] + 1})";
            Keep("exception", text);
        };
        web.GetDevToolsProtocolEventReceiver("Log.entryAdded").DevToolsProtocolEventReceived += (_, e) =>
        {
            var entry = JsonNode.Parse(e.ParameterObjectAsJson)?["entry"];
            var level = (string?)entry?["level"] switch { "error" => "error", "warning" => "warning", _ => "log" };
            Keep(level, ((string?)entry?["text"] ?? "") + ((string?)entry?["url"] is { Length: > 0 } url ? $" ({url})" : ""));
        };
        await web.CallDevToolsProtocolMethodAsync("Runtime.enable", "{}");
        await web.CallDevToolsProtocolMethodAsync("Log.enable", "{}");
    }

    /// <summary>What the console said since the agent last asked (or all that's kept).</summary>
    public IReadOnlyList<BrowserConsoleLine> ConsoleSince(bool all)
    {
        var lines = all ? _console.ToList() : _console.Skip(_consoleRead).ToList();
        _consoleRead = _console.Count;
        return lines;
    }

    /// <summary>Errors in the console the agent hasn't asked about yet.</summary>
    public int NewConsoleErrors => _console.Skip(_consoleRead).Count(l => l.IsError);

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

    private CoreWebView2 Live => Web ?? throw new BrowserException($"ProJict's browser for {Name} isn't open.");

    /// <summary>Goes to an address it may go to and waits for it to load (up to <paramref name="seconds"/>).</summary>
    public async Task GoAsync(string address, double seconds = 30)
    {
        if (Problem(address) is { } why) throw new BrowserException(why);
        _loading = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
        Live.Navigate(address);
        await Task.WhenAny(_loading.Task, Task.Delay(TimeSpan.FromSeconds(seconds)));
        await Settle();
    }

    /// <summary>Back or forward in the browser's history.</summary>
    public async Task HistoryAsync(bool back)
    {
        if (back ? !Live.CanGoBack : !Live.CanGoForward) throw new BrowserException(back ? "There's no page to go back to." : "There's no page to go forward to.");
        _loading = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
        if (back) Live.GoBack(); else Live.GoForward();
        await Task.WhenAny(_loading.Task, Task.Delay(TimeSpan.FromSeconds(30)));
        await Settle();
    }

    /// <summary>A moment for the page to react (scripts, a page that follows), after a click, typing or loading.</summary>
    public static Task Settle(int milliseconds = 1200) => Task.Delay(milliseconds);

    private const string ReadScript = """
        ((part, withImages) => {
          const visible = el => { const r = el.getBoundingClientRect(); const s = getComputedStyle(el);
            return r.width > 0 && r.height > 0 && s.visibility !== 'hidden' && s.display !== 'none' && s.opacity !== '0'; };
          document.querySelectorAll('[data-projict-ref]').forEach(e => e.removeAttribute('data-projict-ref'));
          const tidy = t => (t || '').trim().replace(/\s+/g, ' ').slice(0, 120);
          const labelFor = el => {
            if (el.id) { const l = document.querySelector('label[for="' + CSS.escape(el.id) + '"]'); if (l) return tidy(l.innerText); }
            const p = el.closest('label'); return p ? tidy(p.innerText) : tidy(el.getAttribute('aria-label') || el.getAttribute('placeholder'));
          };
          const picks = document.querySelectorAll('a[href], button, input:not([type=hidden]), textarea, select, [role=button], [role=link], [role=tab], [role=menuitem], [role=checkbox], [role=switch], [role=combobox], [role=option], [contenteditable=true]');
          const elements = []; let n = 0;
          for (const el of picks) {
            if (elements.length >= 150) break;
            if (!visible(el) || el.closest('[aria-hidden=true]')) continue;
            n++; el.setAttribute('data-projict-ref', String(n));
            const tag = el.tagName.toLowerCase(); const type = (el.getAttribute('type') || '').toLowerCase();
            const kind = tag === 'a' || el.getAttribute('role') === 'link' ? 'link' : tag === 'select' ? 'choice'
              : (tag === 'input' || tag === 'textarea' || el.isContentEditable) ? (type === 'checkbox' || type === 'radio' ? type : type === 'file' ? 'file' : 'field') : 'button';
            const field = kind === 'field';
            const options = tag === 'select' ? Array.from(el.options).slice(0, 30).map(o => tidy(o.text)) : null;
            elements.push({ ref: n, kind, text: tidy(field || tag === 'select' ? '' : (el.getAttribute('aria-label') || el.innerText || el.value || el.getAttribute('title') || el.getAttribute('alt'))),
              label: labelFor(el), type, name: el.getAttribute('name') || '', autocomplete: el.getAttribute('autocomplete') || '',
              href: tag === 'a' ? el.href : '', value: tag === 'select' ? tidy(el.selectedOptions[0] ? el.selectedOptions[0].text : '')
                : field && type !== 'password' ? tidy(el.value !== undefined ? el.value : el.innerText) : '',
              checked: !!el.checked, disabled: !!el.disabled || el.getAttribute('aria-disabled') === 'true', options });
          }
          const all = (document.body ? document.body.innerText : '').replace(/\n{3,}/g, '\n\n');
          const size = __PART__;
          const parts = Math.max(1, Math.ceil(all.length / size));
          const p = Math.min(Math.max(1, part), parts);
          const images = [];
          if (withImages) {
            const seen = new Set();
            for (const img of document.images) {
              if (images.length >= 40) break;
              const src = img.currentSrc || img.src;
              if (!src || seen.has(src) || src.startsWith('data:') || img.naturalWidth < 48 || img.naturalHeight < 48 || !visible(img)) continue;
              seen.add(src);
              images.push({ src, alt: tidy(img.alt || img.title), w: img.naturalWidth, h: img.naturalHeight });
            }
          }
          return JSON.stringify({ title: document.title, url: location.href, text: all.slice((p - 1) * size, p * size), part: p, parts, elements, images });
        })
        """;

    /// <summary>Reads the page: its title, address, text (one part of it) and numbered links, buttons and fields (the refs to click and type in).</summary>
    public async Task<BrowserPage> ReadAsync(int part = 1, bool images = false)
    {
        var json = await Live.ExecuteScriptAsync(ReadScript.Replace("__PART__", PartLength.ToString()) + $"({part}, {(images ? "true" : "false")})");
        var inner = JsonSerializer.Deserialize<string>(json) ?? "{}";
        var page = JsonNode.Parse(inner)!.AsObject();
        var elements = (page["elements"]?.AsArray() ?? []).Select(e => new BrowserElement(
            (int)e!["ref"]!, (string)e["kind"]!, (string?)e["text"] ?? "", (string?)e["label"] ?? "", (string?)e["type"] ?? "", (string?)e["name"] ?? "",
            (string?)e["autocomplete"] ?? "", (string?)e["href"] ?? "", (string?)e["value"] ?? "", (bool?)e["checked"] ?? false, (bool?)e["disabled"] ?? false,
            e["options"] is JsonArray o ? [.. o.Select(x => (string?)x ?? "")] : null)).ToList();
        var pictures = (page["images"]?.AsArray() ?? []).Select((i, n) => new BrowserImage(n + 1, (string?)i!["src"] ?? "", (string?)i["alt"] ?? "", (int?)i["w"] ?? 0, (int?)i["h"] ?? 0)).ToList();
        Page = new BrowserPage((string?)page["title"] ?? "", (string?)page["url"] ?? "", (string?)page["text"] ?? "", elements,
            (int?)page["part"] ?? 1, (int?)page["parts"] ?? 1, images ? pictures : null);
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
        if (submit) await KeyAsync(Keys["enter"], 0);
        await Settle();
    }

    /// <summary>Picks an option of a drop-down list (a select) by its text or value; the option picked, or null when there's none such.</summary>
    public async Task<string?> ChooseAsync(int reference, string option)
    {
        if (await Locate(reference) is null)
            throw new BrowserException($"[{reference}] isn't on the page any more: read it again (browser_read).");
        var json = await Live.ExecuteScriptAsync($$"""
            ((want) => { const el = document.querySelector('[data-projict-ref="{{reference}}"]'); if (!el || !el.options) return null;
              const norm = t => (t || '').trim().replace(/\s+/g, ' ').toLowerCase(); const w = norm(want);
              const o = Array.from(el.options).find(o => norm(o.text) === w || norm(o.value) === w) || Array.from(el.options).find(o => norm(o.text).includes(w));
              if (!o) return null; el.focus(); el.value = o.value;
              el.dispatchEvent(new Event('input', { bubbles: true })); el.dispatchEvent(new Event('change', { bubbles: true }));
              return o.text.trim(); })({{JsonSerializer.Serialize(option)}})
            """);
        await Settle(600);
        return JsonSerializer.Deserialize<string?>(json);
    }

    private sealed record Key(string Name, string Code, int VirtualKey, string? Text = null);

    private static readonly Dictionary<string, Key> Keys = new(StringComparer.OrdinalIgnoreCase)
    {
        ["enter"] = new("Enter", "Enter", 13, "\r"), ["tab"] = new("Tab", "Tab", 9), ["escape"] = new("Escape", "Escape", 27), ["esc"] = new("Escape", "Escape", 27),
        ["backspace"] = new("Backspace", "Backspace", 8), ["delete"] = new("Delete", "Delete", 46), ["space"] = new(" ", "Space", 32, " "),
        ["arrowup"] = new("ArrowUp", "ArrowUp", 38), ["arrowdown"] = new("ArrowDown", "ArrowDown", 40), ["arrowleft"] = new("ArrowLeft", "ArrowLeft", 37),
        ["arrowright"] = new("ArrowRight", "ArrowRight", 39), ["up"] = new("ArrowUp", "ArrowUp", 38), ["down"] = new("ArrowDown", "ArrowDown", 40),
        ["left"] = new("ArrowLeft", "ArrowLeft", 37), ["right"] = new("ArrowRight", "ArrowRight", 39),
        ["pageup"] = new("PageUp", "PageUp", 33), ["pagedown"] = new("PageDown", "PageDown", 34), ["home"] = new("Home", "Home", 36), ["end"] = new("End", "End", 35),
    };

    /// <summary>The keys browser_press knows, for its description and refusals.</summary>
    public const string KeyNames = "Enter, Tab, Escape, Backspace, Delete, Space, ArrowUp, ArrowDown, ArrowLeft, ArrowRight, PageUp, PageDown, Home, End, a letter or digit, and Ctrl+, Shift+ or Alt+ with any of them";

    /// <summary>
    /// Presses a key as a person would ("PageDown", "Escape", "Ctrl+A", "Shift+Tab"), on the element <paramref name="reference"/>
    /// when given (it's focused first), else wherever the page has its focus.
    /// </summary>
    public async Task PressAsync(string combination, int? reference)
    {
        var parts = combination.Split('+', StringSplitOptions.TrimEntries | StringSplitOptions.RemoveEmptyEntries);
        if (parts.Length == 0) throw new BrowserException($"Say which key: {KeyNames}.");
        var modifiers = 0;
        foreach (var m in parts[..^1])
            modifiers |= m.ToLowerInvariant() switch
            {
                "alt" => 1, "ctrl" or "control" => 2, "meta" or "cmd" or "win" => 4, "shift" => 8,
                _ => throw new BrowserException($"“{m}” isn't Ctrl, Shift or Alt: say keys like Ctrl+A or Shift+Tab."),
            };
        var name = parts[^1];
        Key key;
        if (Keys.TryGetValue(name, out var known)) key = known;
        else if (name.Length == 1 && char.IsLetterOrDigit(name[0]))
        {
            var upper = char.ToUpperInvariant(name[0]);
            var printed = char.IsDigit(upper) || (modifiers & 8) != 0 ? upper.ToString() : char.ToLowerInvariant(upper).ToString();
            // With Ctrl, Alt or the Windows key it's a shortcut (Ctrl+A), not a letter typed.
            key = new Key(printed, char.IsDigit(upper) ? $"Digit{upper}" : $"Key{upper}", upper, (modifiers & 7) == 0 ? printed : null);
        }
        else throw new BrowserException($"ProJict's browser doesn't know the key “{name}”. Keys: {KeyNames}. To type text, use browser_type.");
        if (reference is { } r)
        {
            if (await Locate(r) is null) throw new BrowserException($"[{r}] isn't on the page any more: read it again (browser_read).");
            await Live.ExecuteScriptAsync($"(() => {{ const el = document.querySelector('[data-projict-ref=\"{r}\"]'); if (el) el.focus(); }})()");
        }
        await KeyAsync(key, modifiers);
        await Settle(800);
    }

    private async Task KeyAsync(Key key, int modifiers)
    {
        await Input("Input.dispatchKeyEvent", key.Text != null
            ? new { type = "keyDown", key = key.Name, code = key.Code, windowsVirtualKeyCode = key.VirtualKey, nativeVirtualKeyCode = key.VirtualKey, modifiers, text = key.Text }
            : (object)new { type = "rawKeyDown", key = key.Name, code = key.Code, windowsVirtualKeyCode = key.VirtualKey, nativeVirtualKeyCode = key.VirtualKey, modifiers });
        await Input("Input.dispatchKeyEvent", new { type = "keyUp", key = key.Name, code = key.Code, windowsVirtualKeyCode = key.VirtualKey, nativeVirtualKeyCode = key.VirtualKey, modifiers });
    }

    /// <summary>Scrolls the page with the mouse wheel, as a person would: down or up by about a screen.</summary>
    public async Task ScrollAsync(bool down, int screens = 1)
    {
        var height = Size is { } s && s.Split('x') is [_, var h] && int.TryParse(h, out var hh) ? hh : PageHeight;
        await Input("Input.dispatchMouseEvent", new { type = "mouseWheel", x = 200, y = 200, deltaX = 0, deltaY = (down ? 1 : -1) * screens * (int)(height * 0.85) });
        await Settle(800);
    }

    /// <summary>
    /// The page's size: "phone" (390×844, a touch screen), "tablet" (820×1180), "desktop" (the usual 1400×900) or
    /// "WIDTHxHEIGHT"; pages laid out for that size, as on such a device.
    /// </summary>
    public async Task ResizeAsync(string size)
    {
        var (width, height, mobile) = size.Trim().ToLowerInvariant() switch
        {
            "phone" or "mobile" => (390, 844, true),
            "tablet" => (820, 1180, true),
            "desktop" or "" => (PageWidth, PageHeight, false),
            var s when s.Split('x', '×') is [var w, var h] && int.TryParse(w, out var ww) && int.TryParse(h, out var hh) && ww is >= 200 and <= 3840 && hh is >= 200 and <= 4000
                => (ww, hh, ww < 800),
            _ => throw new BrowserException($"“{size}” isn't a size: say phone, tablet, desktop or a width and height like 1280x720 (200 to 3840 by 200 to 4000)."),
        };
        if (!mobile && width == PageWidth && height == PageHeight)
        {
            await Input("Emulation.clearDeviceMetricsOverride", new { });
            await Input("Emulation.setTouchEmulationEnabled", new { enabled = false });
            Size = null;
        }
        else
        {
            await Input("Emulation.setDeviceMetricsOverride", new { width, height, deviceScaleFactor = 1, mobile });
            await Input("Emulation.setTouchEmulationEnabled", new { enabled = mobile, maxTouchPoints = mobile ? 5 : 0 });
            Size = $"{width}x{height}";
        }
        await Settle(600);
    }

    /// <summary>
    /// A picture of the page as it shows now (JPEG): what's on screen, or the whole page from the top (up to 8000 pixels
    /// tall) with <paramref name="fullPage"/>; with <paramref name="reference"/> just that element, scrolled into view.
    /// </summary>
    public async Task<byte[]> ScreenshotAsync(bool fullPage, int? reference = null)
    {
        object parameters;
        if (reference is { } r)
        {
            if (await Locate(r) is null) throw new BrowserException($"[{r}] isn't on the page any more: read it again (browser_read).");
            var json = await Live.ExecuteScriptAsync($$"""
                (() => { const el = document.querySelector('[data-projict-ref="{{r}}"]'); if (!el) return null; const b = el.getBoundingClientRect();
                  return JSON.stringify({ x: b.left + scrollX, y: b.top + scrollY, w: b.width, h: b.height }); })()
                """);
            if (json == "null") throw new BrowserException($"[{r}] isn't on the page any more: read it again (browser_read).");
            var box = JsonNode.Parse(JsonSerializer.Deserialize<string>(json)!)!;
            parameters = new
            {
                format = "jpeg", quality = 80, captureBeyondViewport = true,
                clip = new { x = Math.Max(0, (double)box["x"]! - 8), y = Math.Max(0, (double)box["y"]! - 8), width = Math.Max(1, (double)box["w"]! + 16), height = Math.Min(4000, Math.Max(1, (double)box["h"]! + 16)), scale = 1 },
            };
        }
        else if (fullPage)
        {
            var metrics = JsonNode.Parse(await Input("Page.getLayoutMetrics", new { }))!;
            var content = metrics["cssContentSize"] ?? metrics["contentSize"];
            var width = Math.Max(1, (double?)content?["width"] ?? PageWidth);
            var height = Math.Min(8000, Math.Max(1, (double?)content?["height"] ?? PageHeight));
            parameters = new { format = "jpeg", quality = 75, captureBeyondViewport = true, clip = new { x = 0, y = 0, width, height, scale = 1 } };
        }
        else parameters = new { format = "jpeg", quality = 80 };
        var shot = JsonNode.Parse(await Input("Page.captureScreenshot", parameters))!;
        return Convert.FromBase64String((string)shot["data"]!);
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
