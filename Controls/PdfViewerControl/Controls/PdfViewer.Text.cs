using Avalonia;
using Avalonia.Controls;
using Avalonia.Controls.Primitives;
using Avalonia.Input;
using Avalonia.Input.Platform;
using Avalonia.Interactivity;
using Avalonia.Media;
using Avalonia.Threading;
using Avalonia.VisualTree;
using AtomUI.Icons.AntDesign;
using AcrfAnnotationTool.Controls.Menus;
using PdfViewerControl.Controls.Annotation;
using PdfViewerControl.Text;

namespace PdfViewerControl.Controls;

internal sealed class PdfTextOverlay(PdfViewer viewer, int pageIndex) : Control
{
    public override void Render(DrawingContext context) => viewer.DrawPdfTextHighlights(context, pageIndex);
}

public partial class PdfViewer
{
    private static readonly IBrush PdfMatchBrush = new SolidColorBrush(Color.Parse("#66FACC15"));
    private static readonly IBrush PdfCurrentMatchBrush = new SolidColorBrush(Color.Parse("#99FB923C"));
    private static readonly IBrush PdfSelectionBrush = new SolidColorBrush(Color.Parse("#663B82F6"));
    private static readonly Cursor PdfTextCursor = new(StandardCursorType.Ibeam);
    private readonly HashSet<PdfTextOverlay> _textOverlays = new();
    private readonly HashSet<int> _textRequests = new();
    private readonly PdfSearchResults _pdfSearchResults = new();
    private PdfPageTextCache? _pdfTextCache;
    private PdfTextSelection? _pdfTextSelection;
    private PdfSearcher? _pdfSearcher;
    private DispatcherTimer? _pdfSearchDebounce;
    private DispatcherTimer? _pdfSelectionScrollTimer;
    private CancellationTokenSource? _pdfSearchCts;
    private int _pdfSearchGeneration;
    private int _pdfSearchIndex = -1;
    private bool _pdfSearchRunning;
    private IPointer? _pdfTextPointer;
    private Point _pdfTextPointerPosition;
    private MenuSurface? _pdfTextMenu;

    public string SelectedText => _pdfTextSelection?.SelectedText ?? string.Empty;

    private void InitializePdfTextFeatures()
    {
        AddHandler(KeyDownEvent, OnPdfTextKeyDown, RoutingStrategies.Tunnel);
        _pdfSearchDebounce = new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(300) };
        _pdfSearchDebounce.Tick += (_, _) => { _pdfSearchDebounce.Stop(); StartPdfTextSearch(); };
        _pdfSelectionScrollTimer = new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(40) };
        _pdfSelectionScrollTimer.Tick += (_, _) =>
        {
            if (_pdfTextPointer == null) return;
            var y = _pdfTextPointerPosition.Y;
            var height = MainScrollViewer.Viewport.Height;
            var dy = y < 24 ? -Math.Min(40, (24 - y) / 2) : y > height - 24 ? Math.Min(40, (y - height + 24) / 2) : 0;
            if (dy == 0) return;
            MainScrollViewer.Offset = new Vector(MainScrollViewer.Offset.X,
                Math.Clamp(MainScrollViewer.Offset.Y + dy, 0, Math.Max(0, MainScrollViewer.Extent.Height - height)));
            MainScrollViewer.UpdateLayout();
            MovePdfTextSelection();
        };
        DetachedFromVisualTree += (_, _) => StopPdfTextDrag();
        UpdatePdfSearchCount();
    }

    private void AttachPdfTextDocument()
    {
        var version = _loader.DocumentVersion;
        _pdfTextCache = new PdfPageTextCache((page, token) => _loader.ReadPageTextAsync(page, version, token));
        _pdfSearcher = new PdfSearcher(_pdfTextCache, _loader.PageCount);
        _pdfTextSelection = new PdfTextSelection(page => _pdfTextCache?.TryGet(page), RequestPdfPageText,
            page => new Size(_loader.GetPageWidthInPoints(page), _loader.GetPageHeightInPoints(page)));
        _pdfTextSelection.Changed += RefreshPdfTextOverlays;
    }

    private void ResetPdfTextDocument()
    {
        StopPdfTextDrag();
        _pdfTextMenu?.Close(returnFocus: false);
        _pdfTextMenu = null;
        _pdfSearchDebounce?.Stop();
        CancelPdfTextSearch();
        _pdfTextSelection?.Clear();
        _pdfTextSelection = null;
        _pdfSearcher = null;
        _pdfTextCache?.Dispose();
        _pdfTextCache = null;
        _textRequests.Clear();
        _textOverlays.Clear();
        PdfSearchBar.IsVisible = false;
        PdfSearchTextBox.Text = string.Empty;
    }

    private void RequestPdfPageText(int pageIndex)
    {
        var cache = _pdfTextCache;
        if (cache == null || cache.TryGet(pageIndex) != null || !_textRequests.Add(pageIndex)) return;
        _ = ReadPdfPageTextAsync(cache, pageIndex);
    }

    private async Task ReadPdfPageTextAsync(PdfPageTextCache cache, int pageIndex)
    {
        try
        {
            await cache.GetAsync(pageIndex, CancellationToken.None).ConfigureAwait(false);
            // Always post, even for a cached task: resolving a pending selection must not recurse.
            Dispatcher.UIThread.Post(() =>
            {
                if (_disposed || !ReferenceEquals(cache, _pdfTextCache)) return;
                _textRequests.Remove(pageIndex);
                _pdfTextSelection?.OnTextArrived(pageIndex);
            }, DispatcherPriority.Background);
        }
        catch (OperationCanceledException) { }
    }

    private void RefreshPdfTextOverlays()
    {
        foreach (var overlay in _textOverlays) overlay.InvalidateVisual();
    }

    internal void DrawPdfTextHighlights(DrawingContext context, int pageIndex)
    {
        var scale = _loader.DisplayScale;
        Rect Scale(Rect rect) => new(rect.X * scale, rect.Y * scale, rect.Width * scale, rect.Height * scale);
        if (_pdfSearchResults.TryFindPage(pageIndex, out var matches, out var first))
        {
            var current = _pdfSearchIndex >= 0 && _pdfSearchIndex < _pdfSearchResults.Count ? _pdfSearchResults[_pdfSearchIndex] : null;
            for (var i = first; i < matches.Count && matches[i].PageIndex == pageIndex; i++)
                foreach (var rect in matches[i].Rects)
                    context.FillRectangle(ReferenceEquals(matches[i], current) ? PdfCurrentMatchBrush : PdfMatchBrush, Scale(rect));
        }
        if (_pdfTextSelection is { } selection)
            foreach (var rect in selection.GetRects(pageIndex)) context.FillRectangle(PdfSelectionBrush, Scale(rect));
    }

    private static bool IsPdfTextEditor(Visual? source)
    {
        for (var visual = source; visual != null; visual = visual.GetVisualParent())
            if (visual is TextBox or AtomUI.Desktop.Controls.LineEdit) return true;
        return false;
    }

    private static bool IsPdfAnnotationTarget(Visual? source)
    {
        for (var visual = source; visual != null; visual = visual.GetVisualParent())
            if (visual is FreeTextAnnotationControl or BracketCalloutOverlay or ScrollBar or TextBox) return true;
        return false;
    }

    private bool TryGetPdfTextPoint(Point viewportPoint, bool allowOutside, out int pageIndex, out Point point)
    {
        pageIndex = -1;
        point = default;
        if (_pageSlots.Count == 0 || MainScrollViewer.TranslatePoint(viewportPoint, PagesPanel) is not { } panelPoint) return false;
        pageIndex = Math.Clamp(UpperBoundPageHostTop(panelPoint.Y + PagesPanel.Margin.Top) - 1, 0, _pageSlots.Count - 1);
        var slot = _pageSlots[pageIndex];
        if (MainScrollViewer.TranslatePoint(viewportPoint, slot.Host) is not { } local) return false;
        if (!allowOutside && !new Rect(slot.Host.Bounds.Size).Contains(local)) return false;
        point = new Point((local.X - slot.Host.BorderThickness.Left) / _loader.DisplayScale,
            (local.Y - slot.Host.BorderThickness.Top) / _loader.DisplayScale);
        return true;
    }

    private void OnPdfTextPointerPressed(object? sender, PointerPressedEventArgs e)
    {
        if (e.Handled || _pdfTextSelection == null || IsPdfAnnotationTarget(e.Source as Visual) ||
            !e.GetCurrentPoint(MainScrollViewer).Properties.IsLeftButtonPressed ||
            (e.Pointer.Type == PointerType.Touch && e.ClickCount < 2)) return;
        _pdfTextPointerPosition = e.GetPosition(MainScrollViewer);
        if (!TryGetPdfTextPoint(_pdfTextPointerPosition, false, out var page, out var point))
        {
            _pdfTextSelection.Clear();
            ScrollViewer_OnBackgroundClick(sender, e);
            return;
        }
        ClearSelection();
        Focus();
        _pdfTextSelection.Press(page, point, e.ClickCount, e.KeyModifiers);
        _pdfTextPointer = e.Pointer;
        e.Pointer.Capture(MainScrollViewer);
        _pdfSelectionScrollTimer?.Start();
        e.Handled = true;
    }

    private void OnPdfTextPointerMoved(object? sender, PointerEventArgs e)
    {
        if (e.Handled || _pdfTextCache == null) return;
        _pdfTextPointerPosition = e.GetPosition(MainScrollViewer);
        if (_pdfTextPointer != null)
        {
            MovePdfTextSelection();
            e.Handled = true;
            return;
        }
        if (IsPdfAnnotationTarget(e.Source as Visual)) return;
        if (TryGetPdfTextPoint(_pdfTextPointerPosition, false, out var page, out var point))
        {
            RequestPdfPageText(page);
            if (_pageSlots[page].AnnotationLayer is { } layer)
                layer.Cursor = _pdfTextCache.TryGet(page)?.IsOverText(point) == true ? PdfTextCursor : null;
        }
    }

    private void MovePdfTextSelection()
    {
        if (TryGetPdfTextPoint(_pdfTextPointerPosition, true, out var page, out var point))
            _pdfTextSelection?.Move(page, point, _loader.DisplayScale);
    }

    private void OnPdfTextPointerReleased(object? sender, PointerReleasedEventArgs e)
    {
        if (_pdfTextPointer != e.Pointer) return;
        _pdfTextPointerPosition = e.GetPosition(MainScrollViewer);
        MovePdfTextSelection();
        StopPdfTextDrag();
        e.Handled = true;
    }

    private void OnPdfTextPointerCaptureLost(object? sender, PointerCaptureLostEventArgs e) => StopPdfTextDrag();
    private void OnPdfTextPointerExited(object? sender, PointerEventArgs e)
    {
        if (_pdfTextPointer == null) MainScrollViewer.Cursor = null;
    }

    private void StopPdfTextDrag()
    {
        _pdfSelectionScrollTimer?.Stop();
        _pdfTextSelection?.Release();
        var pointer = _pdfTextPointer;
        _pdfTextPointer = null;
        pointer?.Capture(null);
    }

    private async void OnPdfTextKeyDown(object? sender, KeyEventArgs e)
    {
        if (e.Handled || _pdfTextCache == null || LoadingOverlay.IsVisible) return;
        var source = e.Source as Visual;
        var inSearch = source == PdfSearchTextBox || source?.GetVisualAncestors().Contains(PdfSearchTextBox) == true;
        if (IsPdfTextEditor(source) && !inSearch) return;
        var control = e.KeyModifiers.HasFlag(KeyModifiers.Control) && (e.KeyModifiers & (KeyModifiers.Alt | KeyModifiers.Meta)) == 0;
        if (control && e.Key == Key.F)
        {
            OpenPdfTextSearch();
            e.Handled = true;
        }
        else if (e.Key == Key.F3 || (inSearch && e.Key == Key.Enter))
        {
            if (_pdfSearchDebounce?.IsEnabled == true) StartPdfTextSearch();
            else MovePdfSearchResult(e.KeyModifiers.HasFlag(KeyModifiers.Shift) ? -1 : 1);
            e.Handled = true;
        }
        else if (e.Key == Key.Escape)
        {
            if (PdfSearchBar.IsVisible) ClosePdfTextSearch();
            else _pdfTextSelection?.Clear();
            e.Handled = true;
        }
        else if (!inSearch && control && e.Key == Key.C && _pdfTextSelection?.HasSelection == true)
        {
            e.Handled = true;
            await CopyPdfSelectedTextAsync();
        }
        else if (!inSearch && control && e.Key == Key.A)
        {
            ClearSelection();
            _pdfTextSelection?.SelectPage(_currentPageIndex);
            e.Handled = true;
        }
    }

    private async Task CopyPdfSelectedTextAsync()
    {
        var selection = _pdfTextSelection;
        if (selection == null) return;
        try
        {
            await selection.WhenComplete;
            if (!ReferenceEquals(selection, _pdfTextSelection) || selection.SelectedText.Length == 0) return;
            if (TopLevel.GetTopLevel(this)?.Clipboard is { } clipboard)
                await clipboard.SetTextAsync(selection.SelectedText);
        }
        catch (Exception ex) { SetStatus($"复制 PDF 原文失败: {ex.Message}"); }
    }

    private void OnPdfTextContextRequested(object? sender, ContextRequestedEventArgs e)
    {
        if (e.Handled || _pdfTextCache == null || IsPdfAnnotationTarget(e.Source as Visual)) return;
        if (e.TryGetPosition(MainScrollViewer, out var position) &&
            TryGetPdfTextPoint(position, false, out var page, out _)) _currentPageIndex = page;
        var menu = new MenuSurface
        {
            Variant = MenuSurfaceVariant.Context,
            ClosesOnViewportChange = true,
            ClosesOnAnchorPress = true,
            FocusReturnTarget = MainScrollViewer
        };
        var copy = new MenuEntry
        {
            Header = "复制原文",
            Icon = new CopyOutlined { Width = 16, Height = 16 },
            IsEnabled = _pdfTextSelection?.HasSelection == true,
            InputGesture = new KeyGesture(Key.C, KeyModifiers.Control)
        };
        copy.Click += async (_, _) => await CopyPdfSelectedTextAsync();
        var all = new MenuEntry
        {
            Header = "选择当前页全部原文",
            Icon = new SelectOutlined { Width = 16, Height = 16 },
            InputGesture = new KeyGesture(Key.A, KeyModifiers.Control)
        };
        all.Click += (_, _) => { Focus(); ClearSelection(); _pdfTextSelection?.SelectPage(_currentPageIndex); };
        var find = new MenuEntry
        {
            Header = "搜索 PDF 原文",
            Icon = new SearchOutlined { Width = 16, Height = 16 },
            HasSeparatorBefore = true,
            InputGesture = new KeyGesture(Key.F, KeyModifiers.Control)
        };
        find.Click += (_, _) => OpenPdfTextSearch();
        menu.Items.Add(copy);
        menu.Items.Add(all);
        menu.Items.Add(find);
        _pdfTextMenu?.Close(returnFocus: false);
        _pdfTextMenu = menu;
        var menuPosition = e.TryGetPosition(MainScrollViewer, out position)
            ? position
            : new Point(MainScrollViewer.Bounds.Width / 2, MainScrollViewer.Bounds.Height / 2);
        _pdfTextMenu.OpenAt(MainScrollViewer, menuPosition);
        e.Handled = true;
    }

    private void OnPdfSearchButtonClick(object? sender, RoutedEventArgs e) => OpenPdfTextSearch();
    private void OnPdfSearchCloseClick(object? sender, RoutedEventArgs e) => ClosePdfTextSearch();
    private void OnPdfSearchPreviousClick(object? sender, RoutedEventArgs e) => MovePdfSearchResult(-1);
    private void OnPdfSearchNextClick(object? sender, RoutedEventArgs e) => MovePdfSearchResult(1);

    private void OpenPdfTextSearch()
    {
        if (_pdfTextCache == null || LoadingOverlay.IsVisible) return;
        var wasVisible = PdfSearchBar.IsVisible;
        PdfSearchBar.IsVisible = true;
        var selected = SelectedText.Trim();
        if (!wasVisible && selected.Length is > 0 and <= 256 && !selected.Contains('\n')) PdfSearchTextBox.Text = selected;
        if (!wasVisible) StartPdfTextSearch();
        PdfSearchTextBox.Focus();
        PdfSearchTextBox.SelectAll();
    }

    private void ClosePdfTextSearch()
    {
        _pdfSearchDebounce?.Stop();
        CancelPdfTextSearch();
        PdfSearchBar.IsVisible = false;
        Focus();
    }

    private void OnPdfSearchTextChanged(object? sender, TextChangedEventArgs e)
    {
        if (_pdfSearchDebounce == null || !PdfSearchBar.IsVisible) return;
        CancelPdfTextSearch();
        _pdfSearchDebounce.Stop();
        _pdfSearchDebounce.Start();
    }

    private void OnPdfSearchOptionsChanged(object? sender, RoutedEventArgs e) => StartPdfTextSearch();

    private void CancelPdfTextSearch()
    {
        ++_pdfSearchGeneration;
        _pdfSearchCts?.Cancel();
        _pdfSearchCts?.Dispose();
        _pdfSearchCts = null;
        _pdfSearchRunning = false;
        _pdfSearchIndex = -1;
        _pdfSearchResults.Reset(_currentPageIndex);
        UpdatePdfSearchCount();
        RefreshPdfTextOverlays();
    }

    private async void StartPdfTextSearch()
    {
        _pdfSearchDebounce?.Stop();
        CancelPdfTextSearch();
        var searcher = _pdfSearcher;
        var query = PdfSearchTextBox.Text ?? string.Empty;
        var options = new PdfSearchOptions(PdfSearchMatchCase.IsChecked == true, PdfSearchWholeWord.IsChecked == true);
        if (!PdfSearchBar.IsVisible || searcher == null || PdfSearcher.PrepareQuery(query, options.MatchCase).Length == 0) return;
        var generation = _pdfSearchGeneration;
        var cts = _pdfSearchCts = new CancellationTokenSource();
        _pdfSearchRunning = true;
        UpdatePdfSearchCount();
        try
        {
            await searcher.ScanAsync(query, options, _currentPageIndex, (page, matches) =>
            {
                Dispatcher.UIThread.Post(() =>
                {
                    if (generation != _pdfSearchGeneration || _disposed) return;
                    var currentPage = _pdfSearchIndex >= 0 ? _pdfSearchResults[_pdfSearchIndex].PageIndex : -1;
                    var moved = _pdfSearchResults.Add(page, matches);
                    if (_pdfSearchIndex >= 0)
                    {
                        // Wrapped reports only shift matches after their insertion point, not earlier pages the user navigated to.
                        if (currentPage >= page) _pdfSearchIndex += moved;
                    }
                    else if (_pdfSearchResults.Count > 0)
                    {
                        _pdfSearchIndex = 0;
                        ScrollToPdfSearchMatch();
                    }
                    UpdatePdfSearchCount();
                    if (page < _pageSlots.Count) _pageSlots[page].TextOverlay?.InvalidateVisual();
                }, DispatcherPriority.Background);
            }, cts.Token);
            // Drain page reports before reporting completion; they use this same dispatcher priority.
            await Dispatcher.UIThread.InvokeAsync(() =>
            {
                if (generation != _pdfSearchGeneration) return;
                _pdfSearchRunning = false;
                UpdatePdfSearchCount();
            }, DispatcherPriority.Background);
        }
        catch (OperationCanceledException) { }
        catch (Exception ex)
        {
            if (generation != _pdfSearchGeneration) return;
            _pdfSearchRunning = false;
            UpdatePdfSearchCount();
            SetStatus($"PDF 搜索失败: {ex.Message}");
        }
    }

    private void MovePdfSearchResult(int direction)
    {
        if (_pdfSearchResults.Count == 0) return;
        _pdfSearchIndex = (_pdfSearchIndex + direction + _pdfSearchResults.Count) % _pdfSearchResults.Count;
        ScrollToPdfSearchMatch();
        UpdatePdfSearchCount();
        RefreshPdfTextOverlays();
    }

    private void ScrollToPdfSearchMatch()
    {
        if (_pdfSearchIndex < 0 || _pdfSearchIndex >= _pdfSearchResults.Count) return;
        var match = _pdfSearchResults[_pdfSearchIndex];
        if (match.PageIndex >= _pageSlots.Count) return;
        _currentPageIndex = match.PageIndex;
        MountPageContent(_pageSlots[match.PageIndex]);
        var bounds = match.Bounds;
        var y = GetPageHostTop(match.PageIndex) + bounds.Top * _loader.DisplayScale;
        var target = Math.Max(0, y - Math.Max(60, MainScrollViewer.Viewport.Height / 3));
        AnimateViewerScrollTo(new Vector(MainScrollViewer.Offset.X, target));
        UpdatePageInfo();
        RequestVirtualizationImmediate();
    }

    private void UpdatePdfSearchCount()
    {
        if (PdfSearchCountText == null) return;
        PdfSearchCountText.Text = $"{(_pdfSearchIndex >= 0 ? _pdfSearchIndex + 1 : 0)} / {_pdfSearchResults.Count}{(_pdfSearchRunning ? "+" : string.Empty)}";
        PdfSearchPreviousButton.IsEnabled = PdfSearchNextButton.IsEnabled = _pdfSearchResults.Count > 0;
    }
}
