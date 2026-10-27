using Avalonia;
using Avalonia.Automation;
using Avalonia.Automation.Peers;
using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.VisualTree;

namespace AcrfAnnotationTool.Controls;

// Adapted from Arc's ExpandingButtonGroup; icons and commands remain native Avalonia controls.
public class ExpandingButtonGroup : ItemsControl
{
    public static readonly StyledProperty<string> LabelProperty =
        AvaloniaProperty.Register<ExpandingButtonGroup, string>(nameof(Label), "Actions");
    public static readonly StyledProperty<ExpandingButtonGroupItem?> DefaultExpandedItemProperty =
        AvaloniaProperty.Register<ExpandingButtonGroup, ExpandingButtonGroupItem?>(nameof(DefaultExpandedItem));

    public string Label { get => GetValue(LabelProperty); set => SetValue(LabelProperty, value); }
    public ExpandingButtonGroupItem? DefaultExpandedItem { get => GetValue(DefaultExpandedItemProperty); set => SetValue(DefaultExpandedItemProperty, value); }
    public ExpandingButtonGroupItem? ExpandedItem { get; private set; }
    public static readonly StyledProperty<TimeSpan> ConfirmDurationProperty =
        AvaloniaProperty.Register<ExpandingButtonGroup, TimeSpan>(nameof(ConfirmDuration), TimeSpan.FromMilliseconds(1400));
    public TimeSpan ConfirmDuration { get => GetValue(ConfirmDurationProperty); set => SetValue(ConfirmDurationProperty, value); }
    private ExpandingButtonGroupItem? _busy, _done;
    private Avalonia.Threading.DispatcherTimer? _confirmTimer;
    private ExpandingButtonGroupItem? _pointerOver, _keyFocus;
    private Point? _pointerAt;
    private (ExpandingButtonGroupItem Item, bool Expanded)? _touchPress;
    private TopLevel? _outsideWatch;
    private List<ExpandingButtonGroupItem> Buttons => GetRealizedContainers().OfType<ExpandingButtonGroupItem>().Where(b => b.IsVisible).ToList();

    public ExpandingButtonGroup()
    {
        KeyboardNavigation.SetTabNavigation(this, KeyboardNavigationMode.Once);
    }

    protected override bool NeedsContainerOverride(object? item, int index, out object? recycleKey) =>
        NeedsContainer<ExpandingButtonGroupItem>(item, out recycleKey);
    protected override Control CreateContainerForItemOverride(object? item, int index, object? recycleKey) => new ExpandingButtonGroupItem();
    protected override void PrepareContainerForItemOverride(Control container, object? item, int index)
    {
        base.PrepareContainerForItemOverride(container, item, index);
        if (container is ExpandingButtonGroupItem button) button.Group = this;
    }
    protected override void ContainerForItemPreparedOverride(Control container, object? item, int index)
    {
        base.ContainerForItemPreparedOverride(container, item, index);
        Refresh();
    }
    protected override void ClearContainerForItemOverride(Control container)
    {
        if (container is ExpandingButtonGroupItem button)
        {
            if (_busy == button) _busy = null;
            if (_done == button) EndConfirm();
            button.Group = null;
            if (_pointerOver == button) _pointerOver = null;
            if (_keyFocus == button) _keyFocus = null;
        }
        base.ClearContainerForItemOverride(container);
        Refresh();
    }

    private void Refresh()
    {
        var buttons = Buttons;
        var rest = buttons.Contains(DefaultExpandedItem!) ? DefaultExpandedItem : buttons.FirstOrDefault(b => b.IsEffectivelyEnabled);
        ExpandedItem = _busy ?? _done ?? _pointerOver ?? _keyFocus ?? rest;
        foreach (var button in buttons) button.SetExpanded(button == ExpandedItem);
        if ((_keyFocus ?? rest) is { } stop) KeyboardNavigation.SetTabOnceActiveElement(this, stop);
    }

    internal bool BeginInvoke(ExpandingButtonGroupItem item)
    {
        if (_busy != null) return false;
        _busy = item;
        EndConfirm();
        return true;
    }
    internal void EndInvoke(ExpandingButtonGroupItem item)
    {
        if (_busy != item) return;
        _busy = null;
        Refresh();
    }

    internal void Confirm(ExpandingButtonGroupItem item)
    {
        EndConfirm();
        _done = item;
        item.SetDone(true);
        Refresh();
        _confirmTimer ??= new Avalonia.Threading.DispatcherTimer();
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
        Refresh();
    }

    internal void FocusItem(ExpandingButtonGroupItem item, NavigationMethod method)
    {
        if (method is NavigationMethod.Tab or NavigationMethod.Directional) _keyFocus = item;
        Refresh();
    }
    internal void PressItem(ExpandingButtonGroupItem item, PointerType type)
    {
        _touchPress = type == PointerType.Touch ? (item, item == ExpandedItem) : null;
    }
    internal bool ShouldInvoke(ExpandingButtonGroupItem item)
    {
        var press = _touchPress;
        _touchPress = null;
        if (press is { Expanded: false } && press.Value.Item == item)
        {
            _pointerOver = item;
            Refresh();
            WatchOutside();
            return false;
        }
        return true;
    }
    private void WatchOutside()
    {
        if (_outsideWatch != null) return;
        _outsideWatch = TopLevel.GetTopLevel(this);
        _outsideWatch?.AddHandler(PointerPressedEvent, OutsidePressed, Avalonia.Interactivity.RoutingStrategies.Tunnel, handledEventsToo: true);
    }
    private void OutsidePressed(object? sender, PointerPressedEventArgs e)
    {
        if (e.Source is Visual source && (source == this || this.IsVisualAncestorOf(source))) return;
        _pointerOver = null;
        Refresh();
        UnwatchOutside();
    }
    private void UnwatchOutside()
    {
        _outsideWatch?.RemoveHandler(PointerPressedEvent, OutsidePressed);
        _outsideWatch = null;
    }

    protected override void OnPointerMoved(PointerEventArgs e)
    {
        base.OnPointerMoved(e);
        if (e.Pointer.Type != PointerType.Mouse) return;
        var at = e.GetPosition(this);
        if (_pointerAt is { } last && Math.Abs(last.X - at.X) < 1 && Math.Abs(last.Y - at.Y) < 1) return;
        _pointerAt = at;
        foreach (var button in Buttons)
        {
            if (button.TranslatePoint(default, this) is not { } origin) continue;
            var box = new Rect(origin, button.Bounds.Size);
            if (!box.Contains(at) || button == _pointerOver) continue;
            if (_pointerOver != null && (at.X < box.Left + 4 || at.X > box.Right - 4)) return;
            _pointerOver = button;
            Refresh();
            return;
        }
    }
    protected override void OnPointerExited(PointerEventArgs e)
    {
        base.OnPointerExited(e);
        if (e.Pointer.Type != PointerType.Mouse) return;
        _pointerAt = null;
        _pointerOver = null;
        Refresh();
    }
    protected override void OnKeyDown(KeyEventArgs e)
    {
        base.OnKeyDown(e);
        if (e.Handled) return;
        var buttons = Buttons.Where(b => b.IsEffectivelyEnabled).ToList();
        var index = buttons.FindIndex(b => b.IsFocused);
        if (index < 0) return;
        var target = e.Key switch
        {
            Key.Right => (index + 1) % buttons.Count,
            Key.Left => (index + buttons.Count - 1) % buttons.Count,
            Key.Home => 0,
            Key.End => buttons.Count - 1,
            _ => -1
        };
        if (target < 0) return;
        e.Handled = true;
        buttons[target].Focus(NavigationMethod.Directional);
    }
    protected override void OnPropertyChanged(AvaloniaPropertyChangedEventArgs change)
    {
        base.OnPropertyChanged(change);
        if (change.Property == LabelProperty) AutomationProperties.SetName(this, Label);
        else if (change.Property == DefaultExpandedItemProperty) Refresh();
        else if (change.Property == IsKeyboardFocusWithinProperty && !IsKeyboardFocusWithin)
        {
            _keyFocus = null;
            Refresh();
        }
    }
    protected override void OnAttachedToVisualTree(VisualTreeAttachmentEventArgs e)
    {
        base.OnAttachedToVisualTree(e);
        AutomationProperties.SetName(this, Label);
        Refresh();
    }
    protected override void OnDetachedFromVisualTree(VisualTreeAttachmentEventArgs e)
    {
        UnwatchOutside();
        _busy = null;
        EndConfirm();
        _pointerOver = _keyFocus = null;
        _pointerAt = null;
        _touchPress = null;
        base.OnDetachedFromVisualTree(e);
    }
    protected override AutomationPeer OnCreateAutomationPeer() => new GroupPeer(this);
    private sealed class GroupPeer(ExpandingButtonGroup owner) : ItemsControlAutomationPeer(owner)
    {
        protected override AutomationControlType GetAutomationControlTypeCore() => AutomationControlType.ToolBar;
    }
}
