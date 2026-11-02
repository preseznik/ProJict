using System.Globalization;
using System.Text.Json.Serialization;
using System.Text.RegularExpressions;

namespace ProJict.Core.Agents;

/// <summary>How much of its usage an agent has used: a little, half, most (amber), nearly all (red).</summary>
public enum UsageLevel { Low, Half, Most, NearlyAll }

/// <summary>
/// How much of its usage an agent has used, as it reported it (or you set it): a share of a subscription's limit ("62%
/// of the weekly limit, resets Monday 09:00"), or money spent of a budget ("$14 of $50"). ProJict can't read either
/// itself. Kept per agent name.
/// </summary>
public sealed partial class AgentUsage
{
    /// <summary>A report older than this is shown faded: it may be out of date.</summary>
    public static readonly TimeSpan FreshFor = TimeSpan.FromDays(1);

    /// <summary>The share of the limit used, 0 to 100.</summary>
    public double? UsedPercent { get; set; }
    /// <summary>What the share is of: "weekly limit", "5-hour limit".</summary>
    public string? Limit { get; set; }
    public decimal? Spent { get; set; }
    public decimal? Budget { get; set; }
    /// <summary>When the limit starts over, if it said.</summary>
    public DateTime? ResetsAt { get; set; }
    public DateTime ReportedAt { get; set; } = DateTime.Now;
    /// <summary>Who said it: the agent, or you.</summary>
    public string ReportedBy { get; set; } = "";

    /// <summary>How much is used, 0 to 1; null when it can't tell (money spent without a budget).</summary>
    [JsonIgnore]
    public double? Fraction => UsedPercent is { } percent ? Math.Clamp(percent / 100, 0, 1)
        : Spent is { } spent && Budget is > 0 and var budget ? (double)Math.Clamp(spent / budget, 0, 1) : null;

    [JsonIgnore]
    public UsageLevel Level => Fraction switch
    {
        >= 0.9 => UsageLevel.NearlyAll,
        >= 0.75 => UsageLevel.Most,
        >= 0.5 => UsageLevel.Half,
        _ => UsageLevel.Low,
    };

    public bool IsStale(DateTime now) => now - ReportedAt > FreshFor;

    /// <summary>"62% of the weekly limit used · resets Mon 09:00 · reported 10 min ago (by Ada)".</summary>
    public string Describe(DateTime now)
    {
        var used = UsedPercent is { } percent ? $"{percent.ToString("0.#", CultureInfo.CurrentCulture)}% of the {Limit ?? "limit"} used"
            : Spent is { } spent ? Budget is { } budget ? $"{Money(spent)} of {Money(budget)} spent" : $"{Money(spent)} spent"
            : "Usage not known";
        var resets = ResetsAt is { } at && at > now ? $"resets {at.ToString(at - now < TimeSpan.FromDays(6) ? "ddd HH:mm" : "d MMM HH:mm", CultureInfo.CurrentCulture)}" : null;
        var age = now - ReportedAt;
        var when = age < TimeSpan.FromMinutes(1) ? "reported just now" : age < TimeSpan.FromHours(1) ? $"reported {(int)age.TotalMinutes} min ago"
            : age < TimeSpan.FromDays(1) ? $"reported {(int)age.TotalHours} h ago" : $"reported {ReportedAt.ToString("d MMM", CultureInfo.CurrentCulture)}, may be out of date";
        return string.Join(" · ", new[] { used, resets, $"{when} (by {ReportedBy})" }.Where(s => s != null));
    }

    private static string Money(decimal amount) => "$" + amount.ToString(amount == Math.Round(amount) ? "0" : "0.00", CultureInfo.InvariantCulture);

    /// <summary>Why a report can't be taken, in words for whoever made it; null when it can.</summary>
    public string? Problem()
    {
        if (UsedPercent is { } percent && percent is < 0 or > 100) return "The share used goes from 0 to 100 (%).";
        if (Spent is < 0 || Budget is < 0) return "Amounts can't be below zero.";
        if (Budget is 0) return "A budget of nothing can't be used up; leave it out.";
        if (UsedPercent == null && Spent == null) return "Say the share of the limit used (e.g. 62), or what was spent (and of what budget).";
        return null;
    }

    [GeneratedRegex(@"^\s*(?<p>\d+(?:[.,]\d+)?)\s*%\s*(?:(?:used\s*)?(?:of\s+)?(?:the\s+)?(?<limit>.+?))?\s*$", RegexOptions.IgnoreCase)]
    private static partial Regex PercentText();

    [GeneratedRegex(@"^\s*\$?\s*(?<s>\d+(?:[.,]\d+)?)\s*(?:(?:of|/)\s*\$?\s*(?<b>\d+(?:[.,]\d+)?))?\s*\$?\s*(?:spent)?\s*$", RegexOptions.IgnoreCase)]
    private static partial Regex MoneyText();

    private static decimal ParseAmount(string text) => decimal.Parse(text.Replace(',', '.'), NumberStyles.Number, CultureInfo.InvariantCulture);

    /// <summary>
    /// What you type for an agent: "62%", "62% of the weekly limit", "$14 of $50", "14/50"; null when it isn't one.
    /// </summary>
    public static AgentUsage? Parse(string text, string by, DateTime now)
    {
        if (PercentText().Match(text) is { Success: true } p)
        {
            var limit = p.Groups["limit"].Value.Trim().TrimEnd('.').Replace(" used", "", StringComparison.OrdinalIgnoreCase).Trim();
            var usage = new AgentUsage
            {
                UsedPercent = (double)ParseAmount(p.Groups["p"].Value), Limit = limit.Length > 0 ? limit : null, ReportedAt = now, ReportedBy = by,
            };
            return usage.Problem() == null ? usage : null;
        }
        if (MoneyText().Match(text) is { Success: true } m && (text.Contains('$') || m.Groups["b"].Success))
        {
            var usage = new AgentUsage
            {
                Spent = ParseAmount(m.Groups["s"].Value), Budget = m.Groups["b"].Success ? ParseAmount(m.Groups["b"].Value) : null, ReportedAt = now, ReportedBy = by,
            };
            return usage.Problem() == null ? usage : null;
        }
        return null;
    }
}
