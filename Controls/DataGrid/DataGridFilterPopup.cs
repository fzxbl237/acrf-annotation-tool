using System;
using System.Collections.Generic;
using System.ComponentModel;
using System.Linq;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Controls.Primitives;
using Avalonia.Input;
using AtomButton = AtomUI.Desktop.Controls.Button;
using AtomCheckBox = AtomUI.Desktop.Controls.CheckBox;
using AtomLineEdit = AtomUI.Desktop.Controls.LineEdit;
using AtomListBox = AtomUI.Desktop.Controls.ListBox;
using AtomComboBox = AtomUI.Desktop.Controls.ComboBox;

namespace PatChes.Controls.DataGrid;

public class DataGridFilterPopup : TemplatedControl
{
    private AtomLineEdit? _searchBox;
    private AtomComboBox? _matchModeBox;
    private AtomListBox? _filterList;
    private FilterMatchMode _matchMode = FilterMatchMode.Contains;
    private AtomCheckBox? _selectAllCheckBox;
    private readonly List<DataGridFilterItem> _items = new();

    public DataGridColumn? Column { get; set; }
    public DataGrid? OwningGrid { get; set; }
    public event EventHandler? FilterApplied;
    private List<string>? _pendingUniqueValues;
    private HashSet<string>? _pendingSelectedValues;
    protected override Type StyleKeyOverride => typeof(DataGridFilterPopup);

    public void FocusSearchBox() => _searchBox?.Focus();

    public void BuildContent(List<string> uniqueValues, HashSet<string>? selectedValues)
    {
        _pendingUniqueValues = uniqueValues;
        _pendingSelectedValues = selectedValues;
        if (_filterList != null) PopulateItems(uniqueValues, selectedValues);
    }

    private void PopulateItems(List<string> uniqueValues, HashSet<string>? selectedValues)
    {
        _items.Clear();
        foreach (var val in uniqueValues.OrderBy(v => v, StringComparer.OrdinalIgnoreCase))
        {
            _items.Add(new DataGridFilterItem
            {
                Value = val,
                DisplayText = string.IsNullOrEmpty(val) ? "(空白)" : val,
                IsChecked = selectedValues == null || selectedValues.Count == 0 || selectedValues.Contains(val),
            });
        }
        RenderMatchingItems(GetSearchText());
        SyncSelectAll();
    }

    protected override void OnApplyTemplate(TemplateAppliedEventArgs e)
    {
        base.OnApplyTemplate(e);
        _searchBox = e.NameScope.Find<AtomLineEdit>("PART_SearchBox");
        _matchModeBox = e.NameScope.Find<AtomComboBox>("PART_MatchModeBox");
        _filterList = e.NameScope.Find<AtomListBox>("PART_FilterList");
        if (_matchModeBox != null)
        {
            _matchModeBox.SelectedIndex = (int)_matchMode;
            _matchModeBox.SelectionChanged += OnMatchModeChanged;
        }
        _selectAllCheckBox = e.NameScope.Find<AtomCheckBox>("PART_SelectAllCheckBox");
        if (_searchBox != null) _searchBox.TextChanged += (_, _) => DoSearch();
        if (_filterList != null)
        {
            _filterList.ItemClicked -= OnFilterItemClicked;
            _filterList.ItemClicked += OnFilterItemClicked;
        }
        if (_selectAllCheckBox != null)
        {
            _selectAllCheckBox.AddHandler(PointerPressedEvent, (_, e) =>
            {
                if (!e.GetCurrentPoint(_selectAllCheckBox).Properties.IsLeftButtonPressed) return;
                var visibleItems = GetVisibleItems().ToList();
                var allChecked = visibleItems.Count > 0 && visibleItems.All(item => item.IsChecked);
                SetSelectAll(!allChecked);
                e.Handled = true;
            }, Avalonia.Interactivity.RoutingStrategies.Tunnel);
        }
        var sortAsc = e.NameScope.Find<AtomButton>("PART_SortAsc");
        var sortDesc = e.NameScope.Find<AtomButton>("PART_SortDesc");
        if (sortAsc != null) sortAsc.Click += (_, _) => SortItems(true);
        if (sortDesc != null) sortDesc.Click += (_, _) => SortItems(false);
        var clearBtn = e.NameScope.Find<AtomButton>("PART_ClearBtn");
        var okBtn = e.NameScope.Find<AtomButton>("PART_OkBtn");
        if (clearBtn != null) clearBtn.Click += (_, _) =>
        {
            Column?.ClearFilter();
            FilterApplied?.Invoke(this, EventArgs.Empty);
        };
        if (okBtn != null) okBtn.Click += (_, _) =>
        {
            if (Column == null) return;
            var searchText = GetSearchText();
            var selected = GetVisibleItems(searchText)
                .Where(i => i.IsChecked)
                .Select(i => i.Value)
                .ToHashSet(StringComparer.OrdinalIgnoreCase);

            if (selected.Count == 0 ||
                (string.IsNullOrEmpty(searchText) && selected.Count == _items.Count))
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

    private void OnFilterItemClicked(object? sender, AtomUI.Desktop.Controls.ListBoxItemClickedEventArgs e)
    {
        if (e.Item.Content is not DataGridFilterItem item) return;
        item.IsChecked = !item.IsChecked;
        SyncSelectAll();
    }

    private void SetSelectAll(bool val)
    {
        foreach (var item in GetVisibleItems())
            item.IsChecked = val;
        SyncSelectAll();
    }

    private void SyncSelectAll()
    {
        var visibleItems = GetVisibleItems().ToList();
        bool allChecked = visibleItems.Count > 0 && visibleItems.All(i => i.IsChecked);
        bool noneChecked = visibleItems.Count == 0 || visibleItems.All(i => !i.IsChecked);
        if (_selectAllCheckBox != null)
            _selectAllCheckBox.IsChecked = allChecked ? true : noneChecked ? false : null;
    }

    private void OnMatchModeChanged(object? sender, SelectionChangedEventArgs e)
    {
        if (sender is not AtomComboBox comboBox || comboBox.SelectedIndex < 0) return;
        _matchMode = (FilterMatchMode)comboBox.SelectedIndex;
        DoSearch();
    }

    private void DoSearch()
    {
        if (_filterList == null) return;

        var searchText = GetSearchText();
        foreach (var item in GetVisibleItems(searchText))
            item.IsChecked = true;

        RenderMatchingItems(searchText);
        SyncSelectAll();
    }

    private string GetSearchText() => _searchBox?.Text?.Trim() ?? "";

    private IEnumerable<DataGridFilterItem> GetVisibleItems(string? searchText = null)
    {
        searchText ??= GetSearchText();
        if (string.IsNullOrEmpty(searchText)) return _items;

        return _items.Where(item => Matches(item.Value, searchText));
    }

    private bool Matches(string value, string searchText)
    {
        var comparison = StringComparison.OrdinalIgnoreCase;
        return _matchMode switch
        {
            FilterMatchMode.StartsWith => value.StartsWith(searchText, comparison),
            FilterMatchMode.EndsWith => value.EndsWith(searchText, comparison),
            FilterMatchMode.NotStartsWith => !value.StartsWith(searchText, comparison),
            FilterMatchMode.NotEndsWith => !value.EndsWith(searchText, comparison),
            FilterMatchMode.NotContains => !value.Contains(searchText, comparison),
            _ => value.Contains(searchText, comparison),
        };
    }

    private void RenderMatchingItems(string text)
    {
        if (_filterList == null) return;
        _filterList.ItemsSource = GetVisibleItems(text).ToList();
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

}

internal enum FilterMatchMode
{
    Contains,
    StartsWith,
    EndsWith,
    NotContains,
    NotStartsWith,
    NotEndsWith,
}

internal sealed class DataGridFilterItem : INotifyPropertyChanged
{
    private bool _isChecked = true;

    public string Value { get; init; } = "";
    public string DisplayText { get; init; } = "";

    public bool IsChecked
    {
        get => _isChecked;
        set
        {
            if (_isChecked == value) return;
            _isChecked = value;
            PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(nameof(IsChecked)));
        }
    }

    public event PropertyChangedEventHandler? PropertyChanged;
}
