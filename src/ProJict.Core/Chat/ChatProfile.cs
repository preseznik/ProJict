using ProJict.Core.Agents;
using ProJict.Core.Features;
using ProJict.Core.Model;

namespace ProJict.Core.Chat;

/// <summary>A project someone worked on, and how long.</summary>
public sealed record ProfileProject(Project Project, TimeSpan Time);

/// <summary>
/// What a person's or agent's profile card says about them (#490e5810): the channels they're in, how much they've written
/// and when last, and their work on items (as the Time tab counts it: what they finished, how long, what they used).
/// </summary>
public sealed record ChatProfile(
    string Name, bool Agent,
    IReadOnlyList<ChatChannel> Channels,
    int Messages, DateTime? LastMessage, DateTime? Since,
    int ItemsDone, IReadOnlyList<TodoItem> Working,
    TimeSpan Time, long Tokens, decimal Cost,
    IReadOnlyList<ProfileProject> Projects)
{
    /// <summary>
    /// Their profile from the channels and projects you can see. An agent is matched by its name without the client
    /// ("Lark · Claude Code" is Lark); people and agents of the same name are kept apart. Channels are the named ones
    /// they're members of (not private conversations); messages count in every channel given, so pass only those you
    /// can read.
    /// </summary>
    public static ChatProfile Of(string name, bool agent, IEnumerable<ChatChannel> channels, IEnumerable<Project> projects, DateTime now)
    {
        var who = Key(name, agent);
        bool Is(string by, bool byAgent) => byAgent == agent && string.Equals(Key(by, byAgent), who, StringComparison.OrdinalIgnoreCase);

        var all = channels.ToList();
        var theirs = all.Where(c => !c.Direct && c.Members.Any(m => Is(m.Name, m.Agent)))
            .OrderBy(c => c.Name, StringComparer.CurrentCultureIgnoreCase).ToList();
        var written = all.SelectMany(c => c.Messages).Where(m => !m.Notice && !m.Deleted && Is(m.By, m.Agent)).ToList();
        var joined = all.SelectMany(c => c.Members).Where(m => Is(m.Name, m.Agent)).Select(m => (DateTime?)m.JoinedAt).Min();

        var done = 0;
        var working = new List<TodoItem>();
        TimeSpan time = TimeSpan.Zero;
        long tokens = 0;
        decimal cost = 0;
        DateTime? firstWork = null;
        var perProject = new List<ProfileProject>();
        foreach (var project in projects)
        {
            var projectTime = TimeSpan.Zero;
            var any = false;
            foreach (var loc in project.Walk())
            {
                var item = loc.Item;
                var spans = WorkLog.SpansOf(item).Where(s => Is(s.By, s.Agent)).ToList();
                var claimed = agent && item.State == ItemState.InProgress && item.ClaimedBy is { } by && Is(by, true);
                if (spans.Count == 0 && !claimed) continue;
                any = true;
                if (item.IsDone && spans.Count > 0) done++;
                if (item.State == ItemState.InProgress && (claimed || spans.Any(s => s.IsOpen))) working.Add(item);
                foreach (var span in spans)
                {
                    var length = span.Length(now);
                    projectTime += length;
                    tokens += span.Tokens ?? 0;
                    cost += span.Cost ?? 0;
                    if (firstWork == null || span.Start < firstWork) firstWork = span.Start;
                }
            }
            time += projectTime;
            if (any) perProject.Add(new ProfileProject(project, projectTime));
        }

        var since = new[] { written.Count > 0 ? written.Min(m => m.At) : (DateTime?)null, joined, firstWork }.Where(d => d != null).Min();
        return new ChatProfile(name, agent, theirs, written.Count, written.Count > 0 ? written.Max(m => m.At) : null, since,
            done, working, time, tokens, cost,
            perProject.OrderByDescending(p => p.Time).ThenBy(p => p.Project.Name, StringComparer.CurrentCultureIgnoreCase).ToList());
    }

    /// <summary>"3 hours ago", "yesterday", "12 Oct"; for when they last wrote.</summary>
    public static string Ago(DateTime at, DateTime now, System.Globalization.CultureInfo culture)
    {
        var gone = now - at;
        if (gone < TimeSpan.FromMinutes(1)) return "just now";
        if (gone < TimeSpan.FromHours(1)) return Count((int)gone.TotalMinutes, "minute") + " ago";
        if (at.Date == now.Date) return Count((int)gone.TotalHours, "hour") + " ago";
        if (at.Date == now.Date.AddDays(-1)) return "yesterday";
        if (gone < TimeSpan.FromDays(7)) return Count((now.Date - at.Date).Days, "day") + " ago";
        return at.ToString(at.Year == now.Year ? "d MMM" : "d MMM yyyy", culture);
    }

    private static string Count(int n, string what) => n == 1 ? $"1 {what}" : $"{n} {what}s";

    private static string Key(string name, bool agent) => (agent ? AgentNaming.Identity(name) : name).Trim();
}
