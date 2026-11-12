using System.Globalization;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Shapes;
using System.Windows.Threading;
using ProJict.App.ViewModels;
using ProJict.Core.Agents;

namespace ProJict.App.Views;

/// <summary>What the calendar shows and does (#68c97287): the shared machines, and what you do with them.</summary>
public interface IResourceCalendarHost
{
    IReadOnlyList<SharedResource> Machines { get; }
    /// <summary>Your name, to tell your stretches from agents'.</summary>
    string Me { get; }
    /// <summary>What was last done, for the line at the bottom.</summary>
    string Status { get; }
    /// <summary>Something changed: a claim, a release, a booking.</summary>
    event Action? Changed;
    /// <summary>Each returns why it couldn't be done, or null.</summary>
    string? Reserve(string machine, int? minutes);
    string? Queue(string machine, int? minutes);
    string? Book(string machine, DateTime from, DateTime to, string? purpose);
    /// <summary>A booked slot moved: new times and note, on the same machine or another (#d27b8c7b).</summary>
    string? Rebook(string machine, Guid booking, string newMachine, DateTime from, DateTime to, string? purpose);
    /// <summary>When what you hold now ends: a time, or until you free it (null) (#d27b8c7b).</summary>
    string? SetHoldEnd(string machine, DateTime? until, string? purpose);
    void Cancel(string machine, Guid booking);
    void Free(string machine);
}

/// <summary>
/// The shared machines' calendar (#68c97287), Outlook/Teams style: a day or a week, the hours down the side, a column per
/// day (split into a lane per machine when all show). Who held each (the past), who holds it now, your place in the queue
/// and your booked slots. Drag over free time to book it; click your own booking to cancel it, or what you hold to free it.
/// The day view runs on into the next day, so late evenings aren't cut off at midnight; right-click makes or edits a
/// reservation in a dialog, Outlook-style (#d27b8c7b).
/// </summary>
public partial class ResourceCalendarWindow : Window
{
    private const double HourHeight = 44, RulerWidth = 58, HeaderDayHeight = 24;
    private const int SnapMinutes = 15;
    private const string AllMachines = "All machines";

    private readonly IResourceCalendarHost _host;
    private readonly DispatcherTimer _clock;
    private DateTime _day = DateTime.Today;
    private bool _week = true;
    private string? _machine;
    private bool _filling;
    // Booking by dragging: where it started, in which lane, and the band drawn meanwhile.
    private (int Column, DateTime Start)? _drag;
    private Rectangle? _band;

    public ResourceCalendarWindow(IResourceCalendarHost host, string? machine)
    {
        InitializeComponent();
        _host = host;
        _machine = machine;
        _host.Changed += Render;
        _clock = new DispatcherTimer { Interval = TimeSpan.FromMinutes(1) };
        _clock.Tick += (_, _) => Render();
        _clock.Start();
        Closed += (_, _) =>
        {
            _clock.Stop();
            _host.Changed -= Render;
        };
        BodyScroll.SizeChanged += (_, _) => Render();
        KeyDown += (_, e) =>
        {
            if (e.Key == Key.Escape && _drag != null) EndDrag();
        };
        BuildLegend();
        Loaded += (_, _) =>
        {
            Render();
            ScrollToNow();
        };
    }

    /// <summary>Shows one machine (or all, with null).</summary>
    public void ShowMachine(string? machine)
    {
        _machine = machine;
        Render();
    }

    private List<SharedResource> Machines =>
        [.. _host.Machines.OrderBy(r => r.Name, StringComparer.CurrentCultureIgnoreCase)];

    private DateTime FirstDay
    {
        get
        {
            if (!_week) return _day.Date;
            var first = CultureInfo.CurrentCulture.DateTimeFormat.FirstDayOfWeek;
            var back = ((int)_day.DayOfWeek - (int)first + 7) % 7;
            return _day.Date.AddDays(-back);
        }
    }

    private int Days => _week ? 7 : 1;

    /// <summary>A column's hours: a day in the week view; in the day view the day and the next, so a late evening runs on past midnight.</summary>
    private int HoursShown => _week ? 24 : 48;

    /// <summary>The day a column starts on.</summary>
    private DateTime ColumnDay(int column, int lanes) => FirstDay.AddDays(column / Math.Max(1, lanes));

    private List<string> Lanes(List<SharedResource> machines) =>
        _machine != null && machines.Any(m => string.Equals(m.Name, _machine, StringComparison.OrdinalIgnoreCase))
            ? [machines.First(m => string.Equals(m.Name, _machine, StringComparison.OrdinalIgnoreCase)).Name]
            : [.. machines.Select(m => m.Name)];

    private double ColumnWidth(int columns) => Math.Max(1, (Body.Width - RulerWidth) / Math.Max(1, columns));

    // ───────────── Drawing ─────────────

    private void Render()
    {
        if (!IsLoaded) return;
        var machines = Machines;
        FillMachineBox(machines);
        DayButton.IsChecked = !_week;
        WeekButton.IsChecked = _week;
        var first = FirstDay;
        var culture = CultureInfo.CurrentCulture;
        RangeText.Text = _week
            ? $"{first.ToString("d MMM", culture)} – {first.AddDays(6).ToString("d MMM yyyy", culture)}"
            : first.ToString("dddd, d MMMM yyyy", culture);

        var lanes = Lanes(machines);
        var laneCount = Math.Max(1, lanes.Count);
        var columns = Days * laneCount;
        Body.Width = Math.Max(RulerWidth + 60, BodyScroll.ViewportWidth > 0 ? BodyScroll.ViewportWidth : ActualWidth - 50);
        Body.Height = HoursShown * HourHeight;
        Header.Width = Body.Width;
        Body.Children.Clear();
        Header.Children.Clear();
        var col = ColumnWidth(columns);
        var dayWidth = col * laneCount;
        var now = DateTime.Now;

        // Days: weekends and today tinted, the day's name above, a stronger line between days.
        for (var d = 0; d < Days; d++)
        {
            var date = first.AddDays(d);
            var x = RulerWidth + d * dayWidth;
            for (var k = 0; k < HoursShown / 24; k++)
            {
                var shade = date.AddDays(k);
                if (shade == DateTime.Today) Body.Children.Add(Box(x, k * 24 * HourHeight, dayWidth, 24 * HourHeight, "SystemFillColorAttentionBackgroundBrush", null, 0));
                else if (shade.DayOfWeek is DayOfWeek.Saturday or DayOfWeek.Sunday)
                    Body.Children.Add(Box(x, k * 24 * HourHeight, dayWidth, 24 * HourHeight, "SubtleFillColorTertiaryBrush", null, 0));
            }
            var label = new TextBlock
            {
                Text = date.ToString(_week ? "ddd d" : "dddd d", culture),
                FontWeight = date == DateTime.Today ? FontWeights.SemiBold : FontWeights.Normal,
                Width = dayWidth,
                TextAlignment = TextAlignment.Center,
                TextTrimming = TextTrimming.CharacterEllipsis,
            };
            if (date == DateTime.Today) label.SetResourceReference(TextBlock.ForegroundProperty, "AccentTextFillColorPrimaryBrush");
            Place(Header, label, x, 2);
            if (laneCount > 1)
                for (var l = 0; l < laneCount; l++)
                {
                    var lane = new TextBlock
                    {
                        Text = lanes[l], FontSize = 11, Width = col - 4, TextAlignment = TextAlignment.Center, TextTrimming = TextTrimming.CharacterEllipsis,
                        ToolTip = lanes[l],
                    };
                    lane.SetResourceReference(TextBlock.ForegroundProperty, "TextFillColorSecondaryBrush");
                    Place(Header, lane, x + l * col + 2, HeaderDayHeight);
                    if (l > 0) Body.Children.Add(Line(x + l * col, 0, x + l * col, Body.Height, "DividerStrokeColorDefaultBrush", 0.5));
                }
            Body.Children.Add(Line(x, 0, x, Body.Height, "ControlStrokeColorDefaultBrush", 1));
        }
        Header.Height = laneCount > 1 ? HeaderDayHeight + 18 : HeaderDayHeight + 4;

        // Hours: a line and a label each, a fainter line at the half hour; at midnight in the day view, the next day's
        // name and a stronger line.
        for (var h = 0; h < HoursShown; h++)
        {
            var y = h * HourHeight;
            var midnight = h > 0 && h % 24 == 0;
            Body.Children.Add(Line(midnight ? 0 : RulerWidth, y, Body.Width, y, midnight ? "ControlStrongFillColorDefaultBrush" : "DividerStrokeColorDefaultBrush", midnight ? 1.5 : 1));
            Body.Children.Add(Line(RulerWidth, y + HourHeight / 2, Body.Width, y + HourHeight / 2, "DividerStrokeColorDefaultBrush", 0.4));
            var next = first.AddHours(h);
            var hour = new TextBlock
            {
                Text = midnight ? next.ToString("ddd d", culture) : next.ToString("t", culture), FontSize = 11, Width = RulerWidth - 8,
                TextAlignment = TextAlignment.Right, FontWeight = midnight ? FontWeights.SemiBold : FontWeights.Normal,
            };
            hour.SetResourceReference(TextBlock.ForegroundProperty, midnight
                ? next == DateTime.Today ? "AccentTextFillColorPrimaryBrush" : "TextFillColorPrimaryBrush"
                : "TextFillColorTertiaryBrush");
            Place(Body, hour, 0, midnight ? y + 2 : y - 7 < 0 ? 0 : y - 7);
        }

        if (machines.Count == 0)
        {
            var none = new TextBlock
            {
                Text = "No shared machines yet. Agents name one when they first claim it (claim_resource), or reserve one under Agents › Shared machines.",
                TextWrapping = TextWrapping.Wrap, Width = Math.Max(100, Body.Width - RulerWidth - 40),
            };
            none.SetResourceReference(TextBlock.ForegroundProperty, "TextFillColorSecondaryBrush");
            Place(Body, none, RulerWidth + 20, 8 * HourHeight);
            return;
        }

        // The stretches, split at midnight, side by side where they overlap in a lane.
        var spans = ResourceLocks.Calendar(machines, first, first.AddDays(Days - 1).AddHours(HoursShown), now)
            .Where(s => lanes.Contains(s.Resource, StringComparer.OrdinalIgnoreCase)).ToList();
        var pieces = new List<(CalendarSpan Span, int Column, DateTime From, DateTime To)>();
        foreach (var s in spans)
            for (var d = 0; d < Days; d++)
            {
                var dayStart = first.AddDays(d);
                var dayEnd = dayStart.AddHours(HoursShown);
                var from = s.From > dayStart ? s.From : dayStart;
                var to = s.To < dayEnd ? s.To : dayEnd;
                if (to <= from) continue;
                var lane = lanes.FindIndex(l => string.Equals(l, s.Resource, StringComparison.OrdinalIgnoreCase));
                pieces.Add((s, d * laneCount + lane, from, to));
            }
        foreach (var group in pieces.GroupBy(p => p.Column))
        {
            // Greedy columns within the lane, like Outlook's side-by-side.
            var ends = new List<DateTime>();
            var placed = new List<(int Sub, (CalendarSpan Span, int Column, DateTime From, DateTime To) Piece)>();
            foreach (var p in group.OrderBy(p => p.From))
            {
                var sub = ends.FindIndex(e => e <= p.From);
                if (sub < 0) { sub = ends.Count; ends.Add(p.To); }
                else ends[sub] = p.To;
                placed.Add((sub, p));
            }
            var subs = Math.Max(1, ends.Count);
            foreach (var (sub, p) in placed)
            {
                var x = RulerWidth + p.Column * col + 2 + sub * (col - 4) / subs;
                var y = (p.From - ColumnDay(p.Column, laneCount)).TotalHours * HourHeight;
                var height = Math.Max(18, (p.To - p.From).TotalHours * HourHeight - 1);
                Body.Children.Add(Block(p.Span, x, y, (col - 4) / subs - 1, height, now));
            }
        }

        // Now: a red line across the column it's in.
        for (var d = 0; d < Days; d++)
        {
            var dayStart = first.AddDays(d);
            if (now < dayStart || now >= dayStart.AddHours(HoursShown)) continue;
            var y = (now - dayStart).TotalHours * HourHeight;
            var x = RulerWidth + d * dayWidth;
            // Over the stretches, as in Outlook.
            var line = Line(x, y, x + dayWidth, y, "SystemFillColorCriticalBrush", 2);
            Panel.SetZIndex(line, 4);
            Body.Children.Add(line);
            var dot = new Ellipse { Width = 9, Height = 9, IsHitTestVisible = false };
            dot.SetResourceReference(Shape.FillProperty, "SystemFillColorCriticalBrush");
            Panel.SetZIndex(dot, 4);
            Place(Body, dot, x - 4.5, y - 4.5);
        }
        // A booking being dragged out stays through a redraw.
        if (_band != null) Body.Children.Add(_band);
    }

    /// <summary>One stretch: held before (faint), held now (yours in the accent colour), your booking or place in the queue (outlined).</summary>
    private FrameworkElement Block(CalendarSpan s, double x, double y, double width, double height, DateTime now)
    {
        var me = _host.Me;
        var yours = s.Person && string.Equals(s.By, me, StringComparison.OrdinalIgnoreCase);
        var (fill, stroke, text, dashed) = s.Kind switch
        {
            SpanKind.Past => ("SubtleFillColorSecondaryBrush", "DividerStrokeColorDefaultBrush", "TextFillColorSecondaryBrush", false),
            SpanKind.Now when yours => ("AccentFillColorDefaultBrush", "AccentFillColorDefaultBrush", "TextOnAccentFillColorPrimaryBrush", s.OpenEnded),
            SpanKind.Now => ("ControlFillColorDefaultBrush", now > s.To && !s.OpenEnded ? "SystemFillColorCautionBrush" : "ControlStrongFillColorDefaultBrush",
                "TextFillColorPrimaryBrush", s.OpenEnded),
            SpanKind.Booked => ("SystemFillColorAttentionBackgroundBrush", "AccentFillColorDefaultBrush", "TextFillColorPrimaryBrush", false),
            _ => ("SubtleFillColorTertiaryBrush", "AccentFillColorDefaultBrush", "TextFillColorPrimaryBrush", true),
        };
        var shape = new Rectangle { RadiusX = 4, RadiusY = 4, StrokeThickness = s.Kind is SpanKind.Booked or SpanKind.Queued ? 1.5 : 1 };
        shape.SetResourceReference(Shape.FillProperty, fill);
        shape.SetResourceReference(Shape.StrokeProperty, stroke);
        if (dashed) shape.StrokeDashArray = [4, 3];
        if (s.Kind == SpanKind.Past) shape.Opacity = 0.9;

        var culture = CultureInfo.CurrentCulture;
        string Hm(DateTime t) => t.ToString("t", culture);
        var who = yours ? "You" : s.By;
        var when = s.Kind switch
        {
            SpanKind.Queued => s.OpenEnded ? "queued, then until you free it" : $"queued, then {(s.To - s.From).TotalMinutes:0} min",
            _ when s.OpenEnded => $"{Hm(s.From)} – until freed",
            _ => $"{Hm(s.From)} – {Hm(s.To)}",
        };
        var label = new TextBlock
        {
            Margin = new Thickness(6, 3, 4, 2),
            // Long words clip at the edge rather than break in the middle in a narrow lane.
            TextWrapping = TextWrapping.WrapWithOverflow,
            TextTrimming = TextTrimming.CharacterEllipsis,
            FontSize = 11.5,
        };
        label.Inlines.Add(new System.Windows.Documents.Run(who) { FontWeight = FontWeights.SemiBold });
        if (s.For is { } why) label.Inlines.Add(new System.Windows.Documents.Run(" · " + why));
        label.Inlines.Add(new System.Windows.Documents.LineBreak());
        label.Inlines.Add(new System.Windows.Documents.Run(when) { FontSize = 10.5 });
        label.SetResourceReference(TextBlock.ForegroundProperty, text);

        var kind = s.Kind switch
        {
            SpanKind.Past => "Held",
            SpanKind.Now => yours ? "Yours now" : "Holds it now",
            SpanKind.Booked => "Booked",
            _ => "In the queue (yours as soon as it's free)",
        };
        var block = new Grid
        {
            Width = Math.Max(4, width), Height = height, ClipToBounds = true, Cursor = Cursors.Arrow, Tag = s,
            ToolTip = $"{s.Resource}\n{kind}: {(yours ? "you" : s.By)}{(s.For is { } f ? " — " + f : "")}\n" +
                      (s.Kind == SpanKind.Queued ? when : $"{s.From.ToString("ddd d MMM t", culture)} – {(s.OpenEnded ? "until freed" : s.To.ToString("ddd d MMM t", culture))}"),
        };
        block.Children.Add(shape);
        block.Children.Add(label);
        block.MouseLeftButtonDown += (_, e) =>
        {
            e.Handled = true;
            // A double-click edits your reservation, as in Outlook.
            if (e.ClickCount == 2 && yours && s.Kind is SpanKind.Booked or SpanKind.Now) Edit(s);
        };
        block.MouseLeftButtonUp += (_, e) =>
        {
            e.Handled = true;
            BlockMenu(s, yours, block);
        };
        block.MouseRightButtonUp += (_, e) =>
        {
            e.Handled = true;
            BlockMenu(s, yours, block);
        };
        Canvas.SetLeft(block, x);
        Canvas.SetTop(block, y);
        Panel.SetZIndex(block, 2);
        return block;
    }

    /// <summary>What you can do with a stretch: cancel your booking, leave the queue, free what you hold, or take or queue for an agent's.</summary>
    private void BlockMenu(CalendarSpan s, bool yours, FrameworkElement target)
    {
        var menu = new ContextMenu { PlacementTarget = target, Placement = PlacementMode.MousePoint };
        void Add(string header, Action act) => menu.Items.Add(new MenuItem { Header = header, Command = new CommunityToolkit.Mvvm.Input.RelayCommand(act) });
        switch (s.Kind)
        {
            case SpanKind.Booked when s.Booking is { } id:
                Add("Edit reservation…", () => Edit(s));
                Add("Cancel this booking", () => Tell(Do(() => _host.Cancel(s.Resource, id))));
                break;
            case SpanKind.Queued when s.Booking is { } id:
                Add("Leave the queue", () => Tell(Do(() => _host.Cancel(s.Resource, id))));
                break;
            case SpanKind.Now when yours:
                Add("Edit reservation…", () => Edit(s));
                Add("Free it", () => Tell(Do(() => _host.Free(s.Resource))));
                break;
            case SpanKind.Now:
                Add("Take it now (until I free it)", () => Tell(_host.Reserve(s.Resource, null)));
                Add("Queue for it (mine as soon as it's free)", () => Tell(_host.Queue(s.Resource, null)));
                if (!s.OpenEnded) Add("New reservation after it…", () => NewReservation(s.Resource, s.To, s.To.AddHours(1)));
                break;
            default:
                var start = NextQuarter(DateTime.Now);
                Add("New reservation…", () => NewReservation(s.Resource, start, start.AddHours(1)));
                break;
        }
        menu.IsOpen = true;
    }

    // ───────────── Booking by dragging ─────────────

    private (int Column, DateTime Time)? Hit(Point p, bool snapUp = false)
    {
        var machines = Machines;
        var lanes = Lanes(machines);
        if (lanes.Count == 0 || p.X < RulerWidth) return null;
        var columns = Days * lanes.Count;
        var column = Math.Clamp((int)((p.X - RulerWidth) / ColumnWidth(columns)), 0, columns - 1);
        var minutes = Math.Clamp(p.Y / HourHeight * 60, 0, HoursShown * 60);
        var snapped = (snapUp ? Math.Ceiling(minutes / SnapMinutes) : Math.Floor(minutes / SnapMinutes)) * SnapMinutes;
        return (column, ColumnDay(column, lanes.Count).AddMinutes(snapped));
    }

    /// <summary>The time at <paramref name="y"/> in the column starting <paramref name="day"/>, to the quarter hour (rounded up when <paramref name="up"/>).</summary>
    private DateTime TimeAt(double y, DateTime day, bool up)
    {
        var minutes = Math.Clamp(y / HourHeight * 60, 0, HoursShown * 60);
        return day.AddMinutes((up ? Math.Ceiling(minutes / SnapMinutes) : Math.Floor(minutes / SnapMinutes)) * SnapMinutes);
    }

    private Point _downAt;

    private void Body_MouseLeftButtonDown(object sender, MouseButtonEventArgs e)
    {
        if (Hit(e.GetPosition(Body)) is not { } hit) return;
        _downAt = e.GetPosition(Body);
        _drag = (hit.Column, hit.Time);
        _band = new Rectangle { RadiusX = 4, RadiusY = 4, StrokeThickness = 1.5, Opacity = 0.6, IsHitTestVisible = false };
        _band.SetResourceReference(Shape.FillProperty, "AccentFillColorDefaultBrush");
        Panel.SetZIndex(_band, 3);
        Body.Children.Add(_band);
        Body.CaptureMouse();
        DrawBand(hit.Time, hit.Time.AddMinutes(SnapMinutes));
        e.Handled = true;
    }

    private void Body_MouseMove(object sender, MouseEventArgs e)
    {
        if (_drag is not { } drag) return;
        // Within the column it started in: up or down from where it started.
        var y = e.GetPosition(Body).Y;
        var day = ColumnDay(drag.Column, Lanes(Machines).Count);
        var (from, to) = y >= _downAt.Y
            ? (drag.Start, TimeAt(y, day, up: true))
            : (TimeAt(y, day, up: false), drag.Start.AddMinutes(SnapMinutes));
        if (to - from < TimeSpan.FromMinutes(SnapMinutes)) to = from.AddMinutes(SnapMinutes);
        DrawBand(from, to);
    }

    private void DrawBand(DateTime from, DateTime to)
    {
        if (_band == null || _drag is not { } drag) return;
        var lanes = Lanes(Machines);
        var col = ColumnWidth(Days * lanes.Count);
        Canvas.SetLeft(_band, RulerWidth + drag.Column * col + 2);
        Canvas.SetTop(_band, (from - ColumnDay(drag.Column, lanes.Count)).TotalHours * HourHeight);
        _band.Width = Math.Max(4, col - 4);
        _band.Height = Math.Max(6, (to - from).TotalHours * HourHeight);
        _band.Tag = (from, to);
    }

    private void Body_MouseLeftButtonUp(object sender, MouseButtonEventArgs e)
    {
        if (_drag is not { } drag || _band?.Tag is not ValueTuple<DateTime, DateTime> range)
        {
            EndDrag();
            return;
        }
        var (from, to) = range;
        // A click (no drag) books an hour.
        if (Math.Abs(e.GetPosition(Body).Y - _downAt.Y) < 6) to = from.AddHours(1);
        DrawBand(from, to);
        Body.ReleaseMouseCapture();
        var lanes = Lanes(Machines);
        var machine = lanes[drag.Column % lanes.Count];
        var culture = CultureInfo.CurrentCulture;
        var now = DateTime.Now;
        if (to <= now)
        {
            Status.Text = "That time is over: drag over time to come to book it.";
            EndDrag();
            return;
        }
        if (from < now) from = now;
        var menu = new ContextMenu { PlacementTarget = _band, Placement = PlacementMode.MousePoint };
        menu.Items.Add(new MenuItem
        {
            Header = $"Book “{machine}”, {from.ToString("ddd d MMM t", culture)} – {to.ToString("t", culture)}",
            FontWeight = FontWeights.SemiBold,
            Command = new CommunityToolkit.Mvvm.Input.RelayCommand(() => Tell(_host.Book(machine, from, to, null))),
        });
        menu.Items.Add(new MenuItem
        {
            Header = "More options…",
            Command = new CommunityToolkit.Mvvm.Input.RelayCommand(() => Dispatcher.BeginInvoke(() => NewReservation(machine, from, to))),
        });
        menu.Items.Add(new MenuItem { Header = "Cancel", Command = new CommunityToolkit.Mvvm.Input.RelayCommand(() => { }) });
        menu.Closed += (_, _) => EndDrag();
        menu.IsOpen = true;
    }

    /// <summary>Right-click on free time: a new reservation there (an hour), in the dialog.</summary>
    private void Body_MouseRightButtonUp(object sender, MouseButtonEventArgs e)
    {
        if (_drag != null || Hit(e.GetPosition(Body)) is not { } hit) return;
        var lanes = Lanes(Machines);
        var machine = lanes[hit.Column % lanes.Count];
        var from = hit.Time < DateTime.Now ? NextQuarter(DateTime.Now) : hit.Time;
        var menu = new ContextMenu { PlacementTarget = Body, Placement = PlacementMode.MousePoint };
        menu.Items.Add(new MenuItem
        {
            Header = $"New reservation of “{machine}”…",
            Command = new CommunityToolkit.Mvvm.Input.RelayCommand(() => NewReservation(machine, from, from.AddHours(1))),
        });
        menu.IsOpen = true;
        e.Handled = true;
    }

    /// <summary>The next quarter hour from <paramref name="at"/>.</summary>
    private static DateTime NextQuarter(DateTime at)
    {
        var start = at.Date.AddMinutes(Math.Ceiling(at.TimeOfDay.TotalMinutes / SnapMinutes) * SnapMinutes);
        return start <= at ? start.AddMinutes(SnapMinutes) : start;
    }

    // ───────────── The reservation dialog (#d27b8c7b) ─────────────

    private void NewReservation_Click(object sender, RoutedEventArgs e)
    {
        var start = NextQuarter(DateTime.Now);
        NewReservation(_machine, start, start.AddHours(1));
    }

    /// <summary>A new reservation, in the dialog: booked for the time you set (a time to come).</summary>
    private void NewReservation(string? machine, DateTime from, DateTime to)
    {
        var names = Machines.Select(m => m.Name).ToList();
        var dialog = new ReservationDialog(ReservationDialog.Kind.New, names, machine ?? names.FirstOrDefault(), from, to, null,
            r => _host.Book(r.Machine, r.From, r.To!.Value, r.For)) { Owner = this };
        if (dialog.ShowDialog() == true) Tell(null);
    }

    /// <summary>Your booking (its times, machine and note) or what you hold now (when it ends), in the dialog.</summary>
    private void Edit(CalendarSpan s)
    {
        var names = Machines.Select(m => m.Name).ToList();
        ReservationDialog dialog;
        if (s.Kind == SpanKind.Booked && s.Booking is { } id)
            dialog = new ReservationDialog(ReservationDialog.Kind.Booking, names, s.Resource, s.From, s.To, s.For,
                r => _host.Rebook(s.Resource, id, r.Machine, r.From, r.To!.Value, r.For),
                () => _host.Cancel(s.Resource, id));
        else if (s.Kind == SpanKind.Now)
            dialog = new ReservationDialog(ReservationDialog.Kind.Hold, names, s.Resource, s.From, s.OpenEnded ? null : s.To, s.For,
                r => _host.SetHoldEnd(s.Resource, r.To, r.For));
        else return;
        dialog.Owner = this;
        if (dialog.ShowDialog() == true) Tell(null);
    }

    private void EndDrag()
    {
        _drag = null;
        if (_band != null) Body.Children.Remove(_band);
        _band = null;
        if (Body.IsMouseCaptured) Body.ReleaseMouseCapture();
    }

    /// <summary>Says why something couldn't be done, or what was done.</summary>
    private void Tell(string? problem)
    {
        Status.Text = problem ?? _host.Status;
        Render();
    }

    private static string? Do(Action act)
    {
        act();
        return null;
    }

    // ───────────── Bits ─────────────

    private void FillMachineBox(List<SharedResource> machines)
    {
        _filling = true;
        var names = new List<string> { AllMachines };
        names.AddRange(machines.Select(m => m.Name));
        if (!MachineBox.Items.Cast<string>().SequenceEqual(names))
        {
            MachineBox.Items.Clear();
            foreach (var n in names) MachineBox.Items.Add(n);
        }
        MachineBox.SelectedItem = _machine != null && names.Contains(_machine, StringComparer.OrdinalIgnoreCase)
            ? names.First(n => string.Equals(n, _machine, StringComparison.OrdinalIgnoreCase))
            : AllMachines;
        _filling = false;
    }

    private void BuildLegend()
    {
        void Chip(string text, string fill, string stroke, bool dashed)
        {
            var swatch = new Rectangle { Width = 22, Height = 12, RadiusX = 3, RadiusY = 3, StrokeThickness = 1.5, Margin = new Thickness(0, 0, 6, 0) };
            swatch.SetResourceReference(Shape.FillProperty, fill);
            swatch.SetResourceReference(Shape.StrokeProperty, stroke);
            if (dashed) swatch.StrokeDashArray = [3, 2];
            var label = new TextBlock { Text = text, FontSize = 12, VerticalAlignment = VerticalAlignment.Center };
            label.SetResourceReference(TextBlock.ForegroundProperty, "TextFillColorSecondaryBrush");
            Legend.Children.Add(new StackPanel { Orientation = Orientation.Horizontal, Margin = new Thickness(0, 0, 16, 0), Children = { swatch, label } });
        }
        Chip("Yours", "AccentFillColorDefaultBrush", "AccentFillColorDefaultBrush", false);
        Chip("An agent's", "ControlFillColorDefaultBrush", "ControlStrongFillColorDefaultBrush", false);
        Chip("Booked by you", "SystemFillColorAttentionBackgroundBrush", "AccentFillColorDefaultBrush", false);
        Chip("Your place in the queue, or until freed", "SubtleFillColorTertiaryBrush", "AccentFillColorDefaultBrush", true);
        Chip("Earlier", "SubtleFillColorSecondaryBrush", "DividerStrokeColorDefaultBrush", false);
    }

    private static Rectangle Box(double x, double y, double width, double height, string fill, string? stroke, double thickness)
    {
        var box = new Rectangle { Width = Math.Max(0, width), Height = Math.Max(0, height), StrokeThickness = thickness, IsHitTestVisible = false };
        box.SetResourceReference(Shape.FillProperty, fill);
        if (stroke != null) box.SetResourceReference(Shape.StrokeProperty, stroke);
        Canvas.SetLeft(box, x);
        Canvas.SetTop(box, y);
        return box;
    }

    private static Line Line(double x1, double y1, double x2, double y2, string brush, double thickness)
    {
        var line = new Line { X1 = x1, Y1 = y1, X2 = x2, Y2 = y2, StrokeThickness = thickness, IsHitTestVisible = false, SnapsToDevicePixels = true };
        line.SetResourceReference(Shape.StrokeProperty, brush);
        return line;
    }

    private static void Place(Canvas canvas, FrameworkElement element, double x, double y)
    {
        Canvas.SetLeft(element, x);
        Canvas.SetTop(element, y);
        if (element is TextBlock) element.IsHitTestVisible = false;
        canvas.Children.Add(element);
    }

    /// <summary>A few hours before now at the top, so now and what comes next show (the scroll stops at the day's end).</summary>
    private void ScrollToNow()
    {
        var hours = (DateTime.Now - FirstDay).TotalHours;
        if (_week) hours = DateTime.Now.TimeOfDay.TotalHours;
        BodyScroll.ScrollToVerticalOffset(Math.Max(0, hours - 3) * HourHeight);
    }

    private void Today_Click(object sender, RoutedEventArgs e)
    {
        _day = DateTime.Today;
        Render();
        ScrollToNow();
    }

    private void Back_Click(object sender, RoutedEventArgs e)
    {
        _day = _day.AddDays(-Days);
        Render();
    }

    private void Forward_Click(object sender, RoutedEventArgs e)
    {
        _day = _day.AddDays(Days);
        Render();
    }

    private void DayButton_Click(object sender, RoutedEventArgs e)
    {
        _week = false;
        Render();
    }

    private void WeekButton_Click(object sender, RoutedEventArgs e)
    {
        _week = true;
        Render();
    }

    private void MachineBox_SelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        if (_filling || MachineBox.SelectedItem is not string chosen) return;
        _machine = chosen == AllMachines ? null : chosen;
        Render();
    }
}
