using System.IO;
using System.Net.Security;
using System.Net.Sockets;
using System.Security.Authentication;
using System.Text.Json;
using System.Text.Json.Nodes;
using System.Windows.Threading;
using CommunityToolkit.Mvvm.ComponentModel;
using ProJict.App.ViewModels;
using ProJict.Core.Model;
using ProJict.Core.Sharing;
using ProJict.Core.Storage;

namespace ProJict.App.Services.Sharing;

public enum ConnectionState { Connecting, WaitingForApproval, Connected, Reconnecting, Disconnected }

/// <summary>A ProJict you connect to as a guest, and its projects shown here.</summary>
public sealed partial class HostConnection(KnownHost host) : ObservableObject
{
    public KnownHost Host { get; } = host;
    public string Address => Host.Address;

    [ObservableProperty] private ConnectionState _state = ConnectionState.Connecting;
    [ObservableProperty] private string _hostName = host.HostName ?? host.Address;
    [ObservableProperty] private GuestRole _role;
    [ObservableProperty] private string? _message;
    /// <summary>This computer is the host's own user there (confirmed with a code): it takes part under their name.</summary>
    [ObservableProperty] private bool _isSelf;
    /// <summary>The host asks to confirm this computer is its user: type the 6-digit code it shows.</summary>
    [ObservableProperty] private bool _selfAsked;
    /// <summary>What the host said about the code typed (wrong, confirmed, …).</summary>
    [ObservableProperty] private string? _selfMessage;

    public string Title => $"{HostName}'s ProJict ({Address})";
    public string StateText => State switch
    {
        ConnectionState.Connecting => "Connecting…",
        ConnectionState.WaitingForApproval => $"Waiting for {HostName} to let you in…",
        ConnectionState.Connected => IsSelf ? $"Connected · it's you there · {Copies.Count} project(s)"
            : $"Connected · you're {(Role == GuestRole.Editor ? "an" : "a")} {Role.ToString().ToLowerInvariant()} · {Copies.Count} project(s)",
        ConnectionState.Reconnecting => "Connection lost · reconnecting…",
        _ => "Not connected",
    };

    partial void OnStateChanged(ConnectionState value) => OnPropertyChanged(nameof(StateText));
    partial void OnRoleChanged(GuestRole value) => OnPropertyChanged(nameof(StateText));
    partial void OnIsSelfChanged(bool value) => OnPropertyChanged(nameof(StateText));
    partial void OnHostNameChanged(string value) => OnPropertyChanged(nameof(Title));

    internal Wire? Wire { get; set; }
    internal CancellationTokenSource? Cts { get; set; }
    internal bool UserStopped { get; set; }
    internal int Attempts { get; set; }
    internal Dictionary<Guid, RemoteCopy> Copies { get; } = [];
    /// <summary>The host lets guests' AI agents change what it shares (through the guest's role); older hosts don't say, so no.</summary>
    public bool AgentsAllowed { get; internal set; }
    /// <summary>The priorities of items the host lets agents work on (its Settings › AI agents).</summary>
    public IReadOnlyCollection<Priority> AgentPriorities { get; internal set; } = [];

    /// <summary>The host's rules for guests' agents, from its welcome or when they change.</summary>
    internal void TakeAgentRules(JsonObject message)
    {
        AgentsAllowed = (bool?)message["agents"] == true;
        AgentPriorities = (message["agentPriorities"] as JsonArray ?? [])
            .Select(n => Enum.TryParse<Priority>((string?)n, true, out var p) ? p : (Priority?)null).OfType<Priority>().ToList();
    }
    internal HashSet<Guid> Requested { get; } = [];
    /// <summary>The host takes files in pieces, so bigger than 15 MB (its welcome says; ProJict 0.1.40 and newer).</summary>
    internal bool TakesPieces { get; set; }
    /// <summary>Files the host is sending in pieces for your channels (backPutFile), so its later pieces go to them only.</summary>
    internal HashSet<Guid> BackPuts { get; } = [];

    internal void Send(JsonObject message)
    {
        if (Wire is not { } wire || Cts is not { } cts) return;
        _ = SendAsync(wire, message, cts);
    }

    /// <summary>A file to the host as <paramref name="message"/>: whole, or in pieces when it takes them. False when it's too big for it or didn't go.</summary>
    internal async Task<bool> SendFileAsync(JsonObject message, OutgoingFile file)
    {
        if (Wire is not { } wire || Cts is not { } cts) return false;
        try { return await WireFiles.SendAsync(message, file, TakesPieces, m => SendAsync(wire, m, cts), cts.Token); }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or OperationCanceledException or ObjectDisposedException) { return false; }
    }

    private static async Task SendAsync(Wire wire, JsonObject message, CancellationTokenSource cts)
    {
        try { await wire.SendAsync(message, cts.Token).ConfigureAwait(false); }
        catch (Exception)
        {
            try { cts.Cancel(); }
            catch (ObjectDisposedException) { }
        }
    }

    internal void Refresh() => OnPropertyChanged(nameof(StateText));
}

/// <summary>A project shown from the host: the host's last version (to merge against) and whether a save is on its way.</summary>
internal sealed class RemoteCopy(Project project)
{
    public Project Project { get; } = project;
    public int Revision { get; set; }
    public string BaseJson { get; set; } = "";
    public string BaseNormalized { get; set; } = "";
    /// <summary>The copy as last sent to the host, to know when the host's "saved" means it has all of it.</summary>
    public string? SentNormalized { get; set; }
    public bool InFlight { get; set; }
    public DispatcherTimer? Timer { get; set; }
}

/// <summary>
/// Connects this ProJict to others on the network as a guest (Settings › Sharing › Connect). Their projects show in the
/// sidebar and update live; your changes go to the host after a moment (merged there with what changed meanwhile),
/// within what your role allows. Nothing of theirs is saved here except fetched pictures and files.
/// </summary>
public sealed partial class GuestService
{
    private static readonly TimeSpan SaveDelay = TimeSpan.FromMilliseconds(400);
    private static readonly TimeSpan PingEvery = TimeSpan.FromSeconds(30);
    private static readonly int[] RetrySeconds = [2, 5, 10, 30, 60];
    private readonly AppState _state;
    private readonly MainViewModel _main;
    private readonly SharingStore _store;
    private readonly Dispatcher _dispatcher;
    private bool _applying;

    public GuestService(AppState state, MainViewModel main, SharingStore store)
    {
        _state = state;
        _main = main;
        _store = store;
        _dispatcher = Dispatcher.CurrentDispatcher;
        state.RemoteProjectChanged += OnLocalChange;
        WatchOwnChat();
    }

    public System.Collections.ObjectModel.ObservableCollection<HostConnection> Connections { get; } = [];

    /// <summary>Raised when a connection's state or projects changed (UI thread).</summary>
    public event Action? Changed;

    /// <summary>Connects to every known host set to connect at start.</summary>
    public void ConnectKnownHosts()
    {
        foreach (var host in _store.Data.Hosts.Where(h => h.AutoConnect && _store.Token(h) != null).ToList())
            _ = ConnectAsync(host.Address, invite: null, confirm: null);
    }

    /// <summary>
    /// Connects with an invite (first time) or with the saved sign-in. <paramref name="confirm"/> is asked to trust a host
    /// seen for the first time (its certificate fingerprint); without it, unknown hosts are refused. Returns null when
    /// connected (or waiting for approval), else what went wrong.
    /// </summary>
    public async Task<string?> ConnectAsync(string address, string? invite, Func<string, Task<bool>>? confirm)
    {
        address = address.Trim();
        if (!TryParse(address, out var hostName, out var port)) return "Enter the host's address, e.g. 192.168.1.20 or 192.168.1.20:47613.";
        var known = _store.Data.Hosts.FirstOrDefault(h => string.Equals(h.Address, address, StringComparison.OrdinalIgnoreCase));
        if (invite == null && (known == null || _store.Token(known) == null)) return "Enter the invite code you were given.";
        var connection = Connections.FirstOrDefault(c => string.Equals(c.Address, address, StringComparison.OrdinalIgnoreCase));
        if (connection is { State: ConnectionState.Connected or ConnectionState.WaitingForApproval or ConnectionState.Connecting } && connection.Cts != null)
            return connection.State == ConnectionState.Connected ? null : "Already connecting to it.";
        if (connection == null)
        {
            connection = new HostConnection(known ?? new KnownHost { Address = address });
            Connections.Add(connection);
        }
        connection.UserStopped = false;
        connection.State = ConnectionState.Connecting;
        connection.Message = null;
        Changed?.Invoke();
        return await RunAsync(connection, hostName, port, invite, confirm, firstAttempt: true);
    }

    public void Disconnect(HostConnection connection, bool forget)
    {
        connection.UserStopped = true;
        connection.Send(Wire.Message("bye"));
        connection.Cts?.Cancel();
        DropProjects(connection);
        DropChat(connection);
        connection.State = ConnectionState.Disconnected;
        if (forget)
        {
            _store.Data.Hosts.Remove(connection.Host);
            Connections.Remove(connection);
            _store.Save();
        }
        Changed?.Invoke();
    }

    private static bool TryParse(string address, out string host, out int port)
    {
        host = address;
        port = SharingData.DefaultPort;
        var colon = address.LastIndexOf(':');
        if (colon > 0 && !address.Contains(']') && address.IndexOf(':') == colon)
        {
            host = address[..colon];
            if (!int.TryParse(address[(colon + 1)..], out port) || port is < 1 or > 65535) return false;
        }
        return host.Length > 0;
    }

    // ───────────── The connection ─────────────

    private async Task<string?> RunAsync(HostConnection connection, string hostName, int port, string? invite, Func<string, Task<bool>>? confirm, bool firstAttempt)
    {
        var cts = new CancellationTokenSource();
        connection.Cts = cts;
        var ct = cts.Token;
        var welcomed = new TaskCompletionSource<string?>(TaskCreationOptions.RunContinuationsAsynchronously);
        _ = Task.Run(async () =>
        {
            string? failure = null;
            try
            {
                using var client = new TcpClient { NoDelay = true };
                using (var connect = CancellationTokenSource.CreateLinkedTokenSource(ct))
                {
                    connect.CancelAfter(TimeSpan.FromSeconds(10));
                    await client.ConnectAsync(hostName, port, connect.Token).ConfigureAwait(false);
                }
                var pinned = connection.Host.Fingerprint;
                string? seen = null;
                await using var ssl = new SslStream(client.GetStream(), leaveInnerStreamOpen: false, (_, certificate, _, _) =>
                {
                    seen = certificate == null ? null : SharingData.Fingerprint(certificate.GetRawCertData());
                    return seen != null && (string.IsNullOrEmpty(pinned) || pinned == seen);
                });
                try
                {
                    using var handshake = CancellationTokenSource.CreateLinkedTokenSource(ct);
                    handshake.CancelAfter(TimeSpan.FromSeconds(15));
                    await ssl.AuthenticateAsClientAsync(new SslClientAuthenticationOptions { TargetHost = "ProJict" }, handshake.Token).ConfigureAwait(false);
                }
                catch (AuthenticationException) when (!string.IsNullOrEmpty(pinned) && seen != null && seen != pinned)
                {
                    connection.UserStopped = true;
                    throw new InvalidDataException(
                        "This host's certificate isn't the one you trusted before. If its ProJict was reinstalled, remove it here and connect " +
                        "again with a new invite; otherwise someone may be pretending to be it, so don't connect.");
                }
                if (string.IsNullOrEmpty(pinned))
                {
                    var trusted = confirm != null && await _dispatcher.InvokeAsync(() => confirm(seen!)).Task.Unwrap().ConfigureAwait(false);
                    if (!trusted)
                    {
                        connection.UserStopped = true;
                        throw new InvalidDataException("Not connected: you didn't trust the host's fingerprint.");
                    }
                    connection.Host.Fingerprint = seen!;
                }

                var wire = new Wire(ssl);
                connection.Wire = wire;
                var token = invite == null ? _store.Token(connection.Host) : null;
                await wire.SendAsync(token != null
                    ? Wire.Message("hello", ("version", Wire.Version), ("name", _state.UserName), ("token", token), ("pieces", true))
                    : Wire.Message("join", ("version", Wire.Version), ("name", _state.UserName), ("invite", invite), ("device", Environment.MachineName), ("pieces", true)), ct).ConfigureAwait(false);
                using var pinger = new Timer(_ => connection.Send(Wire.Message("ping")), null, PingEvery, PingEvery);
                while (true)
                {
                    var message = await wire.ReadAsync(ct).ConfigureAwait(false);
                    if (message == null) break;
                    var type = (string?)message["type"];
                    if (type == "denied")
                    {
                        connection.UserStopped = true;
                        throw new InvalidDataException((string?)message["reason"] ?? "The host refused the connection.");
                    }
                    await _dispatcher.InvokeAsync(() => Handle(connection, message)).Task.ConfigureAwait(false);
                    if (type is "welcome" or "pending") welcomed.TrySetResult(null);
                }
            }
            catch (Exception ex) when (ex is IOException or SocketException or AuthenticationException or InvalidDataException or JsonException
                                           or OperationCanceledException or ObjectDisposedException or FormatException or InvalidOperationException)
            {
                failure = ex switch
                {
                    InvalidDataException => ex.Message,
                    SocketException { SocketErrorCode: SocketError.ConnectionRefused } => "Nothing answered there: is sharing turned on in that ProJict, and the address right?",
                    SocketException or OperationCanceledException when !connection.UserStopped && connection.State == ConnectionState.Connecting =>
                        "Couldn't reach that address (is the computer on, on the same network, and does its firewall let ProJict in?).",
                    _ => null,
                };
            }
            finally
            {
                connection.Wire = null;
            }
            welcomed.TrySetResult(failure ?? "The connection closed.");
            await _dispatcher.InvokeAsync(() => Lost(connection, hostName, port, failure)).Task.ConfigureAwait(false);
        });
        var result = await welcomed.Task;
        return firstAttempt ? result : null;
    }

    /// <summary>The connection ended: show its projects read only and try again, unless you stopped it or were refused.</summary>
    private void Lost(HostConnection connection, string hostName, int port, string? failure)
    {
        connection.Cts = null;
        connection.Message = failure;
        var wasConnected = connection.State is ConnectionState.Connected or ConnectionState.Reconnecting;
        if (connection.UserStopped || !wasConnected)
        {
            DropProjects(connection);
            connection.State = ConnectionState.Disconnected;
            Changed?.Invoke();
            return;
        }
        connection.State = ConnectionState.Reconnecting;
        SetConnected(connection, false);
        Changed?.Invoke();
        var attempt = connection.Attempts++;
        var timer = new DispatcherTimer(DispatcherPriority.Background, _dispatcher);
        timer.Tick += (_, _) =>
        {
            timer.Stop();
            if (connection.UserStopped || connection.State != ConnectionState.Reconnecting || !Connections.Contains(connection)) return;
            _ = RunAsync(connection, hostName, port, invite: null, confirm: null, firstAttempt: false);
        };
        timer.Interval = TimeSpan.FromSeconds(RetrySeconds[Math.Min(attempt, RetrySeconds.Length - 1)]);
        timer.Start();
    }

    private void SetConnected(HostConnection connection, bool connected)
    {
        if (!connected)
        {
            DropChat(connection);
            DropWiki(connection);
        }
        foreach (var copy in connection.Copies.Values)
        {
            if (copy.Project.Remote is { } remote) remote.Connected = connected;
            _main.GetProjectViewModel(copy.Project).RefreshAccess();
        }
    }

    private void DropProjects(HostConnection connection)
    {
        foreach (var copy in connection.Copies.Values.ToList()) Remove(connection, copy.Project.Id);
    }

    // ───────────── Messages (UI thread) ─────────────

    private void Handle(HostConnection connection, JsonObject message)
    {
        switch ((string?)message["type"])
        {
            case "pending":
                connection.HostName = (string?)message["host"] ?? connection.HostName;
                connection.State = ConnectionState.WaitingForApproval;
                Changed?.Invoke();
                break;
            case "confirmSelf":
                connection.SelfAsked = true;
                connection.SelfMessage = null;
                _state.StatusText = $"{connection.HostName}'s ProJict asks whether this computer is {(string?)message["host"] ?? connection.HostName}: " +
                                    "type the 6-digit code it shows, in Sharing.";
                Changed?.Invoke();
                break;
            case "selfResult":
                connection.SelfMessage = (string?)message["message"];
                if ((bool?)message["done"] == true) connection.SelfAsked = false;
                if (connection.SelfMessage != null) _state.StatusText = $"{connection.HostName}'s ProJict: {connection.SelfMessage}";
                Changed?.Invoke();
                break;
            case "welcome":
                Welcome(connection, message);
                break;
            case "agentRules":
                connection.TakeAgentRules(message);
                break;
            case "project":
                Receive(connection, message["project"]!, (int)message["revision"]!);
                break;
            case "rejected":
                var reason = (string?)message["reason"];
                if (Guid.TryParse((string?)message["projectId"], out var rejected) && connection.Copies.TryGetValue(rejected, out var copy))
                {
                    copy.InFlight = false;
                    Replace(connection, copy, message["project"]!, (int)message["revision"]!, keepLocal: false);
                }
                _state.StatusText = $"{connection.HostName}'s ProJict didn't take your change: {reason}";
                break;
            case "saved":
                if (Guid.TryParse((string?)message["projectId"], out var saved) && connection.Copies.TryGetValue(saved, out var done))
                {
                    done.InFlight = false;
                    // The host had all of it already: the copy is in step with it, so it isn't sent again and again.
                    if (done.SentNormalized == ProjectMerge.Normalized(done.Project))
                    {
                        done.BaseJson = Json.Serialize(done.Project);
                        done.BaseNormalized = done.SentNormalized;
                        if ((int?)message["revision"] is { } revision) done.Revision = revision;
                    }
                    ScheduleSave(done);
                }
                break;
            case "removed":
                if (Guid.TryParse((string?)message["projectId"], out var removed)) Remove(connection, removed);
                break;
            case "testBuildComing":
                TestBuildComing(connection, message);
                break;
            case "testBuild":
                StoreTestBuild(connection, message);
                break;
            case "testBuildError":
                TestBuildFailed(connection, message);
                break;
            case "file":
                StoreFile(connection, message);
                break;
            case "chat" or "chatChannel" or "chatGone" or "chatError":
                HandleChat(connection, (string)message["type"]!, message);
                break;
            case "wiki" or "wikiPage" or "wikiGone" or "wikiError":
                HandleWiki(connection, (string)message["type"]!, message);
                break;
            case "presence":
                TakePresence(connection, message);
                break;
            case "backOp":
                HandleBackOp(connection, message);
                break;
            case "backGetFile":
                SendBackFile(connection, message);
                break;
            case "backPutFile":
                StoreBackPut(connection, message);
                break;
        }
    }

    private void Welcome(HostConnection connection, JsonObject message)
    {
        var host = connection.Host;
        connection.HostName = (string?)message["host"] ?? connection.HostName;
        connection.Role = Enum.TryParse<GuestRole>((string?)message["role"], out var role) ? role : GuestRole.Viewer;
        connection.MyName = (string?)message["name"] ?? connection.MyName;
        connection.IsSelf = (bool?)message["self"] == true;
        connection.TakesPieces = (bool?)message["pieces"] == true;
        connection.TakeAgentRules(message);
        host.HostName = connection.HostName;
        if (Guid.TryParse((string?)message["guestId"], out var guestId)) host.GuestId = guestId;
        if ((string?)message["token"] is { } token) _store.SetToken(host, token);
        if (!_store.Data.Hosts.Contains(host)) _store.Data.Hosts.Add(host);
        _store.Save();
        connection.State = ConnectionState.Connected;
        connection.Message = null;
        connection.Attempts = 0;
        // The projects follow one by one. Ones you already have wait for theirs: the host may have restarted, so its
        // version numbers mean nothing until then (your unsent changes are merged into what arrives, then sent).
        var ids = (message["projectIds"] as JsonArray ?? []).Select(n => Guid.TryParse((string?)n, out var g) ? g : Guid.Empty).ToHashSet();
        foreach (var gone in connection.Copies.Keys.Except(ids).ToList()) Remove(connection, gone);
        foreach (var copy in connection.Copies.Values)
        {
            copy.Revision = -1;
            copy.InFlight = false;
            if (copy.Project.Remote is { } remote) remote.Role = connection.Role;
        }
        SetConnected(connection, true);
        _state.StatusText = $"Connected to {connection.HostName}'s ProJict.";
        _main.SendViewing();
        SendPresence();
        if (connection.SharesChatBack) SendBackSnapshot(connection);
        Changed?.Invoke();
    }

    /// <summary>A new version of a project from the host; returns the local project (null if it can't be shown).</summary>
    private Project? Receive(HostConnection connection, JsonNode node, int revision)
    {
        var id = Guid.Parse((string)node["id"]!);
        if (connection.Copies.TryGetValue(id, out var copy))
        {
            copy.InFlight = false;
            Replace(connection, copy, node, revision, keepLocal: true);
            return copy.Project;
        }
        if (_state.Projects.Any(p => p.Id == id))
        {
            _state.StatusText = $"{connection.HostName} shares a project you also have here; it isn't shown twice.";
            return null;
        }
        var json = node.ToJsonString();
        var project = Json.Deserialize<Project>(json)!;
        project.Remote = new RemoteOrigin { HostKey = connection.Address, HostName = connection.HostName, Role = connection.Role };
        copy = new RemoteCopy(project) { Revision = revision, BaseJson = json, BaseNormalized = ProjectMerge.Normalized(project) };
        connection.Copies[id] = copy;
        _state.AddRemoteProject(project);
        return project;
    }

    /// <summary>
    /// Puts the host's version in place, keeping your changes it doesn't have yet (merged) and your folded categories.
    /// </summary>
    private void Replace(HostConnection connection, RemoteCopy copy, JsonNode node, int revision, bool keepLocal)
    {
        var json = node.ToJsonString();
        var incoming = Json.Deserialize<Project>(json)!;
        var local = copy.Project;
        var snapshot = keepLocal && ProjectMerge.Normalized(local) != copy.BaseNormalized
            ? ProjectMerge.Merge(Json.Deserialize<Project>(copy.BaseJson)!, local, incoming)
            : incoming;
        var folded = local.Categories.ToDictionary(c => c.Id, c => c.Collapsed);
        foreach (var category in snapshot.Categories)
            if (folded.TryGetValue(category.Id, out var collapsed)) category.Collapsed = collapsed;
        copy.Revision = revision;
        copy.BaseJson = json;
        copy.BaseNormalized = ProjectMerge.Normalized(incoming);
        _applying = true;
        try { _main.GetProjectViewModel(local).ReplaceFromHost(snapshot); }
        finally { _applying = false; }
        ScheduleSave(copy);
        connection.Refresh();
    }

    private void Remove(HostConnection connection, Guid projectId)
    {
        if (!connection.Copies.Remove(projectId, out var copy)) return;
        copy.Timer?.Stop();
        _main.ForgetProject(copy.Project);
        _state.RemoveRemoteProject(copy.Project);
        connection.Refresh();
    }

    // ───────────── Your changes ─────────────

    private void OnLocalChange(Project project)
    {
        if (_applying) return;
        var copy = Connections.SelectMany(c => c.Copies.Values).FirstOrDefault(c => c.Project == project);
        if (copy != null) ScheduleSave(copy);
    }

    private void ScheduleSave(RemoteCopy copy)
    {
        if (ProjectMerge.Normalized(copy.Project) == copy.BaseNormalized) return;
        if (copy.Timer == null)
        {
            copy.Timer = new DispatcherTimer(DispatcherPriority.Background, _dispatcher) { Interval = SaveDelay };
            copy.Timer.Tick += (_, _) =>
            {
                copy.Timer.Stop();
                SendSave(copy);
            };
        }
        copy.Timer.Stop();
        copy.Timer.Start();
    }

    private void SendSave(RemoteCopy copy)
    {
        var connection = Connections.FirstOrDefault(c => c.Copies.ContainsKey(copy.Project.Id));
        // One save at a time: the host's answer (its new version) comes first, then the rest is sent.
        if (connection is not { State: ConnectionState.Connected } || copy.InFlight || copy.Revision < 0) return;
        if (ProjectMerge.Normalized(copy.Project) == copy.BaseNormalized) return;
        // Files you attached go first, so the host has them when the save refers to them.
        var known = Json.Deserialize<Project>(copy.BaseJson)!.Attachments().Select(a => a.Id).ToHashSet();
        var files = copy.Project.Attachments().Where(a => !known.Contains(a.Id))
            .Select(a => (Attachment: a, Path: _state.AttachmentPath(copy.Project, a))).Where(f => File.Exists(f.Path)).ToList();
        copy.InFlight = true;
        copy.SentNormalized = ProjectMerge.Normalized(copy.Project);
        var save = Wire.Message("save", ("projectId", copy.Project.Id.ToString()), ("base", copy.Revision),
            ("project", JsonNode.Parse(Json.Serialize(copy.Project))));
        if (files.Count == 0) connection.Send(save);
        else _ = SendSaveAsync(connection, files, save);
    }

    private static async Task SendSaveAsync(HostConnection connection, List<(Attachment Attachment, string Path)> files, JsonObject save)
    {
        foreach (var (attachment, path) in files)
            await connection.SendFileAsync(Wire.Message("putFile", ("id", attachment.Id.ToString()), ("name", attachment.FileName)), new OutgoingFile(attachment.FileName, Path: path));
        connection.Send(save);
    }

    // ───────────── Files ─────────────

    /// <summary>Asks the host for an attachment's file (once) when it isn't here yet.</summary>
    public void Fetch(Project project, Attachment attachment)
    {
        if (project.Remote is not { } remote) return;
        var connection = Connections.FirstOrDefault(c => c.Address == remote.HostKey && c.State == ConnectionState.Connected);
        if (connection == null || File.Exists(_state.AttachmentPath(project, attachment)) || !connection.Requested.Add(attachment.Id)) return;
        connection.Send(Wire.Message("getFile", ("id", attachment.Id.ToString())));
    }

    /// <summary>A file you asked for (getFile), whole or a piece of it: a project's attachment, or a chat message's or wiki page's file.</summary>
    private void StoreFile(HostConnection connection, JsonObject message)
    {
        if (!Guid.TryParse((string?)message["id"], out var id)) return;
        string path;
        Action arrived;
        if (connection.Copies.Values.FirstOrDefault(c => c.Project.Attachments().Any(a => a.Id == id)) is { } copy)
        {
            path = _state.AttachmentPath(copy.Project, copy.Project.Attachments().First(a => a.Id == id));
            arrived = () =>
            {
                _main.GetProjectViewModel(copy.Project).RebuildAll();
                if (copy.Project.Icon?.Id == id) _main.IconArrived(copy.Project);
            };
        }
        else if (ChatAttachment(connection, id) is { } chat)
        {
            path = ChatFilePath(connection, chat);
            arrived = () => ChatChanged?.Invoke(connection);
        }
        else if (WikiAttachment(connection, id) is { } wiki)
        {
            path = WikiFilePath(connection, wiki);
            arrived = () => WikiChanged?.Invoke(connection);
        }
        else return;
        try
        {
            switch (WireFiles.Receive(message, path, WireFiles.MaxBytes))
            {
                case FileArrival.Done: arrived(); break;
                case FileArrival.Failed: connection.Requested.Remove(id); break;
            }
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            connection.Requested.Remove(id);
        }
    }

    /// <summary>Tells each host which of its projects you have open (it shows you there, like an agent's badge).</summary>
    public void Viewing(Project? project)
    {
        foreach (var connection in Connections.Where(c => c.State == ConnectionState.Connected))
        {
            var id = project?.Remote?.HostKey == connection.Address ? project.Id.ToString() : null;
            connection.Send(Wire.Message("viewing", ("projectId", id)));
        }
    }
}
