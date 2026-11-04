using System.Globalization;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using ProJict.Core.Chat;
using ProJict.Core.Features;
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
        Viewing = null;
        RefreshFiles();
        // Back at the messages: what came meanwhile is read.
        if (value == ChannelTab.Messages) ReadIfLooking();
    }

    partial void OnFileSearchChanged(string value) => RefreshFiles();
    partial void OnMediaAsListChanged(bool value)
    {
        Viewing = null;
        RefreshFiles();
    }

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
        // The file in the viewer went (its message was deleted, or a search left it out): the viewer closes.
        if (Viewing != null && ViewingIndex < 0) Viewing = null;
        foreach (var name in new[] { nameof(ShowsMessages), nameof(ShowsMedia), nameof(ShowsFiles), nameof(ShowsFileGrid), nameof(ShowsFileList), nameof(Files),
                     nameof(MediaTabText), nameof(FilesTabText), nameof(FilesNote), nameof(CanViewPrevious), nameof(CanViewNext), nameof(ViewingPosition) })
            OnPropertyChanged(name);
    }

    // ───────────── The viewer (#9113ea6a) ─────────────

    /// <summary>
    /// The Media tab's file open in the viewer over the tab: a picture as big as it fits, a video or sound playing. A
    /// click beside it, Esc or × closes it; ← and → show the one before or after. Null while none is open.
    /// </summary>
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(IsViewing), nameof(CanViewPrevious), nameof(CanViewNext), nameof(ViewingPosition))]
    private ChatFileViewModel? _viewing;

    public bool IsViewing => Viewing != null;
    private int ViewingIndex => Viewing is { } viewing ? Files.FindIndex(f => f.File.Model.Id == viewing.File.Model.Id) : -1;
    public bool CanViewPrevious => ViewingIndex > 0;
    public bool CanViewNext => ViewingIndex is >= 0 and var i && i < Files.Count - 1;
    /// <summary>"3 of 9", among the tab's files as found.</summary>
    public string ViewingPosition => ViewingIndex is >= 0 and var i ? $"{i + 1} of {Files.Count}" : "";

    /// <summary>Opens the file in the viewer; one that isn't here opens as before (which says why not).</summary>
    internal void View(ChatFileViewModel file)
    {
        if (!file.IsHere)
        {
            file.File.OpenCommand.Execute(null);
            return;
        }
        // Opening one from the Chat cache counts as using it, as opening it in its app does: it stays as long again.
        if (file.File.Model.Cached) PrivateFiles.Touch(file.File.Path);
        Viewing = file;
    }

    [RelayCommand] private void CloseViewer() => Viewing = null;

    [RelayCommand]
    private void ViewPrevious()
    {
        if (CanViewPrevious) View(Files[ViewingIndex - 1]);
    }

    [RelayCommand]
    private void ViewNext()
    {
        if (CanViewNext) View(Files[ViewingIndex + 1]);
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
public sealed partial class ChatFileViewModel : ObservableObject
{
    private readonly ChatFile file;
    private readonly AttachmentViewModel attachment;
    private readonly ChatViewModel owner;

    public ChatFileViewModel(ChatFile file, AttachmentViewModel attachment, ChatViewModel owner)
    {
        this.file = file;
        this.attachment = attachment;
        this.owner = owner;
        // A video's frame comes a moment later.
        attachment.PropertyChanged += (_, e) =>
        {
            if (e.PropertyName is not (nameof(AttachmentViewModel.Poster) or "" or null)) return;
            OnPropertyChanged(nameof(Picture));
            OnPropertyChanged(nameof(HasPicture));
        };
    }

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
    public string ToolTip => $"{FileName}\n{Details}\nClick to view it here · right-click to open it in its app or show it in the chat";

    /// <summary>The file is on this computer (a host's file comes when it's first shown).</summary>
    public bool IsHere => System.IO.File.Exists(attachment.Path);
    public bool IsPicture => attachment.Model.IsImage;
    public bool Plays => attachment.Plays;
    /// <summary>A sound's player stays a comfortable width in the viewer; a video's fills it.</summary>
    public double PlayerMaxWidth => IsAudio ? 560 : double.PositiveInfinity;

    private ImageSource? _fullPicture;
    private bool _fullLoaded;

    /// <summary>The picture for the viewer, as big as a screen shows it (loaded when it's opened there, not for the tile).</summary>
    public ImageSource? FullPicture
    {
        get
        {
            if (_fullLoaded || !IsPicture) return _fullPicture;
            _fullLoaded = true;
            _fullPicture = LoadPicture(attachment.Path, ViewerPixels);
            return _fullPicture;
        }
    }

    /// <summary>The picture couldn't be shown (a format Windows can't decode).</summary>
    public bool PictureFailed => IsPicture && FullPicture == null;

    /// <summary>The widest a picture is decoded for the viewer.</summary>
    private const int ViewerPixels = 3840;

    private static ImageSource? LoadPicture(string path, int maxWidth)
    {
        try
        {
            using var stream = System.IO.File.OpenRead(path);
            var frame = BitmapDecoder.Create(stream, BitmapCreateOptions.DelayCreation | BitmapCreateOptions.IgnoreColorProfile, BitmapCacheOption.None).Frames[0];
            stream.Position = 0;
            var bitmap = new BitmapImage();
            bitmap.BeginInit();
            bitmap.StreamSource = stream;
            bitmap.CacheOption = BitmapCacheOption.OnLoad;
            bitmap.CreateOptions = BitmapCreateOptions.IgnoreColorProfile;
            if (frame.PixelWidth > maxWidth) bitmap.DecodePixelWidth = maxWidth;
            bitmap.EndInit();
            bitmap.Freeze();
            return bitmap;
        }
        catch (Exception)
        {
            // A format Windows can't decode: the viewer says so, and it still opens in its app.
            return null;
        }
    }

    [RelayCommand] private void ShowInChat() => owner.ShowInChat(this);
    [RelayCommand] private void View() => owner.View(this);
}
