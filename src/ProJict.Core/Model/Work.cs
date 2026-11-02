using System.Text.Json.Serialization;

namespace ProJict.Core.Model;

/// <summary>
/// Time someone spent on an item: from when they started it until they stopped (or still going), or how long they logged
/// by hand; and what an agent reported using for it (tokens, cost).
/// </summary>
public sealed class WorkSpan
{
    public string By { get; set; } = "";
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingDefault)]
    public bool Agent { get; set; }
    public DateTime Start { get; set; } = DateTime.Now;
    /// <summary>When they stopped; null while they're still at it.</summary>
    public DateTime? End { get; set; }
    /// <summary>Logged by hand: how long, instead of start to end.</summary>
    public int? Minutes { get; set; }
    /// <summary>Tokens an agent reported using.</summary>
    public long? Tokens { get; set; }
    /// <summary>What it cost, as the agent reported it (in dollars).</summary>
    public decimal? Cost { get; set; }
    public string? Note { get; set; }

    [JsonIgnore] public bool IsOpen => End == null && Minutes == null;

    /// <summary>How long it took (so far, for one still going).</summary>
    public TimeSpan Length(DateTime now) =>
        Minutes is { } minutes ? TimeSpan.FromMinutes(Math.Max(0, minutes))
        : (End ?? now) - Start is { Ticks: > 0 } length ? length : TimeSpan.Zero;

    public WorkSpan Clone() => (WorkSpan)MemberwiseClone();
}
