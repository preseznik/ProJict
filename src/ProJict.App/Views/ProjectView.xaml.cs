using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Threading;
using ProJict.App.Services;
using ProJict.App.ViewModels;

namespace ProJict.App.Views;

/// <summary>Keyboard, focus and drag &amp; drop plumbing for the outline board; all edits go to <see cref="ProjectViewModel"/>.</summary>
public partial class ProjectView : UserControl
{
    private ProjectViewModel? _vm;
    private Point _dragStart;
    private object? _dragCandidate;

    public ProjectView()
    {
        InitializeComponent();
        DataContextChanged += OnDataContextChanged;
        PreviewKeyDown += OnPreviewKeyDown;
        // Row text boxes would otherwise swallow the mouse wheel.
        BoardScroll.PreviewMouseWheel += (_, e) =>
        {
            BoardScroll.ScrollToVerticalOffset(BoardScroll.VerticalOffset - e.Delta * 0.6);
            e.Handled = true;
        };
    }

    private void OnDataContextChanged(object sender, DependencyPropertyChangedEventArgs e)
    {
        if (_vm != null)
        {
            _vm.FocusRequested -= OnFocusRequested;
            _vm.FocusAddItemRequested -= OnFocusAddItem;
            _vm.CaretOf = null;
        }
        _vm = DataContext as ProjectViewModel;
        if (_vm != null)
        {
            _vm.FocusRequested += OnFocusRequested;
            _vm.FocusAddItemRequested += OnFocusAddItem;
            _vm.CaretOf = id => FindRowTextBox(id) is { IsKeyboardFocused: true } box ? box.CaretIndex : null;
        }
    }

    private void OnPreviewKeyDown(object sender, KeyEventArgs e)
    {
        if (e.Key == Key.F && Keyboard.Modifiers == ModifierKeys.Control && _vm != null)
        {
            _vm.SelectedTab = 0;
            Dispatcher.BeginInvoke(() =>
            {
                FilterBox.Focus();
                FilterBox.SelectAll();
            }, DispatcherPriority.Loaded);
            e.Handled = true;
        }
    }

    // ───────────── Narrow layout ─────────────

    /// <summary>
    /// The toolbar's width with every label and the filter box at its narrowest, what the minor labels take of it, and
    /// the space around the toolbar in this view.
    /// </summary>
    private double _fullToolbarWidth, _minorLabelsWidth, _toolbarChrome, _offeredWidth;

    private TextBlock[] ToolbarLabels => [PriorityLabel, WorkingOnLabel, ShowDoneLabel, SideBySideLabel, ToggleAllLabel, HideEmptyLabel, CategoryLabel];
    /// <summary>The labels that go first when the toolbar gets tight: their icons say enough.</summary>
    private TextBlock[] MinorLabels => [SideBySideLabel, ToggleAllLabel, HideEmptyLabel];

    /// <summary>
    /// Fits the toolbar and header to the width this view is offered (less with the discussion panel open): the toolbar
    /// buttons drop their labels, the minor ones first (their tooltips still name them), and the header buttons wrap
    /// before reaching the title. Decided here because the toolbar can't shrink below its content, so its own size never
    /// shows it is too narrow.
    /// </summary>
    protected override Size MeasureOverride(Size constraint)
    {
        if (!double.IsInfinity(constraint.Width))
        {
            _offeredWidth = constraint.Width;
            var needed = _fullToolbarWidth + _toolbarChrome;
            var tight = _fullToolbarWidth > 0 && constraint.Width < needed;
            var compact = tight && constraint.Width < needed - _minorLabelsWidth;
            var labels = compact ? Visibility.Collapsed : Visibility.Visible;
            foreach (var label in ToolbarLabels)
            {
                var visibility = tight && MinorLabels.Contains(label) ? Visibility.Collapsed : labels;
                if (label.Visibility == visibility) continue;
                label.Visibility = visibility;
                // Re-measured up to the toolbar: a button whose margin changes below in this same pass otherwise keeps
                // its content's old size (Show done stayed as wide as with its label).
                for (DependencyObject? d = label; d != null && d != Toolbar; d = VisualTreeHelper.GetParent(d))
                    (d as UIElement)?.InvalidateMeasure();
            }
            // Compact also narrows the filters and gaps a little, so the buttons on the right fit next to the discussion panel.
            TagFilterBox.Width = StateFilterBox.Width = compact ? 90 : 130;
            TagFilterBox.Margin = StateFilterBox.Margin = PriorityButton.Margin = new Thickness(compact ? 4 : 8, 0, 0, 0);
            Toolbar.ColumnDefinitions[0].MinWidth = compact ? 70 : 140;
            FilterHint.Text = compact ? "Filter" : "Filter (Ctrl+F)";
            PriorityChevron.Visibility = labels;
            ShowDoneToggle.Margin = new Thickness(compact ? 4 : 12, 0, 0, 0);
            ToolbarRight.Margin = new Thickness(compact ? 4 : 12, 0, 0, 0);
            // Leaves room for the title and the status and group boxes under it.
            HeaderButtons.MaxWidth = Math.Clamp(constraint.Width - 340, 180, 560);
        }
        return base.MeasureOverride(constraint);
    }

    private void Toolbar_SizeChanged(object sender, SizeChangedEventArgs e)
    {
        if (ToolbarLabels.Any(l => l.Visibility != Visibility.Visible)) return;
        _fullToolbarWidth = Toolbar.ColumnDefinitions[0].MinWidth + ToolbarMiddle.DesiredSize.Width + ToolbarRight.DesiredSize.Width;
        _minorLabelsWidth = MinorLabels.Sum(l => l.ActualWidth + l.Margin.Left);
        _toolbarChrome = 2 * Toolbar.TranslatePoint(default, this).X;
        if (_offeredWidth > 0 && _offeredWidth < _fullToolbarWidth + _toolbarChrome) InvalidateMeasure();
    }

    // ───────────── Priority ─────────────

    /// <summary>The toolbar's Priority menu: sort the board by priority, and show only items of some priority.</summary>
    private void PriorityButton_Click(object sender, RoutedEventArgs e)
    {
        if (_vm is not { } vm) return;
        var menu = new ContextMenu { PlacementTarget = PriorityButton, Placement = System.Windows.Controls.Primitives.PlacementMode.Bottom };
        menu.Items.Add(new MenuItem
        {
            Header = "Sort by priority", IsChecked = vm.SortByPriority,
            Command = new CommunityToolkit.Mvvm.Input.RelayCommand(() => vm.SortByPriority = !vm.SortByPriority),
        });
        menu.Items.Add(new Separator());
        foreach (var option in ProjectViewModel.PriorityFilters)
            menu.Items.Add(new MenuItem
            {
                Header = option.Label, IsChecked = vm.PriorityFilter == option,
                Command = new CommunityToolkit.Mvvm.Input.RelayCommand(() => vm.PriorityFilter = option),
            });
        menu.IsOpen = true;
    }

    /// <summary>The priority choices for one item (its chip and its right-click menu).</summary>
    private static IEnumerable<MenuItem> PriorityItems(ItemRowViewModel row) =>
        ProjectViewModel.PriorityChoices.Select((p, i) => new MenuItem
        {
            Header = Core.Model.Priorities.Label(p), IsChecked = row.Priority == p, InputGestureText = $"Alt+{i + 1}",
            Command = row.SetPriorityCommand, CommandParameter = p,
        }).Append(new MenuItem { Header = "None", IsChecked = !row.HasPriority, InputGestureText = "Alt+0", Command = row.SetPriorityCommand, CommandParameter = Core.Model.Priority.None });

    private void PriorityChip_Click(object sender, RoutedEventArgs e)
    {
        if (sender is not FrameworkElement { DataContext: ItemRowViewModel row } chip) return;
        var menu = new ContextMenu { PlacementTarget = chip, Placement = System.Windows.Controls.Primitives.PlacementMode.Bottom };
        foreach (var item in PriorityItems(row)) menu.Items.Add(item);
        menu.IsOpen = true;
    }

    private void FilterBox_PreviewKeyDown(object sender, KeyEventArgs e)
    {
        if (e.Key == Key.Escape && _vm != null)
        {
            _vm.FilterText = "";
            e.Handled = true;
        }
        else if (e.Key == Key.Down && _vm?.VisibleRows().FirstOrDefault() is { } first)
        {
            OnFocusRequested(first.Item.Id, -1);
            e.Handled = true;
        }
    }

    // ───────────── Focus ─────────────

    private void OnFocusRequested(Guid itemId, int caret)
    {
        Dispatcher.BeginInvoke(() =>
        {
            var box = FindRowTextBox(itemId);
            if (box == null)
            {
                UpdateLayout();
                box = FindRowTextBox(itemId);
            }
            if (box == null) return;
            box.Focus();
            box.CaretIndex = caret < 0 ? box.Text.Length : Math.Min(caret, box.Text.Length);
            box.BringIntoView();
        }, DispatcherPriority.Loaded);
    }

    private void OnFocusAddItem(CategoryViewModel category)
    {
        Dispatcher.BeginInvoke(() =>
        {
            var box = FindVisual<TextBox>(Board, t => t.Uid == "AddItem" && t.DataContext == category);
            if (box == null) return;
            box.Focus();
            box.BringIntoView();
        }, DispatcherPriority.Loaded);
    }

    private TextBox? FindRowTextBox(Guid itemId) =>
        FindVisual<TextBox>(Board, t => t.Uid == "RowText" && t.DataContext is ItemRowViewModel r && r.Item.Id == itemId && t.IsVisible);

    private static T? FindVisual<T>(DependencyObject root, Func<T, bool> predicate) where T : DependencyObject
    {
        var count = VisualTreeHelper.GetChildrenCount(root);
        for (var i = 0; i < count; i++)
        {
            var child = VisualTreeHelper.GetChild(root, i);
            if (child is T match && predicate(match)) return match;
            var found = FindVisual(child, predicate);
            if (found != null) return found;
        }
        return null;
    }

    // ───────────── Row keyboard ─────────────

    private void RowText_GotFocus(object sender, KeyboardFocusChangedEventArgs e)
    {
        if (sender is FrameworkElement { DataContext: ItemRowViewModel row }) row.Owner.OnRowGotFocus(row);
    }

    private void RowText_LostFocus(object sender, KeyboardFocusChangedEventArgs e)
    {
        if (sender is FrameworkElement { DataContext: ItemRowViewModel row }) row.Owner.OnRowLostFocus(row);
    }

    private void Header_GotFocus(object sender, KeyboardFocusChangedEventArgs e) => _vm?.OnHeaderGotFocus();

    private void RowText_PreviewKeyDown(object sender, KeyEventArgs e)
    {
        if (sender is not TextBox box || box.DataContext is not ItemRowViewModel row) return;
        var vm = row.Owner;
        var mods = Keyboard.Modifiers;
        var key = e.Key == Key.System ? e.SystemKey : e.Key;
        var caret = box.CaretIndex;

        switch (key)
        {
            case Key.Enter when mods == ModifierKeys.Shift:
                var at = box.CaretIndex;
                box.Text = box.Text.Insert(at, "\n");
                box.CaretIndex = at + 1;
                break;
            case Key.Enter when mods == ModifierKeys.Control:
                vm.SetState(row, row.IsDone ? Core.Model.ItemState.Todo : Core.Model.ItemState.Done);
                break;
            case Key.Enter when mods == ModifierKeys.None:
                vm.InsertSibling(row, before: caret == 0 && box.Text.Length > 0);
                break;
            case Key.Tab when mods == ModifierKeys.None:
                vm.Indent(row, caret);
                break;
            case Key.Tab when mods == ModifierKeys.Shift:
                vm.Outdent(row, caret);
                break;
            case Key.Up when mods == ModifierKeys.Alt:
                vm.Move(row, -1, caret);
                break;
            case Key.Down when mods == ModifierKeys.Alt:
                vm.Move(row, +1, caret);
                break;
            case Key.Up when mods == ModifierKeys.None && box.GetLineIndexFromCharacterIndex(caret) <= 0:
                vm.FocusRelative(row, -1);
                break;
            case Key.Down when mods == ModifierKeys.None && box.GetLineIndexFromCharacterIndex(caret) >= box.LineCount - 1:
                vm.FocusRelative(row, +1);
                break;
            case Key.I when mods == ModifierKeys.Control:
                vm.SetState(row, row.IsInProgress ? Core.Model.ItemState.Todo : Core.Model.ItemState.InProgress);
                break;
            case Key.D0 or Key.NumPad0 when mods == ModifierKeys.Alt:
                vm.SetPriority(row, Core.Model.Priority.None);
                break;
            case >= Key.D1 and <= Key.D5 when mods == ModifierKeys.Alt:
                vm.SetPriority(row, ProjectViewModel.PriorityChoices[key - Key.D1]);
                break;
            case >= Key.NumPad1 and <= Key.NumPad5 when mods == ModifierKeys.Alt:
                vm.SetPriority(row, ProjectViewModel.PriorityChoices[key - Key.NumPad1]);
                break;
            case Key.V when mods == ModifierKeys.Control && ClipboardAttachments.HasAttachment():
                vm.PasteAttachments(row);
                break;
            case Key.Space when mods == ModifierKeys.Control:
                row.IsSelected = !row.IsSelected;
                break;
            case Key.Back when mods == ModifierKeys.None && box.Text.Length == 0:
                vm.Delete(row, focusPrevious: true);
                break;
            case Key.Delete when mods == (ModifierKeys.Control | ModifierKeys.Shift):
                vm.Delete(row, focusPrevious: false);
                break;
            case Key.Escape:
                if (vm.AllRows.Any(r => r.IsSelected)) vm.ClearSelectionCommand.Execute(null);
                else Keyboard.ClearFocus();
                break;
            default:
                return;
        }
        e.Handled = true;
    }

    // ───────────── Item menu ─────────────
    // Right-click on the text (whose built-in Cut/Copy/Paste menu is switched off in XAML), right-click anywhere else on
    // the row, or its ⋯ button.

    /// <summary>
    /// A text box shows its own Cut/Copy/Paste menu on right-click before any handler of ours runs, unless its
    /// ContextMenu is explicitly null on the box itself; a value from the row template doesn't count, so it's set here.
    /// </summary>
    private void RowText_Loaded(object sender, RoutedEventArgs e)
    {
        if (sender is TextBox box && box.ReadLocalValue(ContextMenuProperty) != null) box.ContextMenu = null;
    }

    private void RowText_ContextMenuOpening(object sender, ContextMenuEventArgs e)
    {
        if (sender is not TextBox box || box.DataContext is not ItemRowViewModel row) return;
        e.Handled = true;
        // From the keyboard (Menu key, Shift+F10) the menu opens under the item rather than at the mouse.
        OpenItemMenu(row, box, box, e.CursorLeft < 0 ? System.Windows.Controls.Primitives.PlacementMode.Bottom
            : System.Windows.Controls.Primitives.PlacementMode.MousePoint);
    }

    private void Row_ContextMenuOpening(object sender, ContextMenuEventArgs e)
    {
        if (sender is not FrameworkElement { DataContext: ItemRowViewModel row } element) return;
        e.Handled = true;
        OpenItemMenu(row, element, FindRowTextBox(row.Item.Id), System.Windows.Controls.Primitives.PlacementMode.MousePoint);
    }

    private void RowMore_Click(object sender, RoutedEventArgs e)
    {
        if (sender is not FrameworkElement { DataContext: ItemRowViewModel row } button) return;
        OpenItemMenu(row, button, FindRowTextBox(row.Item.Id), System.Windows.Controls.Primitives.PlacementMode.Bottom);
    }

    private void OpenItemMenu(ItemRowViewModel row, UIElement target, TextBox? box, System.Windows.Controls.Primitives.PlacementMode placement)
    {
        var menu = BuildItemMenu(row, box);
        menu.PlacementTarget = target;
        menu.Placement = placement;
        menu.IsOpen = true;
    }

    /// <summary>
    /// Everything you can do with an item. Cut/Copy/Paste act on its text box when given. Its own font is set, so it
    /// doesn't pick up the icon font of the ⋯ button.
    /// </summary>
    internal ContextMenu BuildItemMenu(ItemRowViewModel row, TextBox? box)
    {
        var menu = new ContextMenu { FontFamily = FontFamily };

        MenuItem Item(string header, ICommand command, object? parameter = null, string? gesture = null) =>
            new() { Header = header, Command = command, CommandParameter = parameter, InputGestureText = gesture ?? "" };

        if (row.NeedsReview) menu.Items.Add(Item("Accept review", row.AcceptReviewCommand));
        if (row.ClaimedBy != null) menu.Items.Add(Item($"Release {row.ClaimedBy}'s check-out", row.ReleaseClaimCommand));
        if (row.NeedsReview || row.ClaimedBy != null) menu.Items.Add(new Separator());
        menu.Items.Add(Item(row.CommentCount > 0 ? $"Discussion ({row.CommentCount})…" : "Discussion…", row.OpenDiscussionCommand));
        if (row.Owner.CanStartChannels || row.Channels.Count > 0)
        {
            foreach (var chip in row.Channels) menu.Items.Add(Item($"Open {chip.Text}", chip.OpenCommand));
            if (row.Owner.CanStartChannels) menu.Items.Add(Item("Start a chat channel…", row.StartChannelCommand));
        }
        if (!row.IsFocusTarget) menu.Items.Add(Item("Focus", row.FocusCommand));
        if (row.Owner.IsFocused) menu.Items.Add(Item("Leave focus", row.Owner.LeaveFocusCommand));
        if (row.Contributors.Count > 0)
        {
            var workedBy = new MenuItem { Header = "Show what was worked on by" };
            foreach (var contributor in row.Contributors)
                workedBy.Items.Add(Item(contributor.Name, row.Owner.ShowWorkedByCommand, contributor.Name));
            menu.Items.Add(workedBy);
        }
        menu.Items.Add(new Separator());
        menu.Items.Add(Item(row.IsDone ? "Mark not done" : "Mark done", row.ToggleDoneCommand, gesture: "Ctrl+Enter"));
        menu.Items.Add(Item(row.IsInProgress ? "Not in progress" : "In progress", row.ToggleInProgressCommand, gesture: "Ctrl+I"));
        menu.Items.Add(Item("Add sub-item", row.AddChildCommand));
        menu.Items.Add(Item("Attach files…", row.AttachCommand));
        menu.Items.Add(Item("Link files (no copy)…", row.LinkMediaCommand));
        menu.Items.Add(Item(row.TimeChip is { } spent ? $"Log time… ({spent} so far)" : "Log time…", row.LogTimeCommand));
        if (ClipboardAttachments.HasAttachment()) menu.Items.Add(Item("Paste as attachment", row.PasteAttachmentsCommand, gesture: "Ctrl+V"));

        var priority = new MenuItem { Header = "Priority" };
        foreach (var item in PriorityItems(row)) priority.Items.Add(item);
        menu.Items.Add(priority);

        var tags = new MenuItem { Header = "Tag" };
        foreach (var tag in ProjectViewModel.TagChoices)
            tags.Items.Add(new MenuItem { Header = tag, Command = row.SetTagCommand, CommandParameter = tag, IsChecked = row.Tag == tag });
        tags.Items.Add(new Separator());
        tags.Items.Add(Item("No tag", row.SetTagCommand, null));
        menu.Items.Add(tags);

        var move = new MenuItem { Header = "Move to category" };
        foreach (var category in row.Owner.Categories)
            move.Items.Add(Item(category.Name, row.MoveToCategoryCommand, category));
        menu.Items.Add(move);

        menu.Items.Add(new Separator());
        menu.Items.Add(Item($"Copy {ProjectViewModel.ShortIdOf(row.Item)}", row.CopyIdCommand));
        menu.Items.Add(Item("Copy as prompt", row.CopyPromptCommand));
        menu.Items.Add(new MenuItem { Header = "Select for prompt", Command = row.ToggleSelectedCommand, IsChecked = row.IsSelected, InputGestureText = "Ctrl+Space" });
        menu.Items.Add(Item("Promote to project…", row.PromoteCommand));
        menu.Items.Add(new Separator());
        if (box != null)
        {
            menu.Items.Add(new MenuItem { Header = "Cut", Command = ApplicationCommands.Cut, CommandTarget = box, InputGestureText = "Ctrl+X" });
            menu.Items.Add(new MenuItem { Header = "Copy", Command = ApplicationCommands.Copy, CommandTarget = box, InputGestureText = "Ctrl+C" });
            menu.Items.Add(new MenuItem { Header = "Paste", Command = ApplicationCommands.Paste, CommandTarget = box, InputGestureText = "Ctrl+V" });
            menu.Items.Add(new Separator());
        }
        menu.Items.Add(Item("Delete", row.DeleteCommand, gesture: "Ctrl+Shift+Del"));
        return menu;
    }

    /// <summary>Right-click on a row's checkbox: the two states the item isn't in.</summary>
    private void StateBox_ContextMenuOpening(object sender, ContextMenuEventArgs e)
    {
        if (sender is not CheckBox box || box.DataContext is not ItemRowViewModel row) return;
        e.Handled = true;
        var menu = new ContextMenu { PlacementTarget = box };
        foreach (var state in row.OtherStates)
        {
            var (header, gesture) = state switch
            {
                Core.Model.ItemState.Done => ("Mark as done", "Ctrl+Enter"),
                Core.Model.ItemState.InProgress => ("Mark as in progress", "Ctrl+I"),
                _ => ("Mark as to do", row.IsDone ? "Ctrl+Enter" : "Ctrl+I"),
            };
            menu.Items.Add(new MenuItem { Header = header, InputGestureText = gesture, Command = new CommunityToolkit.Mvvm.Input.RelayCommand(() => row.Owner.SetState(row, state)) });
        }
        menu.IsOpen = true;
    }

    private void Attachment_ContextMenuOpening(object sender, ContextMenuEventArgs e)
    {
        if (sender is not FrameworkElement { DataContext: AttachmentViewModel attachment } element) return;
        e.Handled = true;
        var menu = new ContextMenu { PlacementTarget = element };
        menu.Items.Add(new MenuItem { Header = attachment.OpensInViewer ? "Open (mark up)" : "Open", Command = attachment.OpenCommand });
        if (attachment.OpensInViewer)
            menu.Items.Add(new MenuItem { Header = "Open in your image viewer", Command = attachment.OpenExternallyCommand });
        menu.Items.Add(new MenuItem { Header = "Show in folder", Command = attachment.ShowInFolderCommand });
        menu.Items.Add(new MenuItem { Header = "Copy path", Command = attachment.CopyPathCommand });
        menu.Items.Add(new Separator());
        menu.Items.Add(new MenuItem { Header = "Remove", Command = attachment.RemoveCommand });
        menu.IsOpen = true;
    }

    // ───────────── Add item box: paste or drop files ─────────────

    private void AddItem_PreviewKeyDown(object sender, KeyEventArgs e)
    {
        if (e.Key != Key.V || Keyboard.Modifiers != ModifierKeys.Control || !ClipboardAttachments.HasAttachment()) return;
        if (sender is not FrameworkElement { DataContext: CategoryViewModel category }) return;
        var (files, png) = ClipboardAttachments.Read();
        _ = category.Owner.AddItemWithAttachments(category, files, png);
        e.Handled = true;
    }

    private void AddItem_PreviewDragOver(object sender, DragEventArgs e)
    {
        if (!e.Data.GetDataPresent(DataFormats.FileDrop)) return;
        e.Effects = DragDropEffects.Copy;
        e.Handled = true;
    }

    private void AddItem_PreviewDrop(object sender, DragEventArgs e)
    {
        if (!e.Data.GetDataPresent(DataFormats.FileDrop) || sender is not FrameworkElement { DataContext: CategoryViewModel category }) return;
        e.Handled = true;
        if (e.Data.GetData(DataFormats.FileDrop) is string[] paths) _ = category.Owner.AddItemWithAttachments(category, paths, null);
    }

    private void AddItemButton_Click(object sender, RoutedEventArgs e)
    {
        if (sender is FrameworkElement { DataContext: CategoryViewModel category }) category.Owner.RequestAddItem(category);
    }

    private void CategoryMore_Click(object sender, RoutedEventArgs e)
    {
        if (sender is not FrameworkElement { DataContext: CategoryViewModel category } button) return;
        var menu = BuildCategoryMenu(category);
        menu.PlacementTarget = button;
        menu.IsOpen = true;
    }

    /// <summary>The category's "…" menu. Its own font is set: it would take the icon font from the "…" button and show its text as boxes.</summary>
    internal ContextMenu BuildCategoryMenu(CategoryViewModel category)
    {
        var menu = new ContextMenu { FontFamily = FontFamily };
        var categories = category.Owner.Project.Categories;
        var first = categories.FirstOrDefault() == category.Model;
        var last = categories.LastOrDefault() == category.Model;
        menu.Items.Add(new MenuItem { Header = "Move to top", Command = category.MoveToTopCommand, IsEnabled = !first });
        menu.Items.Add(new MenuItem { Header = "Move up", Command = category.MoveUpCommand, IsEnabled = !first });
        menu.Items.Add(new MenuItem { Header = "Move down", Command = category.MoveDownCommand, IsEnabled = !last });
        menu.Items.Add(new MenuItem { Header = "Move to bottom", Command = category.MoveToBottomCommand, IsEnabled = !last });
        menu.Items.Add(new MenuItem { Header = category.IsDoneExpanded ? "Hide done" : "Show done", Command = new CommunityToolkit.Mvvm.Input.RelayCommand(() => category.IsDoneExpanded = !category.IsDoneExpanded) });
        if (category.Owner.CanStartChannels || category.Channels.Count > 0)
        {
            menu.Items.Add(new Separator());
            foreach (var chip in category.Channels) menu.Items.Add(new MenuItem { Header = $"Open {chip.Text}", Command = chip.OpenCommand });
            if (category.Owner.CanStartChannels) menu.Items.Add(new MenuItem { Header = "Start a chat channel…", Command = category.StartChannelCommand });
        }
        menu.Items.Add(new Separator());
        menu.Items.Add(new MenuItem { Header = "Delete category…", Command = category.DeleteCommand });
        return menu;
    }

    // ───────────── Drag & drop ─────────────

    private void Grip_MouseDown(object sender, MouseButtonEventArgs e)
    {
        if (sender is not FrameworkElement { DataContext: ItemRowViewModel row }) return;
        if (Keyboard.Modifiers.HasFlag(ModifierKeys.Control))
        {
            row.IsSelected = !row.IsSelected;
            e.Handled = true;
            return;
        }
        _dragStart = e.GetPosition(this);
        _dragCandidate = row;
    }

    private void CategoryGrip_MouseDown(object sender, MouseButtonEventArgs e)
    {
        if (sender is not FrameworkElement { DataContext: CategoryViewModel category }) return;
        _dragStart = e.GetPosition(this);
        _dragCandidate = category;
    }

    private void Grip_MouseMove(object sender, MouseEventArgs e) => TryStartDrag(sender, e);
    private void CategoryGrip_MouseMove(object sender, MouseEventArgs e) => TryStartDrag(sender, e);

    private void TryStartDrag(object sender, MouseEventArgs e)
    {
        if (e.LeftButton != MouseButtonState.Pressed || _dragCandidate == null) return;
        var delta = e.GetPosition(this) - _dragStart;
        if (Math.Abs(delta.X) < SystemParameters.MinimumHorizontalDragDistance &&
            Math.Abs(delta.Y) < SystemParameters.MinimumVerticalDragDistance) return;
        var payload = _dragCandidate;
        _dragCandidate = null;
        DragDrop.DoDragDrop((DependencyObject)sender, new DataObject(payload.GetType(), payload), DragDropEffects.Move);
        ClearDropTargets();
    }

    private static T? Payload<T>(DragEventArgs e) where T : class =>
        e.Data.GetDataPresent(typeof(T)) ? e.Data.GetData(typeof(T)) as T : null;

    private void Row_DragOver(object sender, DragEventArgs e)
    {
        var target = (sender as FrameworkElement)?.DataContext as ItemRowViewModel;
        var source = Payload<ItemRowViewModel>(e);
        e.Effects = source != null && target != null && source.Item != target.Item ? DragDropEffects.Move : DragDropEffects.None;
        if (target != null) target.IsDropTarget = e.Effects == DragDropEffects.Move;
        e.Handled = true;
    }

    private void Row_DragLeave(object sender, DragEventArgs e)
    {
        if ((sender as FrameworkElement)?.DataContext is ItemRowViewModel row) row.IsDropTarget = false;
    }

    private void Row_Drop(object sender, DragEventArgs e)
    {
        if (sender is not FrameworkElement { DataContext: ItemRowViewModel target } element) return;
        target.IsDropTarget = false;
        if (Payload<ItemRowViewModel>(e) is not { } source) return;
        var after = e.GetPosition(element).Y > element.ActualHeight / 2;
        e.Handled = true;
        target.Owner.MoveItem(source, target, after);
    }

    // Files from Explorer are taken in the preview, before the row's text box can claim them.
    private void Row_PreviewFileDragOver(object sender, DragEventArgs e)
    {
        if (!e.Data.GetDataPresent(DataFormats.FileDrop) || (sender as FrameworkElement)?.DataContext is not ItemRowViewModel row) return;
        e.Effects = DragDropEffects.Copy;
        row.IsDropTarget = true;
        e.Handled = true;
    }

    private void Row_PreviewFileDrop(object sender, DragEventArgs e)
    {
        if (!e.Data.GetDataPresent(DataFormats.FileDrop) || (sender as FrameworkElement)?.DataContext is not ItemRowViewModel row) return;
        row.IsDropTarget = false;
        e.Handled = true;
        if (e.Data.GetData(DataFormats.FileDrop) is string[] paths) _ = row.Owner.AttachFiles(row, paths);
    }

    private void Category_DragOver(object sender, DragEventArgs e)
    {
        var target = (sender as FrameworkElement)?.DataContext as CategoryViewModel;
        var ok = target != null && (Payload<ItemRowViewModel>(e) != null || Payload<CategoryViewModel>(e) is { } c && c != target);
        e.Effects = ok ? DragDropEffects.Move : DragDropEffects.None;
        if (target != null) target.IsDropTarget = ok;
        e.Handled = true;
    }

    private void Category_DragLeave(object sender, DragEventArgs e)
    {
        if ((sender as FrameworkElement)?.DataContext is CategoryViewModel category) category.IsDropTarget = false;
    }

    private void Category_Drop(object sender, DragEventArgs e)
    {
        if ((sender as FrameworkElement)?.DataContext is not CategoryViewModel target) return;
        target.IsDropTarget = false;
        e.Handled = true;
        if (Payload<ItemRowViewModel>(e) is { } row) target.Owner.MoveToCategory(row, target);
        else if (Payload<CategoryViewModel>(e) is { } category) target.Owner.MoveCategoryTo(category, target);
    }

    private void ClearDropTargets()
    {
        if (_vm == null) return;
        foreach (var category in _vm.Categories)
        {
            category.IsDropTarget = false;
            foreach (var row in category.AllRows) row.IsDropTarget = false;
        }
    }
}
