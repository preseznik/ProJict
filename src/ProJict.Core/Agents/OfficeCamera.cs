namespace ProJict.Core.Agents;

/// <summary>
/// How the Agents view's office is looked at: how far it's zoomed in and which part of the room shows. At zoom 1 the
/// whole room shows; zoomed in, it pans over the room but never past its walls. Points and offsets are in the room
/// drawing's own units: the room is drawn at room × <see cref="Zoom"/> + offset.
/// </summary>
public sealed class OfficeCamera(double width, double height)
{
    public const double MaxZoom = 4;

    public double Width { get; } = width;
    public double Height { get; } = height;
    public double Zoom { get; private set; } = 1;
    /// <summary>Where the room's top left corner is drawn: 0 or less, as far as the zoom lets it go.</summary>
    public double OffsetX { get; private set; }
    public double OffsetY { get; private set; }
    public bool IsZoomed => Zoom > 1.001;

    /// <summary>Zooms in (factor above 1) or out, keeping the part of the room under the pointer at (x, y) where it is.</summary>
    public void ZoomAt(double x, double y, double factor)
    {
        var zoom = Math.Clamp(Zoom * factor, 1, MaxZoom);
        if (zoom < 1.001) zoom = 1;
        var roomX = (x - OffsetX) / Zoom;
        var roomY = (y - OffsetY) / Zoom;
        Zoom = zoom;
        MoveTo(x - roomX * zoom, y - roomY * zoom);
    }

    /// <summary>Drags the room along by (dx, dy).</summary>
    public void PanBy(double dx, double dy) => MoveTo(OffsetX + dx, OffsetY + dy);

    /// <summary>Back to the whole room.</summary>
    public void Reset()
    {
        Zoom = 1;
        OffsetX = OffsetY = 0;
    }

    private void MoveTo(double x, double y)
    {
        OffsetX = Math.Clamp(x, Width * (1 - Zoom), 0);
        OffsetY = Math.Clamp(y, Height * (1 - Zoom), 0);
    }
}
