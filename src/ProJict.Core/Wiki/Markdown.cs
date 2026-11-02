using System.Text;
using System.Text.RegularExpressions;

namespace ProJict.Core.Wiki;

// ───────────── Blocks ─────────────

public abstract record MdBlock;

public sealed record MdHeading(int Level, string Text) : MdBlock;

/// <summary>A paragraph's text; single line breaks inside it are soft (two spaces or a backslash at the end of a line make a hard one).</summary>
public sealed record MdParagraph(string Text) : MdBlock;

/// <summary>
/// A code block: fenced (```lang) or indented. Its language also says which blocks are diagrams ("mermaid",
/// "drawio"), which a later version draws.
/// </summary>
public sealed record MdCode(string? Language, string Code) : MdBlock
{
    public bool IsDiagram => Language is { } l && Markdown.DiagramLanguages.Contains(l);
}

public sealed record MdQuote(IReadOnlyList<MdBlock> Blocks) : MdBlock;

public sealed record MdListItem(IReadOnlyList<MdBlock> Blocks, bool? Checked);

public sealed record MdList(bool Ordered, int Start, IReadOnlyList<MdListItem> Items) : MdBlock;

public enum MdAlign { None, Left, Center, Right }

public sealed record MdTable(IReadOnlyList<string> Header, IReadOnlyList<MdAlign> Aligns, IReadOnlyList<IReadOnlyList<string>> Rows) : MdBlock;

public sealed record MdRule : MdBlock;

// ───────────── Inlines ─────────────

public abstract record MdInline;

public sealed record MdText(string Text) : MdInline;

public sealed record MdStrong(IReadOnlyList<MdInline> Content) : MdInline;

public sealed record MdEmphasis(IReadOnlyList<MdInline> Content) : MdInline;

public sealed record MdStrike(IReadOnlyList<MdInline> Content) : MdInline;

public sealed record MdCodeSpan(string Code) : MdInline;

/// <summary>[text](url): a web link, or the name of a file attached to the page.</summary>
public sealed record MdLink(string Url, IReadOnlyList<MdInline> Content) : MdInline;

/// <summary>
/// ![alt](url "caption"){layout}: a picture, video or sound attached to the page (by file name), or a picture on the
/// web; the caption shows under it, and the layout (<see cref="MediaLayout"/>) says its size and place.
/// </summary>
public sealed record MdImage(string Url, string Alt, string Caption = "", MediaLayout Layout = default) : MdInline;

/// <summary>[[Page name]] or [[Page name|what to show]].</summary>
public sealed record MdWikiLink(string Target, string Label) : MdInline;

/// <summary>"#4e84570c" or "#grid2vr-hud": an item or a channel when one is called that, else plain text.</summary>
public sealed record MdHashRef(string Token) : MdInline;

public sealed record MdLineBreak : MdInline;

/// <summary>
/// The Markdown wiki pages are written in: headings, paragraphs, lists (with [ ] tasks), quotes, code, tables, rules,
/// emphasis, links and pictures, plus [[page]] links and #item and #channel references. HTML is shown as text.
/// </summary>
public static partial class Markdown
{
    /// <summary>Fenced code in these languages is a diagram (drawn in a later version; until then shown as code).</summary>
    public static readonly IReadOnlySet<string> DiagramLanguages = new HashSet<string>(StringComparer.OrdinalIgnoreCase) { "mermaid", "drawio" };

    public static IReadOnlyList<MdBlock> Parse(string text) => ParseBlocks(text.Replace("\r\n", "\n").Replace('\t', ' ').Split('\n').ToList(), 0);

    [GeneratedRegex(@"^ {0,3}(#{1,6})(?:[ ]+(.*?))?(?:[ ]+#+)?[ ]*$")]
    private static partial Regex HeadingLine();

    [GeneratedRegex(@"^ {0,3}([-*_])(?:[ ]*\1){2,}[ ]*$")]
    private static partial Regex RuleLine();

    [GeneratedRegex(@"^( {0,3})([-*+]|\d{1,9}[.)])( +|$)(.*)$")]
    private static partial Regex ListMarker();

    [GeneratedRegex(@"^ {0,3}(`{3,}|~{3,})[ ]*([^`\s]*)")]
    private static partial Regex FenceLine();

    [GeneratedRegex(@"^ *\|? *:?-+:? *(\| *:?-+:? *)*\|? *$")]
    private static partial Regex TableDelimiter();

    private static bool Blank(string line) => line.Trim().Length == 0;

    private static int Indent(string line) => line.Length - line.TrimStart(' ').Length;

    private static List<MdBlock> ParseBlocks(List<string> lines, int depth)
    {
        var blocks = new List<MdBlock>();
        var i = 0;
        while (i < lines.Count)
        {
            var line = lines[i];
            if (Blank(line))
            {
                i++;
                continue;
            }
            if (FenceLine().Match(line) is { Success: true } fence)
            {
                var marker = fence.Groups[1].Value;
                var language = fence.Groups[2].Value is { Length: > 0 } l ? l : null;
                var indent = Indent(line);
                var code = new List<string>();
                i++;
                while (i < lines.Count)
                {
                    var t = lines[i].TrimStart();
                    if (t.StartsWith(marker, StringComparison.Ordinal) && t.Trim().All(c => c == marker[0])) break;
                    code.Add(lines[i].Length >= indent && Indent(lines[i]) >= indent ? lines[i][indent..] : lines[i].TrimStart());
                    i++;
                }
                i++;
                blocks.Add(new MdCode(language, string.Join('\n', code)));
                continue;
            }
            if (HeadingLine().Match(line) is { Success: true } heading)
            {
                blocks.Add(new MdHeading(heading.Groups[1].Value.Length, heading.Groups[2].Value.Trim()));
                i++;
                continue;
            }
            if (RuleLine().IsMatch(line))
            {
                blocks.Add(new MdRule());
                i++;
                continue;
            }
            if (line.TrimStart().StartsWith('>') && Indent(line) < 4)
            {
                var quoted = new List<string>();
                while (i < lines.Count && !Blank(lines[i]))
                {
                    var t = lines[i].TrimStart();
                    if (t.StartsWith('>')) t = t.Length > 1 && t[1] == ' ' ? t[2..] : t[1..];
                    else if (quoted.Count == 0 || StartsBlock(lines[i])) break;
                    quoted.Add(t);
                    i++;
                }
                blocks.Add(new MdQuote(depth < 20 ? ParseBlocks(quoted, depth + 1) : [new MdParagraph(string.Join('\n', quoted))]));
                continue;
            }
            if (ListMarker().Match(line) is { Success: true } && depth < 20)
            {
                blocks.Add(ParseList(lines, ref i, depth));
                continue;
            }
            if (line.Contains('|') && i + 1 < lines.Count && TableDelimiter().IsMatch(lines[i + 1]) && lines[i + 1].Contains('-') &&
                Cells(line).Count == Cells(lines[i + 1]).Count)
            {
                var header = Cells(line);
                var aligns = Cells(lines[i + 1]).Select(c => c.StartsWith(':') && c.EndsWith(':') ? MdAlign.Center
                    : c.EndsWith(':') ? MdAlign.Right : c.StartsWith(':') ? MdAlign.Left : MdAlign.None).ToList();
                var rows = new List<IReadOnlyList<string>>();
                i += 2;
                while (i < lines.Count && !Blank(lines[i]) && lines[i].Contains('|'))
                {
                    var cells = Cells(lines[i]);
                    while (cells.Count < header.Count) cells.Add("");
                    rows.Add(cells.Take(header.Count).ToList());
                    i++;
                }
                blocks.Add(new MdTable(header, aligns, rows));
                continue;
            }
            if (Indent(line) >= 4)
            {
                var code = new List<string>();
                while (i < lines.Count && (Indent(lines[i]) >= 4 || Blank(lines[i])))
                {
                    code.Add(Blank(lines[i]) ? "" : lines[i][4..]);
                    i++;
                }
                while (code.Count > 0 && code[^1].Length == 0) code.RemoveAt(code.Count - 1);
                blocks.Add(new MdCode(null, string.Join('\n', code)));
                continue;
            }

            // A paragraph, up to a blank line or something that starts another block; "===" or "---" under one line makes it a heading.
            var paragraph = new List<string> { line.EndsWith("  ", StringComparison.Ordinal) ? line.TrimStart() : line.Trim() };
            i++;
            while (i < lines.Count && !Blank(lines[i]))
            {
                var next = lines[i];
                if (paragraph.Count == 1 && next.Trim().Length > 0 && next.Trim().All(c => c == '='))
                {
                    blocks.Add(new MdHeading(1, paragraph[0]));
                    paragraph.Clear();
                    i++;
                    break;
                }
                if (paragraph.Count == 1 && next.Trim().Length > 1 && next.Trim().All(c => c == '-'))
                {
                    blocks.Add(new MdHeading(2, paragraph[0]));
                    paragraph.Clear();
                    i++;
                    break;
                }
                if (StartsBlock(next)) break;
                // Keep a hard break's trailing spaces; trim the rest.
                paragraph.Add(next.EndsWith("  ", StringComparison.Ordinal) ? next.TrimStart() : next.Trim());
                i++;
            }
            if (paragraph.Count > 0)
            {
                paragraph[^1] = paragraph[^1].TrimEnd();
                blocks.Add(new MdParagraph(string.Join('\n', paragraph)));
            }
        }
        return blocks;
    }

    /// <summary>Whether a line starts a block of its own (so it ends a paragraph before it).</summary>
    private static bool StartsBlock(string line) =>
        HeadingLine().IsMatch(line) || RuleLine().IsMatch(line) || FenceLine().IsMatch(line) ||
        (line.TrimStart().StartsWith('>') && Indent(line) < 4) ||
        (ListMarker().Match(line) is { Success: true } m && m.Groups[4].Value.Trim().Length > 0 && (!char.IsDigit(m.Groups[2].Value[0]) || m.Groups[2].Value.StartsWith("1")));

    private static MdList ParseList(List<string> lines, ref int i, int depth)
    {
        var first = ListMarker().Match(lines[i]);
        var ordered = char.IsDigit(first.Groups[2].Value[0]);
        var bullet = ordered ? first.Groups[2].Value[^1] : first.Groups[2].Value[0];
        var start = ordered && int.TryParse(first.Groups[2].Value[..^1], out var s) ? s : 1;
        var items = new List<MdListItem>();
        while (i < lines.Count)
        {
            var m = ListMarker().Match(lines[i]);
            if (!m.Success) break;
            var marker = m.Groups[2].Value;
            if (char.IsDigit(marker[0]) != ordered || (ordered ? marker[^1] : marker[0]) != bullet) break;
            var spaces = m.Groups[3].Value.Length;
            var contentIndent = m.Groups[1].Value.Length + marker.Length + (spaces is 0 or > 4 ? 1 : spaces);
            var itemLines = new List<string> { m.Groups[4].Value };
            i++;
            while (i < lines.Count)
            {
                var line = lines[i];
                if (Blank(line))
                {
                    // A blank line goes on only if what follows still belongs to the item.
                    var next = i + 1;
                    while (next < lines.Count && Blank(lines[next])) next++;
                    if (next < lines.Count && Indent(lines[next]) >= contentIndent)
                    {
                        for (; i < next; i++) itemLines.Add("");
                        continue;
                    }
                    break;
                }
                if (Indent(line) >= contentIndent)
                {
                    itemLines.Add(line[contentIndent..]);
                    i++;
                    continue;
                }
                if (ListMarker().IsMatch(line) || StartsBlock(line) || Blank(itemLines[^1])) break;
                // A lazy continuation of the item's paragraph.
                itemLines.Add(line.Trim());
                i++;
            }
            bool? check = null;
            var text = itemLines[0];
            if (text.StartsWith("[ ] ", StringComparison.Ordinal) || text == "[ ]") check = false;
            else if (text.StartsWith("[x] ", StringComparison.OrdinalIgnoreCase) || text.Equals("[x]", StringComparison.OrdinalIgnoreCase)) check = true;
            if (check != null) itemLines[0] = text.Length > 3 ? text[4..] : "";
            items.Add(new MdListItem(ParseBlocks(itemLines, depth + 1), check));

            // The list goes on past blank lines when another item of it follows.
            var after = i;
            while (after < lines.Count && Blank(lines[after])) after++;
            if (after < lines.Count && ListMarker().Match(lines[after]) is { Success: true } again && Indent(lines[after]) < contentIndent &&
                char.IsDigit(again.Groups[2].Value[0]) == ordered && (ordered ? again.Groups[2].Value[^1] : again.Groups[2].Value[0]) == bullet)
                i = after;
            else break;
        }
        return new MdList(ordered, start, items);
    }

    /// <summary>A table row's cells: split at | (not \| or one inside `code`), trimmed, without the outer pipes.</summary>
    private static List<string> Cells(string line)
    {
        var cells = new List<string>();
        var cell = new StringBuilder();
        var inCode = false;
        var t = line.Trim();
        if (t.StartsWith('|')) t = t[1..];
        if (t.EndsWith('|') && !t.EndsWith("\\|", StringComparison.Ordinal)) t = t[..^1];
        for (var k = 0; k < t.Length; k++)
        {
            var c = t[k];
            if (c == '\\' && k + 1 < t.Length && t[k + 1] == '|')
            {
                cell.Append('|');
                k++;
            }
            else if (c == '`')
            {
                inCode = !inCode;
                cell.Append(c);
            }
            else if (c == '|' && !inCode)
            {
                cells.Add(cell.ToString().Trim());
                cell.Clear();
            }
            else cell.Append(c);
        }
        cells.Add(cell.ToString().Trim());
        return cells;
    }

    // ───────────── Inlines ─────────────

    /// <summary>A heading's, paragraph's or table cell's text as inlines.</summary>
    public static IReadOnlyList<MdInline> Inlines(string text) => ParseInlines(text, 0);

    private const string Escapable = "\\`*_{}[]()#+-.!|~<>\"'";

    private static List<MdInline> ParseInlines(string text, int depth)
    {
        var result = new List<MdInline>();
        var plain = new StringBuilder();
        void Flush()
        {
            if (plain.Length == 0) return;
            result.Add(new MdText(plain.ToString()));
            plain.Clear();
        }
        void Add(MdInline inline)
        {
            Flush();
            result.Add(inline);
        }

        var i = 0;
        while (i < text.Length)
        {
            var c = text[i];
            var standsAlone = i == 0 || !char.IsLetterOrDigit(text[i - 1]);
            if (c == '\\' && i + 1 < text.Length)
            {
                if (text[i + 1] == '\n')
                {
                    Add(new MdLineBreak());
                    i += 2;
                    continue;
                }
                if (Escapable.Contains(text[i + 1]))
                {
                    plain.Append(text[i + 1]);
                    i += 2;
                    continue;
                }
            }
            if (c == '\n')
            {
                // Two spaces before a line break make it a hard one.
                if (plain.Length >= 2 && plain[^1] == ' ' && plain[^2] == ' ')
                {
                    while (plain.Length > 0 && plain[^1] == ' ') plain.Length--;
                    Add(new MdLineBreak());
                }
                else plain.Append(' ');
                i++;
                continue;
            }
            if (c == '`')
            {
                var ticks = 0;
                while (i + ticks < text.Length && text[i + ticks] == '`') ticks++;
                var fence = new string('`', ticks);
                var close = text.IndexOf(fence, i + ticks, StringComparison.Ordinal);
                while (close >= 0 && close + ticks < text.Length && text[close + ticks] == '`') close = text.IndexOf(fence, close + ticks + 1, StringComparison.Ordinal);
                if (close > 0)
                {
                    var code = text[(i + ticks)..close].Replace('\n', ' ');
                    if (code.Length >= 2 && code[0] == ' ' && code[^1] == ' ' && code.Trim().Length > 0) code = code[1..^1];
                    Add(new MdCodeSpan(code));
                    i = close + ticks;
                    continue;
                }
                plain.Append(fence);
                i += ticks;
                continue;
            }
            if (c == '[' && i + 1 < text.Length && text[i + 1] == '[')
            {
                var close = text.IndexOf("]]", i + 2, StringComparison.Ordinal);
                if (close > i + 2 && !text[(i + 2)..close].Contains('\n'))
                {
                    var inside = text[(i + 2)..close];
                    var bar = inside.IndexOf('|');
                    var target = (bar >= 0 ? inside[..bar] : inside).Trim();
                    var label = bar >= 0 ? inside[(bar + 1)..].Trim() : "";
                    if (target.Length > 0)
                    {
                        Add(new MdWikiLink(target, label.Length > 0 ? label : WikiPaths.Title(WikiPaths.Normalize(target))));
                        i = close + 2;
                        continue;
                    }
                }
            }
            if (c == '!' && i + 1 < text.Length && text[i + 1] == '[' && ImageAt(text, i) is { } image)
            {
                Add(new MdImage(image.Url, PlainText(ParseInlines(image.Label, depth + 1)), image.Caption, image.Layout));
                i = image.End;
                continue;
            }
            if (c == '[' && LinkAt(text, i) is { } link)
            {
                Add(new MdLink(link.Url, depth < 10 ? ParseInlines(link.Label, depth + 1) : [new MdText(link.Label)]));
                i = link.End;
                continue;
            }
            if (c == '<' && text.IndexOf('>', i + 1) is > 0 and var gt && IsWebLink(text[(i + 1)..gt]))
            {
                var url = text[(i + 1)..gt];
                Add(new MdLink(url, [new MdText(url)]));
                i = gt + 1;
                continue;
            }
            if (c is 'h' or 'H' && standsAlone && (Starts(text, i, "https://") || Starts(text, i, "http://")))
            {
                var end = i;
                while (end < text.Length && !char.IsWhiteSpace(text[end]) && text[end] != '<') end++;
                while (end > i && ".,;:!?)]'\"*_".Contains(text[end - 1])) end--;
                var url = text[i..end];
                if (Uri.TryCreate(url, UriKind.Absolute, out _))
                {
                    Add(new MdLink(url, [new MdText(url)]));
                    i = end;
                    continue;
                }
            }
            if (c == '#' && standsAlone)
            {
                var end = i + 1;
                while (end < text.Length && (char.IsAsciiLetterOrDigit(text[end]) || text[end] == '-')) end++;
                var token = text[(i + 1)..end].TrimEnd('-');
                if (token.Length > 0)
                {
                    Add(new MdHashRef(token));
                    i += token.Length + 1;
                    continue;
                }
            }
            if ((c is '*' or '_' or '~') && depth < 10 && Emphasis(text, i, depth) is { } emphasis)
            {
                Add(emphasis.Inline);
                i = emphasis.End;
                continue;
            }
            plain.Append(c);
            i++;
        }
        Flush();
        return result;
    }

    private static bool Starts(string text, int at, string prefix) => string.Compare(text, at, prefix, 0, prefix.Length, StringComparison.OrdinalIgnoreCase) == 0;

    private static bool IsWebLink(string text) =>
        (text.StartsWith("http://", StringComparison.OrdinalIgnoreCase) || text.StartsWith("https://", StringComparison.OrdinalIgnoreCase) ||
         text.StartsWith("mailto:", StringComparison.OrdinalIgnoreCase)) && !text.Any(char.IsWhiteSpace) && Uri.TryCreate(text, UriKind.Absolute, out _);

    /// <summary>"![label](url "caption"){layout}" at <paramref name="at"/> (the "!"); the layout only when its words are all layout words.</summary>
    internal static WikiMediaRef? ImageAt(string text, int at)
    {
        if (LinkAt(text, at + 1) is not { } link) return null;
        var end = link.End;
        var layout = default(MediaLayout);
        if (end < text.Length && text[end] == '{' && text.IndexOf('}', end) is > 0 and var shut && MediaLayout.Parse(text[(end + 1)..shut]) is { } found)
        {
            layout = found;
            end = shut + 1;
        }
        return new WikiMediaRef(at, end, link.Label, link.Target, link.Url, link.Title, layout);
    }

    /// <summary>"[label](url "title")" at <paramref name="at"/> (the "["): its label, url (and as written, without the title), title and where it ends.</summary>
    private static (string Label, string Url, string Target, string Title, int End)? LinkAt(string text, int at)
    {
        var depth = 0;
        var close = -1;
        for (var k = at; k < text.Length; k++)
        {
            if (text[k] == '\\') { k++; continue; }
            if (text[k] == '[') depth++;
            else if (text[k] == ']' && --depth == 0)
            {
                close = k;
                break;
            }
        }
        if (close < 0 || close + 1 >= text.Length || text[close + 1] != '(') return null;
        var end = -1;
        var parens = 0;
        for (var k = close + 1; k < text.Length; k++)
        {
            if (text[k] == '(') parens++;
            else if (text[k] == ')' && --parens == 0)
            {
                end = k;
                break;
            }
        }
        if (end < 0) return null;
        var target = text[(close + 2)..end].Trim();
        // An optional "title" after the url (also after a <url with spaces>).
        var title = "";
        if (target.StartsWith('<') && target.IndexOf('>') is > 0 and var gt && Title(target[(gt + 1)..]) is { } quoted)
        {
            title = quoted;
            target = target[..(gt + 1)];
        }
        else if (target.Length > 0 && target[^1] is '"' or '\'' && target.IndexOf(' ') is > 0 and var space && !target.StartsWith('<'))
        {
            title = Title(target[space..]) ?? "";
            target = target[..space];
        }
        var url = target.StartsWith('<') && target.EndsWith('>') ? target[1..^1] : target;
        return (text[(at + 1)..close], url.Replace("%20", " "), target, title, end + 1);
    }

    /// <summary>A link's "title" (or 'title') after its url; "" for none, null when what's there isn't one.</summary>
    private static string? Title(string rest)
    {
        rest = rest.Trim();
        if (rest.Length == 0) return "";
        return rest.Length >= 2 && rest[0] is '"' or '\'' && rest[^1] == rest[0] ? rest[1..^1] : null;
    }

    /// <summary>**strong**, __strong__, *emphasis*, _emphasis_ (not inside words) or ~~struck~~ starting at <paramref name="at"/>.</summary>
    private static (MdInline Inline, int End)? Emphasis(string text, int at, int depth)
    {
        var c = text[at];
        var run = 0;
        while (at + run < text.Length && text[at + run] == c) run++;
        if (c == '~' && run != 2) return null;
        var size = c == '~' ? 2 : Math.Min(run, 3);
        if (at + size >= text.Length || char.IsWhiteSpace(text[at + size])) return null;
        if (c == '_' && at > 0 && char.IsLetterOrDigit(text[at - 1])) return null;
        var delimiter = new string(c, size);
        for (var close = text.IndexOf(delimiter, at + size, StringComparison.Ordinal); close > 0; close = text.IndexOf(delimiter, close + 1, StringComparison.Ordinal))
        {
            if (char.IsWhiteSpace(text[close - 1]) || text[close - 1] == '\\') continue;
            // A longer run here belongs to something else ("**a*" doesn't close "**").
            if (close + size < text.Length && text[close + size] == c && size < 3) continue;
            if (c == '_' && close + size < text.Length && char.IsLetterOrDigit(text[close + size])) continue;
            var inner = ParseInlines(text[(at + size)..close], depth + 1);
            MdInline inline = (c, size) switch
            {
                ('~', _) => new MdStrike(inner),
                (_, 1) => new MdEmphasis(inner),
                (_, 2) => new MdStrong(inner),
                _ => new MdStrong([new MdEmphasis(inner)]),
            };
            return (inline, close + size);
        }
        return null;
    }

    /// <summary>The text of inlines without their formatting (for a picture's description, search and tooltips).</summary>
    public static string PlainText(IEnumerable<MdInline> inlines)
    {
        var builder = new StringBuilder();
        foreach (var inline in inlines)
        {
            builder.Append(inline switch
            {
                MdText t => t.Text,
                MdCodeSpan code => code.Code,
                MdStrong s => PlainText(s.Content),
                MdEmphasis e => PlainText(e.Content),
                MdStrike s => PlainText(s.Content),
                MdLink l => PlainText(l.Content),
                MdImage image => image.Alt,
                MdWikiLink w => w.Label,
                MdHashRef h => "#" + h.Token,
                MdLineBreak => "\n",
                _ => "",
            });
        }
        return builder.ToString();
    }
}
