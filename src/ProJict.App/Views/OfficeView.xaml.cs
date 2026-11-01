using System.ComponentModel;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using System.Windows.Threading;
using ProJict.App.ViewModels;

namespace ProJict.App.Views;

/// <summary>
/// The Agents view's office. Each robot is placed, scaled and mirrored here rather than by bindings, so that when it
/// moves (to the cooler to chat, back to its desk) it can walk there: the walk takes its steps frame by frame, growing
/// as it comes forward and turned the way it goes, then it turns to face where it arrived.
/// </summary>
public partial class OfficeView : UserControl
{
    public OfficeView() => InitializeComponent();

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

    /// <summary>One robot on screen: where it's drawn now, and its walk to where its agent should be.</summary>
    private sealed class Robot
    {
        /// <summary>Room units per second.</summary>
        private const double Speed = 120;

        private readonly OfficeAgentViewModel _agent;
        private readonly Dispatcher _dispatcher;
        private readonly TranslateTransform _mover;
        private readonly ScaleTransform _body, _label, _initials, _zzz;
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
            Snap();
        }

        public void Attach()
        {
            if (_attached) return;
            _attached = true;
            _agent.PropertyChanged += OnChanged;
            Snap();
        }

        public void Detach()
        {
            if (!_attached) return;
            _attached = false;
            _agent.PropertyChanged -= OnChanged;
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
