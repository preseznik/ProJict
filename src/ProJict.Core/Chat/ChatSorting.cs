namespace ProJict.Core.Chat;

/// <summary>How Chat's list is sorted: by name (A–Z), by the last message (newest first), or by when they were added (newest first).</summary>
public enum ChatOrder { Name, LastMessage, Added }

/// <summary>
/// Sorting Chat's list (its sort button cycles the orders): channels and private conversations alike, a conversation by the
/// other one's name. Ties go by name.
/// </summary>
public static class ChatSorting
{
    public static ChatOrder Next(ChatOrder order) => order switch
    {
        ChatOrder.Name => ChatOrder.LastMessage,
        ChatOrder.LastMessage => ChatOrder.Added,
        _ => ChatOrder.Name,
    };

    /// <summary>"by name", "by the last message", "newest added first".</summary>
    public static string Describe(ChatOrder order) => order switch
    {
        ChatOrder.LastMessage => "by the last message",
        ChatOrder.Added => "newest added first",
        _ => "by name",
    };

    /// <summary>When something last happened in it: its newest message, or when it was started.</summary>
    public static DateTime LastActivity(ChatChannel channel) => channel.Messages.LastOrDefault()?.At ?? channel.CreatedAt;

    /// <param name="title">What it's called in the list: a channel's name, a conversation's other one.</param>
    public static IEnumerable<ChatChannel> Sort(IEnumerable<ChatChannel> channels, ChatOrder order, Func<ChatChannel, string> title) => order switch
    {
        ChatOrder.LastMessage => channels.OrderByDescending(LastActivity).ThenBy(title, StringComparer.OrdinalIgnoreCase),
        ChatOrder.Added => channels.OrderByDescending(c => c.CreatedAt).ThenBy(title, StringComparer.OrdinalIgnoreCase),
        _ => channels.OrderBy(title, StringComparer.OrdinalIgnoreCase),
    };
}
