using Avalonia;
using Avalonia.Media.Fonts;
using Dock.Settings;

namespace AcrfAnnotationTool;

internal sealed class AcrfProjectFontCollection : EmbeddedFontCollection
{
    public AcrfProjectFontCollection() : base(
        new Uri("fonts:AcrfProjectFonts", UriKind.Absolute),
        new Uri("avares://AcrfAnnotationTool/Fonts", UriKind.Absolute))
    {
    }
}

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
            .ConfigureFonts(fontManager =>
            {
                fontManager.AddFontCollection(new AcrfProjectFontCollection());
            })
            .UseFloatingDockAdorner()
            .ShowDockablePreviewOnDrag()
            .CloseFloatingWindowsOnMainWindowClose(true)
            .LogToTrace();
    }
}
