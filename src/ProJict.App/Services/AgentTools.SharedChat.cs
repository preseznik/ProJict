using System.Globalization;
using System.Text.Json.Nodes;
using ProJict.App.Services.Sharing;
using ProJict.App.ViewModels;
using ProJict.Core.Agents;
using ProJict.Core.Chat;
using ProJict.Core.Sharing;

namespace ProJict.App.Services;

/// <summary>
/// Chat in other ProJicts this one is connected to: an agent here reads the channels this ProJict's user can see there
/// ("#hud (Sam's)"), and posts in them as "Ada (via Alice)" when that ProJict lets guests' agents take part and Alice's
/// role there allows writing. Messages for it there show in waitingForYou and its chat watcher like its own.
/// </summary>
public sealed partial class AgentTools
{
    /// <summary>The other ProJicts whose channels agents here can reach (not an API token limited to some projects).</summary>
    private IEnumerable<HostConnection> ChatHosts =>
        _main.Guest.Connections.Where(c => c.State == ConnectionState.Connected && c.Chat != null);

    private static IEnumerable<HostConnection> ReachableHosts(McpSession session, IEnumerable<HostConnection> hosts) =>
        session.Projects != null ? [] : hosts;

    /// <summary>"#hud (Sam's)".</summary>
    private static string SharedName(HostConnection host, ChatChannel channel) => $"#{channel.Name} ({host.HostName}'s)";

    /// <summary>How the agent goes by in that ProJict's channels: "Ada (via Alice)", or "Ada" where this computer is its user.</summary>
    private string ViaIn(McpSession session, HostConnection host) =>
        host.IsSelf ? session.AgentName : AgentNaming.Via(session.AgentName, host.MyName ?? _state.UserName);

    /// <summary>The other ProJict's channel a "#hud (Sam's)" argument names, or null for one of this ProJict's.</summary>
    private (HostConnection Host, ChatChannel Channel)? SharedChannelFor(McpSession session, Args args, string argument = "channel")
    {
        var key = args.String(argument)?.Trim();
        if (key == null || !key.EndsWith("'s)", StringComparison.Ordinal) || key.LastIndexOf(" (", StringComparison.Ordinal) is not (> 0 and var open)) return null;
        var name = key[..open].Trim().TrimStart('#');
        var hostName = key[(open + 2)..^3];
        if (session.Projects != null)
            throw new McpToolException("This API token reaches only some projects, so other ProJicts' channels are outside it.", ToolProblem.Forbidden);
        var host = ChatHosts.FirstOrDefault(h => string.Equals(h.HostName, hostName, StringComparison.OrdinalIgnoreCase))
            ?? throw new McpToolException($"This ProJict isn't connected to {hostName}'s right now; list_channels shows the channels you can reach.", ToolProblem.NotFound);
        var channel = host.Chat!.Channels.FirstOrDefault(c => !c.Direct && string.Equals(c.Name, name, StringComparison.OrdinalIgnoreCase))
            ?? throw new McpToolException($"No channel #{name} in {hostName}'s ProJict that you can see; list_channels shows them.", ToolProblem.NotFound);
        return (host, channel);
    }

    private JsonObject SharedChannelJson(HostConnection host, ChatChannel channel, McpSession session)
    {
        var me = ViaIn(session, host);
        var member = ChatRoom.Member(channel, me);
        var unread = member != null ? ChatRoom.Unread(channel, me).ToList() : [];
        return new JsonObject
        {
            ["name"] = SharedName(host, channel),
            ["sharedBy"] = $"{host.HostName}'s ProJict: you read here, and post as {me}" + (host.AgentsAllowed && GuestAccess.CanComment(host.Role) ? "" : " (not now: " +
                (!host.AgentsAllowed ? $"{host.HostName} doesn't let guests' agents take part" : $"{host.MyName} is a viewer there") + ")"),
            ["topic"] = channel.Topic,
            ["access"] = channel.Access == ChannelAccess.Open ? "open" : "invite_only",
            ["member"] = member != null,
            ["join"] = member != null ? null : JoinProblem(host, channel, me) is { } problem ? problem : "join_channel",
            ["members"] = channel.Members.Count,
            ["unread"] = unread.Count > 0 ? unread.Count : null,
            ["mentionsYou"] = unread.Any(m => ChatRoom.Mentions(channel, m, me)) ? true : null,
            ["lastMessage"] = channel.Messages.LastOrDefault(m => !m.Notice) is { } last ? Time(last.At) : null,
            ["startedBy"] = channel.CreatedBy,
        };
    }

    /// <summary>Why the agent can't simply join that ProJict's channel (it decides; this says it before asking), or null.</summary>
    private static string? JoinProblem(HostConnection host, ChatChannel channel, string me) =>
        channel.Access == ChannelAccess.InviteOnly
            ? $"{SharedName(host, channel)} is invite only: ask {host.HostName} or one of its moderators to invite you (as {me})."
            : !host.IsSelf && channel.Link == null
                ? $"{SharedName(host, channel)} isn't about a project, so in {host.HostName}'s ProJict only those invited take part in it: ask {host.HostName} to invite " +
                  $"you (as {me}). If that ProJict is your user's own on another computer, they can link this one as them there (Sharing › This is me…), and its " +
                  "agents then join like theirs."
                : null;

    private IEnumerable<JsonNode> SharedChannelsJson(McpSession session, Core.Model.Project? only) =>
        ReachableHosts(session, ChatHosts)
            .SelectMany(h => h.Chat!.Channels.Where(c => !c.Direct && (only == null || c.Link?.ProjectId == only.Id)).Select(c => (Host: h, Channel: c)))
            .OrderByDescending(x => x.Channel.Messages.LastOrDefault()?.At ?? x.Channel.CreatedAt)
            .Select(x => (JsonNode)SharedChannelJson(x.Host, x.Channel, session));

    private string SharedRead(McpSession session, HostConnection host, ChatChannel channel, Args args)
    {
        if (ChatRoom.Member(channel, ViaIn(session, host)) == null)
            throw new McpToolException($"You're not in {SharedName(host, channel)}: join it first with join_channel, then read_channel shows its messages.", ToolProblem.Forbidden);
        // The same little at a time as this ProJict's own channels (#35c53efa).
        ChatMessage Find(string id) =>
            ChatRoom.FindMessage(channel, id.Trim()) ?? throw new McpToolException($"No message {id} in {SharedName(host, channel)}.", ToolProblem.NotFound);
        var full = false;
        List<ChatMessage> shown;
        if (args.String("id") is { } one)
        {
            shown = [Find(one)];
            full = true;
        }
        else if (args.String("around") is { } around) shown = ChatCatchUp.Around(channel.Messages, Find(around));
        else
        {
            var limit = Math.Clamp(args.Int("limit") ?? ChatCatchUp.DefaultRead, 1, ChatCatchUp.MaxRead);
            IEnumerable<ChatMessage> messages = channel.Messages;
            if (args.String("before") is { } before)
            {
                var from = Find(before);
                messages = messages.TakeWhile(m => m != from);
            }
            shown = ChatCatchUp.Latest(messages.ToList(), limit, m => (m.Deleted ? 0 : Math.Min(m.Text.Length, ChatCatchUp.LongStart + 80)) + 120);
        }
        var me = ViaIn(session, host);
        var json = SharedChannelJson(host, channel, session);
        json["members"] = new JsonArray(channel.Members.Select(m => (JsonNode)WithoutEmpty(new JsonObject { ["name"] = m.Name, ["agent"] = m.Agent ? true : null })).ToArray());
        json["messages"] = new JsonArray(shown.Select(m => (JsonNode)MessageJson(channel, m, me, full, SharedName(host, channel))).ToArray());
        if (!full && shown.Count > 0 && shown[0] != channel.Messages[0]) json["more"] = $"Earlier messages: read_channel with before={ShortId(shown[0].Id)}.";
        SharedDelivered(session, host, channel, shown);
        return Json(json);
    }

    /// <summary>Joins (or leaves) another ProJict's channel, through the guest's connection; the host says so in the channel.</summary>
    private string SharedJoin(McpSession session, HostConnection host, ChatChannel channel, bool leave)
    {
        if (!host.AgentsAllowed)
            throw new McpToolException($"{host.HostName} doesn't let guests' AI agents take part (or runs an older ProJict), so you can't join {SharedName(host, channel)}.", ToolProblem.Forbidden);
        if (!GuestAccess.CanComment(host.Role))
            throw new McpToolException($"You take part in {host.HostName}'s ProJict through {host.MyName}, who is a viewer there, so you can't join its channels.", ToolProblem.Forbidden);
        if (!leave && ChatRoom.Member(channel, ViaIn(session, host)) == null && JoinProblem(host, channel, ViaIn(session, host)) is { } problem)
            throw new McpToolException(problem, ToolProblem.Forbidden);
        _main.Guest.ChatOp(host, leave ? "leave" : "join", ("channel", channel.Id.ToString()), ("as", session.AgentName));
        return Json(new JsonObject
        {
            ["channel"] = SharedName(host, channel),
            [leave ? "left" : "joined"] = true,
            ["as"] = ViaIn(session, host),
            ["note"] = $"Sent to {host.HostName}'s ProJict; read_channel shows its messages once you're in. If it says no, you hear why in waitingForYou.",
        });
    }

    private string SharedPost(McpSession session, HostConnection host, ChatChannel channel, Args args, List<Core.Model.Attachment> files)
    {
        if (!host.AgentsAllowed)
            throw new McpToolException($"{host.HostName} doesn't let guests' AI agents take part (or runs an older ProJict): you can read {SharedName(host, channel)}, not post.", ToolProblem.Forbidden);
        if (!GuestAccess.CanComment(host.Role))
            throw new McpToolException($"You take part in {host.HostName}'s ProJict through {host.MyName}, who is a viewer there: you can read, not post.", ToolProblem.Forbidden);
        var text = (args.String("text") ?? "").Trim();
        if (text.Length == 0) throw new McpToolException("The message is empty.");
        if (text.Length > ChatRoom.MaxText) throw new McpToolException($"Messages can be up to {ChatRoom.MaxText} characters.");
        Guid? replyTo = args.String("reply_to") is { } r
            ? (ChatRoom.FindMessage(channel, r.Trim()) ?? throw new McpToolException($"No message {r} in {SharedName(host, channel)}.", ToolProblem.NotFound)).Id
            : null;
        if (files.FirstOrDefault(f => f.Size > WireFiles.LimitFor(host.TakesPieces)) is { } big)
            throw new McpToolException($"{big.FileName} is {AttachmentViewModel.FormatSize(big.Size)}: {host.HostName}'s ProJict takes files up to " +
                                       $"{AttachmentViewModel.FormatSize(WireFiles.LimitFor(host.TakesPieces))} (it's an older one). Send a smaller file, or say where it is.");
        _main.Guest.ChatPost(host, channel, text, replyTo, files.Select(f => new OutgoingFile(f.FileName, Path: _state.Store.AttachmentPath(f))).ToList(), session.AgentName);
        var result = new JsonObject
        {
            ["channel"] = SharedName(host, channel),
            ["sent"] = true,
            ["as"] = ViaIn(session, host),
            ["note"] = $"Sent to {host.HostName}'s ProJict; it shows in read_channel once it's there" + (files.Count > 0 ? " (a big file takes a while to get there)." : "."),
        };
        if (IsWallOfText(text))
            result["note"] += " That was long for a chat channel: keep messages short and conversational, and put details in the item's discussion.";
        return Json(result);
    }

    /// <summary>
    /// What waits for the agent in other ProJicts' channels: what it hasn't read in the ones it's in, and mentions of it
    /// ("@Ada" or "@Ada (via Alice)") since it connected in the others; each once.
    /// </summary>
    private List<(HostConnection Host, ChatChannel Channel, ChatMessage Message)> SharedWaiting(McpSession session)
    {
        var waiting = new List<(HostConnection, ChatChannel, ChatMessage)>();
        foreach (var host in ReachableHosts(session, ChatHosts).Where(h => h.AgentsAllowed))
        {
            var me = ViaIn(session, host);
            foreach (var channel in host.Chat!.Channels.Where(c => !c.Direct))
            {
                var messages = ChatRoom.Member(channel, me) != null
                    ? ChatRoom.Unread(channel, me)
                    : channel.Messages.Where(m => !m.Notice && !m.Deleted && m.At >= session.ConnectedAt && !string.Equals(m.By, me, StringComparison.OrdinalIgnoreCase) &&
                                                  ChatText.Mentions(m.Text, me));
                waiting.AddRange(messages.Where(m => !session.Noticed.Contains($"chat:{m.Id}")).Select(m => (host, channel, m)));
            }
        }
        return waiting;
    }

    /// <summary>The agent has these: noticed here, and read in that ProJict's channel when it's in it.</summary>
    private void SharedDelivered(McpSession session, HostConnection host, ChatChannel channel, List<ChatMessage> messages)
    {
        if (messages.Count == 0) return;
        foreach (var m in messages) session.Noticed.Add($"chat:{m.Id}");
        if (ChatRoom.Member(channel, ViaIn(session, host)) != null)
            _main.Guest.ChatOp(host, "read", ("channel", channel.Id.ToString()), ("upTo", messages.Max(m => m.At).ToString("O", CultureInfo.InvariantCulture)),
                ("as", session.AgentName));
    }

    /// <summary>For waitingForYou: news in other ProJicts' channels, in one line.</summary>
    private string? SharedChatLine(McpSession session)
    {
        var fresh = SharedWaiting(session);
        if (fresh.Count == 0) return null;
        foreach (var (_, _, message) in fresh) session.Noticed.Add($"chat:{message.Id}");
        var parts = fresh.GroupBy(w => (w.Host, w.Channel)).Select(g =>
        {
            var me = ViaIn(session, g.Key.Host);
            if (ChatRoom.Member(g.Key.Channel, me) == null)
                return $"{Count(g.Count(), "message")} mentioning you in {SharedName(g.Key.Host, g.Key.Channel)}, which you're not in (join_channel first)";
            var mentions = g.Count(w => ChatRoom.Mentions(w.Channel, w.Message, me));
            return $"{Count(g.Count(), "new message")} in {SharedName(g.Key.Host, g.Key.Channel)}" +
                   (mentions > 0 ? $" ({(mentions == 1 ? "one mentions" : $"{mentions} mention")} you)" : "");
        });
        return "Chat in another ProJict: " + string.Join("; ", parts) +
               ". Read them with read_channel (the channel as named here), and reply with post_message to the same channel.";
    }

    /// <summary>What other ProJicts refused that the agent sent them (joining, posting), each once.</summary>
    private List<string> SharedRefusalLines(McpSession session)
    {
        var lines = new List<string>();
        foreach (var host in ChatHosts)
            foreach (var refusal in host.AgentRefusals.Where(r => string.Equals(r.Agent, session.AgentName, StringComparison.OrdinalIgnoreCase)).ToList())
            {
                host.AgentRefusals.Remove(refusal);
                lines.Add($"{host.HostName}'s ProJict said no to what you sent it: {refusal.Why}");
            }
        return lines;
    }

    /// <summary>For a chat watcher: a line per message for the agent in other ProJicts' channels.</summary>
    private List<string> SharedWatchLines(McpSession identity)
    {
        var lines = new List<string>();
        foreach (var group in SharedWaiting(identity).GroupBy(w => (w.Host, w.Channel)))
        {
            var me = ViaIn(identity, group.Key.Host);
            var where = SharedName(group.Key.Host, group.Key.Channel);
            // A backlog comes as a catch-up, not a line per message (#35c53efa).
            var (shown, left) = ChatCatchUp.Pick([.. group.Select(w => (w.Channel, w.Message))],
                (c, m) => string.Equals(m.About, me, StringComparison.OrdinalIgnoreCase) || ChatRoom.Mentions(c, m, me));
            foreach (var l in left)
                lines.Add($"[{where}] {Count(l.Count, "older message")} ({Time(l.Oldest.At)} to {Time(l.Newest.At)}) left out, so your context " +
                          $"stays clear; they're marked read. read_channel \"{where}\" reads back through them.");
            foreach (var (_, m) in shown)
            {
                if (m.Notice)
                {
                    lines.Add($"[{where}] {OneLine(string.Equals(m.About, me, StringComparison.OrdinalIgnoreCase) ? m.AboutText ?? m.Text : m.Text)}");
                    continue;
                }
                var text = OneLine(m.Text);
                if (text.Length > WatchTextLength) text = text[..WatchTextLength].TrimEnd() + $"… (read_channel \"{where}\" for the rest)";
                var mention = ChatRoom.Mentions(group.Key.Channel, m, me) ? " (mentions you)" : "";
                lines.Add($"[{where}] {m.By}{mention}: {text} (id {ShortId(m.Id)}; reply: post_message channel=\"{where}\")");
            }
            SharedDelivered(identity, group.Key.Host, group.Key.Channel, group.Select(w => w.Message).ToList());
        }
        return lines;
    }

    /// <summary>Other ProJicts' chat rooms, so a watcher wakes when something arrives there.</summary>
    private IEnumerable<ChatRoom> SharedRooms(McpSession session) => ReachableHosts(session, ChatHosts).Select(h => h.Chat!);
}
