using Avalonia;
using Avalonia.Controls;
using Avalonia.Controls.Shapes;
using Avalonia.Media;
using Avalonia.VisualTree;
using System.Collections.Generic;
using PdfViewerControl.Controls;

namespace PdfViewerControl.Controls.Annotation;

internal static class AnnotationAdornerService
{
    private static readonly Dictionary<FreeTextAnnotationControl, AnnotationOverlay> _overlays = new();
    private static readonly Dictionary<FreeTextAnnotationControl, bool> _pointerOver = new();

    public static void SetSelected(FreeTextAnnotationControl c, bool s) { if (s) EnsureVisible(c); else Hide(c); }
    public static void UpdateAdornerState(FreeTextAnnotationControl c) { if (c.IsSelected || _pointerOver.GetValueOrDefault(c)) EnsureVisible(c); else Hide(c); }
    public static void SetPointerOver(FreeTextAnnotationControl c, bool o) { if (o) _pointerOver[c] = true; else _pointerOver.Remove(c); UpdateAdornerState(c); }
    public static void Refresh(FreeTextAnnotationControl c) { if (c.IsSelected) EnsureVisible(c); }
    public static void RebindSelectedAdorner(FreeTextAnnotationControl c) => Refresh(c);
    public static void SyncAdornerLayerPosition(FreeTextAnnotationControl c) { if (_overlays.TryGetValue(c, out var o)) o.UpdatePosition(c); }
    public static void SyncAdornerPosition(FreeTextAnnotationControl c) { if (_overlays.TryGetValue(c, out var o)) o.UpdatePosition(c); }

    public static Size GetAnnotationSize(FreeTextAnnotationControl ann)
    {
        var w = double.IsFinite(ann.Width) && ann.Width > 0 ? ann.Width : ann.Bounds.Width;
        var h = double.IsFinite(ann.Height) && ann.Height > 0 ? ann.Height : ann.Bounds.Height;
        return new Size(Math.Max(0, w), Math.Max(0, h));
    }

    public static AnnotationResizeHandleKind? TryGetResizeHandle(FreeTextAnnotationControl ann, Point canvasPoint)
    {
        if (!ann.IsAnnotationEditingEnabled) return null;
        var left = Canvas.GetLeft(ann); var top = Canvas.GetTop(ann);
        if (double.IsNaN(left)) left = 0; if (double.IsNaN(top)) top = 0;
        var size = GetAnnotationSize(ann);
        return AnnotationHandleGeometry.TryHit(left, top, size.Width, size.Height, canvasPoint, out var kind) ? kind : null;
    }

    /// <summary>Find the annotation whose body contains the given canvas point (ignoring resize handles).</summary>
    public static FreeTextAnnotationControl? FindAnnotationAtPoint(PageAnnotationCanvas canvas, Point pos)
    {
        foreach (var c in canvas.Children.OfType<FreeTextAnnotationControl>())
        {
            if (c.IsInEditMode) continue;
            var left = Canvas.GetLeft(c); var top = Canvas.GetTop(c);
            if (double.IsNaN(left)) left = 0; if (double.IsNaN(top)) top = 0;
            var size = GetAnnotationSize(c);
            var w = size.Width;
            var h = size.Height;
            if (w > 0 && h > 0 && pos.X >= left && pos.X <= left + w && pos.Y >= top && pos.Y <= top + h)
                return c;
        }
        return null;
    }

    private static void EnsureVisible(FreeTextAnnotationControl c)
    {
        if (!c.IsLoaded) { c.AttachedToVisualTree += (_, _) => { if (c.IsSelected || _pointerOver.GetValueOrDefault(c)) TryCreate(c); }; return; }
        TryCreate(c);
    }

    private static void TryCreate(FreeTextAnnotationControl c)
    {
        if (c.Parent is not Canvas parent) return;
        if (!parent.IsAttachedToVisualTree()) return;
        if (_overlays.TryGetValue(c, out var ex)) { ex.UpdatePosition(c); return; }
        var o = new AnnotationOverlay(); parent.Children.Add(o); _overlays[c] = o; o.UpdatePosition(c);
    }

    private static void Hide(FreeTextAnnotationControl c)
    {
        if (_overlays.TryGetValue(c, out var o))
        {
            if (o.Parent is Canvas p && p.IsAttachedToVisualTree())
            {
                try { p.Children.Remove(o); } catch (InvalidOperationException) { }
            }
            _overlays.Remove(c);
        }
    }

    public static void Cleanup(FreeTextAnnotationControl c)
    {
        Hide(c);
        _pointerOver.Remove(c);
    }

    /// <summary>Drop overlay tracking for a canvas without mutating its visual tree.</summary>
    public static void CleanupCanvas(Canvas canvas)
    {
        List<FreeTextAnnotationControl>? overlayKeys = null;
        foreach (var kv in _overlays)
        {
            if (ReferenceEquals(kv.Value.Parent, canvas) || ReferenceEquals(kv.Key.Parent, canvas))
                (overlayKeys ??= new List<FreeTextAnnotationControl>()).Add(kv.Key);
        }
        if (overlayKeys != null)
        {
            foreach (var c in overlayKeys)
            {
                _overlays.Remove(c);
                _pointerOver.Remove(c);
            }
        }

        List<FreeTextAnnotationControl>? pointerKeys = null;
        foreach (var c in _pointerOver.Keys)
        {
            if (ReferenceEquals(c.Parent, canvas))
                (pointerKeys ??= new List<FreeTextAnnotationControl>()).Add(c);
        }
        if (pointerKeys == null) return;
        foreach (var c in pointerKeys)
            _pointerOver.Remove(c);
    }

    /// <summary>Clear all overlay references without modifying visual tree (safe to call during detach).</summary>
    public static void CleanupAll()
    {
        _overlays.Clear();
        _pointerOver.Clear();
    }
}

internal sealed class AnnotationOverlay : Canvas
{
    private readonly Border _focusShadow;
    private readonly Rectangle _focusFrame;
    private const double FocusGap = 18;
    private readonly Border _selBorder;
    private readonly Ellipse[] _handles = new Ellipse[8];
    private const double HandleDiameter = 12;
    private const double SelectionGap = 4;
    private const double MarginOutside = 2;

    public AnnotationOverlay()
    {
        IsHitTestVisible = false; Background = Brushes.Transparent; ClipToBounds = false;
        // Selection-only chrome stays outside the annotation and never intercepts input.
        _focusShadow = new Border
        {
            Background = Brushes.Transparent,
            CornerRadius = new CornerRadius(5),
            BoxShadow = BoxShadows.Parse("0 3 12 2 #48000000"),
            IsHitTestVisible = false,
            IsVisible = false
        };
        _focusFrame = new Rectangle
        {
            Stroke = new SolidColorBrush(Color.Parse("#FF747474")),
            StrokeThickness = 1.5,
            RadiusX = 5,
            RadiusY = 5,
            Fill = new SolidColorBrush(Color.Parse("#144A83C4")),
            IsHitTestVisible = false,
            IsVisible = false
        };
        Children.Add(_focusShadow);
        Children.Add(_focusFrame);
        _selBorder = new Border
        {
            BorderBrush = new SolidColorBrush(Color.Parse("#FF4A83C4")),
            BorderThickness = new Thickness(1),
            Background = Brushes.Transparent,
            IsHitTestVisible = false
        };
        Children.Add(_selBorder);
        for (int i = 0; i < 8; i++)
        {
            var e = new Ellipse
            {
                Width = HandleDiameter, Height = HandleDiameter,
                Fill = new SolidColorBrush(Color.Parse("#FF4A83C4")),
                Stroke = Brushes.White, StrokeThickness = 2,
                IsHitTestVisible = false
            };
            _handles[i] = e;
            Children.Add(e);
        }
    }

    public void UpdatePosition(FreeTextAnnotationControl c)
    {
        var left = Canvas.GetLeft(c); var top = Canvas.GetTop(c);
        if (double.IsNaN(left)) left = 0; if (double.IsNaN(top)) top = 0;
        var size = AnnotationAdornerService.GetAnnotationSize(c);
        var w = size.Width;
        var h = size.Height;
        Canvas.SetLeft(this, left); Canvas.SetTop(this, top);
        _focusShadow.IsVisible = c.IsSelected;
        _focusFrame.IsVisible = c.IsSelected;
        Canvas.SetLeft(_focusShadow, -FocusGap); Canvas.SetTop(_focusShadow, -FocusGap);
        _focusShadow.Width = w + FocusGap * 2; _focusShadow.Height = h + FocusGap * 2;
        Canvas.SetLeft(_focusFrame, -FocusGap); Canvas.SetTop(_focusFrame, -FocusGap);
        _focusFrame.Width = w + FocusGap * 2; _focusFrame.Height = h + FocusGap * 2;
        Canvas.SetLeft(_selBorder, -SelectionGap); Canvas.SetTop(_selBorder, -SelectionGap);
        _selBorder.Width = w + SelectionGap * 2; _selBorder.Height = h + SelectionGap * 2;
        double ho = SelectionGap + MarginOutside, r = HandleDiameter / 2;
        void Sh(int i, double x, double y) { Canvas.SetLeft(_handles[i], x); Canvas.SetTop(_handles[i], y); }
        Sh(0, -ho - r, -ho - r); Sh(1, w / 2 - r, -ho - r); Sh(2, w + ho - r, -ho - r);
        Sh(3, w + ho - r, h / 2 - r); Sh(4, w + ho - r, h + ho - r); Sh(5, w / 2 - r, h + ho - r);
        Sh(6, -ho - r, h + ho - r); Sh(7, -ho - r, h / 2 - r);
    }
}
