using Avalonia;
using Avalonia.Controls.ApplicationLifetimes;
using Avalonia.Markup.Xaml;
using AcrfAnnotationTool.Services;
using AcrfAnnotationTool.Services.Interface;
using AcrfAnnotationTool.ViewModels;
using AcrfAnnotationTool.Views;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Data.Sqlite;

namespace AcrfAnnotationTool;

public partial class App : Application
{
    private ServiceProvider? _serviceProvider;

    public override void Initialize()
    {
        AvaloniaXamlLoader.Load(this);
    }

    public override void OnFrameworkInitializationCompleted()
    {
        if (ApplicationLifetime is IClassicDesktopStyleApplicationLifetime desktop)
        {
            var services = new ServiceCollection();
            services.AddSingleton<Func<SqliteConnection>>(_ => () =>
            {
                var databasePath = Path.Combine(AppContext.BaseDirectory, "cdisc_setting.db");
                var connectionString = new SqliteConnectionStringBuilder
                {
                    DataSource = databasePath
                }.ToString();
                return new SqliteConnection(connectionString);
            });
            services.AddSingleton<IDatabaseService, DatabaseService>();
            services.AddSingleton<CdiscDatasetCatalog>();
            services.AddSingleton<TemplateVariableDomainCatalog>();
            services.AddSingleton<SuggestionOptionsAsyncLoader>();
            services.AddSingleton<IMessageService, MessageService>();
            services.AddSingleton<MainWindowViewModel>();

            _serviceProvider = services.BuildServiceProvider();
            //_serviceProvider.GetRequiredService<IDatabaseService>().InitializeSuggestions();
            desktop.MainWindow = new MainWindow
            {
                DataContext = _serviceProvider.GetRequiredService<MainWindowViewModel>()
            };
            desktop.Exit += (_, _) => _serviceProvider.Dispose();
        }

        base.OnFrameworkInitializationCompleted();
    }
}
