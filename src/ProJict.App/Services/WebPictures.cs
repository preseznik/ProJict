using System.Globalization;
using System.IO;
using System.Windows;
using System.Windows.Media;
using System.Windows.Media.Imaging;

namespace ProJict.App.Services;

/// <summary>
/// Pictures for agents to look at: a screenshot or an image made small enough to send, and a numbered contact sheet of
/// image search results. Made with WPF's imaging on the UI thread; a picture Windows can't read is left out.
/// </summary>
public static class WebPictures
{
    /// <summary>The picture read, or null when Windows can't read it (an SVG, a broken file).</summary>
    public static BitmapSource? Read(byte[] data)
    {
        try
        {
            var image = new BitmapImage();
            image.BeginInit();
            image.CacheOption = BitmapCacheOption.OnLoad;
            image.CreateOptions = BitmapCreateOptions.IgnoreColorProfile;
            image.StreamSource = new MemoryStream(data);
            image.EndInit();
            image.Freeze();
            return image;
        }
        catch (Exception ex) when (ex is NotSupportedException or FileFormatException or ArgumentException or IOException or InvalidOperationException or System.Runtime.InteropServices.COMException)
        {
            return null;
        }
    }

    /// <summary>The picture's size in pixels, or null when it can't be read.</summary>
    public static (int Width, int Height)? Size(byte[] data) => Read(data) is { } image ? (image.PixelWidth, image.PixelHeight) : null;

    /// <summary>The picture as a JPEG no bigger than <paramref name="maxWidth"/>×<paramref name="maxHeight"/> (on white where it's see-through); the bytes as they are when it can't be read.</summary>
    public static byte[] Fit(byte[] data, int maxWidth, int maxHeight)
    {
        if (Read(data) is not { } image) return data;
        var scale = Math.Min(1, Math.Min((double)maxWidth / image.PixelWidth, (double)maxHeight / image.PixelHeight));
        var width = Math.Max(1, (int)Math.Round(image.PixelWidth * scale));
        var height = Math.Max(1, (int)Math.Round(image.PixelHeight * scale));
        var visual = new DrawingVisual();
        using (var dc = visual.RenderOpen())
        {
            dc.DrawRectangle(Brushes.White, null, new Rect(0, 0, width, height));
            dc.DrawImage(image, new Rect(0, 0, width, height));
        }
        return Jpeg(Render(visual, width, height), 85);
    }

    /// <summary>One picture on a contact sheet: its number, the small picture (null when it didn't come), and a line under it.</summary>
    public sealed record Tile(int Number, byte[]? Picture, string Caption);

    /// <summary>
    /// The results side by side, numbered as image_search lists them, each with its size or site under it, so an agent
    /// sees them all in one picture.
    /// </summary>
    public static byte[] ContactSheet(IReadOnlyList<Tile> tiles, int columns = 5)
    {
        const int cellWidth = 220, cellHeight = 200, pictureHeight = 160, gap = 6;
        var rows = Math.Max(1, (tiles.Count + columns - 1) / columns);
        var width = columns * cellWidth;
        var height = rows * cellHeight;
        var typeface = new Typeface(new FontFamily("Segoe UI"), FontStyles.Normal, FontWeights.SemiBold, FontStretches.Normal);
        var plain = new Typeface(new FontFamily("Segoe UI"), FontStyles.Normal, FontWeights.Normal, FontStretches.Normal);
        var visual = new DrawingVisual();
        var muted = new SolidColorBrush(Color.FromRgb(0x55, 0x55, 0x55));
        var badge = new SolidColorBrush(Color.FromRgb(0x1F, 0x5F, 0xD6));
        var empty = new SolidColorBrush(Color.FromRgb(0xEE, 0xEE, 0xEE));
        using (var dc = visual.RenderOpen())
        {
            dc.DrawRectangle(Brushes.White, null, new Rect(0, 0, width, height));
            for (var i = 0; i < tiles.Count; i++)
            {
                var tile = tiles[i];
                var x = i % columns * cellWidth + gap;
                var y = i / columns * cellHeight + gap;
                var box = new Rect(x, y, cellWidth - 2 * gap, pictureHeight);
                if (tile.Picture is { } bytes && Read(bytes) is { } image)
                {
                    var scale = Math.Min(box.Width / image.PixelWidth, box.Height / image.PixelHeight);
                    var w = image.PixelWidth * scale;
                    var h = image.PixelHeight * scale;
                    dc.DrawImage(image, new Rect(box.X + (box.Width - w) / 2, box.Y + (box.Height - h) / 2, w, h));
                }
                else
                {
                    dc.DrawRectangle(empty, null, box);
                    dc.DrawText(Text("no preview", plain, 12, muted), new Point(box.X + 8, box.Y + box.Height / 2 - 8));
                }
                var number = Text(tile.Number.ToString(CultureInfo.InvariantCulture), typeface, 15, Brushes.White);
                dc.DrawRoundedRectangle(badge, null, new Rect(box.X, box.Y, number.Width + 12, number.Height + 4), 4, 4);
                dc.DrawText(number, new Point(box.X + 6, box.Y + 2));
                var caption = Text(tile.Caption, plain, 12, muted);
                caption.MaxTextWidth = box.Width;
                caption.MaxLineCount = 1;
                caption.Trimming = TextTrimming.CharacterEllipsis;
                dc.DrawText(caption, new Point(box.X, box.Y + pictureHeight + 6));
            }
        }
        return Jpeg(Render(visual, width, height), 82);
    }

    private static FormattedText Text(string text, Typeface typeface, double size, Brush brush) =>
        new(text, CultureInfo.CurrentUICulture, FlowDirection.LeftToRight, typeface, size, brush, 1.0);

    private static RenderTargetBitmap Render(Visual visual, int width, int height)
    {
        var bitmap = new RenderTargetBitmap(width, height, 96, 96, PixelFormats.Pbgra32);
        bitmap.Render(visual);
        bitmap.Freeze();
        return bitmap;
    }

    private static byte[] Jpeg(BitmapSource source, int quality)
    {
        var encoder = new JpegBitmapEncoder { QualityLevel = quality };
        encoder.Frames.Add(BitmapFrame.Create(source));
        using var stream = new MemoryStream();
        encoder.Save(stream);
        return stream.ToArray();
    }
}
