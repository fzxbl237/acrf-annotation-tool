using System.Buffers.Binary;

namespace AcrfAnnotationTool.Services;

/// <summary>Reads horizontal glyph advances from a TrueType sfnt font.</summary>
internal sealed class TrueTypeFontMetrics
{
    private readonly byte[] _data;
    private readonly int _unitsPerEm;
    private readonly int _numberOfHMetrics;
    private readonly int _numberOfGlyphs;
    private readonly int _hmtxOffset;
    private readonly int _cmapOffset;
    private readonly int _cmapLength;
    private readonly int _unicodeCmap;

    public TrueTypeFontMetrics(string path)
    {
        _data = File.ReadAllBytes(path);
        var tables = ReadTables();
        var head = RequiredTable(tables, "head");
        var hhea = RequiredTable(tables, "hhea");
        var maxp = RequiredTable(tables, "maxp");
        var hmtx = RequiredTable(tables, "hmtx");
        var cmap = RequiredTable(tables, "cmap");
        _unitsPerEm = ReadU16(head.Offset + 18);
        _numberOfHMetrics = ReadU16(hhea.Offset + 34);
        _numberOfGlyphs = ReadU16(maxp.Offset + 4);
        _hmtxOffset = hmtx.Offset;
        _cmapOffset = cmap.Offset;
        _cmapLength = cmap.Length;
        if (_unitsPerEm == 0 || _numberOfHMetrics == 0 || _numberOfHMetrics > _numberOfGlyphs ||
            hmtx.Length < _numberOfHMetrics * 4)
            throw new InvalidDataException("Invalid TrueType font metrics.");
        _unicodeCmap = SelectUnicodeCmap();

        // Keep only advances and the selected Unicode map, not outlines or embedded font data.
        var cmapLength = _unicodeCmap < 0 ? 0 : ReadU16(_unicodeCmap) == 12
            ? checked((int)ReadU32(_unicodeCmap + 4))
            : ReadU16(_unicodeCmap + 2);
        if (_unicodeCmap >= 0 && (cmapLength < 2 || _unicodeCmap > _cmapOffset + _cmapLength - cmapLength))
            throw new InvalidDataException("Invalid TrueType Unicode map bounds.");
        if (_unicodeCmap >= 0)
        {
            var format = ReadU16(_unicodeCmap);
            if (format == 12)
            {
                if (cmapLength < 16 || ReadU32(_unicodeCmap + 12) > (uint)((cmapLength - 16) / 12))
                    throw new InvalidDataException("Invalid TrueType Unicode groups.");
            }
            else
            {
                if (cmapLength < 16)
                    throw new InvalidDataException("Invalid TrueType Unicode segments.");
                var segmentCountX2 = ReadU16(_unicodeCmap + 6);
                if (segmentCountX2 == 0 || segmentCountX2 % 2 != 0 || 16 + segmentCountX2 * 4 > cmapLength)
                    throw new InvalidDataException("Invalid TrueType Unicode segments.");
            }
        }
        var metricsLength = _numberOfHMetrics * 4;
        var compactData = new byte[checked(metricsLength + cmapLength)];
        _data.AsSpan(_hmtxOffset, metricsLength).CopyTo(compactData);
        if (_unicodeCmap >= 0)
            _data.AsSpan(_unicodeCmap, cmapLength).CopyTo(compactData.AsSpan(metricsLength));
        _hmtxOffset = 0;
        _unicodeCmap = _unicodeCmap < 0 ? -1 : metricsLength;
        _data = compactData;
    }

    public double GetWidth(string text, double fontSize)
    {
        double units = 0;
        foreach (var rune in text.EnumerateRunes())
            units += GetAdvanceWidth(FindGlyph(rune.Value));
        return units * fontSize / 1000;
    }

    private int GetAdvanceWidth(int glyph)
    {
        glyph = Math.Clamp(glyph, 0, _numberOfGlyphs - 1);
        var metricIndex = Math.Min(glyph, _numberOfHMetrics - 1);
        // iText normalizes each glyph to integer PDF units before summing advances.
        return ReadU16(_hmtxOffset + metricIndex * 4) * 1000 / _unitsPerEm;
    }

    private int SelectUnicodeCmap()
    {
        var count = ReadU16(_cmapOffset + 2);
        var selected = -1;
        var selectedRank = -1;
        for (var i = 0; i < count; i++)
        {
            var record = _cmapOffset + 4 + i * 8;
            var platform = ReadU16(record);
            var encoding = ReadU16(record + 2);
            var subtableOffset = checked((int)ReadU32(record + 4));
            if (subtableOffset < 0 || subtableOffset > _cmapLength - 2)
                continue;
            var subtable = _cmapOffset + subtableOffset;
            var format = ReadU16(subtable);
            if (format is not (4 or 12))
                continue;
            var rank = format == 12 && platform == 3 && encoding == 10 ? 4
                : format == 12 ? 3
                : format == 4 && platform == 3 ? 2
                : format == 4 ? 1 : 0;
            if (rank > selectedRank)
            {
                selected = subtable;
                selectedRank = rank;
            }
        }
        return selected;
    }

    private int FindGlyph(int codepoint)
    {
        if (_unicodeCmap < 0)
            return 0;
        return ReadU16(_unicodeCmap) == 12
            ? FindGlyphFormat12(_unicodeCmap, codepoint)
            : FindGlyphFormat4(_unicodeCmap, codepoint);
    }

    private int FindGlyphFormat12(int offset, int codepoint)
    {
        var groups = ReadU32(offset + 12);
        uint low = 0, high = groups;
        while (low < high)
        {
            var mid = low + (high - low) / 2;
            var group = offset + 16 + checked((int)mid * 12);
            var start = ReadU32(group);
            var end = ReadU32(group + 4);
            if ((uint)codepoint < start) high = mid;
            else if ((uint)codepoint > end) low = mid + 1;
            else return checked((int)(ReadU32(group + 8) + (uint)codepoint - start));
        }
        return 0;
    }

    private int FindGlyphFormat4(int offset, int codepoint)
    {
        if (codepoint > 0xFFFF) return 0;
        var segments = ReadU16(offset + 6) / 2;
        var endCodes = offset + 14;
        var startCodes = endCodes + segments * 2 + 2;
        var deltas = startCodes + segments * 2;
        var rangeOffsets = deltas + segments * 2;
        for (var i = 0; i < segments; i++)
        {
            var end = ReadU16(endCodes + i * 2);
            if (codepoint > end) continue;
            var start = ReadU16(startCodes + i * 2);
            if (codepoint < start) return 0;
            var delta = ReadS16(deltas + i * 2);
            var rangeOffsetAddress = rangeOffsets + i * 2;
            var rangeOffset = ReadU16(rangeOffsetAddress);
            if (rangeOffset == 0) return (codepoint + delta) & 0xFFFF;
            var glyphAddress = rangeOffsetAddress + rangeOffset + (codepoint - start) * 2;
            var glyph = ReadU16(glyphAddress);
            return glyph == 0 ? 0 : (glyph + delta) & 0xFFFF;
        }
        return 0;
    }

    private Dictionary<string, (int Offset, int Length)> ReadTables()
    {
        var count = ReadU16(4);
        var tables = new Dictionary<string, (int, int)>(StringComparer.Ordinal);
        for (var i = 0; i < count; i++)
        {
            var record = 12 + i * 16;
            var tag = System.Text.Encoding.ASCII.GetString(_data, record, 4);
            var offset = checked((int)ReadU32(record + 8));
            var length = checked((int)ReadU32(record + 12));
            if (offset < 0 || length < 0 || offset > _data.Length - length)
                throw new InvalidDataException("Invalid TrueType table bounds.");
            tables[tag] = (offset, length);
        }
        return tables;
    }

    private static (int Offset, int Length) RequiredTable(Dictionary<string, (int Offset, int Length)> tables, string name) =>
        tables.TryGetValue(name, out var table) ? table : throw new InvalidDataException($"Missing TrueType table: {name}.");

    private ushort ReadU16(int offset) => BinaryPrimitives.ReadUInt16BigEndian(_data.AsSpan(offset, 2));
    private short ReadS16(int offset) => BinaryPrimitives.ReadInt16BigEndian(_data.AsSpan(offset, 2));
    private uint ReadU32(int offset) => BinaryPrimitives.ReadUInt32BigEndian(_data.AsSpan(offset, 4));
}
