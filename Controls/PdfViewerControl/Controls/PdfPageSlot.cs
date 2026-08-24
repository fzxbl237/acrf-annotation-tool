using Avalonia.Controls;
using PdfViewerControl.Controls.Annotation;
namespace PdfViewerControl.Controls;
internal sealed class PdfPageSlot
{
    public int PageIndex { get; set; }
    public required Border Host { get; init; }
    public required Image PageImage { get; init; }
    public required TextBlock LoadingOverlay { get; init; }
    public required Canvas DomainAnnotationLayer { get; init; }
    public required List<FreeTextAnnotationControl> DomainAnnotations { get; init; }
    public required PageAnnotationCanvas AnnotationLayer { get; init; }
    public double DomainAnnotationHorizontalOffset { get; set; } = 8;
    public double DomainAnnotationVerticalOffset { get; set; } = 8;
    public bool IsBitmapLoaded { get; set; }
    public bool IsRendering { get; set; }
}
