using System;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Controls.Primitives;
using Avalonia.Controls.Templates;
using Avalonia.Input;
using Avalonia.Layout;
using Avalonia.Threading;

namespace PatChes.Controls.DataGrid;

/// <summary>
/// Provides a hover-activated floating action bar for a DataGridColumn.
/// Ported from ProDataGrid's DataGridColumnPopupBar, renamed to FloatingBar.
/// </summary>
public static class FloatingBar
{
    public static readonly AttachedProperty<IDataTemplate?> FloatingBarTemplateProperty =
        AvaloniaProperty.RegisterAttached<DataGridColumn, IDataTemplate?>(
            "FloatingBarTemplate",
            typeof(FloatingBar));

    public static IDataTemplate? GetFloatingBarTemplate(AvaloniaObject column)
    {
        return column.GetValue(FloatingBarTemplateProperty);
    }

    public static void SetFloatingBarTemplate(AvaloniaObject column, IDataTemplate? value)
    {
        column.SetValue(FloatingBarTemplateProperty, value);
    }

    internal static Control Build(DataGridColumn column, DataGridCell cell, Control cellContent, object row)
    {
        var template = GetFloatingBarTemplate(column);
        if (template is null)
            return cellContent;

        var host = new FloatingBarHost(row, template, cell)
        {
            HorizontalAlignment = HorizontalAlignment.Stretch,
            VerticalAlignment = VerticalAlignment.Stretch
        };

        host.Children.Add(new Border
        {
            HorizontalAlignment = HorizontalAlignment.Stretch,
            VerticalAlignment = VerticalAlignment.Stretch,
            Child = cellContent
        });

        return host;
    }
}

internal sealed class FloatingBarHost : Panel
{
    private const int HideDelayMilliseconds = 250;
    private const int RowOverlapPixels = 6;
    private static FloatingBarHost? s_activeHost;

    private readonly object _row;
    private readonly IDataTemplate _template;
    private readonly DataGridCell _cell;
    private readonly DispatcherTimer _hideTimer;
    private Popup? _popup;
    private bool _pointerOverHost;
    private bool _pointerOverPopup;

    public FloatingBarHost(object row, IDataTemplate template, DataGridCell cell)
    {
        _row = row;
        _template = template;
        _cell = cell;
        _hideTimer = new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(HideDelayMilliseconds) };
        _hideTimer.Tick += OnHideTimerTick;
    }

    protected override void OnAttachedToVisualTree(VisualTreeAttachmentEventArgs e)
    {
        base.OnAttachedToVisualTree(e);
        _cell.PointerEntered += OnCellPointerEntered;
        _cell.PointerExited += OnCellPointerExited;
        EnsurePopup();
    }

    protected override void OnDetachedFromVisualTree(VisualTreeAttachmentEventArgs e)
    {
        _cell.PointerEntered -= OnCellPointerEntered;
        _cell.PointerExited -= OnCellPointerExited;
        ForceClose();
        base.OnDetachedFromVisualTree(e);
    }

    private void OnCellPointerEntered(object? sender, PointerEventArgs e)
    {
        _pointerOverHost = true;
        _hideTimer.Stop();
        OpenPopup();
    }

    private void OnCellPointerExited(object? sender, PointerEventArgs e)
    {
        _pointerOverHost = false;
        Dispatcher.UIThread.Post(ScheduleHide, DispatcherPriority.Input);
    }

    private void OnHideTimerTick(object? sender, EventArgs e)
    {
        _hideTimer.Stop();
        if (!_pointerOverHost && !_pointerOverPopup)
            ClosePopup();
    }

    private void EnsurePopup()
    {
        if (_popup is not null)
            return;

        var content = _template.Build(_row) as Control;
        if (content is null)
            return;

        WirePopupPointerHandlers(content);
        _popup = new Popup
        {
            PlacementTarget = this,
            Placement = PlacementMode.Top,
            VerticalOffset = RowOverlapPixels,
            IsLightDismissEnabled = false,
            ShouldUseOverlayLayer = true,
            Child = content
        };
        WirePopupPointerHandlers(_popup);
        Children.Add(_popup);
    }

    private void OpenPopup()
    {
        EnsurePopup();
        if (_popup is null)
            return;

        if (s_activeHost is not null && !ReferenceEquals(s_activeHost, this))
            s_activeHost.ForceClose();

        s_activeHost = this;
        _popup.IsOpen = true;
    }

    private void ClosePopup()
    {
        if (_popup is not null)
            _popup.IsOpen = false;

        if (ReferenceEquals(s_activeHost, this))
            s_activeHost = null;
    }

    private void ForceClose()
    {
        _hideTimer.Stop();
        _pointerOverHost = false;
        _pointerOverPopup = false;
        ClosePopup();
    }

    private void ScheduleHide()
    {
        _hideTimer.Stop();
        if (!_pointerOverHost && !_pointerOverPopup)
            _hideTimer.Start();
    }

    private void WirePopupPointerHandlers(InputElement element)
    {
        element.PointerEntered += OnPopupPointerEntered;
        element.PointerExited += OnPopupPointerExited;
    }

    private void OnPopupPointerEntered(object? sender, PointerEventArgs e)
    {
        _pointerOverPopup = true;
        _hideTimer.Stop();
    }

    private void OnPopupPointerExited(object? sender, PointerEventArgs e)
    {
        _pointerOverPopup = false;
        Dispatcher.UIThread.Post(ScheduleHide, DispatcherPriority.Input);
    }
}