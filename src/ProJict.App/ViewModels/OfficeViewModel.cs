using System.Collections.ObjectModel;
using System.Globalization;
using System.Windows.Media;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using ProJict.Core.Agents;

namespace ProJict.App.ViewModels;

/// <summary>
/// The Agents view's office: a room in perspective with desks along both side walls and a water cooler at the back.
/// Each agent shown in the view keeps a desk; it works there (Building, Managing, Reading), sleeps there when idle
/// (Resting), or stands at the cooler with the others while it chats (Talking). Positions are in the room drawing's
/// own units (<see cref="Width"/> × <see cref="Height"/>); the view scales it.
/// </summary>
public sealed partial class OfficeViewModel : ObservableObject
{
    public const double Width = 800;
    public const double Height = 520;
    /// <summary>Where the back wall meets the floor (y), and the room's back corners (x).</summary>
    private const double BackY = 300, BackLeft = 220, BackRight = 580;
    private static readonly double[] DeskDepths = [0.18, 0.40, 0.62, 0.84];
    /// <summary>Where the water cooler stands against the back wall (x), under the window.</summary>
    public const double CoolerX = 400;
    /// <summary>Where talkers stand around the cooler, clear of the desks; the first ones closest.</summary>
    public static readonly (double X, double Y)[] CoolerSpots =
        [(332, 328), (468, 328), (292, 354), (508, 354), (360, 386), (440, 386), (262, 384), (538, 384)];

    private readonly Dictionary<McpSession, OfficeAgentViewModel> _agents = [];
    private readonly Dictionary<McpSession, int> _deskOf = [];
    private readonly Dictionary<McpSession, int> _spotOf = [];

    public OfficeViewModel()
    {
        Desks = DeskDepths.Select(d => new OfficeDeskViewModel(left: true, d)).Concat(DeskDepths.Select(d => new OfficeDeskViewModel(left: false, d))).ToList();
        foreach (var desk in Desks) Scene.Add(desk);
    }

    public List<OfficeDeskViewModel> Desks { get; }
    public ObservableCollection<OfficeAgentViewModel> Agents { get; } = [];
    /// <summary>Desks and agents together, drawn back to front.</summary>
    public ObservableCollection<object> Scene { get; } = [];
    public bool IsEmpty => Agents.Count == 0;
    public string? MoreText { get; private set; }
    /// <summary>"Building 2 · Talking 1 · Resting 3".</summary>
    public string Summary { get; private set; } = "";

    /// <summary>How big things look at a point on the floor: small at the back, larger at the front.</summary>
    public static double ScaleAt(double y) => 0.55 + 0.85 * Math.Clamp((y - BackY) / (Height - BackY), 0, 1.2);

    /// <summary>Where the side walls meet the floor, from the back (0) to the front (1).</summary>
    internal static (double X, double Y) WallPoint(bool left, double depth) =>
        (left ? BackLeft - BackLeft * depth : BackRight + (Width - BackRight) * depth, BackY + (Height - BackY) * depth);

    public OfficeDeskViewModel? DeskOf(McpSession session) => _deskOf.TryGetValue(session, out var i) ? Desks[i] : null;

    // ───────────── Sky ─────────────

    [ObservableProperty] private Brush _sky = SkyAt(DateTime.Now.Hour);
    [ObservableProperty] private bool _isNight = DateTime.Now.Hour is < 6 or >= 20;
    [ObservableProperty] private bool _isDay = DateTime.Now.Hour is >= 8 and < 18;
    private int _skyHour = DateTime.Now.Hour;
    /// <summary>The clock on the wall.</summary>
    [ObservableProperty] private double _hourAngle;
    [ObservableProperty] private double _minuteAngle;

    private static Brush SkyAt(int hour)
    {
        var (top, bottom) = hour switch
        {
            >= 6 and < 8 => ("#7FA7D9", "#F7C08A"),
            >= 8 and < 17 => ("#6FB1EA", "#CFE8FA"),
            >= 17 and < 20 => ("#5A6DA8", "#F2A46C"),
            _ => ("#0B1430", "#22386A"),
        };
        var brush = new LinearGradientBrush((Color)ColorConverter.ConvertFromString(top), (Color)ColorConverter.ConvertFromString(bottom), 90);
        brush.Freeze();
        return brush;
    }

    // ───────────── Who's where ─────────────

    /// <summary>The agents the view shows, each at its desk or at the cooler; called as they come, go and act.</summary>
    public void Update(IReadOnlyList<AgentCardViewModel> cards, DateTime now, Func<McpSession, bool> holdsItems, Func<McpSession, string?> lastChat)
    {
        var sessions = cards.Select(c => c.Session).ToList();
        foreach (var gone in _agents.Keys.Except(sessions).ToList())
        {
            Agents.Remove(_agents[gone]);
            Scene.Remove(_agents[gone]);
            _agents.Remove(gone);
            _deskOf.Remove(gone);
            _spotOf.Remove(gone);
        }
        // Each keeps its desk; newcomers take the first free one, front first (they're easier to see).
        var order = Enumerable.Range(0, Desks.Count).OrderByDescending(i => Desks[i].Depth).ThenBy(i => Desks[i].Left ? 0 : 1).ToList();
        var placed = new List<OfficeAgentViewModel>();
        foreach (var card in cards)
        {
            if (!_deskOf.ContainsKey(card.Session))
            {
                var free = order.Where(i => !_deskOf.ContainsValue(i)).Cast<int?>().FirstOrDefault();
                if (free == null) continue;
                _deskOf[card.Session] = free.Value;
            }
            if (!_agents.TryGetValue(card.Session, out var agent))
            {
                agent = new OfficeAgentViewModel(card);
                _agents[card.Session] = agent;
                Agents.Add(agent);
                Scene.Add(agent);
            }
            else agent.Card = card;
            placed.Add(agent);
        }
        MoreText = cards.Count > placed.Count ? $"+{cards.Count - placed.Count} more without a desk" : null;

        var activities = placed.ToDictionary(a => a, a => OfficeActivities.Of(a.Session, now, holdsItems(a.Session)));
        // Talkers keep their spot at the cooler while they talk; newcomers take the first free one.
        foreach (var (agent, activity) in activities)
            if (activity != OfficeActivity.Talking) _spotOf.Remove(agent.Session);
        foreach (var (agent, activity) in activities)
            if (activity == OfficeActivity.Talking && !_spotOf.ContainsKey(agent.Session))
                _spotOf[agent.Session] = Enumerable.Range(0, CoolerSpots.Length).Cast<int?>().FirstOrDefault(i => !_spotOf.ContainsValue(i!.Value)) ?? _spotOf.Count % CoolerSpots.Length;
        foreach (var agent in placed)
        {
            var desk = Desks[_deskOf[agent.Session]];
            var activity = activities[agent];
            if (activity == OfficeActivity.Talking)
            {
                var (x, y) = CoolerSpots[_spotOf[agent.Session]];
                agent.Place(activity, x, y, standing: true, facingRight: x < CoolerX, desk, lastChat(agent.Session));
            }
            else
            {
                var (x, y) = desk.Seat;
                agent.Place(activity, x, y, standing: false, facingRight: !desk.Left, desk, null);
            }
        }
        for (var i = 0; i < Desks.Count; i++)
        {
            var at = placed.FirstOrDefault(a => _deskOf[a.Session] == i);
            Desks[i].Show(at == null ? null : at.Activity == OfficeActivity.Talking ? DeskScreen.Away : (DeskScreen)at.Activity);
        }

        HourAngle = now.Hour % 12 * 30 + now.Minute * 0.5;
        MinuteAngle = now.Minute * 6 + now.Second * 0.1;
        if (now.Hour != _skyHour)
        {
            _skyHour = now.Hour;
            Sky = SkyAt(now.Hour);
            IsNight = now.Hour is < 6 or >= 20;
            IsDay = now.Hour is >= 8 and < 18;
        }
        Summary = string.Join(" · ", placed.GroupBy(a => a.Activity).OrderBy(g => g.Key).Select(g => $"{OfficeActivities.Name(g.Key)} {g.Count()}"));
        OnPropertyChanged(nameof(IsEmpty));
        OnPropertyChanged(nameof(MoreText));
        OnPropertyChanged(nameof(Summary));
    }
}

/// <summary>What a desk's monitor shows: its agent's work, a screen saver while it's at the cooler, or nothing.</summary>
public enum DeskScreen { Building, Managing, Reading, Talking, Resting, Away, Empty }

/// <summary>A desk against a side wall, with its monitor and chair; the screen follows whoever sits there.</summary>
public sealed partial class OfficeDeskViewModel : ObservableObject
{
    public OfficeDeskViewModel(bool left, double depth)
    {
        Left = left;
        Depth = depth;
        (X, Y) = OfficeViewModel.WallPoint(left, depth);
        Scale = OfficeViewModel.ScaleAt(Y);
        Seat = (X + (left ? 89 : -89) * Scale, Y - 38 * Scale);
    }

    public bool Left { get; }
    public double Depth { get; }
    /// <summary>Where the desk stands against the wall, and how big it looks there.</summary>
    public double X { get; }
    public double Y { get; }
    public double Scale { get; }
    /// <summary>Mirrored on the right wall.</summary>
    public double ScaleX => Left ? Scale : -Scale;
    public int ZIndex => (int)Y * 2;
    /// <summary>Where its agent sits.</summary>
    public (double X, double Y) Seat { get; }

    [ObservableProperty] private DeskScreen _screen = DeskScreen.Empty;

    public void Show(DeskScreen? screen) => Screen = screen ?? DeskScreen.Empty;
}

/// <summary>
/// An agent in the office: a small robot in its badge's colour with its initials, at its desk or at the cooler, posed by
/// what it's doing; the view walks it over when that changes.
/// </summary>
public sealed partial class OfficeAgentViewModel(AgentCardViewModel card) : ObservableObject
{
    /// <summary>Its card in the view (rebuilt as agents come and go).</summary>
    [ObservableProperty] private AgentCardViewModel _card = card;
    public McpSession Session => Card.Session;
    public string Name => Card.Name;
    public WorkerBadge Badge => Card.Badge;

    partial void OnCardChanged(AgentCardViewModel value)
    {
        OnPropertyChanged(nameof(Name));
        OnPropertyChanged(nameof(Badge));
    }

    [ObservableProperty] private OfficeActivity _activity = OfficeActivity.Reading;
    /// <summary>Its seat (sitting) or hips (standing, legs below), in room units, and how big it looks there.</summary>
    [ObservableProperty] private double _x;
    [ObservableProperty] private double _y;
    [ObservableProperty] private double _scale = 1;
    [ObservableProperty] private bool _standing;
    /// <summary>Faces the right wall (drawn mirrored), else the left.</summary>
    [ObservableProperty] private bool _facingRight;
    /// <summary>On its way between its desk and the cooler (set by the view while it walks).</summary>
    [ObservableProperty] private bool _walking;
    /// <summary>In front of its desk; at the cooler, by where it stands.</summary>
    [ObservableProperty] private int _zIndex;
    /// <summary>What it says at the cooler: its last chat message, or that it's listening.</summary>
    [ObservableProperty] private string? _bubble;
    [ObservableProperty] private string _toolTip = "";

    public string ActivityName => OfficeActivities.Name(Activity);

    partial void OnActivityChanged(OfficeActivity value) => OnPropertyChanged(nameof(ActivityName));

    internal void Place(OfficeActivity activity, double x, double y, bool standing, bool facingRight, OfficeDeskViewModel desk, string? said)
    {
        Activity = activity;
        Standing = standing;
        FacingRight = facingRight;
        Scale = standing ? OfficeViewModel.ScaleAt(y) : desk.Scale;
        X = x;
        Y = y;
        ZIndex = standing ? (int)(y + 24 * Scale) * 2 + 1 : desk.ZIndex + 1;
        Bubble = activity == OfficeActivity.Talking ? Said(said) ?? (Session.Listening ? "Listening…" : "…") : null;
        var doing = activity switch
        {
            OfficeActivity.Resting => $"Resting since {AgentsViewSettings.LastSeen(Session).ToString("HH:mm", CultureInfo.CurrentCulture)}",
            OfficeActivity.Talking => Session.Listening ? "Listening in Chat" : "Talking in Chat",
            OfficeActivity.Building when Card.CheckedOut.FirstOrDefault() is { } item => $"Building: {item.Text}",
            _ => $"{OfficeActivities.Name(activity)}: {Card.StatusText}",
        };
        ToolTip = $"{Session.FullName}\n{doing}\n{Card.Where}\nClick to show its card · right-click to write to it";
    }

    /// <summary>One line of what it said, short enough for a bubble.</summary>
    private static string? Said(string? text)
    {
        if (string.IsNullOrWhiteSpace(text)) return null;
        var line = string.Join(' ', text.Split(['\r', '\n', '\t'], StringSplitOptions.RemoveEmptyEntries)).Trim();
        return line.Length > 80 ? line[..79].TrimEnd() + "…" : line;
    }

    /// <summary>A click on it shows its card.</summary>
    [RelayCommand] private void ShowCard() => Card.RequestReveal();
}
