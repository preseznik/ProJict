using System.ComponentModel;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Threading;
using ProJict.App.ViewModels;
using ProJict.Core.Agents;

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
        OfficePane.SizeChanged += (_, e) =>
        {
            if (e.WidthChanged) PlaceChannel();
        };
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
            _vm.ChannelRefreshed -= ScrollChannelToEnd;
        }
        _vm = vm;
        if (_vm != null)
        {
            _vm.RevealRequested += OnRevealRequested;
            _vm.PropertyChanged += OnViewModelChanged;
            _vm.ChannelRefreshed += ScrollChannelToEnd;
        }
        PlaceOffice();
    }

    private void OnViewModelChanged(object? sender, PropertyChangedEventArgs e)
    {
        if (e.PropertyName is nameof(AgentActivityViewModel.ShowOffice) or nameof(AgentActivityViewModel.Fold) or nameof(AgentActivityViewModel.OfficeShare)
            or nameof(AgentActivityViewModel.ShowChannel))
            PlaceOffice();
    }

    /// <summary>The narrowest the page or the office side gets while the divider is dragged.</summary>
    private const double MinPage = 420, MinOffice = 320;

    /// <summary>
    /// The office and the list of all agents: beside the page when there's room (the divider's share of the width, a
    /// narrow column with the office hidden), else above the cards. Side by side, either side can be folded into a
    /// ribbon; the office then fills the whole view, or the page does.
    /// </summary>
    private void PlaceOffice()
    {
        var office = _vm?.ShowOffice ?? false;
        var beside = ActualWidth >= SideBySideFrom;
        var fold = beside ? _vm?.Fold ?? AgentsFold.None : AgentsFold.None;
        var host = beside ? OfficeSide : OfficeTop;
        if (OfficePane.Parent != host)
        {
            (OfficePane.Parent as Border)!.Child = null;
            host.Child = OfficePane;
        }
        var share = _vm?.OfficeShare ?? AgentsViewSettings.DefaultOfficeShare;
        PageColumn.MinWidth = fold == AgentsFold.None && beside ? MinPage : 0;
        OfficeColumn.MinWidth = fold == AgentsFold.None && beside ? MinOffice : 0;
        PageColumn.Width = fold == AgentsFold.Page ? new GridLength(0) : new GridLength(1, GridUnitType.Star);
        OfficeColumn.Width = !beside || fold == AgentsFold.Office ? new GridLength(0)
            : fold == AgentsFold.Page ? new GridLength(1, GridUnitType.Star)
            : new GridLength(office ? Math.Clamp(ActualWidth * share, MinOffice, Math.Max(MinOffice, ActualWidth - MinPage)) : 360);
        DividerColumn.Width = new GridLength(beside && fold == AgentsFold.None && office ? 10 : 0);
        Divider.Visibility = beside && fold == AgentsFold.None && office ? Visibility.Visible : Visibility.Collapsed;
        PageScroll.Visibility = fold == AgentsFold.Page ? Visibility.Collapsed : Visibility.Visible;
        PageRibbon.Visibility = fold == AgentsFold.Page ? Visibility.Visible : Visibility.Collapsed;
        OfficeRibbon.Visibility = fold == AgentsFold.Office ? Visibility.Visible : Visibility.Collapsed;
        OfficeSide.Visibility = beside && fold != AgentsFold.Office ? Visibility.Visible : Visibility.Collapsed;
        OfficeTop.Visibility = beside ? Visibility.Collapsed : Visibility.Visible;
        // Beside the page the office fills the height it gets; above the cards it keeps to a sensible height.
        OfficeRow.Height = office && beside ? new GridLength(1, GridUnitType.Star) : GridLength.Auto;
        OfficeCard.MaxHeight = beside ? double.PositiveInfinity : 420;
        FoldButtons.Visibility = beside ? Visibility.Visible : Visibility.Collapsed;
        WidenButton.Visibility = fold == AgentsFold.Page || !office ? Visibility.Collapsed : Visibility.Visible;
        // Above the cards the header shows only for its Channel toggle.
        OfficeHeader.Visibility = beside || office ? Visibility.Visible : Visibility.Collapsed;
        PlaceChannel();
    }

    /// <summary>The channel under the office shows its newest message.</summary>
    private void ScrollChannelToEnd() => Dispatcher.BeginInvoke(() => ChannelScroll.ScrollToEnd(), DispatcherPriority.Loaded);

    /// <summary>From this width of the office side the channel and All agents stand side by side; narrower, one above the other.</summary>
    private const double ChannelBesideFrom = 520, AgentListBesideWidth = 240;

    /// <summary>
    /// The project's channel under the office: on the left, with All agents at the bottom right, when there's room; else
    /// All agents goes under it. Without it, All agents takes the whole width.
    /// </summary>
    private void PlaceChannel()
    {
        var channel = (_vm?.ShowOffice ?? false) && (_vm?.ShowChannel ?? false);
        var side = channel && OfficePane.ActualWidth >= ChannelBesideFrom;
        ChannelCard.Visibility = channel ? Visibility.Visible : Visibility.Collapsed;
        Grid.SetColumn(AgentListCard, side ? 1 : 0);
        Grid.SetColumnSpan(AgentListCard, side ? 1 : 2);
        Grid.SetRow(AgentListCard, channel && !side ? 1 : 0);
        Grid.SetColumnSpan(ChannelCard, side ? 1 : 2);
        AgentListCard.Width = side ? AgentListBesideWidth : double.NaN;
        AgentListCard.Margin = side ? new Thickness(10, 0, 0, 0) : channel ? new Thickness(0, 10, 0, 0) : new Thickness(0);
        AgentListCard.VerticalAlignment = side ? VerticalAlignment.Bottom : VerticalAlignment.Top;
        // Beside the page the office side has a height to share: the office and the channel split it 3:2, and the
        // channel's messages fill its card (the newest by the box). Above the cards there's no height to share.
        var fills = channel && OfficePane.Parent == OfficeSide;
        OfficeRow.Height = fills ? new GridLength(3, GridUnitType.Star)
            : (_vm?.ShowOffice ?? false) && OfficePane.Parent == OfficeSide ? new GridLength(1, GridUnitType.Star) : GridLength.Auto;
        BottomRow.Height = fills ? new GridLength(2, GridUnitType.Star) : GridLength.Auto;
        OfficeBottom.RowDefinitions[0].Height = fills ? new GridLength(1, GridUnitType.Star) : GridLength.Auto;
        ChannelScroll.Height = fills ? double.NaN : 260;
        ChannelCard.MinHeight = fills ? 200 : 0;
    }

    /// <summary>The divider was dragged: the office's new share of the width is kept.</summary>
    private void Divider_DragCompleted(object sender, DragCompletedEventArgs e)
    {
        if (_vm == null || ActualWidth <= 0) return;
        _vm.OfficeShare = OfficeColumn.ActualWidth / ActualWidth;
        PlaceOffice();
    }

    private void Divider_MouseDoubleClick(object sender, MouseButtonEventArgs e) => _vm?.ResetDividerCommand.Execute(null);

    private void OnRevealRequested(AgentCardViewModel card)
    {
        StopFlash();
        _flashing = card;
        card.IsFlashing = true;
        _flash.Start();
        // Its card may only just have been made (it was hidden): scroll once it's laid out.
        Dispatcher.BeginInvoke(DispatcherPriority.Loaded, () =>
        {
            if (LiveCards.ItemContainerGenerator.ContainerFromItem(card) is not FrameworkElement container) return;
            container.BringIntoView();
            // Clicked in the office: ready to write to it (only while ProJict is in front, so nothing takes focus from you).
            if (card.IsFocused && Window.GetWindow(this)?.IsActive == true && FindDraft(container) is { } draft) draft.Focus();
        });
    }

    private static TextBox? FindDraft(DependencyObject parent)
    {
        for (var i = 0; i < VisualTreeHelper.GetChildrenCount(parent); i++)
        {
            var child = VisualTreeHelper.GetChild(parent, i);
            if (child is TextBox { Name: "PrivateDraft" } box) return box;
            if (FindDraft(child) is { } found) return found;
        }
        return null;
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
