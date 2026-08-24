using Avalonia;
using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Media;
using PdfViewerControl.Controls;
namespace PdfViewerControl.Controls.Annotation;
public sealed class AnnotationDragCompletedEventArgs(FreeTextAnnotationControl annotation) : EventArgs
{
    public FreeTextAnnotationControl Annotation { get; } = annotation;
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
        DetachedFromVisualTree += (_, _) => AnnotationAdornerService.CleanupAll();
    }
    public event EventHandler<AnnotationDragCompletedEventArgs>? AnnotationDragCompleted;
    public event EventHandler? AnnotationResizeCompleted;
    public void HookAnnotation(FreeTextAnnotationControl ann) => ann.PointerPressed += OnAnnotationPointerPressed;
    public void UnhookAnnotation(FreeTextAnnotationControl ann) => ann.PointerPressed -= OnAnnotationPointerPressed;
    private void OnAnnotationPointerPressed(object? sender, PointerPressedEventArgs e)
    {
        if (sender is not FreeTextAnnotationControl ann || ann.IsInEditMode) return;
        if (e.ClickCount >= 2) return;
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
            if (_resize.CommitIfActive()) _resizeTarget?.NotifyResizeCommitted();
            _resize = null;
            _resizeTarget = null;
            _activeResizeHandle = null;
            AnnotationResizeCompleted?.Invoke(this, EventArgs.Empty);
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