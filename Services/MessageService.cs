using Avalonia;
using Avalonia.Controls;
using Avalonia.Controls.ApplicationLifetimes;
using AtomUI.Desktop.Controls;
using AcrfAnnotationTool.Services.Interface;
using AtomMessage = AtomUI.Desktop.Controls.Message;

namespace AcrfAnnotationTool.Services;

public sealed class MessageService : IMessageService, IDisposable
{
    private WindowMessageManager? _messageManager;

    public void Info(string message) => Show(message, MessageType.Information);

    public void Success(string message) => Show(message, MessageType.Success);

    public void Warning(string message) => Show(message, MessageType.Warning);

    public void Error(string message) => Show(message, MessageType.Error);

    public void Dispose()
    {
        _messageManager?.Dispose();
        _messageManager = null;
    }

    private void Show(string message, MessageType type)
    {
        GetMessageManager()?.Show(new AtomMessage(message, type));
    }

    private WindowMessageManager? GetMessageManager()
    {
        if (_messageManager is not null)
            return _messageManager;

        if (Application.Current?.ApplicationLifetime is not IClassicDesktopStyleApplicationLifetime desktop ||
            desktop.MainWindow is not TopLevel mainWindow)
            return null;

        _messageManager = new WindowMessageManager(mainWindow)
        {
            MaxItems = 10
        };
        return _messageManager;
    }
}
