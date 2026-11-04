using System.ComponentModel;
using System.Windows;
using System.Windows.Threading;
using ProJict.App.ViewModels;

namespace ProJict.App.Views;

/// <summary>A voice call with agents: its tiles, captions, Mute and Hang up. Closing it hangs up; it closes itself when the call is over.</summary>
public partial class CallWindow : Window
{
    /// <summary>Whether a new call window comes to the front (off-screen tests turn it off).</summary>
    public static bool Activates { get; set; } = true;

    private readonly CallViewModel _vm;

    public CallWindow(CallViewModel vm)
    {
        InitializeComponent();
        _vm = vm;
        DataContext = vm;
        ShowActivated = Activates;
        // Over: a moment to read "Call ended", then it closes.
        vm.Over += () =>
        {
            var timer = new DispatcherTimer { Interval = TimeSpan.FromSeconds(2) };
            timer.Tick += (_, _) =>
            {
                timer.Stop();
                if (IsLoaded) Close();
            };
            timer.Start();
        };
    }

    protected override void OnClosing(CancelEventArgs e)
    {
        _vm.Closed();
        base.OnClosing(e);
    }
}
