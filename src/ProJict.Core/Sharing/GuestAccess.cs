using System.Text.Json.Nodes;
using ProJict.Core.Agents;
using ProJict.Core.Model;
using ProJict.Core.Storage;

namespace ProJict.Core.Sharing;

/// <summary>What a guest may do in the host's projects; each role can do everything the ones before it can.</summary>
public enum GuestRole
{
    /// <summary>Sees the projects, changes nothing.</summary>
    Viewer,
    /// <summary>Also takes part in discussions.</summary>
    Commenter,
    /// <summary>Also adds new items (for testers or clients reporting bugs), but doesn't change existing ones.</summary>
    Reporter,
    /// <summary>Changes everything in the projects, but not their name, group, status or folder, and can't delete them.</summary>
    Editor,
}

/// <summary>
/// The host's check of every change a guest sends: whatever the guest's app claims, a viewer changes nothing, a commenter
/// only comments, a reporter only adds items and comments, and nobody signs in someone else's name. The guest's AI agents
/// work through the same role, signed "Ada (via Alice)", when the host lets them, and only on items its agent rules allow.
/// </summary>
public static class GuestAccess
{
    public static bool CanComment(GuestRole role) => role >= GuestRole.Commenter;
    public static bool CanAddItems(GuestRole role) => role >= GuestRole.Reporter;
    public static bool CanEdit(GuestRole role) => role == GuestRole.Editor;

    public static string Describe(GuestRole role) => role switch
    {
        GuestRole.Viewer => "Viewer: sees the projects, changes nothing",
        GuestRole.Commenter => "Commenter: sees and discusses",
        GuestRole.Reporter => "Reporter: adds items and discusses, doesn't change existing ones",
        _ => "Editor: changes everything in the projects (not their name, group or folder)",
    };

    /// <summary>Only the host changes these; a guest's values are replaced by the host's.</summary>
    private static readonly string[] HostFields =
        ["id", "name", "key", "filePrefix", "group", "status", "isInbox", "repoPath", "gitHubUrl", "issues", "gitHubRelease", "archivedFiles", "shipping", "ships", "testBuild", "cleanups", "studio", "createdAt", "updatedAt"];

    /// <summary>The longest agent name a guest's agent may sign with ("Ada" in "Ada (via Alice)").</summary>
    private const int MaxAgentName = 40;

    /// <summary>
    /// The guest's version of the project as the host will store it, or the reason it is refused. Comments, history
    /// entries, notes and releases the guest added are signed with the guest's name, or "Ada (via Alice)" when the guest's
    /// agent did it and <paramref name="agentsAllowed"/>; such work on items is limited to <paramref name="agentPriorities"/>.
    /// The host's own user on another computer (<paramref name="self"/>, confirmed with a code) comes as
    /// <paramref name="guestName"/> = the host's user name, and its agents sign with their own names, like the host's.
    /// </summary>
    public static (Project? Accepted, string? Refusal) Check(Project current, Project incoming, string guestName, GuestRole role,
        bool agentsAllowed = false, IReadOnlyCollection<Priority>? agentPriorities = null, bool self = false)
    {
        var c = ProjectMerge.ToJson(current);
        var n = ProjectMerge.ToJson(incoming);
        foreach (var field in HostFields)
        {
            if (c[field] is { } value) n[field] = value.DeepClone();
            else n.Remove(field);
        }
        // Folding a category only changes your own view.
        var collapsed = Categories(c).ToDictionary(k => (string)k["id"]!, k => k["collapsed"]?.DeepClone());
        foreach (var category in Categories(n))
        {
            if (collapsed.TryGetValue((string)category["id"]!, out var value) && value != null) category["collapsed"] = value;
            else category.Remove("collapsed");
        }

        var before = AllComments(c);
        var after = AllComments(n);
        var commentsChanged = before.Count != after.Count;
        foreach (var (id, comment) in after)
        {
            if (!before.TryGetValue(id, out var old))
            {
                if (Sign(comment, "by", guestName, agentsAllowed, self) is { } refused) return (null, refused);
                commentsChanged = true;
                continue;
            }
            if (JsonNode.DeepEquals(Without(old, "replies"), Without(comment, "replies"))) continue;
            commentsChanged = true;
            if (!Mine(old, guestName, self)) return (null, "You can only change or remove your own comments.");
        }
        if (before.Keys.Except(after.Keys).Any(id => !Mine(before[id], guestName, self)))
            return (null, "You can only change or remove your own comments.");
        if (commentsChanged && !CanComment(role)) return (null, "As a viewer you can't change anything here.");

        if (!CanEdit(role))
        {
            var known = AllItems(c).Keys.ToHashSet();
            var stripped = Strip(n, newItemsOf: CanAddItems(role) ? known : null);
            if (!JsonNode.DeepEquals(Strip(c, newItemsOf: null), stripped))
                return (null, role switch
                {
                    GuestRole.Viewer => "As a viewer you can't change anything here.",
                    GuestRole.Commenter => "As a commenter you can only take part in discussions.",
                    _ => "As a reporter you can add items and comments, but not change existing items.",
                });
        }
        if (SignNewWork(c, n, guestName, agentsAllowed, agentPriorities ?? [], self) is { } refusal) return (null, refusal);
        return (Json.Deserialize<Project>(n.ToJsonString(Json.Options)), null);
    }

    /// <summary>
    /// Signs something new the guest sent: with the guest's name, or for the guest's agent (marked agent) "Ada (via Alice)"
    /// when the host lets guests' agents work; returns why not otherwise.
    /// </summary>
    private static string? Sign(JsonObject thing, string field, string guestName, bool agentsAllowed, bool self = false)
    {
        if (thing["agent"] is JsonValue flag && flag.TryGetValue<bool>(out var agent) && agent)
        {
            if (!agentsAllowed) return "This ProJict doesn't let guests' AI agents change what it shares.";
            var name = AgentNaming.WithoutVia((string?)thing[field] ?? "", guestName) ?? (string?)thing[field] ?? "";
            name = name.Trim();
            name = name.Length is > 0 and <= MaxAgentName && !name.Contains(" (via ") ? name : "AI agent";
            // Your own agents on your other computer sign like the ones here.
            thing[field] = self ? name : AgentNaming.Via(name, guestName);
            return null;
        }
        thing[field] = guestName;
        thing.Remove("agent");
        return null;
    }

    /// <summary>The guest's own comment, or its agent's ("Ada (via Alice)"); for you on another computer, any agent's.</summary>
    private static bool Mine(JsonObject comment, string guestName, bool self = false) =>
        comment["agent"] == null ? string.Equals((string?)comment["by"], guestName, StringComparison.OrdinalIgnoreCase)
            : self || AgentNaming.WithoutVia((string?)comment["by"] ?? "", guestName) != null;

    /// <summary>
    /// New history entries, notes changes and releases carry the guest's name, or its agent's; a check-out is the guest's
    /// or its agent's. An agent's work on an item the host's rules keep off limits to agents is refused.
    /// </summary>
    private static string? SignNewWork(JsonObject c, JsonObject n, string guestName, bool agentsAllowed, IReadOnlyCollection<Priority> agentPriorities, bool self)
    {
        var old = AllItems(c);
        var governing = Governing(c);
        foreach (var (id, item) in AllItems(n))
        {
            var was = old.GetValueOrDefault(id);
            var known = (was?["activity"] as JsonArray)?.Count ?? 0;
            if (item["activity"] is JsonArray log)
                for (var i = known; i < log.Count; i++)
                {
                    if (log[i] is not JsonObject entry) continue;
                    if (Sign(entry, "by", guestName, agentsAllowed, self) is { } refused) return refused;
                    // Agents keep off items the host keeps off limits to them; putting one back stays allowed. New items are fine.
                    if (entry["agent"] != null && was != null && (string?)entry["text"] != "Put it back to do." &&
                        !AgentAccess.Allows(agentPriorities, governing.GetValueOrDefault(id)))
                        return "This ProJict keeps that item off limits to AI agents.";
                }
            // Time they spent (or logged) is in their name, or their agent's, too.
            var knownWork = (was?["work"] as JsonArray)?.Count ?? 0;
            if (item["work"] is JsonArray work)
                for (var i = knownWork; i < work.Count; i++)
                    if (work[i] is JsonObject span && Sign(span, "by", guestName, agentsAllowed, self) is { } refused) return refused;
            // Only the guest or its agent checks things out in its name; you on another computer, as here.
            var claimed = (string?)item["claimedBy"];
            if (!self && claimed != null && claimed != (string?)was?["claimedBy"] &&
                !string.Equals(claimed, guestName, StringComparison.OrdinalIgnoreCase) && !(agentsAllowed && AgentNaming.WithoutVia(claimed, guestName) != null))
                item["claimedBy"] = guestName;
        }
        var notes = (c["notes"] as JsonArray ?? []).OfType<JsonObject>().ToDictionary(x => (string)x["id"]!);
        foreach (var note in (n["notes"] as JsonArray ?? []).OfType<JsonObject>())
            if ((!notes.TryGetValue((string)note["id"]!, out var was) || !JsonNode.DeepEquals(was, note)) && !(self && (string?)note["updatedBy"] is { Length: > 0 }))
                note["updatedBy"] = agentsAllowed && AgentNaming.WithoutVia((string?)note["updatedBy"] ?? "", guestName) != null ? note["updatedBy"]!.GetValue<string>() : guestName;
        var releases = (c["releases"] as JsonArray ?? []).OfType<JsonObject>().Select(x => (string)x["id"]!).ToHashSet();
        foreach (var release in (n["releases"] as JsonArray ?? []).OfType<JsonObject>())
            if (!releases.Contains((string)release["id"]!) && !(self && (string?)release["createdBy"] is { Length: > 0 }))
                release["createdBy"] = agentsAllowed && AgentNaming.WithoutVia((string?)release["createdBy"] ?? "", guestName) != null ? release["createdBy"]!.GetValue<string>() : guestName;
        return null;
    }

    /// <summary>Each item's priority as agents go by it (its own, or else its parent's), by id.</summary>
    private static Dictionary<string, Priority> Governing(JsonObject project)
    {
        var map = new Dictionary<string, Priority>();
        void Walk(JsonNode? list, Priority above)
        {
            foreach (var item in (list as JsonArray ?? []).OfType<JsonObject>())
            {
                var own = Enum.TryParse<Priority>((string?)item["priority"], true, out var p) ? p : Priority.None;
                var governing = AgentAccess.Governing(own, above);
                map[(string)item["id"]!] = governing;
                Walk(item["children"], governing);
            }
        }
        foreach (var category in Categories(project)) Walk(category["items"], Priority.None);
        return map;
    }

    private static IEnumerable<JsonObject> Categories(JsonObject project) => (project["categories"] as JsonArray ?? []).OfType<JsonObject>();

    private static Dictionary<string, JsonObject> AllItems(JsonObject project)
    {
        var map = new Dictionary<string, JsonObject>();
        void Walk(JsonNode? list)
        {
            foreach (var item in (list as JsonArray ?? []).OfType<JsonObject>())
            {
                map[(string)item["id"]!] = item;
                Walk(item["children"]);
            }
        }
        foreach (var category in Categories(project)) Walk(category["items"]);
        return map;
    }

    private static Dictionary<string, JsonObject> AllComments(JsonObject project)
    {
        var map = new Dictionary<string, JsonObject>();
        void Walk(JsonNode? list)
        {
            foreach (var comment in (list as JsonArray ?? []).OfType<JsonObject>())
            {
                map[(string)comment["id"]!] = comment;
                Walk(comment["replies"]);
            }
        }
        foreach (var category in Categories(project)) Walk(category["comments"]);
        foreach (var item in AllItems(project).Values) Walk(item["comments"]);
        return map;
    }

    private static JsonObject Without(JsonObject node, string property)
    {
        var copy = node.DeepClone().AsObject();
        copy.Remove(property);
        return copy;
    }

    /// <summary>The project without its discussions and (for reporters) without the items they added.</summary>
    private static JsonObject Strip(JsonObject project, HashSet<string>? newItemsOf)
    {
        var copy = project.DeepClone().AsObject();
        void Walk(JsonArray? list)
        {
            if (list == null) return;
            for (var i = list.Count - 1; i >= 0; i--)
            {
                if (list[i] is not JsonObject item) continue;
                if (newItemsOf != null && !newItemsOf.Contains((string)item["id"]!))
                {
                    list.RemoveAt(i);
                    continue;
                }
                item.Remove("comments");
                Walk(item["children"] as JsonArray);
            }
        }
        foreach (var category in Categories(copy))
        {
            category.Remove("comments");
            Walk(category["items"] as JsonArray);
        }
        return copy;
    }
}
