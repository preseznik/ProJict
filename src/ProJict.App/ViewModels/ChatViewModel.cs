using System.Collections.ObjectModel;
using System.Globalization;
using System.IO;
using System.Windows;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using ProJict.App.Services;
using ProJict.Core.Chat;
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
    private const int PageSize = 200;
    private readonly AppState _state;
    private readonly MainViewModel _main;
    private readonly Dictionary<Guid, ChatMessageViewModel> _messageVms = [];
    private int _shown = PageSize;
    private bool _syncing;

    public ChatViewModel(AppState state, MainViewModel main)
    {
        _state = state;
        _main = main;
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
    public bool HasNoChannels => Room.Channels.Count == 0;

    [ObservableProperty] private ChatChannel? _channel;
    public bool HasChannel => Channel != null;
    /// <summary>The middle shows the open channel, the form to start one, or a hint when there's neither.</summary>
    public bool ShowChannelPane => Channel != null && !IsCreating;
    public bool ShowEmptyPane => Channel == null && !IsCreating;

    /// <summary>Unread messages in your channels, and whether one @mentions you.</summary>
    public int UnreadCount => Room.Channels.Sum(c => ChatRoom.Unread(c, MyName).Count());
    public bool HasMention => Room.Channels.Any(c => ChatRoom.UnreadMentions(c, MyName).Any());
    public string UnreadToolTip
    {
        get
        {
            var lines = Room.Channels.Select(c => (c, n: ChatRoom.Unread(c, MyName).Count(), m: ChatRoom.UnreadMentions(c, MyName).Count()))
                .Where(x => x.n > 0).Select(x => $"#{x.c.Name}: {Count(x.n, "new message")}" + (x.m > 0 ? $" ({(x.m == 1 ? "one mentions" : $"{x.m} mention")} you)" : "")).ToList();
            return lines.Count == 0 ? "Live chat with AI agents in #channels" : string.Join("\n", lines);
        }
    }

    /// <summary>Opens the view, at a channel (and a message in it) or where you were.</summary>
    public void Show(ChatChannel? channel = null, Guid? message = null)
    {
        IsShown = true;
        IsCreating = false;
        if (channel != null && channel != Channel) Channel = channel;
        else if (Channel == null || !Room.Channels.Contains(Channel)) Channel = YourChannels.FirstOrDefault()?.Model ?? OtherChannels.FirstOrDefault()?.Model;
        else
        {
            MarkNew();
            ReadIfLooking();
        }
        Refresh();
        if (message is { } id && _messageVms.TryGetValue(id, out var vm)) Highlight(vm);
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
        if (Channel == null || ChatRoom.Member(Channel, MyName) is not { } me || !ChatRoom.Unread(Channel, MyName).Any()) return;
        UnreadFrom = me.LastRead ?? DateTime.MinValue;
        SyncMessages();
    }

    partial void OnChannelChanged(ChatChannel? value)
    {
        _shown = PageSize;
        _messageVms.Clear();
        Messages.Clear();
        ReplyTo = null;
        Editing = null;
        Draft = "";
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
        if (Channel != null && IsShown && IsLooking() && Room.MarkRead(Channel, MyName)) Refresh();
    }

    /// <summary>Rebuilds the channel lists and tells the sidebar.</summary>
    public void Refresh()
    {
        var mine = new List<ChannelRowViewModel>();
        var others = new List<ChannelRowViewModel>();
        foreach (var channel in Room.Channels.OrderBy(c => c.Name, StringComparer.Ordinal))
            (ChatRoom.Member(channel, MyName) != null ? mine : others).Add(new ChannelRowViewModel(channel, this));
        YourChannels = mine;
        OtherChannels = others;
        foreach (var name in new[] { nameof(YourChannels), nameof(OtherChannels), nameof(HasOtherChannels), nameof(HasNoChannels), nameof(UnreadCount), nameof(HasMention), nameof(UnreadToolTip) })
            OnPropertyChanged(name);
        CountsChanged?.Invoke();
    }

    [RelayCommand]
    private void Open(ChannelRowViewModel? row)
    {
        if (row == null) return;
        IsCreating = false;
        Channel = row.Model;
        Refresh();
    }

    // ───────────── The open channel ─────────────

    public ObservableCollection<ChatMessageViewModel> Messages { get; } = [];
    public List<ChatMemberViewModel> Members { get; private set; } = [];
    public bool HasEarlier { get; private set; }

    public string Title => Channel != null ? "#" + Channel.Name : "";
    public bool IsInviteOnly => Channel?.Access == ChannelAccess.InviteOnly;
    public string AccessText => IsInviteOnly ? "Invite only" : "Open";
    public string AccessToolTip => IsInviteOnly ? "Only members see it; moderators invite others." : "Anyone who can see its project can read and join it.";
    public string? LinkText => Channel?.Link is { } link ? LinkTextOf(link) : null;
    public string LinkSeparator => LinkText != null ? " · " : "";
    public string StartedText => Channel == null ? "" : $"Started by {Channel.CreatedBy} on {Channel.CreatedAt.ToString("d MMMM yyyy", CultureInfo.CurrentCulture)}";
    public bool IsMember => Channel != null && ChatRoom.Member(Channel, MyName) != null;
    public bool IsModerator => Channel != null && ChatRoom.IsModerator(Channel, Me);
    public string MembersText => Channel == null ? "" : Count(Channel.Members.Count, "member");
    public bool CanWrite => Channel != null && (IsMember || Channel.Access == ChannelAccess.Open);

    /// <summary>The topic; moderators (you always) change it in place.</summary>
    public string TopicText
    {
        get => Channel?.Topic ?? "";
        set
        {
            if (Channel == null || (value ?? "").Trim() == (Channel.Topic ?? "")) return;
            Try(() => Room.SetTopic(Channel, Me, value));
        }
    }

    [ObservableProperty] private bool _showMembers = true;

    private void SyncChannel()
    {
        foreach (var name in new[] { nameof(HasChannel), nameof(Title), nameof(IsInviteOnly), nameof(AccessText), nameof(AccessToolTip), nameof(LinkText), nameof(LinkSeparator), nameof(StartedText),
                     nameof(IsMember), nameof(IsModerator), nameof(MembersText), nameof(CanWrite), nameof(TopicText), nameof(ComposerNote), nameof(InviteChoices),
                     nameof(CanInvite), nameof(ShowChannelPane), nameof(ShowEmptyPane), nameof(IsOwner) })
            OnPropertyChanged(name);
        Members = Channel == null ? [] : Channel.Members
            .OrderByDescending(m => !m.Agent && Room.IsOwner(m.Name)).ThenByDescending(m => m.Moderator).ThenBy(m => m.Name, StringComparer.CurrentCultureIgnoreCase)
            .Select(m => new ChatMemberViewModel(m, Channel, this)).ToList();
        OnPropertyChanged(nameof(Members));
        SyncMessages();
        SendCommand.NotifyCanExecuteChanged();
    }

    /// <summary>The newest messages (more with Show earlier); new ones are appended, edited ones refreshed.</summary>
    private void SyncMessages()
    {
        var models = Channel?.Messages ?? [];
        var shown = models.Skip(Math.Max(0, models.Count - _shown)).ToList();
        HasEarlier = shown.Count < models.Count;
        OnPropertyChanged(nameof(HasEarlier));
        var wanted = shown.Select(m => _messageVms.TryGetValue(m.Id, out var vm) ? vm : _messageVms[m.Id] = new ChatMessageViewModel(m, this)).ToList();
        ChatMessage? previous = null;
        var newLine = false;
        foreach (var vm in wanted)
        {
            var first = !newLine && UnreadFrom is { } from && vm.Model.At > from && !vm.Model.Notice && !vm.IsMine;
            newLine |= first;
            vm.Refresh(previous, first);
            previous = vm.Model;
        }
        var keep = Messages.Count <= wanted.Count && Messages.Select((m, i) => wanted[i] == m).All(same => same);
        if (!keep) Messages.Clear();
        foreach (var vm in wanted.Skip(Messages.Count)) Messages.Add(vm);
        MessagesChanged?.Invoke();
    }

    /// <summary>Messages were added or changed: the view scrolls to the newest when it was there.</summary>
    public event Action? MessagesChanged;

    /// <summary>Asks the view to bring a message into sight.</summary>
    public event Action<ChatMessageViewModel>? Reveal;

    [RelayCommand]
    private void ShowEarlier()
    {
        _shown += PageSize;
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
        if (Channel == null) return;
        while (!_messageVms.ContainsKey(id) && HasEarlier) ShowEarlier();
        if (_messageVms.TryGetValue(id, out var vm)) Highlight(vm);
    }

    [RelayCommand]
    private void Join()
    {
        if (Channel is { } channel) Try(() => Room.Join(channel, Me));
    }

    [RelayCommand]
    private void Leave()
    {
        if (Channel is { } channel) Try(() => Room.Leave(channel, Me));
    }

    [RelayCommand]
    private void ToggleAccess()
    {
        if (Channel is { } channel) Try(() => Room.SetAccess(channel, Me, IsInviteOnly ? ChannelAccess.Open : ChannelAccess.InviteOnly));
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
    }

    // ───────────── Inviting ─────────────

    [ObservableProperty] private string _inviteName = "";
    /// <summary>Moderators invite; in an Open channel any member may.</summary>
    public bool CanInvite => Channel != null && (IsModerator || (IsMember && Channel.Access == ChannelAccess.Open));
    public bool IsOwner => Me.Owner;
    public IReadOnlyList<string> InviteChoices => Channel == null ? [] :
        _main.AgentTools.KnownAgentNames().Where(n => ChatRoom.Member(Channel, n) == null).ToList();

    [RelayCommand]
    private void Invite()
    {
        if (Channel is not { } channel || InviteName.Trim().Length == 0) return;
        var name = InviteName.Trim().TrimStart('@');
        var agent = !Room.IsOwner(name);
        if (Try(() => Room.Invite(channel, Me, name, agent))) InviteName = "";
        OnPropertyChanged(nameof(InviteChoices));
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
        : !CanWrite ? "Only members write here."
        : Room.MutedUntil(Channel, MyName) is { } until && !Me.Owner ? $"You're muted until {until:HH:mm}."
        : null;

    private bool CanSend() => Channel != null && CanWrite && (Draft.Trim().Length > 0 || Pending.Count > 0);

    [RelayCommand(CanExecute = nameof(CanSend))]
    private void Send()
    {
        if (Channel is not { } channel) return;
        var text = Draft;
        if (Editing is { } editing)
        {
            if (!Try(() => Room.Edit(channel, Me, editing.Model.Id, text))) return;
            Editing = null;
            Draft = "";
            return;
        }
        var files = new List<Attachment>();
        try
        {
            // Copied into the data folder (as for comments); the originals are only read.
            files = Pending.Select(f => f.Stored ?? (f.Data != null ? _state.Store.WriteAttachment(f.Name, f.Data) : _state.Store.ImportAttachment(f.Path!))).ToList();
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
        if (Channel is { } channel) Try(() => Room.DeleteMessage(channel, Me, message.Model.Id));
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
        var limit = _state.Settings.CommentFileLimitBytes;
        if (size <= limit) return false;
        _state.StatusText = $"{name} is {AttachmentViewModel.FormatSize(size)}; files in chat can be up to {AttachmentViewModel.FormatSize(limit)} (Settings › Comments & history).";
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
    public string? NewNameProblem => NewName.Length == 0 ? null : Room.NameProblem(NewName);

    /// <summary>The form to start a channel, about a project, category or item when given (its name suggested from it).</summary>
    public void BeginNewChannel(ChannelLink? link = null, string? name = null)
    {
        IsShown = true;
        NewLink = link;
        NewName = name ?? "";
        NewTopic = "";
        NewOpen = link != null;
        IsCreating = true;
    }

    [RelayCommand] private void NewChannel() => BeginNewChannel();
    [RelayCommand] private void Unlink() => NewLink = null;
    [RelayCommand] private void CancelNewChannel() => IsCreating = false;

    private bool CanCreate() => NewName.Trim().Length > 0 && Room.NameProblem(NewName) == null;

    [RelayCommand(CanExecute = nameof(CanCreate))]
    private void CreateChannel()
    {
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
            case ChatSegmentKind.Channel when Room.Find(segment.Target!) is { } channel:
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
        }
    }

    /// <summary>Who can be @mentioned in the channel: its members, and you.</summary>
    internal IEnumerable<string> MentionNames => (Channel?.Members.Select(m => m.Name) ?? []).Append(MyName);
    internal bool IsChannelName(string name) => Room.Find(name) != null;
    internal Guid? FindItem(string prefix) =>
        _state.Projects.SelectMany(p => p.Walk()).Select(l => l.Item.Id).FirstOrDefault(id => id.ToString().StartsWith(prefix, StringComparison.OrdinalIgnoreCase)) is { } id && id != Guid.Empty ? id : null;

    internal ProjectViewModel AttachmentOwner => _main.GetProjectViewModel(_state.Inbox);

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

    internal ChatRoom RoomFor => Room;
    internal ChatActor MeActor => Me;

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
    public string Name => "#" + model.Name;
    public string? LinkText => model.Link != null ? owner.LinkTextOf(model.Link) : model.Topic;
    public bool IsInviteOnly => model.Access == ChannelAccess.InviteOnly;
    public bool IsSelected => owner.Channel == model;
    public int Unread { get; } = ChatRoom.Unread(model, owner.MyName).Count();
    public bool HasUnread => Unread > 0;
    public bool HasMention { get; } = ChatRoom.UnreadMentions(model, owner.MyName).Any();
    public string ToolTip => (model.Topic ?? (IsInviteOnly ? "Invite only" : "Open")) + (Unread > 0 ? $"\n{ChatViewModel.Count(Unread, "new message")}" : "");
}

/// <summary>A member: their badge and name, whether they moderate or are muted, and what a moderator can do to them.</summary>
public sealed partial class ChatMemberViewModel(ChatMember model, ChatChannel channel, ChatViewModel owner)
{
    public ChatMember Model => model;
    public string Name => model.Name;
    public bool IsAgent => model.Agent;
    public WorkerBadge Badge => WorkerBadge.For([model.Name])[0];
    private bool IsOwner => !model.Agent && owner.RoomFor.IsOwner(model.Name);
    public string RoleText => IsOwner ? "you · owner" : model.Moderator ? "moderator" : model.Agent ? "AI agent" : "";
    public string? MutedText => owner.RoomFor.MutedUntil(channel, model.Name) is { } until
        ? until == DateTime.MaxValue ? "muted until unmuted" : $"muted until {until.ToString("HH:mm", CultureInfo.CurrentCulture)}" : null;
    public bool IsMuted => MutedText != null;
    /// <summary>You moderate the channel and they're someone else than you.</summary>
    public bool CanModerate => ChatRoom.IsModerator(channel, owner.MeActor) && !IsOwner;
    public string ModeratorHeader => model.Moderator ? "Remove as moderator" : "Make moderator";

    [RelayCommand] private void Mute(string? minutes) =>
        owner.Try(() => owner.RoomFor.Mute(channel, owner.MeActor, model.Name, int.TryParse(minutes, out var m) && m > 0 ? TimeSpan.FromMinutes(m) : null));
    [RelayCommand] private void Unmute() => owner.Try(() => owner.RoomFor.Unmute(channel, owner.MeActor, model.Name));
    [RelayCommand] private void Kick() => owner.Try(() => owner.RoomFor.Kick(channel, owner.MeActor, model.Name));
    [RelayCommand] private void ToggleModerator() => owner.Try(() => owner.RoomFor.SetModerator(channel, owner.MeActor, model.Name, !model.Moderator));
}

/// <summary>A piece of a message to show; channels, items and web links open with a click.</summary>
public sealed partial class ChatSegmentViewModel(ChatSegment segment, ChatViewModel owner)
{
    public ChatSegmentKind Kind => segment.Kind;
    public string Text => segment.Text;
    public bool IsLink => segment.Kind is ChatSegmentKind.Channel or ChatSegmentKind.Item or ChatSegmentKind.Link;
    public bool IsMention => segment.Kind == ChatSegmentKind.Mention;
    public string? ToolTip => segment.Kind switch
    {
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
    public bool IsMine => !Model.Agent && string.Equals(Model.By, _owner.MyName, StringComparison.OrdinalIgnoreCase);
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
        MentionsMe = !IsMine && ChatText.Mentions(Model.Text, _owner.MyName);
        Segments = Model.Deleted ? [new ChatSegmentViewModel(new ChatSegment(ChatSegmentKind.Text, "(deleted)"), _owner)]
            : ChatText.Parse(Model.Text, _owner.MentionNames, _owner.IsChannelName, _owner.FindItem).Select(s => new ChatSegmentViewModel(s, _owner)).ToList();
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

    [RelayCommand] private void Reply() => _owner.StartReply(this);
    [RelayCommand] private void Edit() => _owner.StartEdit(this);
    [RelayCommand] private void Delete() => _owner.DeleteMessage(this);
    [RelayCommand] private void Copy() => _owner.CopyText(Model.Text);
    [RelayCommand] private void ShowReplied()
    {
        if (Model.ReplyTo is { } id) _owner.HighlightMessage(id);
    }
}
