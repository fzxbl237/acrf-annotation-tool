using System.Globalization;
using System.IO.Compression;
using System.Text;
using System.Xml;
using System.Xml.Linq;
using AcrfAnnotationTool.Models;

namespace AcrfAnnotationTool.Services;

/// <summary>
/// Reads and writes the annotation Spec format as an Open XML .xlsx package.
/// The implementation intentionally uses only BCL ZIP/XML APIs so it works without
/// Excel, COM, or a third-party Excel library.
/// </summary>
public static class AnnotationExcelService
{
    private const string MainNamespace =
        "http://schemas.openxmlformats.org/spreadsheetml/2006/main";
    private const string RelationshipsNamespace =
        "http://schemas.openxmlformats.org/package/2006/relationships";
    private const string DocumentRelationshipsNamespace =
        "http://schemas.openxmlformats.org/officeDocument/2006/relationships";
    private const string ContentTypesNamespace =
        "http://schemas.openxmlformats.org/package/2006/content-types";
    private const string XmlNamespace = "http://www.w3.org/XML/1998/namespace";

    private static readonly XNamespace Main = MainNamespace;
    private static readonly XNamespace Relationships = RelationshipsNamespace;
    private static readonly XNamespace DocumentRelationships = DocumentRelationshipsNamespace;

    private static readonly string[] Headers =
    [
        "Form名称",
        "页码",
        "变量名称",
        "文本",
        "类型",
        "注释文本",
        "注释文本所属域",
        "虚线框",
        "X轴",
        "Y轴",
        "宽度",
        "行数",
        "Form书签名称",
        "注释唯一标识"
    ];

    public static async Task<IReadOnlyList<Annotation>> ImportAsync(
        string path,
        CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(path);

        await using var file = new FileStream(
            path,
            FileMode.Open,
            FileAccess.Read,
            FileShare.Read,
            bufferSize: 64 * 1024,
            options: FileOptions.Asynchronous | FileOptions.SequentialScan);
        using var package = new ZipArchive(file, ZipArchiveMode.Read, leaveOpen: false);

        var worksheetPath = await FindFirstWorksheetPathAsync(package, cancellationToken)
            .ConfigureAwait(false);
        var worksheet = package.GetEntry(worksheetPath)
            ?? throw new InvalidDataException("Excel 文件中找不到第一个工作表。");
        var sharedStrings = await ReadSharedStringsAsync(package, cancellationToken)
            .ConfigureAwait(false);

        await using var worksheetStream = worksheet.Open();
        using var reader = XmlReader.Create(
            worksheetStream,
            new XmlReaderSettings
            {
                Async = true,
                DtdProcessing = DtdProcessing.Prohibit,
                XmlResolver = null,
                IgnoreComments = true,
                IgnoreWhitespace = true,
            });

        var annotations = new List<Annotation>();
        var firstDataRow = true;
        while (await reader.ReadAsync().ConfigureAwait(false))
        {
            cancellationToken.ThrowIfCancellationRequested();
            if (reader.NodeType != XmlNodeType.Element ||
                reader.LocalName != "row")
            {
                continue;
            }

            var values = await ReadRowAsync(reader, sharedStrings, cancellationToken)
                .ConfigureAwait(false);
            if (IsEmptyRow(values))
                continue;

            if (firstDataRow && IsHeaderRow(values))
            {
                firstDataRow = false;
                continue;
            }

            firstDataRow = false;
            annotations.Add(ParseAnnotation(values));
        }

        return annotations;
    }

    public static async Task ExportAsync(
        string path,
        IEnumerable<Annotation> annotations,
        CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(path);
        ArgumentNullException.ThrowIfNull(annotations);

        cancellationToken.ThrowIfCancellationRequested();
        var rows = annotations.ToArray();
        var fullPath = Path.GetFullPath(path);
        var directory = Path.GetDirectoryName(fullPath);
        if (!string.IsNullOrEmpty(directory))
            Directory.CreateDirectory(directory);

        var temporaryPath = $"{fullPath}.{Guid.NewGuid():N}.tmp";
        try
        {
            await using (var file = new FileStream(
                temporaryPath,
                FileMode.CreateNew,
                FileAccess.Write,
                FileShare.None,
                bufferSize: 64 * 1024,
                options: FileOptions.Asynchronous | FileOptions.SequentialScan))
            using (var package = new ZipArchive(file, ZipArchiveMode.Create, leaveOpen: false))
            {
                await WriteEntryAsync(
                    package,
                    "[Content_Types].xml",
                    WriteContentTypesAsync,
                    cancellationToken).ConfigureAwait(false);
                await WriteEntryAsync(
                    package,
                    "_rels/.rels",
                    WritePackageRelationshipsAsync,
                    cancellationToken).ConfigureAwait(false);
                await WriteEntryAsync(
                    package,
                    "xl/workbook.xml",
                    WriteWorkbookAsync,
                    cancellationToken).ConfigureAwait(false);
                await WriteEntryAsync(
                    package,
                    "xl/_rels/workbook.xml.rels",
                    WriteWorkbookRelationshipsAsync,
                    cancellationToken).ConfigureAwait(false);
                await WriteEntryAsync(
                    package,
                    "xl/worksheets/sheet1.xml",
                    (writer, token) => WriteWorksheetAsync(writer, rows, token),
                    cancellationToken).ConfigureAwait(false);
            }

            cancellationToken.ThrowIfCancellationRequested();
            File.Move(temporaryPath, fullPath, overwrite: true);
        }
        finally
        {
            try
            {
                if (File.Exists(temporaryPath))
                    File.Delete(temporaryPath);
            }
            catch
            {
                // Keep the original export exception if cleanup is not possible.
            }
        }
    }

    private static async Task<string> FindFirstWorksheetPathAsync(
        ZipArchive package,
        CancellationToken cancellationToken)
    {
        var workbookEntry = package.GetEntry("xl/workbook.xml")
            ?? throw new InvalidDataException("Excel 文件中找不到 workbook.xml。");
        var workbook = await LoadXmlAsync(workbookEntry, cancellationToken).ConfigureAwait(false);
        var sheet = workbook.Root?.Element(Main + "sheets")?.Element(Main + "sheet");
        var relationshipId = sheet?.Attribute(DocumentRelationships + "id")?.Value;

        if (!string.IsNullOrWhiteSpace(relationshipId))
        {
            var relationshipsEntry = package.GetEntry("xl/_rels/workbook.xml.rels");
            if (relationshipsEntry is not null)
            {
                var relationships = await LoadXmlAsync(relationshipsEntry, cancellationToken)
                    .ConfigureAwait(false);
                var target = relationships.Root?
                    .Elements(Relationships + "Relationship")
                    .FirstOrDefault(item =>
                        string.Equals(
                            item.Attribute("Id")?.Value,
                            relationshipId,
                            StringComparison.Ordinal))?
                    .Attribute("Target")?.Value;
                if (!string.IsNullOrWhiteSpace(target))
                    return NormalizePackagePath(target);
            }
        }

        var fallback = package.GetEntry("xl/worksheets/sheet1.xml");
        if (fallback is not null)
            return fallback.FullName;

        throw new InvalidDataException("Excel 文件中找不到工作表关系。");
    }

    private static async Task<IReadOnlyList<string>> ReadSharedStringsAsync(
        ZipArchive package,
        CancellationToken cancellationToken)
    {
        var entry = package.GetEntry("xl/sharedStrings.xml");
        if (entry is null)
            return Array.Empty<string>();

        var document = await LoadXmlAsync(entry, cancellationToken).ConfigureAwait(false);
        var result = new List<string>();
        foreach (var sharedString in document.Root?.Elements(Main + "si") ?? [])
        {
            cancellationToken.ThrowIfCancellationRequested();
            result.Add(string.Concat(sharedString.Descendants(Main + "t").Select(item => item.Value)));
        }

        return result;
    }

    private static async Task<List<string?>> ReadRowAsync(
        XmlReader reader,
        IReadOnlyList<string> sharedStrings,
        CancellationToken cancellationToken)
    {
        var values = new List<string?>();
        var nextColumn = 0;
        if (reader.IsEmptyElement)
            return values;

        while (await reader.ReadAsync().ConfigureAwait(false))
        {
            cancellationToken.ThrowIfCancellationRequested();
            if (reader.NodeType == XmlNodeType.EndElement && reader.LocalName == "row")
                break;
            if (reader.NodeType != XmlNodeType.Element || reader.LocalName != "c")
                continue;

            var cell = await ReadCellAsync(reader, sharedStrings, nextColumn, cancellationToken)
                .ConfigureAwait(false);
            nextColumn = Math.Max(nextColumn, cell.ColumnIndex + 1);
            while (values.Count <= cell.ColumnIndex)
                values.Add(null);
            values[cell.ColumnIndex] = cell.Value;
        }

        return values;
    }

    private static async Task<CellValue> ReadCellAsync(
        XmlReader reader,
        IReadOnlyList<string> sharedStrings,
        int fallbackColumn,
        CancellationToken cancellationToken)
    {
        var columnIndex = ParseColumnIndex(reader.GetAttribute("r")) ?? fallbackColumn;
        var cellType = reader.GetAttribute("t");
        using var subtree = reader.ReadSubtree();
        var cell = await XElement.LoadAsync(subtree, LoadOptions.PreserveWhitespace, cancellationToken)
            .ConfigureAwait(false);
        var inlineString = cell.Element(Main + "is");
        var value = inlineString is not null
            ? string.Concat(inlineString.Descendants(Main + "t").Select(item => item.Value))
            : cell.Element(Main + "v")?.Value ?? string.Empty;
        if (string.Equals(cellType, "s", StringComparison.OrdinalIgnoreCase) &&
            int.TryParse(value, NumberStyles.Integer, CultureInfo.InvariantCulture, out var sharedIndex) &&
            sharedIndex >= 0 && sharedIndex < sharedStrings.Count)
        {
            value = sharedStrings[sharedIndex];
        }
        else if (string.Equals(cellType, "b", StringComparison.OrdinalIgnoreCase))
        {
            value = value == "1" ? "TRUE" : "FALSE";
        }

        return new CellValue(columnIndex, value);
    }

    private static Annotation ParseAnnotation(IReadOnlyList<string?> values)
    {
        return new Annotation
        {
            FormName = EmptyToNull(GetValue(values, 0)),
            PageNum = ParseInt(GetValue(values, 1)),
            VarName = GetValue(values, 2),
            Text = GetValue(values, 3),
            Type = GetValue(values, 4),
            AnnotationText = GetValue(values, 5),
            AnnotationTextDomain = GetValue(values, 6),
            AnnotationDotted = ParseBool(GetValue(values, 7)),
            X = ParseInt(GetValue(values, 8)),
            Y = ParseInt(GetValue(values, 9)),
            Width = ParseInt(GetValue(values, 10)),
            Line = ParseInt(GetValue(values, 11), defaultValue: 1),
            FormOutline = GetValue(values, 12),
            AnnotationUniqueId = EmptyToNull(GetValue(values, 13)) ?? Guid.NewGuid().ToString(),
        };
    }

    private static bool IsHeaderRow(IReadOnlyList<string?> values)
    {
        var matches = 0;
        for (var index = 0; index < Headers.Length && index < values.Count; index++)
        {
            if (string.Equals(
                    GetValue(values, index).Trim(),
                    Headers[index],
                    StringComparison.OrdinalIgnoreCase))
            {
                matches++;
            }
        }

        return matches >= 3;
    }

    private static bool IsEmptyRow(IReadOnlyList<string?> values) =>
        values.Count == 0 || values.All(string.IsNullOrEmpty);

    private static string GetValue(IReadOnlyList<string?> values, int index) =>
        index >= 0 && index < values.Count ? values[index] ?? string.Empty : string.Empty;

    private static string? EmptyToNull(string value) =>
        string.IsNullOrEmpty(value) ? null : value;

    private static int ParseInt(string value, int defaultValue = 0)
    {
        if (int.TryParse(value.Trim(), NumberStyles.Integer, CultureInfo.InvariantCulture, out var result))
            return result;
        if (decimal.TryParse(value.Trim(), NumberStyles.Number, CultureInfo.InvariantCulture, out var decimalResult))
            return decimal.ToInt32(decimal.Truncate(decimalResult));
        if (int.TryParse(value.Trim(), NumberStyles.Integer, CultureInfo.CurrentCulture, out result))
            return result;
        return defaultValue;
    }

    private static bool ParseBool(string value)
    {
        if (bool.TryParse(value.Trim(), out var result))
            return result;

        return value.Trim() switch
        {
            "1" or "是" or "是的" or "Y" or "y" or "YES" or "yes" => true,
            _ => false,
        };
    }

    private static int? ParseColumnIndex(string? cellReference)
    {
        if (string.IsNullOrWhiteSpace(cellReference))
            return null;

        var result = 0;
        var hasLetters = false;
        foreach (var character in cellReference)
        {
            if (character is >= 'a' and <= 'z' or >= 'A' and <= 'Z')
            {
                hasLetters = true;
                result = result * 26 + (char.ToUpperInvariant(character) - 'A' + 1);
                continue;
            }
            break;
        }

        return hasLetters ? result - 1 : null;
    }

    private static string NormalizePackagePath(string target)
    {
        var stack = new List<string>();
        if (!target.StartsWith("/", StringComparison.Ordinal))
            stack.Add("xl");

        foreach (var part in target.Replace('\\', '/').Split('/'))
        {
            if (string.IsNullOrEmpty(part) || part == ".")
                continue;
            if (part == "..")
            {
                if (stack.Count > 0)
                    stack.RemoveAt(stack.Count - 1);
                continue;
            }
            stack.Add(part);
        }

        return string.Join('/', stack);
    }

    private static async Task<XDocument> LoadXmlAsync(
        ZipArchiveEntry entry,
        CancellationToken cancellationToken)
    {
        await using var stream = entry.Open();
        using var reader = XmlReader.Create(
            stream,
            new XmlReaderSettings
            {
                Async = true,
                DtdProcessing = DtdProcessing.Prohibit,
                XmlResolver = null,
                IgnoreComments = true,
                IgnoreWhitespace = true,
            });
        return await XDocument.LoadAsync(reader, LoadOptions.None, cancellationToken)
            .ConfigureAwait(false);
    }

    private static async Task WriteEntryAsync(
        ZipArchive package,
        string entryName,
        Func<XmlWriter, CancellationToken, Task> write,
        CancellationToken cancellationToken)
    {
        var entry = package.CreateEntry(entryName, CompressionLevel.Fastest);
        await using var stream = entry.Open();
        using var writer = XmlWriter.Create(
            stream,
            new XmlWriterSettings
            {
                Async = true,
                Encoding = new UTF8Encoding(encoderShouldEmitUTF8Identifier: false),
                OmitXmlDeclaration = false,
                NewLineHandling = NewLineHandling.Entitize,
                Indent = false,
            });

        await writer.WriteStartDocumentAsync().ConfigureAwait(false);
        await write(writer, cancellationToken).ConfigureAwait(false);
        await writer.WriteEndDocumentAsync().ConfigureAwait(false);
        await writer.FlushAsync().ConfigureAwait(false);
    }

    private static async Task WriteContentTypesAsync(XmlWriter writer, CancellationToken cancellationToken)
    {
        await writer.WriteStartElementAsync(null, "Types", ContentTypesNamespace).ConfigureAwait(false);
        await WriteEmptyElementAsync(writer, "Default", ContentTypesNamespace, "Extension", "rels", "ContentType", "application/vnd.openxmlformats-package.relationships+xml").ConfigureAwait(false);
        await WriteEmptyElementAsync(writer, "Default", ContentTypesNamespace, "Extension", "xml", "ContentType", "application/xml").ConfigureAwait(false);
        await WriteEmptyElementAsync(writer, "Override", ContentTypesNamespace, "PartName", "/xl/workbook.xml", "ContentType", "application/vnd.openxmlformats-officedocument.spreadsheetml.sheet.main+xml").ConfigureAwait(false);
        await WriteEmptyElementAsync(writer, "Override", ContentTypesNamespace, "PartName", "/xl/worksheets/sheet1.xml", "ContentType", "application/vnd.openxmlformats-officedocument.spreadsheetml.worksheet+xml").ConfigureAwait(false);
        await writer.WriteEndElementAsync().ConfigureAwait(false);
    }

    private static async Task WritePackageRelationshipsAsync(XmlWriter writer, CancellationToken cancellationToken)
    {
        await writer.WriteStartElementAsync(null, "Relationships", RelationshipsNamespace).ConfigureAwait(false);
        await WriteEmptyElementAsync(writer, "Relationship", RelationshipsNamespace, "Id", "rId1", "Type", "http://schemas.openxmlformats.org/officeDocument/2006/relationships/officeDocument", "Target", "xl/workbook.xml").ConfigureAwait(false);
        await writer.WriteEndElementAsync().ConfigureAwait(false);
    }

    private static async Task WriteWorkbookAsync(XmlWriter writer, CancellationToken cancellationToken)
    {
        await writer.WriteStartElementAsync(null, "workbook", MainNamespace).ConfigureAwait(false);
        writer.WriteAttributeString("xmlns", "r", null, DocumentRelationshipsNamespace);
        await writer.WriteStartElementAsync(null, "sheets", MainNamespace).ConfigureAwait(false);
        await writer.WriteStartElementAsync(null, "sheet", MainNamespace).ConfigureAwait(false);
        writer.WriteAttributeString("name", "Annotations");
        writer.WriteAttributeString("sheetId", "1");
        writer.WriteAttributeString("r", "id", DocumentRelationshipsNamespace, "rId1");
        await writer.WriteEndElementAsync().ConfigureAwait(false);
        await writer.WriteEndElementAsync().ConfigureAwait(false);
        await writer.WriteEndElementAsync().ConfigureAwait(false);
    }

    private static async Task WriteWorkbookRelationshipsAsync(XmlWriter writer, CancellationToken cancellationToken)
    {
        await writer.WriteStartElementAsync(null, "Relationships", RelationshipsNamespace).ConfigureAwait(false);
        await WriteEmptyElementAsync(writer, "Relationship", RelationshipsNamespace, "Id", "rId1", "Type", "http://schemas.openxmlformats.org/officeDocument/2006/relationships/worksheet", "Target", "worksheets/sheet1.xml").ConfigureAwait(false);
        await writer.WriteEndElementAsync().ConfigureAwait(false);
    }

    private static async Task WriteWorksheetAsync(
        XmlWriter writer,
        IReadOnlyList<Annotation> annotations,
        CancellationToken cancellationToken)
    {
        await writer.WriteStartElementAsync(null, "worksheet", MainNamespace).ConfigureAwait(false);
        await writer.WriteStartElementAsync(null, "sheetData", MainNamespace).ConfigureAwait(false);
        await WriteHeaderRowAsync(writer).ConfigureAwait(false);

        for (var rowIndex = 0; rowIndex < annotations.Count; rowIndex++)
        {
            cancellationToken.ThrowIfCancellationRequested();
            var rowNumber = rowIndex + 2;
            await writer.WriteStartElementAsync(null, "row", MainNamespace).ConfigureAwait(false);
            writer.WriteAttributeString("r", rowNumber.ToString(CultureInfo.InvariantCulture));
            var annotation = annotations[rowIndex];
            await WriteInlineStringCellAsync(writer, 0, rowNumber, annotation.FormName ?? string.Empty).ConfigureAwait(false);
            await WriteIntegerCellAsync(writer, 1, rowNumber, annotation.PageNum).ConfigureAwait(false);
            await WriteInlineStringCellAsync(writer, 2, rowNumber, annotation.VarName).ConfigureAwait(false);
            await WriteInlineStringCellAsync(writer, 3, rowNumber, annotation.Text).ConfigureAwait(false);
            await WriteInlineStringCellAsync(writer, 4, rowNumber, annotation.Type).ConfigureAwait(false);
            await WriteInlineStringCellAsync(writer, 5, rowNumber, annotation.AnnotationText).ConfigureAwait(false);
            await WriteInlineStringCellAsync(writer, 6, rowNumber, annotation.AnnotationTextDomain).ConfigureAwait(false);
            await WriteBooleanCellAsync(writer, 7, rowNumber, annotation.AnnotationDotted).ConfigureAwait(false);
            await WriteIntegerCellAsync(writer, 8, rowNumber, annotation.X).ConfigureAwait(false);
            await WriteIntegerCellAsync(writer, 9, rowNumber, annotation.Y).ConfigureAwait(false);
            await WriteIntegerCellAsync(writer, 10, rowNumber, annotation.Width).ConfigureAwait(false);
            await WriteIntegerCellAsync(writer, 11, rowNumber, annotation.Line).ConfigureAwait(false);
            await WriteInlineStringCellAsync(writer, 12, rowNumber, annotation.FormOutline).ConfigureAwait(false);
            await WriteInlineStringCellAsync(writer, 13, rowNumber, annotation.AnnotationUniqueId).ConfigureAwait(false);
            await writer.WriteEndElementAsync().ConfigureAwait(false);
        }

        await writer.WriteEndElementAsync().ConfigureAwait(false);
        await writer.WriteEndElementAsync().ConfigureAwait(false);
    }

    private static async Task WriteHeaderRowAsync(XmlWriter writer)
    {
        await writer.WriteStartElementAsync(null, "row", MainNamespace).ConfigureAwait(false);
        writer.WriteAttributeString("r", "1");
        for (var column = 0; column < Headers.Length; column++)
            await WriteInlineStringCellAsync(writer, column, 1, Headers[column]).ConfigureAwait(false);
        await writer.WriteEndElementAsync().ConfigureAwait(false);
    }

    private static async Task WriteInlineStringCellAsync(
        XmlWriter writer,
        int columnIndex,
        int rowNumber,
        string value)
    {
        await writer.WriteStartElementAsync(null, "c", MainNamespace).ConfigureAwait(false);
        writer.WriteAttributeString("r", GetCellReference(columnIndex, rowNumber));
        writer.WriteAttributeString("t", "inlineStr");
        await writer.WriteStartElementAsync(null, "is", MainNamespace).ConfigureAwait(false);
        await writer.WriteStartElementAsync(null, "t", MainNamespace).ConfigureAwait(false);
        writer.WriteAttributeString("xml", "space", XmlNamespace, "preserve");
        await writer.WriteStringAsync(value ?? string.Empty).ConfigureAwait(false);
        await writer.WriteEndElementAsync().ConfigureAwait(false);
        await writer.WriteEndElementAsync().ConfigureAwait(false);
        await writer.WriteEndElementAsync().ConfigureAwait(false);
    }

    private static async Task WriteIntegerCellAsync(
        XmlWriter writer,
        int columnIndex,
        int rowNumber,
        int value)
    {
        await writer.WriteStartElementAsync(null, "c", MainNamespace).ConfigureAwait(false);
        writer.WriteAttributeString("r", GetCellReference(columnIndex, rowNumber));
        await writer.WriteStartElementAsync(null, "v", MainNamespace).ConfigureAwait(false);
        await writer.WriteStringAsync(value.ToString(CultureInfo.InvariantCulture)).ConfigureAwait(false);
        await writer.WriteEndElementAsync().ConfigureAwait(false);
        await writer.WriteEndElementAsync().ConfigureAwait(false);
    }

    private static async Task WriteBooleanCellAsync(
        XmlWriter writer,
        int columnIndex,
        int rowNumber,
        bool value)
    {
        await writer.WriteStartElementAsync(null, "c", MainNamespace).ConfigureAwait(false);
        writer.WriteAttributeString("r", GetCellReference(columnIndex, rowNumber));
        writer.WriteAttributeString("t", "b");
        await writer.WriteStartElementAsync(null, "v", MainNamespace).ConfigureAwait(false);
        await writer.WriteStringAsync(value ? "1" : "0").ConfigureAwait(false);
        await writer.WriteEndElementAsync().ConfigureAwait(false);
        await writer.WriteEndElementAsync().ConfigureAwait(false);
    }

    private static async Task WriteEmptyElementAsync(
        XmlWriter writer,
        string localName,
        string namespaceName,
        params string[] attributes)
    {
        await writer.WriteStartElementAsync(null, localName, namespaceName).ConfigureAwait(false);
        for (var index = 0; index + 1 < attributes.Length; index += 2)
            writer.WriteAttributeString(attributes[index], attributes[index + 1]);
        await writer.WriteEndElementAsync().ConfigureAwait(false);
    }

    private static string GetCellReference(int columnIndex, int rowNumber)
    {
        var value = columnIndex + 1;
        var builder = new StringBuilder();
        while (value > 0)
        {
            value--;
            builder.Insert(0, (char)('A' + value % 26));
            value /= 26;
        }

        return builder.Append(rowNumber.ToString(CultureInfo.InvariantCulture)).ToString();
    }

    private readonly record struct CellValue(int ColumnIndex, string Value);
}
