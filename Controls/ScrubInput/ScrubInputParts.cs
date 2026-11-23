using Avalonia;
using Avalonia.Automation;
using Avalonia.Automation.Peers;
using Avalonia.Automation.Provider;
using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Media;

namespace AcrfAnnotationTool.Controls;

/// <summary>
/// The shell layer behind a <see cref="ScrubInput"/>'s row: a rounded surface with a 1px edge and a track wash scaled
/// from the left by the value fraction. Drawn in <see cref="Render"/> so the fill glides without relayout.
/// </summary>
internal sealed class ScrubInputShell : Control
{
    public static readonly StyledProperty<IBrush?> ShellBackgroundProperty =
        AvaloniaProperty.Register<ScrubInputShell, IBrush?>(nameof(ShellBackground));

    public static readonly StyledProperty<IBrush?> ShellBorderBrushProperty =
        AvaloniaProperty.Register<ScrubInputShell, IBrush?>(nameof(ShellBorderBrush));

    public static readonly StyledProperty<IBrush?> TrackBrushProperty =
        AvaloniaProperty.Register<ScrubInputShell, IBrush?>(nameof(TrackBrush));

    public static readonly StyledProperty<CornerRadius> CornerRadiusProperty =
        AvaloniaProperty.Register<ScrubInputShell, CornerRadius>(nameof(CornerRadius));

    private IPen? _pen;
    private double _fraction;
    private bool _isTrackVisible;

    static ScrubInputShell()
    {
        AffectsRender<ScrubInputShell>(ShellBackgroundProperty, ShellBorderBrushProperty, TrackBrushProperty, CornerRadiusProperty);
    }

    public IBrush? ShellBackground { get => GetValue(ShellBackgroundProperty); set => SetValue(ShellBackgroundProperty, value); }

    public IBrush? ShellBorderBrush { get => GetValue(ShellBorderBrushProperty); set => SetValue(ShellBorderBrushProperty, value); }

    public IBrush? TrackBrush { get => GetValue(TrackBrushProperty); set => SetValue(TrackBrushProperty, value); }

    public CornerRadius CornerRadius { get => GetValue(CornerRadiusProperty); set => SetValue(CornerRadiusProperty, value); }

    /// <summary>Track fill fraction 0..1.</summary>
    public double Fraction
    {
        get => _fraction;
        set
        {
            if (_fraction != value)
            {
                _fraction = value;
                InvalidateVisual();
            }
        }
    }

    public bool IsTrackVisible
    {
        get => _isTrackVisible;
        set
        {
            if (_isTrackVisible != value)
            {
                _isTrackVisible = value;
                InvalidateVisual();
            }
        }
    }

    protected override void OnPropertyChanged(AvaloniaPropertyChangedEventArgs change)
    {
        base.OnPropertyChanged(change);
        if (change.Property == ShellBorderBrushProperty)
        {
            _pen = ShellBorderBrush is { } brush ? new Pen(brush, 1) : null;
        }
    }

    public override void Render(DrawingContext context)
    {
        var width = Bounds.Width;
        var height = Bounds.Height;
        if (width <= 0 || height <= 0)
        {
            return;
        }
        var outer = new Rect(0, 0, width, height);
        var radius = Math.Min(CornerRadius.TopLeft, height / 2);
        context.DrawRectangle(ShellBackground, null, new RoundedRect(outer, radius));
        if (_isTrackVisible && _fraction > 0 && TrackBrush is { } track)
        {
            var inner = outer.Deflate(1);
            using (context.PushClip(new RoundedRect(inner, Math.Max(0, radius - 1))))
            {
                context.FillRectangle(track, new Rect(inner.X, inner.Y, inner.Width * Math.Min(1, _fraction), inner.Height));
            }
        }
        if (_pen is not null)
        {
            context.DrawRectangle(null, _pen, new RoundedRect(outer.Deflate(0.5), Math.Max(0, radius - 0.5)));
        }
    }
}

/// <summary>
/// The number editor of a <see cref="ScrubInput"/>: a frameless <see cref="TextBox"/>. Arrow keys, Enter and Escape go
/// to the owner before the text box sees them.
/// </summary>
internal sealed class ScrubInputEditor : TextBox
{
    protected override Type StyleKeyOverride => typeof(ScrubInputEditor);

    internal ScrubInput? Owner => TemplatedParent as ScrubInput;

    protected override void OnKeyDown(KeyEventArgs e)
    {
        Owner?.HandleEditorKeyDown(e);
        if (e.Handled)
        {
            return;
        }
        base.OnKeyDown(e);
    }

    protected override AutomationPeer OnCreateAutomationPeer() =>
        Owner is { HasFiniteRange: true } ? new ScrubInputEditorRangeAutomationPeer(this) : new ScrubInputEditorAutomationPeer(this);
}

/// <summary>The editor as a spin button: name from the label, value text as help text, invalid as status.</summary>
internal class ScrubInputEditorAutomationPeer : TextBoxAutomationPeer
{
    public ScrubInputEditorAutomationPeer(ScrubInputEditor owner) : base(owner)
    {
    }

    protected ScrubInput? Input => ((ScrubInputEditor)Owner).Owner;

    protected override AutomationControlType GetAutomationControlTypeCore() => AutomationControlType.Spinner;

    protected override string? GetNameCore()
    {
        var own = AutomationProperties.GetName(Owner);
        if (!string.IsNullOrEmpty(own))
        {
            return own;
        }
        return Input?.AutomationName ?? base.GetNameCore();
    }

    protected override string? GetHelpTextCore()
    {
        var input = Input;
        if (input is null)
        {
            return base.GetHelpTextCore();
        }
        var help = AutomationProperties.GetHelpText(input);
        return string.IsNullOrEmpty(help) ? input.ValueText : help;
    }

    protected override string? GetItemStatusCore() => Input is { IsInvalid: true } ? "Invalid entry" : base.GetItemStatusCore();
}

/// <summary>Adds the finite range to the spin button.</summary>
internal sealed class ScrubInputEditorRangeAutomationPeer : ScrubInputEditorAutomationPeer, IRangeValueProvider
{
    public ScrubInputEditorRangeAutomationPeer(ScrubInputEditor owner) : base(owner)
    {
    }

    bool IRangeValueProvider.IsReadOnly => Input is not { IsEffectivelyEnabled: true };

    public double Minimum => Input?.Minimum ?? 0;

    public double Maximum => Input?.Maximum ?? 0;

    double IRangeValueProvider.Value => Input?.Value ?? 0;

    public double SmallChange => Input?.Step ?? 1;

    public double LargeChange => Input?.EffectiveCoarseStep ?? 10;

    void IRangeValueProvider.SetValue(double value) => Input?.SetValueFromAutomation(value);
}
