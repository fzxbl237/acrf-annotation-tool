using Avalonia;
using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Interactivity;
using Avalonia.VisualTree;
using AcrfAnnotationTool.Models;
using AcrfAnnotationTool.ViewModels.Documents;
using AtomContextMenu = AtomUI.Desktop.Controls.ContextMenu;
using AtomDialog = AtomUI.Desktop.Controls.Dialog;
using AtomDialogButtonClickedEventArgs = AtomUI.Desktop.Controls.DialogButtonClickedEventArgs;
using AtomDialogButtonRole = AtomUI.Desktop.Controls.DialogButtonRole;
using AtomMenuItem = AtomUI.Desktop.Controls.MenuItem;
using PatChes.Controls.DataGrid;

namespace AcrfAnnotationTool.Views;

public partial class AnnotationToolView : UserControl
{
    private enum CoordinateOperation
    {
        AdjustX,
        AdjustY
    }

    private CoordinateOperation _pendingCoordinateOperation;

    public AnnotationToolView()
    {
        InitializeComponent();
        AnnotationGrid.AddHandler(PointerPressedEvent, OnAnnotationGridPointerPressed, RoutingStrategies.Tunnel);
    }

    private void OnAnnotationGridPointerPressed(object? sender, PointerPressedEventArgs e)
    {
        if (!e.GetCurrentPoint(AnnotationGrid).Properties.IsRightButtonPressed)
            return;

        var visual = e.Source as Visual;
        while (visual is not null && visual is not DataGridRow)
            visual = visual.GetVisualParent();

        if (visual is not DataGridRow row || row.DataContext is not Annotation annotation)
            return;
        if (DataContext is not AcrfDocumentViewModel viewModel)
            return;

        viewModel.SelectedItem = annotation;

        var menu = new AtomContextMenu();
        var deleteItem = new AtomMenuItem { Header = "删除" };
        var copyItem = new AtomMenuItem { Header = "复制" };
        var uppercaseItem = new AtomMenuItem { Header = "大写" };
        var adjustXItem = new AtomMenuItem { Header = "批量调整 X" };
        var adjustYItem = new AtomMenuItem { Header = "批量调整 Y" };

        deleteItem.Click += (_, _) => viewModel.DeleteSelectedCommand.Execute(null);
        copyItem.Click += (_, _) => viewModel.CopySelectedCommand.Execute(null);
        uppercaseItem.Click += (_, _) => viewModel.UppercaseSelectedCommand.Execute(null);
        adjustXItem.Click += (_, _) => OpenCoordinateDialog(row, CoordinateOperation.AdjustX);
        adjustYItem.Click += (_, _) => OpenCoordinateDialog(row, CoordinateOperation.AdjustY);

        menu.Items.Add(deleteItem);
        menu.Items.Add(copyItem);
        menu.Items.Add(uppercaseItem);
        menu.Items.Add(adjustXItem);
        menu.Items.Add(adjustYItem);
        menu.PlacementTarget = row;
        menu.Open(row);
        e.Handled = true;
    }

    private void OpenCoordinateDialog(DataGridRow row, CoordinateOperation operation)
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
