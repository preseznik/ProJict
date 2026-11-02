using System.Globalization;
using System.Windows;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using ProJict.Core.Features;

namespace ProJict.App.ViewModels;

/// <summary>A choice of how a timeline is grouped.</summary>
public sealed record GroupingOption(string Label, ActivityGrouping Grouping)
{
    public static IReadOnlyList<GroupingOption> All { get; } =
        [new("By day", ActivityGrouping.Day), new("By week", ActivityGrouping.Week), new("By month", ActivityGrouping.Month), new("By year", ActivityGrouping.Year)];

    public static GroupingOption For(ActivityGrouping grouping) => All.FirstOrDefault(o => o.Grouping == grouping) ?? All[0];

    public override string ToString() => Label;
}

/// <summary>
/// A timeline split into periods (the Feed, the Agents view's earlier work). It remembers which periods you folded and
/// whose stats you opened while ProJict runs, so rebuilding the timeline (it follows changes live) keeps them.
/// </summary>
public sealed class ActivityTimeline(bool agentsOnly, Action changed)
{
    /// <summary>Periods folded or unfolded against the default (all open, or all folded after Collapse all).</summary>
    private readonly HashSet<(ActivityGrouping, DateTime)> _toggled = [];
    private readonly HashSet<(ActivityGrouping, DateTime)> _statsOpen = [];
    private bool _foldedByDefault;

    public bool AgentsOnly => agentsOnly;

    /// <summary>
    /// Splits events (newest first) into periods, each with its entries made by <paramref name="row"/> (given the day's
    /// name when a day starts inside a week or month). Builds at most <paramref name="limit"/> entries; a folded period
    /// costs one. <paramref name="more"/> says whether anything was left out.
    /// </summary>
    public List<ActivityGroupViewModel> Build(IReadOnlyList<FeedEvent> events, ActivityGrouping grouping, int limit, Func<FeedEvent, string?, object> row,
        string? filterNote, out bool more)
    {
        var groups = new List<ActivityGroupViewModel>();
        var budget = limit;
        more = false;
        foreach (var period in events.GroupBy(e => ActivityPeriods.StartOf(e.At, grouping)))
        {
            if (budget <= 0)
            {
                more = true;
                break;
            }
            var inPeriod = period.ToList();
            var expanded = IsExpanded(grouping, period.Key);
            var rows = new List<object>();
            if (expanded)
            {
                DateTime? day = null;
                foreach (var e in inPeriod.Take(budget))
                {
                    rows.Add(row(e, grouping != ActivityGrouping.Day && day != e.At.Date ? DayName(e.At) : null));
                    day = e.At.Date;
                }
                budget -= rows.Count;
                more |= rows.Count < inPeriod.Count;
            }
            else budget--;
            groups.Add(new ActivityGroupViewModel(this, period.Key, grouping, inPeriod, rows, expanded, _statsOpen.Contains((grouping, period.Key)), filterNote));
        }
        return groups;
    }

    public static string DayName(DateTime at) => ActivityPeriods.Label(at.Date, ActivityGrouping.Day, DateTime.Today, CultureInfo.CurrentCulture);

    private bool IsExpanded(ActivityGrouping grouping, DateTime start) => _foldedByDefault == _toggled.Contains((grouping, start));

    internal void SetExpanded(ActivityGroupViewModel group, bool expanded)
    {
        if (IsExpanded(group.Grouping, group.Start) == expanded) return;
        if (!_toggled.Remove((group.Grouping, group.Start))) _toggled.Add((group.Grouping, group.Start));
        changed();
    }

    internal void SetStatsOpen(ActivityGroupViewModel group, bool open)
    {
        if (open) _statsOpen.Add((group.Grouping, group.Start));
        else _statsOpen.Remove((group.Grouping, group.Start));
    }

    /// <summary>"Collapse all" while any shown period is open, else "Expand all".</summary>
    public static string FoldAllText(IEnumerable<ActivityGroupViewModel> groups) => groups.Any(g => g.IsExpanded) ? "Collapse all" : "Expand all";

    /// <summary>Folds every period (also the ones further down), or opens them all again.</summary>
    public void FoldAll(IEnumerable<ActivityGroupViewModel> groups)
    {
        _foldedByDefault = groups.Any(g => g.IsExpanded);
        _toggled.Clear();
        changed();
    }
}

/// <summary>One period of a timeline: its heading with a count, its entries (they fold) and its stats (opened separately).</summary>
public sealed partial class ActivityGroupViewModel : ObservableObject
{
    private readonly ActivityTimeline _timeline;
    private readonly IReadOnlyList<FeedEvent> _events;
    private readonly string? _filterNote;
    private ActivityStatsViewModel? _stats;

    internal ActivityGroupViewModel(ActivityTimeline timeline, DateTime start, ActivityGrouping grouping, IReadOnlyList<FeedEvent> events,
        IReadOnlyList<object> rows, bool expanded, bool statsOpen, string? filterNote)
    {
        _timeline = timeline;
        _events = events;
        _filterNote = filterNote;
        Start = start;
        Grouping = grouping;
        Rows = rows;
        _isExpanded = expanded;
        _isStatsOpen = statsOpen;
        Label = ActivityPeriods.Label(start, grouping, DateTime.Today, CultureInfo.CurrentCulture);
    }

    public DateTime Start { get; }
    public ActivityGrouping Grouping { get; }
    public string Label { get; }
    public IReadOnlyList<object> Rows { get; }
    public IReadOnlyList<FeedEvent> Events => _events;

    /// <summary>"42 events · 12 by AI agents", or "12 actions" in the Agents view.</summary>
    public string CountText
    {
        get
        {
            if (_timeline.AgentsOnly) return Count(_events.Count, "action");
            var agents = _events.Count(e => e.Agent);
            return Count(_events.Count, "event") + (agents > 0 && agents < _events.Count ? $" · {agents} by AI agents" : agents > 0 ? " · all by AI agents" : "");
        }
    }

    /// <summary>When Show more would add to this period: how much of it shows.</summary>
    public string? CutText => IsExpanded && Rows.Count < _events.Count ? $"{Rows.Count} shown" : null;

    public string ExpandToolTip => IsExpanded ? "Fold this period" : "Show what happened";

    [ObservableProperty] private bool _isExpanded;
    [ObservableProperty] private bool _isStatsOpen;

    /// <summary>Worked out when you open the stats.</summary>
    public ActivityStatsViewModel? Stats => IsStatsOpen
        ? _stats ??= new ActivityStatsViewModel(ActivityStats.Of(_events, Start, Grouping, CultureInfo.CurrentCulture), Start, Grouping, _timeline.AgentsOnly, _filterNote)
        : null;

    partial void OnIsExpandedChanged(bool value) => _timeline.SetExpanded(this, value);

    partial void OnIsStatsOpenChanged(bool value)
    {
        _timeline.SetStatsOpen(this, value);
        OnPropertyChanged(nameof(Stats));
    }

    [RelayCommand] private void ToggleExpanded() => IsExpanded = !IsExpanded;

    internal static string Count(int n, string what) => $"{n} {what}{(n == 1 ? "" : "s")}";
}

/// <summary>A number in a period's stats, with what it counts.</summary>
public sealed record StatTile(string Value, string Label, string? ToolTip = null);

/// <summary>A bar of the activity chart (an hour or a day): people below, agents on top, the rest empty.</summary>
public sealed record ChartBar(GridLength People, GridLength Agents, GridLength Rest, string Label, string ToolTip);

/// <summary>A horizontal bar: a kind, project or person and how much they did, people's share and agents' share.</summary>
public sealed record StatBar(string Label, int Count, GridLength People, GridLength Agents, GridLength Rest, string ToolTip);

/// <summary>A period's stats as the panel shows them: numbers, when it happened, what, where and by whom.</summary>
public sealed class ActivityStatsViewModel
{
    public ActivityStatsViewModel(ActivityStats stats, DateTime start, ActivityGrouping grouping, bool agentsOnly, string? filterNote)
    {
        var culture = CultureInfo.CurrentCulture;
        AgentsOnly = agentsOnly;
        FilterNote = filterNote;
        IsEmpty = stats.Total == 0;
        ChartTitle = grouping switch { ActivityGrouping.Day => "When (by hour)", ActivityGrouping.Year => "When (by month)", _ => "When (by day)" };

        string Split(int people, int agents) => agentsOnly ? ActivityGroupViewModel.Count(agents, "action")
            : people + agents == 0 ? "nothing"
            : string.Join(", ", new[] { people > 0 ? $"{people} by people" : null, agents > 0 ? $"{agents} by AI agents" : null }.OfType<string>());

        var tiles = new List<StatTile>();
        if (agentsOnly)
        {
            tiles.Add(new(stats.Total.ToString(culture), stats.Total == 1 ? "action" : "actions"));
            tiles.Add(new(stats.WhoCount.ToString(culture), stats.WhoCount == 1 ? "agent" : "agents"));
        }
        else
        {
            tiles.Add(new(stats.Total.ToString(culture), stats.Total == 1 ? "event" : "events"));
            tiles.Add(new(stats.ByPeople.ToString(culture), "by people"));
            tiles.Add(new(stats.ByAgents.ToString(culture), "by AI agents"));
        }
        tiles.Add(new(stats.CountOf(FeedKind.Done).ToString(culture), "items finished"));
        if (agentsOnly) tiles.Add(new(stats.CountOf(FeedKind.Comment).ToString(culture), "comments"));
        if (stats.OverTime.MaxBy(s => s.Total) is { Total: > 0 } busiest)
            tiles.Add(grouping switch
            {
                ActivityGrouping.Day => new(busiest.Start.ToString("HH:00", culture), $"busiest hour ({busiest.Total})",
                    $"{busiest.Start:HH:00}–{busiest.Start.AddHours(1):HH:00}: {Split(busiest.People, busiest.Agents)}"),
                ActivityGrouping.Year => new(busiest.Start.ToString("MMM", culture), $"busiest month ({busiest.Total})",
                    $"{busiest.Start.ToString("MMMM yyyy", culture)}: {Split(busiest.People, busiest.Agents)}"),
                _ => new(busiest.Start.ToString("ddd d", culture), $"busiest day ({busiest.Total})", $"{busiest.Start.ToString("dddd d MMMM", culture)}: {Split(busiest.People, busiest.Agents)}"),
            });
        if (grouping == ActivityGrouping.Day && stats.First is { } first && stats.Last is { } last && last - first >= TimeSpan.FromMinutes(1))
            tiles.Add(new($"{first.ToString("HH:mm", culture)}–{last.ToString("HH:mm", culture)}", "first to last"));
        else if (grouping == ActivityGrouping.Year && !IsEmpty)
        {
            // The months so far, for this year.
            var months = start.Year == DateTime.Today.Year ? DateTime.Today.Month : 12;
            var active = stats.OverTime.Count(s => s.Total > 0);
            tiles.Add(new($"{active} of {Math.Max(months, active)}", "months with activity", start.Year == DateTime.Today.Year ? "Months so far" : null));
        }
        else if (grouping != ActivityGrouping.Day && !IsEmpty)
        {
            var end = ActivityPeriods.EndOf(start, grouping);
            var days = (int)((end < DateTime.Today.AddDays(1) ? end : DateTime.Today.AddDays(1)) - start).TotalDays;
            var active = stats.OverTime.Count(s => s.Total > 0);
            tiles.Add(new($"{active} of {Math.Max(days, active)}", "days with activity", end > DateTime.Today ? "Days so far" : null));
        }
        Tiles = tiles;

        var max = Math.Max(1, stats.OverTime.Max(s => s.Total));
        Bars = stats.OverTime.Select((s, i) => new ChartBar(Star(s.People), Star(s.Agents), Star(max - s.Total),
            grouping switch
            {
                ActivityGrouping.Day => i % 3 == 0 ? s.Label : "",
                ActivityGrouping.Week or ActivityGrouping.Year => s.Label,
                _ => s.Start.Day == 1 || s.Start.Day % 5 == 0 ? s.Label : "",
            },
            grouping switch
            {
                ActivityGrouping.Day => $"{s.Start:HH:00}–{s.Start.AddHours(1):HH:00}",
                ActivityGrouping.Year => s.Start.ToString("MMMM yyyy", culture),
                _ => s.Start.ToString("dddd d MMMM", culture),
            } +
            ": " + Split(s.People, s.Agents))).ToList();

        Kinds = Rows(stats.ByKind, c => $"{c.Label}: {Split(c.Count - c.ByAgents, c.ByAgents)}");
        Projects = Rows(stats.ByProject, c => $"{c.Label}: {Split(c.Count - c.ByAgents, c.ByAgents)}");
        Who = Rows(stats.ByWho, c => $"{c.Label} ({(c.Agent ? "AI agent" : "person")}): {(agentsOnly ? Split(0, c.Count) : ActivityGroupViewModel.Count(c.Count, "event"))}");
        MoreProjects = stats.MoreProjects > 0 ? $"and {stats.MoreProjects} more" : null;
        MoreWho = stats.MoreWho > 0 ? $"and {stats.MoreWho} more" : null;
    }

    public bool AgentsOnly { get; }
    public bool IsEmpty { get; }
    /// <summary>Said under the numbers when the Feed's filters leave things out.</summary>
    public string? FilterNote { get; }
    public string ChartTitle { get; }
    public IReadOnlyList<StatTile> Tiles { get; }
    public IReadOnlyList<ChartBar> Bars { get; }
    public IReadOnlyList<StatBar> Kinds { get; }
    public IReadOnlyList<StatBar> Projects { get; }
    public IReadOnlyList<StatBar> Who { get; }
    public string? MoreProjects { get; }
    public string? MoreWho { get; }

    private static GridLength Star(int n) => new(n, GridUnitType.Star);

    private static List<StatBar> Rows(IReadOnlyList<ActivityCount> counts, Func<ActivityCount, string> toolTip)
    {
        var max = Math.Max(1, counts.Count == 0 ? 0 : counts.Max(c => c.Count));
        return counts.Select(c => new StatBar(c.Label, c.Count, Star(c.Count - c.ByAgents), Star(c.ByAgents), Star(max - c.Count), toolTip(c))).ToList();
    }
}
