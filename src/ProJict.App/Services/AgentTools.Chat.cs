using System.Globalization;
using System.Text.Json;
using System.Text.Json.Nodes;
using ProJict.Core.Agents;
using ProJict.Core.Chat;
using ProJict.Core.Model;

namespace ProJict.App.Services;

/// <summary>
/// Chat for agents: channels they can see, reading and posting, starting channels, inviting and moderating, and
/// wait_for_messages to listen. Every other tool's result mentions messages waiting for the agent (new ones in its
/// channels, or @mentions of it), once each, so it notices while it works.
/// </summary>
public sealed partial class AgentTools
{
    private const int MaxWaitSeconds = 60;
    private static readonly HashSet<string> ChatTools =
        ["list_channels", "create_channel", "read_channel", "post_message", "edit_message", "join_channel", "invite_to_channel", "moderate_channel", "wait_for_messages"];

    private static readonly (string, string, string, bool) ChannelArg = ("channel", "string", "The channel's name, e.g. grid2vr-hud (with or without #).", true);

    private void AddChatTools()
    {
        Add("list_channels", "List chat channels",
            "The chat channels you can see: the ones you're in and Open ones in projects you reach, with what each is linked to, " +
            "how many messages wait for you and whether one @mentions you.",
            Schema(("project", "string", "Only channels linked to this project (name or key).", false)), ListChannels, readOnly: true);
        Add("create_channel", "Start a chat channel",
            "Starts a #channel for live chat with the user and other agents; you moderate it. Link it to a project, category or item " +
            "(the name is then suggested from it). Linked channels are Open by default (anyone who can see the project can join), others invite only.",
            Schema(("name", "string", "Lowercase letters, digits and dashes, e.g. grid2vr-hud; suggested from the link when left out.", false),
                ("item", "string", "Item id to link the channel to.", false),
                ("category", "string", "Category name to link it to (in the project).", false),
                ("project", "string", "Project to link it to (name or key); with category, the category's project.", false),
                ("access", "enum:open|invite_only", "open: anyone who can see its project can join; invite_only: only those invited.", false),
                ("topic", "string", "What the channel is for, in a line; it shows in the channel lists.", false),
                ("invite", "string[]", "Names to invite, e.g. Codex.", false)),
            CreateChannel);
        Add("read_channel", "Read a chat channel",
            "A channel's latest messages (oldest first), its members and moderators. Marks what you read as read. " +
            "Items are linked as # and the first 8 characters of their id (#4e84570c), channels as #name, people and agents as @name.",
            Schema(ChannelArg, ("limit", "integer", "Most messages (default 30, at most 200).", false),
                ("before", "string", "Only messages before this message id, to read further back.", false)),
            ReadChannel, readOnly: true);
        Add("post_message", "Post in a chat channel",
            "Posts a message; in an Open channel you join it by posting. @name mentions someone (they're notified), #name links a channel, " +
            "# and an item's 8-character id links the item. At most 20 messages a minute.",
            Schema(ChannelArg, ("text", "string", "The message.", true), ("reply_to", "string", "Id of the message you answer; it's quoted.", false),
                ("attachments", "string[]", "Files to post with it, e.g. a screenshot (absolute paths or relative to your working directory).", false)),
            PostMessage, filesArgument: "attachments");
        Add("edit_message", "Edit or delete a chat message",
            "Changes the text of your own message, or deletes it (moderators can delete anyone's).",
            Schema(ChannelArg, ("id", "string", "The message id.", true), ("text", "string", "The new text.", false),
                ("delete", "boolean", "Delete the message instead.", false)),
            EditMessage);
        Add("join_channel", "Join or leave a chat channel",
            "Joins an Open channel, so new messages there wait for you, or leaves one (leave=true).",
            Schema(ChannelArg, ("leave", "boolean", "Leave the channel instead.", false)), JoinChannel);
        Add("invite_to_channel", "Invite to a chat channel",
            "Invites the user or another agent (by the name they sign with). Moderators can invite to any channel, members to Open ones.",
            Schema(ChannelArg, ("who", "string", "Name, e.g. Codex.", true)), InviteToChannel);
        Add("moderate_channel", "Moderate a chat channel",
            "For a channel's moderators: remove (kick) a member, who can't rejoin unless invited again; mute one for 15 or 60 minutes or until " +
            "unmuted (they can still read); unmute; make someone a moderator; change the topic; or make the channel open or invite only.",
            Schema(ChannelArg,
                ("action", "enum:kick|mute|unmute|make_moderator|remove_moderator|set_topic|open|invite_only", "What to do.", true),
                ("who", "string", "The member, for kick, mute, unmute and the moderator actions.", false),
                ("minutes", "integer", "For mute: 15 or 60; leave out to mute until unmuted.", false),
                ("topic", "string", "For set_topic: the new topic (empty clears it).", false)),
            ModerateChannel);
        Add("wait_for_messages", "Wait for chat messages",
            $"Listens for new chat messages for you: in channels you're in, or anywhere you're @mentioned. Returns at once when some wait, " +
            $"else as soon as one arrives, or empty after the time given (at most {MaxWaitSeconds} s). Call it again to keep listening.",
            Schema(("seconds", "integer", $"How long to wait (default and most {MaxWaitSeconds}).", false),
                ("channel", "string", "Only this channel.", false)),
            (_, _, _) => throw new InvalidOperationException("wait_for_messages runs asynchronously."), readOnly: true);
    }

    private ChatRoom Chat => _state.Chat;

    private static ChatActor ActorOf(McpSession session) => new(session.AgentName, true, Projects: session.Projects);

    private void RequireChat()
    {
        if (!_state.AppSettings.AgentsUseChat)
            throw new McpToolException("The user turned Chat off for AI agents (Settings › AI agents).", ToolProblem.Forbidden);
    }

    private ChatChannel ChannelFor(McpSession session, Args args, string name = "channel")
    {
        RequireChat();
        var key = args.Required(name);
        var channel = Chat.Find(key);
        if (channel == null || !ChatRoom.CanSee(channel, ActorOf(session)))
            throw new McpToolException($"No channel #{key.Trim().TrimStart('#')} you can see. list_channels shows them.", ToolProblem.NotFound);
        return channel;
    }

    private static T Chatting<T>(Func<T> action)
    {
        try { return action(); }
        catch (ChatException ex) { throw new McpToolException(ex.Message, ToolProblem.Forbidden); }
    }

    // ───────────── Tools ─────────────

    private string ListChannels(McpSession session, Args args, List<Attachment> files)
    {
        RequireChat();
        var actor = ActorOf(session);
        var only = args.String("project") != null ? ResolveProject(session, args) : null;
        var channels = Chat.Visible(actor).Where(c => only == null || c.Link?.ProjectId == only.Id)
            .OrderByDescending(c => c.Messages.LastOrDefault()?.At ?? c.CreatedAt)
            .Select(c => (JsonNode)ChannelJson(c, session)).ToArray();
        return Json(new JsonObject
        {
            ["channels"] = new JsonArray(channels),
            ["note"] = channels.Length == 0 ? "No channels yet; start one with create_channel." : null,
        });
    }

    private string CreateChannel(McpSession session, Args args, List<Attachment> files)
    {
        RequireChat();
        var (link, suggested) = LinkFor(session, args);
        var name = args.String("name") is { Length: > 0 } given ? given.Trim().TrimStart('#').ToLowerInvariant()
            : suggested ?? throw new McpToolException("Give the channel a name, or link it to a project, category or item to have one suggested.");
        var access = args.String("access") switch
        {
            "open" => ChannelAccess.Open,
            "invite_only" => ChannelAccess.InviteOnly,
            null => link != null ? ChannelAccess.Open : ChannelAccess.InviteOnly,
            var other => throw new McpToolException($"access must be open or invite_only, not {other}."),
        };
        var actor = ActorOf(session);
        var channel = Chatting(() => Chat.Create(actor, name, access, link, args.String("topic")));
        foreach (var who in args.Strings("invite")) Chatting(() => { Chat.Invite(channel, actor, who, IsAgentName(who)); return 0; });
        return Json(ChannelJson(channel, session));
    }

    /// <summary>The link from item, category or project, and the name it suggests.</summary>
    private (ChannelLink? Link, string? Name) LinkFor(McpSession session, Args args)
    {
        if (args.String("item") is { } itemId)
        {
            var (project, loc) = FindItem(session, itemId);
            return (new ChannelLink { ProjectId = project.Id, CategoryId = loc.Category.Id, ItemId = loc.Item.Id }, Chat.SuggestName(project.Name, null, loc.Item.DisplayText()));
        }
        if (args.String("category") is { } categoryName)
        {
            var project = ResolveProject(session, args);
            var category = project.Categories.FirstOrDefault(c => c.Name.Equals(categoryName.Trim(), StringComparison.OrdinalIgnoreCase))
                           ?? throw new McpToolException($"No category \"{categoryName}\" in {project.Name}.", ToolProblem.NotFound);
            return (new ChannelLink { ProjectId = project.Id, CategoryId = category.Id }, Chat.SuggestName(project.Name, category.Name));
        }
        if (args.String("project") != null)
        {
            var project = ResolveProject(session, args);
            return (new ChannelLink { ProjectId = project.Id }, Chat.SuggestName(project.Name));
        }
        return (null, null);
    }

    private string ReadChannel(McpSession session, Args args, List<Attachment> files)
    {
        var channel = ChannelFor(session, args);
        var limit = Math.Clamp(args.Int("limit") ?? 30, 1, 200);
        IEnumerable<ChatMessage> messages = channel.Messages;
        if (args.String("before") is { } before)
        {
            var from = ChatRoom.FindMessage(channel, before.Trim()) ?? throw new McpToolException($"No message {before} in #{channel.Name}.", ToolProblem.NotFound);
            messages = messages.TakeWhile(m => m != from);
        }
        var shown = messages.TakeLast(limit).ToList();
        var json = ChannelJson(channel, session);
        json["members"] = new JsonArray(channel.Members.Select(m => (JsonNode)new JsonObject
        {
            ["name"] = m.Name,
            ["agent"] = m.Agent ? true : null,
            ["moderator"] = m.Moderator || (!m.Agent && Chat.IsOwner(m.Name)) ? true : null,
            ["mutedUntil"] = Chat.MutedUntil(channel, m.Name) is { } until ? (until == DateTime.MaxValue ? "until unmuted" : Time(until)) : null,
        }).ToArray());
        json["messages"] = new JsonArray(shown.Select(m => (JsonNode)MessageJson(m, session.AgentName)).ToArray());
        if (shown.Count > 0 && shown[0] != channel.Messages[0]) json["more"] = $"Earlier messages: read_channel with before={ShortId(shown[0].Id)}.";
        Delivered(session, channel, shown);
        return Json(json);
    }

    private string PostMessage(McpSession session, Args args, List<Attachment> files)
    {
        var channel = ChannelFor(session, args);
        Guid? replyTo = args.String("reply_to") is { } r
            ? (ChatRoom.FindMessage(channel, r.Trim()) ?? throw new McpToolException($"No message {r} in #{channel.Name}.", ToolProblem.NotFound)).Id
            : null;
        var message = Chatting(() => Chat.Post(channel, ActorOf(session), args.String("text") ?? "", replyTo, files));
        session.Noticed.Add($"chat:{message.Id}");
        return Json(new JsonObject { ["id"] = ShortId(message.Id), ["channel"] = "#" + channel.Name, ["at"] = Time(message.At) });
    }

    private string EditMessage(McpSession session, Args args, List<Attachment> files)
    {
        var channel = ChannelFor(session, args);
        var id = args.Required("id").Trim();
        var message = ChatRoom.FindMessage(channel, id) ?? throw new McpToolException($"No message {id} in #{channel.Name}.", ToolProblem.NotFound);
        var actor = ActorOf(session);
        if (args.Bool("delete") == true)
        {
            Chatting(() => { Chat.DeleteMessage(channel, actor, message.Id); return 0; });
            return Json(new JsonObject { ["deleted"] = ShortId(message.Id) });
        }
        Chatting(() => { Chat.Edit(channel, actor, message.Id, args.Required("text")); return 0; });
        return Json(new JsonObject { ["edited"] = ShortId(message.Id) });
    }

    private string JoinChannel(McpSession session, Args args, List<Attachment> files)
    {
        var channel = ChannelFor(session, args);
        var actor = ActorOf(session);
        if (args.Bool("leave") == true) Chatting(() => { Chat.Leave(channel, actor); return 0; });
        else Chatting(() => { Chat.Join(channel, actor); return 0; });
        return Json(ChannelJson(channel, session));
    }

    private string InviteToChannel(McpSession session, Args args, List<Attachment> files)
    {
        var channel = ChannelFor(session, args);
        var who = args.Required("who").Trim().TrimStart('@');
        Chatting(() => { Chat.Invite(channel, ActorOf(session), who, IsAgentName(who)); return 0; });
        return Json(new JsonObject { ["invited"] = who, ["channel"] = "#" + channel.Name });
    }

    private string ModerateChannel(McpSession session, Args args, List<Attachment> files)
    {
        var channel = ChannelFor(session, args);
        var actor = ActorOf(session);
        var action = args.Required("action");
        string Who() => args.Required("who").Trim().TrimStart('@');
        Chatting(() =>
        {
            switch (action)
            {
                case "kick": Chat.Kick(channel, actor, Who()); break;
                case "mute":
                    TimeSpan? span = args.Int("minutes") is { } minutes and > 0 ? TimeSpan.FromMinutes(Math.Min(minutes, 60 * 24 * 7)) : null;
                    Chat.Mute(channel, actor, Who(), span);
                    break;
                case "unmute": Chat.Unmute(channel, actor, Who()); break;
                case "make_moderator": Chat.SetModerator(channel, actor, Who(), true); break;
                case "remove_moderator": Chat.SetModerator(channel, actor, Who(), false); break;
                case "set_topic": Chat.SetTopic(channel, actor, args.String("topic")); break;
                case "open": Chat.SetAccess(channel, actor, ChannelAccess.Open); break;
                case "invite_only": Chat.SetAccess(channel, actor, ChannelAccess.InviteOnly); break;
                default: throw new McpToolException("action must be kick, mute, unmute, make_moderator, remove_moderator, set_topic, open or invite_only.");
            }
            return 0;
        });
        return Json(new JsonObject { ["done"] = action, ["channel"] = "#" + channel.Name, ["notice"] = channel.Messages.LastOrDefault(m => m.Notice)?.Text });
    }

    // ───────────── Listening ─────────────

    /// <summary>Waits off the UI thread until a message arrives for the agent, or the time is up.</summary>
    private async Task<string> WaitForMessagesAsync(McpSession session, JsonElement arguments)
    {
        var args = new Args(arguments);
        var seconds = Math.Clamp(args.Int("seconds") ?? MaxWaitSeconds, 1, MaxWaitSeconds);
        var arrived = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        Action<ChatChannel>? listener = null;
        var only = await Run(session, () =>
        {
            RequireChat();
            var channel = args.String("channel") != null ? ChannelFor(session, args) : null;
            // Any change can be for it: a message, an @mention, an invitation.
            listener = c =>
            {
                if ((channel == null || c == channel) && Waiting(session, channel).Count > 0) arrived.TrySetResult();
            };
            if (Waiting(session, channel).Count > 0) arrived.TrySetResult();
            else Chat.Changed += listener;
            return channel;
        });
        try
        {
            await Task.WhenAny(arrived.Task, Task.Delay(TimeSpan.FromSeconds(seconds)));
        }
        finally
        {
            await _dispatcher.InvokeAsync(() => Chat.Changed -= listener);
        }
        return await Run(session, () =>
        {
            var waiting = Waiting(session, only);
            var json = new JsonObject
            {
                ["messages"] = new JsonArray(waiting.Select(w =>
                {
                    var node = MessageJson(w.Message, session.AgentName);
                    node["channel"] = "#" + w.Channel.Name;
                    return (JsonNode)node;
                }).ToArray()),
            };
            if (waiting.Count == 0) json["note"] = $"Nothing new in {seconds} s. Call wait_for_messages again to keep listening.";
            foreach (var group in waiting.GroupBy(w => w.Channel)) Delivered(session, group.Key, group.Select(w => w.Message).ToList());
            RecordText(session, waiting.Count == 0 ? "Listened in Chat" : $"Got {Count(waiting.Count, "chat message")}", waiting.FirstOrDefault().Channel);
            return Json(json);
        });
    }

    private async Task<T> Run<T>(McpSession session, Func<T> action)
    {
        var (result, error) = await _dispatcher.InvokeAsync(() =>
        {
            _caller = session;
            try { return (action(), (Exception?)null); }
            catch (Exception ex) { return (default(T)!, ex); }
            finally { _caller = null; }
        });
        if (error != null) System.Runtime.ExceptionServices.ExceptionDispatchInfo.Throw(error);
        return result;
    }

    /// <summary>
    /// What waits for the agent: what it hasn't read in its channels (messages, and lines about it such as an invitation),
    /// and @mentions of it in channels it isn't in, also from while it wasn't connected.
    /// </summary>
    private List<(ChatChannel Channel, ChatMessage Message)> Waiting(McpSession session, ChatChannel? only = null)
    {
        var actor = ActorOf(session);
        var waiting = new List<(ChatChannel, ChatMessage)>();
        foreach (var channel in Chat.Visible(actor).Where(c => only == null || c == only))
        {
            if (ChatRoom.Member(channel, actor.Name) != null)
                waiting.AddRange(ChatRoom.Unread(channel, actor.Name).Select(m => (channel, m)));
            else
                waiting.AddRange(Chat.MentionsOutside(channel, actor.Name).Select(m => (channel, m)));
        }
        return waiting;
    }

    /// <summary>The agent has these messages now: they're read (in its channels) or delivered (mentions elsewhere).</summary>
    private void Delivered(McpSession session, ChatChannel channel, List<ChatMessage> messages)
    {
        if (messages.Count == 0) return;
        foreach (var m in messages) session.Noticed.Add($"chat:{m.Id}");
        var upTo = messages.Max(m => m.At);
        if (ChatRoom.Member(channel, session.AgentName) != null) Chat.MarkRead(channel, session.AgentName, upTo);
        else Chat.MarkMentionsRead(channel, session.AgentName, upTo);
    }

    /// <summary>
    /// For the waitingForYou list (each thing once per session): invitations and other lines about the agent, and how many
    /// new messages wait in which channel, saying when one @mentions it; with what to do about them.
    /// </summary>
    private List<string> ChatLines(McpSession session)
    {
        if (!_state.AppSettings.AgentsUseChat || Chat.Channels.Count == 0) return [];
        var fresh = Waiting(session).Where(w => !session.Noticed.Contains($"chat:{w.Message.Id}")).ToList();
        if (fresh.Count == 0) return [];
        foreach (var (_, message) in fresh) session.Noticed.Add($"chat:{message.Id}");
        var parts = new List<string>();
        var asked = false;
        foreach (var group in fresh.GroupBy(w => w.Channel))
        {
            foreach (var (_, notice) in group.Where(w => w.Message.Notice))
            {
                parts.Add(notice.AboutText ?? notice.Text);
                asked |= notice.Text.Contains(" invited ", StringComparison.Ordinal);
            }
            var messages = group.Where(w => !w.Message.Notice).ToList();
            if (messages.Count == 0) continue;
            var mentions = messages.Count(w => ChatText.Mentions(w.Message.Text, session.AgentName));
            asked |= mentions > 0;
            parts.Add($"{Count(messages.Count, "new message")} in #{group.Key.Name}" +
                      (mentions > 0 ? $" ({(mentions == 1 ? "one mentions" : $"{mentions} mention")} you)" : ""));
        }
        return ["Chat: " + string.Join("; ", parts) + ". Read them with read_channel" +
                (asked ? ", and reply with post_message: say hello when invited, and answer what you're asked (or say you can't now)." : ".")];
    }

    // ───────────── JSON ─────────────

    private JsonObject ChannelJson(ChatChannel channel, McpSession session)
    {
        var name = session.AgentName;
        var member = ChatRoom.Member(channel, name);
        var unread = member != null ? ChatRoom.Unread(channel, name).ToList() : [];
        return new JsonObject
        {
            ["name"] = "#" + channel.Name,
            ["topic"] = channel.Topic,
            ["access"] = channel.Access == ChannelAccess.Open ? "open" : "invite_only",
            ["linkedTo"] = LinkText(channel.Link),
            ["member"] = member != null,
            ["moderator"] = ChatRoom.IsModerator(channel, ActorOf(session)) ? true : null,
            ["mutedUntil"] = Chat.MutedUntil(channel, name) is { } until ? (until == DateTime.MaxValue ? "until unmuted" : Time(until)) : null,
            ["members"] = channel.Members.Count,
            ["unread"] = unread.Count > 0 ? unread.Count : null,
            ["mentionsYou"] = unread.Any(m => ChatText.Mentions(m.Text, name)) ? true : null,
            ["lastMessage"] = channel.Messages.LastOrDefault(m => !m.Notice) is { } last ? Time(last.At) : null,
            ["startedBy"] = channel.CreatedBy,
        };
    }

    private JsonObject MessageJson(ChatMessage m, string you)
    {
        if (m.Notice)
            return string.Equals(m.About, you, StringComparison.OrdinalIgnoreCase)
                ? new JsonObject { ["notice"] = m.AboutText ?? m.Text, ["aboutYou"] = true, ["at"] = Time(m.At) }
                : new JsonObject { ["notice"] = m.Text, ["at"] = Time(m.At) };
        return new JsonObject
        {
            ["id"] = ShortId(m.Id),
            ["by"] = m.By,
            ["agent"] = m.Agent ? true : null,
            ["at"] = Time(m.At),
            ["text"] = m.Deleted ? null : m.Text,
            ["deleted"] = m.Deleted ? true : null,
            ["replyTo"] = m.ReplyTo is { } r ? ShortId(r) : null,
            ["edited"] = m.EditedAt != null && !m.Deleted ? true : null,
            ["mentionsYou"] = ChatText.Mentions(m.Text, you) ? true : null,
            ["attachments"] = m.Attachments is { Count: > 0 } files ? Paths(files) : null,
        };
    }

    /// <summary>"GRID2VR › HUD › “crash when pausing” (#4e84570c)".</summary>
    private string? LinkText(ChannelLink? link)
    {
        if (link == null || _state.Projects.FirstOrDefault(p => p.Id == link.ProjectId) is not { } project) return null;
        var text = project.Name;
        if (link.CategoryId is { } c && project.Categories.FirstOrDefault(x => x.Id == c) is { } category) text += " › " + category.Name;
        if (link.ItemId is { } i && project.Find(i) is { } loc) text += $" › “{Short(loc.Item.DisplayText())}” (#{ShortId(i)})";
        return text;
    }

    private static string Time(DateTime at) => at.ToString("yyyy-MM-dd HH:mm", CultureInfo.InvariantCulture);

    private static string Count(int n, string what) => n == 1 ? $"1 {what}" : $"{n} {what}s";

    /// <summary>An agent's name (connected now, or seen in a channel or in the history); otherwise a person.</summary>
    private bool IsAgentName(string name)
    {
        if (Chat.IsOwner(name)) return false;
        lock (_live)
            if (_live.Any(s => string.Equals(s.AgentName, name, StringComparison.OrdinalIgnoreCase))) return true;
        return Chat.Channels.SelectMany(c => c.Members).Any(m => m.Agent && string.Equals(m.Name, name, StringComparison.OrdinalIgnoreCase)) ||
               _state.Projects.Any(p => p.Walk().Any(l => (l.Item.Activity ?? []).Any(a => a.Agent && string.Equals(a.By, name, StringComparison.OrdinalIgnoreCase))));
    }

    /// <summary>Names the user can invite: agents connected now first, then the ones seen before (in channels or the history).</summary>
    public IReadOnlyList<string> KnownAgentNames()
    {
        List<string> live;
        lock (_live) live = _live.Select(s => s.AgentName).Distinct(StringComparer.OrdinalIgnoreCase).Order(StringComparer.CurrentCultureIgnoreCase).ToList();
        var earlier = Chat.Channels.SelectMany(c => c.Members).Where(m => m.Agent).Select(m => m.Name)
            .Concat(_state.Projects.SelectMany(p => p.Walk()).SelectMany(l => l.Item.Activity ?? []).Where(a => a.Agent).Select(a => AgentNaming.Identity(a.By)))
            .Distinct(StringComparer.OrdinalIgnoreCase).Where(n => !live.Contains(n, StringComparer.OrdinalIgnoreCase))
            .Order(StringComparer.CurrentCultureIgnoreCase);
        return [.. live, .. earlier];
    }
}
