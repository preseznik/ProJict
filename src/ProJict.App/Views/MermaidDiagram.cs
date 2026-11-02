using System.IO;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Documents;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using ProJict.App.Services;

namespace ProJict.App.Views;

/// <summary>
/// A ```mermaid block in a wiki page, drawn: its picture once <see cref="DiagramService"/> has made it (in the light or
/// dark theme ProJict shows), until then a line saying it's being drawn, and if it doesn't draw, why, above its code.
/// </summary>
public sealed class MermaidDiagram(DiagramService diagrams) : WikiRenderer.IDiagram
{
    public bool Draws(string language) => language.Equals("mermaid", StringComparison.OrdinalIgnoreCase);

    /// <summary>Whether ProJict shows dark now: its text is light.</summary>
    public static bool IsDark() =>
        Application.Current?.TryFindResource("TextFillColorPrimaryBrush") is SolidColorBrush { Color: var c } && (c.R * 0.299 + c.G * 0.587 + c.B * 0.114) > 140;

    public Block? Draw(string language, string source, WikiRenderContext context)
    {
        var dark = IsDark();
        var key = DiagramService.Key(language, source, dark);
        if (diagrams.PictureFor(key) is { } path && Picture(path) is { } picture)
            return new BlockUIContainer(picture) { Margin = new Thickness(0, 4, 0, 12) };
        if (diagrams.ErrorFor(key) is { } error) return null;
        diagrams.Request(language, source, dark);
        var waiting = new Paragraph(new Run("Drawing the diagram…")) { FontSize = 12, Margin = new Thickness(0, 4, 0, 12) };
        waiting.SetResourceReference(TextElement.ForegroundProperty, "TextFillColorSecondaryBrush");
        return waiting;
    }

    /// <summary>Why a diagram shows as code: it doesn't draw, or diagrams can't be drawn here.</summary>
    public string? Problem(string language, string source) => diagrams.ErrorFor(DiagramService.Key(language, source, IsDark()));

    private static Image? Picture(string path)
    {
        try
        {
            var bitmap = new BitmapImage();
            bitmap.BeginInit();
            bitmap.UriSource = new Uri(path);
            bitmap.CacheOption = BitmapCacheOption.OnLoad;
            bitmap.EndInit();
            bitmap.Freeze();
            // Drawn at twice the size, for sharpness: shown at its own, or smaller where the page is narrower (the preview).
            var image = new Image
            {
                Source = bitmap, MaxWidth = Math.Min(bitmap.PixelWidth / 2.0, 1100), Stretch = Stretch.Uniform, StretchDirection = StretchDirection.DownOnly,
                HorizontalAlignment = HorizontalAlignment.Left, Cursor = Cursors.Hand, ToolTip = "A diagram · click to open the picture",
            };
            image.MouseLeftButtonUp += (_, e) =>
            {
                e.Handled = true;
                try { ShellLauncher.OpenFile(path); }
                catch (Exception ex) when (ex is System.ComponentModel.Win32Exception or InvalidOperationException) { }
            };
            return image;
        }
        catch (Exception ex) when (ex is IOException or NotSupportedException or UriFormatException)
        {
            return null;
        }
    }
}
