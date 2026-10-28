namespace Dock.Controls;

public enum DockLayout
{
    LeftRight,
    TopBottom,
    Floating
}

public sealed record DockLayoutState(
    DockLayout Layout = DockLayout.LeftRight,
    DockLayout LastDocked = DockLayout.LeftRight,
    DockSide Side = DockSide.Right,
    double HorizontalRatio = 0.5,
    double VerticalRatio = 0.5,
    bool IsPaneOpen = true,
    int? FloatingX = null,
    int? FloatingY = null,
    double FloatingWidth = 340,
    double FloatingHeight = 360,
    bool FloatingMaximized = false);

public enum DockSide
{
    Left,
    Right,
    Top,
    Bottom
}
