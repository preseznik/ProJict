using System.Windows;
using System.Windows.Controls;
using System.Windows.Data;
using System.Windows.Documents;
using System.Windows.Input;
using System.Windows.Markup;
using System.Windows.Media;
using ProJict.App.ViewModels;

namespace ProJict.App.Views;

/// <summary>
/// A chat message's (or a comment's) text: @mentions stand out, #channels, items and web links can be clicked, actions are
/// buttons, and **bold** is bold.
/// You can select it, to copy part of a message (Ctrl+C, or right-click › Copy): it's plain text until the mouse comes
/// over it, then a read-only text box that looks the same (a channel of plain texts opens much faster).
/// </summary>
public sealed class ChatMessageText : Decorator
{
    // No border, padding or scroll viewer: just the text, and the mouse wheel goes on to the message list.
    private static readonly ControlTemplate Plain = (ControlTemplate)XamlReader.Parse(
        "<ControlTemplate TargetType=\"RichTextBox\" xmlns=\"http://schemas.microsoft.com/winfx/2006/xaml/presentation\" " +
        "xmlns:x=\"http://schemas.microsoft.com/winfx/2006/xaml\"><Decorator x:Name=\"PART_ContentHost\" /></ControlTemplate>");

    public static readonly DependencyProperty SegmentsProperty = DependencyProperty.Register(
        nameof(Segments), typeof(IEnumerable<ChatSegmentViewModel>), typeof(ChatMessageText), new PropertyMetadata(null, (d, _) => ((ChatMessageText)d).Build()));

    public IEnumerable<ChatSegmentViewModel>? Segments
    {
        get => (IEnumerable<ChatSegmentViewModel>?)GetValue(SegmentsProperty);
        set => SetValue(SegmentsProperty, value);
    }

    public ChatMessageText() => Child = new TextBlock { TextWrapping = TextWrapping.Wrap };

    /// <summary>The text as it shows now: in the text block, or in the text box once the mouse came over it.</summary>
    public InlineCollection Inlines => Child is RichTextBox box ? ((Paragraph)box.Document.Blocks.FirstBlock).Inlines : ((TextBlock)Child).Inlines;

    public bool IsSelectable => Child is RichTextBox;

    protected override void OnMouseEnter(MouseEventArgs e)
    {
        base.OnMouseEnter(e);
        MakeSelectable();
    }

    /// <summary>From now on a text box: same look, but you can select the text.</summary>
    public void MakeSelectable()
    {
        if (Child is RichTextBox) return;
        var paragraph = new Paragraph { Margin = new Thickness(0) };
        var document = new FlowDocument(paragraph) { TextAlignment = TextAlignment.Left };
        var box = new RichTextBox
        {
            Template = Plain, Document = document, IsReadOnly = true, IsDocumentEnabled = true, IsTabStop = false, FocusVisualStyle = null,
            Background = Brushes.Transparent, BorderThickness = new Thickness(0), Padding = new Thickness(0), MinHeight = 0, MinWidth = 0,
        };
        // The message's font and colour (a deleted one is grey and slanted), in the box and its document (else Georgia).
        foreach (var property in new[] { TextElement.FontFamilyProperty, TextElement.FontSizeProperty, TextElement.FontStyleProperty, TextElement.FontWeightProperty, TextElement.ForegroundProperty })
        {
            box.SetBinding(property, new Binding { Source = this, Path = new PropertyPath(property) });
            document.SetBinding(property, new Binding { Source = box, Path = new PropertyPath(property) });
        }
        Child = box;
        // The text box gives its document 5 px at the sides as it takes it in: none, so the text stays where it was.
        box.ApplyTemplate();
        document.PagePadding = new Thickness(0);
        box.Loaded += (_, _) => document.PagePadding = new Thickness(0);
        Build();
    }

    private void Build()
    {
        var inlines = Inlines;
        inlines.Clear();
        foreach (var segment in Segments ?? [])
        {
            if (segment.IsLink)
            {
                var link = new Hyperlink(new Run(segment.Text)) { Command = segment.OpenCommand, ToolTip = segment.ToolTip };
                if (segment.IsBold) link.FontWeight = FontWeights.SemiBold;
                inlines.Add(link);
            }
            else if (segment.IsAction)
            {
                var button = new Button { Content = segment.Text, Command = segment.OpenCommand, ToolTip = segment.ToolTip, Padding = new Thickness(8, 0, 8, 1), Margin = new Thickness(2, 0, 2, 0) };
                if (segment.IsPrimaryAction) button.SetResourceReference(StyleProperty, "AccentButtonStyle");
                inlines.Add(new InlineUIContainer(button) { BaselineAlignment = BaselineAlignment.Center });
            }
            else if (segment.IsPersonMention)
            {
                // @someone: their profile card with a click (#490e5810); looks as before, underlined under the mouse.
                var link = new Hyperlink(new Run(segment.Text)) { Command = segment.OpenCommand, ToolTip = segment.ToolTip, FontWeight = FontWeights.SemiBold, TextDecorations = null };
                link.SetResourceReference(TextElement.ForegroundProperty, "AccentTextFillColorPrimaryBrush");
                link.MouseEnter += (_, _) => link.TextDecorations = TextDecorations.Underline;
                link.MouseLeave += (_, _) => link.TextDecorations = null;
                inlines.Add(link);
            }
            else if (segment.IsMention)
            {
                var run = new Run(segment.Text) { FontWeight = FontWeights.SemiBold };
                run.SetResourceReference(TextElement.ForegroundProperty, "AccentTextFillColorPrimaryBrush");
                inlines.Add(run);
            }
            else inlines.Add(segment.IsBold ? new Run(segment.Text) { FontWeight = FontWeights.SemiBold } : new Run(segment.Text));
        }
    }
}
