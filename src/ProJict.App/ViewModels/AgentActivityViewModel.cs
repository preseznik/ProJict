using System.Collections.ObjectModel;
using System.Globalization;
using System.Windows.Media;
using System.Windows.Threading;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using ProJict.Core.Agents;
using ProJict.Core.Chat;
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
        Office.IsDrawnLook = View.OfficeLook == OfficeLook.Drawn;
        Office.PropertyChanged += (_, e) =>
        {
            if (e.PropertyName == nameof(OfficeViewModel.Focused)) SyncFocusedCard();
        };
        state.Chat.Changed += channel =>
        {
            if (!channel.Direct)
            {
                // The channel under the office: its new messages, or one of the project's that turned up.
                if (ShowChannel && (channel == _officeChannel || _officeChannel == null)) RefreshChannel();
                return;
            }
            foreach (var card in Live.Where(c => c.IsFocused)) card.RefreshConversation();
        };
        state.Chat.Removed += channel =>
        {
            if (channel == _officeChannel) RefreshChannel();
        };
        // Agents connected to another ProJict came, went away or left.
        main.Agents.PresenceChanged += VisitorsChanged;
        main.Guest.PresenceChanged += _ => VisitorsChanged();
    }

    // ───────────── Agents in other ProJicts ─────────────

    /// <summary>An agent connected to another ProJict connected to this one: online or away there, and whose ProJict.</summary>
    internal sealed record Visitor(string Name, string From, bool Away);

    /// <summary>
    /// On a host, its guests' agents; on a guest, the host's (and its other guests'), but not this one's own coming back.
    /// Online or away only.
    /// </summary>
    internal IReadOnlyList<Visitor> Visitors()
    {
        var here = _main.Agents.Sessions.Select(s => s.AgentName).ToHashSet(StringComparer.OrdinalIgnoreCase);
        var visitors = _main.Agents.RemoteAgents().Where(a => a.State != PresenceState.Offline && !here.Contains(a.Name))
            .Select(a => new Visitor(a.Name, a.Through, a.State == PresenceState.Away)).ToList();
        foreach (var host in _main.Guest.Connections)
            foreach (var name in host.AgentsThere.Order(StringComparer.CurrentCultureIgnoreCase))
            {
                if (!host.Presence.TryGetValue(name, out var state) || state == PresenceState.Offline || here.Contains(name)) continue;
                if (host.MyName is { } me && name.EndsWith($" (via {me})", StringComparison.OrdinalIgnoreCase)) continue;
                if (visitors.Any(v => string.Equals(v.Name, name, StringComparison.OrdinalIgnoreCase))) continue;
                visitors.Add(new Visitor(name, host.HostName, state == PresenceState.Away));
            }
        return visitors;
    }

    private void VisitorsChanged()
    {
        if (!_clock.Dispatcher.CheckAccess())
        {
            _clock.Dispatcher.BeginInvoke(VisitorsChanged);
            return;
        }
        RefreshAgentList();
        UpdateOffice();
    }

    private AgentsViewSettings View => _state.AppSettings.AgentsView;

    /// <summary>The sidebar's count changed (agents came, went idle, or the settings changed).</summary>
    public event Action? CountChanged;
    /// <summary>A card should come into view (its agent was clicked in the office).</summary>
    public event Action<AgentCardViewModel>? RevealRequested;

    /// <summary>The office beside the cards, with the same agents at their desks.</summary>
    public OfficeViewModel Office { get; } = new();

    /// <summary>The drawn rooms instead of today's office (Settings › Office look).</summary>
    public bool IsDrawnLook
    {
        get => View.OfficeLook == OfficeLook.Drawn;
        set
        {
            if (IsDrawnLook == value) return;
            View.OfficeLook = value ? OfficeLook.Drawn : OfficeLook.Classic;
            _state.SaveAppSettings();
            Office.IsDrawnLook = value;
            OnPropertyChanged();
            OnPropertyChanged(nameof(IsClassicLook));
        }
    }

    public bool IsClassicLook
    {
        get => !IsDrawnLook;
        set
        {
            if (value) IsDrawnLook = false;
        }
    }

    public bool ShowOffice
    {
        get => View.ShowOffice;
        set
        {
            if (View.ShowOffice == value) return;
            View.ShowOffice = value;
            _state.SaveAppSettings();
            OnPropertyChanged();
            UpdateOffice();
        }
    }

    // ───────────── Divider and folding ─────────────

    /// <summary>Which side is folded into a ribbon (only while they stand side by side).</summary>
    public AgentsFold Fold
    {
        get => View.Fold;
        set
        {
            if (View.Fold == value) return;
            View.Fold = value;
            _state.SaveAppSettings();
            OnPropertyChanged();
        }
    }

    /// <summary>The office side's share of the width, as the divider left it.</summary>
    public double OfficeShare
    {
        get => View.OfficeShare;
        set
        {
            value = Math.Clamp(value, 0.15, 0.85);
            if (Math.Abs(View.OfficeShare - value) < 0.001) return;
            View.OfficeShare = value;
            _state.SaveAppSettings();
            OnPropertyChanged();
        }
    }

    /// <summary>The office takes the whole view; the cards and earlier work wait in a ribbon on the left.</summary>
    [RelayCommand] private void FoldPage() => Fold = AgentsFold.Page;
    /// <summary>The cards take the whole view; the office waits in a ribbon on the right.</summary>
    [RelayCommand] private void FoldOffice() => Fold = AgentsFold.Office;
    [RelayCommand] private void Unfold() => Fold = AgentsFold.None;
    /// <summary>Double-click the divider: the usual split again.</summary>
    [RelayCommand] private void ResetDivider() => OfficeShare = AgentsViewSettings.DefaultOfficeShare;

    // ───────────── All agents ─────────────

    /// <summary>Every agent: the ones connected now, then the ones seen here before, most recent first.</summary>
    public ObservableCollection<AgentListRow> AllAgents { get; } = [];
    /// <summary>When agents connected since ProJict started were last seen, for after they leave.</summary>
    private readonly Dictionary<string, DateTime> _seenLive = new(StringComparer.OrdinalIgnoreCase);
    /// <summary>"3 connected · 12 in all".</summary>
    public string AgentListCount { get; private set; } = "";

    public bool IsAgentListOpen
    {
        get => View.AgentListOpen;
        set
        {
            if (View.AgentListOpen == value) return;
            View.AgentListOpen = value;
            _state.SaveAppSettings();
            OnPropertyChanged();
        }
    }

    /// <summary>
    /// Rebuilds the list: the agents connected now, and those that signed work, comments or chat messages here or picked
    /// a name (older numbered and labelled names count as their agent's).
    /// </summary>
    private void RefreshAgentList()
    {
        var registry = _state.Settings.AgentNames ?? [];
        var live = _main.Agents.Sessions.OrderBy(s => s.AgentName, StringComparer.CurrentCultureIgnoreCase).ToList();
        var clients = registry.Values.Concat(live.Select(s => s.Client)).Distinct(StringComparer.OrdinalIgnoreCase).ToList();
        var seen = new Dictionary<string, DateTime>(StringComparer.OrdinalIgnoreCase);
        // Members of channels it never wrote in are known, not seen (being invited isn't being here).
        void Saw(string by, DateTime at)
        {
            var name = AgentNaming.Identity(by);
            foreach (var client in clients) name = AgentNaming.Unnumbered(name, client);
            if (name.Length > 0 && (!seen.TryGetValue(name, out var last) || at > last)) seen[name] = at;
        }
        foreach (var project in _state.Projects.Where(p => p.Remote == null))
        {
            foreach (var loc in project.Walk())
            {
                foreach (var entry in loc.Item.Activity ?? []) if (entry.Agent) Saw(entry.By, entry.At);
                foreach (var (comment, _) in loc.Item.Comments.Walk()) if (comment.Agent) Saw(comment.By, comment.At);
            }
            foreach (var category in project.Categories)
                foreach (var (comment, _) in category.Comments.Walk()) if (comment.Agent) Saw(comment.By, comment.At);
        }
        foreach (var channel in _state.Chat.Channels)
        {
            foreach (var message in channel.Messages) if (message.Agent && !message.Notice) Saw(message.By, message.At);
            foreach (var member in channel.Members) if (member.Agent) Saw(member.Name, DateTime.MinValue);
        }
        foreach (var name in registry.Keys) seen.TryAdd(name, DateTime.MinValue);
        foreach (var session in live) _seenLive[session.AgentName] = AgentsViewSettings.LastSeen(session);
        foreach (var (name, at) in _seenLive) if (!seen.TryGetValue(name, out var last) || at > last) seen[name] = at;
        // A connected agent's earlier names (it picked another, or you renamed it) are the same agent.
        var liveNames = live.SelectMany(s => s.FormerNames.Append(s.AgentName)).ToHashSet(StringComparer.OrdinalIgnoreCase);
        var visitors = Visitors().Where(v => !liveNames.Contains(v.Name)).ToList();
        string? ClientOf(string name) => registry.FirstOrDefault(n => string.Equals(n.Key, name, StringComparison.OrdinalIgnoreCase)).Value;
        // Rows stay as long as their agent does (connected, or not), so the list doesn't flicker and a rename under way stays.
        AgentListRow RowFor(string name, McpSession? session, string? client, DateTime? at)
        {
            var row = AllAgents.FirstOrDefault(r => r.Session == session && string.Equals(r.Name, name, StringComparison.Ordinal));
            if (row == null) return new AgentListRow(this, name, session, client, at);
            row.Seen(client, at);
            return row;
        }
        // Then agents connected to other ProJicts (online, then away), then the rest.
        List<AgentListRow> rows =
        [
            .. live.Select(s => RowFor(s.AgentName, s, s.Client, AgentsViewSettings.LastSeen(s))),
            .. visitors.OrderBy(v => v.Away).Select(v =>
            {
                var row = RowFor(v.Name, null, ClientOf(v.Name), seen.TryGetValue(v.Name, out var at) && at != DateTime.MinValue ? at : null);
                row.Visiting(v.From, v.Away);
                return row;
            }),
            .. seen.Where(n => !liveNames.Contains(n.Key) && !visitors.Any(v => string.Equals(v.Name, n.Key, StringComparison.OrdinalIgnoreCase)))
                .OrderByDescending(n => n.Value).ThenBy(n => n.Key, StringComparer.CurrentCultureIgnoreCase)
                .Select(n => RowFor(n.Key, null, ClientOf(n.Key), n.Value == DateTime.MinValue ? null : n.Value)),
        ];
        foreach (var row in rows.Where(r => r.IsVisitor && !visitors.Any(v => string.Equals(v.Name, r.Name, StringComparison.OrdinalIgnoreCase)))) row.Visiting(null, false);
        for (var i = 0; i < rows.Count; i++)
        {
            if (i < AllAgents.Count && AllAgents[i] == rows[i]) continue;
            var at = AllAgents.IndexOf(rows[i]);
            if (at > i) AllAgents.Move(at, i);
            else AllAgents.Insert(i, rows[i]);
        }
        while (AllAgents.Count > rows.Count) AllAgents.RemoveAt(AllAgents.Count - 1);
        var connected = (live.Count == 1 ? "1 connected" : $"{live.Count} connected") + (visitors.Count > 0 ? $" · {visitors.Count} in other ProJicts" : "");
        AgentListCount = live.Count + visitors.Count == rows.Count ? connected : $"{connected} · {rows.Count} in all";
        TickAgentList(DateTime.Now);
        OnPropertyChanged(nameof(AgentListCount));
    }

    private void TickAgentList(DateTime now)
    {
        foreach (var row in AllAgents)
            if (row.Session is { } session)
                row.Tick(OfficeActivities.Name(OfficeActivities.Of(session, now, session.Claimed.Count > 0 && CheckedOut(session, null).Count > 0, IsWatching(session),
                    IsAsleep)),
                    ProjectNames(session));
    }

    /// <summary>Your channels the agent isn't in yet (not private conversations), to invite it to.</summary>
    internal IReadOnlyList<InviteTarget> InviteTargets(string name) =>
        _state.Chat.Channels.Where(c => !c.Direct && ChatRoom.Member(c, name) == null)
            .OrderBy(c => c.Name, StringComparer.CurrentCultureIgnoreCase)
            .Select(c => new InviteTarget("#" + c.Name, () => InviteTo(name, c))).ToList();

    private void InviteTo(string name, ChatChannel channel)
    {
        try
        {
            _state.Chat.Invite(channel, _state.ChatMe, name, agent: true);
            _state.StatusText = $"Invited {name} to #{channel.Name}.";
        }
        catch (ChatException ex) { _state.StatusText = ex.Message; }
    }

    /// <summary>Renames a connected agent; returns why it can't have the name, or null.</summary>
    internal string? Rename(McpSession session, string name)
    {
        var old = session.AgentName;
        if (_main.AgentTools.RenameAgent(session, name) is { } problem) return problem;
        if (old != session.AgentName) _state.StatusText = $"{old} is now called {session.AgentName}; it's told with its next step.";
        Refresh();
        return null;
    }

    /// <summary>The Feed, showing only what the agent did, all of it.</summary>
    internal void ShowWorkOf(string name)
    {
        _main.ShowFeed();
        _main.Feed.ShowWorkOf(name);
    }

    /// <summary>Its card comes into view, also when it's idle (hidden) or works outside the project shown.</summary>
    internal void ShowCardOf(McpSession session)
    {
        if (Live.All(c => c.Session != session) && !_showIdle)
        {
            _showIdle = true;
            RefreshLive();
        }
        if (Live.All(c => c.Session != session) && ProjectFilter != AllProjects) ProjectFilter = AllProjects;
        if (Live.FirstOrDefault(c => c.Session == session) is { } card) RequestReveal(card);
    }

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
        // Remembered, so the view opens here again (with the project's studio).
        if (View.ProjectId != FilterProject?.Id)
        {
            View.ProjectId = FilterProject?.Id;
            _state.SaveAppSettings();
        }
        _historyLimit = HistoryShown;
        Refresh();
    }

    partial void OnGroupingChanged(GroupingOption value)
    {
        _state.AppSettings.AgentsGrouping = value.Grouping;
        _state.SaveAppSettings();
        // By year, earlier work goes back all the way (a year of a week says little).
        if (value.Grouping == ActivityGrouping.Year && SelectedEarlier.Days != 0) SelectedEarlier = EarlierChoices[^1];
        _historyLimit = HistoryShown;
        BuildHistory();
    }

    /// <summary>Shows the view, for one project or all of them.</summary>
    /// <param name="project">A project to show (right-click a project › Agents); null opens the view as you left it.</param>
    public void Show(Project? project)
    {
        ProjectChoices = [AllProjects, .. _state.Projects.Where(p => p.Remote == null).Select(p => p.Name).Order(StringComparer.CurrentCultureIgnoreCase)];
        OnPropertyChanged(nameof(ProjectChoices));
        // As you left it: the project (and so its studio), and the idle and older agents you showed.
        project ??= _state.Projects.FirstOrDefault(p => p.Id == View.ProjectId && p.Remote == null);
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
        foreach (var name in RoomProperties) OnPropertyChanged(name);
        RefreshChannel();
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
        UpdateOffice();
        SyncFocusedCard();
        RefreshAgentList();
    }

    /// <summary>Puts the agents shown at their desks or at the cooler, by what they do now.</summary>
    private void UpdateOffice()
    {
        if (!View.ShowOffice) return;
        var project = FilterProject;
        var shown = ShownStudio;
        IReadOnlyList<AgentCardViewModel> cards = Live;
        string? elsewhere = null;
        if (project == null)
        {
            // All projects: agents hired in the studio of a project they work in are there, not in the office.
            var inStudios = Live.Where(c => StudioOf(c.Session) != null).ToList();
            cards = [.. Live.Except(inStudios)];
            elsewhere = inStudios.Count == 0 ? null
                : "In studios: " + string.Join("; ", inStudios.GroupBy(c => StudioOf(c.Session)!.Name).Select(g => $"{string.Join(", ", g.Select(c => c.Name))} ({g.Key})"));
        }
        else if (project.Remote == null && StudioFlow.HasOffice(project) && StudioFlow.OpenStudiosOf(project).Any())
        {
            // The office holds the agents not hired in the project's studios; a studio, those hired to it.
            var hired = Live.Where(c => StudioFlow.HiredIn(project, c.Session.AgentName) != null).ToList();
            if (shown == null)
            {
                cards = [.. Live.Except(hired)];
                elsewhere = hired.Count == 0 ? null
                    : "In the studios: " + string.Join("; ", hired.GroupBy(c => StudioFlow.HiredIn(project, c.Session.AgentName)!.Kind)
                        .Select(g => $"{string.Join(", ", g.Select(c => c.Name))} ({StudioFlow.KindName(g.Key)})"));
            }
            else cards = [.. Live.Where(c => StudioFlow.HireOf(shown, c.Session.AgentName) != null)];
        }
        Office.Elsewhere = elsewhere;
        Office.Update(cards, DateTime.Now, s => s.Claimed.Count > 0 && CheckedOut(s, project).Count > 0, IsWatching, LastSaid, Studio, IsAsleep, HireInto(shown));
        Office.SetVisitors(Visitors(), MessagePrivately);
    }

    // ───────────── Everyone to sleep ─────────────

    /// <summary>You put every agent to sleep: they finish what they're doing and stop until you wake them.</summary>
    public bool IsAsleep => _main.AgentTools.IsAsleep;

    public string SleepText => _state.Settings.Sleep is { WokenAt: null } sleep
        ? $"Everyone's asleep since {sleep.At.ToString("HH:mm", CultureInfo.CurrentCulture)}. Agents finish what they were doing and stop: starting new work is " +
          "refused, and Chat and other news wait until you wake them."
        : "";

    /// <summary>The big button: every agent (in the office and the studios) finishes what it's doing and goes to sleep.</summary>
    [RelayCommand]
    private void PutEveryoneToSleep()
    {
        _main.AgentTools.PutToSleep();
        _state.StatusText = "Everyone's going to sleep: agents finish what they're doing and stop. Wake them up from the Agents view.";
        RefreshSleep();
    }

    [RelayCommand]
    private void WakeEveryoneUp()
    {
        _main.AgentTools.WakeUp();
        _state.StatusText = "Everyone's awake again: agents carry on, and what waited for them comes through.";
        RefreshSleep();
    }

    private void RefreshSleep()
    {
        OnPropertyChanged(nameof(IsAsleep));
        OnPropertyChanged(nameof(SleepText));
        UpdateOffice();
        TickAgentList(DateTime.Now);
    }

    /// <summary>The project whose open studio the agent is hired in, among those it works in now (if any).</summary>
    private Project? StudioOf(McpSession session) =>
        _main.Agents.ProjectsOf(session).Select(id => _state.Projects.FirstOrDefault(p => p.Id == id))
            .FirstOrDefault(p => p is { Remote: null } && StudioFlow.HiredIn(p, session.AgentName) != null);

    // ───────────── The project's rooms: its office and studios ─────────────

    /// <summary>The room you last showed of each project (null: its office), while ProJict runs.</summary>
    private readonly Dictionary<Guid, StudioKind?> _roomOf = [];

    /// <summary>The project shown, when it's one of yours that can have studios.</summary>
    private Project? OwnProject => FilterProject is { Remote: null, IsInbox: false } project ? project : null;

    /// <summary>The studio shown as the room, or null for the office: the room you last showed, else the project's first open studio.</summary>
    private Studio? ShownStudio
    {
        get
        {
            if (FilterProject is not { Remote: null } project) return null;
            var open = StudioFlow.OpenStudiosOf(project).ToList();
            if (_roomOf.TryGetValue(project.Id, out var kind))
            {
                if (kind == null && StudioFlow.HasOffice(project)) return null;
                if (open.FirstOrDefault(s => s.Kind == kind) is { } shown) return shown;
            }
            return open.FirstOrDefault();
        }
    }

    /// <summary>One of your projects is shown and has no studio open: set up (or open again) a game or film studio.</summary>
    public bool CanSetUpStudio => OwnProject is { } project && !StudioFlow.OpenStudiosOf(project).Any();

    /// <summary>The room shown is one of the project's studios.</summary>
    public bool ShowsStudio => ShownStudio != null;

    private static string RoomName(StudioKind kind) => kind == StudioKind.Film ? "Film studio" : "Game studio";

    /// <summary>The project's open rooms, as tabs above the room: its office, then its studios.</summary>
    public IReadOnlyList<RoomTab> RoomTabs
    {
        get
        {
            if (FilterProject is not { Remote: null } project) return [];
            var shown = ShownStudio;
            var tabs = new List<RoomTab>();
            if (StudioFlow.HasOffice(project))
                tabs.Add(new RoomTab("Office", shown == null, "Agents not hired in the project's studios work here",
                    new RelayCommand(() => ShowRoom(project, null))));
            foreach (var studio in StudioFlow.OpenStudiosOf(project).OrderBy(s => s.Kind))
                tabs.Add(new RoomTab(RoomName(studio.Kind), studio == shown, $"The {StudioFlow.KindName(studio.Kind)}: a station per role",
                    new RelayCommand(() => ShowRoom(project, studio.Kind))));
            // The room shown can be closed while there's another.
            return tabs.Count < 2 ? tabs : [.. tabs.Select(t => t.IsSelected ? t with { Close = CloseRoomCommand, CloseToolTip = CloseRoomToolTip } : t)];
        }
    }

    /// <summary>The project has more than one room open: they're tabs, and the one shown can be closed.</summary>
    public bool ShowRoomTabs => RoomTabs.Count > 1;

    /// <summary>The rooms the project could open: its office (while closed), and a studio of each kind it hasn't open.</summary>
    public IReadOnlyList<RoomChoice> RoomsToOpen
    {
        get
        {
            if (OwnProject is not { } project) return [];
            var rooms = new List<RoomChoice>();
            if (!StudioFlow.HasOffice(project))
                rooms.Add(new RoomChoice("Office", "Open the office again: agents not hired in the studios work there", OpenRoomCommand, "Office"));
            foreach (var kind in new[] { StudioKind.Game, StudioKind.Film })
            {
                var studio = StudioFlow.StudioOf(project, kind);
                if (studio is { Closed: false }) continue;
                rooms.Add(new RoomChoice(CanSetUpStudio ? kind.ToString() : RoomName(kind),
                    studio != null
                        ? $"Open the {StudioFlow.KindName(kind)} again, with its roles and hires"
                        : kind == StudioKind.Film
                            ? "A film studio for this project: a station per role (director, writer, capture, editor, sound, thumbnails, publisher, producer); you hire agents to them"
                            : "A game studio for this project: a station per role (designers, programmer, artist, sound, QA…); you hire agents to them",
                    OpenRoomCommand, kind.ToString()));
            }
            return rooms;
        }
    }

    /// <summary>"Set up a studio:" while the project has none open, else "Open:".</summary>
    public string OpenRoomsLabel => CanSetUpStudio && OwnProject is { } project && !StudioFlow.StudiosOf(project).Any() ? "Set up a studio:" : "Open:";

    public bool HasRoomsToOpen => RoomsToOpen.Count > 0;

    private void ShowRoom(Project project, StudioKind? kind)
    {
        _roomOf[project.Id] = kind;
        RefreshStudio();
    }

    /// <summary>Set up a studio: "Game" or "Film" (or open it again).</summary>
    [RelayCommand]
    private void SetUpStudio(string? kind) => OpenRoom(kind);

    /// <summary>Opens one of the project's rooms and shows it: "Office", "Game" or "Film".</summary>
    [RelayCommand]
    private void OpenRoom(string? room)
    {
        if (OwnProject is not { } project) return;
        if (room == "Office")
        {
            StudioFlow.SetOfficeOpen(project, true);
            _roomOf[project.Id] = null;
            _state.StatusText = $"{project.Name}'s office is open again: agents not hired in its studios work there.";
        }
        else
        {
            var kind = Enum.TryParse<StudioKind>(room, out var k) ? k : StudioKind.Game;
            var reopened = StudioFlow.StudioOf(project, kind) != null;
            var studio = StudioFlow.OpenStudio(project, kind);
            StudioChannel(project, studio);
            _roomOf[project.Id] = kind;
            _state.StatusText = reopened
                ? $"{project.Name}'s {StudioFlow.KindName(kind)} is open again, with its roles and hires."
                : $"{project.Name} has a {StudioFlow.KindName(kind)}: agents working here " +
                  (StudioFlow.HasOffice(project) ? "stay in the office" : "wait in its lobby") +
                  " until you hire them (right-click a robot › Hire as…). The roles and what they expect are in the project's Details.";
        }
        _state.MarkDirty(project);
        _main.GetProjectViewModel(project).RefreshStudio();
        RefreshStudio();
    }

    public string CloseRoomToolTip => ShownStudio is { } studio
        ? $"Close the {StudioFlow.KindName(studio.Kind)}: its roles and hires are kept for when you open it again"
        : "Close the office: agents not hired in the studios wait in a studio's lobby instead";

    /// <summary>Closes the room shown (while the project has another): a studio keeps its roles and hires.</summary>
    [RelayCommand]
    private void CloseRoom()
    {
        if (FilterProject is not { Remote: null } project || !ShowRoomTabs) return;
        if (ShownStudio is { } studio)
        {
            StudioFlow.CloseStudio(project, studio);
            _state.StatusText = $"Closed {project.Name}'s {StudioFlow.KindName(studio.Kind)}: its roles and hires are kept for when you open it again (Open: above the room).";
        }
        else
        {
            StudioFlow.SetOfficeOpen(project, false);
            _state.StatusText = $"Closed {project.Name}'s office: agents not hired yet wait in a studio's lobby.";
        }
        _roomOf.Remove(project.Id);
        _state.MarkDirty(project);
        _main.GetProjectViewModel(project).RefreshStudio();
        RefreshStudio();
    }

    public string OfficeTitle => ShownStudio is { } studio
        ? $"{FilterProject!.Name} {(studio.Kind == StudioKind.Film ? "film studio" : "studio")}"
        : "Office";

    private StudioContext? Studio => ShownStudio is { } studio ? Context(FilterProject!, studio) : null;

    private StudioContext Context(Project project, Studio studio) =>
        new(project.Name, studio, new RoomHiring(this, project, studio), MilestonesOf(project), StudioFlow.HasOffice(project));

    /// <summary>The project's other open studios, whose roles a robot's menu offers too.</summary>
    private IReadOnlyList<StudioContext> HireInto(Studio? shown) =>
        FilterProject is { Remote: null } project ? [.. StudioFlow.OpenStudiosOf(project).Where(s => s != shown).Select(s => Context(project, s))] : [];

    /// <summary>
    /// The studio's milestone board: all of the project's items done, and its categories with items as milestones, the
    /// unfinished first (in the board's order), five at most.
    /// </summary>
    private MilestoneBoard MilestonesOf(Project project)
    {
        var categories = project.Categories.Select(c => (Category: c, Total: c.Walk().Count(), Done: c.Walk().Count(l => l.Item.IsDone)))
            .Where(x => x.Total > 0).ToList();
        var rows = categories.Where(x => x.Done < x.Total).Concat(categories.Where(x => x.Done == x.Total)).Take(5)
            .Select(x => new MilestoneRow(x.Category.Name, x.Done, x.Total, new RelayCommand(() => _main.OpenProject(project).FocusOn(x.Category)))).ToList();
        return new MilestoneBoard(rows, categories.Sum(x => x.Done), categories.Sum(x => x.Total));
    }

    /// <summary>Hiring, firing and swapping in one of the project's studios, from a robot's menu.</summary>
    private sealed class RoomHiring(AgentActivityViewModel owner, Project project, Studio studio) : IStudioHiring
    {
        public void Hire(string agent, StudioRole role, bool junior = false) => owner.Hire(project, studio, agent, role, junior);
        public void Fire(string agent) => owner.Fire(project, studio, agent);
        public void Swap(string agent, string other) => owner.Swap(project, studio, agent, other);
    }

    private void Hire(Project project, Studio studio, string agent, StudioRole role, bool junior) => ChangeStudio(project, () =>
    {
        var replaced = StudioFlow.Hire(studio, agent, role, _state.UserName, junior);
        // The team talks in the studio's channel: the new hire is invited.
        if (StudioChannel(project, studio) is { } channel)
        {
            try { _state.Chat.Invite(channel, _state.ChatMe, agent, true); }
            catch (ChatException) { }
        }
        var title = StudioFlow.TitleOf(studio, agent) ?? role.Name;
        var where = StudioFlow.OpenStudiosOf(project).Count() > 1 ? $" in the {StudioFlow.KindName(studio.Kind)}" : "";
        return replaced != null
            ? $"Hired {agent} as {title}{where} instead of {replaced}, who's back {BackTo(project)}. {agent} is told what the role expects, and invited to the studio's channel."
            : $"Hired {agent} as {title}{where}{(junior && StudioFlow.HolderOf(studio, role) is { } senior ? $" under {senior}" : "")}. It's told what the role expects, and invited to the studio's channel.";
    });

    /// <summary>Where an agent goes when it leaves its role: the office, or the lobby while the office is closed.</summary>
    private static string BackTo(Project project) => StudioFlow.HasOffice(project) ? "in the office" : "in the lobby";

    /// <summary>The studio's channel (made the first time if the project has none), or null if Chat can't have it.</summary>
    private ChatChannel? StudioChannel(Project project, Studio studio)
    {
        try { return StudioFlow.ChannelFor(project, studio, _state.Chat, _state.ChatMe); }
        catch (ChatException) { return null; }
    }

    private void Fire(Project project, Studio studio, string agent) => ChangeStudio(project, () =>
    {
        var role = StudioFlow.RoleOf(studio, agent)?.Name;
        StudioFlow.Fire(studio, agent, _state.UserName);
        return $"{agent} is no longer the {role}: back {BackTo(project)}.";
    });

    private void Swap(Project project, Studio studio, string agent, string other) => ChangeStudio(project, () =>
    {
        StudioFlow.Swap(studio, agent, other, _state.UserName);
        return $"{agent} and {other} swapped roles; both are told.";
    });

    private void ChangeStudio(Project project, Func<string> change)
    {
        try
        {
            _state.StatusText = change();
        }
        catch (InvalidOperationException ex)
        {
            _state.StatusText = ex.Message;
            return;
        }
        _state.MarkDirty(project);
        _main.GetProjectViewModel(project).RefreshStudio();
        RefreshStudio();
    }

    /// <summary>A studio was set up, opened, closed or removed elsewhere (Details).</summary>
    internal void RefreshStudioView() => RefreshStudio();

    private static readonly string[] RoomProperties =
        [nameof(CanSetUpStudio), nameof(ShowsStudio), nameof(OfficeTitle), nameof(RoomTabs), nameof(ShowRoomTabs), nameof(RoomsToOpen), nameof(HasRoomsToOpen),
         nameof(OpenRoomsLabel), nameof(CloseRoomToolTip)];

    private void RefreshStudio()
    {
        foreach (var name in RoomProperties) OnPropertyChanged(name);
        foreach (var card in Live) card.Refresh();
        UpdateOffice();
        RefreshChannel();
    }

    /// <summary>The agent's role in the room shown (in the office, in any of the project's open studios), for its card.</summary>
    internal string? StudioRoleOf(McpSession session) =>
        FilterProject is not { Remote: null } project ? null
        : (ShownStudio ?? StudioFlow.HiredIn(project, session.AgentName)) is { } studio ? StudioFlow.RoleOf(studio, session.AgentName)?.Name : null;

    /// <summary>The agent's latest message in Chat since it connected (for its speech bubble at the cooler).</summary>
    private string? LastSaid(McpSession session)
    {
        ChatMessage? last = null;
        foreach (var channel in _state.Chat.Channels)
            for (var i = channel.Messages.Count - 1; i >= 0; i--)
            {
                var m = channel.Messages[i];
                if (m.At < session.ConnectedAt || (last != null && m.At <= last.At)) break;
                if (m.Agent && !m.Deleted && !m.Notice && string.Equals(m.By, session.AgentName, StringComparison.OrdinalIgnoreCase))
                {
                    last = m;
                    break;
                }
            }
        return last?.Text;
    }

    internal void RequestReveal(AgentCardViewModel card) => RevealRequested?.Invoke(card);

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
        UpdateOffice();
        TickAgentList(DateTime.Now);
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

    internal void MessagePrivately(McpSession session) => MessagePrivately(session.AgentName);

    // ───────────── Writing to an agent from its card ─────────────

    /// <summary>The agent focused in the office: its card shows your private conversation with it, to write in.</summary>
    private void SyncFocusedCard()
    {
        var focused = Office.Focused?.Session;
        foreach (var card in Live) card.IsFocused = card.Session == focused;
    }

    /// <summary>The last few messages between you and the agent, oldest first.</summary>
    internal IReadOnlyList<PrivateLine> PrivateLinesWith(string agent)
    {
        if (_state.Chat.FindDirect(_state.UserName, agent) is not { } channel) return [];
        return [.. channel.Messages.Where(m => !m.Notice && !m.Deleted).TakeLast(4).Select(m =>
        {
            var mine = string.Equals(m.By, _state.UserName, StringComparison.OrdinalIgnoreCase);
            var text = string.Join(' ', m.Text.Split(['\r', '\n'], StringSplitOptions.RemoveEmptyEntries)).Trim();
            return new PrivateLine(mine ? "You" : m.By, text.Length > 300 ? text[..299].TrimEnd() + "…" : text,
                m.At.ToString(m.At.Date == DateTime.Today ? "HH:mm" : "d MMM HH:mm", CultureInfo.CurrentCulture), mine);
        })];
    }

    /// <summary>Writes to the agent privately (as in Chat, without going there); the problem if it can't.</summary>
    internal string? SendPrivately(string agent, string text)
    {
        try
        {
            var room = _state.Chat;
            var channel = room.FindDirect(_state.UserName, agent) ?? room.OpenDirect(_state.ChatMe, agent, true);
            room.Post(channel, _state.ChatMe, text);
            return null;
        }
        catch (ChatException ex)
        {
            return ex.Message;
        }
    }

    /// <summary>A chat watcher runs for the agent (ProJict.exe --watch-chat, or /chat/watch).</summary>
    internal bool IsWatching(McpSession session) => _main.AgentTools.IsWatched(session.AgentName);

    internal void Say(string text) => _state.StatusText = text;

    internal void MessagePrivately(string agent)
    {
        _main.ShowChannel(null);
        _main.Chat.OpenDirect(agent, agent: true);
    }

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
            return $"{s.FullName}: {doing}";
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

    /// <summary>Right-click the card › Private message, or its chat icon: your conversation with it in Chat.</summary>
    [RelayCommand] private void PrivateMessage() => _owner.MessagePrivately(Session);
    public string OpenChatToolTip => $"Open your private conversation with {Name} in Chat";

    /// <summary>Its robot is focused in the office: the card shows your private conversation with it, with a box to write in.</summary>
    [ObservableProperty] private bool _isFocused;
    partial void OnIsFocusedChanged(bool value)
    {
        if (value) RefreshConversation();
    }

    public IReadOnlyList<PrivateLine> Conversation { get; private set; } = [];
    public bool HasConversation => Conversation.Count > 0;
    public string ConversationTitle => $"You and {Name}, privately";

    public void RefreshConversation()
    {
        Conversation = _owner.PrivateLinesWith(Name);
        OnPropertyChanged(nameof(Conversation));
        OnPropertyChanged(nameof(HasConversation));
    }

    [ObservableProperty] [NotifyCanExecuteChangedFor(nameof(SendCommand))] private string _draft = "";

    private bool CanSend() => Draft.Trim().Length > 0;

    /// <summary>The card's Send (or Enter): to the agent privately, staying in the office.</summary>
    [RelayCommand(CanExecute = nameof(CanSend))]
    private void Send()
    {
        if (_owner.SendPrivately(Name, Draft.Trim()) is { } problem)
        {
            _owner.Say(problem);
            return;
        }
        Draft = "";
        RefreshConversation();
    }

    /// <summary>Its agent was clicked in the office: the view scrolls to the card and flashes it.</summary>
    internal void RequestReveal() => _owner.RequestReveal(this);
    [ObservableProperty] private bool _isFlashing;
    public bool HasRecent => Recent.Count > 0;

    /// <summary>Called or set its status in the last half minute: the dots move.</summary>
    public bool IsBusy { get; private set; }
    public string StatusText { get; private set; } = "";
    public string StatusAge { get; private set; } = "";
    /// <summary>It hears Chat right away: a chat watcher runs for it, or it's waiting for messages.</summary>
    public bool HearsChat { get; private set; }
    public string HearsChatToolTip { get; private set; } = "";

    public void Refresh()
    {
        var names = _owner.ProjectNames(Session);
        var since = Session.ConnectedAt.ToString(Session.ConnectedAt.Date == DateTime.Today ? "HH:mm" : "d MMM HH:mm", CultureInfo.CurrentCulture);
        // "Claude Code · login fix · In GRID2VR · since 22:41": what it runs in (when it has a name of its own) and what it works on.
        var about = new[] { _owner.StudioRoleOf(Session), Session.Name != null ? Session.Client : null, Session.Label, _owner.IsWatching(Session) ? "watching Chat" : null }
            .Where(s => s != null);
        Where = string.Join(" · ", about.Append((names.Count == 0 ? "Connected" : "In " + string.Join(", ", names)) + " · since " + since));
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
            : last != null ? (last.Times > 1 ? $"{last.Text} ×{last.Times}" : last.Text, last.At)
            : ("Connected, nothing done yet", Session.ConnectedAt);
        IsBusy = now - (Session.LastActive ?? Session.ConnectedAt) < AgentActivityViewModel.BusyFor ||
                 (statusFresh && now - Session.StatusAt!.Value < AgentActivityViewModel.BusyFor);
        StatusAge = IsBusy ? Age(at, now) : $"idle · {Age(at, now)}";
        var watching = _owner.IsWatching(Session);
        HearsChat = watching || Session.Listening;
        HearsChatToolTip = watching
            ? "Listening: a chat watcher runs for it, so it hears your messages in Chat and answers right away, even while it's idle. " +
              "Agents without one only see them with their next step."
            : "Listening: it's waiting for messages in Chat, so it answers as soon as you write.";
        OnPropertyChanged(nameof(HearsChat));
        OnPropertyChanged(nameof(HearsChatToolTip));
        OnPropertyChanged(nameof(StatusText));
        OnPropertyChanged(nameof(StatusAge));
        OnPropertyChanged(nameof(IsBusy));
        foreach (var row in Recent) row.Tick(now);
    }

    private static string Age(DateTime at, DateTime now) => AgentActivityViewModel.Age(at, now);
}

/// <summary>A line of your private conversation with an agent, on its card.</summary>
public sealed record PrivateLine(string Who, string Text, string Time, bool Mine);

/// <summary>
/// One of an agent's latest actions; the newest stands out, the rest are quieter. Done several times in a row, it says
/// how often ("×6") on one line, aged by the last time.
/// </summary>
public sealed partial class AgentActionRow(AgentAction action, bool isLatest, AgentActivityViewModel owner) : ObservableObject
{
    public string Text => action.Text;
    public bool IsLatest => isLatest;
    public string? TimesText => action.Times > 1 ? $"×{action.Times}" : null;
    public string Project => owner.ProjectName(action.ProjectId);
    public string Age { get; private set; } = AgentActivityViewModel.Age(action.At, DateTime.Now);
    public string Time => action.Times > 1 && action.Since is { } since
        ? $"{action.Times} times, {since.ToString("HH:mm:ss", CultureInfo.CurrentCulture)} to {action.At.ToString("HH:mm:ss", CultureInfo.CurrentCulture)}"
        : action.At.ToString("HH:mm:ss", CultureInfo.CurrentCulture);

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

/// <summary>One of a project's open rooms, as a tab above the room in the Agents view; the one shown can be closed from it.</summary>
public sealed record RoomTab(string Name, bool IsSelected, string ToolTip, System.Windows.Input.ICommand Select)
{
    public System.Windows.Input.ICommand? Close { get; init; }
    public string? CloseToolTip { get; init; }
    public bool CanClose => Close != null;
}

/// <summary>A room the project could open (its office, or a studio), with the command and its parameter ("Office", "Game", "Film").</summary>
public sealed record RoomChoice(string Name, string ToolTip, System.Windows.Input.ICommand Open, string Room);
