using System.Collections.ObjectModel;
using System.Globalization;
using System.IO;
using System.Windows;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using System.Text.Json.Nodes;
using ProJict.App.Services;
using ProJict.App.Services.Sharing;
using ProJict.Core.Chat;
using ProJict.Core.Sharing;
using ProJict.Core.Features;
using ProJict.Core.Model;

namespace ProJict.App.ViewModels;

/// <summary>
/// The Chat view: your channels and the others you could join; the open channel's messages and the box to write in;
/// its members, with moderation for moderators; and the form to start a channel. Messages that arrive while you look at
/// the channel count as read.
/// </summary>
public sealed partial class ChatViewModel : ObservableObject
{
    /// <summary>
    /// A channel opens with its newest messages, and older ones come this many at a time as you scroll up: a message
    /// takes a few milliseconds to lay out, so building all of a long channel's at once made opening it slow.
    /// </summary>
    private const int PageSize = 30;
    /// <summary>To show the New line, a channel opens with up to this many (from a little above the line).</summary>
    private const int MostToShowNew = 200;
    private readonly AppState _state;
    private readonly MainViewModel _main;
    private readonly Dictionary<Guid, ChatMessageViewModel> _messageVms = [];
    /// <summary>The oldest message shown (all newer ones show too); none: the newest PageSize.</summary>
    private Guid? _oldestShown;
    private bool _syncing;

    public ChatViewModel(AppState state, MainViewModel main)
    {
        _state = state;
        _main = main;
        Yours = new ChatSection(state, "yours", "Channels", "The channels you're in");
        Others = new ChatSection(state, "others", "Other channels", "Channels you're not in");
        Private = new ChatSection(state, "private", "Private messages", "One on one: right-click someone › Private message");
        Archive = new ChatSection(state, "archive", "Archive",
            "Private conversations you archived: nothing new in them counts or notifies you until you open one, or take it out (right-click)", foldedAtFirst: true);
        Room.Changed += OnChanged;
        Room.Removed += channel =>
        {
            if (Channel == channel) Channel = null;
            Refresh();
        };
        Pending.CollectionChanged += (_, _) =>
        {
            OnPropertyChanged(nameof(HasPending));
            SendCommand.NotifyCanExecuteChanged();
        };
        Refresh();
    }

    private ChatRoom Room => _state.Chat;
    private ChatActor Me => _state.ChatMe;
    internal string MyName => _state.UserName;
    internal MainViewModel Main => _main;

    /// <summary>
    /// Where a channel lives: your own Chat, a host's (you're its guest), or a guest's it shares back with you; and who
    /// you are there.
    /// </summary>
    internal sealed record ChatPlace(ChatRoom Room, ChatActor Me, IChatRemote? Host);

    internal ChatPlace PlaceOf(ChatChannel? channel)
    {
        if (channel == null) return new ChatPlace(Room, Me, null);
        if (_main.Guest.ConnectionOf(channel) is { Chat: { } room } host)
            return new ChatPlace(room, new ChatActor(host.MyName ?? MyName, false, Guest: true), host);
        if (_main.Host.BackChatOf(channel) is { } back)
            return new ChatPlace(back.Chat, new ChatActor(back.MyName ?? MyName, false, Owner: back.AsOwner), back);
        return new ChatPlace(Room, Me, null);
    }

    /// <summary>Asks the ProJict a channel lives in to do something there; it sends the channel back changed (or says why not).</summary>
    private void SendOp(IChatRemote remote, string op, params (string Name, JsonNode? Value)[] fields)
    {
        if (remote is HostConnection host) _main.Guest.ChatOp(host, op, fields);
        else if (remote is BackChat back) _main.Host.BackOp(back, op, fields);
    }

    private ChatPlace Here => PlaceOf(Channel);

    /// <summary>Your name where the channel lives (the name a host knows you by, in its channels).</summary>
    internal string NameIn(ChatChannel? channel) => PlaceOf(channel).Me.Name;

    /// <summary>Your channels, then the ones of the ProJicts you're connected to.</summary>
    private IEnumerable<ChatChannel> AllChannels => Room.Channels.Concat(_main.Guest.Connections.SelectMany(c => c.Chat?.Channels ?? []))
        .Concat(_main.Host.BackChats.SelectMany(b => b.Chat.Channels));

    /// <summary>A guest may write in a host's channels from commenter up; in your own you always may.</summary>
    internal bool MayWrite(ChatChannel? channel) => PlaceOf(channel).Host is not { } host || host.CanWrite;

    /// <summary>
    /// Does something in a channel: here, or by asking its host, which sends the channel back changed (or says why not).
    /// </summary>
    internal bool Act(ChatChannel channel, Action<ChatRoom, ChatActor> local, string op, params (string Name, JsonNode? Value)[] fields)
    {
        var place = PlaceOf(channel);
        if (place.Host is not { } host) return Try(() => local(place.Room, place.Me));
        SendOp(host, op, [("channel", channel.Id.ToString()), .. fields]);
        return true;
    }

    /// <summary>Another ProJict's channels changed (or it connected or left).</summary>
    public void OnRemoteChanged(IChatRemote remote)
    {
        if (Channel != null && !AllChannels.Contains(Channel)) Channel = null;
        else if (Channel != null && PlaceOf(Channel).Host == remote)
        {
            SyncChannel();
            ReadIfLooking();
        }
        OpenPending();
        Refresh();
    }

    /// <summary>A private conversation or channel you asked a host to start opens when it arrives.</summary>
    private (IChatRemote Host, string Name, bool Direct)? _pending;

    private void OpenPending()
    {
        if (_pending is not { } p || p.Host.Chat is not { } room) return;
        var channel = p.Direct ? room.FindDirect(p.Host.MyName ?? "", p.Name) : room.Find(p.Name);
        if (channel == null) return;
        _pending = null;
        IsShown = true;
        OpenChannel(channel);
    }

    /// <summary>Whether you're looking (the view shows and ProJict is in front): then the open channel's messages are read as they come.</summary>
    public Func<bool> IsLooking { get; set; } = () => Application.Current?.MainWindow?.IsActive == true;
    public bool IsShown { get; private set; }
    /// <summary>Asks before deleting a channel; tests answer for you.</summary>
    public Func<string, bool> Confirm { get; set; } = text =>
        MessageBox.Show(Application.Current.MainWindow!, text, "Delete channel", MessageBoxButton.YesNo, MessageBoxImage.Warning) == MessageBoxResult.Yes;

    /// <summary>The unread counts changed (for the sidebar).</summary>
    public event Action? CountsChanged;

    // ───────────── Channels ─────────────

    public List<ChannelRowViewModel> YourChannels { get; private set; } = [];
    public List<ChannelRowViewModel> OtherChannels { get; private set; } = [];
    public bool HasOtherChannels => OtherChannels.Count > 0;
    /// <summary>Private conversations, below all channels: yours, and (as the owner) the ones between agents.</summary>
    public List<ChannelRowViewModel> PrivateChats { get; private set; } = [];
    public bool HasPrivateChats => PrivateChats.Count > 0;
    /// <summary>Your private conversations in the archive, at the bottom.</summary>
    public List<ChannelRowViewModel> ArchivedChats { get; private set; } = [];
    public bool HasNoChannels => !AllChannels.Any();
    /// <summary>The channels of each ProJict you're connected to, under its owner's name.</summary>
    public List<ChatSection> HostChats { get; private set; } = [];
    public bool HasHostChats => HostChats.Count > 0;
    /// <summary>The sidebar's sections, each folding under its heading.</summary>
    public ChatSection Yours { get; }
    public ChatSection Others { get; }
    public ChatSection Private { get; }
    public ChatSection Archive { get; }

    [ObservableProperty] private ChatChannel? _channel;
    public bool HasChannel => Channel != null;
    /// <summary>The middle shows the open channel, the form to start one, or a hint when there's neither.</summary>
    public bool ShowChannelPane => Channel != null && !IsCreating && !IsBrowsing;
    public bool ShowEmptyPane => Channel == null && !IsCreating && !IsBrowsing;

    /// <summary>Unread messages in your channels, and whether one @mentions you.</summary>
    public int UnreadCount => AllChannels.Sum(c => ChatRoom.Unread(c, NameIn(c)).Count());
    /// <summary>An @mention of you, or a private message, waits: the count shows red.</summary>
    public bool HasMention => AllChannels.Any(c => c.Direct ? ChatRoom.Unread(c, NameIn(c)).Any() : ChatRoom.UnreadMentions(c, NameIn(c)).Any());
    public string UnreadToolTip
    {
        get
        {
            var lines = AllChannels.Select(c => (c, n: ChatRoom.Unread(c, NameIn(c)).Count(), m: ChatRoom.UnreadMentions(c, NameIn(c)).Count()))
                .Where(x => x.n > 0).Select(x => $"{(PlaceOf(x.c).Host is { } h ? h.OwnerName + ": " : "")}{ChatRoom.Title(x.c, NameIn(x.c))}: {Count(x.n, "new message")}" + (x.m > 0 ? $" ({(x.m == 1 ? "one mentions" : $"{x.m} mention")} you)" : "")).ToList();
            return lines.Count == 0 ? "Live chat with AI agents in #channels" : string.Join("\n", lines);
        }
    }

    /// <summary>Opens the view, at a channel (and a message in it) or where you were.</summary>
    public void Show(ChatChannel? channel = null, Guid? message = null)
    {
        IsShown = true;
        IsCreating = false;
        IsBrowsing = false;
        if (channel != null && channel != Channel) Channel = channel;
        else if (Channel == null || !AllChannels.Contains(Channel)) Channel = YourChannels.FirstOrDefault()?.Model ?? OtherChannels.FirstOrDefault()?.Model;
        else
        {
            MarkNew();
            ReadIfLooking();
        }
        Refresh();
        if (message is { } id) HighlightMessage(id);
    }

    public void Hide() => IsShown = false;

    /// <summary>ProJict came to the front: what shows is read now.</summary>
    public void OnActivated()
    {
        if (!IsShown) return;
        MarkNew();
        ReadIfLooking();
    }

    /// <summary>Back at the channel: the "New" line goes above what came while you were away.</summary>
    private void MarkNew()
    {
        if (Channel == null || ChatRoom.Member(Channel, NameIn(Channel)) is not { } me || !ChatRoom.Unread(Channel, NameIn(Channel)).Any()) return;
        UnreadFrom = me.LastRead ?? DateTime.MinValue;
        // The New line shows, with a little of what came before it, unless it's far back.
        if (ChatRoom.Unread(Channel, NameIn(Channel)).FirstOrDefault() is { } first)
            ShowFrom(Math.Max(Channel.Messages.Count - MostToShowNew, Channel.Messages.IndexOf(first) - 2));
        SyncMessages();
    }

    /// <summary>What you were writing in each channel you left, until you're back (not an edit of a message).</summary>
    private readonly Dictionary<Guid, string> _drafts = [];

    partial void OnChannelChanging(ChatChannel? oldValue, ChatChannel? newValue)
    {
        if (oldValue == null) return;
        if (Editing == null && Draft.Trim().Length > 0) _drafts[oldValue.Id] = Draft;
        else _drafts.Remove(oldValue.Id);
    }

    partial void OnChannelChanged(ChatChannel? value)
    {
        _oldestShown = null;
        _messageVms.Clear();
        Messages.Clear();
        ReplyTo = null;
        Editing = null;
        Draft = value != null && _drafts.Remove(value.Id, out var draft) ? draft : "";
        Pending.Clear();
        // The "New" line goes above what you hadn't read when you opened the channel.
        UnreadFrom = null;
        SyncChannel();
        MarkNew();
        ReadIfLooking();
    }

    private DateTime? UnreadFrom { get; set; }

    private void OnChanged(ChatChannel channel)
    {
        if (_syncing) return;
        _syncing = true;
        try
        {
            if (channel == Channel)
            {
                SyncChannel();
                ReadIfLooking();
            }
            Refresh();
        }
        finally { _syncing = false; }
    }

    private void ReadIfLooking()
    {
        if (Channel == null || !IsShown || !IsLooking()) return;
        var place = Here;
        if (place.Host is { } host)
        {
            // Read here at once; the host notes it too, for the next time you connect.
            if (ChatRoom.Member(Channel, place.Me.Name) is not { } me || Channel.Messages.Count == 0 || me.LastRead >= Channel.Messages[^1].At) return;
            me.LastRead = Channel.Messages[^1].At;
            SendOp(host, "read", ("channel", Channel.Id.ToString()), ("upTo", me.LastRead.Value.ToString("O")));
            Refresh();
            return;
        }
        if (place.Room.MarkRead(Channel, place.Me.Name)) Refresh();
    }

    // ───────────── Agents' private chats ─────────────

    /// <summary>Private conversations between agents (you read them; they're not yours to write in).</summary>
    public List<AgentChatRow> AgentChats { get; private set; } = [];
    public bool HasAgentChats => AgentChats.Count > 0;
    public string AgentChatsTitle => $"Agents' private chats ({AgentChats.Count})";

    /// <summary>The panel with them shows (the toggle in Chat's header).</summary>
    public bool ShowAgentChats
    {
        get => _state.AppSettings.ShowAgentChats;
        set
        {
            if (_state.AppSettings.ShowAgentChats == value) return;
            _state.AppSettings.ShowAgentChats = value;
            _state.SaveAppSettings();
            OnPropertyChanged();
            // Searching: their conversations join the results, or leave them.
            if (IsSearching) RunSearch();
        }
    }

    internal void Suspend(ChatChannel channel, bool suspended)
    {
        if (!Try(() => Room.SetSuspended(channel, Me, suspended))) return;
        var both = string.Join(" and ", channel.Members.Select(m => m.Name));
        _state.StatusText = suspended ? $"{both} can't write to each other until you resume their conversation." : $"{both} can write to each other again.";
        Refresh();
        if (Channel == channel) OpenChannel(channel);
    }

    /// <summary>
    /// Puts one of your private conversations in the archive, where nothing new in it counts or notifies you, or takes it
    /// out (and opens it, so what came meanwhile is read there rather than announced).
    /// </summary>
    internal void SetArchived(ChatChannel channel, bool archived)
    {
        if (!Try(() => Room.SetArchived(channel, MyName, archived))) return;
        var who = ChatRoom.Title(channel, MyName);
        _state.StatusText = archived
            ? $"Archived your conversation with {who}: nothing new in it counts until you open it or take it out."
            : $"Took your conversation with {who} out of the archive.";
        Refresh();
        if (!archived) OpenChannel(channel);
    }

    internal void DeleteAgentChat(ChatChannel channel)
    {
        var both = string.Join(" and ", channel.Members.Select(m => m.Name));
        if (!Confirm($"Delete the private conversation between {both}, with its {Count(channel.Messages.Count(m => !m.Notice), "message")}? Its file is kept in backups\\deleted.")) return;
        if (!Try(() => Room.Delete(channel, Me))) return;
        _state.StatusText = $"Deleted the conversation between {both}.";
        if (Channel == channel) Channel = null;
        Refresh();
    }

    /// <summary>Rebuilds the channel lists and tells the sidebar.</summary>
    public void Refresh()
    {
        var mine = new List<ChannelRowViewModel>();
        var others = new List<ChannelRowViewModel>();
        foreach (var channel in Room.Channels.Where(c => !c.Direct).OrderBy(c => c.Name, StringComparer.Ordinal))
            (ChatRoom.Member(channel, MyName) != null ? mine : others).Add(new ChannelRowViewModel(channel, this));
        YourChannels = mine;
        OtherChannels = others;
        // Yours; the agents' conversations with each other have a panel of their own. The newest first.
        var yours = Room.Channels.Where(c => c.Direct && ChatRoom.Member(c, MyName) != null)
            .OrderByDescending(c => c.Messages.LastOrDefault()?.At ?? c.CreatedAt)
            .Select(c => new ChannelRowViewModel(c, this)).ToList();
        PrivateChats = [.. yours.Where(r => !r.IsArchived)];
        ArchivedChats = [.. yours.Where(r => r.IsArchived)];
        AgentChats = Room.Channels.Where(c => c.Direct && ChatRoom.Member(c, MyName) == null)
            .OrderByDescending(c => c.Messages.LastOrDefault()?.At ?? c.CreatedAt)
            .Select(c => new AgentChatRow(c, this)).ToList();
        BrowseRows = Room.Channels.Where(c => !c.Direct).OrderBy(c => c.Name, StringComparer.Ordinal).Select(c => new BrowseRowViewModel(c, this)).ToList();
        // Each host's: channels you're in, the others you could join, then private conversations.
        // And each guest's that shares its own channels back with you.
        HostChats = _main.Guest.Connections.Where(c => c.Chat is { Channels.Count: > 0 }).Cast<IChatRemote>().Concat(_main.Host.BackChats)
            .Select(c => new ChatSection(_state, "host:" + c.OwnerName, $"{c.OwnerName}'s ProJict", "Shared with you: it's kept there, not here", shared: true).Set(
                c.Chat!.Channels.OrderBy(ch => ch.Direct).ThenByDescending(ch => ChatRoom.Member(ch, c.MyName ?? "") != null).ThenBy(ch => ch.Name, StringComparer.Ordinal)
                    .Select(ch => new ChannelRowViewModel(ch, this)).ToList())).ToList();
        Yours.Set(YourChannels);
        Others.Set(OtherChannels);
        Private.Set(PrivateChats);
        Archive.Set(ArchivedChats);
        foreach (var name in new[] { nameof(YourChannels), nameof(OtherChannels), nameof(HasOtherChannels), nameof(HasNoChannels), nameof(UnreadCount), nameof(HasMention),
                     nameof(UnreadToolTip), nameof(BrowseRows), nameof(PrivateChats), nameof(HasPrivateChats), nameof(ArchivedChats), nameof(HostChats), nameof(HasHostChats),
                     nameof(AgentChats), nameof(HasAgentChats), nameof(AgentChatsTitle) })
            OnPropertyChanged(name);
        if (IsSearching) RunSearch();
        CountsChanged?.Invoke();
    }

    [RelayCommand]
    private void Open(ChannelRowViewModel? row)
    {
        if (row != null) OpenChannel(row.Model);
    }

    internal void OpenChannel(ChatChannel channel)
    {
        IsCreating = false;
        IsBrowsing = false;
        Channel = channel;
        Refresh();
    }

    // ───────────── Browsing ─────────────

    /// <summary>Every channel with its topic, what it's about and its members, to open or join.</summary>
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(ShowChannelPane), nameof(ShowEmptyPane))]
    private bool _isBrowsing;
    public List<BrowseRowViewModel> BrowseRows { get; private set; } = [];

    [RelayCommand]
    private void Browse()
    {
        IsShown = true;
        IsCreating = false;
        Refresh();
        IsBrowsing = true;
    }

    [RelayCommand] private void CloseBrowse() => IsBrowsing = false;

    /// <summary>Right-click someone › Private message: your conversation with them, started if there's none.</summary>
    public void OpenDirect(string name, bool agent)
    {
        ChatChannel? channel = null;
        if (!Try(() => channel = Room.OpenDirect(Me, name, agent))) return;
        IsShown = true;
        OpenChannel(channel!);
    }

    /// <summary>The same, with someone met in a channel: in a host's channel, the conversation is on that host.</summary>
    internal void OpenDirectIn(ChatChannel? context, string name, bool agent)
    {
        if (PlaceOf(context).Host is not { } host)
        {
            OpenDirect(name, agent);
            return;
        }
        _pending = (host, name, true);
        if (host.Chat?.FindDirect(host.MyName ?? "", name) == null) SendOp(host, "direct", ("with", name));
        OpenPending();
    }

    /// <summary>Joins the channel if you're not in it, and opens it.</summary>
    internal void JoinAndOpen(ChatChannel channel)
    {
        if (ChatRoom.Member(channel, NameIn(channel)) == null && !Act(channel, (r, me) => r.Join(channel, me), "join")) return;
        OpenChannel(channel);
    }

    // ───────────── The open channel ─────────────

    public ObservableCollection<ChatMessageViewModel> Messages { get; } = [];
    public List<ChatMemberViewModel> Members { get; private set; } = [];
    public bool HasEarlier { get; private set; }

    public string Title => Channel != null ? ChatRoom.Title(Channel, NameIn(Channel)) : "";
    public bool IsDirect => Channel?.Direct == true;
    public bool IsInviteOnly => Channel?.Access == ChannelAccess.InviteOnly;
    public string AccessText => IsDirect ? "Private" : IsInviteOnly ? "Invite only" : "Open";
    public string AccessToolTip => IsDirect ? (IsMember ? "Just the two of you." : "Between the two of them; you see it as the owner of the workspace.")
        : IsInviteOnly ? "Only members see it; moderators invite others." : "Anyone who can see its project can read and join it.";
    /// <summary>Join shows for channels you're not in (a private conversation can't be joined).</summary>
    public bool ShowJoin => Channel != null && !IsMember && !IsDirect;
    public string? LinkText => Channel?.Link != null ? LinkTextOf(Channel) : null;
    public string LinkSeparator => LinkText != null ? " · " : "";
    public string StartedText => Channel == null ? "" : $"Started by {Channel.CreatedBy} on {Channel.CreatedAt.ToString("d MMMM yyyy", CultureInfo.CurrentCulture)}" +
        (Here.Host is { } host ? $" · in {host.OwnerName}'s ProJict" : "");
    public bool IsMember => Channel != null && ChatRoom.Member(Channel, NameIn(Channel)) != null;
    public bool IsModerator => Channel != null && ChatRoom.IsModerator(Channel, Here.Me) && MayWrite(Channel);
    public string MembersText => Channel == null ? "" : Count(Channel.Members.Count, "member");
    public bool CanWrite => Channel != null && (IsMember || Channel.Access == ChannelAccess.Open) && MayWrite(Channel);

    /// <summary>The topic; moderators (you always) change it in place.</summary>
    public string TopicText
    {
        get => Channel?.Topic ?? "";
        set
        {
            if (Channel is not { } channel || (value ?? "").Trim() == (channel.Topic ?? "")) return;
            Act(channel, (r, me) => r.SetTopic(channel, me, value), "topic", ("topic", value));
        }
    }

    [ObservableProperty] private bool _showMembers = true;

    private void SyncChannel()
    {
        // A guest's shared-back channel: its messages' files are fetched to keep with yours, so they open like any other.
        if (Channel != null && Here.Host is BackChat back) _main.Host.BackFetchFiles(back, Channel.Messages.SelectMany(m => m.Attachments ?? []));
        foreach (var name in new[] { nameof(HasChannel), nameof(Title), nameof(IsDirect), nameof(ShowJoin), nameof(IsInviteOnly), nameof(AccessText), nameof(AccessToolTip), nameof(LinkText), nameof(LinkSeparator), nameof(StartedText),
                     nameof(IsMember), nameof(IsModerator), nameof(MembersText), nameof(CanWrite), nameof(TopicText), nameof(ComposerNote),
                     nameof(CanInvite), nameof(ShowChannelPane), nameof(ShowEmptyPane), nameof(IsOwner) })
            OnPropertyChanged(name);
        RefreshInviteChoices();
        RefreshPresence();
        SyncLink();
        SyncSearch();
        SyncMessages();
        SendCommand.NotifyCanExecuteChanged();
    }

    /// <summary>The open channel's members, with who's online, away or offline now.</summary>
    public void RefreshPresence()
    {
        Members = Channel == null ? [] : Channel.Members
            .OrderByDescending(m => !m.Agent && Here.Room.IsOwner(m.Name)).ThenByDescending(m => m.Moderator).ThenBy(m => m.Name, StringComparer.CurrentCultureIgnoreCase)
            .Select(m => new ChatMemberViewModel(m, Channel, this)).ToList();
        OnPropertyChanged(nameof(Members));
        // Who hears Chat changes with presence (a watcher starting or stopping).
        OnPropertyChanged(nameof(ComposerNote));
    }

    /// <summary>
    /// Whether a member is online, away or offline: here, as this ProJict knows it; in another ProJict's channel, as that
    /// one last said (you as you are; everyone offline while it isn't connected).
    /// </summary>
    internal PresenceState PresenceOf(ChatPlace place, ChatMember member)
    {
        if (place.Host is not { } host) return _main.Agents.StateOf(member.Name);
        if (!member.Agent && string.Equals(member.Name, place.Me.Name, StringComparison.OrdinalIgnoreCase)) return _main.Agents.MyState;
        if (!host.IsConnected) return PresenceState.Offline;
        return host.Presence.TryGetValue(member.Name, out var state) ? state : PresenceState.Offline;
    }

    /// <summary>
    /// The newest messages, or all from the oldest you scrolled up to. The rows already built stay: new messages are added
    /// at the end, older ones in front, and a message a host sent again replaces its row.
    /// </summary>
    private void SyncMessages()
    {
        var models = Channel?.Messages ?? [];
        var start = Start(models);
        var shown = models.GetRange(start, models.Count - start);
        _oldestShown = shown.FirstOrDefault()?.Id;
        HasEarlier = start > 0;
        OnPropertyChanged(nameof(HasEarlier));
        // A host's version of a message is a new object: it gets a new row.
        var wanted = shown.Select(m => _messageVms.TryGetValue(m.Id, out var vm) && vm.Model == m ? vm : _messageVms[m.Id] = new ChatMessageViewModel(m, this)).ToList();
        ChatMessage? previous = null;
        var newLine = false;
        foreach (var vm in wanted)
        {
            var first = !newLine && UnreadFrom is { } from && vm.Model.At > from && !vm.Model.Notice && !vm.IsMine;
            newLine |= first;
            vm.Refresh(previous, first);
            previous = vm.Model;
        }
        // Where the rows don't line up with what's wanted (another channel, say), they're built again.
        var lead = Messages.Count == 0 ? -1 : wanted.FindIndex(w => w.Model.Id == Messages[0].Model.Id);
        if (lead >= 0 && lead + Messages.Count <= wanted.Count && Messages.Select((m, i) => wanted[lead + i].Model.Id == m.Model.Id).All(same => same))
        {
            for (var i = 0; i < Messages.Count; i++)
                if (Messages[i] != wanted[lead + i]) Messages[i] = wanted[lead + i];
            for (var i = 0; i < lead; i++) Messages.Insert(i, wanted[i]);
        }
        else Messages.Clear();
        foreach (var vm in wanted.Skip(Messages.Count)) Messages.Add(vm);
        MessagesChanged?.Invoke();
    }

    /// <summary>Where the shown messages start in the channel's.</summary>
    private int Start(List<ChatMessage> models) =>
        _oldestShown is { } id && models.FindIndex(m => m.Id == id) is >= 0 and var at ? at : Math.Max(0, models.Count - PageSize);

    /// <summary>Shows the messages from this one on (never fewer than now).</summary>
    private void ShowFrom(int index)
    {
        if (Channel?.Messages is not { Count: > 0 } models) return;
        index = Math.Clamp(index, 0, models.Count - 1);
        if (index < Start(models)) _oldestShown = models[index].Id;
    }

    /// <summary>Messages were added or changed: the view scrolls to the newest when it was there.</summary>
    public event Action? MessagesChanged;

    /// <summary>Asks the view to bring a message into sight.</summary>
    public event Action<ChatMessageViewModel>? Reveal;

    /// <summary>The next older messages, in front (the view keeps your place).</summary>
    [RelayCommand]
    private void ShowEarlier()
    {
        if (Channel is not { } channel) return;
        ShowFrom(Start(channel.Messages) - PageSize);
        SyncMessages();
    }

    internal void Highlight(ChatMessageViewModel vm)
    {
        foreach (var m in Messages) m.IsHighlighted = false;
        vm.IsHighlighted = true;
        Reveal?.Invoke(vm);
    }

    internal void HighlightMessage(Guid id)
    {
        if (Channel is not { } channel) return;
        // An older message: everything from a little before it shows.
        if (channel.Messages.FindIndex(m => m.Id == id) is >= 0 and var at && at < Start(channel.Messages))
        {
            ShowFrom(at - 2);
            SyncMessages();
        }
        if (_messageVms.TryGetValue(id, out var vm)) Highlight(vm);
    }

    [RelayCommand]
    private void Join()
    {
        if (Channel is { } channel) Act(channel, (r, me) => r.Join(channel, me), "join");
    }

    [RelayCommand]
    private void Leave()
    {
        if (Channel is { } channel) Act(channel, (r, me) => r.Leave(channel, me), "leave");
    }

    [RelayCommand]
    private void ToggleAccess()
    {
        if (Channel is { } channel)
            Act(channel, (r, me) => r.SetAccess(channel, me, IsInviteOnly ? ChannelAccess.Open : ChannelAccess.InviteOnly), "access", ("inviteOnly", !IsInviteOnly));
    }

    [RelayCommand]
    private void DeleteChannel()
    {
        if (Channel is not { } channel) return;
        if (!Confirm($"Delete #{channel.Name} with its {Count(channel.Messages.Count(m => !m.Notice), "message")}? Its file is kept in backups\\deleted.")) return;
        Try(() => Room.Delete(channel, Me));
        _state.StatusText = $"Deleted #{channel.Name}.";
    }

    /// <summary>Opens what the channel is linked to.</summary>
    [RelayCommand]
    private void OpenLink()
    {
        if (Channel?.Link is not { } link || _state.Projects.FirstOrDefault(p => p.Id == link.ProjectId) is not { } project) return;
        var board = _main.OpenProject(project);
        if (link.ItemId is { } itemId && project.Find(itemId) is { } loc) board.Reveal(loc.Item);
        else if (link.CategoryId is { } categoryId && project.Categories.FirstOrDefault(c => c.Id == categoryId) is { } category) board.FocusOn(category);
    }

    // ───────────── Inviting ─────────────

    [ObservableProperty] private string _inviteName = "";
    /// <summary>Moderators invite; in an Open channel any member may.</summary>
    public bool CanInvite => Channel is { Direct: false } && MayWrite(Channel) && (IsModerator || (IsMember && Channel.Access == ChannelAccess.Open));
    public bool IsOwner => Here.Me.Owner;
    /// <summary>
    /// Agents and guests connected now first, then the ones seen before; rebuilt as they come and go and when the list
    /// opens. In a host's channel: the people and agents in its other channels.
    /// </summary>
    public IReadOnlyList<InviteChoice> InviteChoices { get; private set; } = [];

    /// <summary>Someone you share projects with (a guest), not an agent.</summary>
    internal bool IsGuestName(string name) => _main.Host.Data.Guests.Any(g => string.Equals(g.Name, name, StringComparison.OrdinalIgnoreCase));

    public void RefreshInviteChoices()
    {
        var choices = new List<InviteChoice>();
        if (Channel is { } channel)
        {
            var place = Here;
            bool Out(string n) => ChatRoom.Member(channel, n) == null && !string.Equals(n, place.Me.Name, StringComparison.OrdinalIgnoreCase);
            if (place.Host is { } host)
            {
                // Everyone connected there first (agents said as such), then the others its channels have.
                var here = host.Presence.Keys.Where(Out).Order(StringComparer.CurrentCultureIgnoreCase).ToList();
                choices =
                [
                    .. here.Select(n => new InviteChoice(n, host.AgentsThere.Contains(n) ? $"{n} · AI agent, connected" : $"{n} · connected")),
                    .. host.Chat!.Channels.SelectMany(c => c.Members).Select(m => m.Name).Append(host.OwnerName)
                        .Distinct(StringComparer.OrdinalIgnoreCase).Where(n => Out(n) && !here.Contains(n, StringComparer.OrdinalIgnoreCase))
                        .Order(StringComparer.CurrentCultureIgnoreCase).Select(n => new InviteChoice(n, n)),
                ];
            }
            else
            {
                var liveAgents = _main.Agents.Sessions.Select(s => s.AgentName).ToHashSet(StringComparer.OrdinalIgnoreCase);
                var remote = _main.Agents.RemoteAgents().ToDictionary(a => a.Name, a => a.Through, StringComparer.OrdinalIgnoreCase);
                var connected = _main.Host.Sessions.Select(s => s.Account.Name).ToHashSet(StringComparer.OrdinalIgnoreCase);
                var agents = _main.AgentTools.KnownAgentNames().Where(Out).ToList();
                // A private project's channels stay on this computer: nobody you share with is offered.
                var isPrivate = channel.Link is { } link && _state.Projects.Any(p => p.Id == link.ProjectId && p.Private);
                var guests = isPrivate ? [] : _main.Host.Data.Guests.Where(g => !g.Blocked).Select(g => g.Name).Distinct(StringComparer.OrdinalIgnoreCase).Where(Out)
                    .Order(StringComparer.CurrentCultureIgnoreCase).ToList();
                choices =
                [
                    .. agents.Where(liveAgents.Contains).Select(n => new InviteChoice(n, n)),
                    // Agents in the ProJicts of people you share with, connected now; never to a private project's channel.
                    .. (isPrivate ? [] : agents.Where(n => !liveAgents.Contains(n) && remote.ContainsKey(n))
                        .Select(n => new InviteChoice(n, $"{n} · AI agent in {remote[n]}'s ProJict"))),
                    .. guests.Where(connected.Contains).Select(n => new InviteChoice(n, $"{n} · guest")),
                    .. agents.Where(n => !liveAgents.Contains(n) && !remote.ContainsKey(n)).Select(n => new InviteChoice(n, n)),
                    .. guests.Where(n => !connected.Contains(n)).Select(n => new InviteChoice(n, $"{n} · guest, not connected")),
                ];
            }
        }
        if (choices.SequenceEqual(InviteChoices)) return;
        InviteChoices = choices;
        OnPropertyChanged(nameof(InviteChoices));
    }

    [RelayCommand]
    private void Invite()
    {
        if (Channel is not { } channel || InviteName.Trim().Length == 0) return;
        var name = InviteName.Trim().TrimStart('@');
        var agent = !Room.IsOwner(name) && !IsGuestName(name);
        if (Act(channel, (r, me) => r.Invite(channel, me, name, agent), "invite", ("who", name))) InviteName = "";
        RefreshInviteChoices();
    }

    // ───────────── Writing ─────────────

    [ObservableProperty]
    [NotifyCanExecuteChangedFor(nameof(SendCommand))]
    private string _draft = "";
    [ObservableProperty] private ChatMessageViewModel? _replyTo;
    [ObservableProperty] private ChatMessageViewModel? _editing;
    public ObservableCollection<PendingFile> Pending { get; } = [];
    public bool HasPending => Pending.Count > 0;
    public bool IsReplying => ReplyTo != null;
    public bool IsEditing => Editing != null;
    partial void OnReplyToChanged(ChatMessageViewModel? value) => OnPropertyChanged(nameof(IsReplying));
    partial void OnEditingChanged(ChatMessageViewModel? value) => OnPropertyChanged(nameof(IsEditing));

    /// <summary>Why you can't write here, if you can't: not a member of an invite-only channel, or muted.</summary>
    public string? ComposerNote => Channel == null ? null
        : Here.Host is HostConnection host && !MayWrite(Channel) ? $"You're a viewer in {host.HostName}'s ProJict: you can read here, not write."
        : Here.Host is BackChat back && !back.IsConnected ? $"{back.OwnerName}'s ProJict isn't connected: you can read here, not write."
        : !CanWrite ? IsDirect ? $"A private conversation between {string.Join(" and ", Channel.Members.Select(m => m.Name))}." +
                                 (Channel.Suspended ? " Suspended: neither can write here until you resume it (right-click it under Agents' private chats)." : "")
            : "Only members write here."
        : Here.Room.MutedUntil(Channel, Here.Me.Name) is { } until && !Here.Me.Owner ? $"You're muted until {until:HH:mm}."
        : NotHearingNote;

    /// <summary>
    /// Your agents here that are connected but don't hear Chat right now (no chat watcher, not waiting for messages): they
    /// read what you write when they next do something.
    /// </summary>
    private string? NotHearingNote
    {
        get
        {
            if (Channel is not { } channel || Here.Host != null) return null;
            var connected = _main.Agents.Sessions.Select(s => s.AgentName).ToHashSet(StringComparer.OrdinalIgnoreCase);
            var deaf = channel.Members.Where(m => m.Agent && connected.Contains(m.Name) && !_main.AgentTools.HearsChat(m.Name)).Select(m => m.Name).ToList();
            if (deaf.Count == 0) return null;
            var who = deaf.Count == 1 ? deaf[0] : string.Join(", ", deaf.SkipLast(1)) + " and " + deaf[^1];
            return deaf.Count == 1
                ? $"{who} doesn't hear Chat right now (no chat watcher running), so it reads this when it next does something. It's told how to keep listening."
                : $"{who} don't hear Chat right now (no chat watcher running), so they read this when they next do something. They're told how to keep listening.";
        }
    }

    private bool CanSend() => Channel != null && CanWrite && (Draft.Trim().Length > 0 || Pending.Count > 0);

    [RelayCommand(CanExecute = nameof(CanSend))]
    private void Send()
    {
        if (Channel is not { } channel) return;
        var text = Draft;
        if (Editing is { } editing)
        {
            if (!Act(channel, (r, me) => r.Edit(channel, me, editing.Model.Id, text), "edit", ("message", editing.Model.Id.ToString()), ("text", text))) return;
            Editing = null;
            Draft = "";
            return;
        }
        if (Here.Host is { } host)
        {
            // To another ProJict: the files' contents go along, it keeps them.
            try
            {
                var outgoing = Pending.Select(f => new OutgoingFile(f.Name, f.Data, f.Path)).ToList();
                if (host is HostConnection connection) _main.Guest.ChatPost(connection, channel, text, ReplyTo?.Model.Id, outgoing);
                else if (host is BackChat back) _main.Host.BackPost(back, channel, text, ReplyTo?.Model.Id, outgoing);
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
            {
                _state.StatusText = ex.Message;
                return;
            }
            Draft = "";
            ReplyTo = null;
            Pending.Clear();
            return;
        }
        var files = new List<Attachment>();
        try
        {
            // Copied into the data folder (as for comments), a private conversation's into the cache for them; the originals are only read.
            var cached = channel.Direct;
            files = Pending.Select(f => f.Stored ?? (f.Data != null ? _state.Store.WriteAttachment(f.Name, f.Data, cached) : _state.Store.ImportAttachment(f.Path!, cached))).ToList();
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            _state.StatusText = ex.Message;
            return;
        }
        if (!Try(() => Room.Post(channel, Me, text, ReplyTo?.Model.Id, files))) return;
        Draft = "";
        ReplyTo = null;
        Pending.Clear();
    }

    [RelayCommand]
    private void CancelReply()
    {
        ReplyTo = null;
        if (Editing != null)
        {
            Editing = null;
            Draft = "";
        }
    }

    internal void StartReply(ChatMessageViewModel message)
    {
        Editing = null;
        ReplyTo = message;
        FocusComposer?.Invoke();
    }

    internal void StartEdit(ChatMessageViewModel message)
    {
        ReplyTo = null;
        Editing = message;
        Draft = message.Model.Text;
        FocusComposer?.Invoke();
    }

    /// <summary>Asks the view to put the cursor in the box.</summary>
    public event Action? FocusComposer;

    /// <summary>Up in an empty box edits your last message.</summary>
    public bool EditLast()
    {
        if (Draft.Length > 0 || Messages.LastOrDefault(m => m.CanEdit) is not { } last) return false;
        StartEdit(last);
        return true;
    }

    internal void DeleteMessage(ChatMessageViewModel message)
    {
        if (Channel is { } channel) Act(channel, (r, me) => r.DeleteMessage(channel, me, message.Model.Id), "delete", ("message", message.Model.Id.ToString()));
    }

    [RelayCommand] private void AttachFiles() => AddFiles(DiscussionViewModel.AskForFiles());

    public void AddFiles(IEnumerable<string> paths)
    {
        foreach (var path in paths)
        {
            var info = new FileInfo(path);
            if (!info.Exists || TooBig(info.Name, info.Length)) continue;
            Pending.Add(new PendingFile(info.Name, info.Length, path, null, Pending));
        }
    }

    public void PasteFiles()
    {
        var (files, png) = ClipboardAttachments.Read();
        if (png != null)
        {
            var name = $"Pasted image {DateTime.Now:yyyy-MM-dd HHmmss}.png";
            if (!TooBig(name, png.Length)) Pending.Add(new PendingFile(name, png.Length, null, png, Pending));
        }
        else AddFiles(files);
    }

    private bool TooBig(string name, long size)
    {
        var limit = _state.Settings.ChatFileLimitBytes;
        if (size <= limit) return false;
        _state.StatusText = $"{name} is {AttachmentViewModel.FormatSize(size)}; files in Chat can be up to {AttachmentViewModel.FormatSize(limit)} (Settings › Chat).";
        return true;
    }

    // ───────────── Starting a channel ─────────────

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(ShowChannelPane), nameof(ShowEmptyPane))]
    private bool _isCreating;
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(NewNameProblem))]
    [NotifyCanExecuteChangedFor(nameof(CreateChannelCommand))]
    private string _newName = "";
    [ObservableProperty] private string _newTopic = "";
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(NewInviteOnly))]
    private bool _newOpen = true;
    public bool NewInviteOnly
    {
        get => !NewOpen;
        set
        {
            if (value) NewOpen = false;
        }
    }
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(NewLinkText), nameof(HasNewLink))]
    private ChannelLink? _newLink;

    public string? NewLinkText => NewLink != null ? LinkTextOf(NewLink) : null;
    public bool HasNewLink => NewLink != null;
    public string? NewNameProblem => NewName.Length == 0 ? null : NewRoom.NameProblem(NewName);

    /// <summary>A channel about a host's project is started on that host.</summary>
    private HostConnection? NewHost => NewLink is { } link ? HostOfProject(link.ProjectId) : null;
    private ChatRoom NewRoom => NewHost?.Chat ?? Room;

    internal HostConnection? HostOfProject(Guid projectId) =>
        _state.Projects.FirstOrDefault(p => p.Id == projectId)?.Remote is { } remote
            ? _main.Guest.Connections.FirstOrDefault(c => c.Address == remote.HostKey && c.State == ConnectionState.Connected) : null;

    /// <summary>The form to start a channel, about a project, category or item when given (its name suggested from it).</summary>
    public void BeginNewChannel(ChannelLink? link = null, string? name = null)
    {
        IsShown = true;
        IsBrowsing = false;
        NewLink = link;
        NewName = name ?? "";
        NewTopic = "";
        NewOpen = link != null;
        IsCreating = true;
    }

    [RelayCommand] private void NewChannel() => BeginNewChannel();
    [RelayCommand] private void Unlink() => NewLink = null;
    [RelayCommand] private void CancelNewChannel() => IsCreating = false;

    private bool CanCreate() => NewName.Trim().Length > 0 && NewRoom.NameProblem(NewName) == null;

    [RelayCommand(CanExecute = nameof(CanCreate))]
    private void CreateChannel()
    {
        if (NewHost is { } host && NewLink is { } link)
        {
            _pending = (host, NewName.Trim(), false);
            _main.Guest.ChatOp(host, "create", ("name", NewName.Trim()), ("topic", NewTopic), ("inviteOnly", !NewOpen),
                ("projectId", link.ProjectId.ToString()), ("categoryId", link.CategoryId?.ToString()), ("itemId", link.ItemId?.ToString()));
            IsCreating = false;
            return;
        }
        ChatChannel? created = null;
        if (!Try(() => created = Room.Create(Me, NewName.Trim(), NewOpen ? ChannelAccess.Open : ChannelAccess.InviteOnly, NewLink, NewTopic))) return;
        IsCreating = false;
        Channel = created;
        Refresh();
        _main.RefreshChannelChips();
    }

    // ───────────── Links in messages ─────────────

    internal void OpenSegment(ChatSegment segment)
    {
        switch (segment.Kind)
        {
            case ChatSegmentKind.Channel when (Here.Room.Find(segment.Target!) ?? Room.Find(segment.Target!)) is { } channel:
                Channel = channel;
                Refresh();
                break;
            case ChatSegmentKind.Item when Guid.TryParse(segment.Target, out var id):
                foreach (var project in _state.Projects)
                    if (project.Find(id) is { } loc)
                    {
                        _main.OpenProject(project).Reveal(loc.Item);
                        return;
                    }
                break;
            case ChatSegmentKind.Link:
                ShellLauncher.OpenUrl(segment.Target!);
                break;
            case ChatSegmentKind.Action when segment.Target?.Split(':') is [var id, var word] && Guid.TryParse(id, out var projectId):
                if (_state.Projects.FirstOrDefault(p => p.Id == projectId) is not { OpenShip.State: ShipState.Planned } shipping)
                {
                    _state.StatusText = "That ship plan isn't waiting for you any more.";
                    RebuildMessages();
                    break;
                }
                var board = _main.GetProjectViewModel(shipping);
                if (word == "Approve") board.ApproveShipCommand.Execute(null);
                else board.DeclineShipCommand.Execute(null);
                // The words are plain again once it's decided.
                RebuildMessages();
                break;
        }
    }

    // ───────────── Buttons in agents' messages ─────────────

    /// <summary>Shows the messages afresh (their buttons come and go with what they're about).</summary>
    private void RebuildMessages()
    {
        _messageVms.Clear();
        Messages.Clear();
        SyncMessages();
    }

    /// <summary>
    /// Your own project whose ship plan, posted by the message's agent, waits for you: the channel's project, one the
    /// message names, or the agent's only one. Guests don't approve here, so in another ProJict's channels there's none.
    /// </summary>
    internal Project? ShipWaitingFor(ChatChannel? channel, ChatMessage message)
    {
        if (channel == null || !message.Agent || PlaceOf(channel).Host != null) return null;
        // Only what it wrote about this request, not about an earlier one.
        var waiting = _state.Projects.Where(p => p.Remote == null && p.OpenShip is { State: ShipState.Planned, PlannedBy: { } by } ship &&
                                                 message.At >= ship.RequestedAt &&
                                                 string.Equals(Core.Agents.AgentNaming.Identity(by), message.By, StringComparison.OrdinalIgnoreCase)).ToList();
        return waiting.FirstOrDefault(p => channel.Link?.ProjectId == p.Id) ?? waiting.FirstOrDefault(p => message.Text.Contains(p.Name, StringComparison.OrdinalIgnoreCase))
               ?? (waiting.Count == 1 ? waiting[0] : null);
    }

    [System.Text.RegularExpressions.GeneratedRegex(@"\b(Approve|Decline)\b")]
    private static partial System.Text.RegularExpressions.Regex ShipWords();

    /// <summary>"Approve" and "Decline" in the message's plain text become buttons for that project's ship plan.</summary>
    internal static IReadOnlyList<ChatSegment> WithShipButtons(IReadOnlyList<ChatSegment> segments, Project project) =>
        segments.SelectMany(s => s.Kind != ChatSegmentKind.Text ? [s]
            : ShipWords().Split(s.Text).Where(part => part.Length > 0)
                .Select(part => part is "Approve" or "Decline" ? new ChatSegment(ChatSegmentKind.Action, part, $"{project.Id}:{part}") : new ChatSegment(ChatSegmentKind.Text, part)))
            .ToList();

    /// <summary>Who can be @mentioned in the channel: its members, and you.</summary>
    internal IEnumerable<string> MentionNames => (Channel?.Members.Select(m => m.Name) ?? []).Append(NameIn(Channel));

    // ───────────── @mention suggestions ─────────────

    /// <summary>The channel's members matching the "@na" being typed (not you); Up/Down pick, Tab or Enter puts the name in.</summary>
    public List<MentionChoice> MentionChoices { get; private set; } = [];
    public bool HasMentionChoices => MentionChoices.Count > 0;
    private int _mentionIndex;

    /// <summary>The "@na" being typed just before the caret: where its @ is and what follows it; null when the caret isn't in one.</summary>
    public static (int Start, string Query)? MentionAt(string text, int caret)
    {
        caret = Math.Clamp(caret, 0, text.Length);
        if (caret == 0) return null;
        var at = text.LastIndexOf('@', caret - 1);
        if (at < 0 || (at > 0 && char.IsLetterOrDigit(text[at - 1]))) return null;
        var query = text[(at + 1)..caret];
        if (query.Length > 40 || query.Contains('\n') || query.Contains("  ", StringComparison.Ordinal)) return null;
        return (at, query);
    }

    /// <summary>Replaces the "@na" before the caret with "@Name "; returns the text and where the caret goes.</summary>
    public static (string Text, int Caret) InsertMention(string text, int caret, string name)
    {
        caret = Math.Clamp(caret, 0, text.Length);
        if (MentionAt(text, caret) is not { } found) return (text, caret);
        var after = text[caret..];
        var mention = "@" + name + (after.StartsWith(' ') ? "" : " ");
        return (text[..found.Start] + mention + after, found.Start + mention.Length);
    }

    /// <summary>Called as you type or move the caret in the box.</summary>
    public void UpdateMentions(string text, int caret)
    {
        var names = new List<string>();
        if (Channel != null && MentionAt(text, caret) is { } found)
        {
            var query = found.Query;
            // @everyone last, for a channel with others in it.
            var candidates = Channel.Members.Select(m => m.Name).Where(n => !string.Equals(n, NameIn(Channel), StringComparison.OrdinalIgnoreCase))
                .Distinct(StringComparer.OrdinalIgnoreCase).ToList();
            if (!Channel.Direct && candidates.Count > 1) candidates.Add(ChatText.Everyone);
            // A whole name already typed, followed by a space: done.
            if (!(query.EndsWith(' ') && candidates.Any(n => string.Equals(n, query.TrimEnd(), StringComparison.OrdinalIgnoreCase))))
                names = candidates.Where(n => query.Length == 0 || n.StartsWith(query, StringComparison.OrdinalIgnoreCase) ||
                                              n.Split([' ', '·'], StringSplitOptions.RemoveEmptyEntries).Any(w => w.StartsWith(query, StringComparison.OrdinalIgnoreCase)))
                    .OrderBy(n => n.StartsWith(query, StringComparison.OrdinalIgnoreCase) ? 0 : 1).ThenBy(n => n, StringComparer.CurrentCultureIgnoreCase)
                    .Take(8).ToList();
        }
        if (names.SequenceEqual(MentionChoices.Select(c => c.Name))) return;
        MentionChoices = names.Select(n => new MentionChoice(n)).ToList();
        _mentionIndex = 0;
        if (MentionChoices.Count > 0) MentionChoices[0].IsSelected = true;
        OnPropertyChanged(nameof(MentionChoices));
        OnPropertyChanged(nameof(HasMentionChoices));
    }

    public void ClearMentions()
    {
        if (MentionChoices.Count == 0) return;
        MentionChoices = [];
        OnPropertyChanged(nameof(MentionChoices));
        OnPropertyChanged(nameof(HasMentionChoices));
    }

    /// <summary>Up or Down in the suggestions.</summary>
    public void MoveMention(int by)
    {
        if (MentionChoices.Count == 0) return;
        MentionChoices[_mentionIndex].IsSelected = false;
        _mentionIndex = (_mentionIndex + by + MentionChoices.Count) % MentionChoices.Count;
        MentionChoices[_mentionIndex].IsSelected = true;
    }

    public string? SelectedMention => MentionChoices.Count > 0 ? MentionChoices[_mentionIndex].Name : null;
    internal bool IsChannelName(string name) => Here.Room.Find(name) != null;
    internal Guid? FindItem(string prefix) =>
        _state.Projects.SelectMany(p => p.Walk()).Select(l => l.Item.Id).FirstOrDefault(id => id.ToString().StartsWith(prefix, StringComparison.OrdinalIgnoreCase)) is { } id && id != Guid.Empty ? id : null;

    /// <summary>Shows a message's files: yours from your data folder; a host's fetched from it, like its projects' files.</summary>
    internal ProjectViewModel AttachmentOwner => Here.Host is HostConnection host && host.Copies.Values.FirstOrDefault() is { } copy
        ? _main.GetProjectViewModel(copy.Project) : _main.GetProjectViewModel(_state.Inbox);

    internal bool Try(Action action)
    {
        try
        {
            action();
            return true;
        }
        catch (ChatException ex)
        {
            _state.StatusText = ex.Message;
            return false;
        }
    }

    /// <summary>👍 or 👎 on a message (the same again takes it back); an agent hears it as a yes or no to what it wrote.</summary>
    internal void React(ChatMessageViewModel message, bool up)
    {
        if (Channel is not { } channel) return;
        var take = message.Model.Reactions?.Any(r => string.Equals(r.By, NameIn(channel), StringComparison.OrdinalIgnoreCase) && r.Up == up) == true;
        if (Act(channel, (r, me) => r.React(channel, me, message.Model.Id, up), "react", ("message", message.Model.Id.ToString()), ("up", take ? null : up)))
            RebuildMessages();
    }

    internal void CopyText(string text)
    {
        try
        {
            Clipboard.SetText(text);
            _state.StatusText = "Message copied.";
        }
        catch (System.Runtime.InteropServices.ExternalException)
        {
            _state.StatusText = "Clipboard is busy, try again.";
        }
    }

    internal ChatRoom RoomFor => Here.Room;

    /// <summary>What an agent runs in ("Claude Code"), when it picked a name of its own; else "AI agent".</summary>
    internal string ClientOf(string name) =>
        _state.Settings.AgentNames is { } names && names.FirstOrDefault(n => string.Equals(n.Key, name, StringComparison.OrdinalIgnoreCase)).Value is { } client
            ? client : "AI agent";
    internal ChatActor MeActor => Here.Me;

    /// <summary>What a channel is about; a guest's shared-back channel says it as its ProJict does (its projects may not be here).</summary>
    internal string LinkTextOf(ChatChannel channel) =>
        PlaceOf(channel).Host is BackChat back && back.LinkTexts.TryGetValue(channel.Id, out var text) ? text
        : channel.Link is { } link ? LinkTextOf(link) : "";

    /// <summary>"GRID2VR › HUD › “crash when pausing”".</summary>
    internal string LinkTextOf(ChannelLink link)
    {
        if (_state.Projects.FirstOrDefault(p => p.Id == link.ProjectId) is not { } project) return "a deleted project";
        var text = project.Name;
        if (link.CategoryId is { } c && project.Categories.FirstOrDefault(x => x.Id == c) is { } category) text += " › " + category.Name;
        if (link.ItemId is { } i && project.Find(i) is { } loc)
        {
            var line = loc.Item.DisplayText().Replace("\r\n", "\n").Split('\n')[0];
            text += $" › “{(line.Length > 50 ? line[..50] + "…" : line)}”";
        }
        return text;
    }

    internal static string Count(int n, string what) => n == 1 ? $"1 {what}" : $"{n} {what}s";
}

/// <summary>Someone to invite: a name, and how the list shows it ("Bob · guest").</summary>
public sealed record InviteChoice(string Name, string Label)
{
    public override string ToString() => Name;
}

/// <summary>A connected ProJict's channels, under its owner's name.</summary>
/// <summary>A heading in Chat's sidebar with its channels under it. A click folds them away, leaving only the open one and those with
/// something new; folded ones stay folded (AppSettings.FoldedChatSections).</summary>
public sealed partial class ChatSection(AppState state, string key, string title, string hint, bool shared = false, bool foldedAtFirst = false) : ObservableObject
{
    private List<ChannelRowViewModel> _all = [];
    public string Key => key;
    public string Title => title;
    /// <summary>Another ProJict's, shown with a link mark.</summary>
    public bool IsShared => shared;
    public bool HasRows => _all.Count > 0;
    /// <summary>Open, or folded; one that starts folded (the archive) is kept as "open:key" once you open it.</summary>
    public bool IsOpen
    {
        get => foldedAtFirst ? state.AppSettings.FoldedChatSections?.Contains("open:" + key) == true : state.AppSettings.FoldedChatSections?.Contains(key) != true;
        set
        {
            if (value == IsOpen) return;
            var folded = state.AppSettings.FoldedChatSections ??= [];
            var entry = foldedAtFirst ? "open:" + key : key;
            if (value == foldedAtFirst) folded.Add(entry);
            else folded.Remove(entry);
            if (folded.Count == 0) state.AppSettings.FoldedChatSections = null;
            state.SaveAppSettings();
            Changed();
        }
    }
    /// <summary>All of them while it's open; folded, the open channel and those with something unread.</summary>
    public List<ChannelRowViewModel> Rows { get; private set; } = [];
    /// <summary>Folded: how many it hides.</summary>
    public string HiddenText => _all.Count - Rows.Count is > 0 and var n ? $"{n} more" : "";
    public string ToolTip => hint + "\n" + (IsOpen ? "Click to fold it away" : "Click to show them all");

    internal ChatSection Set(List<ChannelRowViewModel> rows)
    {
        _all = rows;
        Changed();
        return this;
    }

    private void Changed()
    {
        Rows = IsOpen ? _all : _all.Where(r => r.IsSelected || r.HasUnread).ToList();
        foreach (var name in new[] { nameof(IsOpen), nameof(Rows), nameof(HiddenText), nameof(HasRows), nameof(ToolTip) })
            OnPropertyChanged(name);
    }

    [RelayCommand]
    private void Toggle() => IsOpen = !IsOpen;
}

/// <summary>A #channel chip on a project, category or item it's linked to; a click opens it in Chat.</summary>
public sealed partial class ChannelChip(ChatChannel channel, MainViewModel main, bool unread)
{
    public ChatChannel Channel => channel;
    public string Text => "#" + channel.Name;
    public bool HasUnread => unread;
    public string ToolTip => $"Open #{channel.Name} in Chat" + (channel.Topic != null ? $"\n{channel.Topic}" : "") + (unread ? "\nNew messages" : "");

    [RelayCommand] private void Open() => main.ShowChannel(channel);
}

/// <summary>A channel in the list: its name, what it's about, and how many messages wait for you.</summary>
public sealed partial class ChannelRowViewModel(ChatChannel model, ChatViewModel owner)
{
    public ChatChannel Model => model;
    public string Name => ChatRoom.Title(model, owner.NameIn(model));
    /// <summary>Whose ProJict a channel from another one is in ("iztok4090"), shown next to its name; null for yours.</summary>
    public string? Source => owner.PlaceOf(model).Host?.OwnerName;
    public string SourceTag => Source != null ? "· " + Source : "";
    public bool IsDirect => model.Direct;
    /// <summary>Your own private conversation (not another ProJict's): it can go in the archive, or come out.</summary>
    public bool CanArchive => model.Direct && owner.PlaceOf(model).Host == null && ChatRoom.Member(model, owner.NameIn(model)) != null;
    public bool IsArchived => ChatRoom.IsArchived(model, owner.NameIn(model));
    [RelayCommand] private void Archive() => owner.SetArchived(model, true);
    [RelayCommand] private void Unarchive() => owner.SetArchived(model, false);
    /// <summary>Its topic, else what it's about; for a private conversation, the last thing said.</summary>
    public string? Subtitle => model.Direct
        ? model.Messages.LastOrDefault(m => !m.Notice && !m.Deleted) is { } last ? Snippet(last.Text) : "No messages yet"
        : model.Topic ?? (model.Link != null ? owner.LinkTextOf(model) : null);
    private static string Snippet(string text)
    {
        var line = text.Replace("\r\n", "\n").Split('\n')[0];
        return line.Length > 60 ? line[..60] + "…" : line;
    }
    public bool IsInviteOnly => model.Access == ChannelAccess.InviteOnly;
    public bool IsSelected => owner.Channel == model;
    /// <summary>A channel's topic shows only while it's open (it's in the tooltip too); a private conversation's last line always.</summary>
    public bool ShowSubtitle => Subtitle != null && (model.Direct || IsSelected);
    public int Unread { get; } = ChatRoom.Unread(model, owner.NameIn(model)).Count();
    public bool HasUnread => Unread > 0;
    public bool HasMention { get; } = model.Direct ? ChatRoom.Unread(model, owner.NameIn(model)).Any() : ChatRoom.UnreadMentions(model, owner.NameIn(model)).Any();
    public string ToolTip => string.Join("\n", new[]
    {
        model.Direct ? "Private conversation" : model.Topic, model.Link != null ? "About " + owner.LinkTextOf(model) : null,
        model.Direct ? null : IsInviteOnly ? "Invite only" : "Open",
        Unread > 0 ? ChatViewModel.Count(Unread, "new message") : null,
    }.Where(s => s != null));
}

/// <summary>
/// A private conversation between two agents, in Agents' private chats: you read it (a click opens it); right-click to
/// suspend or resume it, delete it, or write to either of them.
/// </summary>
public sealed partial class AgentChatRow(ChatChannel model, ChatViewModel owner)
{
    public ChatChannel Model => model;
    public string Name => string.Join(" & ", model.Members.Select(m => m.Name));
    public string First => model.Members.ElementAtOrDefault(0)?.Name ?? "";
    public string Second => model.Members.ElementAtOrDefault(1)?.Name ?? "";
    public bool IsSuspended => model.Suspended;
    public bool IsSelected => owner.Channel == model;
    public string Subtitle
    {
        get
        {
            var messages = model.Messages.Where(m => !m.Notice && !m.Deleted).ToList();
            var last = messages.LastOrDefault()?.At;
            var when = last is not { } at ? "nothing said yet"
                : "last " + (at.Date == DateTime.Today ? at.ToString("HH:mm", CultureInfo.CurrentCulture) : at.ToString("d MMM", CultureInfo.CurrentCulture));
            return (model.Suspended ? "Suspended · " : "") + $"{ChatViewModel.Count(messages.Count, "message")} · {when}";
        }
    }
    public string ToolTip => $"Between {First} and {Second}: you read it, it isn't yours to write in." + (model.Suspended ? "\nSuspended: neither can write in it." : "") +
                             "\nRight-click to suspend, resume or delete it, or to write to either of them.";

    [RelayCommand] private void Open() => owner.OpenChannel(model);
    [RelayCommand] private void Suspend() => owner.Suspend(model, true);
    [RelayCommand] private void Resume() => owner.Suspend(model, false);
    [RelayCommand] private void Delete() => owner.DeleteAgentChat(model);
    [RelayCommand] private void WriteToFirst() => owner.OpenDirect(First, agent: model.Members.ElementAtOrDefault(0)?.Agent ?? true);
    [RelayCommand] private void WriteToSecond() => owner.OpenDirect(Second, agent: model.Members.ElementAtOrDefault(1)?.Agent ?? true);
}

/// <summary>A channel in Browse channels: its topic, what it's about, members and last message; Open, or Join if you're not in it.</summary>
public sealed partial class BrowseRowViewModel(ChatChannel model, ChatViewModel owner)
{
    public ChatChannel Model => model;
    public string Name => "#" + model.Name;
    public string Topic => model.Topic ?? "No topic yet.";
    public bool HasTopic => model.Topic != null;
    public string? LinkText => model.Link != null ? "About " + owner.LinkTextOf(model) : null;
    public string AccessText => model.Access == ChannelAccess.InviteOnly ? "Invite only" : "Open";
    public string Details
    {
        get
        {
            var last = model.Messages.LastOrDefault(m => !m.Notice)?.At;
            var when = last is { } at ? at.Date == DateTime.Today ? $"last message {at:HH:mm}" : $"last message {at.ToString("d MMM", CultureInfo.CurrentCulture)}" : "no messages yet";
            return $"{ChatViewModel.Count(model.Members.Count, "member")} · {when} · started by {model.CreatedBy}";
        }
    }
    public bool IsMember => ChatRoom.Member(model, owner.MyName) != null;
    public string ActionText => IsMember ? "Open" : "Join";

    [RelayCommand] private void Open() => owner.JoinAndOpen(model);
}

/// <summary>A name suggested after @ in the message box.</summary>
public sealed partial class MentionChoice(string name) : ObservableObject
{
    public string Name => name;
    public WorkerBadge Badge => WorkerBadge.For([name])[0];
    [ObservableProperty] private bool _isSelected;
}

/// <summary>A member: their badge and name, whether they moderate or are muted, and what a moderator can do to them.</summary>
public sealed partial class ChatMemberViewModel(ChatMember model, ChatChannel channel, ChatViewModel owner)
{
    public ChatMember Model => model;
    public string Name => model.Name;
    public bool IsAgent => model.Agent;
    public WorkerBadge Badge => WorkerBadge.For([model.Name])[0];
    private ChatViewModel.ChatPlace Place => owner.PlaceOf(channel);
    private bool IsOwner => !model.Agent && Place.Room.IsOwner(model.Name);
    private bool IsMe => string.Equals(model.Name, Place.Me.Name, StringComparison.OrdinalIgnoreCase);
    public string RoleText => string.Join(" · ", new[]
    {
        IsOwner ? (Place.Me.Owner ? "you · owner" : "owner") : null,
        !IsOwner && IsMe ? "you" : null, IsOwner ? null : model.Agent ? owner.ClientOf(model.Name) : Place.Host == null && owner.IsGuestName(model.Name) ? "guest" : null,
        !IsOwner && model.Moderator ? "moderator" : null,
        Presence != PresenceState.Online ? Core.Chat.Presence.Word(Presence) : null,
    }.Where(s => s != null));

    /// <summary>Online, away (idle a while) or offline (not connected), worked out when the members were listed.</summary>
    public PresenceState Presence { get; } = owner.PresenceOf(owner.PlaceOf(channel), model);
    public bool IsAway => Presence == PresenceState.Away;
    public bool IsOffline => Presence == PresenceState.Offline;
    public string PresenceText => Presence switch
    {
        PresenceState.Online => "Online",
        PresenceState.Away => model.Agent ? "Away: hasn't used ProJict's tools for a while" : "Away: hasn't done anything on their computer for a while",
        _ => model.Agent ? "Offline: not connected to ProJict" : "Offline: their ProJict isn't connected",
    };
    public string? MutedText => Place.Room.MutedUntil(channel, model.Name) is { } until
        ? until == DateTime.MaxValue ? "muted until unmuted" : $"muted until {until.ToString("HH:mm", CultureInfo.CurrentCulture)}" : null;
    public bool IsMuted => MutedText != null;
    /// <summary>You moderate the channel and they're someone else than you.</summary>
    public bool CanModerate => ChatRoom.IsModerator(channel, Place.Me) && owner.MayWrite(channel) && !IsOwner && !IsMe;
    public string ModeratorHeader => model.Moderator ? "Remove as moderator" : "Make moderator";
    /// <summary>Anyone but you can be written to privately.</summary>
    public bool CanMessage => !IsMe && owner.MayWrite(channel);

    [RelayCommand] private void PrivateMessage() => owner.OpenDirectIn(channel, model.Name, model.Agent);

    [RelayCommand]
    private void Mute(string? minutes)
    {
        var span = int.TryParse(minutes, out var m) && m > 0 ? TimeSpan.FromMinutes(m) : (TimeSpan?)null;
        owner.Act(channel, (r, me) => r.Mute(channel, me, model.Name, span), "mute", ("who", model.Name), ("minutes", span is { } s ? (int)s.TotalMinutes : 0));
    }
    [RelayCommand] private void Unmute() => owner.Act(channel, (r, me) => r.Unmute(channel, me, model.Name), "unmute", ("who", model.Name));
    [RelayCommand] private void Kick() => owner.Act(channel, (r, me) => r.Kick(channel, me, model.Name), "kick", ("who", model.Name));
    [RelayCommand]
    private void ToggleModerator() =>
        owner.Act(channel, (r, me) => r.SetModerator(channel, me, model.Name, !model.Moderator), "moderator", ("who", model.Name), ("on", !model.Moderator));
}

/// <summary>A piece of a message to show; channels, items and web links open with a click.</summary>
public sealed partial class ChatSegmentViewModel(ChatSegment segment, ChatViewModel owner)
{
    public ChatSegmentKind Kind => segment.Kind;
    public string Text => segment.Text;
    public bool IsLink => segment.Kind is ChatSegmentKind.Channel or ChatSegmentKind.Item or ChatSegmentKind.Link;
    public bool IsMention => segment.Kind == ChatSegmentKind.Mention;
    public bool IsAction => segment.Kind == ChatSegmentKind.Action;
    public bool IsPrimaryAction => IsAction && segment.Text == "Approve";
    public string? ToolTip => segment.Kind switch
    {
        ChatSegmentKind.Action => segment.Text == "Approve" ? "Approve the ship plan: the agent runs it" : "Decline the ship plan: nothing of it runs",
        ChatSegmentKind.Channel => "Open the channel",
        ChatSegmentKind.Item => "Show the item on its board",
        ChatSegmentKind.Link => segment.Target,
        _ => null,
    };

    [RelayCommand] private void Open() => owner.OpenSegment(segment);
}

/// <summary>
/// One message: who wrote it and when (once for a run of messages by the same person), the message it answers, its
/// text with links, its files, and Reply, Edit, Delete and Copy. Lines from ProJict ("Codex joined") show small.
/// </summary>
public sealed partial class ChatMessageViewModel : ObservableObject
{
    private readonly ChatViewModel _owner;

    public ChatMessageViewModel(ChatMessage model, ChatViewModel owner)
    {
        Model = model;
        _owner = owner;
    }

    public ChatMessage Model { get; }
    public string By => Model.By;
    public bool IsAgent => Model.Agent;
    public bool IsNotice => Model.Notice;
    public string NoticeText => Model.Text;
    public WorkerBadge Badge => WorkerBadge.For([Model.By])[0];
    public string Time => Model.At.ToString("HH:mm", CultureInfo.CurrentCulture);
    public string WhenToolTip => Model.At.ToString("dddd d MMMM yyyy, HH:mm", CultureInfo.CurrentCulture);
    public bool IsMine => !Model.Agent && string.Equals(Model.By, _owner.NameIn(_owner.Channel), StringComparison.OrdinalIgnoreCase);
    public bool CanEdit => IsMine && !Model.Notice && !Model.Deleted;
    public bool CanDelete => !Model.Notice && !Model.Deleted && (IsMine || _owner.IsModerator);

    public List<ChatSegmentViewModel> Segments { get; private set; } = [];
    public bool IsDeleted { get; private set; }
    public string? EditedText { get; private set; }
    public bool MentionsMe { get; private set; }
    public List<AttachmentViewModel> Attachments { get; private set; } = [];
    public bool HasAttachments => Attachments.Count > 0;

    /// <summary>The first of a run by the same person (within five minutes): it shows the name and badge.</summary>
    public bool ShowHeader { get; private set; }
    public string? DayHeader { get; private set; }
    /// <summary>The first message you hadn't read when you opened the channel: a "New" line goes above it.</summary>
    public bool IsFirstUnread { get; private set; }

    public bool HasReply { get; private set; }
    public string? ReplyBy { get; private set; }
    public string? ReplySnippet { get; private set; }

    [ObservableProperty] private bool _isHighlighted;

    public void Refresh(ChatMessage? previous, bool firstUnread)
    {
        var day = previous == null || previous.At.Date != Model.At.Date
            ? ActivityPeriods.Label(Model.At.Date, ActivityGrouping.Day, DateTime.Today, CultureInfo.CurrentCulture) : null;
        DayHeader = day;
        IsFirstUnread = firstUnread;
        ShowHeader = !Model.Notice && (day != null || firstUnread || previous == null || previous.Notice ||
                                       !string.Equals(previous.By, Model.By, StringComparison.OrdinalIgnoreCase) || Model.At - previous.At > TimeSpan.FromMinutes(5) ||
                                       Model.ReplyTo != null);
        IsDeleted = Model.Deleted;
        EditedText = Model.EditedAt != null && !Model.Deleted ? "(edited)" : null;
        MentionsMe = !IsMine && _owner.Channel is { } here && ChatRoom.Mentions(here, Model, _owner.NameIn(here));
        var parsed = ChatText.Parse(Model.Text, _owner.MentionNames, _owner.IsChannelName, _owner.FindItem);
        if (_owner.ShipWaitingFor(_owner.Channel, Model) is { } shipping) parsed = ChatViewModel.WithShipButtons(parsed, shipping);
        Segments = Model.Deleted ? [new ChatSegmentViewModel(new ChatSegment(ChatSegmentKind.Text, "(deleted)"), _owner)]
            : parsed.Select(s => new ChatSegmentViewModel(s, _owner)).ToList();
        var owner = _owner.AttachmentOwner;
        Attachments = (Model.Attachments ?? []).Select(a => new AttachmentViewModel(owner, a, () => { })).ToList();
        var replied = Model.ReplyTo is { } r ? _owner.Channel?.Messages.FirstOrDefault(m => m.Id == r) : null;
        HasReply = replied != null;
        ReplyBy = replied?.By;
        ReplySnippet = replied == null ? null : replied.Deleted ? "(deleted)" : Snippet(replied.Text);
        OnPropertyChanged(string.Empty);
    }

    private static string Snippet(string text)
    {
        var line = text.Replace("\r\n", "\n").Split('\n')[0];
        return line.Length > 90 ? line[..90] + "…" : line;
    }

    // Thumbs: yours shows filled; on an agent's message it's a yes or no it hears.
    public int Ups => Model.Reactions?.Count(r => r.Up) ?? 0;
    public int Downs => Model.Reactions?.Count(r => !r.Up) ?? 0;
    public bool HasUps => Ups > 0;
    public bool HasDowns => Downs > 0;
    public bool CanReact => !Model.Notice && !Model.Deleted && !IsMine && _owner.MayWrite(_owner.Channel);
    private ChatReaction? Mine => Model.Reactions?.FirstOrDefault(r => string.Equals(r.By, _owner.NameIn(_owner.Channel), StringComparison.OrdinalIgnoreCase));
    public bool IsUpMine => Mine is { Up: true };
    public bool IsDownMine => Mine is { Up: false };
    public string UpsToolTip => "Yes: " + string.Join(", ", Model.Reactions?.Where(r => r.Up).Select(r => r.By) ?? []);
    public string DownsToolTip => "No: " + string.Join(", ", Model.Reactions?.Where(r => !r.Up).Select(r => r.By) ?? []);
    [RelayCommand] private void ThumbUp() => _owner.React(this, true);
    [RelayCommand] private void ThumbDown() => _owner.React(this, false);

    [RelayCommand] private void Reply() => _owner.StartReply(this);
    [RelayCommand] private void Edit() => _owner.StartEdit(this);
    [RelayCommand] private void Delete() => _owner.DeleteMessage(this);
    [RelayCommand] private void Copy() => _owner.CopyText(Model.Text);
    /// <summary>Right-click the author › Private message.</summary>
    public bool CanMessageAuthor => !Model.Notice && !IsMine && _owner.MayWrite(_owner.Channel);
    [RelayCommand] private void PrivateMessage() => _owner.OpenDirectIn(_owner.Channel, Model.By, Model.Agent);

    [RelayCommand] private void ShowReplied()
    {
        if (Model.ReplyTo is { } id) _owner.HighlightMessage(id);
    }
}
