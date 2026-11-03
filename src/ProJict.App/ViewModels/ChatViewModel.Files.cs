using System.Globalization;
using System.Windows.Media;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using ProJict.Core.Chat;
using ProJict.Core.Model;

namespace ProJict.App.ViewModels;

/// <summary>Which of a channel's tabs shows.</summary>
public enum ChannelTab { Messages, Media, Files }

/// <summary>
/// A channel's Media and Files tabs, beside its messages: the pictures, videos and sounds posted in it, in a grid or a
/// list (where they play in place), and its other files in a list; both newest first and searchable. Files the Chat
/// cache cleaned out aren't held any more, so they're left out (and counted). Opening another channel shows its messages.
/// </summary>
public sealed partial class ChatViewModel
{
    [ObservableProperty] private ChannelTab _tab;
    [ObservableProperty] private string _fileSearch = "";
    /// <summary>Media as a list, where videos and sounds play in place, instead of a grid of pictures.</summary>
    [ObservableProperty] private bool _mediaAsList;

    public bool ShowsMessages => Tab == ChannelTab.Messages;
    public bool ShowsMedia => Tab == ChannelTab.Media;
    public bool ShowsFiles => Tab == ChannelTab.Files;
    public bool ShowsFileGrid => ShowsMedia && !MediaAsList;
    public bool ShowsFileList => ShowsFiles || (ShowsMedia && MediaAsList);

    /// <summary>The tab's files, as found.</summary>
    public List<ChatFileViewModel> Files { get; private set; } = [];
    public string MediaTabText { get; private set; } = "Media";
    public string FilesTabText { get; private set; } = "Files";
    /// <summary>Above the files: that there are none, none match, or how many the Chat cache cleaned out.</summary>
    public string? FilesNote { get; private set; }

    [RelayCommand]
    private void ShowTab(string? tab) => Tab = Enum.TryParse<ChannelTab>(tab, out var shown) ? shown : ChannelTab.Messages;

    partial void OnTabChanged(ChannelTab value)
    {
        RefreshFiles();
        // Back at the messages: what came meanwhile is read.
        if (value == ChannelTab.Messages) ReadIfLooking();
    }

    partial void OnFileSearchChanged(string value) => RefreshFiles();
    partial void OnMediaAsListChanged(bool value) => RefreshFiles();

    /// <summary>The tabs' counts, and the files of the one shown (the channel's messages changed, or the tab or search).</summary>
    internal void RefreshFiles()
    {
        var channel = Channel;
        var me = NameIn(channel);
        var ignored = _state.Settings.IgnoredInChat;
        int Held(bool media) => channel == null ? 0 : ChatFiles.Of(channel, media, null, me, ignored).Count(f => !CleanedOut(f.Attachment));
        MediaTabText = Held(true) is > 0 and var media ? $"Media · {media}" : "Media";
        FilesTabText = Held(false) is > 0 and var files ? $"Files · {files}" : "Files";
        if (channel == null || ShowsMessages)
        {
            Files = [];
            FilesNote = null;
        }
        else
        {
            var owner = AttachmentOwner;
            var found = ChatFiles.Of(channel, ShowsMedia, FileSearch, me, ignored).Select(f => new ChatFileViewModel(f, new AttachmentViewModel(owner, f.Attachment, () => { }), this)).ToList();
            Files = [.. found.Where(f => !f.File.CleanedOut)];
            var gone = found.Count - Files.Count;
            var what = ShowsMedia ? "pictures, videos or sounds" : "other files";
            FilesNote = Files.Count == 0 && gone == 0
                ? string.IsNullOrWhiteSpace(FileSearch) ? $"No {what} posted here yet." : $"Nothing here matches “{FileSearch.Trim()}”."
                : gone > 0 ? $"{Count(gone, "older file")} cleaned out of the Chat cache (Settings › Chat) isn't shown." : null;
        }
        foreach (var name in new[] { nameof(ShowsMessages), nameof(ShowsMedia), nameof(ShowsFiles), nameof(ShowsFileGrid), nameof(ShowsFileList), nameof(Files),
                     nameof(MediaTabText), nameof(FilesTabText), nameof(FilesNote) })
            OnPropertyChanged(name);
    }

    /// <summary>A file of the Chat cache that was cleaned out here.</summary>
    private bool CleanedOut(Attachment file) =>
        file.Cached && Here.Host == null && !System.IO.File.Exists(AttachmentOwner.AttachmentPath(file));

    /// <summary>The file's message, in the channel's messages.</summary>
    internal void ShowInChat(ChatFileViewModel file)
    {
        FileSearch = "";
        Tab = ChannelTab.Messages;
        HighlightMessage(file.Message.Id);
    }
}

/// <summary>A file in a channel's Media or Files tab: the file itself (to open, play or keep), who posted it and when.</summary>
public sealed partial class ChatFileViewModel(ChatFile file, AttachmentViewModel attachment, ChatViewModel owner)
{
    public AttachmentViewModel File => attachment;
    public ChatMessage Message => file.Message;
    public string FileName => attachment.FileName;
    /// <summary>For its tile in the grid: the picture, or a frame of the video.</summary>
    public ImageSource? Picture => attachment.Thumbnail ?? attachment.Poster;
    public bool HasPicture => Picture != null;
    public bool IsVideo => attachment.MediaKind == MediaKind.Video;
    public bool IsAudio => attachment.MediaKind == MediaKind.Audio;
    public string When => file.Message.At.ToString(file.Message.At.Date == DateTime.Today ? "HH:mm" : "d MMM yyyy", CultureInfo.CurrentCulture);
    public string Details => $"{file.Message.By} · {When} · {attachment.SizeText}";
    public string ToolTip => $"{FileName}\n{Details}\nClick to open · right-click to show it in the chat";

    [RelayCommand] private void ShowInChat() => owner.ShowInChat(this);
}
