using System.IO;
using System.Text.Json.Nodes;
using ProJict.App.Services.Sharing;
using ProJict.Core.Agents;
using ProJict.Core.Model;
using ProJict.Core.Wiki;

namespace ProJict.App.Services;

/// <summary>
/// The wiki for agents: list_wiki, read_wiki and write_wiki (a whole page, or a section by heading). Every save is a
/// version signed with the agent's name and waits for the user's review; writing on top of an older version merges by
/// paragraph, and a real clash keeps both for the user to pick. An API token limited to some projects reaches only the
/// pages about them. Pages of ProJicts that share theirs with this one are read as "Renderer (Sam's)", and written there
/// as "Ada (via Alice)" when that ProJict lets guests' agents work and Alice is an editor there.
/// </summary>
public sealed partial class AgentTools
{
    private static readonly HashSet<string> WikiTools = ["list_wiki", "read_wiki", "write_wiki"];

    private void AddWikiTools()
    {
        Add("list_wiki", "List wiki pages",
            "The wiki's pages: one wiki for the whole workspace, with pages in folders (e.g. GRID2VR/Architecture/Renderer), each perhaps about a project. " +
            "Gives each page's path, project, version, who changed it last and whether it waits for the user's review. Filter by folder, project or words. " +
            "Pages of other ProJicts that share theirs with this one are listed too, named like \"Renderer (Sam's)\".",
            Schema(("folder", "string", "Only pages in this folder and below, e.g. GRID2VR/Architecture.", false),
                ("project", "string", "Only pages about this project (name or key).", false),
                ("query", "string", "Words that must all appear in the page's path or text.", false),
                ("limit", "integer", "Most pages to return (default 100).", false)),
            ListWiki, readOnly: true);
        Add("read_wiki", "Read wiki page",
            "One wiki page: its Markdown text, its version (pass it to write_wiki), project, files and the pages it links to with [[…]]. " +
            "With section, only the part under that heading (up to the next heading of the same or a higher level).",
            Schema(("page", "string", "The page: its path (GRID2VR/Architecture/Renderer), its title when only one page has it, or its id.", true),
                ("section", "string", "Only the section under this heading.", false)),
            ReadWiki, readOnly: true);
        Add("write_wiki", "Write wiki page",
            "Creates or changes a wiki page, in Markdown: the whole page, or one section by its heading (replaced, or added to with mode=append; a missing section is " +
            "added at the end). Links: [[Page name]] for pages, # and an item's 8-character id for items (#4e84570c), #name for chat channels; files you attach show " +
            "with ![](file name) or link with [name](file name). Pass the version you read: if someone changed the page since, your change is merged with theirs " +
            "paragraph by paragraph, and a real clash keeps both versions for the user to pick. Every save is a version signed with your name, and pages you " +
            "change wait for the user's review. Write for people: headings, short paragraphs and lists. Diagrams: a ```mermaid block (flowchart, sequence, class, " +
            "state, ER, gantt, …) is drawn in the page; read_wiki then says if one doesn't draw, and why.",
            Schema(("page", "string", "The page's path, e.g. GRID2VR/Architecture/Renderer (folders are made as needed); or an existing page's title or id.", true),
                ("text", "string", "Markdown: the whole page, or the section's text.", true),
                ("section", "string", "Write only the section under this heading.", false),
                ("mode", "enum:replace|append", "replace (default) or append to the page or section.", false),
                ("version", "integer", "The version you read (read_wiki gives it); leave it out to write on top of whatever the page is now.", false),
                ("project", "string", "For a new page: the project it's about (it then shows on the project and is shared as the project is); " +
                                      "defaults to the project matching your working directory.", false),
                ("attachments", "string[]", "Files for the page, e.g. pictures it shows (absolute paths or relative to your working directory).", false)),
            WriteWiki, filesArgument: "attachments");
    }

    /// <summary>Your own pages the caller reaches: all of them, or for an API token limited to some projects, the pages about those.</summary>
    private IEnumerable<WikiPage> WikiPagesFor(McpSession session) =>
        _state.Wiki.Pages.Where(p => session.Projects is not { } only || p.ProjectId is { } id && only.Contains(id));

    /// <summary>The other ProJicts whose wikis agents here can reach (not an API token limited to some projects).</summary>
    private IEnumerable<HostConnection> WikiHosts(McpSession session) =>
        session.Projects != null ? [] : _main.Guest.Connections.Where(c => c.State == ConnectionState.Connected && c.Wiki != null);

    /// <summary>"Renderer (Sam's)".</summary>
    private static string SharedPageName(HostConnection host, WikiPage page) => $"{page.Path} ({host.HostName}'s)";

    /// <summary>The page a "page" argument names, and the ProJict it's in (null: this one); null when there's no such page.</summary>
    private (WikiPage Page, HostConnection? Host)? FindPage(McpSession session, string key)
    {
        key = key.Trim();
        if (key.EndsWith("'s)", StringComparison.Ordinal) && key.LastIndexOf(" (", StringComparison.Ordinal) is > 0 and var open)
        {
            var hostName = key[(open + 2)..^3];
            if (session.Projects != null)
                throw new McpToolException("This API token reaches only some projects, so other ProJicts' wikis are outside it.", ToolProblem.Forbidden);
            var host = WikiHosts(session).FirstOrDefault(h => string.Equals(h.HostName, hostName, StringComparison.OrdinalIgnoreCase))
                       ?? throw new McpToolException($"This ProJict isn't connected to {hostName}'s right now; list_wiki shows the pages you can reach.", ToolProblem.NotFound);
            var shared = host.Wiki!.FindAll(key[..open]);
            return shared.Count switch
            {
                1 => (shared[0], host),
                0 => null,
                _ => throw new McpToolException($"Several pages in {hostName}'s wiki are called that: {string.Join(", ", shared.Select(p => p.Path))}. Give the whole path."),
            };
        }
        var reachable = WikiPagesFor(session).ToHashSet();
        var matches = _state.Wiki.FindAll(key).Where(reachable.Contains).ToList();
        return matches.Count switch
        {
            1 => (matches[0], null),
            0 => null,
            _ => throw new McpToolException($"Several pages are called that: {string.Join(", ", matches.Select(p => p.Path))}. Give the whole path."),
        };
    }

    /// <summary>The page a name means, for the Agents view: none rather than an error.</summary>
    private WikiPage? FindPageQuietly(McpSession session, string key)
    {
        try { return FindPage(session, key)?.Page; }
        catch (McpToolException) { return null; }
    }

    private (WikiPage Page, HostConnection? Host) PageFor(McpSession session, Args args) =>
        FindPage(session, args.Required("page")) ?? throw new McpToolException(
            $"No page “{args.Required("page")}”. list_wiki shows the pages; write_wiki starts a new one.", ToolProblem.NotFound);

    private string ProjectNameOf(Guid? id) => id is { } p ? _state.Projects.FirstOrDefault(x => x.Id == p)?.Name ?? "(a project that's gone)" : "";

    private JsonObject PageSummary(WikiPage page, HostConnection? host) => new()
    {
        ["page"] = host != null ? SharedPageName(host, page) : page.Path,
        ["id"] = ShortId(page.Id),
        ["project"] = page.ProjectId != null ? ProjectNameOf(page.ProjectId) : null,
        ["version"] = page.Version,
        ["updated"] = Time(page.UpdatedAt),
        ["updatedBy"] = page.UpdatedBy,
        ["characters"] = page.Text.Length,
        ["waitsForReview"] = page.NeedsReview ? true : null,
        ["clashes"] = page.HasClashes ? page.Clashes!.Count : null,
        ["sharedBy"] = host != null ? $"{host.HostName}'s ProJict" : null,
    };

    private string ListWiki(McpSession session, Args args, List<Attachment> files)
    {
        var folder = args.String("folder") is { } f ? WikiPaths.Normalize(f) : "";
        Project? project = args.String("project") != null ? ResolveProject(session, args) : null;
        var query = args.String("query");
        var limit = Math.Clamp(args.Int("limit") ?? 100, 1, 500);
        IEnumerable<(WikiPage Page, HostConnection? Host)> pages = WikiPagesFor(session).Select(p => (p, (HostConnection?)null))
            .Concat(WikiHosts(session).SelectMany(h => h.Wiki!.Pages.Select(p => (p, (HostConnection?)h))));
        pages = pages.Where(x => WikiPaths.IsIn(x.Page.Path, folder) && (project == null || x.Page.ProjectId == project.Id));
        if (query != null)
        {
            var found = WikiText.Search(pages.Select(x => x.Page), query, int.MaxValue).Select(h => h.Page).ToHashSet();
            pages = pages.Where(x => found.Contains(x.Page));
        }
        var list = pages.OrderBy(x => x.Host != null).ThenBy(x => x.Page.Path, StringComparer.OrdinalIgnoreCase).ToList();
        var result = new JsonObject
        {
            ["pages"] = new JsonArray(list.Take(limit).Select(x => (JsonNode)PageSummary(x.Page, x.Host)).ToArray()),
            ["folders"] = new JsonArray(_state.Wiki.Folders().Where(x => WikiPaths.IsIn(x, folder)).Take(200).Select(x => (JsonNode)x).ToArray()),
        };
        if (list.Count > limit) result["more"] = list.Count - limit;
        if (list.Count == 0) result["note"] = _state.Wiki.Pages.Count == 0 ? "The wiki is empty: write_wiki starts a page." : "No pages match.";
        return Json(result);
    }

    private string ReadWiki(McpSession session, Args args, List<Attachment> files)
    {
        var (page, host) = PageFor(session, args);
        if (page.ProjectId is { } id && _state.Projects.FirstOrDefault(p => p.Id == id) is { } project) _main.Agents.WorkingIn(session, project);
        var json = PageSummary(page, host);
        if (args.String("section") is { } heading)
        {
            json["section"] = WikiText.ReadSection(page.Text, heading) ?? throw new McpToolException(
                $"The page has no heading “{heading}”. Its headings: {string.Join(", ", WikiText.Headings(page.Text).Select(h => h.Text))}.", ToolProblem.NotFound);
        }
        else json["text"] = page.Text;
        json["headings"] = new JsonArray(WikiText.Headings(page.Text).Select(h => (JsonNode)$"{new string('#', h.Level)} {h.Text}").ToArray());
        json["links"] = new JsonArray(WikiText.Links(page.Text).Select(l => (JsonNode)new JsonObject
        {
            ["link"] = l,
            ["page"] = (host?.Wiki ?? _state.Wiki).Resolve(l, page)?.Path,
        }).ToArray());
        json["files"] = new JsonArray((page.Attachments ?? []).Select(a => (JsonNode)new JsonObject
        {
            ["name"] = a.FileName,
            ["size"] = a.Size,
            ["path"] = _caller is { Remote: true } remote ? $"{remote.BaseUrl}{ProJict.Core.Api.ApiRoutes.Base}/attachments/{a.Id:N}"
                : host != null ? _main.Guest.WikiFilePath(host, a) : _state.Store.AttachmentPath(a),
        }).ToArray());
        if (host != null) foreach (var a in page.Attachments ?? []) _main.Guest.FetchWikiFile(host, a);
        if (DiagramsJson(page.Text) is { } diagrams) json["diagrams"] = diagrams;
        if (page.HasClashes) json["note"] = "Two edits of this page clashed; the user picks which to keep. The text is the page as it is now.";
        return Json(json);
    }

    private string WriteWiki(McpSession session, Args args, List<Attachment> files)
    {
        var key = args.Required("page");
        var text = args.Required("text").Replace("\r\n", "\n");
        var section = args.String("section");
        var append = args.String("mode") switch
        {
            null or "replace" => false,
            "append" => true,
            _ => throw new McpToolException("mode must be replace or append."),
        };
        var version = args.Int("version") ?? 0;
        var found = FindPage(session, key);
        string Compose(string current) =>
            section != null ? WikiText.ReplaceSection(current, section, text, append)
            : append ? (current.TrimEnd().Length > 0 ? current.TrimEnd() + "\n\n" : "") + text.Trim('\n') + "\n"
            : text;

        if (found is { Host: { } host } shared)
            return WriteShared(session, host, shared.Page, Compose(shared.Page.Text), version, files);

        if (found is { } own)
        {
            var page = own.Page;
            if (page.ProjectId is { } pid && _state.Projects.FirstOrDefault(p => p.Id == pid) is { } project) _main.Agents.WorkingIn(session, project);
            // A section is written into the version the agent read, so the merge sees just that change.
            var start = version > 0 && page.VersionAt(version) is { } read ? read.Text : page.Text;
            var result = _state.Wiki.Save(page, Compose(start), version, new WikiAuthor(session.AgentName, true));
            DrawDiagrams(page.Text);
            if (files.Count > 0) _state.Wiki.Attach(page, files);
            _state.StatusText = $"{session.AgentName} {(result.Outcome == WikiSaveOutcome.Clashed ? "clashed with an edit of" : "changed")} the wiki page “{page.Title}”.";
            return Json(new JsonObject
            {
                ["page"] = page.Path,
                ["outcome"] = result.Outcome.ToString().ToLowerInvariant(),
                ["version"] = page.Version,
                ["note"] = result.Describe(),
                ["files"] = files.Count > 0 ? new JsonArray(files.Select(f => (JsonNode)f.FileName).ToArray()) : null,
            });
        }

        // A new page.
        if (section != null) text = WikiText.ReplaceSection("", section, text);
        var path = WikiPaths.Normalize(key);
        if (WikiPaths.Problem(path) is { } problem) throw new McpToolException(problem);
        Project? about = args.String("project") != null ? ResolveProject(session, args)
            : ProjectMatcher.ForFolder(LocalProjects, session.WorkingDirectory)?.Project;
        if (about is { Remote: not null })
            throw new McpToolException($"{about.Name} is shared by {about.Remote.HostName}: start pages about it in their wiki, as \"{path} ({about.Remote.HostName}'s)\".");
        if (session.Projects != null && about == null)
            throw new McpToolException("This API token reaches only some projects: say which one the page is about with \"project\".", ToolProblem.Forbidden);
        if (about != null) _main.Agents.WorkingIn(session, about);
        try
        {
            var page = _state.Wiki.Create(path, text, new WikiAuthor(session.AgentName, true), about?.Id);
            DrawDiagrams(page.Text);
            if (files.Count > 0) _state.Wiki.Attach(page, files);
            _state.StatusText = $"{session.AgentName} started the wiki page “{page.Title}”.";
            return Json(new JsonObject
            {
                ["page"] = page.Path,
                ["outcome"] = "created",
                ["version"] = page.Version,
                ["project"] = about?.Name,
                ["note"] = about != null ? $"It's about {about.Name}: it shows there and is shared as {about.Name} is." : "It's about no project, so it stays on this computer unless the user shares it.",
            });
        }
        catch (WikiException ex) { throw new McpToolException(ex.Message); }
    }

    /// <summary>Starts drawing a page's Mermaid diagrams right away, so read_wiki can say soon if one doesn't draw.</summary>
    private void DrawDiagrams(string text)
    {
        foreach (var diagram in WikiText.Diagrams(text).Where(d => d.Language!.Equals("mermaid", StringComparison.OrdinalIgnoreCase)))
            _main.Diagrams.Request(diagram.Language!, diagram.Code, ProJict.App.Views.MermaidDiagram.IsDark());
    }

    /// <summary>The page's diagrams and how they're doing: drawn, being drawn, or why not; null when it has none.</summary>
    private JsonArray? DiagramsJson(string text)
    {
        var diagrams = WikiText.Diagrams(text);
        if (diagrams.Count == 0) return null;
        DrawDiagrams(text);
        var dark = ProJict.App.Views.MermaidDiagram.IsDark();
        return new JsonArray(diagrams.Select((d, i) =>
        {
            var key = DiagramService.Key(d.Language!, d.Code, dark);
            var drawable = d.Language!.Equals("mermaid", StringComparison.OrdinalIgnoreCase);
            var error = drawable ? _main.Diagrams.ErrorFor(key) : null;
            return (JsonNode)new JsonObject
            {
                ["diagram"] = i + 1,
                ["language"] = d.Language,
                ["state"] = !drawable ? "not drawn yet (only mermaid diagrams are, for now)" : error != null ? "doesn't draw" : _main.Diagrams.PictureFor(key) != null ? "drawn" : "being drawn",
                ["error"] = error,
            };
        }).ToArray());
    }

    /// <summary>A page of another ProJict: written there through this ProJict's user, when that ProJict and their role allow it.</summary>
    private string WriteShared(McpSession session, HostConnection host, WikiPage page, string text, int version, List<Attachment> files)
    {
        if (!host.AgentsAllowed && !host.IsSelf)
            throw new McpToolException($"{host.HostName} doesn't let guests' AI agents change what they share: you can read their wiki, not write it.", ToolProblem.Forbidden);
        if (!host.CanEditWiki)
            throw new McpToolException($"You work in {host.HostName}'s ProJict through {host.MyName}, who is a {host.Role.ToString().ToLowerInvariant()} there, " +
                                       "so you can read their wiki, not write it (only editors do).", ToolProblem.Forbidden);
        _main.Guest.WikiOp(host, "save", session.AgentName, ("page", page.Id.ToString()), ("text", text), ("base", version));
        if (files.Count > 0)
            _main.Guest.WikiAttach(host, page, files.Select(f => (f.FileName, File.ReadAllBytes(_state.Store.AttachmentPath(f)))).ToList());
        return Json(new JsonObject
        {
            ["page"] = SharedPageName(host, page),
            ["outcome"] = "sent",
            ["note"] = $"Sent to {host.HostName}'s ProJict, which saves it as {ViaIn(session, host)} (merged with any edit since version {(version > 0 ? version : page.Version)}). read_wiki shows it once it's there.",
        });
    }
}
