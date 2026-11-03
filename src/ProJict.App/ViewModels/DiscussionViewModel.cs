using System.Collections.ObjectModel;
using System.Globalization;
using System.IO;
using System.Windows;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using ProJict.App.Services;
using ProJict.Core.Features;
using ProJict.Core.Model;

namespace ProJict.App.ViewModels;

public enum DiscussionKind { Item, Category, Group }

/// <summary>What a discussion is about: an item or category (by id, so it survives undo) or a group (by name).</summary>
public sealed record DiscussionTarget(DiscussionKind Kind, Guid ProjectId, Guid TargetId, string? Group)
{
    public static DiscussionTarget InProject(Project project, Guid targetId) =>
        new(project.Find(targetId) != null ? DiscussionKind.Item : DiscussionKind.Category, project.Id, targetId, null);

    public static DiscussionTarget ForGroup(string group) => new(DiscussionKind.Group, Guid.Empty, Guid.Empty, group.Trim());
}

/// <summary>
/// The side panel with the threads of one item, category or group. It follows changes from anywhere (an agent's
/// reply, undo) by updating its comments in place, so a half-written reply keeps its text and focus.
/// </summary>
public sealed partial class DiscussionViewModel : ObservableObject
{
    private readonly MainViewModel _main;
    private readonly AppState _state;

    public DiscussionViewModel(MainViewModel main, AppState state, DiscussionTarget target)
    {
        _main = main;
        _state = state;
        Target = target;
        Pending.CollectionChanged += (_, _) => NotifyDrafts();
        Sync();
    }

    public DiscussionTarget Target { get; }
    public ObservableCollection<CommentViewModel> Threads { get; } = [];

    public string KindText => Target.Kind switch
    {
        DiscussionKind.Item => "Item",
        DiscussionKind.Category => "Category",
        _ => "Group",
    };

    [ObservableProperty] private string _subject = "";
    [ObservableProperty] private string _location = "";
    [ObservableProperty] private bool _isGone;
    [ObservableProperty] private int _count;
    [ObservableProperty]
    [NotifyCanExecuteChangedFor(nameof(PostCommand))]
    private string _newText = "";

    public bool IsEmpty => Threads.Count == 0 && ItemDiscussions.Count == 0 && !IsGone;

    /// <summary>
    /// A category's discussion also shows what was said on its items: each item's discussion as a sub-thread under the
    /// item's name, the most recently active first. Replies there go to the item.
    /// </summary>
    public ObservableCollection<ItemDiscussion> ItemDiscussions { get; } = [];
    public bool HasItemDiscussions => ItemDiscussions.Count > 0;

    private void SyncItemDiscussions()
    {
        var project = Project;
        var category = Target.Kind == DiscussionKind.Category ? project?.Categories.FirstOrDefault(c => c.Id == Target.TargetId) : null;
        var items = category == null ? [] : category.Walk()
            .Select(l => l.Item)
            .Where(i => i.Comments.Walk().Any())
            .OrderByDescending(i => i.Comments.Walk().Max(e => e.Comment.At))
            .ToList();
        for (var i = ItemDiscussions.Count - 1; i >= 0; i--)
            if (items.All(item => item.Id != ItemDiscussions[i].ItemId)) ItemDiscussions.RemoveAt(i);
        for (var i = 0; i < items.Count; i++)
        {
            var existing = ItemDiscussions.FirstOrDefault(d => d.ItemId == items[i].Id);
            if (existing == null) ItemDiscussions.Insert(i, new ItemDiscussion(_main, _state, project!, items[i]));
            else
            {
                existing.Refresh(items[i]);
                var at = ItemDiscussions.IndexOf(existing);
                if (at != i) ItemDiscussions.Move(at, i);
            }
        }
        OnPropertyChanged(nameof(HasItemDiscussions));
    }

    /// <summary>False in another ProJict's project where your role doesn't include discussions.</summary>
    public bool CanWrite => !IsGone && (Project?.Remote is not { } remote || (remote.Connected && ProJict.Core.Sharing.GuestAccess.CanComment(remote.Role)));
    public string SignedAs => $"Posting as {_state.UserName}";

    private Project? Project => Target.Kind == DiscussionKind.Group ? null : _state.Projects.FirstOrDefault(p => p.Id == Target.ProjectId);

    private List<Comment>? Comments()
    {
        if (Target.Kind == DiscussionKind.Group) return _state.Settings.CommentsFor(Target.Group!);
        var project = Project;
        return project?.Find(Target.TargetId)?.Item.Comments ?? project?.Categories.FirstOrDefault(c => c.Id == Target.TargetId)?.Comments;
    }

    /// <summary>Brings the panel up to date with the stored threads.</summary>
    public void Sync()
    {
        var project = Project;
        switch (Target.Kind)
        {
            case DiscussionKind.Group:
                Subject = Target.Group!;
                Location = "Projects in this group";
                IsGone = !_state.Settings.Groups.Contains(Target.Group!, StringComparer.OrdinalIgnoreCase);
                break;
            default:
                var loc = project?.Find(Target.TargetId);
                var category = loc?.Category ?? project?.Categories.FirstOrDefault(c => c.Id == Target.TargetId);
                IsGone = category == null;
                Subject = loc != null ? loc.Item.DisplayText().Replace("\r\n", " ").Replace('\n', ' ') : category?.Name ?? "(removed)";
                Location = project == null ? "" : loc != null ? $"{project.Name} › {loc.Category.Name}" : project.Name;
                break;
        }
        var comments = Comments();
        CommentViewModel.SyncList(Threads, comments, this, 0);
        Count = comments.CountComments();
        SyncItemDiscussions();
        OnPropertyChanged(nameof(IsEmpty));
        OnPropertyChanged(nameof(CanWrite));
        OnPropertyChanged(nameof(CanShowInProject));
        _state.MarkRead(Target.Kind == DiscussionKind.Group ? AppState.ReadKey(Target.Group!) : AppState.ReadKey(Target.TargetId), comments);
    }

    private bool CanPost() => (NewText.Trim().Length > 0 || Pending.Count > 0) && !IsGone;

    [RelayCommand(CanExecute = nameof(CanPost))]
    private void Post()
    {
        if (!Post(NewText, null, [.. Pending])) return;
        NewText = "";
        Pending.Clear();
    }

    // ───────────── Files in comments ─────────────

    /// <summary>Files to go with the next new thread.</summary>
    public ObservableCollection<PendingFile> Pending { get; } = [];

    /// <summary>Shows and opens the files of this discussion: the project's own, or the Inbox's (the same data folder) for a group.</summary>
    internal ProjectViewModel AttachmentOwner => _main.GetProjectViewModel(Project ?? _state.Inbox);
    internal ChatViewModel Chat => _main.Chat;

    [RelayCommand] private void AttachFiles() => AddFiles(Pending, AskForFiles());

    /// <summary>Pastes files or a picture from the clipboard into the new thread.</summary>
    public void PasteFiles() => Paste(Pending);

    public void AddFiles(IEnumerable<string> paths) => AddFiles(Pending, paths);

    internal static IReadOnlyList<string> AskForFiles()
    {
        var dialog = new Microsoft.Win32.OpenFileDialog { Title = "Attach files to the comment", Multiselect = true };
        return dialog.ShowDialog(Application.Current.MainWindow) == true ? dialog.FileNames : [];
    }

    internal void Paste(ObservableCollection<PendingFile> to)
    {
        var (files, png) = ClipboardAttachments.Read();
        if (png != null) AddPicture(to, png);
        else AddFiles(to, files);
    }

    /// <summary>Adds files to a draft, leaving out (and saying so) the ones over the size limit (Settings › Comments &amp; history).</summary>
    internal void AddFiles(ObservableCollection<PendingFile> to, IEnumerable<string> paths)
    {
        foreach (var path in paths)
        {
            var info = new FileInfo(path);
            if (!info.Exists) continue;
            if (TooBig(info.Name, info.Length)) continue;
            to.Add(new PendingFile(info.Name, info.Length, path, null, to));
        }
        NotifyDrafts();
    }

    internal void AddPicture(ObservableCollection<PendingFile> to, byte[] png)
    {
        var name = $"Pasted image {DateTime.Now:yyyy-MM-dd HHmmss}.png";
        if (TooBig(name, png.Length)) return;
        to.Add(new PendingFile(name, png.Length, null, png, to));
        NotifyDrafts();
    }

    private bool TooBig(string name, long size)
    {
        var limit = _state.Settings.CommentFileLimitBytes;
        if (size <= limit) return false;
        _state.StatusText = $"{name} is {AttachmentViewModel.FormatSize(size)}; files in comments can be up to {AttachmentViewModel.FormatSize(limit)} " +
                            "(Settings › Comments & history).";
        return true;
    }

    internal void NotifyDrafts()
    {
        PostCommand.NotifyCanExecuteChanged();
        OnPropertyChanged(nameof(HasPending));
    }

    public bool HasPending => Pending.Count > 0;

    /// <summary>A marked-up copy of a picture in the discussion goes back in as a reply to its comment.</summary>
    internal void PostCopy(Attachment copy, Guid replyTo) =>
        Post("", replyTo, [new PendingFile(copy.FileName, copy.Size, null, null, null) { Stored = copy }]);

    [RelayCommand] private void Close() => _main.CloseDiscussion();

    /// <summary>An item's or category's discussion can take you to it on its project's board.</summary>
    public bool CanShowInProject => Target.Kind != DiscussionKind.Group && !IsGone && Project != null;

    [RelayCommand]
    private void ShowInProject()
    {
        if (!CanShowInProject) return;
        var board = _main.OpenProject(Project!);
        if (Project!.Find(Target.TargetId) is { } loc) board.Reveal(loc.Item);
    }

    /// <summary>Points out one comment (e.g. the one clicked in the Feed); the panel scrolls to it.</summary>
    public void Highlight(Guid commentId)
    {
        void Mark(IEnumerable<CommentViewModel> comments)
        {
            foreach (var comment in comments)
            {
                comment.IsHighlighted = comment.Id == commentId;
                Mark(comment.Replies);
            }
        }
        Mark(Threads);
    }

    /// <summary>Posts your comment as a new thread or a reply; false (with the reason in the status bar) when it couldn't.</summary>
    public bool Post(string text, Guid? replyTo, IReadOnlyList<PendingFile>? files = null)
    {
        files ??= [];
        if ((text.Trim().Length == 0 && files.Count == 0) || IsGone) return false;
        var comment = new Comment { By = _state.UserName, Text = text.Trim() };
        try
        {
            // Copied into the data folder (as for items); the originals are only read.
            if (files.Count > 0)
                comment.Attachments = files.Select(f => f.Stored ?? (f.Data != null ? _state.Store.WriteAttachment(f.Name, f.Data) : _state.Store.ImportAttachment(f.Path!))).ToList();
            if (Target.Kind == DiscussionKind.Group) _state.PostGroupComment(Target.Group!, comment, replyTo);
            else _main.GetProjectViewModel(Project!).PostComment(Target.TargetId, comment, replyTo);
        }
        catch (Exception ex) when (ex is InvalidOperationException or ArgumentException or IOException or UnauthorizedAccessException)
        {
            _state.StatusText = ex.Message;
            return false;
        }
        return true;
    }

    /// <summary>A whole comment to the clipboard (parts of one are selected and copied in the panel itself).</summary>
    public void Copy(string text)
    {
        try
        {
            Clipboard.SetText(text);
            _state.StatusText = "Comment copied.";
        }
        catch (System.Runtime.InteropServices.ExternalException)
        {
            _state.StatusText = "Clipboard is busy, try again.";
        }
    }

    public void Remove(CommentViewModel comment)
    {
        if (Target.Kind == DiscussionKind.Group)
        {
            // Group discussions live in the workspace settings, outside the projects' undo.
            if (MessageBox.Show(Application.Current.MainWindow!, "Delete this comment? This can't be undone.", "Delete comment",
                    MessageBoxButton.YesNo, MessageBoxImage.Question) != MessageBoxResult.Yes) return;
            _state.RemoveGroupComment(Target.Group!, comment.Id);
            return;
        }
        _main.GetProjectViewModel(Project!).RemoveComment(Target.TargetId, comment.Id);
        _state.StatusText = "Comment deleted (Ctrl+Z on the board brings it back).";
    }
}

/// <summary>One comment in the panel, with its replies below it.</summary>
public sealed partial class CommentViewModel : ObservableObject
{
    private readonly DiscussionViewModel _owner;

    public CommentViewModel(DiscussionViewModel owner, Comment model, int depth)
    {
        _owner = owner;
        Model = model;
        Depth = depth;
        Pending.CollectionChanged += (_, _) => NotifyDraft();
        SyncList(Replies, model.Replies, owner, depth + 1);
    }

    public Comment Model { get; private set; }
    public Guid Id => Model.Id;
    public int Depth { get; }
    public ObservableCollection<CommentViewModel> Replies { get; } = [];
    public bool HasReplies => Replies.Count > 0;

    public string By => Model.Deleted ? "" : Model.By;
    public bool IsAgent => Model.Agent;
    /// <summary>A robot for AI agents, a person otherwise.</summary>
    public string Glyph => Model.Agent ? "" : "";
    public string Text => Model.Deleted ? "(deleted)" : Model.Text;
    /// <summary>The text as it shows: **…** bold, as agents often write it.</summary>
    public IReadOnlyList<ChatSegmentViewModel> Segments =>
        [.. ProJict.Core.Chat.ChatText.BoldSpans(Text).Select(s => new ChatSegmentViewModel(new ProJict.Core.Chat.ChatSegment(ProJict.Core.Chat.ChatSegmentKind.Text, s.Text, Bold: s.Bold), _owner.Chat))];
    public bool IsDeleted => Model.Deleted;
    public string WhenText => Model.At.Date == DateTime.Today
        ? Model.At.ToString("HH:mm", CultureInfo.CurrentCulture)
        : Model.At.ToString("d MMM HH:mm", CultureInfo.CurrentCulture);
    public string WhenToolTip => Model.At.ToString("dddd d MMMM yyyy, HH:mm", CultureInfo.CurrentCulture);

    [ObservableProperty] private bool _isReplying;
    /// <summary>The comment you came for (from the Feed).</summary>
    [ObservableProperty] private bool _isHighlighted;
    [ObservableProperty]
    [NotifyCanExecuteChangedFor(nameof(SendReplyCommand))]
    private string _replyText = "";

    [RelayCommand] private void Reply() => IsReplying = true;

    [RelayCommand]
    private void CancelReply()
    {
        IsReplying = false;
        ReplyText = "";
    }

    private bool CanSendReply() => ReplyText.Trim().Length > 0 || Pending.Count > 0;

    [RelayCommand(CanExecute = nameof(CanSendReply))]
    private void SendReply()
    {
        if (!_owner.Post(ReplyText, Id, [.. Pending])) return;
        ReplyText = "";
        Pending.Clear();
        IsReplying = false;
    }

    // ───────────── Questions to answer with a click ─────────────

    private List<ChoiceViewModel>? _choices;
    public IReadOnlyList<ChoiceViewModel> Choices => _choices ??= [.. (Model.Choices ?? []).Select(c => new ChoiceViewModel(this, c))];
    public bool HasChoices => Model.Choices is { Count: > 0 } && !Model.Deleted;
    public bool IsAnswered => CommentChoices.IsAnswered(Model.Choices);
    /// <summary>Several questions: picked one by one, then sent together (one is sent with the click).</summary>
    public bool NeedsSend => HasChoices && !IsAnswered && Choices.Count > 1;
    private bool CanSendAnswers() => !IsAnswered && Choices.Count > 0 && Choices.All(c => c.Picked != null);

    internal void Picked()
    {
        if (Choices.Count == 1) SendAnswers();
        else SendAnswersCommand.NotifyCanExecuteChanged();
    }

    /// <summary>
    /// Your answers, kept on the question and posted as your reply (so the agent hears it). They go on the comment as it is
    /// now, which may be a newer copy than the one the questions were shown from (another ProJict's, or after an undo).
    /// </summary>
    [RelayCommand(CanExecute = nameof(CanSendAnswers))]
    private void SendAnswers()
    {
        if (!CanSendAnswers() || Model.Choices is not { } questions || questions.Count != Choices.Count) return;
        for (var i = 0; i < questions.Count; i++) questions[i].Answer = Choices[i].Picked;
        if (!_owner.Post(CommentChoices.AnswerText(questions), Id))
        {
            foreach (var question in questions) question.Answer = null;
            return;
        }
        foreach (var choice in Choices) choice.Refresh();
        OnPropertyChanged(nameof(IsAnswered));
        OnPropertyChanged(nameof(NeedsSend));
    }

    /// <summary>Takes the agent's suggestion (the first option) for every question.</summary>
    [RelayCommand]
    private void UseSuggestions()
    {
        foreach (var choice in Choices) choice.Picked = choice.Model.Options[0];
        SendAnswers();
    }

    /// <summary>Files to go with the reply being written.</summary>
    public ObservableCollection<PendingFile> Pending { get; } = [];
    public bool HasPending => Pending.Count > 0;

    [RelayCommand]
    private void AttachFiles()
    {
        _owner.AddFiles(Pending, DiscussionViewModel.AskForFiles());
        NotifyDraft();
    }

    public void PasteFiles()
    {
        _owner.Paste(Pending);
        NotifyDraft();
    }

    public void AddFiles(IEnumerable<string> paths)
    {
        _owner.AddFiles(Pending, paths);
        NotifyDraft();
    }

    internal void NotifyDraft()
    {
        SendReplyCommand.NotifyCanExecuteChanged();
        OnPropertyChanged(nameof(HasPending));
    }

    /// <summary>The comment's files: pictures as thumbnails (they open in ProJict's viewer), other files as chips.</summary>
    public IReadOnlyList<AttachmentViewModel> Attachments => _attachments ??= Model.Deleted ? []
        : (Model.Attachments ?? []).Select(a => new AttachmentViewModel(_owner.AttachmentOwner, a, () => { }, copy => _owner.PostCopy(copy, Id))).ToList();
    private IReadOnlyList<AttachmentViewModel>? _attachments;
    public bool HasAttachments => Attachments.Count > 0;

    [RelayCommand] private void Delete() => _owner.Remove(this);

    [RelayCommand] private void Copy() => _owner.Copy(Model.Text);

    private void Update(Comment model)
    {
        var changed = model.Text != Model.Text || model.Deleted != Model.Deleted || (model.Attachments?.Count ?? 0) != (Model.Attachments?.Count ?? 0);
        var newChoices = !ReferenceEquals(model.Choices, Model.Choices);
        Model = model;
        // A newer copy of the comment (another ProJict's, or after an undo): its questions are new objects too. Shown from
        // them, keeping what you picked but haven't sent, so the answers go on the comment as it is.
        if (newChoices && _choices != null)
        {
            var picked = _choices.Select(c => (c.Question, c.Picked)).ToList();
            _choices = null;
            for (var i = 0; i < Choices.Count && i < picked.Count; i++)
                if (Choices[i].Question == picked[i].Question && !Choices[i].IsAnswered && picked[i].Picked != null) Choices[i].Picked = picked[i].Picked;
            changed = true;
        }
        if (changed)
        {
            _attachments = null;
            OnPropertyChanged(string.Empty);
            SendAnswersCommand.NotifyCanExecuteChanged();
        }
        SyncList(Replies, model.Replies, _owner, Depth + 1);
        OnPropertyChanged(nameof(HasReplies));
    }

    /// <summary>Makes <paramref name="items"/> match <paramref name="models"/> by id, keeping existing view models (and their drafts).</summary>
    public static void SyncList(ObservableCollection<CommentViewModel> items, List<Comment>? models, DiscussionViewModel owner, int depth)
    {
        var list = models ?? [];
        for (var i = items.Count - 1; i >= 0; i--)
            if (list.All(m => m.Id != items[i].Id)) items.RemoveAt(i);
        for (var i = 0; i < list.Count; i++)
        {
            var existing = items.FirstOrDefault(v => v.Id == list[i].Id);
            if (existing == null)
            {
                items.Insert(i, new CommentViewModel(owner, list[i], depth));
                continue;
            }
            var at = items.IndexOf(existing);
            if (at != i) items.Move(at, i);
            existing.Update(list[i]);
        }
    }
}

/// <summary>A file chosen for a comment that isn't posted yet: a file on disk, or a pasted picture.</summary>
/// <summary>A question asked with a comment: its options as buttons, the asker's suggestion first.</summary>
public sealed partial class ChoiceViewModel : ObservableObject
{
    private readonly CommentViewModel _comment;

    public ChoiceViewModel(CommentViewModel comment, CommentChoice model)
    {
        _comment = comment;
        Model = model;
        _picked = model.Answer;
        Options = [.. model.Options.Select((o, i) => new ChoiceOptionViewModel(this, o, i == 0))];
    }

    public CommentChoice Model { get; }
    public string Question => Model.Question;
    public IReadOnlyList<ChoiceOptionViewModel> Options { get; }
    public bool IsAnswered => Model.Answer != null;
    public string AnswerText => Model.Answer ?? "";
    [ObservableProperty] private string? _picked;

    partial void OnPickedChanged(string? value)
    {
        foreach (var option in Options) option.Refresh();
    }

    internal void Pick(string option)
    {
        if (IsAnswered) return;
        Picked = option;
        _comment.Picked();
    }

    internal void Refresh()
    {
        OnPropertyChanged(nameof(IsAnswered));
        OnPropertyChanged(nameof(AnswerText));
        foreach (var option in Options) option.Refresh();
    }
}

public sealed partial class ChoiceOptionViewModel(ChoiceViewModel choice, string text, bool suggested) : ObservableObject
{
    public string Text => text;
    public bool IsSuggested => suggested;
    public bool IsPicked => choice.Picked == text;
    public string? ToolTip => suggested ? "The suggestion" : null;
    [RelayCommand] private void Pick() => choice.Pick(text);
    internal void Refresh() => OnPropertyChanged(nameof(IsPicked));
}

public sealed partial class PendingFile(string name, long size, string? path, byte[]? data, ObservableCollection<PendingFile>? list)
{
    public string Name { get; } = name;
    public long Size { get; } = size;
    public string? Path { get; } = path;
    public byte[]? Data { get; } = data;
    /// <summary>Already in the data folder (a marked-up copy).</summary>
    public Attachment? Stored { get; init; }
    public string SizeText => AttachmentViewModel.FormatSize(Size);

    [RelayCommand] private void Remove() => list?.Remove(this);
}

/// <summary>An item's discussion shown inside its category's: the item's name (a click shows it on the board) and its threads.</summary>
public sealed partial class ItemDiscussion : ObservableObject
{
    private readonly MainViewModel _main;
    private readonly Project _project;

    public ItemDiscussion(MainViewModel main, AppState state, Project project, TodoItem item)
    {
        _main = main;
        _project = project;
        ItemId = item.Id;
        // A discussion of its own, so replies, files and deleting work as in the item's panel.
        Discussion = new DiscussionViewModel(main, state, DiscussionTarget.InProject(project, item.Id));
        Refresh(item);
    }

    public Guid ItemId { get; }
    public DiscussionViewModel Discussion { get; }
    public string Text { get; private set; } = "";
    public bool IsDone { get; private set; }

    public void Refresh(TodoItem item)
    {
        Text = item.DisplayText().Replace("\r\n", " ").Replace('\n', ' ');
        IsDone = item.IsDone;
        Discussion.Sync();
        OnPropertyChanged(nameof(Text));
        OnPropertyChanged(nameof(IsDone));
    }

    [RelayCommand]
    private void Open()
    {
        if (_project.Find(ItemId) is { } loc) _main.OpenProject(_project).Reveal(loc.Item);
    }
}
