using Avalonia;
using Avalonia.Controls;
using PdfViewerControl.Helpers;
using PdfViewerControl.Controls;
using PdfViewerControl.Controls.Annotation;
namespace PdfViewerControl.Controls.Annotation;
internal sealed class AnnotationResizeSession
{
    private readonly Canvas _host;
    private readonly FreeTextAnnotationControl _target;
    private readonly AnnotationResizeHandleKind _kind;
    private Point _startMouseInHost;
    private double _startLeft, _startTop, _startWidth, _startHeight;
    private bool _active;
    public AnnotationResizeSession(Canvas host, FreeTextAnnotationControl target, AnnotationResizeHandleKind kind, Point startMouseInHost)
    {
        _host = host; _target = target; _kind = kind; _startMouseInHost = startMouseInHost;
        _startLeft = Canvas.GetLeft(target); _startTop = Canvas.GetTop(target);
        if (double.IsNaN(_startLeft)) _startLeft = 0; if (double.IsNaN(_startTop)) _startTop = 0;
        var size = AnnotationAdornerService.GetAnnotationSize(target);
        _startWidth = size.Width;
        _startHeight = size.Height;
    }
    public void Begin() { _active = true; }
    public void Move(Point mouseInHost)
    {
        if (!_active) return;
        var dx = mouseInHost.X - _startMouseInHost.X; var dy = mouseInHost.Y - _startMouseInHost.Y;
        var minW = _target.MinWidth > 0 ? _target.MinWidth : 4; var minH = _target.MinHeight > 0 ? _target.MinHeight : 4;
        var left = _startLeft; var top = _startTop; var width = _startWidth; var height = _startHeight;
        switch (_kind)
        {
            case AnnotationResizeHandleKind.East: width = Math.Max(minW, _startWidth + dx); break;
            case AnnotationResizeHandleKind.West: width = Math.Max(minW, _startWidth - dx); left = _startLeft + (_startWidth - width); break;
            case AnnotationResizeHandleKind.South: height = Math.Max(minH, _startHeight + dy); break;
            case AnnotationResizeHandleKind.North: height = Math.Max(minH, _startHeight - dy); top = _startTop + (_startHeight - height); break;
            case AnnotationResizeHandleKind.SouthEast: width = Math.Max(minW, _startWidth + dx); height = Math.Max(minH, _startHeight + dy); break;
            case AnnotationResizeHandleKind.SouthWest: width = Math.Max(minW, _startWidth - dx); left = _startLeft + (_startWidth - width); height = Math.Max(minH, _startHeight + dy); break;
            case AnnotationResizeHandleKind.NorthEast: width = Math.Max(minW, _startWidth + dx); height = Math.Max(minH, _startHeight - dy); top = _startTop + (_startHeight - height); break;
            case AnnotationResizeHandleKind.NorthWest: width = Math.Max(minW, _startWidth - dx); left = _startLeft + (_startWidth - width); height = Math.Max(minH, _startHeight - dy); top = _startTop + (_startHeight - height); break;
        }
        Canvas.SetLeft(_target, left); Canvas.SetTop(_target, top); _target.Width = width; _target.Height = height;
        AnnotationAdornerService.SyncAdornerPosition(_target);
        _host.InvalidateArrange();
    }
    public bool CommitIfActive() { if (!_active) return false; _active = false; return true; }
    public void Cancel() { if (!_active) return; _active = false; Canvas.SetLeft(_target, _startLeft); Canvas.SetTop(_target, _startTop); _target.Width = _startWidth; _target.Height = _startHeight; }
}
