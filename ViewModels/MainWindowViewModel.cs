using CommunityToolkit.Mvvm.ComponentModel;
using Dock.Model.Controls;
using Dock.Model.Core;
using AcrfAnnotationTool.Services;

namespace AcrfAnnotationTool.ViewModels;

public partial class MainWindowViewModel : ObservableObject
{
    private readonly DockFactory _factory;
    private IRootDock? _layout;

    public TemplateVariableDomainCatalog VariableDomainCatalog { get; }
    private string _status = "就绪";

    public IRootDock? Layout
    {
        get => _layout;
        private set => SetProperty(ref _layout, value);
    }

    public DockFactory Factory => _factory;

    public string Status
    {
        get => _status;
        set => SetProperty(ref _status, value);
    }

    public MainWindowViewModel(TemplateVariableDomainCatalog? templateVariableDomainCatalog = null)
    {
        VariableDomainCatalog = templateVariableDomainCatalog ??
            Services.TemplateVariableDomainCatalog.StartFromApplicationDatabase();
        _factory = new DockFactory(this);
        ResetLayout();
    }

    private void ResetLayout()
    {
        Layout = _factory.CreateLayout();
    }
}
