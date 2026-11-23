using System.Collections.Concurrent;
using PdfSharp.Drawing;
using PdfSharp.Fonts;

namespace PdfViewerControl.Services;

internal sealed class ProjectFontResolver : IFontResolver
{
    private const string ArialFace = "ProjectArial";
    private const string ArialBoldFace = "ProjectArialBold";
    private const string ArialUnicodeFace = "ProjectArialUnicode";

    // PDFsharp asks for the same face several times per save (and every save repeats it); without this
    // cache every save re-read ARIALUNI.TTF (22 MB) plus the two Arial faces from disk. The bytes are
    // immutable, so one copy per face is enough for the process lifetime.
    private static readonly ConcurrentDictionary<string, Lazy<byte[]?>> FontData = new(StringComparer.Ordinal);

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
        if (faceName is not (ArialFace or ArialBoldFace or ArialUnicodeFace))
            return null;

        return FontData.GetOrAdd(faceName, static name => new Lazy<byte[]?>(() =>
        {
            var fileName = name switch
            {
                ArialFace => "ARIAL.TTF",
                ArialBoldFace => "ARIALBD.TTF",
                _ => "ARIALUNI.TTF"
            };
            var path = Path.Combine(AppContext.BaseDirectory, "Fonts", fileName);
            return File.Exists(path) ? File.ReadAllBytes(path) : null;
        })).Value;
    }
}
