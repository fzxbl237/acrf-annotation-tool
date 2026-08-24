using System;
using System.Collections.Generic;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Controls.Documents;
using Avalonia.Media;

namespace PatChes.Controls.DataGrid.Searching;

/// <summary>
/// TextBlock that highlights search matches by building Inlines.
/// When no matches exist, falls back to plain Text rendering.
/// </summary>
internal sealed class DataGridSearchTextBlock : TextBlock
{
    public static readonly DirectProperty<DataGridSearchTextBlock, IReadOnlyList<SearchMatch>?> SearchMatchesProperty =
        AvaloniaProperty.RegisterDirect<DataGridSearchTextBlock, IReadOnlyList<SearchMatch>?>(
            nameof(SearchMatches), o => o.SearchMatches, (o, v) => o.SearchMatches = v);

    public static readonly DirectProperty<DataGridSearchTextBlock, bool> IsSearchCurrentProperty =
        AvaloniaProperty.RegisterDirect<DataGridSearchTextBlock, bool>(
            nameof(IsSearchCurrent), o => o.IsSearchCurrent, (o, v) => o.IsSearchCurrent = v);

    private IReadOnlyList<SearchMatch>? _searchMatches;
    private bool _isSearchCurrent;
    private bool _inlinesActive;

    public IReadOnlyList<SearchMatch>? SearchMatches
    {
        get => _searchMatches;
        set
        {
            if (!ReferenceEquals(_searchMatches, value))
            {
                SetAndRaise(SearchMatchesProperty, ref _searchMatches, value);
                RebuildDisplay();
            }
        }
    }

    public bool IsSearchCurrent
    {
        get => _isSearchCurrent;
        set
        {
            if (_isSearchCurrent != value)
            {
                SetAndRaise(IsSearchCurrentProperty, ref _isSearchCurrent, value);
                RebuildDisplay();
            }
        }
    }

    protected override void OnPropertyChanged(AvaloniaPropertyChangedEventArgs change)
    {
        base.OnPropertyChanged(change);
        if (change.Property == TextProperty)
            RebuildDisplay();
    }

    private void RebuildDisplay()
    {
        var text = Text;
        bool hasMatches = SearchMatches is { Count: > 0 };

        if (!hasMatches)
        {
            if (_inlinesActive)
            {
                Inlines?.Clear();
                _inlinesActive = false;
            }
            return;
        }

        if (string.IsNullOrEmpty(text)) return;

        var inlines = Inlines;
        if (inlines == null) return;

        inlines.Clear();
        _inlinesActive = true;

        var matchBrush = new SolidColorBrush(Color.Parse("#FFC107")) { Opacity = 0.4 };
        var currentBrush = new SolidColorBrush(Color.Parse("#FF9800")) { Opacity = 0.5 };
        var highlightBrush = IsSearchCurrent ? currentBrush : matchBrush;

        int last = 0;
        foreach (var m in SearchMatches!)
        {
            if (m.Length <= 0 || m.Start >= text.Length) continue;
            var len = Math.Min(m.Length, text.Length - m.Start);
            if (m.Start > last)
                inlines.Add(new Run(text.Substring(last, m.Start - last)));
            var run = new Run(text.Substring(m.Start, len)) { Background = highlightBrush };
            inlines.Add(run);
            last = m.Start + len;
        }
        if (last < text.Length)
            inlines.Add(new Run(text.Substring(last)));
    }
}
