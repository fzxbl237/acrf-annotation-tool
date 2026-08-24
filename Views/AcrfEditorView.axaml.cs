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
    }

    private void OnDomainAnnotationSettingsClick(object? sender, Avalonia.Interactivity.RoutedEventArgs e)
    {
        if (DataContext is not AcrfDocumentViewModel viewModel)
            return;

        DomainAnnotationHorizontalOffsetInput.Value = (decimal)viewModel.DomainAnnotationHorizontalOffset;
        DomainAnnotationVerticalOffsetInput.Value = (decimal)viewModel.DomainAnnotationVerticalOffset;
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

        viewModel.ApplyDomainAnnotationOffsets((double)horizontalValue, (double)verticalValue);
        dialog.IsOpen = false;
    }
}
