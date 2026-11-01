namespace ProJict.Core.Agents;

/// <summary>What an agent in the Agents view's office is doing, from what it did last.</summary>
public enum OfficeActivity { Building, Managing, Reading, Talking, Resting }

/// <summary>
/// Reads an agent's latest tool call as an activity for the office: chatting (or listening for chat) is Talking,
/// checking out and finishing items is Building, adding items, comments, notes and wiki pages is Managing, going through
/// items, discussions and the wiki is Reading, and nothing for a while is Resting. Saying what it does (set_status, set_label, set_name)
/// changes nothing.
/// </summary>
public static class OfficeActivities
{
    /// <summary>An agent that did nothing this long sleeps at its desk.</summary>
    public static readonly TimeSpan RestAfter = TimeSpan.FromMinutes(5);

    private static readonly HashSet<string> Chat =
        ["list_channels", "create_channel", "read_channel", "post_message", "edit_message", "join_channel", "invite_to_channel", "moderate_channel", "wait_for_messages"];
    private static readonly HashSet<string> Build = ["claim_item", "complete_item", "attach_file", "report_ship", "finish_ship"];
    private static readonly HashSet<string> Manage = ["add_item", "add_comment", "write_doc", "write_wiki", "update_project", "plan_ship", "draft_release", "update_item"];
    private static readonly HashSet<string> Meta = ["set_status", "set_label", "set_name"];

    public static OfficeActivity Of(McpSession session, DateTime now, bool holdsItems)
    {
        if (session.Listening) return OfficeActivity.Talking;
        if (now - AgentsViewSettings.LastSeen(session) >= RestAfter) return OfficeActivity.Resting;
        var tool = session.Recent.LastOrDefault(a => a.Tool == null || !Meta.Contains(a.Tool))?.Tool;
        if (tool != null && Chat.Contains(tool)) return OfficeActivity.Talking;
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
        OfficeActivity.Resting => "Resting",
        _ => "Reading",
    };
}
