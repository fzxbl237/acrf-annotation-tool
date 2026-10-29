using Avalonia;
using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Controls.Shapes;
using Avalonia.Media;
using PdfViewerControl.Controls;
namespace PdfViewerControl.Controls.Annotation;
public sealed class AnnotationResizeCompletedEventArgs(FreeTextAnnotationControl annotation) : EventArgs
{
    public FreeTextAnnotationControl Annotation { get; } = annotation;
}

public sealed class AnnotationDragCompletedEventArgs(FreeTextAnnotationControl annotation) : EventArgs
{
    public FreeTextAnnotationControl Annotation { get; } = annotation;
}

internal sealed class BracketCalloutOverlay : Canvas
{
    private readonly FreeTextAnnotationControl _annotation;
    private readonly Avalonia.Controls.Shapes.Path _bracket = new() { Stroke = Brushes.Red, Cursor = new Cursor(StandardCursorType.SizeAll) };
    private readonly Line _connector = new() { Stroke = Brushes.Red, IsHitTestVisible = false };
    private readonly Ellipse _upper = new() { Width = 10, Height = 10, Fill = Brushes.DodgerBlue, Stroke = Brushes.White, StrokeThickness = 1, Cursor = new Cursor(StandardCursorType.SizeNorthSouth) };
    private readonly Ellipse _lower = new() { Width = 10, Height = 10, Fill = Brushes.DodgerBlue, Stroke = Brushes.White, StrokeThickness = 1, Cursor = new Cursor(StandardCursorType.SizeNorthSouth) };
    private Control? _active;
    private Point _start;
    private double _x, _y1, _y2, _width;

    public BracketCalloutOverlay(PageAnnotationCanvas host, FreeTextAnnotationControl annotation)
    {
        _annotation = annotation;
        Width = host.Width; Height = host.Height;
        ClipToBounds = false;
        Children.Add(_connector);
        Children.Add(_bracket);
        Children.Add(_upper);
        Children.Add(_lower);
        foreach (var control in new Control[] { _bracket, _upper, _lower })
        {
            control.PointerPressed += (_, e) =>
            {
                if (!annotation.IsAnnotationEditingEnabled || !e.GetCurrentPoint(host).Properties.IsLeftButtonPressed || annotation.Model is not { } model) return;
                if (annotation.IsInEditMode) annotation.EndEdit();
                annotation.IsSelected = true;
                _active = control;
                _start = e.GetPosition(host);
                _x = model.CalloutX; _y1 = Math.Max(model.CalloutY1, model.CalloutY2); _y2 = Math.Min(model.CalloutY1, model.CalloutY2);
                _width = double.IsFinite(model.CalloutWidth) ? Math.Max(1, model.CalloutWidth) : 40;
                e.Pointer.Capture(control);
                e.Handled = true;
            };
            control.PointerMoved += (_, e) =>
            {
                if (_active != control || annotation.Model is not { } model) return;
                var delta = e.GetPosition(host) - _start;
                var scale = annotation.DisplayScale;
                if (control == _bracket)
                {
                    model.CalloutX = Math.Clamp(_x + delta.X / scale, 0, host.Width / scale);
                    var dy = -delta.Y / scale;
                    model.CalloutY1 = _y1 + dy; model.CalloutY2 = _y2 + dy;
                }
                else if (model.EffectiveCalloutShape is "TopBracket" or "BottomBracket")
                {
                    if (control == _upper)
                    {
                        var dx = Math.Min(delta.X / scale, _width - 1);
                        model.CalloutX = _x + dx;
                        model.CalloutWidth = _width - dx;
                    }
                    else
                        model.CalloutWidth = Math.Max(1, _width + delta.X / scale);
                }
                else if (control == _upper)
                    model.CalloutY1 = Math.Max(model.CalloutY2 + 1, _y1 - delta.Y / scale);
                else
                {
                    model.CalloutY2 = Math.Min(model.CalloutY1 - 1, _y2 - delta.Y / scale);
                    if (model.EffectiveCalloutShape == "Rectangle")
                        model.CalloutWidth = Math.Max(1, _width + delta.X / scale);
                }
                UpdatePosition();
                e.Handled = true;
            };
            control.PointerReleased += (_, e) =>
            {
                if (_active != control) return;
                _active = null;
                e.Pointer.Capture(null);
                annotation.NotifyResizeCommitted();
                e.Handled = true;
            };
            control.PointerCaptureLost += (_, _) =>
            {
                if (_active != control) return;
                _active = null;
                annotation.NotifyResizeCommitted();
            };
        }
    }

    public void UpdatePosition()
    {
        if (_annotation.Model is not { } model) return;
        if (Parent is Canvas host) { Width = host.Width; Height = host.Height; }
        var scale = _annotation.DisplayScale;
        var x = model.CalloutX * scale;
        var top = (_annotation.PageHeightPoints - Math.Max(model.CalloutY1, model.CalloutY2)) * scale;
        var height = Math.Abs(model.CalloutY1 - model.CalloutY2) * scale;
        var liveModel = model.Clone();
        var size = AnnotationAdornerService.GetAnnotationSize(_annotation);
        liveModel.PdfX = GetLeft(_annotation) / scale;
        liveModel.PdfY = _annotation.PageHeightPoints - (GetTop(_annotation) + size.Height) / scale;
        liveModel.Width = size.Width / scale;
        liveModel.Height = size.Height / scale;
        var geometry = liveModel.GetCalloutGeometry();
        Point ToScreen(Point point) => new(point.X * scale, (_annotation.PageHeightPoints - point.Y) * scale);
        var tail = new StreamGeometry();
        using (var context = tail.Open())
        {
            context.BeginFigure(ToScreen(geometry.Tail[0]), isFilled: false);
            foreach (var point in geometry.Tail.Skip(1)) context.LineTo(ToScreen(point));
            context.EndFigure(isClosed: false);
        }
        _bracket.Data = tail;
        _bracket.StrokeThickness = scale;
        _bracket.IsHitTestVisible = _annotation.IsAnnotationEditingEnabled;
        _connector.StartPoint = ToScreen(geometry.Start);
        _connector.EndPoint = ToScreen(geometry.Anchor);
        _connector.StrokeThickness = scale;
        _upper.IsVisible = _lower.IsVisible = _annotation.IsSelected && _annotation.IsAnnotationEditingEnabled;
        if (model.EffectiveCalloutShape is "TopBracket" or "BottomBracket")
        {
            var left = ToScreen(geometry.Tail[1]);
            var right = ToScreen(geometry.Tail[2]);
            _upper.Cursor = _lower.Cursor = new Cursor(StandardCursorType.SizeWestEast);
            SetLeft(_upper, left.X - 5); SetTop(_upper, left.Y - 5);
            SetLeft(_lower, right.X - 5); SetTop(_lower, right.Y - 5);
        }
        else
        {
            var resizeWidth = model.EffectiveCalloutShape == "Rectangle";
            _upper.Cursor = new Cursor(StandardCursorType.SizeNorthSouth);
            _lower.Cursor = new Cursor(resizeWidth ? StandardCursorType.BottomRightCorner : StandardCursorType.SizeNorthSouth);
            SetLeft(_upper, x - 5); SetTop(_upper, top - 5);
            SetLeft(_lower, x + (resizeWidth ? Math.Max(1, model.CalloutWidth) * scale : 0) - 5);
            SetTop(_lower, top + height - 5);
        }
    }
}

public sealed class PageAnnotationCanvas : Canvas
{
    private AnnotationDragSession? _drag;
    private AnnotationResizeSession? _resize;
    private AnnotationResizeHandleKind? _activeResizeHandle;
    private FreeTextAnnotationControl? _dragTarget, _resizeTarget, _pressTarget, _hoveredAnnotation;
    private Point? _pressPosition;
    public PageAnnotationCanvas()
    {
        Background = Brushes.Transparent;
        ClipToBounds = false;
        DetachedFromVisualTree += (_, _) => AnnotationAdornerService.CleanupCanvas(this);
    }
    public event EventHandler<AnnotationDragCompletedEventArgs>? AnnotationDragCompleted;
    public event EventHandler<AnnotationResizeCompletedEventArgs>? AnnotationResizeCompleted;
    private readonly Dictionary<FreeTextAnnotationControl, BracketCalloutOverlay> _callouts = new();
    public void HookAnnotation(FreeTextAnnotationControl ann)
    {
        ann.PointerPressed += OnAnnotationPointerPressed;
        ann.PropertyChanged += OnAnnotationLayoutChanged;
        RefreshCallout(ann);
    }
    private void OnAnnotationLayoutChanged(object? sender, AvaloniaPropertyChangedEventArgs e)
    {
        if (sender is FreeTextAnnotationControl ann &&
            (e.Property == LeftProperty || e.Property == TopProperty || e.Property == WidthProperty ||
             e.Property == HeightProperty || e.Property == FreeTextAnnotationControl.IsSelectedProperty ||
             e.Property == FreeTextAnnotationControl.IsAnnotationEditingEnabledProperty))
            RefreshCallout(ann);
    }
    internal void RefreshCallout(FreeTextAnnotationControl ann)
    {
        if (ann.Model?.CalloutEnabled != true)
        {
            if (_callouts.Remove(ann, out var old)) Children.Remove(old);
            return;
        }
        if (!_callouts.TryGetValue(ann, out var overlay) || !Children.Contains(overlay))
        {
            overlay = new BracketCalloutOverlay(this, ann);
            _callouts[ann] = overlay;
            Children.Insert(0, overlay);
        }
        overlay.UpdatePosition();
    }
    public void UnhookAnnotation(FreeTextAnnotationControl ann)
    {
        ann.PointerPressed -= OnAnnotationPointerPressed;
        ann.PropertyChanged -= OnAnnotationLayoutChanged;
        if (_callouts.Remove(ann, out var overlay)) Children.Remove(overlay);
        AnnotationAdornerService.Cleanup(ann);
    }
    private void OnAnnotationPointerPressed(object? sender, PointerPressedEventArgs e)
    {
        if (sender is not FreeTextAnnotationControl ann || ann.IsInEditMode) return;
        if (e.ClickCount >= 2 || !e.GetCurrentPoint(this).Properties.IsLeftButtonPressed) return;
        var pos = e.GetPosition(this);
        if (AnnotationAdornerService.TryGetResizeHandle(ann, pos) is { } hk)
        {
            BeginResize(ann, hk, pos);
            e.Handled = true;
            return;
        }
        ann.IsSelected = true;
        _pressTarget = ann;
        _pressPosition = pos;
        e.Handled = true;
    }

    /// <summary>
    /// Handle presses that land on a resize handle. Handles are drawn just outside the
    /// annotation bounds (the overlay is hit-test transparent), so such presses reach the
    /// canvas directly instead of the annotation and must be routed here.
    /// </summary>
    protected override void OnPointerPressed(PointerPressedEventArgs e)
    {
        base.OnPointerPressed(e);
        var pos = e.GetPosition(this);
        foreach (var ann in Children.OfType<FreeTextAnnotationControl>().Reverse())
        {
            if (ann.IsInEditMode)
            {
                var left = Canvas.GetLeft(ann); var top = Canvas.GetTop(ann);
                if (double.IsNaN(left)) left = 0; if (double.IsNaN(top)) top = 0;
                var size = AnnotationAdornerService.GetAnnotationSize(ann);
                var w = size.Width;
                var h = size.Height;
                if (w > 0 && h > 0 && pos.X >= left && pos.X <= left + w && pos.Y >= top && pos.Y <= top + h)
                    return;
                ann.EndEdit();
                continue;
            }
            if (e.ClickCount >= 2) continue;
            if (AnnotationAdornerService.TryGetResizeHandle(ann, pos) is { } hk)
            {
                BeginResize(ann, hk, pos);
                e.Handled = true;
                return;
            }
        }
    }

    private void BeginResize(FreeTextAnnotationControl ann, AnnotationResizeHandleKind kind, Point pos)
    {
        ann.IsSelected = true;
        _resizeTarget = ann;
        _activeResizeHandle = kind;
        _resize = new AnnotationResizeSession(this, ann, kind, pos);
        _resize.Begin();
        Cursor = AnnotationResizeCursors.ForHandle(kind);
    }
    protected override void OnPointerMoved(PointerEventArgs e)
    {
        base.OnPointerMoved(e);
        var pos = e.GetPosition(this);
        if (_resize != null)
        {
            if (e.GetCurrentPoint(this).Properties.IsLeftButtonPressed) { _resize.Move(pos); e.Handled = true; }
            return;
        }
        if (_drag != null)
        {
            Cursor = new Cursor(StandardCursorType.SizeAll);
            if (e.GetCurrentPoint(this).Properties.IsLeftButtonPressed) { _drag.Move(pos); e.Handled = true; }
            return;
        }
        UpdateHover(pos);
        if (_pressTarget == null || _pressPosition == null || !e.GetCurrentPoint(this).Properties.IsLeftButtonPressed) return;
        var dx = pos.X - _pressPosition.Value.X;
        var dy = pos.Y - _pressPosition.Value.Y;
        if (dx * dx + dy * dy < 9) return;
        _dragTarget = _pressTarget;
        _drag = new AnnotationDragSession(this, _dragTarget);
        _drag.Begin(_pressPosition.Value);
        _drag.Move(pos);
        _pressTarget = null;
        _pressPosition = null;
        e.Handled = true;
    }
    protected override void OnPointerReleased(PointerReleasedEventArgs e)
    {
        base.OnPointerReleased(e);
        _pressTarget = null;
        _pressPosition = null;
        if (_resize != null)
        {
            var resize = _resize;
            var resizedAnnotation = _resizeTarget;
            if (resize.CommitIfActive()) resizedAnnotation?.NotifyResizeCommitted();
            _resize = null;
            _resizeTarget = null;
            _activeResizeHandle = null;
            if (resizedAnnotation is { } annotation)
                AnnotationResizeCompleted?.Invoke(this, new AnnotationResizeCompletedEventArgs(annotation));
            e.Handled = true;
            return;
        }
        if (_drag == null) return;
        var dragTarget = _dragTarget;
        if (dragTarget != null && _drag.CommitIfActive())
            AnnotationDragCompleted?.Invoke(this, new AnnotationDragCompletedEventArgs(dragTarget));
        _drag = null;
        _dragTarget = null;
        e.Handled = true;
    }
    private void UpdateHover(Point pos)
    {
        FreeTextAnnotationControl? hit = null;
        AnnotationResizeHandleKind? handle = null;
        foreach (var ann in Children.OfType<FreeTextAnnotationControl>().Reverse())
        {
            if (ann.IsInEditMode) continue;
            var left = Canvas.GetLeft(ann); var top = Canvas.GetTop(ann);
            if (double.IsNaN(left)) left = 0; if (double.IsNaN(top)) top = 0;
            var size = AnnotationAdornerService.GetAnnotationSize(ann);
            var w = size.Width;
            var h = size.Height;
            var rh = AnnotationAdornerService.TryGetResizeHandle(ann, pos);
            if (rh != null) { hit = ann; handle = rh; break; }
            if (w > 0 && h > 0 && pos.X >= left && pos.X <= left + w && pos.Y >= top && pos.Y <= top + h)
            { hit = ann; handle = null; break; }
        }
        if (handle != null) Cursor = AnnotationResizeCursors.ForHandle(handle.Value);
        else if (hit != null) Cursor = new Cursor(StandardCursorType.SizeAll);
        else Cursor = new Cursor(StandardCursorType.Arrow);

        if (_hoveredAnnotation != hit)
        {
            if (_hoveredAnnotation != null) AnnotationAdornerService.SetPointerOver(_hoveredAnnotation, false);
            _hoveredAnnotation = hit;
            if (hit != null) AnnotationAdornerService.SetPointerOver(hit, true);
        }
    }

    protected override void OnPointerEntered(PointerEventArgs e)
    {
        base.OnPointerEntered(e);
        UpdateHover(e.GetPosition(this));
    }

    protected override void OnPointerExited(PointerEventArgs e)
    {
        base.OnPointerExited(e);
        if (_hoveredAnnotation != null)
        {
            AnnotationAdornerService.SetPointerOver(_hoveredAnnotation, false);
            _hoveredAnnotation = null;
        }
        Cursor = new Cursor(StandardCursorType.Arrow);
    }
}