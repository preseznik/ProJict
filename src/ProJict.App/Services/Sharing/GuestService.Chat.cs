using System.IO;
using System.Text.Json.Nodes;
using ProJict.App.ViewModels;
using ProJict.Core.Chat;
using ProJict.Core.Model;
using ProJict.Core.Sharing;
using ProJict.Core.Storage;

namespace ProJict.App.Services.Sharing;

public sealed partial class HostConnection : IChatRemote
{
    string IChatRemote.OwnerName => HostName;
    bool IChatRemote.IsConnected => State == ConnectionState.Connected;
    bool IChatRemote.CanWrite => GuestAccess.CanComment(Role);
    bool IChatRemote.AsOwner => false;

    /// <summary>The host's channels you may see: a copy kept in memory while connected, never saved here.</summary>
    public ChatRoom? Chat { get; internal set; }
    /// <summary>The name the host knows you by; you write in its Chat with it.</summary>
    public string? MyName { get; internal set; }
    /// <summary>What the host refused that an agent here sent it (by the agent's name), until the agent hears it.</summary>
    internal List<(string Agent, string Why)> AgentRefusals { get; } = [];
    /// <summary>Who's online or away in the host's ProJict, as it last said; everyone else there is offline.</summary>
    public IReadOnlyDictionary<string, PresenceState> Presence { get; internal set; } = new Dictionary<string, PresenceState>();
    /// <summary>Which of those are AI agents (the host's own, and those of everyone connected to it).</summary>
    public IReadOnlySet<string> AgentsThere { get; internal set; } = new HashSet<string>();
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
        if (connection.Presence.Count > 0 || connection.AgentsThere.Count > 0)
        {
            connection.Presence = new Dictionary<string, PresenceState>();
            connection.AgentsThere = new HashSet<string>();
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
        connection.AgentsThere = (message["agents"] as JsonArray ?? []).Select(n => n is JsonValue v && v.TryGetValue<string>(out var s) ? s.Trim() : "")
            .Where(s => s.Length is > 0 and <= 200).Take(200).ToHashSet(StringComparer.OrdinalIgnoreCase);
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

    /// <summary>
    /// Writes in a host's channel (as one of your agents, with <paramref name="agent"/>); its files go first, so the host
    /// has them when the message refers to them.
    /// </summary>
    public void ChatPost(HostConnection connection, ChatChannel channel, string text, Guid? replyTo, IReadOnlyList<OutgoingFile> files, string? agent = null)
    {
        if (files.Count == 0) ChatOp(connection, "post", ("channel", channel.Id.ToString()), ("text", text), ("replyTo", replyTo?.ToString()), ("files", new JsonArray()), ("as", agent));
        else _ = ChatPostAsync(connection, channel, text, replyTo, files, agent);
    }

    private async Task ChatPostAsync(HostConnection connection, ChatChannel channel, string text, Guid? replyTo, IReadOnlyList<OutgoingFile> files, string? agent)
    {
        var sent = await PutFilesAsync(connection, files);
        ChatOp(connection, "post", ("channel", channel.Id.ToString()), ("text", text), ("replyTo", replyTo?.ToString()), ("files", sent), ("as", agent));
    }

    /// <summary>Sends files to the host (putFile), one after the other; what went, for the message or page that refers to them.</summary>
    private async Task<JsonArray> PutFilesAsync(HostConnection connection, IReadOnlyList<OutgoingFile> files)
    {
        var sent = new JsonArray();
        foreach (var file in files)
        {
            var id = Guid.NewGuid();
            long size;
            try { size = file.Size; }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { continue; }
            if (size > WireFiles.LimitFor(connection.TakesPieces))
            {
                _state.StatusText = $"{file.Name} is {AttachmentViewModel.FormatSize(size)}: {connection.HostName}'s ProJict takes files up to " +
                                    $"{AttachmentViewModel.FormatSize(WireFiles.LimitFor(connection.TakesPieces))} (it's an older one), so it didn't go.";
                continue;
            }
            if (size > WireFiles.PieceBytes) _state.StatusText = $"Sending {file.Name} ({AttachmentViewModel.FormatSize(size)}) to {connection.HostName}'s ProJict…";
            if (await connection.SendFileAsync(Wire.Message("putFile", ("id", id.ToString()), ("name", file.Name)), file))
                sent.Add(new JsonObject { ["id"] = id.ToString(), ["name"] = file.Name, ["size"] = size });
        }
        return sent;
    }

    /// <summary>Where a file from a host's chat is kept once fetched: with the host's other files, a private conversation's in the cache for them.</summary>
    public string ChatFilePath(HostConnection connection, Attachment attachment) => attachment.Cached ? _state.Store.AttachmentPath(attachment) :
        Path.Combine(_state.RemoteCacheRoot, ProjectStore.SafeFileName(connection.Address), attachment.Id.ToString("N"), ProjectStore.SafeFileName(attachment.FileName));

    /// <summary>Asks the host for a chat file (once) when it isn't here yet.</summary>
    public void FetchChatFile(HostConnection connection, Attachment attachment)
    {
        if (connection.State != ConnectionState.Connected || File.Exists(ChatFilePath(connection, attachment)) || !connection.Requested.Add(attachment.Id)) return;
        connection.Send(Wire.Message("getFile", ("id", attachment.Id.ToString())));
    }

    /// <summary>The file of a message in the host's chat, if the id is one.</summary>
    private static Attachment? ChatAttachment(HostConnection connection, Guid id) =>
        connection.Chat?.Channels.SelectMany(ProJict.Core.Chat.ChatRoom.Files).FirstOrDefault(a => a.Id == id);
}
