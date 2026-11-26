using System.IO;
using System.Net;
using System.Net.Http;
using System.Net.Sockets;
using System.Runtime.CompilerServices;
using System.Text.Json.Nodes;
using ProJict.Core.Agents;
using ProJict.Core.Model;
using ProJict.Core.Tools;

namespace ProJict.App.Services;

/// <summary>
/// Image search for agents: image_search finds pictures on the web (Bing Images) or free to use with their licence
/// (Openverse), and shows them numbered on one contact sheet the agent sees; image_view looks closer at some; image_save
/// puts the ones it picks onto an item or into its project's folder, with where each came from and its licence in the
/// item's history. Nothing goes to the user's own network or this computer's sites (Settings › Tools turns it off).
/// </summary>
public sealed partial class AgentTools
{
    /// <summary>Each agent's latest search, whose numbers image_view and image_save go by.</summary>
    private readonly ConditionalWeakTable<McpSession, ImageSearchDone> _imageSearches = [];

    private sealed record ImageSearchDone(ImageQuery Query, List<ImageResult> Results);

    private const int MaxImageBytes = 25 * 1024 * 1024, MaxPageBytes = 4 * 1024 * 1024, MaxThumbBytes = 2 * 1024 * 1024;

    /// <summary>
    /// The web, for image search: never another device on the user's network nor this computer (checked on the address
    /// it actually connects to, so a name can't lead there), and redirects followed one at a time so each is checked.
    /// </summary>
    private static readonly HttpClient WebClient = MakeWebClient();

    private static HttpClient MakeWebClient()
    {
        var handler = new SocketsHttpHandler
        {
            AllowAutoRedirect = false,
            AutomaticDecompression = DecompressionMethods.All,
            ConnectTimeout = TimeSpan.FromSeconds(10),
            ConnectCallback = async (context, token) =>
            {
                var host = context.DnsEndPoint.Host;
                var addresses = IPAddress.TryParse(host.Trim('[', ']'), out var literal) ? [literal] : await Dns.GetHostAddressesAsync(host, token);
                if (addresses.Length == 0 || addresses.Any(BrowserRules.IsPrivate))
                    throw new HttpRequestException($"{host} is on the user's own network or this computer: agents don't fetch from there.");
                var socket = new Socket(SocketType.Stream, ProtocolType.Tcp) { NoDelay = true };
                try
                {
                    await socket.ConnectAsync(addresses, context.DnsEndPoint.Port, token);
                    return new NetworkStream(socket, ownsSocket: true);
                }
                catch
                {
                    socket.Dispose();
                    throw;
                }
            },
        };
        var client = new HttpClient(handler) { Timeout = TimeSpan.FromSeconds(25) };
        client.DefaultRequestHeaders.UserAgent.ParseAdd("Mozilla/5.0 (Windows NT 10.0; Win64; x64) AppleWebKit/537.36 (KHTML, like Gecko) Chrome/130.0.0.0 Safari/537.36 Edg/130.0.0.0");
        client.DefaultRequestHeaders.AcceptLanguage.ParseAdd("en-US,en;q=0.8");
        return client;
    }

    private void AddImageTools()
    {
        AddAsync("image_search", "Search for images",
            "Searches for pictures and shows you the results numbered on one contact sheet (a picture you see), with each one's title, size, site, " +
            "address and page. source=web is Bing Images: anything on the web, belonging to its makers (licences unknown unless you filter by " +
            "license). source=free is Openverse: Creative Commons and public domain pictures (Flickr, Wikimedia Commons and others), each with its " +
            "licence and the attribution line to credit it; use it for anything that will be shipped or published. Filter by size, kind (photo, " +
            "clipart, line, transparent, animated, illustration), shape and colour. Then image_view to look closer at some, and image_save to keep the " +
            "ones you pick (onto an item, or into your project's folder). page=2 for more.",
            Schema(("query", "string", "What to look for, e.g. red fox in snow.", true),
                ("source", "enum:web|free", "web (Bing Images, the default) or free (Openverse: free to use, with licences).", false),
                ("license", "enum:any|share|commercial|modify", "What you'll do with it: share it, use it commercially, or change it too. any if left out.", false),
                ("size", "enum:small|medium|large|wallpaper", "Its size.", false),
                ("kind", "enum:photo|clipart|line|transparent|animated|illustration", "What kind of picture.", false),
                ("shape", "enum:square|wide|tall", "Its shape.", false),
                ("color", "enum:color|bw|red|orange|yellow|green|teal|blue|purple|pink|brown|black|gray|white", "Its main colour, or color/bw (web only).", false),
                ("page", "integer", "Which page of results (1 if left out).", false)),
            ImageSearchTool, readOnly: true);
        AddAsync("image_view", "Look at images",
            "Shows you up to 4 pictures bigger than on the contact sheet: by their numbers from your last image_search, or by address (an image's " +
            "url, one from browser_read images=true). You get each picture and its real size.",
            Schema(("numbers", "integer[]", "Numbers from your last image_search.", false),
                ("urls", "string[]", "Pictures' addresses instead.", false)),
            ImageView, readOnly: true);
        AddAsync("image_save", "Save images",
            "Saves pictures in full: by their numbers from your last image_search, or by address. Onto an item (attached, with where each came from " +
            "and its licence in the item's history), or into a folder in your project (folder, relative to your working directory). Credit the " +
            "pictures as their licence asks (the answer gives each one's attribution where known); pictures from source=web belong to their makers: " +
            "use them only as the user agrees.",
            Schema(("numbers", "integer[]", "Numbers from your last image_search.", false),
                ("urls", "string[]", "Pictures' addresses instead.", false),
                ("item", "string", "Item id to attach them to.", false),
                ("folder", "string", "A folder in your project to save them in, e.g. assets/reference.", false),
                ("name", "string", "A file name for one picture (its extension is added).", false)),
            ImageSave);
    }

    private const string ImagesOff = "Right now the user has image search off (Settings › Tools): ask them if you need it.";

    /// <summary>What the user lets agents do in ProJict's web browser now, in words for them.</summary>
    private string WebBrowsingNow => _state.AppSettings.AgentsBrowseWeb switch
    {
        WebBrowsing.Web => "Right now the user lets you open the web and this computer's own sites.",
        WebBrowsing.ThisComputer => "Right now the user lets you open only this computer's own sites (localhost), not the web: ask them if you need it.",
        _ => "Right now the user has ProJict's web browser off (Settings › Tools › Agents browse the web): ask them to turn it on if you need it. " +
             "Tools' websites work either way.",
    };

    /// <summary>ProJict's web browser and image search as list_tools and get_started describe them: whether the user allows them now.</summary>
    private JsonObject WebToolsJson() => new()
    {
        ["webBrowser"] = "browser_open url=… without a tool: a private web browser in ProJict for agents without their own. " + WebBrowsingNow,
        ["imageSearch"] = _state.AppSettings.AgentsSearchImages
            ? "image_search finds pictures (source=web: Bing Images; source=free: Openverse, with licences); image_view and image_save."
            : ImagesOff,
    };

    private void RequireImageSearch()
    {
        if (!_state.AppSettings.AgentsSearchImages)
            throw new McpToolException("The user hasn't let agents search for images (Settings › Tools › Agents may search for images). Ask them if you need it.", ToolProblem.Forbidden);
    }

    /// <summary>An address on the web image search may fetch: http(s), not this computer nor the user's network.</summary>
    private static Uri WebAddress(string address)
    {
        if (!Uri.TryCreate(address.Trim(), UriKind.Absolute, out var url) || url.Scheme is not ("http" or "https"))
            throw new McpToolException($"“{address}” isn't a picture's web address (http or https).");
        if (BrowserRules.IsThisComputer(url.Host) || BrowserRules.IsLocalNetwork(url.Host))
            throw new McpToolException($"{url.Host} is this computer or on the user's own network: agents don't fetch pictures from there.", ToolProblem.Forbidden);
        return url;
    }

    /// <summary>Fetches from the web, following up to 5 redirects (each checked), and reading at most <paramref name="maxBytes"/>.</summary>
    private static async Task<(byte[] Data, string? MediaType, Uri Address)> FetchAsync(Uri url, int maxBytes, string accept, Uri? referrer = null)
    {
        for (var hop = 0; hop < 6; hop++)
        {
            using var request = new HttpRequestMessage(HttpMethod.Get, url);
            request.Headers.Accept.ParseAdd(accept);
            if (referrer != null) request.Headers.Referrer = referrer;
            using var response = await WebClient.SendAsync(request, HttpCompletionOption.ResponseHeadersRead);
            if ((int)response.StatusCode is >= 300 and < 400 && response.Headers.Location is { } next)
            {
                url = WebAddress(new Uri(url, next).ToString());
                continue;
            }
            if (!response.IsSuccessStatusCode) throw new HttpRequestException($"{url.Host} answered {(int)response.StatusCode} {response.ReasonPhrase}.");
            if (response.Content.Headers.ContentLength > maxBytes) throw new HttpRequestException($"It's bigger than {maxBytes / 1024 / 1024} MB.");
            await using var stream = await response.Content.ReadAsStreamAsync();
            using var memory = new MemoryStream();
            var buffer = new byte[81920];
            int read;
            while ((read = await stream.ReadAsync(buffer)) > 0)
            {
                memory.Write(buffer, 0, read);
                if (memory.Length > maxBytes) throw new HttpRequestException($"It's bigger than {maxBytes / 1024 / 1024} MB.");
            }
            return (memory.ToArray(), response.Content.Headers.ContentType?.MediaType, url);
        }
        throw new HttpRequestException("It redirected too many times.");
    }

    private static T? Pick<T>(Args args, string name, IReadOnlyList<string> allowed, Func<string, T> make) where T : class
    {
        if (args.String(name)?.Trim().ToLowerInvariant() is not { Length: > 0 } value || value == "any") return null;
        return allowed.Contains(value) ? make(value) : throw new McpToolException($"{name} is one of: {string.Join(", ", allowed)}.");
    }

    private async Task<string> ImageSearchTool(McpSession session, Args args)
    {
        RequireImageSearch();
        var words = args.Required("query").Trim();
        var source = args.String("source")?.Trim().ToLowerInvariant() is "free" or "openverse" or "cc" ? ImageSource.Free : ImageSource.Web;
        var license = args.String("license")?.Trim().ToLowerInvariant() switch
        {
            null or "" or "any" => ImageLicense.Any,
            "share" => ImageLicense.Share,
            "commercial" => ImageLicense.Commercial,
            "modify" => ImageLicense.Modify,
            var other => throw new McpToolException($"license is any, share, commercial or modify, not “{other}”."),
        };
        var query = new ImageQuery(words, source, license,
            Pick(args, "size", ImageSearch.Sizes, s => s), Pick(args, "kind", ImageSearch.Kinds, s => s), Pick(args, "shape", ImageSearch.Shapes, s => s),
            Pick(args, "color", ImageSearch.Colors, s => s), Math.Clamp(args.Int("page") ?? 1, 1, 20));
        List<ImageResult> results;
        try
        {
            var (data, _, _) = await FetchAsync(new Uri(ImageSearch.Address(query)), MaxPageBytes, source == ImageSource.Free ? "application/json" : "text/html");
            results = ImageSearch.Read(source, System.Text.Encoding.UTF8.GetString(data));
        }
        catch (Exception ex) when (ex is HttpRequestException or TaskCanceledException or IOException or System.Text.Json.JsonException)
        {
            throw new McpToolException($"The image search ({(source == ImageSource.Free ? "Openverse" : "Bing Images")}) didn't answer: {ex.Message} Try again, or the other source.");
        }
        _imageSearches.AddOrUpdate(session, new ImageSearchDone(query, results));
        var answer = new JsonObject
        {
            ["query"] = words,
            ["source"] = source == ImageSource.Free ? "free (Openverse)" : "web (Bing Images)",
            ["page"] = query.Page,
        };
        if (results.Count == 0)
        {
            answer["note"] = "Nothing found: try other words, fewer filters" + (source == ImageSource.Web ? ", or source=free." : ", or source=web.");
            return Json(answer);
        }
        // The small pictures, a few at a time, for the contact sheet.
        using var gate = new SemaphoreSlim(8);
        var thumbs = await Task.WhenAll(results.Select(async r =>
        {
            await gate.WaitAsync();
            try { return (await FetchAsync(WebAddress(r.ThumbnailUrl ?? r.ImageUrl), MaxThumbBytes, "image/*")).Data; }
            catch (Exception ex) when (ex is HttpRequestException or TaskCanceledException or IOException or McpToolException or UriFormatException) { return null; }
            finally { gate.Release(); }
        }));
        var sheet = WebPictures.ContactSheet([.. results.Select((r, i) => new WebPictures.Tile(r.Number, thumbs[i], string.Join(" · ", new[] { r.SizeText, r.License ?? r.Site }.OfType<string>())))]);
        var path = Path.Combine(_main.Browsers.PicturesFolder, $"images-{DateTime.Now:yyyyMMdd-HHmmss}.jpg");
        await File.WriteAllBytesAsync(path, sheet);
        answer["results"] = new JsonArray(results.Select(r =>
        {
            var json = new JsonObject { ["n"] = r.Number, ["title"] = r.Title };
            if (r.SizeText is { } size) json["size"] = size;
            json["site"] = r.Site;
            if (r.License is { } l) json["license"] = l;
            if (r.Attribution is { } a) json["attribution"] = a;
            else if (r.Creator is { } c) json["creator"] = c;
            json["image"] = r.ImageUrl;
            if (r.PageUrl is { } p) json["page"] = p;
            return (JsonNode)json;
        }).ToArray());
        answer["contactSheet"] = path;
        answer["licences"] = source == ImageSource.Free
            ? "Each picture has its licence: credit it with its attribution line (CC BY and BY-SA ask for that). NC isn't for commercial use, ND not to change, SA shares alike; CC0 and PDM are free for anything."
            : license == ImageLicense.Any
                ? "Pictures from the web belong to their makers: fine to look at for reference, but for anything shipped or published, search with source=free or license=commercial, and check the picture's page."
                : "Bing's licence filter isn't a guarantee: check the licence on the picture's page before using it.";
        answer["next"] = "The contact sheet follows (open the file if you can't see pictures). image_view numbers=[…] to look closer, image_save numbers=[…] item=… or folder=… to keep some, page=" + (query.Page + 1) + " for more.";
        McpImages.Add(answer, sheet, "image/jpeg");
        return Json(answer);
    }

    /// <summary>The pictures a call names: numbers from the agent's last search, and addresses.</summary>
    private List<(ImageResult? Result, Uri Address)> ImagesNamed(McpSession session, Args args, int most)
    {
        var picked = new List<(ImageResult?, Uri)>();
        var numbers = args.Ints("numbers");
        if (numbers.Count > 0)
        {
            if (!_imageSearches.TryGetValue(session, out var done) || done.Results.Count == 0)
                throw new McpToolException("You haven't searched for images yet (image_search): give the pictures' addresses in urls instead.");
            foreach (var n in numbers.Distinct())
            {
                var result = done.Results.FirstOrDefault(r => r.Number == n)
                             ?? throw new McpToolException($"There's no {n} in your last image search (“{done.Query.Words}”, 1 to {done.Results.Count}).");
                picked.Add((result, WebAddress(result.ImageUrl)));
            }
        }
        foreach (var url in args.Strings("urls")) picked.Add((null, WebAddress(url)));
        if (picked.Count == 0) throw new McpToolException("Say which pictures: numbers from your last image_search, or urls.");
        if (picked.Count > most) throw new McpToolException($"At most {most} pictures at a time.");
        return picked;
    }

    private async Task<string> ImageView(McpSession session, Args args)
    {
        RequireImageSearch();
        var picked = ImagesNamed(session, args, 4);
        var answer = new JsonObject();
        var shown = new JsonArray();
        var pictures = new List<byte[]>();
        foreach (var (result, address) in picked)
        {
            var json = new JsonObject();
            if (result != null) json["n"] = result.Number;
            json["image"] = address.AbsoluteUri;
            try
            {
                var (data, media, _) = await FetchAsync(address, MaxImageBytes, "image/*", result?.PageUrl is { } page && Uri.TryCreate(page, UriKind.Absolute, out var p) ? p : null);
                if (ImageSearch.Extension(media, data) is not { } extension || WebPictures.Size(data) is not { } size)
                {
                    json["problem"] = "It isn't a picture ProJict can show (it may be a web page, or an SVG).";
                    shown.Add(json);
                    continue;
                }
                json["size"] = $"{size.Width}×{size.Height}";
                json["type"] = extension;
                json["bytes"] = data.Length;
                if (result?.License is { } l) json["license"] = l;
                pictures.Add(WebPictures.Fit(data, 1200, 1200));
            }
            catch (Exception ex) when (ex is HttpRequestException or TaskCanceledException or IOException or McpToolException)
            {
                json["problem"] = $"It didn't come: {ex.Message}" + (result?.ThumbnailUrl != null ? " Its small picture is on the contact sheet." : "");
            }
            shown.Add(json);
        }
        answer["images"] = shown;
        answer["note"] = pictures.Count > 0 ? "The pictures follow, in this order (those that came). image_save to keep some." : "None of them came.";
        foreach (var picture in pictures) McpImages.Add(answer, picture, "image/jpeg");
        return Json(answer);
    }

    private async Task<string> ImageSave(McpSession session, Args args)
    {
        RequireImageSearch();
        var picked = ImagesNamed(session, args, 20);
        var itemId = args.String("item");
        var folderArg = args.String("folder")?.Trim();
        if (itemId == null && string.IsNullOrEmpty(folderArg))
            throw new McpToolException("Say where: item (an item's id, to attach them) or folder (a folder in your project).");
        // Where they go, checked before anything is fetched.
        Project? project = null;
        TodoItem? item = null;
        string? takenFrom = null;
        if (itemId != null)
        {
            var (p, loc) = FindItem(session, itemId);
            MayChange(p, Change.Edit);
            CheckMayWork(p, loc.Item);
            takenFrom = CheckOut(session, p, loc.Item, args);
            (project, item) = (p, loc.Item);
        }
        string? folder = null;
        if (!string.IsNullOrEmpty(folderArg))
        {
            if (session.Remote) throw new McpToolException("Over the API, folders are on ProJict's computer, not yours: save onto an item (item=…) instead.", ToolProblem.Forbidden);
            if (session.WorkingDirectory == null && !Path.IsPathRooted(folderArg)) throw new McpToolException("Give the folder's full path: ProJict doesn't know your working directory.");
            folder = Path.GetFullPath(Path.IsPathRooted(folderArg) ? folderArg : Path.Combine(session.WorkingDirectory!, folderArg));
            string?[] roots = [session.WorkingDirectory, .. LocalProjects.Select(p => p.RepoPath)];
            if (!roots.OfType<string>().Where(r => r.Trim().Length > 0)
                    .Any(r => (folder + Path.DirectorySeparatorChar).StartsWith(Path.TrimEndingDirectorySeparator(Path.GetFullPath(r)) + Path.DirectorySeparatorChar, StringComparison.OrdinalIgnoreCase)))
                throw new McpToolException($"{folder} isn't in your working directory or a project's folder: pictures are saved only there.", ToolProblem.Forbidden);
        }
        var saved = new JsonArray();
        var attachments = new List<Attachment>();
        var history = new List<string>();
        foreach (var (result, address) in picked)
        {
            var json = new JsonObject();
            if (result != null) json["n"] = result.Number;
            json["image"] = address.AbsoluteUri;
            try
            {
                var (data, media, _) = await FetchAsync(address, MaxImageBytes, "image/*", result?.PageUrl is { } page && Uri.TryCreate(page, UriKind.Absolute, out var p) ? p : null);
                if (ImageSearch.Extension(media, data) is not { } extension)
                {
                    json["problem"] = "It isn't a picture (the address may be a web page): not saved.";
                    saved.Add(json);
                    continue;
                }
                var name = picked.Count == 1 && args.String("name")?.Trim() is { Length: > 0 } given
                    ? ImageSearch.FileName(Path.GetFileNameWithoutExtension(given), address.AbsoluteUri, extension)
                    : ImageSearch.FileName(result?.Title, address.AbsoluteUri, extension);
                if (WebPictures.Size(data) is { } size) json["size"] = $"{size.Width}×{size.Height}";
                if (result?.License is { } license) json["license"] = license;
                if (result?.Attribution is { } attribution) json["attribution"] = attribution;
                if (result?.PageUrl is { } from) json["page"] = from;
                if (folder != null)
                {
                    Directory.CreateDirectory(folder);
                    var path = UniquePath(Path.Combine(folder, name));
                    await File.WriteAllBytesAsync(path, data);
                    json["file"] = path;
                }
                if (item != null)
                {
                    var attachment = _state.Store.WriteAttachment(name, data);
                    attachments.Add(attachment);
                    json["attached"] = attachment.FileName;
                    history.Add($"{attachment.FileName} from {result?.PageUrl ?? address.AbsoluteUri}" + (result?.License is { } l ? $" ({l})" : ""));
                }
            }
            catch (Exception ex) when (ex is HttpRequestException or TaskCanceledException or IOException or UnauthorizedAccessException or McpToolException)
            {
                json["problem"] = $"It didn't come: {ex.Message}";
            }
            saved.Add(json);
        }
        if (project != null && item != null && attachments.Count > 0)
        {
            var agent = SignAs(session, project);
            Edit(project, item.Id, i =>
            {
                if (takenFrom != null)
                {
                    i.AddActivity(agent, $"Took it over from {takenFrom}.", agent: true);
                    i.ClaimedBy = agent;
                }
                (i.Attachments ??= []).AddRange(attachments);
                i.AddActivity(agent, "Saved pictures found on the web: " + string.Join("; ", history) + ".", agent: true);
            });
            if (takenFrom != null) session.Claimed.Add(item.Id);
        }
        var count = saved.Count(s => s?["problem"] == null);
        _state.StatusText = $"{session.AgentName} saved {count} picture{(count == 1 ? "" : "s")} from the web.";
        return Json(new JsonObject
        {
            ["saved"] = saved,
            ["note"] = count == 0 ? "Nothing was saved." : "Credit each picture as its licence asks, with its attribution where given.",
        });
    }

    private static string UniquePath(string path)
    {
        if (!File.Exists(path)) return path;
        var folder = Path.GetDirectoryName(path)!;
        var stem = Path.GetFileNameWithoutExtension(path);
        var extension = Path.GetExtension(path);
        for (var n = 2; ; n++)
        {
            var candidate = Path.Combine(folder, $"{stem}-{n}{extension}");
            if (!File.Exists(candidate)) return candidate;
        }
    }
}
