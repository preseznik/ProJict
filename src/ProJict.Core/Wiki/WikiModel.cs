using System.Text;
using System.Text.Json.Serialization;
using ProJict.Core.Model;

namespace ProJict.Core.Wiki;

/// <summary>One saved state of a page: its text, who saved it and when. Version 1 is the page as it was created.</summary>
public sealed class WikiVersion
{
    public int Number { get; set; }
    public DateTime At { get; set; } = DateTime.Now;
    public string By { get; set; } = "";
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingDefault)]
    public bool Agent { get; set; }
    public string Text { get; set; } = "";
    /// <summary>What happened besides writing: "Merged with Ada's edit", "Restored version 3", "Moved from Notes".</summary>
    public string? Note { get; set; }

    public WikiVersion Clone() => (WikiVersion)MemberwiseClone();
}

/// <summary>
/// A save that changed the same paragraphs as someone else's save meanwhile. Nothing is lost: the page keeps the earlier
/// save, and this one waits beside it until you pick (or merge by hand).
/// </summary>
public sealed class WikiClash
{
    public Guid Id { get; set; } = Guid.NewGuid();
    public DateTime At { get; set; } = DateTime.Now;
    public string By { get; set; } = "";
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingDefault)]
    public bool Agent { get; set; }
    /// <summary>The whole page as they meant it.</summary>
    public string Text { get; set; } = "";
    /// <summary>The version they started from.</summary>
    public int BaseVersion { get; set; }
    /// <summary>The page's version when their save arrived.</summary>
    public int AgainstVersion { get; set; }
}

/// <summary>Who saves: a person (you, or someone you share with) or an AI agent, by the name they sign with.</summary>
public sealed record WikiAuthor(string Name, bool Agent = false);

/// <summary>
/// A page of the workspace's wiki: Markdown text, its place in the tree ("GRID2VR/Architecture/Renderer"), and every
/// version it had. A page linked to a project also shows from that project, and is shared as the project is.
/// </summary>
public sealed class WikiPage
{
    public Guid Id { get; set; } = Guid.NewGuid();
    /// <summary>Folders and the page's title, separated by "/" ("GRID2VR/Architecture/Renderer").</summary>
    public string Path { get; set; } = "";
    public string Text { get; set; } = "";
    /// <summary>The project the page belongs to: it shows in the project's header and follows its sharing.</summary>
    public Guid? ProjectId { get; set; }
    /// <summary>For a page without a project: shared with everyone you share with (else it stays on this computer).</summary>
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingDefault)]
    public bool SharedWithEveryone { get; set; }
    public DateTime CreatedAt { get; set; } = DateTime.Now;
    public string CreatedBy { get; set; } = "";
    public DateTime UpdatedAt { get; set; } = DateTime.Now;
    public string? UpdatedBy { get; set; }
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingDefault)]
    public bool UpdatedByAgent { get; set; }
    /// <summary>Changed by an AI agent and not checked by you yet.</summary>
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingDefault)]
    public bool NeedsReview { get; set; }
    /// <summary>The version you last checked; a review shows what changed since. Null until an agent first changed the page.</summary>
    public int? ReviewedVersion { get; set; }
    /// <summary>Pictures and files the page shows or links to; null while there are none.</summary>
    public List<Attachment>? Attachments { get; set; }
    /// <summary>Every saved version, oldest first (the oldest go once there are very many, see <see cref="WikiBook.KeptVersions"/>).</summary>
    public List<WikiVersion> Versions { get; set; } = [];
    /// <summary>Saves that clashed with another, waiting for you to pick; null while there are none.</summary>
    public List<WikiClash>? Clashes { get; set; }

    [JsonIgnore] public int Version => Versions.Count > 0 ? Versions[^1].Number : 0;
    [JsonIgnore] public string Title => WikiPaths.Title(Path);
    [JsonIgnore] public string Folder => WikiPaths.Folder(Path);
    [JsonIgnore] public bool HasClashes => Clashes is { Count: > 0 };

    public WikiVersion? VersionAt(int number) => Versions.FirstOrDefault(v => v.Number == number);

    public WikiPage DeepClone()
    {
        var copy = (WikiPage)MemberwiseClone();
        copy.Attachments = Attachments?.ToList();
        copy.Versions = Versions.Select(v => v.Clone()).ToList();
        copy.Clashes = Clashes?.ToList();
        return copy;
    }
}

/// <summary>Where pages are in the tree: "GRID2VR / Architecture / Renderer" is stored as "GRID2VR/Architecture/Renderer".</summary>
public static class WikiPaths
{
    public const int MaxPartLength = 100;
    public const int MaxDepth = 8;

    /// <summary>Trims each part, drops empty ones and squeezes spaces: " a /b//  c d " → "a/b/c d".</summary>
    public static string Normalize(string path)
    {
        var parts = path.Replace('\\', '/').Split('/')
            .Select(p => string.Join(' ', p.Split(' ', '\t', StringSplitOptions.RemoveEmptyEntries)))
            .Where(p => p.Length > 0);
        return string.Join('/', parts);
    }

    public static string[] Parts(string path) => path.Length == 0 ? [] : path.Split('/');

    public static string Title(string path) => path.LastIndexOf('/') is >= 0 and var i ? path[(i + 1)..] : path;

    /// <summary>"GRID2VR/Architecture" for "GRID2VR/Architecture/Renderer"; empty at the top.</summary>
    public static string Folder(string path) => path.LastIndexOf('/') is >= 0 and var i ? path[..i] : "";

    public static string Combine(string folder, string title) => Normalize(folder.Length == 0 ? title : $"{folder}/{title}");

    /// <summary>"GRID2VR › Architecture › Renderer", for people.</summary>
    public static string Display(string path) => path.Replace("/", " › ");

    /// <summary>Whether <paramref name="path"/> is <paramref name="folder"/> itself or somewhere under it.</summary>
    public static bool IsIn(string path, string folder) =>
        folder.Length == 0 || string.Equals(path, folder, StringComparison.OrdinalIgnoreCase) ||
        path.StartsWith(folder + "/", StringComparison.OrdinalIgnoreCase);

    /// <summary>Why the path can't name a page (empty, too deep or long, or with characters links use); null when it can.</summary>
    public static string? Problem(string path)
    {
        var parts = Parts(Normalize(path));
        if (parts.Length == 0) return "Give the page a name.";
        if (parts.Length > MaxDepth) return $"Pages go at most {MaxDepth} folders deep.";
        foreach (var part in parts)
        {
            if (part.Length > MaxPartLength) return $"Keep names under {MaxPartLength} characters.";
            if (part.Any(c => c is '[' or ']' or '|' || char.IsControl(c))) return "Names can't have [ ] or | in them (links use those).";
        }
        return null;
    }

    /// <summary>A key for comparing paths: case and spacing don't count.</summary>
    public static string Key(string path) => Normalize(path).ToLowerInvariant();

    /// <summary>"Release notes 2" when "Release notes" is taken.</summary>
    public static string Unique(string path, Func<string, bool> taken)
    {
        path = Normalize(path);
        if (!taken(path)) return path;
        var builder = new StringBuilder();
        for (var i = 2; ; i++)
        {
            builder.Clear().Append(path).Append(' ').Append(i);
            if (!taken(builder.ToString())) return builder.ToString();
        }
    }
}
