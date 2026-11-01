using ProJict.Core.Model;

namespace ProJict.Core.Features;

/// <summary>
/// A project's game studio: setting it up with the usual game-making roles, and hiring agents to them, firing them and
/// swapping two. One agent per role; agents are known by their name.
/// </summary>
public static class StudioFlow
{
    /// <summary>How many ended hires are kept for the agents to hear about.</summary>
    private const int EndedKept = 30;

    /// <summary>A new game studio with its eight roles, each with what it's expected to do.</summary>
    public static Studio NewGame() => new() { Roles = [.. DefaultRoles()] };

    public static IEnumerable<StudioRole> DefaultRoles()
    {
        StudioRole Role(string name, StudioStation station, string definition) => new() { Name = name, Station = station, Definition = definition };
        yield return Role("Lead designer", StudioStation.Lead,
            "Owns the game's vision and the design as a whole: decides between options, keeps the items coherent and in order of priority, and signs off " +
            "on features before they're done. Delivers design decisions in the items' discussions and notes, and a clear next step for the others. " +
            "Works with everyone: reviews the Game designer's and Level designer's proposals, settles disagreements, and agrees scope with the Producer.");
        yield return Role("Game designer", StudioStation.Design,
            "Owns the rules, systems and feel: mechanics, controls, progression, balance, menus and settings. Delivers a short spec in the item before " +
            "it's built (what, why, how it should feel, edge cases) and tuning values after playtests. Works with the Lead designer on decisions, the " +
            "Programmer to build it and QA to test it.");
        yield return Role("Level designer", StudioStation.Level,
            "Owns the levels, tracks or maps: layout, flow, pacing, difficulty and what goes where. Delivers level items with a description or sketch, " +
            "notes per level, and fixes for the level bugs QA finds. Works with the Artist on the look, the Game designer on the rules and QA on playtests.");
        yield return Role("Programmer", StudioStation.Code,
            "Owns the code: builds features and fixes bugs from the items, keeps the build working, and writes down how things work. Delivers working " +
            "changes with a short resolution (what changed, the commit), test builds worth trying, and notes on technical limits. Works with the " +
            "designers on specs, QA on bugs (asks for steps to reproduce when unclear) and the Producer on what ships.");
        yield return Role("Artist", StudioStation.Art,
            "Owns how the game looks: models, textures, UI art, effects and the visual style. Delivers assets attached to their items, in the sizes and " +
            "formats the game needs, with notes on the style. Works with the Level designer on levels, the Programmer on getting assets in and the " +
            "Lead designer on the style.");
        yield return Role("Sound engineer", StudioStation.Sound,
            "Owns how the game sounds: effects, music, the mix and audio settings. Delivers sounds attached to their items, the mix, and notes on where " +
            "each sound plays. Works with the Programmer to get them in, the Game designer on feedback sounds and QA on audio bugs.");
        yield return Role("Quality assurance", StudioStation.QA,
            "Owns quality: plays the test builds, finds what's broken or feels wrong, and checks fixes. Delivers BUG items with steps to reproduce, what " +
            "happened, what was expected and a screenshot or log, and a pass or fail on fixed items. Works with the Programmer and Level designer on " +
            "fixes, and tells the Producer what blocks a release.");
        yield return Role("Producer", StudioStation.Production,
            "Owns the schedule and shipping: what goes into the next build or release, priorities, and that nothing waits unnoticed. Delivers milestones " +
            "and their items, release notes, ship requests and plans, and a short status when asked. Works with everyone: agrees scope with the Lead " +
            "designer and asks QA what blocks a release.");
    }

    private static bool Same(string a, string b) => string.Equals(a.Trim(), b.Trim(), StringComparison.OrdinalIgnoreCase);

    public static StudioHire? HireOf(Studio studio, string agent) => studio.Hires.FirstOrDefault(h => Same(h.Agent, agent));

    public static StudioRole? RoleOf(Studio studio, string agent) =>
        HireOf(studio, agent) is { } hire ? studio.Roles.FirstOrDefault(r => r.Id == hire.RoleId) : null;

    /// <summary>Who has the role, if anyone.</summary>
    public static string? HolderOf(Studio studio, StudioRole role) => studio.Hires.FirstOrDefault(h => h.RoleId == role.Id)?.Agent;

    /// <summary>Hires the agent to the role (leaving any role it had); whoever had it goes back to the lobby, and is returned.</summary>
    public static string? Hire(Studio studio, string agent, StudioRole role, string by)
    {
        agent = agent.Trim();
        if (agent.Length == 0) throw new InvalidOperationException("Say which agent to hire.");
        if (!studio.Roles.Contains(role)) throw new InvalidOperationException("That role isn't in this studio.");
        if (HireOf(studio, agent) is { } current && current.RoleId == role.Id) return null;
        var replaced = studio.Hires.FirstOrDefault(h => h.RoleId == role.Id && !Same(h.Agent, agent));
        if (replaced != null) End(studio, replaced, by);
        if (HireOf(studio, agent) is { } previous) studio.Hires.Remove(previous);
        studio.Hires.Add(new StudioHire { Agent = agent, RoleId = role.Id, By = by });
        return replaced?.Agent;
    }

    /// <summary>The agent leaves its role and goes back to the lobby.</summary>
    public static void Fire(Studio studio, string agent, string by)
    {
        if (HireOf(studio, agent) is { } hire) End(studio, hire, by);
    }

    /// <summary>Two hired agents swap roles.</summary>
    public static void Swap(Studio studio, string a, string b, string by)
    {
        if (HireOf(studio, a) is not { } first || HireOf(studio, b) is not { } second || first == second)
            throw new InvalidOperationException("Both need a role to swap them.");
        (first.RoleId, second.RoleId) = (second.RoleId, first.RoleId);
        first.At = second.At = DateTime.Now;
        first.By = second.By = by;
    }

    private static void End(Studio studio, StudioHire hire, string by)
    {
        studio.Hires.Remove(hire);
        (studio.Ended ??= []).Add(new StudioHire { Agent = hire.Agent, RoleId = hire.RoleId, By = by });
        if (studio.Ended.Count > EndedKept) studio.Ended.RemoveRange(0, studio.Ended.Count - EndedKept);
    }
}
