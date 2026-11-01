using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;

namespace ProJict.Core.Agents;

/// <summary>A tool offered to AI agents; read-only tools are marked so clients can allow them without asking.</summary>
public sealed record McpTool(string Name, string Title, string Description, JsonObject InputSchema, bool ReadOnly);

/// <summary>One connected agent.</summary>
public sealed class McpSession
{
    private static int _next;

    public int Id { get; } = Interlocked.Increment(ref _next);
    public string? ClientName { get; set; }
    public string? ClientTitle { get; set; }
    /// <summary>The agent's working directory, passed on by the ProJict bridge.</summary>
    public string? WorkingDirectory { get; set; }
    /// <summary>Items this agent has checked out (claimed) in this session.</summary>
    public HashSet<Guid> Claimed { get; } = [];
    /// <summary>What the agent says it's working on (set_label), shown on its card; not part of its name.</summary>
    public string? Label { get; set; }
    /// <summary>The short name the agent picked for itself (set_name), e.g. "Ada"; it signs everything with it.</summary>
    public string? Name { get; set; }
    /// <summary>Sessions of the same client without a name of their own are numbered: 2 for "Claude Code 2".</summary>
    public int Number { get; set; } = 1;
    /// <summary>Who renamed it (the user, from the Agents view), so its next tool result tells it its new name.</summary>
    public string? RenamedBy { get; set; }
    /// <summary>Names it had earlier in this session, before it (or the user) picked another.</summary>
    public HashSet<string> FormerNames { get; } = new(StringComparer.OrdinalIgnoreCase);

    /// <summary>The name it signs with, whatever the client calls itself, until it picks one: an API token's name.</summary>
    public string? SignAs { get; init; }
    /// <summary>The API token it connected with, which remembers the name it picks.</summary>
    public Guid? TokenId { get; init; }
    /// <summary>Connected over the API rather than the local pipe: files come as uploads, never as paths on this computer.</summary>
    public bool Remote { get; init; }
    /// <summary>A read-only API token: reading only (it can change while connected).</summary>
    public bool ReadOnly { get; set; }
    /// <summary>A frozen API token: nothing works until the user unfreezes it.</summary>
    public bool Frozen { get; set; }
    /// <summary>The projects an API token reaches; null for all.</summary>
    public IReadOnlySet<Guid>? Projects { get; init; }
    /// <summary>Where the API answers ("http://127.0.0.1:47614"), for links to attachments; set per request.</summary>
    public string? BaseUrl { get; set; }

    public DateTime ConnectedAt { get; } = DateTime.Now;
    /// <summary>When it last called a tool.</summary>
    public DateTime? LastActive { get; set; }
    /// <summary>What the agent says it's doing (set_status), and since when.</summary>
    public string? Status { get; set; }
    public DateTime? StatusAt { get; set; }
    /// <summary>What it did lately, oldest first (at most <see cref="KeptActions"/>), for the Agents view.</summary>
    public List<AgentAction> Recent { get; } = [];
    public const int KeptActions = 40;

    /// <summary>
    /// Keeps what it did. The same thing again right after (reading the same channel while it waits for news, say) counts
    /// on the last line instead of adding one: "Read #test", 6 times. Returns the line as kept.
    /// </summary>
    public AgentAction Remember(AgentAction action)
    {
        if (Recent.Count > 0 && Recent[^1] is var last && last.Text == action.Text && last.ProjectId == action.ProjectId &&
            last.ItemId == action.ItemId && last.Tool == action.Tool)
            return Recent[^1] = action with { Times = last.Times + 1, Since = last.Since ?? last.At };
        Recent.Add(action);
        if (Recent.Count > KeptActions) Recent.RemoveRange(0, Recent.Count - KeptActions);
        return action;
    }
    /// <summary>Waiting for chat messages (wait_for_messages) right now.</summary>
    public volatile bool Listening;
    /// <summary>What its tool results already told it is waiting (chat messages, comments, …), so each is mentioned once.</summary>
    public HashSet<string> Noticed { get; } = [];

    /// <summary>
    /// How the agent signs its work: the name it picked ("Ada"), else an API token's name, or else its client's name
    /// ("Claude Code" for a client named "claude-code"), numbered when several run at once ("Claude Code 2").
    /// </summary>
    public string AgentName => Name ?? (!string.IsNullOrWhiteSpace(SignAs) ? SignAs.Trim()
        : Number > 1 ? $"{Client} {Number}" : Client);

    /// <summary>What it runs in: "Claude Code", "Codex", …</summary>
    public string Client => !string.IsNullOrWhiteSpace(ClientTitle) ? ClientTitle.Trim()
        : !string.IsNullOrWhiteSpace(ClientName) ? Prettify(ClientName) : "AI agent";

    /// <summary>Its name with its client, for tooltips: "Ada · Claude Code".</summary>
    public string FullName => Name != null ? $"{Name} · {Client}" : AgentName;

    private static string Prettify(string name) => name.Contains(' ') ? name.Trim() :
        string.Join(" ", name.Split(['-', '_'], StringSplitOptions.RemoveEmptyEntries).Select(w => char.ToUpperInvariant(w[0]) + w[1..]));
}

/// <summary>
/// One thing an agent did, in a few words ("Checked out “Fix the launcher”"), and the project and item it was about; done
/// <paramref name="Times"/> times in a row, the first <paramref name="Since"/>, the last <paramref name="At"/>.
/// </summary>
public sealed record AgentAction(DateTime At, string Text, Guid? ProjectId, Guid? ItemId, string? Tool = null, int Times = 1, DateTime? Since = null);

public interface IMcpToolHost
{
    IReadOnlyList<McpTool> Tools { get; }
    /// <summary>Sent to the agent when it connects: what ProJict is and how to use the tools.</summary>
    string Instructions { get; }
    void SessionStarted(McpSession session);
    /// <summary>The agent disconnected (also called when it never got as far as initializing).</summary>
    void SessionEnded(McpSession session);
    /// <summary>Runs a tool; throws <see cref="McpToolException"/> for problems the agent should read and fix.</summary>
    Task<string> CallAsync(McpSession session, string name, JsonElement arguments);
}

/// <summary>What kind of problem a tool reports; the API answers with the matching HTTP status.</summary>
public enum ToolProblem { Invalid, NotFound, Forbidden, Conflict }

/// <summary>A tool failed in a way the agent can act on (unknown item, missing argument, …).</summary>
public sealed class McpToolException(string message, ToolProblem problem = ToolProblem.Invalid) : Exception(message)
{
    public ToolProblem Problem { get; } = problem;
}

/// <summary>
/// The part of the Model Context Protocol ProJict needs: JSON-RPC 2.0 messages, one per line (the stdio transport),
/// with initialize, ping, tools/list and tools/call. Requests are answered in order.
/// </summary>
public static class McpServer
{
    /// <summary>Protocol versions this server can speak; it answers with the client's if listed, else the first stable one below.</summary>
    public static readonly string[] SupportedVersions = ["2025-11-25", "2025-06-18", "2025-03-26", "2024-11-05"];
    private const string FallbackVersion = "2025-06-18";

    /// <summary>First line the bridge sends (not JSON-RPC): <c>{"projictBridge":1,"cwd":"…"}</c>.</summary>
    public const string BridgeHelloKey = "projictBridge";

    /// <summary>
    /// First line a chat watcher (<c>ProJict.exe --watch-chat --as Ada</c>) sends instead: <c>{"projictWatch":1,"as":"Ada"}</c>.
    /// No MCP follows; ProJict writes a line per chat message for that agent.
    /// </summary>
    public const string WatchHelloKey = "projictWatch";

    /// <summary>The agent a chat watcher's first line asks for, or null when the line is something else.</summary>
    public static string? WatchRequest(string line)
    {
        if (!line.Contains(WatchHelloKey, StringComparison.Ordinal)) return null;
        try
        {
            return JsonNode.Parse(line.TrimStart('\uFEFF')) is JsonObject hello && hello.ContainsKey(WatchHelloKey) && Text(hello["as"]) is { } name &&
                   name.Trim().Length > 0 ? name.Trim() : null;
        }
        catch (JsonException) { return null; }
    }

    public static async Task RunAsync(Stream input, Stream output, IMcpToolHost host, McpSession session, string serverVersion,
        CancellationToken cancellation = default)
    {
        using var reader = new StreamReader(input, new UTF8Encoding(false), false, 4096, leaveOpen: true);
        await RunAsync(reader, null, output, host, session, serverVersion, cancellation);
    }

    /// <summary>The same, starting with a line already read (the pipe looks at the first line to tell agents from watchers).</summary>
    public static async Task RunAsync(TextReader reader, string? firstLine, Stream output, IMcpToolHost host, McpSession session, string serverVersion,
        CancellationToken cancellation = default)
    {
        try
        {
            var pending = firstLine;
            while ((pending ?? await reader.ReadLineAsync(cancellation)) is { } read)
            {
                pending = null;
                // Some clients start their stream with a byte order mark.
                var line = read.TrimStart('\uFEFF');
                if (string.IsNullOrWhiteSpace(line)) continue;
                var response = await HandleAsync(line, host, session, serverVersion);
                if (response == null) continue;
                await output.WriteAsync(Encoding.UTF8.GetBytes(response.ToJsonString() + "\n"), cancellation);
                await output.FlushAsync(cancellation);
            }
        }
        finally
        {
            host.SessionEnded(session);
        }
    }

    /// <summary>Handles one message; null when nothing is sent back (notifications, the bridge's hello).</summary>
    public static async Task<JsonObject?> HandleAsync(string line, IMcpToolHost host, McpSession session, string serverVersion)
    {
        JsonNode? node;
        try { node = JsonNode.Parse(line); }
        catch (JsonException) { return Error(null, -32700, "Parse error"); }
        if (node is not JsonObject message) return Error(null, -32600, "Invalid request: expected a JSON object");

        if (message.ContainsKey(BridgeHelloKey))
        {
            session.WorkingDirectory = Text(message["cwd"]);
            return null;
        }

        var isNotification = !message.ContainsKey("id");
        var id = message["id"]?.DeepClone();
        var method = Text(message["method"]);
        // ProJict never sends requests, so a message without a method is a stray response; ignore it.
        if (method == null) return null;

        try
        {
            var parameters = message["params"] as JsonObject;
            JsonNode? result = method switch
            {
                "initialize" => Initialize(parameters, host, session, serverVersion),
                "ping" => new JsonObject(),
                "tools/list" => ListTools(host),
                "tools/call" => await CallToolAsync(parameters, host, session),
                _ when method.StartsWith("notifications/", StringComparison.Ordinal) => null,
                _ => throw new ProtocolError(-32601, $"Method not found: {method}"),
            };
            return isNotification ? null : new JsonObject { ["jsonrpc"] = "2.0", ["id"] = id, ["result"] = result ?? new JsonObject() };
        }
        catch (ProtocolError error)
        {
            return isNotification ? null : Error(id, error.Code, error.Message);
        }
    }

    private static JsonObject Initialize(JsonObject? parameters, IMcpToolHost host, McpSession session, string serverVersion)
    {
        var requested = Text(parameters?["protocolVersion"]);
        if (parameters?["clientInfo"] is JsonObject client)
        {
            session.ClientName = Text(client["name"]);
            session.ClientTitle = Text(client["title"]);
        }
        host.SessionStarted(session);
        return new JsonObject
        {
            ["protocolVersion"] = requested != null && SupportedVersions.Contains(requested) ? requested : FallbackVersion,
            ["capabilities"] = new JsonObject { ["tools"] = new JsonObject { ["listChanged"] = false } },
            ["serverInfo"] = new JsonObject { ["name"] = "projict", ["title"] = "ProJict", ["version"] = serverVersion },
            ["instructions"] = host.Instructions,
        };
    }

    private static JsonObject ListTools(IMcpToolHost host) => new()
    {
        ["tools"] = new JsonArray(host.Tools.Select(t => (JsonNode)new JsonObject
        {
            ["name"] = t.Name,
            ["title"] = t.Title,
            ["description"] = t.Description,
            ["inputSchema"] = t.InputSchema.DeepClone(),
            ["annotations"] = new JsonObject
            {
                ["title"] = t.Title,
                ["readOnlyHint"] = t.ReadOnly,
                ["destructiveHint"] = false,
                ["openWorldHint"] = false,
            },
        }).ToArray()),
    };

    private static async Task<JsonObject> CallToolAsync(JsonObject? parameters, IMcpToolHost host, McpSession session)
    {
        var name = Text(parameters?["name"]) ?? throw new ProtocolError(-32602, "Missing tool name");
        if (host.Tools.All(t => t.Name != name)) throw new ProtocolError(-32602, $"Unknown tool: {name}");
        var arguments = parameters?["arguments"] is JsonObject args
            ? JsonSerializer.SerializeToElement(args)
            : JsonSerializer.SerializeToElement(new JsonObject());
        string text;
        var isError = false;
        try
        {
            text = await host.CallAsync(session, name, arguments);
        }
        catch (McpToolException ex)
        {
            (text, isError) = (ex.Message, true);
        }
        catch (Exception ex)
        {
            (text, isError) = ($"ProJict could not run {name}: {ex.Message}", true);
        }
        return new JsonObject
        {
            ["content"] = new JsonArray(new JsonObject { ["type"] = "text", ["text"] = text }),
            ["isError"] = isError,
        };
    }

    private static JsonObject Error(JsonNode? id, int code, string message) => new()
    {
        ["jsonrpc"] = "2.0",
        ["id"] = id,
        ["error"] = new JsonObject { ["code"] = code, ["message"] = message },
    };

    private static string? Text(JsonNode? node) => node is JsonValue value && value.TryGetValue<string>(out var s) ? s : null;

    private sealed class ProtocolError(int code, string message) : Exception(message)
    {
        public int Code { get; } = code;
    }
}
