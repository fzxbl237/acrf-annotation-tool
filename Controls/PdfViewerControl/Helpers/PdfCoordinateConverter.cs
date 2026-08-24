using Avalonia;
namespace PdfViewerControl.Helpers;
public static class PdfCoordinateConverter
{
    public static Point PdfToAvalonia(double pdfX, double pdfY, double boxHeightPoints, double pageHeightPoints, double scale)
    {
        var pxX = pdfX * scale;
        var pxY = (pageHeightPoints - pdfY - boxHeightPoints) * scale;
        return new Point(pxX, pxY);
    }
    public static Point AvaloniaToPdf(Point avaloniaPoint, double boxHeightPoints, double pageHeightPoints, double scale)
    {
        var pdfX = avaloniaPoint.X / scale;
        var pdfY = pageHeightPoints - avaloniaPoint.Y / scale - boxHeightPoints;
        return new Point(pdfX, pdfY);
    }
    public static double PointsToPixels(double points, double scale) => points * scale;
    public static double PixelsToPoints(double pixels, double scale) => pixels / scale;
    public static void ClampAvaloniaRect(double pageWidthPx, double pageHeightPx, ref double left, ref double top, double width, double height)
    {
        if (pageWidthPx <= 0 || pageHeightPx <= 0) return;
        left = Math.Clamp(left, 0, Math.Max(0, pageWidthPx - Math.Max(0, width)));
        top = Math.Clamp(top, 0, Math.Max(0, pageHeightPx - Math.Max(0, height)));
    }
    public static void ClampPdfRect(double pageWidthPoints, double pageHeightPoints, ref double pdfX, ref double pdfY, double widthPoints, double heightPoints)
    {
        pdfX = Math.Clamp(pdfX, 0, Math.Max(0, pageWidthPoints - Math.Max(0, widthPoints)));
        pdfY = Math.Clamp(pdfY, 0, Math.Max(0, pageHeightPoints - Math.Max(0, heightPoints)));
    }
}
