using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;

namespace ProJict.App.Services;

/// <summary>
/// The size of everything in ProJict (#b242e155): Ctrl+mouse wheel (or Ctrl+plus and Ctrl+minus, Ctrl+0 back to 100%)
/// makes text, icons and spacing bigger or smaller in every window together, like zooming a web page, for big or small
/// screens. It's kept on this computer. Where the wheel already zooms something of its own (a picture in the viewer),
/// that keeps it.
/// </summary>
public static class UiScale
{
    public static readonly double[] Steps = [0.75, 0.8, 0.9, 1.0, 1.1, 1.25, 1.5, 1.75, 2.0];

    private static AppState? _state;

    /// <summary>The modifier keys held (tests say which).</summary>
    public static Func<ModifierKeys> Modifiers { get; set; } = () => Keyboard.Modifiers;

    /// <summary>The size now (1 is 100%).</summary>
    public static double Current => _state?.AppSettings.UiScale is { } scale && scale > 0 ? scale : 1;

    /// <summary>Raised when the size changed, with the new one.</summary>
    public static event Action<double>? Changed;

    /// <summary>Once: every window ProJict opens gets the size, and Ctrl+wheel and Ctrl+plus/minus/0 change it.</summary>
    public static void Install(AppState state)
    {
        if (_state != null) return;
        _state = state;
        EventManager.RegisterClassHandler(typeof(Window), FrameworkElement.LoadedEvent, new RoutedEventHandler((sender, _) => Apply((Window)sender)));
        // After the window's own controls: what handles the wheel itself (a picture's zoom, a scroller) goes first.
        EventManager.RegisterClassHandler(typeof(Window), UIElement.MouseWheelEvent, new MouseWheelEventHandler(OnWheel));
        EventManager.RegisterClassHandler(typeof(Window), UIElement.KeyDownEvent, new KeyEventHandler(OnKey));
        // Menus and tooltips open in windows of their own: they get the size too.
        EventManager.RegisterClassHandler(typeof(ContextMenu), ContextMenu.OpenedEvent, new RoutedEventHandler((sender, _) => Scale((FrameworkElement)sender)));
        EventManager.RegisterClassHandler(typeof(ToolTip), ToolTip.OpenedEvent, new RoutedEventHandler((sender, _) => Scale((FrameworkElement)sender)));
    }

    /// <summary>The next step up (+1) or down (-1) from the size now, within 75% and 200%.</summary>
    public static double Next(double now, int direction)
    {
        var at = Array.FindIndex(Steps, s => s >= now - 0.001);
        if (at < 0) at = Steps.Length - 1;
        if (direction < 0 && Steps[at] > now + 0.001) at--;
        return Steps[Math.Clamp(at + Math.Sign(direction), 0, Steps.Length - 1)];
    }

    /// <summary>Sets the size of every window, and keeps it.</summary>
    public static void Set(double scale)
    {
        if (_state == null) return;
        scale = Math.Clamp(scale, Steps[0], Steps[^1]);
        if (Math.Abs(scale - Current) < 0.001) return;
        _state.AppSettings.UiScale = Math.Abs(scale - 1) < 0.001 ? 0 : scale;
        _state.SaveAppSettings();
        if (Application.Current != null)
            foreach (Window window in Application.Current.Windows) Apply(window);
        _state.StatusText = $"Size: {scale:P0} (Ctrl+mouse wheel or Ctrl+plus and minus; Ctrl+0 for 100%).";
        Changed?.Invoke(scale);
    }

    private static void Apply(Window window)
    {
        if (window.Content is FrameworkElement root) Scale(root);
    }

    private static void Scale(FrameworkElement element)
    {
        var scale = Current;
        element.LayoutTransform = Math.Abs(scale - 1) < 0.001 ? Transform.Identity : new ScaleTransform(scale, scale);
    }

    private static void OnWheel(object sender, MouseWheelEventArgs e)
    {
        if (e.Handled || Modifiers() != ModifierKeys.Control) return;
        Set(Next(Current, e.Delta > 0 ? 1 : -1));
        e.Handled = true;
    }

    private static void OnKey(object sender, KeyEventArgs e)
    {
        if (e.Handled || Modifiers() != ModifierKeys.Control) return;
        switch (e.Key)
        {
            case Key.OemPlus or Key.Add:
                Set(Next(Current, 1));
                break;
            case Key.OemMinus or Key.Subtract:
                Set(Next(Current, -1));
                break;
            case Key.D0 or Key.NumPad0:
                Set(1);
                break;
            default:
                return;
        }
        e.Handled = true;
    }
}
