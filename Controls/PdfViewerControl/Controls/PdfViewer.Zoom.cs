/*
 * Spring motion and page anchoring adapted from TKCode.Arc.
 * MIT License
 *
 * Copyright (c) 2026 TKCode.Arc contributors
 *
 * Permission is hereby granted, free of charge, to any person obtaining a copy
 * of this software and associated documentation files (the "Software"), to deal
 * in the Software without restriction, including without limitation the rights
 * to use, copy, modify, merge, publish, distribute, sublicense, and/or sell
 * copies of the Software, and to permit persons to whom the Software is
 * furnished to do so, subject to the following conditions:
 *
 * The above copyright notice and this permission notice shall be included in all
 * copies or substantial portions of the Software.
 *
 * THE SOFTWARE IS PROVIDED "AS IS", WITHOUT WARRANTY OF ANY KIND, EXPRESS OR
 * IMPLIED, INCLUDING BUT NOT LIMITED TO THE WARRANTIES OF MERCHANTABILITY,
 * FITNESS FOR A PARTICULAR PURPOSE AND NONINFRINGEMENT. IN NO EVENT SHALL THE
 * AUTHORS OR COPYRIGHT HOLDERS BE LIABLE FOR ANY CLAIM, DAMAGES OR OTHER
 * LIABILITY, WHETHER IN AN ACTION OF CONTRACT, TORT OR OTHERWISE, ARISING FROM,
 * OUT OF OR IN CONNECTION WITH THE SOFTWARE OR THE USE OR OTHER DEALINGS IN THE
 * SOFTWARE.
 */

using Avalonia;
using Avalonia.Controls;
using Avalonia.Controls.Primitives;
using Avalonia.Input;
using Avalonia.Interactivity;
using Avalonia.Threading;
using Avalonia.VisualTree;
using PdfViewerControl.Helpers;
using ComboBox = AtomUI.Desktop.Controls.ComboBox;
using ComboBoxItem = AtomUI.Desktop.Controls.ComboBoxItem;

namespace PdfViewerControl.Controls;

public partial class PdfViewer
{
    public const double ZoomMin = 0.25;
    public const double ZoomMax = 4.0;
    public const double ZoomFactor = 1.2;

    private readonly ViewerSpring _zoomMotion = new(1, 0.26, 0.12);
    private readonly ViewerSpring _scrollMotionX = new(0, 0.4, 0);
    private readonly ViewerSpring _scrollMotionY = new(0, 0.4, 0);
    private ZoomAnchor? _zoomAnchor;
    private Vector? _navigationOffset;
    private bool _viewerFrameRequested;
    private TimeSpan? _lastViewerFrame;
    private int _viewerFrameGeneration;
    private int _zoomAnchorRevision;
    private bool _settingViewerOffset;
    private IPointer? _viewerPanPointer;
    private MouseButton _viewerPanButton;
    private Point _viewerPanStart;
    private Vector _viewerPanOffset;
    private bool _viewerSpaceHeld;
    private static readonly Cursor ViewerPanCursor = new(StandardCursorType.SizeAll);

    private readonly record struct ZoomAnchor(int PageIndex, Point PointInPage, Point ViewportPoint);

    private double TargetZoom => _zoomMotion.IsAnimating ? _zoomMotion.Target : _zoom;
    private bool IsViewerScrollAnimating => _scrollMotionX.IsAnimating || _scrollMotionY.IsAnimating;

    private void ZoomIn() => ApplyZoomLevel(TargetZoom * ZoomFactor);
    private void ZoomOut() => ApplyZoomLevel(TargetZoom / ZoomFactor);

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
        ChangeViewerZoom(Math.Clamp(ZoomSlider.Value / 100.0, ZoomMin, ZoomMax), null, animate: false);
    }

    private void MainScrollViewer_OnPointerWheelChanged(object? sender, PointerWheelEventArgs e)
    {
        if (e.Handled || string.IsNullOrEmpty(_currentPdfPath) || LoadingOverlay.IsVisible || IsPdfTextEditor(e.Source as Visual)) return;
        if (e.KeyModifiers.HasFlag(KeyModifiers.Control))
        {
            if (e.Delta.Y == 0) return;
            // Direct gestures follow the pointer; only toolbar zooms use a spring.
            ChangeViewerZoom(_zoom * Math.Exp(e.Delta.Y * 0.22), e.GetPosition(MainScrollViewer), animate: false);
            e.Handled = true;
        }
        else
        {
            StopViewerZoomMotion();
            StopViewerScrollMotion();
        }
    }

    private void ApplyZoomLevel(double newZoom) => ChangeViewerZoom(newZoom, null, animate: true);

    private void ChangeViewerZoom(double newZoom, Point? anchorPoint, bool animate)
    {
        if (_disposed || string.IsNullOrEmpty(_currentPdfPath) || _pageSlots.Count == 0 || LoadingOverlay.IsVisible || !double.IsFinite(newZoom)) return;
        newZoom = Math.Clamp(newZoom, ZoomMin, ZoomMax);
        if (Math.Abs(newZoom - TargetZoom) < 0.001 && (animate || !_zoomMotion.IsAnimating)) return;

        StopViewerScrollMotion();
        StopPdfTextDrag();
        SyncAllAnnotationModelsFromUi();
        MainScrollViewer.UpdateLayout();
        var point = anchorPoint ?? new Point(MainScrollViewer.Viewport.Width / 2, MainScrollViewer.Viewport.Height / 2);
        if (!TryGetPdfTextPoint(point, true, out var page, out var pagePoint)) return;
        var anchor = new ZoomAnchor(page, pagePoint, point);
        _zoomAnchor = anchor;
        ++_zoomAnchorRevision;
        ++_renderGeneration;
        InvalidateAllPageBitmaps(keepVisible: true);
        SetViewerZoomLabel(newZoom);

        if (animate && VisualRoot != null && IsEffectivelyVisible)
        {
            if (!_zoomMotion.IsAnimating) _lastViewerFrame = null;
            _zoomMotion.AnimateTo(_zoom, newZoom);
            RequestViewerAnimationFrame();
        }
        else
        {
            _zoomMotion.Jump(newZoom);
            ApplyViewerZoomFrame(newZoom, anchor);
            CompleteViewerZoom(anchor);
        }
    }

    private void SetViewerZoomLabel(double zoom)
    {
        _suppressSliderZoomSchedule = true;
        try { ZoomSlider.Value = Math.Round(zoom * 100); }
        finally { _suppressSliderZoomSchedule = false; }
        UpdateZoomLabel();
    }

    private void ApplyViewerZoomFrame(double zoom, ZoomAnchor anchor)
    {
        _suspendScrollPageTracking++;
        try
        {
            _zoom = Math.Clamp(zoom, ZoomMin, ZoomMax);
            _loader.SetDisplayZoom(_zoom, DisplayDpiHelper.GetDpiScale(this));
            ApplyZoomLayoutInPlace();
            // Both layout passes must finish before painting: otherwise pages/annotations above
            // the anchor can flash at the old offset. Keep the old bitmap until its replacement arrives.
            MainScrollViewer.UpdateLayout();
            RestoreViewerZoomAnchor(anchor);
            MainScrollViewer.UpdateLayout();
        }
        finally { _suspendScrollPageTracking--; }
        UpdateCurrentPageIndexFromViewport();
        ReleaseFramesOutsideVisible();
        SetStatus($"Page {_currentPageIndex + 1} / {_loader.PageCount} - {(int)Math.Round(_zoom * 100)}%");
    }

    private void RestoreViewerZoomAnchor(ZoomAnchor anchor)
    {
        if (anchor.PageIndex >= _pageSlots.Count) return;
        var host = _pageSlots[anchor.PageIndex].Host;
        var local = new Point(host.BorderThickness.Left + anchor.PointInPage.X * _loader.DisplayScale,
            host.BorderThickness.Top + anchor.PointInPage.Y * _loader.DisplayScale);
        if (host.TranslatePoint(local, MainScrollViewer) is { } point)
            SetViewerOffset(MainScrollViewer.Offset + (point - anchor.ViewportPoint));
    }

    private void CompleteViewerZoom(ZoomAnchor anchor)
    {
        _zoomAnchor = null;
        SetViewerZoomLabel(_zoom);
        var generation = _renderGeneration;
        var revision = _zoomAnchorRevision;
        // Retain the old deferred-layout safety net, but never undo a newer zoom or user scroll.
        Dispatcher.UIThread.Post(() =>
        {
            if (_disposed || generation != _renderGeneration || revision != _zoomAnchorRevision) return;
            _suspendScrollPageTracking++;
            try
            {
                RestoreViewerZoomAnchor(anchor);
                MainScrollViewer.UpdateLayout();
            }
            finally { _suspendScrollPageTracking--; }
        }, DispatcherPriority.Loaded);
        RequestVirtualizationImmediate();
    }

    private void StopViewerZoomMotion(bool render = true)
    {
        ++_zoomAnchorRevision;
        if (!_zoomMotion.IsAnimating) return;
        _zoomMotion.Jump(_zoom);
        _zoomAnchor = null;
        SetViewerZoomLabel(_zoom);
        if (render && !_disposed) RequestVirtualizationImmediate();
    }

    private Vector ClampViewerOffset(Vector offset) => new(
        Math.Clamp(offset.X, 0, Math.Max(0, MainScrollViewer.Extent.Width - MainScrollViewer.Viewport.Width)),
        Math.Clamp(offset.Y, 0, Math.Max(0, MainScrollViewer.Extent.Height - MainScrollViewer.Viewport.Height)));

    private void SetViewerOffset(Vector offset)
    {
        var wasSetting = _settingViewerOffset;
        _settingViewerOffset = true;
        try { MainScrollViewer.Offset = ClampViewerOffset(offset); }
        finally { _settingViewerOffset = wasSetting; }
    }

    private void AnimateViewerScrollTo(Vector offset)
    {
        if (_disposed || _pageSlots.Count == 0) return;
        StopViewerZoomMotion();
        MainScrollViewer.UpdateLayout();
        var target = ClampViewerOffset(offset);
        _navigationOffset = target;
        if (VisualRoot == null || !IsEffectivelyVisible)
        {
            _scrollMotionX.Jump(target.X);
            _scrollMotionY.Jump(target.Y);
            SetViewerOffset(target);
            RequestVirtualizationImmediate();
            return;
        }
        if (!_zoomMotion.IsAnimating && !IsViewerScrollAnimating) _lastViewerFrame = null;
        _scrollMotionX.AnimateTo(MainScrollViewer.Offset.X, target.X);
        _scrollMotionY.AnimateTo(MainScrollViewer.Offset.Y, target.Y);
        RequestViewerAnimationFrame();
    }

    private void StopViewerScrollMotion()
    {
        _scrollMotionX.Jump(MainScrollViewer.Offset.X);
        _scrollMotionY.Jump(MainScrollViewer.Offset.Y);
        _navigationOffset = null;
    }

    private void RequestViewerAnimationFrame()
    {
        if (_viewerFrameRequested || _disposed || TopLevel.GetTopLevel(this) is not { } topLevel) return;
        _viewerFrameRequested = true;
        var generation = _viewerFrameGeneration;
        topLevel.RequestAnimationFrame(timestamp =>
        {
            if (generation != _viewerFrameGeneration) return;
            _viewerFrameRequested = false;
            OnViewerAnimationFrame(timestamp);
        });
    }

    private void OnViewerAnimationFrame(TimeSpan timestamp)
    {
        if (_disposed || _pageSlots.Count == 0) return;
        var dt = _lastViewerFrame is { } last ? (timestamp - last).TotalSeconds : 1.0 / 60;
        _lastViewerFrame = timestamp;
        dt = dt <= 0 ? 1.0 / 60 : Math.Min(dt, 0.25);
        var snap = !IsEffectivelyVisible;
        if (_zoomMotion.IsAnimating && _zoomAnchor is { } anchor)
        {
            if (snap) _zoomMotion.Jump(_zoomMotion.Target);
            else _zoomMotion.Advance(dt);
            ApplyViewerZoomFrame(_zoomMotion.Value, anchor);
            if (!_zoomMotion.IsAnimating) CompleteViewerZoom(anchor);
        }
        if (IsViewerScrollAnimating)
        {
            if (snap)
            {
                _scrollMotionX.Jump(_scrollMotionX.Target);
                _scrollMotionY.Jump(_scrollMotionY.Target);
            }
            else
            {
                _scrollMotionX.Advance(dt);
                _scrollMotionY.Advance(dt);
            }
            SetViewerOffset(new Vector(_scrollMotionX.Value, _scrollMotionY.Value));
            MainScrollViewer.UpdateLayout();
        }
        // The current navigation page may be far away. Only mount the actual viewport, not
        // every page between it and the destination; native rendering is deferred during zoom.
        UnloadOffscreenPages();
        RequestVirtualizationImmediate();
        if (_zoomMotion.IsAnimating || IsViewerScrollAnimating) RequestViewerAnimationFrame();
        else _lastViewerFrame = null;
    }

    private void InitializeViewerMotion()
    {
        MainScrollViewer.PropertyChanged += (_, e) =>
        {
            if (_settingViewerOffset || _suspendScrollPageTracking > 0) return;
            if (e.Property == ScrollViewer.OffsetProperty || e.Property == ScrollViewer.ViewportProperty)
            {
                StopViewerZoomMotion();
                StopViewerScrollMotion();
            }
        };
        MainScrollViewer.AddHandler(PointerPressedEvent, OnViewerPanPressed, RoutingStrategies.Tunnel);
        MainScrollViewer.AddHandler(PointerMovedEvent, OnViewerPanMoved, RoutingStrategies.Tunnel);
        MainScrollViewer.AddHandler(PointerReleasedEvent, OnViewerPanReleased, RoutingStrategies.Tunnel);
        MainScrollViewer.PointerCaptureLost += (_, _) => EndViewerPan();
        AddHandler(KeyDownEvent, OnViewerMotionKeyDown, RoutingStrategies.Tunnel);
        AddHandler(KeyUpEvent, OnViewerMotionKeyUp, RoutingStrategies.Tunnel);
        LostFocus += (_, _) =>
        {
            if (TopLevel.GetTopLevel(this)?.FocusManager?.GetFocusedElement() is Visual focused &&
                !IsPdfTextEditor(focused) &&
                (focused == this || focused == MainScrollViewer || focused.GetVisualAncestors().Contains(MainScrollViewer))) return;
            _viewerSpaceHeld = false;
            EndViewerPan();
        };
        DetachedFromVisualTree += (_, _) => ResetViewerMotion();
        AttachedToVisualTree += (_, _) => RequestVirtualizationImmediate();
    }

    private void OnViewerMotionKeyDown(object? sender, KeyEventArgs e)
    {
        if (LoadingOverlay.IsVisible || _pageSlots.Count == 0 || e.Handled) return;
        var source = e.Source as Visual;
        if (IsPdfTextEditor(source))
        {
            _viewerSpaceHeld = false;
            EndViewerPan();
            StopViewerZoomMotion();
            return;
        }
        var inViewport = source == this || source == MainScrollViewer || source?.GetVisualAncestors().Contains(MainScrollViewer) == true;
        if (!inViewport)
        {
            _viewerSpaceHeld = false;
            EndViewerPan();
            return;
        }
        if (e.Key == Key.Space && e.KeyModifiers == KeyModifiers.None)
        {
            _viewerSpaceHeld = true;
            e.Handled = true;
        }
        else if (e.Key is Key.Up or Key.Down or Key.Left or Key.Right or Key.PageUp or Key.PageDown or Key.Home or Key.End)
        {
            StopViewerZoomMotion();
            StopViewerScrollMotion();
        }
    }

    private void OnViewerMotionKeyUp(object? sender, KeyEventArgs e)
    {
        if (e.Key != Key.Space || !_viewerSpaceHeld) return;
        _viewerSpaceHeld = false;
        var source = e.Source as Visual;
        if (!IsPdfTextEditor(source) &&
            (source == this || source == MainScrollViewer || source?.GetVisualAncestors().Contains(MainScrollViewer) == true))
            e.Handled = true;
    }

    private void OnViewerPanPressed(object? sender, PointerPressedEventArgs e)
    {
        if (e.Handled || LoadingOverlay.IsVisible || _pageSlots.Count == 0) return;
        StopViewerZoomMotion();
        StopViewerScrollMotion();
        var point = e.GetCurrentPoint(MainScrollViewer);
        if (IsPdfTextEditor(e.Source as Visual) || (e.Source as Visual)?.GetVisualAncestors().OfType<ScrollBar>().Any() == true || e.Source is ScrollBar) return;
        if (!point.Properties.IsMiddleButtonPressed && !(_viewerSpaceHeld && point.Properties.IsLeftButtonPressed && e.Pointer.Type != PointerType.Touch)) return;
        StopPdfTextDrag();
        Focus();
        _viewerPanPointer = e.Pointer;
        _viewerPanButton = point.Properties.IsMiddleButtonPressed ? MouseButton.Middle : MouseButton.Left;
        _viewerPanStart = point.Position;
        _viewerPanOffset = MainScrollViewer.Offset;
        MainScrollViewer.Cursor = ViewerPanCursor;
        e.Pointer.Capture(MainScrollViewer);
        e.Handled = true;
    }

    private void OnViewerPanMoved(object? sender, PointerEventArgs e)
    {
        if (_viewerPanPointer != e.Pointer) return;
        SetViewerOffset(_viewerPanOffset - (e.GetPosition(MainScrollViewer) - _viewerPanStart));
        MainScrollViewer.UpdateLayout();
        e.Handled = true;
    }

    private void OnViewerPanReleased(object? sender, PointerReleasedEventArgs e)
    {
        if (_viewerPanPointer != e.Pointer || e.InitialPressMouseButton != _viewerPanButton) return;
        EndViewerPan();
        RequestVirtualizationImmediate();
        e.Handled = true;
    }

    private void EndViewerPan()
    {
        var pointer = _viewerPanPointer;
        _viewerPanPointer = null;
        if (pointer == null) return;
        MainScrollViewer.Cursor = null;
        pointer.Capture(null);
    }

    private void ResetViewerMotion()
    {
        ++_viewerFrameGeneration;
        _viewerFrameRequested = false;
        _lastViewerFrame = null;
        _viewerSpaceHeld = false;
        EndViewerPan();
        StopViewerZoomMotion(render: false);
        StopViewerScrollMotion();
    }

    // Arc's closed-form springs keep velocity when retargeted. Only its two viewer presets
    // are needed here, not the generic MotionValue/scheduler or the Arc control library.
    private sealed class ViewerSpring(double initial, double visualDuration, double bounce)
    {
        private readonly double _frequency = 2 * Math.PI / (1.2 * visualDuration);
        private readonly double _dampingRatio = Math.Clamp(1 - bounce, 0.05, 1);
        private double _startDisplacement, _startVelocity, _elapsed;
        public double Value { get; private set; } = initial;
        public double Target { get; private set; } = initial;
        public double Velocity { get; private set; }
        public bool IsAnimating { get; private set; }

        public void AnimateTo(double from, double target)
        {
            if (IsAnimating && Target == target) return;
            if (!IsAnimating) Jump(from);
            Target = target;
            _startDisplacement = Value - target;
            _startVelocity = Velocity;
            _elapsed = 0;
            IsAnimating = _startDisplacement != 0 || _startVelocity != 0;
        }

        public void Jump(double value)
        {
            Value = Target = value;
            Velocity = 0;
            IsAnimating = false;
        }

        public void Advance(double dt)
        {
            if (!IsAnimating) return;
            _elapsed += dt;
            var w = _frequency;
            double x, v;
            if (Math.Abs(_dampingRatio - 1) < 1e-6)
            {
                var e = Math.Exp(-w * _elapsed);
                var b = _startVelocity + w * _startDisplacement;
                x = e * (_startDisplacement + b * _elapsed);
                v = e * (b - w * (_startDisplacement + b * _elapsed));
            }
            else
            {
                var z = _dampingRatio;
                var wd = w * Math.Sqrt(1 - z * z);
                var e = Math.Exp(-z * w * _elapsed);
                var b = (_startVelocity + z * w * _startDisplacement) / wd;
                var cos = Math.Cos(wd * _elapsed);
                var sin = Math.Sin(wd * _elapsed);
                x = e * (_startDisplacement * cos + b * sin);
                v = e * ((b * wd - z * w * _startDisplacement) * cos - (_startDisplacement * wd + z * w * b) * sin);
            }
            Value = Target + x;
            Velocity = v;
            var granular = Math.Abs(_startDisplacement) < 5;
            if (Math.Abs(x) <= (granular ? 0.005 : 0.5) && Math.Abs(v) <= (granular ? 0.01 : 2)) Jump(Target);
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
