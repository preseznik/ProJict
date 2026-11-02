using System.IO;
using System.Text.Json.Nodes;
using ProJict.Core.Chat;
using ProJict.Core.Model;
using ProJict.Core.Sharing;
using ProJict.Core.Storage;

namespace ProJict.App.Services.Sharing;

public sealed partial class HostConnection
{
    /// <summary>The host's channels you may see: a copy kept in memory while connected, never saved here.</summary>
    public ChatRoom? Chat { get; internal set; }
    /// <summary>The name the host knows you by; you write in its Chat with it.</summary>
    public string? MyName { get; internal set; }
    /// <summary>What the host refused that an agent here sent it (by the agent's name), until the agent hears it.</summary>
    internal List<(string Agent, string Why)> AgentRefusals { get; } = [];
    /// <summary>Who's online or away in the host's ProJict, as it last said; everyone else there is offline.</summary>
    public IReadOnlyDictionary<string, PresenceState> Presence { get; internal set; } = new Dictionary<string, PresenceState>();
    /// <summary>Whether you're away and which agents are here, as the host last heard it.</summary>
    internal string? PresenceSent { get; set; }
}

/// <summary>
/// The hosts' Chat, as a guest: their channels you may see arrive and stay up to date; what you do in them goes to the
/// host, which checks it against your role and sends the channel back changed (or says why not).
/// </summary>
public sealed partial class GuestService
{
    /// <summary>A host's channels changed (UI thread).</summary>
    public event Action<HostConnection>? ChatChanged;

    private ChatRoom MirrorOf(HostConnection connection)
    {
        if (connection.Chat is { } room) return room;
        room = new ChatRoom([], () => connection.HostName);
        room.Changed += _ => ChatChanged?.Invoke(connection);
        room.Removed += _ => ChatChanged?.Invoke(connection);
        connection.Chat = room;
        return room;
    }

    private static ChatChannel? ChannelFrom(JsonNode? node) => node == null ? null : Json.Deserialize<ChatChannel>(node.ToJsonString());

    private void HandleChat(HostConnection connection, string type, JsonObject message)
    {
        switch (type)
        {
            case "chat":
                connection.MyName = (string?)message["name"] ?? connection.MyName;
                var room = MirrorOf(connection);
                var incoming = (message["channels"] as JsonArray ?? []).Select(ChannelFrom).OfType<ChatChannel>().ToList();
                foreach (var gone in room.Channels.Where(c => incoming.All(i => i.Id != c.Id)).Select(c => c.Id).ToList()) room.Drop(gone);
                foreach (var channel in incoming) room.Upsert(channel);
                ChatChanged?.Invoke(connection);
                break;
            case "chatChannel":
                if (ChannelFrom(message["channel"]) is { } changed) MirrorOf(connection).Upsert(changed);
                break;
            case "chatGone":
                if (Guid.TryParse((string?)message["id"], out var id)) connection.Chat?.Drop(id);
                break;
            case "chatError":
                if ((string?)message["as"] is { Length: > 0 } agent) connection.AgentRefusals.Add((agent, (string?)message["message"] ?? ""));
                else _state.StatusText = $"{connection.HostName}'s ProJict: {(string?)message["message"]}";
                break;
        }
    }

    /// <summary>While disconnected the host's channels are gone; connecting again brings them all back.</summary>
    private void DropChat(HostConnection connection)
    {
        connection.PresenceSent = null;
        if (connection.Presence.Count > 0)
        {
            connection.Presence = new Dictionary<string, PresenceState>();
            PresenceChanged?.Invoke(connection);
        }
        if (connection.Chat is not { } room) return;
        foreach (var id in room.Channels.Select(c => c.Id).ToList()) room.Drop(id);
    }

    /// <summary>Who's online or away in a host's ProJict changed (UI thread).</summary>
    public event Action<HostConnection>? PresenceChanged;

    private void TakePresence(HostConnection connection, JsonObject message)
    {
        connection.Presence = Presence.FromJson(message["people"]);
        PresenceChanged?.Invoke(connection);
    }

    /// <summary>
    /// Tells each host you're connected to whether you're away, and which of your agents are here (online or away), when
    /// that changed since it last heard: its Chat then shows you and them as you are.
    /// </summary>
    public void SendPresence()
    {
        var agents = _main.Agents.AgentsHere();
        var away = _main.Agents.MyState == PresenceState.Away;
        var signature = $"{away}|{Presence.Signature(agents)}";
        foreach (var connection in Connections.Where(c => c.State == ConnectionState.Connected && c.PresenceSent != signature))
        {
            connection.PresenceSent = signature;
            connection.Send(Wire.Message("presence", ("away", away), ("agents", Presence.ToJson(agents))));
        }
    }

    /// <summary>The connection a channel came from, or null for your own.</summary>
    public HostConnection? ConnectionOf(ChatChannel channel) => Connections.FirstOrDefault(c => c.Chat?.Channels.Contains(channel) == true);

    /// <summary>Something you do in a host's Chat; the host answers with the changed channel, or why not.</summary>
    /// <summary>The code the host shows, typed here to confirm this computer is its user; or "it isn't me".</summary>
    public void AnswerSelf(HostConnection connection, string? code)
    {
        connection.SelfMessage = null;
        connection.Send(code == null ? Wire.Message("selfCode", ("decline", true)) : Wire.Message("selfCode", ("code", code.Trim())));
    }

    public void ChatOp(HostConnection connection, string op, params (string Name, JsonNode? Value)[] fields)
    {
        var message = Wire.Message("chatOp", fields);
        message["op"] = op;
        connection.Send(message);
    }

    /// <summary>Writes in a host's channel; its files go first, so the host has them when the message refers to them.</summary>
    public void ChatPost(HostConnection connection, ChatChannel channel, string text, Guid? replyTo, IReadOnlyList<(string Name, byte[] Data)> files)
    {
        var sent = new JsonArray();
        foreach (var (name, data) in files)
        {
            if (data.Length > MaxFileBytes) continue;
            var id = Guid.NewGuid();
            connection.Send(Wire.Message("putFile", ("id", id.ToString()), ("name", name), ("data", Convert.ToBase64String(data))));
            sent.Add(new JsonObject { ["id"] = id.ToString(), ["name"] = name, ["size"] = data.Length });
        }
        ChatOp(connection, "post", ("channel", channel.Id.ToString()), ("text", text), ("replyTo", replyTo?.ToString()), ("files", sent));
    }

    /// <summary>Where a file from a host's chat is kept once fetched (with the host's other files).</summary>
    public string ChatFilePath(HostConnection connection, Attachment attachment) =>
        Path.Combine(_state.RemoteCacheRoot, ProjectStore.SafeFileName(connection.Address), attachment.Id.ToString("N"), ProjectStore.SafeFileName(attachment.FileName));

    /// <summary>Asks the host for a chat file (once) when it isn't here yet.</summary>
    public void FetchChatFile(HostConnection connection, Attachment attachment)
    {
        if (connection.State != ConnectionState.Connected || File.Exists(ChatFilePath(connection, attachment)) || !connection.Requested.Add(attachment.Id)) return;
        connection.Send(Wire.Message("getFile", ("id", attachment.Id.ToString())));
    }

    /// <summary>A fetched file that belongs to a chat message; false when it's not one.</summary>
    private bool StoreChatFile(HostConnection connection, Guid id, byte[] data)
    {
        var attachment = connection.Chat?.Channels.SelectMany(c => c.Messages).SelectMany(m => m.Attachments ?? []).FirstOrDefault(a => a.Id == id);
        if (attachment == null) return false;
        var path = ChatFilePath(connection, attachment);
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        File.WriteAllBytes(path, data);
        ChatChanged?.Invoke(connection);
        return true;
    }
}
