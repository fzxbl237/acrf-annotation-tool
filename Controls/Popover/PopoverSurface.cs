using Avalonia;
using Avalonia.Automation.Peers;
using Avalonia.Controls;

namespace AcrfAnnotationTool.Controls;

/// <summary>The anchored floating surface of <see cref="Popover"/>: raised surface, border, panel radius and padding.</summary>
public class PopoverSurface : ContentControl
{
    public static readonly StyledProperty<Thickness> ShadowRoomProperty =
        AvaloniaProperty.Register<PopoverSurface, Thickness>(
            nameof(ShadowRoom),
            new Thickness(24, 16, 24, 32));

    /// <summary>Transparent space around the card that the drop shadow paints into.</summary>
    public Thickness ShadowRoom { get => GetValue(ShadowRoomProperty); set => SetValue(ShadowRoomProperty, value); }

    static PopoverSurface()
    {
        FocusableProperty.OverrideDefaultValue<PopoverSurface>(true);
    }

    protected override AutomationPeer OnCreateAutomationPeer() => new PopoverSurfaceAutomationPeer(this);

    private sealed class PopoverSurfaceAutomationPeer(Control owner) : ControlAutomationPeer(owner)
    {
        protected override AutomationControlType GetAutomationControlTypeCore() => AutomationControlType.Pane;

        protected override bool IsContentElementCore() => true;

        protected override bool IsControlElementCore() => true;
    }
}
