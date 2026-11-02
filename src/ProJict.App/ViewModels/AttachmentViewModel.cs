using System.Globalization;
using System.IO;
using System.Windows;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using CommunityToolkit.Mvvm.Input;
using ProJict.App.Services;
using ProJict.Core.Features;
using ProJict.Core.Model;

namespace ProJict.App.ViewModels;

/// <summary>A file in a list that may play in place (audio or video): InlinePlayer plays it, PlayableTemplateSelector picks it.</summary>
public interface IPlayable
{
    bool Plays { get; }
}

/// <summary>A file attached to an item or note: a thumbnail for images, otherwise a chip with the file name.</summary>
public sealed partial class AttachmentViewModel : IPlayable
{
    // Rows are rebuilt on every edit; stored attachments never change, so each thumbnail is decoded once.
    private static readonly Dictionary<Guid, ImageSource?> Thumbnails = [];
    private const int ThumbnailPixels = 480;
    /// <summary>A frame from each video, for its player; also made once.</summary>
    private static readonly Dictionary<Guid, ImageSource?> Posters = [];
    private const int PosterPixels = 340;

    private readonly ProjectViewModel _owner;
    private readonly Action _remove;
    private readonly Action<Attachment>? _attachCopy;

    /// <param name="attachCopy">Attaches a marked-up copy to the same item or note.</param>
    public AttachmentViewModel(ProjectViewModel owner, Attachment model, Action remove, Action<Attachment>? attachCopy = null)
    {
        _owner = owner;
        _remove = remove;
        _attachCopy = attachCopy;
        Model = model;
        Path = owner.AttachmentPath(model);
        owner.EnsureFile(model);
    }

    /// <summary>A picture ProJict can show (and mark up) itself; other files open in their usual app.</summary>
    public bool OpensInViewer => ShowsThumbnail && _attachCopy != null;

    /// <summary>Stores a marked-up copy next to this attachment; returns its file name.</summary>
    public string SaveCopy(byte[] png, string fileName)
    {
        var copy = _owner.WriteAttachment(fileName, png);
        _attachCopy!(copy);
        return copy.FileName;
    }

    public Attachment Model { get; }
    public string Path { get; }
    public string FileName => Model.FileName;
    public ImageSource? Thumbnail => Model.IsImage ? LoadThumbnail() : null;
    public bool ShowsThumbnail => Thumbnail != null;
    public string SizeText => CleanedOut ? "cleaned out" : FormatSize(Model.Size);
    /// <summary>A private conversation's file that was cleaned out of their cache here (Settings › Chat).</summary>
    public bool CleanedOut => Model.Cached && _owner.Project.Remote == null && !File.Exists(Path);
    /// <summary>Audio or video: it plays in place.</summary>
    public MediaKind MediaKind => MediaLinks.KindOf(FileName);
    public bool Plays => MediaKind is MediaKind.Audio or MediaKind.Video;
    public string PlayPath => Path;
    /// <summary>A frame from a video, shown on its player until it plays.</summary>
    public ImageSource? Poster
    {
        get
        {
            if (MediaKind != MediaKind.Video) return null;
            if (!Posters.TryGetValue(Model.Id, out var poster)) Posters[Model.Id] = poster = ShellThumbnails.Get(Path, PosterPixels);
            return poster;
        }
    }
    public string ToolTip => CleanedOut
        ? $"{FileName} · {FormatSize(Model.Size)}\nCleaned out: private conversations' files are kept for a while, then deleted (Settings › Chat)"
        : $"{FileName} · {SizeText}\nClick to open · right-click for more";
    /// <summary>For comments, which have no right-click menu on their files.</summary>
    public string ShortToolTip => $"{FileName} · {SizeText}\nClick to open";

    [RelayCommand] private void Open() => _owner.OpenAttachment(this);
    [RelayCommand] private void OpenExternally() => _owner.OpenAttachmentExternally(this);
    [RelayCommand] private void ShowInFolder() => _owner.ShowAttachmentInFolder(this);
    [RelayCommand] private void Remove() => _remove();

    [RelayCommand]
    private void CopyPath()
    {
        try { Clipboard.SetText(Path); }
        catch (System.Runtime.InteropServices.ExternalException) { }
    }

    private ImageSource? LoadThumbnail()
    {
        if (Thumbnails.TryGetValue(Model.Id, out var cached)) return cached;
        ImageSource? image = null;
        try
        {
            using var stream = File.OpenRead(Path);
            var frame = BitmapDecoder.Create(stream, BitmapCreateOptions.DelayCreation | BitmapCreateOptions.IgnoreColorProfile, BitmapCacheOption.None).Frames[0];
            var scale = Math.Min(1.0, Math.Min((double)ThumbnailPixels / frame.PixelWidth, ThumbnailPixels / 3.0 / frame.PixelHeight));
            stream.Position = 0;
            var bitmap = new BitmapImage();
            bitmap.BeginInit();
            bitmap.StreamSource = stream;
            bitmap.CacheOption = BitmapCacheOption.OnLoad;
            bitmap.CreateOptions = BitmapCreateOptions.IgnoreColorProfile;
            if (scale < 1) bitmap.DecodePixelWidth = Math.Max(1, (int)Math.Round(frame.PixelWidth * scale));
            bitmap.EndInit();
            bitmap.Freeze();
            image = bitmap;
        }
        catch (Exception)
        {
            // Missing file or a format Windows can't decode: the file chip is shown instead.
        }
        Thumbnails[Model.Id] = image;
        return image;
    }

    public static string FormatSize(long bytes) => bytes switch
    {
        < 1024 => $"{bytes} B",
        < 1024 * 1024 => string.Format(CultureInfo.CurrentCulture, "{0:0} KB", bytes / 1024.0),
        _ => string.Format(CultureInfo.CurrentCulture, "{0:0.#} MB", bytes / (1024.0 * 1024)),
    };
}
