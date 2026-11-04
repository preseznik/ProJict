using System.IO;
using System.Windows;
using System.Windows.Media;
using System.Windows.Media.Imaging;

namespace ProJict.App.Services;

/// <summary>
/// Agents' pictures for the Agents view: one square PNG per agent name in the data folder's "agents" folder, made from
/// any image (cut to a square in the middle, 256 px). Used on the UI thread.
/// </summary>
public sealed class AgentPictures(string dataRoot)
{
    public const int Size = 256;

    // Shown on every card and row, rebuilt often: each picture is read once (again when it changes).
    private readonly Dictionary<string, (DateTime Written, ImageSource? Image)> _cache = new(StringComparer.OrdinalIgnoreCase);

    /// <summary>A picture was set or removed.</summary>
    public event Action? Changed;

    private string Folder => Path.Combine(dataRoot, "agents");

    public string PathOf(string agent)
    {
        var invalid = Path.GetInvalidFileNameChars();
        return Path.Combine(Folder, new string(agent.Trim().Select(c => invalid.Contains(c) ? '_' : c).ToArray()).ToLowerInvariant() + ".png");
    }

    public bool Has(string agent) => File.Exists(PathOf(agent));

    public ImageSource? Of(string agent)
    {
        var file = new FileInfo(PathOf(agent));
        if (!file.Exists) return null;
        if (_cache.TryGetValue(agent, out var cached) && cached.Written == file.LastWriteTimeUtc) return cached.Image;
        ImageSource? image = null;
        try
        {
            var bitmap = new BitmapImage();
            bitmap.BeginInit();
            bitmap.UriSource = new Uri(file.FullName);
            bitmap.CacheOption = BitmapCacheOption.OnLoad;
            bitmap.CreateOptions = BitmapCreateOptions.IgnoreImageCache | BitmapCreateOptions.IgnoreColorProfile;
            bitmap.EndInit();
            bitmap.Freeze();
            image = bitmap;
        }
        catch (Exception)
        {
            // Not readable: the initials show instead.
        }
        _cache[agent] = (file.LastWriteTimeUtc, image);
        return image;
    }

    /// <summary>Makes the agent's picture from an image file. Throws InvalidOperationException when it isn't an image.</summary>
    public void Set(string agent, string imageFile)
    {
        var png = SquarePng(imageFile, Size);
        Directory.CreateDirectory(Folder);
        var target = PathOf(agent);
        var fresh = target + ".new";
        File.WriteAllBytes(fresh, png);
        File.Move(fresh, target, overwrite: true);
        _cache.Remove(agent);
        Changed?.Invoke();
    }

    /// <summary>The image file cut to a square in the middle and scaled to <paramref name="size"/>, as PNG. Throws InvalidOperationException when it isn't an image.</summary>
    public static byte[] SquarePng(string imageFile, int size)
    {
        BitmapSource source;
        try
        {
            using var stream = File.OpenRead(imageFile);
            source = BitmapDecoder.Create(stream, BitmapCreateOptions.IgnoreColorProfile, BitmapCacheOption.OnLoad).Frames[0];
        }
        catch (Exception ex) when (ex is not OutOfMemoryException)
        {
            throw new InvalidOperationException($"That isn't an image ProJict can read ({Path.GetFileName(imageFile)}): use a PNG or JPG.");
        }
        var side = Math.Min(source.PixelWidth, source.PixelHeight);
        BitmapSource square = new CroppedBitmap(source, new Int32Rect((source.PixelWidth - side) / 2, (source.PixelHeight - side) / 2, side, side));
        if (side != size) square = new TransformedBitmap(square, new ScaleTransform((double)size / side, (double)size / side));
        var encoder = new PngBitmapEncoder();
        encoder.Frames.Add(BitmapFrame.Create(square));
        using var output = new MemoryStream();
        encoder.Save(output);
        return output.ToArray();
    }

    public void Remove(string agent)
    {
        if (!Has(agent)) return;
        File.Delete(PathOf(agent));
        _cache.Remove(agent);
        Changed?.Invoke();
    }
}
