using System.Globalization;
using System.Reflection;
using System.Security;
using System.Text.RegularExpressions;
using Avalonia.Media;
using PdfSharp.Drawing;
using PdfSharp.Drawing.Layout;
using PdfSharp.Fonts;
using PdfSharp.Pdf;
using PdfSharp.Pdf.Annotations;
using PdfSharp.Pdf.IO;
using PdfViewerControl.Helpers;
using PdfViewerControl.Models;

namespace PdfViewerControl.Services;

public static class PdfAnnotationService
{
    private static readonly Regex DaRegex = new(
        @"/(?<font>[\w+-]+)\s+(?<size>[\d.]+)\s+Tf\s+(?<r>[\d.]+)\s+(?<g>[\d.]+)\s+(?<b>[\d.]+)\s+rg",
        RegexOptions.Compiled);

    private static readonly PropertyInfo PdfFormProperty =
        typeof(XForm).GetProperty("PdfForm", BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Instance)
        ?? throw new InvalidOperationException("PDFsharp XForm.PdfForm property is unavailable.");

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
                    Text = annotation.Contents ?? ""
                };
                var da = annotation.Elements.GetString("/DA");
                if (!string.IsNullOrWhiteSpace(da))
                {
                    var match = DaRegex.Match(da);
                    if (match.Success)
                    {
                        if (double.TryParse(match.Groups["size"].Value, NumberStyles.Float, CultureInfo.InvariantCulture, out var sz))
                            m.FontSize = sz;
                        m.FontFamily = "Arial";
                        m.IsBold = IsBoldFontResource(annotation, "/" + match.Groups["font"].Value);
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
        }
        foreach (var g in annotations.GroupBy(a => a.PageIndex))
        {
            if (g.Key < 0 || g.Key >= doc.PageCount) continue;
            foreach (var m in g)
            {
                var a = new PdfTextAnnotation { Contents = m.Text ?? "" };
                a.Elements.SetName("/Subtype", "/FreeText");
                a.Elements.SetString("/PdfViewerAnnotationId", m.Id);
                if (m.Id.StartsWith("__domain_", StringComparison.Ordinal))
                    a.Elements.SetBoolean("/PdfViewerDomainAnnotation", true);
                a.Rectangle = new PdfRectangle(
                    new XPoint(m.PdfX, m.PdfY),
                    new XPoint(m.PdfX + m.Width, m.PdfY + m.Height));
                var c = Color.Parse(m.FontColorHex);
                var fontResourceName = m.IsBold ? "/ArialBold" : "/Arial";
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
            }
        }

        foreach (var page in doc.Pages)
        {
            foreach (var annotation in page.Annotations.OfType<PdfAnnotation>()
                         .Where(IsFreeTextAnnotation)
                         .ToList())
                page.Annotations.Remove(annotation);
        }

        doc.Save(dst);
    }

    private static string CreateDefaultStyle(FreeTextAnnotationModel model)
    {
        var color = Color.Parse(model.FontColorHex);
        var weight = model.IsBold ? "bold" : "normal";
        var alignment = model.HorizontalTextAlignment switch
        {
            AnnotationHorizontalTextAlignment.Center => "center",
            AnnotationHorizontalTextAlignment.Right => "right",
            _ => "left"
        };
        return $"font: {weight} Arial {model.FontSize.ToString(CultureInfo.InvariantCulture)}pt; " +
               $"text-align:{alignment}; color:#{color.R:X2}{color.G:X2}{color.B:X2}";
    }

    private static string CreateRichTextValue(FreeTextAnnotationModel model)
    {
        var color = Color.Parse(model.FontColorHex);
        var weight = model.IsBold ? "bold" : "normal";
        var alignment = model.HorizontalTextAlignment switch
        {
            AnnotationHorizontalTextAlignment.Center => "center",
            AnnotationHorizontalTextAlignment.Right => "right",
            _ => "left"
        };
        var escapedText = SecurityElement.Escape(model.Text ?? string.Empty) ?? string.Empty;
        escapedText = escapedText.Replace("\r\n", "<br/>").Replace("\r", "<br/>").Replace("\n", "<br/>");
        var style = $"font-family:Arial,sans-serif;font-size:{model.FontSize.ToString(CultureInfo.InvariantCulture)}pt;" +
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

        var pdfForm = PdfFormProperty.GetValue(form) as PdfObject
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
            borderPen.DashStyle = XDashStyle.Dash;
        graphics.DrawRectangle(borderPen, x + 0.5, y + 0.5, Math.Max(0, width - 1), Math.Max(0, height - 1));

        var font = new XFont(
            ResolvePdfFontFamily(model),
            Math.Max(1, model.FontSize),
            model.IsBold ? XFontStyleEx.Bold : XFontStyleEx.Regular);
        var format = new XStringFormat
        {
            Alignment = model.HorizontalTextAlignment switch
            {
                AnnotationHorizontalTextAlignment.Center => XStringAlignment.Center,
                AnnotationHorizontalTextAlignment.Right => XStringAlignment.Far,
                _ => XStringAlignment.Near
            },
            LineAlignment = model.VerticalTextAlignment switch
            {
                AnnotationVerticalTextAlignment.Center => XLineAlignment.Center,
                AnnotationVerticalTextAlignment.Bottom => XLineAlignment.Far,
                _ => XLineAlignment.Near
            }
        };
        var padding = 2.0;
        var textRectangle = new XRect(
            x + padding,
            y + padding,
            Math.Max(0, width - padding * 2),
            Math.Max(0, height - padding * 2));
        var text = model.Text ?? string.Empty;
        if (text.Contains('\n') || text.Contains('\r') || graphics.MeasureString(text, font).Width > textRectangle.Width)
        {
            var formatter = new XTextFormatter(graphics);
            formatter.Alignment = model.HorizontalTextAlignment switch
            {
                AnnotationHorizontalTextAlignment.Center => XParagraphAlignment.Center,
                AnnotationHorizontalTextAlignment.Right => XParagraphAlignment.Right,
                _ => XParagraphAlignment.Left
            };
            formatter.DrawString(text, font, textBrush, textRectangle);
        }
        else
        {
            graphics.DrawString(text, font, textBrush, textRectangle, format);
        }
    }

    private static string ResolvePdfFontFamily(FreeTextAnnotationModel model)
    {
        var text = model.Text ?? string.Empty;
        return text.Any(c => c > 127) ? "SimSun" : "Arial";
    }

    private static PdfDictionary CreateAnnotationFontResources(
        PdfDocument document,
        IReadOnlyList<FreeTextAnnotationModel> annotations)
    {
        GlobalFontSettings.UseWindowsFontsUnderWindows = true;

        var resourcePage = document.AddPage();
        using (var graphics = XGraphics.FromPdfPage(resourcePage))
        {
            var regularFont = new XFont("Arial", 12, XFontStyleEx.Regular);
            var boldFont = new XFont("Arial", 12, XFontStyleEx.Bold);
            var regularText = annotations.Where(a => !a.IsBold).Select(a => a.Text).Where(t => !string.IsNullOrEmpty(t)).ToList();
            var boldText = annotations.Where(a => a.IsBold).Select(a => a.Text).Where(t => !string.IsNullOrEmpty(t)).ToList();
            if (regularText.Count == 0) regularText.Add("A");
            if (boldText.Count == 0) boldText.Add("B");

            for (var i = 0; i < regularText.Count; i++)
                graphics.DrawString(regularText[i], regularFont, XBrushes.Black, new XPoint(-1000, -1000 - i * 20));
            for (var i = 0; i < boldText.Count; i++)
                graphics.DrawString(boldText[i], boldFont, XBrushes.Black, new XPoint(-1000, -2000 - i * 20));
        }

        var generatedFonts = resourcePage.Elements.GetDictionary("/Resources")?.Elements.GetDictionary("/Font");
        if (generatedFonts is null)
            throw new InvalidOperationException("PDFsharp did not generate font resources for FreeText annotations.");

        var result = new PdfDictionary(document);
        foreach (var key in generatedFonts.Elements.Keys)
        {
            var fontObject = generatedFonts.Elements.GetObject(key) as PdfDictionary;
            var baseFont = fontObject?.Elements.GetName("/BaseFont") ?? string.Empty;
            var resourceName = baseFont.Contains("Bold", StringComparison.OrdinalIgnoreCase)
                ? "/ArialBold"
                : "/Arial";
            if (!result.Elements.ContainsKey(resourceName))
                result.Elements.Add(resourceName, generatedFonts.Elements[key]);
        }

        document.Pages.Remove(resourcePage);
        if (!result.Elements.ContainsKey("/Arial") || !result.Elements.ContainsKey("/ArialBold"))
            throw new InvalidOperationException("PDFsharp did not generate both Arial and Arial Bold resources.");

        var resourceDictionary = new PdfDictionary(document);
        resourceDictionary.Elements.SetObject("/Font", result);
        return resourceDictionary;
    }

    private static bool IsBoldFontResource(PdfAnnotation annotation, string resourceName)
    {
        if (string.Equals(resourceName, "/ArialBold", StringComparison.OrdinalIgnoreCase))
            return true;

        var fonts = annotation.Elements.GetDictionary("/DR")?.Elements.GetDictionary("/Font");
        var fontObject = fonts?.Elements.GetObject(resourceName) as PdfDictionary;
        var baseFont = fontObject?.Elements.GetName("/BaseFont") ?? string.Empty;
        return baseFont.Contains("Bold", StringComparison.OrdinalIgnoreCase);
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
            bs.Elements.SetObject("/D", new PdfArray(document, new PdfReal(2), new PdfReal(2)));
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