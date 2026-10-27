using Avalonia.Controls;
using AcrfAnnotationTool.ViewModels.Documents;
using AtomDialog = AtomUI.Desktop.Controls.Dialog;
using AtomDialogButtonClickedEventArgs = AtomUI.Desktop.Controls.DialogButtonClickedEventArgs;
using AtomDialogButtonRole = AtomUI.Desktop.Controls.DialogButtonRole;

namespace AcrfAnnotationTool.Views;

public partial class AcrfEditorView : UserControl
{
    public AcrfEditorView()
    {
        InitializeComponent();
        PdfDock.DockHost = PanelDock;
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
