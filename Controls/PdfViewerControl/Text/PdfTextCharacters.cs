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

using System.Globalization;
using System.Text;

namespace PdfViewerControl.Text;

/// <summary>What a character is to a double click.</summary>
internal enum PdfCharacterKind
{
    /// <summary>A letter, a digit or a mark on one, outside the CJK scripts.</summary>
    Word,

    /// <summary>A CJK character: there are no spaces between words, so it only joins its own kind.</summary>
    Cjk,

    /// <summary>White space.</summary>
    Space,

    /// <summary>Everything else, punctuation above all: a character on its own.</summary>
    Other
}

/// <summary>Sorting characters for word boundaries, in search and in selection alike.</summary>
internal static class PdfTextCharacters
{
    /// <summary>Whether a code point belongs to a script written without spaces between words: no whole-word matching there.</summary>
    public static bool IsCjk(int codePoint) => codePoint switch
    {
        >= 0x2E80 and <= 0x9FFF => true,    // radicals, CJK punctuation, kana, Bopomofo, extension A, unified ideographs
        >= 0xAC00 and <= 0xD7AF => true,    // Hangul syllables
        >= 0xF900 and <= 0xFAFF => true,    // compatibility ideographs
        >= 0xFF66 and <= 0xFF9F => true,    // half-width kana
        >= 0x20000 and <= 0x3FFFF => true,  // the supplementary ideographic planes
        _ => false
    };

    /// <summary>A letter, digit or mark, for the whole-word check: a mark continues the letter it sits on.</summary>
    public static bool IsWordCharacter(Rune rune) =>
        Rune.IsLetterOrDigit(rune) || Rune.GetUnicodeCategory(rune) is UnicodeCategory.NonSpacingMark or UnicodeCategory.SpacingCombiningMark or UnicodeCategory.EnclosingMark;

    public static PdfCharacterKind KindAt(string text, int index)
    {
        var c = text[index];
        if (char.IsWhiteSpace(c))
        {
            return PdfCharacterKind.Space;
        }
        var rune = RuneAt(text, index);
        if (IsCjk(rune.Value))
        {
            // CJK punctuation stands alone like any other.
            return Rune.IsLetterOrDigit(rune) ? PdfCharacterKind.Cjk : PdfCharacterKind.Other;
        }
        return IsWordCharacter(rune) ? PdfCharacterKind.Word : PdfCharacterKind.Other;
    }

    /// <summary>The UTF-16 units of the character at an index: 2 for a surrogate pair.</summary>
    public static int LengthAt(string text, int index) =>
        index + 1 < text.Length && char.IsSurrogatePair(text[index], text[index + 1]) ? 2 : 1;

    /// <summary>The character at an index; a lone surrogate reads as the replacement character.</summary>
    public static Rune RuneAt(string text, int index) =>
        Rune.TryGetRuneAt(text, index, out var rune) ? rune : Rune.ReplacementChar;
}
