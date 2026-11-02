using System.Windows;
using System.Windows.Media;

namespace ProJict.App.Views;

/// <summary>A small ring that fills clockwise from the top as an agent's usage is used up (full: all of it used).</summary>
public sealed class UsageRing : FrameworkElement
{
    public static readonly DependencyProperty FractionProperty = DependencyProperty.Register(nameof(Fraction), typeof(double), typeof(UsageRing),
        new FrameworkPropertyMetadata(0.0, FrameworkPropertyMetadataOptions.AffectsRender));
    public static readonly DependencyProperty StrokeProperty = DependencyProperty.Register(nameof(Stroke), typeof(Brush), typeof(UsageRing),
        new FrameworkPropertyMetadata(Brushes.Gray, FrameworkPropertyMetadataOptions.AffectsRender));
    public static readonly DependencyProperty TrackProperty = DependencyProperty.Register(nameof(Track), typeof(Brush), typeof(UsageRing),
        new FrameworkPropertyMetadata(Brushes.LightGray, FrameworkPropertyMetadataOptions.AffectsRender));
    public static readonly DependencyProperty ThicknessProperty = DependencyProperty.Register(nameof(Thickness), typeof(double), typeof(UsageRing),
        new FrameworkPropertyMetadata(2.5, FrameworkPropertyMetadataOptions.AffectsRender));

    /// <summary>How much is used, 0 to 1.</summary>
    public double Fraction { get => (double)GetValue(FractionProperty); set => SetValue(FractionProperty, value); }
    public Brush Stroke { get => (Brush)GetValue(StrokeProperty); set => SetValue(StrokeProperty, value); }
    /// <summary>The ring's empty part.</summary>
    public Brush Track { get => (Brush)GetValue(TrackProperty); set => SetValue(TrackProperty, value); }
    public double Thickness { get => (double)GetValue(ThicknessProperty); set => SetValue(ThicknessProperty, value); }

    protected override void OnRender(DrawingContext dc)
    {
        var size = Math.Min(ActualWidth, ActualHeight);
        if (size <= Thickness) return;
        var radius = (size - Thickness) / 2;
        var center = new Point(ActualWidth / 2, ActualHeight / 2);
        dc.DrawEllipse(null, new Pen(Track, Thickness), center, radius, radius);
        var fraction = Math.Clamp(Fraction, 0, 1);
        if (fraction <= 0) return;
        var pen = new Pen(Stroke, Thickness) { StartLineCap = PenLineCap.Round, EndLineCap = PenLineCap.Round };
        if (fraction >= 0.999)
        {
            dc.DrawEllipse(null, pen, center, radius, radius);
            return;
        }
        Point At(double share) => new(center.X + radius * Math.Sin(share * 2 * Math.PI), center.Y - radius * Math.Cos(share * 2 * Math.PI));
        var arc = new StreamGeometry();
        using (var context = arc.Open())
        {
            context.BeginFigure(At(0), isFilled: false, isClosed: false);
            context.ArcTo(At(fraction), new Size(radius, radius), 0, isLargeArc: fraction > 0.5, SweepDirection.Clockwise, isStroked: true, isSmoothJoin: false);
        }
        arc.Freeze();
        dc.DrawGeometry(null, pen, arc);
    }
}
