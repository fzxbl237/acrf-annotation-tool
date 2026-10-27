using Avalonia;
using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Interactivity;
using Avalonia.Threading;
using Avalonia.VisualTree;
using AtomUI.Data;
using AtomUI.Desktop.Controls;
using AcrfAnnotationTool.Models;
using AcrfAnnotationTool.ViewModels.Documents;
using AcrfAnnotationTool.Controls.Menus;
using AtomUI.Icons.AntDesign;
using AtomDialog = AtomUI.Desktop.Controls.Dialog;
using AtomDialogButtonClickedEventArgs = AtomUI.Desktop.Controls.DialogButtonClickedEventArgs;
using AtomDialogButtonRole = AtomUI.Desktop.Controls.DialogButtonRole;
using PatChesDataGridRow = PatChes.Controls.DataGrid.DataGridRow;

namespace AcrfAnnotationTool.Views;

public partial class AnnotationToolView : UserControl
{
    private enum CoordinateOperation
    {
        AdjustX,
        AdjustY
    }

    private CoordinateOperation _pendingCoordinateOperation;
    private Annotation? _pendingDeletion;

    public AnnotationToolView()
    {
        InitializeComponent();
        AnnotationGrid.AddHandler(PointerPressedEvent, OnAnnotationGridPointerPressed, RoutingStrategies.Tunnel);
        DataContextChanged += OnDataContextChanged;
        AttachedToVisualTree += OnAttachedToVisualTree;
    }

    private void OnDataContextChanged(object? sender, EventArgs e)
    {
        if (VisualRoot is not null)
            AttachCurrentViewModel();
    }

    private void OnAttachedToVisualTree(object? sender, VisualTreeAttachmentEventArgs e)
    {
        AttachCurrentViewModel();
    }

    private void AttachCurrentViewModel()
    {
        if (DataContext is not AcrfDocumentViewModel viewModel)
            return;

        viewModel.AttachAnnotationGrid(AnnotationGrid);
    }

    protected override void OnDetachedFromVisualTree(VisualTreeAttachmentEventArgs e)
    {
        base.OnDetachedFromVisualTree(e);
    }

    private void OnAnnotationGridPointerPressed(object? sender, PointerPressedEventArgs e)
    {
        if (!e.GetCurrentPoint(AnnotationGrid).Properties.IsRightButtonPressed)
            return;

        var visual = e.Source as Visual;
        while (visual is not null && visual is not PatChesDataGridRow)
            visual = visual.GetVisualParent();

        if (visual is not PatChesDataGridRow row || row.DataContext is not Annotation annotation)
            return;
        if (DataContext is not AcrfDocumentViewModel viewModel)
            return;

        viewModel.SelectedItem = annotation;

        var menu = new MenuSurface
        {
            Variant = MenuSurfaceVariant.Context,
            ClosesOnViewportChange = true,
            FocusReturnTarget = AnnotationGrid
        };
        var deleteItem = new MenuEntry { Header = "删除", IsDestructive = true, Icon = new DeleteOutlined { Width = 16, Height = 16,StrokeBrush = Avalonia.Media.Brush.Parse("#FF4D4F"), Foreground = Avalonia.Media.Brush.Parse("#FF4D4F") } };
        var copyItem = new MenuEntry { Header = "复制", Icon = new CopyOutlined { Width = 16, Height = 16 } };
        var uppercaseItem = new MenuEntry { Header = "大写", Icon = new FontSizeOutlined { Width = 16, Height = 16 } };
        var adjustXItem = new MenuEntry { Header = "批量调整 X", Icon = new ColumnWidthOutlined { Width = 16, Height = 16 } };
        var adjustYItem = new MenuEntry { Header = "批量调整 Y", Icon = new ColumnHeightOutlined { Width = 16, Height = 16 } };

        deleteItem.Click += (_, _) => OpenDeleteConfirmationDialog(row, annotation);
        copyItem.Click += (_, _) => viewModel.CopySelectedCommand.Execute(null);
        uppercaseItem.Click += (_, _) => viewModel.UppercaseSelectedCommand.Execute(null);
        adjustXItem.Click += (_, _) => OpenCoordinateDialog(row, CoordinateOperation.AdjustX);
        adjustYItem.Click += (_, _) => OpenCoordinateDialog(row, CoordinateOperation.AdjustY);

        menu.Items.Add(deleteItem);
        menu.Items.Add(copyItem);
        menu.Items.Add(uppercaseItem);
        menu.Items.Add(adjustXItem);
        menu.Items.Add(adjustYItem);
        menu.OpenAt(row, e.GetPosition(row));
        e.Handled = true;
    }

    private void OpenDeleteConfirmationDialog(PatChesDataGridRow row, Annotation annotation)
    {
        _pendingDeletion = annotation;
        DeleteConfirmationDialog.PlacementTarget = row;
        DeleteConfirmationDialog.IsOpen = true;
    }

    private void OnDeleteConfirmationDialogButtonClicked(object? sender, AtomDialogButtonClickedEventArgs e)
    {
        if (sender is not AtomDialog dialog || e.SourceButton.Role != AtomDialogButtonRole.AcceptRole)
            return;

        if (DataContext is AcrfDocumentViewModel viewModel && _pendingDeletion is { } annotation)
        {
            viewModel.SelectedItem = annotation;
            viewModel.DeleteSelectedCommand.Execute(null);
        }

        _pendingDeletion = null;
        dialog.IsOpen = false;
    }

    private void OpenCoordinateDialog(PatChesDataGridRow row, CoordinateOperation operation)
    {
        _pendingCoordinateOperation = operation;
        CoordinateDialog.Title = GetCoordinateOperationTitle(operation);
        CoordinateOffsetInput.Value = 0;
        CoordinateErrorText.IsVisible = false;
        CoordinateDialog.PlacementTarget = row;
        CoordinateDialog.IsOpen = true;
    }

    private void OnCoordinateDialogButtonClicked(object? sender, AtomDialogButtonClickedEventArgs e)
    {
        if (sender is not AtomDialog dialog || e.SourceButton.Role != AtomDialogButtonRole.AcceptRole)
            return;

        if (CoordinateOffsetInput.Value is not decimal value)
        {
            CoordinateErrorText.Text = "请输入偏移量。";
            CoordinateErrorText.IsVisible = true;
            return;
        }

        int offset;
        try
        {
            offset = checked((int)Math.Round(value, MidpointRounding.AwayFromZero));
        }
        catch (OverflowException)
        {
            CoordinateErrorText.Text = "偏移量超出整数范围。";
            CoordinateErrorText.IsVisible = true;
            return;
        }

        if (DataContext is not AcrfDocumentViewModel viewModel)
            return;

        var updates = new List<(Annotation Annotation, int X, int Y)>();
        try
        {
            foreach (var annotation in viewModel.Annotations)
            {
                var x = annotation.X;
                var y = annotation.Y;
                switch (_pendingCoordinateOperation)
                {
                    case CoordinateOperation.AdjustX:
                        x = checked(x + offset);
                        break;
                    case CoordinateOperation.AdjustY:
                        y = checked(y + offset);
                        break;
                }

                updates.Add((annotation, x, y));
            }
        }
        catch (OverflowException)
        {
            CoordinateErrorText.Text = "坐标更新会超出整数范围，未修改任何观测。";
            CoordinateErrorText.IsVisible = true;
            return;
        }

        foreach (var update in updates)
        {
            update.Annotation.X = update.X;
            update.Annotation.Y = update.Y;
        }

        dialog.IsOpen = false;
    }

    private static string GetCoordinateOperationTitle(CoordinateOperation operation) => operation switch
    {
        CoordinateOperation.AdjustX => "批量调整 X",
        CoordinateOperation.AdjustY => "批量调整 Y",
        _ => "批量坐标调整"
    };
}
