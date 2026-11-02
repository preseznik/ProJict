using System.Globalization;
using System.Text.Json.Nodes;
using ProJict.Core.Agents;
using ProJict.Core.Features;
using ProJict.Core.Model;

namespace ProJict.App.Services;

/// <summary>
/// Time and tokens for agents: ProJict counts an agent's time on an item from claim_item until it completes it or puts
/// it back; complete_item and log_work take the tokens and cost it used, and log_work also time spent without a check-out.
/// They add up in the project's Time tab.
/// </summary>
public sealed partial class AgentTools
{
    private void AddWorkTools()
    {
        Add("log_work", "Log work on an item",
            "Records time you spent on an item, and the tokens and cost you used, when ProJict didn't count it (it counts the time from claim_item to " +
            "complete_item itself). Use it for work without a check-out, or to report tokens and cost after the fact. It shows in the project's Time tab.",
            Schema(IdArg, ("time", "string", "How long, e.g. 1h 30m, 45m or 2:15; leave it out to report only tokens and cost.", false),
                ("tokens", "integer", "Tokens you used on it.", false),
                ("cost", "string", "What it cost in dollars, e.g. 1.40.", false),
                ("note", "string", "What the time was for, in a few words.", false)),
            LogWork);
    }

    /// <summary>The tokens and cost an agent reports; nonsense is refused.</summary>
    private static (long? Tokens, decimal? Cost) UsageArgs(Args args)
    {
        long? tokens = null;
        if (args.String("tokens") is { } t)
            tokens = long.TryParse(t.Trim().Replace(",", "").Replace("_", ""), NumberStyles.Integer, CultureInfo.InvariantCulture, out var n) && n >= 0
                ? n : throw new McpToolException("tokens must be a whole number, e.g. 182000.");
        decimal? cost = null;
        if (args.String("cost") is { } c)
            cost = decimal.TryParse(c.Trim().TrimStart('$').Trim(), NumberStyles.Number, CultureInfo.InvariantCulture, out var d) && d >= 0
                ? d : throw new McpToolException("cost must be an amount in dollars, e.g. 1.40.");
        return (tokens, cost);
    }

    private string LogWork(McpSession session, Args args, List<Attachment> files)
    {
        var (project, loc) = FindItem(session, args.Required("id"));
        var (tokens, cost) = UsageArgs(args);
        int? minutes = null;
        if (args.String("time") is { Length: > 0 } time)
            minutes = WorkLog.ParseMinutes(time) is { } m && m <= 24 * 60 ? m : throw new McpToolException("time must be a length up to a day, e.g. 1h 30m, 45m or 2:15.");
        if (minutes == null && tokens is not > 0 && cost is not > 0) throw new McpToolException("Say how long (time), or the tokens or cost you used.");
        MayChange(project, Change.Edit);
        var agent = SignAs(session, project);
        _main.Agents.WorkingIn(session, project);
        Edit(project, loc.Item.Id, i =>
        {
            if (minutes is { } length) WorkLog.Log(i, agent, agent: true, length, tokens, cost, args.String("note"));
            else WorkLog.Report(i, agent, tokens, cost);
        });
        var item = project.Find(loc.Item.Id)!.Item;
        return Json(new JsonObject
        {
            ["id"] = ShortId(item.Id),
            ["logged"] = string.Join(", ", new[]
            {
                minutes is { } mm ? WorkLog.Format(TimeSpan.FromMinutes(mm)) : null,
                tokens is > 0 ? WorkLog.FormatTokens(tokens.Value) + " tokens" : null,
                cost is > 0 ? WorkLog.FormatCost(cost.Value) : null,
            }.Where(x => x != null)),
            ["time"] = TimeText(item),
        });
    }

    /// <summary>"3h 20m (Ada 2h 50m, iztok 30m) · 182k tokens · $1.40"; null for an item no time was spent on.</summary>
    internal static string? TimeText(TodoItem item)
    {
        var spans = WorkLog.SpansOf(item);
        if (spans.Count == 0) return null;
        var now = DateTime.Now;
        var total = spans.Aggregate(TimeSpan.Zero, (sum, s) => sum + s.Length(now));
        var who = spans.GroupBy(s => s.Agent ? AgentNaming.Identity(s.By) : s.By, StringComparer.OrdinalIgnoreCase)
            .Select(g => (Name: g.Key, Time: g.Aggregate(TimeSpan.Zero, (sum, s) => sum + s.Length(now)))).Where(w => w.Time > TimeSpan.Zero).ToList();
        var tokens = spans.Sum(s => s.Tokens ?? 0);
        var cost = spans.Sum(s => s.Cost ?? 0);
        return string.Join(" · ", new[]
        {
            WorkLog.Format(total) + (who.Count > 1 ? $" ({string.Join(", ", who.Select(w => $"{w.Name} {WorkLog.Format(w.Time)}"))})" : who.Count == 1 ? $" ({who[0].Name})" : ""),
            spans.Any(s => s.IsOpen) ? "still going" : null,
            tokens > 0 ? WorkLog.FormatTokens(tokens) + " tokens" : null,
            cost > 0 ? WorkLog.FormatCost(cost) : null,
        }.Where(x => x != null));
    }
}
