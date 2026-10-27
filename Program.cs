using Avalonia;
using Avalonia.Media.Fonts;
using AtomUI;
using AtomUI.Desktop.Controls;
using AtomUI.Localization;
using AtomUI.Theme;

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
        return AppBuilder.Configure<App>()
            .UsePlatformDetect()
            .UseAtomUI(builder =>
            {
                builder.UseLanguages(LanguageTags.ZhCN, [LanguageTags.En, LanguageTags.ZhCN]);
                builder.WithInitialTheme(IThemeManager.DEFAULT_THEME_ID);
                builder.UseAlibabaSansFont();
                builder.UseDesktopControls();
            })
            .WithInterFont()
            .ConfigureFonts(fontManager =>
            {
                fontManager.AddFontCollection(new AcrfProjectFontCollection());
            })
            .LogToTrace();
    }
}
