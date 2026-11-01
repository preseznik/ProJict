using System.IO;
using System.Text.Json.Nodes;
using System.Windows.Threading;
using ProJict.Core.Agents;
using ProJict.Core.Model;
using ProJict.Core.Sharing;
using ProJict.Core.Storage;
using ProJict.Core.Wiki;

namespace ProJict.App.Services.Sharing;

/// <summary>
/// The wiki for guests. A page linked to a project is shared as the project is (never a private one); a page without a
/// project only when you tick "Share with everyone I share with". Each guest gets the pages it may see, kept up to date;
/// editors (and their agents, when you let guests' agents work) change them through requests the host checks and saves
/// as versions signed with their names. Everything stays with the host.
/// </summary>
public sealed partial class HostService
{
    private static readonly TimeSpan WikiDelay = TimeSpan.FromMilliseconds(200);
    private readonly Dictionary<Guid, DispatcherTimer> _wikiTimers = [];

    /// <summary>Whether the guest sees the page: its project is shared with it, or it has none and you share it with everyone.</summary>
    private bool SeesPage(GuestAccount account, WikiPage page) =>
        page.ProjectId is { } id ? Visible(account).Any(p => p.Id == id) : page.SharedWithEveryone;

    private static JsonNode PageJson(WikiPage page) => JsonNode.Parse(Json.Serialize(WikiBook.ForGuest(page)))!;

    /// <summary>All the pages the guest may see, replacing what it had (on connecting).</summary>
    private JsonObject WikiSnapshot(GuestSession session)
    {
        var pages = _state.Wiki.Pages.Where(p => SeesPage(session.Account, p)).ToList();
        session.WikiSent.Clear();
        foreach (var page in pages) session.WikiSent.Add(page.Id);
        return Wire.Message("wiki", ("pages", new JsonArray(pages.Select(PageJson).ToArray())));
    }

    private void OnWikiChanged(WikiPage page)
    {
        if (_sessions.Count == 0) return;
        if (!_wikiTimers.TryGetValue(page.Id, out var timer))
        {
            timer = new DispatcherTimer(DispatcherPriority.Background, _dispatcher) { Interval = WikiDelay };
            timer.Tick += (_, _) =>
            {
                timer.Stop();
                SendPage(page);
            };
            _wikiTimers[page.Id] = timer;
        }
        timer.Stop();
        timer.Start();
    }

    /// <summary>The page to every guest that may see it; ones that no longer may lose it.</summary>
    private void SendPage(WikiPage page)
    {
        var gone = !_state.Wiki.Pages.Contains(page);
        foreach (var session in _sessions)
        {
            if (!gone && SeesPage(session.Account, page))
            {
                session.WikiSent.Add(page.Id);
                session.Send(Wire.Message("wikiPage", ("page", PageJson(page))));
            }
            else if (session.WikiSent.Remove(page.Id))
                session.Send(Wire.Message("wikiGone", ("id", page.Id.ToString())));
        }
    }

    /// <summary>Projects moved between groups or became private: guests get the pages they may now see and lose the others.</summary>
    private void SyncWiki()
    {
        foreach (var session in _sessions)
        {
            var visible = _state.Wiki.Pages.Where(p => SeesPage(session.Account, p)).ToList();
            foreach (var page in visible.Where(p => !session.WikiSent.Contains(p.Id)))
            {
                session.WikiSent.Add(page.Id);
                session.Send(Wire.Message("wikiPage", ("page", PageJson(page))));
            }
            foreach (var id in session.WikiSent.Except(visible.Select(p => p.Id)).ToList())
            {
                session.WikiSent.Remove(id);
                session.Send(Wire.Message("wikiGone", ("id", id.ToString())));
            }
        }
    }

    /// <summary>
    /// A change a guest makes in the wiki. Only editors change pages: write, rename, restore a version, settle a clash and
    /// add files; the pages they start are about a project shared with them. Their agents (when you let guests' agents
    /// work) write pages, signed "Ada (via Alice)", and their work waits for your review. Linking, sharing, accepting and
    /// deleting stay yours. Returns why not, or null (the page itself goes out to everyone who sees it).
    /// </summary>
    private JsonObject? WikiOp(GuestSession session, JsonObject message)
    {
        var account = session.Account;
        var op = (string?)message["op"] ?? "";
        string? Text(string name) => (string?)message[name];
        JsonObject Refuse(string why) => Wire.Message("wikiError", ("message", why));
        var author = new WikiAuthor(NameOf(account));
        if (Text("as") is { } agentName && agentName.Trim().Length > 0)
        {
            if (!account.Self && !Data.GuestAgents) return Refuse("This ProJict doesn't let guests' AI agents change what it shares.");
            if (op is not ("save" or "create")) return Refuse("Guests' AI agents write pages; the rest is for people.");
            var agent = (AgentNaming.WithoutVia(agentName.Trim(), account.Name) ?? agentName).Trim();
            if (agent.Length is 0 or > 40 || agent.Contains(" (via ", StringComparison.Ordinal) ||
                (account.Self && string.Equals(agent, _state.UserName, StringComparison.OrdinalIgnoreCase))) return Refuse("That isn't an agent's name.");
            author = new WikiAuthor(account.Self ? agent : AgentNaming.Via(agent, account.Name), true);
        }
        if (!account.Self && RoleOf(account) != GuestRole.Editor)
            return Refuse($"As a {RoleOf(account).ToString().ToLowerInvariant()} you can read the wiki, not change it.");
        var wiki = _state.Wiki;
        try
        {
            if (op == "create")
            {
                if (!Guid.TryParse(Text("projectId"), out var projectId) || Visible(account).All(p => p.Id != projectId))
                    return Refuse("Pages you start are about a project shared with you.");
                wiki.Create(Text("path") ?? "", Text("text") ?? "", author, projectId);
                _state.StatusText = $"{author.Name} started the wiki page “{WikiPaths.Normalize(Text("path") ?? "")}”.";
                return null;
            }
            if (!Guid.TryParse(Text("page"), out var id) || wiki.Pages.FirstOrDefault(p => p.Id == id) is not { } page || !SeesPage(account, page))
                return Refuse("That page isn't there any more.");
            switch (op)
            {
                case "save":
                    var result = wiki.Save(page, Text("text") ?? "", (int?)message["base"] ?? 0, author);
                    if (result.Outcome != WikiSaveOutcome.Unchanged) _state.StatusText = $"{author.Name} changed the wiki page “{page.Title}”.";
                    // Nothing changed: the guest still gets the page back, so it knows its save arrived.
                    if (result.Outcome == WikiSaveOutcome.Unchanged) session.Send(Wire.Message("wikiPage", ("page", PageJson(page))));
                    break;
                case "move":
                    wiki.Move(page, Text("path") ?? "");
                    break;
                case "restore":
                    wiki.Restore(page, (int?)message["version"] ?? 0, author);
                    break;
                case "resolve":
                    if (!Guid.TryParse(Text("clash"), out var clash)) return Refuse("Which clash?");
                    wiki.ResolveClash(page, clash, (bool?)message["take"] == true, author);
                    break;
                case "attach":
                    var files = new List<Attachment>();
                    foreach (var f in message["files"] as JsonArray ?? [])
                    {
                        if (!Guid.TryParse((string?)f?["id"], out var fileId)) continue;
                        var attachment = new Attachment { Id = fileId, FileName = ProjectStore.SafeFileName((string?)f!["name"] ?? "file"), Size = (long?)f["size"] ?? 0 };
                        // Only files it sent first (putFile), never someone else's.
                        if (File.Exists(_state.Store.AttachmentPath(attachment)) && page.Attachments?.Any(a => a.Id == fileId) != true) files.Add(attachment);
                    }
                    wiki.Attach(page, files);
                    break;
                default:
                    return Refuse("ProJict here doesn't know that.");
            }
            return null;
        }
        catch (WikiException ex)
        {
            return Refuse(ex.Message);
        }
    }

    /// <summary>A file on a page the guest may see.</summary>
    private Attachment? WikiAttachment(GuestSession session, Guid id) =>
        _state.Wiki.Pages.Where(p => SeesPage(session.Account, p)).SelectMany(p => p.Attachments ?? []).FirstOrDefault(a => a.Id == id);
}
