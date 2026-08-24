using Avalonia.Controls;
using PdfViewerControl.Controls;
using AcrfAnnotationTool.ViewModels.Documents;

namespace AcrfAnnotationTool.Views;

public partial class PdfToolView : UserControl
{
    public PdfToolView()
    {
        InitializeComponent();
        AttachedToVisualTree += OnAttachedToVisualTree;
    }

    private void OnAttachedToVisualTree(object? sender, Avalonia.VisualTreeAttachmentEventArgs e)
    {
        if (DataContext is AcrfDocumentViewModel vm && TopLevel.GetTopLevel(this) is Window window)
            vm.Attach(PdfViewer, window);
    }
}
