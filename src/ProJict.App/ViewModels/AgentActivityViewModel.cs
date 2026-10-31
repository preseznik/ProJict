using System.Globalization;
using System.Windows.Media;
using System.Windows.Threading;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using ProJict.Core.Agents;
using ProJict.Core.Features;
using ProJict.Core.Model;

namespace ProJict.App.ViewModels;

/// <summary>
/// The Agents view: the AI agents connected right now, each with what it's doing (live), what it has checked out and
/// what it did lately; below them, older agent work from the projects' history, greyed out and grouped by day, week or
/// month, each period with its stats. Optionally for one project. Its Settings panel limits the cards to agents that
/// did something lately (idle sessions stay connected until they're closed) and how far back the earlier work goes.
/// </summary>
public sealed partial class AgentActivityViewModel : ObservableObject
{
    public const string AllProjects = "All projects";
    /// <summary>An agent counts as busy this long after its last call, or after setting its status.</summary>
    public static readonly TimeSpan BusyFor = TimeSpan.FromSeconds(30);
    private const int HistoryShown = 100;

    private readonly AppState _state;
    private readonly MainViewModel _main;
    private readonly DispatcherTimer _clock;
    private readonly ActivityTimeline _timeline;
    private readonly DispatcherTimer _countClock;
    private List<FeedEvent> _historyEvents = [];
    private int _historyLimit = HistoryShown;
    private int _ticks;
    private int _lastCount = -1;
    // "Show" on the hidden lines: until you leave the view.
    private bool _showIdle;
    private bool _showOlder;

    public AgentActivityViewModel(AppState state, MainViewModel main)
    {
        _state = state;
        _main = main;
        _timeline = new ActivityTimeline(agentsOnly: true, BuildHistory);
        _grouping = GroupingOption.For(state.AppSettings.AgentsGrouping);
        // Ages ("12 s ago") and busy/idle move on by themselves; only while the view shows.
        _clock = new DispatcherTimer(DispatcherPriority.Background) { Interval = TimeSpan.FromSeconds(1) };
        _clock.Tick += (_, _) => Tick();
        // Agents drop off the sidebar's count as they go idle, also while the view is closed.
        _countClock = new DispatcherTimer(DispatcherPriority.Background) { Interval = TimeSpan.FromSeconds(30) };
        _countClock.Tick += (_, _) => CheckCount();
        _countClock.Start();
        var units = new[] { AgeUnit.Minutes, AgeUnit.Hours, AgeUnit.Days }.Select(u => new UnitOption(u, View.ActiveWithin)).ToList();
        Units = units;
        _selectedUnit = units.First(u => u.Unit == View.ActiveWithinUnit);
        _selectedEarlier = EarlierChoices.FirstOrDefault(c => c.Days == View.EarlierDays) ?? EarlierChoices[1];
    }

    private AgentsViewSettings View => _state.AppSettings.AgentsView;

    /// <summary>The sidebar's count changed (agents came, went idle, or the settings changed).</summary>
    public event Action? CountChanged;

    [ObservableProperty] private string _projectFilter = AllProjects;
    [ObservableProperty] private GroupingOption _grouping;
    public List<string> ProjectChoices { get; private set; } = [AllProjects];
    public List<AgentCardViewModel> Live { get; private set; } = [];
    /// <summary>Earlier agent work by period, newest first.</summary>
    public List<ActivityGroupViewModel> HistoryGroups { get; private set; } = [];
    /// <summary>Every earlier entry built, in order (folded periods have none).</summary>
    public List<AgentHistoryRow> History { get; private set; } = [];
    public bool HasLive => Live.Count > 0;
    /// <summary>The Earlier heading shows when there's work in the time frame or older work it hides.</summary>
    public bool HasHistory => HistoryGroups.Count > 0 || OlderCount > 0;
    public bool HasMoreHistory { get; private set; }
    public string FoldAllText => ActivityTimeline.FoldAllText(HistoryGroups);

    public string Subtitle => ProjectFilter == AllProjects ? "What AI agents are doing in your projects" : $"What AI agents are doing in {ProjectFilter}";

    /// <summary>Connected agents left out because they did nothing lately (counted also while "Show" lists them).</summary>
    public int IdleCount { get; private set; }
    private int ConnectedCount { get; set; }

    public string EmptyText => !(_state.AppSettings.AllowAgents || _main.Api.IsRunning)
        ? "AI agents can't connect: they're turned off in Settings › AI agents."
        : ConnectedCount > 0
            ? View.Show == AgentsShown.WorkingNow
                ? ProjectFilter == AllProjects ? "No AI agent is working right now." : $"No AI agent is working in {ProjectFilter} right now."
                : $"No AI agent did anything{In} in {View.Within}."
            : ProjectFilter == AllProjects
                ? "No AI agent is connected right now. Agents show up here as soon as they connect, with what they're doing."
                : $"No AI agent is working in {ProjectFilter} right now.";

    private string In => ProjectFilter == AllProjects ? "" : $" in {ProjectFilter}";

    /// <summary>The cards' heading: what the Show setting lets through.</summary>
    public string LiveTitle => View.Show switch
    {
        AgentsShown.WorkingNow => "Working now",
        AgentsShown.ActiveWithin => "Active in " + View.Within,
        _ => "Connected now",
    };

    public bool HasIdleLine => IdleCount > 0;
    public string IdleText => _showIdle
        ? $"Showing {Count(IdleCount, "idle agent")} too (nothing done in {View.Within})."
        : $"{Count(IdleCount, "idle agent")} hidden (nothing done in {View.Within}).";
    public string IdleButtonText => _showIdle ? "Hide" : "Show";

    /// <summary>Earlier work before the time frame, left out until "Show it".</summary>
    public int OlderCount { get; private set; }
    private DateTime? _earlierSince;
    public string EarlierScope => _earlierSince == null ? "" : SelectedEarlier.Label;
    public bool HasOlderLine => OlderCount > 0 && !HasMoreHistory;
    public string OlderText => _earlierSince is { } since
        ? $"{Count(OlderCount, "earlier action")} from before {since.ToString("d MMMM", CultureInfo.CurrentCulture)} hidden."
        : "";
    public bool IsHistoryEmpty => HistoryGroups.Count == 0 && OlderCount > 0;
    public string HistoryEmptyText => $"No agent work{In} in the {SelectedEarlier.Label.ToLowerInvariant()}.";

    private static string Count(int n, string what) => n == 1 ? $"1 {what}" : $"{n} {what}s";

    // The Settings panel.
    [ObservableProperty] private bool _isSettingsOpen;

    public bool ShowWorkingNow { get => View.Show == AgentsShown.WorkingNow; set { if (value) SetShow(AgentsShown.WorkingNow); } }
    public bool ShowActiveWithin { get => View.Show == AgentsShown.ActiveWithin; set { if (value) SetShow(AgentsShown.ActiveWithin); } }
    public bool ShowAllConnected { get => View.Show == AgentsShown.AllConnected; set { if (value) SetShow(AgentsShown.AllConnected); } }

    /// <summary>The number in "Active in the past [1] [hour]"; typing one picks that option.</summary>
    public string ActiveWithinText
    {
        get => View.ActiveWithin.ToString(CultureInfo.CurrentCulture);
        set
        {
            if (!int.TryParse(value, NumberStyles.Integer, CultureInfo.CurrentCulture, out var n) || n is < 1 or > 999) return;
            View.ActiveWithin = n;
            foreach (var unit in Units) unit.Count = n;
            SetShow(AgentsShown.ActiveWithin, changed: true);
        }
    }

    public IReadOnlyList<UnitOption> Units { get; }
    private UnitOption _selectedUnit;
    public UnitOption SelectedUnit
    {
        get => _selectedUnit;
        set
        {
            if (value == null || value == _selectedUnit) return;
            _selectedUnit = value;
            View.ActiveWithinUnit = value.Unit;
            OnPropertyChanged();
            SetShow(AgentsShown.ActiveWithin, changed: true);
        }
    }

    public static IReadOnlyList<EarlierOption> EarlierChoices { get; } =
        [new("Past 24 hours", 1), new("Past 7 days", 7), new("Past 30 days", 30), new("All", 0)];
    private EarlierOption _selectedEarlier;
    public EarlierOption SelectedEarlier
    {
        get => _selectedEarlier;
        set
        {
            if (value == null || value == _selectedEarlier) return;
            _selectedEarlier = value;
            View.EarlierDays = value.Days;
            _state.SaveAppSettings();
            OnPropertyChanged();
            _historyLimit = HistoryShown;
            BuildHistory();
        }
    }

    private void SetShow(AgentsShown show, bool changed = false)
    {
        if (View.Show == show && !changed) return;
        View.Show = show;
        _state.SaveAppSettings();
        foreach (var name in new[] { nameof(ShowWorkingNow), nameof(ShowActiveWithin), nameof(ShowAllConnected), nameof(ActiveWithinText) })
            OnPropertyChanged(name);
        RefreshLive();
        CheckCount();
    }

    [RelayCommand]
    private void ToggleIdle()
    {
        _showIdle = !_showIdle;
        RefreshLive();
    }

    [RelayCommand]
    private void ShowOlder()
    {
        _showOlder = true;
        BuildHistory();
    }

    /// <summary>For the sidebar: the agents the view would show (all projects).</summary>
    public int ShownCount
    {
        get
        {
            var now = DateTime.Now;
            return _main.Agents.Sessions.Count(s => Shows(s, now, null));
        }
    }

    public string CountToolTip
    {
        get
        {
            var connected = _main.Agents.Sessions.Count;
            var shown = ShownCount;
            return View.Within is not { } within || shown == connected
                ? (connected == 1 ? "1 agent connected now" : $"{connected} agents connected now")
                : $"{shown} active in {within}, {connected} connected";
        }
    }

    private bool Shows(McpSession session, DateTime now, Project? only) =>
        View.Shows(session, now, holdsItems: session.Claimed.Count > 0 && CheckedOut(session, only).Count > 0);

    /// <summary>Tells the sidebar when the number of agents shown changed.</summary>
    public void CheckCount()
    {
        var count = ShownCount;
        if (count == _lastCount) return;
        _lastCount = count;
        CountChanged?.Invoke();
    }

    partial void OnProjectFilterChanged(string value)
    {
        _historyLimit = HistoryShown;
        Refresh();
    }

    partial void OnGroupingChanged(GroupingOption value)
    {
        _state.AppSettings.AgentsGrouping = value.Grouping;
        _state.SaveAppSettings();
        _historyLimit = HistoryShown;
        BuildHistory();
    }

    /// <summary>Shows the view, for one project or all of them.</summary>
    public void Show(Project? project)
    {
        ProjectChoices = [AllProjects, .. _state.Projects.Where(p => p.Remote == null).Select(p => p.Name).Order(StringComparer.CurrentCultureIgnoreCase)];
        OnPropertyChanged(nameof(ProjectChoices));
        _showIdle = _showOlder = false;
        _historyLimit = HistoryShown;
        if (ProjectFilter != (project?.Name ?? AllProjects)) ProjectFilter = project?.Name ?? AllProjects; // refreshes
        else Refresh();
        _clock.Start();
    }

    public void Hide() => _clock.Stop();

    private Project? FilterProject => ProjectFilter == AllProjects ? null : _state.Projects.FirstOrDefault(p => p.Name == ProjectFilter);

    /// <summary>Rebuilds the cards and the history (when agents connect, act or leave, and on open).</summary>
    public void Refresh()
    {
        var project = FilterProject;
        RefreshLive();
        _historyEvents = [.. ActivityFeed.Build(_state.Projects.Where(p => p.Remote == null && (project == null || p == project)), _state.Settings, _state.UserName)
            .Where(e => e.Agent)];
        OnPropertyChanged(nameof(Subtitle));
        BuildHistory();
    }

    /// <summary>The agents connected to the project shown (or to any).</summary>
    private List<McpSession> Connected(Project? project) =>
        _main.Agents.Sessions.Where(s => project == null || _main.Agents.ProjectsOf(s).Contains(project.Id)).ToList();

    /// <summary>Rebuilds the cards: the agents the Show setting lets through, the others counted on the idle line.</summary>
    private void RefreshLive()
    {
        var project = FilterProject;
        var now = DateTime.Now;
        var connected = Connected(project);
        var active = connected.Where(s => Shows(s, now, project)).ToList();
        ConnectedCount = connected.Count;
        IdleCount = connected.Count - active.Count;
        Live = (_showIdle ? connected : active).Select(s => new AgentCardViewModel(s, this, project)).ToList();
        foreach (var name in new[] { nameof(Live), nameof(HasLive), nameof(EmptyText), nameof(LiveTitle), nameof(IdleCount), nameof(HasIdleLine), nameof(IdleText), nameof(IdleButtonText) })
            OnPropertyChanged(name);
    }

    private void BuildHistory()
    {
        // Within the time frame, from the start of its oldest day, week or month, so each period's stats are whole.
        _earlierSince = _showOlder ? null : View.EarlierSince(DateTime.Now) is { } since ? ActivityPeriods.StartOf(since, Grouping.Grouping) : null;
        var events = _earlierSince is { } from ? _historyEvents.Where(e => e.At >= from).ToList() : _historyEvents;
        OlderCount = _historyEvents.Count - events.Count;
        HistoryGroups = _timeline.Build(events, Grouping.Grouping, _historyLimit, (e, dayHeader) => new AgentHistoryRow(e, dayHeader, _main),
            ProjectFilter == AllProjects ? null : $"Only in {ProjectFilter}.", out var more);
        History = HistoryGroups.SelectMany(g => g.Rows).Cast<AgentHistoryRow>().ToList();
        HasMoreHistory = more;
        foreach (var name in new[] { nameof(HistoryGroups), nameof(History), nameof(HasHistory), nameof(HasMoreHistory), nameof(FoldAllText),
                     nameof(OlderCount), nameof(HasOlderLine), nameof(OlderText), nameof(EarlierScope), nameof(IsHistoryEmpty), nameof(HistoryEmptyText) })
            OnPropertyChanged(name);
    }

    [RelayCommand]
    private void FoldAll() => _timeline.FoldAll(HistoryGroups);

    [RelayCommand]
    private void ShowMoreHistory()
    {
        _historyLimit += HistoryShown;
        BuildHistory();
    }

    /// <summary>An agent did something: its card shows it at once.</summary>
    public void OnRecorded(McpSession session)
    {
        if (Live.FirstOrDefault(c => c.Session == session) is { } card) card.Refresh();
        else Refresh();
    }

    private void Tick()
    {
        foreach (var card in Live) card.Tick();
        // Every few seconds: agents that went idle drop off, and the idle line counts them.
        if (++_ticks % 5 != 0 || _showIdle) return;
        var project = FilterProject;
        var now = DateTime.Now;
        var active = Connected(project).Where(s => Shows(s, now, project));
        if (!active.SequenceEqual(Live.Select(c => c.Session))) RefreshLive();
    }

    internal string ProjectName(Guid? id) => id is { } p ? _state.Projects.FirstOrDefault(x => x.Id == p)?.Name ?? "" : "";

    internal IReadOnlyList<string> ProjectNames(McpSession session) =>
        _main.Agents.ProjectsOf(session).Select(id => ProjectName(id)).Where(n => n.Length > 0).ToList();

    /// <summary>Items the agent has checked out right now.</summary>
    internal IReadOnlyList<(Project Project, TodoItem Item)> CheckedOut(McpSession session, Project? only) =>
        _state.Projects.Where(p => p.Remote == null && (only == null || p == only))
            .SelectMany(p => p.Walk().Select(l => (Project: p, l.Item)))
            .Where(x => x.Item.State == ItemState.InProgress && session.Claimed.Contains(x.Item.Id) &&
                        string.Equals(x.Item.ClaimedBy, session.AgentName, StringComparison.OrdinalIgnoreCase))
            .ToList();

    internal void Reveal(Project project, Guid itemId)
    {
        var board = _main.OpenProject(project);
        if (project.Find(itemId) is { } loc) board.Reveal(loc.Item);
    }

    /// <summary>"now", "12 s ago", "3 min ago", "10:42".</summary>
    internal static string Age(DateTime at, DateTime now)
    {
        var age = now - at;
        if (age < TimeSpan.FromSeconds(5)) return "now";
        if (age < TimeSpan.FromMinutes(1)) return $"{(int)age.TotalSeconds} s ago";
        if (age < TimeSpan.FromHours(1)) return $"{(int)age.TotalMinutes} min ago";
        return at.Date == now.Date ? at.ToString("HH:mm", CultureInfo.CurrentCulture) : at.ToString("d MMM HH:mm", CultureInfo.CurrentCulture);
    }

    /// <summary>For a project's badges in the sidebar: who is there and what each did last.</summary>
    public string ToolTipFor(Project project)
    {
        var sessions = _main.Agents.Sessions.Where(s => _main.Agents.ProjectsOf(s).Contains(project.Id)).ToList();
        if (sessions.Count == 0) return "";
        var now = DateTime.Now;
        var lines = sessions.Select(s =>
        {
            var last = s.Recent.LastOrDefault(a => a.ProjectId == project.Id) ?? s.Recent.LastOrDefault();
            var doing = s.Status is { } status && s.StatusAt is { } since && now - since < TimeSpan.FromMinutes(15) ? status
                : last != null ? $"{last.Text} ({Age(last.At, now)})" : "connected, nothing done yet";
            return $"{s.AgentName}: {doing}";
        });
        return string.Join("\n", lines) + "\nRight-click › Agents shows more.";
    }
}

/// <summary>"minutes", "hours" or "days" after the number in the Settings panel ("1 hour", "3 hours").</summary>
public sealed partial class UnitOption(AgeUnit unit, int count) : ObservableObject
{
    private int _count = count;
    public AgeUnit Unit => unit;
    public string Label => AgentsViewSettings.Unit(unit, _count);

    public int Count
    {
        get => _count;
        set
        {
            _count = value;
            OnPropertyChanged(nameof(Label));
        }
    }
}

/// <summary>How far back the Agents view's earlier work goes; 0 days for all of it.</summary>
public sealed record EarlierOption(string Label, int Days);

/// <summary>One connected agent: name, where it works, what it's doing now (with dots while busy), its check-outs and latest actions.</summary>
public sealed partial class AgentCardViewModel : ObservableObject
{
    private readonly AgentActivityViewModel _owner;
    private readonly Project? _only;

    public AgentCardViewModel(McpSession session, AgentActivityViewModel owner, Project? only)
    {
        Session = session;
        _owner = owner;
        _only = only;
        Refresh();
    }

    public McpSession Session { get; }
    public string Name => Session.AgentName;
    public WorkerBadge Badge => WorkerBadge.For([Session.AgentName])[0];
    public string Where { get; private set; } = "";
    public List<AgentActionRow> Recent { get; private set; } = [];
    public List<CheckoutRow> CheckedOut { get; private set; } = [];
    public bool HasCheckedOut => CheckedOut.Count > 0;
    public bool HasRecent => Recent.Count > 0;

    /// <summary>Called or set its status in the last half minute: the dots move.</summary>
    public bool IsBusy { get; private set; }
    public string StatusText { get; private set; } = "";
    public string StatusAge { get; private set; } = "";

    public void Refresh()
    {
        var names = _owner.ProjectNames(Session);
        var since = Session.ConnectedAt.ToString(Session.ConnectedAt.Date == DateTime.Today ? "HH:mm" : "d MMM HH:mm", CultureInfo.CurrentCulture);
        Where = (names.Count == 0 ? "Connected" : "In " + string.Join(", ", names)) + " · since " + since;
        var actions = Session.Recent.Where(a => _only == null || a.ProjectId == null || a.ProjectId == _only.Id).ToList();
        Recent = actions.AsEnumerable().Reverse().Take(6).Select((a, i) => new AgentActionRow(a, i == 0, _owner)).ToList();
        CheckedOut = _owner.CheckedOut(Session, _only).Select(x => new CheckoutRow(x.Project, x.Item, _owner)).ToList();
        foreach (var name in new[] { nameof(Name), nameof(Badge), nameof(Where), nameof(Recent), nameof(CheckedOut), nameof(HasCheckedOut), nameof(HasRecent) })
            OnPropertyChanged(name);
        Tick();
    }

    public void Tick()
    {
        var now = DateTime.Now;
        var last = Session.Recent.LastOrDefault(a => _only == null || a.ProjectId == null || a.ProjectId == _only.Id);
        var statusFresh = Session.Status != null && Session.StatusAt is { } since && now - since < TimeSpan.FromMinutes(15);
        // Its own words while they're fresh, else what it last did.
        (StatusText, var at) = statusFresh ? (Session.Status!, Session.StatusAt!.Value)
            : last != null ? (last.Text, last.At)
            : ("Connected, nothing done yet", Session.ConnectedAt);
        IsBusy = now - (Session.LastActive ?? Session.ConnectedAt) < AgentActivityViewModel.BusyFor ||
                 (statusFresh && now - Session.StatusAt!.Value < AgentActivityViewModel.BusyFor);
        StatusAge = IsBusy ? Age(at, now) : $"idle · {Age(at, now)}";
        OnPropertyChanged(nameof(StatusText));
        OnPropertyChanged(nameof(StatusAge));
        OnPropertyChanged(nameof(IsBusy));
        foreach (var row in Recent) row.Tick(now);
    }

    private static string Age(DateTime at, DateTime now) => AgentActivityViewModel.Age(at, now);
}

/// <summary>One of an agent's latest actions; the newest stands out, the rest are quieter.</summary>
public sealed partial class AgentActionRow(AgentAction action, bool isLatest, AgentActivityViewModel owner) : ObservableObject
{
    public string Text => action.Text;
    public bool IsLatest => isLatest;
    public string Project => owner.ProjectName(action.ProjectId);
    public string Age { get; private set; } = AgentActivityViewModel.Age(action.At, DateTime.Now);
    public string Time => action.At.ToString("HH:mm:ss", CultureInfo.CurrentCulture);

    public void Tick(DateTime now)
    {
        Age = AgentActivityViewModel.Age(action.At, now);
        OnPropertyChanged(nameof(Age));
    }
}

/// <summary>An item the agent has checked out; a click shows it on its board.</summary>
public sealed partial class CheckoutRow(Project project, TodoItem item, AgentActivityViewModel owner)
{
    public string Text => item.DisplayText().Replace("\r\n", " ").Replace('\n', ' ');
    public string Project => project.Name;

    [RelayCommand] private void Open() => owner.Reveal(project, item.Id);
}

/// <summary>
/// Older agent work from the projects' history, with the day's name above it when a day starts inside a week or month;
/// a click goes where it happened, as in the Feed.
/// </summary>
public sealed partial class AgentHistoryRow(FeedEvent e, string? dayHeader, MainViewModel main)
{
    public string? DayHeader => dayHeader;
    public string When => e.At.ToString("HH:mm", CultureInfo.CurrentCulture);
    public string WhenToolTip => e.At.ToString("dddd d MMMM yyyy, HH:mm", CultureInfo.CurrentCulture);
    public string Who => e.Who;
    public string Action => e.Action;
    public string Subject => e.Subject.Replace("\r\n", " ").Replace('\n', ' ');
    public string? Detail => e.Detail?.Replace("\r\n", " ").Replace('\n', ' ');
    public string Where => e.Category != null ? $"{e.Project?.Name} › {e.Category.Name}" : e.Project?.Name ?? e.Group ?? "";

    [RelayCommand] private void Open() => main.Feed.Open(e);
}
