using System.Windows.Input;
using Avalonia;
using Avalonia.Automation;
using Avalonia.Controls;
using Avalonia.Controls.Primitives;
using Avalonia.Controls.Primitives.PopupPositioning;
using Avalonia.Data;
using Avalonia.Input;
using Avalonia.Interactivity;
using Avalonia.Threading;
using Avalonia.VisualTree;
using AvaloniaButton = Avalonia.Controls.Button;

namespace AcrfAnnotationTool.Controls;

/// <summary>Side of the trigger an anchored surface opens on.</summary>
public enum PopoverPlacement
{
    Top,
    Bottom,
    Left,
    Right
}

/// <summary>Alignment of an anchored surface along the trigger edge.</summary>
public enum PopoverAlignment
{
    Start,
    Center,
    End
}

/// <summary>
/// A small surface anchored to a trigger and opened by a click, for interactive content that should not block the page.
/// Two ways to use it: put the trigger in <see cref="Trigger"/> with the content as <see cref="ContentControl.Content"/>,
/// or attach it to any control with <see cref="AttachProperty"/>. Escape and a press outside close it and return focus
/// to the trigger; focus moves into the panel on open.
/// </summary>
public class Popover : ContentControl
{
    #region Properties

    public static readonly StyledProperty<Control?> TriggerProperty =
        AvaloniaProperty.Register<Popover, Control?>(nameof(Trigger));

    public static readonly StyledProperty<bool> IsOpenProperty =
        AvaloniaProperty.Register<Popover, bool>(nameof(IsOpen), defaultBindingMode: BindingMode.TwoWay);

    public static readonly StyledProperty<PopoverPlacement> PlacementProperty =
        AvaloniaProperty.Register<Popover, PopoverPlacement>(nameof(Placement), PopoverPlacement.Bottom);

    public static readonly StyledProperty<PopoverAlignment> AlignmentProperty =
        AvaloniaProperty.Register<Popover, PopoverAlignment>(nameof(Alignment), PopoverAlignment.Start);

    public static readonly StyledProperty<double> OffsetProperty =
        AvaloniaProperty.Register<Popover, double>(nameof(Offset), 6);

    public static readonly StyledProperty<bool> IsModalProperty =
        AvaloniaProperty.Register<Popover, bool>(nameof(IsModal));

    /// <summary>Attaches a popover to any control: a click on the control toggles it.</summary>
    public static readonly AttachedProperty<Popover?> AttachProperty =
        AvaloniaProperty.RegisterAttached<Popover, Control, Popover?>("Attach");

    public static Popover? GetAttach(Control element) => element.GetValue(AttachProperty);

    public static void SetAttach(Control element, Popover? value) => element.SetValue(AttachProperty, value);

    /// <summary>The control that opens the panel, shown in place of the popover (optional with <see cref="AttachProperty"/>).</summary>
    public Control? Trigger { get => GetValue(TriggerProperty); set => SetValue(TriggerProperty, value); }

    /// <summary>Open state (two-way).</summary>
    public bool IsOpen { get => GetValue(IsOpenProperty); set => SetValue(IsOpenProperty, value); }

    /// <summary>Preferred side (flips when it does not fit).</summary>
    public PopoverPlacement Placement { get => GetValue(PlacementProperty); set => SetValue(PlacementProperty, value); }

    /// <summary>Alignment along the trigger edge (default Start).</summary>
    public PopoverAlignment Alignment { get => GetValue(AlignmentProperty); set => SetValue(AlignmentProperty, value); }

    /// <summary>Gap to the trigger in px (default 6).</summary>
    public double Offset { get => GetValue(OffsetProperty); set => SetValue(OffsetProperty, value); }

    /// <summary>Modal: presses outside are swallowed (they only close the popover). Default false.</summary>
    public bool IsModal { get => GetValue(IsModalProperty); set => SetValue(IsModalProperty, value); }

    #endregion

    /// <summary>Raised when the popover opens.</summary>
    public event EventHandler? Opened;

    /// <summary>Raised when the popover closes.</summary>
    public event EventHandler? Closed;

    public ICommand OpenCommand { get; }

    /// <summary>Closes the popover.</summary>
    public ICommand CloseCommand { get; }

    private Popup? _popup;
    private PopoverSurface? _surface;
    private TopLevel? _dismissRoot;
    private Control? _attachedTarget;
    private Control? _showAnchor;
    private Control? _hookedTrigger;
    private bool _parentedToTarget;
    private bool _pendingOpen;

    static Popover()
    {
        AttachProperty.Changed.AddClassHandler<Control>(OnAttachChanged);
        FocusableProperty.OverrideDefaultValue<Popover>(false);
    }

    public Popover()
    {
        OpenCommand = new PopoverCommand(() => SetCurrentValue(IsOpenProperty, true));
        CloseCommand = new PopoverCommand(Close);
    }

    /// <summary>The surface (created on first open).</summary>
    public PopoverSurface? Surface => _surface;

    /// <summary>The popup hosting the surface (created on first open).</summary>
    public Popup? Popup => _popup;

    /// <summary>The control the panel is anchored to.</summary>
    public Control? Anchor => _showAnchor ?? _attachedTarget ?? Trigger;

    /// <summary>Opens the panel anchored to <paramref name="anchor"/> (any control in a window).</summary>
    public void ShowAt(Control anchor)
    {
        if (IsOpen && !ReferenceEquals(anchor, Anchor))
        {
            SetCurrentValue(IsOpenProperty, false);
        }
        _showAnchor = anchor;
        SetCurrentValue(IsOpenProperty, true);
    }

    /// <summary>Closes the panel.</summary>
    public void Close() => SetCurrentValue(IsOpenProperty, false);

    #region Attached usage

    private static void OnAttachChanged(Control target, AvaloniaPropertyChangedEventArgs e)
    {
        if (e.OldValue is Popover old && ReferenceEquals(old._attachedTarget, target))
        {
            old.Close();
            old.UnhookTrigger();
            old._attachedTarget = null;
        }
        if (e.NewValue is Popover popover)
        {
            popover._attachedTarget = target;
            popover.HookTrigger(target);
        }
    }

    #endregion

    #region Trigger

    protected override void OnPropertyChanged(AvaloniaPropertyChangedEventArgs change)
    {
        base.OnPropertyChanged(change);
        if (change.Property == TriggerProperty)
        {
            if (_attachedTarget is null)
            {
                HookTrigger(change.GetNewValue<Control?>());
            }
        }
        else if (change.Property == IsOpenProperty)
        {
            if (change.GetNewValue<bool>())
            {
                OpenCore();
            }
            else
            {
                CloseCore();
            }
        }
        else if (IsOpen && (change.Property == PlacementProperty || change.Property == AlignmentProperty || change.Property == OffsetProperty))
        {
            ApplyPlacement();
        }
    }

    private void HookTrigger(Control? trigger)
    {
        UnhookTrigger();
        _hookedTrigger = trigger;
        if (trigger is null)
        {
            return;
        }
        if (trigger is AvaloniaButton button)
        {
            button.Click += OnTriggerClick;
        }
        else
        {
            trigger.Tapped += OnTriggerTapped;
            trigger.KeyDown += OnTriggerKeyDown;
        }
        trigger.AttachedToVisualTree += OnTriggerAttached;
        trigger.DetachedFromVisualTree += OnTriggerDetached;
        UpdateTriggerState();
    }

    private void UnhookTrigger()
    {
        if (_hookedTrigger is null)
        {
            return;
        }
        if (_hookedTrigger is AvaloniaButton button)
        {
            button.Click -= OnTriggerClick;
        }
        else
        {
            _hookedTrigger.Tapped -= OnTriggerTapped;
            _hookedTrigger.KeyDown -= OnTriggerKeyDown;
        }
        _hookedTrigger.AttachedToVisualTree -= OnTriggerAttached;
        _hookedTrigger.DetachedFromVisualTree -= OnTriggerDetached;
        _hookedTrigger = null;
    }

    private void OnTriggerAttached(object? sender, VisualTreeAttachmentEventArgs e)
    {
        if (_pendingOpen && IsOpen)
        {
            OpenCore();
        }
    }

    private void OnTriggerDetached(object? sender, VisualTreeAttachmentEventArgs e)
    {
        // The trigger left the window (page navigated away): close without waiting for a dismissal.
        if (IsOpen && ReferenceEquals(sender, Anchor))
        {
            SetCurrentValue(IsOpenProperty, false);
        }
    }

    private void OnTriggerClick(object? sender, RoutedEventArgs e) => Toggle(sender as Control);

    private void OnTriggerTapped(object? sender, TappedEventArgs e) => Toggle(sender as Control);

    private void OnTriggerKeyDown(object? sender, KeyEventArgs e)
    {
        if (e.Key is Key.Enter or Key.Space && !IsOpen)
        {
            e.Handled = true;
            Toggle(sender as Control);
        }
    }

    private void Toggle(Control? trigger)
    {
        if (IsOpen)
        {
            SetCurrentValue(IsOpenProperty, false);
            return;
        }
        _showAnchor = trigger;
        SetCurrentValue(IsOpenProperty, true);
    }

    private void UpdateTriggerState()
    {
        if (_hookedTrigger is { } trigger)
        {
            // Mirrors aria-expanded for assistive technology.
            AutomationProperties.SetItemStatus(trigger, IsOpen ? "Expanded" : "Collapsed");
        }
    }

    #endregion

    #region Open / close

    private void EnsurePopup()
    {
        if (_popup is not null)
        {
            return;
        }
        _surface = new PopoverSurface();
        _surface.Bind(ContentProperty, this.GetObservable(ContentProperty));
        _surface.Bind(ContentTemplateProperty, this.GetObservable(ContentTemplateProperty));
        _surface.AddHandler(KeyDownEvent, OnSurfaceKeyDown, RoutingStrategies.Tunnel | RoutingStrategies.Bubble, handledEventsToo: true);
        _popup = new Popup
        {
            Child = _surface,
            IsLightDismissEnabled = false,
            WindowManagerAddShadowHint = false,
            // The overlay layer shares this top level, so the surface's transparent shadow room is not
            // clipped by a native popup window.
            ShouldUseOverlayLayer = true,
            Placement = PlacementMode.Custom,
            CustomPopupPlacementCallback = PlacePopup,
            PlacementConstraintAdjustment = PopupPositionerConstraintAdjustment.None
        };
        _popup.Closed += OnPopupClosed;
        LogicalChildren.Add(_popup);
    }

    private void OpenCore()
    {
        var anchor = Anchor ?? this;
        if (TopLevel.GetTopLevel(anchor) is null)
        {
            _pendingOpen = true;
            return;
        }
        _pendingOpen = false;
        EnsurePopup();
        if (Parent is null && _attachedTarget is not null)
        {
            // Attached popovers join their target's logical tree while open (styles, DataContext).
            ((ISetLogicalParent)this).SetParent(_attachedTarget);
            _parentedToTarget = true;
        }
        if (AutomationProperties.GetName(this) is { Length: > 0 } name)
        {
            AutomationProperties.SetName(_surface!, name);
        }
        AttachDismissHandlers();
        ApplyPlacement();
        _popup!.IsOpen = true;
        UpdateTriggerState();
        Dispatcher.UIThread.Post(() =>
        {
            if (!IsOpen || _surface is null)
            {
                return;
            }
            Opened?.Invoke(this, EventArgs.Empty);
            // Focus moves into the panel (first focusable element, or the panel itself).
            (FocusManager.FindFirstFocusableElement(_surface) ?? _surface).Focus(NavigationMethod.Unspecified);
        }, DispatcherPriority.Input);
    }

    private void ApplyPlacement()
    {
        if (_popup is null)
        {
            return;
        }
        _popup.PlacementTarget = Anchor;
        _popup.Placement = PlacementMode.Custom;
        _popup.CustomPopupPlacementCallback = PlacePopup;
        _popup.PlacementConstraintAdjustment = PopupPositionerConstraintAdjustment.None;

        // The offsets exist only so a change to the placement inputs repositions the popup; PlacePopup
        // computes the actual position from the shadow room.
        var (_, _, offset) = GetPlacement(Placement, Alignment, ShadowRoom, Offset);
        _popup.HorizontalOffset = offset.X;
        _popup.VerticalOffset = offset.Y;
    }

    private Thickness ShadowRoom => _surface?.ShadowRoom ?? default;

    private void PlacePopup(CustomPopupPlacement placement)
    {
        var room = ShadowRoom;
        var topLevel = TopLevel.GetTopLevel(placement.Target);
        var hasBounds = topLevel is not null;
        var bounds = hasBounds ? new Rect(topLevel!.ClientSize) : default;
        var side = hasBounds
            ? ChooseSide(placement.AnchorRectangle, placement.PopupSize, bounds, room)
            : Placement;
        var (anchor, gravity, offset) = GetPlacement(side, Alignment, room, Offset);
        if (hasBounds)
        {
            var popupRect = GetPopupRect(placement.AnchorRectangle, placement.PopupSize, anchor, gravity, offset);
            offset = ClampOffset(popupRect, placement.PopupSize, bounds, offset);
        }
        placement.Anchor = anchor;
        placement.Gravity = gravity;
        placement.Offset = offset;
        placement.ConstraintAdjustment = PopupPositionerConstraintAdjustment.None;
    }

    private PopoverPlacement ChooseSide(Rect anchorRect, Size popupSize, Rect bounds, Thickness room)
    {
        if (Fits(Placement, anchorRect, popupSize, bounds, room))
        {
            return Placement;
        }
        var opposite = Placement switch
        {
            PopoverPlacement.Top => PopoverPlacement.Bottom,
            PopoverPlacement.Bottom => PopoverPlacement.Top,
            PopoverPlacement.Left => PopoverPlacement.Right,
            _ => PopoverPlacement.Left
        };
        return Fits(opposite, anchorRect, popupSize, bounds, room) ? opposite : Placement;
    }

    private bool Fits(PopoverPlacement side, Rect anchorRect, Size popupSize, Rect bounds, Thickness room)
    {
        var (anchor, gravity, offset) = GetPlacement(side, Alignment, room, Offset);
        var popupRect = GetPopupRect(anchorRect, popupSize, anchor, gravity, offset);
        return side switch
        {
            PopoverPlacement.Top => popupRect.Top >= bounds.Top,
            PopoverPlacement.Bottom => popupRect.Bottom <= bounds.Bottom,
            PopoverPlacement.Left => popupRect.Left >= bounds.Left,
            _ => popupRect.Right <= bounds.Right
        };
    }

    private static (PopupAnchor Anchor, PopupGravity Gravity, Point Offset) GetPlacement(
        PopoverPlacement side,
        PopoverAlignment alignment,
        Thickness room,
        double gap)
    {
        var left = room.Left;
        var top = room.Top;
        var right = room.Right;
        var bottom = room.Bottom;
        var centerX = (right - left) / 2;
        var centerY = (bottom - top) / 2;
        return side switch
        {
            PopoverPlacement.Top => alignment switch
            {
                PopoverAlignment.Center => (PopupAnchor.Top, PopupGravity.Top, new Point(centerX, -gap + bottom)),
                PopoverAlignment.End => (PopupAnchor.TopRight, PopupGravity.TopLeft, new Point(right, -gap + bottom)),
                _ => (PopupAnchor.TopLeft, PopupGravity.TopRight, new Point(-left, -gap + bottom))
            },
            PopoverPlacement.Left => alignment switch
            {
                PopoverAlignment.Center => (PopupAnchor.Left, PopupGravity.Left, new Point(-gap + right, centerY)),
                PopoverAlignment.End => (PopupAnchor.BottomLeft, PopupGravity.TopLeft, new Point(-gap + right, bottom)),
                _ => (PopupAnchor.TopLeft, PopupGravity.BottomLeft, new Point(-gap + right, -top))
            },
            PopoverPlacement.Right => alignment switch
            {
                PopoverAlignment.Center => (PopupAnchor.Right, PopupGravity.Right, new Point(gap - left, centerY)),
                PopoverAlignment.End => (PopupAnchor.BottomRight, PopupGravity.TopRight, new Point(gap - left, bottom)),
                _ => (PopupAnchor.TopRight, PopupGravity.BottomRight, new Point(gap - left, -top))
            },
            _ => alignment switch
            {
                PopoverAlignment.Center => (PopupAnchor.Bottom, PopupGravity.Bottom, new Point(centerX, gap - top)),
                PopoverAlignment.End => (PopupAnchor.BottomRight, PopupGravity.BottomLeft, new Point(right, gap - top)),
                _ => (PopupAnchor.BottomLeft, PopupGravity.BottomRight, new Point(-left, gap - top))
            }
        };
    }

    private static Rect GetPopupRect(
        Rect anchorRect,
        Size popupSize,
        PopupAnchor anchor,
        PopupGravity gravity,
        Point offset)
    {
        var anchorPoint = GetAnchorPoint(anchorRect, anchor);
        var x = (gravity & PopupGravity.Left) != 0
            ? anchorPoint.X - popupSize.Width
            : (gravity & PopupGravity.Right) != 0
                ? anchorPoint.X
                : anchorPoint.X - popupSize.Width / 2;
        var y = (gravity & PopupGravity.Top) != 0
            ? anchorPoint.Y - popupSize.Height
            : (gravity & PopupGravity.Bottom) != 0
                ? anchorPoint.Y
                : anchorPoint.Y - popupSize.Height / 2;
        return new Rect(x + offset.X, y + offset.Y, popupSize.Width, popupSize.Height);
    }

    private static Point GetAnchorPoint(Rect rect, PopupAnchor anchor)
    {
        var x = (anchor & PopupAnchor.Left) != 0
            ? rect.Left
            : (anchor & PopupAnchor.Right) != 0
                ? rect.Right
                : rect.Left + rect.Width / 2;
        var y = (anchor & PopupAnchor.Top) != 0
            ? rect.Top
            : (anchor & PopupAnchor.Bottom) != 0
                ? rect.Bottom
                : rect.Top + rect.Height / 2;
        return new Point(x, y);
    }

    private static Point ClampOffset(Rect popupRect, Size popupSize, Rect bounds, Point offset)
    {
        var x = ClampAxis(popupRect.X, popupSize.Width, bounds.Left, bounds.Right);
        var y = ClampAxis(popupRect.Y, popupSize.Height, bounds.Top, bounds.Bottom);
        return new Point(offset.X + x - popupRect.X, offset.Y + y - popupRect.Y);
    }

    private static double ClampAxis(double position, double size, double min, double max)
    {
        if (size >= max - min)
        {
            return min;
        }
        return Math.Clamp(position, min, max - size);
    }

    private void CloseCore()
    {
        _pendingOpen = false;
        var anchor = Anchor;
        var focusInside = IsFocusInside();
        DetachDismissHandlers();
        if (_popup is not null)
        {
            _popup.IsOpen = false;
        }
        UpdateTriggerState();
        _showAnchor = null;
        ReleaseParent();
        if (focusInside && anchor is not null && TopLevel.GetTopLevel(anchor) is not null)
        {
            anchor.Focus(NavigationMethod.Unspecified);
        }
        Closed?.Invoke(this, EventArgs.Empty);
    }

    private bool IsFocusInside()
    {
        if (_surface is null)
        {
            return false;
        }
        var focused = TopLevel.GetTopLevel(_surface)?.FocusManager?.GetFocusedElement() as Visual;
        return focused is not null &&
               (ReferenceEquals(focused, _surface) || _surface.IsVisualAncestorOf(focused));
    }

    private void OnPopupClosed(object? sender, EventArgs e)
    {
        if (IsOpen)
        {
            SetCurrentValue(IsOpenProperty, false);
        }
        ReleaseParent();
    }

    private void ReleaseParent()
    {
        if (_parentedToTarget && !IsOpen)
        {
            _parentedToTarget = false;
            ((ISetLogicalParent)this).SetParent(null);
        }
    }

    #endregion

    #region Dismissal

    private void AttachDismissHandlers()
    {
        var anchor = Anchor;
        var topLevel = anchor is null ? null : TopLevel.GetTopLevel(anchor);
        if (topLevel is null || ReferenceEquals(topLevel, _dismissRoot))
        {
            return;
        }
        DetachDismissHandlers();
        _dismissRoot = topLevel;
        topLevel.AddHandler(PointerPressedEvent, OnTopLevelPointerPressed, RoutingStrategies.Tunnel, handledEventsToo: true);
        topLevel.AddHandler(KeyDownEvent, OnTopLevelKeyDown, RoutingStrategies.Tunnel, handledEventsToo: true);
    }

    private void DetachDismissHandlers()
    {
        if (_dismissRoot is null)
        {
            return;
        }
        _dismissRoot.RemoveHandler(PointerPressedEvent, OnTopLevelPointerPressed);
        _dismissRoot.RemoveHandler(KeyDownEvent, OnTopLevelKeyDown);
        _dismissRoot = null;
    }

    private void OnTopLevelPointerPressed(object? sender, PointerPressedEventArgs e)
    {
        if (!IsOpen)
        {
            return;
        }
        var anchor = Anchor;
        if (e.Source is Visual source)
        {
            // A press on the trigger is left to the trigger click, which toggles the popover closed.
            if (anchor is not null && (ReferenceEquals(source, anchor) || anchor.IsVisualAncestorOf(source)))
            {
                return;
            }
            // In the overlay layer the panel shares this top level, so its own presses reach this handler.
            if (_surface is not null && (ReferenceEquals(source, _surface) || _surface.IsVisualAncestorOf(source)))
            {
                return;
            }
        }
        if (IsModal)
        {
            e.Handled = true;
        }
        SetCurrentValue(IsOpenProperty, false);
    }

    private void OnTopLevelKeyDown(object? sender, KeyEventArgs e)
    {
        if (IsOpen && e.Key == Key.Escape)
        {
            e.Handled = true;
            SetCurrentValue(IsOpenProperty, false);
        }
    }

    private void OnSurfaceKeyDown(object? sender, KeyEventArgs e)
    {
        if (e.Key == Key.Escape)
        {
            e.Handled = true;
            SetCurrentValue(IsOpenProperty, false);
        }
    }

    #endregion
}

internal sealed class PopoverCommand(Action execute) : System.Windows.Input.ICommand
{
    public event EventHandler? CanExecuteChanged
    {
        add { }
        remove { }
    }

    public bool CanExecute(object? parameter) => true;

    public void Execute(object? parameter) => execute();
}
