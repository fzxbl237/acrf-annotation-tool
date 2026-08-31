using PdfSharp.Drawing;
using PdfSharp.Fonts;

namespace PdfViewerControl.Services;

internal sealed class ProjectFontResolver : IFontResolver
{
    private const string ArialFace = "ProjectArial";
    private const string ArialBoldFace = "ProjectArialBold";
    private const string ArialUnicodeFace = "ProjectArialUnicode";

    public FontResolverInfo? ResolveTypeface(string familyName, bool isBold, bool isItalic)
    {
        if (isItalic)
            return null;

        if (familyName.Equals("Arial Unicode MS", StringComparison.OrdinalIgnoreCase))
            return new FontResolverInfo(ArialUnicodeFace);

        if (familyName.Equals("Arial", StringComparison.OrdinalIgnoreCase))
            return new FontResolverInfo(isBold ? ArialBoldFace : ArialFace);

        return null;
    }

    public byte[]? GetFont(string faceName)
    {
        var fileName = faceName switch
        {
            ArialFace => "ARIAL.TTF",
            ArialBoldFace => "ARIALBD.TTF",
            ArialUnicodeFace => "ARIALUNI.TTF",
            _ => null
        };
        if (fileName is null)
            return null;

        var path = Path.Combine(AppContext.BaseDirectory, "Fonts", fileName);
        return File.Exists(path) ? File.ReadAllBytes(path) : null;
    }
}
