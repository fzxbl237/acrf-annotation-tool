using Avalonia;
using Avalonia.Controls;
using Avalonia.Media;
using PdfViewerControl.Controls.Annotation;
using PdfViewerControl.Helpers;
using PdfViewerControl.Models;

namespace PdfViewerControl.Controls;

public sealed class AnnotationMovedEventArgs(FreeTextAnnotationModel annotation, Point? previousPosition = null) : EventArgs
{
    public FreeTextAnnotationModel Annotation { get; } = annotation;
    public Point? PreviousPosition { get; } = previousPosition;
}

public sealed class AnnotationCreatedEventArgs(FreeTextAnnotationModel annotation) : EventArgs
{
    public FreeTextAnnotationModel Annotation { get; } = annotation;
}

public sealed class AnnotationDeletedEventArgs(string annotationId) : EventArgs
{
    public string AnnotationId { get; } = annotationId;
}

public sealed class AnnotationChangedEventArgs(FreeTextAnnotationModel annotation) : EventArgs
{
    public FreeTextAnnotationModel Annotation { get; } = annotation;
}

public sealed class AnnotationSelectedEventArgs(string annotationId) : EventArgs
{
    public string AnnotationId { get; } = annotationId;
}

public sealed record DomainAnnotationDisplay(
    string Text,
    string BackgroundColorHex,
    double Width);

public partial class PdfViewer
{
    private const double DomainAnnotationHeight = 20;

    private bool UsesChineseAnnotationFont() =>
        AnnotationFontFamily.Equals("Arial Unicode MS", StringComparison.OrdinalIgnoreCase);

    private double GetDomainAnnotationHeight() => DomainAnnotationHeight;

    public IReadOnlyList<FreeTextAnnotationModel> GetAnnotations() => _annotations.AsReadOnly();

    public string AnnotationFontFamily { get; set; } = "Arial";

    public IReadOnlyList<FreeTextAnnotationModel> GetDomainAnnotationModels() =>
        _pageSlots
            .SelectMany(slot => slot.DomainAnnotationModels)
            .ToList();

    public IEnumerable<FreeTextAnnotationModel> GetAnnotationsOnPage(int pageIndex) =>
        _annotations.Where(a => a.PageIndex == pageIndex);

    public FreeTextAnnotationModel? GetAnnotationById(string id) =>
        _annotationsById.TryGetValue(id, out var model) ? model : null;

    public FreeTextAnnotationModel? CreateAnnotation(
        int pageIndex, double pdfX, double pdfY,
        double width = 200, double height = 36,
        string? text = null, double fontSize = 12,
        bool scrollIntoView = true, bool updateStatus = true,
        string? id = null, bool notifyCreated = false)
    {
        if (_loader.PageCount == 0 || pageIndex < 0 || pageIndex >= _loader.PageCount)
            return null;

        var pageHeightPoints = _loader.GetPageHeightInPoints(pageIndex);
        var pageWidthPoints = _loader.GetPageWidthInPoints(pageIndex);
        PdfCoordinateConverter.ClampPdfRect(pageWidthPoints, pageHeightPoints, ref pdfX, ref pdfY, width, height);

        var model = new FreeTextAnnotationModel
        {
            Id = string.IsNullOrWhiteSpace(id) ? Guid.NewGuid().ToString("N") : id,
            PageIndex = pageIndex, PdfX = pdfX, PdfY = pdfY,
            Width = width, Height = height,
            Text = text ?? FreeTextAnnotationControl.DefaultPlaceholderText,
            FontSize = fontSize,
            FontFamily = AnnotationFontFamily,
            IsBold = false
        };
        _annotations.Add(model);
        _annotationsById[model.Id] = model;

        if (pageIndex < _pageSlots.Count)
        {
            var slot = _pageSlots[pageIndex];
            if (slot.AnnotationLayer is { } annotationLayer)
                AddAnnotationControl(model, pageHeightPoints, _loader.DisplayScale, annotationLayer);
            if (scrollIntoView) ScrollAnnotationIntoView(model.Id);
            if (updateStatus) SetStatus($"Annotation created, page {pageIndex + 1}");
        }
        if (notifyCreated) AnnotationCreated?.Invoke(this, new AnnotationCreatedEventArgs(model));
        return model;
    }

    public FreeTextAnnotationModel? CreateAnnotation(
        int pageIndex, double pdfX, double pdfY,
        double width, double height, string? text, double fontSize,
        Color backgroundColor, Color fontColor)
    {
        var model = CreateAnnotation(pageIndex, pdfX, pdfY, width, height, text, fontSize);
        if (model == null) return null;
        model.BackgroundColorHex = $"#{backgroundColor.R:X2}{backgroundColor.G:X2}{backgroundColor.B:X2}";
        model.FontColorHex = $"#{fontColor.R:X2}{fontColor.G:X2}{fontColor.B:X2}";
        if (_annotationControls.TryGetValue(model.Id, out var control))
        {
            control.UpdateChrome();
            control.ApplyModelToUi();
        }
        return model;
    }

    public bool UpdateAnnotation(string id, Action<FreeTextAnnotationModel> updateAction)
    {
        if (!_annotationsById.TryGetValue(id, out var model)) return false;
        updateAction(model);
        if (_annotationControls.TryGetValue(id, out var control))
        {
            // ApplyLayoutFromPdf already rebuilds the text/chrome; calling ApplyModelToUi again here
            // only repeated the colour/font parsing and re-assigned Editor.Text.
            var php = _loader.GetPageHeightInPoints(model.PageIndex);
            control.ApplyLayoutFromPdf(php, _loader.DisplayScale);
        }
        return true;
    }

    public bool UpdateAnnotationPosition(string id, int pageIndex, double pdfX, double pdfY)
    {
        if (!_annotationsById.TryGetValue(id, out var model) || pageIndex < 0 || pageIndex >= _loader.PageCount) return false;
        if (model.PageIndex != pageIndex && pageIndex >= 0 && pageIndex < _loader.PageCount)
        {
            var wasSelected = false;
            if (_annotationControls.Remove(id, out var oldControl))
            {
                wasSelected = oldControl.IsSelected || ReferenceEquals(_selectedAnnotation, oldControl);
                var oldSlot = _pageSlots.ElementAtOrDefault(model.PageIndex);
                if (oldSlot?.AnnotationLayer is { } oldLayer)
                {
                    oldLayer.UnhookAnnotation(oldControl);
                    oldLayer.Children.Remove(oldControl);
                }
            }
            model.PageIndex = pageIndex;
            if (wasSelected)
                MountPageContent(_pageSlots[pageIndex]);
            if (pageIndex < _pageSlots.Count && _pageSlots[pageIndex].AnnotationLayer is { } newLayer)
            {
                AddAnnotationControl(model, _loader.GetPageHeightInPoints(pageIndex), _loader.DisplayScale, newLayer);
                if (wasSelected && _annotationControls.TryGetValue(id, out var newControl))
                    DoSelectAnnotation(newControl);
            }
        }
        var php = _loader.GetPageHeightInPoints(pageIndex);
        var pw = _loader.GetPageWidthInPoints(pageIndex);
        PdfCoordinateConverter.ClampPdfRect(pw, php, ref pdfX, ref pdfY, model.Width, model.Height);
        model.PdfX = pdfX;
        model.PdfY = pdfY;
        if (_annotationControls.TryGetValue(id, out var ctrl))
            ctrl.ApplyLayoutFromPdf(_loader.GetPageHeightInPoints(pageIndex), _loader.DisplayScale);
        return true;
    }

    public bool UpdateAnnotationSize(string id, double width, double height)
    {
        if (!_annotationsById.TryGetValue(id, out var model)) return false;
        model.Width = width;
        model.Height = height;
        if (_annotationControls.TryGetValue(id, out var ctrl))
            ctrl.ApplyLayoutFromPdf(_loader.GetPageHeightInPoints(model.PageIndex), _loader.DisplayScale);
        return true;
    }

    public bool RestoreAnnotation(FreeTextAnnotationModel snapshot, int index)
    {
        if (snapshot.PageIndex < 0 || snapshot.PageIndex >= _loader.PageCount ||
            GetAnnotationById(snapshot.Id) != null)
            return false;

        var model = snapshot.Clone();
        _annotations.Insert(Math.Clamp(index, 0, _annotations.Count), model);
        _annotationsById[model.Id] = model;
        if (_pageSlots[model.PageIndex].AnnotationLayer is { } layer)
            RebuildAnnotationsForPage(model.PageIndex, _loader.GetPageHeightInPoints(model.PageIndex), _loader.DisplayScale, layer);
        return true;
    }

    public bool DeleteAnnotation(string id) => RemoveAnnotationById(id);

    public int DeleteAnnotationsOnPage(int pageIndex)
    {
        var toDelete = _annotations.Where(a => a.PageIndex == pageIndex).ToList();
        foreach (var m in toDelete) RemoveAnnotationById(m.Id);
        return toDelete.Count;
    }

    public void DeleteAllAnnotations()
    {
        var ids = _annotations.Select(a => a.Id).ToList();
        foreach (var id in ids) RemoveAnnotationById(id);
    }

    public void ReplaceAnnotations(IEnumerable<FreeTextAnnotationModel> annotations)
    {
        _annotations.Clear();
        foreach (var annotation in annotations)
            _annotations.Add(annotation);
        RebuildAnnotationIndex();
        _annotationControls.Clear();
        _selectedAnnotation = null;

        foreach (var slot in _pageSlots)
        {
            if (slot.AnnotationLayer is not { } annotationLayer)
                continue;
            var pageHeightPoints = _loader.GetPageHeightInPoints(slot.PageIndex);
            RebuildAnnotationsForPage(
                slot.PageIndex,
                pageHeightPoints,
                _loader.DisplayScale,
                annotationLayer);
        }
    }

    public bool SelectAnnotationById(string id)
    {
        var model = GetAnnotationById(id);
        if (model is null || model.PageIndex < 0 || model.PageIndex >= _pageSlots.Count)
            return false;
        var slot = _pageSlots[model.PageIndex];
        MountPageContent(slot);
        if (!_annotationControls.TryGetValue(id, out var control))
            return false;
        DoSelectAnnotation(control);
        _currentPageIndex = model.PageIndex;
        UpdatePageInfo();
        ScrollToPage(_currentPageIndex);
        RequestVirtualizationImmediate();
        return true;
    }

    public string? GetSelectedAnnotationId() => _selectedAnnotation?.Model?.Id;
    public void ClearAnnotationSelection() => ClearSelection();

    public bool ScrollToPageNumber(int pageNumber)
    {
        var pageIndex = pageNumber - 1;
        if (pageIndex < 0 || pageIndex >= _pageSlots.Count)
            return false;

        _currentPageIndex = pageIndex;
        UpdatePageInfo();
        ScrollToPage(pageIndex);
        return true;
    }
    public int GetAnnotationCount() => _annotations.Count;
    public int GetAnnotationCountOnPage(int pageIndex) => _annotations.Count(a => a.PageIndex == pageIndex);

    public void UpdatePageDomainAnnotations(
        int pageIndex,
        IReadOnlyList<DomainAnnotationDisplay> displays,
        double horizontalOffset,
        double verticalOffset)
    {
        if (pageIndex < 0 || pageIndex >= _pageSlots.Count)
            return;

        var visibleDisplays = displays
            .Where(display => !string.IsNullOrWhiteSpace(display.Text))
            .ToList();
        var slot = _pageSlots[pageIndex];
        var pageHeightPoints = _loader.GetPageHeightInPoints(pageIndex);
        var pageWidthPoints = _loader.GetPageWidthInPoints(pageIndex);
        var scale = _loader.DisplayScale;
        slot.DomainAnnotationHorizontalOffset = horizontalOffset;
        slot.DomainAnnotationVerticalOffset = verticalOffset;
        var x = horizontalOffset / scale;
        var top = verticalOffset / scale;

        if (slot.DomainAnnotationModels.Count != visibleDisplays.Count)
        {
            slot.DomainAnnotationModels.Clear();
            for (var i = 0; i < visibleDisplays.Count; i++)
            {
                var display = visibleDisplays[i];
                var model = new FreeTextAnnotationModel
                {
                    Id = $"__domain_{pageIndex}_{i}",
                    PageIndex = pageIndex,
                    PdfX = x,
                    PdfY = pageHeightPoints - top - GetDomainAnnotationHeight(),
                    Width = GetDomainAnnotationWidth(display.Width, x, pageWidthPoints, scale),
                    Height = GetDomainAnnotationHeight(),
                    Text = display.Text,
                    FontSize = 10,
                    FontFamily = AnnotationFontFamily,
                    IsBold = !UsesChineseAnnotationFont(),
                    VerticalTextAlignment = AnnotationVerticalTextAlignment.Center,
                    FontColorHex = "#FF000000",
                    BackgroundColorHex = display.BackgroundColorHex,
                    BorderStyle = AnnotationBorderStyle.Solid
                };
                slot.DomainAnnotationModels.Add(model);
                x += model.Width + 4 / scale;
            }
            RebuildDomainAnnotationControls(slot);
            return;
        }

        for (var i = 0; i < visibleDisplays.Count; i++)
        {
            var display = visibleDisplays[i];
            var model = slot.DomainAnnotationModels[i];
            model.Text = display.Text;
            model.FontFamily = AnnotationFontFamily;
            model.BackgroundColorHex = display.BackgroundColorHex;
            model.IsBold = !UsesChineseAnnotationFont();
            model.VerticalTextAlignment = AnnotationVerticalTextAlignment.Center;
            model.Width = GetDomainAnnotationWidth(display.Width, x, pageWidthPoints, scale);
            model.Height = GetDomainAnnotationHeight();
            model.PdfX = x;
            model.PdfY = pageHeightPoints - top - model.Height;
            if (i < slot.DomainAnnotations.Count)
            {
                var annotation = slot.DomainAnnotations[i];
                annotation.IsVisible = true;
                annotation.IsHitTestVisible = false;
                annotation.ApplyLayoutFromPdf(pageHeightPoints, scale);
            }
            x += model.Width + 4 / scale;
        }
    }

    private static double GetDomainAnnotationWidth(double measuredWidth, double x, double pageWidthPoints, double scale)
    {
        var remainingWidth = Math.Max(20, pageWidthPoints - x - 8 / scale);
        return Math.Clamp(measuredWidth, 20, remainingWidth);
    }

    public bool ScrollAnnotationIntoView(string id)
    {
        var model = GetAnnotationById(id);
        if (model is null) return false;
        var pageIndex = model.PageIndex;
        if (pageIndex < 0 || pageIndex >= _pageSlots.Count) return false;
        MountPageContent(_pageSlots[pageIndex]);
        var y = GetPageHostTop(pageIndex);
        var annotationTop = _annotationControls.TryGetValue(id, out var control)
            ? Canvas.GetTop(control)
            : PdfCoordinateConverter.PdfToAvalonia(model.PdfX, model.PdfY, model.Height, _loader.GetPageHeightInPoints(pageIndex), _loader.DisplayScale).Y;
        if (double.IsNaN(annotationTop)) annotationTop = 0;
        var annotationHeight = control is not null
            ? AnnotationAdornerService.GetAnnotationSize(control).Height
            : PdfCoordinateConverter.PointsToPixels(model.Height, _loader.DisplayScale);
        var center = y + annotationTop + annotationHeight / 2;
        var vpH = MainScrollViewer.Bounds.Height;
        var off = Math.Max(0, center - vpH / 2);
        AnimateViewerScrollTo(new Vector(MainScrollViewer.Offset.X, off));
        _currentPageIndex = pageIndex;
        UpdatePageInfo();
        RequestVirtualizationImmediate();
        return true;
    }

    public bool UpdateAnnotationBackgroundColor(string id, string colorHex)
    {
        if (!_annotationsById.TryGetValue(id, out var model)) return false;
        if (string.Equals(model.BackgroundColorHex, colorHex, StringComparison.OrdinalIgnoreCase))
            return true;
        model.BackgroundColorHex = colorHex;
        if (_annotationControls.TryGetValue(id, out var control)) control.UpdateChrome();
        return true;
    }

    public bool UpdateAnnotationBackgroundColor(string id, byte r, byte g, byte b)
    {
        if (!_annotationsById.TryGetValue(id, out var model)) return false;
        model.BackgroundColorHex = $"#{r:X2}{g:X2}{b:X2}";
        if (_annotationControls.TryGetValue(id, out var c)) c.UpdateChrome();
        return true;
    }

    public bool UpdateAnnotationBackgroundColor(string id, Color color) =>
        UpdateAnnotationBackgroundColor(id, color.R, color.G, color.B);

    public bool UpdateAnnotationFontColor(string id, byte r, byte g, byte b)
    {
        if (!_annotationsById.TryGetValue(id, out var model)) return false;
        model.FontColorHex = $"#{r:X2}{g:X2}{b:X2}";
        if (_annotationControls.TryGetValue(id, out var c)) c.ApplyModelToUi();
        return true;
    }

    public bool UpdateAnnotationFontColor(string id, Color color) =>
        UpdateAnnotationFontColor(id, color.R, color.G, color.B);

    private bool RemoveAnnotationById(string id)
    {
        if (!_annotationsById.Remove(id, out var model)) return false;
        _annotations.Remove(model);
        if (_annotationControls.Remove(id, out var control))
        {
            var layer = _pageSlots.Select(s => s.AnnotationLayer).OfType<PageAnnotationCanvas>().FirstOrDefault(c => c.Children.Contains(control));
            layer?.UnhookAnnotation(control);
            layer?.Children.Remove(control);
        }
        if (_selectedAnnotation?.Model?.Id == id) _selectedAnnotation = null;
        AnnotationDeleted?.Invoke(this, new AnnotationDeletedEventArgs(id));
        return true;
    }
}