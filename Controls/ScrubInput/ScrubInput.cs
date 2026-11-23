using Avalonia;
using Avalonia.Automation;
using Avalonia.Controls;
using Avalonia.Controls.Metadata;
using Avalonia.Controls.Presenters;
using Avalonia.Controls.Primitives;
using Avalonia.Data;
using Avalonia.Input;
using Avalonia.Interactivity;
using Avalonia.Media;
using Avalonia.Threading;

namespace AcrfAnnotationTool.Controls;

/// <summary>What produced a <see cref="ScrubInput"/> value change.</summary>
public enum ScrubInputChangeSource
{
    /// <summary><see cref="ScrubInput.Value"/> was set from code or a binding (raises only <see cref="ScrubInput.ValueChanged"/>).</summary>
    Programmatic,

    /// <summary>A drag on the label (also a drag cancelled with Escape).</summary>
    Scrub,

    /// <summary>A typed entry committed with Enter, blur or assistive technology.</summary>
    Type,

    /// <summary>An ArrowUp / ArrowDown step.</summary>
    Key,

    /// <summary>A double click on the label returning to <see cref="ScrubInput.ResetValue"/>.</summary>
    Reset
}

/// <summary>Data of <see cref="ScrubInput.ValueChanged"/> and <see cref="ScrubInput.ValueCommitted"/>.</summary>
public sealed class ScrubInputValueChangedEventArgs : RoutedEventArgs
{
    public ScrubInputValueChangedEventArgs(RoutedEvent routedEvent, double oldValue, double newValue,
                                           ScrubInputChangeSource source, string unit)
        : base(routedEvent)
    {
        OldValue = oldValue;
        NewValue = newValue;
        ChangeSource = source;
        Unit = unit;
    }

    public double OldValue { get; }

    public double NewValue { get; }

    public ScrubInputChangeSource ChangeSource { get; }

    public string Unit { get; }
}

/// <summary>
/// A compact, Figma-style number field for inspectors and property panels. Drag the label sideways to scrub the value
/// (Shift ten times coarser, Alt ten times finer), or click in and type an exact value: entries accept arithmetic such
/// as <c>12*2</c> or <c>(100-24)/2</c> and a unit from <see cref="Units"/> such as <c>50%</c>. Arrow keys step, Enter
/// commits, Escape restores the last value, and a double click on the label restores <see cref="ResetValue"/>.
/// </summary>
[TemplatePart(ShellPart, typeof(ScrubInputShell))]
[TemplatePart(HandlePart, typeof(ContentPresenter))]
[TemplatePart(EditorPart, typeof(ScrubInputEditor))]
[TemplatePart(UnitPart, typeof(TextBlock))]
[PseudoClasses(ScrubbingPseudoClass, InvalidPseudoClass, HoverPseudoClass, HandleHoverPseudoClass, TextLabelPseudoClass, SmallPseudoClass)]
public class ScrubInput : TemplatedControl
{
    public const string SmallPseudoClass = ":small";
    public const string ScrubbingPseudoClass = ":scrubbing";
    public const string InvalidPseudoClass = ":invalid";
    public const string HoverPseudoClass = ":hover";
    public const string HandleHoverPseudoClass = ":handle-hover";
    public const string TextLabelPseudoClass = ":text-label";

    internal const string ShellPart = "PART_Shell";
    internal const string HandlePart = "PART_Handle";
    internal const string EditorPart = "PART_Editor";
    internal const string UnitPart = "PART_Unit";

    /// <summary>Pointer travel before a press on the label becomes a scrub.</summary>
    private const double Slop = 3;

    /// <summary>Stored overshoot cap in px, so reversing relaxes after a short way instead of a long walk back.</summary>
    private const double OvershootCap = 90;

    private static readonly TimeSpan InvalidDuration = TimeSpan.FromMilliseconds(900);

    #region Properties

    public static readonly StyledProperty<object?> LabelProperty =
        AvaloniaProperty.Register<ScrubInput, object?>(nameof(Label));

    public static readonly StyledProperty<double> ValueProperty =
        AvaloniaProperty.Register<ScrubInput, double>(nameof(Value), defaultBindingMode: BindingMode.TwoWay);

    public static readonly StyledProperty<double> MinimumProperty =
        AvaloniaProperty.Register<ScrubInput, double>(nameof(Minimum), double.NegativeInfinity);

    public static readonly StyledProperty<double> MaximumProperty =
        AvaloniaProperty.Register<ScrubInput, double>(nameof(Maximum), double.PositiveInfinity);

    public static readonly StyledProperty<double> StepProperty =
        AvaloniaProperty.Register<ScrubInput, double>(nameof(Step), 1);

    public static readonly StyledProperty<double?> FineStepProperty =
        AvaloniaProperty.Register<ScrubInput, double?>(nameof(FineStep));

    public static readonly StyledProperty<double?> CoarseStepProperty =
        AvaloniaProperty.Register<ScrubInput, double?>(nameof(CoarseStep));

    public static readonly StyledProperty<int?> PrecisionProperty =
        AvaloniaProperty.Register<ScrubInput, int?>(nameof(Precision));

    public static readonly StyledProperty<double> PixelsPerStepProperty =
        AvaloniaProperty.Register<ScrubInput, double>(nameof(PixelsPerStep), 2);

    public static readonly StyledProperty<bool> IsWrappingProperty =
        AvaloniaProperty.Register<ScrubInput, bool>(nameof(IsWrapping));

    public static readonly StyledProperty<string?> UnitProperty =
        AvaloniaProperty.Register<ScrubInput, string?>(nameof(Unit), defaultBindingMode: BindingMode.TwoWay);

    public static readonly StyledProperty<IList<string>?> UnitsProperty =
        AvaloniaProperty.Register<ScrubInput, IList<string>?>(nameof(Units));

    public static readonly StyledProperty<double?> ResetValueProperty =
        AvaloniaProperty.Register<ScrubInput, double?>(nameof(ResetValue));

    public static readonly StyledProperty<bool?> IsTrackVisibleProperty =
        AvaloniaProperty.Register<ScrubInput, bool?>(nameof(IsTrackVisible));

    public static readonly StyledProperty<ControlSize> SizeProperty =
        AvaloniaProperty.Register<ScrubInput, ControlSize>(nameof(Size), ControlSize.Medium);

    public static readonly DirectProperty<ScrubInput, bool> IsInvalidProperty =
        AvaloniaProperty.RegisterDirect<ScrubInput, bool>(nameof(IsInvalid), o => o.IsInvalid);

    public static readonly DirectProperty<ScrubInput, bool> IsScrubbingProperty =
        AvaloniaProperty.RegisterDirect<ScrubInput, bool>(nameof(IsScrubbing), o => o.IsScrubbing);

    public static readonly RoutedEvent<ScrubInputValueChangedEventArgs> ValueChangedEvent =
        RoutedEvent.Register<ScrubInput, ScrubInputValueChangedEventArgs>(nameof(ValueChanged), RoutingStrategies.Bubble);

    public static readonly RoutedEvent<ScrubInputValueChangedEventArgs> ValueCommittedEvent =
        RoutedEvent.Register<ScrubInput, ScrubInputValueChangedEventArgs>(nameof(ValueCommitted), RoutingStrategies.Bubble);

    public object? Label { get => GetValue(LabelProperty); set => SetValue(LabelProperty, value); }

    public double Value { get => GetValue(ValueProperty); set => SetValue(ValueProperty, value); }

    public double Minimum { get => GetValue(MinimumProperty); set => SetValue(MinimumProperty, value); }

    public double Maximum { get => GetValue(MaximumProperty); set => SetValue(MaximumProperty, value); }

    public double Step { get => GetValue(StepProperty); set => SetValue(StepProperty, value); }

    public double? FineStep { get => GetValue(FineStepProperty); set => SetValue(FineStepProperty, value); }

    public double? CoarseStep { get => GetValue(CoarseStepProperty); set => SetValue(CoarseStepProperty, value); }

    public int? Precision { get => GetValue(PrecisionProperty); set => SetValue(PrecisionProperty, value); }

    public double PixelsPerStep { get => GetValue(PixelsPerStepProperty); set => SetValue(PixelsPerStepProperty, value); }

    public bool IsWrapping { get => GetValue(IsWrappingProperty); set => SetValue(IsWrappingProperty, value); }

    public string? Unit { get => GetValue(UnitProperty); set => SetValue(UnitProperty, value); }

    public IList<string>? Units { get => GetValue(UnitsProperty); set => SetValue(UnitsProperty, value); }

    public double? ResetValue { get => GetValue(ResetValueProperty); set => SetValue(ResetValueProperty, value); }

    public bool? IsTrackVisible { get => GetValue(IsTrackVisibleProperty); set => SetValue(IsTrackVisibleProperty, value); }

    public ControlSize Size { get => GetValue(SizeProperty); set => SetValue(SizeProperty, value); }

    public bool IsInvalid { get => _isInvalid; private set => SetAndRaise(IsInvalidProperty, ref _isInvalid, value); }

    public bool IsScrubbing { get => _isScrubbing; private set => SetAndRaise(IsScrubbingProperty, ref _isScrubbing, value); }

    public event EventHandler<ScrubInputValueChangedEventArgs>? ValueChanged
    {
        add => AddHandler(ValueChangedEvent, value);
        remove => RemoveHandler(ValueChangedEvent, value);
    }

    public event EventHandler<ScrubInputValueChangedEventArgs>? ValueCommitted
    {
        add => AddHandler(ValueCommittedEvent, value);
        remove => RemoveHandler(ValueCommittedEvent, value);
    }

    #endregion

    private bool _isInvalid;
    private bool _isScrubbing;
    private ScrubInputShell? _shell;
    private ContentPresenter? _handle;
    private ScrubInputEditor? _editor;
    private TextBlock? _unit;
    private string? _draft;
    private bool _syncingText;
    private bool _emitting;
    private bool _selectAllOnRelease;
    private bool _pointerInside;
    private TopLevel? _keyRoot;
    private DispatcherTimer? _invalidTimer;

    private bool _pressed;
    private bool _scrubbing;
    private IPointer? _pointer;
    private double _pressX;
    private double _pressY;
    private double _lastX;
    private double _dragRaw;
    private double _dragStart;

    static ScrubInput()
    {
        FocusableProperty.OverrideDefaultValue<ScrubInput>(false);
    }

    public ScrubInput()
    {
        PseudoClasses.Set(SmallPseudoClass, Size == ControlSize.Small);
    }

    #region Derived values

    public double EffectiveFineStep => FineStep ?? Step / 10;

    public double EffectiveCoarseStep => CoarseStep ?? Step * 10;

    public int EffectivePrecision => Precision ?? Math.Max(ScrubInputNumber.DecimalsOf(Step), ScrubInputNumber.DecimalsOf(EffectiveFineStep));

    public string EffectiveUnit => Unit ?? (Units is { Count: > 0 } units ? units[0] : string.Empty);

    public string FormattedValue => ScrubInputNumber.Format(Value, EffectivePrecision);

    internal bool HasFiniteRange => double.IsFinite(Minimum) && double.IsFinite(Maximum);

    internal bool CanWrap => IsWrapping && double.IsFinite(Minimum) && double.IsFinite(Maximum) && Maximum > Minimum;

    internal bool ShowTrack => (IsTrackVisible ?? true) && double.IsFinite(Minimum) && double.IsFinite(Maximum) && Maximum > Minimum;

    private IReadOnlyList<string> UnitList
    {
        get
        {
            if (Units is { } units)
            {
                return units as IReadOnlyList<string> ?? units.ToArray();
            }
            var unit = EffectiveUnit;
            return unit.Length > 0 ? [unit] : [];
        }
    }

    internal string ValueText
    {
        get
        {
            var unit = EffectiveUnit;
            var text = FormattedValue;
            if (unit.Length == 0)
            {
                return text;
            }
            var spoken = unit == "%" ? "percent" : unit is "deg" or "°" ? "degrees" : unit;
            return text + " " + spoken;
        }
    }

    internal string? AutomationName
    {
        get
        {
            var name = AutomationProperties.GetName(this);
            if (!string.IsNullOrEmpty(name))
            {
                return name;
            }
            return Label switch
            {
                string text => text,
                IFormattable number when IsTextLabel(Label) => number.ToString(null, System.Globalization.CultureInfo.InvariantCulture),
                _ => null
            };
        }
    }

    internal double DisplayedFraction => _shell?.Fraction ?? 0;

    internal ScrubInputEditor? Editor => _editor;

    private double TargetFraction => ShowTrack ? Math.Clamp((Value - Minimum) / (Maximum - Minimum), 0, 1) : 0;

    private static bool IsTextLabel(object? label) => label is string or int or long or short or byte or double or float or decimal;

    #endregion

    #region Template

    protected override void OnApplyTemplate(TemplateAppliedEventArgs e)
    {
        base.OnApplyTemplate(e);
        DetachParts();

        _shell = e.NameScope.Find<ScrubInputShell>(ShellPart);
        _handle = e.NameScope.Find<ContentPresenter>(HandlePart);
        _editor = e.NameScope.Find<ScrubInputEditor>(EditorPart);
        _unit = e.NameScope.Find<TextBlock>(UnitPart);

        if (_handle is not null)
        {
            _handle.PointerPressed += OnHandlePointerPressed;
            _handle.PointerMoved += OnHandlePointerMoved;
            _handle.PointerReleased += OnHandlePointerReleased;
            _handle.PointerCaptureLost += OnHandlePointerCaptureLost;
            _handle.PointerEntered += OnHandlePointerEntered;
            _handle.PointerExited += OnHandlePointerExited;
            _handle.DoubleTapped += OnHandleDoubleTapped;
        }
        if (_editor is not null)
        {
            _editor.FontFeatures = s_tabular;
            _editor.TextChanging += OnEditorTextChanging;
            _editor.GotFocus += OnEditorGotFocus;
            _editor.LostFocus += OnEditorLostFocus;
            _editor.AddHandler(PointerReleasedEvent, OnEditorPointerReleased, RoutingStrategies.Bubble, true);
        }

        UpdateLabel();
        UpdateUnit();
        UpdateShell();
        UpdateFill();
        SyncText();
    }

    private static readonly FontFeatureCollection s_tabular = new() { FontFeature.Parse("tnum") };

    private void DetachParts()
    {
        CancelScrubSilently();
        if (_handle is not null)
        {
            _handle.PointerPressed -= OnHandlePointerPressed;
            _handle.PointerMoved -= OnHandlePointerMoved;
            _handle.PointerReleased -= OnHandlePointerReleased;
            _handle.PointerCaptureLost -= OnHandlePointerCaptureLost;
            _handle.PointerEntered -= OnHandlePointerEntered;
            _handle.PointerExited -= OnHandlePointerExited;
            _handle.DoubleTapped -= OnHandleDoubleTapped;
        }
        if (_editor is not null)
        {
            _editor.TextChanging -= OnEditorTextChanging;
            _editor.GotFocus -= OnEditorGotFocus;
            _editor.LostFocus -= OnEditorLostFocus;
            _editor.RemoveHandler(PointerReleasedEvent, OnEditorPointerReleased);
        }
    }

    protected override void OnDetachedFromVisualTree(VisualTreeAttachmentEventArgs e)
    {
        CancelScrubSilently();
        _invalidTimer?.Stop();
        if (_isInvalid)
        {
            IsInvalid = false;
            PseudoClasses.Set(InvalidPseudoClass, false);
        }
        base.OnDetachedFromVisualTree(e);
    }

    protected override void OnPropertyChanged(AvaloniaPropertyChangedEventArgs change)
    {
        base.OnPropertyChanged(change);
        var property = change.Property;
        if (property == SizeProperty)
        {
            PseudoClasses.Set(SmallPseudoClass, Size == ControlSize.Small);
        }
        if (property == ValueProperty)
        {
            SyncText();
            UpdateFill();
            if (!_emitting)
            {
                RaiseEvent(new ScrubInputValueChangedEventArgs(ValueChangedEvent, change.GetOldValue<double>(),
                    change.GetNewValue<double>(), ScrubInputChangeSource.Programmatic, EffectiveUnit));
            }
        }
        else if (property == MinimumProperty || property == MaximumProperty || property == IsTrackVisibleProperty)
        {
            UpdateShell();
            UpdateFill();
        }
        else if (property == StepProperty || property == FineStepProperty || property == PrecisionProperty)
        {
            SyncText();
        }
        else if (property == UnitProperty || property == UnitsProperty)
        {
            UpdateUnit();
        }
        else if (property == LabelProperty)
        {
            UpdateLabel();
        }
        else if (property == IsEffectivelyEnabledProperty)
        {
            if (!IsEffectivelyEnabled)
            {
                CancelScrubSilently();
            }
            UpdateHover();
        }
    }

    private void UpdateLabel() => PseudoClasses.Set(TextLabelPseudoClass, IsTextLabel(Label));

    private void UpdateUnit()
    {
        if (_unit is null)
        {
            return;
        }
        var unit = EffectiveUnit;
        _unit.Text = unit;
        _unit.IsVisible = unit.Length > 0;
    }

    private void UpdateShell()
    {
        if (_shell is not null)
        {
            _shell.IsTrackVisible = ShowTrack;
        }
    }

    private void UpdateFill()
    {
        if (_shell is not null)
        {
            _shell.Fraction = TargetFraction;
        }
    }

    #endregion

    #region Value model

    /// <summary>Rounds to the precision, then wraps or clamps to the limits.</summary>
    internal double Constrain(double next)
    {
        var places = EffectivePrecision;
        var rounded = ScrubInputNumber.RoundTo(next, places);
        var min = Minimum;
        var max = Maximum;
        if (CanWrap)
        {
            var span = max - min;
            return ScrubInputNumber.RoundTo(((rounded - min) % span + span) % span + min, places);
        }
        return Math.Min(max, Math.Max(min, rounded));
    }

    private void Emit(double next, ScrubInputChangeSource source, bool commit, double? committedFrom = null)
    {
        var old = Value;
        if (!next.Equals(old))
        {
            _emitting = true;
            try
            {
                SetCurrentValue(ValueProperty, next);
            }
            finally
            {
                _emitting = false;
            }
            RaiseEvent(new ScrubInputValueChangedEventArgs(ValueChangedEvent, old, next, source, EffectiveUnit));
        }
        if (commit)
        {
            RaiseEvent(new ScrubInputValueChangedEventArgs(ValueCommittedEvent, committedFrom ?? old, Value, source, EffectiveUnit));
        }
    }

    private void SetUnitFromUser(string unit)
    {
        if (unit != EffectiveUnit)
        {
            SetCurrentValue(UnitProperty, unit);
        }
    }

    /// <summary>Commits the typed draft. Returns false when the entry was rejected (the old value stays).</summary>
    private bool CommitDraft()
    {
        var text = _draft;
        _draft = null;
        if (text is null)
        {
            return true;
        }
        if (text.Trim() == FormattedValue)
        {
            SyncText();
            return true;
        }
        var parsed = ScrubExpression.Evaluate(text, UnitList);
        if (parsed is not { } result)
        {
            SyncText();
            FlagInvalid();
            return false;
        }
        if (result.Unit is { } typedUnit && typedUnit != EffectiveUnit)
        {
            SetUnitFromUser(typedUnit);
        }
        Emit(Constrain(result.Value), ScrubInputChangeSource.Type, true);
        SyncText();
        return true;
    }

    /// <summary>Commits a typed value that was not yet confirmed, used when the cell edit ends.</summary>
    internal void CommitPendingEdit()
    {
        if (_draft is not null)
        {
            CommitDraft();
        }
    }

    private void StepBy(int direction, KeyModifiers modifiers)
    {
        var shift = modifiers.HasFlag(KeyModifiers.Shift);
        var alt = modifiers.HasFlag(KeyModifiers.Alt);
        var amount = shift ? EffectiveCoarseStep : alt ? EffectiveFineStep : Step;
        var current = Value;
        if (!CanWrap && ((direction > 0 && current >= Maximum) || (direction < 0 && current <= Minimum)))
        {
            return;
        }
        if (!(amount > 0))
        {
            return;
        }
        // Coarse steps land on the coarse grid, so 13 with Shift goes to 20, not 23.
        var snapped = shift
            ? (direction > 0 ? Math.Floor(current / amount + 1e-9) + 1 : Math.Ceiling(current / amount - 1e-9) - 1) * amount
            : current + direction * amount;
        Emit(Constrain(snapped), ScrubInputChangeSource.Key, true);
    }

    internal void SetValueFromAutomation(double value)
    {
        if (!IsEffectivelyEnabled)
        {
            return;
        }
        _draft = null;
        Emit(Constrain(value), ScrubInputChangeSource.Type, true);
        SyncText();
    }

    #endregion

    #region Editor

    private void SyncText()
    {
        if (_editor is null || _draft is not null)
        {
            return;
        }
        var text = FormattedValue;
        if (_editor.Text == text)
        {
            return;
        }
        _syncingText = true;
        try
        {
            _editor.Text = text;
        }
        finally
        {
            _syncingText = false;
        }
    }

    private void SelectAll()
    {
        _editor?.SelectAll();
    }

    /// <summary>Focuses the number editor and selects its text (used when a DataGrid cell begins editing).</summary>
    internal void FocusEditor()
    {
        _editor?.Focus();
        SelectAll();
    }

    // TextChanging is synchronous, so text written by SyncText is never mistaken for a draft.
    private void OnEditorTextChanging(object? sender, TextChangingEventArgs e)
    {
        if (_syncingText || _editor is null)
        {
            return;
        }
        _draft = _editor.Text ?? string.Empty;
    }

    private void OnEditorGotFocus(object? sender, FocusChangedEventArgs e)
    {
        // Focus selects everything; a click lands its caret after focus, so a pointer focus selects on release.
        if (e.NavigationMethod == NavigationMethod.Pointer)
        {
            _selectAllOnRelease = true;
        }
        else
        {
            SelectAll();
        }
    }

    private void OnEditorPointerReleased(object? sender, PointerReleasedEventArgs e)
    {
        if (!_selectAllOnRelease || _editor is null)
        {
            return;
        }
        _selectAllOnRelease = false;
        if (_editor.SelectionStart == _editor.SelectionEnd)
        {
            SelectAll();
        }
    }

    private void OnEditorLostFocus(object? sender, FocusChangedEventArgs e)
    {
        _selectAllOnRelease = false;
        CommitDraft();
    }

    internal void HandleEditorKeyDown(KeyEventArgs e)
    {
        if (!IsEffectivelyEnabled)
        {
            return;
        }
        switch (e.Key)
        {
            case Key.Up:
            case Key.Down:
            {
                e.Handled = true;
                if (_draft is not null && !CommitDraft())
                {
                    return;
                }
                StepBy(e.Key == Key.Up ? 1 : -1, e.KeyModifiers);
                SyncText();
                SelectAll();
                return;
            }
            case Key.Enter:
            {
                // Enter is left unhandled so a hosting DataGrid sees it and commits/navigates;
                // a rejected entry stays in the editor.
                if (!CommitDraft())
                {
                    e.Handled = true;
                }
                else
                {
                    SelectAll();
                }
                return;
            }
            case Key.Escape when _draft is not null:
            {
                e.Handled = true;
                _draft = null;
                SyncText();
                SelectAll();
                return;
            }
        }
    }

    #endregion

    #region Scrubbing

    private void OnHandlePointerPressed(object? sender, PointerPressedEventArgs e)
    {
        if (!IsEffectivelyEnabled || _pressed || _scrubbing || _handle is null)
        {
            return;
        }
        if (!e.GetCurrentPoint(_handle).Properties.IsLeftButtonPressed)
        {
            return;
        }
        if (_draft is not null)
        {
            CommitDraft();
        }
        _pressed = true;
        _scrubbing = false;
        _pointer = e.Pointer;
        var position = e.GetPosition(this);
        _pressX = position.X;
        _pressY = position.Y;
        _lastX = position.X;
        _dragRaw = Value;
        _dragStart = Value;
        // Capture so the scrub survives leaving the field; the press stays unhandled so the grid still selects the cell.
        e.Pointer.Capture(_handle);
    }

    private void OnHandlePointerMoved(object? sender, PointerEventArgs e)
    {
        if (!_pressed || e.Pointer != _pointer)
        {
            return;
        }
        if (!e.GetCurrentPoint(this).Properties.IsLeftButtonPressed)
        {
            return;
        }
        var x = e.GetPosition(this).X;
        if (!_scrubbing)
        {
            var travel = x - _pressX;
            if (Math.Abs(travel) < Slop)
            {
                return;
            }
            _scrubbing = true;
            _lastX = _pressX + Math.Sign(travel) * Slop;
            IsScrubbing = true;
            PseudoClasses.Set(ScrubbingPseudoClass, true);
            UpdateHover();
            AttachKeys();
        }

        var shift = e.KeyModifiers.HasFlag(KeyModifiers.Shift);
        var alt = e.KeyModifiers.HasFlag(KeyModifiers.Alt);
        var amount = shift ? EffectiveCoarseStep : alt ? EffectiveFineStep : Step;
        if (!(amount > 0))
        {
            return;
        }
        var pixelsPerStep = PixelsPerStep > 0 ? PixelsPerStep : 2;
        _dragRaw += (x - _lastX) / pixelsPerStep * amount;
        _lastX = x;

        if (!CanWrap)
        {
            var perPx = amount / pixelsPerStep;
            var over = _dragRaw > Maximum ? (_dragRaw - Maximum) / perPx : _dragRaw < Minimum ? (_dragRaw - Minimum) / perPx : 0;
            if (Math.Abs(over) > OvershootCap)
            {
                _dragRaw = (over > 0 ? Maximum : Minimum) + Math.Sign(over) * OvershootCap * perPx;
            }
        }
        var snapped = ScrubInputNumber.JsRound(_dragRaw / amount) * amount;
        var next = Constrain(snapped);
        if (!next.Equals(Value))
        {
            Emit(next, ScrubInputChangeSource.Scrub, false);
        }
    }

    private void OnHandlePointerReleased(object? sender, PointerReleasedEventArgs e)
    {
        if (e.Pointer != _pointer)
        {
            return;
        }
        var wasScrubbing = _scrubbing;
        var travel = Math.Abs(e.GetPosition(this).X - _pressX) + Math.Abs(e.GetPosition(this).Y - _pressY);
        EndScrub(false);
        _pressed = false;
        _pointer = null;
        // A plain click on the label focuses the number and selects it.
        if (!wasScrubbing && travel < Slop && IsEffectivelyEnabled)
        {
            _editor?.Focus();
            SelectAll();
        }
    }

    private void OnHandlePointerCaptureLost(object? sender, PointerCaptureLostEventArgs e)
    {
        if (e.Pointer != _pointer)
        {
            return;
        }
        EndScrub(false);
        _pressed = false;
        _pointer = null;
    }

    private void OnHandleDoubleTapped(object? sender, TappedEventArgs e)
    {
        if (!IsEffectivelyEnabled || ResetValue is not { } reset)
        {
            return;
        }
        Emit(Constrain(reset), ScrubInputChangeSource.Reset, true);
        SyncText();
    }

    private void EndScrub(bool cancel)
    {
        if (!_scrubbing)
        {
            return;
        }
        _scrubbing = false;
        DetachKeys();
        IsScrubbing = false;
        PseudoClasses.Set(ScrubbingPseudoClass, false);
        UpdateHover();
        if (cancel)
        {
            Emit(_dragStart, ScrubInputChangeSource.Scrub, true, _dragStart);
        }
        else
        {
            RaiseEvent(new ScrubInputValueChangedEventArgs(ValueCommittedEvent, _dragStart, Value, ScrubInputChangeSource.Scrub, EffectiveUnit));
        }
        if (cancel)
        {
            _pointer?.Capture(null);
            _pressed = false;
            _pointer = null;
        }
    }

    /// <summary>Ends a scrub without events (template change, detach, disable).</summary>
    private void CancelScrubSilently()
    {
        if (!_scrubbing && !_pressed)
        {
            return;
        }
        _scrubbing = false;
        _pressed = false;
        DetachKeys();
        IsScrubbing = false;
        PseudoClasses.Set(ScrubbingPseudoClass, false);
        _pointer?.Capture(null);
        _pointer = null;
        UpdateHover();
    }

    private void AttachKeys()
    {
        if (_keyRoot is not null)
        {
            return;
        }
        _keyRoot = TopLevel.GetTopLevel(this);
        _keyRoot?.AddHandler(KeyDownEvent, OnScrubKeyDown, RoutingStrategies.Tunnel, true);
    }

    private void DetachKeys()
    {
        if (_keyRoot is null)
        {
            return;
        }
        _keyRoot.RemoveHandler(KeyDownEvent, OnScrubKeyDown);
        _keyRoot = null;
    }

    private void OnScrubKeyDown(object? sender, KeyEventArgs e)
    {
        if (!_scrubbing)
        {
            return;
        }
        if (e.Key == Key.Escape)
        {
            // Escape cancels back to where the drag started.
            e.Handled = true;
            EndScrub(true);
        }
    }

    #endregion

    #region Hover

    protected override void OnPointerEntered(PointerEventArgs e)
    {
        base.OnPointerEntered(e);
        _pointerInside = e.Pointer.Type != PointerType.Touch;
        UpdateHover();
    }

    protected override void OnPointerExited(PointerEventArgs e)
    {
        base.OnPointerExited(e);
        _pointerInside = false;
        UpdateHover();
    }

    private void UpdateHover() =>
        PseudoClasses.Set(HoverPseudoClass, IsEffectivelyEnabled && (_pointerInside || _scrubbing));

    private void OnHandlePointerEntered(object? sender, PointerEventArgs e) =>
        PseudoClasses.Set(HandleHoverPseudoClass, IsEffectivelyEnabled && e.Pointer.Type != PointerType.Touch);

    private void OnHandlePointerExited(object? sender, PointerEventArgs e) =>
        PseudoClasses.Set(HandleHoverPseudoClass, false);

    private void FlagInvalid()
    {
        IsInvalid = true;
        PseudoClasses.Set(InvalidPseudoClass, true);
        if (_invalidTimer is null)
        {
            _invalidTimer = new DispatcherTimer { Interval = InvalidDuration };
            _invalidTimer.Tick += OnInvalidTimerTick;
        }
        _invalidTimer.Stop();
        _invalidTimer.Start();
    }

    private void OnInvalidTimerTick(object? sender, EventArgs e)
    {
        _invalidTimer?.Stop();
        IsInvalid = false;
        PseudoClasses.Set(InvalidPseudoClass, false);
    }

    #endregion
}
