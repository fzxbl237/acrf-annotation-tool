using Avalonia;
using Dock.Settings;

namespace AcrfAnnotationTool;

internal static class Program
{
    [STAThread]
    public static void Main(string[] args) => BuildAvaloniaApp().StartWithClassicDesktopLifetime(args);

    public static AppBuilder BuildAvaloniaApp()
    {
        DockSettings.DocumentWindowDragScope = WindowDragDockScope.FullWindow;

        return AppBuilder.Configure<App>()
            .UsePlatformDetect()
            .WithInterFont()
            .UseFloatingDockAdorner()
            .ShowDockablePreviewOnDrag()
            .CloseFloatingWindowsOnMainWindowClose(true)
            .LogToTrace();
    }
}
