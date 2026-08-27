using Avalonia;
using Avalonia.Controls.ApplicationLifetimes;
using Avalonia.Markup.Xaml;
using AcrfAnnotationTool.Services;
using AcrfAnnotationTool.Services.Interface;
using AcrfAnnotationTool.ViewModels;
using AcrfAnnotationTool.Views;
using Microsoft.Extensions.DependencyInjection;
using SqlSugar;
using AtomUI;
using AtomUI.Desktop.Controls;
using AtomUI.Localization;
using AtomUI.Theme;

namespace AcrfAnnotationTool;

public partial class App : Application
{
    private ServiceProvider? _serviceProvider;

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
            var services = new ServiceCollection();
            services.AddSingleton<ISqlSugarClient>(_ =>
            {
                var databasePath = Path.Combine(AppContext.BaseDirectory, "cdisc_setting.db");
                return new SqlSugarClient(new ConnectionConfig
                {
                    ConfigId = "setting",
                    DbType = DbType.Sqlite,
                    ConnectionString = $"DataSource={databasePath}",
                    IsAutoCloseConnection = true
                });
            });
            services.AddSingleton<IDatabaseService, DatabaseService>();
            services.AddSingleton<CdiscDatasetCatalog>();
            services.AddSingleton<TemplateVariableDomainCatalog>();
            services.AddSingleton<SuggestionOptionsAsyncLoader>();
            services.AddSingleton<IMessageService, MessageService>();
            services.AddSingleton<MainWindowViewModel>();

            _serviceProvider = services.BuildServiceProvider();
            _serviceProvider.GetRequiredService<IDatabaseService>().InitializeSuggestions();
            desktop.MainWindow = new MainWindow
            {
                DataContext = _serviceProvider.GetRequiredService<MainWindowViewModel>()
            };
            desktop.Exit += (_, _) => _serviceProvider.Dispose();
        }

        base.OnFrameworkInitializationCompleted();
    }
}
