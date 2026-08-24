using Avalonia;
using Avalonia.Controls.ApplicationLifetimes;
using Avalonia.Markup.Xaml;
using AcrfAnnotationTool.Services;
using AcrfAnnotationTool.ViewModels;
using AcrfAnnotationTool.Views;
using AtomUI;
using AtomUI.Desktop.Controls;
using AtomUI.Localization;
using AtomUI.Theme;

namespace AcrfAnnotationTool;

public partial class App : Application
{
    public override void Initialize()
    {
        AvaloniaXamlLoader.Load(this);
        this.UseAtomUI(builder =>
        {
            builder.UseLanguages(LanguageTags.ZhCN, [LanguageTags.En,LanguageTags.ZhCN]);
            builder.WithInitialTheme(IThemeManager.DEFAULT_THEME_ID);
            builder.UseAlibabaSansFont();
            builder.UseDesktopControls();
        });
    }

    public override void OnFrameworkInitializationCompleted()
    {
        if (ApplicationLifetime is IClassicDesktopStyleApplicationLifetime desktop)
        {
            var templateVariableDomainCatalog =
                TemplateVariableDomainCatalog.StartFromApplicationDatabase();
            desktop.MainWindow = new MainWindow
            {
                DataContext = new MainWindowViewModel(templateVariableDomainCatalog)
            };
        }

        base.OnFrameworkInitializationCompleted();
    }
}
