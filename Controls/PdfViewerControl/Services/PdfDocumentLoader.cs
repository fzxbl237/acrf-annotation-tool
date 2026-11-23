using System.Buffers;
using System.IO;
using System.Runtime.InteropServices;
using Avalonia;
using Avalonia.Media.Imaging;
using PdfViewerControl.Text;

namespace PdfViewerControl.Services;

public sealed class PdfDocumentLoader : IDisposable
{
    private readonly object _readerLock = new();
    private readonly Docnet.Core.DocLib _docLib = Docnet.Core.DocLib.Instance;
    private byte[]? _pdfBytes;
    private Docnet.Core.Readers.IDocReader? _reader;
    private Docnet.Core.Readers.IDocReader? _textReader;
    private int _documentVersion;
    private double _readerScaleFactor;
    private List<Size> _pageSizesInPoints = new();
    private double _scaleFactor = 96.0 / 72.0;
    private double _dpiScale = 1.0;
    private double _zoom = 1.0;
    private bool _disposed;

    public IReadOnlyList<Size> PageSizes { get; private set; } = Array.Empty<Size>();
    public int PageCount => PageSizes.Count;
    public double ScaleFactor => _scaleFactor;
    public double DpiScale => _dpiScale;
    public double DisplayScale => _scaleFactor / _dpiScale;
    public int DocumentVersion { get { lock (_readerLock) return _documentVersion; } }

    public void Load(string filePath, double zoom = 1.0, double dpiScale = 1.0)
    {
        var bytes = File.ReadAllBytes(filePath);
        LoadFromBytes(bytes, zoom, dpiScale);
    }

    public void LoadFromBytes(byte[] pdfBytes, double zoom = 1.0, double dpiScale = 1.0)
    {
        lock (_readerLock)
        {
            ObjectDisposedException.ThrowIf(_disposed, this);
            DisposeReaderUnsafe();
            _pdfBytes = pdfBytes;
            _zoom = zoom;
            _dpiScale = Math.Max(1.0, dpiScale);
            RecomputeScaleFactor();
            OpenReaderAtCurrentScale();
            CachePageSizesInPoints();
            RebuildDisplayPageSizes();
        }
    }

    public void SetDisplayZoom(double zoom, double dpiScale)
    {
        lock (_readerLock)
        {
            if (_pdfBytes == null || _pageSizesInPoints.Count == 0) return;
            _zoom = zoom;
            _dpiScale = Math.Max(1.0, dpiScale);
            RecomputeScaleFactor();
            RebuildDisplayPageSizes();
        }
    }

    public double GetPageWidthInPoints(int pageIndex)
    {
        lock (_readerLock)
        {
            if (pageIndex >= 0 && pageIndex < _pageSizesInPoints.Count) return _pageSizesInPoints[pageIndex].Width;
            return 612;
        }
    }

    public double GetPageHeightInPoints(int pageIndex)
    {
        lock (_readerLock)
        {
            if (pageIndex >= 0 && pageIndex < _pageSizesInPoints.Count) return _pageSizesInPoints[pageIndex].Height;
            return 792;
        }
    }

    private const double MaxRenderLongEdgePixels = 3500;

    public WriteableBitmap? TryRenderPage(int pageIndex)
    {
        lock (_readerLock)
        {
            if (_pdfBytes == null || pageIndex < 0 || pageIndex >= PageCount) return null;
            var renderScale = GetRenderScale(pageIndex);
            EnsureReaderMatchesScale(renderScale);
            if (_reader == null) return null;
            byte[]? rented = null;
            try
            {
                using var pageReader = _reader.GetPageReader(pageIndex);
                var width = Math.Max(1, pageReader.GetPageWidth());
                var height = Math.Max(1, pageReader.GetPageHeight());
                var byteCount = checked(width * height * 4);
                rented = ArrayPool<byte>.Shared.Rent(byteCount);
                pageReader.WriteImageToBuffer((Docnet.Core.Models.RenderFlags)0, rented);
                var bmp = new WriteableBitmap(new PixelSize(width, height), new Vector(96, 96));
                try
                {
                    using var fb = bmp.Lock();
                    var srcStride = width * 4;
                    if (fb.RowBytes == srcStride)
                        Marshal.Copy(rented, 0, fb.Address, byteCount);
                    else
                        for (var y = 0; y < height; y++)
                            Marshal.Copy(rented, y * srcStride, fb.Address + y * fb.RowBytes, Math.Min(srcStride, fb.RowBytes));
                    return bmp;
                }
                catch
                {
                    bmp.Dispose();
                    throw;
                }
            }
            catch (Docnet.Core.Exceptions.DocnetException) { return null; }
            finally
            {
                if (rented is not null)
                    ArrayPool<byte>.Shared.Return(rented);
            }
        }
    }

    public WriteableBitmap RenderPage(int pageIndex) => TryRenderPage(pageIndex) ?? CreateErrorPlaceholder(pageIndex);

    // Text has its own fixed-resolution reader, so zooming never invalidates character geometry
    // or repeatedly reopens the bitmap reader during a document-wide search.
    internal Task<PdfPageText> ReadPageTextAsync(int pageIndex, int documentVersion, CancellationToken token) => Task.Run(() =>
    {
        lock (_readerLock)
        {
            token.ThrowIfCancellationRequested();
            if (_disposed || documentVersion != _documentVersion)
                throw new OperationCanceledException("The PDF document has changed.", token);
            if (_pdfBytes == null || pageIndex < 0 || pageIndex >= PageCount)
                return new PdfPageText(string.Empty, []);
            _textReader ??= _docLib.GetDocReader(_pdfBytes, new Docnet.Core.Models.PageDimensions(2.0));
            using var page = _textReader.GetPageReader(pageIndex);
            // This PDFium build emits U+FFFE for a line-end hyphen. Use the text algorithms'
            // marker without changing UTF-16 offsets; U+FFFE cannot pass .NET normalization.
            var text = page.GetText().Replace('\uFFFE', '\u0002');
            var boxes = new Rect[text.Length];
            var sx = _pageSizesInPoints[pageIndex].Width / Math.Max(1, page.GetPageWidth());
            var sy = _pageSizesInPoints[pageIndex].Height / Math.Max(1, page.GetPageHeight());
            var at = 0;
            foreach (var character in page.GetCharacters())
            {
                token.ThrowIfCancellationRequested();
                // Docnet exposes one char per PDFium code point and can omit characters with no box.
                // Keep the full UTF-16 text (including line breaks and supplementary characters).
                var found = at;
                while (found < text.Length && !MatchesCharacter(text, found, character.Char))
                    found += char.IsSurrogatePair(text, found) ? 2 : 1;
                if (found == text.Length) continue;
                at = found;
                var units = char.IsSurrogatePair(text, at) ? 2 : 1;
                var box = character.Box;
                var rect = text[at] is '\r' or '\n' ? default : new Rect(
                    Math.Min(box.Left, box.Right) * sx, Math.Min(box.Top, box.Bottom) * sy,
                    Math.Abs(box.Right - box.Left) * sx, Math.Abs(box.Bottom - box.Top) * sy);
                for (var i = 0; i < units; i++) boxes[at + i] = rect;
                at += units;
            }
            return new PdfPageText(text, boxes);
        }
    }, token);

    private static bool MatchesCharacter(string text, int index, char character) =>
        text[index] == character || (text[index] == '\u0002' && character == '\uFFFE') ||
        (char.IsSurrogatePair(text, index) &&
            unchecked((char)char.ConvertToUtf32(text, index)) == character);

    public void Dispose()
    {
        lock (_readerLock)
        {
            if (_disposed) return;
            _disposed = true;
            DisposeReaderUnsafe();
            _pdfBytes = null;
        }
        GC.SuppressFinalize(this);
    }

    private void RecomputeScaleFactor() => _scaleFactor = 96.0 / 72.0 * Math.Max(0.25, _zoom) * _dpiScale;

    private void RebuildDisplayPageSizes()
    {
        var dipPerPoint = 96.0 / 72.0 * Math.Max(0.25, _zoom);
        var sizes = new List<Size>(_pageSizesInPoints.Count);
        foreach (var pt in _pageSizesInPoints)
            sizes.Add(new Size(pt.Width * dipPerPoint, pt.Height * dipPerPoint));
        PageSizes = sizes;
    }

    private void EnsureReaderMatchesScale(double renderScale)
    {
        if (Math.Abs(_readerScaleFactor - renderScale) < 0.0001 && _reader != null) return;
        _reader?.Dispose();
        _reader = _docLib.GetDocReader(_pdfBytes!, new Docnet.Core.Models.PageDimensions(renderScale));
        _readerScaleFactor = renderScale;
    }

    private double GetRenderScale(int pageIndex)
    {
        var maxLongEdge = _pageSizesInPoints.Count > 0
            ? Math.Max(_pageSizesInPoints[Math.Clamp(pageIndex, 0, _pageSizesInPoints.Count - 1)].Width,
                _pageSizesInPoints[Math.Clamp(pageIndex, 0, _pageSizesInPoints.Count - 1)].Height)
            : 612;
        var scale = _scaleFactor;
        if (maxLongEdge * scale > MaxRenderLongEdgePixels)
            scale = MaxRenderLongEdgePixels / maxLongEdge;
        return Math.Max(0.1, scale);
    }

    private void OpenReaderAtCurrentScale()
    {
        if (_pdfBytes == null) return;
        _reader?.Dispose();
        _reader = _docLib.GetDocReader(_pdfBytes, new Docnet.Core.Models.PageDimensions(_scaleFactor));
        _readerScaleFactor = _scaleFactor;
    }

    private void CachePageSizesInPoints()
    {
        _pageSizesInPoints = new List<Size>();
        if (_reader == null) return;
        var pageCount = _reader.GetPageCount();
        var fallback = new Size(612, 792);
        for (var i = 0; i < pageCount; i++)
        {
            try
            {
                using var pageReader = _reader.GetPageReader(i);
                var wPt = pageReader.GetPageWidth() / _scaleFactor;
                var hPt = pageReader.GetPageHeight() / _scaleFactor;
                if (wPt < 1 || hPt < 1) throw new Docnet.Core.Exceptions.DocnetException("invalid");
                var size = new Size(wPt, hPt);
                _pageSizesInPoints.Add(size);
                fallback = size;
            }
            catch (Docnet.Core.Exceptions.DocnetException) { _pageSizesInPoints.Add(fallback); }
        }
    }

    private WriteableBitmap CreateErrorPlaceholder(int pageIndex)
    {
        if (pageIndex < 0 || pageIndex >= PageSizes.Count) return CreateSolidBitmap(400, 520);
        var s = PageSizes[pageIndex];
        return CreateSolidBitmap((int)Math.Max(1, s.Width), (int)Math.Max(1, s.Height));
    }

    private WriteableBitmap CreateSolidBitmap(int width, int height)
    {
        var w = Math.Max(1, width);
        var h = Math.Max(1, height);
        // Reuse one scanline instead of allocating another full-page buffer.
        var pixels = new byte[checked(w * 4)];
        for (var i = 0; i < pixels.Length; i += 4) { pixels[i] = 0xF3; pixels[i + 1] = 0xF4; pixels[i + 2] = 0xF6; pixels[i + 3] = 0xFF; }
        var bitmap = new WriteableBitmap(new PixelSize(w, h), new Vector(96, 96));
        using (var fb = bitmap.Lock())
        {
            var stride = w * 4;
            for (var y = 0; y < h; y++)
                Marshal.Copy(pixels, 0, fb.Address + y * fb.RowBytes, Math.Min(stride, fb.RowBytes));
        }
        return bitmap;
    }

    private void DisposeReaderUnsafe()
    {
        ++_documentVersion;
        _textReader?.Dispose();
        _textReader = null;
        _reader?.Dispose();
        _reader = null;
        _readerScaleFactor = 0;
        _pageSizesInPoints.Clear();
        PageSizes = Array.Empty<Size>();
    }
}