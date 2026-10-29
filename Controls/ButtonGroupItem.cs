using System.Diagnostics;
using Avalonia;
using Avalonia.Automation;
using Avalonia.Automation.Peers;
using Avalonia.Controls;
using Avalonia.Controls.Primitives;
using Avalonia.Input;
using Avalonia.Media;

namespace AcrfAnnotationTool.Controls;

/// <summary>One action segment in a <see cref="ButtonGroup"/>.</summary>
public class ButtonGroupItem : Button
{
    public const string IconOnlyPseudoClass = ":icon-only";
    public const string HasIconPseudoClass = ":has-icon";
    public const string CollapsedPseudoClass = ":collapsed";
    public const string ValuePseudoClass = ":value";
    public const string InertPseudoClass = ":inert";
    public const string VerticalPseudoClass = ":vertical";
    public const string SmallPseudoClass = ":small";
    public const string SolidPseudoClass = ":solid";
    public const string JoinedPseudoClass = ":joined";
    public const string QuietPseudoClass = ":quiet";
    public const string ActivePseudoClass = ":active";

    public static readonly StyledProperty<string> LabelProperty =
        AvaloniaProperty.Register<ButtonGroupItem, string>(nameof(Label), string.Empty);
    public static readonly StyledProperty<Control?> IconProperty =
        AvaloniaProperty.Register<ButtonGroupItem, Control?>(nameof(Icon));
    public static readonly StyledProperty<bool> IsIconOnlyProperty =
        AvaloniaProperty.Register<ButtonGroupItem, bool>(nameof(IsIconOnly));
    public static readonly StyledProperty<object?> ValueProperty =
        AvaloniaProperty.Register<ButtonGroupItem, object?>(nameof(Value));
    public static readonly StyledProperty<IReadOnlyList<string>?> ReserveLabelsProperty =
        AvaloniaProperty.Register<ButtonGroupItem, IReadOnlyList<string>?>(nameof(ReserveLabels));
    public static readonly StyledProperty<Uri?> NavigateUriProperty =
        AvaloniaProperty.Register<ButtonGroupItem, Uri?>(nameof(NavigateUri));
    public static readonly StyledProperty<bool> IsDisabledProperty =
        AvaloniaProperty.Register<ButtonGroupItem, bool>(nameof(IsDisabled));
    public static readonly StyledProperty<string?> DoneLabelProperty =
        AvaloniaProperty.Register<ButtonGroupItem, string?>(nameof(DoneLabel));
    public static readonly StyledProperty<double> LabelGapProperty =
        AvaloniaProperty.Register<ButtonGroupItem, double>(nameof(LabelGap), 7);
    public static readonly DirectProperty<ButtonGroupItem, Control?> PhaseProperty =
        AvaloniaProperty.RegisterDirect<ButtonGroupItem, Control?>(nameof(Phase), item => item.Phase);
    public static readonly DirectProperty<ButtonGroupItem, IReadOnlyList<string>> SizerLabelsProperty =
        AvaloniaProperty.RegisterDirect<ButtonGroupItem, IReadOnlyList<string>>(nameof(SizerLabels), item => item.SizerLabels);

    public string Label { get => GetValue(LabelProperty); set => SetValue(LabelProperty, value); }
    public Control? Icon { get => GetValue(IconProperty); set => SetValue(IconProperty, value); }
    public bool IsIconOnly { get => GetValue(IsIconOnlyProperty); set => SetValue(IsIconOnlyProperty, value); }
    public object? Value { get => GetValue(ValueProperty); set => SetValue(ValueProperty, value); }
    public IReadOnlyList<string>? ReserveLabels { get => GetValue(ReserveLabelsProperty); set => SetValue(ReserveLabelsProperty, value); }
    public Uri? NavigateUri { get => GetValue(NavigateUriProperty); set => SetValue(NavigateUriProperty, value); }
    public bool IsDisabled { get => GetValue(IsDisabledProperty); set => SetValue(IsDisabledProperty, value); }
    public string? DoneLabel { get => GetValue(DoneLabelProperty); set => SetValue(DoneLabelProperty, value); }
    public Func<Task<bool>>? AsyncAction { get; set; }
    public bool IsDone { get; private set; }
    public bool IsBusy { get; private set; }
    internal double LabelGap { get => GetValue(LabelGapProperty); set => SetValue(LabelGapProperty, value); }

    private Control? _phase;
    internal Control? Phase
    {
        get => _phase;
        private set => SetAndRaise(PhaseProperty, ref _phase, value);
    }

    private IReadOnlyList<string> _sizerLabels = [];
    internal IReadOnlyList<string> SizerLabels
    {
        get => _sizerLabels;
        private set => SetAndRaise(SizerLabelsProperty, ref _sizerLabels, value);
    }

    private static readonly object ValueKey = new();
    private bool _built;
    private (Control?, object?) _phaseKey;
    private TextBlock? _phaseLabel;
    private TextBlock? _phaseValue;
    private readonly Control _doneIcon = new AtomUI.Icons.AntDesign.CheckOutlined
    {
        Width = 16,
        Height = 16,
        Foreground = Brush.Parse("#2E7D32")
    };
    private ButtonGroup? _group;

    internal ButtonGroup? Group
    {
        get => _group;
        set => _group = value;
    }

    internal bool IsCollapsible => Icon is not null && !IsIconOnly && Value is null;
    internal bool IsInert => IsDisabled || !IsEffectivelyEnabled;

    internal void SyncFromGroup()
    {
        var group = Group;
        PseudoClasses.Set(VerticalPseudoClass, group?.Orientation == Avalonia.Layout.Orientation.Vertical);
        PseudoClasses.Set(SmallPseudoClass, group?.IsSmall == true);
        PseudoClasses.Set(SolidPseudoClass, group?.Variant == ButtonGroupVariant.Solid);
        Rebuild();
    }

    internal void SetSegmentState(bool joined, bool quiet, bool active)
    {
        PseudoClasses.Set(JoinedPseudoClass, joined);
        PseudoClasses.Set(QuietPseudoClass, quiet);
        PseudoClasses.Set(ActivePseudoClass, active);
    }

    protected override void OnInitialized()
    {
        base.OnInitialized();
        Rebuild();
    }

    protected override void OnPropertyChanged(AvaloniaPropertyChangedEventArgs change)
    {
        base.OnPropertyChanged(change);
        if (change.Property == LabelProperty || change.Property == IconProperty || change.Property == IsIconOnlyProperty ||
            change.Property == ValueProperty || change.Property == ReserveLabelsProperty || change.Property == DoneLabelProperty)
        {
            if (_built)
            {
                Rebuild();
            }
        }
        else if (change.Property == IsDisabledProperty || change.Property == IsEffectivelyEnabledProperty)
        {
            PseudoClasses.Set(InertPseudoClass, IsInert);
            Group?.InvalidateVisual();
        }
    }

    internal void Rebuild()
    {
        _built = true;
        var iconOnly = Icon is not null && IsIconOnly;
        var collapsed = !iconOnly && IsCollapsible && Group?.AreLabelsCollapsed == true;
        var hasValue = Value is not null;
        PseudoClasses.Set(IconOnlyPseudoClass, iconOnly);
        PseudoClasses.Set(CollapsedPseudoClass, collapsed);
        PseudoClasses.Set(HasIconPseudoClass, Icon is not null);
        PseudoClasses.Set(ValuePseudoClass, hasValue);
        PseudoClasses.Set(InertPseudoClass, IsInert);
        AutomationProperties.SetName(this, Label);

        var labels = new List<string>();
        if (!iconOnly && !hasValue && !collapsed)
        {
            labels.Add(Label);
            if (ReserveLabels is { } reserve)
            {
                foreach (var text in reserve)
                {
                    if (!labels.Contains(text))
                    {
                        labels.Add(text);
                    }
                }
            }
        }
        if (!labels.SequenceEqual(SizerLabels))
        {
            SizerLabels = labels;
        }

        var phaseKey = IsDone ? (object?)_doneIcon : hasValue ? Value as Control ?? ValueKey : iconOnly ? null : Label;
        var key = (Icon, phaseKey);
        if (Phase is null || !Equals(key, _phaseKey))
        {
            _phaseKey = key;
            DetachFromParent(Icon);
            if (Value is Control valueControl)
            {
                DetachFromParent(valueControl);
            }
            DetachFromParent(_doneIcon);
            var phase = new StackPanel
            {
                Orientation = Avalonia.Layout.Orientation.Horizontal,
                VerticalAlignment = Avalonia.Layout.VerticalAlignment.Center,
                Spacing = LabelGap
            };
            _phaseLabel = null;
            _phaseValue = null;
            if (IsDone)
            {
                phase.Children.Add(_doneIcon);
                if (!string.IsNullOrEmpty(DoneLabel))
                {
                    _phaseLabel = new TextBlock { Text = DoneLabel, VerticalAlignment = Avalonia.Layout.VerticalAlignment.Center };
                    phase.Children.Add(_phaseLabel);
                }
            }
            else
            {
                if (Icon is { } icon)
                {
                    phase.Children.Add(icon);
                }
                if (hasValue)
                {
                    if (Value is Control control)
                    {
                        phase.Children.Add(control);
                    }
                    else
                    {
                        _phaseValue = new TextBlock { VerticalAlignment = Avalonia.Layout.VerticalAlignment.Center };
                        phase.Children.Add(_phaseValue);
                    }
                }
                else if (!iconOnly)
                {
                    _phaseLabel = new TextBlock { Text = Label, VerticalAlignment = Avalonia.Layout.VerticalAlignment.Center };
                    phase.Children.Add(_phaseLabel);
                }
            }
            Phase = phase;
        }

        if (_phaseValue is not null)
        {
            _phaseValue.Text = Value?.ToString();
        }
        if (_phaseLabel is not null && !IsDone)
        {
            _phaseLabel.Text = Label;
            _phaseLabel.IsVisible = !collapsed;
        }

        if (iconOnly || collapsed)
        {
            ToolTip.SetTip(this, Label);
        }
        else
        {
            ClearValue(ToolTip.TipProperty);
        }

        InvalidateMeasure();
    }

    internal void SetDone(bool done)
    {
        IsDone = done;
        PseudoClasses.Set(":done", done);
        Rebuild();
    }

    protected override async void OnClick()
    {
        if (IsDisabled || IsBusy || IsDone)
        {
            return;
        }

        var group = Group;
        if (group != null && !group.BeginInvoke(this))
        {
            return;
        }

        IsBusy = true;
        PseudoClasses.Set(":busy", true);
        try
        {
            base.OnClick();
            var succeeded = AsyncAction is not { } action || await action();
            if (succeeded && IsLoaded && ReferenceEquals(Group, group) && !string.IsNullOrEmpty(DoneLabel))
            {
                group?.Confirm(this);
            }

            if (NavigateUri is { } uri && TopLevel.GetTopLevel(this)?.Launcher is { } launcher)
            {
                await launcher.LaunchUriAsync(uri);
            }
        }
        catch (Exception exception)
        {
            Trace.TraceError("Button group action failed: {0}", exception);
        }
        finally
        {
            IsBusy = false;
            PseudoClasses.Set(":busy", false);
            group?.EndInvoke(this);
        }
    }

    protected override void OnPointerPressed(PointerPressedEventArgs e)
    {
        base.OnPointerPressed(e);
    }

    protected override void OnGotFocus(FocusChangedEventArgs e)
    {
        base.OnGotFocus(e);
        Group?.OnSegmentFocused(this, e.NavigationMethod);
    }

    private static void DetachFromParent(Control? control)
    {
        if (control?.Parent is Panel panel)
        {
            panel.Children.Remove(control);
        }
    }

    protected override AutomationPeer OnCreateAutomationPeer() => new ButtonGroupItemAutomationPeer(this);

    protected override void OnDetachedFromVisualTree(VisualTreeAttachmentEventArgs e)
    {
        base.OnDetachedFromVisualTree(e);
        _built = false;
    }

    private sealed class ButtonGroupItemAutomationPeer(ButtonGroupItem owner) : ButtonAutomationPeer(owner)
    {
        protected override AutomationControlType GetAutomationControlTypeCore() =>
            owner.NavigateUri is null ? AutomationControlType.Button : AutomationControlType.Hyperlink;

        protected override bool IsEnabledCore() => base.IsEnabledCore() && !owner.IsDisabled;
    }
}
