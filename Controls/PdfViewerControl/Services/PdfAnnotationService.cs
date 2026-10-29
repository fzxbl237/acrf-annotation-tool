using System.Globalization;
using System.Runtime.CompilerServices;
using PdfSharp.Pdf.Advanced;
using System.Security;
using System.Text.RegularExpressions;
using Avalonia.Media;
using PdfSharp.Drawing;
using PdfSharp.Fonts;
using PdfSharp.Pdf;
using PdfSharp.Pdf.Annotations;
using PdfSharp.Pdf.IO;
using PdfViewerControl.Helpers;
using PdfViewerControl.Models;

namespace PdfViewerControl.Services;

public static partial class PdfAnnotationService
{
    static PdfAnnotationService()
    {
        GlobalFontSettings.FontResolver = new ProjectFontResolver();
    }

    [GeneratedRegex(@"/(?<font>[\w+-]+)\s+(?<size>[\d.]+)\s+Tf\s+(?<r>[\d.]+)\s+(?<g>[\d.]+)\s+(?<b>[\d.]+)\s+rg")]
    private static partial Regex DefaultAppearanceRegex();

    // PDFsharp 6.2.4 exposes the appearance form only through this internal getter.
    [UnsafeAccessor(UnsafeAccessorKind.Method, Name = "get_PdfForm")]
    private static extern PdfFormXObject GetPdfForm(XForm form);

    public static List<FreeTextAnnotationModel> LoadFreeTextAnnotations(string pdfPath)
    {
        var list = new List<FreeTextAnnotationModel>();
        using var document = PdfReader.Open(pdfPath, PdfDocumentOpenMode.Import);
        for (int pi = 0; pi < document.PageCount; pi++)
        {
            foreach (var ann in document.Pages[pi].Annotations)
            {
                if (ann is not PdfAnnotation annotation) continue;
                if (!IsFreeTextAnnotation(annotation)) continue;
                if (annotation.Elements.GetBoolean("/PdfViewerDomainAnnotation")) continue;
                var r = annotation.Rectangle;
                var m = new FreeTextAnnotationModel
                {
                    Id = annotation.Elements.GetString("/PdfViewerAnnotationId") ?? Guid.NewGuid().ToString("N"),
                    PageIndex = pi,
                    PdfX = r.X1,
                    PdfY = r.Y1,
                    Width = Math.Max(40, r.Width),
                    Height = Math.Max(20, r.Height),
                    Text = annotation.Contents ?? "",
                    CalloutEnabled = annotation.Elements.GetBoolean("/PdfViewerCalloutEnabled"),
                    CalloutIsLeft = ReadOptionalBoolean(annotation, "/PdfViewerCalloutIsLeft", true),
                    CalloutX = ReadOptionalReal(annotation, "/PdfViewerCalloutX"),
                    CalloutY1 = ReadOptionalReal(annotation, "/PdfViewerCalloutY1"),
                    CalloutY2 = ReadOptionalReal(annotation, "/PdfViewerCalloutY2"),
                    CalloutArmLength = ReadOptionalReal(annotation, "/PdfViewerCalloutArmLength", 8),
                    CalloutShape = annotation.Elements.GetString("/PdfViewerCalloutShape") ?? string.Empty,
                    CalloutWidth = ReadOptionalReal(annotation, "/PdfViewerCalloutWidth", 40)
                };
                var da = annotation.Elements.GetString("/DA");
                if (!string.IsNullOrWhiteSpace(da))
                {
                    var match = DefaultAppearanceRegex().Match(da);
                    if (match.Success)
                    {
                        if (double.TryParse(match.Groups["size"].Value, NumberStyles.Float, CultureInfo.InvariantCulture, out var sz))
                            m.FontSize = sz;
                        var fontResourceName = match.Groups["font"].Value;
                        m.FontFamily = ResolveLoadedFontFamily(fontResourceName);
                        m.IsBold = m.FontFamily.Equals("Arial Unicode MS", StringComparison.OrdinalIgnoreCase)
                            ? false
                            : IsBoldFontResource(annotation, fontResourceName);
                        if (double.TryParse(match.Groups["r"].Value, NumberStyles.Float, CultureInfo.InvariantCulture, out var cr) &&
                            double.TryParse(match.Groups["g"].Value, NumberStyles.Float, CultureInfo.InvariantCulture, out var cg) &&
                            double.TryParse(match.Groups["b"].Value, NumberStyles.Float, CultureInfo.InvariantCulture, out var cb))
                            m.FontColorHex = $"#{(byte)(cr * 255):X2}{(byte)(cg * 255):X2}{(byte)(cb * 255):X2}";
                    }
                }
                if (TryReadPdfColor(annotation, "/C", out var backgroundColorHex) ||
                    TryReadPdfColor(annotation, "/IC", out backgroundColorHex))
                    m.BackgroundColorHex = backgroundColorHex;
                m.BorderStyle = ReadBorderStyle(annotation);
                AnnotationTextAlignmentHelper.TryParse(annotation.Elements.GetString("/PdfViewerTextAlignment"), m);
                list.Add(m);
            }
        }
        return list;
    }

    public static void SaveFreeTextAnnotations(string src, string dst, IReadOnlyList<FreeTextAnnotationModel> annotations)
    {
        using var doc = PdfReader.Open(src, PdfDocumentOpenMode.Modify);
        var fontResources = CreateAnnotationFontResources(doc, annotations);
        foreach (var page in doc.Pages)
        {
            foreach (var a in page.Annotations.OfType<PdfAnnotation>()
                .Where(IsFreeTextAnnotation).ToList())
                page.Annotations.Remove(a);
            RemoveCalloutCompanions(page);
        }
        foreach (var g in annotations.GroupBy(a => a.PageIndex))
        {
            if (g.Key < 0 || g.Key >= doc.PageCount) continue;
            foreach (var m in g)
            {
                var a = new PdfTextAnnotation { Contents = m.Text ?? "" };
                a.Elements.SetName("/Subtype", "/FreeText");
                a.Elements.SetString("/PdfViewerAnnotationId", m.Id);
                a.Elements.SetBoolean("/PdfViewerCalloutEnabled", m.CalloutEnabled);
                a.Elements.SetBoolean("/PdfViewerCalloutIsLeft", m.CalloutIsLeft);
                a.Elements.SetReal("/PdfViewerCalloutX", m.CalloutX);
                a.Elements.SetReal("/PdfViewerCalloutY1", m.CalloutY1);
                a.Elements.SetReal("/PdfViewerCalloutY2", m.CalloutY2);
                a.Elements.SetReal("/PdfViewerCalloutArmLength", m.CalloutArmLength);
                a.Elements.SetString("/PdfViewerCalloutShape", m.CalloutShape);
                a.Elements.SetReal("/PdfViewerCalloutWidth", m.CalloutWidth);
                if (m.Id.StartsWith("__domain_", StringComparison.Ordinal))
                    a.Elements.SetBoolean("/PdfViewerDomainAnnotation", true);
                a.Rectangle = new PdfRectangle(
                    new XPoint(m.PdfX, m.PdfY),
                    new XPoint(m.PdfX + m.Width, m.PdfY + m.Height));
                var c = Color.Parse(m.FontColorHex);
                var fontResourceName = GetFontResourceName(m);
                a.Elements.SetString("/DA",
                    $"{fontResourceName} {m.FontSize.ToString(CultureInfo.InvariantCulture)} Tf " +
                    $"{(c.R / 255.0).ToString(CultureInfo.InvariantCulture)} " +
                    $"{(c.G / 255.0).ToString(CultureInfo.InvariantCulture)} " +
                    $"{(c.B / 255.0).ToString(CultureInfo.InvariantCulture)} rg");
                a.Elements.SetObject("/DR", fontResources);
                a.Elements.SetString("/DS", CreateDefaultStyle(m));
                a.Elements.SetString("/RC", CreateRichTextValue(m));
                var background = Color.Parse(m.BackgroundColorHex);
                a.Elements.SetObject("/AP", CreateAnnotationAppearance(doc, m));
                a.Elements.SetObject("/C", new PdfArray(doc,
                    new PdfReal(background.R / 255.0),
                    new PdfReal(background.G / 255.0),
                    new PdfReal(background.B / 255.0)));
                WriteBorderStyle(a, doc, m.BorderStyle);
                a.Elements.SetString("/PdfViewerTextAlignment",
                    AnnotationTextAlignmentHelper.Serialize(m.HorizontalTextAlignment, m.VerticalTextAlignment));
                doc.Pages[g.Key].Annotations.Add(a);
                if (m.CalloutEnabled)
                    doc.Pages[g.Key].Annotations.Add(CreateCalloutCompanion(doc, m));
            }
        }
        doc.Save(dst);
    }

    public static void SaveFreeTextAnnotationsFlattened(
        string src,
        string dst,
        IReadOnlyList<FreeTextAnnotationModel> annotations)
    {
        GlobalFontSettings.UseWindowsFontsUnderWindows = true;
        using var doc = PdfReader.Open(src, PdfDocumentOpenMode.Modify);

        foreach (var group in annotations.GroupBy(a => a.PageIndex))
        {
            if (group.Key < 0 || group.Key >= doc.PageCount)
                continue;

            var page = doc.Pages[group.Key];
            var pageHeight = page.Height.Point;
            using var graphics = XGraphics.FromPdfPage(page, XGraphicsPdfPageOptions.Append);
            foreach (var model in group)
            {
                var x = model.PdfX;
                var y = pageHeight - model.PdfY - model.Height;
                DrawAnnotationAppearanceContent(graphics, model, x, y);
                DrawCalloutLines(graphics, model, GetCalloutGeometry(model), 0, pageHeight);
            }
        }

        foreach (var page in doc.Pages)
        {
            foreach (var annotation in page.Annotations.OfType<PdfAnnotation>()
                         .Where(IsFreeTextAnnotation)
                         .ToList())
                page.Annotations.Remove(annotation);
            RemoveCalloutCompanions(page);
        }

        doc.Save(dst);
    }

    private static bool ReadOptionalBoolean(PdfAnnotation annotation, string key, bool defaultValue) =>
        annotation.Elements.ContainsKey(key) ? annotation.Elements.GetBoolean(key) : defaultValue;

    private static double ReadOptionalReal(PdfAnnotation annotation, string key, double defaultValue = 0) =>
        annotation.Elements.ContainsKey(key) ? annotation.Elements.GetReal(key) : defaultValue;

    private static void RemoveCalloutCompanions(PdfPage page)
    {
        foreach (var annotation in page.Annotations.OfType<PdfAnnotation>()
                     .Where(IsCalloutCompanion)
                     .ToList())
            page.Annotations.Remove(annotation);
    }

    private static bool IsCalloutCompanion(PdfAnnotation annotation) =>
        (string.Equals(annotation.Elements.GetString("/Subtype"), "/PolyLine", StringComparison.OrdinalIgnoreCase) ||
         string.Equals(annotation.Elements.GetString("/Subtype"), "PolyLine", StringComparison.OrdinalIgnoreCase)) &&
        annotation.Elements.ContainsKey("/PdfViewerCalloutOwner");

    private static PdfTextAnnotation CreateCalloutCompanion(
        PdfDocument document,
        FreeTextAnnotationModel model)
    {
        var geometry = GetCalloutGeometry(model);
        var annotation = new PdfTextAnnotation();
        annotation.Elements.SetName("/Subtype", "/PolyLine");
        annotation.Elements.SetString("/PdfViewerCalloutOwner", model.Id);
        annotation.Elements.SetInteger("/F", 4);
        annotation.Rectangle = geometry.Rectangle;
        annotation.Elements.SetObject("/C", new PdfArray(document,
            new PdfReal(1),
            new PdfReal(0),
            new PdfReal(0)));
        var borderStyle = new PdfDictionary(document);
        borderStyle.Elements.SetReal("/W", 1);
        borderStyle.Elements.SetName("/S", "/S");
        annotation.Elements.SetObject("/BS", borderStyle);

        var vertices = new PdfArray(document);
        foreach (var point in geometry.Tail)
        {
            vertices.Elements.Add(new PdfReal(point.X));
            vertices.Elements.Add(new PdfReal(point.Y));
        }
        annotation.Elements.SetObject("/Vertices", vertices);
        annotation.Elements.SetObject("/AP", CreateCalloutAppearance(document, model, geometry));
        return annotation;
    }

    private static PdfDictionary CreateCalloutAppearance(
        PdfDocument document,
        FreeTextAnnotationModel model,
        (XPoint ConnectorStart, XPoint Anchor, IReadOnlyList<XPoint> Tail, PdfRectangle Rectangle) geometry)
    {
        var rectangle = geometry.Rectangle;
        var form = new XForm(document, new XRect(0, 0, rectangle.Width, rectangle.Height));
        using (var graphics = XGraphics.FromForm(form))
            DrawCalloutLines(graphics, model, geometry, rectangle.X1, rectangle.Y2);

        var pdfForm = GetPdfForm(form)
            ?? throw new InvalidOperationException("PDFsharp did not create the callout appearance form.");
        var appearance = new PdfDictionary(document);
        appearance.Elements.SetReference("/N", pdfForm);
        return appearance;
    }

    private static (XPoint ConnectorStart, XPoint Anchor, IReadOnlyList<XPoint> Tail, PdfRectangle Rectangle)
        GetCalloutGeometry(FreeTextAnnotationModel model)
    {
        var geometry = model.GetCalloutGeometry();
        var start = new XPoint(geometry.Start.X, geometry.Start.Y);
        var anchor = new XPoint(geometry.Anchor.X, geometry.Anchor.Y);
        var tail = geometry.Tail.Select(static p => new XPoint(p.X, p.Y)).ToArray();
        var points = tail.Concat([start, anchor]).ToArray();
        return (start, anchor, tail, new PdfRectangle(
            new XPoint(points.Min(p => p.X) - 1, points.Min(p => p.Y) - 1),
            new XPoint(points.Max(p => p.X) + 1, points.Max(p => p.Y) + 1)));
    }

    private static void DrawCalloutLines(
        XGraphics graphics,
        FreeTextAnnotationModel model,
        (XPoint ConnectorStart, XPoint Anchor, IReadOnlyList<XPoint> Tail, PdfRectangle Rectangle) geometry,
        double originX,
        double originY)
    {
        if (!model.CalloutEnabled)
            return;

        static XPoint ToDrawingPoint(XPoint point, double xOrigin, double yOrigin) =>
            new(point.X - xOrigin, yOrigin - point.Y);

        var pen = new XPen(XColors.Red, 1);
        graphics.DrawLine(pen, ToDrawingPoint(geometry.ConnectorStart, originX, originY),
            ToDrawingPoint(geometry.Anchor, originX, originY));
        for (var index = 1; index < geometry.Tail.Count; index++)
            graphics.DrawLine(pen, ToDrawingPoint(geometry.Tail[index - 1], originX, originY),
                ToDrawingPoint(geometry.Tail[index], originX, originY));
    }

    private static string CreateDefaultStyle(FreeTextAnnotationModel model)
    {
        var color = Color.Parse(model.FontColorHex);
        var weight = GetEffectiveIsBold(model) ? "bold" : "normal";
        var alignment = model.HorizontalTextAlignment switch
        {
            AnnotationHorizontalTextAlignment.Center => "center",
            AnnotationHorizontalTextAlignment.Right => "right",
            _ => "left"
        };
        var fontFamily = ResolvePdfFontFamily(model);
        return $"font: {weight} {fontFamily} {model.FontSize.ToString(CultureInfo.InvariantCulture)}pt; " +
               $"text-align:{alignment}; color:#{color.R:X2}{color.G:X2}{color.B:X2}";
    }

    private static string CreateRichTextValue(FreeTextAnnotationModel model)
    {
        var color = Color.Parse(model.FontColorHex);
        var weight = GetEffectiveIsBold(model) ? "bold" : "normal";
        var alignment = model.HorizontalTextAlignment switch
        {
            AnnotationHorizontalTextAlignment.Center => "center",
            AnnotationHorizontalTextAlignment.Right => "right",
            _ => "left"
        };
        var escapedText = SecurityElement.Escape(model.Text ?? string.Empty) ?? string.Empty;
        escapedText = escapedText.Replace("\r\n", "<br/>").Replace("\r", "<br/>").Replace("\n", "<br/>");
        var fontFamily = ResolvePdfFontFamily(model);
        var style = $"font-family:{fontFamily},sans-serif;font-size:{model.FontSize.ToString(CultureInfo.InvariantCulture)}pt;" +
                    $"font-weight:{weight};font-style:normal;text-align:{alignment};" +
                    $"color:#{color.R:X2}{color.G:X2}{color.B:X2}";
        return $"<?xml version=\"1.0\"?><body xmlns=\"http://www.w3.org/1999/xhtml\" " +
               "xmlns:xfa=\"http://www.xfa.org/schema/xfa-data/1.0/\" " +
               "xfa:contentType=\"text/html\" xfa:APIVersion=\"Acrobat:9.3.3\" xfa:spec=\"2.0.2\" " +
               $"style=\"{style}\"><p dir=\"ltr\">{escapedText}</p></body>";
    }

    private static PdfDictionary CreateAnnotationAppearance(
        PdfDocument document,
        FreeTextAnnotationModel model)
    {
        var width = Math.Max(1, model.Width);
        var height = Math.Max(1, model.Height);
        var form = new XForm(document, new XRect(0, 0, width, height));
        using (var graphics = XGraphics.FromForm(form))
            DrawAnnotationAppearanceContent(graphics, model, 0, 0);

        var pdfForm = GetPdfForm(form)
            ?? throw new InvalidOperationException("PDFsharp did not create the FreeText appearance form.");
        var appearance = new PdfDictionary(document);
        appearance.Elements.SetReference("/N", pdfForm);
        return appearance;
    }

    private static void DrawAnnotationAppearanceContent(
        XGraphics graphics,
        FreeTextAnnotationModel model,
        double x,
        double y)
    {
        var width = Math.Max(1, model.Width);
        var height = Math.Max(1, model.Height);
        var background = Color.Parse(model.BackgroundColorHex);
        var foreground = Color.Parse(model.FontColorHex);
        var backgroundBrush = new XSolidBrush(XColor.FromArgb(background.R, background.G, background.B));
        var textBrush = new XSolidBrush(XColor.FromArgb(foreground.R, foreground.G, foreground.B));
        graphics.DrawRectangle(backgroundBrush, x, y, width, height);

        var borderPen = new XPen(XColors.Black, 1);
        if (model.BorderStyle == AnnotationBorderStyle.Dashed)
        {
            borderPen.DashStyle = XDashStyle.Custom;
            borderPen.DashPattern = new[] { 3d, 3d };
        }
        graphics.DrawRectangle(borderPen, x + 0.5, y + 0.5, Math.Max(0, width - 1), Math.Max(0, height - 1));

        var font = new XFont(
            ResolvePdfFontFamily(model),
            Math.Max(1, model.FontSize),
            GetEffectiveIsBold(model) ? XFontStyleEx.Bold : XFontStyleEx.Regular);
        const double padding = 2;
        var textRectangle = new XRect(
            x + padding,
            y + padding,
            Math.Max(0, width - padding * 2),
            Math.Max(0, height - padding * 2));
        var text = model.Text ?? string.Empty;
        var lines = WrapAnnotationText(graphics, text, font, textRectangle.Width);
        if (lines.Count == 0)
            return;

        const double lineSpacingMultiplier = 1.2;
        var lineHeight = Math.Max(1, model.FontSize) * lineSpacingMultiplier;
        var contentHeight = lineHeight * lines.Count;
        var firstLineY = model.VerticalTextAlignment switch
        {
            AnnotationVerticalTextAlignment.Center => textRectangle.Y + (textRectangle.Height - contentHeight) / 2,
            AnnotationVerticalTextAlignment.Bottom => textRectangle.Bottom - contentHeight,
            _ => textRectangle.Y
        };
        var format = new XStringFormat
        {
            Alignment = model.HorizontalTextAlignment switch
            {
                AnnotationHorizontalTextAlignment.Center => XStringAlignment.Center,
                AnnotationHorizontalTextAlignment.Right => XStringAlignment.Far,
                _ => XStringAlignment.Near
            },
            LineAlignment = XLineAlignment.Near
        };

        var graphicsState = graphics.Save();
        try
        {
            graphics.IntersectClip(textRectangle);
            for (var index = 0; index < lines.Count; index++)
                graphics.DrawString(lines[index], font, textBrush, new XRect(
                    textRectangle.X,
                    firstLineY + index * lineHeight,
                    textRectangle.Width,
                    lineHeight), format);
        }
        finally
        {
            graphics.Restore(graphicsState);
        }
    }

    private static List<string> WrapAnnotationText(
        XGraphics graphics,
        string text,
        XFont font,
        double maxWidth)
    {
        var lines = new List<string>();
        if (string.IsNullOrEmpty(text))
            return lines;

        var normalizedText = text.Replace("\r\n", "\n").Replace('\r', '\n');
        foreach (var paragraph in normalizedText.Split('\n'))
        {
            if (paragraph.Length == 0)
            {
                lines.Add(string.Empty);
                continue;
            }

            var currentLine = string.Empty;
            foreach (var word in paragraph.Split(' ', StringSplitOptions.RemoveEmptyEntries))
            {
                var candidate = currentLine.Length == 0 ? word : $"{currentLine} {word}";
                if (graphics.MeasureString(candidate, font).Width <= maxWidth)
                {
                    currentLine = candidate;
                    continue;
                }

                if (currentLine.Length > 0)
                {
                    lines.Add(currentLine);
                    currentLine = string.Empty;
                }

                foreach (var character in word)
                {
                    candidate = $"{currentLine}{character}";
                    if (currentLine.Length == 0 || graphics.MeasureString(candidate, font).Width <= maxWidth)
                    {
                        currentLine = candidate;
                    }
                    else
                    {
                        lines.Add(currentLine);
                        currentLine = character.ToString();
                    }
                }
            }

            lines.Add(currentLine);
        }

        return lines;
    }

    private static string ResolveLoadedFontFamily(string fontResourceName)
    {
        var normalizedResourceName = fontResourceName.TrimStart('/');
        return normalizedResourceName.Equals("ArialUnicode", StringComparison.OrdinalIgnoreCase) ||
               normalizedResourceName.Equals("ArialUnicodeBold", StringComparison.OrdinalIgnoreCase)
            ? "Arial Unicode MS"
            : "Arial";
    }

    private static bool NeedsUnicodeFont(string? text) =>
        !string.IsNullOrEmpty(text) && text.Any(static c => c > 127);

    private static string ResolvePdfFontFamily(FreeTextAnnotationModel model) =>
        NeedsUnicodeFont(model.Text) ? "Arial Unicode MS" : "Arial";

    private static bool GetEffectiveIsBold(FreeTextAnnotationModel model) =>
        !NeedsUnicodeFont(model.Text) && model.IsBold;

    private static string GetFontResourceName(FreeTextAnnotationModel model) =>
        NeedsUnicodeFont(model.Text)
            ? "/ArialUnicode"
            : GetEffectiveIsBold(model) ? "/ArialBold" : "/Arial";

    private static PdfDictionary CreateAnnotationFontResources(
        PdfDocument document,
        IReadOnlyList<FreeTextAnnotationModel> annotations)
    {
        GlobalFontSettings.UseWindowsFontsUnderWindows = true;

        var result = new PdfDictionary(document);
        AddAnnotationFontResource(document, result, "/Arial", "Arial", XFontStyleEx.Regular, "A");
        AddAnnotationFontResource(document, result, "/ArialBold", "Arial", XFontStyleEx.Bold, "A");
        AddAnnotationFontResource(document, result, "/ArialUnicode", "Arial Unicode MS", XFontStyleEx.Regular, "中");

        var resourceDictionary = new PdfDictionary(document);
        resourceDictionary.Elements.SetObject("/Font", result);
        return resourceDictionary;
    }

    private static void AddAnnotationFontResource(
        PdfDocument document,
        PdfDictionary result,
        string resourceName,
        string familyName,
        XFontStyleEx style,
        string sample)
    {
        var resourcePage = document.AddPage();
        try
        {
            using (var graphics = XGraphics.FromPdfPage(resourcePage))
            {
                var font = new XFont(familyName, 12, style);
                graphics.DrawString(sample, font, XBrushes.Black, new XPoint(-1000, -1000));
            }

            var generatedFonts = resourcePage.Elements.GetDictionary("/Resources")?.Elements.GetDictionary("/Font");
            var generatedFontKey = generatedFonts?.Elements.Keys.FirstOrDefault();
            if (generatedFonts is null || generatedFontKey is null)
                throw new InvalidOperationException($"PDFsharp did not generate the {resourceName} annotation font resource.");

            result.Elements.Add(resourceName, generatedFonts.Elements[generatedFontKey]);
        }
        finally
        {
            document.Pages.Remove(resourcePage);
        }
    }

    private static bool IsBoldFontResource(PdfAnnotation annotation, string resourceName)
    {
        if (string.Equals(resourceName, "/ArialBold", StringComparison.OrdinalIgnoreCase))
            return true;

        var normalizedResourceName = resourceName.StartsWith("/", StringComparison.Ordinal)
            ? resourceName
            : $"/{resourceName}";
        var fonts = annotation.Elements.GetDictionary("/DR")?.Elements.GetDictionary("/Font");
        var fontObject = fonts?.Elements.GetObject(normalizedResourceName) as PdfDictionary;
        var baseFont = fontObject?.Elements.GetName("/BaseFont") ?? string.Empty;
        return baseFont.Contains("Bold", StringComparison.OrdinalIgnoreCase) ||
               normalizedResourceName.Contains("Bold", StringComparison.OrdinalIgnoreCase);
    }

    private static bool IsFreeTextAnnotation(PdfAnnotation annotation) =>
        string.Equals(annotation.Elements.GetString("/Subtype"), "/FreeText", StringComparison.OrdinalIgnoreCase) ||
        string.Equals(annotation.Elements.GetString("/Subtype"), "FreeText", StringComparison.OrdinalIgnoreCase);

    private static bool TryReadPdfColor(PdfAnnotation annotation, string key, out string colorHex)
    {
        colorHex = string.Empty;
        try
        {
            var array = annotation.Elements.GetArray(key);
            if (array is null || array.Elements.Count < 3)
                return false;

            var r = Math.Clamp(array.Elements.GetReal(0), 0, 1);
            var g = Math.Clamp(array.Elements.GetReal(1), 0, 1);
            var b = Math.Clamp(array.Elements.GetReal(2), 0, 1);
            colorHex = $"#FF{(byte)Math.Round(r * 255):X2}{(byte)Math.Round(g * 255):X2}{(byte)Math.Round(b * 255):X2}";
            return true;
        }
        catch
        {
            return false;
        }
    }

    private static AnnotationBorderStyle ReadBorderStyle(PdfAnnotation annotation)
    {
        var bs = annotation.Elements.GetDictionary("/BS");
        if (bs != null)
        {
            var style = bs.Elements.GetName("/S");
            if (string.Equals(style, "/D", StringComparison.OrdinalIgnoreCase) ||
                string.Equals(style, "D", StringComparison.OrdinalIgnoreCase))
                return AnnotationBorderStyle.Dashed;
            return AnnotationBorderStyle.Solid;
        }

        var custom = annotation.Elements.GetString("/PdfViewerBorderStyle");
        return string.Equals(custom, "Dashed", StringComparison.OrdinalIgnoreCase)
            ? AnnotationBorderStyle.Dashed
            : AnnotationBorderStyle.Solid;
    }

    private static void WriteBorderStyle(PdfAnnotation annotation, PdfDocument document, AnnotationBorderStyle style)
    {
        var bs = new PdfDictionary(document);
        bs.Elements.SetReal("/W", 1);
        if (style == AnnotationBorderStyle.Dashed)
        {
            bs.Elements.SetName("/S", "/D");
            bs.Elements.SetObject("/D", new PdfArray(document, new PdfReal(3), new PdfReal(3)));
            annotation.Elements.SetString("/PdfViewerBorderStyle", "Dashed");
        }
        else
        {
            bs.Elements.SetName("/S", "/S");
        }

        annotation.Elements.SetObject("/BS", bs);
        annotation.Elements.SetObject("/Border", new PdfArray(document, new PdfReal(0), new PdfReal(0), new PdfReal(1)));
    }
}