using System.ComponentModel;
using System.Globalization;
using System.Windows.Data;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using ProJict.Core.Features;
using ProJict.Core.Model;
using ProJict.Core.Storage;

namespace ProJict.App.ViewModels;

public sealed partial class ProjectCardViewModel(Project project, ProjectStats stats, int staleDays, MainViewModel main) : ObservableObject
{
    public Project Project { get; } = project;
    public ProjectStats Stats { get; } = stats;
    /// <summary>Its icon, beside its name (null while it has none).</summary>
    public System.Windows.Media.ImageSource? Icon => main.IconOf(Project);
    public bool HasIcon => Icon != null;

    // Time spent and tokens reported (the project's Time tab), for the Table; worked out when first shown.
    private ProjectTime? _time;
    private ProjectTime TimeSpent => _time ??= WorkLog.Of(Project, DateTime.Now);
    public double TimeMinutes => TimeSpent.Total.TotalMinutes;
    public string TimeText => TimeSpent.Total > TimeSpan.Zero ? WorkLog.Format(TimeSpent.Total) : "";
    public long Tokens => TimeSpent.Tokens;
    public string TokensText => Tokens > 0 ? WorkLog.FormatTokens(Tokens) : "";
    public string Name => Project.Name;
    public string Group => string.IsNullOrWhiteSpace(Project.Group) ? "Personal" : Project.Group;
    /// <summary>The group as the sidebar shows it ("Sam's Work" for a shared one), and where it comes there.</summary>
    public string DisplayGroup => ProjectGroups.Of(Project);
    public int GroupOrder => ProjectGroups.Order(Project, main.State.Settings);
    /// <summary>Its place in the order chosen (Sort), which List and Table keep within a group.</summary>
    public int Order { get; set; }
    public ProjectStatus Status => Project.Status;
    /// <summary>Its status can be changed here (on the Board): it's yours, not one shared with you.</summary>
    public bool CanChangeStatus => Project.Remote == null;
    public double ProgressPercent => Stats.Progress * 100;
    public string ProgressText => Stats.Total == 0 ? "no items" : $"{Stats.Done}/{Stats.Total} done ({Stats.Progress:P0})";
    public string OpenText => Stats.Open == 1 ? "1 open" : $"{Stats.Open} open";
    public bool HasInProgress => Stats.InProgress > 0;
    public string InProgressText => $"{Stats.InProgress} in progress";
    public bool HasBugs => Stats.OpenBugs > 0;
    public string BugsText => Stats.OpenBugs == 1 ? "1 bug" : $"{Stats.OpenBugs} bugs";
    public IReadOnlyList<WorkerBadge> Workers => WorkerBadge.For(main.Agents.AgentsIn(Project.Id));
    public bool HasWorkers => main.Agents.AgentsIn(Project.Id).Count > 0;
    public string WorkersToolTip => WorkerBadge.ToolTipFor(main.Agents.AgentsIn(Project.Id));
    public bool HasReview => Stats.ToReview > 0;
    public string ReviewText => $"{Stats.ToReview} to review";
    /// <summary>A ship request in progress: "Ship: waiting for your approval".</summary>
    public bool HasShip => Project.OpenShip != null;
    public bool IsPrivate => Project.Private;
    public string? ShipText => Project.OpenShip is { } s ? "Ship: " + Core.Features.ShipFlow.StateName(s.State) : null;
    public string? ShipToolTip => Project.OpenShip is { } s ? $"Ship request: {Core.Features.ShipFlow.Describe(s.Steps)}" : null;
    public bool IsStale => Project.Status is ProjectStatus.Active && Stats.IsStale(staleDays, DateTime.Now);
    public bool HasGitHubRelease => Project.GitHubRelease?.Tag != null;
    public string? GitHubReleaseTag => Project.GitHubRelease?.Tag;
    public string GitHubReleaseToolTip => Project.GitHubRelease is { Tag: { } tag } info
        ? $"Latest {(info.Prerelease ? "pre-release" : "release")} on GitHub: {tag}" + (info.PublishedAt is { } at ? $", published {at:d MMM yyyy}" : "")
        : "";
    public string LastActivityText => "Last activity " + DashboardViewModel.Ago(Stats.LastActivity);
    public string LastActivityShort => DashboardViewModel.Ago(Stats.LastActivity);
    public string ProgressPercentText => Stats.Total == 0 ? "–" : Stats.Progress.ToString("P0", CultureInfo.CurrentCulture);
    public string? Description => Project.Description;

    [RelayCommand] private void Open() => main.OpenProject(Project);

    [RelayCommand]
    private void Hide()
    {
        main.Dashboard.Filter.SetShown([Project], false);
        main.State.StatusText = $"Hid {Name}. The eye button next to a filter box shows it again.";
    }
}

/// <summary>A column of the dashboard's Board: the projects with one status, which a project dragged there takes on.</summary>
public sealed class BoardColumnViewModel(ProjectStatus status, IReadOnlyList<ProjectCardViewModel> cards)
{
    public ProjectStatus Status => status;
    public string Title => status.ToString();
    public IReadOnlyList<ProjectCardViewModel> Cards => cards;
    public string Count => cards.Count.ToString(CultureInfo.CurrentCulture);
}

/// <summary>A line in the dashboard's side feed; a click opens the item (or project) and, for a comment, its discussion.</summary>
public sealed record RecentDoneRow(string ProjectName, string CategoryName, string Text, string When, Project? Project, TodoItem? Item,
    DiscussionTarget? Discussion = null);

public sealed partial class DashboardViewModel : ObservableObject
{
    public const string AllGroups = "All groups";
    public const string NotArchived = "All but archived";
    public const string AnyStatus = "Any status";

    private readonly AppState _state;
    private readonly MainViewModel _main;

    public DashboardViewModel(AppState state, MainViewModel main)
    {
        _state = state;
        _main = main;
        Filter = new ProjectFilterViewModel(state, state.Settings.HiddenProjects);
        Filter.Changed += Refresh;
        Filter.Accepted += () =>
        {
            if (Cards.FirstOrDefault() is { } first) main.OpenProject(first.Project);
        };
    }

    /// <summary>Search box and show/hide dropdown (same hidden projects as the sidebar); hidden projects also leave the summary and the done feed.</summary>
    public ProjectFilterViewModel Filter { get; }

    public List<ProjectCardViewModel> Cards { get; private set; } = [];

    // ───────────── Views: Cards · List · Table · Board ─────────────

    public static IReadOnlyList<DashboardMode> Modes { get; } = Enum.GetValues<DashboardMode>();

    /// <summary>How projects show, remembered on this computer.</summary>
    public DashboardMode Mode
    {
        get => _state.AppSettings.DashboardMode;
        set
        {
            if (value == _state.AppSettings.DashboardMode) return;
            _state.AppSettings.DashboardMode = value;
            _state.SaveAppSettings();
            foreach (var name in new[] { nameof(Mode), nameof(IsCards), nameof(IsList), nameof(IsTable), nameof(IsBoard), nameof(ShowsGroupToggle) })
                OnPropertyChanged(name);
        }
    }

    // For the switch's buttons: each one checked sets its mode.
    public bool IsCards { get => Mode == DashboardMode.Cards; set { if (value) Mode = DashboardMode.Cards; } }
    public bool IsList { get => Mode == DashboardMode.List; set { if (value) Mode = DashboardMode.List; } }
    public bool IsTable { get => Mode == DashboardMode.Table; set { if (value) Mode = DashboardMode.Table; } }
    public bool IsBoard { get => Mode == DashboardMode.Board; set { if (value) Mode = DashboardMode.Board; } }
    public bool ShowsGroupToggle => Mode is DashboardMode.List or DashboardMode.Table;

    /// <summary>List and Table show projects under their groups, like the sidebar.</summary>
    public bool GroupByGroup
    {
        get => _state.AppSettings.DashboardGroupByGroup;
        set
        {
            if (value == _state.AppSettings.DashboardGroupByGroup) return;
            _state.AppSettings.DashboardGroupByGroup = value;
            _state.SaveAppSettings();
            OnPropertyChanged();
            Group(Rows);
        }
    }

    /// <summary>The side panels folded away, for more room.</summary>
    public bool PanelsFolded
    {
        get => _state.AppSettings.DashboardPanelsFolded;
        set
        {
            if (value == _state.AppSettings.DashboardPanelsFolded) return;
            _state.AppSettings.DashboardPanelsFolded = value;
            _state.SaveAppSettings();
            OnPropertyChanged();
        }
    }

    /// <summary>The panels toggle: checked while they show.</summary>
    public bool ShowPanels
    {
        get => !PanelsFolded;
        set
        {
            PanelsFolded = !value;
            OnPropertyChanged();
        }
    }

    /// <summary>The projects for List and Table: in the chosen order, under their groups when <see cref="GroupByGroup"/>.</summary>
    public ICollectionView Rows { get; private set; } = new ListCollectionView(new List<ProjectCardViewModel>());

    /// <summary>The Board's columns: Idea, Active, Paused and Released, and Archived when the status filter asks for it.</summary>
    public List<BoardColumnViewModel> Board { get; private set; } = [];

    private void Group(ICollectionView view)
    {
        using (view.DeferRefresh())
        {
            view.GroupDescriptions.Clear();
            view.SortDescriptions.Clear();
            if (!GroupByGroup) return;
            view.GroupDescriptions.Add(new PropertyGroupDescription(nameof(ProjectCardViewModel.DisplayGroup)));
            // Groups in the sidebar's order; within one, the order chosen.
            view.SortDescriptions.Add(new SortDescription(nameof(ProjectCardViewModel.GroupOrder), ListSortDirection.Ascending));
            view.SortDescriptions.Add(new SortDescription(nameof(ProjectCardViewModel.Order), ListSortDirection.Ascending));
        }
    }

    /// <summary>A click on a Table column's header: sorted by it (again: the other way), still under the groups.</summary>
    public ListSortDirection SortTable(string member, ListSortDirection? current)
    {
        var direction = current == ListSortDirection.Ascending ? ListSortDirection.Descending : ListSortDirection.Ascending;
        // Numbers and dates make most sense biggest or newest first.
        if (current == null && member != nameof(ProjectCardViewModel.Name) && member != nameof(ProjectCardViewModel.DisplayGroup) &&
            member != nameof(ProjectCardViewModel.Status) && member != nameof(ProjectCardViewModel.GitHubReleaseTag))
            direction = ListSortDirection.Descending;
        using (Rows.DeferRefresh())
        {
            Rows.SortDescriptions.Clear();
            if (GroupByGroup) Rows.SortDescriptions.Add(new SortDescription(nameof(ProjectCardViewModel.GroupOrder), ListSortDirection.Ascending));
            Rows.SortDescriptions.Add(new SortDescription(member, direction));
            Rows.SortDescriptions.Add(new SortDescription(nameof(ProjectCardViewModel.Order), ListSortDirection.Ascending));
        }
        return direction;
    }

    /// <summary>A project shared with you stays where it is on the Board: says why.</summary>
    public void CantMove(ProjectCardViewModel card) =>
        _state.StatusText = $"{card.Name} is shared with you by {card.Project.Remote?.HostName}: only they change its status.";

    /// <summary>A project dragged to another column of the Board takes on that status (one of yours; shared ones keep theirs).</summary>
    public void MoveToStatus(ProjectCardViewModel card, ProjectStatus status)
    {
        if (card.Project.Status == status) return;
        if (!card.CanChangeStatus)
        {
            CantMove(card);
            return;
        }
        _main.GetProjectViewModel(card.Project).Status = status;
        _state.StatusText = $"{card.Name} is {status.ToString().ToLowerInvariant()} now.";
        Refresh();
    }
    public List<RecentDoneRow> RecentDone { get; private set; } = [];
    /// <summary>Items AI agents finished that wait for the user's review, newest first.</summary>
    public List<RecentDoneRow> ToReview { get; private set; } = [];
    public string ToReviewHeader => $"Waiting for your review ({ToReview.Count})";
    /// <summary>What AI agents did in the last 7 days (history and comments), newest first.</summary>
    public List<RecentDoneRow> AgentActivity { get; private set; } = [];
    public List<string> GroupFilters { get; private set; } = [AllGroups];
    public List<string> StatusFilters { get; } = [NotArchived, AnyStatus, .. Enum.GetNames<ProjectStatus>()];
    public List<string> SortOptions { get; } = ["Last activity", "Name", "Open items", "Progress"];

    [ObservableProperty] private string _groupFilter = AllGroups;
    [ObservableProperty] private string _statusFilter = NotArchived;
    [ObservableProperty] private string _sort = "Last activity";
    [ObservableProperty] private string _summary = "";

    partial void OnGroupFilterChanged(string value) => Refresh();
    partial void OnStatusFilterChanged(string value) => Refresh();
    partial void OnSortChanged(string value) => Refresh();

    public void Refresh()
    {
        var projects = _state.Projects.Where(p => !p.IsInbox).ToList();
        Filter.Rebuild(projects);
        GroupFilters = [AllGroups, .. _state.Settings.Groups.Union(projects.Select(p => p.Group).Where(g => !string.IsNullOrWhiteSpace(g)), StringComparer.OrdinalIgnoreCase)];
        OnPropertyChanged(nameof(GroupFilters));
        if (!GroupFilters.Contains(GroupFilter, StringComparer.OrdinalIgnoreCase))
        {
            GroupFilter = AllGroups; // its group was deleted; this refreshes again
            return;
        }

        var shown = projects.Where(p => !Filter.IsHidden(p)).ToList();
        var filtered = shown.Where(p => Filter.MatchesSearch(p) &&
            (GroupFilter == AllGroups || string.Equals(p.Group, GroupFilter, StringComparison.OrdinalIgnoreCase)) &&
            (StatusFilter == AnyStatus || (StatusFilter == NotArchived ? p.Status != ProjectStatus.Archived : MatchesStatus(p))));

        var cards = filtered.Select(p => new ProjectCardViewModel(p, PortfolioStats.For(p), _state.Settings.StaleDays, _main));
        Cards = (Sort switch
        {
            "Name" => cards.OrderBy(c => c.Name, StringComparer.CurrentCultureIgnoreCase),
            "Open items" => cards.OrderByDescending(c => c.Stats.Open),
            "Progress" => cards.OrderByDescending(c => c.Stats.Progress),
            _ => cards.OrderByDescending(c => c.Stats.LastActivity),
        }).ToList();
        for (var i = 0; i < Cards.Count; i++) Cards[i].Order = i;
        OnPropertyChanged(nameof(Cards));
        OnPropertyChanged(nameof(EmptyText));
        var rows = new ListCollectionView(Cards);
        Group(rows);
        Rows = rows;
        OnPropertyChanged(nameof(Rows));
        var statuses = Enum.GetValues<ProjectStatus>().Where(s => s != ProjectStatus.Archived || StatusFilter is AnyStatus or nameof(ProjectStatus.Archived));
        Board = statuses.Select(s => new BoardColumnViewModel(s, Cards.Where(c => c.Status == s).ToList())).ToList();
        OnPropertyChanged(nameof(Board));

        RecentDone = PortfolioStats.RecentlyDone(_state.Projects.Where(p => !Filter.IsHidden(p)), DateTime.Now.AddDays(-7))
            .Take(40)
            .Select(r => new RecentDoneRow(r.Project.Name, r.Category.Name, r.Item.DisplayText().Replace("\n", " "), Ago(r.Item.DoneAt ?? DateTime.Now), r.Project, r.Item))
            .ToList();
        OnPropertyChanged(nameof(RecentDone));

        ToReview = _state.Projects.Where(p => !Filter.IsHidden(p))
            .SelectMany(p => p.Walk().Where(l => l.Item.NeedsReview).Select(l => (Project: p, Loc: l)))
            .OrderByDescending(x => x.Loc.Item.DoneAt)
            .Select(x => new RecentDoneRow(x.Project.Name, x.Loc.Category.Name, x.Loc.Item.DisplayText().Replace("\n", " "),
                Ago(x.Loc.Item.DoneAt ?? DateTime.Now), x.Project, x.Loc.Item))
            .ToList();
        OnPropertyChanged(nameof(ToReview));
        OnPropertyChanged(nameof(ToReviewHeader));
        AgentActivity = BuildAgentActivity(_state.Projects.Where(p => !Filter.IsHidden(p)).ToList(), DateTime.Now.AddDays(-7));
        OnPropertyChanged(nameof(AgentActivity));

        var all = shown.Where(p => p.Status != ProjectStatus.Archived).Select(PortfolioStats.For).ToList();
        Summary = $"{all.Count} projects · {all.Sum(s => s.Open)} open items · {all.Sum(s => s.InProgress)} in progress · {RecentDone.Count} done this week" +
                  (ToReview.Count > 0 ? $" · {ToReview.Count} to review" : "");
    }

    /// <summary>"Released" also takes in projects whose GitHub repository has a published release.</summary>
    private bool MatchesStatus(Project p) =>
        p.Status.ToString() == StatusFilter ||
        (StatusFilter == nameof(ProjectStatus.Released) && p.GitHubRelease?.Tag != null);

    public string EmptyText => StatusFilter == nameof(ProjectStatus.Released)
        ? "Nothing released yet. A project counts as released when its status is Released or its GitHub repository has a published release. "
          + "Enter your GitHub account in Settings › GitHub so ProJict can find the repositories."
        : "No projects to show.";

    private List<RecentDoneRow> BuildAgentActivity(List<Project> projects, DateTime since)
    {
        static string Line(string text) => text.Replace("\r\n", " ").Replace('\n', ' ');
        var entries = new List<(DateTime At, RecentDoneRow Row)>();
        foreach (var project in projects)
        {
            foreach (var loc in project.Walk())
            {
                foreach (var a in (loc.Item.Activity ?? []).Where(a => a.Agent && a.At >= since))
                    entries.Add((a.At, new RecentDoneRow(project.Name, loc.Category.Name, $"{a.By}: {Line(a.Text)}", Ago(a.At), project, loc.Item)));
                foreach (var (c, _) in loc.Item.Comments.Walk().Where(e => e.Comment.Agent && !e.Comment.Deleted && e.Comment.At >= since))
                    entries.Add((c.At, new RecentDoneRow(project.Name, loc.Category.Name, $"{c.By} commented: {Line(c.Text)}", Ago(c.At), project, loc.Item,
                        DiscussionTarget.InProject(project, loc.Item.Id))));
            }
            foreach (var category in project.Categories)
                foreach (var (c, _) in category.Comments.Walk().Where(e => e.Comment.Agent && !e.Comment.Deleted && e.Comment.At >= since))
                    entries.Add((c.At, new RecentDoneRow(project.Name, category.Name, $"{c.By} commented: {Line(c.Text)}", Ago(c.At), project, null,
                        DiscussionTarget.InProject(project, category.Id))));
        }
        foreach (var group in _state.Settings.Groups)
            foreach (var (c, _) in _state.Settings.CommentsFor(group).Walk().Where(e => e.Comment.Agent && !e.Comment.Deleted && e.Comment.At >= since))
                entries.Add((c.At, new RecentDoneRow(group, "group discussion", $"{c.By} commented: {Line(c.Text)}", Ago(c.At), null, null,
                    DiscussionTarget.ForGroup(group))));
        return entries.OrderByDescending(e => e.At).Take(25).Select(e => e.Row).ToList();
    }

    [RelayCommand]
    private void OpenRecent(RecentDoneRow? row)
    {
        if (row == null) return;
        if (row.Project != null)
        {
            var project = _main.OpenProject(row.Project);
            if (row.Item != null) project.Reveal(row.Item);
        }
        if (row.Discussion != null) _main.OpenDiscussion(row.Discussion);
    }

    public static string Ago(DateTime when)
    {
        var span = DateTime.Now - when;
        if (span.TotalMinutes < 1) return "just now";
        if (span.TotalHours < 1) return $"{(int)span.TotalMinutes} min ago";
        if (span.TotalDays < 1) return $"{(int)span.TotalHours} h ago";
        if (span.TotalDays < 2) return "yesterday";
        if (span.TotalDays < 60) return $"{(int)span.TotalDays} days ago";
        return when.ToString("d MMM yyyy", CultureInfo.CurrentCulture);
    }
}
