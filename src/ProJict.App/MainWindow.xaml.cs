using System.ComponentModel;
using System.Windows;
using System.Windows.Controls;
using ProJict.App.ViewModels;

namespace ProJict.App;

public partial class MainWindow : Window
{
    public MainWindow(MainViewModel viewModel)
    {
        InitializeComponent();
        DataContext = viewModel;
        // Ctrl+mouse wheel makes everything bigger or smaller, in every window (#b242e155).
        Services.UiScale.Install(viewModel.State);
        RestorePlacement();
        // Back in front: the chat channel you look at is read now.
        Activated += (_, _) => viewModel.Chat.OnActivated();
        // A voice call started: its window opens beside this one.
        viewModel.Calls.Started += calls => new Views.CallWindow(new CallViewModel(calls, viewModel.State)) { Owner = this }.Show();
        // The mouse's side buttons go back and forward, like in a browser.
        PreviewMouseDown += (_, e) =>
        {
            var command = e.ChangedButton switch
            {
                System.Windows.Input.MouseButton.XButton1 => viewModel.GoBackCommand,
                System.Windows.Input.MouseButton.XButton2 => viewModel.GoForwardCommand,
                _ => null,
            };
            if (command == null) return;
            if (command.CanExecute(null)) command.Execute(null);
            e.Handled = true;
        };
    }

    private void RestorePlacement()
    {
        var s = App.Current.State.AppSettings;
        if (s.WindowWidth is > 400 && s.WindowHeight is > 300 && s.WindowLeft is { } left && s.WindowTop is { } top)
        {
            // Only restore when the window would still be on a connected screen.
            var screen = new Rect(SystemParameters.VirtualScreenLeft, SystemParameters.VirtualScreenTop,
                SystemParameters.VirtualScreenWidth, SystemParameters.VirtualScreenHeight);
            if (screen.IntersectsWith(new Rect(left, top, s.WindowWidth.Value, s.WindowHeight.Value)))
            {
                WindowStartupLocation = WindowStartupLocation.Manual;
                Left = left;
                Top = top;
                Width = s.WindowWidth.Value;
                Height = s.WindowHeight.Value;
            }
        }
        if (s.WindowMaximized) WindowState = WindowState.Maximized;
    }

    private void SavePlacement()
    {
        var s = App.Current.State.AppSettings;
        var bounds = WindowState == WindowState.Normal ? new Rect(Left, Top, Width, Height) : RestoreBounds;
        s.WindowLeft = bounds.Left;
        s.WindowTop = bounds.Top;
        s.WindowWidth = bounds.Width;
        s.WindowHeight = bounds.Height;
        s.WindowMaximized = WindowState == WindowState.Maximized;
        App.Current.State.SaveAppSettings();
    }

    protected override void OnClosing(CancelEventArgs e)
    {
        SavePlacement();
        var app = App.Current;
        app.MainVm?.Wiki.Page?.Leave();
        app.State.SaveNow();
        if (!app.IsExiting && app.State.Settings.CloseToTray)
        {
            e.Cancel = true;
            Hide();
            return;
        }
        base.OnClosing(e);
    }

    private void ProjectMenu_Opened(object sender, RoutedEventArgs e)
    {
        if (sender is FrameworkElement { DataContext: ProjectListItemViewModel item }) item.RefreshMenu();
    }

    /// <summary>A group picked under "Move to group".</summary>
    private void MoveToGroupMenu_Click(object sender, RoutedEventArgs e)
    {
        if (e.OriginalSource is MenuItem { DataContext: GroupChoiceViewModel choice }) choice.Choose();
    }

    /// <summary>A project picked under "Copy categories from" (the click bubbles up from the generated sub-item).</summary>
    private void CopyCategoriesMenu_Click(object sender, RoutedEventArgs e)
    {
        if (e.OriginalSource is MenuItem { DataContext: TemplateSourceViewModel source }) source.Copy();
    }

    protected override void OnClosed(EventArgs e)
    {
        base.OnClosed(e);
        App.Current.OnMainWindowClosed();
    }
}
