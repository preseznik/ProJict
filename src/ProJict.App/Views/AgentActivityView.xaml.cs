using System.ComponentModel;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Threading;
using ProJict.App.ViewModels;

namespace ProJict.App.Views;

/// <summary>
/// The Agents view: who is working where right now, and what they did before. The office stands beside the page
/// when the view is wide enough, else above the cards; a click on a robot scrolls to its card and flashes it.
/// </summary>
public partial class AgentActivityView : UserControl
{
    /// <summary>From this width the office goes beside the page.</summary>
    public const double SideBySideFrom = 1100;

    private AgentActivityViewModel? _vm;
    private AgentCardViewModel? _flashing;
    private readonly DispatcherTimer _flash;

    public AgentActivityView()
    {
        InitializeComponent();
        _flash = new DispatcherTimer { Interval = TimeSpan.FromSeconds(1.6) };
        _flash.Tick += (_, _) => StopFlash();
        SizeChanged += (_, _) => PlaceOffice();
        DataContextChanged += (_, _) => Attach(DataContext as AgentActivityViewModel);
        Loaded += (_, _) => Attach(DataContext as AgentActivityViewModel);
        Unloaded += (_, _) => Attach(null);
    }

    public bool OfficeBeside => OfficeColumn.Width.Value > 0;

    private void Attach(AgentActivityViewModel? vm)
    {
        if (_vm == vm) return;
        if (_vm != null)
        {
            _vm.RevealRequested -= OnRevealRequested;
            _vm.PropertyChanged -= OnViewModelChanged;
        }
        _vm = vm;
        if (_vm != null)
        {
            _vm.RevealRequested += OnRevealRequested;
            _vm.PropertyChanged += OnViewModelChanged;
        }
        PlaceOffice();
    }

    private void OnViewModelChanged(object? sender, PropertyChangedEventArgs e)
    {
        if (e.PropertyName == nameof(AgentActivityViewModel.ShowOffice)) PlaceOffice();
    }

    /// <summary>
    /// The office and the list of all agents: beside the page when there's room (about two fifths of the width, less
    /// with the office hidden), else above the cards.
    /// </summary>
    private void PlaceOffice()
    {
        var office = _vm?.ShowOffice ?? false;
        var beside = ActualWidth >= SideBySideFrom;
        var host = beside ? OfficeSide : OfficeTop;
        if (OfficePane.Parent != host)
        {
            (OfficePane.Parent as Border)!.Child = null;
            host.Child = OfficePane;
        }
        OfficeColumn.Width = new GridLength(!beside ? 0 : office ? Math.Clamp(ActualWidth * 0.42, 440, 820) : 360);
        OfficeCard.MaxHeight = beside ? double.PositiveInfinity : 420;
        OfficeSide.Visibility = beside ? Visibility.Visible : Visibility.Collapsed;
        OfficeTop.Visibility = beside ? Visibility.Collapsed : Visibility.Visible;
    }

    private void OnRevealRequested(AgentCardViewModel card)
    {
        StopFlash();
        _flashing = card;
        card.IsFlashing = true;
        _flash.Start();
        // Its card may only just have been made (it was hidden): scroll once it's laid out.
        Dispatcher.BeginInvoke(DispatcherPriority.Loaded, () =>
        {
            if (LiveCards.ItemContainerGenerator.ContainerFromItem(card) is FrameworkElement container) container.BringIntoView();
        });
    }

    private void AgentRow_ContextMenuOpening(object sender, ContextMenuEventArgs e) => ((sender as FrameworkElement)?.DataContext as AgentListRow)?.RefreshMenu();

    /// <summary>Right-click an agent › Invite to channel › a channel.</summary>
    private void InviteTarget_Click(object sender, RoutedEventArgs e)
    {
        if (e.OriginalSource is MenuItem { DataContext: InviteTarget target }) target.InviteCommand.Execute(null);
    }

    /// <summary>Renaming an agent: the name is selected to type over (only while ProJict is in front, so nothing takes focus from you).</summary>
    private void RenameBox_IsVisibleChanged(object sender, DependencyPropertyChangedEventArgs e)
    {
        if (sender is not TextBox box || !box.IsVisible || Window.GetWindow(box) is not { IsActive: true }) return;
        Dispatcher.BeginInvoke(DispatcherPriority.Input, () =>
        {
            box.Focus();
            box.SelectAll();
        });
    }

    /// <summary>Clicking elsewhere keeps a changed name, like Enter, and drops an unchanged one.</summary>
    private void RenameBox_LostKeyboardFocus(object sender, KeyboardFocusChangedEventArgs e)
    {
        if (e.NewFocus == null || sender is not TextBox { DataContext: AgentListRow { IsRenaming: true } row }) return;
        if (string.Equals(row.RenameText.Trim(), row.Name, StringComparison.Ordinal)) row.CancelRenameCommand.Execute(null);
        else row.CommitRenameCommand.Execute(null);
    }

    private void StopFlash()
    {
        _flash.Stop();
        if (_flashing != null) _flashing.IsFlashing = false;
        _flashing = null;
    }
}
