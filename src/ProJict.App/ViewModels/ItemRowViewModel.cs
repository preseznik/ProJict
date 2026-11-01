using System.Globalization;
using System.Windows;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using ProJict.Core.Model;

namespace ProJict.App.ViewModels;

/// <summary>One line on the board: an item at some depth, shown in its category's TODO or DONE section.</summary>
public sealed partial class ItemRowViewModel : ObservableObject
{
    public ItemRowViewModel(CategoryViewModel category, TodoItem item, TodoItem? parent, int depth, bool inDoneSection)
    {
        Category = category;
        Item = item;
        Parent = parent;
        Depth = depth;
        InDoneSection = inDoneSection;
    }

    public TodoItem Item { get; }
    public CategoryViewModel Category { get; }
    public TodoItem? Parent { get; }
    public int Depth { get; }
    public bool InDoneSection { get; }
    public ProjectViewModel Owner => Category.Owner;

    public Thickness IndentMargin => new(Depth * 26, 0, 0, 0);

    public string Text
    {
        get => Item.Text;
        set
        {
            if (value == Item.Text) return;
            if (!Owner.CanEdit)
            {
                OnPropertyChanged();
                return;
            }
            Owner.BeforeTextChange();
            Item.Text = value;
            Owner.Touch();
            OnPropertyChanged();
        }
    }

    public string? Tag => Item.Tag;
    public bool HasTag => !string.IsNullOrWhiteSpace(Item.Tag);
    public Priority Priority => Item.Priority;
    public bool HasPriority => Item.Priority != Priority.None;
    public string PriorityText => Priorities.Label(Item.Priority);
    public string PriorityToolTip => (Item.Priority == Priority.Hold ? "On hold: AI agents leave it alone" : $"{Item.Priority} priority") +
                                     " · click to change (Alt+1 urgent … Alt+4 low, Alt+5 on hold, Alt+0 none)";
    public bool IsDone => Item.IsDone;
    public bool IsInProgress => Item.State == ItemState.InProgress;
    /// <summary>A done child still listed under an open parent (shown struck through).</summary>
    public bool IsDoneInOpenSection => Item.IsDone && !InDoneSection;

    /// <summary>The two states the item isn't in, offered when its checkbox is right-clicked.</summary>
    public IEnumerable<ItemState> OtherStates => Enum.GetValues<ItemState>().Where(s => s != Item.State);

    /// <summary>Three-state checkbox: unchecked = todo, indeterminate = in progress, checked = done.</summary>
    public bool? CheckState
    {
        get => Item.State switch { ItemState.Done => true, ItemState.InProgress => null, _ => false };
        set => Owner.SetState(this, value == true ? ItemState.Done : ItemState.Todo);
    }

    public string? DoneAtText => !Item.IsDone ? null
        : Item.Imported ? "imported"
        : Item.DoneAt?.ToString("d MMM yyyy", CultureInfo.CurrentCulture);

    private IReadOnlyList<AttachmentViewModel>? _attachments;
    public IReadOnlyList<AttachmentViewModel> Attachments => _attachments ??=
        (Item.Attachments ?? []).Select(a => new AttachmentViewModel(Owner, a, () => Owner.RemoveAttachment(this, a), copy => Owner.AttachToItem(Item.Id, copy))).ToList();
    public bool HasAttachments => Item.HasAttachments;

    /// <summary>The agent working on the item, shown as a chip while it is in progress.</summary>
    public string? ClaimedBy => Item.State == ItemState.InProgress ? Item.ClaimedBy : null;
    public bool NeedsReview => Item.NeedsReview;
    public string ClaimToolTip => $"{Item.ClaimedBy} has this checked out: other agents can't change it. Right-click › Release check-out to free it.";
    public string ReviewToolTip => $"Finished by {Item.Activity?.LastOrDefault(a => a.Agent)?.By ?? "an AI agent"} — click to accept once you have checked it. " +
                                   "Mark it to do or in progress to send it back.";

    /// <summary>The newest activity entry, e.g. an agent's resolution, as one line under the text.</summary>
    public string? ActivityText => Item.Activity?.LastOrDefault() is { } last
        ? $"{last.By} · {When(last.At)} · {last.Text.Replace("\r\n", " ").Replace('\n', ' ')}"
        : null;
    public string? ActivityToolTip => Item.Activity is { Count: > 0 } log
        ? string.Join("\n\n", log.Select(a => $"{a.At.ToString("d MMM yyyy HH:mm", CultureInfo.CurrentCulture)} · {a.By}\n{a.Text}"))
        : null;

    /// <summary>Everyone (agents and people) who wrote in the item's history or discussion; a click filters the board by them.</summary>
    public IReadOnlyList<ContributorViewModel> Contributors => Item.Contributors()
        .Select(c => new ContributorViewModel(c, ContributorToolTip(c.Name), Owner.ShowWorkedByCommand)).ToList();
    public int CommentCount => Item.Comments.CountComments();
    /// <summary>Someone else commented since you last opened the discussion: a red dot on its chip.</summary>
    public bool HasUnreadComments => Owner.HasUnread(Item.Id, Item.Comments);
    /// <summary>Waiting for you: an unread comment or work to review (folded sections show a red dot for it).</summary>
    public bool NeedsAttention => Item.NeedsReview || HasUnreadComments;

    /// <summary>What waits on the item, for the red dot next to it.</summary>
    public string AttentionToolTip => (Item.NeedsReview, HasUnreadComments) switch
    {
        (true, true) => "Waits for you: work to review (the review chip) and new comments",
        (true, false) => "Waits for you: an AI agent finished it; check it, then click the review chip",
        (false, true) => "Waits for you: new comments in its discussion",
        _ => "",
    };

    public string GripToolTip => NeedsAttention ? AttentionToolTip + "\nDrag to move · Ctrl+click to select for a prompt" : "Drag to move · Ctrl+click to select for a prompt";

    /// <summary>"To review" and/or "New comments", for the category's list of what waits.</summary>
    public string AttentionKind => (Item.NeedsReview, HasUnreadComments) switch
    {
        (true, true) => "To review, new comments",
        (true, false) => "To review",
        _ => "New comments",
    };
    /// <summary>The second line shows when there is something on it.</summary>
    public bool HasDetails => Item.Activity is { Count: > 0 } || CommentCount > 0 || HasChannels;

    /// <summary>Chat channels about this item.</summary>
    public List<ChannelChip> Channels => Owner.Main.ChannelChips(Owner.Project.Id, itemId: Item.Id);
    public bool HasChannels => Owner.Main.ChannelsFor(Owner.Project.Id, itemId: Item.Id).Count > 0;

    public void RefreshChannels()
    {
        OnPropertyChanged(nameof(Channels));
        OnPropertyChanged(nameof(HasChannels));
        OnPropertyChanged(nameof(HasDetails));
    }

    [RelayCommand] private void StartChannel() => Owner.Main.StartChannel(Owner.Project, Category.Model, Item);

    private string ContributorToolTip(string name)
    {
        var lines = (Item.Activity ?? []).Where(a => string.Equals(a.By, name, StringComparison.OrdinalIgnoreCase))
            .Select(a => $"{a.At.ToString("d MMM HH:mm", CultureInfo.CurrentCulture)}  {a.Text}").ToList();
        var comments = Item.Comments.Walk().Count(e => !e.Comment.Deleted && string.Equals(e.Comment.By, name, StringComparison.OrdinalIgnoreCase));
        if (comments > 0) lines.Add(comments == 1 ? "1 comment" : $"{comments} comments");
        lines.Add("Click to show only what " + name + " worked on");
        return name + "\n" + string.Join("\n", lines);
    }

    private static string When(DateTime at) => at.Date == DateTime.Today
        ? at.ToString("HH:mm", CultureInfo.CurrentCulture)
        : at.ToString("d MMM HH:mm", CultureInfo.CurrentCulture);

    public string? ReleaseText => Item.ReleaseId is { } id
        ? Owner.Project.Releases.FirstOrDefault(r => r.Id == id) is { IsBaseline: false } r ? r.Version : null
        : null;

    /// <summary>The item's GitHub issue and the pull requests that mention it, as chips that open them on GitHub.</summary>
    public IReadOnlyList<GitHubLinkViewModel> GitHubLinks => Item.Issue is { } issue
        ?
        [
            new($"#{issue.Number}", issue.Closed ? GitHubLinkViewModel.Purple : GitHubLinkViewModel.Green, issue.Url,
                $"GitHub issue #{issue.Number}{(issue.Author != null ? " by " + issue.Author : "")}{(issue.Closed ? ", closed" : "")}: {issue.Title}\nClick to open it on GitHub"),
            .. (issue.PullRequests ?? []).Select(p => new GitHubLinkViewModel($"PR #{p.Number}",
                p.State switch { PullRequestState.Merged => GitHubLinkViewModel.Purple, PullRequestState.Closed => GitHubLinkViewModel.Red, _ => GitHubLinkViewModel.Green },
                p.Url, $"Pull request #{p.Number}, {p.State.ToString().ToLowerInvariant()}: {p.Title}\nClick to open it on GitHub")),
        ]
        : [];
    public bool HasGitHubLinks => Item.Issue != null;

    [ObservableProperty] private bool _isSelected;

    partial void OnIsSelectedChanged(bool value) => Owner.OnSelectionChanged();
    [ObservableProperty] private bool _isVisible = true;
    [ObservableProperty] private bool _isDropTarget;

    public void Refresh()
    {
        _attachments = null;
        OnPropertyChanged(string.Empty);
    }

    public void RefreshUnread()
    {
        OnPropertyChanged(nameof(HasUnreadComments));
        OnPropertyChanged(nameof(NeedsAttention));
        OnPropertyChanged(nameof(AttentionToolTip));
        OnPropertyChanged(nameof(GripToolTip));
    }

    public override string ToString() => Item.DisplayText();

    [RelayCommand] private void ToggleDone() => Owner.SetState(this, Item.IsDone ? ItemState.Todo : ItemState.Done);
    [RelayCommand] private void ToggleInProgress() => Owner.SetState(this, Item.State == ItemState.InProgress ? ItemState.Todo : ItemState.InProgress);
    [RelayCommand] private void SetTag(string? tag) => Owner.SetTag(this, string.IsNullOrWhiteSpace(tag) ? null : tag);
    [RelayCommand] private void MoveToCategory(CategoryViewModel? target)
    {
        if (target != null) Owner.MoveToCategory(this, target);
    }
    [RelayCommand] private void Delete() => Owner.Delete(this, focusPrevious: false);
    [RelayCommand] private void Promote() => Owner.PromoteToProject(this);
    [RelayCommand] private void CopyPrompt() => Owner.CopyPromptFor([Item]);
    [RelayCommand] private void CopyId() => Owner.CopyIdOf(Item);
    [RelayCommand] private void ToggleSelected() => IsSelected = !IsSelected;
    [RelayCommand] private void AddChild() => Owner.AddChild(this);
    [RelayCommand] private void Attach() => Owner.BrowseAttachments(this);
    [RelayCommand] private void PasteAttachments() => Owner.PasteAttachments(this);
    [RelayCommand] private void AcceptReview() => Owner.AcceptReview(this);
    [RelayCommand] private void SetPriority(Priority priority) => Owner.SetPriority(this, priority);
    [RelayCommand] private void ReleaseClaim() => Owner.ReleaseClaim(this);
    [RelayCommand] private void OpenDiscussion() => Owner.OpenDiscussion(Item.Id);
}

/// <summary>A chip for a GitHub issue or pull request; its dot has GitHub's colour for the state (open, closed or merged).</summary>
public sealed record GitHubLinkViewModel(string Text, System.Windows.Media.Brush Dot, string Url, string ToolTip)
{
    public static readonly System.Windows.Media.Brush Green = Frozen(0x2D, 0xA4, 0x4E);
    public static readonly System.Windows.Media.Brush Purple = Frozen(0x82, 0x50, 0xDF);
    public static readonly System.Windows.Media.Brush Red = Frozen(0xCF, 0x22, 0x2E);

    public System.Windows.Input.ICommand Open => new RelayCommand(() =>
    {
        try { Services.ShellLauncher.OpenUrl(Url); }
        catch (Exception) { /* no browser registered: nothing to do */ }
    });

    private static System.Windows.Media.Brush Frozen(byte r, byte g, byte b)
    {
        var brush = new System.Windows.Media.SolidColorBrush(System.Windows.Media.Color.FromRgb(r, g, b));
        brush.Freeze();
        return brush;
    }
}

/// <summary>A chip under an item naming someone who worked on it.</summary>
public sealed record ContributorViewModel(Contributor Contributor, string ToolTip, System.Windows.Input.ICommand Filter)
{
    public string Name => Contributor.Name;
    /// <summary>A robot for AI agents, a person otherwise.</summary>
    public string Glyph => Contributor.Agent ? "" : "";
}
