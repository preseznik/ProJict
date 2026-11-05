using System.ComponentModel;
using System.Windows;
using System.Windows.Controls;
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

    /// <summary>An agent's tile, right-clicked: its voice (the ones for the call's language), the one picked ticked.</summary>
    private async void Tile_ContextMenuOpening(object sender, ContextMenuEventArgs e)
    {
        if (sender is not FrameworkElement { DataContext: CallTile tile, ContextMenu: { } menu } || tile.IsYou)
        {
            e.Handled = true;
            return;
        }
        menu.Items.Clear();
        menu.Items.Add(new MenuItem { Header = $"Voice for {tile.Name}", IsEnabled = false });
        var loading = new MenuItem { Header = "Finding voices…", IsEnabled = false };
        menu.Items.Add(loading);
        var voices = await _vm.VoicesAsync();
        menu.Items.Remove(loading);
        var picked = _vm.VoicePickedFor(tile.Name);
        MenuItem Choice(string header, string? voice)
        {
            var item = new MenuItem { Header = header, IsCheckable = true, IsChecked = string.Equals(voice, picked, StringComparison.OrdinalIgnoreCase) };
            item.Click += (_, _) => _vm.PickVoice(tile.Name, voice);
            return item;
        }
        menu.Items.Add(Choice("Its own (by its name)", null));
        menu.Items.Add(new Separator());
        if (voices.Count == 0) menu.Items.Add(new MenuItem { Header = "No voices to pick from", IsEnabled = false });
        foreach (var voice in voices) menu.Items.Add(Choice(voice, voice));
    }

    protected override void OnClosing(CancelEventArgs e)
    {
        _vm.Closed();
        base.OnClosing(e);
    }
}
