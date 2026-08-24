﻿using LineEdit = AtomUI.Desktop.Controls.LineEdit;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Data;
using Avalonia.Input;
using Avalonia.Interactivity;
using Avalonia.Media;

namespace PatChes.Controls.DataGrid;

public class DataGridTextColumn : DataGridBoundColumn
{
    public static readonly StyledProperty<FontWeight> FontWeightProperty =
        AvaloniaProperty.Register<DataGridTextColumn, FontWeight>(nameof(FontWeight), FontWeight.Normal);
    public static readonly StyledProperty<double> FontSizeProperty =
        AvaloniaProperty.Register<DataGridTextColumn, double>(nameof(FontSize), 13);
    public static readonly StyledProperty<IBrush?> ForegroundProperty =
        AvaloniaProperty.Register<DataGridTextColumn, IBrush?>(
            nameof(Foreground), DataGridColumn.DefaultCellTextForeground);
    public static readonly StyledProperty<bool> ShowToolTipProperty =
        AvaloniaProperty.Register<DataGridTextColumn, bool>(nameof(ShowToolTip));
    public static readonly StyledProperty<TextTrimming> TextTrimmingProperty =
        AvaloniaProperty.Register<DataGridTextColumn, TextTrimming>(nameof(TextTrimming), TextTrimming.WordEllipsis);

    public FontWeight FontWeight { get => GetValue(FontWeightProperty); set => SetValue(FontWeightProperty, value); }
    public double FontSize { get => GetValue(FontSizeProperty); set => SetValue(FontSizeProperty, value); }
    public IBrush? Foreground { get => GetValue(ForegroundProperty); set => SetValue(ForegroundProperty, value); }
    public bool ShowToolTip { get => GetValue(ShowToolTipProperty); set => SetValue(ShowToolTipProperty, value); }
    public TextTrimming TextTrimming { get => GetValue(TextTrimmingProperty); set => SetValue(TextTrimmingProperty, value); }

    public DataGridTextColumn() { BindingTarget = TextBox.TextProperty; }

    public override Control GenerateElement(DataGridCell cell, object? dataItem)
    {
        var trimming = this.TextTrimming;
        var tb = new TextBlock
        {
            VerticalAlignment = Avalonia.Layout.VerticalAlignment.Center,
            Margin = new Thickness(4, 0), FontWeight = FontWeight, FontSize = FontSize,
            TextTrimming = trimming,
        };
        if (Foreground != null) tb.Foreground = Foreground;
        if (Binding != null && dataItem != null)
        {
            var b = CloneBinding(Binding);
            if (b is Binding binding) binding.Mode = BindingMode.OneWay;
            tb.Bind(TextBlock.TextProperty, b);
            if (ShowToolTip) ToolTip.SetTip(tb, CreateTextToolTip(b));
        }
        else if (dataItem != null) tb.Text = dataItem.ToString();
        return tb;
    }

    private static ToolTip CreateTextToolTip(BindingBase binding)
    {
        var content = new TextBlock
        {
            TextWrapping = TextWrapping.Wrap,
            MaxWidth = 360,
        };
        content.Bind(TextBlock.TextProperty, CloneBinding(binding));

        return new ToolTip
        {
            Content = content,
            Background = new SolidColorBrush(Colors.White),
            BorderBrush = new SolidColorBrush(Color.Parse("#DCDCDC")),
            BorderThickness = new Thickness(1),
            CornerRadius = new CornerRadius(4),
            Padding = new Thickness(8, 6),
        };
    }

    protected override Control? GenerateEditingElementDirect(DataGridCell cell, object? dataItem)
    {
        return new LineEdit
        {
            VerticalAlignment = Avalonia.Layout.VerticalAlignment.Stretch,
            HorizontalAlignment = Avalonia.Layout.HorizontalAlignment.Stretch,
            Margin = new Thickness(0),
        };
    }

    public override Control? GenerateEditingElement(DataGridCell cell, object? dataItem)
    {
        var element = GenerateEditingElementDirect(cell, dataItem);
        if (element is not LineEdit lineEdit || dataItem == null)
            return element;

        if (!string.IsNullOrWhiteSpace(BindingPath))
        {
            var property = dataItem.GetType().GetProperty(BindingPath);
            lineEdit.Text = property?.GetValue(dataItem)?.ToString() ?? string.Empty;
        }

        return lineEdit;
    }

    public override object? PrepareCellForEdit(Control editingElement, RoutedEventArgs? editingEventArgs)
    {
        if (editingElement is LineEdit lineEdit)
        {
            string uneditedText = lineEdit.Text ?? string.Empty;
            // Focus() is applied after this method and resets the caret; move it to the
            // end once the LineEdit actually receives focus.
            lineEdit.GotFocus += (_, _) => lineEdit.CaretIndex = lineEdit.Text?.Length ?? 0;
            lineEdit.CaretIndex = uneditedText.Length;
            return uneditedText;
        }
        return string.Empty;
    }

    public override void CancelCellEdit(Control editingElement, object? uneditedValue)
    {
        if (editingElement is LineEdit lineEdit)
            lineEdit.Text = uneditedValue as string ?? string.Empty;
    }

    public override object? CommitCellEdit(Control editingElement)
    {
        if (editingElement is LineEdit lineEdit) return lineEdit.Text;
        return null;
    }
}