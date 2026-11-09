using System.Globalization;
using System.Text.Json;
using System.Text.Json.Nodes;
using ProJict.App.ViewModels;
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
        ["list_channels", "create_channel", "read_channel", "post_message", "ask_approval", "claim_reply", "edit_message", "join_channel", "invite_to_channel", "moderate_channel", "wait_for_messages"];
    /// <summary>Chat's tools, for an API token that may only use Chat.</summary>
    public static IReadOnlySet<string> ChatToolNames => ChatTools;

    private static readonly (string, string, string, bool) ChannelArg = ("channel", "string",
        "The channel's name, e.g. grid2vr-hud (with or without #), or @name for your private conversation with the user or an agent.", true);

    private void AddChatTools()
    {
        Add("list_channels", "List chat channels",
            "The chat channels you can see: the ones you're in and Open ones in projects you reach, with what each is linked to, " +
            "how many messages wait for you and whether one @mentions you. To read an Open channel's messages, join it first (join_channel).",
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
            "A channel's latest messages (oldest first), its members and moderators, in a channel you're in (join an Open one first " +
            "with join_channel). Marks what you read as read. " +
            "Items are linked as # and the first 8 characters of their id (#4e84570c), channels as #name, people and agents as @name.",
            Schema(ChannelArg, ("limit", "integer", "Most messages (default 30, at most 200).", false),
                ("before", "string", "Only messages before this message id, to read further back.", false)),
            ReadChannel, readOnly: true, output: ReadChannelOutput);
        Add("post_message", "Post in a chat channel",
            "Posts a message; in an Open channel you join it by posting. With channel @name it goes privately to the user or an agent (the " +
            "conversation starts with your first message). @name in the text mentions someone (they're notified), @everyone everyone in the channel, #name links a channel, " +
            "# and an item's 8-character id links the item. At most 20 messages a minute. " + ChatStyle,
            Schema(ChannelArg, ("text", "string", "The message.", true), ("reply_to", "string", "Id of the message you answer; it's quoted.", false),
                ("attachments", "string[]", "Files to post with it, e.g. a screenshot or a build (absolute paths or relative to your working directory), " +
                 "each up to the user's limit for Chat (500 MB unless they changed it).", false)),
            PostMessage, filesArgument: "attachments");
        Add("ask_approval", "Ask for approval",
            "Asks a person in Chat to approve one change you name, before you make it (commit, push, delete, spend…). The message gets Approve and Decline " +
            "buttons; a 👍 or 👎 on it answers too, and the first person to answer decides. You're told \"approved\" or \"declined\" for exactly what you " +
            "named (your chat watcher prints [approval] …), and nothing else counts as approval: a 👍 on any other message is just agreement with it. " +
            "Do nothing of it before you're told. One change per question. With pin=true, for what can't be taken back (publishing a release, " +
            "say): only the user answers it, a 👍 doesn't, and approving it asks them \"Are you sure?\" and their PIN.",
            Schema(ChannelArg, ("asks", "string", $"Exactly what you'll do once approved, as a short line (up to {ChatRoom.MaxApproval} characters), " +
                                                  "e.g. \"commit #9113ea6a (not pushed)\" or \"delete the 3 old test builds\".", true),
                ("text", "string", "The message, if you want to say more than the request (it's shown above it).", false),
                ("reply_to", "string", "Id of the message you answer; it's quoted.", false),
                ("pin", "boolean", "It can't be taken back (a release, a push to the main branch, deleting for good): the user approves it with " +
                                   "\"Are you sure?\" and their PIN.", false)),
            AskApproval);
        Add("claim_reply", "Say you're answering a message",
            "Before you answer a person's message that other agents could answer too (it @mentions nobody, several of you, or @everyone), claim it: " +
            "if it's yours, answer it (post_message with reply_to it); if another agent has it, you're told who, so leave it to them. The others are " +
            "told you're answering, and their replies to it are refused. A message that @mentions only you is yours already, and your first reply " +
            "to a message claims it too. A claim nobody answers lapses after 10 minutes.",
            Schema(ChannelArg, ("id", "string", "The message's id.", true)),
            ClaimReply);
        Add("edit_message", "Edit or delete a chat message",
            "Changes the text of your own message, or deletes it (moderators can delete anyone's).",
            Schema(ChannelArg, ("id", "string", "The message id.", true), ("text", "string", "The new text.", false),
                ("delete", "boolean", "Delete the message instead.", false)),
            EditMessage);
        Add("join_channel", "Join or leave a chat channel",
            "Joins an Open channel, so you can read it (read_channel) and new messages there wait for you, or leaves one (leave=true).",
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
            $"Listens for new chat messages for you: in channels you're in, or anywhere you're @mentioned; for the answers to what you asked with " +
            $"ask_approval (approved or declined); and for 👍/👎 on your other messages (agreement or disagreement with them, never approval). " +
            $"Returns at once when some wait, else as soon as one arrives, " +
            $"or empty after the time given (at most {MaxWaitSeconds} s). Call it again to keep listening.",
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

    private ChatChannel ChannelFor(McpSession session, Args args, string name = "channel", bool start = false)
    {
        RequireChat();
        var key = args.Required(name).Trim();
        if (key.StartsWith('@'))
        {
            // @name: the private conversation with them, started by writing to them.
            var other = key[1..].Trim();
            if (Chat.FindDirect(session.AgentName, other) is { } direct) return direct;
            if (!start) throw new McpToolException($"You have no private conversation with {other} yet; post_message with channel @{other} starts one.", ToolProblem.NotFound);
            return Chatting(() => Chat.OpenDirect(ActorOf(session), other, IsAgentName(other)));
        }
        var channel = Chat.Find(key);
        if (channel == null && SharedChannelFor(session, args, name) is { } shared)
            throw new McpToolException($"In {SharedName(shared.Host, shared.Channel)} you join, read and post (join_channel, read_channel, post_message); the rest is for the people there.", ToolProblem.Forbidden);
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
        // This ProJict's channels, then other ProJicts' (named like #hud (Sam's)).
        var channels = Chat.Visible(actor).Where(c => only == null || c.Link?.ProjectId == only.Id)
            .OrderByDescending(c => c.Messages.LastOrDefault()?.At ?? c.CreatedAt)
            .Select(c => (JsonNode)ChannelJson(c, session))
            .Concat(only is { Remote: null } ? [] : SharedChannelsJson(session, only)).ToArray();
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
        RequireChat();
        if (SharedChannelFor(session, args) is { } shared) return SharedRead(session, shared.Host, shared.Channel, args);
        var channel = ChannelFor(session, args);
        // Its messages are for those in it: an agent joins an Open channel first, which the channel sees.
        if (ChatRoom.Member(channel, session.AgentName) == null)
            throw new McpToolException($"You're not in #{channel.Name}: join it first with join_channel (anyone may, and the channel sees you joined), " +
                                       "then read_channel shows its messages. list_channels has its name and topic.", ToolProblem.Forbidden);
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
            ["presence"] = Presence.Word(_main.Agents.StateOf(m.Name)),
            ["moderator"] = m.Moderator || (!m.Agent && Chat.IsOwner(m.Name)) ? true : null,
            ["mutedUntil"] = Chat.MutedUntil(channel, m.Name) is { } until ? (until == DateTime.MaxValue ? "until unmuted" : Time(until)) : null,
        }).ToArray());
        json["messages"] = new JsonArray(shown.Select(m => (JsonNode)MessageJson(channel, m, session.AgentName)).ToArray());
        if (shown.Count > 0 && shown[0] != channel.Messages[0]) json["more"] = $"Earlier messages: read_channel with before={ShortId(shown[0].Id)}.";
        Delivered(session, channel, shown);
        return Json(json);
    }

    /// <summary>How agents are asked to write in Chat (post_message's description and their instructions).</summary>
    private const string ChatStyle =
        "Write like a person in a chat: a few short sentences, one point at a time, in plain words; no walls of text, headings, long lists " +
        "or reports. Put details in the item's discussion (add_comment) or a note (write_doc) and link it, and take a long or technical " +
        "back-and-forth with another agent to a private conversation (channel @name).";

    /// <summary>A channel message longer than this (characters or lines) gets a gentle note back; private conversations don't.</summary>
    private const int ChatLongCharacters = 600, ChatLongLines = 8;

    private string PostMessage(McpSession session, Args args, List<Attachment> files)
    {
        RequireChat();
        RequireOwnName(session, "post");
        if (SharedChannelFor(session, args) is { } shared) return SharedPost(session, shared.Host, shared.Channel, args, files);
        var channel = ChannelFor(session, args, start: true);
        Guid? replyTo = args.String("reply_to") is { } r
            ? (ChatRoom.FindMessage(channel, r.Trim()) ?? throw new McpToolException($"No message {r} in #{channel.Name}.", ToolProblem.NotFound)).Id
            : null;
        var limit = _state.Settings.ChatFileLimitBytes;
        if (files.FirstOrDefault(f => f.Size > limit) is { } big)
            throw new McpToolException($"{big.FileName} is {AttachmentViewModel.FormatSize(big.Size)}; files in Chat can be up to {AttachmentViewModel.FormatSize(limit)} " +
                                       "(the user's setting, Settings › Chat). Send a smaller file, or say where it is.");
        var text = args.String("text") ?? "";
        var message = Chatting(() => Chat.Post(channel, ActorOf(session), text, replyTo, files));
        session.Noticed.Add($"chat:{message.Id}");
        var result = new JsonObject { ["id"] = ShortId(message.Id), ["channel"] = Display(channel, session), ["at"] = Time(message.At) };
        if ((channel.Direct && ChatRoom.Member(channel, _state.UserName) != null || ChatRoom.Mentions(channel, message, _state.UserName)) &&
            Presence.UnavailableNotice(_state.UserName, _main.Agents.MyStatus, _main.Agents.MyState) is { } unavailable)
            result["userUnavailable"] = unavailable;
        if (!channel.Direct && IsWallOfText(text))
            result["note"] = "That was long for a chat channel. Keep messages short and conversational: a few sentences, one point at a time. " +
                             "Put details in the item's discussion (add_comment) or a note (write_doc) and link it, or go on privately with post_message to @name.";
        return Json(result);
    }

    /// <summary>An agent says it answers a person's message (#1581da50): the first one gets it, the others are told who does.</summary>
    private string ClaimReply(McpSession session, Args args, List<Attachment> files)
    {
        RequireChat();
        RequireOwnName(session, "claim it");
        if (SharedChannelFor(session, args) is not null)
            throw new McpToolException("Messages in another ProJict's channels can't be claimed yet: answer only what's meant for you there.");
        var channel = ChannelFor(session, args, start: false);
        var id = args.Required("id").Trim();
        var message = ChatRoom.FindMessage(channel, id) ?? throw new McpToolException($"No message {id} in {Display(channel, session)}.", ToolProblem.NotFound);
        var (yours, answerer) = Chatting(() => Chat.ClaimReply(channel, ActorOf(session), message.Id));
        session.Noticed.Add($"claim:{message.Id}:{answerer}");
        return Json(new JsonObject
        {
            ["message"] = ShortId(message.Id),
            ["yours"] = yours,
            ["answering"] = answerer,
            ["next"] = yours
                ? $"It's yours: answer it with post_message reply_to {ShortId(message.Id)}. The others are told you're answering. If you can't after all, say so: the claim lapses in 10 minutes."
                : $"{answerer} is answering it: leave it to them. If you have something they don't, tell them privately.",
        });
    }

    /// <summary>
    /// The person's messages another agent is answering, each once, to the agents who were told about them: leave them to it.
    /// </summary>
    private List<string> ClaimLines(McpSession session, bool watcher = false)
    {
        var lines = new List<string>();
        var now = DateTime.Now;
        foreach (var channel in Chat.Visible(ActorOf(session)))
            foreach (var m in channel.Messages.TakeLast(50))
            {
                if (m.ClaimedBy is not { } answerer || string.Equals(answerer, session.AgentName, StringComparison.OrdinalIgnoreCase) ||
                    !session.Noticed.Contains($"chat:{m.Id}") || ChatRoom.AnswererOf(channel, m, now) != answerer || !session.Noticed.Add($"claim:{m.Id}:{answerer}"))
                    continue;
                var text = $"{answerer} is answering {m.By}'s message “{Short(OneLine(m.Text))}” (id {ShortId(m.Id)}): leave it to them.";
                lines.Add(watcher ? $"[{(channel.Direct ? "private" : Display(channel, session))}] {text}" : $"In {Display(channel, session)}, {text}");
            }
        return lines;
    }

    /// <summary>A message asking a person to approve one named change (#848d7643): Approve and Decline buttons, and only its answer counts.</summary>
    private string AskApproval(McpSession session, Args args, List<Attachment> files)
    {
        RequireChat();
        RequireOwnName(session, "ask");
        if (SharedChannelFor(session, args) is not null)
            throw new McpToolException("Ask for approval in this ProJict's own channels, or privately with post_message to @name; another ProJict's channels can't take it yet.");
        var channel = ChannelFor(session, args, start: true);
        var asks = args.Required("asks").Trim();
        Guid? replyTo = args.String("reply_to") is { } r
            ? (ChatRoom.FindMessage(channel, r.Trim()) ?? throw new McpToolException($"No message {r} in #{channel.Name}.", ToolProblem.NotFound)).Id
            : null;
        var text = args.String("text") is { } said && said.Trim().Length > 0 ? said : $"May I {asks}?";
        var withPin = args.Bool("pin") == true;
        var message = Chatting(() => Chat.Post(channel, ActorOf(session), text, replyTo, approval: asks, withPin: withPin));
        session.Noticed.Add($"chat:{message.Id}");
        var json = new JsonObject
        {
            ["id"] = ShortId(message.Id),
            ["channel"] = Display(channel, session),
            ["asks"] = message.Approval,
            ["next"] = withPin
                ? $"Wait for the answer: {_state.UserName} approves it with their PIN, in ProJict (your chat watcher prints [approval] …). " +
                  "Do nothing of it before. If they tell you their answer where you run (Claude Code, Codex), that counts too."
                : "Wait for the answer: you'll be told when it's approved or declined (your chat watcher prints [approval] …). Do nothing of it before.",
        };
        if (withPin) json["pin"] = true;
        if (Presence.UnavailableNotice(_state.UserName, _main.Agents.MyStatus, _main.Agents.MyState) is { } unavailable)
            json["userUnavailable"] = unavailable;
        return Json(json);
    }

    private static bool IsWallOfText(string text) =>
        text.Trim().Length > ChatLongCharacters || text.Split('\n').Count(l => l.Trim().Length > 0) > ChatLongLines;

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
        if (SharedChannelFor(session, args) is { } shared) return SharedJoin(session, shared.Host, shared.Channel, args.Bool("leave") == true);
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
        return Json(new JsonObject { ["invited"] = who, ["channel"] = Display(channel, session) });
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
        return Json(new JsonObject { ["done"] = action, ["channel"] = Display(channel, session), ["notice"] = channel.Messages.LastOrDefault(m => m.Notice)?.Text });
    }

    // ───────────── Listening ─────────────

    /// <summary>Waits off the UI thread until a message arrives for the agent, or the time is up.</summary>
    private async Task<string> WaitForMessagesAsync(McpSession session, JsonElement arguments)
    {
        var args = new Args(arguments);
        var seconds = Math.Clamp(args.Int("seconds") ?? MaxWaitSeconds, 1, MaxWaitSeconds);
        if (IsAsleep)
        {
            // Asleep: it waits (until woken, at most the time given) and gets nothing, so it doesn't spin.
            var woke = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
            Action awake = () => woke.TrySetResult();
            SleepChanged += awake;
            try { await Task.WhenAny(woke.Task, Task.Delay(TimeSpan.FromSeconds(seconds))); }
            finally { SleepChanged -= awake; }
            if (IsAsleep)
                return await Run(session, () => WithWaiting(session, "wait_for_messages", Json(new JsonObject
                {
                    ["messages"] = new JsonArray(),
                    ["note"] = "ProJict is asleep: messages wait until you're woken. Stop here; you'll hear when it's woken.",
                })));
        }
        var arrived = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        Action<ChatChannel>? listener = null;
        var only = await Run(session, () =>
        {
            RequireChat();
            var channel = args.String("channel") != null ? ChannelFor(session, args) : null;
            // Any change can be for it: a message, an @mention, an invitation, a 👍 or 👎 on its message.
            listener = c =>
            {
                if ((channel == null || c == channel) && (Waiting(session, channel).Count > 0 || NewAnswers(session, channel).Any())) arrived.TrySetResult();
            };
            if (Waiting(session, channel).Count > 0 || NewAnswers(session, channel).Any()) arrived.TrySetResult();
            else Chat.Changed += listener;
            return channel;
        });
        // The office shows it listening at its desk, headphones on, meanwhile.
        session.Listening = true;
        try
        {
            await Task.WhenAny(arrived.Task, Task.Delay(TimeSpan.FromSeconds(seconds)));
        }
        finally
        {
            session.Listening = false;
            await _dispatcher.InvokeAsync(() => Chat.Changed -= listener);
        }
        return await Run(session, () =>
        {
            var waiting = Waiting(session, only);
            var answers = NewAnswers(session, only).ToList();
            var json = new JsonObject
            {
                ["messages"] = new JsonArray(waiting.Select(w =>
                {
                    var node = MessageJson(w.Channel, w.Message, session.AgentName);
                    node["channel"] = Display(w.Channel, session);
                    return (JsonNode)node;
                }).ToArray()),
            };
            if (answers.Count > 0)
                json["answers"] = new JsonArray(answers.Select(a => (JsonNode)new JsonObject
                {
                    ["channel"] = Display(a.Channel, session),
                    ["message"] = ShortId(a.Message.Id),
                    ["text"] = Short(OneLine(a.Message.Text)),
                    ["by"] = a.Reaction.By,
                    ["answer"] = a.Reaction.Up ? "yes" : "no",
                    ["at"] = Time(a.Reaction.At),
                    ["meaning"] = a.Reaction.Up ? "approval: go ahead" : "a decline: don't",
                }).ToArray());
            foreach (var a in answers) session.Noticed.Add(a.Key);
            if (waiting.Count == 0 && answers.Count == 0) json["note"] = $"Nothing new in {seconds} s. Call wait_for_messages again to keep listening.";
            foreach (var group in waiting.GroupBy(w => w.Channel)) Delivered(session, group.Key, group.Select(w => w.Message).ToList());
            var got = new[] { waiting.Count > 0 ? Count(waiting.Count, "chat message") : null, answers.Count > 0 ? Count(answers.Count, "answer") : null }.OfType<string>().ToList();
            RecordText(session, got.Count == 0 ? "Listened in Chat" : "Got " + string.Join(" and ", got), waiting.FirstOrDefault().Channel ?? answers.FirstOrDefault().Channel,
                "wait_for_messages");
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
        // A read-only token that may only read Chat isn't asked to answer.
        if (!_state.AppSettings.AgentsUseChat || (session.ReadOnly && !_state.AppSettings.ReadOnlyTokensUseChat)) return [];
        var lines = LocalChatLines(session);
        if (SharedChatLine(session) is { } shared) lines.Add(shared);
        lines.AddRange(SharedRefusalLines(session));
        if (lines.Count > 0 && WatchTip(session) is { } tip) lines.Add(tip);
        return lines;
    }

    /// <summary>Thumbs a reaction stays news for an agent that wasn't connected when it came.</summary>
    private static readonly TimeSpan AnswersKeptFor = TimeSpan.FromDays(3);

    /// <summary>
    /// 👍 and 👎 others gave the agent's messages, each once: a yes or no to what it asked or said, so nobody has to type it.
    /// "iztok answered yes (👍) to your message “Shall I ship 1.4?” in #grid2vr".
    /// </summary>
    private List<string> AnswerLines(McpSession session) => [.. AnswerNews(session).Select(a => a.Text)];

    /// <summary>
    /// What others answered to the agent's messages, each once: a person's decision on what it asked to approve
    /// (ask_approval, #848d7643), and 👍 or 👎 on its other messages, which are agreement or disagreement with what they say,
    /// not a go-ahead for anything.
    /// </summary>
    private List<(bool Approval, string Text)> AnswerNews(McpSession session)
    {
        var news = new List<(bool, string)>();
        foreach (var (channel, message, decision, key) in NewDecisions(session).ToList())
            if (session.Noticed.Add(key))
                news.Add((true, decision.Approved
                    ? $"{decision.By} approved{(message.WithPin ? " with their PIN" : "")} what you asked in {Display(channel, session)} (id {ShortId(message.Id)}): “{message.Approval}”. Go ahead with that, and only that."
                    : $"{decision.By} declined what you asked in {Display(channel, session)} (id {ShortId(message.Id)}): “{message.Approval}”. Don't do it; ask them what they'd like instead if it isn't clear."));
        foreach (var a in NewAnswers(session).ToList())
            if (session.Noticed.Add(a.Key))
                news.Add((false, $"{a.Reaction.By} gave {(a.Reaction.Up ? "👍" : "👎")} to your message “{Short(OneLine(a.Message.Text))}” in {Display(a.Channel, session)}" +
                                 $" (id {ShortId(a.Message.Id)}): {(a.Reaction.Up ? "agreement" : "disagreement")} with what it says, not a go-ahead for anything else" +
                                 " (ask for one with ask_approval)."));
        return news;
    }

    /// <summary>Decisions on what the agent asked to approve that it hasn't heard about yet.</summary>
    private IEnumerable<(ChatChannel Channel, ChatMessage Message, ChatDecision Decision, string Key)> NewDecisions(McpSession session)
    {
        var since = DateTime.Now - AnswersKeptFor;
        foreach (var channel in Chat.Visible(ActorOf(session)))
            foreach (var m in channel.Messages)
                if (m.Decision is { } decision && decision.At > since && string.Equals(m.By, session.AgentName, StringComparison.OrdinalIgnoreCase) &&
                    $"approval:{m.Id}" is var key && !session.Noticed.Contains(key))
                    yield return (channel, m, decision, key);
    }

    /// <summary>👍 and 👎 on the agent's messages that it hasn't heard about yet (in one channel, or in all it sees).</summary>
    private IEnumerable<(ChatChannel Channel, ChatMessage Message, ChatReaction Reaction, string Key)> NewAnswers(McpSession session, ChatChannel? only = null)
    {
        var since = DateTime.Now - AnswersKeptFor;
        foreach (var channel in Chat.Visible(ActorOf(session)).Where(c => only == null || c == only))
            foreach (var m in channel.Messages)
            {
                if (m.Reactions == null || m.Approval != null || !string.Equals(m.By, session.AgentName, StringComparison.OrdinalIgnoreCase)) continue;
                foreach (var r in m.Reactions.Where(r => r.At > since))
                    if ($"answer:{m.Id}:{r.By}:{r.Up}:{r.At.Ticks}" is var key && !session.Noticed.Contains(key)) yield return (channel, m, r, key);
            }
    }

    private List<string> LocalChatLines(McpSession session)
    {
        var answers = AnswerLines(session);
        var lines = LocalNewsLines(session);
        lines.AddRange(ClaimLines(session));
        return answers.Count == 0 ? lines : [.. lines, "Chat answers: " + string.Join(" ", answers)];
    }

    private List<string> LocalNewsLines(McpSession session)
    {
        if (Chat.Channels.Count == 0) return [];
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
            var mentions = messages.Count(w => ChatRoom.Mentions(w.Channel, w.Message, session.AgentName));
            asked |= mentions > 0;
            if (group.Key.Direct)
            {
                // Everything in a private conversation is for it.
                asked = true;
                parts.Add($"{Count(messages.Count, "private message")} from {ChatRoom.Title(group.Key, session.AgentName)} (read_channel @{ChatRoom.Title(group.Key, session.AgentName)})");
                continue;
            }
            parts.Add(ChatRoom.Member(group.Key, session.AgentName) == null
                // @mentioned in a channel it isn't in: it joins to read it.
                ? $"{Count(messages.Count, "message")} mentioning you in #{group.Key.Name}, which you're not in (join_channel #{group.Key.Name}, then read_channel)"
                : $"{Count(messages.Count, "new message")} in #{group.Key.Name}" +
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
            ["name"] = Display(channel, session),
            ["private"] = channel.Direct ? true : null,
            ["topic"] = channel.Topic,
            ["access"] = channel.Access == ChannelAccess.Open ? "open" : "invite_only",
            ["linkedTo"] = LinkText(channel.Link),
            ["member"] = member != null,
            ["moderator"] = ChatRoom.IsModerator(channel, ActorOf(session)) ? true : null,
            ["mutedUntil"] = Chat.MutedUntil(channel, name) is { } until ? (until == DateTime.MaxValue ? "until unmuted" : Time(until)) : null,
            ["members"] = channel.Members.Count,
            ["unread"] = unread.Count > 0 ? unread.Count : null,
            ["mentionsYou"] = unread.Any(m => ChatRoom.Mentions(channel, m, name)) ? true : null,
            ["lastMessage"] = channel.Messages.LastOrDefault(m => !m.Notice) is { } last ? Time(last.At) : null,
            ["startedBy"] = channel.CreatedBy,
        };
    }

    private JsonObject MessageJson(ChatChannel channel, ChatMessage m, string you)
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
            ["answering"] = !m.Agent && ChatRoom.AnswererOf(channel, m, DateTime.Now) is { } answerer ? answerer : null,
            ["answered"] = m.AnsweredAt != null ? true : null,
            ["replyTo"] = m.ReplyTo is { } r ? ShortId(r) : null,
            ["edited"] = m.EditedAt != null && !m.Deleted ? true : null,
            ["mentionsYou"] = ChatRoom.Mentions(channel, m, you) ? true : null,
            ["yes"] = m.Reactions?.Any(r => r.Up) == true ? new JsonArray(m.Reactions.Where(r => r.Up).Select(r => (JsonNode)r.By).ToArray()) : null,
            ["no"] = m.Reactions?.Any(r => !r.Up) == true ? new JsonArray(m.Reactions.Where(r => !r.Up).Select(r => (JsonNode)r.By).ToArray()) : null,
            ["asksApproval"] = m.Approval,
            ["approval"] = m.Approval == null ? null : m.Decision is { } d ? (d.Approved ? "approved by " : "declined by ") + d.By : "waiting",
            ["withPin"] = m.WithPin ? true : null,
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

    /// <summary>"#grid2vr-hud", or "@Tester" for the agent's private conversation with Tester.</summary>
    private static string Display(ChatChannel channel, McpSession session) =>
        channel.Direct ? "@" + ChatRoom.Title(channel, session.AgentName) : "#" + channel.Name;

    private static string Count(int n, string what) => n == 1 ? $"1 {what}" : $"{n} {what}s";

    /// <summary>An agent's name (connected now, or seen in a channel or in the history); otherwise a person.</summary>
    private bool IsAgentName(string name)
    {
        if (Chat.IsOwner(name)) return false;
        lock (_live)
            if (_live.Any(s => string.Equals(s.AgentName, name, StringComparison.OrdinalIgnoreCase))) return true;
        // An agent connected to another ProJict that's connected here ("Hermes (via alice)").
        if (_main.Agents.RemoteAgents().Any(a => string.Equals(a.Name, name, StringComparison.OrdinalIgnoreCase))) return true;
        return Chat.Channels.SelectMany(c => c.Members).Any(m => m.Agent && string.Equals(m.Name, name, StringComparison.OrdinalIgnoreCase)) ||
               _state.Projects.Any(p => p.Walk().Any(l => (l.Item.Activity ?? []).Any(a => a.Agent && string.Equals(a.By, name, StringComparison.OrdinalIgnoreCase))));
    }

    /// <summary>Names the user can invite: agents connected now first, then the ones seen before (in channels or the history).</summary>
    public IReadOnlyList<string> KnownAgentNames()
    {
        List<string> live;
        lock (_live) live = _live.Select(s => s.AgentName).Distinct(StringComparer.OrdinalIgnoreCase).Order(StringComparer.CurrentCultureIgnoreCase).ToList();
        // Agents connected through the people connected here count as connected too.
        live = [.. live, .. _main.Agents.RemoteAgents().Select(a => a.Name).Where(n => !live.Contains(n, StringComparer.OrdinalIgnoreCase))];
        var earlier = Chat.Channels.SelectMany(c => c.Members).Where(m => m.Agent).Select(m => m.Name)
            .Concat(_state.Projects.SelectMany(p => p.Walk()).SelectMany(l => l.Item.Activity ?? []).Where(a => a.Agent).Select(a => AgentNaming.Identity(a.By)))
            .Distinct(StringComparer.OrdinalIgnoreCase).Where(n => !live.Contains(n, StringComparer.OrdinalIgnoreCase))
            .Order(StringComparer.CurrentCultureIgnoreCase);
        return [.. live, .. earlier];
    }
}
