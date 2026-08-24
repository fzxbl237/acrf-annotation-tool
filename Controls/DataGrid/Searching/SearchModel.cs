using System;
using System.Collections.Generic;
using System.ComponentModel;
using System.Linq;
using System.Diagnostics;
using System.Globalization;

namespace PatChes.Controls.DataGrid.Searching;

public enum SearchMatchMode { Contains, StartsWith, EndsWith, Equals, Regex, Wildcard }
public enum SearchTermCombineMode { Any, All }
public enum SearchScope { AllColumns, VisibleColumns, ExplicitColumns }
public enum SearchHighlightMode { None, Cell, TextAndCell }

public sealed class SearchDescriptor : IEquatable<SearchDescriptor>
{
    public SearchDescriptor(string query, SearchMatchMode matchMode = SearchMatchMode.Contains,
        SearchTermCombineMode termMode = SearchTermCombineMode.Any,
        SearchScope scope = SearchScope.AllColumns, IReadOnlyList<string>? columnNames = null,
        StringComparison? comparison = null, CultureInfo? culture = null,
        bool wholeWord = false, bool normalizeWhitespace = true, bool ignoreDiacritics = false, bool allowEmpty = false)
    {
        Query = query ?? string.Empty; MatchMode = matchMode; TermMode = termMode; Scope = scope;
        ColumnNames = columnNames; Comparison = comparison; Culture = culture; WholeWord = wholeWord;
        NormalizeWhitespace = normalizeWhitespace; IgnoreDiacritics = ignoreDiacritics; AllowEmpty = allowEmpty;
    }
    public string Query { get; }
    public SearchMatchMode MatchMode { get; }
    public SearchTermCombineMode TermMode { get; }
    public SearchScope Scope { get; }
    public IReadOnlyList<string>? ColumnNames { get; }
    public StringComparison? Comparison { get; }
    public CultureInfo? Culture { get; }
    public bool WholeWord { get; }
    public bool NormalizeWhitespace { get; }
    public bool IgnoreDiacritics { get; }
    public bool AllowEmpty { get; }
    public override bool Equals(object? obj) => Equals(obj as SearchDescriptor);
    public bool Equals(SearchDescriptor? other)
    {
        if (ReferenceEquals(this, other)) return true;
        if (other is null) return false;
        return string.Equals(Query, other.Query, StringComparison.Ordinal) && MatchMode == other.MatchMode
            && TermMode == other.TermMode && Scope == other.Scope && ColumnNamesEqual(ColumnNames, other.ColumnNames)
            && Comparison == other.Comparison && Equals(Culture, other.Culture) && WholeWord == other.WholeWord
            && NormalizeWhitespace == other.NormalizeWhitespace && IgnoreDiacritics == other.IgnoreDiacritics
            && AllowEmpty == other.AllowEmpty;
    }
    public override int GetHashCode()
    {
        unchecked
        {
            int h = 17;
            h = h * 23 + (Query?.GetHashCode() ?? 0);
            h = h * 23 + MatchMode.GetHashCode();
            h = h * 23 + TermMode.GetHashCode();
            h = h * 23 + Scope.GetHashCode();
            h = h * 23 + (Comparison?.GetHashCode() ?? 0);
            h = h * 23 + (Culture?.GetHashCode() ?? 0);
            h = h * 23 + WholeWord.GetHashCode();
            h = h * 23 + NormalizeWhitespace.GetHashCode();
            h = h * 23 + IgnoreDiacritics.GetHashCode();
            h = h * 23 + AllowEmpty.GetHashCode();
            if (ColumnNames != null)
                foreach (var columnName in ColumnNames)
                    h = h * 23 + (columnName?.GetHashCode() ?? 0);
            return h;
        }
    }

    private static bool ColumnNamesEqual(IReadOnlyList<string>? left, IReadOnlyList<string>? right)
    {
        if (ReferenceEquals(left, right)) return true;
        if (left == null || right == null || left.Count != right.Count) return false;
        for (var i = 0; i < left.Count; i++)
            if (!string.Equals(left[i], right[i], StringComparison.Ordinal)) return false;
        return true;
    }
}

public sealed class SearchMatch
{
    public SearchMatch(int start, int length) { Start = start; Length = length; }
    public int Start { get; }
    public int Length { get; }
}

public sealed class SearchResult
{
    public SearchResult(object item, int rowIndex, string columnName, int columnIndex, string text, IReadOnlyList<SearchMatch> matches)
    { Item = item; RowIndex = rowIndex; ColumnName = columnName; ColumnIndex = columnIndex; Text = text; Matches = matches ?? Array.Empty<SearchMatch>(); }
    public object Item { get; }
    public int RowIndex { get; }
    public string ColumnName { get; }
    public int ColumnIndex { get; }
    public string Text { get; }
    public IReadOnlyList<SearchMatch> Matches { get; }
}

public class SearchChangedEventArgs : EventArgs
{
    public SearchChangedEventArgs(IReadOnlyList<SearchDescriptor> oldD, IReadOnlyList<SearchDescriptor> newD)
    { OldDescriptors = oldD ?? Array.Empty<SearchDescriptor>(); NewDescriptors = newD ?? Array.Empty<SearchDescriptor>(); }
    public IReadOnlyList<SearchDescriptor> OldDescriptors { get; }
    public IReadOnlyList<SearchDescriptor> NewDescriptors { get; }
}

public class SearchResultsChangedEventArgs : EventArgs
{
    public SearchResultsChangedEventArgs(IReadOnlyList<SearchResult> oldR, IReadOnlyList<SearchResult> newR)
    { OldResults = oldR ?? Array.Empty<SearchResult>(); NewResults = newR ?? Array.Empty<SearchResult>(); }
    public IReadOnlyList<SearchResult> OldResults { get; }
    public IReadOnlyList<SearchResult> NewResults { get; }
}

public class SearchCurrentChangedEventArgs : EventArgs
{
    public SearchCurrentChangedEventArgs(int oldIdx, int newIdx, SearchResult? oldR, SearchResult? newR)
    { OldIndex = oldIdx; NewIndex = newIdx; OldResult = oldR; NewResult = newR; }
    public int OldIndex { get; }
    public int NewIndex { get; }
    public SearchResult? OldResult { get; }
    public SearchResult? NewResult { get; }
}

public interface ISearchModel : INotifyPropertyChanged
{
    IReadOnlyList<SearchDescriptor> Descriptors { get; }
    IReadOnlyList<SearchResult> Results { get; }
    SearchHighlightMode HighlightMode { get; set; }
    bool HighlightCurrent { get; set; }
    bool WrapNavigation { get; set; }
    int CurrentIndex { get; }
    SearchResult? CurrentResult { get; }
    event EventHandler<SearchChangedEventArgs>? SearchChanged;
    event EventHandler<SearchResultsChangedEventArgs>? ResultsChanged;
    event EventHandler<SearchCurrentChangedEventArgs>? CurrentChanged;
    void Apply(IEnumerable<SearchDescriptor> descriptors);
    void Clear();
    void UpdateResults(IReadOnlyList<SearchResult> results);
    bool MoveNext();
    bool MovePrevious();
    void BeginUpdate();
    void EndUpdate();
    IDisposable DeferRefresh();
}

public sealed class SearchModel : ISearchModel
{
    private readonly List<SearchDescriptor> _descriptors = new();
    private readonly List<SearchResult> _results = new();
    private readonly IReadOnlyList<SearchDescriptor> _readOnlyDescriptors;
    private readonly IReadOnlyList<SearchResult> _readOnlyResults;
    private int _currentIndex = -1;
    private int _updateNesting;
    private bool _hasPendingChange;
    private List<SearchDescriptor>? _pendingOld;

    public SearchModel()
    {
        HighlightMode = SearchHighlightMode.Cell; HighlightCurrent = true; WrapNavigation = true;
        _readOnlyDescriptors = _descriptors.AsReadOnly(); _readOnlyResults = _results.AsReadOnly();
    }
    public IReadOnlyList<SearchDescriptor> Descriptors => _readOnlyDescriptors;
    public IReadOnlyList<SearchResult> Results => _readOnlyResults;
    private SearchHighlightMode _highlightMode; private bool _highlightCurrent; private bool _wrapNavigation;
    public SearchHighlightMode HighlightMode { get => _highlightMode; set { if (_highlightMode != value) { _highlightMode = value; PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(nameof(HighlightMode))); } } }
    public bool HighlightCurrent { get => _highlightCurrent; set { if (_highlightCurrent != value) { _highlightCurrent = value; PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(nameof(HighlightCurrent))); } } }
    public bool WrapNavigation { get => _wrapNavigation; set { if (_wrapNavigation != value) { _wrapNavigation = value; PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(nameof(WrapNavigation))); } } }
    public int CurrentIndex => _currentIndex;
    public SearchResult? CurrentResult => _currentIndex >= 0 && _currentIndex < _results.Count ? _results[_currentIndex] : null;
    public event EventHandler<SearchChangedEventArgs>? SearchChanged;
    public event EventHandler<SearchResultsChangedEventArgs>? ResultsChanged;
    public event EventHandler<SearchCurrentChangedEventArgs>? CurrentChanged;
    public event PropertyChangedEventHandler? PropertyChanged;

    public void Apply(IEnumerable<SearchDescriptor> descriptors)
    { if (descriptors == null) throw new ArgumentNullException(nameof(descriptors)); ApplyState(new List<SearchDescriptor>(descriptors)); }

    public void Clear()
    {
        if (_descriptors.Count == 0) { if (_results.Count > 0) UpdateResults(Array.Empty<SearchResult>()); return; }
        ApplyState(new List<SearchDescriptor>());
    }

    public void BeginUpdate() => _updateNesting++;
    public void EndUpdate()
    {
        if (_updateNesting == 0) throw new InvalidOperationException("EndUpdate without BeginUpdate.");
        _updateNesting--;
        if (_updateNesting == 0 && _hasPendingChange)
        { var old = _pendingOld ?? new List<SearchDescriptor>(); _pendingOld = null; _hasPendingChange = false; RaiseSearchChanged(old, new List<SearchDescriptor>(_descriptors)); }
    }
    public IDisposable DeferRefresh() { BeginUpdate(); return new UpdateScope(this); }

    public bool MoveNext()
    {
        if (_results.Count == 0) return false;
        int next = _currentIndex + 1;
        if (next >= _results.Count)
        {
            if (!WrapNavigation) return false;
            next = 0;
        }
        return SetCurrentIndex(next);
    }
    public bool MovePrevious()
    {
        if (_results.Count == 0) return false;
        int previous = _currentIndex - 1;
        if (previous < 0)
        {
            if (!WrapNavigation) return false;
            previous = _results.Count - 1;
        }
        return SetCurrentIndex(previous);
    }

    public void UpdateResults(IReadOnlyList<SearchResult> results)
    {
        var next = results == null ? Array.Empty<SearchResult>() : results.ToArray();
        var oldR = new List<SearchResult>(_results); _results.Clear(); _results.AddRange(next);
        ResultsChanged?.Invoke(this, new SearchResultsChangedEventArgs(oldR, new List<SearchResult>(_results)));
        if (_results.Count == 0) { SetCurrentIndexInternal(-1); return; }
        if (_currentIndex < 0) SetCurrentIndexInternal(0);
        else if (_currentIndex >= _results.Count) SetCurrentIndexInternal(_results.Count - 1);
    }

    private bool SetCurrentIndex(int index) { if (index < 0 || index >= _results.Count) return false; return SetCurrentIndexInternal(index); }
    private bool SetCurrentIndexInternal(int index)
    {
        if (_currentIndex == index) return false;
        var oldIdx = _currentIndex; var oldR = CurrentResult; _currentIndex = index; var newR = CurrentResult;
        CurrentChanged?.Invoke(this, new SearchCurrentChangedEventArgs(oldIdx, _currentIndex, oldR, newR)); return true;
    }

    private void ApplyState(List<SearchDescriptor> next)
    {
        if (SeqEq(_descriptors, next)) return;
        var old = new List<SearchDescriptor>(_descriptors); _descriptors.Clear(); _descriptors.AddRange(next);
        if (_updateNesting > 0) { if (!_hasPendingChange) _pendingOld = old; _hasPendingChange = true; return; }
        RaiseSearchChanged(old, new List<SearchDescriptor>(_descriptors));
    }
    private void RaiseSearchChanged(IReadOnlyList<SearchDescriptor> o, IReadOnlyList<SearchDescriptor> n) => SearchChanged?.Invoke(this, new SearchChangedEventArgs(o, n));
    private static bool SeqEq(List<SearchDescriptor> a, List<SearchDescriptor> b)
    { if (ReferenceEquals(a, b)) return true; if (a.Count != b.Count) return false; for (int i = 0; i < a.Count; i++) if (!Equals(a[i], b[i])) return false; return true; }
    private sealed class UpdateScope : IDisposable { private readonly SearchModel _o; private bool _d; public UpdateScope(SearchModel o) => _o = o; public void Dispose() { if (!_d) { _o.EndUpdate(); _d = true; } } }
}
