using System.Text.Json.Serialization;

namespace ProJict.Core.Model;

public enum CleanupState { Requested, Planned, Done, Declined, Withdrawn }

/// <summary>Safe: an agent is sure (build leftovers, duplicates with a copy kept, temp files). Thorough: also doubtful ones.</summary>
public enum CleanupLevel { Safe, Thorough }

/// <summary>A file or folder in the project's repo folder an agent proposes to remove, and why.</summary>
public sealed class CleanupEntry
{
    /// <summary>Relative to the repo folder.</summary>
    public string Path { get; set; } = "";
    public bool Folder { get; set; }
    public long Size { get; set; }
    public CleanupLevel Level { get; set; }
    public string Reason { get; set; } = "";
    /// <summary>Git tracks it (or something in it): only ever in the Thorough list.</summary>
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingDefault)]
    public bool Tracked { get; set; }
    /// <summary>Your tick on the card; unticked lines stay where they are.</summary>
    public bool Checked { get; set; } = true;
    /// <summary>What happened to it: moved to the Recycle Bin, or why not.</summary>
    public string? Result { get; set; }
}

/// <summary>
/// You asked for the project's folder to be cleaned up: an agent scans it and posts what could go, in two levels; you
/// tick and approve one, and ProJict itself moves exactly those to the Recycle Bin. Until it's decided, agents do no
/// other work in the project.
/// </summary>
public sealed class CleanupRequest
{
    public Guid Id { get; set; } = Guid.NewGuid();
    public CleanupState State { get; set; }
    public string RequestedBy { get; set; } = "";
    public DateTime RequestedAt { get; set; } = DateTime.Now;
    public List<CleanupEntry>? Entries { get; set; }
    public string? PlannedBy { get; set; }
    public DateTime? PlannedAt { get; set; }
    public string? DecidedBy { get; set; }
    /// <summary>What you approved: Safe, or everything ticked.</summary>
    public CleanupLevel? Level { get; set; }
    public int Moved { get; set; }
    public long Freed { get; set; }
    public List<ActivityEntry> Log { get; set; } = [];
    public DateTime? FinishedAt { get; set; }
    /// <summary>You closed its card once it was finished.</summary>
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingDefault)]
    public bool Dismissed { get; set; }

    [JsonIgnore] public bool IsOpen => State is CleanupState.Requested or CleanupState.Planned;
}
