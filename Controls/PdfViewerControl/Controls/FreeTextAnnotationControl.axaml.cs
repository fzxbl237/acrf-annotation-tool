using Avalonia;
using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Media;
using Avalonia.Threading;
using PdfViewerControl.Controls.Annotation;
using PdfViewerControl.Helpers;
using PdfViewerControl.Models;

namespace PdfViewerControl.Controls;

public partial class FreeTextAnnotationControl : UserControl
{
    public const string DefaultPlaceholderText = "Insert text here";
    public const string DefaultBackgroundHex = "#FF87CEEB";
    public const double DefaultBorderWidthPoints = 1;
    public const double DefaultDashPoints = 3;
    public static readonly StyledProperty<FreeTextAnnotationModel?> ModelProperty = AvaloniaProperty.Register<FreeTextAnnotationControl, FreeTextAnnotationModel?>(nameof(Model));
    public static readonly StyledProperty<bool> IsSelectedProperty = AvaloniaProperty.Register<FreeTextAnnotationControl, bool>(nameof(IsSelected));
    public static readonly StyledProperty<bool> IsAnnotationEditingEnabledProperty = AvaloniaProperty.Register<FreeTextAnnotationControl, bool>(nameof(IsAnnotationEditingEnabled), false);
    public event EventHandler? CommitEdit;
    public event EventHandler? DeleteRequested;
    private double _displayScale = 96.0 / 72.0;
    private bool _endingEdit;
    public FreeTextAnnotationControl()
    {
        InitializeComponent(); ClipToBounds = false;
        Editor.IsReadOnly = !IsAnnotationEditingEnabled;
        if (ContextMenu is { } contextMenu) contextMenu.IsEnabled = IsAnnotationEditingEnabled;
        Editor.TextChanged += (_, _) => UpdatePlaceholder();
        Editor.GotFocus += (_, _) => { DragOverlay.IsVisible = false; Placeholder.IsVisible = false; if (IsPlaceholderContent(Editor.Text)) Editor.Text = string.Empty; };
        Editor.LostFocus += (_, _) => EndEdit();
        Editor.KeyDown += OnEditorKeyDown;
        DragOverlay.PointerPressed += (_, e) => { if (Model == null) return; IsSelected = true; if (e.ClickCount >= 2) { BeginEdit(); e.Handled = true; } };
        PointerPressed += (_, e) => { if (Model == null || e.Handled) return; IsSelected = true; if (e.ClickCount >= 2) { BeginEdit(); e.Handled = true; } };
        Loaded += (_, _) => ApplyModelToUi();
        DetachedFromVisualTree += (_, _) => AnnotationAdornerService.Cleanup(this);
        PropertyChanged += (_, e) =>
        {
            if (e.Property == ModelProperty) ApplyModelToUi();
            else if (e.Property == IsSelectedProperty)
            {
                if (!IsSelected && Editor.IsFocused) EndEdit();
                UpdateChrome();
                AnnotationAdornerService.SetSelected(this, IsSelected);
            }
            else if (e.Property == IsAnnotationEditingEnabledProperty)
            {
                if (!IsAnnotationEditingEnabled && Editor.IsFocused) EndEdit();
                Editor.IsReadOnly = !IsAnnotationEditingEnabled;
                if (ContextMenu is { } contextMenu) contextMenu.IsEnabled = IsAnnotationEditingEnabled;
                AnnotationAdornerService.UpdateAdornerState(this);
            }
        };
    }
    public bool IsInEditMode => Editor.IsFocused;
    public FreeTextAnnotationModel? Model { get => GetValue(ModelProperty); set => SetValue(ModelProperty, value); }
    public bool IsSelected { get => GetValue(IsSelectedProperty); set => SetValue(IsSelectedProperty, value); }
    public bool IsAnnotationEditingEnabled { get => GetValue(IsAnnotationEditingEnabledProperty); set => SetValue(IsAnnotationEditingEnabledProperty, value); }
    public static bool IsPlaceholderContent(string? text) => string.IsNullOrWhiteSpace(text) || string.Equals(text?.Trim(), DefaultPlaceholderText, StringComparison.OrdinalIgnoreCase);
    public void NotifyResizeCommitted() => CommitEdit?.Invoke(this, EventArgs.Empty);
    public void ApplyLayoutFromPdf(double pageHeightPoints, double displayScale)
    {
        if (Model == null) return;
        _displayScale = displayScale > 0 ? displayScale : 96.0 / 72.0;
        var w = PdfCoordinateConverter.PointsToPixels(Model.Width, displayScale);
        var h = PdfCoordinateConverter.PointsToPixels(Model.Height, displayScale);
        var wpfPos = PdfCoordinateConverter.PdfToAvalonia(Model.PdfX, Model.PdfY, Model.Height, pageHeightPoints, displayScale);
        var left = wpfPos.X; var top = wpfPos.Y;
        Canvas.SetLeft(this, left); Canvas.SetTop(this, top);
        MinWidth = Math.Max(2, Math.Min(w, 12 * displayScale)); MinHeight = Math.Max(2, Math.Min(h, 8 * displayScale));
        Width = w; Height = h;
        var isArialUnicodeMs = Model.FontFamily.Equals("Arial Unicode MS", StringComparison.OrdinalIgnoreCase);
        var horizontalPadding = Math.Max(1, 3 * displayScale);
        var verticalPadding = Math.Max(0.5, (isArialUnicodeMs ? 0.5 : 2) * displayScale);
        var baselineCompensation = isArialUnicodeMs ? 0.5 * displayScale : 0;
        Chrome.Padding = new Thickness(
            horizontalPadding,
            Math.Max(0, verticalPadding - baselineCompensation),
            horizontalPadding,
            verticalPadding + baselineCompensation);
        Editor.FontSize = Math.Max(1, Model.FontSize * displayScale); Placeholder.FontSize = Editor.FontSize;
        ApplyTextAlignmentFromModel(); ApplyModelToUi(); UpdateChrome();
    }
    public void BeginEdit()
    {
        if (!IsAnnotationEditingEnabled) return;
        IsSelected = true; UpdateChrome();
        Dispatcher.Post(() => { if (!IsAnnotationEditingEnabled) { UpdateDragOverlay(); return; } Placeholder.IsVisible = false; if (IsPlaceholderContent(Editor.Text) || string.IsNullOrEmpty(Editor.Text)) Editor.Text = string.Empty; DragOverlay.IsVisible = false; Editor.Focus(); Editor.SelectAll(); UpdatePlaceholder(); });
    }
    public void EndEdit()
    {
        if (_endingEdit) return;
        _endingEdit = true;
        try
        {
            if (Editor.IsFocused)
            {
                var focusManager = TopLevel.GetTopLevel(this)?.FocusManager;
                focusManager?.Focus(null);
                if (Editor.IsFocused) Editor.Focusable = false;
            }

            SyncToModel();
            if (string.IsNullOrWhiteSpace(Editor.Text)) Editor.Text = string.Empty;
            Editor.Focusable = true;
            UpdateDragOverlay();
            UpdateChrome();
            UpdatePlaceholder();
            CommitEdit?.Invoke(this, EventArgs.Empty);
        }
        finally
        {
            _endingEdit = false;
        }
    }
    private void OnEditorKeyDown(object? sender, KeyEventArgs e)
    {
        if (e.Key == Key.Escape)
        {
            EndEdit();
            e.Handled = true;
            return;
        }
        if (e.Key == Key.Delete && e.KeyModifiers.HasFlag(KeyModifiers.Control))
        {
            if (IsAnnotationEditingEnabled)
                DeleteRequested?.Invoke(this, EventArgs.Empty);
            e.Handled = true;
        }
    }
    internal void ApplyModelToUi()
    {
        if (Model == null) return;
        try { Editor.Foreground = new SolidColorBrush(Color.Parse(Model.FontColorHex)); } catch { Editor.Foreground = Brushes.Black; }
        var isDomainAnnotation = Model.Id.StartsWith("__domain_", StringComparison.Ordinal);
        var fn = string.IsNullOrWhiteSpace(Model.FontFamily) ? "Arial" : Model.FontFamily;
        var isArialUnicodeMs = fn.Equals("Arial Unicode MS", StringComparison.OrdinalIgnoreCase);
        var effectiveIsBold = !isArialUnicodeMs && (Model.IsBold || isDomainAnnotation);
        try
        {
            if (isArialUnicodeMs)
            {
                Editor.FontFamily = new FontFamily(new Uri("fonts:AcrfProjectFonts", UriKind.Absolute), "Arial Unicode MS");
            }
            else
            {
                Editor.FontFamily = new FontFamily(fn + ",SimSun,Arial");
            }
        }
        catch
        {
            Editor.FontFamily = new FontFamily("Arial,SimSun");
        }
        Editor.FontWeight = effectiveIsBold ? FontWeight.Bold : FontWeight.Normal;
        BoldMenuItem.IsEnabled = IsAnnotationEditingEnabled && isDomainAnnotation && !isArialUnicodeMs;
        BoldMenuItem.IsChecked = effectiveIsBold;
        Editor.Text = IsPlaceholderContent(Model.Text) ? string.Empty : Model.Text;
        ApplyTextAlignmentFromModel(); UpdateChrome(); UpdatePlaceholder(); UpdateDragOverlay();
    }
    private void ApplyTextAlignmentFromModel() { if (Model != null) AnnotationTextAlignmentHelper.ApplyToEditor(Editor, Placeholder, Model.HorizontalTextAlignment, Model.VerticalTextAlignment); }
    internal void UpdateChrome()
    {
        var isDashed = Model?.BorderStyle == AnnotationBorderStyle.Dashed;
        var borderDip = PdfCoordinateConverter.PointsToPixels(DefaultBorderWidthPoints, _displayScale);
        Chrome.Background = TryParseColor(Model?.BackgroundColorHex, new SolidColorBrush(Color.Parse(DefaultBackgroundHex)));
        if (isDashed)
        {
            var dashDip = PdfCoordinateConverter.PointsToPixels(DefaultDashPoints, _displayScale);
            Chrome.BorderBrush = Brushes.Transparent;
            Chrome.BorderThickness = new Thickness(0);
            DashedBorderFrame.IsVisible = true;
            DashedBorderFrame.UseLayoutRounding = false;
            DashedBorderFrame.Stroke = Brushes.Black;
            DashedBorderFrame.StrokeThickness = borderDip;
            var dashOnStroke = dashDip / Math.Max(borderDip, 0.001);
            DashedBorderFrame.StrokeDashArray = new Avalonia.Collections.AvaloniaList<double> { dashOnStroke, dashOnStroke };
        }
        else
        {
            DashedBorderFrame.IsVisible = false;
            Chrome.BorderBrush = Brushes.Black;
            Chrome.BorderThickness = new Thickness(borderDip);
        }
        AnnotationAdornerService.UpdateAdornerState(this);
    }
    private static Brush TryParseColor(string? hex, Brush fb) { if (string.IsNullOrWhiteSpace(hex)) return fb; try { return new SolidColorBrush(Color.Parse(hex)); } catch { return fb; } }
    private void UpdatePlaceholder() { var show = !Editor.IsFocused && (string.IsNullOrEmpty(Editor.Text) || IsPlaceholderContent(Editor.Text)); Placeholder.IsVisible = show; if (show) Placeholder.FontSize = Editor.FontSize; }
    private void UpdateDragOverlay() => DragOverlay.IsVisible = !Editor.IsFocused;
    public void SyncToModel() { if (Model != null) Model.Text = IsPlaceholderContent(Editor.Text) ? DefaultPlaceholderText : Editor.Text; }
    private void ContextMenu_OnOpened(object? s, EventArgs e) { if (Model != null) IsSelected = true; BorderSolidMenuItem.IsChecked = Model?.BorderStyle != AnnotationBorderStyle.Dashed; BorderDashedMenuItem.IsChecked = Model?.BorderStyle == AnnotationBorderStyle.Dashed; if (Model != null) { AlignHLeftMenuItem.IsChecked = Model.HorizontalTextAlignment == AnnotationHorizontalTextAlignment.Left; AlignHCenterMenuItem.IsChecked = Model.HorizontalTextAlignment == AnnotationHorizontalTextAlignment.Center; AlignHRightMenuItem.IsChecked = Model.HorizontalTextAlignment == AnnotationHorizontalTextAlignment.Right; AlignVTopMenuItem.IsChecked = Model.VerticalTextAlignment == AnnotationVerticalTextAlignment.Top; AlignVCenterMenuItem.IsChecked = Model.VerticalTextAlignment == AnnotationVerticalTextAlignment.Center; AlignVBottomMenuItem.IsChecked = Model.VerticalTextAlignment == AnnotationVerticalTextAlignment.Bottom; } }
    private void HAlign(AnnotationHorizontalTextAlignment v) { if (!IsAnnotationEditingEnabled || Model == null) return; Model.HorizontalTextAlignment = v; ApplyTextAlignmentFromModel(); CommitEdit?.Invoke(this, EventArgs.Empty); }
    private void VAlign(AnnotationVerticalTextAlignment v) { if (!IsAnnotationEditingEnabled || Model == null) return; Model.VerticalTextAlignment = v; ApplyTextAlignmentFromModel(); CommitEdit?.Invoke(this, EventArgs.Empty); }
    private void AlignHLeftMenuItem_OnClick(object? s, Avalonia.Interactivity.RoutedEventArgs e) => HAlign(AnnotationHorizontalTextAlignment.Left);
    private void AlignHCenterMenuItem_OnClick(object? s, Avalonia.Interactivity.RoutedEventArgs e) => HAlign(AnnotationHorizontalTextAlignment.Center);
    private void AlignHRightMenuItem_OnClick(object? s, Avalonia.Interactivity.RoutedEventArgs e) => HAlign(AnnotationHorizontalTextAlignment.Right);
    private void AlignVTopMenuItem_OnClick(object? s, Avalonia.Interactivity.RoutedEventArgs e) => VAlign(AnnotationVerticalTextAlignment.Top);
    private void AlignVCenterMenuItem_OnClick(object? s, Avalonia.Interactivity.RoutedEventArgs e) => VAlign(AnnotationVerticalTextAlignment.Center);
    private void AlignVBottomMenuItem_OnClick(object? s, Avalonia.Interactivity.RoutedEventArgs e) => VAlign(AnnotationVerticalTextAlignment.Bottom);
    private void BorderSolidMenuItem_OnClick(object? s, Avalonia.Interactivity.RoutedEventArgs e) { if (IsAnnotationEditingEnabled && Model != null) { Model.BorderStyle = AnnotationBorderStyle.Solid; UpdateChrome(); CommitEdit?.Invoke(this, EventArgs.Empty); } }
    private void BorderDashedMenuItem_OnClick(object? s, Avalonia.Interactivity.RoutedEventArgs e) { if (IsAnnotationEditingEnabled && Model != null) { Model.BorderStyle = AnnotationBorderStyle.Dashed; UpdateChrome(); CommitEdit?.Invoke(this, EventArgs.Empty); } }
    private void SetBg(string hex) { if (IsAnnotationEditingEnabled && Model != null) { Model.BackgroundColorHex = hex; UpdateChrome(); CommitEdit?.Invoke(this, EventArgs.Empty); } }
    private void BgYellowMenuItem_OnClick(object? s, Avalonia.Interactivity.RoutedEventArgs e) => SetBg("#FFFFF59D");
    private void BgGreenMenuItem_OnClick(object? s, Avalonia.Interactivity.RoutedEventArgs e) => SetBg("#FFA5D6A7");
    private void BgRedMenuItem_OnClick(object? s, Avalonia.Interactivity.RoutedEventArgs e) => SetBg("#FFEF9A9A");
    private void BgBlueMenuItem_OnClick(object? s, Avalonia.Interactivity.RoutedEventArgs e) => SetBg("#FF90CAF9");
    private void BgWhiteMenuItem_OnClick(object? s, Avalonia.Interactivity.RoutedEventArgs e) => SetBg("#FFFFFFFF");
    private void FontSizeMenuItem_OnClick(object? s, Avalonia.Interactivity.RoutedEventArgs e) { if (IsAnnotationEditingEnabled && Model != null && s is MenuItem mi && mi.Tag is string t && double.TryParse(t, out var fs)) { Model.FontSize = fs; ApplyModelToUi(); CommitEdit?.Invoke(this, EventArgs.Empty); } }
    private void BoldMenuItem_OnClick(object? s, Avalonia.Interactivity.RoutedEventArgs e)
    {
        if (!IsAnnotationEditingEnabled || Model == null ||
            !Model.Id.StartsWith("__domain_", StringComparison.Ordinal) ||
            Model.FontFamily.Equals("Arial Unicode MS", StringComparison.OrdinalIgnoreCase))
            return;

        Model.IsBold = true;
        BoldMenuItem.IsChecked = true;
        ApplyModelToUi();
        CommitEdit?.Invoke(this, EventArgs.Empty);
    }
    private void DeleteMenuItem_OnClick(object? s, Avalonia.Interactivity.RoutedEventArgs e) { if (IsAnnotationEditingEnabled) DeleteRequested?.Invoke(this, EventArgs.Empty); }
}