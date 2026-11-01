using System.Windows;
using System.Windows.Controls;
using System.Windows.Documents;
using ProJict.App.ViewModels;

namespace ProJict.App.Views;

/// <summary>A chat message's text: @mentions stand out, #channels, items and web links can be clicked, and actions are buttons.</summary>
public sealed class ChatTextBlock : TextBlock
{
    public static readonly DependencyProperty SegmentsProperty = DependencyProperty.Register(
        nameof(Segments), typeof(IEnumerable<ChatSegmentViewModel>), typeof(ChatTextBlock), new PropertyMetadata(null, (d, _) => ((ChatTextBlock)d).Build()));

    public IEnumerable<ChatSegmentViewModel>? Segments
    {
        get => (IEnumerable<ChatSegmentViewModel>?)GetValue(SegmentsProperty);
        set => SetValue(SegmentsProperty, value);
    }

    private void Build()
    {
        Inlines.Clear();
        foreach (var segment in Segments ?? [])
        {
            if (segment.IsLink)
            {
                var link = new Hyperlink(new Run(segment.Text)) { Command = segment.OpenCommand, ToolTip = segment.ToolTip };
                Inlines.Add(link);
            }
            else if (segment.IsAction)
            {
                var button = new Button { Content = segment.Text, Command = segment.OpenCommand, ToolTip = segment.ToolTip, Padding = new Thickness(8, 0, 8, 1), Margin = new Thickness(2, 0, 2, 0) };
                if (segment.IsPrimaryAction) button.SetResourceReference(StyleProperty, "AccentButtonStyle");
                Inlines.Add(new InlineUIContainer(button) { BaselineAlignment = BaselineAlignment.Center });
            }
            else if (segment.IsMention)
            {
                var run = new Run(segment.Text) { FontWeight = FontWeights.SemiBold };
                run.SetResourceReference(TextElement.ForegroundProperty, "AccentTextFillColorPrimaryBrush");
                Inlines.Add(run);
            }
            else Inlines.Add(new Run(segment.Text));
        }
    }
}
