using Avalonia.Media.Imaging;
using System.IO;
using Avalonia.Media;
using Avalonia.Layout;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Interactivity;
using Avalonia.Threading;
using PdfViewerControl.Controls.Annotation;
using PdfViewerControl.Helpers;
using PdfViewerControl.Models;
using PdfViewerControl.Services;

namespace PdfViewerControl.Controls;

public partial class PdfViewer : UserControl, IDisposable
{
    private readonly PdfDocumentLoader _loader = new();
    private readonly List<FreeTextAnnotationModel> _annotations = new();
    private readonly List<PdfPageSlot> _pageSlots = new();
    private readonly Dictionary<string, FreeTextAnnotationControl> _annotationControls = new();
    private const int ViewportPageBuffer = 2;
    private string? _currentPdfPath;
    private int _currentPageIndex;
    private double _zoom = 1.0;
    private FreeTextAnnotationControl? _selectedAnnotation;
    private int _renderGeneration;
    private CancellationTokenSource? _unloadCts;
    private readonly object _slotLoadGate = new();
    private bool _virtualizeRunning, _virtualizePending;
    private DispatcherTimer? _virtualizeScrollTimer;
    private DispatcherTimer? _pageNumberBarHideTimer;
    private bool _isPointerOverPageNumberHotspot;
    private bool _isUpdatingPageNumberText;
    private readonly SemaphoreSlim _pageRenderSemaphore = new(1, 1);
    private bool _suppressSliderZoomSchedule;
    private CancellationTokenSource? _loadCts;
    private int _layoutGeneration;
    private const int PageSlotsPerUiBatch = 4;
    private int _suspendScrollPageTracking;
    private bool _isAnnotationEditingEnabled;
    private bool _disposed;

    public PdfViewer()
    {
        InitializeComponent();
        UpdateZoomLabel();
        // Handle Ctrl+wheel at the tunnel phase so we can consume the event before the
        // ScrollViewer's content presenter turns it into a scroll gesture. This makes
        // Ctrl+wheel zoom trigger reliably regardless of scroll position.
        AddHandler(PointerWheelChangedEvent, MainScrollViewer_OnPointerWheelChanged, RoutingStrategies.Tunnel);
        _virtualizeScrollTimer = new DispatcherTimer(DispatcherPriority.Background)
        {
            Interval = TimeSpan.FromMilliseconds(120)
        };
        _virtualizeScrollTimer.Tick += (_, _) =>
        {
            _virtualizeScrollTimer!.Stop();
            RequestVirtualizationImmediate();
        };
        _pageNumberBarHideTimer = new DispatcherTimer(DispatcherPriority.Background)
        {
            Interval = TimeSpan.FromSeconds(3)
        };
        _pageNumberBarHideTimer.Tick += (_, _) =>
        {
            _pageNumberBarHideTimer!.Stop();
            if (!_isPointerOverPageNumberHotspot)
                PageNumberBar.IsVisible = false;
        };
    }

    public event EventHandler<AnnotationMovedEventArgs>? AnnotationMoved;
    public event EventHandler<AnnotationCreatedEventArgs>? AnnotationCreated;
    public event EventHandler<AnnotationDeletedEventArgs>? AnnotationDeleted;
    public event EventHandler<AnnotationChangedEventArgs>? AnnotationChanged;
    public event EventHandler<AnnotationSelectedEventArgs>? AnnotationSelected;
    public event EventHandler? DocumentLoaded;
    public string? DocumentPath => _currentPdfPath;
    public int PageCount => _loader.PageCount;
    public IReadOnlyList<FreeTextAnnotationModel> FreeTextAnnotations => _annotations;
    public int CurrentPageIndex => _currentPageIndex;
    public bool IsAnnotationEditingEnabled
    {
        get => _isAnnotationEditingEnabled;
        set
        {
            if (_isAnnotationEditingEnabled == value) return;
            _isAnnotationEditingEnabled = value;
            foreach (var control in _annotationControls.Values.ToList())
                control.IsAnnotationEditingEnabled = value;
        }
    }

    public async void LoadDocument(string filePath)
    {
        if (_disposed) return;
        if (!File.Exists(filePath)) { SetStatus("File not found."); return; }
        _loadCts?.Cancel();
        _loadCts = new CancellationTokenSource();
        var token = _loadCts.Token;
        var layoutGen = ++_layoutGeneration;
        ++_renderGeneration;
        SetLoadUiBusy(true, "正在加载 PDF…");
        try
        {
            var dpiScale = DisplayDpiHelper.GetDpiScale(this);
            var zoom = _zoom;
            var annotations = await Task.Run(() =>
            {
                token.ThrowIfCancellationRequested();
                var bytes = File.ReadAllBytes(filePath);
                token.ThrowIfCancellationRequested();
                _loader.LoadFromBytes(bytes, zoom, dpiScale);
                token.ThrowIfCancellationRequested();
                return PdfAnnotationService.LoadFreeTextAnnotations(filePath);
            }, token).ConfigureAwait(true);
            token.ThrowIfCancellationRequested();
            _currentPdfPath = filePath;
            _annotations.Clear();
            _annotations.AddRange(annotations);
            _currentPageIndex = 0;
            ReleaseAllPageBitmaps();
            PagesPanel.Children.Clear();
            _pageSlots.Clear();
            _annotationControls.Clear();
            _selectedAnnotation = null;
            SetLoadUiBusy(true, "正在构建页面…");
            await BuildDocumentLayoutAsync(layoutGen, token);
            token.ThrowIfCancellationRequested();
            DocumentLoaded?.Invoke(this, EventArgs.Empty);
            SetLoadUiBusy(true, "正在渲染页面…");
            await VirtualizeVisiblePagesAsync();
            MainScrollViewer.Offset = new Vector(MainScrollViewer.Offset.X, 0);
            RequestVirtualizationImmediate();
            UpdatePageInfo();
            SetStatus($"Loaded: {Path.GetFileName(filePath)}, {_loader.PageCount} pages, {_annotations.Count} annotations.");
        }
        catch (OperationCanceledException) { SetStatus("Loading cancelled."); }
        catch (Exception ex) { SetStatus($"Failed: {ex.Message}"); }
        finally { SetLoadUiBusy(false, null); }
    }

    private void SetLoadUiBusy(bool busy, string? status)
    {
        if (!string.IsNullOrEmpty(status)) { LoadingText.Text = status; SetStatus(status); }
        LoadingOverlay.IsVisible = busy;
        MainScrollViewer.IsEnabled = !busy;
    }

    private async Task BuildDocumentLayoutAsync(int layoutGen, CancellationToken token)
    {
        if (_loader.PageCount == 0) { UpdatePageInfo(); return; }
        var gen = ++_renderGeneration;
        var scale = _loader.DisplayScale;
        var pc = _loader.PageCount;
        for (int start = 0; start < pc; start += PageSlotsPerUiBatch)
        {
            token.ThrowIfCancellationRequested();
            if (layoutGen != _layoutGeneration) return;
            var end = Math.Min(start + PageSlotsPerUiBatch, pc);
            await Dispatcher.UIThread.InvokeAsync(() =>
            {
                if (layoutGen != _layoutGeneration || gen != _renderGeneration) return;
                for (int i = start; i < end; i++) AddPageSlot(i, scale);
                SetStatus($"Building pages... {end} / {pc}");
            }, DispatcherPriority.Background);
            await Task.Yield();
        }
        await Dispatcher.UIThread.InvokeAsync(() =>
        {
            if (layoutGen != _layoutGeneration) return;
            _currentPageIndex = Math.Clamp(_currentPageIndex, 0, pc - 1);
            UpdatePageInfo();
        }, DispatcherPriority.Background);
    }

    public bool SaveAnnotations(string? dest, out string? error)
    {
        error = null;
        if (string.IsNullOrEmpty(_currentPdfPath))
        {
            error = "Please open a PDF first.";
            SetStatus(error);
            return false;
        }

        var target = dest ?? _currentPdfPath;
        try
        {
            SyncAllAnnotationModelsFromUi();
            var exportAnnotations = _annotations
                .Concat(GetDomainAnnotationModels())
                .ToList();
            PdfAnnotationService.SaveFreeTextAnnotations(_currentPdfPath, target, exportAnnotations);
            if (!File.Exists(target) || new FileInfo(target).Length == 0)
            {
                error = $"Save failed: output file was not created: {target}";
                SetStatus(error);
                return false;
            }

            SetStatus(!string.Equals(target, _currentPdfPath, StringComparison.OrdinalIgnoreCase)
                ? $"Saved to: {target}" : "Annotations saved.");
            return true;
        }
        catch (Exception ex)
        {
            error = $"Save failed: {ex.Message}";
            SetStatus(error);
            return false;
        }
    }

    public bool SaveAnnotationsFlattened(string dest, out string? error)
    {
        error = null;
        if (string.IsNullOrEmpty(_currentPdfPath))
        {
            error = "Please open a PDF first.";
            SetStatus(error);
            return false;
        }

        try
        {
            SyncAllAnnotationModelsFromUi();
            var exportAnnotations = _annotations
                .Concat(GetDomainAnnotationModels())
                .ToList();
            PdfAnnotationService.SaveFreeTextAnnotationsFlattened(_currentPdfPath, dest, exportAnnotations);
            if (!File.Exists(dest) || new FileInfo(dest).Length == 0)
            {
                error = $"Flatten save failed: output file was not created: {dest}";
                SetStatus(error);
                return false;
            }

            SetStatus($"Flattened PDF saved to: {dest}");
            return true;
        }
        catch (Exception ex)
        {
            error = $"Flatten save failed: {ex.Message}";
            SetStatus(error);
            return false;
        }
    }

    private void BuildDocumentLayout(bool fullRebuild = true)
    {
        if (fullRebuild)
        {
            var gen = ++_renderGeneration;
            ReleaseAllPageBitmaps();
            PagesPanel.Children.Clear();
            _pageSlots.Clear();
            _annotationControls.Clear();
            _selectedAnnotation = null;
            if (_loader.PageCount == 0) { UpdatePageInfo(); return; }
            for (int i = 0; i < _loader.PageCount; i++)
            {
                if (gen != _renderGeneration) return;
                AddPageSlot(i, _loader.DisplayScale);
            }
            _currentPageIndex = Math.Clamp(_currentPageIndex, 0, _loader.PageCount - 1);
            UpdatePageInfo();
        }
        else
            ApplyZoomLayoutInPlace();
    }

    private void ApplyZoomLayoutInPlace()
    {
        if (_pageSlots.Count != _loader.PageCount) { BuildDocumentLayout(true); return; }
        var scale = _loader.DisplayScale;
        for (int i = 0; i < _pageSlots.Count; i++)
        {
            var slot = _pageSlots[i];
            var ps = _loader.PageSizes[i];
            var php = _loader.GetPageHeightInPoints(i);
            slot.PageIndex = i;
            slot.Host.Width = ps.Width + 2;
            slot.Host.Height = ps.Height + 2;
            if (slot.PageRoot is { } pageRoot)
            {
                pageRoot.Width = ps.Width;
                pageRoot.Height = ps.Height;
            }
            if (slot.PageImage is { } pageImage)
            {
                pageImage.Width = ps.Width;
                pageImage.Height = ps.Height;
            }
            if (slot.AnnotationLayer is { } annotationLayer)
            {
                annotationLayer.Width = ps.Width;
                annotationLayer.Height = ps.Height;
                RescaleAnnotationsOnLayer(annotationLayer, i, php, scale);
            }
            if (slot.DomainAnnotationLayer is { } domainLayer)
            {
                domainLayer.Width = ps.Width;
                domainLayer.Height = ps.Height;
                RelayoutDomainAnnotations(slot, php, scale);
            }
        }
        UpdatePageInfo();
    }

    private void RescaleAnnotationsOnLayer(PageAnnotationCanvas layer, int pi, double php, double scale)
    {
        foreach (var c in layer.Children.OfType<FreeTextAnnotationControl>().ToList())
        {
            if (c.Model == null) continue;
            c.Model.PageIndex = pi;
            c.ApplyLayoutFromPdf(php, scale);
            AnnotationAdornerService.Refresh(c);
        }
    }

    private void RelayoutAllAnnotations()
    {
        var s = _loader.DisplayScale;
        foreach (var sl in _pageSlots)
        {
            var php = _loader.GetPageHeightInPoints(sl.PageIndex);
            if (sl.AnnotationLayer is { } annotationLayer)
                RescaleAnnotationsOnLayer(annotationLayer, sl.PageIndex, php, s);
            RelayoutDomainAnnotations(sl, php, s);
        }
    }

    private static void RelayoutDomainAnnotations(PdfPageSlot slot, double pageHeightPoints, double scale)
    {
        var x = slot.DomainAnnotationHorizontalOffset / scale;
        var top = slot.DomainAnnotationVerticalOffset / scale;
        for (var i = 0; i < slot.DomainAnnotationModels.Count; i++)
        {
            var model = slot.DomainAnnotationModels[i];
            model.PdfX = x;
            model.PdfY = pageHeightPoints - top - model.Height;
            if (i < slot.DomainAnnotations.Count)
                slot.DomainAnnotations[i].ApplyLayoutFromPdf(pageHeightPoints, scale);
            x += model.Width + 4 / scale;
        }
    }

    private void AddPageSlot(int pi, double scale)
    {
        var ps = _loader.PageSizes[pi];
        var host = new Border
        {
            Width = ps.Width + 2,
            Height = ps.Height + 2,
            HorizontalAlignment = HorizontalAlignment.Center,
            Margin = new Thickness(0, 0, 0, 4),
            Background = Brushes.White,
            BorderBrush = new SolidColorBrush(Color.Parse("#D1D5DB")),
            BorderThickness = new Thickness(1)
        };
        PagesPanel.Children.Add(host);
        _pageSlots.Add(new PdfPageSlot { PageIndex = pi, Host = host });
    }

    private void MountPageContent(PdfPageSlot slot)
    {
        if (_disposed || slot.IsContentMounted) return;
        var ps = _loader.PageSizes[slot.PageIndex];
        var php = _loader.GetPageHeightInPoints(slot.PageIndex);
        var scale = _loader.DisplayScale;
        var pageRoot = new Panel { Width = ps.Width, Height = ps.Height, Background = Brushes.White, ClipToBounds = false };
        var pageImage = new Image { Width = ps.Width, Height = ps.Height };
        pageRoot.Children.Add(pageImage);
        var loadingOverlay = new TextBlock
        {
            Text = "Loading...",
            HorizontalAlignment = HorizontalAlignment.Center,
            VerticalAlignment = VerticalAlignment.Center,
            Foreground = new SolidColorBrush(Color.Parse("#9CA3AF")),
            FontSize = 13,
            IsHitTestVisible = false
        };
        pageRoot.Children.Add(loadingOverlay);
        var domainLayer = new Canvas
        {
            Width = ps.Width,
            Height = ps.Height,
            ClipToBounds = false,
            IsHitTestVisible = false,
            ZIndex = 3
        };
        pageRoot.Children.Add(domainLayer);
        var annotationLayer = new PageAnnotationCanvas { Width = ps.Width, Height = ps.Height, ZIndex = 2 };
        annotationLayer.AnnotationDragCompleted += (_, args) =>
        {
            CommitAnnotationFromControl(args.Annotation);
            if (args.Annotation.Model is { } model)
                AnnotationMoved?.Invoke(this, new AnnotationMovedEventArgs(model));
        };
        annotationLayer.AnnotationResizeCompleted += (_, _) =>
        {
            foreach (var control in annotationLayer.Children.OfType<FreeTextAnnotationControl>())
            {
                CommitAnnotationFromControl(control);
                if (control.Model is { } model)
                    AnnotationMoved?.Invoke(this, new AnnotationMovedEventArgs(model));
            }
        };
        pageRoot.Children.Add(annotationLayer);

        slot.PageRoot = pageRoot;
        slot.PageImage = pageImage;
        slot.LoadingOverlay = loadingOverlay;
        slot.DomainAnnotationLayer = domainLayer;
        slot.AnnotationLayer = annotationLayer;
        slot.Host.Child = pageRoot;
        RebuildAnnotationsForPage(slot.PageIndex, php, scale, annotationLayer);
        RebuildDomainAnnotationControls(slot);
    }

    private void UnmountPageContent(PdfPageSlot slot)
    {
        if (!slot.IsContentMounted) return;
        if (slot.AnnotationLayer is { } annotationLayer)
        {
            foreach (var control in annotationLayer.Children.OfType<FreeTextAnnotationControl>().ToList())
            {
                CommitAnnotationFromControl(control);
                annotationLayer.UnhookAnnotation(control);
                if (control.Model is { } model &&
                    _annotationControls.TryGetValue(model.Id, out var tracked) &&
                    ReferenceEquals(tracked, control))
                    _annotationControls.Remove(model.Id);
            }
        }
        if (_selectedAnnotation?.Model?.PageIndex == slot.PageIndex)
            _selectedAnnotation = null;
        ReleasePageBitmap(slot);
        slot.Host.Child = null;
        slot.PageRoot = null;
        slot.PageImage = null;
        slot.LoadingOverlay = null;
        slot.DomainAnnotationLayer = null;
        slot.DomainAnnotations.Clear();
        slot.AnnotationLayer = null;
    }

    private void RebuildDomainAnnotationControls(PdfPageSlot slot)
    {
        if (slot.DomainAnnotationLayer is not { } layer) return;
        layer.Children.Clear();
        slot.DomainAnnotations.Clear();
        var pageHeightPoints = _loader.GetPageHeightInPoints(slot.PageIndex);
        var scale = _loader.DisplayScale;
        foreach (var model in slot.DomainAnnotationModels)
        {
            var annotation = new FreeTextAnnotationControl
            {
                Model = model,
                IsVisible = true,
                IsHitTestVisible = false,
                Focusable = false,
                ZIndex = 3
            };
            annotation.ApplyLayoutFromPdf(pageHeightPoints, scale);
            layer.Children.Add(annotation);
            slot.DomainAnnotations.Add(annotation);
        }
    }

    private void RequestVirtualization() { if (_pageSlots.Count == 0 || _virtualizeScrollTimer == null) return; _virtualizeScrollTimer.Stop(); _virtualizeScrollTimer.Start(); }
    private void RequestVirtualizationImmediate() { if (_pageSlots.Count == 0) return; if (_virtualizeRunning) { _virtualizePending = true; return; } _ = RunVirtualizeLoopAsync(); ScheduleUnloadOffscreenPages(); }
    private async Task RunVirtualizeLoopAsync() { _virtualizeRunning = true; try { do { _virtualizePending = false; await VirtualizeVisiblePagesAsync(); } while (_virtualizePending && _pageSlots.Count > 0); } finally { _virtualizeRunning = false; } }
    private void ScheduleUnloadOffscreenPages() { _unloadCts?.Cancel(); _unloadCts = new CancellationTokenSource(); _ = UnloadAfterDelayAsync(_unloadCts.Token); }
    private async Task UnloadAfterDelayAsync(CancellationToken t) { try { await Task.Delay(400, t); UnloadOffscreenPages(); } catch (OperationCanceledException) { } }

    private void UnloadOffscreenPages()
    {
        if (_pageSlots.Count == 0) return;
        var keep = new HashSet<int>(GetVisiblePageRange(ViewportPageBuffer));
        if (_selectedAnnotation?.Model?.PageIndex is { } selectedPageIndex)
            keep.Add(selectedPageIndex);
        foreach (var s in _pageSlots)
        {
            if (!keep.Contains(s.PageIndex))
                UnmountPageContent(s);
        }
    }

    private void ReleaseAllPageBitmaps()
    {
        foreach (var slot in _pageSlots)
            ReleasePageBitmap(slot);
    }

    private static void ReleasePageBitmap(PdfPageSlot slot)
    {
        var bitmap = slot.PageImage?.Source as IDisposable;
        if (slot.PageImage is { } pageImage)
            pageImage.Source = null;
        slot.IsBitmapLoaded = false;
        if (slot.LoadingOverlay is { } loadingOverlay)
            loadingOverlay.IsVisible = false;
        bitmap?.Dispose();
    }

    private static void AttachPageBitmap(PdfPageSlot slot, WriteableBitmap bitmap)
    {
        if (slot.PageImage is not { } pageImage)
        {
            bitmap.Dispose();
            return;
        }
        var previousBitmap = pageImage.Source as IDisposable;
        pageImage.Source = bitmap;
        slot.IsBitmapLoaded = true;
        if (slot.LoadingOverlay is { } loadingOverlay)
            loadingOverlay.IsVisible = false;
        previousBitmap?.Dispose();
    }

    private async Task VirtualizeVisiblePagesAsync()
    {
        if (_pageSlots.Count == 0) return;
        var gen = _renderGeneration;
        var vis = GetVisiblePageRange(ViewportPageBuffer);
        foreach (var pageIndex in vis)
            MountPageContent(_pageSlots[pageIndex]);
        var ordered = vis.OrderBy(i => Math.Abs(i - _currentPageIndex)).ThenBy(i => i).ToList();
        if (ordered.Count == 0) return;
        await TryRenderPageAsync(ordered[0], gen);
        if (ordered.Count > 1) await Task.WhenAll(ordered.Skip(1).Select(i => TryRenderPageAsync(i, gen)));
    }

    private async Task TryRenderPageAsync(int pi, int gen)
    {
        if (_disposed || gen != _renderGeneration || pi < 0 || pi >= _pageSlots.Count) return;
        var s = _pageSlots[pi];
        MountPageContent(s);
        if (s.PageImage?.Source != null && s.IsBitmapLoaded) return;
        lock (_slotLoadGate) { if (s.IsRendering) return; s.IsRendering = true; }
        if (s.PageImage?.Source == null && s.LoadingOverlay is { } loadingOverlay)
            loadingOverlay.IsVisible = true;
        WriteableBitmap? bitmap = null;
        var renderPermitAcquired = false;
        try
        {
            await _pageRenderSemaphore.WaitAsync();
            renderPermitAcquired = true;
            if (_disposed || gen != _renderGeneration || !GetVisiblePageRange(ViewportPageBuffer).Contains(pi))
                return;
            bitmap = await Task.Run(() => _loader.RenderPage(pi));
            if (_disposed || gen != _renderGeneration) return;
            if (!GetVisiblePageRange(ViewportPageBuffer).Contains(pi))
            {
                if (s.LoadingOverlay is { } staleLoadingOverlay)
                    staleLoadingOverlay.IsVisible = false;
                return;
            }
            AttachPageBitmap(s, bitmap);
            bitmap = null;
        }
        finally
        {
            bitmap?.Dispose();
            if (renderPermitAcquired)
                _pageRenderSemaphore.Release();
            s.IsRendering = false;
        }
    }

    private IReadOnlyList<int> GetVisiblePageRange(int buf)
    {
        if (_pageSlots.Count == 0) return Array.Empty<int>();
        var vt = MainScrollViewer.Offset.Y; var vb = vt + MainScrollViewer.Bounds.Height;
        int first = -1, last = -1; double cy = 16;
        for (int i = 0; i < _pageSlots.Count; i++) { var h = GetPageHostHeight(i); if (cy + h > vt && cy < vb) { if (first < 0) first = i; last = i; } cy += h; }
        if (first < 0) first = last = Math.Clamp(_currentPageIndex, 0, _pageSlots.Count - 1);
        var s = Math.Max(0, first - buf); var e = Math.Min(_pageSlots.Count - 1, last + buf);
        var cur = Math.Clamp(_currentPageIndex, 0, _pageSlots.Count - 1);
        s = Math.Min(s, Math.Max(0, cur - buf)); e = Math.Max(e, Math.Min(_pageSlots.Count - 1, cur + buf));
        var list = new List<int>(); for (int i = s; i <= e; i++) list.Add(i); return list;
    }

    private void RebuildAnnotationsForPage(int pi, double php, double scale, Canvas layer) { layer.Children.Clear(); foreach (var m in _annotations.Where(a => a.PageIndex == pi)) AddAnnotationControl(m, php, scale, layer); }
    private void AddAnnotationControl(FreeTextAnnotationModel m, double php, double scale, Canvas layer)
    {
        var c = new FreeTextAnnotationControl
        {
            Model = m,
            IsAnnotationEditingEnabled = IsAnnotationEditingEnabled
        };
        c.ApplyLayoutFromPdf(php, scale);
        c.CommitEdit += (_, _) =>
        {
            CommitAnnotationFromControl(c);
            if (c.Model is { } model)
                AnnotationChanged?.Invoke(this, new AnnotationChangedEventArgs(model));
        };
        c.DeleteRequested += (_, _) => RemoveAnnotation(m.Id);
        c.PropertyChanged += (_, e) => { if (e.Property == FreeTextAnnotationControl.IsSelectedProperty) OnAnnotationIsSelectedChanged(c); };
        layer.Children.Add(c);
        if (layer is PageAnnotationCanvas pac) pac.HookAnnotation(c);
        _annotationControls[m.Id] = c;
    }

    private void CommitAnnotationFromControl(FreeTextAnnotationControl c)
    {
        if (c.Model == null) return;
        var pi = c.Model.PageIndex; var php = _loader.GetPageHeightInPoints(pi); var scale = _loader.DisplayScale;
        var left = Canvas.GetLeft(c); var top = Canvas.GetTop(c);
        if (double.IsNaN(left)) left = 0; if (double.IsNaN(top)) top = 0;
        var annotationSize = AnnotationAdornerService.GetAnnotationSize(c);
        var hp = PdfCoordinateConverter.PixelsToPoints(annotationSize.Height, scale);
        var wp = PdfCoordinateConverter.PixelsToPoints(annotationSize.Width, scale);
        var pdf = PdfCoordinateConverter.AvaloniaToPdf(new Point(left, top), hp, php, scale);
        var px = pdf.X; var py = pdf.Y;
        PdfCoordinateConverter.ClampPdfRect(_loader.GetPageWidthInPoints(pi), php, ref px, ref py, wp, hp);
        c.SyncToModel(); c.Model.PdfX = px; c.Model.PdfY = py; c.Model.Width = wp; c.Model.Height = hp;
        c.ApplyLayoutFromPdf(php, scale);
    }

    private void SyncAllAnnotationModelsFromUi() { foreach (var s in _pageSlots) if (s.AnnotationLayer is { } layer) foreach (var c in layer.Children.OfType<FreeTextAnnotationControl>()) CommitAnnotationFromControl(c); }
    private void RemoveAnnotation(string id) { _annotations.RemoveAll(a => a.Id == id); if (_annotationControls.Remove(id, out var c)) { var l = _pageSlots.Select(s => s.AnnotationLayer).OfType<PageAnnotationCanvas>().FirstOrDefault(x => x.Children.Contains(c)); l?.UnhookAnnotation(c); l?.Children.Remove(c); } if (_selectedAnnotation?.Model?.Id == id) _selectedAnnotation = null; }
    private void OnAnnotationIsSelectedChanged(FreeTextAnnotationControl c)
    {
        if (c.IsSelected)
        {
            if (_selectedAnnotation != c)
            {
                if (_selectedAnnotation != null) _selectedAnnotation.IsSelected = false;
                _selectedAnnotation = c;
            }

            if (c.Model?.Id is { Length: > 0 } annotationId)
                AnnotationSelected?.Invoke(this, new AnnotationSelectedEventArgs(annotationId));
        }
        else if (_selectedAnnotation == c)
        {
            _selectedAnnotation = null;
        }

    }

    private void DoSelectAnnotation(FreeTextAnnotationControl c)
    {
        if (_selectedAnnotation != null && _selectedAnnotation != c) _selectedAnnotation.IsSelected = false;
        c.IsSelected = true;
        _selectedAnnotation = c;
        AnnotationAdornerService.RebindSelectedAdorner(c);
    }
    private void ClearSelection()
    {
        if (_selectedAnnotation == null) return;
        if (_selectedAnnotation.IsInEditMode) _selectedAnnotation.EndEdit();
        _selectedAnnotation.IsSelected = false;
        _selectedAnnotation = null;
    }
    private void MainScrollViewer_OnScrollChanged(object? s, ScrollChangedEventArgs e)
    {
        if (_loader.PageCount == 0 || _pageSlots.Count == 0) return;
        ShowPageNumberBar();
        if (_suspendScrollPageTracking <= 0) UpdateCurrentPageIndexFromViewport();
        ReleaseFramesOutsideVisible();
        RequestVirtualization();
    }

    private void PageNumberHotspot_OnPointerEntered(object? s, PointerEventArgs e)
    {
        _isPointerOverPageNumberHotspot = true;
        ShowPageNumberBar();
    }

    private void PageNumberHotspot_OnPointerExited(object? s, PointerEventArgs e)
    {
        _isPointerOverPageNumberHotspot = false;
        SchedulePageNumberBarHide();
    }

    private void ShowPageNumberBar()
    {
        if (_disposed) return;
        PageNumberBar.IsVisible = true;
        if (_isPointerOverPageNumberHotspot)
        {
            _pageNumberBarHideTimer?.Stop();
            return;
        }
        SchedulePageNumberBarHide();
    }

    private void SchedulePageNumberBarHide()
    {
        if (_disposed || _isPointerOverPageNumberHotspot || _pageNumberBarHideTimer == null) return;
        _pageNumberBarHideTimer.Stop();
        _pageNumberBarHideTimer.Start();
    }

    private void UpdateCurrentPageIndexFromViewport() { if (_pageSlots.Count == 0) return; double cy = 16; var vc = MainScrollViewer.Offset.Y + MainScrollViewer.Bounds.Height / 2; int best = 0; for (int i = 0; i < _pageSlots.Count; i++) { var h = GetPageHostHeight(i); if (vc >= cy && vc < cy + h) { best = i; break; } if (vc >= cy + h) best = i; cy += h; } if (best != _currentPageIndex) { _currentPageIndex = best; UpdatePageInfo(); } }
    private double GetPageHostHeight(int i)
    {
        var host = _pageSlots[i].Host;
        if (host.Bounds.Height > 0)
            return host.Bounds.Height + host.Margin.Top + host.Margin.Bottom;
        return _loader.PageSizes.Count > i ? _loader.PageSizes[i].Height + 20 : 800;
    }
    private void ScrollToPage(int pi) { if (pi < 0 || pi >= PagesPanel.Children.Count) return; double y = 16; for (int i = 0; i < pi && i < _pageSlots.Count; i++) y += GetPageHostHeight(i); MainScrollViewer.Offset = new Vector(MainScrollViewer.Offset.X, Math.Max(0, y - 8)); }
    private void UpdateZoomLabel()
    {
        if (ZoomComboBox != null && ZoomSlider != null)
            ZoomComboBox.Text = $"{(int)ZoomSlider.Value}%";
    }
    private void UpdatePageInfo()
    {
        if (_loader.PageCount == 0)
        {
            SetCurrentPageNumberText("0");
            PageCountText.Text = "0";
            PrevPageButton.IsEnabled = false;
            NextPageButton.IsEnabled = false;
            return;
        }

        SetCurrentPageNumberText((_currentPageIndex + 1).ToString());
        PageCountText.Text = _loader.PageCount.ToString();
        PrevPageButton.IsEnabled = _currentPageIndex > 0;
        NextPageButton.IsEnabled = _currentPageIndex < _loader.PageCount - 1;
    }

    private void SetCurrentPageNumberText(string text)
    {
        _isUpdatingPageNumberText = true;
        CurrentPageNumberText.Text = text;
        _isUpdatingPageNumberText = false;
    }

    private void CurrentPageNumberText_OnTextChanged(object? sender, TextChangedEventArgs e)
    {
        if (_isUpdatingPageNumberText) return;

        var text = CurrentPageNumberText.Text ?? string.Empty;
        var digits = new string(text.Where(c => c is >= '0' and <= '9').ToArray());
        if (digits == text) return;

        SetCurrentPageNumberText(digits);
        CurrentPageNumberText.CaretIndex = digits.Length;
    }

    private void CurrentPageNumberText_OnKeyDown(object? sender, KeyEventArgs e)
    {
        if (e.Key != Key.Enter) return;
        e.Handled = true;
        CommitCurrentPageNumber();
    }

    private void CurrentPageNumberText_OnLostFocus(object? sender, RoutedEventArgs e)
    {
        CommitCurrentPageNumber();
    }

    private void CommitCurrentPageNumber()
    {
        if (_loader.PageCount == 0)
        {
            UpdatePageInfo();
            return;
        }

        if (!int.TryParse(CurrentPageNumberText.Text, out var pageNumber))
        {
            UpdatePageInfo();
            return;
        }

        pageNumber = Math.Clamp(pageNumber, 1, _loader.PageCount);
        _currentPageIndex = pageNumber - 1;
        ScrollToPage(_currentPageIndex);
        UpdatePageInfo();
    }
    private void ScrollViewer_OnBackgroundClick(object? s, PointerPressedEventArgs e)
    {
        if (e.Source is FreeTextAnnotationControl or TextBox) return;
        if (_selectedAnnotation?.IsInEditMode == true) _selectedAnnotation.EndEdit();
        if (_selectedAnnotation != null) ClearSelection();
    }

    private void PrevPage_OnClick(object? s, RoutedEventArgs e) { if (_loader.PageCount == 0) return; _currentPageIndex = Math.Max(0, _currentPageIndex - 1); ScrollToPage(_currentPageIndex); UpdatePageInfo(); }
    private void NextPage_OnClick(object? s, RoutedEventArgs e) { if (_loader.PageCount == 0) return; _currentPageIndex = Math.Min(_loader.PageCount - 1, _currentPageIndex + 1); ScrollToPage(_currentPageIndex); UpdatePageInfo(); }
    private void SetStatus(string msg) => StatusText.Text = msg;

    public void Dispose()
    {
        if (_disposed) return;
        _disposed = true;
        ++_renderGeneration;
        ++_layoutGeneration;
        _loadCts?.Cancel();
        _loadCts?.Dispose();
        _loadCts = null;
        _unloadCts?.Cancel();
        _unloadCts?.Dispose();
        _unloadCts = null;
        _virtualizeScrollTimer?.Stop();
        _virtualizeScrollTimer = null;
        _pageNumberBarHideTimer?.Stop();
        _pageNumberBarHideTimer = null;
        foreach (var slot in _pageSlots)
            UnmountPageContent(slot);
        PagesPanel.Children.Clear();
        _pageSlots.Clear();
        _annotationControls.Clear();
        _selectedAnnotation = null;
        _loader.Dispose();
        GC.SuppressFinalize(this);
    }
}