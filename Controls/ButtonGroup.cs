using System.Diagnostics;
using AcrfAnnotationTool.Controls.Menus;
using Avalonia;
using Avalonia.Automation;
using Avalonia.Automation.Peers;
using Avalonia.Controls;
using Avalonia.Controls.Primitives;
using Avalonia.Input;
using Avalonia.Interactivity;
using Avalonia.Layout;
using Avalonia.VisualTree;
using Avalonia.Threading;

namespace AcrfAnnotationTool.Controls;

public enum ButtonGroupVariant
{
    Outline,
    Solid
}

public sealed class ButtonGroupActionEventArgs : RoutedEventArgs
{
    public ButtonGroupActionEventArgs(RoutedEvent routedEvent, ButtonGroupItem item, object? data)
        : base(routedEvent)
    {
        Item = item;
        Data = data;
    }

    public ButtonGroupItem Item { get; }
    public object? Data { get; }
}

/// <summary>
/// A joined group of related actions. The highlight glides between the hovered or focused segments,
/// while each segment keeps its own command and asynchronous completion state.
/// </summary>
public class ButtonGroup : ItemsControl
{
    public const string HighlightPart = "PART_Highlight";
    public const string RowPart = "PART_Row";
    public const string PressingPseudoClass = ":pressing";
    public const string VerticalPseudoClass = ":vertical";
    public const string SolidPseudoClass = ":solid";
    public const string CompactPseudoClass = ":compact";

    private const double HighlightInset = 3;
    private static readonly TimeSpan FollowWindow = TimeSpan.FromMilliseconds(600);

    public static readonly StyledProperty<string> LabelProperty =
        AvaloniaProperty.Register<ButtonGroup, string>(nameof(Label), "Actions");
    public static readonly StyledProperty<ButtonGroupVariant> VariantProperty =
        AvaloniaProperty.Register<ButtonGroup, ButtonGroupVariant>(nameof(Variant));
    public static readonly StyledProperty<ControlSize> SizeProperty =
        AvaloniaProperty.Register<ButtonGroup, ControlSize>(nameof(Size), ControlSize.Medium);
    public static readonly StyledProperty<Orientation> OrientationProperty =
        AvaloniaProperty.Register<ButtonGroup, Orientation>(nameof(Orientation), Orientation.Horizontal);
    public static readonly StyledProperty<bool> IsLabelCollapseEnabledProperty =
        AvaloniaProperty.Register<ButtonGroup, bool>(nameof(IsLabelCollapseEnabled), true);
    public static readonly StyledProperty<TimeSpan> ConfirmDurationProperty =
        AvaloniaProperty.Register<ButtonGroup, TimeSpan>(nameof(ConfirmDuration), TimeSpan.FromMilliseconds(1400));
    public static readonly DirectProperty<ButtonGroup, bool> AreLabelsCollapsedProperty =
        AvaloniaProperty.RegisterDirect<ButtonGroup, bool>(nameof(AreLabelsCollapsed), group => group.AreLabelsCollapsed);
    public static readonly RoutedEvent<ButtonGroupActionEventArgs> ActionInvokedEvent =
        RoutedEvent.Register<ButtonGroup, ButtonGroupActionEventArgs>(nameof(ActionInvoked), RoutingStrategies.Bubble);

    public string Label { get => GetValue(LabelProperty); set => SetValue(LabelProperty, value); }
    public ButtonGroupVariant Variant { get => GetValue(VariantProperty); set => SetValue(VariantProperty, value); }
    public ControlSize Size { get => GetValue(SizeProperty); set => SetValue(SizeProperty, value); }
    public Orientation Orientation { get => GetValue(OrientationProperty); set => SetValue(OrientationProperty, value); }
    public bool IsLabelCollapseEnabled { get => GetValue(IsLabelCollapseEnabledProperty); set => SetValue(IsLabelCollapseEnabledProperty, value); }
    public TimeSpan ConfirmDuration { get => GetValue(ConfirmDurationProperty); set => SetValue(ConfirmDurationProperty, value); }

    private bool _labelsCollapsed;
    public bool AreLabelsCollapsed
    {
        get => _labelsCollapsed;
        private set => SetAndRaise(AreLabelsCollapsedProperty, ref _labelsCollapsed, value);
    }

    public event EventHandler<ButtonGroupActionEventArgs>? ActionInvoked
    {
        add => AddHandler(ActionInvokedEvent, value);
        remove => RemoveHandler(ActionInvokedEvent, value);
    }

    private readonly List<Control> _segments = new();
    private readonly List<Control> _navigable = new();
    private GlidingIndicator? _highlight;
    private StackPanel? _row;
    private ButtonGroupItem? _busy;
    private ButtonGroupItem? _done;
    private DispatcherTimer? _confirmTimer;
    private Control? _hover;
    private Control? _pressed;
    private Control? _focused;
    private Control? _shown;
    private Point? _pointerAt;
    private long _travelStamp;
    private double _fullWidth;
    private double _blockedAt;

    static ButtonGroup()
    {
        FocusableProperty.OverrideDefaultValue<ButtonGroup>(false);
    }

    public ButtonGroup()
    {
        KeyboardNavigation.SetTabNavigation(this, KeyboardNavigationMode.Once);
        AddHandler(Button.ClickEvent, OnSegmentClick);
        AddHandler(PointerPressedEvent, OnAnyPointerPressed, RoutingStrategies.Tunnel, handledEventsToo: true);
        AddHandler(PointerReleasedEvent, OnAnyPointerReleased, RoutingStrategies.Bubble, handledEventsToo: true);
        AddHandler(KeyDownEvent, OnTunnelKeyDown, RoutingStrategies.Tunnel);
    }

    internal bool IsSmall => Size == ControlSize.Small;
    internal Control? ActiveSegment => _hover ?? _pressed ?? _focused;

    protected override void OnApplyTemplate(TemplateAppliedEventArgs e)
    {
        base.OnApplyTemplate(e);
        _highlight = e.NameScope.Find<GlidingIndicator>(HighlightPart);
        _row = e.NameScope.Find<StackPanel>(RowPart);
        SyncLayout();
    }

    protected override void OnInitialized()
    {
        base.OnInitialized();
        AutomationProperties.SetName(this, Label);
        SyncLayout();
    }

    protected override void OnPropertyChanged(AvaloniaPropertyChangedEventArgs change)
    {
        base.OnPropertyChanged(change);
        if (change.Property == LabelProperty)
        {
            AutomationProperties.SetName(this, Label);
        }
        else if (change.Property == SizeProperty || change.Property == OrientationProperty || change.Property == VariantProperty)
        {
            SyncLayout();
        }
        else if (change.Property == IsLabelCollapseEnabledProperty)
        {
            InvalidateMeasure();
        }
        else if (change.Property == IsKeyboardFocusWithinProperty && !change.GetNewValue<bool>())
        {
            _focused = null;
            Place();
        }
    }

    private void SyncLayout()
    {
        PseudoClasses.Set(VerticalPseudoClass, Orientation == Orientation.Vertical);
        PseudoClasses.Set(SolidPseudoClass, Variant == ButtonGroupVariant.Solid);
        if (Orientation == Orientation.Vertical && AreLabelsCollapsed)
        {
            SetCollapsed(false);
        }

        for (var i = 0; i < ItemCount; i++)
        {
            if (ContainerFromIndex(i) is ButtonGroupItem item)
            {
                item.SyncFromGroup();
            }
        }

        Place();
    }

    protected override bool NeedsContainerOverride(object? item, int index, out object? recycleKey)
    {
        if (item is ButtonGroupItem)
        {
            recycleKey = null;
            return false;
        }

        return NeedsContainer<ButtonGroupItem>(item, out recycleKey);
    }

    protected override Control CreateContainerForItemOverride(object? item, int index, object? recycleKey) => new ButtonGroupItem();

    protected override void PrepareContainerForItemOverride(Control container, object? item, int index)
    {
        base.PrepareContainerForItemOverride(container, item, index);
        if (container is ButtonGroupItem segment)
        {
            if (!ReferenceEquals(item, segment) && string.IsNullOrEmpty(segment.Label))
            {
                segment.Label = item?.ToString() ?? string.Empty;
            }

            segment.Group = this;
            segment.PropertyChanged += OnSegmentPropertyChanged;
            segment.SyncFromGroup();
        }
    }

    protected override void ContainerForItemPreparedOverride(Control container, object? item, int index)
    {
        base.ContainerForItemPreparedOverride(container, item, index);
        Place();
    }

    protected override void ClearContainerForItemOverride(Control container)
    {
        base.ClearContainerForItemOverride(container);
        if (container is not ButtonGroupItem segment)
        {
            return;
        }

        segment.PropertyChanged -= OnSegmentPropertyChanged;
        segment.Group = null;
        if (ReferenceEquals(_hover, segment)) _hover = null;
        if (ReferenceEquals(_pressed, segment)) _pressed = null;
        if (ReferenceEquals(_focused, segment)) _focused = null;
        if (ReferenceEquals(_busy, segment)) _busy = null;
        if (ReferenceEquals(_done, segment)) EndConfirm();
        Place();
    }

    private List<Control> Segments()
    {
        _segments.Clear();
        for (var i = 0; i < ItemCount; i++)
        {
            if (ContainerFromIndex(i) is ButtonGroupItem { IsVisible: true } segment)
            {
                _segments.Add(segment);
            }
        }

        return _segments;
    }

    private static bool IsInert(Control segment) => segment is ButtonGroupItem item
        ? item.IsInert
        : !segment.IsEffectivelyEnabled;

    private Rect? Box(Control segment)
    {
        if (_highlight is null || segment.TranslatePoint(default, _highlight) is not { } origin)
        {
            return null;
        }

        return new Rect(origin, segment.Bounds.Size).Deflate(HighlightInset);
    }

    private void Place()
    {
        var segments = Segments();
        var active = ActiveSegment;
        var activeIndex = active is null ? -1 : segments.IndexOf(active);
        for (var i = 0; i < segments.Count; i++)
        {
            var joined = i > 0;
            var quiet = activeIndex >= 0 && (i == activeIndex || i == activeIndex + 1);
            if (segments[i] is ButtonGroupItem item)
            {
                item.SetSegmentState(joined, quiet, i == activeIndex);
            }
        }

        PseudoClasses.Set(PressingPseudoClass, _pressed is not null && ReferenceEquals(_pressed, active));
        if (_highlight is null)
        {
            return;
        }

        if (active is null || activeIndex < 0 || (IsInert(active) && !ReferenceEquals(active, _focused)) || Box(active) is not { } target)
        {
            _highlight.Hide(IsLoaded);
            _shown = null;
            return;
        }

        var animate = IsLoaded && ArcMotion.IsFull(this);
        if (_shown is null || !_highlight.HasTarget)
        {
            _highlight.MoveTo(target, animate);
        }
        else if (!ReferenceEquals(active, _shown))
        {
            _highlight.MoveTo(target, animate, ArcSpring.Snappy);
            _travelStamp = Stopwatch.GetTimestamp();
        }
        else
        {
            _highlight.MoveTo(target, animate && Stopwatch.GetElapsedTime(_travelStamp) < FollowWindow, ArcSpring.Snappy);
        }

        _shown = active;
    }

    private void Follow(Control segment)
    {
        if (_highlight is null || !ReferenceEquals(segment, _shown) || Box(segment) is not { } target)
        {
            return;
        }

        var animate = IsLoaded && ArcMotion.IsFull(this) && Stopwatch.GetElapsedTime(_travelStamp) < FollowWindow;
        _highlight.MoveTo(target, animate, ArcSpring.Snappy);
    }

    private void OnSegmentPropertyChanged(object? sender, AvaloniaPropertyChangedEventArgs e)
    {
        if (sender is not Control segment)
        {
            return;
        }

        if (e.Property == BoundsProperty)
        {
            Follow(segment);
        }
        else if (e.Property == Button.IsPressedProperty && !e.GetNewValue<bool>() && ReferenceEquals(segment, _pressed))
        {
            _pressed = null;
            Place();
        }
        else if (e.Property == IsEffectivelyEnabledProperty || e.Property == ButtonGroupItem.IsDisabledProperty ||
                 e.Property == IsVisibleProperty)
        {
            Place();
        }
    }

    private Control? HitSegment(PointerEventArgs e)
    {
        foreach (var segment in Segments())
        {
            var point = e.GetPosition(segment);
            if (point.X >= 0 && point.Y >= 0 && point.X < segment.Bounds.Width && point.Y < segment.Bounds.Height)
            {
                return IsInert(segment) ? null : segment;
            }
        }

        return null;
    }

    protected override void OnPointerMoved(PointerEventArgs e)
    {
        base.OnPointerMoved(e);
        if (e.Pointer.Type == PointerType.Touch)
        {
            return;
        }

        var at = e.GetPosition(this);
        if (_pointerAt is { } last && Math.Abs(last.X - at.X) < 1 && Math.Abs(last.Y - at.Y) < 1)
        {
            return;
        }

        _pointerAt = at;
        var hit = HitSegment(e);
        if (!ReferenceEquals(hit, _hover))
        {
            _hover = hit;
            Place();
        }
    }

    protected override void OnPointerExited(PointerEventArgs e)
    {
        base.OnPointerExited(e);
        if (e.Pointer.Type == PointerType.Mouse)
        {
            _pointerAt = null;
            _hover = null;
            Place();
        }
    }

    private void OnAnyPointerPressed(object? sender, PointerPressedEventArgs e)
    {
        if (e.Pointer.Type == PointerType.Mouse && !e.GetCurrentPoint(this).Properties.IsLeftButtonPressed)
        {
            return;
        }

        _pressed = HitSegment(e);
        Place();
    }

    private void OnAnyPointerReleased(object? sender, PointerReleasedEventArgs e)
    {
        if (_pressed is not null)
        {
            _pressed = null;
            Place();
        }
    }

    internal void OnSegmentFocused(Control segment, NavigationMethod method)
    {
        _focused = method is NavigationMethod.Tab or NavigationMethod.Directional ? segment : null;
        Place();
    }

    private void OnTunnelKeyDown(object? sender, KeyEventArgs e)
    {
        if (e.Handled || e.KeyModifiers != KeyModifiers.None)
        {
            return;
        }

        _navigable.Clear();
        var index = -1;
        foreach (var segment in Segments())
        {
            if (!segment.IsEffectivelyEnabled)
            {
                continue;
            }

            if (segment.IsFocused)
            {
                index = _navigable.Count;
            }

            _navigable.Add(segment);
        }

        if (index < 0 || _navigable.Count == 0)
        {
            return;
        }

        var vertical = Orientation == Orientation.Vertical;
        var rtl = !vertical && this.FlowDirection == Avalonia.Media.FlowDirection.RightToLeft;
        var forward = vertical ? Key.Down : rtl ? Key.Left : Key.Right;
        var backward = vertical ? Key.Up : rtl ? Key.Right : Key.Left;
        var last = _navigable.Count - 1;
        var target = e.Key == forward ? (index == last ? 0 : index + 1)
            : e.Key == backward ? (index == 0 ? last : index - 1)
            : e.Key == Key.Home ? 0
            : e.Key == Key.End ? last
            : -1;
        if (target < 0)
        {
            return;
        }

        e.Handled = true;
        _navigable[target].Focus(NavigationMethod.Directional);
    }

    private void OnSegmentClick(object? sender, RoutedEventArgs e)
    {
        if (e.Source is ButtonGroupItem item && ReferenceEquals(item.Group, this))
        {
            var data = ItemFromContainer(item) ?? item;
            RaiseEvent(new ButtonGroupActionEventArgs(ActionInvokedEvent, item, data));
        }
    }

    internal bool BeginInvoke(ButtonGroupItem item)
    {
        if (_busy != null)
        {
            return false;
        }

        _busy = item;
        EndConfirm();
        return true;
    }

    internal void EndInvoke(ButtonGroupItem item)
    {
        if (!ReferenceEquals(_busy, item))
        {
            return;
        }

        _busy = null;
        Place();
    }

    internal void Confirm(ButtonGroupItem item)
    {
        EndConfirm();
        _done = item;
        item.SetDone(true);
        Place();
        _confirmTimer ??= new DispatcherTimer();
        _confirmTimer.Interval = ConfirmDuration > TimeSpan.Zero ? ConfirmDuration : TimeSpan.FromMilliseconds(1);
        _confirmTimer.Tick -= OnConfirmElapsed;
        _confirmTimer.Tick += OnConfirmElapsed;
        _confirmTimer.Start();
    }

    private void OnConfirmElapsed(object? sender, EventArgs e) => EndConfirm();

    private void EndConfirm()
    {
        _confirmTimer?.Stop();
        _done?.SetDone(false);
        _done = null;
        Place();
    }

    private bool CanCollapse => IsLabelCollapseEnabled && Orientation == Orientation.Horizontal &&
        Enumerable.Range(0, ItemCount).Any(i => ContainerFromIndex(i) is ButtonGroupItem { IsCollapsible: true });

    private void SetCollapsed(bool collapsed)
    {
        if (AreLabelsCollapsed == collapsed)
        {
            return;
        }

        AreLabelsCollapsed = collapsed;
        PseudoClasses.Set(CompactPseudoClass, collapsed);
        for (var i = 0; i < ItemCount; i++)
        {
            if (ContainerFromIndex(i) is ButtonGroupItem item)
            {
                item.SyncFromGroup();
            }
        }
    }

    protected override Size MeasureOverride(Size availableSize)
    {
        var size = base.MeasureOverride(availableSize);
        if (!CanCollapse)
        {
            if (AreLabelsCollapsed)
            {
                SetCollapsed(false);
                size = base.MeasureOverride(availableSize);
            }

            return size;
        }

        if (double.IsInfinity(availableSize.Width) || _row is null)
        {
            return size;
        }

        var available = availableSize.Width;
        if (!AreLabelsCollapsed)
        {
            var natural = _row.DesiredSize.Width + BorderThickness.Left + BorderThickness.Right + 6;
            if (natural > available + 1)
            {
                _fullWidth = natural + 2;
                _blockedAt = available;
                SetCollapsed(true);
                size = base.MeasureOverride(availableSize);
            }
        }
        else if (available > _blockedAt && available >= _fullWidth)
        {
            SetCollapsed(false);
            size = base.MeasureOverride(availableSize);
        }

        return size;
    }

    protected override void OnAttachedToVisualTree(VisualTreeAttachmentEventArgs e)
    {
        base.OnAttachedToVisualTree(e);
        AutomationProperties.SetName(this, Label);
        SyncLayout();
    }

    protected override void OnDetachedFromVisualTree(VisualTreeAttachmentEventArgs e)
    {
        _confirmTimer?.Stop();
        _busy = null;
        EndConfirm();
        _hover = _pressed = _focused = null;
        _pointerAt = null;
        base.OnDetachedFromVisualTree(e);
    }

    protected override AutomationPeer OnCreateAutomationPeer() => new ButtonGroupAutomationPeer(this);

    private sealed class ButtonGroupAutomationPeer(ButtonGroup owner) : ItemsControlAutomationPeer(owner)
    {
        protected override AutomationControlType GetAutomationControlTypeCore() => AutomationControlType.Group;
    }
}
