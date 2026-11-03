namespace ProJict.Core.Chat;

/// <summary>
/// People and agents you ignore in Chat (right-click a member › Ignore): their messages are hidden from you in channels,
/// and don't count as new, except the ones that @mention you. It's only for you, to declutter: they aren't told, lines
/// from ProJict about them still show, and your private conversation with them shows everything.
/// </summary>
public static class ChatIgnoring
{
    public static bool IsIgnored(IReadOnlyCollection<string>? ignored, string name) =>
        ignored is { Count: > 0 } && ignored.Contains(name, StringComparer.OrdinalIgnoreCase);

    /// <summary>Whether <paramref name="me"/> doesn't see the message: by someone they ignore, in a channel, and not mentioning them.</summary>
    public static bool Hides(ChatChannel channel, ChatMessage message, string me, IReadOnlyCollection<string>? ignored) =>
        !channel.Direct && !message.Notice && IsIgnored(ignored, message.By) && !ChatRoom.Mentions(channel, message, me);

    /// <summary>
    /// The messages <paramref name="me"/> sees in the channel: not those of people they ignore (unless they mention them),
    /// or with Chat's @ filter only those that @mention them (a private conversation shows everything either way).
    /// </summary>
    public static List<ChatMessage> Shown(ChatChannel channel, IEnumerable<ChatMessage> messages, string me, IReadOnlyCollection<string>? ignored, bool onlyMentions) =>
        onlyMentions && !channel.Direct ? [.. messages.Where(m => !m.Notice && !m.Deleted && ChatRoom.Mentions(channel, m, me))]
        : [.. messages.Where(m => !Hides(channel, m, me, ignored))];

    /// <summary>What's new for them in the channel, without what's hidden.</summary>
    public static IEnumerable<ChatMessage> Unread(ChatChannel channel, string me, IReadOnlyCollection<string>? ignored) =>
        ChatRoom.Unread(channel, me).Where(m => !Hides(channel, m, me, ignored));

    /// <summary>Ignores <paramref name="name"/>, or stops; the list to keep (null when nobody's ignored).</summary>
    public static List<string>? Set(List<string>? ignored, string name, bool on)
    {
        var list = (ignored ?? []).Where(n => !string.Equals(n, name, StringComparison.OrdinalIgnoreCase)).ToList();
        if (on) list.Add(name);
        return list.Count == 0 ? null : list;
    }
}
