using System.Globalization;
using System.IO;
using System.Net;
using System.Net.Sockets;
using System.Security.Cryptography;
using System.Security.Cryptography.X509Certificates;
using System.Text.Json;
using System.Text.Json.Nodes;
using System.Windows.Threading;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Hosting.Server;
using Microsoft.AspNetCore.Hosting.Server.Features;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.StaticFiles;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Net.Http.Headers;
using ProJict.Core.Agents;
using ProJict.Core.Api;
using ProJict.Core.Model;

namespace ProJict.App.Services.Api;

/// <summary>
/// ProJict's API (Settings › API): a REST API and the agent tools over MCP's HTTP transport, both answered by
/// <see cref="AgentTools"/> under the same rules as local agents. Every request needs a token; work is signed with its
/// name. Off by default; on, it serves this computer only, or the network over HTTPS.
/// </summary>
public sealed class ApiServer
{
    private const long MaxBodyBytes = 20 * 1024 * 1024;
    private const string McpPath = "/mcp";
    private const string SessionHeader = "Mcp-Session-Id";
    private static readonly TimeSpan RestIdle = TimeSpan.FromMinutes(10);
    private static readonly TimeSpan McpIdle = TimeSpan.FromHours(1);
    private static readonly TimeSpan LastUsedSaveEvery = TimeSpan.FromMinutes(1);
    private static readonly JsonSerializerOptions ReadableJson = new() { Encoder = System.Text.Encodings.Web.JavaScriptEncoder.UnsafeRelaxedJsonEscaping };
    private static readonly FileExtensionContentTypeProvider ContentTypes = new();

    private readonly AgentTools _tools;
    private readonly ApiStore _store;
    private readonly Func<IReadOnlyList<Project>> _projects;
    private readonly string _version;
    private readonly Dispatcher _dispatcher;
    private readonly ApiGate _gate = new();
    private readonly ApiToolHost _readWrite;
    private readonly ApiToolHost _readOnly;
    /// <summary>REST callers by token, and MCP sessions by their Mcp-Session-Id.</summary>
    private readonly Dictionary<Guid, Client> _rest = [];
    private readonly Dictionary<string, Client> _mcp = [];
    private readonly Lock _lock = new();
    private WebApplication? _app;
    private Timer? _sweep;
    private X509Certificate2? _certificate;

    private sealed class Client(ApiToken token, McpSession session)
    {
        public ApiToken Token { get; } = token;
        public McpSession Session { get; } = session;
        public DateTime LastSeen { get; set; } = DateTime.Now;
    }

    public ApiServer(AgentTools tools, ApiStore store, Func<IReadOnlyList<Project>> projects, string version)
    {
        _tools = tools;
        _store = store;
        _projects = projects;
        _version = version;
        _dispatcher = Dispatcher.CurrentDispatcher;
        _readWrite = new ApiToolHost(tools, readOnly: false);
        _readOnly = new ApiToolHost(tools, readOnly: true);
    }

    public ApiData Data => _store.Data;
    public ApiStore Store => _store;
    public bool IsRunning => _app != null;
    public bool IsStarting { get; private set; }
    public string? Error { get; private set; }
    public int Port { get; private set; }
    /// <summary>Serving the network over HTTPS (set when it started).</summary>
    public bool Https { get; private set; }
    /// <summary>Where to listen instead of the default; tests use loopback even with network access, so nothing is opened to the network.</summary>
    public IPAddress? BindAddress { get; set; }
    public string Fingerprint => _certificate != null ? Core.Sharing.SharingData.Fingerprint(_certificate.RawData) : "";
    public X509Certificate2? Certificate => _certificate;

    /// <summary>Raised on the UI thread when the server starts or stops, or tokens change or get used.</summary>
    public event Action? Changed;

    /// <summary>Where to reach it from this computer.</summary>
    public string LocalUrl => $"{(Https ? "https" : "http")}://{(Https ? "localhost" : "127.0.0.1")}:{Port}";

    /// <summary>Where other devices reach it: by this computer's name, or its addresses.</summary>
    public IReadOnlyList<string> NetworkUrls() =>
        [$"https://{Environment.MachineName}:{Port}", .. ApiStore.NetworkAddresses().Select(a => $"https://{a}:{Port}")];

    // ───────────── Starting and stopping ─────────────

    public async Task<bool> StartAsync()
    {
        if (_app != null || IsStarting) return _app != null;
        IsStarting = true;
        Error = null;
        bool network;
        int port;
        lock (_store.Sync) (network, port) = (Data.AllowNetwork, Data.Port);
        try
        {
            if (network) _certificate ??= await Task.Run(_store.Certificate);
            var builder = WebApplication.CreateEmptyBuilder(new WebApplicationOptions { ApplicationName = "ProJict" });
            builder.WebHost.UseKestrelCore();
            if (network) builder.WebHost.UseKestrelHttpsConfiguration();
            builder.WebHost.ConfigureKestrel(kestrel =>
            {
                kestrel.AddServerHeader = false;
                kestrel.Limits.MaxRequestBodySize = MaxBodyBytes;
                Action<Microsoft.AspNetCore.Server.Kestrel.Core.ListenOptions> secure = l => { if (network) l.UseHttps(_certificate!); };
                if (BindAddress != null) kestrel.Listen(BindAddress, port, secure);
                else if (network) kestrel.ListenAnyIP(port, secure);
                else kestrel.Listen(IPAddress.Loopback, port, secure);
            });
            var app = builder.Build();
            app.Run(HandleAsync);
            await app.StartAsync();
            _app = app;
            Https = network;
            Port = BoundPort(app) ?? port;
            _sweep = new Timer(_ => Sweep(), null, TimeSpan.FromMinutes(1), TimeSpan.FromMinutes(1));
        }
        catch (Exception ex)
        {
            Error = ex is IOException && (ex.InnerException is Microsoft.AspNetCore.Connections.AddressInUseException or SocketException
                { SocketErrorCode: SocketError.AddressAlreadyInUse } || ex.Message.Contains("address already in use", StringComparison.OrdinalIgnoreCase))
                ? $"Port {port} is in use by another program; choose another."
                : ex.Message;
        }
        finally
        {
            IsStarting = false;
        }
        RaiseChanged();
        return _app != null;
    }

    public async Task StopAsync()
    {
        var app = _app;
        _app = null;
        _sweep?.Dispose();
        _sweep = null;
        EndSessions(_ => true);
        if (app != null)
        {
            try
            {
                using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(2));
                await app.StopAsync(timeout.Token);
                await app.DisposeAsync();
            }
            catch (Exception) { /* stopping anyway */ }
        }
        RaiseChanged();
    }

    public async Task RestartAsync()
    {
        await StopAsync();
        await StartAsync();
    }

    private static int? BoundPort(WebApplication app)
    {
        var addresses = app.Services.GetRequiredService<IServer>().Features.Get<IServerAddressesFeature>()?.Addresses;
        return addresses?.Select(a => Uri.TryCreate(a.Replace("://+", "://localhost").Replace("://*", "://localhost").Replace("[::]", "localhost"), UriKind.Absolute, out var uri) ? uri.Port : (int?)null)
            .FirstOrDefault(p => p != null);
    }

    // ───────────── Tokens (UI thread) ─────────────

    /// <summary>Makes a token and returns its secret; throws ArgumentException when something is missing.</summary>
    public string CreateToken(string name, ApiAccess access, IReadOnlyCollection<Guid>? projects, int days)
    {
        string secret;
        lock (_store.Sync) secret = Data.Create(name, access, projects, days, DateTime.Now, out _);
        _store.Save();
        RaiseChanged();
        return secret;
    }

    /// <summary>A new secret for the token; the old one stops working and its connections end.</summary>
    public string RenewToken(ApiToken token)
    {
        string secret;
        lock (_store.Sync) secret = Data.Renew(token, DateTime.Now);
        EndSessions(c => c.Token == token);
        _store.Save();
        RaiseChanged();
        return secret;
    }

    public void RevokeToken(ApiToken token)
    {
        lock (_store.Sync) Data.Revoke(token);
        EndSessions(c => c.Token == token);
        _store.Save();
        RaiseChanged();
    }

    /// <summary>Read and write, or read only, from the next request on; its connections stay.</summary>
    public void SetTokenAccess(ApiToken token, ApiAccess access)
    {
        lock (_store.Sync) token.Access = access;
        _store.Save();
        RaiseChanged();
    }

    /// <summary>Freezes the token (it still connects, but can't do anything) or thaws it; a chat watch it runs ends.</summary>
    public void SetTokenFrozen(ApiToken token, bool frozen)
    {
        lock (_store.Sync) token.Frozen = frozen;
        if (frozen) EndWatches(token);
        _store.Save();
        RaiseChanged();
    }

    // ───────────── Requests ─────────────

    private async Task HandleAsync(HttpContext context)
    {
        var request = context.Request;
        var response = context.Response;
        response.Headers.CacheControl = "no-store";
        response.Headers.XContentTypeOptions = "nosniff";
        var now = DateTime.Now;
        var address = context.Connection.RemoteIpAddress is { } ip ? (ip.IsIPv4MappedToIPv6 ? ip.MapToIPv4() : ip).ToString() : "unknown";

        // Browsers send Origin: web pages (and DNS rebinding tricks) don't get in.
        if (request.Headers.Origin.Count > 0)
        {
            await Problem(context, 403, "Web pages in a browser can't call ProJict's API.");
            return;
        }
        if (_gate.LockedOut(address, now) is { } wait)
        {
            response.Headers.RetryAfter = ((int)Math.Ceiling(wait.TotalSeconds)).ToString(CultureInfo.InvariantCulture);
            await Problem(context, 429, $"Too many wrong tokens from {address}. Try again in {Math.Ceiling(wait.TotalMinutes)} minutes.");
            return;
        }
        var secret = Bearer(request.Headers.Authorization.ToString());
        if (secret == null)
        {
            response.Headers.WWWAuthenticate = "Bearer realm=\"ProJict\"";
            await Problem(context, 401, "Send your ProJict API token: \"Authorization: Bearer pjt_…\". Tokens are made in ProJict (Settings › API).");
            return;
        }
        ApiToken? token;
        lock (_store.Sync) token = Data.Find(secret);
        if (token == null)
        {
            _gate.Failed(address, now);
            response.Headers.WWWAuthenticate = "Bearer realm=\"ProJict\", error=\"invalid_token\"";
            await Problem(context, 401, "That token isn't valid: it may have been revoked or renewed.");
            return;
        }
        if (token.IsExpired(now))
        {
            response.Headers.WWWAuthenticate = "Bearer realm=\"ProJict\", error=\"invalid_token\"";
            await Problem(context, 401, $"The token “{token.Name}” expired on {token.ExpiresAt:yyyy-MM-dd}. Renew it in ProJict (Settings › API).");
            return;
        }
        _gate.Succeeded(address);
        if (!_gate.Allow(token.Id, now, out var retry))
        {
            response.Headers.RetryAfter = ((int)Math.Ceiling(retry.TotalSeconds)).ToString(CultureInfo.InvariantCulture);
            await Problem(context, 429, $"Slow down: a token may make {_gate.PerMinute} requests a minute.");
            return;
        }
        Touch(token, address, now);

        var baseUrl = $"{request.Scheme}://{request.Host}";
        var path = request.Path.Value ?? "/";
        try
        {
            if (path.Equals(McpPath, StringComparison.OrdinalIgnoreCase) || path.Equals(McpPath + "/", StringComparison.OrdinalIgnoreCase))
                await McpAsync(context, token, baseUrl);
            else if (path.StartsWith(ApiRoutes.Base, StringComparison.OrdinalIgnoreCase) &&
                     (path.Length == ApiRoutes.Base.Length || path[ApiRoutes.Base.Length] == '/'))
                await RestAsync(context, token, baseUrl, path[ApiRoutes.Base.Length..]);
            else
                await Problem(context, 404, $"Nothing here. The REST API is at {ApiRoutes.Base} (described at {ApiRoutes.Base}/openapi.json) and MCP at {McpPath}.");
        }
        catch (BadHttpRequestException ex) when (ex.StatusCode == 413)
        {
            await Problem(context, 413, $"Requests can be up to {MaxBodyBytes / 1024 / 1024} MB.");
        }
        catch (JsonException)
        {
            await Problem(context, 400, "The body isn't valid JSON.");
        }
    }

    private static string? Bearer(string header) =>
        header.StartsWith("Bearer ", StringComparison.OrdinalIgnoreCase) && header[7..].Trim() is { Length: > 0 } value ? value : null;

    /// <summary>Remembers when and where the token was used; saved at most once a minute (or when the address changes).</summary>
    private void Touch(ApiToken token, string address, DateTime now)
    {
        bool save;
        lock (_store.Sync)
        {
            save = token.LastUsedAt is not { } last || now - last >= LastUsedSaveEvery || token.LastUsedFrom != address;
            token.LastUsedAt = now;
            token.LastUsedFrom = address;
        }
        if (!save) return;
        _store.Save();
        RaiseChanged();
    }

    // ───────────── REST ─────────────

    private async Task RestAsync(HttpContext context, ApiToken token, string baseUrl, string path)
    {
        var method = context.Request.Method;
        var session = RestSession(token, baseUrl);
        var host = HostFor(token);

        if (path is "" or "/")
        {
            if (method != "GET") await NotAllowed(context, "GET");
            else await Reply(context, 200, (await WhoAmIAsync(token, baseUrl)).ToJsonString(ReadableJson));
            return;
        }
        // A frozen token only learns who it is (and that it's frozen).
        if (token.Frozen)
        {
            await Problem(context, 403, AgentTools.FrozenMessage);
            return;
        }
        if (path.Equals("/openapi.json", StringComparison.OrdinalIgnoreCase))
        {
            if (method != "GET") await NotAllowed(context, "GET");
            else await Reply(context, 200, ApiRoutes.OpenApi(host.Tools, baseUrl, _version).ToJsonString(ReadableJson));
            return;
        }
        if (path.Equals("/chat/watch", StringComparison.OrdinalIgnoreCase))
        {
            if (method != "GET") await NotAllowed(context, "GET");
            else await WatchAsync(context, token);
            return;
        }
        if (path.StartsWith("/attachments/", StringComparison.OrdinalIgnoreCase))
        {
            if (method != "GET") await NotAllowed(context, "GET");
            else await DownloadAsync(context, session, path["/attachments/".Length..]);
            return;
        }

        if (ApiRoutes.Match(method, path) is not { } match)
        {
            var allowed = ApiRoutes.MethodsFor(path);
            if (allowed.Count > 0) context.Response.Headers.Allow = string.Join(", ", allowed);
            await Problem(context, allowed.Count > 0 ? 405 : 404, ApiRoutes.WrongEndpoint(method, path, allowed));
            return;
        }
        var (route, values) = match;

        var body = await BodyAsync(context.Request, _tools.FilesArgument(route.Tool) != null);
        var query = context.Request.Query.Select(q => KeyValuePair.Create(q.Key, q.Value.ToString()));
        var args = JsonSerializer.SerializeToElement(ApiRoutes.Arguments(route, values, query, body));
        try
        {
            var text = await _tools.CallAsync(session, route.Tool, args);
            await Reply(context, route.Status, text);
        }
        catch (McpToolException ex)
        {
            await Problem(context, ex.Problem switch
            {
                ToolProblem.NotFound => 404,
                ToolProblem.Forbidden => 403,
                ToolProblem.Conflict => 409,
                _ => 400,
            }, ex.Message);
        }
        catch (Exception ex)
        {
            await Problem(context, 500, $"ProJict could not do that: {ex.Message}");
        }
    }

    /// <summary>The JSON body, or a form with files (multipart) turned into the same shape: files become { name, data }.</summary>
    private static async Task<JsonObject?> BodyAsync(HttpRequest request, bool takesFiles)
    {
        if (request.HasFormContentType)
        {
            var form = await request.ReadFormAsync();
            var json = new JsonObject();
            foreach (var (key, value) in form) json[key] = value.ToString();
            if (takesFiles && form.Files.Count > 0)
            {
                var files = new JsonArray();
                foreach (var file in form.Files)
                {
                    using var buffer = new MemoryStream();
                    await file.CopyToAsync(buffer);
                    files.Add(new JsonObject { ["name"] = Path.GetFileName(file.FileName), ["data"] = Convert.ToBase64String(buffer.ToArray()) });
                }
                json["files"] = files;
            }
            return json;
        }
        if (request.ContentLength == 0 || !HttpMethods.IsPost(request.Method) && !HttpMethods.IsPut(request.Method) && !HttpMethods.IsPatch(request.Method))
            return null;
        using var reader = new StreamReader(request.Body);
        var text = await reader.ReadToEndAsync();
        if (string.IsNullOrWhiteSpace(text)) return null;
        return JsonNode.Parse(text) as JsonObject ?? throw new JsonException("Expected an object.");
    }

    private async Task<JsonObject> WhoAmIAsync(ApiToken token, string baseUrl)
    {
        var names = await _dispatcher.InvokeAsync(() =>
            token.Projects == null ? null : _projects().Where(p => token.Projects.Contains(p.Id)).Select(p => p.Name).ToList());
        return new JsonObject
        {
            ["name"] = token.Name,
            ["access"] = token.ReadOnly ? "read only" : "read and write",
            ["chat"] = _tools.ChatAccess(token.ReadOnly),
            ["frozen"] = token.Frozen ? true : null,
            ["note"] = token.Frozen ? AgentTools.FrozenMessage : null,
            ["projects"] = names == null ? "all" : new JsonArray(names.Select(n => (JsonNode)n).ToArray()),
            ["expires"] = token.ExpiresAt.ToString("yyyy-MM-dd HH:mm", CultureInfo.InvariantCulture),
            ["projict"] = _version,
            ["openApi"] = $"{baseUrl}{ApiRoutes.Base}/openapi.json",
            ["mcp"] = baseUrl + McpPath,
            ["endpoints"] = ApiRoutes.Index(),
            ["howTo"] = "Each endpoint runs one of the agent tools, with the same rules; openApi describes them and their arguments, and mcp gives the same " +
                        "tools over MCP (the simplest for AI agents). Chat: GET /chat/channels, then POST /chat/channels/{channel}/messages.",
        };
    }

    /// <summary>
    /// GET /chat/watch: a line of text per chat message for the token, as they come, for as long as the client keeps
    /// the request open (curl -sN under a client that tells its agent about each line).
    /// </summary>
    private async Task WatchAsync(HttpContext context, ApiToken token)
    {
        var response = context.Response;
        response.StatusCode = 200;
        response.ContentType = "text/plain; charset=utf-8";
        response.Headers.CacheControl = "no-store";
        // Ends when the client goes away, or the token is frozen, renewed or revoked.
        using var watch = CancellationTokenSource.CreateLinkedTokenSource(context.RequestAborted);
        var stop = watch.Token;
        lock (_lock) _watches.Add((token, watch));
        try
        {
            await _tools.WatchChatAsync(NewSession(token), async line =>
            {
                await response.WriteAsync(line + "\n", stop);
                await response.Body.FlushAsync(stop);
            }, stop);
        }
        catch (Exception ex) when (ex is OperationCanceledException or IOException or ObjectDisposedException)
        {
            // The client went away, or the token can't watch any more.
        }
        finally
        {
            lock (_lock) _watches.Remove((token, watch));
        }
    }

    private readonly List<(ApiToken Token, CancellationTokenSource Stop)> _watches = [];

    private void EndWatches(ApiToken token)
    {
        List<CancellationTokenSource> ending;
        lock (_lock) ending = _watches.Where(w => w.Token == token).Select(w => w.Stop).ToList();
        foreach (var stop in ending)
        {
            try { stop.Cancel(); }
            catch (ObjectDisposedException) { }
        }
    }

    private async Task DownloadAsync(HttpContext context, McpSession session, string id)
    {
        var found = Guid.TryParse(id, out var guid) ? await _tools.FindAttachmentAsync(session, guid) : null;
        if (found is not { } hit || !File.Exists(hit.Path))
        {
            await Problem(context, 404, "No such attachment in the projects this token reaches.");
            return;
        }
        var (attachment, file) = hit;
        context.Response.ContentType = ContentTypes.TryGetContentType(attachment.FileName, out var type) ? type : "application/octet-stream";
        var disposition = new ContentDispositionHeaderValue("attachment");
        disposition.SetHttpFileName(attachment.FileName);
        context.Response.Headers.ContentDisposition = disposition.ToString();
        await context.Response.SendFileAsync(file);
    }

    private McpSession RestSession(ApiToken token, string baseUrl)
    {
        Client? client;
        var started = false;
        lock (_lock)
        {
            if (!_rest.TryGetValue(token.Id, out client))
            {
                client = new Client(token, NewSession(token));
                _rest[token.Id] = client;
                started = true;
            }
            client.LastSeen = DateTime.Now;
            client.Session.BaseUrl = baseUrl;
            Follow(client.Session, token);
        }
        if (started) _tools.SessionStarted(client.Session);
        return client.Session;
    }

    /// <summary>
    /// The token's access, and its agent's name, may have changed since its session started (the name in another session
    /// with the same token): each request goes by what they are now.
    /// </summary>
    private static void Follow(McpSession session, ApiToken token)
    {
        session.ReadOnly = token.ReadOnly;
        session.Frozen = token.Frozen;
        if (token.AgentName != null) session.Name = token.AgentName;
    }

    private static McpSession NewSession(ApiToken token) => new()
    {
        SignAs = token.Name,
        TokenId = token.Id,
        // The name its agent picked last time.
        Name = token.AgentName,
        Remote = true,
        ReadOnly = token.ReadOnly,
        Frozen = token.Frozen,
        Projects = token.Projects?.ToHashSet(),
    };

    private ApiToolHost HostFor(ApiToken token) => token.ReadOnly ? _readOnly : _readWrite;

    // ───────────── MCP over HTTP ─────────────

    /// <summary>
    /// MCP's Streamable HTTP transport, answering each POSTed message with JSON (ProJict never sends anything first, so
    /// there's no event stream): initialize gets an Mcp-Session-Id that later requests send back, and DELETE ends it.
    /// </summary>
    private async Task McpAsync(HttpContext context, ApiToken token, string baseUrl)
    {
        var request = context.Request;
        var id = request.Headers[SessionHeader].ToString();
        if (HttpMethods.IsDelete(request.Method))
        {
            Client? ended;
            lock (_lock) ended = _mcp.TryGetValue(id, out var c) && c.Token == token && _mcp.Remove(id) ? c : null;
            if (ended == null) await Problem(context, 404, "No such session.");
            else
            {
                _tools.SessionEnded(ended.Session);
                context.Response.StatusCode = 204;
            }
            return;
        }
        if (!HttpMethods.IsPost(request.Method))
        {
            await NotAllowed(context, "POST, DELETE");
            return;
        }

        string body;
        using (var reader = new StreamReader(request.Body)) body = await reader.ReadToEndAsync();
        var isInitialize = false;
        try { isInitialize = JsonNode.Parse(body) is JsonObject m && m["method"] is JsonValue v && v.TryGetValue<string>(out var name) && name == "initialize"; }
        catch (JsonException) { /* answered as a parse error below */ }

        Client? client;
        lock (_lock)
        {
            if (isInitialize)
            {
                id = Convert.ToHexString(RandomNumberGenerator.GetBytes(16)).ToLowerInvariant();
                client = new Client(token, NewSession(token));
                _mcp[id] = client;
            }
            else client = id.Length > 0 && _mcp.TryGetValue(id, out var c) && c.Token == token ? c : null;
            if (client != null)
            {
                client.LastSeen = DateTime.Now;
                client.Session.BaseUrl = baseUrl;
                Follow(client.Session, token);
            }
        }
        if (client == null)
        {
            // An unknown session tells the client to initialize again.
            await Problem(context, id.Length == 0 ? 400 : 404, id.Length == 0 ? $"Initialize first, then send the {SessionHeader} header you got." : "No such session: initialize again.");
            return;
        }
        if (isInitialize) context.Response.Headers[SessionHeader] = id;

        var reply = await McpServer.HandleAsync(body, HostFor(token), client.Session, _version);
        if (reply == null) context.Response.StatusCode = 202;
        else await Reply(context, 200, reply.ToJsonString());
    }

    /// <summary>
    /// The agent tools as an API token sees them: files as uploads, no folders, and only reading for a read-only token
    /// (apart from Chat while Settings › API lets read-only tokens use it).
    /// </summary>
    private sealed class ApiToolHost(AgentTools tools, bool readOnly) : IMcpToolHost
    {
        private static readonly JsonElement NoArguments = JsonSerializer.SerializeToElement(new JsonObject());
        private static readonly McpSession ReadOnlySession = new() { ReadOnly = true };

        private readonly List<McpTool> _all = tools.Tools
            .Select(t => t with { InputSchema = Schema(t, tools.FilesArgument(t.Name)) })
            .ToList();

        public IReadOnlyList<McpTool> Tools => readOnly ? _all.Where(t => tools.Allows(ReadOnlySession, t.Name, NoArguments)).ToList() : _all;

        public string Instructions => tools.InstructionsFor(remote: true) +
            " You're connected over ProJict's API, signing with the token's name: say which project with \"project\" (there's no working " +
            "directory), send files as objects with \"name\" and \"data\" (base64), and download attachments from their url with the same " +
            "Authorization header." + (!readOnly ? ""
                : tools.ChatAccess(readOnly) == "read and write" ? " This token is read only, apart from Chat: there you read, post, start channels and invite like any agent."
                : " This token is read only.");

        public void SessionStarted(McpSession session) => tools.SessionStarted(session);
        public void SessionEnded(McpSession session) => tools.SessionEnded(session);
        public Task<string> CallAsync(McpSession session, string name, JsonElement arguments) => tools.CallAsync(session, name, arguments);

        private static JsonObject Schema(McpTool tool, string? filesArgument)
        {
            var schema = (JsonObject)tool.InputSchema.DeepClone();
            if (schema["properties"] is not JsonObject properties) return schema;
            if (tool.Name == "current_project") properties.Remove("path");
            if (filesArgument != null && properties[filesArgument] is JsonObject files)
            {
                properties[filesArgument] = new JsonObject
                {
                    ["type"] = "array",
                    ["description"] = files["description"]?.GetValue<string>() is { } d ? d.Split(" (absolute")[0] + ", uploaded as name and base64 data." : "Files to upload.",
                    ["items"] = new JsonObject
                    {
                        ["type"] = "object",
                        ["properties"] = new JsonObject
                        {
                            ["name"] = new JsonObject { ["type"] = "string", ["description"] = "File name, e.g. screenshot.png" },
                            ["data"] = new JsonObject { ["type"] = "string", ["contentEncoding"] = "base64", ["description"] = "The file's content in base64" },
                        },
                        ["required"] = new JsonArray("name", "data"),
                    },
                };
            }
            return schema;
        }
    }

    // ───────────── Sessions ─────────────

    /// <summary>Ends callers that went quiet, and those whose token expired.</summary>
    private void Sweep()
    {
        var now = DateTime.Now;
        EndSessions(c => c.Token.IsExpired(now) || now - c.LastSeen > (_rest.ContainsValue(c) ? RestIdle : McpIdle));
    }

    private void EndSessions(Func<Client, bool> which)
    {
        List<Client> ended;
        lock (_lock)
        {
            ended = [.. _rest.Values.Where(which), .. _mcp.Values.Where(which)];
            foreach (var key in _rest.Where(kv => ended.Contains(kv.Value)).Select(kv => kv.Key).ToList()) _rest.Remove(key);
            foreach (var key in _mcp.Where(kv => ended.Contains(kv.Value)).Select(kv => kv.Key).ToList()) _mcp.Remove(key);
        }
        foreach (var client in ended) _tools.SessionEnded(client.Session);
        foreach (var token in ended.Select(c => c.Token).Distinct()) EndWatches(token);
    }

    // ───────────── Replies ─────────────

    private static async Task Reply(HttpContext context, int status, string json)
    {
        context.Response.StatusCode = status;
        context.Response.ContentType = "application/json; charset=utf-8";
        await context.Response.WriteAsync(json);
    }

    private static Task Problem(HttpContext context, int status, string message) =>
        Reply(context, status, new JsonObject { ["error"] = message }.ToJsonString(ReadableJson));

    private static Task NotAllowed(HttpContext context, string allow)
    {
        context.Response.Headers.Allow = allow;
        return Problem(context, 405, $"Use {allow} here.");
    }

    private void RaiseChanged()
    {
        if (_dispatcher.HasShutdownStarted) return;
        if (_dispatcher.CheckAccess()) Changed?.Invoke();
        else _dispatcher.BeginInvoke(() => Changed?.Invoke());
    }
}
