using System.Text.Json.Nodes;
using ProJict.Core.Agents;
using ProJict.Core.Api;
using ProJict.Core.Chat;
using ProJict.Core.Features;
using ProJict.Core.Model;

namespace ProJict.App.Services;

/// <summary>
/// ProJict on your phone (#1bbc8a8d): what a "You, on your phone" API token does, at /api/v1/me. It acts as you, not as
/// an agent, so it does what only you can: answer what agents ask (ask_approval), approve or decline ship plans, accept
/// finished work or send it back. It also chats, comments and adds items under your name. Each runs the same code as
/// your click in ProJict, on its UI thread, so the board, Chat and undo behave the same.
/// </summary>
public sealed partial class AgentTools
{
    /// <summary>Runs a /me request (path after /api/v1, e.g. "/me/waiting"); throws McpToolException for what the phone should show.</summary>
    public Task<string> MeAsync(string method, string path, JsonObject? body, IReadOnlyDictionary<string, string> query, ApiToken? phoneToken = null) =>
        _dispatcher.InvokeAsync(() => MeRoute(method, path.Trim('/').Split('/'), body ?? [], query, phoneToken)).Task;

    private string MeRoute(string method, string[] parts, JsonObject body, IReadOnlyDictionary<string, string> query, ApiToken? phoneToken)
    {
        string Part(int i) => Uri.UnescapeDataString(parts[i]);
        return (method, parts.Length, parts.ElementAtOrDefault(1)) switch
        {
            ("GET", 1, _) => Json(MeWho()),
            ("GET", 2, "waiting") => Json(MeWaiting()),
            ("POST", 3, "approvals") => Json(MeDecideQuestion(Part(2), Approve(body), Optional(body, "pin"))),
            ("POST", 3, "ship") => Json(MeDecideShip(Part(2), Approve(body))),
            ("POST", 3, "reviews") => Json(MeReview(Part(2), body)),
            ("GET", 2, "projects") => Json(MeProjects()),
            ("GET", 2, "tests") => Json(PhoneTestsApi.Pending(_state.Projects, phoneToken, query.GetValueOrDefault("project"))),
            ("GET", 5, "projects") when parts[3] == "tests" => Json(PhoneTestsApi.Detail(_state.Projects, phoneToken, Part(2), Part(4))),
            ("POST", 5, "projects") when parts[3] == "tests" => Json(MeAnswerTest(phoneToken, Part(2), Part(4), body)),
            ("POST", 4, "projects") when parts[3] == "items" => Json(MeAddItem(Part(2), body)),
            ("POST", 4, "items") when parts[3] == "comments" => Json(MeComment(Part(2), body)),
            ("GET", 2, "channels") => Json(MeChannels()),
            ("GET", 3, "channels") => Json(MeReadChannel(Part(2), query)),
            ("POST", 4, "channels") when parts[3] == "messages" => Json(MePost(Part(2), body)),
            _ => throw new McpToolException($"Nothing at {method} /api/v1/{string.Join('/', parts)}. A phone's token reaches: GET /me, GET /me/waiting, " +
                                             "POST /me/approvals/{message}, POST /me/ship/{project}, POST /me/reviews/{item}, GET /me/projects, " +
                                             "POST /me/projects/{project}/items, POST /me/items/{item}/comments, GET /me/channels, GET /me/channels/{channel}, " +
                                              "POST /me/channels/{channel}/messages, GET /me/watch, GET /me/tests?project=<id>, " +
                                              "GET or POST /me/projects/{project}/tests/{test}.", ToolProblem.NotFound),
        };
    }

    private JsonObject MeAnswerTest(ApiToken? token, string project, string test, JsonObject body) =>
        PhoneTestsApi.Answer(_state.Projects, token, project, test, body, (board, request, works, note) =>
        {
            if (!_main.GetProjectViewModel(board).AnswerTest(request, works, note, []))
                throw new McpToolException("That test changed. Refresh To test before trying again.", ToolProblem.Conflict);
        });

    private static bool Approve(JsonObject body) => body["approve"] is JsonValue v && v.TryGetValue<bool>(out var yes)
        ? yes : throw new McpToolException("Say \"approve\": true or false.");

    private static string Text(JsonObject body, string name = "text") =>
        body[name] is JsonValue v && v.TryGetValue<string>(out var s) && s.Trim().Length > 0 ? s.Trim() : throw new McpToolException($"Give \"{name}\".");

    private static string? Optional(JsonObject body, string name) =>
        body[name] is JsonValue v && v.TryGetValue<string>(out var s) && s.Trim().Length > 0 ? s.Trim() : null;

    private string YourName => _state.UserName;

    // ───────────── Who, and what waits ─────────────

    private JsonObject MeWho()
    {
        var waiting = MeWaiting();
        return new JsonObject
        {
            ["name"] = YourName,
            ["projict"] = "ProJict " + (typeof(AgentTools).Assembly.GetName().Version?.ToString(3) ?? ""),
            ["waiting"] = new JsonObject
            {
                ["approvals"] = waiting["approvals"]!.AsArray().Count,
                ["shipPlans"] = waiting["shipPlans"]!.AsArray().Count,
                ["reviews"] = waiting["reviews"]!.AsArray().Count,
                ["unread"] = OwnChannelsForMe().Sum(c => ChatRoom.Unread(c, YourName).Count()),
            },
        };
    }

    /// <summary>What only you can decide, newest first: agents' questions, ship plans, finished work.</summary>
    private JsonObject MeWaiting()
    {
        var questions = OwnChannelsForMe()
            .SelectMany(c => c.Messages.Where(m => m is { Approval: not null, Decision: null, Deleted: false }).Select(m => (Channel: c, Message: m)))
            .OrderByDescending(x => x.Message.At)
            .Select(x => (JsonNode)new JsonObject
            {
                ["message"] = x.Message.Id.ToString(),
                ["channel"] = ChannelRef(x.Channel),
                ["by"] = x.Message.By,
                ["at"] = x.Message.At,
                ["asks"] = x.Message.Approval,
                ["text"] = x.Message.Text,
                ["pin"] = x.Message.WithPin ? true : null,
            });
        var ships = LocalProjects.Where(p => p.OpenShip is { State: ShipState.Planned })
            .Select(p => (JsonNode)new JsonObject
            {
                ["project"] = p.Name,
                ["projectId"] = p.Id.ToString(),
                ["steps"] = ShipFlow.Describe(p.OpenShip!.Steps),
                ["version"] = p.OpenShip.Version,
                ["plannedBy"] = p.OpenShip.PlannedBy,
                ["plan"] = p.OpenShip.Plan,
            });
        var reviews = LocalProjects.SelectMany(p => p.Walk().Where(l => l.Item.NeedsReview).Select(l => (Project: p, l.Category, l.Item)))
            .OrderByDescending(x => x.Item.DoneAt)
            .Select(x => (JsonNode)new JsonObject
            {
                ["item"] = x.Item.Id.ToString(),
                ["project"] = x.Project.Name,
                ["category"] = x.Category.Name,
                ["text"] = x.Item.DisplayText(),
                ["doneAt"] = x.Item.DoneAt,
                ["by"] = x.Item.Activity?.LastOrDefault(a => a.Agent)?.By,
            });
        return new JsonObject
        {
            ["approvals"] = new JsonArray(questions.ToArray()),
            ["shipPlans"] = new JsonArray(ships.ToArray()),
            ["reviews"] = new JsonArray(reviews.ToArray()),
        };
    }

    // ───────────── Deciding ─────────────

    /// <summary>
    /// Your answer to what an agent asks. One asked with the PIN (#8793323e) is approved only with this computer's PIN
    /// ("pin" in the body; wrong ones in a row make it wait a while), which the phone asks you for after "Are you sure?".
    /// </summary>
    private JsonObject MeDecideQuestion(string id, bool approve, string? pin)
    {
        var (channel, message) = FindMessage(id);
        if (message.Approval == null) throw new McpToolException("That message doesn't ask for approval.", ToolProblem.Conflict);
        if (message.Decision is { } decided)
            throw new McpToolException($"{decided.By} already {(decided.Approved ? "approved" : "declined")} it.", ToolProblem.Conflict);
        try
        {
            if (message.WithPin)
            {
                if (approve && pin == null) throw new McpToolException("This one can't be taken back: approving it takes your PIN (\"pin\").", ToolProblem.Forbidden);
                if (approve && _state.CheckApprovalPin(pin) is { } wrong) throw new McpToolException(wrong, ToolProblem.Forbidden);
                Chat.Decide(channel, _state.ChatMe, message.Id, approve, pinChecked: approve);
            }
            // Your 👍 or 👎 on it, as in Chat: the first person's answer decides.
            else Chat.React(channel, _state.ChatMe, message.Id, approve);
        }
        catch (ChatException ex) { throw new McpToolException(ex.Message, ToolProblem.Forbidden); }
        return new JsonObject { ["message"] = message.Id.ToString(), ["asks"] = message.Approval, ["approved"] = approve, ["by"] = YourName };
    }

    private JsonObject MeDecideShip(string name, bool approve)
    {
        var project = MyProject(name);
        if (project.OpenShip is not { State: ShipState.Planned } ship)
            throw new McpToolException($"{project.Name} has no ship plan waiting for you.", ToolProblem.Conflict);
        var vm = _main.GetProjectViewModel(project);
        if (approve) vm.ApproveShipCommand.Execute(null);
        else vm.DeclineShipCommand.Execute(null);
        return new JsonObject { ["project"] = project.Name, ["state"] = ShipFlow.StateName(ship.State), ["by"] = ship.DecidedBy };
    }

    /// <summary>Accepts an agent's finished work, or sends it back (with why, as a comment the agent is told about).</summary>
    private JsonObject MeReview(string id, JsonObject body)
    {
        var accept = body["accept"] is JsonValue v && v.TryGetValue<bool>(out var yes) ? yes : throw new McpToolException("Say \"accept\": true or false.");
        var (project, loc) = FindItemIn(id);
        if (project.Remote != null) throw new McpToolException("That item is in another ProJict's project: review it there.", ToolProblem.Forbidden);
        if (!loc.Item.NeedsReview) throw new McpToolException("That item doesn't wait for your review.", ToolProblem.Conflict);
        var vm = _main.GetProjectViewModel(project);
        var reason = Optional(body, "reason");
        if (accept)
            vm.AgentEdit(loc.Item.Id, i =>
            {
                i.NeedsReview = false;
                i.AddActivity(YourName, "Accepted the work.", agent: false);
            });
        else
        {
            vm.AgentEdit(loc.Item.Id, i =>
            {
                i.SetState(ItemState.Todo, YourName);
                i.AddActivity(YourName, $"{ActivityEntry.SentBack}: {reason ?? "not accepted yet."}", agent: false);
            });
            if (reason != null) vm.PostComment(loc.Item.Id, new Comment { By = YourName, Text = reason }, null);
        }
        var item = project.Find(loc.Item.Id)!.Item;
        return new JsonObject { ["item"] = item.Id.ToString(), ["state"] = item.State.ToString(), ["needsReview"] = item.NeedsReview, ["accepted"] = accept };
    }

    // ───────────── Items and comments ─────────────

    private JsonObject MeProjects() => new()
    {
        ["projects"] = new JsonArray(LocalProjects.Where(p => !p.IsInbox).OrderBy(p => p.Name, StringComparer.CurrentCultureIgnoreCase).Select(p =>
        {
            var stats = PortfolioStats.For(p);
            return (JsonNode)new JsonObject
            {
                ["name"] = p.Name,
                ["id"] = p.Id.ToString(),
                ["group"] = p.Group,
                ["open"] = stats.Open,
                ["toReview"] = stats.ToReview,
                ["categories"] = new JsonArray(p.Categories.Select(c => (JsonNode)c.Name).ToArray()),
            };
        }).ToArray()),
    };

    /// <summary>A new item at the end of a category, as typed in its Add item… box ("BUG: …" tags it), with a priority if given.</summary>
    private JsonObject MeAddItem(string name, JsonObject body)
    {
        var project = MyProject(name);
        var vm = _main.GetProjectViewModel(project);
        var categoryName = Text(body, "category");
        var category = vm.Categories.FirstOrDefault(c => ProjectExtensions.CategoryNamesEqual(c.Model.Name, categoryName))
            ?? throw new McpToolException($"{project.Name} has no category “{categoryName}”: GET /me/projects lists them.", ToolProblem.NotFound);
        var priority = Optional(body, "priority") is { } p ? Priorities.Parse(p) ?? throw new McpToolException("priority is urgent, high, medium, low, hold or none.") : (Priority?)null;
        var before = category.Model.Items.ToHashSet();
        vm.AddItem(category, Text(body));
        var added = category.Model.Items.FirstOrDefault(i => !before.Contains(i)) ?? throw new McpToolException("Nothing to add.");
        if (priority is { } level) vm.AgentEdit(added.Id, i => i.Priority = level);
        return new JsonObject { ["item"] = added.Id.ToString(), ["project"] = project.Name, ["category"] = category.Model.Name, ["text"] = added.DisplayText(), ["priority"] = added.Priority.ToString().ToLowerInvariant() };
    }

    private JsonObject MeComment(string id, JsonObject body)
    {
        var (project, loc) = FindItemIn(id);
        if (project.Remote != null) throw new McpToolException("That item is in another ProJict's project: comment there.", ToolProblem.Forbidden);
        Guid? replyTo = Optional(body, "replyTo") is { } r ? Guid.TryParse(r, out var g) ? g : throw new McpToolException("replyTo is a comment's id.") : null;
        var comment = new Comment { By = YourName, Text = Text(body) };
        try { _main.GetProjectViewModel(project).PostComment(loc.Item.Id, comment, replyTo); }
        catch (InvalidOperationException ex) { throw new McpToolException(ex.Message, ToolProblem.Conflict); }
        return new JsonObject { ["comment"] = comment.Id.ToString(), ["item"] = loc.Item.Id.ToString(), ["by"] = YourName, ["text"] = comment.Text };
    }

    // ───────────── Chat ─────────────

    /// <summary>This ProJict's channels you're in, and your private conversations (as Chat's list has them).</summary>
    private IEnumerable<ChatChannel> OwnChannelsForMe() => Chat.Channels.Where(c => ChatRoom.Member(c, YourName) != null);

    private JsonObject ChannelRef(ChatChannel channel) => new()
    {
        ["id"] = channel.Id.ToString(),
        ["name"] = channel.Direct ? "@" + ChatRoom.Title(channel, YourName) : "#" + channel.Name,
    };

    private JsonObject MeChannels() => new()
    {
        ["channels"] = new JsonArray(OwnChannelsForMe()
            .OrderByDescending(c => c.Messages.LastOrDefault()?.At ?? c.CreatedAt)
            .Select(c =>
            {
                var json = ChannelRef(c);
                json["direct"] = c.Direct;
                json["topic"] = c.Topic;
                json["unread"] = ChatRoom.Unread(c, YourName).Count();
                json["mentions"] = ChatRoom.UnreadMentions(c, YourName).Count();
                if (c.Messages.LastOrDefault(m => !m.Notice) is { } last)
                    json["last"] = new JsonObject { ["by"] = last.By, ["at"] = last.At, ["text"] = last.Deleted ? "" : Short(last.Text) };
                return (JsonNode)json;
            }).ToArray()),
    };

    /// <summary>A channel's latest messages, oldest first (before= an id reads further back); what you read counts as read.</summary>
    private JsonObject MeReadChannel(string name, IReadOnlyDictionary<string, string> query)
    {
        var channel = MyChannel(name);
        var limit = query.TryGetValue("limit", out var l) && int.TryParse(l, out var n) ? Math.Clamp(n, 1, 200) : 50;
        var messages = channel.Messages.AsEnumerable();
        if (query.TryGetValue("before", out var before) && before.Length > 0)
        {
            var at = channel.Messages.FindIndex(m => m.Id.ToString().StartsWith(before, StringComparison.OrdinalIgnoreCase));
            if (at < 0) throw new McpToolException($"No message {before} in {ChannelRef(channel)["name"]}.", ToolProblem.NotFound);
            messages = channel.Messages.Take(at);
        }
        var page = messages.TakeLast(limit).ToList();
        var json = ChannelRef(channel);
        json["messages"] = new JsonArray(page.Select(m => (JsonNode)MessageForMe(m)).ToArray());
        json["more"] = page.Count > 0 && channel.Messages.IndexOf(page[0]) > 0;
        if (!query.ContainsKey("before")) Chat.MarkRead(channel, YourName);
        return json;
    }

    private static JsonObject MessageForMe(ChatMessage m)
    {
        var json = new JsonObject { ["id"] = m.Id.ToString(), ["by"] = m.By, ["agent"] = m.Agent, ["at"] = m.At, ["text"] = m.Deleted ? "" : m.Text };
        if (m.Notice) json["notice"] = true;
        if (m.Deleted) json["deleted"] = true;
        if (m.EditedAt != null) json["edited"] = true;
        if (m.ReplyTo is { } reply) json["replyTo"] = reply.ToString();
        if (m.Approval != null)
            json["approval"] = new JsonObject
            {
                ["asks"] = m.Approval,
                ["decision"] = m.Decision is { } d ? new JsonObject { ["by"] = d.By, ["approved"] = d.Approved, ["at"] = d.At } : null,
            };
        if (m.Reactions is { Count: > 0 } reactions)
            json["reactions"] = new JsonArray(reactions.Select(r => (JsonNode)new JsonObject { ["by"] = r.By, ["up"] = r.Up }).ToArray());
        if (m.Attachments is { Count: > 0 } files && !m.Deleted)
            json["files"] = new JsonArray(files.Select(f => (JsonNode)new JsonObject { ["name"] = f.FileName, ["size"] = f.Size }).ToArray());
        return json;
    }

    private JsonObject MePost(string name, JsonObject body)
    {
        var channel = MyChannel(name);
        Guid? replyTo = Optional(body, "replyTo") is { } r
            ? channel.Messages.FirstOrDefault(m => m.Id.ToString().StartsWith(r, StringComparison.OrdinalIgnoreCase))?.Id ?? throw new McpToolException($"No message {r} there.", ToolProblem.NotFound)
            : null;
        ChatMessage message;
        try { message = Chat.Post(channel, _state.ChatMe, Text(body), replyTo); }
        catch (ChatException ex) { throw new McpToolException(ex.Message, ToolProblem.Forbidden); }
        return MessageForMe(message);
    }

    // ───────────── Finding things ─────────────

    private Project MyProject(string name) =>
        LocalProjects.FirstOrDefault(p => p.Id.ToString().Equals(name, StringComparison.OrdinalIgnoreCase))
        ?? LocalProjects.FirstOrDefault(p => p.Name.Equals(name, StringComparison.OrdinalIgnoreCase) || p.Key.Equals(name, StringComparison.OrdinalIgnoreCase))
        ?? throw new McpToolException($"No project “{name}”: GET /me/projects lists them.", ToolProblem.NotFound);

    /// <summary>A channel of this ProJict you're in, by its id (or the first 8 characters), #name, or @name for a private conversation.</summary>
    private ChatChannel MyChannel(string name)
    {
        var mine = OwnChannelsForMe().ToList();
        return mine.FirstOrDefault(c => c.Id.ToString().StartsWith(name, StringComparison.OrdinalIgnoreCase) && name.Length >= 8)
               ?? mine.FirstOrDefault(c => !c.Direct && ("#" + c.Name).Equals(name.StartsWith('#') ? name : "#" + name, StringComparison.OrdinalIgnoreCase))
               ?? mine.FirstOrDefault(c => c.Direct && ("@" + ChatRoom.Title(c, YourName)).Equals(name, StringComparison.OrdinalIgnoreCase))
               ?? throw new McpToolException($"You're in no channel “{name}” here: GET /me/channels lists yours.", ToolProblem.NotFound);
    }

    private (ChatChannel Channel, ChatMessage Message) FindMessage(string id)
    {
        if (id.Length < 8) throw new McpToolException("Give the message's id (at least its first 8 characters).");
        foreach (var channel in OwnChannelsForMe())
            if (channel.Messages.FirstOrDefault(m => m.Id.ToString().StartsWith(id, StringComparison.OrdinalIgnoreCase)) is { } message)
                return (channel, message);
        throw new McpToolException($"No message {id} in your channels.", ToolProblem.NotFound);
    }
}
