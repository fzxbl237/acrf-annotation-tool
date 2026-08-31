using Avalonia.Controls;
using PdfViewerControl.Controls.Annotation;
using PdfViewerControl.Models;
namespace PdfViewerControl.Controls;
internal sealed class PdfPageSlot
{
    public int PageIndex { get; set; }
    public required Border Host { get; init; }
    public Panel? PageRoot { get; set; }
    public Image? PageImage { get; set; }
    public TextBlock? LoadingOverlay { get; set; }
    public Canvas? DomainAnnotationLayer { get; set; }
    public List<FreeTextAnnotationControl> DomainAnnotations { get; } = new();
    public List<FreeTextAnnotationModel> DomainAnnotationModels { get; } = new();
    public PageAnnotationCanvas? AnnotationLayer { get; set; }
    public double DomainAnnotationHorizontalOffset { get; set; } = 8;
    public double DomainAnnotationVerticalOffset { get; set; } = 8;
    public bool IsContentMounted => PageRoot is not null;
    public bool IsBitmapLoaded { get; set; }
    public bool IsRendering { get; set; }
}
