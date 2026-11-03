#if PLINTH_XLSX
using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.IO.Compression;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using System.Xml;
using ClosedXML.Excel;
using Plinth.App.Data;

namespace Plinth.App.Modules.Demo.Services;

/// <summary>
/// Optional, bounded XLSX transport for Demo. Import/preview never write to the database.
/// Rows include the header; any Errors invalidate the entire batch, including a preview.
/// Enable PLINTH_XLSX and reference ClosedXML 0.105.1 before registering this service.
/// </summary>
public sealed class SpreadsheetService
{
    public const int MaxRows = 5000;
    public const int MaxBytes = 5 * 1024 * 1024;
    public const int MaxExpandedBytes = 32 * 1024 * 1024;
    public const int MaxEntries = 256;
    public const int MaxErrors = 100;
    // Match DemoService's field limits without coupling the transport to its DB/DI dependencies.
    private static readonly int[] FieldLimits = [50, 200, 500];
    private const string MainNamespace = "http://schemas.openxmlformats.org/spreadsheetml/2006/main";
    private const string RelationshipNamespace = "http://schemas.openxmlformats.org/package/2006/relationships";

    public Task<CsvReadResult> ImportAsync(string path, CancellationToken cancellationToken = default) =>
        Task.Run(() => Read(path, cancellationToken), cancellationToken);

    /// <summary>Validates the complete bounded workbook before returning up to sampleRows data rows.</summary>
    public async Task<CsvReadResult> PreviewAsync(string path, int sampleRows = 20,
        CancellationToken cancellationToken = default)
    {
        if (sampleRows is < 1 or > MaxRows) throw new ArgumentOutOfRangeException(nameof(sampleRows));
        var result = await ImportAsync(path, cancellationToken).ConfigureAwait(false);
        cancellationToken.ThrowIfCancellationRequested();
        return new(result.Rows.Take(sampleRows + 1).ToArray(), result.Errors)
        {
            RowNumbers = result.RowNumbers.Take(sampleRows + 1).ToArray(),
        };
    }

    /// <summary>
    /// Writes Code/Name/Remark as text, even for =,+,-,@ and leading apostrophes.
    /// Publishes a unique file only after validation/save/size checks; failure deletes the temporary file.
    /// Enumerating rows and ClosedXML work run off the caller's UI thread.
    /// </summary>
    public Task<string> ExportAsync(IEnumerable<IReadOnlyList<string>> rows, string directory,
        CancellationToken cancellationToken = default) =>
        Task.Run(() => Export(rows, directory, cancellationToken), cancellationToken);

    /// <summary>Convert the exact preview batch. Caller deletes the returned CSV after importing it.</summary>
    public async Task<string> PrepareImportCsvAsync(CsvReadResult batch, string directory,
        CancellationToken cancellationToken = default) =>
        (await PrepareConfirmedImportCsvAsync(batch, directory, cancellationToken).ConfigureAwait(false)).Path;

    /// <summary>Bind the confirmed batch to its serialized bytes. Caller deletes the returned CSV.</summary>
    public async Task<(string Path, string Sha256)> PrepareConfirmedImportCsvAsync(CsvReadResult batch, string directory,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(batch);
        cancellationToken.ThrowIfCancellationRequested();
        if (batch.Errors.Count != 0 || batch.Rows.Count is < 2 or > MaxRows + 1 ||
            batch.Rows[0].Count != 3 || !ValidHeader(batch.Rows[0]))
            throw new InvalidDataException("Only a complete validated XLSX batch can be converted.");
        var seen = new HashSet<string>(StringComparer.Ordinal);
        var errors = new List<CsvIssue>();
        for (var i = 1; i < batch.Rows.Count; i++)
        {
            cancellationToken.ThrowIfCancellationRequested();
            if (batch.Rows[i].Count != 3 || batch.Rows[i].Any(v => v is null))
                throw new InvalidDataException("Exactly three text columns are required.");
            ValidateRow(batch.Rows[i], i + 1, seen, errors);
        }
        if (errors.Count != 0) throw new InvalidDataException("Invalid XLSX batch.");
        Directory.CreateDirectory(directory);
        var path = Path.Combine(directory, $"xlsx-import-{Guid.NewGuid():N}.csv");
        var complete = false;
        try
        {
            // CsvReader removes a leading apostrophe before formula-like text. A surrounding
            // space prevents that ambiguous unprotection; Demo's existing Trim removes the
            // space afterwards and preserves the original literal apostrophe.
            var csvRows = batch.Rows.Skip(1).Select(row => (IReadOnlyList<string>)row
                .Select(value => value.StartsWith('\'') ? " " + value : value).ToArray());
            var sha256 = await CsvWriter.WriteFileWithHashAsync(path, ["Code", "Name", "Remark"], csvRows,
                cancellationToken).ConfigureAwait(false);
            cancellationToken.ThrowIfCancellationRequested();
            if (new FileInfo(path).Length > CsvReader.MaxBytes)
                throw new InvalidDataException("Converted CSV exceeds the Demo import size limit.");
            complete = true;
            return (path, sha256);
        }
        finally
        {
            if (!complete && File.Exists(path)) File.Delete(path);
        }
    }

    private static CsvReadResult Read(string path, CancellationToken token)
    {
        token.ThrowIfCancellationRequested();
        if (!string.Equals(Path.GetExtension(path), ".xlsx", StringComparison.OrdinalIgnoreCase))
            throw new InvalidDataException("Only .xlsx files are supported.");

        // Read a bounded snapshot: preflight and ClosedXML must inspect exactly the same bytes.
        using var snapshot = new MemoryStream();
        using (var file = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.Read))
        {
            if (file.Length > MaxBytes) throw new InvalidDataException("XLSX file size limit exceeded.");
            var buffer = new byte[8192];
            int count;
            while ((count = file.Read(buffer, 0, buffer.Length)) != 0)
            {
                token.ThrowIfCancellationRequested();
                if (snapshot.Length + count > MaxBytes)
                    throw new InvalidDataException("XLSX file size limit exceeded.");
                snapshot.Write(buffer, 0, count);
            }
        }

        try
        {
            Preflight(snapshot, token);
            snapshot.Position = 0;
            token.ThrowIfCancellationRequested();
            // ClosedXML load/save are synchronous and have no cancellation overload.
            using var workbook = new XLWorkbook(snapshot);
            token.ThrowIfCancellationRequested();
            if (workbook.Worksheets.Count != 1)
                throw new InvalidDataException("Exactly one worksheet is required.");
            var sheet = workbook.Worksheets.Single();
            var last = sheet.LastRowUsed(XLCellsUsedOptions.Contents)?.RowNumber() ?? 0;
            var lastColumn = sheet.LastColumnUsed(XLCellsUsedOptions.Contents)?.ColumnNumber() ?? 0;
            if (last > MaxRows + 1 || lastColumn > 3)
                throw new InvalidDataException("XLSX row or column limit exceeded.");
            var rows = new List<IReadOnlyList<string>>();
            var numbers = new List<long>();
            var errors = new List<CsvIssue>();
            if (last < 2) errors.Add(new(1, "EMPTY", "A header and at least one data row are required."));
            var seen = new HashSet<string>(StringComparer.Ordinal);
            for (var row = 1; row <= last; row++)
            {
                token.ThrowIfCancellationRequested();
                var values = new string[3];
                for (var col = 1; col <= 3; col++)
                {
                    var cell = sheet.Cell(row, col);
                    // Do not read formatted values, cached formulas, dates or rounded numeric identifiers.
                    if (cell.HasFormula || cell.DataType is not (XLDataType.Text or XLDataType.Blank))
                    {
                        AddError(errors, row, "CELL_TYPE", $"Column {col} must contain literal text.");
                        values[col - 1] = "";
                    }
                    else values[col - 1] = NormalizeNewLines(cell.GetString());
                }
                if (row == 1)
                {
                    if (!ValidHeader(values))
                        AddError(errors, row, "HEADER", "Expected Code,Name,Remark or 编码,名称,备注.");
                }
                else ValidateRow(values, row, seen, errors);
                rows.Add(Array.AsReadOnly(values));
                numbers.Add(row);
            }
            return new(rows.AsReadOnly(), errors.AsReadOnly()) { RowNumbers = numbers.AsReadOnly() };
        }
        catch (OperationCanceledException) { throw; }
        catch (InvalidDataException) { throw; }
        catch (Exception ex) when (ex is XmlException or FormatException or ArgumentException
                                   or InvalidOperationException or DocumentFormat.OpenXml.Packaging.OpenXmlPackageException)
        {
            throw new InvalidDataException("Invalid or unsupported XLSX workbook.", ex);
        }
    }

    private static string Export(IEnumerable<IReadOnlyList<string>> rows, string directory, CancellationToken token)
    {
        ArgumentNullException.ThrowIfNull(rows);
        token.ThrowIfCancellationRequested();
        Directory.CreateDirectory(directory);
        var target = Path.Combine(Path.GetFullPath(directory), $"demo-{Guid.NewGuid():N}.xlsx");
        var temporary = target + ".tmp";
        try
        {
            using var workbook = new XLWorkbook();
            var sheet = workbook.Worksheets.Add("示例数据");
            WriteText(sheet, 1, ["Code", "Name", "Remark"]);
            var seen = new HashSet<string>(StringComparer.Ordinal);
            var errors = new List<CsvIssue>();
            var row = 1;
            foreach (var values in rows)
            {
                token.ThrowIfCancellationRequested();
                if (++row > MaxRows + 1) throw new InvalidDataException("XLSX row limit exceeded.");
                if (values is null || values.Count != 3 || values.Any(v => v is null))
                    throw new InvalidDataException("Exactly three non-null text fields are required.");
                ValidateRow(values, row, seen, errors);
                if (errors.Count != 0) throw new InvalidDataException($"Invalid export row {row}: {errors[0].Code}.");
                WriteText(sheet, row, values);
            }
            if (row == 1) throw new InvalidDataException("At least one data row is required.");
            token.ThrowIfCancellationRequested();
            using (var output = new FileStream(temporary, FileMode.CreateNew, FileAccess.Write, FileShare.None))
                workbook.SaveAs(output, new SaveOptions { EvaluateFormulasBeforeSaving = false });
            token.ThrowIfCancellationRequested();
            // Apply the same package limits to exports; every successful export must be importable.
            using (var saved = new FileStream(temporary, FileMode.Open, FileAccess.Read, FileShare.Read))
            {
                if (saved.Length > MaxBytes) throw new InvalidDataException("XLSX file size limit exceeded.");
                Preflight(saved, token);
            }
            token.ThrowIfCancellationRequested();
            File.Move(temporary, target, overwrite: false);
            return target;
        }
        finally
        {
            if (File.Exists(temporary)) File.Delete(temporary);
        }
    }

    private static void WriteText(IXLWorksheet sheet, int row, IReadOnlyList<string> values)
    {
        for (var col = 1; col <= 3; col++)
        {
            var cell = sheet.Cell(row, col);
            cell.Style.NumberFormat.Format = "@";
            // The implicit string -> XLCellValue conversion is text-only. ClosedXML strips one
            // initial apostrophe in its Value setter, so double that prefix to preserve literal data.
            var value = values[col - 1];
            cell.Value = value.StartsWith('\'') ? "'" + value : value;
        }
    }

    // OpenXML 只保存 LF；导入时恢复为 Windows 文本使用的 CRLF，保证 CSV→XLSX→CSV 往返稳定。
    private static string NormalizeNewLines(string value) =>
        value.Contains('\n') ? value.Replace("\r\n", "\n").Replace("\n", "\r\n") : value;

    private static bool ValidHeader(IReadOnlyList<string> values) =>
        values.Select(v => v.Trim()).SequenceEqual(["Code", "Name", "Remark"], StringComparer.OrdinalIgnoreCase) ||
        values.Select(v => v.Trim()).SequenceEqual(["编码", "名称", "备注"], StringComparer.Ordinal);

    private static void ValidateRow(IReadOnlyList<string> values, long row, HashSet<string> seen, List<CsvIssue> errors)
    {
        for (var col = 0; col < 3; col++)
        {
            var value = values[col].Trim();
            if (col < 2 && value.Length == 0)
                AddError(errors, row, "REQUIRED", $"Column {col + 1} is required.");
            if (value.Length > FieldLimits[col] || values[col].Length > FieldLimits[col])
                AddError(errors, row, "LENGTH", $"Column {col + 1} exceeds {FieldLimits[col]} characters.");
            try { XmlConvert.VerifyXmlChars(values[col]); }
            catch (XmlException) { AddError(errors, row, "TEXT", $"Column {col + 1} contains invalid XML characters."); }
        }
        var code = values[0].Trim();
        if (!CodeIdentity.IsValid(code))
            AddError(errors, row, "TEXT", "Code contains invalid Unicode text.");
        else if (!seen.Add(CodeIdentity.Create(code)))
            AddError(errors, row, "DUPLICATE", "Duplicate code in workbook (ASCII case insensitive).");
    }

    private static void AddError(List<CsvIssue> errors, long row, string code, string message)
    {
        if (errors.Count < MaxErrors) errors.Add(new(row, code, message));
    }

    private static void Preflight(Stream snapshot, CancellationToken token)
    {
        snapshot.Position = 0;
        using var zip = new ZipArchive(snapshot, ZipArchiveMode.Read, leaveOpen: true);
        if (zip.Entries.Count is 0 or > MaxEntries)
            throw new InvalidDataException("XLSX ZIP entry limit exceeded.");
        long expanded = 0;
        var names = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (var entry in zip.Entries)
        {
            token.ThrowIfCancellationRequested();
            var name = entry.FullName;
            if (!names.Add(name) || name.StartsWith('/') || name.Contains('\\') || name.Contains(':') ||
                name.Split('/').Any(p => p is ".." or "."))
                throw new InvalidDataException("Unsafe or duplicate XLSX ZIP entry.");
            if (entry.Length > MaxExpandedBytes - expanded)
                throw new InvalidDataException("XLSX expanded size limit exceeded.");
            expanded += entry.Length;
            // Restrict to the basic OpenXML package; no macros, OLE, embedded files or executable payloads.
            // .psmdcp 是 System.IO.Packaging 写出的 OPC 核心属性(仍是 XML)，ClosedXML 0.105+ 会生成它。
            if (!(name.EndsWith(".xml", StringComparison.OrdinalIgnoreCase) ||
                  name.EndsWith(".rels", StringComparison.OrdinalIgnoreCase) ||
                  name.EndsWith(".psmdcp", StringComparison.OrdinalIgnoreCase)))
                throw new InvalidDataException("Only basic XML workbook parts are supported.");
            using var content = entry.Open();
            using var xml = XmlReader.Create(content, new XmlReaderSettings
            {
                DtdProcessing = DtdProcessing.Prohibit, XmlResolver = null,
                MaxCharactersInDocument = MaxExpandedBytes,
            });
            var rowCount = 0;
            var cellCount = 0;
            while (xml.Read())
            {
                token.ThrowIfCancellationRequested();
                if (xml.NodeType != XmlNodeType.Element) continue;
                if (xml.NamespaceURI == RelationshipNamespace && xml.LocalName == "Relationship" &&
                    string.Equals(xml.GetAttribute("TargetMode"), "External", StringComparison.OrdinalIgnoreCase))
                    throw new InvalidDataException("External workbook relationships are not supported.");
                if (xml.LocalName == "Override" &&
                    (xml.GetAttribute("ContentType") ?? "").Contains("macro", StringComparison.OrdinalIgnoreCase))
                    throw new InvalidDataException("Macro-enabled workbooks are not supported.");
                if (xml.NamespaceURI != MainNamespace) continue;
                if (xml.LocalName is "f" or "externalReference")
                    throw new InvalidDataException("Formula cells and external references are not supported.");
                if (xml.LocalName == "row")
                {
                    if (++rowCount > MaxRows + 1 || !int.TryParse(xml.GetAttribute("r"),
                            NumberStyles.None, CultureInfo.InvariantCulture, out var index) || index is < 1 or > MaxRows + 1)
                        throw new InvalidDataException("XLSX row limit or row address is invalid.");
                }
                if (xml.LocalName == "c")
                {
                    if (++cellCount > (MaxRows + 1) * 3 || !ValidAddress(xml.GetAttribute("r")))
                        throw new InvalidDataException("XLSX cell limit or cell address is invalid.");
                }
            }
        }
        if (!names.Contains("[Content_Types].xml") || !names.Contains("xl/workbook.xml") || !names.Contains("_rels/.rels"))
            throw new InvalidDataException("Not an XLSX workbook package.");
    }

    private static bool ValidAddress(string? address) =>
        address is { Length: >= 2 } && address[0] is >= 'A' and <= 'C' &&
        int.TryParse(address.AsSpan(1), NumberStyles.None, CultureInfo.InvariantCulture, out var row) &&
        row is >= 1 and <= MaxRows + 1;
}
#endif
