using System.Globalization;
using System.Runtime.InteropServices;
using System.Security.Cryptography;
using System.Text;
using AcrfAnnotationTool;
using AcrfAnnotationTool.Controls.Menus;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Controls.Presenters;
using Avalonia.Input.Platform;
using Avalonia.Layout;
using Avalonia.Media.TextFormatting;
using Avalonia.VisualTree;
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
    internal double DisplayScale => _displayScale;
    internal double PageHeightPoints { get; private set; }
    public FreeTextAnnotationControl()
    {
        InitializeComponent(); ClipToBounds = false;
        Editor.IsReadOnly = !IsAnnotationEditingEnabled;
        UpdateMenuEditability();
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
                UpdateMenuEditability();
                if (!IsAnnotationEditingEnabled) AnnotationMenu.Close(false);
                ApplyModelToUi();
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
        PageHeightPoints = pageHeightPoints;
        var w = PdfCoordinateConverter.PointsToPixels(Model.Width, displayScale);
        var h = PdfCoordinateConverter.PointsToPixels(Model.Height, displayScale);
        var wpfPos = PdfCoordinateConverter.PdfToAvalonia(Model.PdfX, Model.PdfY, Model.Height, pageHeightPoints, displayScale);
        var left = wpfPos.X; var top = wpfPos.Y;
        Canvas.SetLeft(this, left); Canvas.SetTop(this, top);
        MinWidth = Math.Max(2, Math.Min(w, 12 * displayScale)); MinHeight = Math.Max(2, Math.Min(h, 8 * displayScale));
        Width = w; Height = h;
        var isArialUnicodeMs = Model.FontFamily.Equals("Arial Unicode MS", StringComparison.OrdinalIgnoreCase);
        // PdfAnnotationService draws the exported text inside a rectangle inset by 2pt on every side and
        // strokes the 1pt border within that same inset, so the viewer has to leave the text the same
        // room: border + padding = 2pt. With a 2pt padding the viewer gave the text only width - 6pt and
        // height - 6pt, so text the export keeps on one line wrapped here, and in a single-line box the
        // missing 2pt of vertical room clipped the descender of letters like "g" at the bottom.
        var borderDip = PdfCoordinateConverter.PointsToPixels(DefaultBorderWidthPoints, _displayScale);
        var horizontalPadding = Math.Max(0, 2 * displayScale - borderDip);
        var verticalPadding = isArialUnicodeMs ? Math.Max(0.5, 0.5 * displayScale) : Math.Max(0, 2 * displayScale - borderDip);
        var baselineCompensation = isArialUnicodeMs ? 0.5 * displayScale : 0;
        Chrome.Padding = new Thickness(
            horizontalPadding,
            Math.Max(0, verticalPadding - baselineCompensation),
            horizontalPadding,
            verticalPadding + baselineCompensation);
        Editor.FontSize = Math.Max(1, Model.FontSize * displayScale); Placeholder.FontSize = Editor.FontSize;
        ApplyTextAlignmentFromModel(); ApplyModelToUi(); UpdateChrome();
        if (Parent is PageAnnotationCanvas page) page.RefreshCallout(this);
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
                Editor.FontFamily = AcrfProjectFontCollection.AnnotationFontFamily;
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
    private void ContextMenu_OnOpened(object? s, Avalonia.Interactivity.RoutedEventArgs e) { if (Model != null) IsSelected = true; BorderSolidMenuItem.IsChecked = Model?.BorderStyle != AnnotationBorderStyle.Dashed; BorderDashedMenuItem.IsChecked = Model?.BorderStyle == AnnotationBorderStyle.Dashed; if (Model != null) { AlignHLeftMenuItem.IsChecked = Model.HorizontalTextAlignment == AnnotationHorizontalTextAlignment.Left; AlignHCenterMenuItem.IsChecked = Model.HorizontalTextAlignment == AnnotationHorizontalTextAlignment.Center; AlignHRightMenuItem.IsChecked = Model.HorizontalTextAlignment == AnnotationHorizontalTextAlignment.Right; AlignVTopMenuItem.IsChecked = Model.VerticalTextAlignment == AnnotationVerticalTextAlignment.Top; AlignVCenterMenuItem.IsChecked = Model.VerticalTextAlignment == AnnotationVerticalTextAlignment.Center; AlignVBottomMenuItem.IsChecked = Model.VerticalTextAlignment == AnnotationVerticalTextAlignment.Bottom; } }
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
    private void FontSizeMenuItem_OnClick(object? s, Avalonia.Interactivity.RoutedEventArgs e) { if (IsAnnotationEditingEnabled && Model != null && s is MenuEntry mi && mi.CommandParameter is string t && double.TryParse(t, out var fs)) { Model.FontSize = fs; ApplyModelToUi(); CommitEdit?.Invoke(this, EventArgs.Empty); } }
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
    private void SetCallout(bool isLeft)
    {
        if (!IsAnnotationEditingEnabled || Model == null || Parent is not Canvas canvas) return;
        if (!Model.CalloutEnabled)
        {
            var pageWidth = canvas.Bounds.Width / _displayScale;
            Model.CalloutArmLength = 8;
            Model.CalloutX = Math.Clamp(isLeft ? Model.PdfX - 40 : Model.PdfX + Model.Width + 40,
                8, Math.Max(8, pageWidth - 8));
            var centerY = Model.PdfY + Model.Height / 2;
            Model.CalloutY1 = Math.Clamp(centerY + 36, 0, PageHeightPoints);
            Model.CalloutY2 = Math.Clamp(centerY - 36, 0, PageHeightPoints);
        }
        Model.CalloutEnabled = true;
        Model.CalloutIsLeft = isLeft;
        Model.CalloutShape = isLeft ? "LeftBracket" : "RightBracket";
        NotifyResizeCommitted();
        if (Parent is PageAnnotationCanvas page) page.RefreshCallout(this);
    }
    private void CalloutLeftMenuItem_OnClick(object? s, Avalonia.Interactivity.RoutedEventArgs e) => SetCallout(true);
    private void CalloutRightMenuItem_OnClick(object? s, Avalonia.Interactivity.RoutedEventArgs e) => SetCallout(false);
    private void CalloutShapeMenuItem_OnClick(object? sender, Avalonia.Interactivity.RoutedEventArgs e)
    {
        if (!IsAnnotationEditingEnabled || Model == null || sender is not MenuEntry { CommandParameter: string shape }) return;
        if (!Model.CalloutEnabled) SetCallout(true);
        Model.CalloutShape = shape;
        NotifyResizeCommitted();
        if (Parent is PageAnnotationCanvas page) page.RefreshCallout(this);
    }
    private void CalloutRemoveMenuItem_OnClick(object? s, Avalonia.Interactivity.RoutedEventArgs e)
    {
        if (!IsAnnotationEditingEnabled || Model == null) return;
        Model.CalloutEnabled = false;
        NotifyResizeCommitted();
        if (Parent is PageAnnotationCanvas page) page.RefreshCallout(this);
    }
    private void UpdateMenuEditability()
    {
        // Diagnostics are read-only and remain available when annotation editing is disabled.
        foreach (var entry in AnnotationMenu.Items.OfType<MenuEntry>())
            entry.IsEnabled = ReferenceEquals(entry, FontDiagnosticsMenuItem) || IsAnnotationEditingEnabled;
    }

    private void FontDiagnosticsMenuItem_OnClick(object? sender, Avalonia.Interactivity.RoutedEventArgs e)
    {
        // Let the menu finish closing before moving focus to the report window.
        Dispatcher.Post(ShowFontDiagnostics, DispatcherPriority.Background);
    }

    private async void ShowFontDiagnostics()
    {
        if (Model == null || TopLevel.GetTopLevel(this) is not Window owner) return;
        string report;
        try
        {
            report = BuildFontDiagnosticsReport();
        }
        catch (Exception exception)
        {
            report = $"读取字体诊断失败：{exception.Message}";
        }

        var reportBox = new TextBox
        {
            Text = report,
            IsReadOnly = true,
            AcceptsReturn = true,
            TextWrapping = TextWrapping.NoWrap,
            FontSize = 13,
            HorizontalAlignment = HorizontalAlignment.Stretch,
            VerticalAlignment = VerticalAlignment.Stretch
        };
        ScrollViewer.SetHorizontalScrollBarVisibility(reportBox, Avalonia.Controls.Primitives.ScrollBarVisibility.Auto);
        ScrollViewer.SetVerticalScrollBarVisibility(reportBox, Avalonia.Controls.Primitives.ScrollBarVisibility.Auto);
        var copyButton = new Button { Content = "复制报告" };
        var closeButton = new Button { Content = "关闭" };
        var status = new TextBlock { VerticalAlignment = VerticalAlignment.Center };
        var buttons = new StackPanel
        {
            Orientation = Orientation.Horizontal,
            Spacing = 8,
            Margin = new Thickness(0, 12, 0, 0),
            Children = { copyButton, closeButton, status }
        };
        var content = new Grid { RowDefinitions = new RowDefinitions("*,Auto"), Margin = new Thickness(16) };
        Grid.SetRow(buttons, 1);
        content.Children.Add(reportBox);
        content.Children.Add(buttons);
        var dialog = new Window
        {
            Title = "注释字体诊断",
            Width = 820,
            Height = 620,
            MinWidth = 360,
            MinHeight = 240,
            WindowStartupLocation = WindowStartupLocation.CenterOwner,
            Content = content
        };
        copyButton.Click += async (_, _) =>
        {
            try
            {
                var clipboard = TopLevel.GetTopLevel(dialog)?.Clipboard;
                if (clipboard == null) { status.Text = "剪贴板不可用，可选中文本后复制。"; return; }
                await clipboard.SetTextAsync(report);
                status.Text = "已复制";
            }
            catch (Exception exception)
            {
                status.Text = $"复制失败：{exception.Message}";
            }
        };
        closeButton.Click += (_, _) => dialog.Close();
        dialog.KeyDown += (_, args) => { if (args.Key == Key.Escape) { dialog.Close(); args.Handled = true; } };
        await dialog.ShowDialog(owner);
    }

    private string BuildFontDiagnosticsReport()
    {
        var report = new StringBuilder();
        void Add(FormattableString line) => report.AppendLine(line.ToString(CultureInfo.InvariantCulture));
        static string Escape(string text) => text.Replace("\\", "\\\\").Replace("\r", "\\r").Replace("\n", "\\n").Replace("\t", "\\t");

        report.AppendLine("注释字体诊断（读取当前 Editor.TextPresenter.TextLayout，不是重新测量的替代布局）");
        Add($"时间：{DateTimeOffset.Now:O}");
        Add($"程序版本：{typeof(FreeTextAnnotationControl).Assembly.GetName().Version}");
        Add($"系统：{RuntimeInformation.OSDescription} / {RuntimeInformation.ProcessArchitecture}");
        Add($"注释 ID：{Model?.Id}；页码：{Model?.PageIndex + 1}");
        Add($"注释文本：{Escape(Editor.Text ?? string.Empty)}");
        Add($"模型请求字体：{Model?.FontFamily}");
        Add($"Editor.FontFamily：{Editor.FontFamily}");
        Add($"Editor 字体集合键：{Editor.FontFamily.Key?.ToString() ?? "(system)"}");
        Add($"字重/样式/拉伸：{Editor.FontWeight} / {Editor.FontStyle} / {Editor.FontStretch}");
        Add($"字号：{Model?.FontSize:0.###} pt；Editor：{Editor.FontSize:0.###} DIP");
        Add($"PDF→DIP 缩放：{_displayScale:0.######}；屏幕 RenderScaling：{TopLevel.GetTopLevel(this)?.RenderScaling:0.###}");
        Add($"PDF 注释矩形：{Model?.Width:0.###} × {Model?.Height:0.###} pt（不是表格的原始 Width）");
        Add($"控件尺寸：{Bounds.Width:0.###} × {Bounds.Height:0.###} DIP；Editor：{Editor.Bounds.Width:0.###} × {Editor.Bounds.Height:0.###} DIP");
        Add($"边框：{Chrome.BorderThickness}；内边距：{Chrome.Padding}");
        var requested = new Typeface(Editor.FontFamily, Editor.FontStyle, Editor.FontWeight, Editor.FontStretch).GlyphTypeface;
        Add($"请求字体解析结果：{requested.FamilyName} / {requested.TypographicFamilyName}；{requested.Weight} / {requested.Style}");

        var fontPath = Path.Combine(AppContext.BaseDirectory, "Fonts", "ARIALUNI.TTF");
        string? projectFontHash = null;
        Add($"项目中文字体文件：{fontPath}");
        try
        {
            using var stream = File.OpenRead(fontPath);
            projectFontHash = Convert.ToHexString(SHA256.HashData(stream));
            Add($"字体文件大小：{stream.Length} bytes；SHA-256：{projectFontHash}");
        }
        catch (Exception exception)
        {
            Add($"无法校验字体文件：{exception.Message}");
        }

        var fontDataHashes = new Dictionary<GlyphTypeface, string?>();
        string? GetFontDataHash(GlyphTypeface font)
        {
            if (fontDataHashes.TryGetValue(font, out var hash)) return hash;
            try
            {
                if (font.PlatformTypeface.TryGetStream(out var stream))
                {
                    using (stream)
                        hash = Convert.ToHexString(SHA256.HashData(stream));
                }
            }
            catch (Exception exception)
            {
                Add($"  无法读取实际字体数据：{exception.Message}");
            }
            fontDataHashes.Add(font, hash);
            return hash;
        }

        var presenter = Editor.GetVisualDescendants().OfType<TextPresenter>().FirstOrDefault();
        if (presenter == null)
        {
            report.AppendLine("未找到 TextPresenter，无法获取实际字形字体；请确认注释已显示后重试。");
        }
        else
        {
            var layout = presenter.TextLayout;
            Add($"实际布局宽度：{layout.Width:0.###} DIP；含行尾空白：{layout.WidthIncludingTrailingWhitespace:0.###} DIP");
            report.AppendLine("以下为实际绘制的字形字体；同一注释可能按文字片段回退到不同字体。");
            var lineIndex = 0;
            foreach (var line in layout.TextLines)
            {
                Add($"行 {++lineIndex}：字宽 {line.Width:0.###} DIP = {line.Width / _displayScale:0.###} pt；含行尾空白 {line.WidthIncludingTrailingWhitespace:0.###} DIP");
                foreach (var run in line.TextRuns)
                {
                    if (run is not ShapedTextRun shaped) continue;
                    var glyphRun = shaped.GlyphRun;
                    var font = glyphRun.GlyphTypeface;
                    Add($"  文本片段：{Escape(shaped.Text.ToString())}");
                    Add($"  实际字体：{font.FamilyName}；TypographicFamilyName：{font.TypographicFamilyName}");
                    Add($"  实际字重/样式/拉伸：{font.Weight} / {font.Style} / {font.Stretch}；模拟：{font.FontSimulations}");
                    Add($"  与请求解析字面为同一实例：{ReferenceEquals(font, requested)}（仅用于观察字体回退，不证明文件来源）");
                    var actualFontHash = GetFontDataHash(font);
                    Add($"  实际字形字体数据 SHA-256：{actualFontHash ?? "不可读取"}");
                    Add($"  实际字体数据与程序目录 ARIALUNI.TTF 一致：{(actualFontHash == null || projectFontHash == null ? "无法验证" : (actualFontHash == projectFontHash).ToString())}");
                    Add($"  字形字号：{glyphRun.FontRenderingEmSize:0.###} DIP；字形墨迹宽度：{glyphRun.InkBounds.Width:0.###} DIP");
                }
            }
        }

        report.AppendLine("注：字体数据一致通过实际字形的底层字体流与磁盘文件 SHA-256 比较，不是仅比较字体家族名称。");
        report.AppendLine("注：DIP 为逻辑像素，pt 为 PDF 点；字宽为排版前进宽度，不等于可见墨迹最右边界。");
        return report.ToString();
    }

    private void DeleteMenuItem_OnClick(object? s, Avalonia.Interactivity.RoutedEventArgs e) { if (IsAnnotationEditingEnabled) DeleteRequested?.Invoke(this, EventArgs.Empty); }
}