using Avalonia;
using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Interactivity;
using Avalonia.VisualTree;
using AcrfAnnotationTool.ViewModels.Documents;
using AcrfAnnotationTool.Services;
using AcrfAnnotationTool.Services.Crf;
using AtomDialog = AtomUI.Desktop.Controls.Dialog;
using AtomDialogButtonClickedEventArgs = AtomUI.Desktop.Controls.DialogButtonClickedEventArgs;
using AtomDialogButtonRole = AtomUI.Desktop.Controls.DialogButtonRole;

namespace AcrfAnnotationTool.Views;

public partial class AcrfEditorView : UserControl
{
    private static readonly string[] CrfDatabaseTypeNames =
    [
        "Rave",
        "VieDoc",
        "Aurora Prime",
        "Taimei"
    ];

    private TopLevel? _shortcutRoot;
    private bool _crfExtractionRunning;
    private bool _updatingDomainAnnotationSettings;

    protected override void OnAttachedToVisualTree(VisualTreeAttachmentEventArgs e)
    {
        base.OnAttachedToVisualTree(e);
        _shortcutRoot = TopLevel.GetTopLevel(this);
        _shortcutRoot?.AddHandler(KeyDownEvent, OnDocumentHistoryKeyDown, RoutingStrategies.Bubble);
    }

    protected override void OnDetachedFromVisualTree(VisualTreeAttachmentEventArgs e)
    {
        _shortcutRoot?.RemoveHandler(KeyDownEvent, OnDocumentHistoryKeyDown);
        _shortcutRoot = null;
        base.OnDetachedFromVisualTree(e);
    }

    private void OnDocumentHistoryKeyDown(object? sender, KeyEventArgs e)
    {
        if (e.Handled || !IsVisible || !IsEffectivelyEnabled ||
            !e.KeyModifiers.HasFlag(KeyModifiers.Control) ||
            (e.KeyModifiers & (KeyModifiers.Alt | KeyModifiers.Meta)) != 0 ||
            e.Key is not (Key.Z or Key.Y) ||
            DataContext is not AcrfDocumentViewModel viewModel)
            return;

        // A recycled cell may no longer own focus; use the document's shared history instead.
        // Never take text undo away from an editor, even when it has no local undo left.
        var focused = _shortcutRoot?.FocusManager?.GetFocusedElement() as Visual;
        for (var visual = focused ?? e.Source as Visual; visual != null; visual = visual.GetVisualParent())
        {
            if (visual is TextBox or AtomUI.Desktop.Controls.LineEdit)
                return;
        }
        if (_shortcutRoot?.GetVisualDescendants().OfType<AtomDialog>()
                .Any(dialog => dialog.IsOpen && dialog.IsModal) == true)
            return;

        var redo = e.Key == Key.Y || e.KeyModifiers.HasFlag(KeyModifiers.Shift);
        if (viewModel.TryReplayAnnotationHistory(redo))
            e.Handled = true;
    }

    public AcrfEditorView()
    {
        _updatingDomainAnnotationSettings = true;
        InitializeComponent();
        _updatingDomainAnnotationSettings = false;
        CrfExtractionTypeComboBox.ItemsSource = CrfDatabaseTypeNames;
        CrfExtractionTypeComboBox.SelectedIndex = 0;
        PdfDock.DockHost = PanelDock;
        var settings = ApplicationSettingsStore.Load();
        if (settings.PanelDock is { } panelLayout) PanelDock.RestoreLayoutState(panelLayout);
        if (settings.PdfDock is { } pdfLayout) PdfDock.RestoreLayoutState(pdfLayout);
        OpenPdfButton.AsyncAction = () => DataContext is AcrfDocumentViewModel vm
            ? vm.OpenPdfWithResultAsync() : Task.FromResult(false);
        ImportSpecButton.AsyncAction = () => DataContext is AcrfDocumentViewModel vm
            ? vm.ImportWithResultAsync() : Task.FromResult(false);
        ExportSpecButton.AsyncAction = async () => DataContext is AcrfDocumentViewModel vm &&
            await vm.ExportSpecAsync() == SpecExportOutcome.Succeeded;
        SavePdfButton.AsyncAction = () => DataContext is AcrfDocumentViewModel vm
            ? vm.SavePdfWithResultAsync() : Task.FromResult(false);
        SavePdfFlattenButton.AsyncAction = () => DataContext is AcrfDocumentViewModel vm
            ? vm.SavePdfFlattenWithResultAsync() : Task.FromResult(false);
    }

    public bool TrySaveDockLayout(out string? error)
    {
        var settings = ApplicationSettingsStore.Load() with
        {
            PanelDock = PanelDock.CaptureLayoutState(),
            PdfDock = PdfDock.CaptureLayoutState()
        };
        return ApplicationSettingsStore.TrySave(settings, out error);
    }

    private void OnExtractCrfButtonClick(object? sender, Avalonia.Interactivity.RoutedEventArgs e)
    {
        if (_crfExtractionRunning || DataContext is not AcrfDocumentViewModel viewModel)
            return;

        CrfExtractionErrorText.IsVisible = false;
        if (string.IsNullOrWhiteSpace(viewModel.PdfPath))
        {
            CrfExtractionErrorText.Text = "请先导入 aCRF PDF。";
            CrfExtractionErrorText.IsVisible = true;
        }

        CrfExtractionDialog.PlacementTarget = ExtractCrfButton;
        CrfExtractionDialog.IsOpen = true;
    }

    private async void OnCrfExtractionDialogButtonClicked(object? sender, AtomDialogButtonClickedEventArgs e)
    {
        if (sender is not AtomDialog dialog)
            return;

        if (e.SourceButton.Role != AtomDialogButtonRole.AcceptRole)
        {
            dialog.IsOpen = false;
            return;
        }

        if (DataContext is not AcrfDocumentViewModel viewModel)
        {
            dialog.IsOpen = false;
            return;
        }

        if (string.IsNullOrWhiteSpace(viewModel.PdfPath))
        {
            CrfExtractionErrorText.Text = "请先导入 aCRF PDF。";
            CrfExtractionErrorText.IsVisible = true;
            return;
        }

        if (CrfExtractionTypeComboBox.SelectedIndex is < 0 or >= 4)
        {
            CrfExtractionErrorText.Text = "请选择数据库类型。";
            CrfExtractionErrorText.IsVisible = true;
            return;
        }

        var databaseType = (CrfDatabaseType)CrfExtractionTypeComboBox.SelectedIndex;
        dialog.IsOpen = false;
        _crfExtractionRunning = true;
        ExtractCrfButton.IsDisabled = true;
        try
        {
            await viewModel.ExtractCrfWithResultAsync(databaseType);
        }
        finally
        {
            _crfExtractionRunning = false;
            ExtractCrfButton.IsDisabled = false;
        }
    }

    private void OnDomainAnnotationSettingsOpened(object? sender, EventArgs e)
    {
        if (DataContext is not AcrfDocumentViewModel viewModel)
            return;

        _updatingDomainAnnotationSettings = true;
        try
        {
            DomainAnnotationHorizontalOffsetInput.Value = (decimal)viewModel.DomainAnnotationHorizontalOffset;
            DomainAnnotationVerticalOffsetInput.Value = (decimal)viewModel.DomainAnnotationVerticalOffset;
            PdfAnnotationEditingCheckBox.IsChecked = viewModel.IsPdfAnnotationEditingEnabled;
            LanguageToggleSwitch.IsChecked = viewModel.Language;
        }
        finally
        {
            _updatingDomainAnnotationSettings = false;
        }
    }

    private void OnDomainAnnotationSettingChanged(object? sender, RoutedEventArgs e)
    {
        if (_updatingDomainAnnotationSettings || DataContext is not AcrfDocumentViewModel viewModel)
            return;

        var horizontalValue = DomainAnnotationHorizontalOffsetInput.Value;
        var verticalValue = DomainAnnotationVerticalOffsetInput.Value;
        // An emptied offset input keeps the last applied value; the other settings still apply.
        if ((sender == DomainAnnotationHorizontalOffsetInput && horizontalValue is null) ||
            (sender == DomainAnnotationVerticalOffsetInput && verticalValue is null))
            return;

        viewModel.ApplyDomainAnnotationSettings(
            (double)(horizontalValue ?? (decimal)viewModel.DomainAnnotationHorizontalOffset),
            (double)(verticalValue ?? (decimal)viewModel.DomainAnnotationVerticalOffset),
            PdfAnnotationEditingCheckBox.IsChecked == true,
            LanguageToggleSwitch.IsChecked == true);
    }
}
