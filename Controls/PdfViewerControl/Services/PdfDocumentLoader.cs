using System.IO;
using System.Runtime.InteropServices;
using Avalonia;
using Avalonia.Media.Imaging;

namespace PdfViewerControl.Services;

public sealed class PdfDocumentLoader : IDisposable
{
    private readonly object _readerLock = new();
    private readonly Docnet.Core.DocLib _docLib = Docnet.Core.DocLib.Instance;
    private byte[]? _pdfBytes;
    private Docnet.Core.Readers.IDocReader? _reader;
    private double _readerScaleFactor;
    private List<Size> _pageSizesInPoints = new();
    private double _scaleFactor = 96.0 / 72.0;
    private double _dpiScale = 1.0;
    private double _zoom = 1.0;

    public IReadOnlyList<Size> PageSizes { get; private set; } = Array.Empty<Size>();
    public int PageCount => PageSizes.Count;
    public double ScaleFactor => _scaleFactor;
    public double DpiScale => _dpiScale;
    public double DisplayScale => _scaleFactor / _dpiScale;

    public void Load(string filePath, double zoom = 1.0, double dpiScale = 1.0)
    {
        var bytes = File.ReadAllBytes(filePath);
        LoadFromBytes(bytes, zoom, dpiScale);
    }

    public void LoadFromBytes(byte[] pdfBytes, double zoom = 1.0, double dpiScale = 1.0)
    {
        lock (_readerLock)
        {
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

    public WriteableBitmap? TryRenderPage(int pageIndex)
    {
        lock (_readerLock)
        {
            if (_pdfBytes == null || pageIndex < 0 || pageIndex >= PageCount) return null;
            EnsureReaderMatchesScale();
            if (_reader == null) return null;
            try
            {
                using var pageReader = _reader.GetPageReader(pageIndex);
                var width = Math.Max(1, pageReader.GetPageWidth());
                var height = Math.Max(1, pageReader.GetPageHeight());
                var rawBytes = pageReader.GetImage();
                var bmp = new WriteableBitmap(new PixelSize(width, height), new Vector(96, 96));
                using (var fb = bmp.Lock())
                {
                    var srcStride = width * 4;
                    for (var y = 0; y < height; y++)
                    {
                        var srcOffset = y * srcStride;
                        var dstOffset = y * fb.RowBytes;
                        Marshal.Copy(rawBytes, srcOffset, fb.Address + dstOffset, Math.Min(srcStride, fb.RowBytes));
                    }
                }
                return bmp;
            }
            catch (Docnet.Core.Exceptions.DocnetException) { return null; }
        }
    }

    public WriteableBitmap RenderPage(int pageIndex) => TryRenderPage(pageIndex) ?? CreateErrorPlaceholder(pageIndex);

    public void Dispose()
    {
        lock (_readerLock) { DisposeReaderUnsafe(); }
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

    private void EnsureReaderMatchesScale()
    {
        if (Math.Abs(_readerScaleFactor - _scaleFactor) < 0.0001 && _reader != null) return;
        OpenReaderAtCurrentScale();
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
        var pixels = new byte[w * h * 4];
        for (var i = 0; i < pixels.Length; i += 4) { pixels[i] = 0xF3; pixels[i + 1] = 0xF4; pixels[i + 2] = 0xF6; pixels[i + 3] = 0xFF; }
        var bitmap = new WriteableBitmap(new PixelSize(w, h), new Vector(96, 96));
        using (var fb = bitmap.Lock())
        {
            var stride = w * 4;
            for (var y = 0; y < h; y++)
                Marshal.Copy(pixels, y * stride, fb.Address + y * fb.RowBytes, Math.Min(stride, fb.RowBytes));
        }
        return bitmap;
    }

    private void DisposeReaderUnsafe()
    {
        _reader?.Dispose();
        _reader = null;
        _readerScaleFactor = 0;
        _pageSizesInPoints.Clear();
        PageSizes = Array.Empty<Size>();
    }
}