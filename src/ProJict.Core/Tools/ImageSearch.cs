using System.Net;
using System.Text.Json.Nodes;
using System.Text.RegularExpressions;

namespace ProJict.Core.Tools;

/// <summary>Where image_search looks: the whole web (Bing Images), or images free to use with their licence (Openverse).</summary>
public enum ImageSource { Web, Free }

/// <summary>What the images may be used for: anything (unknown licences), shared, used commercially, or changed too.</summary>
public enum ImageLicense { Any, Share, Commercial, Modify }

/// <summary>What an image search asks for: the words, and filters for licence, size, kind, shape and colour.</summary>
public sealed record ImageQuery(string Words, ImageSource Source = ImageSource.Web, ImageLicense License = ImageLicense.Any,
    string? Size = null, string? Kind = null, string? Shape = null, string? Color = null, int Page = 1);

/// <summary>One image found: the picture itself, the page it's on, a small preview, its size and, where known, its licence and who made it.</summary>
public sealed record ImageResult(int Number, string Title, string ImageUrl, string? PageUrl, string? ThumbnailUrl, int? Width, int? Height,
    string? License = null, string? LicenseUrl = null, string? Creator = null, string? Attribution = null)
{
    public string Site => Uri.TryCreate(PageUrl ?? ImageUrl, UriKind.Absolute, out var u) ? (u.Host.StartsWith("www.", StringComparison.OrdinalIgnoreCase) ? u.Host[4..] : u.Host) : "";
    public string? SizeText => Width is { } w && Height is { } h ? $"{w}×{h}" : null;
}

/// <summary>
/// Image search for agents (image_search): the address to ask, and the results read from the answer. The web is Bing
/// Images (any picture, licences unknown unless a licence filter is used); free is Openverse (Creative Commons and public
/// domain, from Flickr, Wikimedia Commons and others), each with its licence and an attribution line to credit it.
/// </summary>
public static partial class ImageSearch
{
    public const int MaxResults = 40;
    /// <summary>Openverse gives at most 20 at a time without an account.</summary>
    public const int OpenversePage = 20;
    public static readonly string[] Sizes = ["small", "medium", "large", "wallpaper"];
    public static readonly string[] Kinds = ["photo", "clipart", "line", "transparent", "animated", "illustration"];
    public static readonly string[] Shapes = ["square", "wide", "tall"];
    public static readonly string[] Colors = ["color", "bw", "red", "orange", "yellow", "green", "teal", "blue", "purple", "pink", "brown", "black", "gray", "white"];

    /// <summary>The address to ask for one page of results.</summary>
    public static string Address(ImageQuery query) => query.Source == ImageSource.Free ? OpenverseAddress(query) : BingAddress(query);

    private static string BingAddress(ImageQuery q)
    {
        var filters = new List<string>();
        switch (q.License)
        {
            // Bing's licence filters: free to share and use, … commercially, … and to change.
            case ImageLicense.Share: filters.Add("license-L2_L3_L4_L5_L6_L7"); break;
            case ImageLicense.Commercial: filters.Add("license-L2_L3_L4"); break;
            case ImageLicense.Modify: filters.Add("license-L2_L3"); break;
        }
        if (q.Size is { } size) filters.Add("imagesize-" + size);
        if (q.Kind is { } kind)
            filters.Add(kind switch
            {
                "photo" => "photo-photo", "clipart" or "illustration" => "photo-clipart", "line" => "photo-linedrawing",
                "transparent" => "photo-transparent", "animated" => "photo-animatedgif", _ => "photo-" + kind,
            });
        if (q.Shape is { } shape) filters.Add("aspect-" + shape);
        if (q.Color is { } color) filters.Add(color switch { "color" => "color2-color", "bw" => "color2-bw", _ => "color2-FGcls_" + color.ToUpperInvariant() });
        var first = 1 + (Math.Max(1, q.Page) - 1) * 35;
        return $"https://www.bing.com/images/search?q={Uri.EscapeDataString(q.Words)}&first={first}&form=IRFLTR" +
               (filters.Count > 0 ? "&qft=" + string.Concat(filters.Select(f => "+filterui:" + f)) : "") + "&safeSearch=Moderate";
    }

    private static string OpenverseAddress(ImageQuery q)
    {
        var parts = new List<string> { "q=" + Uri.EscapeDataString(q.Words), "page_size=" + OpenversePage, "page=" + Math.Max(1, q.Page), "mature=false" };
        switch (q.License)
        {
            case ImageLicense.Commercial: parts.Add("license_type=commercial"); break;
            case ImageLicense.Modify: parts.Add("license_type=commercial,modification"); break;
        }
        if (q.Size is { } size) parts.Add("size=" + (size == "wallpaper" ? "large" : size));
        if (q.Kind is { } kind)
            parts.Add(kind switch
            {
                "photo" => "category=photograph", "clipart" or "illustration" or "line" => "category=illustration",
                "transparent" => "extension=png,svg", "animated" => "extension=gif", _ => "category=" + kind,
            });
        if (q.Shape is { } shape) parts.Add("aspect_ratio=" + shape);
        return "https://api.openverse.org/v1/images/?" + string.Join("&", parts);
    }

    [GeneratedRegex("""<a[^>]*?class="iusc"[^>]*?\sm="([^"]+)"[^>]*>""", RegexOptions.IgnoreCase)]
    private static partial Regex BingResult();

    [GeneratedRegex("""^\s*(\d+)\s*[×x]\s*(\d+)""")]
    private static partial Regex SizePattern();

    /// <summary>Bing Images' results page: each picture's JSON (murl, purl, turl, t) and the size shown under it.</summary>
    public static List<ImageResult> ReadBing(string html)
    {
        var results = new List<ImageResult>();
        var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        var matches = BingResult().Matches(html);
        for (var k = 0; k < matches.Count; k++)
        {
            var match = matches[k];
            JsonNode? m;
            try { m = JsonNode.Parse(WebUtility.HtmlDecode(match.Groups[1].Value)); }
            catch (System.Text.Json.JsonException) { continue; }
            if ((string?)m?["murl"] is not { Length: > 0 } image || !seen.Add(image)) continue;
            // The size Bing shows under the picture ("1920 × 1080 · jpeg") comes after its link, before the next picture's.
            var end = k + 1 < matches.Count ? matches[k + 1].Index : Math.Min(html.Length, match.Index + 6000);
            var after = html[match.Index..end];
            int? width = null, height = null;
            // Its link says the size too (expw, exph).
            if (Regex.Match(match.Value, @"[?&;]expw=(\d+)").Groups[1] is { Success: true } w && Regex.Match(match.Value, @"[?&;]exph=(\d+)").Groups[1] is { Success: true } h)
                (width, height) = (int.Parse(w.Value), int.Parse(h.Value));
            var info = after.IndexOf("img_info", StringComparison.Ordinal);
            if (info >= 0)
            {
                var text = WebUtility.HtmlDecode(Regex.Replace(after[info..Math.Min(after.Length, info + 300)], "<[^>]+>", " "));
                var sized = SizePattern().Match(text[(text.IndexOf('>') + 1)..]);
                if (sized.Success) (width, height) = (int.Parse(sized.Groups[1].Value), int.Parse(sized.Groups[2].Value));
            }
            results.Add(new ImageResult(results.Count + 1, Tidy((string?)m!["t"]) ?? Tidy((string?)m["desc"]) ?? "", image,
                (string?)m["purl"], (string?)m["turl"], width, height));
            if (results.Count >= MaxResults) break;
        }
        return results;
    }

    /// <summary>Openverse's answer: each image with its licence, maker and attribution line.</summary>
    public static List<ImageResult> ReadOpenverse(string json)
    {
        var results = new List<ImageResult>();
        if (JsonNode.Parse(json)?["results"] is not JsonArray list) return results;
        foreach (var r in list)
        {
            if ((string?)r?["url"] is not { Length: > 0 } image) continue;
            var license = (string?)r!["license"] is { } l ? (l is "cc0" or "pdm" ? l.ToUpperInvariant() : "CC " + l.ToUpperInvariant()) +
                                                            ((string?)r["license_version"] is { Length: > 0 } v && l is not "pdm" ? " " + v : "") : null;
            results.Add(new ImageResult(results.Count + 1, Tidy((string?)r["title"]) ?? "", image, (string?)r["foreign_landing_url"], (string?)r["thumbnail"],
                (int?)r["width"], (int?)r["height"], license, (string?)r["license_url"], Tidy((string?)r["creator"]), Tidy((string?)r["attribution"])));
            if (results.Count >= MaxResults) break;
        }
        return results;
    }

    /// <summary>Reads a search's answer, by where it was asked.</summary>
    public static List<ImageResult> Read(ImageSource source, string answer) => source == ImageSource.Free ? ReadOpenverse(answer) : ReadBing(answer);

    private static string? Tidy(string? text) => text == null ? null : Regex.Replace(WebUtility.HtmlDecode(text), @"\s+", " ").Trim() is { Length: > 0 } t ? t : null;

    /// <summary>What a file of this kind is called when saved ("jpg" for image/jpeg), or null when it isn't a picture.</summary>
    public static string? Extension(string? mediaType, ReadOnlySpan<byte> start)
    {
        // By the file's first bytes first: servers often say the wrong type.
        if (start.Length >= 3 && start[0] == 0xFF && start[1] == 0xD8 && start[2] == 0xFF) return "jpg";
        if (start.Length >= 8 && start[..8].SequenceEqual(new byte[] { 0x89, 0x50, 0x4E, 0x47, 0x0D, 0x0A, 0x1A, 0x0A })) return "png";
        if (start.Length >= 6 && start[0] == 'G' && start[1] == 'I' && start[2] == 'F' && start[3] == '8') return "gif";
        if (start.Length >= 12 && start[0] == 'R' && start[1] == 'I' && start[2] == 'F' && start[3] == 'F' && start[8] == 'W' && start[9] == 'E' && start[10] == 'B' && start[11] == 'P') return "webp";
        if (start.Length >= 2 && start[0] == 'B' && start[1] == 'M') return "bmp";
        if (start.Length >= 12 && start[4] == 'f' && start[5] == 't' && start[6] == 'y' && start[7] == 'p' && start[8] == 'a' && start[9] == 'v' && start[10] == 'i') return "avif";
        var media = mediaType?.Split(';')[0].Trim().ToLowerInvariant();
        if (media == "image/svg+xml") return "svg";
        if (media == "image/x-icon" || media == "image/vnd.microsoft.icon") return "ico";
        return null;
    }

    /// <summary>A file name for a saved image: from its title (or its address), with the right extension.</summary>
    public static string FileName(string? title, string address, string extension)
    {
        var stem = title;
        if (string.IsNullOrWhiteSpace(stem) && Uri.TryCreate(address, UriKind.Absolute, out var u)) stem = Path.GetFileNameWithoutExtension(u.AbsolutePath);
        stem = Regex.Replace(stem ?? "", @"[^\p{L}\p{N}]+", "-").Trim('-').ToLowerInvariant();
        if (stem.Length > 60) stem = stem[..60].TrimEnd('-');
        return (stem.Length > 0 ? stem : "image") + "." + extension;
    }
}
