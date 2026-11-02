namespace ProJict.Core.Agents;

/// <summary>What an agent in the Agents view's office is doing, from what it did last.</summary>
public enum OfficeActivity { Building, Managing, Reading, Talking, Resting, Listening }

/// <summary>
/// Reads an agent's latest tool call as an activity for the office: a conversation in Chat is Talking (at the water
/// cooler), checking out and finishing items is Building, adding items, comments, notes and wiki pages is Managing, going
/// through items, discussions and the wiki is Reading, and nothing for a while is Resting. A message now and then is part
/// of the work at the desk: reading a channel is Reading and posting one is Managing; only a real back-and-forth
/// (<see cref="ConversationPosts"/> messages lately, the last one just now) takes it to the cooler. An agent that hears
/// Chat right away (waiting for messages, or idle with a chat watcher running for it) is Listening rather than Resting.
/// Saying what it does (set_status, set_label, set_name) changes nothing.
/// </summary>
public static class OfficeActivities
{
    /// <summary>An agent that did nothing this long sleeps at its desk.</summary>
    public static readonly TimeSpan RestAfter = TimeSpan.FromMinutes(5);

    private static readonly HashSet<string> Chat =
        ["list_channels", "create_channel", "read_channel", "post_message", "edit_message", "join_channel", "invite_to_channel", "moderate_channel"];
    private static readonly HashSet<string> Build = ["claim_item", "complete_item", "attach_file", "report_ship", "finish_ship"];
    private static readonly HashSet<string> Manage = ["add_item", "add_comment", "write_doc", "write_wiki", "update_project", "plan_ship", "draft_release", "update_item", "hire_to_role"];
    private static readonly HashSet<string> Meta = ["set_status", "set_label", "set_name"];
    /// <summary>Chat tools that say something (the rest read).</summary>
    private static readonly HashSet<string> Says = ["post_message", "edit_message", "create_channel", "invite_to_channel", "moderate_channel"];

    /// <summary>A conversation: at least this many messages posted within <see cref="ConversationWithin"/>, the last within <see cref="ConversationGoesOn"/>.</summary>
    public const int ConversationPosts = 3;
    public static readonly TimeSpan ConversationWithin = TimeSpan.FromMinutes(10);
    /// <summary>Quiet this long after its last message, it goes back to its desk.</summary>
    public static readonly TimeSpan ConversationGoesOn = TimeSpan.FromMinutes(3);

    /// <summary>In a back-and-forth in Chat: posted <see cref="ConversationPosts"/> messages lately, the last one just now.</summary>
    public static bool InConversation(McpSession session, DateTime now)
    {
        var posts = session.Recent.Where(a => a.Tool == "post_message" && now - a.At <= ConversationWithin).ToList();
        if (posts.Count == 0 || now - posts.Max(a => a.At) > ConversationGoesOn) return false;
        // Repeats are kept as one action: count each, but only the ones within the window.
        var count = posts.Sum(a => a.Since is { } since && now - since > ConversationWithin ? 1 : a.Times);
        return count >= ConversationPosts;
    }

    /// <summary>Everyone's asleep: an agent that did nothing this long has finished up and sleeps.</summary>
    public static readonly TimeSpan AsleepAfter = TimeSpan.FromSeconds(20);

    /// <param name="watching">A chat watcher runs for it, so it hears Chat while idle.</param>
    /// <param name="asleep">You put every agent to sleep: once it's done it sleeps, whatever it listens for.</param>
    public static OfficeActivity Of(McpSession session, DateTime now, bool holdsItems, bool watching = false, bool asleep = false)
    {
        if (asleep && now - AgentsViewSettings.LastSeen(session) >= AsleepAfter) return OfficeActivity.Resting;
        if (session.Listening) return OfficeActivity.Listening;
        if (now - AgentsViewSettings.LastSeen(session) >= RestAfter) return watching ? OfficeActivity.Listening : OfficeActivity.Resting;
        var tool = session.Recent.LastOrDefault(a => a.Tool == null || !Meta.Contains(a.Tool))?.Tool;
        // Between two waits for messages.
        if (tool == "wait_for_messages") return OfficeActivity.Listening;
        if (tool != null && Chat.Contains(tool))
            return InConversation(session, now) ? OfficeActivity.Talking : Says.Contains(tool) ? OfficeActivity.Managing : OfficeActivity.Reading;
        if (tool != null && Build.Contains(tool)) return OfficeActivity.Building;
        // Changing the item it works on is part of the work.
        if (tool == "update_item" && holdsItems) return OfficeActivity.Building;
        if (tool != null && Manage.Contains(tool)) return OfficeActivity.Managing;
        if (tool == null && holdsItems) return OfficeActivity.Building;
        return OfficeActivity.Reading;
    }

    public static string Name(OfficeActivity activity) => activity switch
    {
        OfficeActivity.Building => "Building",
        OfficeActivity.Managing => "Managing",
        OfficeActivity.Talking => "Talking",
        OfficeActivity.Listening => "Listening",
        OfficeActivity.Resting => "Resting",
        _ => "Reading",
    };
}
