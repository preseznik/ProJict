using System.Text.Json.Nodes;
using ProJict.Core.Agents;

namespace ProJict.Core.Api;

/// <summary>
/// A REST endpoint and the agent tool that answers it. Path values ({project}, {id}, …), query parameters and the JSON
/// body become the tool's arguments, so the API follows exactly the same rules as the MCP tools.
/// </summary>
public sealed record ApiRoute(string Method, string Template, string Tool, string OperationId, string Summary, int Status = 200)
{
    /// <summary>Body properties named differently from the tool's arguments (REST name → tool argument).</summary>
    public IReadOnlyDictionary<string, string> Rename { get; init; } = new Dictionary<string, string>();
    /// <summary>Arguments the route sets itself.</summary>
    public IReadOnlyDictionary<string, bool> Fixed { get; init; } = new Dictionary<string, bool>();

    public string[] Segments { get; } = Template.Trim('/').Split('/');

    public IEnumerable<string> PathNames => Segments.Where(IsParameter).Select(s => s[1..^1]);

    public static bool IsParameter(string segment) => segment.StartsWith('{') && segment.EndsWith('}');
}

public static class ApiRoutes
{
    public const string Base = "/api/v1";

    private static readonly Dictionary<string, string> Files = new() { ["files"] = "paths" };

    public static readonly IReadOnlyList<ApiRoute> All =
    [
        new("GET", "/projects", "list_projects", "listProjects", "Your projects with their item counts"),
        new("GET", "/projects/{project}", "current_project", "getProject", "A project with its details, categories and your standing instructions"),
        new("PATCH", "/projects/{project}", "update_project", "updateProject", "Changes a project's details: repo folder, GitHub link, description, instructions, shipping"),
        new("GET", "/projects/{project}/items", "list_items", "listItems", "A project's items, filtered"),
        new("POST", "/projects/{project}/items", "add_item", "addItem", "Adds an item", 201),
        new("GET", "/projects/{project}/comments", "list_comments", "listProjectComments", "The newest comments across a project"),
        new("GET", "/projects/{project}/categories/{category}/comments", "list_comments", "listCategoryComments", "A category's discussion"),
        new("POST", "/projects/{project}/categories/{category}/comments", "add_comment", "addCategoryComment", "Comments on a category", 201),
        new("GET", "/projects/{project}/notes", "read_docs", "listNotes", "A project's notes"),
        new("GET", "/projects/{project}/notes/{title}", "read_docs", "getNote", "One note's text and files"),
        new("PUT", "/projects/{project}/notes/{title}", "write_doc", "writeNote", "Creates or updates a note"),
        new("GET", "/projects/{project}/releases/draft", "draft_release", "draftRelease", "Patch notes for what was finished since the last release")
            { Fixed = new Dictionary<string, bool> { ["save"] = false } },
        new("POST", "/projects/{project}/releases", "draft_release", "saveRelease", "Records a release and links its items", 201)
            { Fixed = new Dictionary<string, bool> { ["save"] = true } },
        new("GET", "/projects/{project}/ship", "ship_request", "getShipRequest", "The project's ship request"),
        new("POST", "/projects/{project}/ship/plan", "plan_ship", "planShip", "Posts your plan for the ship request"),
        new("POST", "/projects/{project}/ship/steps", "report_ship", "reportShipStep", "Reports one approved step"),
        new("POST", "/projects/{project}/ship/finish", "finish_ship", "finishShip", "Ends an approved ship request"),
        new("GET", "/items/{id}", "get_item", "getItem", "An item with its sub-items, history and discussion"),
        new("PATCH", "/items/{id}", "update_item", "updateItem", "Changes an item's text, tag, priority or state"),
        new("POST", "/items/{id}/claim", "claim_item", "claimItem", "Checks an item out before you work on it"),
        new("POST", "/items/{id}/complete", "complete_item", "completeItem", "Marks an item done, for the user's review"),
        new("POST", "/items/{id}/attachments", "attach_file", "attachFiles", "Attaches files to an item") { Rename = Files },
        new("GET", "/items/{id}/comments", "list_comments", "listItemComments", "An item's discussion") { Rename = new Dictionary<string, string> { ["id"] = "item" } },
        new("POST", "/items/{id}/comments", "add_comment", "addItemComment", "Comments on an item", 201) { Rename = new Dictionary<string, string> { ["id"] = "item" } },
        new("GET", "/groups/{group}/comments", "list_comments", "listGroupComments", "A group's discussion"),
        new("POST", "/groups/{group}/comments", "add_comment", "addGroupComment", "Comments on a group", 201),
        new("GET", "/chat/channels", "list_channels", "listChannels", "The chat channels you can see, with how many messages wait for you in each"),
        new("POST", "/chat/channels", "create_channel", "createChannel", "Starts a chat channel, linked to a project, category or item if you like; you moderate it", 201),
        new("GET", "/chat/channels/{channel}", "read_channel", "readChannel", "A channel's latest messages and its members; what you read counts as read"),
        new("POST", "/chat/channels/{channel}/messages", "post_message", "postMessage", "Posts a message; in an Open channel this joins it", 201),
        new("PATCH", "/chat/channels/{channel}/messages/{id}", "edit_message", "editMessage", "Changes your message's text, or deletes it with \"delete\": true"),
        new("POST", "/chat/channels/{channel}/join", "join_channel", "joinChannel", "Joins an Open channel")
            { Fixed = new Dictionary<string, bool> { ["leave"] = false } },
        new("POST", "/chat/channels/{channel}/leave", "join_channel", "leaveChannel", "Leaves a channel")
            { Fixed = new Dictionary<string, bool> { ["leave"] = true } },
        new("POST", "/chat/channels/{channel}/invites", "invite_to_channel", "inviteToChannel", "Invites the user or an agent, by name"),
        new("POST", "/chat/channels/{channel}/moderation", "moderate_channel", "moderateChannel",
            "For moderators: remove, mute or unmute a member, make or unmake moderators, change the topic, open the channel or make it invite only"),
        new("GET", "/chat/search", "search_chat", "searchChat",
            "Finds messages in your channels' history by their words (?query=crash pause&channel=%23general&by=Ada), newest first"),
        new("GET", "/chat/presence", "check_presence", "checkPresence",
            "Who's online, busy, away or offline: everyone here now, or the names you give (?names=Ada,iztok)"),
        new("GET", "/chat/wait", "wait_for_messages", "waitForMessages", "Waits up to 60 s for chat messages for you, and 👍/👎 answers to yours (long polling)"),
        new("GET", "/wiki", "list_wiki", "listWiki", "The wiki's pages, with their folders, projects and versions"),
        new("GET", "/wiki/pages/{page}", "read_wiki", "readWikiPage", "A wiki page's Markdown and version (or one section); a path's / is written %2F"),
        new("PUT", "/wiki/pages/{page}", "write_wiki", "writeWikiPage", "Creates or changes a wiki page, or one section of it; give the version you read"),
        new("POST", "/wiki/pages/{page}/review", "review_wiki", "reviewWikiPage",
            "For a studio's lead, when the user lets the lead review wiki pages: accepts what the studio's other agents changed, or sends it back with a note"),
    ];

    /// <summary>The route for a request path (relative to <see cref="Base"/>) and its path values; null when none matches.</summary>
    public static (ApiRoute Route, Dictionary<string, string> Values)? Match(string method, string path)
    {
        foreach (var route in All)
        {
            if (!route.Method.Equals(method, StringComparison.OrdinalIgnoreCase)) continue;
            if (Values(route, path) is { } values) return (route, values);
        }
        return null;
    }

    /// <summary>
    /// Every endpoint in a line ("POST /api/v1/chat/channels/{channel}/messages: Posts a message…"), for the API's front
    /// page, so a client finds Chat and the rest without guessing.
    /// </summary>
    public static JsonArray Index() => new(All
        .Select(r => $"{r.Method} {Base}{r.Template}: {r.Summary}")
        .Concat([
            $"GET {Base}/chat/watch: A line of text per chat message for you, as they come (keep the request open)",
            $"GET {Base}/attachments/{{id}}: Downloads a file",
            $"GET {Base}/openapi.json: Every endpoint with its arguments (OpenAPI 3)",
        ])
        .Select(line => (JsonNode)line).ToArray());

    /// <summary>What a request that matched no endpoint is told: the methods the path has, and where the others are.</summary>
    public static string WrongEndpoint(string method, string path, IReadOnlyList<string> allowed)
    {
        var where = $"GET {Base} lists every endpoint ({Base}/openapi.json describes them)";
        var chat = path.Contains("chat", StringComparison.OrdinalIgnoreCase) || path.Contains("channel", StringComparison.OrdinalIgnoreCase) || method == "POST"
            ? $"; Chat is under {Base}/chat/channels (post with POST {Base}/chat/channels/{{channel}}/messages)" : "";
        return allowed.Count > 0
            ? $"{method} isn't used at {Base}{path}: use {string.Join(", ", allowed)} here. {where}{chat}."
            : $"No such endpoint: {method} {Base}{path}. {where}{chat}.";
    }

    /// <summary>The methods a path answers to, for "405 Method Not Allowed".</summary>
    public static IReadOnlyList<string> MethodsFor(string path) =>
        All.Where(r => Values(r, path) != null).Select(r => r.Method).Distinct().ToList();

    private static Dictionary<string, string>? Values(ApiRoute route, string path)
    {
        var parts = path.Trim('/').Split('/');
        if (parts.Length != route.Segments.Length) return null;
        var values = new Dictionary<string, string>();
        for (var i = 0; i < parts.Length; i++)
        {
            var segment = route.Segments[i];
            if (ApiRoute.IsParameter(segment))
            {
                if (parts[i].Length == 0) return null;
                // The server decodes everything but "/".
                values[segment[1..^1]] = parts[i].Replace("%2F", "/", StringComparison.OrdinalIgnoreCase);
            }
            else if (!segment.Equals(parts[i], StringComparison.OrdinalIgnoreCase)) return null;
        }
        return values;
    }

    /// <summary>The tool's arguments: the query, then the body's properties, then the path values and what the route fixes.</summary>
    public static JsonObject Arguments(ApiRoute route, IReadOnlyDictionary<string, string> values,
        IEnumerable<KeyValuePair<string, string>> query, JsonObject? body)
    {
        var args = new JsonObject();
        foreach (var (name, value) in query) args[ToolName(route, name)] = value;
        if (body != null)
            foreach (var (name, value) in body) args[ToolName(route, name)] = value?.DeepClone();
        foreach (var (name, value) in values) args[ToolName(route, name)] = value;
        foreach (var (name, value) in route.Fixed) args[name] = value;
        return args;
    }

    private static string ToolName(ApiRoute route, string name) => route.Rename.TryGetValue(name, out var renamed) ? renamed : name;

    // ───────────── OpenAPI ─────────────

    /// <summary>An OpenAPI 3.1 description of the routes, built from the tools' own argument schemas.</summary>
    public static JsonObject OpenApi(IReadOnlyList<McpTool> tools, string serverUrl, string version)
    {
        var paths = new JsonObject();
        foreach (var route in All)
        {
            var tool = tools.FirstOrDefault(t => t.Name == route.Tool);
            if (tool == null) continue; // e.g. a read-only token's view leaves writing tools out
            if (paths[route.Template] is not JsonObject item) paths[route.Template] = item = new JsonObject();
            item[route.Method.ToLowerInvariant()] = Operation(route, tool);
        }
        paths["/"] = new JsonObject
        {
            ["get"] = new JsonObject
            {
                ["operationId"] = "whoAmI",
                ["summary"] = "The token you're using: its name, access, projects and expiry",
                ["responses"] = Responses(200),
            },
        };
        if (tools.Any(t => t.Name == "wait_for_messages"))
            paths["/chat/watch"] = new JsonObject
            {
                ["get"] = new JsonObject
                {
                    ["operationId"] = "watchChat",
                    ["summary"] = "Streams a line of text per chat message for you, as they come, for as long as you keep the request open (e.g. curl -sN)",
                    ["responses"] = new JsonObject
                    {
                        ["200"] = new JsonObject
                        {
                            ["description"] = "One line per message, starting with a line that says it's watching",
                            ["content"] = new JsonObject { ["text/plain"] = new JsonObject { ["schema"] = new JsonObject { ["type"] = "string" } } },
                        },
                        ["4XX"] = ErrorResponse(),
                    },
                },
            };
        paths["/attachments/{attachmentId}"] = new JsonObject
        {
            ["get"] = new JsonObject
            {
                ["operationId"] = "downloadAttachment",
                ["summary"] = "Downloads an attached file (the url given with each attachment)",
                ["parameters"] = new JsonArray(PathParameter("attachmentId")),
                ["responses"] = new JsonObject
                {
                    ["200"] = new JsonObject
                    {
                        ["description"] = "The file",
                        ["content"] = new JsonObject { ["application/octet-stream"] = new JsonObject { ["schema"] = new JsonObject { ["type"] = "string", ["format"] = "binary" } } },
                    },
                    ["4XX"] = ErrorResponse(),
                },
            },
        };
        return new JsonObject
        {
            ["openapi"] = "3.1.0",
            ["info"] = new JsonObject
            {
                ["title"] = "ProJict API",
                ["version"] = version,
                ["description"] = "Work with your ProJict projects: the same tools AI agents get over MCP. Send your token as " +
                                  "\"Authorization: Bearer pjt_…\". Everything you do is signed with the token's name, finished items wait for " +
                                  "the user's review, and nothing can be deleted except your own chat messages. Results list what waits for you " +
                                  "(chat invitations and @mentions, the user's comments, work sent back) under waitingForYou. " +
                                  "Ids may be shortened to their first 8 characters.",
            },
            ["servers"] = new JsonArray(new JsonObject { ["url"] = serverUrl.TrimEnd('/') + Base }),
            ["security"] = new JsonArray(new JsonObject { ["bearer"] = new JsonArray() }),
            ["components"] = new JsonObject
            {
                ["securitySchemes"] = new JsonObject
                {
                    ["bearer"] = new JsonObject { ["type"] = "http", ["scheme"] = "bearer", ["description"] = "A ProJict API token (pjt_…)" },
                },
                ["schemas"] = new JsonObject
                {
                    ["Error"] = new JsonObject
                    {
                        ["type"] = "object",
                        ["properties"] = new JsonObject { ["error"] = new JsonObject { ["type"] = "string" } },
                        ["required"] = new JsonArray("error"),
                    },
                },
            },
            ["paths"] = paths,
        };
    }

    private static JsonObject Operation(ApiRoute route, McpTool tool)
    {
        var properties = tool.InputSchema["properties"] as JsonObject ?? [];
        var required = (tool.InputSchema["required"] as JsonArray)?.Select(n => n!.GetValue<string>()).ToHashSet() ?? [];
        var toolToRest = route.Rename.ToDictionary(kv => kv.Value, kv => kv.Key);
        string RestName(string toolName) => toolToRest.TryGetValue(toolName, out var rest) ? rest : toolName;
        var inPath = route.PathNames.Select(n => route.Rename.TryGetValue(n, out var t) ? t : n).ToHashSet();

        var parameters = new JsonArray();
        foreach (var name in route.PathNames) parameters.Add(PathParameter(name));
        var operation = new JsonObject
        {
            ["operationId"] = route.OperationId,
            ["summary"] = route.Summary,
            ["description"] = tool.Description,
        };

        var rest = properties.Where(p => !inPath.Contains(p.Key) && !route.Fixed.ContainsKey(p.Key)).ToList();
        if (route.Method == "GET")
        {
            foreach (var (name, schema) in rest)
            {
                var parameter = new JsonObject { ["name"] = RestName(name), ["in"] = "query", ["schema"] = Bare(schema) };
                if (schema?["description"] is { } d) parameter["description"] = d.DeepClone();
                if (required.Contains(name)) parameter["required"] = true;
                parameters.Add(parameter);
            }
        }
        else if (rest.Count > 0)
        {
            var body = new JsonObject();
            foreach (var (name, schema) in rest) body[RestName(name)] = schema?.DeepClone();
            var bodySchema = new JsonObject { ["type"] = "object", ["properties"] = body };
            var bodyRequired = rest.Where(p => required.Contains(p.Key)).Select(p => (JsonNode)RestName(p.Key)).ToArray();
            if (bodyRequired.Length > 0) bodySchema["required"] = new JsonArray(bodyRequired);
            operation["requestBody"] = new JsonObject
            {
                ["required"] = bodyRequired.Length > 0,
                ["content"] = new JsonObject { ["application/json"] = new JsonObject { ["schema"] = bodySchema } },
            };
        }
        if (parameters.Count > 0) operation["parameters"] = parameters;
        operation["responses"] = Responses(route.Status);
        return operation;
    }

    private static JsonObject PathParameter(string name) => new()
    {
        ["name"] = name,
        ["in"] = "path",
        ["required"] = true,
        ["schema"] = new JsonObject { ["type"] = "string" },
    };

    private static JsonObject Bare(JsonNode? schema)
    {
        var copy = schema?.DeepClone() as JsonObject ?? new JsonObject { ["type"] = "string" };
        copy.Remove("description");
        return copy;
    }

    private static JsonObject Responses(int status) => new()
    {
        [status.ToString(System.Globalization.CultureInfo.InvariantCulture)] = new JsonObject
        {
            ["description"] = "OK",
            ["content"] = new JsonObject { ["application/json"] = new JsonObject { ["schema"] = new JsonObject { ["type"] = "object" } } },
        },
        ["4XX"] = ErrorResponse(),
    };

    private static JsonObject ErrorResponse() => new()
    {
        ["description"] = "What went wrong, for you or the user to fix",
        ["content"] = new JsonObject { ["application/json"] = new JsonObject { ["schema"] = new JsonObject { ["$ref"] = "#/components/schemas/Error" } } },
    };
}
