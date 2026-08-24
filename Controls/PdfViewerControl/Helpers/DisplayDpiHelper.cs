using Avalonia;
using Avalonia.Controls;

namespace PdfViewerControl.Helpers;

public static class DisplayDpiHelper
{
    public static double GetDpiScale(Visual? visual)
    {
        if (visual == null) return 1.0;
        try
        {
            var topLevel = TopLevel.GetTopLevel(visual);
            if (topLevel != null) return topLevel.RenderScaling;
            return 1.0;
        }
        catch { return 1.0; }
    }
}