using System.ComponentModel;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Threading;
using ProJict.App.ViewModels;
using ProJict.Core.Agents;

namespace ProJict.App.Views;

/// <summary>
/// The Agents view's office. Each robot is placed, scaled and mirrored here rather than by bindings, so that when it
/// moves (to the cooler to chat, back to its desk) it can walk there: the walk takes its steps frame by frame, growing
/// as it comes forward and turned the way it goes, then it turns to face where it arrived. The room zooms and pans.
/// </summary>
public partial class OfficeView : UserControl
{
    public OfficeView()
    {
        InitializeComponent();
        // Rebuilt (another view and back, or the theme changed), it shows the room as it was left.
        DataContextChanged += (_, e) =>
        {
            if (e.OldValue is OfficeViewModel old) old.PropertyChanged -= OnOfficeChanged;
            if (e.NewValue is OfficeViewModel office) office.PropertyChanged += OnOfficeChanged;
            ShowCamera();
        };
    }

    // ───────────── Focusing an agent ─────────────
    // A click on a robot focuses it (OfficeViewModel.Focus): the view flies in on it (and its activity screen), the rest
    // dimmed. A click on the room, a double-click or Fit lets go.

    private const double FocusZoom = 2.4;
    private static readonly TimeSpan FlightTime = TimeSpan.FromMilliseconds(380);
    private (double Zoom, double X, double Y) _flightFrom, _flightTo;
    private TimeSpan _flightStart;
    private bool _flying;

    private void OnOfficeChanged(object? sender, PropertyChangedEventArgs e)
    {
        if (e.PropertyName != nameof(OfficeViewModel.Focused) || DataContext is not OfficeViewModel { Focused: { } agent } office || Camera is not { } camera) return;
        // The robot's middle (at the cooler, with what it says over its head), or between it and its screen when that shows.
        var (x, y) = (agent.X, agent.Activity == OfficeActivity.Talking ? agent.Y - 28 * agent.Scale - agent.LabelSize().Height / 2 : agent.Y - 30 * agent.Scale);
        if (office.Screen.Shown) (x, y) = ((x + office.Screen.Middle.X) / 2, (y + office.Screen.Middle.Y) / 2);
        var target = new OfficeCamera(OfficeViewModel.Width, OfficeViewModel.Height);
        target.FocusOn(x, y, Math.Max(FocusZoom, camera.Zoom));
        FlyTo(target.Zoom, target.OffsetX, target.OffsetY);
    }

    private void FlyTo(double zoom, double x, double y)
    {
        if (Camera is not { } camera) return;
        if (!SystemParameters.ClientAreaAnimation)
        {
            camera.Set(zoom, x, y);
            ShowCamera();
            return;
        }
        _flightFrom = (camera.Zoom, camera.OffsetX, camera.OffsetY);
        _flightTo = (zoom, x, y);
        _flightStart = TimeSpan.MinValue;
        if (_flying) return;
        _flying = true;
        CompositionTarget.Rendering += OnFlightFrame;
    }

    private void OnFlightFrame(object? sender, EventArgs e)
    {
        var now = e is RenderingEventArgs r ? r.RenderingTime : TimeSpan.Zero;
        if (_flightStart == TimeSpan.MinValue) _flightStart = now;
        var t = Math.Clamp((now - _flightStart) / FlightTime, 0, 1);
        // Eased in and out.
        var k = t < 0.5 ? 2 * t * t : 1 - Math.Pow(-2 * t + 2, 2) / 2;
        Camera?.Set(_flightFrom.Zoom + (_flightTo.Zoom - _flightFrom.Zoom) * k, _flightFrom.X + (_flightTo.X - _flightFrom.X) * k,
            _flightFrom.Y + (_flightTo.Y - _flightFrom.Y) * k);
        ShowCamera();
        if (t < 1) return;
        _flying = false;
        CompositionTarget.Rendering -= OnFlightFrame;
    }

    private void StopFlight()
    {
        if (!_flying) return;
        _flying = false;
        CompositionTarget.Rendering -= OnFlightFrame;
    }

    /// <summary>Lets go of the focused agent (the camera stays where it is).</summary>
    private void Unfocus()
    {
        if (DataContext is OfficeViewModel { Focused: not null } office) office.Focus(null);
    }

    // ───────────── Zoom and pan ─────────────
    // In room units: the Viewbox scales the room to fit, and the world inside it is zoomed and panned by the camera.

    private OfficeCamera? Camera => (DataContext as OfficeViewModel)?.Camera;
    private Point? _panFrom;
    private MouseButton _panButton;

    private void Room_MouseWheel(object sender, MouseWheelEventArgs e)
    {
        if (Camera is not { } camera) return;
        StopFlight();
        var factor = Math.Pow(1.2, e.Delta / 120.0);
        // Already the whole room: the wheel scrolls the page around it.
        if (factor < 1 && !camera.IsZoomed) return;
        var at = e.GetPosition(Room);
        camera.ZoomAt(at.X, at.Y, factor);
        ShowCamera();
        e.Handled = true;
    }

    /// <summary>
    /// A robot takes its own left clicks (they show its card), so a left button here is on the room: drag to pan, or
    /// double-click for the whole room. The middle button pans anywhere.
    /// </summary>
    private void Room_MouseDown(object sender, MouseButtonEventArgs e)
    {
        if (e.ChangedButton is not (MouseButton.Left or MouseButton.Middle)) return;
        if (e.ChangedButton == MouseButton.Left && e.ClickCount == 2)
        {
            FitRoom();
            e.Handled = true;
            return;
        }
        // A click on the room lets go of a focused agent.
        if (e.ChangedButton == MouseButton.Left) Unfocus();
        StopFlight();
        if (Camera is not { IsZoomed: true }) return;
        _panFrom = e.GetPosition(Room);
        _panButton = e.ChangedButton;
        Room.CaptureMouse();
        Room.Cursor = Cursors.SizeAll;
        e.Handled = true;
    }

    private void Room_MouseMove(object sender, MouseEventArgs e)
    {
        if (_panFrom is not { } from || Camera is not { } camera) return;
        var at = e.GetPosition(Room);
        camera.PanBy(at.X - from.X, at.Y - from.Y);
        _panFrom = at;
        ShowCamera();
    }

    private void Room_MouseUp(object sender, MouseButtonEventArgs e)
    {
        if (_panFrom == null || e.ChangedButton != _panButton) return;
        Room.ReleaseMouseCapture();
        e.Handled = true;
    }

    private void Room_LostMouseCapture(object sender, MouseEventArgs e)
    {
        _panFrom = null;
        Room.ClearValue(CursorProperty);
    }

    private void Fit_Click(object sender, RoutedEventArgs e) => FitRoom();

    private void FitRoom()
    {
        StopFlight();
        Unfocus();
        Camera?.Reset();
        ShowCamera();
    }

    private void ShowCamera()
    {
        var camera = Camera ?? new OfficeCamera(OfficeViewModel.Width, OfficeViewModel.Height);
        ZoomScale.ScaleX = ZoomScale.ScaleY = camera.Zoom;
        PanMove.X = camera.OffsetX;
        PanMove.Y = camera.OffsetY;
        FitButton.Visibility = camera.IsZoomed ? Visibility.Visible : Visibility.Collapsed;
        FitText.Text = $"{camera.Zoom * 100:0}% · Fit";
    }

    // ───────────── Robots ─────────────

    private void Agent_Loaded(object sender, RoutedEventArgs e)
    {
        if (sender is not Canvas root || root.DataContext is not OfficeAgentViewModel agent) return;
        if (root.Tag is not Robot robot)
        {
            robot = new Robot(root, agent);
            root.Tag = robot;
        }
        robot.Attach();
    }

    private void Agent_Unloaded(object sender, RoutedEventArgs e) => ((sender as Canvas)?.Tag as Robot)?.Detach();

    /// <summary>A robot's Hire as… or Swap roles with: the line picked.</summary>
    private void StudioChoice_Click(object sender, RoutedEventArgs e)
    {
        if (e.OriginalSource is MenuItem { DataContext: StudioChoice choice }) choice.Command.Execute(null);
    }

    /// <summary>One robot on screen: where it's drawn now, and its walk to where its agent should be.</summary>
    private sealed class Robot
    {
        /// <summary>Room units per second.</summary>
        private const double Speed = 120;

        private readonly OfficeAgentViewModel _agent;
        private readonly Dispatcher _dispatcher;
        private readonly TranslateTransform _mover;
        private readonly ScaleTransform _body, _label, _initials, _zzz;
        private readonly TranslateTransform _hop;
        private bool _attached, _retargetQueued;
        private double _x, _y, _scale;
        // The walk under way.
        private double _fromX, _fromY, _fromScale;
        private TimeSpan _start, _duration;
        private bool _walking;

        public Robot(Canvas root, OfficeAgentViewModel agent)
        {
            _agent = agent;
            _dispatcher = root.Dispatcher;
            T Part<T>(string name) => (T)root.FindName(name);
            _mover = Part<TranslateTransform>("Mover");
            _body = Part<ScaleTransform>("BodyScale");
            _label = Part<ScaleTransform>("LabelScale");
            _initials = Part<ScaleTransform>("InitialsFlip");
            _zzz = Part<ScaleTransform>("ZzzFlip");
            _hop = Part<TranslateTransform>("Hop");
            Snap();
        }

        public void Attach()
        {
            if (_attached) return;
            _attached = true;
            _agent.PropertyChanged += OnChanged;
            _agent.Poked += OnPoked;
            Snap();
        }

        public void Detach()
        {
            if (!_attached) return;
            _attached = false;
            _agent.PropertyChanged -= OnChanged;
            _agent.Poked -= OnPoked;
            StopWalking();
            Snap();
        }

        private void OnChanged(object? sender, PropertyChangedEventArgs e)
        {
            if (e.PropertyName is not (nameof(OfficeAgentViewModel.X) or nameof(OfficeAgentViewModel.Y) or nameof(OfficeAgentViewModel.Scale)
                or nameof(OfficeAgentViewModel.FacingRight)) || _retargetQueued) return;
            // Place() sets these one after the other: look once it's done.
            _retargetQueued = true;
            _dispatcher.BeginInvoke(DispatcherPriority.Normal, Retarget);
        }

        private void Retarget()
        {
            _retargetQueued = false;
            if (!_attached) return;
            var distance = Math.Sqrt(Math.Pow(_agent.X - _x, 2) + Math.Pow(_agent.Y - _y, 2));
            // Too close to bother, or animations are off in Windows: it's just there.
            if (distance < 2 || !SystemParameters.ClientAreaAnimation)
            {
                StopWalking();
                Snap();
                return;
            }
            (_fromX, _fromY, _fromScale) = (_x, _y, _scale);
            _duration = TimeSpan.FromSeconds(Math.Clamp(distance / Speed, 0.6, 4));
            _start = TimeSpan.MinValue;
            if (!_walking)
            {
                _walking = true;
                _agent.Walking = true;
                CompositionTarget.Rendering += OnFrame;
            }
        }

        private void OnFrame(object? sender, EventArgs e)
        {
            var now = e is RenderingEventArgs r ? r.RenderingTime : TimeSpan.Zero;
            if (_start == TimeSpan.MinValue) _start = now;
            var t = _duration <= TimeSpan.Zero ? 1 : Math.Clamp((now - _start) / _duration, 0, 1);
            if (t >= 1)
            {
                StopWalking();
                Snap();
                return;
            }
            var x = _fromX + (_agent.X - _fromX) * t;
            var y = _fromY + (_agent.Y - _fromY) * t;
            var scale = _fromScale + (_agent.Scale - _fromScale) * t;
            // Turned the way it goes (unless it goes nearly straight back or forward).
            var facing = Math.Abs(_agent.X - _fromX) > 8 ? _agent.X > _fromX : _agent.FacingRight;
            Apply(x, y, scale, facing);
        }

        private void StopWalking()
        {
            if (!_walking) return;
            _walking = false;
            _agent.Walking = false;
            CompositionTarget.Rendering -= OnFrame;
        }

        /// <summary>Poked: a hop when it giggles, a quick shake when it's cross (nothing when Windows' animations are off).</summary>
        private void OnPoked(bool happy)
        {
            if (!SystemParameters.ClientAreaAnimation) return;
            var ease = new System.Windows.Media.Animation.QuadraticEase { EasingMode = System.Windows.Media.Animation.EasingMode.EaseOut };
            if (happy)
                _hop.BeginAnimation(TranslateTransform.YProperty, new System.Windows.Media.Animation.DoubleAnimation(0, -14, TimeSpan.FromMilliseconds(160))
                    { AutoReverse = true, EasingFunction = ease, RepeatBehavior = new System.Windows.Media.Animation.RepeatBehavior(2) });
            else
                _hop.BeginAnimation(TranslateTransform.XProperty, new System.Windows.Media.Animation.DoubleAnimation(-3, 3, TimeSpan.FromMilliseconds(45))
                    { AutoReverse = true, RepeatBehavior = new System.Windows.Media.Animation.RepeatBehavior(4), FillBehavior = System.Windows.Media.Animation.FillBehavior.Stop });
        }

        private void Snap() => Apply(_agent.X, _agent.Y, _agent.Scale, _agent.FacingRight);

        private void Apply(double x, double y, double scale, bool facingRight)
        {
            (_x, _y, _scale) = (x, y, scale);
            var mirror = facingRight ? 1 : -1;
            _mover.X = x;
            _mover.Y = y;
            _body.ScaleX = mirror * scale;
            _body.ScaleY = scale;
            // Its name, bubble, initials and Zzz read the right way round, at full size.
            var inverse = scale <= 0 ? 1 : 1 / scale;
            _label.ScaleX = mirror * inverse;
            _label.ScaleY = inverse;
            _initials.ScaleX = mirror;
            _zzz.ScaleX = mirror;
        }
    }
}
