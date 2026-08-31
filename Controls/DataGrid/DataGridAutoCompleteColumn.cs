using System.Collections.Generic;
using AtomUI.Controls.Utils;
using AtomUI.Data;
using AtomUI.Desktop.Controls;
using AtomAutoComplete = AtomUI.Desktop.Controls.AutoComplete;
using AbstractAutoComplete = AtomUI.Desktop.Controls.AbstractAutoComplete;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Controls.Primitives;
using Avalonia.Controls.Templates;
using Avalonia.Data;
using Avalonia.Interactivity;
using Avalonia.Layout;
using TextBlock = Avalonia.Controls.TextBlock;

namespace PatChes.Controls.DataGrid;

public class DataGridAutoCompleteColumn : DataGridBoundColumn
{
    public static readonly StyledProperty<ICompleteOptionsAsyncLoader?> OptionsAsyncLoaderProperty =
        AvaloniaProperty.Register<DataGridAutoCompleteColumn, ICompleteOptionsAsyncLoader?>(nameof(OptionsAsyncLoader));
    
    public ICompleteOptionsAsyncLoader? OptionsAsyncLoader
    {
        get => GetValue(OptionsAsyncLoaderProperty);
        set => SetValue(OptionsAsyncLoaderProperty, value);
    }
    
    public static readonly StyledProperty<IEnumerable<IAutoCompleteOption>?> OptionsSourceProperty =
        AvaloniaProperty.Register<DataGridAutoCompleteColumn, IEnumerable<IAutoCompleteOption>?>(nameof(OptionsSource));
    
    public IEnumerable<IAutoCompleteOption>? OptionsSource
    {
        get => GetValue(OptionsSourceProperty);
        set => SetValue(OptionsSourceProperty, value);
    }
    
    
    
    public static readonly StyledProperty<IDataTemplate?> OptionTemplateProperty =
        AvaloniaProperty.Register<DataGridAutoCompleteColumn, IDataTemplate?>(nameof(OptionTemplate));
    
    public IDataTemplate? OptionTemplate
    {
        get => GetValue(OptionTemplateProperty);
        set => SetValue(OptionTemplateProperty, value);
    }

    public DataGridAutoCompleteColumn()
    {
        BindingTarget = AbstractAutoComplete.ValueProperty;
    }

    public override Control GenerateElement(DataGridCell cell, object? dataItem)
    {
        var textBlock = new TextBlock
        {
            VerticalAlignment = VerticalAlignment.Center,
            Margin = new Thickness(4, 0),
            Foreground = DataGridColumn.DefaultCellTextForeground,
        };

        if (Binding != null && dataItem != null)
        {
            var binding = CloneBinding(Binding);
            if (binding is Binding dataBinding) dataBinding.Mode = BindingMode.OneWay;
            textBlock.Bind(TextBlock.TextProperty, binding);
        }
        else if (dataItem != null)
        {
            textBlock.Text = dataItem.ToString();
        }

        return textBlock;
    }

    protected override Control? GenerateEditingElementDirect(DataGridCell cell, object? dataItem)
    {
        var autoComplete = new DataGridAutoComplete()
        {
            HorizontalAlignment = HorizontalAlignment.Stretch,
            VerticalAlignment = VerticalAlignment.Stretch,
            Margin = new Thickness(0),
            IsPopupMatchSelectWidth = true,
            MinimumPrefixLength = 0,
            ShouldUseOverlayPopup = true
        };
        
        if (OptionsAsyncLoader is null)
        {
            autoComplete.Filter = ValueFilterFactory.BuildFilter(ValueFilterMode.Contains);
            autoComplete.FilterValueSelector = ValueFilterPropertySelector;
        }
        else
        {
            autoComplete.Filter = PassThroughFilter;
        }

        BindUtils.RelayBind(this, OptionsAsyncLoaderProperty, autoComplete, AbstractAutoComplete.OptionsAsyncLoaderProperty);
        BindUtils.RelayBind(this, OptionsSourceProperty, autoComplete, AbstractAutoComplete.OptionsSourceProperty);
        BindUtils.RelayBind(this, OptionTemplateProperty, autoComplete, AbstractAutoComplete.OptionTemplateProperty);

        return autoComplete;
    }

    public override Control? GenerateEditingElement(DataGridCell cell, object? dataItem)
    {
        var element = GenerateEditingElementDirect(cell, dataItem);
        if (element is not AtomAutoComplete autoComplete || Binding == null || dataItem == null)
            return element;

        var binding = CloneBinding(Binding);
        if (binding is Binding valueBinding) valueBinding.Mode = BindingMode.OneWay;
        autoComplete.Bind(AbstractAutoComplete.ValueProperty, binding);
        return autoComplete;
    }

    public override object? PrepareCellForEdit(Control editingElement, RoutedEventArgs? editingEventArgs)
    {
        if (editingElement is not AtomAutoComplete autoComplete)
            return null;

        var originalValue = autoComplete.GetValue(AbstractAutoComplete.ValueProperty);

        void OpenDropDown()
        {
            if (autoComplete.IsKeyboardFocusWithin)
                autoComplete.IsDropDownOpen = true;
        }

        void OpenAfterPopulate(object? _, CompletePopulatedEventArgs __)
        {
            autoComplete.Populated -= OpenAfterPopulate;
            Avalonia.Threading.Dispatcher.UIThread.Post(OpenDropDown,
                Avalonia.Threading.DispatcherPriority.Background);
        }

        autoComplete.Populated += OpenAfterPopulate;
        Avalonia.Threading.Dispatcher.UIThread.Post(OpenDropDown,
            Avalonia.Threading.DispatcherPriority.Background);
        return originalValue;
    }

    public override void CancelCellEdit(Control editingElement, object? uneditedValue)
    {
        if (editingElement is AtomAutoComplete autoComplete)
            autoComplete.SetValue(AbstractAutoComplete.ValueProperty, uneditedValue);
    }

    public override object? CommitCellEdit(Control editingElement)
    {
        return editingElement is AtomAutoComplete autoComplete ? autoComplete.Value : null;
    }

    private static readonly DefaultFilterValueSelector ValueFilterPropertySelector = data =>
    {
        if (data is IAutoCompleteOption option)
        {
            return option.Header;
        }
        return null;
    };

    private static readonly IValueFilter PassThroughFilter = new PassThroughValueFilter();

    private sealed class PassThroughValueFilter : IValueFilter
    {
        public ValueFilterMode Mode => ValueFilterMode.Custom;

        public bool Filter(object? value, object? filterValue) => true;
    }

    private sealed class DataGridAutoComplete : AtomAutoComplete
    {
        protected override System.Type StyleKeyOverride => typeof(AtomAutoComplete);

        protected override void OnInitialized()
        {
            base.OnInitialized();
            if (OptionsAsyncLoader != null)
                Filter = PassThroughFilter;
        }

        protected override void OnApplyTemplate(TemplateAppliedEventArgs e)
        {
            base.OnApplyTemplate(e);
            var textBox = e.NameScope.Find<Avalonia.Controls.TextBox>("PART_TextBox");
            if (textBox == null)
                return;

            Avalonia.Threading.Dispatcher.UIThread.Post(() =>
            {
                textBox.Focus();
                textBox.CaretIndex = textBox.Text?.Length ?? 0;
            }, Avalonia.Threading.DispatcherPriority.Input);
        }
    }
    
}
