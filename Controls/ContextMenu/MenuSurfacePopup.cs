using Avalonia;
using Avalonia.Controls;
using Avalonia.Controls.Primitives;
using Avalonia.Media;

namespace AcrfAnnotationTool.Controls.Menus;

// Use Avalonia's positioner and light-dismiss host, retaining Arc's context-menu spring.
internal sealed class MenuSurfacePopup : Popup
{
    private MotionValue<double>? _scale;
    private MotionValue<double>? _opacity;
    private readonly ScaleTransform _transform = new(1, 1);
    public bool IsMotionEnabled { get; set; } = true;
    internal Vector LastTravelDirection => default;

    public MenuSurfacePopup()
    {
        IsLightDismissEnabled = true;
        WindowManagerAddShadowHint = false;
        Opened += (_, _) => PlayEntrance();
        Closed += (_, _) => { _scale?.Jump(1); _opacity?.Jump(1); };
    }

    public void Configure(MenuSurfaceVariant variant, MenuSide side, MenuAlignment alignment, double gap, Point? point)
    {
        IsMotionEnabled = ArcMotion.IsFull(PlacementTarget ?? (AvaloniaObject)this);
        Placement = point is null ? PlacementMode.BottomEdgeAlignedLeft : PlacementMode.AnchorAndGravity;
        PlacementRect = point is { } p ? new Rect(p, new Size(1, 1)) : null;
        PlacementAnchor = Avalonia.Controls.Primitives.PopupPositioning.PopupAnchor.BottomLeft;
        PlacementGravity = Avalonia.Controls.Primitives.PopupPositioning.PopupGravity.BottomRight;
        PlacementConstraintAdjustment = Avalonia.Controls.Primitives.PopupPositioning.PopupPositionerConstraintAdjustment.All;
        VerticalOffset = point is null ? gap : 0;
        HorizontalOffset = 0;
    }

    public void Reposition()
    {
        var offset = VerticalOffset;
        VerticalOffset = offset + 1;
        VerticalOffset = offset;
    }

    private void PlayEntrance()
    {
        if (Child is not { } child) return;
        child.RenderTransformOrigin = new RelativePoint(0, 0, RelativeUnit.Relative);
        child.RenderTransform = _transform;
        _scale ??= new MotionValue<double>(child, 1, MotionVectors.Double);
        _opacity ??= new MotionValue<double>(child, 1, MotionVectors.Double);
        _scale.Changed += ApplyScale;
        _scale.Changed -= ApplyScale;
        _scale.Changed += ApplyScale;
        if (!IsMotionEnabled) { child.Opacity = 1; ApplyScale(1); return; }
        _opacity.Changed -= ApplyOpacity;
        _opacity.Changed += ApplyOpacity;
        _scale.Jump(0.96);
        _opacity.Jump(0);
        _ = _scale.AnimateTo(1, MotionSpec.Spring(ArcSpring.Snappy));
        _ = _opacity.AnimateTo(1, MotionSpec.Tween(ArcMotionPresets.Fast, ArcEasings.Enter));
    }

    private void ApplyScale(double value) { _transform.ScaleX = value; _transform.ScaleY = value; }
    private void ApplyOpacity(double value) { if (Child is { } child) child.Opacity = value; }
}
