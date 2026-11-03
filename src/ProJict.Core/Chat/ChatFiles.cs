using ProJict.Core.Features;
using ProJict.Core.Model;

namespace ProJict.Core.Chat;

/// <summary>A file posted in a channel, with the message it came in.</summary>
public sealed record ChatFile(ChatMessage Message, Attachment Attachment);

/// <summary>
/// A channel's files for its Media and Files tabs, newest first: its pictures, videos and sounds, or its other files. A
/// search finds them by all its words, in the file's name, who posted it or the message's text. Not those of deleted
/// messages, nor of people you ignore.
/// </summary>
public static class ChatFiles
{
    public static bool IsMedia(Attachment file) => file.IsImage || MediaLinks.KindOf(file.FileName) is MediaKind.Audio or MediaKind.Video;

    public static IReadOnlyList<ChatFile> Of(ChatChannel channel, bool media, string? search, string me, IReadOnlyCollection<string>? ignored)
    {
        var words = (search ?? "").Split(' ', StringSplitOptions.RemoveEmptyEntries);
        bool Has(ChatFile f, string word) =>
            f.Attachment.FileName.Contains(word, StringComparison.OrdinalIgnoreCase) || f.Message.By.Contains(word, StringComparison.OrdinalIgnoreCase) ||
            f.Message.Text.Contains(word, StringComparison.OrdinalIgnoreCase);
        return [.. channel.Messages.Where(m => !m.Deleted && !ChatIgnoring.Hides(channel, m, me, ignored))
            .SelectMany(m => (m.Attachments ?? []).Select(a => new ChatFile(m, a)))
            .Where(f => IsMedia(f.Attachment) == media && words.All(w => Has(f, w)))
            .OrderByDescending(f => f.Message.At)];
    }
}
