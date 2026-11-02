namespace ProJict.Core.Agents;

/// <summary>Which connected agents the Agents view gives a card.</summary>
public enum AgentsShown { WorkingNow, ActiveWithin, AllConnected }

public enum AgeUnit { Minutes, Hours, Days }

/// <summary>Which side of the Agents view is folded into a ribbon at the window's edge, if any.</summary>
public enum AgentsFold { None, Page, Office }

/// <summary>
/// What the Agents view shows (its Settings panel), kept in the app settings. Each Claude Code session stays connected
/// until it's closed, so by default only agents that did something in the past hour get a card.
/// </summary>
/// <summary>Classic: the office drawn by ProJict, as it always was. Drawn: pictures of the rooms (made with ComfyUI) behind the desks and robots.</summary>
public enum OfficeLook { Classic, Drawn }

public sealed class AgentsViewSettings
{
    /// <summary>"Working now": called a tool or said what it's doing this recently.</summary>
    public static readonly TimeSpan WorkingNowFor = TimeSpan.FromMinutes(15);

    public AgentsShown Show { get; set; } = AgentsShown.ActiveWithin;
    public int ActiveWithin { get; set; } = 1;
    public AgeUnit ActiveWithinUnit { get; set; } = AgeUnit.Hours;
    /// <summary>How many days back Earlier goes; 0 for all of it.</summary>
    public int EarlierDays { get; set; } = 7;
    /// <summary>The office with the agents at their desks, beside (or above) the cards.</summary>
    public bool ShowOffice { get; set; } = true;
    /// <summary>The list of all agents under the office is open.</summary>
    public bool AgentListOpen { get; set; }
    /// <summary>The project's channel under the office, with the list of all agents beside it.</summary>
    public bool ShowChannel { get; set; }
    /// <summary>How the office and studios look: today's drawing, or the drawn rooms.</summary>
    public OfficeLook OfficeLook { get; set; }
    /// <summary>The project the view showed last (its studio with it); null for all projects. The sidebar opens it there again.</summary>
    public Guid? ProjectId { get; set; }
    /// <summary>How much of the view's width the office side gets beside the page (the divider).</summary>
    public double OfficeShare { get; set; } = DefaultOfficeShare;
    public const double DefaultOfficeShare = 0.42;
    /// <summary>The cards and earlier work, or the office side, folded away into a ribbon.</summary>
    public AgentsFold Fold { get; set; }

    /// <summary>How recently an agent must have done something to get a card; null when every connected one does.</summary>
    public TimeSpan? ActiveSpan => Show switch
    {
        AgentsShown.WorkingNow => WorkingNowFor,
        AgentsShown.ActiveWithin => Span(ActiveWithin, ActiveWithinUnit),
        _ => null,
    };

    public static TimeSpan Span(int count, AgeUnit unit)
    {
        count = Math.Max(1, count);
        return unit switch
        {
            AgeUnit.Minutes => TimeSpan.FromMinutes(count),
            AgeUnit.Hours => TimeSpan.FromHours(count),
            _ => TimeSpan.FromDays(count),
        };
    }

    /// <summary>When the agent last did anything: called a tool, set its status, or connected.</summary>
    public static DateTime LastSeen(McpSession session)
    {
        var last = session.ConnectedAt;
        if (session.LastActive > last) last = session.LastActive.Value;
        if (session.StatusAt > last) last = session.StatusAt.Value;
        return last;
    }

    /// <summary>Whether a connected agent gets a card; one holding checked-out items always does, so you see who has them.</summary>
    public bool Shows(McpSession session, DateTime now, bool holdsItems) =>
        holdsItems || ActiveSpan is not { } span || now - LastSeen(session) < span;

    /// <summary>Where Earlier starts, or null for all of it.</summary>
    public DateTime? EarlierSince(DateTime now) => EarlierDays > 0 ? now.AddDays(-EarlierDays) : null;

    /// <summary>"the past hour", "the past 3 days", "the past 15 minutes".</summary>
    public static string Past(int count, AgeUnit unit) => count <= 1
        ? "the past " + Unit(unit, 1)
        : $"the past {count} {Unit(unit, count)}";

    public static string Unit(AgeUnit unit, int count) => (unit, count == 1) switch
    {
        (AgeUnit.Minutes, true) => "minute",
        (AgeUnit.Minutes, false) => "minutes",
        (AgeUnit.Hours, true) => "hour",
        (AgeUnit.Hours, false) => "hours",
        (_, true) => "day",
        _ => "days",
    };

    /// <summary>The time frame in words: "the past 15 minutes" for Working now, "the past hour", or null for all connected.</summary>
    public string? Within => Show switch
    {
        AgentsShown.WorkingNow => Past((int)WorkingNowFor.TotalMinutes, AgeUnit.Minutes),
        AgentsShown.ActiveWithin => Past(ActiveWithin, ActiveWithinUnit),
        _ => null,
    };
}
