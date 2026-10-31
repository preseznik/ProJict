using System.Globalization;

namespace ProJict.Core.Features;

/// <summary>How a timeline (the Feed, the Agents view's history) is grouped.</summary>
public enum ActivityGrouping { Day, Week, Month }

/// <summary>The periods a timeline is grouped in: where one starts and ends, what it's called, and its chart's slots.</summary>
public static class ActivityPeriods
{
    /// <summary>The start of the day, of the week (Monday) or of the month the moment falls in.</summary>
    public static DateTime StartOf(DateTime at, ActivityGrouping grouping) => grouping switch
    {
        ActivityGrouping.Day => at.Date,
        ActivityGrouping.Week => at.Date.AddDays(-(((int)at.DayOfWeek + 6) % 7)),
        _ => new DateTime(at.Year, at.Month, 1),
    };

    public static DateTime EndOf(DateTime start, ActivityGrouping grouping) => grouping switch
    {
        ActivityGrouping.Day => start.AddDays(1),
        ActivityGrouping.Week => start.AddDays(7),
        _ => start.AddMonths(1),
    };

    /// <summary>"Today", "Yesterday", "Wednesday 29 October"; "This week", "Last week", "13 – 19 October"; "This month", "Last month", "August 2026".</summary>
    public static string Label(DateTime start, ActivityGrouping grouping, DateTime today, CultureInfo culture)
    {
        string Year(DateTime d) => d.Year == today.Year ? "" : " " + d.Year.ToString(CultureInfo.InvariantCulture);
        switch (grouping)
        {
            case ActivityGrouping.Day:
                if (start == today.Date) return "Today";
                if (start == today.Date.AddDays(-1)) return "Yesterday";
                return start.ToString("dddd d MMMM", culture) + Year(start);
            case ActivityGrouping.Week:
                var thisWeek = StartOf(today, grouping);
                if (start == thisWeek) return "This week";
                if (start == thisWeek.AddDays(-7)) return "Last week";
                var end = start.AddDays(6);
                return start.Month == end.Month
                    ? $"{start.Day} – {end.ToString("d MMMM", culture)}{Year(end)}"
                    : $"{start.ToString("d MMMM", culture)}{(start.Year != end.Year ? Year(start) : "")} – {end.ToString("d MMMM", culture)}{Year(end)}";
            default:
                var thisMonth = StartOf(today, grouping);
                if (start == thisMonth) return "This month";
                if (start == thisMonth.AddMonths(-1)) return "Last month";
                return start.ToString("MMMM yyyy", culture);
        }
    }

    /// <summary>The bars of a period's activity chart: its hours for a day, its days for a week or month.</summary>
    public static IReadOnlyList<(DateTime Start, DateTime End, string Label)> Slots(DateTime start, ActivityGrouping grouping, CultureInfo culture)
    {
        if (grouping == ActivityGrouping.Day)
            return Enumerable.Range(0, 24).Select(h => (start.AddHours(h), start.AddHours(h + 1), h.ToString("00", CultureInfo.InvariantCulture))).ToList();
        var end = EndOf(start, grouping);
        var slots = new List<(DateTime, DateTime, string)>();
        for (var day = start; day < end; day = day.AddDays(1))
            slots.Add((day, day.AddDays(1), grouping == ActivityGrouping.Week ? day.ToString("ddd", culture) : day.Day.ToString(CultureInfo.InvariantCulture)));
        return slots;
    }
}

/// <summary>A count in a period's stats, split between people and AI agents where that applies.</summary>
public sealed record ActivityCount(string Label, int Count, int ByAgents = 0, bool Agent = false);

/// <summary>A bar of the activity chart: one hour or day, people and agents stacked.</summary>
public sealed record ActivitySlot(DateTime Start, string Label, int People, int Agents)
{
    public int Total => People + Agents;
}

/// <summary>What went on in a period: how much, what kind, when, where and by whom.</summary>
public sealed record ActivityStats(
    int Total,
    int ByPeople,
    int ByAgents,
    IReadOnlyList<ActivityCount> ByKind,
    IReadOnlyList<ActivitySlot> OverTime,
    IReadOnlyList<ActivityCount> ByProject,
    IReadOnlyList<ActivityCount> ByWho,
    int MoreProjects,
    int MoreWho,
    IReadOnlyDictionary<FeedKind, int> Kinds,
    DateTime? First,
    DateTime? Last)
{
    public int CountOf(FeedKind kind) => Kinds.GetValueOrDefault(kind);
    public int WhoCount => ByWho.Count + MoreWho;

    /// <summary>Kinds in the order the Feed's filter lists them, with the names people use.</summary>
    private static readonly (FeedKind Kind, string Label)[] KindNames =
    [
        (FeedKind.Added, "Added"), (FeedKind.Done, "Finished"), (FeedKind.Comment, "Comments"), (FeedKind.History, "Check-outs & reviews"),
        (FeedKind.Release, "Releases"), (FeedKind.Ship, "Shipping"), (FeedKind.Note, "Notes"), (FeedKind.Project, "Projects"), (FeedKind.Api, "API tokens"), (FeedKind.Chat, "Chat channels"),
    ];

    public static ActivityStats Of(IReadOnlyCollection<FeedEvent> events, DateTime start, ActivityGrouping grouping, CultureInfo culture, int top = 5)
    {
        var byKind = KindNames
            .Select(k => (k.Label, Events: events.Where(e => e.Kind == k.Kind).ToList()))
            .Where(k => k.Events.Count > 0)
            .Select(k => new ActivityCount(k.Label, k.Events.Count, k.Events.Count(e => e.Agent)))
            .ToList();
        var overTime = ActivityPeriods.Slots(start, grouping, culture)
            .Select(s =>
            {
                var inSlot = events.Where(e => e.At >= s.Start && e.At < s.End).ToList();
                return new ActivitySlot(s.Start, s.Label, inSlot.Count(e => !e.Agent), inSlot.Count(e => e.Agent));
            })
            .ToList();
        var projects = events.GroupBy(e => e.Project?.Name ?? (e.Group != null ? $"{e.Group} group" : "ProJict"))
            .Select(g => new ActivityCount(g.Key, g.Count(), g.Count(e => e.Agent)))
            .OrderByDescending(c => c.Count).ThenBy(c => c.Label, StringComparer.CurrentCultureIgnoreCase)
            .ToList();
        var who = events.GroupBy(e => e.Who, StringComparer.OrdinalIgnoreCase)
            .Select(g => new ActivityCount(g.First().Who, g.Count(), g.Count(e => e.Agent), g.Any(e => e.Agent)))
            .OrderByDescending(c => c.Count).ThenBy(c => c.Label, StringComparer.CurrentCultureIgnoreCase)
            .ToList();
        return new ActivityStats(events.Count, events.Count(e => !e.Agent), events.Count(e => e.Agent), byKind, overTime,
            projects.Take(top).ToList(), who.Take(top).ToList(), Math.Max(0, projects.Count - top), Math.Max(0, who.Count - top),
            events.GroupBy(e => e.Kind).ToDictionary(g => g.Key, g => g.Count()),
            events.Count > 0 ? events.Min(e => e.At) : null, events.Count > 0 ? events.Max(e => e.At) : null);
    }
}
