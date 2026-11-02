using System.Globalization;
using ProJict.Core.Agents;
using ProJict.Core.Model;

namespace ProJict.Core.Features;

/// <summary>One person's or agent's time in a project: items they finished, how long, and what they reported using.</summary>
public sealed record WorkerTime(string Name, bool Agent, int ItemsDone, TimeSpan Time, long Tokens, decimal Cost);

/// <summary>An item's time, tokens and cost, all who worked on it together.</summary>
public sealed record ItemTime(TodoItem Item, string Category, TimeSpan Time, long Tokens, decimal Cost);

/// <summary>A week (from Monday): the time people and agents spent.</summary>
public sealed record WeekTime(DateTime Start, TimeSpan People, TimeSpan Agents);

/// <summary>A project's time and tokens: in all, this week, week by week, per person and agent, and the longest items.</summary>
public sealed record ProjectTime(TimeSpan Total, TimeSpan ThisWeek, long Tokens, decimal Cost, IReadOnlyList<WeekTime> Weeks,
    IReadOnlyList<WorkerTime> Workers, IReadOnlyList<ItemTime> Longest);

/// <summary>
/// Time spent on items. ProJict counts it while an item is in progress, for whoever put it there (a person, or an agent
/// checking it out), and people can log time by hand; agents report tokens and cost. For items worked on before ProJict
/// kept time, the agents' check-outs and finishes in the item's history stand in.
/// </summary>
public static class WorkLog
{
    /// <summary>The time spans an item has: its own, else the ones its history shows.</summary>
    public static IReadOnlyList<WorkSpan> SpansOf(TodoItem item) => item.Work is { Count: > 0 } work ? work : FromHistory(item);

    /// <summary>
    /// An agent's "Checked it out" until it finished, put it back, took it over or was sent back or released, from an
    /// item's history (for items from before ProJict kept time).
    /// </summary>
    public static List<WorkSpan> FromHistory(TodoItem item)
    {
        var spans = new List<WorkSpan>();
        WorkSpan? open = null;
        foreach (var entry in item.Activity ?? [])
        {
            var text = entry.Text;
            var start = entry.Agent && text.StartsWith("Checked it out", StringComparison.Ordinal);
            var end = text.StartsWith("Done:", StringComparison.Ordinal) || text is "Marked it done." or "Put it back to do." ||
                      text.StartsWith(ActivityEntry.SentBack, StringComparison.Ordinal) || text.StartsWith("Released ", StringComparison.Ordinal) ||
                      text.StartsWith("Took it over", StringComparison.Ordinal);
            if (open != null && (start || end))
            {
                open.End = entry.At;
                spans.Add(open);
                open = null;
            }
            if (start) open = new WorkSpan { By = AgentNaming.Identity(entry.By), Agent = true, Start = entry.At };
        }
        if (open != null)
        {
            // Still at it while in progress; otherwise it ended when the item did (or there's nothing to count).
            if (item.State != ItemState.InProgress) open.End = item.DoneAt is { } done && done > open.Start ? done : open.Start;
            spans.Add(open);
        }
        return spans;
    }

    /// <summary>Logs time by hand (and what was used, for an agent): a span of that many minutes, ending now.</summary>
    public static WorkSpan Log(TodoItem item, string by, bool agent, int minutes, long? tokens = null, decimal? cost = null, string? note = null, DateTime? now = null)
    {
        var at = now ?? DateTime.Now;
        var span = new WorkSpan
        {
            By = by, Agent = agent, Start = at.AddMinutes(-Math.Max(0, minutes)), End = null, Minutes = Math.Max(0, minutes),
            Tokens = tokens is > 0 ? tokens : null, Cost = cost is > 0 ? cost : null, Note = string.IsNullOrWhiteSpace(note) ? null : note.Trim(),
        };
        (item.Work ??= []).Add(span);
        return span;
    }

    /// <summary>
    /// What an agent reports using: added to its latest span on the item (the one going on, or the last), or a span of
    /// its own when it has none there.
    /// </summary>
    public static void Report(TodoItem item, string by, long? tokens, decimal? cost, DateTime? now = null)
    {
        if (tokens is not > 0 && cost is not > 0) return;
        var span = item.Work?.LastOrDefault(w => w.IsOpen && Same(w.By, by)) ?? item.Work?.LastOrDefault(w => Same(w.By, by));
        if (span == null)
        {
            Log(item, by, agent: true, minutes: 0, tokens, cost, now: now);
            return;
        }
        if (tokens is > 0) span.Tokens = (span.Tokens ?? 0) + tokens;
        if (cost is > 0) span.Cost = (span.Cost ?? 0) + cost;
    }

    private static bool Same(string a, string b) => string.Equals(a.Trim(), b.Trim(), StringComparison.OrdinalIgnoreCase);

    /// <summary>Monday of the week the time falls in.</summary>
    public static DateTime WeekOf(DateTime at) => at.Date.AddDays(-(((int)at.DayOfWeek + 6) % 7));

    /// <summary>The project's time and tokens, the <paramref name="weeks"/> last weeks one by one.</summary>
    public static ProjectTime Of(Project project, DateTime now, int weeks = 12)
    {
        var thisWeek = WeekOf(now);
        var weekly = Enumerable.Range(0, weeks).Select(i => thisWeek.AddDays(-7 * (weeks - 1 - i))).ToDictionary(w => w, _ => (People: TimeSpan.Zero, Agents: TimeSpan.Zero));
        var workers = new Dictionary<string, (bool Agent, HashSet<Guid> Done, TimeSpan Time, long Tokens, decimal Cost)>(StringComparer.OrdinalIgnoreCase);
        var items = new List<ItemTime>();
        TimeSpan total = TimeSpan.Zero, week = TimeSpan.Zero;
        long tokens = 0;
        decimal cost = 0;
        foreach (var loc in project.Walk())
        {
            var item = loc.Item;
            var spans = SpansOf(item);
            if (spans.Count == 0) continue;
            TimeSpan itemTime = TimeSpan.Zero;
            long itemTokens = 0;
            decimal itemCost = 0;
            foreach (var span in spans)
            {
                var length = span.Length(now);
                itemTime += length;
                itemTokens += span.Tokens ?? 0;
                itemCost += span.Cost ?? 0;
                var start = WeekOf(span.Start);
                if (weekly.TryGetValue(start, out var w)) weekly[start] = span.Agent ? (w.People, w.Agents + length) : (w.People + length, w.Agents);
                if (start == thisWeek) week += length;
                var name = span.Agent ? AgentNaming.Identity(span.By) : span.By;
                var was = workers.TryGetValue(name, out var x) ? x : (Agent: span.Agent, Done: new HashSet<Guid>(), Time: TimeSpan.Zero, Tokens: 0L, Cost: 0m);
                if (item.IsDone) was.Done.Add(item.Id);
                workers[name] = (was.Agent || span.Agent, was.Done, was.Time + length, was.Tokens + (span.Tokens ?? 0), was.Cost + (span.Cost ?? 0));
            }
            total += itemTime;
            tokens += itemTokens;
            cost += itemCost;
            items.Add(new ItemTime(item, loc.Category.Name, itemTime, itemTokens, itemCost));
        }
        return new ProjectTime(total, week, tokens, cost,
            weekly.OrderBy(w => w.Key).Select(w => new WeekTime(w.Key, w.Value.People, w.Value.Agents)).ToList(),
            workers.Select(w => new WorkerTime(w.Key, w.Value.Agent, w.Value.Done.Count, w.Value.Time, w.Value.Tokens, w.Value.Cost))
                .OrderByDescending(w => w.Time).ThenBy(w => w.Name, StringComparer.CurrentCultureIgnoreCase).ToList(),
            items.Where(i => i.Time > TimeSpan.Zero).OrderByDescending(i => i.Time).Take(10).ToList());
    }

    /// <summary>"45m", "3h 20m", "2d 4h"; "0m" for nothing.</summary>
    public static string Format(TimeSpan time)
    {
        var minutes = (long)Math.Round(time.TotalMinutes);
        if (minutes < 60) return $"{minutes}m";
        var hours = minutes / 60;
        if (hours < 24) return minutes % 60 == 0 ? $"{hours}h" : $"{hours}h {minutes % 60}m";
        return hours % 24 == 0 ? $"{hours / 24}d" : $"{hours / 24}d {hours % 24}h";
    }

    /// <summary>"182k", "1.2M", "950".</summary>
    public static string FormatTokens(long tokens) =>
        tokens >= 1_000_000 ? (tokens / 1_000_000.0).ToString("0.#", CultureInfo.InvariantCulture) + "M"
        : tokens >= 1_000 ? (tokens / 1_000.0).ToString("0", CultureInfo.InvariantCulture) + "k"
        : tokens.ToString(CultureInfo.InvariantCulture);

    /// <summary>"$1.40".</summary>
    public static string FormatCost(decimal cost) => "$" + cost.ToString("0.00", CultureInfo.InvariantCulture);

    /// <summary>Minutes from what people type: "1h 30m", "90m", "90", "1.5h", "2:30", "1d 2h"; null when it isn't a time.</summary>
    public static int? ParseMinutes(string? text)
    {
        text = text?.Trim().ToLowerInvariant().Replace(",", ".");
        if (string.IsNullOrEmpty(text)) return null;
        if (text.Split(':') is [var h, var m] && int.TryParse(h, out var hh) && int.TryParse(m, out var mm) && mm is >= 0 and < 60 && hh >= 0)
            return hh * 60 + mm;
        if (double.TryParse(text, NumberStyles.Float, CultureInfo.InvariantCulture, out var bare)) return bare >= 0 ? (int)Math.Round(bare) : null;
        double total = 0;
        var any = false;
        foreach (var part in System.Text.RegularExpressions.Regex.Matches(text, @"(\d+(?:\.\d+)?)\s*(d|h|m|min|mins|hours?|days?)\b").Cast<System.Text.RegularExpressions.Match>())
        {
            var value = double.Parse(part.Groups[1].Value, CultureInfo.InvariantCulture);
            total += part.Groups[2].Value[0] switch { 'd' => value * 24 * 60, 'h' => value * 60, _ => value };
            any = true;
        }
        var leftover = System.Text.RegularExpressions.Regex.Replace(text, @"(\d+(?:\.\d+)?)\s*(d|h|m|min|mins|hours?|days?)\b", "").Trim();
        return any && leftover.Length == 0 ? (int)Math.Round(total) : null;
    }
}
