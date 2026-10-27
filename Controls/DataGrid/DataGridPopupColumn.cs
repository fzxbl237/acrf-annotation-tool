using AtomUI;
using AtomUI.Controls;
using AtomUI.Desktop.Controls;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Data;
using Avalonia.Input;
using Avalonia.Interactivity;
using Avalonia.Media;
using Button = Avalonia.Controls.Button;
using AtomFlyout = AtomUI.Desktop.Controls.Flyout;
using AtomTextArea = AtomUI.Desktop.Controls.TextArea;
using TextBlock = Avalonia.Controls.TextBlock;
using TextBox = Avalonia.Controls.TextBox;

namespace PatChes.Controls.DataGrid;

public class DataGridPopupColumn : DataGridBoundColumn
{
    public static readonly StyledProperty<double> FontSizeProperty =
        AvaloniaProperty.Register<DataGridPopupColumn, double>(nameof(FontSize), 13);
    public static readonly StyledProperty<string> TitleProperty =
        AvaloniaProperty.Register<DataGridPopupColumn, string>(nameof(Title), "Edit");
    public static readonly StyledProperty<double> PopupWidthProperty =
        AvaloniaProperty.Register<DataGridPopupColumn, double>(nameof(PopupWidth), 300);
    public static readonly StyledProperty<double> PopupMaxHeightProperty =
        AvaloniaProperty.Register<DataGridPopupColumn, double>(nameof(PopupMaxHeight), 400);
    public static readonly StyledProperty<string> PlaceholderProperty =
        AvaloniaProperty.Register<DataGridPopupColumn, string>(nameof(Placeholder), "Please enter text...");
    public static readonly StyledProperty<string?> ValidationMessageProperty =
        AvaloniaProperty.Register<DataGridPopupColumn, string?>(nameof(ValidationMessage));

    public double FontSize { get => GetValue(FontSizeProperty); set => SetValue(FontSizeProperty, value); }
    public string Title { get => GetValue(TitleProperty); set => SetValue(TitleProperty, value); }
    public double PopupWidth { get => GetValue(PopupWidthProperty); set => SetValue(PopupWidthProperty, value); }
    public double PopupMaxHeight { get => GetValue(PopupMaxHeightProperty); set => SetValue(PopupMaxHeightProperty, value); }
    public string Placeholder { get => GetValue(PlaceholderProperty); set => SetValue(PlaceholderProperty, value); }
    public string? ValidationMessage { get => GetValue(ValidationMessageProperty); set => SetValue(ValidationMessageProperty, value); }

    public DataGridPopupColumn() { BindingTarget = Panel.TagProperty; }

    private DataGridCell? _pendingCell;
    private AtomFlyout? _activeFlyout;

    public override Control GenerateElement(DataGridCell cell, object? dataItem)
    {
        var tb = new TextBlock
        {
            VerticalAlignment = Avalonia.Layout.VerticalAlignment.Center,
            Margin = new Thickness(4, 0),
            FontSize = FontSize,
            Foreground = DataGridColumn.DefaultCellTextForeground,
        };
        if (Binding != null && dataItem != null)
        {
            var b = CloneBinding(Binding, BindingMode.OneWay);
            tb.Bind(TextBlock.TextProperty, b);
        }
        else if (dataItem != null) tb.Text = dataItem.ToString();
        return tb;
    }

    protected override Control? GenerateEditingElementDirect(DataGridCell cell, object? dataItem)
    {
        _pendingCell = cell;

        var panel = new Panel
        {
            Background = new SolidColorBrush(Color.Parse("#EAF4FF")),
        };
        panel.Children.Add(new TextBlock
        {
            Text = "Editing...",
            Foreground = new SolidColorBrush(Color.Parse("#A6A6A6")),
            FontSize = 12,
            Margin = new Thickness(4, 0),
            VerticalAlignment = Avalonia.Layout.VerticalAlignment.Center,
        });
        return panel;
    }

    public override object? PrepareCellForEdit(Control editingElement, RoutedEventArgs? editingEventArgs)
    {
        if (editingElement is not Panel panel) return null;

        var cell = _pendingCell;
        _pendingCell = null;
        var originalValue = panel.Tag;

        var editTextBox = new AtomTextArea
        {
            PlaceholderText = Placeholder,
            MinHeight = 36,
            FontSize = FontSize,
            Lines = 5,
            Margin = new Thickness(0, 0, 0, 4),
        };
        editTextBox[!TextBox.TextProperty] = panel[!Panel.TagProperty];

        var validationText = new TextBlock
        {
            Foreground = new SolidColorBrush(Color.Parse("#E81123")),
            FontSize = 12,
            Margin = new Thickness(0, 0, 0, 8),
            IsVisible = false,
            TextWrapping = TextWrapping.Wrap,
        };

        if (ValidationMessage != null)
        {
            validationText.Text = ValidationMessage;
            validationText.IsVisible = true;
        }
        
        var cancelButton = new AtomUI.Desktop.Controls.Button
        {
            Content = "Cancel",
            ButtonType = ButtonType.Default,
            SizeType = CustomizableSizeType.Small,
            FontSize = 13,
        };

        var saveButton = new AtomUI.Desktop.Controls.Button
        {
            Content = "Save",
            ButtonType = ButtonType.Primary,
            SizeType = CustomizableSizeType.Small,
            FontSize = 13,
        };

        void CommitAndCloseFlyout()
        {
            DataGridOwner?.CommitEdit();
            _activeFlyout?.Hide();
        }

        cancelButton.Click += (_, _) => DataGridOwner?.CancelEdit();

        saveButton.Click += (_, _) => CommitAndCloseFlyout();

        editTextBox.KeyDown += (_, e) =>
        {
            if (e.Key == Key.Escape)
            {
                cancelButton.RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
                e.Handled = true;
            }
            else if (e.Key == Key.Enter && e.KeyModifiers.HasFlag(KeyModifiers.Control))
            {
                CommitAndCloseFlyout();
                e.Handled = true;
            }
        };

        var buttonPanel = new StackPanel
        {
            Orientation = Avalonia.Layout.Orientation.Horizontal,
            HorizontalAlignment = Avalonia.Layout.HorizontalAlignment.Right,
            Spacing = 8,
            Margin = new Thickness(0, 8, 0, 0),
        };
        buttonPanel.Children.Add(cancelButton);
        buttonPanel.Children.Add(saveButton);

        var titleBlock = new TextBlock
        {
            Text = Title,
            FontSize = 15,
            FontWeight = FontWeight.SemiBold,
            Foreground = new SolidColorBrush(Color.Parse("#1A1A1A")),
            Margin = new Thickness(0, 0, 0, 12),
        };

        var stack = new StackPanel
        {
            Spacing = 0,
            Width = PopupWidth,
            MaxHeight = PopupMaxHeight,
            Margin = new Thickness(8),
        };
        stack.Children.Add(titleBlock);
        stack.Children.Add(editTextBox);
        stack.Children.Add(validationText);
        stack.Children.Add(buttonPanel);

        _activeFlyout = new AtomFlyout
        {
            Content = stack,
            RequestedPlacement = PlacementMode.BottomEdgeAlignedLeft,
            IsArrowVisible = false,
            IsLightDismissEnabled = true,
            ShouldUseOverlayPopup = true,
        };
        _activeFlyout.Closed += (_, _) => _activeFlyout = null;

        Avalonia.Threading.Dispatcher.UIThread.Post(() =>
        {
            if (cell != null) _activeFlyout?.ShowAt(cell);
            editTextBox.Focus();
            editTextBox.SelectAll();
        }, Avalonia.Threading.DispatcherPriority.Loaded);

        return originalValue;
    }

    public override void CancelCellEdit(Control editingElement, object? uneditedValue)
    {
        _activeFlyout?.Hide();
    }

    public override object? CommitCellEdit(Control editingElement)
    {
        return (editingElement as Panel)?.Tag;
    }
}
