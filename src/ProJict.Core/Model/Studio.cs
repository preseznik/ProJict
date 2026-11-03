using System.Text.Json.Serialization;

namespace ProJict.Core.Model;

/// <summary>What a studio makes: games, or films (videos).</summary>
public enum StudioKind { Game, Film }

/// <summary>
/// Where a role works, and with what: a game studio's eight stations, then a film studio's (in the same places), then a
/// game studio's UI designer's and Cinematics' (added later, in the two places a film studio leaves empty).
/// </summary>
public enum StudioStation
{
    Lead, Design, Level, Code, Art, Sound, QA, Production,
    Director, Writer, Capture, Editing, Mixing, Graphics, Publishing, Producing,
    UI, Cinematics,
}

/// <summary>A job in a project's studio: what it's called, and what it owns, delivers and how it works with the others.</summary>
public sealed class StudioRole
{
    public Guid Id { get; set; } = Guid.NewGuid();
    public string Name { get; set; } = "";
    /// <summary>What the agent in this role is expected to do; it's told this when hired.</summary>
    public string Definition { get; set; } = "";
    public StudioStation Station { get; set; }
}

/// <summary>An agent, by its name, in a role: since when and who hired it (or when and who ended it).</summary>
public sealed class StudioHire
{
    public string Agent { get; set; } = "";
    public Guid RoleId { get; set; }
    public DateTime At { get; set; } = DateTime.Now;
    public string By { get; set; } = "";
    /// <summary>One of more in the role, under its senior (the first hired): "Junior Programmer".</summary>
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingDefault)]
    public bool Junior { get; set; }
}

/// <summary>
/// A project's game or film studio: the Agents view shows it as one of the project's rooms, with a station per role.
/// Agents working in the project stay in the office (or wait in the lobby, while the office is closed) until you hire them
/// to a role; the role and what it expects are theirs from then on (by their name), until you fire them or put someone
/// else in it. A project has at most one studio of each kind.
/// </summary>
public sealed class Studio
{
    public StudioKind Kind { get; set; }
    public List<StudioRole> Roles { get; set; } = [];
    /// <summary>The studio's channel, where the team talks: the project's Open channel, made with the studio if it had none.</summary>
    public Guid? ChannelId { get; set; }
    public List<StudioHire> Hires { get; set; } = [];
    /// <summary>
    /// The lead (Lead designer, or Director) reviews the wiki pages about the project that the studio's other agents
    /// change, instead of you: accepts them or sends them back.
    /// </summary>
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingDefault)]
    public bool LeadReviewsWiki { get; set; }
    /// <summary>Only pages in this folder (and below), e.g. "GRID2VR/Design"; null: every page about the project.</summary>
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public string? LeadReviewFolder { get; set; }
    /// <summary>Closed for now: not one of the project's rooms, but its roles and hires are kept for when it opens again.</summary>
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingDefault)]
    public bool Closed { get; set; }
    /// <summary>Hires that ended lately (fired, or someone else took the role), so the agent hears it.</summary>
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public List<StudioHire>? Ended { get; set; }
}
