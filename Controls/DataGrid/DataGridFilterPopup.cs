using System;
using System.Collections.Generic;
using System.Linq;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Controls.Primitives;
using Avalonia.Input;
using Avalonia.Layout;
using Avalonia.Media;
using AtomButton = AtomUI.Desktop.Controls.Button;
using AtomCheckBox = AtomUI.Desktop.Controls.CheckBox;
using AtomLineEdit = AtomUI.Desktop.Controls.LineEdit;
using AtomListBox = AtomUI.Desktop.Controls.ListBox;
using AtomListBoxItem = AtomUI.Desktop.Controls.ListBoxItem;

namespace PatChes.Controls.DataGrid;

public class DataGridFilterPopup : TemplatedControl
{
    private AtomLineEdit? _searchBox;
    private AtomListBox? _filterList;
    private AtomCheckBox? _selectAllCheckBox;
    private readonly List<FilterItem> _items = new();
    private static readonly IBrush TextPrimary = new SolidColorBrush(Color.Parse("#1E293B"));

    public DataGridColumn? Column { get; set; }
    public DataGrid? OwningGrid { get; set; }
    public event EventHandler? FilterApplied;
    private List<string>? _pendingUniqueValues;
    private HashSet<string>? _pendingSelectedValues;
    private bool _hasInitialFilter;
    protected override Type StyleKeyOverride => typeof(DataGridFilterPopup);

    public void FocusSearchBox() => _searchBox?.Focus();

    public void BuildContent(List<string> uniqueValues, HashSet<string>? selectedValues)
    {
        _pendingUniqueValues = uniqueValues;
        _pendingSelectedValues = selectedValues;
        _hasInitialFilter = selectedValues != null;
        if (_filterList != null) PopulateItems(uniqueValues, selectedValues);
    }

    private void PopulateItems(List<string> uniqueValues, HashSet<string>? selectedValues)
    {
        _items.Clear();
        _filterList!.Items.Clear();
        var sorted = uniqueValues.OrderBy(v => v, StringComparer.OrdinalIgnoreCase).ToList();
        foreach (var val in sorted)
        {
            bool isChecked = selectedValues == null || selectedValues.Count == 0 || selectedValues.Contains(val);
            var displayText = string.IsNullOrEmpty(val) ? "(\u7A7A\u767D)" : val;
            var item = new FilterItem { Value = val, IsChecked = isChecked };
            item.ListItem = CreateListBoxItem(item, displayText);
            _items.Add(item);
            _filterList!.Items.Add(item.ListItem);
        }
        SyncSelectAll();
    }

    private AtomListBoxItem CreateListBoxItem(FilterItem item, string displayText)
    {
        var checkBox = new AtomCheckBox
        {
            IsChecked = item.IsChecked,
            IsHitTestVisible = false,
            VerticalAlignment = VerticalAlignment.Center,
        };
        var panel = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 6, Margin = new Thickness(4, 0) };
        panel.Children.Add(checkBox);
        panel.Children.Add(new TextBlock
        {
            Text = displayText,
            FontSize = 13,
            TextWrapping = TextWrapping.NoWrap,
            VerticalAlignment = VerticalAlignment.Center,
            Foreground = TextPrimary,
        });
        var container = new AtomListBoxItem
        {
            Content = panel,
            Padding = new Thickness(6, 3),
            Margin = new Thickness(0, 0),
            Cursor = new Cursor(StandardCursorType.Hand),
        };
        item.CheckBox = checkBox;
        container.AddHandler(PointerPressedEvent, (_, e) =>
        {
            if (!e.GetCurrentPoint(container).Properties.IsLeftButtonPressed) return;
            item.IsChecked = !item.IsChecked;
            UpdateItemVisual(item);
            SyncSelectAll();
            e.Handled = true;
        }, Avalonia.Interactivity.RoutingStrategies.Tunnel);
        return container;
    }

    protected override void OnApplyTemplate(TemplateAppliedEventArgs e)
    {
        base.OnApplyTemplate(e);
        _searchBox = e.NameScope.Find<AtomLineEdit>("PART_SearchBox");
        _filterList = e.NameScope.Find<AtomListBox>("PART_FilterList");
        _selectAllCheckBox = e.NameScope.Find<AtomCheckBox>("PART_SelectAllCheckBox");
        if (_searchBox != null) _searchBox.TextChanged += (_, _) => DoSearch();
        if (_selectAllCheckBox != null) _selectAllCheckBox.PointerPressed += (_, e) =>
        {
            var visibleItems = GetVisibleItems().ToList();
            bool allChecked = visibleItems.Count > 0 && visibleItems.All(i => i.IsChecked);
            SetSelectAll(!allChecked);
            e.Handled = true;
        };
        var sortAsc = e.NameScope.Find<AtomButton>("PART_SortAsc");
        var sortDesc = e.NameScope.Find<AtomButton>("PART_SortDesc");
        if (sortAsc != null) sortAsc.Click += (_, _) => SortItems(true);
        if (sortDesc != null) sortDesc.Click += (_, _) => SortItems(false);
        var clearBtn = e.NameScope.Find<AtomButton>("PART_ClearBtn");
        var okBtn = e.NameScope.Find<AtomButton>("PART_OkBtn");
        if (clearBtn != null) clearBtn.Click += (_, _) =>
        {
            SetSelectAll(true);
            Column?.ClearFilter();
            FilterApplied?.Invoke(this, EventArgs.Empty);
        };
        if (okBtn != null) okBtn.Click += (_, _) =>
        {
            if (Column == null) return;
            var searchText = GetSearchText();
            var visibleItems = GetVisibleItems(searchText).ToList();
            var selected = visibleItems
                .Where(i => i.IsChecked)
                .Select(i => i.Value)
                .ToHashSet(StringComparer.OrdinalIgnoreCase);

            if (!string.IsNullOrEmpty(searchText))
            {
                if (_hasInitialFilter)
                {
                    foreach (var item in _items.Where(i => !visibleItems.Contains(i) && i.IsChecked))
                        selected.Add(item.Value);
                }

                Column.SetFilter(selected);
            }
            else if (selected.Count == _items.Count || selected.Count == 0)
            {
                Column.ClearFilter();
            }
            else
            {
                Column.SetFilter(selected);
            }
            FilterApplied?.Invoke(this, EventArgs.Empty);
        };
        if (_pendingUniqueValues != null)
        {
            PopulateItems(_pendingUniqueValues, _pendingSelectedValues);
            _pendingUniqueValues = null;
        }
    }

    private void SetSelectAll(bool val)
    {
        foreach (var item in GetVisibleItems())
        {
            item.IsChecked = val;
            UpdateItemVisual(item);
        }
        SyncSelectAll();
    }

    private static void UpdateItemVisual(FilterItem item)
    {
        if (item.CheckBox != null && item.CheckBox.IsChecked != item.IsChecked)
            item.CheckBox.IsChecked = item.IsChecked;
    }

    private void SyncSelectAll()
    {
        var visibleItems = GetVisibleItems().ToList();
        bool allChecked = visibleItems.Count > 0 && visibleItems.All(i => i.IsChecked);
        bool noneChecked = visibleItems.Count == 0 || visibleItems.All(i => !i.IsChecked);
        if (_selectAllCheckBox != null)
            _selectAllCheckBox.IsChecked = allChecked ? true : noneChecked ? false : null;
    }

    private void DoSearch()
    {
        if (_filterList == null) return;
        RenderMatchingItems(GetSearchText());
        SyncSelectAll();
    }

    private string GetSearchText() => _searchBox?.Text?.Trim() ?? "";

    private IEnumerable<FilterItem> GetVisibleItems(string? searchText = null)
    {
        searchText ??= GetSearchText();
        if (string.IsNullOrEmpty(searchText)) return _items;

        return _items.Where(item => item.Value.Contains(searchText, StringComparison.OrdinalIgnoreCase));
    }

    private void RenderMatchingItems(string text)
    {
        if (_filterList == null) return;
        _filterList.Items.Clear();
        foreach (var item in GetVisibleItems(text))
        {
            var displayText = string.IsNullOrEmpty(item.Value) ? "(空白)" : item.Value;
            item.ListItem = CreateListBoxItem(item, displayText);
            _filterList.Items.Add(item.ListItem);
        }
    }

    private void SortItems(bool ascending)
    {
        if (_filterList == null || _items.Count == 0) return;
        var text = _searchBox?.Text?.ToLowerInvariant() ?? "";
        var sorted = ascending
            ? _items.OrderBy(i => i.Value, StringComparer.OrdinalIgnoreCase).ToList()
            : _items.OrderByDescending(i => i.Value, StringComparer.OrdinalIgnoreCase).ToList();
        _items.Clear();
        _items.AddRange(sorted);
        RenderMatchingItems(text);
    }

    private class FilterItem
    {
        public string Value { get; set; } = "";
        public bool IsChecked { get; set; } = true;
        public AtomListBoxItem ListItem { get; set; } = null!;
        public AtomCheckBox? CheckBox { get; set; }
    }
}
