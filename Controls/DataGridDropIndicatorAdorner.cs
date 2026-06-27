using System.Windows;
using System.Windows.Documents;
using System.Windows.Media;

namespace SIGFUR.Wpf.Controls;

public sealed class DataGridDropIndicatorAdorner : Adorner
{
    private readonly bool _after;
    private readonly Pen _linePen;
    private readonly Brush _markerBrush;

    public DataGridDropIndicatorAdorner(UIElement adornedElement, bool after)
        : base(adornedElement)
    {
        _after = after;
        IsHitTestVisible = false;
        _markerBrush = new SolidColorBrush(Color.FromRgb(21, 101, 192));
        _linePen = new Pen(_markerBrush, 3.0);
        _linePen.Freeze();
    }

    protected override void OnRender(DrawingContext drawingContext)
    {
        var rect = new Rect(AdornedElement.RenderSize);
        var y = _after ? Math.Max(0, rect.Bottom - 1.5) : 1.5;
        drawingContext.DrawLine(_linePen, new Point(4, y), new Point(Math.Max(4, rect.Width - 4), y));
        drawingContext.DrawGeometry(_markerBrush, null, Triangle(new Point(4, y), left: true));
        drawingContext.DrawGeometry(_markerBrush, null, Triangle(new Point(Math.Max(4, rect.Width - 4), y), left: false));
    }

    private static Geometry Triangle(Point anchor, bool left)
    {
        var geometry = new StreamGeometry();
        using var context = geometry.Open();
        var direction = left ? 1d : -1d;
        context.BeginFigure(anchor, true, true);
        context.LineTo(new Point(anchor.X + 8 * direction, anchor.Y - 5), true, false);
        context.LineTo(new Point(anchor.X + 8 * direction, anchor.Y + 5), true, false);
        geometry.Freeze();
        return geometry;
    }
}
