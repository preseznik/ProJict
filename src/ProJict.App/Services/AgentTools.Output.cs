using System.Text.Json.Nodes;

namespace ProJict.App.Services;

/// <summary>
/// Output schemas (#aec5ff30): what a tool's result has, for clients that read structured content (MCP 2025-06-18 and
/// newer get every JSON result as structuredContent too). A field gets a type only where it's always that type when it's
/// there, and none is required, so no result can fail its schema in a client that checks; the descriptions say what each
/// is. Every result may also have waitingForYou and asleep. Added a tool at a time, the most used first.
/// </summary>
public sealed partial class AgentTools
{
    private static JsonObject Output(params (string Name, string? Type, string Description)[] fields)
    {
        var properties = new JsonObject();
        foreach (var (name, type, description) in fields)
        {
            var property = new JsonObject();
            if (type != null) property["type"] = type;
            property["description"] = description;
            properties[name] = property;
        }
        properties["waitingForYou"] = new JsonObject
        {
            ["type"] = "array",
            ["items"] = new JsonObject { ["type"] = "string" },
            ["description"] = "What waits for you (chat, comments on your work, work sent back, requests): deal with each before going on.",
        };
        properties["asleep"] = new JsonObject { ["type"] = "string", ["description"] = "The user put every agent to sleep: finish what you're doing, then stay idle." };
        return new JsonObject { ["type"] = "object", ["properties"] = properties };
    }

    /// <summary>An item's fields, as get_item and list_items give them.</summary>
    private static readonly (string, string?, string)[] ItemFields =
    [
        ("id", "string", "The item's id; its first 8 characters are enough in other calls."),
        ("category", null, "Its category."),
        ("text", "string", "What it says."),
        ("state", "string", "todo, in_progress or done."),
        ("tag", null, "Its tag, e.g. BUG or ADD."),
        ("priority", null, "urgent, high, medium, low or hold; none when it has none."),
        ("checkedOutBy", null, "Who has it checked out (claim_item)."),
        ("needsReview", null, "Finished by an agent, waiting for the user's review."),
        ("offLimits", null, "true when you may read and comment on it but not work on it."),
        ("parent", null, "The item it's a sub-item of."),
        ("subItems", null, "Its sub-items (list_items: how many)."),
        ("comments", null, "How many comments its discussion has."),
        ("attachments", null, "Its files, with paths you can open."),
        ("media", null, "Pictures and videos linked to it."),
        ("gitHubIssue", null, "The GitHub issue it stands for: mention \"Fixes #<number>\" when you resolve it."),
        ("time", null, "Time worked on it, by whom."),
    ];

    private static JsonObject GetItemOutput => Output(
    [
        .. ItemFields,
        ("history", null, "What happened to it, oldest first."),
        ("discussion", null, "Its discussion: threads of comments with replies."),
        ("project", null, "Its project: name, repo folder, GitHub link, instructions."),
    ]);

    private static JsonObject ListItemsOutput => Output(
        ("project", "string", "The project's name."),
        ("state", null, "Which states are listed."),
        ("total", "integer", "How many items match."),
        ("items", "array", "The items, each with " + string.Join(", ", ItemFields.Select(f => f.Item1)) + " (as get_item has them, without history and discussion)."),
        ("truncated", "boolean", "More items match than limit lists."),
        ("offLimits", null, "How many matching items are off limits to agents."),
        ("note", null, "About the items off limits."));

    private static JsonObject ReadChannelOutput => Output(
        ("name", null, "The channel, as #name (or @name for a private conversation)."),
        ("topic", null, "What it's for."),
        ("access", null, "open or invite only."),
        ("linkedTo", null, "The project, category or item it's about."),
        ("member", null, "Whether you're in it."),
        ("moderator", null, "Whether you moderate it."),
        ("mutedUntil", null, "You can't post until then."),
        ("members", "array", "Who's in it: name, agent, moderator, mutedUntil."),
        ("messages", "array", "The latest messages, oldest first: id, by, agent, at, text, replyTo, edited, deleted, mentionsYou, yes and no (who answered " +
                              "👍 or 👎), attachments (file paths); or a notice (someone joined, …) with at."),
        ("more", null, "How to read further back (before=<message id>)."),
        ("startedBy", null, "Who started it."),
        ("lastMessage", null, "When its last message came."));

    private static JsonObject GetStartedOutput => Output(
        ("you", "object", "Your name here, what you run in, and a note."),
        ("steps", "array", "The first steps in order: step, done (when ProJict can tell) and how."),
        ("project", "object", "Your project, as current_project gives it: details, instructions, the user's instructions for all agents, which items " +
                              "agents may work on, shipping, categories."),
        ("instructionsForAllAgents", "string", "The user's instructions for every agent (here when no project matched; otherwise in project)."),
        ("guide", "object", "How ProJict works, by topic: items, you, waitingForYou, chat, approvals, sharedMachines, helpers, projectDetails, " +
                            "wiki, shipping, publishing, studios, outsideTools, cleanups, sharing."));
}
