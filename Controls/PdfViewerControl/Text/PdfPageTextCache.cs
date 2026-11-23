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

namespace PdfViewerControl.Text;

/// <summary>
/// The text of the most recently used pages of one document (64, spec section 5.5), shared by search and selection: one read per
/// page, whoever asks first. A page that cannot be read counts as a page without text, so it neither ends a search nor comes
/// back on every pointer move. Reading is only ever awaited, never waited for: a backend may be busy rendering a page.
/// </summary>
internal sealed class PdfPageTextCache : IDisposable
{
    /// <summary>How many pages are kept.</summary>
    public const int Capacity = 64;

    private static readonly PdfPageText s_noText = new(string.Empty, []);

    private readonly Func<int, CancellationToken, Task<PdfPageText>> _readText;
    private readonly CancellationTokenSource _lifetime = new();
    private readonly object _gate = new();
    private readonly Dictionary<int, LinkedListNode<(int Page, Task<PdfPageText> Text)>> _pages = new();
    private readonly LinkedList<(int Page, Task<PdfPageText> Text)> _recent = new();

    public PdfPageTextCache(Func<int, CancellationToken, Task<PdfPageText>> readText)
    {
        _readText = readText;
    }

    /// <summary>How many pages are held (tests).</summary>
    public int Count
    {
        get
        {
            lock (_gate)
            {
                return _pages.Count;
            }
        }
    }

    /// <summary>The text of a page if it has arrived; never asks for anything.</summary>
    public PdfPageText? TryGet(int pageIndex)
    {
        lock (_gate)
        {
            if (_pages.TryGetValue(pageIndex, out var node) && node.Value.Text.IsCompletedSuccessfully)
            {
                Touch(node);
                return node.Value.Text.Result;
            }
            return null;
        }
    }

    /// <summary>
    /// The text of a page. Cancelling ends this wait only: the read stays in the cache for whoever asks next. Ends with
    /// <see cref="OperationCanceledException"/> when the document is gone.
    /// </summary>
    public Task<PdfPageText> GetAsync(int pageIndex, CancellationToken cancellationToken)
    {
        Task<PdfPageText> text;
        lock (_gate)
        {
            _lifetime.Token.ThrowIfCancellationRequested();
            if (_pages.TryGetValue(pageIndex, out var node) && !node.Value.Text.IsCanceled)
            {
                Touch(node);
                text = node.Value.Text;
            }
            else
            {
                if (node is not null)
                {
                    _recent.Remove(node);
                    _pages.Remove(pageIndex);
                }
                text = ReadAsync(pageIndex);
                _pages[pageIndex] = _recent.AddFirst((pageIndex, text));
                while (_recent.Count > Capacity)
                {
                    _pages.Remove(_recent.Last!.Value.Page);
                    _recent.RemoveLast();
                }
            }
        }
        return cancellationToken.CanBeCanceled ? text.WaitAsync(cancellationToken) : text;
    }

    private void Touch(LinkedListNode<(int Page, Task<PdfPageText> Text)> node)
    {
        if (!ReferenceEquals(_recent.First, node))
        {
            _recent.Remove(node);
            _recent.AddFirst(node);
        }
    }

    private async Task<PdfPageText> ReadAsync(int pageIndex)
    {
        try
        {
            return await _readText(pageIndex, _lifetime.Token).ConfigureAwait(false);
        }
        catch (Exception e) when (e is not OperationCanceledException and not OutOfMemoryException)
        {
            return s_noText;
        }
    }

    public void Dispose()
    {
        lock (_gate)
        {
            _lifetime.Cancel();
            _pages.Clear();
            _recent.Clear();
        }
    }
}
