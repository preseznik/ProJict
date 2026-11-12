using System.Windows;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using CommunityToolkit.Mvvm.Input;
using ProJict.App.ViewModels;

namespace ProJict.App.Views;

/// <summary>
/// Reserve ▾ on a shared thing (#6f04e17e, #68c97287): free, it's yours now for how long you say; held, you take it now or
/// queue for it (yours the moment it's freed); and the calendar of who has it when.
/// </summary>
public static class ReserveMenu
{
    private static readonly (string Header, int? Minutes)[] Choices =
        [("For 30 minutes", 30), ("For 1 hour", 60), ("For 2 hours", 120), ("For 4 hours", 240), ("Until I free it", null)];

    /// <summary>Opens the menu under <paramref name="sender"/>, the button on the shared thing.</summary>
    public static void Open(object sender)
    {
        if (sender is not FrameworkElement { DataContext: ResourceBarItem item } button) return;
        var menu = new ContextMenu { PlacementTarget = button, Placement = PlacementMode.Bottom };
        if (!item.IsHeld)
            foreach (var (header, minutes) in Choices)
                menu.Items.Add(new MenuItem { Header = header, Command = new RelayCommand(() => item.Reserve(minutes)) });
        else
        {
            var now = new MenuItem { Header = "Take it now" };
            now.Items.Add(new MenuItem { Header = "The agent using it is told to stop, and waits for it", IsEnabled = false });
            foreach (var (header, minutes) in Choices)
                now.Items.Add(new MenuItem { Header = header, Command = new RelayCommand(() => item.Reserve(minutes)) });
            menu.Items.Add(now);
            var queue = new MenuItem { Header = item.QueuedId != null ? "Queue for it (you're next)" : "Queue for it" };
            queue.Items.Add(new MenuItem { Header = "Yours the moment it's freed, ahead of agents waiting", IsEnabled = false });
            foreach (var (header, minutes) in Choices)
                queue.Items.Add(new MenuItem { Header = header, Command = new RelayCommand(() => item.Queue(minutes)) });
            menu.Items.Add(queue);
            if (item.QueuedId != null) menu.Items.Add(new MenuItem { Header = "Leave the queue", Command = new RelayCommand(item.LeaveQueue) });
        }
        menu.Items.Add(new Separator());
        menu.Items.Add(new MenuItem { Header = "Calendar…", Command = new RelayCommand(item.ShowCalendar) });
        menu.IsOpen = true;
    }
}
