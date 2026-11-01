using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using ProJict.Core.Model;

namespace ProJict.App.ViewModels;

/// <summary>
/// Focus: the board shows one item (with its sub-items and the items it's under) or one category, and nothing else.
/// A link to an item (from Chat, the Feed, search, a notification, an agent's card) lands there, and so do the item's
/// ⋯ › Focus and a category's eye button. Your filters stay as they were underneath; Leave focus shows them again.
/// </summary>
public sealed partial class ProjectViewModel
{
    [ObservableProperty] private TodoItem? _focusItem;
    [ObservableProperty] private Category? _focusCategory;

    public bool IsFocused => FocusItem != null || FocusCategory != null;

    public string FocusText => FocusItem != null
        ? $"Focus on “{Shorten(FocusItem.DisplayText())}”: only it, its sub-items and the items it's under show."
        : FocusCategory != null ? $"Focus on {FocusCategory.Name}: only this category shows." : "";

    partial void OnFocusItemChanged(TodoItem? value) => FocusChanged();
    partial void OnFocusCategoryChanged(Category? value) => FocusChanged();

    private void FocusChanged()
    {
        OnPropertyChanged(nameof(IsFocused));
        OnPropertyChanged(nameof(FocusText));
        ApplyFilter();
        foreach (var category in Categories) category.RefreshFocus();
    }

    /// <summary>Only this item shows, with its sub-items and the items it's under; it gets the keyboard focus.</summary>
    public void FocusOn(TodoItem item)
    {
        SelectedTab = 0;
        if (Project.Find(item.Id) is not { } loc || CategoryFor(loc.Category) is not { } category) return;
        FocusCategory = null;
        FocusItem = loc.Item;
        category.IsExpanded = true;
        if (loc.Item.IsDone || Project.Walk().Any(l => l.Depth == 0 && l.Item.IsDone && l.Item.Descendants().Contains(item)))
            category.IsDoneExpanded = true;
        System.Windows.Application.Current.Dispatcher.BeginInvoke(() => FocusRequested?.Invoke(item.Id, -1),
            System.Windows.Threading.DispatcherPriority.Background);
    }

    /// <summary>Only this category shows.</summary>
    public void FocusOn(Category category)
    {
        SelectedTab = 0;
        FocusItem = null;
        FocusCategory = category;
        if (CategoryFor(category) is { } vm) vm.IsExpanded = true;
    }

    [RelayCommand]
    public void LeaveFocus()
    {
        FocusItem = null;
        FocusCategory = null;
    }

    /// <summary>While focused, shows just that (filters don't apply); false when nothing is focused.</summary>
    private bool ApplyFocus()
    {
        // What it was on is gone (deleted, or undone): back to the whole board.
        if (FocusItem is { } item && Project.Find(item.Id) == null) FocusItem = null;
        if (FocusCategory is { } gone && Project.Categories.All(c => c.Id != gone.Id)) FocusCategory = null;
        if (!IsFocused) return false;
        // By id: after an undo the same item or category may be a new object.
        var at = FocusItem != null ? Project.Find(FocusItem.Id) : null;
        var where = at?.Category ?? Project.Categories.First(c => c.Id == FocusCategory!.Id);
        HashSet<TodoItem>? shown = null;
        if (at != null)
        {
            var parents = new Dictionary<TodoItem, TodoItem>();
            foreach (var loc in where.Walk())
                if (loc.Parent != null) parents[loc.Item] = loc.Parent;
            shown = [.. at.Item.Descendants().Prepend(at.Item)];
            for (var p = parents.GetValueOrDefault(at.Item); p != null; p = parents.GetValueOrDefault(p)) shown.Add(p);
        }
        foreach (var category in Categories)
        {
            var mine = category.Model == where;
            foreach (var row in category.AllRows) row.IsVisible = mine && (shown == null || shown.Contains(row.Item));
            category.IsVisible = mine;
            category.RaiseShowDone();
        }
        return true;
    }
}
