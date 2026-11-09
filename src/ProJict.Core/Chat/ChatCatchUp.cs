using System.Globalization;

namespace ProJict.Core.Chat;

/// <summary>
/// How much of Chat an agent is handed at once (#35c53efa), so its context doesn't overflow: a backlog is caught up on,
/// not replayed; a read stops at a size; a long message shows its start. What's left out is a read or a search away.
/// </summary>
public static class ChatCatchUp
{
    /// <summary>read_channel's messages when it isn't told how many, and the most it gives.</summary>
    public const int DefaultRead = 10, MaxRead = 100;
    /// <summary>A read stops before this many characters of messages (one always comes).</summary>
    public const int ReadBudget = 16_000;
    /// <summary>Of a backlog: the newest messages, and older ones meant for the agent (private, @mentions) up to this many more.</summary>
    public const int Newest = 20, ForYou = 10;
    /// <summary>A message longer than this shows its start in lists, this long.</summary>
    public const int LongMessage = 1_200, LongStart = 1_000;
    /// <summary>The messages around one that read_channel around=… shows, on each side.</summary>
    public const int AroundEach = 5;

    /// <summary>What was left out of a channel's backlog: how many, and its oldest and newest.</summary>
    public sealed record LeftOut(ChatChannel Channel, int Count, ChatMessage Oldest, ChatMessage Newest);

    /// <summary>
    /// What of a backlog to hand over: its newest messages, and older ones meant for the agent (<paramref name="forYou"/>:
    /// private, @mentions, notices about it) up to <paramref name="forYouMax"/> more, in the order given; and per channel,
    /// what was left out.
    /// </summary>
    public static (List<(ChatChannel Channel, ChatMessage Message)> Shown, List<LeftOut> Left) Pick(
        IReadOnlyList<(ChatChannel Channel, ChatMessage Message)> waiting, Func<ChatChannel, ChatMessage, bool> forYou,
        int newest = Newest, int forYouMax = ForYou)
    {
        var keep = new HashSet<ChatMessage>(waiting.OrderByDescending(w => w.Message.At).Take(newest).Select(w => w.Message),
            ReferenceEqualityComparer.Instance);
        foreach (var (_, message) in waiting.Where(w => !keep.Contains(w.Message) && forYou(w.Channel, w.Message))
                     .OrderByDescending(w => w.Message.At).Take(forYouMax).ToList())
            keep.Add(message);
        var shown = waiting.Where(w => keep.Contains(w.Message)).ToList();
        var left = waiting.Where(w => !keep.Contains(w.Message)).GroupBy(w => w.Channel)
            .Select(g => new LeftOut(g.Key, g.Count(), g.MinBy(w => w.Message.At).Message, g.MaxBy(w => w.Message.At).Message))
            .ToList();
        return (shown, left);
    }

    /// <summary>
    /// A long message's start and how to read it all (<paramref name="readAll"/>, e.g. "read_channel #general id=4e84570c");
    /// a short one as it is.
    /// </summary>
    public static string Shorten(string text, string readAll)
    {
        if (text.Length <= LongMessage) return text;
        var cut = LongStart;
        if (char.IsHighSurrogate(text[cut - 1])) cut--;
        return text[..cut].TrimEnd() + $"… ({text.Length.ToString("N0", CultureInfo.InvariantCulture)} characters: {readAll} for all of it)";
    }

    /// <summary>
    /// The newest of <paramref name="messages"/> (oldest first) that fit: at most <paramref name="limit"/>, stopping before
    /// <paramref name="budget"/> characters as <paramref name="size"/> counts them. One always comes. Oldest first.
    /// </summary>
    public static List<ChatMessage> Latest(IReadOnlyList<ChatMessage> messages, int limit, Func<ChatMessage, int> size, int budget = ReadBudget)
    {
        var picked = new List<ChatMessage>();
        var used = 0;
        for (var i = messages.Count - 1; i >= 0 && picked.Count < limit; i--)
        {
            var s = size(messages[i]);
            if (picked.Count > 0 && used + s > budget) break;
            used += s;
            picked.Add(messages[i]);
        }
        picked.Reverse();
        return picked;
    }

    /// <summary>The message and the ones around it (<see cref="AroundEach"/> on each side), oldest first.</summary>
    public static List<ChatMessage> Around(IReadOnlyList<ChatMessage> messages, ChatMessage message, int each = AroundEach)
    {
        var at = messages.ToList().IndexOf(message);
        if (at < 0) return [message];
        var from = Math.Max(0, at - each);
        return messages.Skip(from).Take(at + each + 1 - from).ToList();
    }
}
