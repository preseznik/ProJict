using System.Collections.ObjectModel;
using System.Globalization;
using System.Windows;
using System.Windows.Input;
using System.Windows.Media;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using ProJict.Core.Agents;
using ProJict.Core.Features;
using ProJict.Core.Model;

namespace ProJict.App.ViewModels;

/// <summary>
/// The Agents view's office: a room in perspective with desks along both side walls and a water cooler in front.
/// Each agent shown in the view keeps a desk; it works there (Building, Managing, Reading), sleeps there when idle
/// (Resting) or sits there with headphones on when it hears Chat right away (Listening), or stands at the cooler with the
/// others while it chats (Talking). Positions are in the room drawing's
/// own units (<see cref="Width"/> × <see cref="Height"/>); the view scales it.
/// </summary>
public sealed partial class OfficeViewModel : ObservableObject
{
    public const double Width = 800;
    public const double Height = 520;
    /// <summary>Where the back wall meets the floor (y), and the room's back corners (x).</summary>
    private const double BackY = 300, BackLeft = 220, BackRight = 580;
    private static readonly double[] DeskDepths = [0.18, 0.40, 0.62, 0.84];
    /// <summary>Where the water cooler stands, at the front of the room in the middle (its base).</summary>
    public const double CoolerX = 400, CoolerY = 460;
    /// <summary>
    /// Where talkers stand (their hips) around the cooler: first on either side, facing each other, then behind it, in
    /// front of it (wider, so nobody hides the one behind) and further out. From where their feet are, next to its base.
    /// </summary>
    public static readonly (double X, double Y)[] CoolerSpots =
        [.. new (double X, double Y)[] { (-118, 0), (118, 0), (-46, -30), (46, -30), (-78, 30), (78, 30), (-190, -14), (190, -14) }
            .Select(o => StandingAt(CoolerX + o.X, CoolerY + o.Y))];

    /// <summary>Where agents not hired yet wait in the studio (their hips): in front of the bench under the window, two rows.</summary>
    public static readonly (double X, double Y)[] LobbySpots =
        [.. Enumerable.Range(0, 5).Select(i => StandingAt(344 + 28 * i, 318)), .. Enumerable.Range(0, 4).Select(i => StandingAt(358 + 28 * i, 340))];

    /// <summary>Where a robot standing with its feet here has its hips.</summary>
    private static (double X, double Y) StandingAt(double x, double feetY) => (x, feetY - 24 * ScaleAt(feetY));
    /// <summary>Room between talkers' names and bubbles stacked above each other.</summary>
    private const double LabelGap = 4;

    private readonly Dictionary<McpSession, OfficeAgentViewModel> _agents = [];
    private readonly Dictionary<McpSession, int> _deskOf = [];
    private readonly Dictionary<McpSession, int> _spotOf = [];
    private readonly Dictionary<McpSession, int> _lobbyOf = [];
    /// <summary>In the studio, a role's juniors: they stand beside its desk, where its senior sits (the desk, and which of them).</summary>
    private readonly Dictionary<McpSession, (int Desk, int Index)> _besideOf = [];

    /// <summary>The project whose game studio shows instead of the office (its banner), or null for the office.</summary>
    [ObservableProperty] [NotifyPropertyChangedFor(nameof(IsStudio), nameof(BannerText))] private string? _studioName;
    public bool IsStudio => StudioName != null;
    /// <summary>A film studio: a green screen on the back wall.</summary>
    [ObservableProperty] private bool _isFilm;
    public string BannerText => StudioName is not { } name ? "" : Board is { Total: > 0 } board ? $"{name} studio · {board.Percent}% done" : $"{name} studio";

    /// <summary>The studio's milestone board: how much of the project is done, and of its categories.</summary>
    [ObservableProperty] [NotifyPropertyChangedFor(nameof(BannerText), nameof(Milestones), nameof(OverallText), nameof(OverallFill))] private MilestoneBoard? _board;
    public IReadOnlyList<MilestoneRow> Milestones => Board?.Rows ?? [];
    public string OverallText => Board is { } board ? $"{board.Percent}%" : "";
    public double OverallFill => Board is { Total: > 0 } board ? MilestoneRow.BarWidth * board.Done / board.Total : 0;

    /// <summary>A new board only when something on it changed, so it isn't rebuilt every second.</summary>
    private void ShowBoard(MilestoneBoard? board)
    {
        if (board?.Signature != Board?.Signature) Board = board;
    }

    public OfficeViewModel()
    {
        Desks = DeskDepths.Select(d => new OfficeDeskViewModel(left: true, d)).Concat(DeskDepths.Select(d => new OfficeDeskViewModel(left: false, d))).ToList();
        foreach (var desk in Desks) Scene.Add(desk);
        Scene.Add(Cooler);
        Scene.Add(Dim);
        Scene.Add(Screen);
    }

    /// <summary>The agent you clicked: the view flies in on it and dims the rest, its work on a screen by its desk.</summary>
    [ObservableProperty] private OfficeAgentViewModel? _focused;
    /// <summary>The water cooler, in the scene so talkers behind it are hidden by it and those in front hide it.</summary>
    public OfficeCoolerViewModel Cooler { get; } = new();
    /// <summary>Darkens the room behind the focused agent.</summary>
    public OfficeDimViewModel Dim { get; } = new();
    /// <summary>The focused agent's activity, on a big screen above its desk while it works there.</summary>
    public OfficeScreenViewModel Screen { get; } = new();

    /// <summary>Focuses the agent (or none): in front of everything, the rest dimmed.</summary>
    public void Focus(OfficeAgentViewModel? agent)
    {
        foreach (var other in Agents) other.OnTop = other == agent;
        // The screen first: the view flies to the agent and its screen when Focused changes.
        Screen.Show(agent, agent != null ? DeskOf(agent.Session) : null);
        Dim.Shown = agent != null;
        Focused = agent;
    }

    private void ShowScreen() => Screen.Show(Focused, Focused is { } a ? DeskOf(a.Session) : null);

    public List<OfficeDeskViewModel> Desks { get; }
    public ObservableCollection<OfficeAgentViewModel> Agents { get; } = [];
    /// <summary>Desks and agents together, drawn back to front.</summary>
    public ObservableCollection<object> Scene { get; } = [];
    public bool IsEmpty => Agents.Count == 0 && _visitors.Count == 0;

    // Agents connected to another ProJict connected here: at the back of the room, not at desks.
    private readonly List<OfficeVisitorViewModel> _visitors = [];
    private string? _visitorsSignature;
    /// <summary>Visitors shown at most; the rest are counted in <see cref="VisitorsText"/>.</summary>
    public const int MaxVisitors = 6;
    private const double VisitorsX = 302, VisitorGap = 27, VisitorsFeetY = 306;
    /// <summary>"2 visiting from other ProJicts".</summary>
    [ObservableProperty] private string? _visitorsText;
    public IReadOnlyList<OfficeVisitorViewModel> Visitors => _visitors;

    /// <summary>Who's visiting from other ProJicts; only rebuilt when that changes.</summary>
    internal void SetVisitors(IReadOnlyList<AgentActivityViewModel.Visitor> visitors, Action<string> message)
    {
        // In the drawn office they stand on its floor, at the back between the desks (its back wall is lower down).
        var drawnOffice = IsDrawnLook && !IsStudio;
        var (startX, feetY) = drawnOffice ? (334.0, 386.0) : (VisitorsX, VisitorsFeetY);
        var signature = $"{drawnOffice}|" + string.Join("|", visitors.Select(v => $"{v.Name}:{v.From}:{v.Away}"));
        if (signature == _visitorsSignature) return;
        _visitorsSignature = signature;
        foreach (var visitor in _visitors) Scene.Remove(visitor);
        _visitors.Clear();
        foreach (var (v, i) in visitors.Take(MaxVisitors).Select((v, i) => (v, i)))
        {
            var visitor = new OfficeVisitorViewModel(v.Name, v.From, v.Away, startX + VisitorGap * i, feetY, () => message(v.Name));
            _visitors.Add(visitor);
            Scene.Add(visitor);
        }
        VisitorsText = visitors.Count switch
        {
            0 => null,
            1 => $"Visiting: {visitors[0].Name}, from {visitors[0].From}'s ProJict",
            _ => $"Visiting from other ProJicts: {string.Join(", ", visitors.Select(v => v.Name))}",
        };
        OnPropertyChanged(nameof(IsEmpty));
        OnPropertyChanged(nameof(Visitors));
    }
    public string? MoreText { get; private set; }
    /// <summary>All projects: the agents hired in a project's studio, who are there rather than here.</summary>
    [ObservableProperty] private string? _elsewhere;
    /// <summary>"Building 2 · Talking 1 · Resting 3".</summary>
    public string Summary { get; private set; } = "";

    /// <summary>How big things look at a point on the floor: small at the back, larger at the front.</summary>
    public static double ScaleAt(double y) => 0.55 + 0.85 * Math.Clamp((y - BackY) / (Height - BackY), 0, 1.2);

    /// <summary>Where the side walls meet the floor, from the back (0) to the front (1).</summary>
    internal static (double X, double Y) WallPoint(bool left, double depth) =>
        (left ? BackLeft - BackLeft * depth : BackRight + (Width - BackRight) * depth, BackY + (Height - BackY) * depth);

    public OfficeDeskViewModel? DeskOf(McpSession session) => _deskOf.TryGetValue(session, out var i) ? Desks[i] : null;

    /// <summary>How far the room is zoomed in and which part shows; kept here, so it stays when the view is rebuilt.</summary>
    public OfficeCamera Camera { get; } = new(Width, Height);

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
    /// <param name="watching">A chat watcher runs for the agent.</param>
    /// <param name="studio">A project's game studio to show instead of the office: hired agents work at their role's station, the others wait in the lobby.</param>
    /// <param name="asleep">You put every agent to sleep: the ones done sleep at their desks.</param>
    public void Update(IReadOnlyList<AgentCardViewModel> cards, DateTime now, Func<McpSession, bool> holdsItems, Func<McpSession, bool> watching,
        Func<McpSession, string?> lastChat, StudioContext? studio = null, bool asleep = false)
    {
        var sessions = cards.Select(c => c.Session).ToList();
        foreach (var gone in _agents.Keys.Except(sessions).ToList())
        {
            Agents.Remove(_agents[gone]);
            Scene.Remove(_agents[gone]);
            _agents.Remove(gone);
            _deskOf.Remove(gone);
            _spotOf.Remove(gone);
            _lobbyOf.Remove(gone);
            _besideOf.Remove(gone);
        }
        StudioName = studio?.ProjectName;
        IsFilm = studio?.Studio.Kind == StudioKind.Film;
        ShowBoard(studio?.Board);
        for (var i = 0; i < Desks.Count; i++) Desks[i].SetRole(studio?.Studio.Roles.FirstOrDefault(r => StudioFlow.PlaceOf(r.Station) == i));
        if (studio != null)
        {
            // In the studio a desk is a role's station: its senior works there (a junior, while the senior isn't here),
            // its juniors stand beside it, and the others wait in the lobby.
            _deskOf.Clear();
            _besideOf.Clear();
            var hired = cards.Select(c => (c.Session, Role: StudioFlow.RoleOf(studio.Studio, c.Session.AgentName))).Where(h => h.Role != null).ToList();
            foreach (var (session, role) in hired.OrderBy(h => string.Equals(StudioFlow.HolderOf(studio.Studio, h.Role!), h.Session.AgentName, StringComparison.OrdinalIgnoreCase) ? 0 : 1))
            {
                var place = StudioFlow.PlaceOf(role!.Station);
                if (!_deskOf.ContainsValue(place)) _deskOf[session] = place;
                else _besideOf[session] = (place, _besideOf.Values.Count(b => b.Desk == place));
            }
            foreach (var session in _lobbyOf.Keys.Where(s => _deskOf.ContainsKey(s) || _besideOf.ContainsKey(s)).ToList()) _lobbyOf.Remove(session);
        }
        else
        {
            _lobbyOf.Clear();
            _besideOf.Clear();
        }
        // Each keeps its desk; newcomers take the first free one, front first (they're easier to see).
        var order = Enumerable.Range(0, Desks.Count).OrderByDescending(i => Desks[i].Depth).ThenBy(i => Desks[i].Left ? 0 : 1).ToList();
        var placed = new List<OfficeAgentViewModel>();
        foreach (var card in cards)
        {
            if (studio != null)
            {
                if (!_deskOf.ContainsKey(card.Session) && !_besideOf.ContainsKey(card.Session) && !_lobbyOf.ContainsKey(card.Session))
                {
                    var spot = Enumerable.Range(0, Lobby.Length).Cast<int?>().FirstOrDefault(i => !_lobbyOf.ContainsValue(i!.Value));
                    if (spot == null) continue;
                    _lobbyOf[card.Session] = spot.Value;
                }
            }
            else if (!_deskOf.ContainsKey(card.Session))
            {
                var free = order.Where(i => !_deskOf.ContainsValue(i)).Cast<int?>().FirstOrDefault();
                if (free == null) continue;
                _deskOf[card.Session] = free.Value;
            }
            if (!_agents.TryGetValue(card.Session, out var agent))
            {
                agent = new OfficeAgentViewModel(card, BringToFront);
                agent.Feel(Temperature);
                _agents[card.Session] = agent;
                Agents.Add(agent);
                Scene.Add(agent);
            }
            else agent.Card = card;
            placed.Add(agent);
        }
        MoreText = cards.Count > placed.Count ? $"+{cards.Count - placed.Count} more {(studio != null ? "waiting outside" : "without a desk")}" : null;

        var activities = placed.ToDictionary(a => a, a => OfficeActivities.Of(a.Session, now, holdsItems(a.Session), watching(a.Session), asleep));
        // Talkers keep their spot at the cooler while they talk, but move in on their side when a closer one comes free;
        // newcomers take the first free one.
        foreach (var (agent, activity) in activities)
            if (activity != OfficeActivity.Talking) _spotOf.Remove(agent.Session);
        foreach (var (session, spot) in _spotOf.OrderBy(s => s.Value).ToList())
        {
            var closer = Enumerable.Range(0, spot).Where(i => i % 2 == spot % 2 && !_spotOf.ContainsValue(i)).Cast<int?>().FirstOrDefault();
            if (closer != null) _spotOf[session] = closer.Value;
        }
        foreach (var (agent, activity) in activities)
            if (activity == OfficeActivity.Talking && !_spotOf.ContainsKey(agent.Session))
                _spotOf[agent.Session] = Enumerable.Range(0, TalkSpots.Length).Cast<int?>().FirstOrDefault(i => !_spotOf.ContainsValue(i!.Value)) ?? _spotOf.Count % TalkSpots.Length;
        foreach (var agent in placed)
        {
            var desk = _deskOf.TryGetValue(agent.Session, out var d) ? Desks[d] : null;
            var activity = activities[agent];
            var hears = watching(agent.Session) || agent.Session.Listening;
            agent.SetStudio(studio);
            if (activity == OfficeActivity.Talking)
            {
                var (x, y) = TalkSpots[_spotOf[agent.Session]];
                agent.Place(activity, x, y, standing: true, facingRight: x < CoolerX, desk, lastChat(agent.Session), hears);
            }
            else if (desk == null && _besideOf.TryGetValue(agent.Session, out var beside))
            {
                // Beside its senior's desk, on the room's side, facing it; the next one a step further and in front.
                var station = Desks[beside.Desk];
                var (x, y) = StandingAt(station.Seat.X + (station.Left ? 1 : -1) * (36 + 30 * beside.Index) * station.Scale, station.Y + 3 + 8 * beside.Index);
                agent.Place(activity, x, y, standing: true, facingRight: !station.Left, null, null, hears);
            }
            else if (desk == null)
            {
                var (x, y) = Lobby[_lobbyOf[agent.Session]];
                agent.Place(activity, x, y, standing: true, facingRight: x < CoolerX, null, null, hears);
            }
            else
            {
                var (x, y) = desk.Seat;
                agent.Place(activity, x, y, standing: false, facingRight: !desk.Left, desk, null, hears);
            }
        }
        StackLabels(placed);
        for (var i = 0; i < Desks.Count; i++)
        {
            var at = placed.FirstOrDefault(a => _deskOf.TryGetValue(a.Session, out var d) && d == i);
            Desks[i].Show(at == null ? null : at.Activity == OfficeActivity.Talking ? DeskScreen.Away : (DeskScreen)at.Activity);
        }

        SyncTheme();
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
        // The focused agent left: nothing's focused. Still here: its screen follows what it does.
        if (Focused != null && !Agents.Contains(Focused)) Focus(null);
        else ShowScreen();
    }

    /// <summary>
    /// Lifts the names and bubbles of those standing (at the cooler, in the studio's lobby) just enough that none covers
    /// another, another of them, or an agent at its desk and its name: the front ones stay over their heads, the ones
    /// behind go higher, with a line down to their robot.
    /// </summary>
    private static void StackLabels(List<OfficeAgentViewModel> agents)
    {
        foreach (var agent in agents)
            if (!agent.Standing) agent.LabelLift = 0;
        var talkers = agents.Where(a => a.Standing).OrderByDescending(a => a.Y).ToList();
        // What a label must stay clear of: the talkers (each its own excepted), the others with their names, and the
        // labels already placed.
        var taken = talkers.Select(a => new Rect(a.X - 22 * a.Scale, a.Y - 77 * a.Scale, 44 * a.Scale, 101 * a.Scale)).ToList();
        taken.AddRange(agents.Where(a => !a.Standing).Select(a => new Rect(a.X - 22 * a.Scale, a.Y - 77 * a.Scale - 24, 44 * a.Scale, 77 * a.Scale + 24)));
        for (var t = 0; t < talkers.Count; t++)
        {
            var agent = talkers[t];
            var (width, height) = agent.LabelSize();
            var natural = agent.Y - 80 * agent.Scale;
            var box = new Rect(agent.X - width / 2, natural - height, width, height);
            for (var hit = Hit(); hit >= 0; hit = Hit()) box.Y = taken[hit].Top - LabelGap - height;
            taken.Add(box);
            agent.LabelLift = natural - box.Bottom;

            int Hit()
            {
                for (var i = 0; i < taken.Count; i++)
                    if (i != t && taken[i].IntersectsWith(box)) return i;
                return -1;
            }
        }
    }

    /// <summary>A clicked agent is focused: in front of everything, in case a desk or another agent hides part of it.</summary>
    private void BringToFront(OfficeAgentViewModel agent) => Focus(agent);
}

/// <summary>What hiring in the studio does: the Agents view does it and saves the project.</summary>
public interface IStudioHiring
{
    /// <param name="junior">Under the role's senior, rather than in its place.</param>
    void Hire(string agent, StudioRole role, bool junior = false);
    void Fire(string agent);
    void Swap(string agent, string other);
}

/// <summary>A project's game studio, shown instead of the office: its name, roles and hires, how to change them, and its milestone board.</summary>
public sealed record StudioContext(string ProjectName, Studio Studio, IStudioHiring Hiring, MilestoneBoard? Board = null);

/// <summary>The studio's milestone board: the project's items done of all, and its categories as milestones.</summary>
public sealed record MilestoneBoard(IReadOnlyList<MilestoneRow> Rows, int Done, int Total)
{
    public int Percent => Total == 0 ? 0 : (int)Math.Round(100.0 * Done / Total);
    public string Signature => $"{Done}/{Total};" + string.Join(";", Rows.Select(r => $"{r.Name}:{r.Done}/{r.Total}"));
}

/// <summary>A category on the milestone board: how much of it is done; a click shows it on the board, focused.</summary>
public sealed record MilestoneRow(string Name, int Done, int Total, ICommand Open)
{
    public const double BarWidth = 78;
    public int Percent => Total == 0 ? 0 : (int)Math.Round(100.0 * Done / Total);
    public bool IsComplete => Total > 0 && Done == Total;
    public string Text => IsComplete ? "✓" : $"{Percent}%";
    public double FillWidth => Total == 0 ? 0 : BarWidth * Done / Total;
    public string ToolTip => $"{Name}: {Done} of {Total} item(s) done{(IsComplete ? ", all of it" : "")}. Click to see it on the board.";
}

/// <summary>A line of a robot's Hire as… or Swap roles with menu.</summary>
public sealed record StudioChoice(string Text, ICommand Command);

/// <summary>What a desk's monitor shows: its agent's work (the same order as <see cref="OfficeActivity"/>), a screen saver while it's at the cooler, or nothing.</summary>
public enum DeskScreen { Building, Managing, Reading, Talking, Resting, Listening, Away, Empty }

/// <summary>A desk against a side wall, with its monitor and chair; the screen follows whoever sits there.</summary>
public sealed partial class OfficeDeskViewModel : ObservableObject
{
    public OfficeDeskViewModel(bool left, double depth)
    {
        Left = left;
        Depth = depth;
        StandAt(OfficeViewModel.WallPoint(left, depth));
    }

    public bool Left { get; }
    /// <summary>How far to the front it is (0 at the back, 1 at the front): newcomers take the front desks first.</summary>
    public double Depth { get; }
    /// <summary>Where the desk stands, and how big it looks there.</summary>
    public double X { get; private set; }
    public double Y { get; private set; }
    public double Scale { get; private set; }
    /// <summary>Mirrored on the right wall.</summary>
    public double ScaleX => Left ? Scale : -Scale;
    public int ZIndex => (int)Y * 2;
    /// <summary>Where its agent sits.</summary>
    public (double X, double Y) Seat { get; private set; }

    /// <summary>Stands it at a point on the floor (against the wall in the classic room, out on the floor in the drawn office).</summary>
    internal void StandAt((double X, double Y) at)
    {
        (X, Y) = at;
        Scale = OfficeViewModel.ScaleAt(Y);
        Seat = (X + (Left ? 89 : -89) * Scale, Y - 38 * Scale);
        foreach (var name in new[] { nameof(X), nameof(Y), nameof(Scale), nameof(ScaleX), nameof(ZIndex), nameof(Seat) }) OnPropertyChanged(name);
    }

    [ObservableProperty] private DeskScreen _screen = DeskScreen.Empty;

    public void Show(DeskScreen? screen) => Screen = screen ?? DeskScreen.Empty;

    /// <summary>In the studio: the role that works here, drawn with its station's things and a name plate (null in the office).</summary>
    [ObservableProperty] private StudioStation? _station;
    [ObservableProperty] private string? _roleName;
    /// <summary>The plate reads the right way round on the mirrored right wall.</summary>
    public double TextFlip => Left ? 1 : -1;

    public void SetRole(StudioRole? role)
    {
        Station = role?.Station;
        RoleName = role?.Name;
    }
}

/// <summary>
/// An agent in the office: a small robot in its badge's colour with its initials, at its desk or at the cooler, posed by
/// what it's doing; the view walks it over when that changes.
/// </summary>
public sealed partial class OfficeAgentViewModel(AgentCardViewModel card, Action<OfficeAgentViewModel> bringToFront) : ObservableObject
{
    /// <summary>Above anything else in the room (the dimming goes just below, the activity screen above).</summary>
    internal const int FrontZ = 100_000;

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
    /// <summary>In front of its desk; at the cooler, by where it stands; in front of everything once clicked.</summary>
    public int ZIndex => OnTop ? FrontZ : _depthZ;
    private int _depthZ;
    /// <summary>Clicked (focused), so drawn in front of everything until you click elsewhere.</summary>
    [ObservableProperty] [NotifyPropertyChangedFor(nameof(ZIndex), nameof(Lift), nameof(LeaderTop))] private bool _onTop;
    /// <summary>What it says at the cooler: its last chat message.</summary>
    [ObservableProperty] private string? _bubble;
    /// <summary>It hears Chat right away (a chat watcher runs for it, or it waits for messages): it wears headphones.</summary>
    [ObservableProperty] private bool _hearsChat;
    /// <summary>How far its name and bubble are lifted at the cooler, clear of the others' (room units); a line leads down.</summary>
    [ObservableProperty] [NotifyPropertyChangedFor(nameof(Lift), nameof(LeaderTop))] private double _labelLift;
    /// <summary>The lift as drawn: none while it's focused, as it's in front of everything then.</summary>
    public double Lift => OnTop ? 0 : LabelLift;
    public double LeaderTop => -Lift;
    [ObservableProperty] private string _toolTip = "";

    public string ActivityName => OfficeActivities.Name(Activity);

    partial void OnActivityChanged(OfficeActivity value) => OnPropertyChanged(nameof(ActivityName));

    // ───────────── In the studio ─────────────

    /// <summary>In a studio: its role there (null while it waits in the lobby).</summary>
    [ObservableProperty] private string? _roleName;
    [ObservableProperty] private bool _inStudio;
    /// <summary>Right-click › Hire as…: the roles, "instead of" whoever has one.</summary>
    [ObservableProperty] private IReadOnlyList<StudioChoice> _hireChoices = [];
    /// <summary>Right-click › Swap roles with: the others who have a role.</summary>
    [ObservableProperty] private IReadOnlyList<StudioChoice> _swapChoices = [];
    public bool CanFire => RoleName != null;
    public bool HasSwapChoices => SwapChoices.Count > 0;
    private Action? _fire;

    partial void OnRoleNameChanged(string? value) => OnPropertyChanged(nameof(CanFire));
    partial void OnSwapChoicesChanged(IReadOnlyList<StudioChoice> value) => OnPropertyChanged(nameof(HasSwapChoices));

    /// <summary>Right-click › Fire: back to the lobby.</summary>
    [RelayCommand] private void Fire() => _fire?.Invoke();

    internal void SetStudio(StudioContext? studio)
    {
        InStudio = studio != null;
        var name = Session.AgentName;
        var role = studio != null ? StudioFlow.RoleOf(studio.Studio, name) : null;
        RoleName = studio != null ? StudioFlow.TitleOf(studio.Studio, name) : null;
        _fire = studio != null && role != null ? () => studio.Hiring.Fire(name) : null;
        if (studio == null)
        {
            HireChoices = SwapChoices = [];
            return;
        }
        // A role with someone in it: in their place, or as a junior under them. A junior can take its senior's place.
        var junior = StudioFlow.HireOf(studio.Studio, name)?.Junior == true;
        HireChoices = [.. studio.Studio.Roles.Where(r => r != role || junior).SelectMany(r => StudioFlow.HolderOf(studio.Studio, r) is { } holder
            ? r == role
                ? [new StudioChoice($"{r.Name} (instead of {holder})", new RelayCommand(() => studio.Hiring.Hire(name, r)))]
                : new[]
                {
                    new StudioChoice($"{r.Name} (instead of {holder})", new RelayCommand(() => studio.Hiring.Hire(name, r))),
                    new StudioChoice($"Junior {r.Name} (under {holder})", new RelayCommand(() => studio.Hiring.Hire(name, r, junior: true))),
                }
            : [new StudioChoice(r.Name, new RelayCommand(() => studio.Hiring.Hire(name, r)))])];
        SwapChoices = role == null ? [] : studio.Studio.Hires.Where(h => !string.Equals(h.Agent, name, StringComparison.OrdinalIgnoreCase))
            .Select(h => new StudioChoice($"{h.Agent} ({StudioFlow.TitleOf(studio.Studio, h.Agent)})",
                new RelayCommand(() => studio.Hiring.Swap(name, h.Agent)))).ToList();
    }

    internal void Place(OfficeActivity activity, double x, double y, bool standing, bool facingRight, OfficeDeskViewModel? desk, string? said, bool hearsChat = false)
    {
        HearsChat = hearsChat;
        Activity = activity;
        Standing = standing;
        FacingRight = facingRight;
        Scale = standing ? OfficeViewModel.ScaleAt(y) : desk!.Scale;
        X = x;
        Y = y;
        _depthZ = standing ? (int)(y + 24 * Scale) * 2 + 1 : desk!.ZIndex + 1;
        OnPropertyChanged(nameof(ZIndex));
        Bubble = activity == OfficeActivity.Talking ? Said(said) ?? "…" : null;
        var doing = activity switch
        {
            OfficeActivity.Resting => $"Resting since {AgentsViewSettings.LastSeen(Session).ToString("HH:mm", CultureInfo.CurrentCulture)}",
            OfficeActivity.Talking => "Talking in Chat",
            OfficeActivity.Listening when Session.Listening => "Listening: waiting for messages in Chat, it answers as soon as you write",
            OfficeActivity.Listening => $"Listening since {AgentsViewSettings.LastSeen(Session).ToString("HH:mm", CultureInfo.CurrentCulture)}: " +
                                        "a chat watcher runs for it, so it hears you in Chat and answers right away",
            OfficeActivity.Building when Card.CheckedOut.FirstOrDefault() is { } item => $"Building: {item.Text}",
            _ => $"{OfficeActivities.Name(activity)}: {Card.StatusText}",
        };
        var hears = hearsChat && activity != OfficeActivity.Listening ? "Hears Chat right away (headphones): a chat watcher runs for it\n" : "";
        var role = !InStudio ? "" : RoleName != null ? $"{RoleName} in this studio\n" : "Waiting in the lobby: right-click › Hire as… to give it a role\n";
        ToolTip = $"{Session.FullName}\n{role}{doing}\n{hears}{Card.Where}\nClick to show its card · Shift+click to poke it · right-click to write to it" + (InStudio ? ", hire, fire or swap it" : "");
    }

    private static readonly Typeface LabelFont = new(SystemFonts.MessageFontFamily, FontStyles.Normal, FontWeights.Normal, FontStretches.Normal);

    /// <summary>About how big its name and bubble are above its head, in room units (as the view draws them).</summary>
    internal (double Width, double Height) LabelSize()
    {
        var name = Measure(Name, 160 - 12, 1);
        double width = name.Width + 12, height = name.Height + 3;
        if (Bubble is { } said)
        {
            var bubble = Measure(said, 150 - 18, 2);
            width = Math.Max(width, bubble.Width + 18);
            height += bubble.Height + 12;
        }
        return (width, height);
    }

    private static Size Measure(string text, double maxWidth, int lines)
    {
        var formatted = new FormattedText(text, CultureInfo.CurrentCulture, FlowDirection.LeftToRight, LabelFont, 14, Brushes.Black, 1)
        {
            MaxTextWidth = maxWidth, MaxLineCount = lines, Trimming = TextTrimming.CharacterEllipsis,
        };
        return new Size(formatted.WidthIncludingTrailingWhitespace, formatted.Height);
    }

    /// <summary>One line of what it said, short enough for a bubble.</summary>
    private static string? Said(string? text)
    {
        if (string.IsNullOrWhiteSpace(text)) return null;
        var line = string.Join(' ', text.Split(['\r', '\n', '\t'], StringSplitOptions.RemoveEmptyEntries)).Trim();
        return line.Length > 80 ? line[..79].TrimEnd() + "…" : line;
    }

    /// <summary>A click on it brings it to the front and shows its card.</summary>
    [RelayCommand]
    private void ShowCard()
    {
        bringToFront(this);
        Card.RequestReveal();
    }
}

/// <summary>
/// An agent connected to another ProJict connected here: it stands at the back of the room with a suitcase, asleep when
/// it's away there; its name and whose ProJict on hover, and a click writes to it.
/// </summary>
public sealed partial class OfficeVisitorViewModel(string name, string from, bool away, double feetX, double feetY, Action message)
{
    public string Name => name;
    public string From => from;
    public bool Away => away;
    public double Scale { get; } = OfficeViewModel.ScaleAt(feetY);
    /// <summary>Its hips, like a robot's.</summary>
    public double X => feetX;
    public double Y => feetY - 24 * Scale;
    public int ZIndex => (int)Y * 2;
    public WorkerBadge Badge { get; } = WorkerBadge.For([name])[0];
    public string ToolTip => $"{name}: an AI agent connected to {from}'s ProJict, {(away ? "away" : "online")}.\nClick to write to it.";

    [CommunityToolkit.Mvvm.Input.RelayCommand] private void Message() => message();
}

/// <summary>The water cooler at the front of the room, drawn by depth among the desks and agents.</summary>
public sealed class OfficeCoolerViewModel
{
    public double X => OfficeViewModel.CoolerX;
    public double Y => OfficeViewModel.CoolerY;
    public double Scale { get; } = OfficeViewModel.ScaleAt(OfficeViewModel.CoolerY);
    public int ZIndex => (int)Y * 2;
}

/// <summary>A veil over the room behind a focused agent (in the scene just below it); clicks go through it.</summary>
public sealed partial class OfficeDimViewModel : ObservableObject
{
    public int ZIndex => OfficeAgentViewModel.FrontZ - 1;
    [ObservableProperty] private bool _shown;
}

/// <summary>
/// A focused agent's activity on a big screen beside it while it works at its desk: its name, what it's doing and its
/// latest actions. In room units, toward the middle of the room; above everything else.
/// </summary>
public sealed partial class OfficeScreenViewModel : ObservableObject
{
    public const double Width = 170, Height = 112;
    public int ZIndex => OfficeAgentViewModel.FrontZ + 1;
    [ObservableProperty] private bool _shown;
    [ObservableProperty] private double _x;
    [ObservableProperty] private double _y;
    [ObservableProperty] private string _title = "";
    [ObservableProperty] private string _status = "";
    [ObservableProperty] private IReadOnlyList<string> _lines = [];

    /// <summary>Where the screen's middle is, for the camera to fly to it.</summary>
    public (double X, double Y) Middle => (X + Width / 2, Y + Height / 2);

    internal void Show(OfficeAgentViewModel? agent, OfficeDeskViewModel? desk)
    {
        if (agent == null || desk == null || agent.Standing || agent.Activity == OfficeActivity.Talking)
        {
            Shown = false;
            return;
        }
        // Beside the robot, toward the middle of the room (its name above it stays clear); inside the room.
        var scale = desk.Scale;
        X = Math.Clamp(desk.Left ? agent.X + 34 * scale : agent.X - 34 * scale - Width, 4, OfficeViewModel.Width - Width - 4);
        Y = Math.Clamp(agent.Y - 60 * scale - Height / 2, 4, OfficeViewModel.Height - Height - 4);
        Title = agent.Name;
        Status = $"{agent.ActivityName} · {agent.Card.StatusText}";
        Lines = agent.Card.Recent.Take(5).Select(r => $"{r.Age,-8} {r.Text}" + (r.TimesText is { } times ? $" {times}" : "")).ToList();
        Shown = true;
    }
}

