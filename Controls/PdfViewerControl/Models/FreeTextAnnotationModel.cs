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
    public bool CalloutEnabled { get; set; }
    public bool CalloutIsLeft { get; set; } = true;
    public double CalloutX { get; set; }
    public double CalloutY1 { get; set; }
    public double CalloutY2 { get; set; }
    public double CalloutArmLength { get; set; } = 8;
    public string CalloutShape { get; set; } = string.Empty;
    public double CalloutWidth { get; set; } = 40;
    public string EffectiveCalloutShape => CalloutShape is "LeftBracket" or "RightBracket" or "TopBracket" or "BottomBracket" or "OpenArrow" or "Rectangle"
        ? CalloutShape : CalloutIsLeft ? "LeftBracket" : "RightBracket";

    // All points are in PDF coordinates. The UI and PDF export share this geometry.
    public (Avalonia.Point Start, Avalonia.Point Anchor, Avalonia.Point[] Tail) GetCalloutGeometry()
    {
        var x = CalloutX;
        var top = Math.Max(CalloutY1, CalloutY2);
        var bottom = Math.Min(CalloutY1, CalloutY2);
        var mid = (top + bottom) / 2;
        var arm = double.IsFinite(CalloutArmLength) ? Math.Max(1, CalloutArmLength) : 8;
        var width = double.IsFinite(CalloutWidth) ? Math.Max(1, CalloutWidth) : 40;
        var shape = EffectiveCalloutShape;
        var anchor = new Avalonia.Point(x, mid);
        Avalonia.Point[] tail;
        switch (shape)
        {
            case "RightBracket":
                tail = [new(x - arm, top), new(x, top), new(x, bottom), new(x - arm, bottom)];
                break;
            case "TopBracket":
                anchor = new(x + width / 2, top);
                tail = [new(x, top - arm), new(x, top), new(x + width, top), new(x + width, top - arm)];
                break;
            case "BottomBracket":
                anchor = new(x + width / 2, bottom);
                tail = [new(x, bottom + arm), new(x, bottom), new(x + width, bottom), new(x + width, bottom + arm)];
                break;
            case "Rectangle":
                var cx = PdfX + Width / 2;
                var cy = PdfY + Height / 2;
                var dx = cx - (x + width / 2);
                var dy = cy - mid;
                var ratio = Math.Max(Math.Abs(dx) / (width / 2), Math.Abs(dy) / Math.Max(0.001, (top - bottom) / 2));
                anchor = new(x + width / 2 + dx / Math.Max(1, ratio), mid + dy / Math.Max(1, ratio));
                tail = [new(x, top), new(x + width, top), new(x + width, bottom), new(x, bottom), new(x, top)];
                break;
            case "OpenArrow":
                var direction = new Avalonia.Vector(PdfX + Width / 2 - x, PdfY + Height / 2 - mid);
                direction = direction.Length > 0.001 ? direction / direction.Length : new Avalonia.Vector(1, 0);
                var perpendicular = new Avalonia.Vector(-direction.Y, direction.X);
                tail = [anchor + direction * arm + perpendicular * (arm / 2), anchor,
                    anchor + direction * arm - perpendicular * (arm / 2)];
                break;
            default:
                tail = [new(x + arm, top), new(x, top), new(x, bottom), new(x + arm, bottom)];
                break;
        }
        var center = new Avalonia.Point(PdfX + Width / 2, PdfY + Height / 2);
        var delta = anchor - center;
        var edgeRatio = Math.Max(Math.Abs(delta.X) / Math.Max(0.001, Width / 2), Math.Abs(delta.Y) / Math.Max(0.001, Height / 2));
        return (center + delta / Math.Max(1, edgeRatio), anchor, tail);
    }
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
