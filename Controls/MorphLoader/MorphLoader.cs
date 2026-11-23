using System.Diagnostics;
using Avalonia;
using Avalonia.Automation;
using Avalonia.Automation.Peers;
using Avalonia.Controls;
using Avalonia.Controls.Documents;
using Avalonia.Controls.Metadata;
using Avalonia.Media;
using Avalonia.Threading;
using Avalonia.VisualTree;

namespace AcrfAnnotationTool.Controls;

/// <summary>
/// A tiny loader drawn from four strokes that morph between dots, bars, a ring and a square while loading, then fold into
/// a check (success) or a cross (error) with a small pop. Drive it with <see cref="Status"/> from your request state and
/// return it to Loading for the next request: the strokes gather back into the <see cref="Variant"/>. It inherits its color
/// from <see cref="Foreground"/> like text, takes the success or danger tone for a result (<see cref="Tone"/>), never
/// changes its footprint, and is announced as a polite status unless <see cref="IsDecorative"/> is set.
/// </summary>
[PseudoClasses(LoadingPseudoClass, SuccessPseudoClass, ErrorPseudoClass)]
public class MorphLoader : Control
{
    internal const string LoadingPseudoClass = ":loading";
    internal const string SuccessPseudoClass = ":success";
    internal const string ErrorPseudoClass = ":error";

    /// <summary>The loading shape. Changing it while loading morphs the strokes into the new shape.</summary>
    public static readonly StyledProperty<MorphLoaderVariant> VariantProperty =
        AvaloniaProperty.Register<MorphLoader, MorphLoaderVariant>(nameof(Variant));

    /// <summary>Loading loops the variant, Success folds the strokes into a check, Error into a cross.</summary>
    public static readonly StyledProperty<MorphLoaderStatus> StatusProperty =
        AvaloniaProperty.Register<MorphLoader, MorphLoaderStatus>(nameof(Status));

    /// <summary>The rendered side in px (24 by default). The drawing uses a 24 unit grid scaled to this size.</summary>
    public static readonly StyledProperty<double> SizeProperty =
        AvaloniaProperty.Register<MorphLoader, double>(nameof(Size), 24, validate: v => v >= 0 && !double.IsInfinity(v));

    /// <summary>The stroke thickness of the ring, square, check and cross in grid units (2.5 by default).</summary>
    public static readonly StyledProperty<double> StrokeWidthProperty =
        AvaloniaProperty.Register<MorphLoader, double>(nameof(StrokeWidth), 2.5, validate: v => v >= 0 && !double.IsInfinity(v));

    /// <summary>Colors the check with the success tone and the cross with the danger tone. Off keeps the foreground.</summary>
    public static readonly StyledProperty<bool> ToneProperty =
        AvaloniaProperty.Register<MorphLoader, bool>(nameof(Tone), true);

    /// <summary>
    /// Drops the status announcement and hides the loader from assistive technology, for loaders inside a control that
    /// announces its own state.
    /// </summary>
    public static readonly StyledProperty<bool> IsDecorativeProperty =
        AvaloniaProperty.Register<MorphLoader, bool>(nameof(IsDecorative));

    /// <summary>The color of the strokes while loading (inherited like text color).</summary>
    public static readonly StyledProperty<IBrush?> ForegroundProperty =
        TextElement.ForegroundProperty.AddOwner<MorphLoader>();

    /// <summary>The accessible names for loading, success and error.</summary>
    public static readonly StyledProperty<MorphLoaderLabels?> LabelsProperty =
        AvaloniaProperty.Register<MorphLoader, MorphLoaderLabels?>(nameof(Labels));

    internal static readonly StyledProperty<double> DotWidthProperty =
        AvaloniaProperty.Register<MorphLoader, double>(nameof(DotWidth), 4);

    internal static readonly StyledProperty<double> BarWidthProperty =
        AvaloniaProperty.Register<MorphLoader, double>(nameof(BarWidth), 3);

    // Arc reads these from its design tokens; the tones are fixed here so the control stays self-contained.
    internal static readonly StyledProperty<IBrush?> SuccessInkProperty =
        AvaloniaProperty.Register<MorphLoader, IBrush?>(nameof(SuccessInk), new SolidColorBrush(Color.Parse("#FF2E7D32")));

    internal static readonly StyledProperty<IBrush?> DangerInkProperty =
        AvaloniaProperty.Register<MorphLoader, IBrush?>(nameof(DangerInk), new SolidColorBrush(Color.Parse("#FFE5484D")));

    private static readonly MorphLoaderLabels s_defaultLabels = new();

    private readonly MorphLoaderEngine _engine = new();
    private readonly DispatcherTimer _timer;
    private readonly SolidColorBrush _ink = new(Colors.Black);
    private readonly Pen[] _pens = new Pen[MorphLoaderShapes.StrokeCount];
    private readonly Point[] _points = new Point[24];
    private readonly HashSet<Visual> _visibilityAncestors = [];

    private bool _animating;
    private long _lastFrameStamp;
    private bool _settledPending;

    static MorphLoader()
    {
        AffectsMeasure<MorphLoader>(SizeProperty);
        AffectsRender<MorphLoader>(ForegroundProperty, SuccessInkProperty, DangerInkProperty);
        FocusableProperty.OverrideDefaultValue<MorphLoader>(false);
        IsHitTestVisibleProperty.OverrideDefaultValue<MorphLoader>(false);
        HorizontalAlignmentProperty.OverrideDefaultValue<MorphLoader>(Avalonia.Layout.HorizontalAlignment.Center);
        VerticalAlignmentProperty.OverrideDefaultValue<MorphLoader>(Avalonia.Layout.VerticalAlignment.Center);
    }

    /// <summary>Creates a loader showing the dots loop.</summary>
    public MorphLoader()
    {
        _timer = new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(16) };
        _timer.Tick += OnTimerTick;
        for (var i = 0; i < _pens.Length; i++)
        {
            _pens[i] = new Pen(_ink, 1, lineCap: PenLineCap.Round);
        }
        UpdatePseudoClasses();
        UpdateAccessibility();
    }

    /// <summary>Raised once a result (Success or Error) has finished folding, popping and taking its tone.</summary>
    public event EventHandler<MorphLoaderSettledEventArgs>? Settled;

    /// <inheritdoc cref="VariantProperty"/>
    public MorphLoaderVariant Variant { get => GetValue(VariantProperty); set => SetValue(VariantProperty, value); }

    /// <inheritdoc cref="StatusProperty"/>
    public MorphLoaderStatus Status { get => GetValue(StatusProperty); set => SetValue(StatusProperty, value); }

    /// <inheritdoc cref="SizeProperty"/>
    public double Size { get => GetValue(SizeProperty); set => SetValue(SizeProperty, value); }

    /// <inheritdoc cref="StrokeWidthProperty"/>
    public double StrokeWidth { get => GetValue(StrokeWidthProperty); set => SetValue(StrokeWidthProperty, value); }

    /// <inheritdoc cref="ToneProperty"/>
    public bool Tone { get => GetValue(ToneProperty); set => SetValue(ToneProperty, value); }

    /// <inheritdoc cref="IsDecorativeProperty"/>
    public bool IsDecorative { get => GetValue(IsDecorativeProperty); set => SetValue(IsDecorativeProperty, value); }

    /// <inheritdoc cref="ForegroundProperty"/>
    public IBrush? Foreground { get => GetValue(ForegroundProperty); set => SetValue(ForegroundProperty, value); }

    /// <inheritdoc cref="LabelsProperty"/>
    public MorphLoaderLabels? Labels { get => GetValue(LabelsProperty); set => SetValue(LabelsProperty, value); }

    internal double DotWidth { get => GetValue(DotWidthProperty); set => SetValue(DotWidthProperty, value); }
    internal double BarWidth { get => GetValue(BarWidthProperty); set => SetValue(BarWidthProperty, value); }
    internal IBrush? SuccessInk { get => GetValue(SuccessInkProperty); set => SetValue(SuccessInkProperty, value); }
    internal IBrush? DangerInk { get => GetValue(DangerInkProperty); set => SetValue(DangerInkProperty, value); }

    // Test hooks.
    internal MorphLoaderEngine Engine => _engine;
    internal bool IsLooping => _timer.IsEnabled;
    internal Color InkColor => _ink.Color;

    /// <summary>The accessible name now: the label for the current status.</summary>
    internal string SpokenName => (Labels ?? s_defaultLabels).For(Status);

    /// <summary>Advances the motion by <paramref name="seconds"/> as one frame would (tests drive it with this).</summary>
    internal bool Advance(double seconds) => OnFrame(seconds);

    /// <inheritdoc />
    protected override Size MeasureOverride(Size availableSize)
    {
        var side = Size;
        return new Size(side, side);
    }

    /// <inheritdoc />
    protected override void OnPropertyChanged(AvaloniaPropertyChangedEventArgs change)
    {
        base.OnPropertyChanged(change);
        var property = change.Property;
        if (property == VariantProperty || property == StatusProperty)
        {
            var statusChanged = property == StatusProperty;
            if (Animates)
            {
                _engine.MorphTo(Variant, Status);
            }
            else
            {
                _engine.Jump(Variant, Status, false);
            }
            _settledPending = Status != MorphLoaderStatus.Loading && (statusChanged || _settledPending);
            UpdatePseudoClasses();
            if (statusChanged)
            {
                UpdateAccessibility();
            }
            AfterChange();
        }
        else if (property == StrokeWidthProperty)
        {
            _engine.SetWidths(change.GetNewValue<double>(), DotWidth, BarWidth);
            AfterChange();
        }
        else if (property == DotWidthProperty || property == BarWidthProperty)
        {
            RebuildEngine();
        }
        else if (property == ToneProperty || property == SuccessInkProperty || property == DangerInkProperty ||
                 property == ForegroundProperty)
        {
            UpdateInk();
            InvalidateVisual();
        }
        else if (property == LabelsProperty || property == IsDecorativeProperty)
        {
            UpdateAccessibility();
        }
        else if (property == IsVisibleProperty)
        {
            AfterChange();
        }
    }

    /// <inheritdoc />
    protected override void OnAttachedToVisualTree(VisualTreeAttachmentEventArgs e)
    {
        base.OnAttachedToVisualTree(e);
        HookVisibilityAncestors();
        _animating = true;
        _engine.Jump(Variant, Status, true);
        AfterChange();
    }

    /// <inheritdoc />
    protected override void OnDetachedFromVisualTree(VisualTreeAttachmentEventArgs e)
    {
        base.OnDetachedFromVisualTree(e);
        UnhookVisibilityAncestors();
        // Off screen nothing is shown: the next attach starts from the resting shape (the loop keeps its own phase).
        _animating = false;
        StopTimer();
        _engine.Jump(Variant, Status, false);
        FlushSettled();
    }

    // IsEffectivelyVisible has no property notification of its own, so watch the IsVisible of every ancestor.
    private void HookVisibilityAncestors()
    {
        UnhookVisibilityAncestors();
        for (var ancestor = this.GetVisualParent(); ancestor is not null; ancestor = ancestor.GetVisualParent())
        {
            _visibilityAncestors.Add(ancestor);
            ancestor.PropertyChanged += OnAncestorPropertyChanged;
        }
    }

    private void UnhookVisibilityAncestors()
    {
        foreach (var ancestor in _visibilityAncestors)
        {
            ancestor.PropertyChanged -= OnAncestorPropertyChanged;
        }
        _visibilityAncestors.Clear();
    }

    private void OnAncestorPropertyChanged(object? sender, AvaloniaPropertyChangedEventArgs e)
    {
        if (e.Property == IsVisibleProperty)
        {
            AfterChange();
        }
    }

    /// <inheritdoc />
    public override void Render(DrawingContext context)
    {
        var bounds = Bounds;
        var side = Math.Min(bounds.Width, bounds.Height);
        if (side <= 0)
        {
            return;
        }
        var brush = InkBrush;
        if (brush is null)
        {
            return;
        }
        var unit = side / MorphLoaderShapes.Grid;
        var c = MorphLoaderShapes.Center;
        var matrix = Matrix.CreateTranslation(-c, -c) *
                     Matrix.CreateRotation(_engine.Rotation * Math.PI / 180) *
                     Matrix.CreateScale(_engine.Scale, _engine.Scale) *
                     Matrix.CreateTranslation(c, c) *
                     Matrix.CreateScale(unit, unit) *
                     Matrix.CreateTranslation((bounds.Width - side) / 2, (bounds.Height - side) / 2);
        using (context.PushTransform(matrix))
        {
            var strokes = _engine.Strokes;
            for (var s = 0; s < strokes.Length; s++)
            {
                DrawStroke(context, strokes[s], _pens[s], brush);
            }
        }
    }

    private void DrawStroke(DrawingContext context, in MorphLoaderStroke stroke, Pen pen, IBrush brush)
    {
        if (stroke.Opacity <= 0.001 || stroke.Width <= 0.001)
        {
            return;
        }
        var length = Math.Max(MorphLoaderShapes.MinLength, stroke.Length);
        // A faded stroke is one layer, so the round joints of a sampled arc do not show through each other.
        DrawingContext.PushedState? fade = stroke.Opacity < 0.999 ? context.PushOpacity(stroke.Opacity) : null;
        try
        {
            if (length <= MorphLoaderShapes.MinLength + 1e-6)
            {
                // A zero-length stroke with round caps is a dot as wide as the stroke.
                var radius = stroke.Width / 2;
                context.DrawEllipse(brush, null, new Point(stroke.Cx, stroke.Cy), radius, radius);
                return;
            }
            if (!ReferenceEquals(pen.Brush, brush))
            {
                pen.Brush = brush;
            }
            if (pen.Thickness != stroke.Width)
            {
                pen.Thickness = stroke.Width;
            }
            // Straight strokes are one line; arcs are short lines with round caps (the caps make the joints round), which
            // needs no geometry per frame.
            var count = MorphLoaderShapes.Sample(stroke, length, _points);
            for (var i = 0; i < count - 1; i++)
            {
                context.DrawLine(pen, _points[i], _points[i + 1]);
            }
        }
        finally
        {
            fade?.Dispose();
        }
    }

    private bool Animates => _animating && IsVisible && AncestorsAreVisible();

    private bool AncestorsAreVisible()
    {
        for (var ancestor = this.GetVisualParent(); ancestor is not null; ancestor = ancestor.GetVisualParent())
        {
            if (!ancestor.IsVisible)
            {
                return false;
            }
        }
        return true;
    }

    /// <summary>The brush the strokes use now: the foreground itself at rest, the tone color for a result.</summary>
    private IBrush? InkBrush
    {
        get
        {
            if (!Toned && Foreground is { } foreground && foreground is not ISolidColorBrush)
            {
                return foreground;
            }
            return _ink;
        }
    }

    private bool Toned => Tone && Status != MorphLoaderStatus.Loading;

    private Color TargetColor
    {
        get
        {
            var brush = Toned ? (Status == MorphLoaderStatus.Success ? SuccessInk : DangerInk) ?? Foreground : Foreground;
            if (brush is ISolidColorBrush solid)
            {
                var color = solid.Color;
                return solid.Opacity >= 1 ? color : Color.FromArgb((byte)Math.Round(color.A * Math.Clamp(solid.Opacity, 0, 1)), color.R, color.G, color.B);
            }
            return Colors.Black;
        }
    }

    private void UpdateInk() => _ink.Color = TargetColor;

    private void RebuildEngine()
    {
        _engine.SetWidths(StrokeWidth, DotWidth, BarWidth);
        AfterChange();
    }

    private void AfterChange()
    {
        UpdateInk();
        UpdateLoop();
        if (!Animates)
        {
            FlushSettled();
        }
        InvalidateVisual();
    }

    private void UpdateLoop()
    {
        if (Animates && _engine.NeedsFrames)
        {
            StartTimer();
        }
        else
        {
            StopTimer();
        }
    }

    private void StartTimer()
    {
        if (_timer.IsEnabled)
        {
            return;
        }
        _lastFrameStamp = Stopwatch.GetTimestamp();
        _timer.Start();
    }

    private void StopTimer()
    {
        if (_timer.IsEnabled)
        {
            _timer.Stop();
        }
    }

    private void OnTimerTick(object? sender, EventArgs e)
    {
        if (!Animates)
        {
            StopTimer();
            return;
        }
        var now = Stopwatch.GetTimestamp();
        var seconds = Stopwatch.GetElapsedTime(_lastFrameStamp, now).TotalSeconds;
        _lastFrameStamp = now;
        if (!OnFrame(seconds))
        {
            StopTimer();
        }
    }

    private bool OnFrame(double seconds)
    {
        var running = _engine.Step(seconds);
        UpdateInk();
        InvalidateVisual();
        if (!_engine.IsSettling)
        {
            FlushSettled();
        }
        return running;
    }

    private void FlushSettled()
    {
        if (!_settledPending || Status == MorphLoaderStatus.Loading)
        {
            _settledPending = false;
            return;
        }
        _settledPending = false;
        Settled?.Invoke(this, new MorphLoaderSettledEventArgs(Status));
    }

    private void UpdatePseudoClasses()
    {
        var status = Status;
        PseudoClasses.Set(LoadingPseudoClass, status == MorphLoaderStatus.Loading);
        PseudoClasses.Set(SuccessPseudoClass, status == MorphLoaderStatus.Success);
        PseudoClasses.Set(ErrorPseudoClass, status == MorphLoaderStatus.Error);
    }

    /// <summary>
    /// A polite live region carries the label for the current status, so a change is read once; a decorative loader is
    /// hidden from assistive technology and says nothing.
    /// </summary>
    private void UpdateAccessibility()
    {
        var decorative = IsDecorative;
        AutomationProperties.SetAccessibilityView(this, decorative ? AccessibilityView.Raw : AccessibilityView.Content);
        AutomationProperties.SetLiveSetting(this, decorative ? AutomationLiveSetting.Off : AutomationLiveSetting.Polite);
        AutomationProperties.SetControlTypeOverride(this, AutomationControlType.Text);
        AutomationProperties.SetName(this, decorative ? string.Empty : SpokenName);
    }
}
