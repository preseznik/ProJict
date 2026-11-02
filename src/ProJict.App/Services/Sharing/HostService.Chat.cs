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

    /// <summary>The guest in Chat: a person, who reaches the projects shared with it and never sees your private ones.</summary>
    /// <summary>
    /// How Chat sees a guest: in the channels about projects shared with them and the ones they're invited to. A ProJict
    /// linked as you (and its agents) also takes part in the Open channels about nothing in particular, as you and your
    /// agents do here; private projects' channels stay hidden either way.
    /// </summary>
    private ChatActor ChatActorOf(GuestAccount account) =>
        new(NameOf(account), false, Projects: Visible(account).Select(p => p.Id).ToHashSet(), Guest: !account.Self,
            Hidden: _state.Projects.Where(p => p.Private).Select(p => p.Id).ToHashSet());

    private static JsonNode ChannelJson(ChatChannel channel) => JsonNode.Parse(Json.Serialize(ChatRoom.ForGuest(channel)))!;

    /// <summary>All the channels the guest may see, replacing what it had (on connecting).</summary>
    private JsonObject ChatSnapshot(GuestSession session)
    {
        var actor = ChatActorOf(session.Account);
        var channels = _state.Chat.Visible(actor).ToList();
        session.ChatSent.Clear();
        foreach (var channel in channels) session.ChatSent.Add(channel.Id);
        return Wire.Message("chat", ("name", NameOf(session.Account)), ("channels", new JsonArray(channels.Select(ChannelJson).ToArray())));
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

    /// <summary>
    /// Who's online or away here (you, your agents, and the people connected with their agents), to each guest when it
    /// changed, so their Chat shows the members of your channels as they are; whoever isn't said is offline.
    /// </summary>
    public void SendPresence()
    {
        if (_sessions.Count == 0) return;
        var everyone = _main.Agents.Everyone();
        // Which of them are agents (yours, and those of everyone connected), so guests can show and invite them as agents.
        var agents = _main.Agents.AgentsHere().Keys.Concat(_main.Agents.RemoteAgents().Select(a => a.Name))
            .Distinct(StringComparer.OrdinalIgnoreCase).Order(StringComparer.OrdinalIgnoreCase).ToList();
        var signature = Presence.Signature(everyone) + "|" + string.Join("|", agents);
        foreach (var session in _sessions.Where(s => s.PresenceSent != signature))
        {
            session.PresenceSent = signature;
            session.Send(Wire.Message("presence", ("people", Presence.ToJson(everyone)), ("agents", new JsonArray(agents.Select(a => (JsonNode)a).ToArray()))));
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
        // Whose it was, so a guest's agent hears why (older guests show it in their status bar).
        JsonObject Refuse(string why) => Wire.Message("chatError", ("message", why), ("as", Text("as")));
        if (Text("as") is { } agentName && agentName.Trim().Length > 0)
        {
            if (!account.Self && !Data.GuestAgents) return Refuse("This ProJict doesn't let guests' AI agents take part.");
            if (op is not ("post" or "read" or "edit" or "delete" or "join" or "leave")) return Refuse("Guests' AI agents read and write in channels; the rest is for people.");
            var agent = (AgentNaming.WithoutVia(agentName.Trim(), account.Name) ?? agentName).Trim();
            if (agent.Length is 0 or > 40 || agent.Contains(" (via ", StringComparison.Ordinal) ||
                (account.Self && string.Equals(agent, _state.UserName, StringComparison.OrdinalIgnoreCase))) return Refuse("That isn't an agent's name.");
            actor = actor with { Name = account.Self ? agent : AgentNaming.Via(agent, account.Name), Agent = true };
        }
        try
        {
            if (op == "create")
            {
                if (RoleOf(account) != GuestRole.Editor) return Refuse("Only editors start channels.");
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
                if (!GuestAccess.CanComment(RoleOf(account))) return Refuse("As a viewer you can read, not write.");
                var other = (Text("with") ?? "").Trim();
                var agent = _main.AgentTools.KnownAgentNames().Contains(other, StringComparer.OrdinalIgnoreCase);
                var person = room.IsOwner(other) || Data.Guests.Any(g => !g.Blocked && string.Equals(g.Name, other, StringComparison.OrdinalIgnoreCase));
                if (!agent && !person) return Refuse($"There's nobody called {other} here.");
                room.OpenDirect(actor, other, agent && !room.IsOwner(other));
                return null;
            }

            if (!Guid.TryParse(Text("channel"), out var id) || room.Channels.FirstOrDefault(c => c.Id == id) is not { } channel || !ChatRoom.CanSee(channel, seeing))
                return Refuse("That channel isn't there any more.");
            RemoteChatOps.OnChannel(room, channel, actor, message, GuestAccess.CanComment(RoleOf(account)),
                who => _main.AgentTools.KnownAgentNames().Contains(who, StringComparer.OrdinalIgnoreCase), () =>
                {
                    var files = new List<Attachment>();
                    foreach (var f in message["files"] as JsonArray ?? [])
                    {
                        if (!Guid.TryParse((string?)f?["id"], out var fileId)) continue;
                        var attachment = new Attachment { Id = fileId, FileName = ProjectStore.SafeFileName((string?)f!["name"] ?? "file"), Size = (long?)f["size"] ?? 0 };
                        // Only files it sent first (putFile), never someone else's.
                        if (File.Exists(_state.Store.AttachmentPath(attachment))) files.Add(attachment);
                    }
                    return files;
                });
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
