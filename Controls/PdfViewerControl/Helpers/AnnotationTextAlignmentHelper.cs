using Avalonia.Layout;
using Avalonia.Controls;
using PdfViewerControl.Models;

namespace PdfViewerControl.Helpers;

public static class AnnotationTextAlignmentHelper
{
    public static void ApplyToEditor(TextBox editor, TextBlock? placeholder, AnnotationHorizontalTextAlignment horizontal, AnnotationVerticalTextAlignment vertical)
    {
        var h = ToHorizontal(horizontal);
        var v = ToVertical(vertical);
        editor.HorizontalContentAlignment = h;
        editor.VerticalContentAlignment = v;
        editor.TextAlignment = horizontal switch
        {
            AnnotationHorizontalTextAlignment.Center => Avalonia.Media.TextAlignment.Center,
            AnnotationHorizontalTextAlignment.Right => Avalonia.Media.TextAlignment.Right,
            _ => Avalonia.Media.TextAlignment.Left
        };
        if (placeholder == null) return;
        placeholder.HorizontalAlignment = h;
        placeholder.VerticalAlignment = v;
    }

    public static HorizontalAlignment ToHorizontal(AnnotationHorizontalTextAlignment a) => a switch
    {
        AnnotationHorizontalTextAlignment.Center => HorizontalAlignment.Center,
        AnnotationHorizontalTextAlignment.Right => HorizontalAlignment.Right,
        _ => HorizontalAlignment.Left
    };

    public static VerticalAlignment ToVertical(AnnotationVerticalTextAlignment a) => a switch
    {
        AnnotationVerticalTextAlignment.Center => VerticalAlignment.Center,
        AnnotationVerticalTextAlignment.Bottom => VerticalAlignment.Bottom,
        _ => VerticalAlignment.Top
    };

    public static string Serialize(AnnotationHorizontalTextAlignment h, AnnotationVerticalTextAlignment v) => $"{h},{v}";

    public static void TryParse(string? value, FreeTextAnnotationModel model)
    {
        if (string.IsNullOrWhiteSpace(value)) return;
        var parts = value.Split(',', StringSplitOptions.TrimEntries);
        if (parts.Length >= 1 && Enum.TryParse(parts[0], true, out AnnotationHorizontalTextAlignment h))
            model.HorizontalTextAlignment = h;
        if (parts.Length >= 2 && Enum.TryParse(parts[1], true, out AnnotationVerticalTextAlignment vert))
            model.VerticalTextAlignment = vert;
    }
}