using Avalonia;

namespace AcrfAnnotationTool.Controls.Menus;

public enum ArcMotionLevel { Off, Reduced, Full }

// Local motion policy; no dependency on Arc's theme-token registry.
public sealed class ArcMotion
{
    public static readonly AttachedProperty<ArcMotionLevel> LevelProperty =
        AvaloniaProperty.RegisterAttached<ArcMotion, AvaloniaObject, ArcMotionLevel>("Level", ArcMotionLevel.Full, inherits: true);

    public static ArcMotionLevel GetLevel(AvaloniaObject element) => element.GetValue(LevelProperty);
    public static void SetLevel(AvaloniaObject element, ArcMotionLevel value) => element.SetValue(LevelProperty, value);
    public static ArcMotionLevel GetEffectiveLevel(AvaloniaObject element) => GetLevel(element);
    public static bool IsFull(AvaloniaObject element) => GetEffectiveLevel(element) == ArcMotionLevel.Full;
}
