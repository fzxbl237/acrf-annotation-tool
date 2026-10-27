namespace PdfViewerControl.Models;
public sealed class FreeTextAnnotationModel
{
    public FreeTextAnnotationModel Clone() => (FreeTextAnnotationModel)MemberwiseClone();

    public string Id { get; init; } = Guid.NewGuid().ToString("N");
    public int PageIndex { get; set; }
    public double PdfX { get; set; }
    public double PdfY { get; set; }
    public double Width { get; set; } = 120;
    public double Height { get; set; } = 24;
    public string Text { get; set; } = "Text";
    public double FontSize { get; set; } = 12;
    public string FontFamily { get; set; } = "Arial";
    public bool IsBold { get; set; }
    public string FontColorHex { get; set; } = "#000000";
    public string BackgroundColorHex { get; set; } = "#FF87CEEB";
    public AnnotationBorderStyle BorderStyle { get; set; } = AnnotationBorderStyle.Solid;
    public AnnotationHorizontalTextAlignment HorizontalTextAlignment { get; set; } = AnnotationHorizontalTextAlignment.Left;
    public AnnotationVerticalTextAlignment VerticalTextAlignment { get; set; } = AnnotationVerticalTextAlignment.Top;
}
