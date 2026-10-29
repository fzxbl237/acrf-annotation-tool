using AcrfAnnotationTool.Controls.Menus;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Media;

namespace PatChes.Controls.DataGrid;

internal sealed class ErrorFilterIcon : PathIcon
{
    public static readonly StyledProperty<bool> IsActiveProperty =
        AvaloniaProperty.Register<ErrorFilterIcon, bool>(nameof(IsActive));

    private static readonly Point[] s_filterPoints =
    [
        new(3, 4),
        new(21, 4),
        new(14, 12),
        new(14, 20),
        new(10, 18),
        new(10, 12)
    ];

    private static readonly Point[] s_warningPoints =
    [
        new(4.205771, 7.5),
        new(12, 3),
        new(19.794229, 7.5),
        new(19.794229, 16.5),
        new(12, 21),
        new(4.205771, 16.5)
    ];

    private static readonly Color s_filterColor = Color.FromRgb(0x59, 0x59, 0x59);
    private static readonly Color s_warningColor = Color.FromRgb(0xD4, 0x38, 0x0D);
    private readonly MotionValue<double> _morph;

    static ErrorFilterIcon()
    {
        WidthProperty.OverrideDefaultValue<ErrorFilterIcon>(20);
        HeightProperty.OverrideDefaultValue<ErrorFilterIcon>(20);
        FocusableProperty.OverrideDefaultValue<ErrorFilterIcon>(false);
        IsHitTestVisibleProperty.OverrideDefaultValue<ErrorFilterIcon>(false);
    }

    public ErrorFilterIcon()
    {
        _morph = new MotionValue<double>(this, 0, MotionVectors.Double);
        _morph.Changed += _ => InvalidateVisual();
    }

    public bool IsActive
    {
        get => GetValue(IsActiveProperty);
        set => SetValue(IsActiveProperty, value);
    }

    protected override void OnPropertyChanged(AvaloniaPropertyChangedEventArgs change)
    {
        base.OnPropertyChanged(change);
        if (change.Property != IsActiveProperty)
        {
            return;
        }

        var target = IsActive ? 1 : 0;
        if (TopLevel.GetTopLevel(this) is not null)
        {
            _ = _morph.AnimateTo(target, MotionSpec.Spring(ArcSpring.Morph));
        }
        else
        {
            _morph.Jump(target);
        }
    }

    protected override void OnDetachedFromVisualTree(VisualTreeAttachmentEventArgs e)
    {
        _morph.Jump(IsActive ? 1 : 0);
        base.OnDetachedFromVisualTree(e);
    }

    protected override Size MeasureOverride(Size availableSize) => new(20, 20);

    public override void Render(DrawingContext context)
    {
        var bounds = Bounds;
        if (bounds.Width <= 0 || bounds.Height <= 0)
        {
            return;
        }

        var scale = Math.Min(bounds.Width / 24, bounds.Height / 24);
        var offsetX = (bounds.Width - 24 * scale) / 2;
        var offsetY = (bounds.Height - 24 * scale) / 2;
        var progress = _morph.Value;
        var colorProgress = Math.Clamp(progress, 0, 1);
        var color = Color.FromRgb(
            Lerp(s_filterColor.R, s_warningColor.R, colorProgress),
            Lerp(s_filterColor.G, s_warningColor.G, colorProgress),
            Lerp(s_filterColor.B, s_warningColor.B, colorProgress));
        var brush = new SolidColorBrush(color);
        var pen = new Pen(brush, 1.8 * scale, null, PenLineCap.Round, PenLineJoin.Round);

        var vertices = new Point[s_filterPoints.Length];
        for (var i = 0; i < vertices.Length; i++)
        {
            var from = s_filterPoints[i];
            var to = s_warningPoints[i];
            vertices[i] = Map(
                new Point(
                    from.X + (to.X - from.X) * progress,
                    from.Y + (to.Y - from.Y) * progress),
                scale,
                offsetX,
                offsetY);
        }

        context.DrawGeometry(null, pen, CreateOutline(vertices, progress, scale));

        var exclamationOpacity = Math.Clamp(progress, 0, 1);
        if (exclamationOpacity > 0.001)
        {
            using (context.PushOpacity(exclamationOpacity))
            {
                context.DrawLine(
                    pen,
                    Map(new Point(8.5, 8.5), scale, offsetX, offsetY),
                    Map(new Point(15.5, 15.5), scale, offsetX, offsetY));
                context.DrawLine(
                    pen,
                    Map(new Point(15.5, 8.5), scale, offsetX, offsetY),
                    Map(new Point(8.5, 15.5), scale, offsetX, offsetY));
            }
        }
    }

    private static StreamGeometry CreateOutline(Point[] points, double progress, double scale)
    {
        var geometry = new StreamGeometry();
        using (var path = geometry.Open())
        {
            path.BeginFigure(points[0], false);
            var handle = 12 * Math.Tan(Math.PI / 12) * scale;
            for (var i = 0; i < points.Length; i++)
            {
                var start = points[i];
                var end = points[(i + 1) % points.Length];
                var angle = (-150 + i * 60) * Math.PI / 180;
                var nextAngle = angle + Math.PI / 3;
                var straight1 = start + (end - start) / 3;
                var straight2 = start + (end - start) * (2.0 / 3);
                var curved1 = start + new Vector(-Math.Sin(angle), Math.Cos(angle)) * handle;
                var curved2 = end - new Vector(-Math.Sin(nextAngle), Math.Cos(nextAngle)) * handle;
                path.CubicBezierTo(
                    straight1 + (curved1 - straight1) * progress,
                    straight2 + (curved2 - straight2) * progress,
                    end);
            }
            path.EndFigure(true);
        }
        return geometry;
    }

    private static Point Map(Point point, double scale, double offsetX, double offsetY) =>
        new(offsetX + point.X * scale, offsetY + point.Y * scale);

    private static byte Lerp(byte from, byte to, double progress) =>
        (byte)Math.Clamp(Math.Round(from + (to - from) * progress), 0, 255);
}
