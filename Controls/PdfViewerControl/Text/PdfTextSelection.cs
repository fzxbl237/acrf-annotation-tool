/*
 * MIT License
 *
 * Copyright (c) 2026 TKCode.Arc contributors
 * Copyright (c) 2026 Elia Kuratli
 *
 * Permission is hereby granted, free of charge, to any person obtaining a copy
 * of this software and associated documentation files (the "Software"), to deal
 * in the Software without restriction, including without limitation the rights
 * to use, copy, modify, merge, publish, distribute, sublicense, and/or sell
 * copies of the Software, and to permit persons to whom the Software is
 * furnished to do so, subject to the following conditions:
 *
 * The above copyright notice and this permission notice shall be included in all
 * copies or substantial portions of the Software.
 *
 * THE SOFTWARE IS PROVIDED "AS IS", WITHOUT WARRANTY OF ANY KIND, EXPRESS OR
 * IMPLIED, INCLUDING BUT NOT LIMITED TO THE WARRANTIES OF MERCHANTABILITY,
 * FITNESS FOR A PARTICULAR PURPOSE AND NONINFRINGEMENT. IN NO EVENT SHALL THE
 * AUTHORS OR COPYRIGHT HOLDERS BE LIABLE FOR ANY CLAIM, DAMAGES OR OTHER
 * LIABILITY, WHETHER IN AN ACTION OF CONTRACT, TORT OR OTHERWISE, ARISING FROM,
 * OUT OF OR IN CONNECTION WITH THE SOFTWARE OR THE USE OR OTHER DEALINGS IN THE
 * SOFTWARE.
 */

using System.Text;
using Avalonia;
using Avalonia.Input;


namespace PdfViewerControl.Text;

/// <summary>
/// Text selection (design/components/pdf/pdf.md section 5.6). The ends of a selection are kept as they were given, points of pages,
/// and turned into character positions whenever the text is there: a page's text is read only when the pointer first enters it,
/// so a press may come before its text does. Until it arrives such a selection simply has nothing to show, and it is worked out
/// from the same points when it does. Pages between the ends are read one after another, in the background.
/// </summary>
internal sealed class PdfTextSelection
{
    private enum SelectionUnit
    {
        Character,
        Word,
        Line
    }

    /// <summary>One end of a selection: a point of a page, or a position in its text when no pointer was involved.</summary>
    private readonly record struct End(int Page, Point Point, int? Index = null);

    /// <summary>What is selected on one page. <see cref="To"/> of int.MaxValue means up to the end of the page.</summary>
    private sealed record PageSelection(int From, int To, string Text, Rect[] Rects);



    private readonly Func<int, PdfPageText?> _tryGetText;
    private readonly Action<int> _requestText;
    private readonly Func<int, Size> _pageSize;
    public event Action? Changed;
    public string SelectedText { get; private set; } = string.Empty;
    private readonly SortedDictionary<int, PageSelection> _selected = new();
    private End? _anchor;
    private End? _focus;
    private SelectionUnit _unit;
    private bool _hasRange;
    private bool _started;
    private bool _dragging;
    private End _press;
    private int _pendingPage = -1;
    private (int StartPage, int StartIndex, int EndPage, int EndIndex) _range;
    private TaskCompletionSource? _complete;

    public PdfTextSelection(Func<int, PdfPageText?> tryGetText, Action<int> requestText, Func<int, Size> pageSize)
    {
        _tryGetText = tryGetText;
        _requestText = requestText;
        _pageSize = pageSize;
    }

    /// <summary>The pointer is down with a press this layer took.</summary>
    public bool IsDragging => _dragging;

    /// <summary>Whether anything is selected, or will be once its text arrives.</summary>
    public bool HasSelection => _hasRange || (_started && _anchor is not null);

    /// <summary>Finished when the text of every selected page has arrived.</summary>
    public Task WhenComplete => _complete?.Task ?? Task.CompletedTask;

    /// <summary>Selects a whole page.</summary>
    public void SelectPage(int pageIndex)
    {
        _anchor = new End(pageIndex, default, 0);
        _focus = new End(pageIndex, default, int.MaxValue);
        _unit = SelectionUnit.Character;
        _started = true;
        Recompute();
    }

    public void Clear()
    {
        _anchor = null;
        _focus = null;
        _started = false;
        _dragging = false;
        ClearRange();
    }

    private void ClearRange()
    {
        _hasRange = false;
        _pendingPage = -1;
        _selected.Clear();
        FinishComplete();
        SelectedText = string.Empty;
        Changed?.Invoke();
    }

    /// <summary>The text of a page arrived; it may be what the selection was waiting for.</summary>
    public void OnTextArrived(int pageIndex)
    {
        if (pageIndex == _pendingPage || (_started && (_anchor?.Page == pageIndex || _focus?.Page == pageIndex)))
        {
            Recompute();
        }
        // The cursor over this page may change.
        Changed?.Invoke();
    }

    public IReadOnlyList<Rect> GetRects(int pageIndex) =>
        _selected.TryGetValue(pageIndex, out var selection) ? selection.Rects : Array.Empty<Rect>();

    public void Press(int pageIndex, Point point, int clickCount, KeyModifiers modifiers)
    {
        _requestText(pageIndex);
        var end = new End(pageIndex, point);
        _press = end;
        _dragging = true;
        if (modifiers.HasFlag(KeyModifiers.Shift) && _anchor is not null && clickCount <= 1)
        {
            _focus = end;
            _unit = SelectionUnit.Character;
            _started = true;
            Recompute();
            return;
        }
        _anchor = _focus = end;
        _unit = clickCount >= 3 ? SelectionUnit.Line : clickCount == 2 ? SelectionUnit.Word : SelectionUnit.Character;
        _started = clickCount >= 2;
        if (_started) Recompute();
        else ClearRange();
    }

    public void Move(int pageIndex, Point point, double displayScale)
    {
        if (!_dragging) return;
        if (!_started)
        {
            var delta = new Vector(point.X - _press.Point.X, point.Y - _press.Point.Y);
            if (pageIndex == _press.Page && delta.Length * displayScale <= 4) return;
            _started = true;
        }
        _focus = new End(pageIndex, point);
        Recompute();
    }

    public void Release() => _dragging = false;

    /// <summary>Works the selection out from its ends, as far as the text is there.</summary>
    private void Recompute()
    {
        if (!_started || _anchor is not { } anchor || _focus is not { } focus)
        {
            return;
        }
        var anchorText = _tryGetText(anchor.Page);
        var focusText = anchor.Page == focus.Page ? anchorText : _tryGetText(focus.Page);
        if (anchorText is null || focusText is null)
        {
            // Nothing to go by yet. Whatever was shown stays as it was; a guess could only be wrong.
            WaitFor(anchorText is null ? anchor.Page : focus.Page);
            return;
        }
        var (anchorStart, anchorEnd) = Resolve(anchor, anchorText);
        var (focusStart, focusEnd) = Resolve(focus, focusText);
        (int, int, int, int) range;
        if (anchor.Page == focus.Page)
        {
            range = (anchor.Page, Math.Min(anchorStart, focusStart), anchor.Page, Math.Max(anchorEnd, focusEnd));
        }
        else if (anchor.Page < focus.Page)
        {
            range = (anchor.Page, anchorStart, focus.Page, focusEnd);
        }
        else
        {
            range = (focus.Page, focusStart, anchor.Page, anchorEnd);
        }
        _range = range;
        _hasRange = range.Item1 != range.Item3 || range.Item2 != range.Item4;
        ApplyRange();
    }

    private (int Start, int End) Resolve(in End end, PdfPageText text)
    {
        if (end.Index is { } index)
        {
            var at = Math.Min(index, text.Text.Length);
            return (at, at);
        }
        var size = _pageSize(end.Page);
        // Past the top or the bottom of the page (a drag over the gap to the next one) means all the way.
        var caret = end.Point.Y < 0 ? 0 : end.Point.Y > size.Height ? text.Text.Length : text.CaretAt(end.Point);
        switch (_unit)
        {
            case SelectionUnit.Word when text.Text.Length > 0:
                return text.WordAt(text.HitTest(end.Point) is >= 0 and var word ? word : caret);
            case SelectionUnit.Line when text.Text.Length > 0:
                return text.LineAt(text.HitTest(end.Point) is >= 0 and var line ? line : caret);
            default:
                return (caret, caret);
        }
    }

    /// <summary>Brings the per-page parts in line with <see cref="_range"/>, reusing pages that did not change: a drag touches only its end.</summary>
    private void ApplyRange()
    {
        var (startPage, startIndex, endPage, endIndex) = _range;
        if (!_hasRange)
        {
            ClearRange();
            return;
        }
        List<int>? gone = null;
        foreach (var page in _selected.Keys)
        {
            if (page < startPage || page > endPage)
            {
                (gone ??= new List<int>()).Add(page);
            }
        }
        if (gone is not null)
        {
            foreach (var page in gone)
            {
                _selected.Remove(page);
            }
        }
        var missing = -1;
        var changed = gone is not null;
        for (var page = startPage; page <= endPage; page++)
        {
            var from = page == startPage ? startIndex : 0;
            var to = page == endPage ? endIndex : int.MaxValue;
            if (_selected.TryGetValue(page, out var known) && known.From == from && known.To == to)
            {
                continue;
            }
            if (_tryGetText(page) is not { } text)
            {
                if (missing < 0)
                {
                    missing = page;
                }
                continue;
            }
            var end = Math.Min(to, text.Text.Length);
            var start = Math.Min(from, end);
            _selected[page] = new PageSelection(from, to, text.Text.Substring(start, end - start), text.GetRects(start, end - start).ToArray());
            changed = true;
        }
        if (missing >= 0)
        {
            WaitFor(missing);
        }
        else
        {
            _pendingPage = -1;
            if (changed || SelectedText.Length == 0)
            {
                SelectedText = BuildText();
            }
            FinishComplete();
        }
        Changed?.Invoke();
    }

    private void WaitFor(int page)
    {
        _pendingPage = page;
        _complete ??= new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        _requestText(page);
    }

    private void FinishComplete()
    {
        var complete = _complete;
        _complete = null;
        complete?.TrySetResult();
    }

    /// <summary>The pages in order with one line break between them.</summary>
    private string BuildText()
    {
        var text = new StringBuilder();
        foreach (var part in _selected.Values)
        {
            var clean = Clean(part.Text);
            if (clean.Length == 0)
            {
                continue;
            }
            if (text.Length > 0)
            {
                text.Append('\n');
            }
            text.Append(clean);
        }
        return text.ToString();
    }

    /// <summary>Line ends as one character, and the control character PDFium writes for a line-end hyphen as what it stands for: the hyphen and the line end it left out.</summary>
    internal static string Clean(string text) =>
        text.Replace("\r\n", "\n", StringComparison.Ordinal).Replace('\r', '\n').Replace("\u0002", "-\n", StringComparison.Ordinal).TrimEnd('\n');
}
