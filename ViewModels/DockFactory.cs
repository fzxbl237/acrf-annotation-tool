using AcrfAnnotationTool.ViewModels.Documents;
using Dock.Model.Controls;
using Dock.Model.Core;
using Dock.Model.Mvvm;

namespace AcrfAnnotationTool.ViewModels;

public sealed class DockFactory : Factory
{
    private readonly MainWindowViewModel _owner;

    public DockFactory(MainWindowViewModel owner) => _owner = owner;

    public override IRootDock CreateLayout()
    {
        var editor = CreateEditor("AcrfEditor-1", "注释编辑", false);

        var root = CreateRootDock();
        root.Id = "Root";
        root.Title = "ACRF 注释工具";
        root.IsCollapsable = false;
        root.ActiveDockable = editor;
        root.DefaultDockable = editor;
        root.VisibleDockables = CreateList<IDockable>(editor);
        return root;
    }

    private AcrfDocumentViewModel CreateEditor(string id, string title, bool canClose)
    {
        var editor = new AcrfDocumentViewModel(_owner.VariableDomainCatalog)
        {
            Id = id,
            Title = title,
            CanClose = canClose,
            AppOwner = _owner
        };
        return editor;
    }

    public override void InitLayout(IDockable layout)
    {
        ContextLocator = new Dictionary<string, Func<object?>>
        {
            ["Root"] = () => _owner,
        };
        base.InitLayout(layout);
    }
}
