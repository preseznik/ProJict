using System.Globalization;
using System.Text.Json.Nodes;
using ProJict.Core.Agents;

namespace ProJict.App.Services;

/// <summary>
/// How much of its usage an agent has used (its subscription's limit, or money of a budget): it reports it with
/// report_usage, or the user sets it on its card. The Agents view shows it as a ring by its name; at 90% the agent is
/// told to finish up and hand over.
/// </summary>
public sealed partial class AgentTools
{
    private void AddUsageTools()
    {
        Add("report_usage", "Report usage",
            "Tells ProJict how much of your usage you've used, if you can tell (e.g. from your client's usage screen): the share of your subscription's limit " +
            "(used_percent, with which limit and when it resets), or money spent of a budget (spent, budget). The user sees it as a ring by your name in the " +
            "Agents view; near the limit you're told to finish what you're on and hand over. Report again when it changes much; leave it if you can't tell.",
            Schema(("used_percent", "string", "The share of the limit used, 0 to 100, e.g. 62.", false),
                ("limit", "string", "Which limit, e.g. weekly limit or 5-hour limit.", false),
                ("resets", "string", "When it starts over, e.g. 2026-11-09 09:00.", false),
                ("spent", "string", "Money spent, in dollars, e.g. 14.20.", false),
                ("budget", "string", "Of how much, in dollars, e.g. 50.", false)),
            ReportUsage);
    }

    private static decimal? Amount(Args args, string name) =>
        args.String(name) is { } text
            ? decimal.TryParse(text.Trim().TrimStart('$').TrimEnd('%').Trim().Replace(",", "."), NumberStyles.Number, CultureInfo.InvariantCulture, out var d)
                ? d : throw new McpToolException($"{name} must be a number, e.g. {(name == "used_percent" ? "62" : "14.20")}.")
            : null;

    private string ReportUsage(McpSession session, Args args, List<ProJict.Core.Model.Attachment> files)
    {
        DateTime? resets = null;
        if (args.String("resets") is { Length: > 0 } when)
            resets = DateTime.TryParse(when, CultureInfo.InvariantCulture, DateTimeStyles.AssumeLocal, out var at) || DateTime.TryParse(when, CultureInfo.CurrentCulture, DateTimeStyles.AssumeLocal, out at)
                ? at : throw new McpToolException("resets must be a date and time, e.g. 2026-11-09 09:00.");
        var usage = new AgentUsage
        {
            UsedPercent = Amount(args, "used_percent") is { } percent ? (double)percent : null,
            Limit = args.String("limit")?.Trim() is { Length: > 0 } limit ? limit : null,
            ResetsAt = resets,
            Spent = Amount(args, "spent"),
            Budget = Amount(args, "budget"),
            ReportedBy = session.AgentName,
        };
        if (usage.Problem() is { } problem) throw new McpToolException(problem);
        _state.Settings.SetUsage(session.AgentName, usage);
        _state.MarkSettingsDirty();
        _main.AgentActivity.RefreshUsage();
        var near = usage.Level == UsageLevel.NearlyAll;
        return Json(new JsonObject
        {
            ["usage"] = usage.Describe(DateTime.Now),
            ["note"] = near ? NearLimitAdvice : "Thanks: the user sees it by your name in the Agents view.",
        });
    }

    private const string NearLimitAdvice =
        "You're near your usage limit: finish what you're on, then hand over what's left (in the studio's channel if you're in a studio) or tell the user.";

    /// <summary>For waitingForYou and the watcher: the agent is near its usage limit (once per report, its own or the user's).</summary>
    private IEnumerable<(string Key, string Line)> UsageLines(McpSession session)
    {
        if (_state.Settings.UsageOf(session.AgentName) is not { Level: UsageLevel.NearlyAll } usage || usage.IsStale(DateTime.Now)) yield break;
        yield return ($"usage:{usage.ReportedAt.Ticks}", $"{usage.Describe(DateTime.Now)}. {NearLimitAdvice}");
    }
}
