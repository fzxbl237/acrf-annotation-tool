using Avalonia.Controls;
using Avalonia.Interactivity;
using Avalonia.Threading;
using AcrfAnnotationTool.ViewModels;
using AcrfAnnotationTool.ViewModels.Documents;
using Window = AtomUI.Desktop.Controls.Window;

namespace AcrfAnnotationTool.Views;

public partial class MainWindow : Window
{
    private bool _isShutdownConfirmationPending;
    private bool _allowClose;

    public MainWindow()
    {
        InitializeComponent();
        Closing += OnClosing;
    }

    private void OnClosing(object? sender, WindowClosingEventArgs e)
    {
        if (_allowClose)
            return;

        e.Cancel = true;
        if (_isShutdownConfirmationPending)
            return;

        Dispatcher.UIThread.Post(OpenShutdownConfirmation);
    }

    private void OnCancelShutdownClick(object? sender, RoutedEventArgs e)
    {
        CloseShutdownConfirmation();
    }

    private void OnShutdownConfirmationClosed(object? sender, EventArgs e)
    {
        if (_allowClose || ShutdownExportDialog.IsOpen)
            return;

        _isShutdownConfirmationPending = false;
        ShutdownExportDialog.IsModal = false;
    }

    private void OnCloseWithoutExportClick(object? sender, RoutedEventArgs e)
    {
        if (!_isShutdownConfirmationPending)
            return;

        CompleteShutdown();
    }

    private async void OnExportAndCloseClick(object? sender, RoutedEventArgs e)
    {
        if (!_isShutdownConfirmationPending)
            return;

        CloseShutdownConfirmation();
        if (DataContext is not MainWindowViewModel viewModel)
        {
            ShowShutdownExportError("无法找到当前 Spec 文档，窗口保持打开。", null);
            return;
        }

        viewModel.Status = "正在导出 Spec...";
        var outcome = await viewModel.ExportSpecForShutdownAsync();
        if (outcome is SpecExportOutcome.Succeeded or SpecExportOutcome.Canceled)
        {
            CompleteShutdown();
            return;
        }

        ShowShutdownExportError("Spec 导出失败，窗口保持打开。请检查文件路径或文件权限后重试。", viewModel);
    }

    private void ShowShutdownExportError(string message, MainWindowViewModel? viewModel)
    {
        _isShutdownConfirmationPending = true;
        if (viewModel is not null)
            viewModel.Status = message;
        ShutdownExportErrorText.Text = message;
        ShutdownExportErrorText.IsVisible = true;
        OpenShutdownConfirmation();
    }

    private void OpenShutdownConfirmation()
    {
        _isShutdownConfirmationPending = true;
        ShutdownExportDialog.IsModal = true;
        ShutdownExportDialog.IsOpen = true;
    }

    private void CloseShutdownConfirmation()
    {
        ShutdownExportDialog.IsOpen = false;
        ShutdownExportDialog.IsModal = false;
    }

    private void CompleteShutdown()
    {
        _isShutdownConfirmationPending = false;
        CloseShutdownConfirmation();
        _allowClose = true;
        Close();
    }
}
