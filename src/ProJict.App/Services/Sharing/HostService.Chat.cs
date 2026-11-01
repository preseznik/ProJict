using System.IO;
using System.Text.Json.Nodes;
using System.Windows.Threading;
using ProJict.Core.Agents;
using ProJict.Core.Chat;
using ProJict.Core.Model;
using ProJict.Core.Sharing;
using ProJict.Core.Storage;

namespace ProJict.App.Services.Sharing;

/// <summary>
/// Chat for guests. Each guest gets the channels it may see (Open ones about the projects shared with it, and the ones
/// it's in), kept up to date; what it does in them (writing, reading, joining, moderating) comes back as a request the
/// host checks against its role and the channel's rules. Everything stays with the host.
/// </summary>
public sealed partial class HostService
{
    private static readonly TimeSpan ChatDelay = TimeSpan.FromMilliseconds(150);
    private readonly Dictionary<Guid, DispatcherTimer> _chatTimers = [];

    /// <summary>The guest in Chat: a person, who reaches the projects shared with it.</summary>
    private ChatActor ChatActorOf(GuestAccount account) =>
        new(account.Name, false, Projects: Visible(account).Select(p => p.Id).ToHashSet(), Guest: true);

    private static JsonNode ChannelJson(ChatChannel channel) => JsonNode.Parse(Json.Serialize(ChatRoom.ForGuest(channel)))!;

    /// <summary>All the channels the guest may see, replacing what it had (on connecting).</summary>
    private JsonObject ChatSnapshot(GuestSession session)
    {
        var actor = ChatActorOf(session.Account);
        var channels = _state.Chat.Visible(actor).ToList();
        session.ChatSent.Clear();
        foreach (var channel in channels) session.ChatSent.Add(channel.Id);
        return Wire.Message("chat", ("name", session.Account.Name), ("channels", new JsonArray(channels.Select(ChannelJson).ToArray())));
    }

    private void OnChatChanged(ChatChannel channel)
    {
        if (_sessions.Count == 0) return;
        if (!_chatTimers.TryGetValue(channel.Id, out var timer))
        {
            timer = new DispatcherTimer(DispatcherPriority.Background, _dispatcher) { Interval = ChatDelay };
            timer.Tick += (_, _) =>
            {
                timer.Stop();
                SendChannel(channel);
            };
            _chatTimers[channel.Id] = timer;
        }
        timer.Stop();
        timer.Start();
    }

    /// <summary>The channel to every guest that may see it; ones that no longer may lose it.</summary>
    private void SendChannel(ChatChannel channel)
    {
        var gone = !_state.Chat.Channels.Contains(channel);
        foreach (var session in _sessions)
        {
            if (!gone && ChatRoom.CanSee(channel, ChatActorOf(session.Account)))
            {
                session.ChatSent.Add(channel.Id);
                session.Send(Wire.Message("chatChannel", ("channel", ChannelJson(channel))));
            }
            else if (session.ChatSent.Remove(channel.Id))
                session.Send(Wire.Message("chatGone", ("id", channel.Id.ToString())));
        }
    }

    /// <summary>Projects moved between groups: guests get the channels they may now see and lose the others.</summary>
    private void SyncChat()
    {
        foreach (var session in _sessions)
        {
            var actor = ChatActorOf(session.Account);
            var visible = _state.Chat.Visible(actor).ToList();
            foreach (var channel in visible.Where(c => !session.ChatSent.Contains(c.Id)))
            {
                session.ChatSent.Add(channel.Id);
                session.Send(Wire.Message("chatChannel", ("channel", ChannelJson(channel))));
            }
            foreach (var id in session.ChatSent.Except(visible.Select(c => c.Id)).ToList())
            {
                session.ChatSent.Remove(id);
                session.Send(Wire.Message("chatGone", ("id", id.ToString())));
            }
        }
    }

    /// <summary>
    /// Something a guest does in Chat. Viewers only read; commenters and up write, join, leave and write privately;
    /// moderating follows the channel's rules (a guest moderates what it started); only editors start channels, about
    /// projects shared with them. Returns why not, or null (the change itself goes out to everyone who sees it).
    /// </summary>
    private JsonObject? ChatOp(GuestSession session, JsonObject message)
    {
        var account = session.Account;
        var actor = ChatActorOf(account);
        // What the guest may see decides; its agent acts as "Ada (via Alice)".
        var seeing = actor;
        var room = _state.Chat;
        var op = (string?)message["op"] ?? "";
        string? Text(string name) => (string?)message[name];
        JsonObject Refuse(string why) => Wire.Message("chatError", ("message", why));
        if (Text("as") is { } agentName && agentName.Trim().Length > 0)
        {
            if (!Data.GuestAgents) return Refuse("This ProJict doesn't let guests' AI agents take part.");
            if (op is not ("post" or "read" or "edit" or "delete" or "join" or "leave")) return Refuse("Guests' AI agents read and write in channels; the rest is for people.");
            var agent = (AgentNaming.WithoutVia(agentName.Trim(), account.Name) ?? agentName).Trim();
            if (agent.Length is 0 or > 40 || agent.Contains(" (via ", StringComparison.Ordinal)) return Refuse("That isn't an agent's name.");
            actor = new ChatActor(AgentNaming.Via(agent, account.Name), true, Projects: actor.Projects, Guest: true);
        }
        try
        {
            if (op == "create")
            {
                if (account.Role != GuestRole.Editor) return Refuse("Only editors start channels.");
                if (!Guid.TryParse(Text("projectId"), out var projectId) || Visible(account).All(p => p.Id != projectId))
                    return Refuse("Channels you start are about a project shared with you.");
                var link = new ChannelLink
                {
                    ProjectId = projectId,
                    CategoryId = Guid.TryParse(Text("categoryId"), out var c) ? c : null,
                    ItemId = Guid.TryParse(Text("itemId"), out var i) ? i : null,
                };
                room.Create(actor, Text("name") ?? "", (bool?)message["inviteOnly"] == true ? ChannelAccess.InviteOnly : ChannelAccess.Open, link, Text("topic"));
                return null;
            }
            if (op == "direct")
            {
                if (!GuestAccess.CanComment(account.Role)) return Refuse("As a viewer you can read, not write.");
                var other = (Text("with") ?? "").Trim();
                var agent = _main.AgentTools.KnownAgentNames().Contains(other, StringComparer.OrdinalIgnoreCase);
                var person = room.IsOwner(other) || Data.Guests.Any(g => !g.Blocked && string.Equals(g.Name, other, StringComparison.OrdinalIgnoreCase));
                if (!agent && !person) return Refuse($"There's nobody called {other} here.");
                room.OpenDirect(actor, other, agent && !room.IsOwner(other));
                return null;
            }

            if (!Guid.TryParse(Text("channel"), out var id) || room.Channels.FirstOrDefault(c => c.Id == id) is not { } channel || !ChatRoom.CanSee(channel, seeing))
                return Refuse("That channel isn't there any more.");
            if (op == "read")
            {
                if (DateTime.TryParse(Text("upTo"), null, System.Globalization.DateTimeStyles.RoundtripKind, out var upTo))
                {
                    if (ChatRoom.Member(channel, actor.Name) != null) room.MarkRead(channel, actor.Name, upTo);
                    else if (actor.Agent) room.MarkMentionsRead(channel, actor.Name, upTo);
                }
                return null;
            }
            if (!GuestAccess.CanComment(account.Role)) return Refuse("As a viewer you can read, not write.");
            Guid? messageId = Guid.TryParse(Text("message"), out var m) ? m : null;
            switch (op)
            {
                case "post":
                    var files = new List<Attachment>();
                    foreach (var f in message["files"] as JsonArray ?? [])
                    {
                        if (!Guid.TryParse((string?)f?["id"], out var fileId)) continue;
                        var attachment = new Attachment { Id = fileId, FileName = ProjectStore.SafeFileName((string?)f!["name"] ?? "file"), Size = (long?)f["size"] ?? 0 };
                        // Only files it sent first (putFile), never someone else's.
                        if (File.Exists(_state.Store.AttachmentPath(attachment))) files.Add(attachment);
                    }
                    room.Post(channel, actor, Text("text") ?? "", Guid.TryParse(Text("replyTo"), out var r) ? r : null, files);
                    break;
                case "edit": room.Edit(channel, actor, messageId ?? Guid.Empty, Text("text") ?? ""); break;
                case "delete": room.DeleteMessage(channel, actor, messageId ?? Guid.Empty); break;
                case "join": room.Join(channel, actor); break;
                case "leave": room.Leave(channel, actor); break;
                case "invite":
                    var who = (Text("who") ?? "").Trim();
                    room.Invite(channel, actor, who, _main.AgentTools.KnownAgentNames().Contains(who, StringComparer.OrdinalIgnoreCase) && !room.IsOwner(who));
                    break;
                case "kick": room.Kick(channel, actor, Text("who") ?? ""); break;
                case "mute":
                    room.Mute(channel, actor, Text("who") ?? "", (int?)message["minutes"] is > 0 and var minutes ? TimeSpan.FromMinutes(minutes) : null);
                    break;
                case "unmute": room.Unmute(channel, actor, Text("who") ?? ""); break;
                case "moderator": room.SetModerator(channel, actor, Text("who") ?? "", (bool?)message["on"] == true); break;
                case "topic": room.SetTopic(channel, actor, Text("topic")); break;
                case "access": room.SetAccess(channel, actor, (bool?)message["inviteOnly"] == true ? ChannelAccess.InviteOnly : ChannelAccess.Open); break;
                default: return Refuse("ProJict here doesn't know that.");
            }
            return null;
        }
        catch (ChatException ex)
        {
            return Refuse(ex.Message);
        }
    }

    /// <summary>A file in a chat message the guest may see.</summary>
    private Attachment? ChatAttachment(GuestSession session, Guid id)
    {
        var actor = ChatActorOf(session.Account);
        return _state.Chat.Visible(actor).SelectMany(c => c.Messages).SelectMany(m => m.Attachments ?? []).FirstOrDefault(a => a.Id == id);
    }
}
