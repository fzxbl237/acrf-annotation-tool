using Avalonia;
using Avalonia.Controls;
using PdfViewerControl.Helpers;
using PdfViewerControl.Controls;
using PdfViewerControl.Controls.Annotation;
namespace PdfViewerControl.Controls.Annotation;
internal sealed class AnnotationDragSession
{
    private readonly Canvas _host;
    private readonly FreeTextAnnotationControl _target;
    private Point _lastInHost;
    private bool _active;
    public AnnotationDragSession(Canvas host, FreeTextAnnotationControl target) { _host = host; _target = target; }
    public void Begin(Point positionInHost) { _active = true; _lastInHost = positionInHost; }
    public void Move(Point positionInHost)
    {
        if (!_active) return;
        var dx = positionInHost.X - _lastInHost.X; var dy = positionInHost.Y - _lastInHost.Y;
        _lastInHost = positionInHost;
        var left = Canvas.GetLeft(_target); var top = Canvas.GetTop(_target);
        if (double.IsNaN(left)) left = 0; if (double.IsNaN(top)) top = 0;
        left += dx; top += dy;
        var size = AnnotationAdornerService.GetAnnotationSize(_target);
        var pageWidth = _host.Width > 0 ? _host.Width : _host.Bounds.Width;
        var pageHeight = _host.Height > 0 ? _host.Height : _host.Bounds.Height;
        PdfCoordinateConverter.ClampAvaloniaRect(pageWidth, pageHeight, ref left, ref top, size.Width, size.Height);
        Canvas.SetLeft(_target, left); Canvas.SetTop(_target, top);
        AnnotationAdornerService.SyncAdornerPosition(_target);
        _host.InvalidateArrange();
    }
    public bool CommitIfActive() { if (!_active) return false; _active = false; return true; }
    public void Cancel() { _active = false; }
}
