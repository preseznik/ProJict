using System.Globalization;
using ProJict.Core.Features;

namespace ProJict.Core.Chat;

/// <summary>
/// How a channel's messages are laid out, without any screen: the day's name above its first message, the "New" line
/// above the first one you haven't read (not your own, nor a line from ProJict), and the author's name over a run of
/// messages, again after five quiet minutes, for another author, after a line from ProJict, over a reply and at the New
/// line. Lines from ProJict ("Lark joined") never have it.
/// </summary>
public static class ChatTimeline
{
    public static readonly TimeSpan RunGap = TimeSpan.FromMinutes(5);

    /// <summary>One message as it shows: the day above it (if it's the day's first), whether the New line is above it, and whether its author's name is.</summary>
    public sealed record Line(ChatMessage Message, string? DayHeader, bool FirstUnread, bool ShowHeader);

    public static IReadOnlyList<Line> Lay(IReadOnlyList<ChatMessage> messages, DateTime? unreadFrom, Func<ChatMessage, bool> isMine, DateTime today, CultureInfo culture)
    {
        var lines = new List<Line>(messages.Count);
        ChatMessage? previous = null;
        var newLine = false;
        foreach (var message in messages)
        {
            var first = !newLine && unreadFrom is { } from && message.At > from && !message.Notice && !isMine(message);
            newLine |= first;
            var day = previous == null || previous.At.Date != message.At.Date
                ? ActivityPeriods.Label(message.At.Date, ActivityGrouping.Day, today, culture) : null;
            var header = !message.Notice && (day != null || first || previous == null || previous.Notice ||
                                             !string.Equals(previous.By, message.By, StringComparison.OrdinalIgnoreCase) ||
                                             message.At - previous.At > RunGap || message.ReplyTo != null);
            lines.Add(new Line(message, day, first, header));
            previous = message;
        }
        return lines;
    }

    /// <summary>A message in brief: who wrote it ("You" for yours), its text on one line, and when.</summary>
    public sealed record Brief(string Who, string Text, string Time, bool Mine);

    /// <summary>
    /// The last <paramref name="most"/> messages in brief (not notices or deleted ones), oldest first: for an agent's card
    /// and the channel under the office. The text is cut at 300 characters, and files alone say "[2 files]".
    /// </summary>
    public static IReadOnlyList<Brief> Briefly(IReadOnlyList<ChatMessage> messages, int most, Func<ChatMessage, bool> isMine, DateTime today, CultureInfo culture) =>
        [.. messages.Where(m => !m.Notice && !m.Deleted).TakeLast(most).Select(m =>
        {
            var mine = isMine(m);
            var text = string.Join(' ', m.Text.Split(['\r', '\n'], StringSplitOptions.RemoveEmptyEntries)).Trim();
            if (text.Length == 0 && m.Attachments is { Count: > 0 } files) text = files.Count == 1 ? "[1 file]" : $"[{files.Count} files]";
            return new Brief(mine ? "You" : m.By, text.Length > 300 ? text[..299].TrimEnd() + "…" : text,
                m.At.ToString(m.At.Date == today ? "HH:mm" : "d MMM HH:mm", culture), mine);
        })];

    /// <summary>A message's first line, cut at <paramref name="most"/> characters (for the one a reply quotes).</summary>
    public static string Snippet(string text, int most = 90)
    {
        var line = text.Replace("\r\n", "\n").Split('\n')[0];
        return line.Length > most ? line[..most] + "…" : line;
    }
}
