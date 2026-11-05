using System.Text.Json.Nodes;
using ProJict.Core.Chat;
using ProJict.Core.Model;

namespace ProJict.App.Services.Sharing;

/// <summary>
/// Another ProJict's Chat as it shows here: a host's, whose guest you are, or a guest's that shares its own channels
/// back (a ProJict linked as you, or one that ticked it). Chat shows both the same way, under the owner's name.
/// </summary>
public interface IChatRemote
{
    /// <summary>Whose ProJict it is: "iztok", "iztok4090".</summary>
    string OwnerName { get; }
    ChatRoom? Chat { get; }
    /// <summary>The name you write with there.</summary>
    string? MyName { get; }
    bool IsConnected { get; }
    /// <summary>You may write there, not just read.</summary>
    bool CanWrite { get; }
    /// <summary>You act there as its owner (a ProJict linked as you).</summary>
    bool AsOwner { get; }
    /// <summary>Who's online or away there; everyone else is offline.</summary>
    IReadOnlyDictionary<string, PresenceState> Presence { get; }
    /// <summary>Which of them are AI agents.</summary>
    IReadOnlySet<string> AgentsThere { get; }
}

/// <summary>
/// What someone in another ProJict does in one of your channels (writing, reading, joining, moderating), checked
/// against the channel's rules for whoever they act as here. Both ways of sharing use it: a host for its guests, and a
/// guest for the host it shares its channels back with. A refusal throws <see cref="ChatException"/> with why.
/// </summary>
internal static class RemoteChatOps
{
    public static void OnChannel(ChatRoom room, ChatChannel channel, ChatActor actor, JsonObject message, bool mayWrite,
        Func<string, bool> isAgent, Func<List<Attachment>> files)
    {
        var op = (string?)message["op"] ?? "";
        string? Text(string name) => (string?)message[name];
        if (op == "read")
        {
            if (DateTime.TryParse(Text("upTo"), null, System.Globalization.DateTimeStyles.RoundtripKind, out var upTo))
            {
                if (ChatRoom.Member(channel, actor.Name) != null) room.MarkRead(channel, actor.Name, upTo);
                else if (actor.Agent) room.MarkMentionsRead(channel, actor.Name, upTo);
            }
            return;
        }
        if (!mayWrite) throw new ChatException("As a viewer you can read, not write.");
        Guid? messageId = Guid.TryParse(Text("message"), out var m) ? m : null;
        switch (op)
        {
            case "post": room.Post(channel, actor, Text("text") ?? "", Guid.TryParse(Text("replyTo"), out var r) ? r : null, files()); break;
            case "edit": room.Edit(channel, actor, messageId ?? Guid.Empty, Text("text") ?? ""); break;
            case "react": room.React(channel, actor, messageId ?? Guid.Empty, (bool?)message["up"]); break;
            // Approve or Decline; "pin": the PIN was checked on the computer you clicked on (#8793323e), which only counts from yours.
            case "decide": room.Decide(channel, actor, messageId ?? Guid.Empty, (bool?)message["approve"] == true, (bool?)message["pin"] == true); break;
            case "delete": room.DeleteMessage(channel, actor, messageId ?? Guid.Empty); break;
            case "join": room.Join(channel, actor); break;
            case "leave": room.Leave(channel, actor); break;
            case "invite":
                var who = (Text("who") ?? "").Trim();
                room.Invite(channel, actor, who, isAgent(who) && !room.IsOwner(who));
                break;
            case "kick": room.Kick(channel, actor, Text("who") ?? ""); break;
            case "mute":
                room.Mute(channel, actor, Text("who") ?? "", (int?)message["minutes"] is > 0 and var minutes ? TimeSpan.FromMinutes(minutes) : null);
                break;
            case "unmute": room.Unmute(channel, actor, Text("who") ?? ""); break;
            case "moderator": room.SetModerator(channel, actor, Text("who") ?? "", (bool?)message["on"] == true); break;
            case "topic": room.SetTopic(channel, actor, Text("topic")); break;
            case "access": room.SetAccess(channel, actor, (bool?)message["inviteOnly"] == true ? ChannelAccess.InviteOnly : ChannelAccess.Open); break;
            default: throw new ChatException("ProJict here doesn't know that.");
        }
    }
}
