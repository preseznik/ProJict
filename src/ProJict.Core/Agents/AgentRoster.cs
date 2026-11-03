using ProJict.Core.Model;

namespace ProJict.Core.Agents;

/// <summary>
/// What the Agents view knows about agents, without any screen: who has been here and when, what each has checked out,
/// said lately and is doing now, and how long ago ("12 s ago").
/// </summary>
public static class AgentRoster
{
    /// <summary>
    /// The projects the Agents view offers, by name: your own, without those you've hidden in the sidebar, except the one
    /// shown (so the list never goes blank under it).
    /// </summary>
    public static IReadOnlyList<string> ProjectChoices(IEnumerable<Project> projects, ICollection<Guid> hidden, Project? shown) =>
        [.. projects.Where(p => p.Remote == null && (!hidden.Contains(p.Id) || p == shown)).Select(p => p.Name).Order(StringComparer.CurrentCultureIgnoreCase)];
}
