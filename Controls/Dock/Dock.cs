using Avalonia;
using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Layout;
using Avalonia.Media;
using AtomUI.Icons.AntDesign;
using AtomIconButton = AtomUI.Desktop.Controls.IconButton;
using AtomWindow = AtomUI.Desktop.Controls.Window;

namespace Dock.Controls;

/// <summary>Two-pane dock with four edge targets and a native floating host.</summary>
public sealed class Dock : UserControl
{
    public static readonly StyledProperty<DockLayout> LayoutProperty =
        AvaloniaProperty.Register<Dock, DockLayout>(nameof(Layout), DockLayout.LeftRight,
            validate: value => Enum.IsDefined(value));
    public static readonly StyledProperty<Control?> MainContentProperty =
        AvaloniaProperty.Register<Dock, Control?>(nameof(MainContent));
    public static readonly StyledProperty<Control?> PaneContentProperty =
        AvaloniaProperty.Register<Dock, Control?>(nameof(PaneContent));
    public static readonly StyledProperty<string> PaneTitleProperty =
        AvaloniaProperty.Register<Dock, string>(nameof(PaneTitle), "Inspector");

    public static readonly StyledProperty<bool> DocumentTabsProperty =
        AvaloniaProperty.Register<Dock, bool>(nameof(DocumentTabs));
    public static readonly StyledProperty<bool> IsPaneOpenProperty =
        AvaloniaProperty.Register<Dock, bool>(nameof(IsPaneOpen), true);

    private static readonly IBrush IndicatorBackground = new Avalonia.Media.Immutable.ImmutableSolidColorBrush(Color.Parse("#FAFCFF"));
    private static readonly IBrush IndicatorHotBackground = new Avalonia.Media.Immutable.ImmutableSolidColorBrush(Color.Parse("#E7EFFF"));
    private static readonly IBrush IndicatorBorder = new Avalonia.Media.Immutable.ImmutableSolidColorBrush(Color.Parse("#DCE3EF"));
    private static readonly IBrush AccentBrush = new Avalonia.Media.Immutable.ImmutableSolidColorBrush(Color.Parse("#477DFF"));
    private static readonly IBrush IndicatorArrowBrush = new Avalonia.Media.Immutable.ImmutableSolidColorBrush(Color.Parse("#415672"));
    private static readonly BoxShadows IndicatorShadow = BoxShadows.Parse("0 3 12 0 #180D2448");
    private static readonly Color AccentColor = Color.Parse("#477DFF");
    private static readonly Color IndicatorRestColor = Color.Parse("#D1D7E0");
    private static readonly Color LineRestColor = Color.Parse("#DDE2EA");
    private static readonly Color LineHotColor = Color.Parse("#B9CEFF");
    private static readonly Color HandleRestColor = Color.Parse("#B3BECE");
    private static readonly Color HandleHotColor = Color.Parse("#8EAEF4");
    private static readonly Geometry IndicatorBarGeometry = Geometry.Parse("M 3,0 L 23,0 Q 26,0 26,3 Q 26,6 23,6 L 3,6 Q 0,6 0,3 Q 0,0 3,0 Z");
    private static readonly Geometry IndicatorArrowGeometry = Geometry.Parse("M 16,29 L 16,13 M 10,19 L 16,13 L 22,19");
    private static readonly Geometry ResizeDotsGeometry = Geometry.Parse("M 2,3 A 1,1 0 1 0 4,3 A 1,1 0 1 0 2,3 M 2,8 A 1,1 0 1 0 4,8 A 1,1 0 1 0 2,8 M 2,13 A 1,1 0 1 0 4,13 A 1,1 0 1 0 2,13");
    private static readonly Cursor HorizontalResizeCursor = new(StandardCursorType.SizeWestEast);
    private static readonly Cursor VerticalResizeCursor = new(StandardCursorType.SizeNorthSouth);

    private readonly Border _tab;
    private readonly AtomIconButton _closeTab;
    private readonly Control _grip;
    private readonly Grid _root = new();
    private readonly Grid _grid = new();
    private readonly Canvas _resizeOverlay = new() { IsHitTestVisible = false };
    private readonly Grid _overlay = new() { IsVisible = false, IsHitTestVisible = false };
    private readonly Border _preview = new() { IsVisible = false, Background = Brush.Parse("#25477DFF"),
        BorderBrush = Brush.Parse("#8DB1FF"), BorderThickness = new Thickness(1), CornerRadius = new CornerRadius(8) };
    private readonly Dictionary<DockSide, Border> _targets = new();
    private readonly ContentControl _main = new();
    private readonly ContentControl _body = new();
    private readonly Border _pane;
    private readonly Border _header;
    private readonly TextBlock _title;
    private readonly AtomIconButton _floatButton;
    private AtomWindow? _floatingWindow;
    private DockLayoutState? _floatingState;
    private Window? _dragProxy;
    private Window? _owner;
    private Action? _stopDockAnimation;
    private DockLayout _lastDocked = DockLayout.LeftRight;
    private DockSide _side = DockSide.Right;
    private double _horizontalRatio = 0.5;
    private Action? _stopResize;
    private double _verticalRatio = 0.5;
    private Point? _pressPoint;
    private IPointer? _dragPointer;
    private bool _dragging;
    private PixelPoint _dragOrigin;
    private Dock? _dragHost;
    private Size _previewSize;
    private readonly Dictionary<Border, IndicatorMotion> _indicatorMotions = new();

    private sealed class IndicatorMotion
    {
        public required SolidColorBrush BarBrush { get; init; }
        public required ScaleTransform BarScale { get; init; }
        public required TranslateTransform ArrowTransform { get; init; }
        public required Avalonia.Threading.DispatcherTimer Timer { get; init; }
        public required System.Diagnostics.Stopwatch Clock { get; init; }
        public bool Hot { get; set; }
        public double ArrowOffset { get; set; }
        public double ArrowVelocity { get; set; }
    }

    // A single-panel Dock can participate in its containing two-panel workspace.
    public Dock? DockHost { get; set; }

    public Dock()
    {
        _title = new TextBlock { Text = PaneTitle, FontSize = 14, FontWeight = FontWeight.SemiBold,
            Foreground = Brush.Parse("#1D293D"), VerticalAlignment = VerticalAlignment.Center, Margin = new Thickness(16, 0) };
        _floatButton = new AtomIconButton { Icon = new ArrowUpOutlined { RenderTransform = new RotateTransform(45),
                RenderTransformOrigin = new RelativePoint(0.5, 0.5, RelativeUnit.Relative) },
            IconWidth = 16, IconHeight = 16, Width = 32, Height = 30, MinWidth = 0, Margin = new Thickness(0, 0, 8, 0),
            Padding = new Thickness(0), Background = Brushes.Transparent, BorderThickness = new Thickness(0),
            VerticalAlignment = VerticalAlignment.Center };
        ToolTip.SetTip(_floatButton, "浮动 / 还原停靠（也可双击标题栏）");
        _floatButton.Click += (_, _) => ToggleFloating();
        var headerGrid = new Grid { ColumnDefinitions = new ColumnDefinitions("*,Auto,Auto") };
        var tabGrid = new Grid { ColumnDefinitions = new ColumnDefinitions("Auto,Auto") };
        tabGrid.Children.Add(_title);
        var close = _closeTab = new AtomIconButton { Name = "CloseDocumentTab", Icon = new CloseOutlined(),
            IconWidth = 14, IconHeight = 14, Foreground = Brushes.White,
            Width = 26, Height = 26, MinWidth = 0, Padding = new Thickness(6), Margin = new Thickness(0, 0, 6, 0),
            Background = Brushes.Transparent, BorderThickness = new Thickness(0), VerticalAlignment = VerticalAlignment.Center };
        Grid.SetColumn(close, 1);
        tabGrid.Children.Add(close);
        close.Click += (_, _) => IsPaneOpen = false;
        _tab = new Border { Name = "DocumentTab", Child = tabGrid, Height = 36,
            CornerRadius = new CornerRadius(7, 7, 0, 0), HorizontalAlignment = HorizontalAlignment.Left };
        headerGrid.Children.Add(_tab);
        _tab.PointerEntered += (_, _) => { if (DocumentTabs) _tab.Background = Brush.Parse("#316BEF"); };
        _tab.PointerExited += (_, _) => { if (DocumentTabs) _tab.Background = Brush.Parse("#477DFF"); };
        close.IsVisible = false;
        var grip = _grip = new TextBlock { Text = "⠿", FontSize = 22, Foreground = Brush.Parse("#B3BECE"),
            VerticalAlignment = VerticalAlignment.Center, Margin = new Thickness(8, 0) };
        Grid.SetColumn(grip, 1);
        headerGrid.Children.Add(grip);
        Grid.SetColumn(_floatButton, 2);
        headerGrid.Children.Add(_floatButton);
        _header = new Border { Height = 44, Background = Brushes.Transparent, Child = headerGrid,
            BorderThickness = new Thickness(0) };
        _header.PointerPressed += HeaderPressed;
        _header.PointerMoved += HeaderMoved;
        _header.PointerReleased += HeaderReleased;
        _header.PointerCaptureLost += (_, _) => CancelDrag();
        _header.KeyDown += (_, e) =>
        {
            if (e.Key == Key.Escape) { CancelDrag(); e.Handled = true; }
        };
        _header.Focusable = true;
        var paneGrid = new Grid { RowDefinitions = new RowDefinitions("44,*") };
        paneGrid.Children.Add(_header);
        Grid.SetRow(_body, 1);
        paneGrid.Children.Add(_body);
        _pane = new Border { Background = Brushes.White, Child = paneGrid, ClipToBounds = true,
            CornerRadius = new CornerRadius(8, 8, 0, 0), BorderThickness = new Thickness(0) };
        _overlay.Opacity = 0;
        _overlay.Transitions = new Avalonia.Animation.Transitions
        {
            new Avalonia.Animation.DoubleTransition
            {
                Property = OpacityProperty, Duration = TimeSpan.FromMilliseconds(140)
            }
        };
        _overlay.Children.Add(_preview);
        foreach (var side in Enum.GetValues<DockSide>())
        {
            var target = CreateIndicator(side);
            _targets.Add(side, target);
            _overlay.Children.Add(target);
        }
        _root.Children.Add(_grid);
        _root.Children.Add(_overlay);
        _root.Children.Add(_resizeOverlay);
        Content = _root;
        ApplyTabStyle();
        BuildLayout();
    }

    private void ToggleMaximized()
    {
        if (_floatingWindow is not { } window) return;
        CancelDrag();
        window.WindowState = window.WindowState == WindowState.Maximized ? WindowState.Normal : WindowState.Maximized;
    }

    private void ApplyTabStyle()
    {
        _tab.IsVisible = true;
        _tab.VerticalAlignment = DocumentTabs ? VerticalAlignment.Bottom : VerticalAlignment.Center;
        _tab.Background = Brush.Parse(DocumentTabs ? "#477DFF" : "Transparent");
        _title.Foreground = DocumentTabs ? Brushes.White : Brush.Parse("#1D293D");
        _grip.IsVisible = !DocumentTabs;
        _closeTab.IsVisible = DocumentTabs;
    }

    public DockLayout Layout { get => GetValue(LayoutProperty); set => SetValue(LayoutProperty, value); }
    public bool DocumentTabs { get => GetValue(DocumentTabsProperty); set => SetValue(DocumentTabsProperty, value); }
    public bool IsPaneOpen { get => GetValue(IsPaneOpenProperty); set => SetValue(IsPaneOpenProperty, value); }
    public Control? MainContent { get => GetValue(MainContentProperty); set => SetValue(MainContentProperty, value); }
    public Control? PaneContent { get => GetValue(PaneContentProperty); set => SetValue(PaneContentProperty, value); }
    public string PaneTitle { get => GetValue(PaneTitleProperty); set => SetValue(PaneTitleProperty, value); }
    public DockSide Side => _side;
    public Window? FloatingWindow => _floatingWindow;
    public Border? DocumentTab => _tab;
    public Window? DragPreviewWindow => _dragProxy;
    public bool IsShowingDockIndicators => _overlay.IsVisible;
    public DockSide? PreviewSide { get; private set; }
    public event EventHandler? LayoutChanged;

    public DockLayoutState CaptureLayoutState()
    {
        CaptureFloatingState();
        return new DockLayoutState(Layout, _lastDocked, _side, _horizontalRatio, _verticalRatio,
            IsPaneOpen, _floatingState?.FloatingX, _floatingState?.FloatingY,
            _floatingState?.FloatingWidth ?? 340, _floatingState?.FloatingHeight ?? 360,
            _floatingState?.FloatingMaximized ?? false);
    }

    public void RestoreLayoutState(DockLayoutState state)
    {
        if (!Enum.IsDefined(state.Layout) || !Enum.IsDefined(state.LastDocked) ||
            state.LastDocked == DockLayout.Floating || !Enum.IsDefined(state.Side)) return;
        _horizontalRatio = double.IsFinite(state.HorizontalRatio) ? Math.Clamp(state.HorizontalRatio, 0.05, 0.95) : 0.5;
        _verticalRatio = double.IsFinite(state.VerticalRatio) ? Math.Clamp(state.VerticalRatio, 0.05, 0.95) : 0.5;
        _side = state.Side;
        _lastDocked = state.LastDocked;
        _floatingState = state;
        SetCurrentValue(IsPaneOpenProperty, state.IsPaneOpen);
        if (Layout == state.Layout) BuildLayout();
        else SetCurrentValue(LayoutProperty, state.Layout);
    }

    private void CaptureFloatingState()
    {
        if (_floatingWindow is not { } window) return;
        if (window.WindowState != WindowState.Normal)
        {
            _floatingState = (_floatingState ?? new DockLayoutState()) with
            {
                FloatingMaximized = window.WindowState == WindowState.Maximized
            };
            return;
        }
        _floatingState = new DockLayoutState(FloatingX: window.Position.X, FloatingY: window.Position.Y,
            FloatingWidth: window.ClientSize.Width, FloatingHeight: window.ClientSize.Height);
    }

    public void DockTo(DockSide side)
    {
        if (!Enum.IsDefined(side)) throw new ArgumentOutOfRangeException(nameof(side));
        _side = side;
        var layout = side is DockSide.Left or DockSide.Right ? DockLayout.LeftRight : DockLayout.TopBottom;
        if (Layout == layout) { BuildLayout(); LayoutChanged?.Invoke(this, EventArgs.Empty); }
        else SetCurrentValue(LayoutProperty, layout);
    }

    public void RestoreDocked() => SetCurrentValue(LayoutProperty, _lastDocked);
    public void ToggleFloating()
    {
        if (Layout == DockLayout.Floating) RestoreDocked();
        else SetCurrentValue(LayoutProperty, DockLayout.Floating);
    }

    protected override void OnPropertyChanged(AvaloniaPropertyChangedEventArgs change)
    {
        base.OnPropertyChanged(change);
        if (change.Property == MainContentProperty)
        {
            _main.Content = MainContent;
            BuildLayout();
        }
        else if (change.Property == PaneContentProperty) _body.Content = PaneContent;
        else if (change.Property == DocumentTabsProperty || change.Property == IsPaneOpenProperty)
        {
            ApplyTabStyle();
            if (change.Property == IsPaneOpenProperty) BuildLayout();
        }
        else if (change.Property == PaneTitleProperty)
        {
            _title.Text = PaneTitle;
            if (_floatingWindow is { } window) window.Title = PaneTitle;
        }
        else if (change.Property == LayoutProperty)
        {
            if (Layout != DockLayout.Floating)
            {
                _lastDocked = Layout;
                if (Layout == DockLayout.LeftRight && _side is DockSide.Top or DockSide.Bottom) _side = DockSide.Right;
                if (Layout == DockLayout.TopBottom && _side is DockSide.Left or DockSide.Right) _side = DockSide.Bottom;
            }
            var animateLayout = change.OldValue is DockLayout oldLayout && oldLayout != DockLayout.Floating
                && Layout != DockLayout.Floating && _owner is not null;
            _stopDockAnimation?.Invoke();
            _stopDockAnimation = null;
            var oldMain = _main.Bounds;
            var oldPane = _pane.Bounds;
            BuildLayout();
            if (animateLayout)
            {
                _grid.UpdateLayout();
                AnimateLayoutChange(oldMain, oldPane);
            }
            LayoutChanged?.Invoke(this, EventArgs.Empty);
        }
    }

    protected override void OnAttachedToVisualTree(VisualTreeAttachmentEventArgs e)
    {
        base.OnAttachedToVisualTree(e);
        _owner = TopLevel.GetTopLevel(this) as Window;
        if (Layout == DockLayout.Floating) BuildLayout();
    }

    protected override void OnDetachedFromVisualTree(VisualTreeAttachmentEventArgs e)
    {
        _stopResize?.Invoke();
        _stopResize = null;
        _stopDockAnimation?.Invoke();
        _stopDockAnimation = null;
        CancelDrag();
        CloseFloating();
        _owner = null;
        base.OnDetachedFromVisualTree(e);
    }

    private void BuildLayout()
    {
        var animateDock = _floatingWindow is not null && Layout != DockLayout.Floating;
        _stopDockAnimation?.Invoke();
        _stopDockAnimation = null;
        _stopResize?.Invoke();
        _stopResize = null;
        CancelDrag();
        CloseFloating();
        _pane.CornerRadius = Layout == DockLayout.Floating ? default : new CornerRadius(8, 8, 0, 0);
        _resizeOverlay.Children.Clear();
        for (var i = _grid.Children.Count - 1; i >= 0; i--)
            if (!ReferenceEquals(_grid.Children[i], _main)) _grid.Children.RemoveAt(i);
        _grid.ColumnDefinitions.Clear();
        _grid.RowDefinitions.Clear();
        Grid.SetColumn(_main, 0);
        Grid.SetRow(_main, 0);
        Grid.SetColumn(_pane, 0);
        Grid.SetRow(_pane, 0);
        if (!_grid.Children.Contains(_main)) _grid.Children.Add(_main);
        if (!IsPaneOpen) return;
        if (Layout == DockLayout.Floating)
        {
            _floatButton.Icon = new ArrowDownOutlined { RenderTransform = new RotateTransform(45),
                RenderTransformOrigin = new RelativePoint(0.5, 0.5, RelativeUnit.Relative) };
            if (_owner is null) return;
            var window = new AtomWindow
            {
                Title = PaneTitle, Width = 340, Height = 360, MinWidth = 280, MinHeight = 240,
                Background = Brushes.White, ShowInTaskbar = true,
                WindowDecorations = WindowDecorations.Full, CanResize = true,
                WindowStartupLocation = WindowStartupLocation.CenterOwner,
                Content = _pane
            };
            _floatingWindow = window;
            window.Closed += FloatingClosed;
            window.Deactivated += FloatingDeactivated;
            var saved = _floatingState;
            if (saved != null)
            {
                window.Width = double.IsFinite(saved.FloatingWidth) ? Math.Clamp(saved.FloatingWidth, 280, 10000) : 340;
                window.Height = double.IsFinite(saved.FloatingHeight) ? Math.Clamp(saved.FloatingHeight, 240, 10000) : 360;
                if (saved.FloatingX is { } x && saved.FloatingY is { } y &&
                    window.Screens.All.Any(screen => screen.WorkingArea.Contains(new PixelPoint(x + 80, y + 20))))
                {
                    window.WindowStartupLocation = WindowStartupLocation.Manual;
                    window.Position = new PixelPoint(x, y);
                }
            }
            window.Show(_owner);
            CaptureFloatingState();
            window.PositionChanged += (_, _) => CaptureFloatingState();
            window.SizeChanged += (_, _) => CaptureFloatingState();
            if (saved?.FloatingMaximized == true) window.WindowState = WindowState.Maximized;
            return;
        }
        _floatButton.Icon = new ArrowUpOutlined { RenderTransform = new RotateTransform(45),
            RenderTransformOrigin = new RelativePoint(0.5, 0.5, RelativeUnit.Relative) };
        if (MainContent is null)
        {
            _grid.Children.Add(_pane);
            if (animateDock) AnimateDockedPane();
            return;
        }
        var horizontal = Layout == DockLayout.LeftRight;
        var paneFirst = _side is DockSide.Left or DockSide.Top;
        var ratio = horizontal ? _horizontalRatio : _verticalRatio;
        var first = paneFirst ? 1 - ratio : ratio;
        var splitter = CreateSplitter(horizontal);
        if (horizontal)
        {
            _grid.ColumnDefinitions.Add(new ColumnDefinition(new GridLength(first, GridUnitType.Star)) { MinWidth = 240 });
            _grid.ColumnDefinitions.Add(new ColumnDefinition(new GridLength(8)));
            _grid.ColumnDefinitions.Add(new ColumnDefinition(new GridLength(1 - first, GridUnitType.Star)) { MinWidth = 240 });
            Grid.SetColumn(splitter, 1);
            Grid.SetColumn(paneFirst ? _main : _pane, 2);
        }
        else
        {
            _grid.RowDefinitions.Add(new RowDefinition(new GridLength(first, GridUnitType.Star)) { MinHeight = 160 });
            _grid.RowDefinitions.Add(new RowDefinition(new GridLength(8)));
            _grid.RowDefinitions.Add(new RowDefinition(new GridLength(1 - first, GridUnitType.Star)) { MinHeight = 160 });
            Grid.SetRow(splitter, 1);
            Grid.SetRow(paneFirst ? _main : _pane, 2);
        }
        _grid.Children.Add(splitter);
        _grid.Children.Add(_pane);
        if (animateDock) AnimateDockedPane();
    }

    private void AnimateDockedPane()
    {
        var offset = new TranslateTransform();
        _pane.RenderTransform = offset;
        var clock = System.Diagnostics.Stopwatch.StartNew();
        var timer = new Avalonia.Threading.DispatcherTimer { Interval = TimeSpan.FromMilliseconds(16) };
        void Apply(double remaining)
        {
            offset.X = _side == DockSide.Left ? -24 * remaining : _side == DockSide.Right ? 24 * remaining : 0;
            offset.Y = _side == DockSide.Top ? -24 * remaining : _side == DockSide.Bottom ? 24 * remaining : 0;
            _pane.Opacity = 1 - 0.2 * remaining;
        }
        void Stop()
        {
            timer.Stop();
            clock.Stop();
            _pane.RenderTransform = null;
            _pane.Opacity = 1;
            _stopDockAnimation = null;
        }
        _stopDockAnimation = Stop;
        Apply(1);
        timer.Tick += (_, _) =>
        {
            var progress = Math.Clamp(clock.Elapsed.TotalMilliseconds / 260, 0, 1);
            Apply(Math.Pow(1 - progress, 3));
            if (progress >= 1) Stop();
        };
        timer.Start();
    }

    private void AnimateLayoutChange(Rect oldMain, Rect oldPane)
    {
        var mainTransform = new MatrixTransform();
        var paneTransform = new MatrixTransform();
        _main.RenderTransformOrigin = _pane.RenderTransformOrigin = new RelativePoint(0, 0, RelativeUnit.Relative);
        _main.RenderTransform = mainTransform;
        _pane.RenderTransform = paneTransform;
        var newMain = _main.Bounds;
        var newPane = _pane.Bounds;
        var clock = System.Diagnostics.Stopwatch.StartNew();
        var timer = new Avalonia.Threading.DispatcherTimer { Interval = TimeSpan.FromMilliseconds(16) };
        static Matrix Interpolate(Rect from, Rect to, double remaining)
        {
            var sx = to.Width > 0 ? 1 + (from.Width / to.Width - 1) * remaining : 1;
            var sy = to.Height > 0 ? 1 + (from.Height / to.Height - 1) * remaining : 1;
            return new Matrix(sx, 0, 0, sy, (from.X - to.X) * remaining, (from.Y - to.Y) * remaining);
        }
        void Apply(double remaining)
        {
            mainTransform.Matrix = Interpolate(oldMain, newMain, remaining);
            paneTransform.Matrix = Interpolate(oldPane, newPane, remaining);
        }
        void Stop()
        {
            timer.Stop();
            clock.Stop();
            _main.RenderTransform = _pane.RenderTransform = null;
            _stopDockAnimation = null;
        }
        _stopDockAnimation = Stop;
        Apply(1);
        timer.Tick += (_, _) =>
        {
            var progress = Math.Clamp(clock.Elapsed.TotalMilliseconds / 300, 0, 1);
            Apply(Math.Pow(1 - progress, 3));
            if (progress >= 1) Stop();
        };
        timer.Start();
    }

    private GridSplitter CreateSplitter(bool horizontal)
    {
        var lineBrush = new SolidColorBrush(LineRestColor)
        {
            Transitions = new Avalonia.Animation.Transitions
            {
                new Avalonia.Animation.ColorTransition
                {
                    Property = SolidColorBrush.ColorProperty, Duration = TimeSpan.FromMilliseconds(220)
                }
            }
        };
        var handleBrush = new SolidColorBrush(HandleRestColor)
        {
            Transitions = new Avalonia.Animation.Transitions
            {
                new Avalonia.Animation.ColorTransition
                {
                    Property = SolidColorBrush.ColorProperty, Duration = TimeSpan.FromMilliseconds(220)
                }
            }
        };
        var line = new Border { Name = "ResizeLine", Background = lineBrush,
            Width = horizontal ? 1 : double.NaN, Height = horizontal ? double.NaN : 1,
            HorizontalAlignment = horizontal ? HorizontalAlignment.Center : HorizontalAlignment.Stretch,
            VerticalAlignment = horizontal ? VerticalAlignment.Stretch : VerticalAlignment.Center };
        var dots = new Avalonia.Controls.Shapes.Path
        {
            Data = ResizeDotsGeometry,
            Fill = Brushes.White, Width = 2, Height = 14, Stretch = Stretch.Fill, IsVisible = false,
            HorizontalAlignment = HorizontalAlignment.Center, VerticalAlignment = VerticalAlignment.Center,
            RenderTransformOrigin = new RelativePoint(0.5, 0.5, RelativeUnit.Relative),
            RenderTransform = new RotateTransform(horizontal ? 0 : 90)
        };
        var handleScale = new ScaleTransform(0.25, 0.25);
        var handle = new Border { Name = "ResizeHandle", Width = horizontal ? 6 : 36,
            Height = horizontal ? 36 : 6, CornerRadius = new CornerRadius(3), Background = handleBrush,
            HorizontalAlignment = HorizontalAlignment.Center, VerticalAlignment = VerticalAlignment.Center,
            IsVisible = false, Opacity = 0, Child = dots,
            RenderTransformOrigin = new RelativePoint(0.5, 0.5, RelativeUnit.Relative), RenderTransform = handleScale };
        var label = new TextBlock { Foreground = Brushes.White, FontSize = 11,
            HorizontalAlignment = HorizontalAlignment.Center, VerticalAlignment = VerticalAlignment.Center };
        var badge = new Border { Name = "ResizeSizeBadge", Background = Brush.Parse("#283446"),
            CornerRadius = new CornerRadius(4), Width = 104, Height = 26, Child = label, IsVisible = false,
            HorizontalAlignment = HorizontalAlignment.Left, VerticalAlignment = VerticalAlignment.Top };
        _resizeOverlay.Children.Add(badge);
        var visuals = new Grid { Background = Brushes.Transparent };
        line.IsHitTestVisible = false;
        handle.IsHitTestVisible = false;
        badge.IsHitTestVisible = false;
        visuals.Children.Add(line);
        visuals.Children.Add(handle);
        var splitter = new GridSplitter { Name = "PanelSplitter", Background = Brushes.Transparent,
            Cursor = horizontal ? HorizontalResizeCursor : VerticalResizeCursor,
            ResizeDirection = horizontal ? GridResizeDirection.Columns : GridResizeDirection.Rows,
            ResizeBehavior = GridResizeBehavior.PreviousAndNext,
            HorizontalAlignment = HorizontalAlignment.Stretch, VerticalAlignment = VerticalAlignment.Stretch,
            Template = new Avalonia.Controls.Templates.FuncControlTemplate<GridSplitter>((_, _) => visuals) };
        var dragging = false;
        var handleTimer = new Avalonia.Threading.DispatcherTimer { Interval = TimeSpan.FromMilliseconds(16) };
        var handleClock = new System.Diagnostics.Stopwatch();
        var hot = false;
        var lastLineColor = lineBrush.Color;
        var lastHandleColor = handleBrush.Color;
        var lastDisplayedSize = -1;
        double fromThickness = 0.25, fromLength = 0.25, fromOpacity = 0;
        handleTimer.Tick += (_, _) =>
        {
            var progress = Math.Clamp(handleClock.Elapsed.TotalMilliseconds / 380, 0, 1);
            // Damped formation, similar to Arc's CSS spring; retarget from the current visual state.
            var spring = progress >= 1 ? 1 : 1 - Math.Exp(-8 * progress) * Math.Cos(10 * progress);
            var fade = Math.Min(1, handleClock.Elapsed.TotalMilliseconds / 140);
            var thickness = Math.Max(0.25, fromThickness + ((hot ? 1 : 0.25) - fromThickness) * spring);
            // Stretch over the full duration so the length change remains visible after the fade-in.
            var stretch = progress * progress * (3 - 2 * progress);
            var length = fromLength + ((hot ? 1 : 0.25) - fromLength) * stretch;
            handleScale.ScaleX = horizontal ? thickness : length;
            handleScale.ScaleY = horizontal ? length : thickness;
            handle.Opacity = fromOpacity + ((hot ? 1 : 0) - fromOpacity) * fade;
            if (progress >= 1)
            {
                handleTimer.Stop();
                handleClock.Stop();
                handle.IsVisible = hot;
            }
        };
        void UpdateState()
        {
            var lineColor = dragging ? AccentColor : splitter.IsPointerOver ? LineHotColor : LineRestColor;
            if (lineColor != lastLineColor)
            {
                lineBrush.Color = lastLineColor = lineColor;
            }
            var nextHot = dragging || splitter.IsPointerOver;
            if (hot != nextHot)
            {
                fromThickness = horizontal ? handleScale.ScaleX : handleScale.ScaleY;
                fromLength = horizontal ? handleScale.ScaleY : handleScale.ScaleX;
                fromOpacity = handle.Opacity;
                hot = nextHot;
                handle.IsVisible = true;
                handleClock.Restart();
                handleTimer.Start();
            }
            var handleColor = dragging ? AccentColor : nextHot ? HandleHotColor : HandleRestColor;
            if (handleColor != lastHandleColor)
            {
                handleBrush.Color = lastHandleColor = handleColor;
            }
            dots.IsVisible = dragging;
            badge.IsVisible = dragging;
            if (dragging)
            {
                _grid.UpdateLayout();
                var displayedSize = (int)Math.Round(horizontal ? _main.Bounds.Width : _main.Bounds.Height);
                if (displayedSize != lastDisplayedSize)
                {
                    lastDisplayedSize = displayedSize;
                    label.Text = horizontal ? $"Width {displayedSize} px" : $"Height {displayedSize} px";
                }
                var center = splitter.TranslatePoint(new Point(splitter.Bounds.Width / 2, splitter.Bounds.Height / 2), _resizeOverlay);
                if (center is { } point)
                {
                    Canvas.SetLeft(badge, Math.Clamp(point.X - badge.Width / 2, 0, Math.Max(0, _root.Bounds.Width - badge.Width)));
                    Canvas.SetTop(badge, Math.Clamp(point.Y - (horizontal ? 57 : 43), 0, Math.Max(0, _root.Bounds.Height - badge.Height)));
                }
            }
        }
        splitter.PointerEntered += (_, _) => UpdateState();
        splitter.PointerExited += (_, _) => UpdateState();
        var timer = new Avalonia.Threading.DispatcherTimer { Interval = TimeSpan.FromMilliseconds(16) };
        var clock = new System.Diagnostics.Stopwatch();
        double origin = 0, initial = 0, total = 0, current = 0, target = 0, displacement = 0;
        var minimum = horizontal ? 240.0 : 160.0;
        double Coordinate(Point point) => horizontal ? point.X : point.Y;
        void Apply(double size)
        {
            current = size;
            if (horizontal)
            {
                _grid.ColumnDefinitions[0].MinWidth = _grid.ColumnDefinitions[2].MinWidth = 0;
                _grid.ColumnDefinitions[0].Width = new GridLength(size, GridUnitType.Star);
                _grid.ColumnDefinitions[2].Width = new GridLength(total - size, GridUnitType.Star);
            }
            else
            {
                _grid.RowDefinitions[0].MinHeight = _grid.RowDefinitions[2].MinHeight = 0;
                _grid.RowDefinitions[0].Height = new GridLength(size, GridUnitType.Star);
                _grid.RowDefinitions[2].Height = new GridLength(total - size, GridUnitType.Star);
            }
            UpdateState();
        }
        void Stop()
        {
            timer.Stop();
            clock.Stop();
            badge.IsVisible = false;
        }
        void RestoreLimits()
        {
            if (horizontal)
                _grid.ColumnDefinitions[0].MinWidth = _grid.ColumnDefinitions[2].MinWidth = minimum;
            else
                _grid.RowDefinitions[0].MinHeight = _grid.RowDefinitions[2].MinHeight = minimum;
        }
        timer.Tick += (_, _) =>
        {
            // Arc Smooth: visualDuration .4, bounce 0 (critically damped).
            var omega = 2 * Math.PI / (1.2 * 0.4);
            var t = clock.Elapsed.TotalSeconds;
            var offset = displacement * (1 + omega * t) * Math.Exp(-omega * t);
            Apply(target + offset);
            if (Math.Abs(offset) < 0.05)
            {
                Apply(target);
                RestoreLimits();
                Stop();
            }
        };
        _stopResize = () =>
        {
            Stop();
            handleTimer.Stop();
            handleClock.Stop();
        };
        splitter.AddHandler(PointerPressedEvent, (_, e) =>
        {
            if (!e.GetCurrentPoint(splitter).Properties.IsLeftButtonPressed) return;
            timer.Stop();
            _grid.UpdateLayout();
            origin = Coordinate(e.GetPosition(_grid));
            initial = horizontal ? _grid.ColumnDefinitions[0].ActualWidth : _grid.RowDefinitions[0].ActualHeight;
            total = horizontal ? _grid.ColumnDefinitions[0].ActualWidth + _grid.ColumnDefinitions[2].ActualWidth
                : _grid.RowDefinitions[0].ActualHeight + _grid.RowDefinitions[2].ActualHeight;
            current = initial;
        }, Avalonia.Interactivity.RoutingStrategies.Tunnel);
        splitter.DragStarted += (_, _) => { dragging = true; UpdateState(); };
        splitter.AddHandler(PointerMovedEvent, (_, e) =>
        {
            if (!dragging || total <= 0) return;
            var raw = initial + Coordinate(e.GetPosition(_grid)) - origin;
            var lo = Math.Min(minimum, total / 2);
            var hi = total - lo;
            // Same 44px asymptote and .55 resistance as ResizablePanelsMath.Rubber.
            double Rubber(double distance) => distance * 44 * 0.55 / (44 + 0.55 * distance);
            Apply(raw < lo ? lo - Rubber(lo - raw) : raw > hi ? hi + Rubber(raw - hi) : raw);
        }, Avalonia.Interactivity.RoutingStrategies.Bubble, handledEventsToo: true);
        splitter.AddHandler(Avalonia.Controls.Primitives.Thumb.DragDeltaEvent, (_, e) => e.Handled = true,
            Avalonia.Interactivity.RoutingStrategies.Tunnel);
        splitter.DragCompleted += (_, _) =>
        {
            dragging = false;
            if (total <= 0) { UpdateState(); return; }
            var lo = Math.Min(minimum, total / 2);
            target = Math.Clamp(current, lo, total - lo);
            var mainSize = _side is DockSide.Left or DockSide.Top ? total - target : target;
            if (horizontal) _horizontalRatio = mainSize / total;
            else _verticalRatio = mainSize / total;
            displacement = current - target;
            if (Math.Abs(displacement) > 0.05)
            {
                clock.Restart();
                timer.Start();
            }
            else
                RestoreLimits();
            UpdateState();
        };
        return splitter;
    }

    private void FloatingClosed(object? sender, EventArgs e)
    {
        if (sender is not Window window || !ReferenceEquals(window, _floatingWindow)) return;
        CaptureFloatingState();
        CancelDrag();
        window.Content = null;
        window.Closed -= FloatingClosed;
        window.Deactivated -= FloatingDeactivated;
        _floatingWindow = null;
        RestoreDocked();
    }

    private void FloatingDeactivated(object? sender, EventArgs e) => CancelDrag();

    private void CloseFloating()
    {
        if (_floatingWindow is not { } window) return;
        CaptureFloatingState();
        _floatingWindow = null;
        window.Closed -= FloatingClosed;
        window.Deactivated -= FloatingDeactivated;
        window.Content = null;
        window.Close();
    }

    private void HeaderPressed(object? sender, PointerPressedEventArgs e)
    {
        if (_closeTab.IsPointerOver || _floatButton.IsPointerOver
            || !e.GetCurrentPoint(_header).Properties.IsLeftButtonPressed) return;
        if (DocumentTabs && !_tab.IsPointerOver) return;
        if (e.ClickCount == 2)
        {
            if (_floatingWindow is not null) ToggleMaximized();
            else ToggleFloating();
            e.Handled = true;
            return;
        }
        if (_floatingWindow?.WindowState == WindowState.Maximized) return;
        _header.Focus();
        e.Pointer.Capture(_header);
        _dragPointer = e.Pointer;
        _pressPoint = e.GetPosition(_header);
        _dragOrigin = _header.PointToScreen(_pressPoint.Value);
        e.Handled = true;
    }

    private void HeaderMoved(object? sender, PointerEventArgs e)
    {
        if (_pressPoint is null) return;
        if (!e.GetCurrentPoint(_header).Properties.IsLeftButtonPressed) { CancelDrag(); return; }
        var screen = _header.PointToScreen(e.GetPosition(_header));
        var dx = screen.X - _dragOrigin.X;
        var dy = screen.Y - _dragOrigin.Y;
        if (!_dragging)
        {
            if (Math.Abs(dx) < 8 && Math.Abs(dy) < 8) return;
            _stopDockAnimation?.Invoke();
            _stopDockAnimation = null;
            _dragging = true;
            _dragHost = DockHost ?? this;
            _pane.Opacity = 0;
            if (_floatingWindow is { } floating)
            {
                // Native decorations are not affected by client opacity.
                floating.WindowDecorations = WindowDecorations.None;
                floating.Opacity = 0;
            }
            {
                _dragProxy = new Window
                {
                    Width = 48, Height = 48, CanResize = false, ShowInTaskbar = false, ShowActivated = false,
                    WindowDecorations = WindowDecorations.None, Background = Brushes.Transparent,
                    TransparencyLevelHint = new[] { WindowTransparencyLevel.Transparent },
                    Content = new Border
                    {
                        Name = "DockDragProxy", Background = Brush.Parse("#477DFF"), CornerRadius = new CornerRadius(6),
                        Child = new Avalonia.Controls.Shapes.Path
                        {
                            Data = Geometry.Parse("M 8,3 L 8,13 M 3,8 L 13,8"), Stroke = Brushes.White,
                            StrokeThickness = 1.5, Width = 16, Height = 16,
                            HorizontalAlignment = HorizontalAlignment.Center, VerticalAlignment = VerticalAlignment.Center
                        }
                    }
                };
                _dragProxy.Show(_floatingWindow ?? _owner!);
            }
            _dragHost._overlay.IsVisible = true;
            _dragHost._overlay.Opacity = 1;
            _dragHost._root.UpdateLayout();
        }
        if (_dragProxy is { } proxy)
            proxy.Position = new PixelPoint(screen.X + (int)(12 * proxy.RenderScaling), screen.Y + (int)(12 * proxy.RenderScaling));
        _dragHost?.UpdatePreview(_dragHost.PointToClient(screen));
        e.Handled = true;
    }

    private void HeaderReleased(object? sender, PointerReleasedEventArgs e)
    {
        var dragged = _dragging;
        var target = dragged ? _dragHost?.PreviewSide : null;
        var screen = _header.PointToScreen(e.GetPosition(_header));
        CancelDrag();
        if (!dragged) return;
        if (target is { } side)
        {
            if (DockHost is { } host)
            {
                RestoreDocked();
                host.DockTo(side switch
                {
                    DockSide.Left => DockSide.Right,
                    DockSide.Right => DockSide.Left,
                    DockSide.Top => DockSide.Bottom,
                    _ => DockSide.Top
                });
            }
            else DockTo(side);
        }
        else
        {
            SetCurrentValue(LayoutProperty, DockLayout.Floating);
            if (_floatingWindow is { } window)
                window.Position = new PixelPoint(screen.X - (int)(48 * window.RenderScaling), screen.Y - (int)(54 * window.RenderScaling));
        }
    }

    private void CancelDrag()
    {
        _pressPoint = null;
        _dragging = false;
        var proxy = _dragProxy;
        _dragProxy = null;
        proxy?.Close();
        _pane.Opacity = 1;
        if (_floatingWindow is { } window)
        {
            window.WindowDecorations = WindowDecorations.Full;
            window.Opacity = 1;
        }
        var host = _dragHost;
        _dragHost = null;
        if (host is not null && !ReferenceEquals(host, this))
        {
            host._overlay.IsVisible = false;
            host._overlay.Opacity = 0;
            host._preview.IsVisible = false;
            host.PreviewSide = null;
            foreach (var target in host._targets.Values) host.SetIndicatorHot(target, false);
        }
        foreach (var target in _targets.Values) SetIndicatorHot(target, false);
        _overlay.IsVisible = false;
        _overlay.Opacity = 0;
        _preview.IsVisible = false;
        PreviewSide = null;
        var pointer = _dragPointer;
        _dragPointer = null;
        if (pointer?.Captured == _header) pointer.Capture(null);
    }

    private void UpdatePreview(Point point)
    {
        var previousSide = PreviewSide;
        PreviewSide = null;
        foreach (var (side, target) in _targets)
        {
            var hit = target.Bounds.Contains(point);
            SetIndicatorHot(target, hit);
            if (hit) PreviewSide = side;
        }
        if (previousSide == PreviewSide && _previewSize == Bounds.Size) return;
        _previewSize = Bounds.Size;
        _preview.IsVisible = PreviewSide is not null;
        _preview.Width = double.NaN;
        _preview.Height = double.NaN;
        _preview.HorizontalAlignment = HorizontalAlignment.Stretch;
        _preview.VerticalAlignment = VerticalAlignment.Stretch;
        if (PreviewSide is DockSide.Left or DockSide.Right)
        {
            _preview.Width = Bounds.Width * (1 - _horizontalRatio);
            _preview.HorizontalAlignment = PreviewSide == DockSide.Left ? HorizontalAlignment.Left : HorizontalAlignment.Right;
        }
        else if (PreviewSide is DockSide.Top or DockSide.Bottom)
        {
            _preview.Height = Bounds.Height * (1 - _verticalRatio);
            _preview.VerticalAlignment = PreviewSide == DockSide.Top ? VerticalAlignment.Top : VerticalAlignment.Bottom;
        }
    }

    public static Control CreateDockIcon(DockSide side)
    {
        // SVG viewBox 0 0 32 32: one top-facing icon, rotated about its exact centre.
        var icon = new Canvas { Width = 32, Height = 32, HorizontalAlignment = HorizontalAlignment.Center,
            VerticalAlignment = VerticalAlignment.Center, IsHitTestVisible = false,
            RenderTransformOrigin = new RelativePoint(0.5, 0.5, RelativeUnit.Relative),
            RenderTransform = new RotateTransform(side switch { DockSide.Top => 0, DockSide.Right => 90, DockSide.Bottom => 180, _ => 270 }) };
        var barBrush = new SolidColorBrush(IndicatorRestColor)
        {
            Transitions = new Avalonia.Animation.Transitions
            {
                new Avalonia.Animation.ColorTransition
                {
                    Property = SolidColorBrush.ColorProperty, Duration = TimeSpan.FromMilliseconds(220)
                }
            }
        };
        var barScale = new ScaleTransform(0.6, 0.5)
        {
            Transitions = new Avalonia.Animation.Transitions
            {
                new Avalonia.Animation.DoubleTransition { Property = ScaleTransform.ScaleXProperty, Duration = TimeSpan.FromMilliseconds(380) },
                new Avalonia.Animation.DoubleTransition { Property = ScaleTransform.ScaleYProperty, Duration = TimeSpan.FromMilliseconds(380) }
            }
        };
        var bar = new Avalonia.Controls.Shapes.Path
        {
            Name = "DockIndicatorBar", Width = 26, Height = 6,
            Data = IndicatorBarGeometry,
            Fill = barBrush, RenderTransform = barScale,
            RenderTransformOrigin = new RelativePoint(0.5, 0.5, RelativeUnit.Relative)
        };
        Canvas.SetLeft(bar, 3);
        Canvas.SetTop(bar, 2);
        icon.Children.Add(bar);
        icon.Children.Add(new Avalonia.Controls.Shapes.Path { Name = "DockIndicatorArrow",
            Data = IndicatorArrowGeometry, RenderTransform = new TranslateTransform(),
            Stroke = IndicatorArrowBrush, StrokeThickness = 2, StrokeLineCap = PenLineCap.Round, StrokeJoin = PenLineJoin.Round });
        return icon;
    }

    private void SetIndicatorHot(Border target, bool hot)
    {
        if (_indicatorMotions.TryGetValue(target, out var existing) ? existing.Hot == hot : !hot) return;
        target.Background = hot ? IndicatorHotBackground : IndicatorBackground;
        target.BorderBrush = hot ? AccentBrush : IndicatorBorder;
        if (target.Child is not Canvas icon) return;
        var bar = (Avalonia.Controls.Shapes.Path)icon.Children[0];
        if (!_indicatorMotions.TryGetValue(target, out var motion))
        {
            motion = new IndicatorMotion
            {
                BarBrush = (SolidColorBrush)bar.Fill!, BarScale = (ScaleTransform)bar.RenderTransform!,
                ArrowTransform = (TranslateTransform)icon.Children[1].RenderTransform!,
                Timer = new Avalonia.Threading.DispatcherTimer { Interval = TimeSpan.FromMilliseconds(16) },
                Clock = new System.Diagnostics.Stopwatch()
            };
            var state = motion;
            state.Timer.Tick += (_, _) =>
            {
                var t = state.Clock.Elapsed.TotalSeconds;
                // A directional impulse followed by an underdamped return to the original position.
                state.ArrowTransform.Y = Math.Exp(-8 * t) *
                    (state.ArrowOffset * Math.Cos(18 * t) + (state.ArrowVelocity + 8 * state.ArrowOffset) / 18 * Math.Sin(18 * t));
                if (t >= 0.7)
                {
                    state.Timer.Stop();
                    state.Clock.Stop();
                    state.ArrowTransform.Y = 0;
                }
            };
            _indicatorMotions.Add(target, motion);
        }
        motion.BarBrush.Color = hot ? AccentColor : IndicatorRestColor;
        motion.BarScale.ScaleX = hot ? 1 : 0.6;
        motion.BarScale.ScaleY = hot ? 1 : 0.5;
        if (motion.Hot == hot) return;
        motion.Hot = hot;
        motion.Timer.Stop();
        motion.Clock.Stop();
        if (hot)
        {
            motion.ArrowOffset = motion.ArrowTransform.Y;
            motion.ArrowVelocity = -100;
            motion.Clock.Restart();
            motion.Timer.Start();
        }
        else motion.ArrowTransform.Y = 0;
    }

    private static Border CreateIndicator(DockSide side)
    {
        var icon = CreateDockIcon(side);
        var target = new Border { Name = $"DockTarget{side}", Width = 48, Height = 48, CornerRadius = new CornerRadius(8),
            Background = IndicatorBackground, BorderBrush = IndicatorBorder, BorderThickness = new Thickness(1),
            BoxShadow = IndicatorShadow, Child = icon, Margin = new Thickness(10),
            HorizontalAlignment = side == DockSide.Left ? HorizontalAlignment.Left : side == DockSide.Right ? HorizontalAlignment.Right : HorizontalAlignment.Center,
            VerticalAlignment = side == DockSide.Top ? VerticalAlignment.Top : side == DockSide.Bottom ? VerticalAlignment.Bottom : VerticalAlignment.Center };
        return target;
    }
}
