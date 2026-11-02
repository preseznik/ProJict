using System.IO;
using System.Text.Json.Nodes;
using ProJict.App.ViewModels;
using ProJict.Core.Chat;
using ProJict.Core.Model;
using ProJict.Core.Sharing;
using ProJict.Core.Storage;

namespace ProJict.App.Services.Sharing;

/// <summary>
/// A guest's own channels, shared back to you over its connection: all of them from a ProJict linked as you (you act
/// there as its owner), the Open ones from someone who ticked "Share my channels" (you act there as a visitor). Kept in
/// memory while it's connected.
/// </summary>
public sealed class BackChat : IChatRemote
{
    internal BackChat(GuestSession session, string ownerName)
    {
        Session = session;
        OwnerName = ownerName;
        Chat = new ChatRoom([], () => Owner);
    }

    public GuestSession Session { get; }
    /// <summary>The guest's name as you know it ("iztok4090").</summary>
    public string OwnerName { get; internal set; }
    /// <summary>The guest's own user name there, who owns its channels.</summary>
    public string Owner { get; internal set; } = "";
    public ChatRoom Chat { get; }
    ChatRoom? IChatRemote.Chat => Chat;
    public string? MyName { get; internal set; }
    public bool AsOwner { get; internal set; }
    public bool IsConnected { get; internal set; } = true;
    public bool CanWrite => IsConnected;
    /// <summary>What each channel is about there ("GRID2VR › HUD"), since its projects may not be here.</summary>
    public Dictionary<Guid, string> LinkTexts { get; } = [];
    public IReadOnlyDictionary<string, PresenceState> Presence { get; internal set; } = new Dictionary<string, PresenceState>();
    public IReadOnlySet<string> AgentsThere { get; internal set; } = new HashSet<string>();
    /// <summary>Files asked for once (to keep with yours, so they open like any other).</summary>
    internal HashSet<Guid> Requested { get; } = [];
}

public sealed partial class HostService
{
    /// <summary>The channels guests share back with you, of the ones connected now.</summary>
    public IEnumerable<BackChat> BackChats => _sessions.Select(s => s.Back).OfType<BackChat>().Where(b => b.Chat.Channels.Count > 0);

    /// <summary>The guest a shared-back channel came from; null for your own channels and hosts'.</summary>
    public BackChat? BackChatOf(ChatChannel channel) => BackChats.FirstOrDefault(b => b.Chat.Channels.Contains(channel));

    /// <summary>A guest's shared-back channels changed (UI thread).</summary>
    public event Action<BackChat>? BackChatChanged;

    private static ChatChannel? BackChannelFrom(JsonNode? node) => node == null ? null : Json.Deserialize<ChatChannel>(node.ToJsonString());

    /// <summary>Named as the guest joined ("iztok4090"), also when it's you on another computer.</summary>
    private static BackChat BackOf(GuestSession session) => session.Back ??= new BackChat(session, session.Account.Name);

    private void HandleBack(GuestSession session, string type, JsonObject message)
    {
        switch (type)
        {
            case "backChat":
                var back = BackOf(session);
                back.Owner = (string?)message["owner"] ?? back.Owner;
                back.MyName = (string?)message["as"];
                back.AsOwner = (bool?)message["asOwner"] == true && session.Account.Self;
                var incoming = (message["channels"] as JsonArray ?? []).OfType<JsonObject>().ToList();
                var ids = new HashSet<Guid>();
                foreach (var entry in incoming)
                    if (BackChannelFrom(entry["channel"]) is { } channel)
                    {
                        ids.Add(channel.Id);
                        Take(back, channel, (string?)entry["linkText"]);
                    }
                foreach (var gone in back.Chat.Channels.Where(c => !ids.Contains(c.Id)).Select(c => c.Id).ToList()) back.Chat.Drop(gone);
                BackChatChanged?.Invoke(back);
                break;
            case "backChannel":
                if (BackChannelFrom(message["channel"]) is { } changed)
                {
                    Take(BackOf(session), changed, (string?)message["linkText"]);
                    BackChatChanged?.Invoke(session.Back!);
                }
                break;
            case "backGone":
                if (session.Back is { } from && Guid.TryParse((string?)message["id"], out var id))
                {
                    from.Chat.Drop(id);
                    from.LinkTexts.Remove(id);
                    BackChatChanged?.Invoke(from);
                }
                break;
            case "backFile":
                StoreBackFile(session, message);
                break;
            case "backError":
                _state.StatusText = $"{session.Account.Name}'s ProJict: {(string?)message["message"]}";
                break;
        }
    }

    private static void Take(BackChat back, ChatChannel channel, string? linkText)
    {
        if (linkText != null) back.LinkTexts[channel.Id] = linkText;
        back.Chat.Upsert(channel);
    }

    /// <summary>Something you do in a guest's shared-back channel; its ProJict sends the channel back changed (or says why not).</summary>
    public void BackOp(BackChat back, string op, params (string Name, JsonNode? Value)[] fields)
    {
        if (!back.IsConnected) return;
        var message = Wire.Message("backOp", fields);
        message["op"] = op;
        back.Session.Send(message);
    }

    /// <summary>
    /// Writes in a guest's shared-back channel, with the files (its ProJict keeps them): sent first in pieces when it
    /// takes them, else in the message itself, up to 15 MB each.
    /// </summary>
    public void BackPost(BackChat back, ChatChannel channel, string text, Guid? replyTo, IReadOnlyList<OutgoingFile> files)
    {
        if (!back.IsConnected) return;
        if (back.Session.TakesPieces && files.Count > 0)
        {
            _ = BackPostAsync(back, channel, text, replyTo, files);
            return;
        }
        var sent = new JsonArray();
        foreach (var file in files)
        {
            try
            {
                if (file.Size <= WireFiles.WholeLimit)
                    sent.Add(new JsonObject { ["name"] = file.Name, ["data"] = Convert.ToBase64String(file.Data ?? File.ReadAllBytes(file.Path!)) });
                else _state.StatusText = $"{file.Name} is {AttachmentViewModel.FormatSize(file.Size)}: {back.OwnerName}'s ProJict takes files up to 15 MB (it's an older one), so it didn't go.";
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { }
        }
        BackOp(back, "post", ("channel", channel.Id.ToString()), ("text", text), ("replyTo", replyTo?.ToString()), ("files", sent));
    }

    private async Task BackPostAsync(BackChat back, ChatChannel channel, string text, Guid? replyTo, IReadOnlyList<OutgoingFile> files)
    {
        var sent = new JsonArray();
        foreach (var file in files)
        {
            var id = Guid.NewGuid();
            if (await back.Session.SendFileAsync(Wire.Message("backPutFile", ("id", id.ToString()), ("name", file.Name)), file))
                sent.Add(new JsonObject { ["id"] = id.ToString(), ["name"] = file.Name });
        }
        BackOp(back, "post", ("channel", channel.Id.ToString()), ("text", text), ("replyTo", replyTo?.ToString()), ("files", sent));
    }

    /// <summary>
    /// Asks the guest for the files of its messages you don't have yet; they're kept with yours (same id), so they open
    /// like any other file in Chat.
    /// </summary>
    public void BackFetchFiles(BackChat back, IEnumerable<Attachment> attachments)
    {
        foreach (var attachment in attachments.Take(50))
            if (back.IsConnected && !File.Exists(_state.Store.AttachmentPath(attachment)) && back.Requested.Add(attachment.Id))
                back.Session.Send(Wire.Message("backGetFile", ("id", attachment.Id.ToString())));
    }

    /// <summary>A file you asked the guest for (backGetFile), whole or a piece of it.</summary>
    private void StoreBackFile(GuestSession session, JsonObject message)
    {
        if (session.Back is not { } back || !Guid.TryParse((string?)message["id"], out var id) || !back.Requested.Contains(id)) return;
        var attachment = back.Chat.Channels.SelectMany(c => c.Messages).SelectMany(m => m.Attachments ?? []).FirstOrDefault(a => a.Id == id);
        if (attachment == null) return;
        try
        {
            switch (WireFiles.Receive(message, _state.Store.AttachmentPath(attachment), WireFiles.MaxBytes))
            {
                case FileArrival.Done: BackChatChanged?.Invoke(back); break;
                case FileArrival.Failed: back.Requested.Remove(id); break;
            }
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { back.Requested.Remove(id); }
    }

    /// <summary>The guest left: its channels go with it.</summary>
    private void DropBack(GuestSession session)
    {
        if (session.Back is not { } back) return;
        back.IsConnected = false;
        foreach (var id in back.Chat.Channels.Select(c => c.Id).ToList()) back.Chat.Drop(id);
        session.Back = null;
        BackChatChanged?.Invoke(back);
    }

    /// <summary>Who's online there, as its "presence" said: the guest themself and their agents (by their names there).</summary>
    private static void BackPresence(GuestSession session, bool away, IReadOnlyDictionary<string, PresenceState> agents)
    {
        if (session.Back is not { } back) return;
        var people = new Dictionary<string, PresenceState>(agents, StringComparer.OrdinalIgnoreCase);
        if (back.Owner.Length > 0) people[back.Owner] = away ? PresenceState.Away : PresenceState.Online;
        back.Presence = people;
        back.AgentsThere = agents.Keys.ToHashSet(StringComparer.OrdinalIgnoreCase);
    }
}
