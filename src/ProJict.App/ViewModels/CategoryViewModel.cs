using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using ProJict.Core.Model;

namespace ProJict.App.ViewModels;

/// <summary>A category with its TODO rows and, below or beside them, its DONE rows.</summary>
public sealed partial class CategoryViewModel : ObservableObject
{
    public CategoryViewModel(ProjectViewModel owner, Category model)
    {
        Owner = owner;
        Model = model;
        Refresh();
    }

    public ProjectViewModel Owner { get; }
    public Category Model { get; }

    public IReadOnlyList<ItemRowViewModel> OpenRows { get; private set; } = [];
    public IReadOnlyList<ItemRowViewModel> DoneRows { get; private set; } = [];

    public string Name
    {
        get => Model.Name;
        set
        {
            if (value == Model.Name || !Owner.CanEdit) return;
            Owner.BeforeTextChange();
            Model.Name = value;
            Owner.Touch();
            OnPropertyChanged();
        }
    }

    public string? Description
    {
        get => Model.Description;
        set
        {
            var v = string.IsNullOrWhiteSpace(value) ? null : value;
            if (v == Model.Description || !Owner.CanEdit) return;
            Owner.BeforeTextChange();
            Model.Description = v;
            Owner.Touch();
            OnPropertyChanged();
        }
    }

    public bool IsExpanded
    {
        get => !Model.Collapsed;
        set
        {
            if (value == !Model.Collapsed) return;
            Model.Collapsed = !value;
            Owner.Touch();
            OnPropertyChanged();
            RaiseAttention();
            Owner.OnCategoryExpandedChanged();
        }
    }

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(ShowDoneRows))]
    [NotifyPropertyChangedFor(nameof(ShowDoneAttention))]
    private bool _isDoneExpanded;

    [ObservableProperty] private bool _isVisible = true;
    [ObservableProperty] private bool _isDropTarget;
    [ObservableProperty] private string _newItemText = "";

    public int OpenCount => Model.Items.Count(i => !i.IsDone);
    public int DoneCount => Model.Items.Count(i => i.IsDone);
    public string CountsText => DoneCount == 0 ? $"{OpenCount} open" : $"{OpenCount} open · {DoneCount} done";
    public string DoneHeader => $"Done ({DoneCount})";
    /// <summary>"(3)" after the category's name: its open items, like "Done (5)" counts the finished ones.</summary>
    public string OpenCountText => $"({OpenCount})";
    public bool HasDone => DoneCount > 0;

    /// <summary>Done rows show when expanded, in side-by-side layout, or when a filter or the focused item shows some of them.</summary>
    public bool ShowDoneRows => IsDoneExpanded || Owner.SideBySide ||
                                ((Owner.IsFocused ? Owner.FocusItem != null : Owner.IsFilterActive) && DoneRows.Any(r => r.IsVisible));

    /// <summary>The board is focused on this category (its eye button then leaves the focus).</summary>
    public bool IsFocusTarget => Owner.FocusCategory?.Id == Model.Id;
    public string FocusToolTip => IsFocusTarget ? "Leave focus: show the whole board again, with your filters" : "Focus: show only this category";

    [RelayCommand]
    private void ToggleFocus()
    {
        if (IsFocusTarget) Owner.LeaveFocus();
        else Owner.FocusOn(Model);
    }

    public void RefreshFocus()
    {
        OnPropertyChanged(nameof(IsFocusTarget));
        OnPropertyChanged(nameof(FocusToolTip));
    }

    /// <summary>Chat channels about this category.</summary>
    public List<ChannelChip> Channels => Owner.Main.ChannelChips(Owner.Project.Id, Model.Id);
    public bool HasChannels => Channels.Count > 0;

    public void RefreshChannels()
    {
        OnPropertyChanged(nameof(Channels));
        OnPropertyChanged(nameof(HasChannels));
    }

    [RelayCommand] private void StartChannel() => Owner.Main.StartChannel(Owner.Project, Model);

    public void Refresh()
    {
        var open = new List<ItemRowViewModel>();
        foreach (var item in Ordered(Model.Items.Where(i => !i.IsDone), false)) AddRows(open, item, null, 0, false);
        var done = new List<ItemRowViewModel>();
        foreach (var item in Model.Items.Where(i => i.IsDone).OrderByDescending(i => i.DoneAt ?? DateTime.MinValue))
            AddRows(done, item, null, 0, true);

        OpenRows = open;
        DoneRows = done;
        OnPropertyChanged(nameof(OpenRows));
        OnPropertyChanged(nameof(DoneRows));
        RefreshCounts();
    }

    public void RefreshCounts()
    {
        OnPropertyChanged(nameof(OpenCount));
        OnPropertyChanged(nameof(DoneCount));
        OnPropertyChanged(nameof(CountsText));
        OnPropertyChanged(nameof(DoneHeader));
        OnPropertyChanged(nameof(OpenCountText));
        OnPropertyChanged(nameof(HasDone));
        OnPropertyChanged(nameof(ShowDoneRows));
        OnPropertyChanged(nameof(CommentCount));
        OnPropertyChanged(nameof(DiscussionToolTip));
        OnPropertyChanged(nameof(HasUnreadComments));
        RaiseAttention();
    }

    /// <summary>After you read a discussion somewhere: its red dots go.</summary>
    public void RefreshUnread()
    {
        foreach (var row in AllRows) row.RefreshUnread();
        OnPropertyChanged(nameof(HasUnreadComments));
        RaiseAttention();
    }

    private void RaiseAttention()
    {
        OnPropertyChanged(nameof(ShowAttention));
        OnPropertyChanged(nameof(ShowDoneAttention));
        OnPropertyChanged(nameof(AttentionToolTip));
        OnPropertyChanged(nameof(DoneAttentionToolTip));
    }

    /// <summary>What waits in the category, and that a click shows just that.</summary>
    public string AttentionToolTip => WaitingText(AllRows) + "\nClick to show only what waits for you.";
    /// <summary>What waits in the folded Done list.</summary>
    public string DoneAttentionToolTip => WaitingText(DoneRows);

    private static string WaitingText(IEnumerable<ItemRowViewModel> rows)
    {
        const int listed = 5;
        var waiting = rows.Where(r => r.NeedsAttention).ToList();
        var lines = waiting.Take(listed).Select(r => $"{r.AttentionKind}: {Short(r.Item.DisplayText())}");
        var more = waiting.Count > listed ? $"\n…and {waiting.Count - listed} more" : "";
        return "Waiting for you:\n" + string.Join("\n", lines) + more;
    }

    private static string Short(string text)
    {
        var line = text.Replace("\r\n", "\n").Split('\n')[0].Trim();
        return line.Length > 70 ? line[..70] + "…" : line;
    }

    /// <summary>The header's red dot: unfold and show only what waits for you.</summary>
    [RelayCommand]
    private void ShowWaiting()
    {
        IsExpanded = true;
        Owner.ShowWaiting();
    }

    public bool HasUnreadComments => Owner.HasUnread(Model.Id, Model.Comments);
    /// <summary>
    /// Something inside waits for you (an unread comment, work to review): a red dot on the header, folded or not, so the
    /// category says so even when what waits is down in its folded Done list.
    /// </summary>
    public bool ShowAttention => AllRows.Any(r => r.NeedsAttention);
    /// <summary>The same for the folded Done list (agents' finished work waits there for review).</summary>
    public bool ShowDoneAttention => !ShowDoneRows && DoneRows.Any(r => r.NeedsAttention);

    public int CommentCount => Model.Comments.CountComments();
    public string DiscussionToolTip => CommentCount switch
    {
        0 => "Discuss this category",
        1 => "Discussion: 1 comment",
        var n => $"Discussion: {n} comments",
    } + (Model.Walk().Sum(l => l.Item.Comments.CountComments()) is > 0 and var onItems ? $" · {onItems} on its items, shown there too" : "");

    public void RaiseShowDone()
    {
        OnPropertyChanged(nameof(ShowDoneRows));
        OnPropertyChanged(nameof(ShowDoneAttention));
        OnPropertyChanged(nameof(DoneAttentionToolTip));
    }

    private void AddRows(List<ItemRowViewModel> rows, TodoItem item, TodoItem? parent, int depth, bool done)
    {
        rows.Add(new ItemRowViewModel(this, item, parent, depth, done));
        foreach (var child in Ordered(item.Children, done)) AddRows(rows, child, item, depth + 1, done);
    }

    /// <summary>Open items most pressing first when the board is sorted by priority; otherwise (and in DONE) their own order.</summary>
    private IEnumerable<TodoItem> Ordered(IEnumerable<TodoItem> items, bool done) =>
        Owner.SortByPriority && !done ? items.ByPriority() : items;

    public IEnumerable<ItemRowViewModel> AllRows => OpenRows.Concat(DoneRows);

    public override string ToString() => Name;

    [RelayCommand]
    private void AddNewItem()
    {
        if (string.IsNullOrWhiteSpace(NewItemText)) return;
        Owner.AddItem(this, NewItemText);
        NewItemText = "";
    }

    [RelayCommand] private void CopyPrompt() => Owner.CopyPromptFor(Model.Items.Where(i => !i.IsDone));
    [RelayCommand] private void OpenDiscussion() => Owner.OpenDiscussion(Model.Id);
    [RelayCommand] private void MoveToTop() => Owner.MoveCategoryToEnd(this, top: true);
    [RelayCommand] private void MoveUp() => Owner.MoveCategory(this, -1);
    [RelayCommand] private void MoveDown() => Owner.MoveCategory(this, +1);
    [RelayCommand] private void MoveToBottom() => Owner.MoveCategoryToEnd(this, top: false);
    [RelayCommand] private void Delete() => Owner.DeleteCategory(this);
    [RelayCommand] private void ToggleExpanded() => IsExpanded = !IsExpanded;
}
