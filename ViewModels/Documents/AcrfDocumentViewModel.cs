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
using AcrfAnnotationTool.ViewModels;
using PdfViewerControl.Controls;

namespace AcrfAnnotationTool.ViewModels.Documents;

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
    private const float AnnotationFontSize = 12;
    private const double AnnotationHorizontalPadding = 6;
    private const float DomainAnnotationFontSize = 10;
    private const double DomainAnnotationHorizontalPadding = 8;

    private readonly SourceList<Annotation> _source = new();
    private readonly ReadOnlyObservableCollection<Annotation> _annotations;
    private string _status = "请打开 PDF 或导入注释表";
    private string? _pdfPath;
    private PdfViewer? _viewer;
    private Window? _window;
    private readonly HashSet<Annotation> _observedAnnotations = new();
    private readonly Dictionary<Annotation, int> _annotationTextSyncVersions = new();
    private readonly CdiscDatasetCatalog _cdiscDatasetCatalog = CdiscDatasetCatalog.LoadFromApplicationDatabase();
    private readonly TemplateVariableDomainCatalog _variableDomainCatalog;
    private bool _synchronizing;

    public MainWindowViewModel? AppOwner { get; init; }
    public DockFactory? PanelFactory => AppOwner?.Factory;
    public ReadOnlyObservableCollection<Annotation> Annotations => _annotations;
    public string Status { get => _status; private set => SetProperty(ref _status, value); }
    public string? PdfPath { get => _pdfPath; private set => SetProperty(ref _pdfPath, value); }
    public double DomainAnnotationHorizontalOffset { get; private set; }
    public double DomainAnnotationVerticalOffset { get; private set; }

    [ObservableProperty]
    private Annotation? _selectedItem;

    public AcrfDocumentViewModel(TemplateVariableDomainCatalog variableDomainCatalog)
    {
        _variableDomainCatalog = variableDomainCatalog;
        var settings = ApplicationSettingsStore.Load();
        DomainAnnotationHorizontalOffset = settings.DomainAnnotationHorizontalOffset;
        DomainAnnotationVerticalOffset = settings.DomainAnnotationVerticalOffset;
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
                _viewer.DocumentLoaded -= OnViewerDocumentLoaded;
            }
            _viewer = viewer;
            _viewer.AnnotationMoved += OnAnnotationMoved;
            _viewer.AnnotationCreated += OnAnnotationCreated;
            _viewer.AnnotationDeleted += OnAnnotationDeleted;
            _viewer.AnnotationChanged += OnAnnotationChanged;
            _viewer.DocumentLoaded += OnViewerDocumentLoaded;
        }

        _window = window;
        if (!string.IsNullOrWhiteSpace(PdfPath) && !string.Equals(_viewer.DocumentPath, PdfPath, StringComparison.OrdinalIgnoreCase))
            _viewer.LoadDocument(PdfPath);
        else
            GenerateDomainAnnotations();
    }

    [RelayCommand]
    private async Task OpenPdfAsync()
    {
        if (_window is null) return;
        var files = await _window.StorageProvider.OpenFilePickerAsync(new FilePickerOpenOptions
        {
            Title = "选择 aCRF PDF",
            AllowMultiple = false,
            FileTypeFilter = new[] { new FilePickerFileType("PDF 文件") { Patterns = new[] { "*.pdf" } } }
        });
        var file = files.FirstOrDefault();
        if (file is null) return;
        var path = file.TryGetLocalPath();
        if (string.IsNullOrWhiteSpace(path)) return;
        PdfPath = path;
        if (_viewer is null) return;
        _viewer.LoadDocument(path);
        Status = $"已打开：{Path.GetFileName(path)}";
    }

    [RelayCommand]
    private async Task ImportAsync()
    {
        if (_window is null) return;
        var files = await _window.StorageProvider.OpenFilePickerAsync(new FilePickerOpenOptions
        {
            Title = "导入注释 Spec",
            AllowMultiple = false,
            FileTypeFilter = new[] { new FilePickerFileType("Excel 文件") { Patterns = new[] { "*.xlsx" } } }
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
        RefreshAllAnnotationColors();
        Status = $"已导入 {rows.Count} 条注释";
    }

    [RelayCommand]
    private async Task ExportAsync()
    {
        if (_window is null) return;
        var file = await _window.StorageProvider.SaveFilePickerAsync(new FilePickerSaveOptions
        {
            Title = "导出注释 Spec",
            SuggestedFileName = "aCRF-annotations.xlsx",
            FileTypeChoices = new[] { new FilePickerFileType("Excel 文件") { Patterns = new[] { "*.xlsx" } } }
        });
        var path = file?.TryGetLocalPath();
        if (string.IsNullOrWhiteSpace(path)) return;
        await MiniExcel.SaveAsAsync(path, _annotations);
        Status = $"已导出：{Path.GetFileName(path)}";
    }

    [RelayCommand]
    private async Task SavePdfAsync()
    {
        if (_window is null || _viewer is null)
            return;

        var file = await _window.StorageProvider.SaveFilePickerAsync(new FilePickerSaveOptions
        {
            Title = "另存 PDF",
            SuggestedFileName = "acrf.pdf",
            FileTypeChoices = new[]
            {
                new FilePickerFileType("PDF 文件") { Patterns = new[] { "*.pdf" } }
            }
        });
        var path = file?.TryGetLocalPath();
        if (string.IsNullOrWhiteSpace(path))
            return;

        _viewer.SaveAnnotations(path);
        Status = $"PDF 注释已另存为：{Path.GetFileName(path)}";
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
            copy.X += Math.Max(copy.Width, 0) + 10;

        var index = _source.Items.ToList().IndexOf(source);
        _source.Edit(updater => updater.Insert(Math.Max(0, index + 1), copy));
        ObserveAnnotation(copy);
        CreatePdfAnnotation(copy);
        GenerateDomainAnnotations();
        SelectedItem = copy;
        Status = "已复制选中注释";
    }

    public void ApplyDomainAnnotationOffsets(double horizontalOffset, double verticalOffset)
    {
        if (!double.IsFinite(horizontalOffset) || !double.IsFinite(verticalOffset) ||
            horizontalOffset < 0 || verticalOffset < 0)
        {
            return;
        }

        DomainAnnotationHorizontalOffset = horizontalOffset;
        DomainAnnotationVerticalOffset = verticalOffset;
        GenerateDomainAnnotations();

        var settings = new ApplicationSettings(
            horizontalOffset,
            verticalOffset);
        if (ApplicationSettingsStore.TrySave(settings, out var error))
        {
            Status = $"Domain annotation 位置已更新：水平 {horizontalOffset:0.##}，垂直 {verticalOffset:0.##}";
        }
        else
        {
            Status = $"Domain annotation 位置已更新，但配置保存失败：{error}";
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
        var unicodeFontPath = Path.Combine(
            AppContext.BaseDirectory,
            "Fonts",
            "ARIALUNI.TTF");
        if (File.Exists(unicodeFontPath))
            return PdfFontFactory.CreateFont(unicodeFontPath, PdfEncodings.IDENTITY_H);

        return PdfFontFactory.CreateFont(iText.IO.Font.Constants.StandardFonts.HELVETICA);
    }

    private static PdfFont CreateDomainAnnotationMeasurementFont()
    {
        var boldFontPath = Path.Combine(
            AppContext.BaseDirectory,
            "Fonts",
            "ARIALBD.TTF");
        if (File.Exists(boldFontPath))
            return PdfFontFactory.CreateFont(boldFontPath, PdfEncodings.IDENTITY_H);

        return PdfFontFactory.CreateFont(iText.IO.Font.Constants.StandardFonts.HELVETICA_BOLD);
    }

    private static double GetAnnotationTextWidth(string text)
    {
        var maxLineWidth = text
            .Split(["\r\n", "\r", "\n"], StringSplitOptions.None)
            .Select(line => (double)AnnotationMeasurementFont.GetWidth(line, AnnotationFontSize))
            .DefaultIfEmpty(0)
            .Max();
        return Math.Max(1, maxLineWidth + AnnotationHorizontalPadding);
    }

    private static double GetAnnotationWidth(Annotation annotation) =>
        annotation.Width > 0 ? annotation.Width : GetAnnotationTextWidth(annotation.AnnotationText);

    private static double GetDomainAnnotationWidth(string text)
    {
        var measuredWidth = DomainAnnotationMeasurementFont.GetWidth(text, DomainAnnotationFontSize);
        return Math.Max(20, measuredWidth + DomainAnnotationHorizontalPadding);
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
        await SynchronizeAnnotationDomainsAsync(_annotations);
        RefreshAllAnnotationColors();
    }

    private void SyncRowsToPdf(IEnumerable<Annotation> rows)
    {
        if (_viewer is null) return;
        _synchronizing = true;
        try
        {
            _viewer.DeleteAllAnnotations();
            foreach (var row in rows)
                CreatePdfAnnotation(row);
            RefreshAllAnnotationColors();
        }
        finally { _synchronizing = false; }
    }

    private void CreatePdfAnnotation(Annotation row)
    {
        if (_viewer is null || row.PageNum <= 0 || string.IsNullOrWhiteSpace(row.AnnotationText)) return;
        var model = _viewer.CreateAnnotation(
            row.PageNum - 1,
            row.X,
            row.Y,
            GetAnnotationWidth(row),
            Math.Max(row.Line, 1) * 18,
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
            row.Width = (int)Math.Round(e.Annotation.Width);
            row.Line = Math.Max(1, (int)Math.Round(e.Annotation.Height / 18d));
            RefreshAllAnnotationColors();
        }
        finally { _synchronizing = false; }
    }

    partial void OnSelectedItemChanged(Annotation? oldValue, Annotation? newValue)
    {
        if (_viewer is null || newValue is null) return;
        _viewer.SelectAnnotationById(newValue.AnnotationUniqueId);
        _viewer.ScrollAnnotationIntoView(newValue.AnnotationUniqueId);
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
    }

    private void UnobserveAllAnnotations()
    {
        foreach (var annotation in _observedAnnotations.ToList())
            annotation.PropertyChanged -= OnAnnotationPropertyChanged;
        _observedAnnotations.Clear();
    }

    private void OnAnnotationPropertyChanged(object? sender, PropertyChangedEventArgs e)
    {
        if (_synchronizing || sender is not Annotation row) return;
        if (string.IsNullOrWhiteSpace(row.AnnotationUniqueId))
            row.AnnotationUniqueId = Guid.NewGuid().ToString();

        if (e.PropertyName == nameof(Annotation.AnnotationText))
        {
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
                    Math.Max(row.Line, 1) * 18);
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
        var version = _annotationTextSyncVersions.TryGetValue(row, out var currentVersion)
            ? currentVersion + 1
            : 1;
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
        var row = new Annotation
        {
            AnnotationUniqueId = model.Id,
            PageNum = model.PageIndex + 1,
            X = (int)Math.Round(model.PdfX),
            Y = (int)Math.Round(model.PdfY),
            Width = (int)Math.Round(model.Width),
            Line = Math.Max(1, (int)Math.Round(model.Height / 18d)),
            AnnotationText = FreeTextAnnotationControl.IsPlaceholderContent(model.Text) ? string.Empty : model.Text,
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
            row.Width = (int)Math.Round(e.Annotation.Width);
            row.Line = Math.Max(1, (int)Math.Round(e.Annotation.Height / 18d));

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
            _ = SynchronizeAnnotationTextAsync(row);
    }
}
