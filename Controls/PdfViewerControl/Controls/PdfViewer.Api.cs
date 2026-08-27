using Avalonia;
using Avalonia.Controls;
using Avalonia.Media;
using PdfViewerControl.Controls.Annotation;
using PdfViewerControl.Helpers;
using PdfViewerControl.Models;

namespace PdfViewerControl.Controls;

public sealed class AnnotationMovedEventArgs(FreeTextAnnotationModel annotation) : EventArgs
{
    public FreeTextAnnotationModel Annotation { get; } = annotation;
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

    public IReadOnlyList<FreeTextAnnotationModel> GetAnnotations() => _annotations.AsReadOnly();

    public IReadOnlyList<FreeTextAnnotationModel> GetDomainAnnotationModels() =>
        _pageSlots
            .SelectMany(slot => slot.DomainAnnotations)
            .Select(annotation => annotation.Model)
            .OfType<FreeTextAnnotationModel>()
            .ToList();

    public IEnumerable<FreeTextAnnotationModel> GetAnnotationsOnPage(int pageIndex) =>
        _annotations.Where(a => a.PageIndex == pageIndex);

    public FreeTextAnnotationModel? GetAnnotationById(string id) =>
        _annotations.FirstOrDefault(a => a.Id == id);

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
            FontSize = fontSize
        };
        _annotations.Add(model);

        if (pageIndex < _pageSlots.Count)
        {
            var slot = _pageSlots[pageIndex];
            AddAnnotationControl(model, pageHeightPoints, _loader.DisplayScale, slot.AnnotationLayer);
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
        var model = _annotations.FirstOrDefault(a => a.Id == id);
        if (model == null) return false;
        updateAction(model);
        if (_annotationControls.TryGetValue(id, out var control))
        {
            var php = _loader.GetPageHeightInPoints(model.PageIndex);
            control.ApplyLayoutFromPdf(php, _loader.DisplayScale);
            control.ApplyModelToUi();
        }
        return true;
    }

    public bool UpdateAnnotationPosition(string id, int pageIndex, double pdfX, double pdfY)
    {
        var model = _annotations.FirstOrDefault(a => a.Id == id);
        if (model == null || pageIndex < 0 || pageIndex >= _loader.PageCount) return false;
        if (model.PageIndex != pageIndex && pageIndex >= 0 && pageIndex < _loader.PageCount)
        {
            if (_annotationControls.TryGetValue(id, out var oldControl))
            {
                var oldSlot = _pageSlots.ElementAtOrDefault(model.PageIndex);
                if (oldSlot?.AnnotationLayer is PageAnnotationCanvas oldPac) oldPac.UnhookAnnotation(oldControl);
                oldSlot?.AnnotationLayer.Children.Remove(oldControl);
            }
            model.PageIndex = pageIndex;
            if (pageIndex < _pageSlots.Count)
            {
                var newSlot = _pageSlots[pageIndex];
                AddAnnotationControl(model, _loader.GetPageHeightInPoints(pageIndex), _loader.DisplayScale, newSlot.AnnotationLayer);
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
        var model = _annotations.FirstOrDefault(a => a.Id == id);
        if (model == null) return false;
        model.Width = width;
        model.Height = height;
        if (_annotationControls.TryGetValue(id, out var ctrl))
            ctrl.ApplyLayoutFromPdf(_loader.GetPageHeightInPoints(model.PageIndex), _loader.DisplayScale);
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
        _annotations.AddRange(annotations);
        _annotationControls.Clear();
        _selectedAnnotation = null;

        foreach (var slot in _pageSlots)
        {
            var pageHeightPoints = _loader.GetPageHeightInPoints(slot.PageIndex);
            RebuildAnnotationsForPage(
                slot.PageIndex,
                pageHeightPoints,
                _loader.DisplayScale,
                slot.AnnotationLayer);
        }
    }

    public bool SelectAnnotationById(string id)
    {
        if (!_annotationControls.TryGetValue(id, out var control)) return false;
        DoSelectAnnotation(control);
        _currentPageIndex = control.Model?.PageIndex ?? 0;
        UpdatePageInfo();
        ScrollToPage(_currentPageIndex);
        return true;
    }

    public string? GetSelectedAnnotationId() => _selectedAnnotation?.Model?.Id;
    public void ClearAnnotationSelection() => ClearSelection();
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

        if (slot.DomainAnnotations.Count != visibleDisplays.Count)
        {
            slot.DomainAnnotationLayer.Children.Clear();
            slot.DomainAnnotations.Clear();

            for (var i = 0; i < visibleDisplays.Count; i++)
            {
                var display = visibleDisplays[i];
                var model = new FreeTextAnnotationModel
                {
                    Id = $"__domain_{pageIndex}_{i}",
                    PageIndex = pageIndex,
                    PdfX = x,
                    PdfY = pageHeightPoints - top - DomainAnnotationHeight,
                    Width = GetDomainAnnotationWidth(display.Width, x, pageWidthPoints, scale),
                    Height = DomainAnnotationHeight,
                    Text = display.Text,
                    FontSize = 10,
                    FontFamily = "Arial",
                    IsBold = true,
                    VerticalTextAlignment = AnnotationVerticalTextAlignment.Center,
                    FontColorHex = "#FF000000",
                    BackgroundColorHex = display.BackgroundColorHex,
                    BorderStyle = AnnotationBorderStyle.Solid
                };
                var annotation = new FreeTextAnnotationControl
                {
                    Model = model,
                    IsVisible = true,
                    IsHitTestVisible = false,
                    Focusable = false,
                    ZIndex = 3
                };
                annotation.ApplyLayoutFromPdf(pageHeightPoints, scale);
                slot.DomainAnnotationLayer.Children.Add(annotation);
                slot.DomainAnnotations.Add(annotation);
                x += model.Width + 4 / scale;
            }

            return;
        }

        for (var i = 0; i < visibleDisplays.Count; i++)
        {
            var display = visibleDisplays[i];
            var annotation = slot.DomainAnnotations[i];
            if (annotation.Model is not { } model)
                continue;

            model.Text = display.Text;
            model.FontFamily = "Arial";
            model.BackgroundColorHex = display.BackgroundColorHex;
            model.IsBold = true;
            model.VerticalTextAlignment = AnnotationVerticalTextAlignment.Center;
            model.Width = GetDomainAnnotationWidth(display.Width, x, pageWidthPoints, scale);
            model.Height = DomainAnnotationHeight;
            model.PdfX = x;
            model.PdfY = pageHeightPoints - top - model.Height;
            annotation.IsVisible = true;
            annotation.IsHitTestVisible = false;
            annotation.ApplyLayoutFromPdf(pageHeightPoints, scale);
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
        if (!_annotationControls.TryGetValue(id, out var control) || control.Model == null) return false;
        var model = control.Model;
        var pageIndex = model.PageIndex;
        if (pageIndex < 0 || pageIndex >= _pageSlots.Count) return false;
        double y = 16;
        for (int i = 0; i < pageIndex && i < _pageSlots.Count; i++) y += GetPageHostHeight(i);
        var annotationTop = Canvas.GetTop(control);
        if (double.IsNaN(annotationTop)) annotationTop = 0;
        var annotationHeight = AnnotationAdornerService.GetAnnotationSize(control).Height;
        var center = y + annotationTop + annotationHeight / 2;
        var vpH = MainScrollViewer.Bounds.Height;
        var off = Math.Max(0, center - vpH / 2);
        MainScrollViewer.Offset = new Vector(MainScrollViewer.Offset.X, Math.Min(off, MainScrollViewer.Extent.Height));
        _currentPageIndex = pageIndex;
        UpdatePageInfo();
        return true;
    }

    public bool UpdateAnnotationBackgroundColor(string id, byte r, byte g, byte b)
    {
        var model = _annotations.FirstOrDefault(a => a.Id == id);
        if (model == null) return false;
        model.BackgroundColorHex = $"#{r:X2}{g:X2}{b:X2}";
        if (_annotationControls.TryGetValue(id, out var c)) c.UpdateChrome();
        return true;
    }

    public bool UpdateAnnotationBackgroundColor(string id, Color color) =>
        UpdateAnnotationBackgroundColor(id, color.R, color.G, color.B);

    public bool UpdateAnnotationFontColor(string id, byte r, byte g, byte b)
    {
        var model = _annotations.FirstOrDefault(a => a.Id == id);
        if (model == null) return false;
        model.FontColorHex = $"#{r:X2}{g:X2}{b:X2}";
        if (_annotationControls.TryGetValue(id, out var c)) c.ApplyModelToUi();
        return true;
    }

    public bool UpdateAnnotationFontColor(string id, Color color) =>
        UpdateAnnotationFontColor(id, color.R, color.G, color.B);

    private bool RemoveAnnotationById(string id)
    {
        var removed = _annotations.RemoveAll(a => a.Id == id) > 0;
        if (!removed) return false;
        if (_annotationControls.Remove(id, out var control))
        {
            var layer = _pageSlots.Select(s => s.AnnotationLayer).FirstOrDefault(c => c.Children.Contains(control));
            if (layer is PageAnnotationCanvas pac) pac.UnhookAnnotation(control);
            layer?.Children.Remove(control);
        }
        if (_selectedAnnotation?.Model?.Id == id) _selectedAnnotation = null;
        AnnotationDeleted?.Invoke(this, new AnnotationDeletedEventArgs(id));
        return true;
    }
}