using Avalonia;
using Avalonia.Media;

namespace PatChes.Controls.DataGrid;

internal class DrawnGeometry : Avalonia.Controls.Control
{
    public static readonly StyledProperty<string?> PathDataProperty =
        AvaloniaProperty.Register<DrawnGeometry, string?>(nameof(PathData));
    public static readonly StyledProperty<IBrush?> FillProperty =
        AvaloniaProperty.Register<DrawnGeometry, IBrush?>(nameof(Fill));

    private Avalonia.Controls.Image? _image;

    public string? PathData { get => GetValue(PathDataProperty); set => SetValue(PathDataProperty, value); }
    public IBrush? Fill { get => GetValue(FillProperty); set => SetValue(FillProperty, value); }

    protected override Size MeasureOverride(Size availableSize)
    {
        EnsureImage();
        if (_image != null) { _image.Measure(availableSize); return _image.DesiredSize; }
        return base.MeasureOverride(availableSize);
    }

    protected override Size ArrangeOverride(Size finalSize)
    {
        if (_image != null) { _image.Arrange(new Rect(finalSize)); return finalSize; }
        return base.ArrangeOverride(finalSize);
    }

    protected override void OnPropertyChanged(AvaloniaPropertyChangedEventArgs change)
    {
        base.OnPropertyChanged(change);
        if (change.Property == PathDataProperty || change.Property == FillProperty)
            UpdateImage();
    }

    private void EnsureImage()
    {
        if (_image != null) return;
        _image = new Avalonia.Controls.Image { Stretch = Stretch.Uniform };
        LogicalChildren.Clear();
        VisualChildren.Clear();
        LogicalChildren.Add(_image);
        VisualChildren.Add(_image);
        UpdateImage();
    }

    private void UpdateImage()
    {
        EnsureImage();
        if (_image == null) return;

        if (string.IsNullOrEmpty(PathData) || Fill == null)
        {
            _image.Source = null;
            return;
        }

        var geo = Geometry.Parse(PathData);
        var drawing = new GeometryDrawing();
        drawing.Geometry = geo;
        drawing.Brush = Fill;
        _image.Source = new DrawingImage(drawing);
    }
}