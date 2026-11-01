using System.Text.RegularExpressions;

namespace ProJict.Core.Wiki;

/// <summary>A heading in a page's text: its level (1–6), its words, and where its line starts.</summary>
public sealed record WikiHeading(int Level, string Text, int Start);

/// <summary>A page found by Ctrl+K search.</summary>
public sealed record WikiHit(WikiPage Page, string Snippet);

/// <summary>Sections of a page by heading (agents read and write one at a time), its [[links]], and search.</summary>
public static partial class WikiText
{
    [GeneratedRegex(@"^ {0,3}(#{1,6})[ ]+(.*?)(?:[ ]+#+)?[ ]*$")]
    private static partial Regex Heading();

    /// <summary>The headings of a page's text (not "#" lines inside code blocks), in order.</summary>
    public static List<WikiHeading> Headings(string text)
    {
        var headings = new List<WikiHeading>();
        string? fence = null;
        var at = 0;
        foreach (var raw in text.Split('\n'))
        {
            var line = raw.TrimEnd('\r');
            var trimmed = line.TrimStart();
            if (fence == null && (trimmed.StartsWith("```", StringComparison.Ordinal) || trimmed.StartsWith("~~~", StringComparison.Ordinal))) fence = trimmed[..3];
            else if (fence != null && trimmed.StartsWith(fence, StringComparison.Ordinal)) fence = null;
            else if (fence == null && Heading().Match(line) is { Success: true } m)
                headings.Add(new WikiHeading(m.Groups[1].Value.Length, m.Groups[2].Value.Trim(), at));
            at += raw.Length + 1;
        }
        return headings;
    }

    /// <summary>
    /// Where the section under a heading is: from its heading line up to the next heading of the same or a higher level
    /// (or the end). The heading is matched by its words, case and #s aside. Null when the page has no such heading.
    /// </summary>
    public static (int Start, int End, WikiHeading Heading)? Section(string text, string heading)
    {
        var wanted = heading.Trim().TrimStart('#').Trim();
        var headings = Headings(text);
        var index = headings.FindIndex(h => string.Equals(h.Text, wanted, StringComparison.OrdinalIgnoreCase));
        if (index < 0) return null;
        var found = headings[index];
        var next = headings.Skip(index + 1).FirstOrDefault(h => h.Level <= found.Level);
        return (found.Start, next?.Start ?? text.Length, found);
    }

    /// <summary>The section's text, heading included; null when there's no such heading.</summary>
    public static string? ReadSection(string text, string heading) =>
        Section(text, heading) is { } s ? text[s.Start..s.End].TrimEnd() : null;

    /// <summary>
    /// The page's text with one section replaced: <paramref name="body"/> goes under the heading (the heading stays, unless
    /// the body starts with a heading line of its own). With no such heading the section is added at the end, as a
    /// level-2 heading. <paramref name="append"/> adds the body at the end of the section instead.
    /// </summary>
    public static string ReplaceSection(string text, string heading, string body, bool append = false)
    {
        body = body.Replace("\r\n", "\n").Trim('\n');
        var newline = text.Contains("\r\n") ? "\r\n" : "\n";
        if (Section(text, heading) is not { } section)
        {
            var title = heading.Trim().TrimStart('#').Trim();
            var startsWithHeading = Heading().IsMatch(body.Split('\n')[0]);
            var added = startsWithHeading ? body : $"## {title}\n\n{body}";
            var before = text.TrimEnd();
            return (before.Length > 0 ? before + "\n\n" : "") + added.Replace("\n", newline) + newline;
        }
        var (start, end, found) = section;
        var current = text[start..end];
        var headingLine = current.Split('\n')[0].TrimEnd('\r');
        string replacement;
        if (append) replacement = current.TrimEnd() + "\n\n" + body;
        else if (Heading().IsMatch(body.Split('\n')[0])) replacement = body;
        else replacement = headingLine + "\n\n" + body;
        replacement = replacement.Replace("\r\n", "\n").Replace("\n", newline);
        var rest = text[end..];
        return text[..start] + replacement + (rest.Length > 0 ? newline + newline : newline) + rest.TrimStart('\r', '\n');
    }

    /// <summary>The pages a text links to with [[…]] (their targets as written), in order, each once.</summary>
    public static List<string> Links(string text)
    {
        var links = new List<string>();
        foreach (var block in Markdown.Parse(text)) Collect(block, links);
        return links.Distinct(StringComparer.OrdinalIgnoreCase).ToList();
    }

    private static void Collect(MdBlock block, List<string> links)
    {
        void FromText(string text)
        {
            foreach (var inline in Markdown.Inlines(text)) FromInline(inline);
        }
        void FromInline(MdInline inline)
        {
            switch (inline)
            {
                case MdWikiLink w: links.Add(w.Target); break;
                case MdStrong s: foreach (var x in s.Content) FromInline(x); break;
                case MdEmphasis e: foreach (var x in e.Content) FromInline(x); break;
                case MdStrike s: foreach (var x in s.Content) FromInline(x); break;
            }
        }
        switch (block)
        {
            case MdHeading h: FromText(h.Text); break;
            case MdParagraph p: FromText(p.Text); break;
            case MdQuote q: foreach (var b in q.Blocks) Collect(b, links); break;
            case MdList l: foreach (var b in l.Items.SelectMany(i => i.Blocks)) Collect(b, links); break;
            case MdTable t: foreach (var cell in t.Header.Concat(t.Rows.SelectMany(r => r))) FromText(cell); break;
        }
    }

    /// <summary>The diagrams in a text (```mermaid and ```drawio blocks, also in lists and quotes), in order.</summary>
    public static List<MdCode> Diagrams(string text)
    {
        var found = new List<MdCode>();
        void Walk(IEnumerable<MdBlock> blocks)
        {
            foreach (var block in blocks)
                switch (block)
                {
                    case MdCode { IsDiagram: true } code: found.Add(code); break;
                    case MdQuote q: Walk(q.Blocks); break;
                    case MdList l: Walk(l.Items.SelectMany(i => i.Blocks)); break;
                }
        }
        Walk(Markdown.Parse(text));
        return found;
    }

    /// <summary>
    /// mermaid's error, said in a line: "Parse error on line 3: expecting 'AMP', 'COLON', 'PIPE', 'TESTSTR', … got
    /// 'TAGEND'" without the excerpt and its ----^ pointer, and with the long list of what it expected cut short.
    /// </summary>
    public static string DiagramError(string? error)
    {
        var parts = new List<string>();
        foreach (var raw in (error ?? "").Replace("\r\n", "\n").Split('\n'))
        {
            var line = raw.Trim();
            if (line.Length == 0 || line.StartsWith("...", StringComparison.Ordinal) || line.All(c => c is '-' or '^')) continue;
            if (line.StartsWith("Expecting ", StringComparison.Ordinal) && line.LastIndexOf(", got ", StringComparison.Ordinal) is > 0 and var got)
            {
                var expected = line["Expecting ".Length..got].Split(", ");
                line = "expecting " + string.Join(", ", expected.Take(4)) + (expected.Length > 4 ? ", …" : "") + line[got..];
            }
            parts.Add(line);
        }
        var text = parts.Count == 0 ? "It doesn't draw." : string.Join(" ", parts);
        return text.Length > 300 ? text[..300] + "…" : text;
    }

    /// <summary>The pages whose [[links]] lead to <paramref name="page"/>.</summary>
    public static List<WikiPage> LinksTo(WikiBook book, WikiPage page) =>
        book.Pages.Where(p => p != page && Links(p.Text).Any(l => book.Resolve(l, p) == page)).OrderBy(p => p.Path, StringComparer.OrdinalIgnoreCase).ToList();

    /// <summary>Pages (most recently changed first) in which all terms appear, in the path or the text.</summary>
    public static List<WikiHit> Search(IEnumerable<WikiPage> pages, string query, int max = 50)
    {
        var terms = query.Split(' ', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
        if (terms.Length == 0) return [];
        return pages
            .Where(p => terms.All(t => p.Path.Contains(t, StringComparison.OrdinalIgnoreCase) || p.Text.Contains(t, StringComparison.OrdinalIgnoreCase)))
            // Pages whose name matches come first.
            .OrderByDescending(p => terms.Count(t => p.Path.Contains(t, StringComparison.OrdinalIgnoreCase)))
            .ThenByDescending(p => p.UpdatedAt)
            .Take(max)
            .Select(p => new WikiHit(p, Snippet(p.Text, terms)))
            .ToList();
    }

    private static string Snippet(string text, string[] terms)
    {
        var lines = text.Replace("\r\n", "\n").Split('\n');
        var line = (lines.FirstOrDefault(l => terms.Any(t => l.Contains(t, StringComparison.OrdinalIgnoreCase)))
                    ?? lines.FirstOrDefault(l => l.Trim().Length > 0) ?? "").Trim();
        // Without the marks that start a heading, quote or list item.
        line = line.TrimStart('#', '>', '-', '*', '+', ' ');
        return line.Length > 160 ? line[..160] + "…" : line;
    }
}
