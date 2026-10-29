using Avalonia;
using Avalonia.Animation;
using Avalonia.Animation.Easings;
using Avalonia.Controls;
using Avalonia.Controls.Shapes;
using Avalonia.Media;
using Avalonia.Threading;
using Avalonia.VisualTree;
using System.Collections.Generic;
using System.Diagnostics;
using PdfViewerControl.Controls;

namespace PdfViewerControl.Controls.Annotation;

internal static class AnnotationAdornerService
{
    private static readonly Dictionary<FreeTextAnnotationControl, AnnotationOverlay> _overlays = new();
    private static readonly Dictionary<FreeTextAnnotationControl, bool> _pointerOver = new();
    // Last highlighted overlay per page canvas, so the frame can travel from it to the next target.
    // The overlay is stored, not a rect, because the frame keeps moving while the spring runs.
    private static readonly Dictionary<Canvas, AnnotationOverlay> _lastHighlights = new();

    public static void SetSelected(FreeTextAnnotationControl c, bool s) => UpdateAdornerState(c);
    internal static bool IsHighlighted(FreeTextAnnotationControl c) => c.IsSelected || _pointerOver.GetValueOrDefault(c);
    public static void UpdateAdornerState(FreeTextAnnotationControl c) { if (IsHighlighted(c)) EnsureVisible(c); else Hide(c); }
    public static void SetPointerOver(FreeTextAnnotationControl c, bool o) { if (o) _pointerOver[c] = true; else _pointerOver.Remove(c); UpdateAdornerState(c); }
    public static void Refresh(FreeTextAnnotationControl c) { if (c.IsSelected) EnsureVisible(c); }
    public static void RebindSelectedAdorner(FreeTextAnnotationControl c) => Refresh(c);
    public static void SyncAdornerLayerPosition(FreeTextAnnotationControl c) { PlaceOverlay(c); }
    public static void SyncAdornerPosition(FreeTextAnnotationControl c) { PlaceOverlay(c); }

    private static void PlaceOverlay(FreeTextAnnotationControl c)
    {
        if (!_overlays.TryGetValue(c, out var o)) return;
        o.UpdatePosition(c);
        RememberRect(c, o);
    }

    private static void RememberRect(FreeTextAnnotationControl c, AnnotationOverlay o)
    {
        if (c.Parent is Canvas parent)
            _lastHighlights[parent] = o;
    }

    private static FreeTextAnnotationControl? FindOwner(AnnotationOverlay overlay)
    {
        foreach (var pair in _overlays)
            if (ReferenceEquals(pair.Value, overlay)) return pair.Key;
        return null;
    }

    public static Size GetAnnotationSize(FreeTextAnnotationControl ann)
    {
        var w = double.IsFinite(ann.Width) && ann.Width > 0 ? ann.Width : ann.Bounds.Width;
        var h = double.IsFinite(ann.Height) && ann.Height > 0 ? ann.Height : ann.Bounds.Height;
        return new Size(Math.Max(0, w), Math.Max(0, h));
    }

    public static AnnotationResizeHandleKind? TryGetResizeHandle(FreeTextAnnotationControl ann, Point canvasPoint)
    {
        // Handles only exist on a selected annotation; moving a box never needs a selection.
        if (!ann.IsAnnotationEditingEnabled || !ann.IsSelected) return null;
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
        if (!c.IsLoaded) { c.AttachedToVisualTree += (_, _) => { if (IsHighlighted(c)) TryCreate(c); }; return; }
        TryCreate(c);
    }

    private static void TryCreate(FreeTextAnnotationControl c)
    {
        if (c.Parent is not Canvas parent) return;
        if (!parent.IsAttachedToVisualTree()) return;
        if (_overlays.TryGetValue(c, out var ex))
        {
            ex.RefreshHighlight(c);
            RememberRect(c, ex);
            return;
        }

        // The travelling frame appears instantly; only a highlight with no predecessor fades in.
        var travels = _lastHighlights.TryGetValue(parent, out var previous);
        var o = travels ? new AnnotationOverlay() : new AnnotationOverlay { Opacity = 0 };
        parent.Children.Add(o);
        _overlays[c] = o;
        if (travels && previous != null)
        {
            // Travel from where the previous highlight currently sits, matching the DataGrid
            // selection spring. Reading it live also continues a spring that is still running.
            var start = previous.FrameRect;
            var owner = FindOwner(previous);
            if (owner != null && !IsHighlighted(owner)) RemoveNow(owner);
            o.AnimateFrom(c, start);
            o.ShowHighlight(c);
        }
        else
        {
            o.UpdatePosition(c);
            Dispatcher.UIThread.Post(() =>
            {
                if (_overlays.TryGetValue(c, out var current) && ReferenceEquals(current, o)) o.ShowHighlight(c);
            }, DispatcherPriority.Render);
        }
        RememberRect(c, o);
    }

    private static void Hide(FreeTextAnnotationControl c)
    {
        if (!_overlays.TryGetValue(c, out var o)) return;
        // Fade out first; the overlay leaves the tree once the animation has finished.
        o.FadeOut(() =>
        {
            if (_overlays.TryGetValue(c, out var current) && !ReferenceEquals(current, o)) return;
            _overlays.Remove(c);
            if (o.Parent is Canvas p)
            {
                if (_lastHighlights.TryGetValue(p, out var remembered) && ReferenceEquals(remembered, o))
                    _lastHighlights.Remove(p);
                try { p.Children.Remove(o); } catch (InvalidOperationException) { }
            }
        });
    }

    private static void RemoveNow(FreeTextAnnotationControl c)
    {
        if (!_overlays.TryGetValue(c, out var o)) return;
        _overlays.Remove(c);
        o.CancelFade();
        if (o.Parent is Canvas p)
        {
            if (_lastHighlights.TryGetValue(p, out var remembered) && ReferenceEquals(remembered, o))
                _lastHighlights.Remove(p);
            try { p.Children.Remove(o); } catch (InvalidOperationException) { }
        }
    }

    public static void Cleanup(FreeTextAnnotationControl c)
    {
        RemoveNow(c);
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
        if (pointerKeys != null)
        {
            foreach (var c in pointerKeys)
                _pointerOver.Remove(c);
        }
        _lastHighlights.Remove(canvas);
    }

    /// <summary>Clear all overlay references without modifying visual tree (safe to call during detach).</summary>
    public static void CleanupAll()
    {
        _overlays.Clear();
        _pointerOver.Clear();
        _lastHighlights.Clear();
    }
}

internal sealed class AnnotationOverlay : Canvas
{
    private readonly Border _focusShadow;
    private readonly Rectangle _focusFrame;
    // Handles live on this frame, so the gap is shared with the resize hit-test.
    private const double FocusGap = AnnotationHandleGeometry.Pad;
    private readonly Border _selBorder;
    private readonly Ellipse[] _handles = new Ellipse[8];
    private const double HandleDiameter = 12;
    private static readonly TimeSpan HighlightFadeDuration = TimeSpan.FromMilliseconds(140);
    private readonly DispatcherTimer _fadeTimer;
    private Action? _fadeCompleted;

    // Frame rect in canvas coordinates: x, y, width, height.
    private readonly double[] _value = new double[4];
    private readonly double[] _velocity = new double[4];
    private readonly double[] _target = new double[4];
    private readonly DispatcherTimer _motionTimer = new() { Interval = TimeSpan.FromMilliseconds(16) };
    private long _lastMotionTick;
    private bool _showSelection;

    public AnnotationOverlay()
    {
        IsHitTestVisible = false; Background = Brushes.Transparent; ClipToBounds = false;
        // Hover and selection chrome stays outside the annotation and never intercepts input.
        _focusShadow = new Border
        {
            Background = Brushes.Transparent,
            CornerRadius = new CornerRadius(5),
            BoxShadow = BoxShadows.Parse("0 3 12 2 #48000000"),
            IsHitTestVisible = false
        };
        _focusFrame = new Rectangle
        {
            Stroke = new SolidColorBrush(Color.Parse("#FF747474")),
            StrokeThickness = 1.5,
            RadiusX = 5,
            RadiusY = 5,
            Fill = new SolidColorBrush(Color.Parse("#144A83C4")),
            IsHitTestVisible = false
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
        Transitions = new Transitions
        {
            new DoubleTransition
            {
                Property = OpacityProperty,
                Duration = HighlightFadeDuration,
                Easing = new CubicEaseOut()
            }
        };
        // Wait a frame past the transition so the overlay never disappears mid-fade.
        _fadeTimer = new DispatcherTimer { Interval = HighlightFadeDuration + TimeSpan.FromMilliseconds(30) };
        _fadeTimer.Tick += (_, _) =>
        {
            _fadeTimer.Stop();
            var completed = _fadeCompleted;
            _fadeCompleted = null;
            completed?.Invoke();
        };
        _motionTimer.Tick += OnMotionTick;
    }

    internal static Rect GetFrameRect(FreeTextAnnotationControl c)
    {
        var left = Canvas.GetLeft(c); var top = Canvas.GetTop(c);
        if (double.IsNaN(left)) left = 0; if (double.IsNaN(top)) top = 0;
        var size = AnnotationAdornerService.GetAnnotationSize(c);
        return new Rect(left - FocusGap, top - FocusGap,
            size.Width + FocusGap * 2, size.Height + FocusGap * 2);
    }

    /// <summary>Current animated frame in canvas coordinates.</summary>
    internal Rect FrameRect => new(_value[0], _value[1], Math.Max(0, _value[2]), Math.Max(0, _value[3]));

    public void ShowHighlight(FreeTextAnnotationControl c)
    {
        _fadeTimer.Stop();
        _fadeCompleted = null;
        _showSelection = c.IsSelected;
        ApplyFrame();
        Opacity = 1;
    }

    /// <summary>The same annotation gained selection while already hovered; no travel to do.</summary>
    public void RefreshHighlight(FreeTextAnnotationControl c)
    {
        // Cancel a pending fade-out: the highlight is wanted again.
        _fadeTimer.Stop();
        _fadeCompleted = null;
        _showSelection = c.IsSelected;
        // Snap only when idle; a running spring keeps its own path.
        if (_motionTimer.IsEnabled) ApplyFrame();
        else UpdatePosition(c);
        Opacity = 1;
    }

    /// <summary>Snap the frame onto the annotation; used while dragging and resizing.</summary>
    public void UpdatePosition(FreeTextAnnotationControl c)
    {
        CancelMotion();
        SetTarget(c);
        for (var i = 0; i < 4; i++) { _value[i] = _target[i]; _velocity[i] = 0; }
        ApplyFrame();
    }

    /// <summary>Travel from a previous highlight rect to the annotation with the DataGrid selection spring.</summary>
    public void AnimateFrom(FreeTextAnnotationControl c, Rect start)
    {
        SetTarget(c);
        if (Math.Abs(start.X - _target[0]) < 0.5 && Math.Abs(start.Y - _target[1]) < 0.5 &&
            Math.Abs(start.Width - _target[2]) < 0.5 && Math.Abs(start.Height - _target[3]) < 0.5)
        {
            for (var i = 0; i < 4; i++) { _value[i] = _target[i]; _velocity[i] = 0; }
            ApplyFrame();
            return;
        }
        _value[0] = start.X; _value[1] = start.Y; _value[2] = start.Width; _value[3] = start.Height;
        Array.Clear(_velocity);
        _lastMotionTick = Stopwatch.GetTimestamp();
        ApplyFrame();
        _motionTimer.Start();
    }

    private void SetTarget(FreeTextAnnotationControl c)
    {
        var rect = GetFrameRect(c);
        _target[0] = rect.X; _target[1] = rect.Y; _target[2] = rect.Width; _target[3] = rect.Height;
        _showSelection = c.IsSelected;
    }

    private void CancelMotion()
    {
        _motionTimer.Stop();
        Array.Clear(_velocity);
    }

    private void OnMotionTick(object? sender, EventArgs e)
    {
        var now = Stopwatch.GetTimestamp();
        var dt = Math.Max(0, Stopwatch.GetElapsedTime(_lastMotionTick, now).TotalSeconds);
        _lastMotionTick = now;
        // Arc Snappy: visualDuration = 0.26s, damping ratio 0.88. Same curve as DataGrid selection.
        const double frequency = 2 * Math.PI / (1.2 * 0.26);
        const double decay = 0.88 * frequency;
        var damped = frequency * Math.Sqrt(1 - 0.88 * 0.88);
        var exp = Math.Exp(-decay * dt);
        var cos = Math.Cos(damped * dt);
        var sin = Math.Sin(damped * dt);
        var moving = false;
        for (var i = 0; i < 4; i++)
        {
            var displacementValue = _value[i] - _target[i];
            var b = (_velocity[i] + decay * displacementValue) / damped;
            var displacement = exp * (displacementValue * cos + b * sin);
            _velocity[i] = exp * ((b * damped - decay * displacementValue) * cos -
                                  (displacementValue * damped + decay * b) * sin);
            _value[i] = _target[i] + displacement;
            moving |= Math.Abs(displacement) > 0.005 || Math.Abs(_velocity[i]) > 0.01;
        }
        if (!moving)
        {
            for (var i = 0; i < 4; i++) { _value[i] = _target[i]; _velocity[i] = 0; }
            _motionTimer.Stop();
        }
        ApplyFrame();
    }

    private void ApplyFrame()
    {
        var w = Math.Max(0, _value[2]);
        var h = Math.Max(0, _value[3]);
        Canvas.SetLeft(_focusShadow, _value[0]); Canvas.SetTop(_focusShadow, _value[1]);
        _focusShadow.Width = w; _focusShadow.Height = h;
        Canvas.SetLeft(_focusFrame, _value[0]); Canvas.SetTop(_focusFrame, _value[1]);
        _focusFrame.Width = w; _focusFrame.Height = h;

        // Resize chrome shares the outer frame, so handles sit on the same edge the hit-test uses.
        var showSelection = _showSelection && !_motionTimer.IsEnabled;
        // The blue resize border replaces the idle gray frame instead of drawing inside it.
        _focusFrame.IsVisible = !showSelection;
        _selBorder.IsVisible = showSelection;
        Canvas.SetLeft(_selBorder, _value[0]);
        Canvas.SetTop(_selBorder, _value[1]);
        _selBorder.Width = w;
        _selBorder.Height = h;
        var r = HandleDiameter / 2;
        void Sh(int i, double x, double y)
        {
            Canvas.SetLeft(_handles[i], _value[0] + x - r);
            Canvas.SetTop(_handles[i], _value[1] + y - r);
            _handles[i].IsVisible = showSelection;
        }
        Sh(0, 0, 0); Sh(1, w / 2, 0); Sh(2, w, 0);
        Sh(3, w, h / 2); Sh(4, w, h); Sh(5, w / 2, h);
        Sh(6, 0, h); Sh(7, 0, h / 2);
    }

    public void FadeOut(Action completed)
    {
        _fadeTimer.Stop();
        _fadeCompleted = completed;
        Opacity = 0;
        _fadeTimer.Start();
    }

    public void CancelFade()
    {
        _fadeTimer.Stop();
        _fadeCompleted = null;
        CancelMotion();
    }
}
