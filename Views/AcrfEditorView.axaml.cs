using Avalonia;
using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Interactivity;
using Avalonia.VisualTree;
using AcrfAnnotationTool.ViewModels.Documents;
using AcrfAnnotationTool.Services;
using AtomDialog = AtomUI.Desktop.Controls.Dialog;
using AtomDialogButtonClickedEventArgs = AtomUI.Desktop.Controls.DialogButtonClickedEventArgs;
using AtomDialogButtonRole = AtomUI.Desktop.Controls.DialogButtonRole;

namespace AcrfAnnotationTool.Views;

public partial class AcrfEditorView : UserControl
{
    private TopLevel? _shortcutRoot;

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
        InitializeComponent();
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

    private void OnDomainAnnotationSettingsClick(object? sender, Avalonia.Interactivity.RoutedEventArgs e)
    {
        if (DataContext is not AcrfDocumentViewModel viewModel)
            return;

        DomainAnnotationHorizontalOffsetInput.Value = (decimal)viewModel.DomainAnnotationHorizontalOffset;
        DomainAnnotationVerticalOffsetInput.Value = (decimal)viewModel.DomainAnnotationVerticalOffset;
        PdfAnnotationEditingCheckBox.IsChecked = viewModel.IsPdfAnnotationEditingEnabled;
        LanguageToggleSwitch.IsChecked = viewModel.Language;
        DomainAnnotationSettingsErrorText.IsVisible = false;
        DomainAnnotationSettingsDialog.PlacementTarget = DomainAnnotationSettingsButton;
        DomainAnnotationSettingsDialog.IsOpen = true;
    }

    private void OnDomainAnnotationDialogButtonClicked(object? sender, AtomDialogButtonClickedEventArgs e)
    {
        if (sender is not AtomDialog dialog || e.SourceButton.Role != AtomDialogButtonRole.AcceptRole)
            return;

        if (DomainAnnotationHorizontalOffsetInput.Value is not decimal horizontalValue ||
            DomainAnnotationVerticalOffsetInput.Value is not decimal verticalValue)
        {
            DomainAnnotationSettingsErrorText.Text = "请输入水平和垂直距离。";
            DomainAnnotationSettingsErrorText.IsVisible = true;
            return;
        }

        if (DataContext is not AcrfDocumentViewModel viewModel)
            return;

        viewModel.ApplyDomainAnnotationSettings(
            (double)horizontalValue,
            (double)verticalValue,
            PdfAnnotationEditingCheckBox.IsChecked == true,
            LanguageToggleSwitch.IsChecked == true);
        dialog.IsOpen = false;
    }
}
