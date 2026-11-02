using ProJict.Core.Chat;
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
    public static Studio NewGame() => New(StudioKind.Game);

    /// <summary>A new studio of that kind, with its eight roles.</summary>
    public static Studio New(StudioKind kind) => new() { Kind = kind, Roles = [.. kind == StudioKind.Film ? FilmRoles() : DefaultRoles()] };

    /// <summary>"game studio", "film studio".</summary>
    public static string KindName(StudioKind kind) => kind == StudioKind.Film ? "film studio" : "game studio";

    /// <summary>Which of the eight places a station is (0–3 the left wall back to front, 4–7 the right).</summary>
    public static int PlaceOf(StudioStation station) => (int)station % 8;

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

    /// <summary>
    /// The studio's channel: the one it has, else the project's own Open channel (the oldest), else a new one named after
    /// the project (#grid2vr), which you start.
    /// </summary>
    public static ChatChannel ChannelFor(Project project, Studio studio, ChatRoom room, ChatActor owner)
    {
        if (studio.ChannelId is { } id && room.Channels.FirstOrDefault(c => c.Id == id) is { } kept) return kept;
        var channel = room.Channels
            .Where(c => !c.Direct && c.Access == ChannelAccess.Open && c.Link is { CategoryId: null, ItemId: null } link && link.ProjectId == project.Id)
            .OrderBy(c => c.CreatedAt).FirstOrDefault()
            ?? room.Create(owner, FreeName(room, project), ChannelAccess.Open, new ChannelLink { ProjectId = project.Id },
                $"{project.Name}'s studio: the team talks here and agrees who takes what");
        studio.ChannelId = channel.Id;
        return channel;
    }

    private static string FreeName(ChatRoom room, Project project)
    {
        var stem = new string(project.Name.ToLowerInvariant().Select(c => char.IsLetterOrDigit(c) ? c : '-').ToArray()).Trim('-');
        if (stem.Length == 0) stem = "studio";
        return new[] { stem, stem + "-studio" }.Concat(Enumerable.Range(2, 50).Select(n => $"{stem}-studio-{n}"))
            .First(name => room.NameProblem(name) == null);
    }

    /// <summary>A film studio's roles: from the idea through recording, editing and sound to the published video.</summary>
    public static IEnumerable<StudioRole> FilmRoles()
    {
        StudioRole Role(string name, StudioStation station, string definition) => new() { Name = name, Station = station, Definition = definition };
        yield return Role("Director", StudioStation.Director,
            "Owns each video's idea and how it comes across: the story, the script and storyboard, the look and the pace. Decides between options and " +
            "signs off a video before it's published. Delivers the plan for each video in its item (what, for whom, how long, the shots) and a clear " +
            "next step for the others. Works with everyone: agrees the script with the Writer and the schedule with the Producer.");
        yield return Role("Writer", StudioStation.Writer,
            "Owns the words: scripts, voice-over text, on-screen titles, and the first draft of the title and description. Delivers a script in the " +
            "video's item (or a note), timed to the shots, and changes after the Director's review. Works with the Director on the story, Sound on the " +
            "voice-over and the Publisher on the title and description.");
        yield return Role("Capture", StudioStation.Capture,
            "Owns the recordings: desktop and game captures, and VR recordings (e.g. with VRCapture), in the resolution, frame rate and audio the video " +
            "needs. Delivers the clips in the project's folder, named by shot, with a note of what's where. Works with the Director on the shots and " +
            "with the Editor on what's missing.");
        yield return Role("Editor", StudioStation.Editing,
            "Owns the cut: picks and orders the clips, pacing, transitions, color, titles and captions, and exports the finished video. Delivers each " +
            "cut in the project's folder with a note of its length and what changed, and the final export. Works with Capture on missing shots, Sound " +
            "on the mix and the Director on sign-off.");
        yield return Role("Sound", StudioStation.Mixing,
            "Owns how the video sounds: music (e.g. made with Suno in the browser), voice-over (e.g. ElevenLabs), effects and the final mix and loudness. " +
            "Delivers the tracks and the mix in the project's folder, with the music's and voices' terms noted. Works with the Writer on the voice-over " +
            "and the Editor on timing.");
        yield return Role("Thumbnail & graphics", StudioStation.Graphics,
            "Owns what's drawn: thumbnails (2–4 to choose from per video), channel art, titles and lower thirds. Delivers images in the sizes the video " +
            "and channel need, attached to the video's item. Works with the Director on the look and the Publisher on the thumbnail choice.");
        yield return Role("Publisher", StudioStation.Publishing,
            "Owns publishing: the title, description, tags, playlists, end screens and the date for each video, and its thumbnail choices. Never " +
            "publishes before the user approves. Delivers a publish plan in the video's item, and the link once it's out. Works with the Writer on the " +
            "text, Graphics on the thumbnails and the Producer on the schedule.");
        yield return Role("Producer", StudioStation.Producing,
            "Owns the schedule: which videos are made when, what each still needs, and that nothing waits unnoticed. Delivers milestones and their " +
            "items, and a short status when asked. Works with everyone: agrees what's next with the Director and checks with the Publisher what's due.");
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
