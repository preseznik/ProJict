using System.Windows;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using CommunityToolkit.Mvvm.Input;
using ProJict.App.ViewModels;

namespace ProJict.App.Views;

/// <summary>Reserve ▾ on a shared thing (#6f04e17e): for how long it's yours, or until you free it.</summary>
public static class ReserveMenu
{
    private static readonly (string Header, int? Minutes)[] Choices =
        [("For 30 minutes", 30), ("For 1 hour", 60), ("For 2 hours", 120), ("For 4 hours", 240), ("Until I free it", null)];

    /// <summary>Opens the menu under <paramref name="button"/> for the shared thing it's on.</summary>
    public static void Open(object sender)
    {
        if (sender is not FrameworkElement { DataContext: ResourceBarItem item } button) return;
        var menu = new ContextMenu { PlacementTarget = button, Placement = PlacementMode.Bottom };
        if (item.IsHeld)
            menu.Items.Add(new MenuItem { Header = "The agent using it now is told to stop, and waits for it", IsEnabled = false });
        foreach (var (header, minutes) in Choices)
            menu.Items.Add(new MenuItem { Header = header, Command = new RelayCommand(() => item.Reserve(minutes)) });
        menu.IsOpen = true;
    }
}
