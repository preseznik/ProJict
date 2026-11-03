using System.Windows;
using System.Windows.Interop;
using ProJict.App.Services;

namespace ProJict.App.Views;

/// <summary>
/// Watch (the browser bar): the browser an agent is using for a tool, moved into this window while you watch, so you
/// see what it does and can click or type yourself. Closing it moves the browser back where nobody sees it; the agent
/// carries on.
/// </summary>
public partial class BrowserWatchWindow : Window
{
    private readonly BrowserSession _session;

    public BrowserWatchWindow(BrowserSession session)
    {
        InitializeComponent();
        _session = session;
        session.Watching = this;
        Title = $"{session.Tool.Name} in ProJict's browser";
        Headline.Text = $"{session.Agent} is using {session.Tool.Name}. You can click and type here too; closing this window leaves it working.";
        SourceInitialized += (_, _) => Show(true);
        SizeChanged += (_, _) => Show(true);
        Closed += (_, _) =>
        {
            Show(false);
            if (_session.Watching == this) _session.Watching = null;
        };
    }

    /// <summary>Puts the browser over this window's space (in device pixels), or back in its hidden window.</summary>
    private void Show(bool here)
    {
        if (_session.Controller is not { } controller) return;
        if (!here)
        {
            if (_session.HiddenWindow != IntPtr.Zero) controller.ParentWindow = _session.HiddenWindow;
            controller.Bounds = new System.Drawing.Rectangle(0, 0, 1400, 900);
            return;
        }
        var handle = new WindowInteropHelper(this).Handle;
        if (handle == IntPtr.Zero || !Space.IsLoaded && Space.ActualWidth == 0) return;
        controller.ParentWindow = handle;
        var scale = PresentationSource.FromVisual(this)?.CompositionTarget?.TransformToDevice.M11 ?? 1;
        var top = Space.TranslatePoint(new Point(0, 0), this);
        controller.Bounds = new System.Drawing.Rectangle((int)(top.X * scale), (int)(top.Y * scale),
            (int)Math.Max(1, Space.ActualWidth * scale), (int)Math.Max(1, Space.ActualHeight * scale));
    }

    /// <summary>Stops the agent's use of the browser (which closes this window too).</summary>
    private void Stop_Click(object sender, RoutedEventArgs e)
    {
        if (Owner?.DataContext is ViewModels.MainViewModel main)
        {
            main.Browsers.Stop(_session);
            main.State.StatusText = $"Stopped {_session.Agent}'s use of {_session.Tool.Name}'s browser; it's told on its next try.";
        }
        else Close();
    }
}
