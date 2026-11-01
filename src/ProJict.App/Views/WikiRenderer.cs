using System.IO;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Documents;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using ProJict.Core.Wiki;

namespace ProJict.App.Views;

/// <summary>What a page's links lead to, and what happens when one is clicked.</summary>
public sealed class WikiRenderContext
{
    /// <summary>The page a [[link]] names, or null when there's none yet (the link then offers to start it).</summary>
    public Func<string, WikiPage?> ResolvePage { get; init; } = _ => null;
    /// <summary>The item a "#4e84570c" names, with its text for the tooltip.</summary>
    public Func<string, (Guid Id, string Text)?> FindItem { get; init; } = _ => null;
    public Func<string, bool> IsChannel { get; init; } = _ => false;
    /// <summary>The local file of a picture or file the page names (an attachment, by file name); null when there's none.</summary>
    public Func<string, string?> FilePath { get; init; } = _ => null;
    public Action<string> OpenPage { get; init; } = _ => { };
    public Action<Guid> OpenItem { get; init; } = _ => { };
    public Action<string> OpenChannel { get; init; } = _ => { };
    /// <summary>A web link, or a file attached to the page.</summary>
    public Action<string> OpenLink { get; init; } = _ => { };
}

/// <summary>
/// Draws a page's Markdown as a FlowDocument: headings, paragraphs, lists and tasks, quotes, code, tables, rules,
/// pictures, and links to pages, items, channels, files and the web. Diagrams go through <see cref="Diagrams"/>
/// (Mermaid, see <see cref="MermaidDiagram"/>); one nothing draws (draw.io, for now) shows as its code.
/// </summary>
public static class WikiRenderer
{
    /// <summary>What draws a fenced diagram ("mermaid", "drawio"): returns the block to show, or null to show the code.</summary>
    public interface IDiagram
    {
        bool Draws(string language);
        Block? Draw(string language, string source, WikiRenderContext context);
        /// <summary>Why it shows as code instead (it doesn't draw); null when it isn't known.</summary>
        string? Problem(string language, string source);
    }

    /// <summary>The diagram renderers (MainViewModel adds Mermaid's).</summary>
    public static List<IDiagram> Diagrams { get; } = [];

    private static readonly FontFamily TextFont = new("Segoe UI Variable Text, Segoe UI");
    private static readonly double[] HeadingSizes = [26, 21, 17.5, 15.5, 14, 13];
    private const double BodySize = 14;

    public static FlowDocument Render(string markdown, WikiRenderContext context)
    {
        var document = new FlowDocument
        {
            FontSize = BodySize,
            PagePadding = new Thickness(4, 0, 12, 24),
            TextAlignment = TextAlignment.Left,
            LineHeight = BodySize * 1.45,
        };
        document.SetResourceReference(TextElement.ForegroundProperty, "TextFillColorPrimaryBrush");
        document.FontFamily = TextFont;
        foreach (var block in Markdown.Parse(markdown)) document.Blocks.Add(Block(block, context, nested: false));
        if (document.Blocks.Count == 0)
        {
            var empty = new Paragraph(new Run("This page is empty."));
            empty.SetResourceReference(TextElement.ForegroundProperty, "TextFillColorSecondaryBrush");
            document.Blocks.Add(empty);
        }
        return document;
    }

    private static Block Block(MdBlock block, WikiRenderContext context, bool nested)
    {
        switch (block)
        {
            case MdHeading h:
            {
                var paragraph = new Paragraph { FontSize = HeadingSizes[h.Level - 1], FontWeight = FontWeights.SemiBold, Margin = new Thickness(0, h.Level <= 2 ? 18 : 12, 0, 6) };
                paragraph.LineHeight = paragraph.FontSize * 1.25;
                AddInlines(paragraph.Inlines, Markdown.Inlines(h.Text), context);
                if (h.Level <= 2)
                {
                    paragraph.BorderThickness = new Thickness(0, 0, 0, 1);
                    paragraph.Padding = new Thickness(0, 0, 0, 4);
                    paragraph.SetResourceReference(System.Windows.Documents.Block.BorderBrushProperty, "ControlStrokeColorDefaultBrush");
                }
                return paragraph;
            }
            case MdParagraph p:
            {
                var inlines = Markdown.Inlines(p.Text);
                // A picture on its own line is shown as a block, as wide as fits.
                if (inlines is [MdImage image] && Picture(image, context, block: true) is { } picture) return new BlockUIContainer(picture) { Margin = new Thickness(0, 4, 0, 10) };
                var paragraph = new Paragraph { Margin = new Thickness(0, 0, 0, nested ? 4 : 10) };
                AddInlines(paragraph.Inlines, inlines, context);
                return paragraph;
            }
            case MdCode code:
            {
                var diagram = code.IsDiagram ? Diagrams.FirstOrDefault(d => d.Draws(code.Language!)) : null;
                if (diagram?.Draw(code.Language!, code.Code, context) is { } drawn) return drawn;
                return CodeBlock(code, diagram?.Problem(code.Language!, code.Code));
            }
            case MdQuote quote:
            {
                var section = new Section { BorderThickness = new Thickness(3, 0, 0, 0), Padding = new Thickness(12, 2, 0, 2), Margin = new Thickness(0, 0, 0, 10) };
                section.SetResourceReference(System.Windows.Documents.Block.BorderBrushProperty, "ControlStrokeColorDefaultBrush");
                section.SetResourceReference(TextElement.ForegroundProperty, "TextFillColorSecondaryBrush");
                foreach (var inner in quote.Blocks) section.Blocks.Add(Block(inner, context, nested: true));
                return section;
            }
            case MdList list:
            {
                var tasks = list.Items.Any(i => i.Checked != null);
                var element = new List
                {
                    MarkerStyle = tasks ? TextMarkerStyle.None : list.Ordered ? TextMarkerStyle.Decimal : nested ? TextMarkerStyle.Circle : TextMarkerStyle.Disc,
                    StartIndex = Math.Max(1, list.Start),
                    Margin = new Thickness(0, 0, 0, nested ? 2 : 10),
                    Padding = new Thickness(tasks ? 6 : 24, 0, 0, 0),
                };
                foreach (var item in list.Items)
                {
                    var listItem = new ListItem();
                    foreach (var inner in item.Blocks) listItem.Blocks.Add(Block(inner, context, nested: true));
                    if (item.Checked is { } done)
                    {
                        if (listItem.Blocks.FirstBlock is not Paragraph first)
                        {
                            first = new Paragraph { Margin = new Thickness(0, 0, 0, 4) };
                            if (listItem.Blocks.FirstBlock is { } top) listItem.Blocks.InsertBefore(top, first);
                            else listItem.Blocks.Add(first);
                        }
                        var box = new Run(done ? "  " : "  ") { FontSize = BodySize - 1 };
                        box.SetResourceReference(TextElement.FontFamilyProperty, "IconFont");
                        if (done) box.SetResourceReference(TextElement.ForegroundProperty, "AccentTextFillColorPrimaryBrush");
                        if (first.Inlines.FirstInline is { } firstInline) first.Inlines.InsertBefore(firstInline, box);
                        else first.Inlines.Add(box);
                        if (done) first.SetResourceReference(TextElement.ForegroundProperty, "TextFillColorSecondaryBrush");
                    }
                    element.ListItems.Add(listItem);
                }
                return element;
            }
            case MdTable table:
                return Table(table, context);
            case MdRule:
            {
                var line = new Border { Height = 1, Margin = new Thickness(0, 6, 0, 6) };
                line.SetResourceReference(Border.BackgroundProperty, "ControlStrokeColorDefaultBrush");
                return new BlockUIContainer(line);
            }
            default:
                return new Paragraph();
        }
    }

    /// <summary>A code block; a diagram's says above it why it isn't drawn (<paramref name="problem"/>, or that its kind comes later).</summary>
    private static Block CodeBlock(MdCode code, string? problem = null)
    {
        var paragraph = new Paragraph { FontSize = 12.5, Padding = new Thickness(10, 8, 10, 8), Margin = new Thickness(0, 0, 0, 10), LineHeight = 12.5 * 1.4 };
        paragraph.SetResourceReference(TextElement.FontFamilyProperty, "MonoFont");
        paragraph.SetResourceReference(TextElement.BackgroundProperty, "SubtleFillColorSecondaryBrush");
        var lines = code.Code.Split('\n');
        for (var i = 0; i < lines.Length; i++)
        {
            if (i > 0) paragraph.Inlines.Add(new LineBreak());
            paragraph.Inlines.Add(new Run(lines[i]));
        }
        if (!code.IsDiagram) return paragraph;
        var section = new Section { Margin = new Thickness(0, 0, 0, 10) };
        var kind = code.Language!.Equals("drawio", StringComparison.OrdinalIgnoreCase) ? "draw.io" : "Mermaid";
        var caption = new Paragraph(new Run(problem != null ? $"This {kind} diagram doesn't draw: {problem}" : $"{kind} diagram (drawn in a later version of ProJict)"))
            { FontSize = 11.5, Margin = new Thickness(0, 0, 0, 2) };
        caption.SetResourceReference(TextElement.ForegroundProperty, problem != null ? "SystemFillColorCriticalBrush" : "TextFillColorSecondaryBrush");
        paragraph.Margin = new Thickness(0);
        section.Blocks.Add(caption);
        section.Blocks.Add(paragraph);
        return section;
    }

    private static Block Table(MdTable table, WikiRenderContext context)
    {
        var element = new Table { CellSpacing = 0, Margin = new Thickness(0, 0, 0, 12), BorderThickness = new Thickness(1, 1, 0, 0) };
        element.SetResourceReference(System.Windows.Documents.Block.BorderBrushProperty, "ControlStrokeColorDefaultBrush");
        foreach (var _ in table.Header) element.Columns.Add(new TableColumn { Width = GridLength.Auto });
        var group = new TableRowGroup();
        element.RowGroups.Add(group);
        TableRow Row(IReadOnlyList<string> cells, bool header)
        {
            var row = new TableRow();
            if (header) row.SetResourceReference(TextElement.BackgroundProperty, "SubtleFillColorSecondaryBrush");
            for (var i = 0; i < cells.Count; i++)
            {
                var paragraph = new Paragraph { Margin = new Thickness(0), TextAlignment = table.Aligns[i] switch
                {
                    MdAlign.Center => TextAlignment.Center,
                    MdAlign.Right => TextAlignment.Right,
                    _ => TextAlignment.Left,
                } };
                if (header) paragraph.FontWeight = FontWeights.SemiBold;
                AddInlines(paragraph.Inlines, Markdown.Inlines(cells[i]), context);
                var cell = new TableCell(paragraph) { Padding = new Thickness(8, 4, 8, 4), BorderThickness = new Thickness(0, 0, 1, 1) };
                cell.SetResourceReference(TableCell.BorderBrushProperty, "ControlStrokeColorDefaultBrush");
                row.Cells.Add(cell);
            }
            return row;
        }
        group.Rows.Add(Row(table.Header, header: true));
        foreach (var cells in table.Rows) group.Rows.Add(Row(cells, header: false));
        return element;
    }

    private static void AddInlines(InlineCollection target, IEnumerable<MdInline> inlines, WikiRenderContext context)
    {
        foreach (var inline in inlines) target.Add(Inline(inline, context));
    }

    private static Inline Inline(MdInline inline, WikiRenderContext context)
    {
        switch (inline)
        {
            case MdText t:
                return new Run(t.Text);
            case MdStrong s:
            {
                var bold = new Bold();
                AddInlines(bold.Inlines, s.Content, context);
                return bold;
            }
            case MdEmphasis e:
            {
                var italic = new Italic();
                AddInlines(italic.Inlines, e.Content, context);
                return italic;
            }
            case MdStrike s:
            {
                var span = new Span { TextDecorations = TextDecorations.Strikethrough };
                AddInlines(span.Inlines, s.Content, context);
                return span;
            }
            case MdCodeSpan code:
            {
                var run = new Run(code.Code) { FontSize = BodySize - 1.5 };
                run.SetResourceReference(TextElement.FontFamilyProperty, "MonoFont");
                run.SetResourceReference(TextElement.BackgroundProperty, "SubtleFillColorSecondaryBrush");
                return run;
            }
            case MdLink link:
            {
                var hyperlink = new Hyperlink { ToolTip = link.Url, Cursor = Cursors.Hand };
                AddInlines(hyperlink.Inlines, link.Content, context);
                hyperlink.Click += (_, _) => context.OpenLink(link.Url);
                return hyperlink;
            }
            case MdImage image:
                return Picture(image, context, block: false) is { } picture
                    ? new InlineUIContainer(picture) { BaselineAlignment = BaselineAlignment.Bottom }
                    : Missing($"[picture: {(image.Alt.Length > 0 ? image.Alt : image.Url)}]", IsWeb(image.Url) ? $"Pictures from the web aren't shown; open it: {image.Url}" : $"No file “{image.Url}” is attached to this page.",
                        IsWeb(image.Url) ? () => context.OpenLink(image.Url) : null);
            case MdWikiLink w:
            {
                var page = context.ResolvePage(w.Target);
                var hyperlink = new Hyperlink(new Run(w.Label)) { Cursor = Cursors.Hand, ToolTip = page != null ? WikiPaths.Display(page.Path) : $"No page “{w.Target}” yet: click to start it" };
                if (page == null)
                {
                    hyperlink.SetResourceReference(TextElement.ForegroundProperty, "SystemFillColorCautionBrush");
                    hyperlink.TextDecorations = null;
                }
                hyperlink.Click += (_, _) => context.OpenPage(w.Target);
                return hyperlink;
            }
            case MdHashRef hash:
            {
                if (hash.Token.Length == 8 && hash.Token.All(char.IsAsciiHexDigit) && context.FindItem(hash.Token) is { } item)
                {
                    var link = new Hyperlink(new Run("#" + hash.Token)) { Cursor = Cursors.Hand, ToolTip = item.Text };
                    link.Click += (_, _) => context.OpenItem(item.Id);
                    return link;
                }
                var name = hash.Token.ToLowerInvariant();
                if (context.IsChannel(name))
                {
                    var link = new Hyperlink(new Run("#" + hash.Token)) { Cursor = Cursors.Hand, ToolTip = "Open the channel" };
                    link.Click += (_, _) => context.OpenChannel(name);
                    return link;
                }
                return new Run("#" + hash.Token);
            }
            case MdLineBreak:
                return new LineBreak();
            default:
                return new Run("");
        }
    }

    private static bool IsWeb(string url) => url.StartsWith("http://", StringComparison.OrdinalIgnoreCase) || url.StartsWith("https://", StringComparison.OrdinalIgnoreCase);

    private static Inline Missing(string text, string toolTip, Action? open)
    {
        if (open != null)
        {
            var link = new Hyperlink(new Run(text)) { ToolTip = toolTip, Cursor = Cursors.Hand };
            link.Click += (_, _) => open();
            return link;
        }
        var run = new Run(text) { ToolTip = toolTip };
        run.SetResourceReference(TextElement.ForegroundProperty, "TextFillColorSecondaryBrush");
        return run;
    }

    /// <summary>An attached picture, at most its own size and as wide as the page; a click opens it. Null when it can't be shown.</summary>
    private static FrameworkElement? Picture(MdImage image, WikiRenderContext context, bool block)
    {
        if (IsWeb(image.Url) || context.FilePath(image.Url) is not { } path || !File.Exists(path)) return null;
        try
        {
            var bitmap = new BitmapImage();
            bitmap.BeginInit();
            bitmap.UriSource = new Uri(path);
            bitmap.CacheOption = BitmapCacheOption.OnLoad;
            bitmap.CreateOptions = BitmapCreateOptions.IgnoreColorProfile;
            bitmap.EndInit();
            bitmap.Freeze();
            var picture = new Image
            {
                Source = bitmap, Stretch = Stretch.Uniform, StretchDirection = StretchDirection.DownOnly,
                MaxWidth = block ? 900 : 360, MaxHeight = block ? 900 : 240, HorizontalAlignment = HorizontalAlignment.Left,
                ToolTip = (image.Alt.Length > 0 ? image.Alt + "\n" : "") + "Click to open", Cursor = Cursors.Hand,
            };
            picture.MouseLeftButtonUp += (_, e) =>
            {
                context.OpenLink(image.Url);
                e.Handled = true;
            };
            return picture;
        }
        catch (Exception)
        {
            // A file Windows can't decode: shown as a missing picture.
            return null;
        }
    }
}
