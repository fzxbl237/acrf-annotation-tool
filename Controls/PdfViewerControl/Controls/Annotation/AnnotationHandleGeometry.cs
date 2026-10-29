using Avalonia;
namespace PdfViewerControl.Controls.Annotation;
internal static class AnnotationHandleGeometry
{
    internal const double Pad = 18;
    private const double HitRadius = 16;
    public static bool TryHit(double left, double top, double width, double height, Point canvasPoint, out AnnotationResizeHandleKind kind)
    {
        kind = default;
        if (width < 1 || height < 1) return false;
        var pts = new (AnnotationResizeHandleKind K, double X, double Y)[] {
            (AnnotationResizeHandleKind.NorthWest, left - Pad, top - Pad),
            (AnnotationResizeHandleKind.North, left + width / 2, top - Pad),
            (AnnotationResizeHandleKind.NorthEast, left + width + Pad, top - Pad),
            (AnnotationResizeHandleKind.East, left + width + Pad, top + height / 2),
            (AnnotationResizeHandleKind.SouthEast, left + width + Pad, top + height + Pad),
            (AnnotationResizeHandleKind.South, left + width / 2, top + height + Pad),
            (AnnotationResizeHandleKind.SouthWest, left - Pad, top + height + Pad),
            (AnnotationResizeHandleKind.West, left - Pad, top + height / 2)
        };
        foreach (var (k, x, y) in pts) {
            var dx = canvasPoint.X - x; var dy = canvasPoint.Y - y;
            if (dx * dx + dy * dy <= HitRadius * HitRadius) { kind = k; return true; }
        }
        return false;
    }
    public static bool ContainsPointerRegion(double left, double top, double width, double height, Point canvasPoint)
    {
        if (width < 1 || height < 1) return false;
        return new Rect(left - Pad, top - Pad, width + Pad * 2, height + Pad * 2).Contains(canvasPoint) || TryHit(left, top, width, height, canvasPoint, out _);
    }
}