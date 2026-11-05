using System.IO;
using System.Runtime.InteropServices;
using System.Text.Json.Nodes;
using ProJict.Core.Agents;
using ProJict.Core.Model;
using ProJict.Core.Tools;

namespace ProJict.App.Services;

/// <summary>
/// ProJict's browser for agents (Settings › Tools, a tool in the browser): browser_open opens the tool's website in a
/// browser of its own, where the user signed in once; the agent reads the page (its text and numbered links, buttons and
/// fields) and clicks, types and waits by those numbers, as a person would. It stays on the tool's website, never types
/// into a password or card field and leaves paying to the user. What it downloads goes onto the item it names.
/// </summary>
public sealed partial class AgentTools
{
    private void AddBrowserTools()
    {
        _main.Browsers.Downloaded += OnBrowserDownload;
        const string rules = "You stay on the tool's website; paying (buy, upgrade, subscribe…), password and card fields, and signing in are the user's: ask them.";
        AddAsync("browser_open", "Open a tool in ProJict's browser",
            "Opens a tool used in the browser (Suno, Tripo3D: list_tools says which) in ProJict's own browser, where the user signed in once " +
            "(Settings › Tools › Sign in…), and reads the page: its title, text and numbered links, buttons and fields. Then use browser_click, " +
            "browser_type and browser_wait by those numbers, browser_read after the page changes, and browser_close when you're done. One agent at " +
            "a time uses a tool's browser. Name an item and what you download goes onto it. " + rules,
            Schema(("tool", "string", "The tool, e.g. Suno.", true),
                ("url", "string", "A page on its website to open; its home page if left out.", false),
                ("item", "string", "Item id: what you download in this browser goes onto it.", false)),
            BrowserOpen);
        AddAsync("browser_read", "Read the page in ProJict's browser",
            "The page a tool's browser is on now (after browser_open): its title, address, text and numbered links, buttons and fields.",
            Schema(("tool", "string", "The tool, e.g. Suno.", true)), BrowserRead);
        AddAsync("browser_click", "Click in ProJict's browser",
            "Clicks a link or button by its number, from the page as you last read it, as a person would, then reads the page again. " + rules,
            Schema(("tool", "string", "The tool, e.g. Suno.", true), ("ref", "integer", "The number in [ ] of the link or button.", true)), BrowserClick);
        AddAsync("browser_type", "Type in ProJict's browser",
            "Types into a field by its number, replacing what's in it, as a person would; submit presses Enter after. Then reads the page again. " + rules,
            Schema(("tool", "string", "The tool, e.g. Suno.", true), ("ref", "integer", "The number in [ ] of the field.", true),
                ("text", "string", "What to type.", true), ("submit", "boolean", "Press Enter after typing.", false)), BrowserType);
        AddAsync("browser_upload", "Upload a file in ProJict's browser",
            "Puts files into a page's file field, by its number from the page as you last read it: a file field itself, or the button that opens the " +
            "file picker (\"Select files\", \"Upload\"). The files must be in your project's folder or ProJict's attachments (an item's or a chat's " +
            "files): nothing else on this computer goes to a website. Then reads the page again. " + rules,
            Schema(("tool", "string", "The tool, e.g. YouTube.", true),
                ("ref", "integer", "The number in [ ] of the file field, or of the button that opens the file picker.", true),
                ("paths", "string[]", "The files: full paths, or relative to your working directory.", true)),
            BrowserUpload);
        AddAsync("browser_wait", "Wait in ProJict's browser",
            "Waits until the page shows a text (a song or model being ready, say), for up to 300 seconds, then reads the page. Wait again if it isn't there yet.",
            Schema(("tool", "string", "The tool, e.g. Suno.", true), ("text", "string", "Text to wait for, e.g. Download.", true),
                ("seconds", "integer", "How long to wait at most (60 if left out, 300 at most).", false)), BrowserWait);
        Add("browser_close", "Close ProJict's browser",
            "Closes a tool's browser when you're done with it, so another agent may use it (it also closes by itself after 10 minutes unused).",
            Schema(("tool", "string", "The tool, e.g. Suno.", true)), BrowserClose);
    }

    /// <summary>The tool a call names, used in the browser and with a website; refused otherwise.</summary>
    private ExternalTool BrowserTool(Args args)
    {
        var name = args.Required("tool");
        var tool = ToolBox.Data.Find(name) ?? throw new McpToolException(
            $"No tool “{name}”. " + (ToolBox.Tools.Count > 0 ? $"Tools: {string.Join(", ", ToolBox.Tools.Select(t => t.Name))}." : "The user hasn't set up any (Settings › Tools)."),
            ToolProblem.NotFound);
        if (tool.Access != ToolAccess.Browser) throw new McpToolException($"{tool.Name} isn't used in the browser: list_tools says how to reach it.");
        if (tool.Website == null) throw new McpToolException($"{tool.Name} has no website set yet: ask the user to set it (Settings › Tools).");
        return tool;
    }

    /// <summary>The tool's browser for the agent (started if it isn't running), or why it can't have it now.</summary>
    private async Task<BrowserSession> BrowserFor(McpSession session, ExternalTool tool)
    {
        try { return await _main.Browsers.TakeAsync(tool, session, DateTime.Now); }
        catch (BrowserException ex) { throw new McpToolException(ex.Message); }
    }

    /// <summary>Runs a step in the browser, saying what went wrong in words.</summary>
    private static async Task Step(Func<Task> step)
    {
        try { await step(); }
        catch (BrowserException ex) { throw new McpToolException(ex.Message); }
        catch (Exception ex) when (ex is COMException or InvalidOperationException or ObjectDisposedException)
        {
            throw new McpToolException($"ProJict's browser didn't respond ({ex.Message}). Try browser_read, or browser_open again.");
        }
    }

    private static BrowserElement ElementOf(BrowserSession browser, int reference)
    {
        try { return browser.Element(reference); }
        catch (BrowserException ex) { throw new McpToolException(ex.Message); }
    }

    private async Task<string> BrowserOpen(McpSession session, Args args)
    {
        var tool = BrowserTool(args);
        var address = args.String("url") ?? tool.Website!;
        if (!BrowserRules.IsOnSite(address, tool.Website))
            throw new McpToolException($"Agents stay on {tool.Name}'s website ({tool.Website}) in ProJict's browser; “{address}” isn't on it.");
        (Guid, Guid)? item = null;
        if (args.String("item") is { } id)
        {
            // What's downloaded goes on the item, so it must be one the agent may change (as with attach_file).
            var (project, loc) = FindItem(session, id);
            MayChange(project, Change.Edit);
            CheckMayWork(project, loc.Item);
            CheckOut(session, project, loc.Item, args);
            item = (project.Id, loc.Item.Id);
        }
        var browser = await BrowserFor(session, tool);
        if (item != null) browser.Item = item;
        await Step(() => browser.GoAsync(address));
        _state.StatusText = $"{session.AgentName} opened {tool.Name} in ProJict's browser.";
        return await PageAnswer(browser, $"Opened {address}.");
    }

    private async Task<string> BrowserRead(McpSession session, Args args)
    {
        var browser = await BrowserFor(session, BrowserTool(args));
        if (browser.Web?.Source is null or "about:blank") await Step(() => browser.GoAsync(browser.Tool.Website!));
        return await PageAnswer(browser, null);
    }

    private async Task<string> BrowserClick(McpSession session, Args args)
    {
        var tool = BrowserTool(args);
        var reference = args.Int("ref") ?? throw new McpToolException("Say which with ref: the number in [ ] on the page.");
        var browser = await BrowserFor(session, tool);
        var element = ElementOf(browser, reference);
        if (BrowserRules.LooksLikeSpending($"{element.Text} {element.Label} {element.Href}"))
            throw new McpToolException($"[{reference}] “{element.Text}” looks like paying (buying, upgrading, subscribing…), which is the user's to do: ask them if it's needed.",
                ToolProblem.Forbidden);
        if (element.Kind == "field") throw new McpToolException($"[{reference}] is a field: type in it with browser_type.");
        if (element.Disabled) throw new McpToolException($"[{reference}] “{element.Text}” can't be clicked right now (it's disabled): something on the page may be missing.");
        await Step(() => browser.ClickAsync(reference));
        return await PageAnswer(browser, $"Clicked [{reference}] “{element.Text}”.");
    }

    private async Task<string> BrowserType(McpSession session, Args args)
    {
        var tool = BrowserTool(args);
        var reference = args.Int("ref") ?? throw new McpToolException("Say which field with ref: the number in [ ] on the page.");
        var text = args.Required("text");
        var browser = await BrowserFor(session, tool);
        var element = ElementOf(browser, reference);
        if (element.Kind != "field") throw new McpToolException($"[{reference}] is a {element.Kind}, not a field to type in.");
        if (BrowserRules.IsSecretField(element.Type, element.Name, element.Label, element.Autocomplete))
            throw new McpToolException($"[{reference}] is a password, card or other secret field: those are the user's to fill in. Ask them.", ToolProblem.Forbidden);
        await Step(() => browser.TypeAsync(reference, text, args.Bool("submit") == true));
        return await PageAnswer(browser, $"Typed into [{reference}]{(element.Label.Length > 0 ? $" “{element.Label}”" : "")}{(args.Bool("submit") == true ? " and pressed Enter" : "")}.");
    }

    private async Task<string> BrowserWait(McpSession session, Args args)
    {
        var tool = BrowserTool(args);
        var text = args.Required("text");
        var seconds = Math.Clamp(args.Int("seconds") ?? 60, 1, 300);
        var browser = await BrowserFor(session, tool);
        var came = false;
        await Step(async () => came = await browser.WaitForAsync(text, TimeSpan.FromSeconds(seconds)));
        return await PageAnswer(browser, came ? $"“{text}” is on the page." : $"“{text}” isn't on the page after {seconds} seconds: read it, or wait again.");
    }

    /// <summary>Puts files from the agent's project folder or ProJict's attachments into a page's file field (#ae854b1c).</summary>
    private async Task<string> BrowserUpload(McpSession session, Args args)
    {
        var tool = BrowserTool(args);
        if (session.Remote)
            throw new McpToolException("Over the API you can't upload files that are on ProJict's computer to a website: ask the user to do it.", ToolProblem.Forbidden);
        var reference = args.Int("ref") ?? throw new McpToolException("Say which with ref: the number in [ ] of the file field, or of the button that opens the file picker.");
        var paths = args.Strings("paths");
        if (paths.Count == 0) throw new McpToolException("Give the files to upload in paths.");
        var browser = await BrowserFor(session, tool);
        var element = ElementOf(browser, reference);
        if (element.Kind is not ("file" or "button" or "link"))
            throw new McpToolException($"[{reference}] is a {element.Kind}: give the file field, or the button that opens the file picker.");
        if (BrowserRules.LooksLikeSpending($"{element.Text} {element.Label} {element.Href}"))
            throw new McpToolException($"[{reference}] “{element.Text}” looks like paying, which is the user's to do: ask them if it's needed.", ToolProblem.Forbidden);
        if (element.Disabled) throw new McpToolException($"[{reference}] “{element.Text}” can't be used right now (it's disabled): something on the page may be missing.");
        // Your project's folders, the agent's working folder, and what ProJict keeps: an item's files and Chat's.
        string?[] roots = [session.WorkingDirectory, _state.Store.AttachmentsDir, _state.Store.CacheRoot, .. LocalProjects.Select(p => p.RepoPath)];
        var files = new List<string>();
        foreach (var path in paths)
        {
            var full = Path.IsPathRooted(path) || session.WorkingDirectory == null ? path : Path.Combine(session.WorkingDirectory, path);
            if (BrowserRules.UploadProblem(full, roots) is { } problem) throw new McpToolException(problem, ToolProblem.Forbidden);
            files.Add(Path.GetFullPath(full));
        }
        var done = false;
        await Step(async () => done = await browser.UploadAsync(reference, files));
        if (!done)
            throw new McpToolException($"[{reference}] “{element.Text}” didn't open a file picker: read the page (browser_read) for its file field, or the button that opens one.");
        var names = string.Join(", ", files.Select(Path.GetFileName));
        if (browser.Item is { } target && _state.Projects.FirstOrDefault(p => p.Id == target.ProjectId) is { } project && project.Find(target.ItemId) != null)
            Edit(project, target.ItemId, i => i.AddActivity(browser.Agent, $"Uploaded {names} to {tool.Name} in ProJict's browser.", agent: true));
        _state.StatusText = $"{browser.Agent} uploaded {names} to {tool.Name}.";
        return await PageAnswer(browser, $"Put {names} into [{reference}]{(element.Text.Length > 0 || element.Label.Length > 0 ? $" “{(element.Text.Length > 0 ? element.Text : element.Label)}”" : "")}.");
    }

    private string BrowserClose(McpSession session, Args args, List<Attachment> files)
    {
        var tool = BrowserTool(args);
        if (_main.Browsers.SessionOf(tool) is { } browser && BrowserService.IsHeldBy(browser, session))
            _main.Browsers.Release(browser);
        return Json(new JsonObject { ["tool"] = tool.Name, ["closed"] = true });
    }

    /// <summary>What the agent is told after a step: the page as it is now, and anything it should know (kept on the site, downloads, signing in).</summary>
    private async Task<string> PageAnswer(BrowserSession browser, string? done)
    {
        BrowserPage page = null!;
        await Step(async () => page = await browser.ReadAsync());
        var answer = new JsonObject
        {
            ["tool"] = browser.Tool.Name,
            ["done"] = done,
            ["title"] = page.Title,
            ["url"] = page.Url,
            ["elements"] = new JsonArray(page.Elements.Select(e => (JsonNode)e.Line).ToArray()),
            ["text"] = page.Text,
        };
        if (browser.Blocked is { } blocked)
        {
            answer["keptOnSite"] = $"The page tried to go to {blocked}, off {browser.Tool.Name}'s website, and was kept on it. Signing in elsewhere is the user's to do.";
            browser.Blocked = null;
        }
        if (browser.News.Count > 0)
        {
            answer["downloads"] = new JsonArray(browser.News.Select(n => (JsonNode)n).ToArray());
            browser.News.Clear();
        }
        if (browser.Tool.SignedInAt == null)
            answer["signIn"] = $"The user hasn't signed in to {browser.Tool.Name} in ProJict's browser yet. If the site wants you signed in, ask them to " +
                               $"(Settings › Tools › {browser.Tool.Name} › Sign in…); never sign in yourself.";
        if (browser.Tool.Budget is { } budget) answer["budget"] = budget;
        answer["note"] = "Click and type by the numbers in elements (browser_click, browser_type); browser_read after the page changes; browser_close when you're done.";
        return Json(answer);
    }

    /// <summary>A download in a tool's browser finished: it goes onto the item the agent named, else it stays in the tool's downloads folder.</summary>
    private void OnBrowserDownload(BrowserSession browser, BrowserDownload download)
    {
        if (!download.Finished)
        {
            browser.News.Add($"{download.FileName} didn't download: {download.Problem ?? "it stopped"}.");
            return;
        }
        if (browser.Item is { } target && _state.Projects.FirstOrDefault(p => p.Id == target.ProjectId) is { } project && project.Find(target.ItemId) != null)
        {
            var itemId = target.ItemId;
            try
            {
                var attachment = _state.Store.WriteAttachment(download.FileName, File.ReadAllBytes(download.Path));
                Edit(project, itemId, i =>
                {
                    (i.Attachments ??= []).Add(attachment);
                    i.AddActivity(browser.Agent, $"Downloaded {download.FileName} from {browser.Tool.Name} in ProJict's browser.", agent: true);
                });
                File.Delete(download.Path);
                browser.News.Add($"{download.FileName}: attached to the item.");
                _state.StatusText = $"{browser.Agent} downloaded {download.FileName} from {browser.Tool.Name}.";
                return;
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
            {
                browser.News.Add($"{download.FileName} downloaded, but couldn't go on the item ({ex.Message}); it's at {download.Path}.");
                return;
            }
        }
        browser.News.Add($"{download.FileName} downloaded to {download.Path} (name an item in browser_open and downloads go onto it).");
    }
}
