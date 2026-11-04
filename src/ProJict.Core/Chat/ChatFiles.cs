using ProJict.Core.Features;
using ProJict.Core.Model;

namespace ProJict.Core.Chat;

/// <summary>A file posted in a channel, with the message it came in.</summary>
public sealed record ChatFile(ChatMessage Message, Attachment Attachment);

/// <summary>How a channel's Media and Files tabs are sorted (#b3359822): newest or oldest first, by name, or largest first.</summary>
public enum ChatFileOrder { Newest, Oldest, Name, Size }

/// <summary>
/// A channel's files for its Media and Files tabs, newest first unless sorted otherwise: its pictures, videos and sounds,
/// or its other files. A search finds them by all its words, in the file's name, who posted it or the message's text. Not
/// those of deleted messages, nor of people you ignore.
/// </summary>
public static class ChatFiles
{
    public static bool IsMedia(Attachment file) => file.IsImage || MediaLinks.KindOf(file.FileName) is MediaKind.Audio or MediaKind.Video;

    public static IReadOnlyList<ChatFile> Of(ChatChannel channel, bool media, string? search, string me, IReadOnlyCollection<string>? ignored,
        ChatFileOrder order = ChatFileOrder.Newest)
    {
        var words = (search ?? "").Split(' ', StringSplitOptions.RemoveEmptyEntries);
        bool Has(ChatFile f, string word) =>
            f.Attachment.FileName.Contains(word, StringComparison.OrdinalIgnoreCase) || f.Message.By.Contains(word, StringComparison.OrdinalIgnoreCase) ||
            f.Message.Text.Contains(word, StringComparison.OrdinalIgnoreCase);
        return [.. channel.Messages.Where(m => !m.Deleted && !ChatIgnoring.Hides(channel, m, me, ignored))
            .SelectMany(m => (m.Attachments ?? []).Select(a => new ChatFile(m, a)))
            .Where(f => IsMedia(f.Attachment) == media && words.All(w => Has(f, w)))
            .Sort(order)];
    }

    /// <summary>In the order asked; ties go newest first.</summary>
    public static IEnumerable<ChatFile> Sort(this IEnumerable<ChatFile> files, ChatFileOrder order) => order switch
    {
        ChatFileOrder.Oldest => files.OrderBy(f => f.Message.At),
        ChatFileOrder.Name => files.OrderBy(f => f.Attachment.FileName, StringComparer.CurrentCultureIgnoreCase).ThenByDescending(f => f.Message.At),
        ChatFileOrder.Size => files.OrderByDescending(f => f.Attachment.Size).ThenByDescending(f => f.Message.At),
        _ => files.OrderByDescending(f => f.Message.At),
    };

    /// <summary>The sort button: newest, oldest, by name, largest, then newest again.</summary>
    public static ChatFileOrder Next(ChatFileOrder order) => order switch
    {
        ChatFileOrder.Newest => ChatFileOrder.Oldest,
        ChatFileOrder.Oldest => ChatFileOrder.Name,
        ChatFileOrder.Name => ChatFileOrder.Size,
        _ => ChatFileOrder.Newest,
    };

    /// <summary>The sort button's word: "Newest", "Oldest", "A–Z", "Largest".</summary>
    public static string Label(ChatFileOrder order) => order switch
    {
        ChatFileOrder.Oldest => "Oldest",
        ChatFileOrder.Name => "A–Z",
        ChatFileOrder.Size => "Largest",
        _ => "Newest",
    };

    /// <summary>"newest first", "oldest first", "by name", "largest first".</summary>
    public static string Describe(ChatFileOrder order) => order switch
    {
        ChatFileOrder.Oldest => "oldest first",
        ChatFileOrder.Name => "by name",
        ChatFileOrder.Size => "largest first",
        _ => "newest first",
    };
}
