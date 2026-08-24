using Avalonia.Input;

namespace PdfViewerControl.Controls.Annotation;

internal static class AnnotationResizeCursors
{
    public static Cursor ForHandle(AnnotationResizeHandleKind kind) => kind switch
    {
        AnnotationResizeHandleKind.North or AnnotationResizeHandleKind.South => new Cursor(StandardCursorType.SizeNorthSouth),
        AnnotationResizeHandleKind.East or AnnotationResizeHandleKind.West => new Cursor(StandardCursorType.SizeWestEast),
        AnnotationResizeHandleKind.NorthWest => new Cursor(StandardCursorType.TopLeftCorner),
        AnnotationResizeHandleKind.NorthEast => new Cursor(StandardCursorType.TopRightCorner),
        AnnotationResizeHandleKind.SouthWest => new Cursor(StandardCursorType.BottomLeftCorner),
        AnnotationResizeHandleKind.SouthEast => new Cursor(StandardCursorType.BottomRightCorner),
        _ => new Cursor(StandardCursorType.SizeAll)
    };
}