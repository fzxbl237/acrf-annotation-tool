using Avalonia;
using Avalonia.Controls.Primitives;
using Avalonia.Input;
using PdfViewerControl.Helpers;

namespace PdfViewerControl.Controls;

public partial class PdfViewer
{
    public const double ZoomMin = 0.25;
    public const double ZoomMax = 4.0;
    public const double ZoomFactor = 1.2;

    private void ZoomIn() => ApplyZoomLevel(_zoom * ZoomFactor);
    private void ZoomOut() => ApplyZoomLevel(_zoom / ZoomFactor);

    private void ZoomSlider_OnValueChanged(object? sender, RangeBaseValueChangedEventArgs e)
    {
        UpdateZoomLabel();
        if (_suppressSliderZoomSchedule || string.IsNullOrEmpty(_currentPdfPath)) return;
        ApplyZoomLevel(Math.Clamp(ZoomSlider.Value / 100.0, ZoomMin, ZoomMax));
    }

    private void MainScrollViewer_OnPointerWheelChanged(object? sender, PointerWheelEventArgs e)
    {
        if (string.IsNullOrEmpty(_currentPdfPath) || !e.KeyModifiers.HasFlag(KeyModifiers.Control)) return;
        e.Handled = true;
        if (e.Delta.Y > 0) ZoomIn();
        else if (e.Delta.Y < 0) ZoomOut();
    }

    private void ApplyZoomLevel(double newZoom)
    {
        if (string.IsNullOrEmpty(_currentPdfPath) || _pageSlots.Count == 0) return;
        newZoom = Math.Clamp(newZoom, ZoomMin, ZoomMax);
        if (Math.Abs(newZoom - _zoom) < 0.001) return;

        ++_renderGeneration;
        var gen = _renderGeneration;
        var anchor = _currentPageIndex;
        var ratio = MainScrollViewer.Extent.Height > 1 ? MainScrollViewer.Offset.Y / MainScrollViewer.Extent.Height : 0.0;

        _suppressSliderZoomSchedule = true;
        ZoomSlider.Value = Math.Round(newZoom * 100);
        _suppressSliderZoomSchedule = false;
        UpdateZoomLabel();

        _loader.SetDisplayZoom(newZoom, DisplayDpiHelper.GetDpiScale(this));
        _zoom = newZoom;

        InvalidateAllPageBitmaps(false);
        BuildDocumentLayout(false);
        RelayoutAllAnnotations();
        ReleaseFramesOutsideVisible();

        _currentPageIndex = Math.Clamp(anchor, 0, Math.Max(0, _pageSlots.Count - 1));
        UpdatePageInfo();

        PagesPanel.InvalidateArrange();
        MainScrollViewer.InvalidateArrange();
        var ext = MainScrollViewer.Extent.Height;
        if (ext > 1)
        {
            var off = Math.Clamp(ratio * ext, 0, ext - MainScrollViewer.Bounds.Height);
            MainScrollViewer.Offset = new Vector(MainScrollViewer.Offset.X, Math.Max(0, off));
        }
        else
            ScrollToPage(_currentPageIndex);

        SetStatus($"Page {_currentPageIndex + 1} / {_loader.PageCount} - {(int)Math.Round(newZoom * 100)}%");
    }

    private void InvalidateAllPageBitmaps(bool keepVisible = false)
    {
        var keep = keepVisible ? new HashSet<int>(GetVisiblePageRange(ViewportPageBuffer)) : null;
        foreach (var s in _pageSlots)
        {
            if (keep != null && keep.Contains(s.PageIndex) && s.PageImage.Source != null)
            {
                s.IsBitmapLoaded = false; s.IsRendering = false; s.LoadingOverlay.IsVisible = false; continue;
            }
            s.PageImage.Source = null; s.IsBitmapLoaded = false; s.IsRendering = false; s.LoadingOverlay.IsVisible = false;
        }
    }

    private void ReleaseFramesOutsideVisible()
    {
        if (_pageSlots.Count == 0) return;
        var keep = new HashSet<int>(GetVisiblePageRange(ViewportPageBuffer));
        foreach (var s in _pageSlots)
        {
            if (!keep.Contains(s.PageIndex) && s.PageImage.Source != null)
            {
                s.PageImage.Source = null; s.IsBitmapLoaded = false; s.IsRendering = false; s.LoadingOverlay.IsVisible = false;
            }
        }
    }
}