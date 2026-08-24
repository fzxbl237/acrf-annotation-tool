using System;
using System.Collections;
using System.Collections.Generic;
using System.ComponentModel;
using System.Diagnostics;
using System.Globalization;
using System.Linq;
using System.Reflection;
using System.Text;
using System.Text.RegularExpressions;

namespace PatChes.Controls.DataGrid.Searching;

public class DataGridSearchAdapter : IDisposable
{
    private readonly ISearchModel _model;
    private readonly DataGrid _grid;
    private bool _isDisposed;

    public DataGridSearchAdapter(ISearchModel model, DataGrid grid)
    {
        _model = model ?? throw new ArgumentNullException(nameof(model));
        _grid = grid ?? throw new ArgumentNullException(nameof(grid));
        _model.SearchChanged += OnModelSearchChanged;
    }

    public void Dispose()
    {
        if (_isDisposed) return;
        _isDisposed = true;
        _model.SearchChanged -= OnModelSearchChanged;
    }

    private void OnModelSearchChanged(object? sender, SearchChangedEventArgs e)
    {
        if (_isDisposed) return;
        RefreshResults();
    }

    public void RefreshResults()
    {
        if (_isDisposed) return;
        var results = ComputeResults(_model.Descriptors);
        _model.UpdateResults(results);
    }

    private IReadOnlyList<SearchResult> ComputeResults(IReadOnlyList<SearchDescriptor> descriptors)
    {
        var items = _grid.ItemsSource;
        var columns = _grid.Columns;
        if (items == null || descriptors.Count == 0 || columns.Count == 0) return Array.Empty<SearchResult>();

        var colInfos = new List<ColInfo>();
        for (int i = 0; i < columns.Count; i++)
        {
            var col = columns[i];
            if (col == null || !col.IsVisible) continue;
            if (!DataGridColumnSearch.GetIsSearchable(col)) continue;
            string? path = DataGridColumnSearch.GetSearchMemberPath(col);
            if (path == null && col is DataGridBoundColumn boundCol)
                path = GetBindingPath(boundCol);
            var tp = DataGridColumnSearch.GetTextProvider(col);
            var name = col.Header?.ToString() ?? $"Col{i}";
            colInfos.Add(new ColInfo(col, name, i, path, tp));
        }

        // Prepare descriptors once instead of per-item (avoids repeated tokenization and regex compilation).
        var prepared = new List<(SearchDescriptor Desc, SearchTextMatcher.PreparedDescriptor Prep, IReadOnlyList<ColInfo> Scope)>();
        foreach (var desc in descriptors)
        {
            if (string.IsNullOrEmpty(desc.Query) && !desc.AllowEmpty) continue;
            var pd = SearchTextMatcher.Prepare(desc);
            if (pd == null) continue;

            IReadOnlyList<ColInfo> scope = desc.Scope == SearchScope.VisibleColumns ? colInfos
                : desc.Scope == SearchScope.ExplicitColumns && desc.ColumnNames != null
                ? colInfos.FindAll(c => desc.ColumnNames.Contains(c.Name))
                : colInfos;
            prepared.Add((desc, pd, scope));
        }
        if (prepared.Count == 0) return Array.Empty<SearchResult>();

        var results = new List<SearchResult>();
        int ri = 0;
        foreach (var item in items)
        {
            if (item == null) { ri++; continue; }
            foreach (var (desc, pd, scope) in prepared)
            {
                foreach (var col in scope)
                {
                    var text = col.TextProvider != null ? col.TextProvider(item) : GetText(col, item);
                    if (string.IsNullOrEmpty(text)) continue;
                    var matches = SearchTextMatcher.FindMatches(text, pd);
                    if (matches.Count > 0) results.Add(new SearchResult(item, ri, col.Name, col.Index, text, matches));
                }
            }
            ri++;
        }
        results.Sort((a, b) => a.RowIndex != b.RowIndex ? a.RowIndex.CompareTo(b.RowIndex) : a.ColumnIndex.CompareTo(b.ColumnIndex));
        return results;
    }

    private static string? GetBindingPath(DataGridBoundColumn col) => col.BindingPath;

    private static readonly System.Collections.Concurrent.ConcurrentDictionary<(Type Type, string Path), PropertyInfo?> _propertyCache = new();

    private static string? GetText(ColInfo col, object item)
    {
        if (col.BindingPath == null) return null;
        var parts = col.BindingPath.Split('.');
        object? current = item;
        foreach (var part in parts)
        {
            if (current == null) return null;
            var prop = _propertyCache.GetOrAdd((current.GetType(), part), key =>
                key.Type.GetProperty(key.Path, BindingFlags.Public | BindingFlags.Instance | BindingFlags.IgnoreCase));
            if (prop == null) return null;
            current = prop.GetValue(current);
        }
        return current?.ToString();
    }

    private sealed class ColInfo
    {
        public DataGridColumn Column; public string Name; public int Index;
        public string? BindingPath; public Func<object, string?>? TextProvider;
        public ColInfo(DataGridColumn c, string n, int i, string? bp, Func<object, string?>? tp)
        { Column = c; Name = n; Index = i; BindingPath = bp; TextProvider = tp; }
    }
}

/// <summary>
/// Simplified text matcher that supports Contains, StartsWith, EndsWith, Equals, Regex, and Wildcard modes.
/// </summary>
internal static class SearchTextMatcher
{
    internal sealed class PreparedDescriptor
    {
        public SearchMatchMode MatchMode { get; }
        public SearchTermCombineMode TermMode { get; }
        public StringComparison Comparison { get; }
        public bool WholeWord { get; }
        public IReadOnlyList<string> Terms { get; }
        public Regex? Regex { get; }
        public bool Valid { get; }

        public PreparedDescriptor(SearchMatchMode matchMode, SearchTermCombineMode termMode,
            StringComparison comparison, bool wholeWord,
            IReadOnlyList<string> terms, Regex? regex, bool valid)
        {
            MatchMode = matchMode; TermMode = termMode; Comparison = comparison;
            WholeWord = wholeWord; Terms = terms; Regex = regex; Valid = valid;
        }
    }

    public static PreparedDescriptor? Prepare(SearchDescriptor descriptor)
    {
        if (descriptor == null) return null;
        var comparison = descriptor.Comparison ?? StringComparison.OrdinalIgnoreCase;
        var hasQuery = !string.IsNullOrEmpty(descriptor.Query);

        if (!hasQuery)
        {
            return new PreparedDescriptor(descriptor.MatchMode, descriptor.TermMode,
                comparison, descriptor.WholeWord, Array.Empty<string>(), null, true);
        }

        var query = descriptor.NormalizeWhitespace ? descriptor.Query.Trim() : descriptor.Query;

        if (descriptor.MatchMode == SearchMatchMode.Regex || descriptor.MatchMode == SearchMatchMode.Wildcard)
        {
            var pattern = descriptor.MatchMode == SearchMatchMode.Wildcard ? WildcardToRegex(query) : query;
            if (descriptor.WholeWord) pattern = $@"\b(?:{pattern})\b";
            var options = RegexOptions.Compiled;
            if (IsIgnoreCase(comparison)) options |= RegexOptions.IgnoreCase;
            try
            {
                var regex = new Regex(pattern, options);
                return new PreparedDescriptor(descriptor.MatchMode, descriptor.TermMode,
                    comparison, descriptor.WholeWord, Array.Empty<string>(), regex, true);
            }
            catch (ArgumentException)
            {
                return new PreparedDescriptor(descriptor.MatchMode, descriptor.TermMode,
                    comparison, descriptor.WholeWord, Array.Empty<string>(), null, false);
            }
        }

        var terms = Tokenize(query);
        return new PreparedDescriptor(descriptor.MatchMode, descriptor.TermMode,
            comparison, descriptor.WholeWord, terms, null, true);
    }

    public static IReadOnlyList<SearchMatch> FindMatches(string text, PreparedDescriptor descriptor)
    {
        if (descriptor == null || string.IsNullOrEmpty(text) || !descriptor.Valid)
            return Array.Empty<SearchMatch>();

        if (descriptor.Regex != null)
        {
            var matches = new List<SearchMatch>();
            foreach (Match m in descriptor.Regex.Matches(text))
            {
                if (m.Success && m.Length > 0)
                    matches.Add(new SearchMatch(m.Index, m.Length));
            }
            return MergeOverlaps(matches);
        }

        if (descriptor.Terms.Count == 0) return Array.Empty<SearchMatch>();

        var collected = new List<SearchMatch>();
        foreach (var term in descriptor.Terms)
        {
            if (string.IsNullOrEmpty(term)) continue;
            var termMatches = FindTermMatches(text, term, descriptor.MatchMode, descriptor.Comparison, descriptor.WholeWord);
            if (termMatches.Count == 0 && descriptor.TermMode == SearchTermCombineMode.All)
                return Array.Empty<SearchMatch>();
            collected.AddRange(termMatches);
        }

        return collected.Count == 0 ? Array.Empty<SearchMatch>() : MergeOverlaps(collected);
    }

    private static List<SearchMatch> FindTermMatches(string text, string term, SearchMatchMode mode, StringComparison comparison, bool wholeWord)
    {
        var matches = new List<SearchMatch>();
        switch (mode)
        {
            case SearchMatchMode.StartsWith:
                if (text.StartsWith(term, comparison) && IsWholeWord(text, 0, term.Length, wholeWord))
                    matches.Add(new SearchMatch(0, term.Length));
                break;
            case SearchMatchMode.EndsWith:
                if (text.EndsWith(term, comparison))
                {
                    int start = text.Length - term.Length;
                    if (IsWholeWord(text, start, term.Length, wholeWord))
                        matches.Add(new SearchMatch(start, term.Length));
                }
                break;
            case SearchMatchMode.Equals:
                if (string.Equals(text, term, comparison) && IsWholeWord(text, 0, term.Length, wholeWord))
                    matches.Add(new SearchMatch(0, term.Length));
                break;
            default: // Contains
                int idx = 0;
                while (idx < text.Length)
                {
                    int pos = text.IndexOf(term, idx, comparison);
                    if (pos < 0) break;
                    if (IsWholeWord(text, pos, term.Length, wholeWord))
                        matches.Add(new SearchMatch(pos, term.Length));
                    idx = pos + term.Length;
                }
                break;
        }
        return matches;
    }

    public static IReadOnlyList<SearchMatch> MergeOverlaps(IReadOnlyList<SearchMatch> matches)
    {
        if (matches == null || matches.Count == 0) return Array.Empty<SearchMatch>();
        var ordered = matches.Where(m => m.Length > 0).OrderBy(m => m.Start).ToList();
        if (ordered.Count == 0) return Array.Empty<SearchMatch>();
        var merged = new List<SearchMatch> { ordered[0] };
        for (int i = 1; i < ordered.Count; i++)
        {
            var cur = ordered[i];
            var last = merged[^1];
            if (cur.Start < last.Start + last.Length)
            {
                int newEnd = Math.Max(last.Start + last.Length, cur.Start + cur.Length);
                merged[^1] = new SearchMatch(last.Start, newEnd - last.Start);
            }
            else merged.Add(cur);
        }
        return merged;
    }

    private static bool IsWholeWord(string text, int start, int length, bool wholeWord)
    {
        if (!wholeWord) return true;
        bool startBound = start == 0 || !char.IsLetterOrDigit(text[start - 1]);
        int end = start + length;
        bool endBound = end >= text.Length || !char.IsLetterOrDigit(text[end]);
        return startBound && endBound;
    }

    private static List<string> Tokenize(string query)
    {
        var terms = new List<string>();
        if (string.IsNullOrWhiteSpace(query)) return terms;
        var sb = new StringBuilder();
        bool inQuote = false;
        foreach (var ch in query)
        {
            if (ch == '"') { inQuote = !inQuote; continue; }
            if (!inQuote && char.IsWhiteSpace(ch)) { Flush(sb, terms); continue; }
            sb.Append(ch);
        }
        Flush(sb, terms);
        return terms;
    }

    private static void Flush(StringBuilder sb, List<string> terms)
    {
        if (sb.Length == 0) return;
        var t = sb.ToString().Trim();
        if (!string.IsNullOrEmpty(t)) terms.Add(t);
        sb.Clear();
    }

    private static string WildcardToRegex(string pattern)
    {
        var sb = new StringBuilder();
        foreach (var ch in pattern)
        {
            switch (ch)
            {
                case '*': sb.Append(".*"); break;
                case '?': sb.Append("."); break;
                default: sb.Append(Regex.Escape(ch.ToString())); break;
            }
        }
        return sb.ToString();
    }

    private static bool IsIgnoreCase(StringComparison comparison)
    {
        return comparison == StringComparison.CurrentCultureIgnoreCase
            || comparison == StringComparison.InvariantCultureIgnoreCase
            || comparison == StringComparison.OrdinalIgnoreCase;
    }
}
