using System.Collections.ObjectModel;
using System.IO;
using System.Net;
using System.Net.Security;
using System.Net.Sockets;
using System.Security.Authentication;
using System.Security.Cryptography;
using System.Security.Cryptography.X509Certificates;
using System.Text.Json;
using System.Text.Json.Nodes;
using System.Windows.Threading;
using ProJict.App.ViewModels;
using ProJict.Core.Agents;
using ProJict.Core.Chat;
using ProJict.Core.Model;
using ProJict.Core.Sharing;
using ProJict.Core.Storage;

namespace ProJict.App.Services.Sharing;

public enum JoinAnswer { Approve, Deny, Block }

/// <summary>Someone asking to join with one of your invites, waiting for your answer.</summary>
public sealed class JoinRequest(string name, string device, string address, Invite invite)
{
    public string Name { get; } = name;
    public string Device { get; } = device;
    public string Address { get; } = address;
    public Invite Invite { get; } = invite;
    internal TaskCompletionSource<JoinAnswer> Answer { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
    /// <summary>Let in as you, on another computer: it's asked for a code at once.</summary>
    public bool Self { get; set; }

    public string Text => $"{Name} ({Device}, {Address}) asks to join your ProJict as {Invite.Role.ToString().ToLowerInvariant()} with invite {Invite.Code}.";
}

/// <summary>A guest connected right now.</summary>
public sealed class GuestSession(GuestAccount account, Wire wire, string address, CancellationTokenSource cts)
{
    public GuestAccount Account { get; } = account;
    public string Address { get; } = address;
    internal Wire Wire { get; } = wire;
    internal CancellationTokenSource Cts { get; } = cts;
    /// <summary>Projects the guest has, so it hears when one stops being shared with it.</summary>
    internal HashSet<Guid> Sent { get; } = [];
    /// <summary>Chat channels the guest has, likewise.</summary>
    internal HashSet<Guid> ChatSent { get; } = [];
    /// <summary>Wiki pages the guest has, likewise.</summary>
    internal HashSet<Guid> WikiSent { get; } = [];
    /// <summary>The agent rules the guest last heard (so it hears again only when they change).</summary>
    internal string? RulesSent { get; set; }
    /// <summary>Who's online or away, as the guest last heard it.</summary>
    internal string? PresenceSent { get; set; }
    /// <summary>The guest's own channels, when it shares them back.</summary>
    public BackChat? Back { get; internal set; }

    internal void Send(JsonObject message) => _ = SendAsync(message);

    internal async Task SendAsync(JsonObject message)
    {
        try { await Wire.SendAsync(message, Cts.Token).ConfigureAwait(false); }
        catch (Exception)
        {
            // The connection is gone; its reader ends the session.
            try { Cts.Cancel(); }
            catch (ObjectDisposedException) { }
        }
    }
}

/// <summary>
/// Shares your projects with other ProJict apps on the network (Settings › Sharing): a TLS listener, invites you
/// approve, and live editing. Every guest change is checked against the guest's role, merged with what changed
/// meanwhile and applied as an undo step; every change goes out to the connected guests that see the project.
/// </summary>
public sealed partial class HostService
{
    private const int KeptRevisions = 50;
    private static readonly TimeSpan BroadcastDelay = TimeSpan.FromMilliseconds(300);
    private static readonly TimeSpan Silence = TimeSpan.FromSeconds(100);
    private const int MaxFileBytes = 15 * 1024 * 1024;

    private readonly AppState _state;
    private readonly MainViewModel _main;
    private readonly SharingStore _store;
    private readonly Dispatcher _dispatcher;
    private readonly List<GuestSession> _sessions = [];
    private readonly Dictionary<Guid, History> _history = [];
    private readonly Dictionary<Guid, DispatcherTimer> _timers = [];
    private readonly Dictionary<string, (int Count, DateTime Since)> _failedJoins = [];
    private TcpListener? _listener;
    private CancellationTokenSource? _cts;
    private X509Certificate2? _certificate;
    private bool _applying;

    private sealed class History
    {
        public int Revision;
        public string Latest = "";
        public readonly Dictionary<int, string> Snapshots = [];
    }

    public HostService(AppState state, MainViewModel main, SharingStore store)
    {
        _state = state;
        _main = main;
        _store = store;
        _dispatcher = Dispatcher.CurrentDispatcher;
        state.ProjectChanged += OnProjectChanged;
        state.ProjectListChanged += OnProjectListChanged;
        state.Chat.Changed += OnChatChanged;
        state.Chat.Removed += channel => SendChannel(channel);
        state.Wiki.Changed += OnWikiChanged;
        state.Wiki.Removed += SendPage;
    }

    public SharingData Data => _store.Data;
    public ObservableCollection<JoinRequest> Requests { get; } = [];
    public IReadOnlyList<GuestSession> Sessions => _sessions;
    public bool IsRunning => _listener != null;
    public string? Error { get; private set; }
    public int Port { get; private set; }
    /// <summary>Any address by default; tests use loopback so nothing is opened to the network.</summary>
    public IPAddress BindAddress { get; set; } = IPAddress.Any;
    public string Fingerprint => _certificate != null ? SharingData.Fingerprint(_certificate.RawData) : "";

    /// <summary>Raised when guests, requests or the listener change (on the UI thread).</summary>
    public event Action? Changed;

    public bool IsConnected(GuestAccount account) => _sessions.Any(s => s.Account == account);

    // ───────────── Listening ─────────────

    public bool Start()
    {
        if (_listener != null) return true;
        try
        {
            _certificate ??= _store.HostCertificate();
            var listener = new TcpListener(BindAddress, Data.Port);
            listener.Start();
            _listener = listener;
            Port = ((IPEndPoint)listener.LocalEndpoint).Port;
            Error = null;
            _cts = new CancellationTokenSource();
            _ = AcceptAsync(listener, _cts.Token);
        }
        catch (Exception ex) when (ex is SocketException or CryptographicException)
        {
            Error = ex is SocketException { SocketErrorCode: SocketError.AddressAlreadyInUse }
                ? $"Port {Data.Port} is in use by another program; choose another."
                : ex.Message;
            _listener = null;
        }
        Changed?.Invoke();
        return _listener != null;
    }

    public void Stop()
    {
        _cts?.Cancel();
        _listener?.Stop();
        _listener = null;
        foreach (var session in _sessions.ToList()) session.Cts.Cancel();
        foreach (var request in Requests.ToList()) request.Answer.TrySetResult(JoinAnswer.Deny);
        Requests.Clear();
        Changed?.Invoke();
    }

    /// <summary>This computer's network addresses, for telling guests where to connect.</summary>
    public IReadOnlyList<string> Addresses()
    {
        try
        {
            return Dns.GetHostAddresses(Dns.GetHostName())
                .Where(a => a.AddressFamily == AddressFamily.InterNetwork && !IPAddress.IsLoopback(a))
                .Select(a => $"{a}:{Port}").Distinct().ToList();
        }
        catch (SocketException)
        {
            return [];
        }
    }

    private async Task AcceptAsync(TcpListener listener, CancellationToken ct)
    {
        while (!ct.IsCancellationRequested)
        {
            TcpClient client;
            try { client = await listener.AcceptTcpClientAsync(ct).ConfigureAwait(false); }
            catch (Exception) { break; }
            _ = Task.Run(() => HandleAsync(client, ct));
        }
    }

    // ───────────── Invites and guests ─────────────

    /// <param name="letsIn">Whoever joins with it is in at once, without asking you.</param>
    public Invite CreateInvite(GuestRole role, List<string>? groups, bool letsIn = false)
    {
        Data.Invites.RemoveAll(i => !i.IsValid(DateTime.Now));
        var invite = new Invite { Code = SharingData.NewInviteCode(), Role = role, Groups = groups, LetsIn = letsIn, ExpiresAt = DateTime.Now.AddHours(24) };
        Data.Invites.Add(invite);
        _store.Save();
        Changed?.Invoke();
        return invite;
    }

    public void RevokeInvite(Invite invite)
    {
        Data.Invites.Remove(invite);
        _store.Save();
        Changed?.Invoke();
    }

    public void Answer(JoinRequest request, JoinAnswer answer)
    {
        Requests.Remove(request);
        request.Answer.TrySetResult(answer);
        Changed?.Invoke();
    }

    /// <summary>New role or groups: the guest is reconnected so it gets what it may now see.</summary>
    public void Update(GuestAccount account, GuestRole role, List<string>? groups)
    {
        account.Role = role;
        account.Groups = groups is { Count: > 0 } ? groups : null;
        _store.Save();
        Disconnect(account);
    }

    public void SetBlocked(GuestAccount account, bool blocked)
    {
        account.Blocked = blocked;
        _store.Save();
        if (blocked) Disconnect(account);
        Changed?.Invoke();
    }

    public void Remove(GuestAccount account)
    {
        Data.Guests.Remove(account);
        _store.Save();
        Disconnect(account);
        Changed?.Invoke();
    }

    private void Disconnect(GuestAccount account)
    {
        foreach (var session in _sessions.Where(s => s.Account == account).ToList()) session.Cts.Cancel();
    }

    // ───────────── A connection ─────────────

    private Task<T> Ui<T>(Func<T> work) => _dispatcher.InvokeAsync(work).Task;
    private Task Ui(Action work) => _dispatcher.InvokeAsync(work).Task;

    private static JsonObject Denied(string reason) => Wire.Message("denied", ("reason", reason));

    private async Task HandleAsync(TcpClient client, CancellationToken listening)
    {
        var address = (client.Client.RemoteEndPoint as IPEndPoint)?.Address.ToString() ?? "?";
        using var cts = CancellationTokenSource.CreateLinkedTokenSource(listening);
        var ct = cts.Token;
        GuestSession? session = null;
        try
        {
            using var _ = client;
            client.NoDelay = true;
            await using var ssl = new SslStream(client.GetStream(), leaveInnerStreamOpen: false);
            using (var handshake = CancellationTokenSource.CreateLinkedTokenSource(ct))
            {
                handshake.CancelAfter(TimeSpan.FromSeconds(15));
                await ssl.AuthenticateAsServerAsync(new SslServerAuthenticationOptions { ServerCertificate = _certificate }, handshake.Token).ConfigureAwait(false);
            }
            var wire = new Wire(ssl);
            JsonObject? first;
            using (var hello = CancellationTokenSource.CreateLinkedTokenSource(ct))
            {
                hello.CancelAfter(TimeSpan.FromSeconds(30));
                first = await wire.ReadAsync(hello.Token).ConfigureAwait(false);
            }
            if (first == null) return;
            if ((int?)first["version"] != Wire.Version)
            {
                await wire.SendAsync(Denied("The two ProJicts are different versions; update both to the newest."), ct).ConfigureAwait(false);
                return;
            }
            var name = Clean((string?)first["name"], 60) ?? "Guest";
            GuestAccount? account;
            string? token = null;
            Task<JsonObject?>? pendingRead = null;
            JoinRequest? joined = null;
            switch ((string?)first["type"])
            {
                case "hello":
                    var hash = SharingData.Hash((string?)first["token"] ?? "");
                    account = await Ui(() => Data.Guests.FirstOrDefault(g => g.TokenHash == hash)).ConfigureAwait(false);
                    if (account == null || account.Blocked)
                    {
                        await wire.SendAsync(Denied(account == null ? "This ProJict doesn't know you any more; ask for a new invite." : "You were blocked by this ProJict."), ct).ConfigureAwait(false);
                        return;
                    }
                    break;
                case "join":
                    if (await Ui(() => TooManyFailures(address)).ConfigureAwait(false))
                    {
                        await wire.SendAsync(Denied("Too many attempts; try again in a few minutes."), ct).ConfigureAwait(false);
                        return;
                    }
                    var code = SharingData.NormalizeCode((string?)first["invite"] ?? "");
                    var device = Clean((string?)first["device"], 60) ?? "unknown computer";
                    var request = await Ui(() =>
                    {
                        var invite = Data.Invites.FirstOrDefault(i => i.Code == code && i.IsValid(DateTime.Now));
                        if (invite == null)
                        {
                            NoteFailure(address);
                            return null;
                        }
                        var r = new JoinRequest(name, device, address, invite);
                        if (invite.LetsIn)
                        {
                            // You let them in when you made the code: no asking.
                            r.Answer.TrySetResult(JoinAnswer.Approve);
                            _state.StatusText = $"{name} ({device}) joined with invite {invite.Code} as {invite.Role.ToString().ToLowerInvariant()}.";
                            return r;
                        }
                        Requests.Add(r);
                        _state.StatusText = r.Text;
                        Changed?.Invoke();
                        return r;
                    }).ConfigureAwait(false);
                    if (request == null)
                    {
                        await wire.SendAsync(Denied("That invite code isn't valid. Codes work once, for 24 hours."), ct).ConfigureAwait(false);
                        return;
                    }
                    if (!request.Invite.LetsIn) await wire.SendAsync(Wire.Message("pending", ("host", _state.UserName)), ct).ConfigureAwait(false);
                    // Wait for your answer; the guest may give up meanwhile (its connection closes).
                    pendingRead = wire.ReadAsync(ct);
                    var giveUp = Task.Delay(TimeSpan.FromMinutes(15), ct);
                    Task answered;
                    while (true)
                    {
                        answered = await Task.WhenAny(request.Answer.Task, pendingRead, giveUp).ConfigureAwait(false);
                        // The guest keeps the connection alive with pings while it waits: that isn't giving up.
                        if (answered != pendingRead || !pendingRead.IsCompletedSuccessfully || (string?)pendingRead.Result?["type"] != "ping") break;
                        pendingRead = wire.ReadAsync(ct);
                    }
                    if (answered != request.Answer.Task)
                    {
                        await Ui(() =>
                        {
                            Requests.Remove(request);
                            Changed?.Invoke();
                        }).ConfigureAwait(false);
                        return;
                    }
                    var answer = request.Answer.Task.Result;
                    var newToken = SharingData.NewToken();
                    account = await Ui(() =>
                    {
                        Data.Invites.Remove(request.Invite);
                        if (answer == JoinAnswer.Deny)
                        {
                            _store.Save();
                            return null;
                        }
                        var a = new GuestAccount
                        {
                            Name = name, Role = request.Invite.Role, Groups = request.Invite.Groups, Device = device,
                            TokenHash = SharingData.Hash(newToken), Blocked = answer == JoinAnswer.Block,
                        };
                        Data.Guests.Add(a);
                        _store.Save();
                        Changed?.Invoke();
                        return a.Blocked ? null : a;
                    }).ConfigureAwait(false);
                    if (account == null)
                    {
                        await wire.SendAsync(Denied($"{_state.UserName} didn't let you join."), ct).ConfigureAwait(false);
                        return;
                    }
                    token = newToken;
                    joined = request;
                    break;
                default:
                    return;
            }

            session = new GuestSession(account, wire, address, cts);
            await Ui(() =>
            {
                _sessions.Add(session);
                account.LastSeen = DateTime.Now;
                _store.Save();
                // Queued in order on the UI thread, so no newer version of a project can overtake these.
                foreach (var message in Welcome(session, token)) session.Send(message);
                _main.Agents.PersonConnected(session, NameOf(account));
                SendPresence();
                _state.StatusText = $"{account.Name} connected to your ProJict.";
                Changed?.Invoke();
                if (joined?.Self == true) StartSelf(account);
            }).ConfigureAwait(false);

            var next = pendingRead ?? wire.ReadAsync(ct);
            while (true)
            {
                var message = await next.WaitAsync(Silence, ct).ConfigureAwait(false);
                if (message == null || (string?)message["type"] == "bye") break;
                var reply = await Ui(() => Handle(session, message)).ConfigureAwait(false);
                if (reply != null) await session.SendAsync(reply).ConfigureAwait(false);
                next = wire.ReadAsync(ct);
            }
        }
        catch (Exception ex) when (ex is IOException or AuthenticationException or InvalidDataException or JsonException or OperationCanceledException
                                       or TimeoutException or SocketException or ObjectDisposedException or FormatException or InvalidOperationException)
        {
            // The guest left, the network dropped or it sent something broken: the connection ends.
        }
        finally
        {
            if (session != null)
                await Ui(() =>
                {
                    _sessions.Remove(session);
                    session.Account.LastSeen = DateTime.Now;
                    _store.Save();
                    _main.Agents.PersonDisconnected(session);
                    DropBack(session);
                    Changed?.Invoke();
                }).ConfigureAwait(false);
        }
    }

    private static string? Clean(string? text, int max)
    {
        var clean = new string((text ?? "").Where(c => !char.IsControl(c)).ToArray()).Trim();
        return clean.Length == 0 ? null : clean.Length > max ? clean[..max] : clean;
    }

    private bool TooManyFailures(string address) =>
        _failedJoins.TryGetValue(address, out var f) && f.Count >= 5 && DateTime.Now - f.Since < TimeSpan.FromMinutes(10);

    private void NoteFailure(string address)
    {
        var f = _failedJoins.TryGetValue(address, out var was) && DateTime.Now - was.Since < TimeSpan.FromMinutes(10) ? was : (0, DateTime.Now);
        _failedJoins[address] = (f.Item1 + 1, f.Item2);
    }

    // ───────────── Messages (UI thread) ─────────────

    /// <summary>What a guest sees: your own projects in the groups shared with them, never a private one.</summary>
    private IEnumerable<Project> Visible(GuestAccount account) => _state.Projects.Where(p => Sees(account, p));

    private static bool Sees(GuestAccount account, Project project) =>
        !project.IsInbox && project.Remote == null && !project.Private && account.Sees(ProjectGroups.Of(project));

    /// <summary>The welcome (who you are, which projects you'll get), then each project as a message of its own.</summary>
    private List<JsonObject> Welcome(GuestSession session, string? token)
    {
        var projects = Visible(session.Account).ToList();
        var welcome = Wire.Message("welcome",
            ("guestId", session.Account.Id.ToString()), ("role", RoleOf(session.Account).ToString()), ("host", _state.UserName), ("name", NameOf(session.Account)),
            ("projectIds", new JsonArray(projects.Select(p => (JsonNode)p.Id.ToString()).ToArray())));
        if (token != null) welcome["token"] = token;
        if (session.Account.Self) welcome["self"] = true;
        AddAgentRules(welcome, session.Account);
        session.RulesSent = AgentRulesSignature(session.Account);
        var messages = new List<JsonObject> { welcome };
        foreach (var project in projects)
        {
            var (revision, json) = Current(project);
            session.Sent.Add(project.Id);
            messages.Add(Wire.Message("project", ("revision", revision), ("project", ForGuest(json))));
        }
        messages.Add(ChatSnapshot(session));
        messages.Add(WikiSnapshot(session));
        return messages;
    }

    private JsonObject? Handle(GuestSession session, JsonObject message)
    {
        switch ((string?)message["type"])
        {
            case "ping":
                return Wire.Message("pong");
            case "save":
                return ApplySave(session, message);
            case "putFile":
                return StoreFile(session, message);
            case "getFile":
                SendFile(session, message);
                return null;
            case "chatOp":
                return ChatOp(session, message);
            case "wikiOp":
                return WikiOp(session, message);
            case "selfCode":
                return ConfirmSelf(session, message);
            case "viewing":
                var id = Guid.TryParse((string?)message["projectId"], out var g) ? g : (Guid?)null;
                _main.Agents.PersonViewing(session, id is { } v && Visible(session.Account).Any(p => p.Id == v) ? v : null);
                return null;
            case "presence":
                // Their agents act here as "Ada (via Alice)", or by their own names from a ProJict linked as you.
                var agents = Presence.FromJson(message["agents"]).Take(100)
                    .ToDictionary(a => session.Account.Self ? a.Key : AgentNaming.Via(a.Key, NameOf(session.Account)), a => a.Value, StringComparer.OrdinalIgnoreCase);
                _main.Agents.PersonPresence(session, (bool?)message["away"] == true, agents);
                BackPresence(session, (bool?)message["away"] == true, Presence.FromJson(message["agents"]));
                if (session.Back is { } back) BackChatChanged?.Invoke(back);
                return null;
            case "backChat" or "backChannel" or "backGone" or "backFile" or "backError":
                HandleBack(session, (string)message["type"]!, message);
                return null;
            default:
                return null;
        }
    }

    private JsonObject? ApplySave(GuestSession session, JsonObject message)
    {
        var account = session.Account;
        var id = Guid.Parse((string)message["projectId"]!);
        var project = Visible(account).FirstOrDefault(p => p.Id == id);
        if (project == null) return Wire.Message("removed", ("projectId", id.ToString()));
        var incoming = Json.Deserialize<Project>(message["project"]!.ToJsonString())!;
        var baseRevision = (int?)message["base"] ?? 0;
        var history = HistoryOf(project);
        // What changed here since guests last got the project (say an item added a moment ago, not sent out yet) is a
        // version of its own, so a save based on the one before merges with it instead of replacing it.
        Bump(project, history);
        Project candidate;
        if (baseRevision == history.Revision) candidate = incoming;
        else if (history.Snapshots.TryGetValue(baseRevision, out var baseJson))
            candidate = ProjectMerge.Merge(Json.Deserialize<Project>(baseJson)!, incoming, project);
        else return Rejected(project, "The project changed too much meanwhile, so your last change couldn't be kept.");

        var (accepted, refusal) = GuestAccess.Check(project, candidate, NameOf(account), RoleOf(account), account.Self || Data.GuestAgents,
            _state.Settings.AgentPriorities, account.Self);
        if (refusal != null) return Rejected(project, refusal);
        if (ProjectMerge.Normalized(accepted!) == ProjectMerge.Normalized(project))
            return Wire.Message("saved", ("projectId", id.ToString()), ("revision", history.Revision));

        _applying = true;
        try { _main.GetProjectViewModel(project).ApplyFromGuest(accepted!); }
        finally { _applying = false; }
        _state.StatusText = $"{account.Name} changed {project.Name}.";
        Broadcast(project);
        return null;
    }

    private JsonObject Rejected(Project project, string reason)
    {
        var (revision, json) = Current(project);
        return Wire.Message("rejected", ("projectId", project.Id.ToString()), ("reason", reason), ("revision", revision), ("project", ForGuest(json)));
    }

    /// <summary>A file for an attachment a guest added (stored before its save arrives, which then refers to it).</summary>
    private JsonObject? StoreFile(GuestSession session, JsonObject message)
    {
        // Commenters send files with comments and chat messages; what refers to them is checked when it arrives.
        if (!GuestAccess.CanComment(RoleOf(session.Account))) return null;
        if (!Guid.TryParse((string?)message["id"], out var id)) return null;
        var data = Convert.FromBase64String((string?)message["data"] ?? "");
        if (data.Length > MaxFileBytes) return null;
        var attachment = new Attachment { Id = id, FileName = ProjectStore.SafeFileName((string?)message["name"] ?? "file"), Size = data.Length };
        var path = _state.Store.AttachmentPath(attachment);
        if (File.Exists(path) || Directory.Exists(Path.GetDirectoryName(path))) return null; // never overwrite
        try { _state.Store.StoreAttachment(attachment, data); }
        catch (IOException) { }
        return null;
    }

    private void SendFile(GuestSession session, JsonObject message)
    {
        if (!Guid.TryParse((string?)message["id"], out var id)) return;
        var attachment = Visible(session.Account).SelectMany(p => p.Attachments()).FirstOrDefault(a => a.Id == id) ?? ChatAttachment(session, id) ?? WikiAttachment(session, id);
        if (attachment == null) return;
        var path = _state.Store.AttachmentPath(attachment);
        _ = Task.Run(async () =>
        {
            try
            {
                var info = new FileInfo(path);
                if (!info.Exists || info.Length > MaxFileBytes) return;
                var data = await File.ReadAllBytesAsync(path).ConfigureAwait(false);
                await session.SendAsync(Wire.Message("file", ("id", id.ToString()), ("data", Convert.ToBase64String(data)))).ConfigureAwait(false);
            }
            catch (IOException) { }
        });
    }

    // ───────────── Keeping guests up to date ─────────────

    private History HistoryOf(Project project)
    {
        if (_history.TryGetValue(project.Id, out var history)) return history;
        // Numbered from a random start, so a save based on a version from before this ProJict restarted can't pass for
        // one of now.
        history = new History { Revision = Random.Shared.Next(1, 1 << 30) };
        _history[project.Id] = history;
        Bump(project, history);
        return history;
    }

    private (int Revision, string Json) Current(Project project)
    {
        var history = HistoryOf(project);
        Bump(project, history);
        return (history.Revision, history.Latest);
    }

    /// <summary>A new revision when the project differs from the last one guests got.</summary>
    private static bool Bump(Project project, History history)
    {
        var json = Json.Serialize(project);
        if (json == history.Latest) return false;
        history.Revision++;
        history.Latest = json;
        history.Snapshots[history.Revision] = json;
        history.Snapshots.Remove(history.Revision - KeptRevisions);
        return true;
    }

    /// <summary>What guests get: not your folder paths or the original imported files.</summary>
    private static JsonNode ForGuest(string json)
    {
        var node = JsonNode.Parse(json)!.AsObject();
        node.Remove("repoPath");
        node.Remove("archivedFiles");
        return node;
    }

    private void OnProjectChanged(Project project)
    {
        if (_applying || _sessions.Count == 0 || project.Remote != null) return;
        if (!_timers.TryGetValue(project.Id, out var timer))
        {
            timer = new DispatcherTimer(DispatcherPriority.Background, _dispatcher) { Interval = BroadcastDelay };
            timer.Tick += (_, _) =>
            {
                timer.Stop();
                if (_state.Projects.Contains(project)) Broadcast(project);
            };
            _timers[project.Id] = timer;
        }
        timer.Stop();
        timer.Start();
    }

    private void Broadcast(Project project)
    {
        if (_timers.TryGetValue(project.Id, out var timer)) timer.Stop();
        var history = HistoryOf(project);
        Bump(project, history);
        var message = Wire.Message("project", ("revision", history.Revision), ("project", ForGuest(history.Latest)));
        foreach (var session in _sessions.Where(s => Sees(s.Account, project)))
        {
            session.Sent.Add(project.Id);
            session.Send((JsonObject)message.DeepClone());
        }
        OnProjectListChanged();
    }

    // ───────────── Guests' agents ─────────────

    /// <summary>Whether guests' AI agents may work in what you share (the Sharing window); guests hear it at once.</summary>
    public bool GuestAgents
    {
        get => Data.GuestAgents;
        set
        {
            if (Data.GuestAgents == value) return;
            Data.GuestAgents = value;
            _store.Save();
            SendAgentRules();
        }
    }

    /// <summary>
    /// What guests' agents may do here: whether at all, and on items of which priorities (Settings › AI agents). Your own
    /// agents on another computer of yours always may, like the ones here.
    /// </summary>
    private void AddAgentRules(JsonObject message, GuestAccount account)
    {
        message["agents"] = account.Self || Data.GuestAgents;
        message["agentPriorities"] = new JsonArray(_state.Settings.AgentPriorities.Select(p => (JsonNode)p.ToString()).ToArray());
    }

    private string AgentRulesSignature(GuestAccount account) =>
        $"{account.Self || Data.GuestAgents}:{string.Join(",", _state.Settings.AgentPriorities.Order())}";

    /// <summary>Tells connected guests the rules when they changed since they last heard them.</summary>
    private void SendAgentRules()
    {
        foreach (var session in _sessions)
        {
            var signature = AgentRulesSignature(session.Account);
            if (session.RulesSent == signature) continue;
            var message = Wire.Message("agentRules");
            AddAgentRules(message, session.Account);
            session.RulesSent = signature;
            session.Send(message);
        }
    }

    /// <summary>Projects added, deleted or moved to another group: guests get the ones they may now see and lose the others.</summary>
    private void OnProjectListChanged()
    {
        SyncChat();
        SyncWiki();
        // Settings › AI agents saved (it refreshes the list too): the agent rules may have changed.
        SendAgentRules();
        foreach (var session in _sessions)
        {
            var visible = Visible(session.Account).ToList();
            foreach (var project in visible.Where(p => !session.Sent.Contains(p.Id)))
            {
                var (revision, json) = Current(project);
                session.Sent.Add(project.Id);
                session.Send(Wire.Message("project", ("revision", revision), ("project", ForGuest(json))));
            }
            foreach (var gone in session.Sent.Except(visible.Select(p => p.Id)).ToList())
            {
                session.Sent.Remove(gone);
                session.Send(Wire.Message("removed", ("projectId", gone.ToString())));
            }
        }
    }
}
