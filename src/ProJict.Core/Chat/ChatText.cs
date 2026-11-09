namespace ProJict.Core.Chat;

public enum ChatSegmentKind { Text, Mention, Channel, Item, Link, Action }

/// <summary>
/// A piece of a message: plain text, an @mention, a #channel, an item (# and its id), a web link, or a word that is a
/// button for you (an agent's "Approve" while its ship plan waits for you). <paramref name="Bold"/>: inside **…**.
/// </summary>
public sealed record ChatSegment(ChatSegmentKind Kind, string Text, string? Target = null, bool Bold = false);

/// <summary>@mentions, #channel and item links, web links and **bold** in chat messages (and bold in discussions).</summary>
public static class ChatText
{
    /// <summary>"@everyone" mentions everyone in the channel (see <see cref="ChatRoom.Mentions"/>).</summary>
    public const string Everyone = "everyone";

    /// <summary>
    /// Whether the text @mentions this name. An agent with a label ("Claude Code · login fix") is also reached by its
    /// plain name ("@Claude Code").
    /// </summary>
    public static bool Mentions(string text, string name)
    {
        if (text.Length == 0 || name.Length == 0) return false;
        var label = name.IndexOf(" · ", StringComparison.Ordinal);
        // A guest's agent, "Ada (via Alice)", is mentioned as @Ada too.
        var via = name.LastIndexOf(" (via ", StringComparison.Ordinal);
        return Has(text, name) || (label > 0 && Has(text, name[..label])) || (via > 0 && name.EndsWith(')') && Has(text, name[..via]));
    }

    /// <summary>Whether the text says "@everyone".</summary>
    public static bool MentionsEveryone(string text) => Has(text, Everyone);

    private static bool Has(string text, string name)
    {
        for (var i = text.IndexOf('@'); i >= 0; i = text.IndexOf('@', i + 1))
            if (StartsAt(text, i, name)) return true;
        return false;
    }

    /// <summary>"@name" at <paramref name="at"/>, standing on its own (not inside an e-mail address or a longer word).</summary>
    private static bool StartsAt(string text, int at, string name)
    {
        if (at > 0 && char.IsLetterOrDigit(text[at - 1])) return false;
        if (at + 1 + name.Length > text.Length || string.Compare(text, at + 1, name, 0, name.Length, StringComparison.OrdinalIgnoreCase) != 0) return false;
        var after = at + 1 + name.Length;
        return after == text.Length || !char.IsLetterOrDigit(text[after]);
    }

    /// <summary>
    /// The text's **bold** parts, as Markdown marks them: "**" before a non-space opens, the next "**" after a non-space on
    /// the same line closes, and the asterisks go. Asterisks without a pair stay as they are.
    /// </summary>
    public static IReadOnlyList<(string Text, bool Bold)> BoldSpans(string text)
    {
        var spans = new List<(string Text, bool Bold)>();
        var from = 0;
        for (var open = text.IndexOf("**", StringComparison.Ordinal); open >= 0; open = text.IndexOf("**", open + 2, StringComparison.Ordinal))
        {
            if (open < from || open + 2 >= text.Length || char.IsWhiteSpace(text[open + 2])) continue;
            var close = -1;
            for (var at = text.IndexOf("**", open + 3, StringComparison.Ordinal); at >= 0; at = text.IndexOf("**", at + 1, StringComparison.Ordinal))
            {
                if (text.IndexOf('\n', open, at - open) >= 0) break;
                if (!char.IsWhiteSpace(text[at - 1]))
                {
                    close = at;
                    break;
                }
            }
            if (close < 0) continue;
            if (open > from) spans.Add((text[from..open], false));
            spans.Add((text[(open + 2)..close], true));
            from = close + 2;
            open = close;
        }
        if (from < text.Length) spans.Add((text[from..], false));
        return spans;
    }

    /// <summary>
    /// Splits a message into pieces to show: @mentions of the given names (longest first) and @everyone, #channels that
    /// exist, items as # and the first 8 characters of their id (#4e84570c), and http(s) links; those inside **…** are bold.
    /// </summary>
    public static IReadOnlyList<ChatSegment> Parse(string text, IEnumerable<string> names, Func<string, bool> isChannel, Func<string, Guid?> findItem)
    {
        var candidates = names.Append(Everyone).Where(n => n.Length > 0).Distinct(StringComparer.OrdinalIgnoreCase).OrderByDescending(n => n.Length).ToList();
        var segments = new List<ChatSegment>();
        foreach (var (span, bold) in BoldSpans(text))
            segments.AddRange(Parse(span, bold, candidates, isChannel, findItem));
        return segments;
    }

    private static List<ChatSegment> Parse(string text, bool bold, List<string> candidates, Func<string, bool> isChannel, Func<string, Guid?> findItem)
    {
        var segments = new List<ChatSegment>();
        var plain = new System.Text.StringBuilder();
        void Add(ChatSegment segment)
        {
            if (plain.Length > 0) segments.Add(new ChatSegment(ChatSegmentKind.Text, plain.ToString(), Bold: bold));
            plain.Clear();
            segments.Add(segment with { Bold = bold });
        }

        var i = 0;
        while (i < text.Length)
        {
            var c = text[i];
            var standsAlone = i == 0 || !char.IsLetterOrDigit(text[i - 1]);
            if (c == '@' && standsAlone && candidates.FirstOrDefault(n => StartsAt(text, i, n)) is { } name)
            {
                Add(new ChatSegment(ChatSegmentKind.Mention, text.Substring(i, name.Length + 1), name));
                i += name.Length + 1;
                continue;
            }
            if (c == '#' && standsAlone)
            {
                var end = i + 1;
                while (end < text.Length && (char.IsAsciiLetterOrDigit(text[end]) || text[end] == '-')) end++;
                var token = text[(i + 1)..end].TrimEnd('-');
                if (token.Length == 8 && token.All(char.IsAsciiHexDigit) && findItem(token) is { } item)
                {
                    Add(new ChatSegment(ChatSegmentKind.Item, "#" + token, item.ToString()));
                    i += token.Length + 1;
                    continue;
                }
                if (token.Length > 0 && isChannel(token.ToLowerInvariant()))
                {
                    Add(new ChatSegment(ChatSegmentKind.Channel, "#" + token, token.ToLowerInvariant()));
                    i += token.Length + 1;
                    continue;
                }
            }
            if (c == 'h' && standsAlone && (Starts(text, i, "https://") || Starts(text, i, "http://")))
            {
                var end = i;
                while (end < text.Length && !char.IsWhiteSpace(text[end])) end++;
                while (end > i && ".,;:!?)]'\"".Contains(text[end - 1])) end--;
                var url = text[i..end];
                if (Uri.TryCreate(url, UriKind.Absolute, out _))
                {
                    Add(new ChatSegment(ChatSegmentKind.Link, url, url));
                    i = end;
                    continue;
                }
            }
            plain.Append(c);
            i++;
        }
        if (plain.Length > 0) segments.Add(new ChatSegment(ChatSegmentKind.Text, plain.ToString(), Bold: bold));
        return segments;
    }

    private static bool Starts(string text, int at, string prefix) => string.Compare(text, at, prefix, 0, prefix.Length, StringComparison.OrdinalIgnoreCase) == 0;
}

/// <summary>A message found by Ctrl+K search.</summary>
public sealed record ChatHit(ChatChannel Channel, ChatMessage Message, string Snippet);

public static class ChatSearch
{
    /// <summary>
    /// Messages (newest first) in which all terms appear, in the text, the author or the channel's name; only
    /// <paramref name="by"/>'s when given (agents' search_chat, #35c53efa).
    /// </summary>
    public static List<ChatHit> Search(IEnumerable<ChatChannel> channels, string query, int max = 50, string? by = null)
    {
        var terms = query.Split(' ', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
        if (terms.Length == 0) return [];
        return channels
            .SelectMany(c => c.Messages.Where(m => !m.Notice && !m.Deleted && (by == null || string.Equals(m.By, by, StringComparison.OrdinalIgnoreCase)))
                .Select(m => (Channel: c, Message: m)))
            .Where(x => terms.All(t => $"{(x.Channel.Direct ? "" : "#" + x.Channel.Name)} {x.Message.By} {x.Message.Text}".Contains(t, StringComparison.OrdinalIgnoreCase)))
            .OrderByDescending(x => x.Message.At)
            .Take(max)
            .Select(x => new ChatHit(x.Channel, x.Message, Snippet(x.Message.Text, terms)))
            .ToList();
    }

    private static string Snippet(string text, string[] terms)
    {
        var lines = text.Replace("\r\n", "\n").Split('\n');
        var line = (lines.FirstOrDefault(l => terms.Any(t => l.Contains(t, StringComparison.OrdinalIgnoreCase))) ?? lines[0]).Trim();
        return line.Length > 160 ? line[..160] + "…" : line;
    }
}
