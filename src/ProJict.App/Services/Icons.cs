using System.IO;
using System.Windows;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using ProJict.Core.Model;
using ProJict.Core.Storage;

namespace ProJict.App.Services;

/// <summary>
/// Small square icons for projects and channels: made from any image (cut to a square in the middle, 128 px) and kept as
/// an attachment, so another ProJict a project or channel is shared with gets it like any other file. Used on the UI thread.
/// </summary>
public static class Icons
{
    public const int Size = 128;

    /// <summary>Asks for an image file (the title says what for); tests answer for you. Null when cancelled.</summary>
    public static Func<string, string?> Pick { get; set; } = title =>
    {
        var dialog = new Microsoft.Win32.OpenFileDialog { Title = title, Filter = "Images|*.png;*.jpg;*.jpeg;*.bmp;*.gif;*.webp|All files|*.*" };
        return dialog.ShowDialog(Application.Current.MainWindow) == true ? dialog.FileName : null;
    };

    /// <summary>An icon made from the image file and stored. Throws InvalidOperationException when it isn't an image.</summary>
    public static Attachment Make(ProjectStore store, string imageFile) => store.WriteAttachment("icon.png", AgentPictures.SquarePng(imageFile, Size));

    // Shown in many rows, rebuilt often: each file is read once (again when it changes).
    private static readonly Dictionary<string, (DateTime Written, ImageSource? Image)> Cache = new(StringComparer.OrdinalIgnoreCase);

    /// <summary>The picture in the file; null when it isn't there (yet) or can't be read.</summary>
    public static ImageSource? Load(string path)
    {
        var file = new FileInfo(path);
        if (!file.Exists) return null;
        if (Cache.TryGetValue(path, out var cached) && cached.Written == file.LastWriteTimeUtc) return cached.Image;
        ImageSource? image = null;
        try
        {
            var bitmap = new BitmapImage();
            bitmap.BeginInit();
            bitmap.UriSource = new Uri(file.FullName);
            bitmap.CacheOption = BitmapCacheOption.OnLoad;
            bitmap.CreateOptions = BitmapCreateOptions.IgnoreImageCache | BitmapCreateOptions.IgnoreColorProfile;
            bitmap.DecodePixelWidth = Size;
            bitmap.EndInit();
            bitmap.Freeze();
            image = bitmap;
        }
        catch (Exception)
        {
            // Not readable: the # or the status dot shows as before.
        }
        Cache[path] = (file.LastWriteTimeUtc, image);
        return image;
    }
}
