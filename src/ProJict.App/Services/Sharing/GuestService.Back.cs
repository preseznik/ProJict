using System.IO;
using System.Text.Json.Nodes;
using System.Windows.Threading;
using ProJict.Core.Chat;
using ProJict.Core.Model;
using ProJict.Core.Sharing;
using ProJict.Core.Storage;

namespace ProJict.App.Services.Sharing;

public sealed partial class HostConnection
{
    /// <summary>Your own channels go to the host too: always from a ProJict linked as you, otherwise when you tick it.</summary>
    public bool SharesChatBack => IsSelf || Host.ShareChatBack;

    /// <summary>Your channels the host has, so it hears when one stops being shared.</summary>
    internal HashSet<Guid> BackSent { get; } = [];
}

/// <summary>
/// Sharing your channels back with a host you're connected to: a ProJict linked as you shares all of them (its owner is
/// you), and for others you tick "Share my channels with …", which shares the Open ones not about a private project.
/// The host's owner then reads and writes in them, as you (linked as you) or by their own name; what they do comes back
/// as a request this ProJict checks like any other.
/// </summary>
public sealed partial class GuestService
{
    private static readonly TimeSpan BackDelay = TimeSpan.FromMilliseconds(150);
    private readonly Dictionary<Guid, DispatcherTimer> _backTimers = [];

    private void WatchOwnChat()
    {
        _state.Chat.Changed += OnOwnChatChanged;
        _state.Chat.Removed += channel => SendBackChannel(channel);
    }

    /// <summary>Whether the host gets this channel of yours.</summary>
    private bool BackVisible(HostConnection connection, ChatChannel channel)
    {
        if (!connection.SharesChatBack || !_state.Chat.Channels.Contains(channel)) return false;
        if (channel.Link is { } link && _state.Projects.Any(p => p.Id == link.ProjectId && p.Private)) return false;
        return connection.IsSelf || (!channel.Direct && channel.Access == ChannelAccess.Open);
    }

    /// <summary>Who the host acts as in your channels: you (linked as you), else a visitor by the host's name.</summary>
    private ChatActor BackActor(HostConnection connection) =>
        connection.IsSelf ? _state.ChatMe : new ChatActor(connection.HostName, false);

    private JsonObject BackEntry(ChatChannel channel) => new()
    {
        ["channel"] = JsonNode.Parse(Json.Serialize(ChatRoom.ForGuest(channel))),
        ["linkText"] = channel.Link != null ? _main.Chat.LinkTextOf(channel.Link) : null,
    };

    /// <summary>All your channels the host gets (none when you stopped sharing them), replacing what it had.</summary>
    internal void SendBackSnapshot(HostConnection connection)
    {
        if (connection.State != ConnectionState.Connected) return;
        var channels = _state.Chat.Channels.Where(c => BackVisible(connection, c)).ToList();
        connection.BackSent.Clear();
        foreach (var channel in channels) connection.BackSent.Add(channel.Id);
        connection.Send(Wire.Message("backChat", ("owner", _state.UserName), ("as", connection.IsSelf ? _state.UserName : connection.HostName),
            ("asOwner", connection.IsSelf), ("channels", new JsonArray(channels.Select(c => (JsonNode)BackEntry(c)).ToArray()))));
    }

    /// <summary>Turns sharing your channels with that host on or off (a ProJict linked as you always shares them).</summary>
    public void SetShareChatBack(HostConnection connection, bool on)
    {
        if (connection.Host.ShareChatBack == on) return;
        connection.Host.ShareChatBack = on;
        _store.Save();
        SendBackSnapshot(connection);
        Changed?.Invoke();
    }

    private void OnOwnChatChanged(ChatChannel channel)
    {
        if (!Connections.Any(c => c.State == ConnectionState.Connected && (c.SharesChatBack || c.BackSent.Contains(channel.Id)))) return;
        if (!_backTimers.TryGetValue(channel.Id, out var timer))
        {
            timer = new DispatcherTimer(DispatcherPriority.Background, _dispatcher) { Interval = BackDelay };
            timer.Tick += (_, _) =>
            {
                timer.Stop();
                SendBackChannel(channel);
            };
            _backTimers[channel.Id] = timer;
        }
        timer.Stop();
        timer.Start();
    }

    /// <summary>The channel to every host that gets it; ones that no longer do lose it.</summary>
    private void SendBackChannel(ChatChannel channel)
    {
        foreach (var connection in Connections.Where(c => c.State == ConnectionState.Connected))
        {
            if (BackVisible(connection, channel))
            {
                connection.BackSent.Add(channel.Id);
                var entry = BackEntry(channel);
                connection.Send(Wire.Message("backChannel", ("channel", entry["channel"]!.DeepClone()), ("linkText", entry["linkText"]?.DeepClone())));
            }
            else if (connection.BackSent.Remove(channel.Id))
                connection.Send(Wire.Message("backGone", ("id", channel.Id.ToString())));
        }
    }

    /// <summary>
    /// Something the host's owner does in one of your channels. Linked as you, they're you here; otherwise they read and
    /// write in the Open channels you share, by their own name. Why not goes back to them.
    /// </summary>
    private void HandleBackOp(HostConnection connection, JsonObject message)
    {
        void Refuse(string why) => connection.Send(Wire.Message("backError", ("message", why)));
        if (!connection.SharesChatBack)
        {
            Refuse("This ProJict doesn't share its channels with you.");
            return;
        }
        var room = _state.Chat;
        var actor = BackActor(connection);
        string? Text(string name) => (string?)message[name];
        try
        {
            if ((string?)message["op"] == "direct")
            {
                if (!connection.IsSelf) throw new ChatException("Write privately to people here in your own ProJict's Chat.");
                var other = (Text("with") ?? "").Trim();
                room.OpenDirect(actor, other, _main.AgentTools.KnownAgentNames().Contains(other, StringComparer.OrdinalIgnoreCase) && !room.IsOwner(other));
                return;
            }
            if (!Guid.TryParse(Text("channel"), out var id) || room.Channels.FirstOrDefault(c => c.Id == id) is not { } channel || !BackVisible(connection, channel))
                throw new ChatException("That channel isn't there any more.");
            RemoteChatOps.OnChannel(room, channel, actor, message, true,
                who => _main.AgentTools.KnownAgentNames().Contains(who, StringComparer.OrdinalIgnoreCase), () =>
                {
                    var files = new List<Attachment>();
                    foreach (var f in (message["files"] as JsonArray ?? []).OfType<JsonObject>())
                    {
                        try
                        {
                            var data = Convert.FromBase64String((string?)f["data"] ?? "");
                            if (data.Length is > 0 and <= MaxFileBytes)
                                files.Add(_state.Store.WriteAttachment(ProjectStore.SafeFileName((string?)f["name"] ?? "file"), data));
                        }
                        catch (FormatException) { }
                    }
                    return files;
                });
        }
        catch (ChatException ex)
        {
            Refuse(ex.Message);
        }
    }

    /// <summary>A file of a message in a channel the host gets, for it to keep and show.</summary>
    private void SendBackFile(HostConnection connection, JsonObject message)
    {
        if (!Guid.TryParse((string?)message["id"], out var id)) return;
        var attachment = _state.Chat.Channels.Where(c => BackVisible(connection, c)).SelectMany(c => c.Messages)
            .SelectMany(m => m.Attachments ?? []).FirstOrDefault(a => a.Id == id);
        if (attachment == null) return;
        var path = _state.Store.AttachmentPath(attachment);
        try
        {
            if (!File.Exists(path) || new FileInfo(path).Length > MaxFileBytes) return;
            connection.Send(Wire.Message("backFile", ("id", id.ToString()), ("data", Convert.ToBase64String(File.ReadAllBytes(path)))));
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { }
    }
}
