using Avalonia.Controls;
using Avalonia.Platform.Storage;
using Avalonia.Threading;
using System.Collections.ObjectModel;
using System.ComponentModel;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using DynamicData;
using AcrfAnnotationTool.Models;
using AcrfAnnotationTool.Services;
using AcrfAnnotationTool.Services.Interface;
using AcrfAnnotationTool.ViewModels;
using PdfViewerControl.Controls;
using PdfViewerControl.Models;
using PatChes.Controls.DataGrid;

namespace AcrfAnnotationTool.ViewModels.Documents;

public enum SpecExportOutcome
{
    Succeeded,
    Canceled,
    Failed
}

public partial class AcrfDocumentViewModel : ObservableObject
{
    private static readonly string[] DomainColors =
    [
        "#FFBFFFFF",
        "#FFFFFF96",
        "#FF96FF96",
        "#FFFFBE9B"
    ];

    private static readonly Lazy<TrueTypeFontMetrics> AnnotationMeasurementFont =
        new(() => CreateMeasurementFont("ARIAL.TTF"));
    private static readonly Lazy<TrueTypeFontMetrics> DomainAnnotationMeasurementFont =
        new(() => CreateMeasurementFont("ARIALBD.TTF"));
    private static readonly Lazy<TrueTypeFontMetrics> ChineseAnnotationMeasurementFont =
        new(() => CreateMeasurementFont("ARIALUNI.TTF"));
    private const float AnnotationFontSize = 12;
    private const double AnnotationHorizontalPadding = 10;
    private const double AnnotationLineHeight = AnnotationFontSize * 1.2;
    private const double AnnotationVerticalPadding = 4;
    private const double ChineseAnnotationAdditionalLineSpacing = 2.28;
    private const double LegacyChineseAnnotationLineHeight = AnnotationFontSize * 1.35;
    private const double LegacyChineseAnnotationVerticalPadding = 6;
    private const float DomainAnnotationFontSize = 10;
    private const double DomainAnnotationHorizontalPadding = 10;

    private readonly SourceList<Annotation> _source = new();
    private readonly ReadOnlyObservableCollection<Annotation> _annotations;
    private string _status = "请打开 PDF 或导入注释表";
    private string? _pdfPath;
    private PdfViewer? _viewer;
    private DataGrid? _annotationGrid;
    private Annotation? _selectedAnnotationRow;
    private Window? _window;
    private readonly HashSet<Annotation> _observedAnnotations = new();
    private readonly Dictionary<Annotation, long> _annotationTextSyncVersions = new();
    private long _nextAnnotationTextSyncVersion;
    private readonly CdiscDatasetCatalog _cdiscDatasetCatalog;
    private readonly TemplateVariableDomainCatalog _variableDomainCatalog;
    private readonly IMessageService _messageService;
    private bool _synchronizing;
    private bool _selectingPdfAnnotation;
    private bool _hasImportedSpec;

    public MainWindowViewModel? AppOwner { get; init; }
    public ReadOnlyObservableCollection<Annotation> Annotations => _annotations;
    public SuggestionOptionsAsyncLoader SuggestionOptionsAsyncLoader { get; }
    public string Status { get => _status; private set => SetProperty(ref _status, value); }
    public string? PdfPath { get => _pdfPath; private set => SetProperty(ref _pdfPath, value); }
    public double DomainAnnotationHorizontalOffset { get; private set; }
    public double DomainAnnotationVerticalOffset { get; private set; }
    public bool IsPdfAnnotationEditingEnabled { get; private set; }
    public bool Language { get; private set; }
    private string GetAnnotationFontFamily() => Language ? "Arial Unicode MS" : "Arial";

    private void ApplyAnnotationFontFamily()
    {
        if (_viewer is null)
            return;

        _viewer.AnnotationFontFamily = GetAnnotationFontFamily();
        foreach (var model in _viewer.GetAnnotations())
        {
            _viewer.UpdateAnnotation(model.Id, annotation =>
            {
                annotation.FontFamily = _viewer.AnnotationFontFamily;
                if (Language)
                    annotation.IsBold = false;
            });
        }
        foreach (var model in _viewer.GetDomainAnnotationModels())
        {
            model.FontFamily = _viewer.AnnotationFontFamily;
            model.IsBold = !Language;
        }
    }

    [ObservableProperty]
    private Annotation? _selectedItem;

    public AcrfDocumentViewModel(
        CdiscDatasetCatalog cdiscDatasetCatalog,
        TemplateVariableDomainCatalog variableDomainCatalog,
        SuggestionOptionsAsyncLoader suggestionOptionsAsyncLoader,
        IMessageService messageService)
    {
        _cdiscDatasetCatalog = cdiscDatasetCatalog;
        _variableDomainCatalog = variableDomainCatalog;
        SuggestionOptionsAsyncLoader = suggestionOptionsAsyncLoader;
        _messageService = messageService;
        var settings = ApplicationSettingsStore.Load();
        DomainAnnotationHorizontalOffset = settings.DomainAnnotationHorizontalOffset;
        DomainAnnotationVerticalOffset = settings.DomainAnnotationVerticalOffset;
        IsPdfAnnotationEditingEnabled = settings.IsPdfAnnotationEditingEnabled ?? false;
        Language = settings.Language;
        _cdiscDatasetCatalog.SetLanguage(Language);
        _source.Connect().Bind(out _annotations).Subscribe();
    }

    public void Attach(PdfViewer viewer, Window window)
    {
        if (!ReferenceEquals(_viewer, viewer))
        {
            if (_viewer is not null)
            {
                _viewer.AnnotationMoved -= OnAnnotationMoved;
                _viewer.AnnotationCreated -= OnAnnotationCreated;
                _viewer.AnnotationDeleted -= OnAnnotationDeleted;
                _viewer.AnnotationChanged -= OnAnnotationChanged;
                _viewer.AnnotationSelected -= OnAnnotationSelected;
                _viewer.DocumentLoaded -= OnViewerDocumentLoaded;
                _viewer.Dispose();
            }
            _viewer = viewer;
            _viewer.AnnotationMoved += OnAnnotationMoved;
            _viewer.AnnotationCreated += OnAnnotationCreated;
            _viewer.AnnotationDeleted += OnAnnotationDeleted;
            _viewer.AnnotationChanged += OnAnnotationChanged;
            _viewer.AnnotationSelected += OnAnnotationSelected;
            _viewer.DocumentLoaded += OnViewerDocumentLoaded;
        }

        _viewer.IsAnnotationEditingEnabled = IsPdfAnnotationEditingEnabled;
        _viewer.AnnotationFontFamily = GetAnnotationFontFamily();
        _window = window;
        if (!string.IsNullOrWhiteSpace(PdfPath) && !string.Equals(_viewer.DocumentPath, PdfPath, StringComparison.OrdinalIgnoreCase))
            _viewer.LoadDocument(PdfPath);
        else
            GenerateDomainAnnotations();
    }

    public void AttachAnnotationGrid(DataGrid annotationGrid)
    {
        _annotationGrid = annotationGrid;
        if (SelectedItem is { } selectedItem)
        {
            SetSelectedAnnotationRow(selectedItem);
            _annotationGrid.ScrollIntoView(selectedItem);
        }
    }

    public bool TryReplayAnnotationHistory(bool redo)
    {
        if (_annotationGrid is not { IsEditing: false, IsReadOnly: false } grid ||
            !(redo ? grid.CanRedo : grid.CanUndo))
            return false;

        if (redo) grid.Redo();
        else grid.Undo();
        return true;
    }

    [RelayCommand]
    private async Task OpenPdfAsync() => await OpenPdfWithResultAsync();

    public async Task<bool> OpenPdfWithResultAsync()
    {
        if (_window is null) return false;
        var files = await _window.StorageProvider.OpenFilePickerAsync(new FilePickerOpenOptions
        {
            Title = "选择 aCRF PDF",
            AllowMultiple = false,
            FileTypeFilter = [new FilePickerFileType("PDF 文件") { Patterns = ["*.pdf"] }]
        });
        var file = files.FirstOrDefault();
        if (file is null) return false;
        var path = file.TryGetLocalPath();
        if (string.IsNullOrWhiteSpace(path)) return false;
        PdfPath = path;
        if (_viewer is null) return false;
        _annotationGrid?.CancelEdit();
        _annotationGrid?.ClearHistory();
        _viewer.LoadDocument(path);
        Status = $"已打开：{Path.GetFileName(path)}";
        _messageService.Success(Status);
        return true;
    }

    [RelayCommand(IncludeCancelCommand = true)]
    private async Task ImportAsync(CancellationToken cancellationToken) => await ImportWithResultAsync(cancellationToken);

    public async Task<bool> ImportWithResultAsync(CancellationToken cancellationToken = default)
    {
        if (_window is null) return false;
        try
        {
            var files = await _window.StorageProvider.OpenFilePickerAsync(new FilePickerOpenOptions
            {
                Title = "导入注释 Spec",
                AllowMultiple = false,
                FileTypeFilter = [new FilePickerFileType("Excel 文件") { Patterns = ["*.xlsx"] }]
            });
            var path = files.FirstOrDefault()?.TryGetLocalPath();
            if (string.IsNullOrWhiteSpace(path)) return false;
            var rows = await Task.Run(
                () => AnnotationExcelService.ImportAsync(path, cancellationToken), cancellationToken);
            cancellationToken.ThrowIfCancellationRequested();
            foreach (var row in rows)
                row.AnnotationUniqueId = Guid.NewGuid().ToString();
            _annotationGrid?.CancelEdit();
            _annotationGrid?.ClearHistory();
            UnobserveAllAnnotations();
            _source.Edit(updater =>
            {
                updater.Clear();
                updater.AddRange(rows);
            });
            foreach (var row in rows)
                ObserveAnnotation(row);

            _hasImportedSpec = true;
            SyncRowsToPdf(rows);
            Status = $"已导入 {rows.Count} 条注释";
            _messageService.Success(Status);
            return true;
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            Status = "Spec 导入已取消";
        }
        catch (Exception exception)
        {
            Status = $"Spec 导入失败：{exception.Message}";
            _messageService.Error(Status);
        }
        return false;
    }

    [RelayCommand(IncludeCancelCommand = true)]
    private async Task ExportAsync(CancellationToken cancellationToken)
    {
        await ExportSpecAsync(cancellationToken);
    }

    public async Task<SpecExportOutcome> ExportSpecAsync(
        CancellationToken cancellationToken = default)
    {
        if (_window is null)
            return SpecExportOutcome.Failed;

        try
        {
            var file = await _window.StorageProvider.SaveFilePickerAsync(new FilePickerSaveOptions
            {
                Title = "导出注释 Spec",
                SuggestedFileName = $"aCRF-annotations-{DateTime.Now:yyyyMMdd-HHmmss-fff}.xlsx",
                FileTypeChoices = new[] { new FilePickerFileType("Excel 文件") { Patterns = new[] { "*.xlsx" } } }
            });
            var path = file?.TryGetLocalPath();
            if (string.IsNullOrWhiteSpace(path))
                return SpecExportOutcome.Canceled;

            var rows = _annotations.Select(row => (Annotation)row.Clone()).ToArray();
            await Task.Run(
                () => AnnotationExcelService.ExportAsync(path, rows, cancellationToken), cancellationToken);
            Status = $"已导出：{Path.GetFileName(path)}";
            _messageService.Success(Status);
            return SpecExportOutcome.Succeeded;
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            Status = "Spec 导出已取消";
            return SpecExportOutcome.Canceled;
        }
        catch (Exception exception)
        {
            Status = $"Spec 导出失败：{exception.Message}";
            _messageService.Error(Status);
            return SpecExportOutcome.Failed;
        }
    }

    [RelayCommand]
    private async Task SavePdfAsync() => await SavePdfWithResultAsync();

    public async Task<bool> SavePdfWithResultAsync()
    {
        if (_viewer is null)
            return false;

        var path = await PickPdfSavePathAsync("另存 PDF", "acrf.pdf");
        if (string.IsNullOrWhiteSpace(path))
            return false;

        if (!_viewer.SaveAnnotations(path, out var error))
        {
            Status = error ?? "PDF 另存失败";
            _messageService.Error(Status);
            return false;
        }

        Status = $"PDF 注释已另存为：{Path.GetFileName(path)}";
        _messageService.Success(Status);
        return true;
    }

    [RelayCommand]
    private async Task SavePdfFlattenAsync() => await SavePdfFlattenWithResultAsync();

    public async Task<bool> SavePdfFlattenWithResultAsync()
    {
        if (_viewer is null)
            return false;

        var path = await PickPdfSavePathAsync("另存 PDF 并 Flatten", "acrf_flatten.pdf");
        if (string.IsNullOrWhiteSpace(path))
            return false;

        if (!_viewer.SaveAnnotationsFlattened(path, out var error))
        {
            Status = error ?? "PDF Flatten 另存失败";
            _messageService.Error(Status);
            return false;
        }

        Status = $"Flatten PDF 已另存为：{Path.GetFileName(path)}";
        _messageService.Success(Status);
        return true;
    }

    private async Task<string?> PickPdfSavePathAsync(string title, string suggestedFileName)
    {
        if (_window is null)
            return null;

        var file = await _window.StorageProvider.SaveFilePickerAsync(new FilePickerSaveOptions
        {
            Title = title,
            SuggestedFileName = suggestedFileName,
            FileTypeChoices =
            [
                new FilePickerFileType("PDF 文件") { Patterns = ["*.pdf"] }
            ]
        });
        return file?.TryGetLocalPath();
    }

    [RelayCommand]
    private void DeleteSelected()
    {
        var item = SelectedItem;
        if (item is null) return;
        _annotationGrid?.CommitEdit();
        if (_annotationGrid?.IsEditing == true) return;
        var index = _source.Items.ToList().IndexOf(item);
        if (index < 0) return;
        var model = _viewer?.GetAnnotationById(item.AnnotationUniqueId)?.Clone();
        var pdfIndex = _viewer?.GetAnnotations().ToList().FindIndex(annotation => annotation.Id == item.AnnotationUniqueId) ?? -1;
        RemoveAnnotationRow(item);
        _annotationGrid?.RecordUndoableAction(
            () => RestoreAnnotationRow(item, index, model, pdfIndex),
            () => RemoveAnnotationRow(item),
            item);
    }

    private void RemoveAnnotationRow(Annotation item)
    {
        _synchronizing = true;
        try { _viewer?.DeleteAnnotation(item.AnnotationUniqueId); }
        finally { _synchronizing = false; }
        UnobserveAnnotation(item);
        _source.Remove(item);
        RefreshPageAnnotationColors(item.PageNum);
        if (ReferenceEquals(SelectedItem, item)) SelectedItem = null;
        _annotationGrid?.RefreshRows();
        Status = "已删除选中注释";
    }

    private void RestoreAnnotationRow(Annotation item, int index, FreeTextAnnotationModel? model, int pdfIndex)
    {
        if (_source.Items.Contains(item)) return;
        _synchronizing = true;
        try
        {
            if (model != null && _viewer != null && !_viewer.RestoreAnnotation(model, pdfIndex))
                throw new InvalidOperationException("无法恢复已删除的 PDF 注释。");
            _source.Edit(updater => updater.Insert(Math.Clamp(index, 0, updater.Count), item));
            ObserveAnnotation(item);
            RefreshPageAnnotationColors(item.PageNum);
        }
        finally { _synchronizing = false; }
        _annotationGrid?.RefreshRows();
        SelectedItem = item;
        Status = "已撤销删除注释";
    }

    [RelayCommand]
    private void CopySelected()
    {
        var source = SelectedItem;
        if (source is null) return;
        var copy = (Annotation)source.Clone();
        copy.AnnotationUniqueId = Guid.NewGuid().ToString();
        if (string.Equals(copy.Type, "Header", StringComparison.OrdinalIgnoreCase))
        {
            copy.Type = "Variable";
            copy.FormOutline = string.Empty;
        }
        if (!string.IsNullOrWhiteSpace(copy.AnnotationText))
            copy.X += (int)Math.Round(GetAnnotationWidth(source)) + 10;
        copy.Width = 0;
        copy.Line = 1;

        var index = _source.Items.ToList().IndexOf(source);
        _source.Edit(updater => updater.Insert(Math.Max(0, index + 1), copy));
        ObserveAnnotation(copy);
        CreatePdfAnnotation(copy);
        GenerateDomainAnnotations();
        SelectedItem = copy;
        Status = "已复制选中注释";
    }

    public void ApplyDomainAnnotationOffsets(double horizontalOffset, double verticalOffset) =>
        ApplyDomainAnnotationSettings(
            horizontalOffset,
            verticalOffset,
            IsPdfAnnotationEditingEnabled,
            Language);

    public void ApplyDomainAnnotationSettings(
        double horizontalOffset,
        double verticalOffset,
        bool isPdfAnnotationEditingEnabled,
        bool language)
    {
        if (!double.IsFinite(horizontalOffset) || !double.IsFinite(verticalOffset) ||
            horizontalOffset < 0 || verticalOffset < 0)
        {
            return;
        }

        var previousLanguage = Language;
        DomainAnnotationHorizontalOffset = horizontalOffset;
        DomainAnnotationVerticalOffset = verticalOffset;
        IsPdfAnnotationEditingEnabled = isPdfAnnotationEditingEnabled;
        Language = language;
        _cdiscDatasetCatalog.SetLanguage(language);
        if (_viewer is not null)
        {
            _viewer.IsAnnotationEditingEnabled = isPdfAnnotationEditingEnabled;
            ApplyAnnotationFontFamily();
            RefreshAutomaticAnnotationHeights(previousLanguage);
        }
        GenerateDomainAnnotations();

        var settings = ApplicationSettingsStore.Load() with
        {
            DomainAnnotationHorizontalOffset = horizontalOffset,
            DomainAnnotationVerticalOffset = verticalOffset,
            IsPdfAnnotationEditingEnabled = isPdfAnnotationEditingEnabled,
            Language = language
        };
        if (ApplicationSettingsStore.TrySave(settings, out var error))
        {
            var editingStatus = isPdfAnnotationEditingEnabled ? "允许" : "禁止";
            Status = $"Domain annotation 位置已更新；PDF 注释编辑：{editingStatus}";
        }
        else
        {
            Status = $"设置已更新，但配置保存失败：{error}";
        }
    }

    [RelayCommand]
    private void UppercaseSelected()
    {
        if (SelectedItem is null) return;
        SelectedItem.AnnotationText = SelectedItem.AnnotationText.ToUpperInvariant();
    }

    private static TrueTypeFontMetrics CreateMeasurementFont(string fontFileName)
    {
        var fontPath = Path.Combine(AppContext.BaseDirectory, "Fonts", fontFileName);
        if (!File.Exists(fontPath))
            throw new FileNotFoundException($"Required annotation measurement font was not found: {fontFileName}", fontPath);
        return new TrueTypeFontMetrics(fontPath);
    }

    private TrueTypeFontMetrics GetAnnotationMeasurementFont() =>
        Language ? ChineseAnnotationMeasurementFont.Value : AnnotationMeasurementFont.Value;

    private TrueTypeFontMetrics GetDomainAnnotationMeasurementFont() =>
        Language ? ChineseAnnotationMeasurementFont.Value : DomainAnnotationMeasurementFont.Value;

    private double GetAnnotationTextWidth(string text)
    {
        var font = GetAnnotationMeasurementFont();
        var maxLineWidth = text
            .Split(["\r\n", "\r", "\n"], StringSplitOptions.None)
            .Select(line => font.GetWidth(line, AnnotationFontSize))
            .DefaultIfEmpty(0)
            .Max();
        return Math.Max(1, maxLineWidth + AnnotationHorizontalPadding);
    }

    private double GetAnnotationWidth(string? text, double currentWidth)
    {
        return currentWidth > 0
            ? currentWidth
            : GetAnnotationTextWidth(text ?? string.Empty);
    }

    private double GetAnnotationWidth(Annotation annotation) =>
        GetAnnotationWidth(annotation.AnnotationText, annotation.Width);

    private double GetAnnotationHeight(int lineCount) =>
        GetAnnotationHeight(lineCount, Language);

    private static double GetAnnotationHeight(int lineCount, bool language)
    {
        var lines = Math.Max(lineCount, 1);
        var additionalLineSpacing = language
            ? (lines - 1) * ChineseAnnotationAdditionalLineSpacing
            : 0;
        return lines * AnnotationLineHeight + AnnotationVerticalPadding + additionalLineSpacing;
    }

    private static double GetLegacyAnnotationHeight(int lineCount, bool language) =>
        language
            ? Math.Max(lineCount, 1) * LegacyChineseAnnotationLineHeight + LegacyChineseAnnotationVerticalPadding
            : GetAnnotationHeight(lineCount, false);

    private int GetAnnotationLineCount(double height)
    {
        var lineHeight = AnnotationLineHeight + (Language ? ChineseAnnotationAdditionalLineSpacing : 0);
        var verticalPadding = AnnotationVerticalPadding - (Language ? ChineseAnnotationAdditionalLineSpacing : 0);
        return Math.Max(1, (int)Math.Round(
            Math.Max(0, height - verticalPadding) / lineHeight));
    }

    private double GetDomainAnnotationWidth(string text)
    {
        var measuredWidth = GetDomainAnnotationMeasurementFont().GetWidth(text, DomainAnnotationFontSize);
        return Math.Max(20, measuredWidth + DomainAnnotationHorizontalPadding);
    }

    private void RefreshAutomaticAnnotationHeights(bool previousLanguage)
    {
        if (_viewer is null || previousLanguage == Language)
            return;

        _synchronizing = true;
        try
        {
            foreach (var row in _annotations)
            {
                var model = _viewer.GetAnnotationById(row.AnnotationUniqueId);
                if (model is null)
                    continue;

                var previousHeight = GetAnnotationHeight(row.Line, previousLanguage);
                var legacyPreviousHeight = GetLegacyAnnotationHeight(row.Line, previousLanguage);
                var height = GetAnnotationHeight(row.Line);
                var hasAutomaticHeight = Math.Abs(model.Height - previousHeight) <= 0.1 ||
                                         Math.Abs(model.Height - legacyPreviousHeight) <= 0.1;
                if (hasAutomaticHeight && Math.Abs(model.Height - height) > 0.1)
                    _viewer.UpdateAnnotationSize(model.Id, model.Width, height);
            }
        }
        finally
        {
            _synchronizing = false;
        }
    }

    private static string? NormalizeDomainCode(string? domain)
    {
        if (string.IsNullOrWhiteSpace(domain))
            return null;

        return domain.Trim().ToUpperInvariant();
    }

    private List<string> GetDistinctDomainCodes(int pageNum)
    {
        return _annotations
            .Where(annotation => annotation.PageNum == pageNum)
            .Select(annotation => NormalizeDomainCode(annotation.AnnotationTextDomain))
            .Where(domain => !string.IsNullOrWhiteSpace(domain))
            .Select(domain => domain!)
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .ToList();
    }

    private static Dictionary<int, List<string>> BuildDistinctDomainCodesByPage(
        IEnumerable<Annotation> annotations)
    {
        var domainsByPage = new Dictionary<int, List<string>>();
        var seenDomainsByPage = new Dictionary<int, HashSet<string>>();
        foreach (var annotation in annotations)
        {
            var domain = NormalizeDomainCode(annotation.AnnotationTextDomain);
            if (domain is null)
                continue;

            if (!domainsByPage.TryGetValue(annotation.PageNum, out var domains))
            {
                domains = [];
                domainsByPage.Add(annotation.PageNum, domains);
                seenDomainsByPage.Add(
                    annotation.PageNum,
                    new HashSet<string>(StringComparer.OrdinalIgnoreCase));
            }

            if (seenDomainsByPage[annotation.PageNum].Add(domain))
                domains.Add(domain);
        }

        return domainsByPage;
    }

    private string GetAnnotationDomainColor(Annotation annotation)
    {
        var domain = NormalizeDomainCode(annotation.AnnotationTextDomain);
        if (annotation.PageNum <= 0 || domain is null)
            return DomainColors[0];

        return GetAnnotationDomainColor(annotation, GetDistinctDomainCodes(annotation.PageNum));
    }

    private static string GetAnnotationDomainColor(
        Annotation annotation,
        IReadOnlyList<string>? domains)
    {
        var domain = NormalizeDomainCode(annotation.AnnotationTextDomain);
        if (annotation.PageNum <= 0 || domain is null || domains is null)
            return DomainColors[0];

        for (var index = 0; index < domains.Count; index++)
        {
            if (string.Equals(domains[index], domain, StringComparison.OrdinalIgnoreCase))
                return DomainColors[index is >= 0 and < 4 ? index : 0];
        }

        return DomainColors[0];
    }

    private void RefreshPageAnnotationColors(int pageNum)
    {
        if (_viewer is null) return;
        var domainsByPage = BuildDistinctDomainCodesByPage(_annotations);
        IReadOnlyList<string> domains = domainsByPage.TryGetValue(pageNum, out var pageDomains)
            ? pageDomains
            : Array.Empty<string>();
        if (pageNum > 0)
        {
            foreach (var annotation in _annotations.Where(a => a.PageNum == pageNum && !string.IsNullOrWhiteSpace(a.AnnotationText)))
            {
                var color = GetAnnotationDomainColor(annotation, domains);
                _viewer.UpdateAnnotationBackgroundColor(annotation.AnnotationUniqueId, color);
            }
        }

        GenerateDomainAnnotations(domainsByPage, pageNum);
    }

    private void RefreshAllAnnotationColors()
    {
        if (_viewer is null) return;
        var domainsByPage = BuildDistinctDomainCodesByPage(_annotations);
        foreach (var annotation in _annotations)
        {
            if (annotation.PageNum <= 0 || string.IsNullOrWhiteSpace(annotation.AnnotationText))
                continue;

            IReadOnlyList<string> domains = domainsByPage.TryGetValue(annotation.PageNum, out var pageDomains)
                ? pageDomains
                : Array.Empty<string>();
            var color = GetAnnotationDomainColor(annotation, domains);
            _viewer.UpdateAnnotationBackgroundColor(annotation.AnnotationUniqueId, color);
        }

        GenerateDomainAnnotations(domainsByPage);
    }

    private void GenerateDomainAnnotations(
        IReadOnlyDictionary<int, List<string>>? domainsByPage = null,
        int? pageNum = null)
    {
        if (_viewer is null) return;
        domainsByPage ??= BuildDistinctDomainCodesByPage(_annotations);
        var firstPage = pageNum is > 0 ? pageNum.Value - 1 : 0;
        var lastPage = pageNum is > 0 ? pageNum.Value - 1 : _viewer.PageCount - 1;

        for (var pageIndex = firstPage; pageIndex <= lastPage && pageIndex < _viewer.PageCount; pageIndex++)
        {
            IReadOnlyList<string> domains = domainsByPage.TryGetValue(pageIndex + 1, out var pageDomains)
                ? pageDomains
                : Array.Empty<string>();
            var displays = domains
                .Select((domain, index) =>
                {
                    var text = _cdiscDatasetCatalog.FormatDomain(domain) ?? domain;
                    return new DomainAnnotationDisplay(
                        Text: text,
                        BackgroundColorHex: DomainColors[index is >= 0 and < 4 ? index : 0],
                        Width: GetDomainAnnotationWidth(text));
                })
                .ToList();

            _viewer.UpdatePageDomainAnnotations(
                pageIndex,
                displays,
                DomainAnnotationHorizontalOffset,
                DomainAnnotationVerticalOffset);
        }
    }

    private async void OnViewerDocumentLoaded(object? sender, EventArgs e)
    {
        _annotationGrid?.ClearHistory();
        if (_hasImportedSpec)
            SyncRowsToPdf(_annotations);
        NormalizeLoadedAnnotationHeights();
        ApplyAnnotationFontFamily();
        await SynchronizeAnnotationDomainsAsync(_annotations);
        RefreshAllAnnotationColors();
    }

    private void NormalizeLoadedAnnotationHeights()
    {
        if (_viewer is null || !Language)
            return;

        _synchronizing = true;
        try
        {
            foreach (var row in _annotations)
            {
                var model = _viewer.GetAnnotationById(row.AnnotationUniqueId);
                if (model is null)
                    continue;

                var previousUnifiedHeight = GetAnnotationHeight(row.Line, false);
                var legacyHeight = GetLegacyAnnotationHeight(row.Line, true);
                var height = GetAnnotationHeight(row.Line, true);
                var hasAutomaticHeight = Math.Abs(model.Height - previousUnifiedHeight) <= 0.1 ||
                                         Math.Abs(model.Height - legacyHeight) <= 0.1;
                if (hasAutomaticHeight && Math.Abs(model.Height - height) > 0.1)
                    _viewer.UpdateAnnotationSize(model.Id, model.Width, height);
            }
        }
        finally
        {
            _synchronizing = false;
        }
    }

    private static void CopyCalloutToModel(Annotation row, FreeTextAnnotationModel model)
    {
        model.CalloutEnabled = row.CalloutEnabled;
        model.CalloutShape = row.CalloutShape;
        model.CalloutWidth = row.CalloutWidth;
        model.CalloutIsLeft = row.CalloutIsLeft;
        model.CalloutX = row.CalloutX;
        model.CalloutY1 = row.CalloutY1;
        model.CalloutY2 = row.CalloutY2;
        model.CalloutArmLength = row.CalloutArmLength;
    }

    private static void CopyCalloutToAnnotation(FreeTextAnnotationModel model, Annotation row)
    {
        row.CalloutEnabled = model.CalloutEnabled;
        row.CalloutShape = model.EffectiveCalloutShape;
        row.CalloutWidth = model.CalloutWidth;
        row.CalloutIsLeft = model.CalloutIsLeft;
        row.CalloutX = model.CalloutX;
        row.CalloutY1 = model.CalloutY1;
        row.CalloutY2 = model.CalloutY2;
        row.CalloutArmLength = model.CalloutArmLength;
    }

    private void SyncRowsToPdf(IEnumerable<Annotation> rows)
    {
        if (_viewer is null) return;
        _synchronizing = true;
        try
        {
            var domainsByPage = BuildDistinctDomainCodesByPage(_annotations);
            var models = rows
                .Where(row => row.PageNum > 0 && !string.IsNullOrWhiteSpace(row.AnnotationText))
                .Select(row =>
                {
                    IReadOnlyList<string> domains = domainsByPage.TryGetValue(row.PageNum, out var pageDomains)
                        ? pageDomains
                        : Array.Empty<string>();
                    return new FreeTextAnnotationModel
                    {
                        Id = row.AnnotationUniqueId,
                        PageIndex = row.PageNum - 1,
                        PdfX = row.X,
                        PdfY = row.Y,
                        Width = GetAnnotationWidth(row),
                        Height = GetAnnotationHeight(row.Line),
                        Text = row.AnnotationText,
                        FontSize = 12,
                        FontFamily = GetAnnotationFontFamily(),
                        BorderStyle = row.AnnotationDotted
                            ? PdfViewerControl.Models.AnnotationBorderStyle.Dashed
                            : PdfViewerControl.Models.AnnotationBorderStyle.Solid,
                        CalloutEnabled = row.CalloutEnabled,
                        CalloutShape = row.CalloutShape,
                        CalloutIsLeft = row.CalloutIsLeft,
                        CalloutX = row.CalloutX,
                        CalloutY1 = row.CalloutY1,
                        CalloutY2 = row.CalloutY2,
                        CalloutArmLength = row.CalloutArmLength,
                        CalloutWidth = row.CalloutWidth,
                        BackgroundColorHex = GetAnnotationDomainColor(row, domains)
                    };
                })
                .ToList();

            _viewer.ReplaceAnnotations(models);
            GenerateDomainAnnotations(domainsByPage);
        }
        finally { _synchronizing = false; }
    }

    private void CreatePdfAnnotation(Annotation row)
    {
        if (_viewer is null || row.PageNum <= 0 || string.IsNullOrWhiteSpace(row.AnnotationText)) return;
        var width = GetAnnotationWidth(row);
        var model = _viewer.CreateAnnotation(
            row.PageNum - 1,
            row.X,
            row.Y,
            width,
            GetAnnotationHeight(row.Line),
            row.AnnotationText,
            12,
            scrollIntoView: false,
            updateStatus: false,
            id: row.AnnotationUniqueId);
        if (model is null) return;
        CopyCalloutToModel(row, model);
        model.BorderStyle = row.AnnotationDotted
            ? PdfViewerControl.Models.AnnotationBorderStyle.Dashed
            : PdfViewerControl.Models.AnnotationBorderStyle.Solid;
        model.BackgroundColorHex = GetAnnotationDomainColor(row);
        _viewer.UpdateAnnotation(model.Id, _ => { });
    }

    private void OnAnnotationMoved(object? sender, AnnotationMovedEventArgs e)
    {
        if (_synchronizing) return;
        var row = _annotations.FirstOrDefault(x => x.AnnotationUniqueId == e.Annotation.Id);
        if (row is null) return;
        var previousX = row.X;
        var previousY = row.Y;
        _synchronizing = true;
        try
        {
            row.PageNum = e.Annotation.PageIndex + 1;
            row.X = (int)Math.Round(e.Annotation.PdfX);
            row.Y = (int)Math.Round(e.Annotation.PdfY);
            row.Line = GetAnnotationLineCount(e.Annotation.Height);
            CopyCalloutToAnnotation(e.Annotation, row);
            RefreshAllAnnotationColors();
        }
        finally { _synchronizing = false; }

        if (e.PreviousPosition is { } previousPosition &&
            (previousPosition.X != e.Annotation.PdfX || previousPosition.Y != e.Annotation.PdfY))
        {
            var pageIndex = e.Annotation.PageIndex;
            var nextPosition = new Avalonia.Point(e.Annotation.PdfX, e.Annotation.PdfY);
            var nextX = row.X;
            var nextY = row.Y;
            _annotationGrid?.RecordUndoableAction(
                () => RestoreAnnotationPosition(row, pageIndex, previousX, previousY, previousPosition),
                () => RestoreAnnotationPosition(row, pageIndex, nextX, nextY, nextPosition),
                row);
        }
    }

    private void RestoreAnnotationPosition(Annotation row, int pageIndex, int x, int y, Avalonia.Point position)
    {
        if (!_observedAnnotations.Contains(row) || _viewer?.GetAnnotationById(row.AnnotationUniqueId) is null)
            return;

        var wasSynchronizing = _synchronizing;
        _synchronizing = true;
        try
        {
            row.X = x;
            row.Y = y;
            _viewer.UpdateAnnotationPosition(row.AnnotationUniqueId, pageIndex, position.X, position.Y);
        }
        finally { _synchronizing = wasSynchronizing; }
    }

    partial void OnSelectedItemChanged(Annotation? oldValue, Annotation? newValue)
    {
        SetSelectedAnnotationRow(newValue);
        if (_viewer is null || newValue is null) return;

        if (!string.Equals(
                _viewer.GetSelectedAnnotationId(),
                newValue.AnnotationUniqueId,
                StringComparison.Ordinal))
        {
            // Selecting a PDF control raises AnnotationSelected synchronously.
            // The outer selection already performs the grid reveal.
            _selectingPdfAnnotation = true;
            try
            {
                if (_viewer.SelectAnnotationById(newValue.AnnotationUniqueId))
                {
                    _viewer.ScrollAnnotationIntoView(newValue.AnnotationUniqueId);
                }
                else
                {
                    _viewer.ClearAnnotationSelection();
                    if (newValue.PageNum > 0)
                        _viewer.ScrollToPageNumber(newValue.PageNum);
                }
            }
            finally { _selectingPdfAnnotation = false; }
        }

        _annotationGrid?.ScrollIntoView(newValue);
    }

    private void OnAnnotationSelected(object? sender, AnnotationSelectedEventArgs e)
    {
        if (_selectingPdfAnnotation)
            return;
        var row = _annotations.FirstOrDefault(annotation =>
            string.Equals(annotation.AnnotationUniqueId, e.AnnotationId, StringComparison.Ordinal));
        if (row is null)
            return;

        SelectedItem = row;
        SetSelectedAnnotationRow(row);
        _annotationGrid?.ScrollIntoView(row);
    }

    private void SetSelectedAnnotationRow(Annotation? selectedRow)
    {
        if (ReferenceEquals(_selectedAnnotationRow, selectedRow))
            return;

        var previous = _selectedAnnotationRow;
        _selectedAnnotationRow = selectedRow;
        if (previous != null)
            previous.IsSelected = false;
        if (selectedRow != null)
            selectedRow.IsSelected = true;
    }

    private void ObserveAnnotation(Annotation annotation)
    {
        if (_observedAnnotations.Add(annotation))
            annotation.PropertyChanged += OnAnnotationPropertyChanged;
    }

    private void UnobserveAnnotation(Annotation annotation)
    {
        if (_observedAnnotations.Remove(annotation))
            annotation.PropertyChanged -= OnAnnotationPropertyChanged;
        _annotationTextSyncVersions.Remove(annotation);
    }

    private void UnobserveAllAnnotations()
    {
        foreach (var annotation in _observedAnnotations.ToList())
            UnobserveAnnotation(annotation);
    }

    private static void EnableDottedBorderForNotSubmitted(Annotation row)
    {
        if (string.Equals(row.AnnotationText?.Trim(), "[NOT SUBMITTED]", StringComparison.OrdinalIgnoreCase))
            row.AnnotationDotted = true;
    }

    private void OnAnnotationPropertyChanged(object? sender, PropertyChangedEventArgs e)
    {
        if (_synchronizing || sender is not Annotation row) return;
        if (string.IsNullOrWhiteSpace(row.AnnotationUniqueId))
            row.AnnotationUniqueId = Guid.NewGuid().ToString();

        if (e.PropertyName == nameof(Annotation.AnnotationText))
        {
            EnableDottedBorderForNotSubmitted(row);
            _ = SynchronizeAnnotationTextAsync(row);
            return;
        }

        if (_viewer is null) return;

        _synchronizing = true;
        try
        {

            if (e.PropertyName == nameof(Annotation.AnnotationTextDomain))
            {
                RefreshPageAnnotationColors(row.PageNum);
                return;
            }

            if (e.PropertyName == nameof(Annotation.PageNum) ||
                e.PropertyName == nameof(Annotation.X) ||
                e.PropertyName == nameof(Annotation.Y))
            {
                if (row.PageNum > 0)
                {
                    _viewer.UpdateAnnotationPosition(row.AnnotationUniqueId, row.PageNum - 1, row.X, row.Y);
                    _viewer.UpdateAnnotation(row.AnnotationUniqueId, model => CopyCalloutToModel(row, model));
                }
                if (e.PropertyName == nameof(Annotation.PageNum))
                    RefreshAllAnnotationColors();
                return;
            }

            if (e.PropertyName == nameof(Annotation.Width) || e.PropertyName == nameof(Annotation.Line))
            {
                _viewer.UpdateAnnotationSize(
                    row.AnnotationUniqueId,
                    GetAnnotationWidth(row),
                    GetAnnotationHeight(row.Line));
                _viewer.UpdateAnnotation(row.AnnotationUniqueId, model => CopyCalloutToModel(row, model));
                return;
            }

            if (e.PropertyName == nameof(Annotation.CalloutEnabled) ||
                e.PropertyName == nameof(Annotation.CalloutShape) ||
                e.PropertyName == nameof(Annotation.CalloutIsLeft) ||
                e.PropertyName == nameof(Annotation.CalloutX) ||
                e.PropertyName == nameof(Annotation.CalloutY1) ||
                e.PropertyName == nameof(Annotation.CalloutY2) ||
                e.PropertyName == nameof(Annotation.CalloutArmLength) ||
                e.PropertyName == nameof(Annotation.CalloutWidth))
            {
                if (e.PropertyName == nameof(Annotation.CalloutEnabled) && row.CalloutEnabled &&
                    row.CalloutX == 0 && row.CalloutY1 == 0 && row.CalloutY2 == 0)
                {
                    row.CalloutX = Math.Max(8, row.X - 40);
                    row.CalloutY1 = row.Y + GetAnnotationHeight(row.Line) / 2 + 36;
                    row.CalloutY2 = Math.Max(0, row.CalloutY1 - 72);
                    if (string.IsNullOrWhiteSpace(row.CalloutShape)) row.CalloutShape = row.CalloutIsLeft ? "LeftBracket" : "RightBracket";
                }
                _viewer.UpdateAnnotation(row.AnnotationUniqueId, model => CopyCalloutToModel(row, model));
                return;
            }

            if (e.PropertyName == nameof(Annotation.AnnotationDotted))
            {
                _viewer.UpdateAnnotation(row.AnnotationUniqueId, model =>
                    model.BorderStyle = row.AnnotationDotted
                        ? PdfViewerControl.Models.AnnotationBorderStyle.Dashed
                        : PdfViewerControl.Models.AnnotationBorderStyle.Solid);
            }
        }
        finally { _synchronizing = false; }
    }

    private async Task SynchronizeAnnotationTextAsync(Annotation row)
    {
        var version = ++_nextAnnotationTextSyncVersion;
        _annotationTextSyncVersions[row] = version;

        var domain = await _variableDomainCatalog.FindDomainAsync(
            row.AnnotationText,
            row.VarName);
        if (!_annotationTextSyncVersions.TryGetValue(row, out var latestVersion) ||
            latestVersion != version)
            return;

        _synchronizing = true;
        try
        {
            row.AnnotationTextDomain = string.IsNullOrWhiteSpace(row.AnnotationText)
                ? string.Empty
                : domain ?? string.Empty;
            if (_viewer is null)
                return;

            if (string.IsNullOrWhiteSpace(row.AnnotationText))
            {
                row.AnnotationDotted = false;
                _viewer.DeleteAnnotation(row.AnnotationUniqueId);
                RefreshPageAnnotationColors(row.PageNum);
                return;
            }

            if (!_viewer.UpdateAnnotation(row.AnnotationUniqueId, model =>
                {
                    model.Text = row.AnnotationText;
                    model.Width = GetAnnotationWidth(row);
                    CopyCalloutToModel(row, model);
                    model.BackgroundColorHex = GetAnnotationDomainColor(row);
                }))
                CreatePdfAnnotation(row);
            RefreshPageAnnotationColors(row.PageNum);
        }
        finally { _synchronizing = false; }
    }

    private async Task SynchronizeAnnotationDomainsAsync(IEnumerable<Annotation> annotations)
    {
        _synchronizing = true;
        try
        {
            await _variableDomainCatalog.SynchronizeAsync(annotations);
        }
        finally { _synchronizing = false; }
    }

    private void OnAnnotationCreated(object? sender, AnnotationCreatedEventArgs e)
    {
        if (_synchronizing) return;
        var model = e.Annotation;
        var annotationText = FreeTextAnnotationControl.IsPlaceholderContent(model.Text) ? string.Empty : model.Text;
        var row = new Annotation
        {
            AnnotationUniqueId = model.Id,
            PageNum = model.PageIndex + 1,
            X = (int)Math.Round(model.PdfX),
            Y = (int)Math.Round(model.PdfY),
            Width = 0,
            Line = GetAnnotationLineCount(model.Height),
            CalloutEnabled = model.CalloutEnabled,
            CalloutShape = model.EffectiveCalloutShape,
            CalloutWidth = model.CalloutWidth,
            CalloutIsLeft = model.CalloutIsLeft,
            CalloutX = model.CalloutX,
            CalloutY1 = model.CalloutY1,
            CalloutY2 = model.CalloutY2,
            CalloutArmLength = model.CalloutArmLength,
            AnnotationText = annotationText,
            Type = "Variable"
        };
        _source.Add(row);
        ObserveAnnotation(row);
        RefreshPageAnnotationColors(row.PageNum);
        SelectedItem = row;
    }

    private void OnAnnotationDeleted(object? sender, AnnotationDeletedEventArgs e)
    {
        if (_synchronizing) return;
        var row = _annotations.FirstOrDefault(x => x.AnnotationUniqueId == e.AnnotationId);
        if (row is null) return;
        var pageNum = row.PageNum;
        UnobserveAnnotation(row);
        _source.Remove(row);
        RefreshPageAnnotationColors(pageNum);
        if (ReferenceEquals(SelectedItem, row)) SelectedItem = null;
    }

    private void OnAnnotationChanged(object? sender, AnnotationChangedEventArgs e)
    {
        if (_synchronizing) return;
        var row = _annotations.FirstOrDefault(x => x.AnnotationUniqueId == e.Annotation.Id);
        if (row is null) return;

        var textChanged = false;
        _synchronizing = true;
        try
        {
            row.PageNum = e.Annotation.PageIndex + 1;
            row.X = (int)Math.Round(e.Annotation.PdfX);
            row.Y = (int)Math.Round(e.Annotation.PdfY);
            row.Line = GetAnnotationLineCount(e.Annotation.Height);
            CopyCalloutToAnnotation(e.Annotation, row);

            var annotationText = FreeTextAnnotationControl.IsPlaceholderContent(e.Annotation.Text)
                ? string.Empty
                : e.Annotation.Text;
            textChanged = !string.Equals(row.AnnotationText, annotationText, StringComparison.Ordinal);
            row.AnnotationText = annotationText;

            if (string.IsNullOrWhiteSpace(row.AnnotationText))
            {
                row.AnnotationTextDomain = string.Empty;
                row.AnnotationDotted = false;
                _viewer?.DeleteAnnotation(e.Annotation.Id);
            }
            else
            {
                row.AnnotationDotted = e.Annotation.BorderStyle == PdfViewerControl.Models.AnnotationBorderStyle.Dashed;
            }
            RefreshPageAnnotationColors(row.PageNum);
        }
        finally { _synchronizing = false; }

        if (textChanged)
        {
            EnableDottedBorderForNotSubmitted(row);
            _ = SynchronizeAnnotationTextAsync(row);
        }
    }
}
