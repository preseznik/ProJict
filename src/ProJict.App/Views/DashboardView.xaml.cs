using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using ProJict.App.ViewModels;

namespace ProJict.App.Views;

public partial class DashboardView : UserControl
{
    private Point? _pressedAt;

    public DashboardView()
    {
        InitializeComponent();
    }

    private DashboardViewModel? ViewModel => DataContext as DashboardViewModel;

    // ───────────── Table ─────────────

    /// <summary>A heading's click sorts by it (again: the other way), keeping the groups in the sidebar's order.</summary>
    private void Table_Sorting(object sender, DataGridSortingEventArgs e)
    {
        e.Handled = true;
        if (ViewModel is not { } vm || string.IsNullOrEmpty(e.Column.SortMemberPath)) return;
        var direction = vm.SortTable(e.Column.SortMemberPath, e.Column.SortDirection);
        foreach (var column in TableView.Columns) column.SortDirection = column == e.Column ? direction : null;
    }

    /// <summary>A click on a row opens its project.</summary>
    private void TableRow_Click(object sender, MouseButtonEventArgs e)
    {
        if (sender is DataGridRow { Item: ProjectCardViewModel card }) card.OpenCommand.Execute(null);
    }

    // ───────────── Board ─────────────

    private void BoardCard_MouseDown(object sender, MouseButtonEventArgs e) => _pressedAt = e.GetPosition(this);

    /// <summary>Dragged a little way: the project goes along, to be dropped on another status.</summary>
    private void BoardCard_MouseMove(object sender, MouseEventArgs e)
    {
        if (_pressedAt is not { } start || e.LeftButton != MouseButtonState.Pressed || sender is not FrameworkElement { DataContext: ProjectCardViewModel card } element) return;
        var moved = e.GetPosition(this) - start;
        if (Math.Abs(moved.X) < SystemParameters.MinimumHorizontalDragDistance && Math.Abs(moved.Y) < SystemParameters.MinimumVerticalDragDistance) return;
        _pressedAt = null;
        if (!card.CanChangeStatus)
        {
            ViewModel?.CantMove(card);
            return;
        }
        DragDrop.DoDragDrop(element, new DataObject(typeof(ProjectCardViewModel), card), DragDropEffects.Move);
    }

    /// <summary>A click without a drag opens the project.</summary>
    private void BoardCard_MouseUp(object sender, MouseButtonEventArgs e)
    {
        if (_pressedAt == null || sender is not FrameworkElement { DataContext: ProjectCardViewModel card }) return;
        _pressedAt = null;
        card.OpenCommand.Execute(null);
    }

    private static ProjectCardViewModel? Dragged(DragEventArgs e) => e.Data.GetData(typeof(ProjectCardViewModel)) as ProjectCardViewModel;

    private void BoardColumn_DragEnter(object sender, DragEventArgs e)
    {
        if (sender is Border border && Dragged(e) != null) border.SetResourceReference(Border.BackgroundProperty, "SubtleFillColorTertiaryBrush");
    }

    private void BoardColumn_DragOver(object sender, DragEventArgs e)
    {
        e.Effects = Dragged(e) != null ? DragDropEffects.Move : DragDropEffects.None;
        e.Handled = true;
    }

    private void BoardColumn_DragLeave(object sender, DragEventArgs e)
    {
        if (sender is Border border) border.SetResourceReference(Border.BackgroundProperty, "SubtleFillColorSecondaryBrush");
    }

    private void BoardColumn_Drop(object sender, DragEventArgs e)
    {
        BoardColumn_DragLeave(sender, e);
        if (Dragged(e) is { } card && sender is FrameworkElement { DataContext: BoardColumnViewModel column }) ViewModel?.MoveToStatus(card, column.Status);
        e.Handled = true;
    }
}
