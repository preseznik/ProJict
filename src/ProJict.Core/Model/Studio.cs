using System.Text.Json.Serialization;

namespace ProJict.Core.Model;

/// <summary>Where a role works in the studio: the eight stations along its side walls, left back to front, then right.</summary>
public enum StudioStation { Lead, Design, Level, Code, Art, Sound, QA, Production }

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
}

/// <summary>
/// A project's game studio: the Agents view shows it as a room of its own with a station per role. Agents working in
/// the project come in unassigned and wait in the lobby until you hire them to a role; the role and what it expects
/// are theirs from then on (by their name), until you fire them or put someone else in it.
/// </summary>
public sealed class Studio
{
    /// <summary>The Agents view shows the studio for this project (else the office; roles and hires stay).</summary>
    public bool Shown { get; set; } = true;
    public List<StudioRole> Roles { get; set; } = [];
    public List<StudioHire> Hires { get; set; } = [];
    /// <summary>Hires that ended lately (fired, or someone else took the role), so the agent hears it.</summary>
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public List<StudioHire>? Ended { get; set; }
}
