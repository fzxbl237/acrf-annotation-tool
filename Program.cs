using Avalonia;
using Avalonia.Media;
using Avalonia.Media.Fonts;
using AtomUI;
using AtomUI.Desktop.Controls;
using AtomUI.Localization;
using AtomUI.Theme;

namespace AcrfAnnotationTool;

internal sealed class AcrfProjectFontCollection : FontCollectionBase
{
    private static readonly Uri CollectionKey = new("fonts:AcrfProjectFonts", UriKind.Absolute);

    public static FontFamily AnnotationFontFamily { get; } = new("fonts:AcrfProjectFonts#Arial Unicode MS");

    public override Uri Key => CollectionKey;

    public AcrfProjectFontCollection()
    {
        var fontPath = Path.Combine(AppContext.BaseDirectory, "Fonts", "ARIALUNI.TTF");
        if (!File.Exists(fontPath))
            throw new FileNotFoundException("The Chinese annotation font was not found.", fontPath);
        if (!TryAddFontSource(new Uri(fontPath)))
            throw new InvalidDataException($"Could not load the Chinese annotation font: {fontPath}");
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
            .LogToTrace();
    }
}
