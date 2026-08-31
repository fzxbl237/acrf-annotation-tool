using Avalonia.Controls;
using Avalonia.Platform.Storage;
using iText.IO.Font;
using iText.Kernel.Font;
using System.Collections.ObjectModel;
using System.ComponentModel;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using Dock.Model.Controls;
using Dock.Model.Mvvm.Controls;
using DynamicData;
using MiniExcelLibs;
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

public partial class AcrfDocumentViewModel : Document
{
    private static readonly string[] DomainColors =
    [
        "#FFBFFFFF",
        "#FFFFFF96",
        "#FF96FF96",
        "#FFFFBE9B"
    ];

    private static readonly PdfFont AnnotationMeasurementFont = CreateAnnotationMeasurementFont();
    private static readonly PdfFont DomainAnnotationMeasurementFont = CreateDomainAnnotationMeasurementFont();
    private static readonly PdfFont ChineseAnnotationMeasurementFont = CreateChineseAnnotationMeasurementFont();
    private static readonly PdfFont ChineseDomainAnnotationMeasurementFont = CreateChineseDomainAnnotationMeasurementFont();
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
    private Window? _window;
    private readonly HashSet<Annotation> _observedAnnotations = new();
    private readonly Dictionary<Annotation, long> _annotationTextSyncVersions = new();
    private long _nextAnnotationTextSyncVersion;
    private readonly CdiscDatasetCatalog _cdiscDatasetCatalog;
    private readonly TemplateVariableDomainCatalog _variableDomainCatalog;
    private readonly IMessageService _messageService;
    private bool _synchronizing;

    public MainWindowViewModel? AppOwner { get; init; }
    public DockFactory? PanelFactory => AppOwner?.Factory;
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

    [RelayCommand]
    private async Task OpenPdfAsync()
    {
        if (_window is null) return;
        var files = await _window.StorageProvider.OpenFilePickerAsync(new FilePickerOpenOptions
        {
            Title = "选择 aCRF PDF",
            AllowMultiple = false,
            FileTypeFilter = [new FilePickerFileType("PDF 文件") { Patterns = ["*.pdf"] }]
        });
        var file = files.FirstOrDefault();
        if (file is null) return;
        var path = file.TryGetLocalPath();
        if (string.IsNullOrWhiteSpace(path)) return;
        PdfPath = path;
        if (_viewer is null) return;
        _viewer.LoadDocument(path);
        Status = $"已打开：{Path.GetFileName(path)}";
        _messageService.Success(Status);
    }

    [RelayCommand]
    private async Task ImportAsync()
    {
        if (_window is null) return;
        var files = await _window.StorageProvider.OpenFilePickerAsync(new FilePickerOpenOptions
        {
            Title = "导入注释 Spec",
            AllowMultiple = false,
            FileTypeFilter = [new FilePickerFileType("Excel 文件") { Patterns = ["*.xlsx"] }]
        });
        var path = files.FirstOrDefault()?.TryGetLocalPath();
        if (string.IsNullOrWhiteSpace(path)) return;
        var rows = await Task.Run(() => MiniExcel.Query<Annotation>(path, hasHeader: false).ToList());
        foreach (var row in rows)
            row.AnnotationUniqueId = Guid.NewGuid().ToString();
        UnobserveAllAnnotations();
        _source.Edit(updater =>
        {
            updater.Clear();
            updater.AddRange(rows);
        });
        foreach (var row in rows)
            ObserveAnnotation(row);

        SyncRowsToPdf(rows);
        _messageService.Success($"已导入 {rows.Count} 条注释");
    }

    [RelayCommand]
    private async Task ExportAsync()
    {
        await ExportSpecAsync();
    }

    public async Task<SpecExportOutcome> ExportSpecAsync()
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

            await MiniExcel.SaveAsAsync(path, _annotations);
            Status = $"已导出：{Path.GetFileName(path)}";
            _messageService.Success(Status);
            return SpecExportOutcome.Succeeded;
        }
        catch (Exception exception)
        {
            Status = $"Spec 导出失败：{exception.Message}";
            _messageService.Error(Status);
            return SpecExportOutcome.Failed;
        }
    }

    [RelayCommand]
    private async Task SavePdfAsync()
    {
        if (_viewer is null)
            return;

        var path = await PickPdfSavePathAsync("另存 PDF", "acrf.pdf");
        if (string.IsNullOrWhiteSpace(path))
            return;

        if (!_viewer.SaveAnnotations(path, out var error))
        {
            Status = error ?? "PDF 另存失败";
            _messageService.Error(Status);
            return;
        }

        Status = $"PDF 注释已另存为：{Path.GetFileName(path)}";
        _messageService.Success(Status);
    }

    [RelayCommand]
    private async Task SavePdfFlattenAsync()
    {
        if (_viewer is null)
            return;

        var path = await PickPdfSavePathAsync("另存 PDF 并 Flatten", "acrf_flatten.pdf");
        if (string.IsNullOrWhiteSpace(path))
            return;

        if (!_viewer.SaveAnnotationsFlattened(path, out var error))
        {
            Status = error ?? "PDF Flatten 另存失败";
            _messageService.Error(Status);
            return;
        }

        Status = $"Flatten PDF 已另存为：{Path.GetFileName(path)}";
        _messageService.Success(Status);
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
        _synchronizing = true;
        try { _viewer?.DeleteAnnotation(item.AnnotationUniqueId); }
        finally { _synchronizing = false; }
        var pageNum = item.PageNum;
        UnobserveAnnotation(item);
        _source.Remove(item);
        RefreshPageAnnotationColors(pageNum);
        SelectedItem = null;
        Status = "已删除选中注释";
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

        var settings = new ApplicationSettings(
            horizontalOffset,
            verticalOffset,
            isPdfAnnotationEditingEnabled,
            language);
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

    private static PdfFont CreateAnnotationMeasurementFont()
    {
        var fontPath = Path.Combine(AppContext.BaseDirectory, "Fonts", "ARIAL.TTF");
        if (File.Exists(fontPath))
            return PdfFontFactory.CreateFont(fontPath, PdfEncodings.IDENTITY_H);

        return PdfFontFactory.CreateFont(iText.IO.Font.Constants.StandardFonts.HELVETICA);
    }

    private static PdfFont CreateDomainAnnotationMeasurementFont()
    {
        var fontPath = Path.Combine(AppContext.BaseDirectory, "Fonts", "ARIALBD.TTF");
        if (File.Exists(fontPath))
            return PdfFontFactory.CreateFont(fontPath, PdfEncodings.IDENTITY_H);

        return PdfFontFactory.CreateFont(iText.IO.Font.Constants.StandardFonts.HELVETICA_BOLD);
    }

    private static PdfFont CreateChineseAnnotationMeasurementFont()
    {
        var fontPath = Path.Combine(AppContext.BaseDirectory, "Fonts", "ARIALUNI.TTF");
        if (File.Exists(fontPath))
            return PdfFontFactory.CreateFont(fontPath, PdfEncodings.IDENTITY_H);

        return PdfFontFactory.CreateFont(iText.IO.Font.Constants.StandardFonts.HELVETICA);
    }

    private static PdfFont CreateChineseDomainAnnotationMeasurementFont()
    {
        var fontPath = Path.Combine(AppContext.BaseDirectory, "Fonts", "ARIALUNI.TTF");
        if (File.Exists(fontPath))
            return PdfFontFactory.CreateFont(fontPath, PdfEncodings.IDENTITY_H);

        return PdfFontFactory.CreateFont(iText.IO.Font.Constants.StandardFonts.HELVETICA);
    }

    private PdfFont GetAnnotationMeasurementFont() =>
        Language ? ChineseAnnotationMeasurementFont : AnnotationMeasurementFont;

    private PdfFont GetDomainAnnotationMeasurementFont() =>
        Language ? ChineseDomainAnnotationMeasurementFont : DomainAnnotationMeasurementFont;

    private double GetAnnotationTextWidth(string text)
    {
        var font = GetAnnotationMeasurementFont();
        var maxLineWidth = text
            .Split(["\r\n", "\r", "\n"], StringSplitOptions.None)
            .Select(line => (double)font.GetWidth(line, AnnotationFontSize))
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

    private string GetAnnotationDomainColor(Annotation annotation)
    {
        var domain = NormalizeDomainCode(annotation.AnnotationTextDomain);
        if (annotation.PageNum <= 0 || domain is null)
            return DomainColors[0];

        var domains = GetDistinctDomainCodes(annotation.PageNum);
        var index = domains.FindIndex(item => string.Equals(item, domain, StringComparison.OrdinalIgnoreCase));
        return DomainColors[index is >= 0 and < 4 ? index : 0];
    }

    private void RefreshPageAnnotationColors(int pageNum)
    {
        if (_viewer is null) return;
        if (pageNum > 0)
        {
            foreach (var annotation in _annotations.Where(a => a.PageNum == pageNum && !string.IsNullOrWhiteSpace(a.AnnotationText)))
            {
                var color = GetAnnotationDomainColor(annotation);
                _viewer.UpdateAnnotation(annotation.AnnotationUniqueId, model => model.BackgroundColorHex = color);
            }
        }

        GenerateDomainAnnotations();
    }

    private void RefreshAllAnnotationColors()
    {
        if (_viewer is null) return;
        foreach (var pageNum in _annotations.Select(a => a.PageNum).Where(page => page > 0).Distinct())
        {
            foreach (var annotation in _annotations.Where(a => a.PageNum == pageNum && !string.IsNullOrWhiteSpace(a.AnnotationText)))
            {
                var color = GetAnnotationDomainColor(annotation);
                _viewer.UpdateAnnotation(annotation.AnnotationUniqueId, model => model.BackgroundColorHex = color);
            }
        }

        GenerateDomainAnnotations();
    }

    private void GenerateDomainAnnotations()
    {
        if (_viewer is null) return;

        for (var pageIndex = 0; pageIndex < _viewer.PageCount; pageIndex++)
        {
            var displays = GetDistinctDomainCodes(pageIndex + 1)
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

    private void SyncRowsToPdf(IEnumerable<Annotation> rows)
    {
        if (_viewer is null) return;
        _synchronizing = true;
        try
        {
            var models = rows
                .Where(row => row.PageNum > 0 && !string.IsNullOrWhiteSpace(row.AnnotationText))
                .Select(row => new FreeTextAnnotationModel
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
                    BackgroundColorHex = GetAnnotationDomainColor(row)
                })
                .ToList();

            _viewer.ReplaceAnnotations(models);
            GenerateDomainAnnotations();
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
        model.BorderStyle = row.AnnotationDotted
            ? PdfViewerControl.Models.AnnotationBorderStyle.Dashed
            : PdfViewerControl.Models.AnnotationBorderStyle.Solid;
        model.BackgroundColorHex = GetAnnotationDomainColor(row);
        _viewer.UpdateAnnotation(model.Id, _ => { });
    }

    private void OnAnnotationMoved(object? sender, AnnotationMovedEventArgs e)
    {
        var row = _annotations.FirstOrDefault(x => x.AnnotationUniqueId == e.Annotation.Id);
        if (row is null) return;
        _synchronizing = true;
        try
        {
            row.PageNum = e.Annotation.PageIndex + 1;
            row.X = (int)Math.Round(e.Annotation.PdfX);
            row.Y = (int)Math.Round(e.Annotation.PdfY);
            row.Line = GetAnnotationLineCount(e.Annotation.Height);
            RefreshAllAnnotationColors();
        }
        finally { _synchronizing = false; }
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

        _annotationGrid?.ScrollIntoView(newValue);
    }

    private void OnAnnotationSelected(object? sender, AnnotationSelectedEventArgs e)
    {
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
        foreach (var row in _annotations)
            row.IsSelected = ReferenceEquals(row, selectedRow);
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
                    _viewer.UpdateAnnotationPosition(row.AnnotationUniqueId, row.PageNum - 1, row.X, row.Y);
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
