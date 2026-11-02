using System.Text.Json.Serialization;

namespace ProJict.Core.Model;

/// <summary>Planned: waits for your decision. Approved: the agent may publish it. The rest are closed.</summary>
public enum PublishState { Planned, Approved, Published, Failed, Declined, Withdrawn }

/// <summary>
/// A video's publish plan, posted by an AI agent (a film studio's Publisher): its title, description, tags, playlists,
/// when it goes out, and thumbnails to choose from. You pick a thumbnail, may edit the text, and approve or decline it;
/// only then does the agent publish it (or hand you a package to upload yourself). See PublishFlow.
/// </summary>
public sealed class PublishPlan
{
    public Guid Id { get; set; } = Guid.NewGuid();
    /// <summary>The video's item, when it has one.</summary>
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public Guid? ItemId { get; set; }
    /// <summary>Where it's published: YouTube, unless the agent says otherwise.</summary>
    public string Platform { get; set; } = "YouTube";
    public string Title { get; set; } = "";
    public string Description { get; set; } = "";
    public List<string> Tags { get; set; } = [];
    public List<string> Playlists { get; set; } = [];
    /// <summary>When it goes out, in words: "now", "2026-11-06 18:00", "Friday evening".</summary>
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public string? When { get; set; }
    /// <summary>The agent's notes for you: what it still needs, what it'll do.</summary>
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public string? Note { get; set; }
    public List<Attachment> Thumbnails { get; set; } = [];
    /// <summary>The thumbnail you picked (its index); null until you pick one.</summary>
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public int? Chosen { get; set; }
    public PublishState State { get; set; }
    public string PlannedBy { get; set; } = "";
    public DateTime PlannedAt { get; set; } = DateTime.Now;
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public string? DecidedBy { get; set; }
    /// <summary>You changed the text before deciding: the agent publishes yours.</summary>
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingDefault)]
    public bool EditedByYou { get; set; }
    /// <summary>Where it's out, once it is.</summary>
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public string? Url { get; set; }
    /// <summary>What happened, signed: planned, picked, edited, decided, published.</summary>
    public List<ActivityEntry> Log { get; set; } = [];
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public DateTime? FinishedAt { get; set; }
    /// <summary>A finished plan's card was closed on the board.</summary>
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingDefault)]
    public bool Dismissed { get; set; }

    [JsonIgnore] public bool IsOpen => State is PublishState.Planned or PublishState.Approved;
    [JsonIgnore] public Attachment? ChosenThumbnail => Chosen is { } i && i >= 0 && i < Thumbnails.Count ? Thumbnails[i] : null;
}
