using Avalonia;
using Avalonia.Controls;
using Avalonia.Controls.Primitives;
using Avalonia.Input;
using Avalonia.Threading;
using PdfViewerControl.Helpers;
using ComboBox = AtomUI.Desktop.Controls.ComboBox;
using ComboBoxItem = AtomUI.Desktop.Controls.ComboBoxItem;

namespace PdfViewerControl.Controls;

public partial class PdfViewer
{
    public const double ZoomMin = 0.25;
    public const double ZoomMax = 4.0;
    public const double ZoomFactor = 1.2;

    private void ZoomIn() => ApplyZoomLevel(_zoom * ZoomFactor);
    private void ZoomOut() => ApplyZoomLevel(_zoom / ZoomFactor);

    private void ZoomOutButton_OnClick(object? sender, Avalonia.Interactivity.RoutedEventArgs e)
    {
        ZoomOut();
    }

    private void ZoomInButton_OnClick(object? sender, Avalonia.Interactivity.RoutedEventArgs e)
    {
        ZoomIn();
    }

    private void ZoomComboBox_OnSelectionChanged(object? sender, SelectionChangedEventArgs e)
    {
        if (sender is ComboBox comboBox && comboBox.SelectedItem is ComboBoxItem item)
            CommitZoomText(item.Content?.ToString());
    }

    private void ZoomComboBox_OnKeyDown(object? sender, KeyEventArgs e)
    {
        if (e.Key != Key.Enter || sender is not ComboBox comboBox) return;
        e.Handled = true;
        CommitZoomText(comboBox.Text);
    }

    private void ZoomComboBox_OnLostFocus(object? sender, Avalonia.Interactivity.RoutedEventArgs e)
    {
        if (sender is ComboBox comboBox)
            CommitZoomText(comboBox.Text);
    }

    private void CommitZoomText(string? text)
    {
        if (string.IsNullOrWhiteSpace(text))
        {
            UpdateZoomLabel();
            return;
        }

        var normalized = text.Trim().TrimEnd('%');
        if (!double.TryParse(normalized, out var percent))
        {
            UpdateZoomLabel();
            return;
        }

        ApplyZoomLevel(Math.Clamp(percent / 100.0, ZoomMin, ZoomMax));
    }

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
        var anchor = Math.Clamp(_currentPageIndex, 0, Math.Max(0, _pageSlots.Count - 1));
        var anchorScrollOffset = MainScrollViewer.Offset.Y - GetPageHostTop(anchor);

        _suppressSliderZoomSchedule = true;
        ZoomSlider.Value = Math.Round(newZoom * 100);
        _suppressSliderZoomSchedule = false;
        UpdateZoomLabel();

        _loader.SetDisplayZoom(newZoom, DisplayDpiHelper.GetDpiScale(this));
        _zoom = newZoom;

        InvalidateAllPageBitmaps(false);
        ApplyZoomLayoutInPlace();
        RelayoutAllAnnotations();
        ReleaseFramesOutsideVisible();

        _currentPageIndex = anchor;
        UpdatePageInfo();

        PagesPanel.InvalidateArrange();
        MainScrollViewer.InvalidateArrange();

        // ScrollViewer.Extent still holds the pre-zoom value here: it is recomputed during the next
        // layout pass. Restoring the previous ratio (offset / extent) would therefore keep the very
        // same offset while every page grew or shrank, which lands on a different page. Anchor on the
        // current page instead and apply the target offset once the new layout has been arranged.
        _suspendScrollPageTracking++;
        Dispatcher.UIThread.Post(() => RestoreScrollAfterZoom(anchor, anchorScrollOffset), DispatcherPriority.Loaded);

        RequestVirtualizationImmediate();

        SetStatus($"Page {_currentPageIndex + 1} / {_loader.PageCount} - {(int)Math.Round(newZoom * 100)}%");
    }

    private void RestoreScrollAfterZoom(int anchor, double anchorScrollOffset)
    {
        try
        {
            var extent = MainScrollViewer.Extent.Height;
            var viewport = MainScrollViewer.Bounds.Height;
            var target = Math.Clamp(GetPageHostTop(anchor) + anchorScrollOffset, 0, Math.Max(0, extent - viewport));
            MainScrollViewer.Offset = new Vector(MainScrollViewer.Offset.X, target);
            UpdateCurrentPageIndexFromViewport();
            SetStatus($"Page {_currentPageIndex + 1} / {_loader.PageCount} - {(int)Math.Round(_zoom * 100)}%");
        }
        finally
        {
            _suspendScrollPageTracking = Math.Max(0, _suspendScrollPageTracking - 1);
        }
    }

    /// <summary>Top of the given page in scroll coordinates.</summary>
    private double GetPageHostTop(int pageIndex)
    {
        if (_pageHostTops.Count == 0) return PagesPanel.Margin.Top;
        return _pageHostTops[Math.Clamp(pageIndex, 0, _pageHostTops.Count - 1)];
    }

    private void InvalidateAllPageBitmaps(bool keepVisible = false)
    {
        var (first, last) = keepVisible ? GetVisiblePageBounds(ViewportPageBuffer) : (0, -1);
        foreach (var s in _pageSlots)
        {
            if (keepVisible && s.PageIndex >= first && s.PageIndex <= last && s.PageImage?.Source != null)
            {
                s.IsBitmapLoaded = false;
                s.IsRendering = false;
                if (s.LoadingOverlay is { } loadingOverlay)
                    loadingOverlay.IsVisible = false;
                continue;
            }
            ReleasePageBitmap(s);
        }
    }

    private void ReleaseFramesOutsideVisible()
    {
        if (_pageSlots.Count == 0) return;
        var (first, last) = GetVisiblePageBounds(ViewportPageBuffer);
        // Only cached frames can be evicted; document length is irrelevant here.
        for (var i = _bitmapSlots.Count - 1; i >= 0; i--)
        {
            var slot = _bitmapSlots[i];
            if (slot.PageIndex < first || slot.PageIndex > last)
                ReleasePageBitmap(slot);
        }
    }
}
