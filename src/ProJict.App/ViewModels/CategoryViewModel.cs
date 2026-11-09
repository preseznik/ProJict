using System.Collections.ObjectModel;
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

    /// <summary>
    /// The rows stay the same objects while their items are there, so a change redraws only what changed: a new list
    /// each time made the board build every row again, and Windows' accessibility tree with it (#0e516918).
    /// </summary>
    public ObservableCollection<ItemRowViewModel> OpenRows { get; } = [];
    /// <summary>Empty while the Done list can't show (see <see cref="DoneRowsWanted"/>); <see cref="DoneCount"/> counts it all the same.</summary>
    public ObservableCollection<ItemRowViewModel> DoneRows { get; } = [];
    private bool _doneBuilt;

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

    partial void OnIsDoneExpandedChanged(bool value) => SyncDoneRows();

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
                                (LooksThroughDone && DoneRows.Any(r => r.IsVisible));

    /// <summary>A filter, or the focus on an item, may show some of the Done list's items.</summary>
    private bool LooksThroughDone => Owner.IsFocused ? Owner.FocusItem != null : Owner.IsFilterActive;

    /// <summary>
    /// The Done list has rows only while it can show: unfolded, side by side, or with a filter or focus looking through
    /// it. Folded, its finished items cost nothing to draw or keep current (#0e516918).
    /// </summary>
    private bool DoneRowsWanted => IsDoneExpanded || Owner.SideBySide || LooksThroughDone;

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

    /// <summary>
    /// Brings the rows in line with the category: rows whose items are still there are kept and re-read (only the rows
    /// of <paramref name="changed"/> and the items it's under, when it's known what changed), new ones are added, and
    /// gone ones removed, so the board redraws only what changed (#0e516918).
    /// </summary>
    public void Refresh(TodoItem? changed = null)
    {
        var stale = changed == null ? null : Lineage(changed);
        var known = new Dictionary<TodoItem, ItemRowViewModel>(ReferenceEqualityComparer.Instance);
        foreach (var row in AllRows) known[row.Item] = row;

        var open = new List<ItemRowViewModel>();
        foreach (var item in Ordered(Model.Items.Where(i => !i.IsDone), false)) AddRows(open, item, null, 0, false, known, stale);
        var done = new List<ItemRowViewModel>();
        _doneBuilt = DoneRowsWanted;
        if (_doneBuilt)
            foreach (var item in DoneTopLevel()) AddRows(done, item, null, 0, true, known, stale);

        var lostSelection = Sync(OpenRows, open) | Sync(DoneRows, done);
        RefreshCounts();
        if (lostSelection) Owner.OnSelectionChanged();
    }

    /// <summary>Builds the Done list's rows when it can show now, or lets them go when it can't (it was folded, a filter ended).</summary>
    public void SyncDoneRows()
    {
        if (DoneRowsWanted == _doneBuilt) return;
        _doneBuilt = !_doneBuilt;
        var done = new List<ItemRowViewModel>();
        if (_doneBuilt)
        {
            var none = new Dictionary<TodoItem, ItemRowViewModel>(ReferenceEqualityComparer.Instance);
            foreach (var item in DoneTopLevel()) AddRows(done, item, null, 0, true, none, null);
        }
        if (Sync(DoneRows, done)) Owner.OnSelectionChanged();
        RaiseShowDone();
        RaiseAttention();
    }

    private IEnumerable<TodoItem> DoneTopLevel() => Model.Items.Where(i => i.IsDone).OrderByDescending(i => i.DoneAt ?? DateTime.MinValue);

    /// <summary>The item, its sub-items and the items it's under: the rows that show something of it.</summary>
    private HashSet<TodoItem> Lineage(TodoItem item)
    {
        var parents = new Dictionary<TodoItem, TodoItem>(ReferenceEqualityComparer.Instance);
        foreach (var loc in Model.Walk())
            if (loc.Parent != null) parents[loc.Item] = loc.Parent;
        var lineage = new HashSet<TodoItem>(item.Descendants().Prepend(item), ReferenceEqualityComparer.Instance);
        for (var p = parents.GetValueOrDefault(item); p != null; p = parents.GetValueOrDefault(p)) lineage.Add(p);
        return lineage;
    }

    /// <summary>
    /// Makes <paramref name="rows"/> hold <paramref name="wanted"/> in that order with as few changes as it takes (removes,
    /// moves, inserts), so the rows that stay keep what's drawn for them. True when a selected row went.
    /// </summary>
    private static bool Sync(ObservableCollection<ItemRowViewModel> rows, List<ItemRowViewModel> wanted)
    {
        var keep = new HashSet<ItemRowViewModel>(wanted, ReferenceEqualityComparer.Instance);
        var lostSelection = false;
        for (var i = rows.Count - 1; i >= 0; i--)
        {
            if (keep.Contains(rows[i])) continue;
            lostSelection |= rows[i].IsSelected;
            rows.RemoveAt(i);
        }
        for (var i = 0; i < wanted.Count; i++)
        {
            if (i < rows.Count && ReferenceEquals(rows[i], wanted[i])) continue;
            var at = rows.IndexOf(wanted[i]);
            if (at >= 0) rows.Move(at, i);
            else rows.Insert(i, wanted[i]);
        }
        return lostSelection;
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
    public string AttentionToolTip => WaitingText(WaitingOpen().Concat(WaitingDone())) + "\nClick to show only what waits for you.";
    /// <summary>What waits in the folded Done list.</summary>
    public string DoneAttentionToolTip => WaitingText(WaitingDone());

    private bool Waits(TodoItem item) => item.NeedsReview || Owner.HasUnread(item.Id, item.Comments);
    private IEnumerable<TodoItem> WaitingOpen() => OpenRows.Select(r => r.Item).Where(Waits);
    /// <summary>What waits in the Done list, read from the category itself while the list has no rows.</summary>
    private IEnumerable<TodoItem> WaitingDone() =>
        (_doneBuilt ? DoneRows.Select(r => r.Item) : DoneTopLevel().SelectMany(i => i.Descendants().Prepend(i))).Where(Waits);

    private string WaitingText(IEnumerable<TodoItem> waiting)
    {
        const int listed = 5;
        var items = waiting.ToList();
        var lines = items.Take(listed).Select(i => $"{ItemRowViewModel.AttentionKindOf(i.NeedsReview, Owner.HasUnread(i.Id, i.Comments))}: {Short(i.DisplayText())}");
        var more = items.Count > listed ? $"\n…and {items.Count - listed} more" : "";
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
    public bool ShowAttention => WaitingOpen().Any() || WaitingDone().Any();
    /// <summary>The same for the folded Done list (agents' finished work waits there for review).</summary>
    public bool ShowDoneAttention => !ShowDoneRows && WaitingDone().Any();

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

    /// <summary>
    /// The item's row and its sub-items' rows: the row it had when it's still in the same place (re-read if it's
    /// <paramref name="stale"/>, or always when that's null), else a new one.
    /// </summary>
    private void AddRows(List<ItemRowViewModel> rows, TodoItem item, TodoItem? parent, int depth, bool done,
        Dictionary<TodoItem, ItemRowViewModel> known, HashSet<TodoItem>? stale)
    {
        if (known.Remove(item, out var row) && ReferenceEquals(row.Parent, parent) && row.Depth == depth && row.InDoneSection == done)
        {
            if (stale == null || stale.Contains(item)) row.Refresh();
        }
        else row = new ItemRowViewModel(this, item, parent, depth, done);
        rows.Add(row);
        foreach (var child in Ordered(item.Children, done)) AddRows(rows, child, item, depth + 1, done, known, stale);
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
