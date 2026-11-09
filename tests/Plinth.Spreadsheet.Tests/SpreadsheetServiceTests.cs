using System.IO.Compression;
using System.Security.Cryptography;
using System.Text;
using ClosedXML.Excel;
using Plinth.App.Modules.Demo.Services;
using Xunit;

namespace Plinth.Spreadsheet.Tests;

public sealed class SpreadsheetServiceTests : IDisposable
{
    private readonly string _directory = Path.Combine(Path.GetTempPath(), $"plinth-xlsx-{Guid.NewGuid():N}");
    private readonly SpreadsheetService _service = new();

    public SpreadsheetServiceTests() => Directory.CreateDirectory(_directory);
    public void Dispose() => Directory.Delete(_directory, recursive: true);

    private string Workbook(Action<IXLWorksheet> edit)
    {
        var path = Path.Combine(_directory, $"{Guid.NewGuid():N}.xlsx");
        using var book = new XLWorkbook();
        var sheet = book.Worksheets.Add("中文工作表");
        sheet.Cell("A1").Value = "编码";
        sheet.Cell("B1").Value = "名称";
        sheet.Cell("C1").Value = "备注";
        sheet.Cell("A2").Value = "001234567890123456789";
        sheet.Cell("B2").Value = "苹果";
        edit(sheet);
        book.SaveAs(path);
        return path;
    }

    private static void AddPart(string path, string name, string value)
    {
        using var zip = ZipFile.Open(path, ZipArchiveMode.Update);
        using var writer = new StreamWriter(zip.CreateEntry(name).Open(), new UTF8Encoding(false));
        writer.Write(value);
    }

    [Fact]
    public async Task Chinese_long_identifiers_newlines_and_empty_remark_roundtrip_as_text()
    {
        IReadOnlyList<string>[] rows =
        [
            ["0012345678901234567890123456789", "中文😀", "第一行\r\n第二行"],
            ["00002", "空备注", ""],
        ];
        var path = await _service.ExportAsync(rows, _directory);
        var result = await _service.ImportAsync(path);
        Assert.Empty(result.Errors);
        Assert.Equal(3, result.Rows.Count);
        Assert.Equal(rows[0], result.Rows[1]);
        Assert.Equal(rows[1], result.Rows[2]);
        Assert.Equal(new long[] { 1, 2, 3 }, result.RowNumbers);
        Assert.Empty(Directory.GetFiles(_directory, "*.tmp"));
    }

    [Theory]
    [InlineData("=1+2")]
    [InlineData("+123")]
    [InlineData("-123")]
    [InlineData("@SUM(A1:A2)")]
    [InlineData(" \t=HYPERLINK(\"https://example.org\")")]
    [InlineData("\t=1")]
    [InlineData("'literal")]
    [InlineData("''double")]
    [InlineData("'=1+2")]
    public async Task Formula_like_strings_are_literal_and_preserved(string value)
    {
        var path = await _service.ExportAsync([new[] { "001", value, value }], _directory);
        using (var book = new XLWorkbook(path))
        {
            var cell = book.Worksheet(1).Cell("B2");
            Assert.False(cell.HasFormula);
            Assert.Equal(XLDataType.Text, cell.DataType);
            Assert.Equal(value, cell.GetString());
        }
        var result = await _service.ImportAsync(path);
        Assert.Empty(result.Errors);
        Assert.Equal(value, result.Rows[1][1]);
        using var zip = ZipFile.OpenRead(path);
        using var reader = new StreamReader(zip.GetEntry("xl/worksheets/sheet1.xml")!.Open());
        Assert.DoesNotContain("<f", await reader.ReadToEndAsync());
    }

    [Fact]
    public async Task Preview_validates_rows_outside_sample_and_does_not_create_files()
    {
        var path = Workbook(s =>
        {
            s.Cell("A3").Value = "002";
            s.Cell("B3").Value = ""; // Invalid row beyond the sample.
        });
        var preview = await _service.PreviewAsync(path, sampleRows: 1);
        Assert.Equal(2, preview.Rows.Count);
        Assert.Contains(preview.Errors, e => e.Row == 3 && e.Code == "REQUIRED");
        Assert.Single(Directory.GetFiles(_directory));
    }

    [Fact]
    public async Task Validation_reports_physical_rows_lengths_and_case_insensitive_duplicates()
    {
        var path = Workbook(s =>
        {
            s.Cell("A2").Value = " A ";
            s.Cell("A3").Value = "a";
            s.Cell("B3").Value = new string('中', 201);
            s.Cell("C3").Value = new string('备', 501);
            s.Cell("A4").Value = new string('x', 51);
            s.Cell("B4").Value = " ";
        });
        var result = await _service.ImportAsync(path);
        Assert.Contains(result.Errors, e => e.Row == 3 && e.Code == "DUPLICATE");
        Assert.Equal(3, result.Errors.Count(e => e.Code == "LENGTH"));
        Assert.Contains(result.Errors, e => e.Row == 4 && e.Code == "REQUIRED");
    }

    [Theory]
    [InlineData("number")]
    [InlineData("date")]
    [InlineData("boolean")]
    public async Task Nontext_cells_are_rejected_instead_of_coerced(string type)
    {
        var path = Workbook(s =>
        {
            if (type == "number") s.Cell("A2").Value = 12345678901234567890d;
            if (type == "date") s.Cell("A2").Value = new DateTime(2026, 11, 1);
            if (type == "boolean") s.Cell("A2").Value = true;
        });
        var result = await _service.ImportAsync(path);
        Assert.Contains(result.Errors, e => e.Row == 2 && e.Code == "CELL_TYPE");
    }

    [Fact]
    public async Task Unicode_case_pairs_roundtrip_and_convert_using_the_same_ascii_identity_rules()
    {
        IReadOnlyList<string>[] rows =
        [
            ["Ä", "one", ""],
            ["ä", "two", ""],
            ["Σ", "three", ""],
            ["σ", "four", ""],
        ];
        var path = await _service.ExportAsync(rows, _directory);
        var batch = await _service.ImportAsync(path);
        Assert.Empty(batch.Errors);
        var csv = await _service.PrepareImportCsvAsync(batch, _directory);
        var converted = await CsvReader.ReadFileAsync(csv, "utf-8");
        Assert.Empty(converted.Errors);
        Assert.Equal(rows.Select(row => row[0]), converted.Rows.Skip(1).Select(row => row[0]));
        await Assert.ThrowsAsync<InvalidDataException>(() =>
            _service.ExportAsync([new[] { "A", "one", "" }, new[] { "a", "two", "" }], _directory));
    }

    [Fact]
    public async Task Formula_cells_are_rejected_even_when_they_have_cached_values()
    {
        var path = Workbook(s => s.Cell("C2").FormulaA1 = "1+2");
        await Assert.ThrowsAsync<InvalidDataException>(() => _service.ImportAsync(path));
    }

    [Fact]
    public async Task Header_extra_columns_empty_workbook_and_extra_sheets_are_rejected()
    {
        var badHeader = Workbook(s => s.Cell("A1").Value = "Unknown");
        Assert.Contains((await _service.ImportAsync(badHeader)).Errors, e => e.Code == "HEADER");
        var extraColumn = Workbook(s => s.Cell("D2").Value = "hidden data");
        await Assert.ThrowsAsync<InvalidDataException>(() => _service.ImportAsync(extraColumn));
        var empty = Workbook(s => s.Row(2).Clear());
        Assert.Contains((await _service.ImportAsync(empty)).Errors, e => e.Code == "EMPTY");
        var extraSheet = Workbook(s => s.Workbook.Worksheets.Add("额外表"));
        await Assert.ThrowsAsync<InvalidDataException>(() => _service.ImportAsync(extraSheet));
    }

    [Fact]
    public async Task Sparse_row_addresses_are_rejected_before_closedxml_loads()
    {
        var path = Workbook(s =>
        {
            s.Cell("A5002").Value = "too many";
            s.Cell("B5002").Value = "超限";
        });
        await Assert.ThrowsAsync<InvalidDataException>(() => _service.ImportAsync(path));
    }

    [Fact]
    public async Task Maximum_row_count_roundtrips_and_one_extra_row_fails_without_artifacts()
    {
        var rows = Enumerable.Range(0, SpreadsheetService.MaxRows)
            .Select(i => (IReadOnlyList<string>)new[] { i.ToString("D5"), "中文", "" });
        var path = await _service.ExportAsync(rows, _directory);
        var result = await _service.ImportAsync(path);
        Assert.Empty(result.Errors);
        Assert.Equal(SpreadsheetService.MaxRows + 1, result.Rows.Count);
        var extra = rows.Append(new[] { "extra", "超限", "" });
        await Assert.ThrowsAsync<InvalidDataException>(() => _service.ExportAsync(extra, _directory));
        Assert.Single(Directory.GetFiles(_directory));
    }

    [Fact]
    public async Task File_size_corruption_and_disguised_extensions_are_rejected()
    {
        var huge = Path.Combine(_directory, "huge.xlsx");
        using (var file = File.Create(huge)) file.SetLength(SpreadsheetService.MaxBytes + 1L);
        await Assert.ThrowsAsync<InvalidDataException>(() => _service.ImportAsync(huge));
        var corrupt = Path.Combine(_directory, "corrupt.xlsx");
        await File.WriteAllTextAsync(corrupt, "not a ZIP workbook");
        await Assert.ThrowsAsync<InvalidDataException>(() => _service.ImportAsync(corrupt));
        await Assert.ThrowsAsync<InvalidDataException>(() => _service.ImportAsync("file.xlsm"));
    }

    [Theory]
    [InlineData("../escape.xml", "<x/>")]
    [InlineData("xl/vbaProject.bin", "macro")]
    [InlineData("xl/evil.xml", "<!DOCTYPE x [<!ENTITY a 'text'>]><x>&a;</x>")]
    [InlineData("xl/evil.rels", "<Relationships xmlns='http://schemas.openxmlformats.org/package/2006/relationships'><Relationship Id='rId1' Type='link' Target='https://example.org' TargetMode='External'/></Relationships>")]
    public async Task Unsafe_package_parts_are_rejected(string name, string value)
    {
        var path = Workbook(_ => { });
        AddPart(path, name, value);
        await Assert.ThrowsAsync<InvalidDataException>(() => _service.ImportAsync(path));
    }

    [Fact]
    public async Task Office_directory_entries_are_allowed_but_carrying_data_is_not()
    {
        // 真实 Excel 保存的包会写 xl/、docProps/ 这类零长度目录条目；旧白名单把它们当成非法部件。
        var office = Workbook(_ => { });
        using (var zip = ZipFile.Open(office, ZipArchiveMode.Update))
            zip.CreateEntry("xl/themes/");
        var result = await _service.ImportAsync(office);
        Assert.Empty(result.Errors);
        Assert.Equal(2, result.Rows.Count);

        var payload = Workbook(_ => { });
        using (var zip = ZipFile.Open(payload, ZipArchiveMode.Update))
        {
            using var stream = zip.CreateEntry("xl/payload/").Open();
            stream.WriteByte(1);
        }
        await Assert.ThrowsAsync<InvalidDataException>(() => _service.ImportAsync(payload));
    }

    [Fact]
    public async Task Compressed_bomb_entry_count_and_duplicate_parts_are_rejected()
    {
        var bomb = Workbook(_ => { });
        AddPart(bomb, "xl/bomb.xml", "<x>" + new string('x', SpreadsheetService.MaxExpandedBytes) + "</x>");
        Assert.True(new FileInfo(bomb).Length < SpreadsheetService.MaxBytes);
        await Assert.ThrowsAsync<InvalidDataException>(() => _service.ImportAsync(bomb));
        var many = Workbook(_ => { });
        using (var zip = ZipFile.Open(many, ZipArchiveMode.Update))
            for (var i = 0; i < SpreadsheetService.MaxEntries; i++)
            {
                using var writer = new StreamWriter(zip.CreateEntry($"extra/{i}.xml").Open());
                writer.Write("<x/>");
            }
        await Assert.ThrowsAsync<InvalidDataException>(() => _service.ImportAsync(many));
        var duplicate = Workbook(_ => { });
        AddPart(duplicate, "xl/workbook.xml", "<x/>");
        await Assert.ThrowsAsync<InvalidDataException>(() => _service.ImportAsync(duplicate));
    }

    [Fact]
    public async Task Cancellation_before_operations_and_during_enumeration_leaves_no_output()
    {
        using var cancelled = new CancellationTokenSource();
        cancelled.Cancel();
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => _service.ImportAsync("unused.xlsx", cancelled.Token));
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => _service.PreviewAsync("unused.xlsx", cancellationToken: cancelled.Token));
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => _service.ExportAsync([], _directory, cancelled.Token));
        using var mid = new CancellationTokenSource();
        IEnumerable<IReadOnlyList<string>> Rows()
        {
            yield return new[] { "001", "中文", "" };
            mid.Cancel();
            yield return new[] { "002", "取消", "" };
        }
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => _service.ExportAsync(Rows(), _directory, mid.Token));
        Assert.Empty(Directory.GetFiles(_directory));
    }

    [Fact]
    public async Task Failed_enumeration_invalid_fields_and_empty_exports_leave_no_files()
    {
        IEnumerable<IReadOnlyList<string>> Failing()
        {
            yield return new[] { "001", "中文", "" };
            throw new IOException("Source failed.");
        }
        await Assert.ThrowsAsync<IOException>(() => _service.ExportAsync(Failing(), _directory));
        await Assert.ThrowsAsync<InvalidDataException>(() => _service.ExportAsync([new[] { "", "中文", "" }], _directory));
        await Assert.ThrowsAsync<InvalidDataException>(() => _service.ExportAsync([new[] { "001", "\0", "" }], _directory));
        await Assert.ThrowsAsync<InvalidDataException>(() => _service.ExportAsync([], _directory));
        Assert.Empty(Directory.GetFiles(_directory));
    }

    [Fact]
    public async Task Oversized_saved_export_deletes_the_already_written_temporary_file()
    {
        // Nonrepeating CJK text exceeds the compressed limit within the permitted row/field limits.
        // This exercises cleanup after SaveAs, rather than only failures before opening the file.
        var random = new Random(20261101);
        string Text(int length) => new(Enumerable.Range(0, length)
            .Select(_ => (char)random.Next(0x4e00, 0x9fff)).ToArray());
        IEnumerable<IReadOnlyList<string>> Rows()
        {
            for (var i = 0; i < SpreadsheetService.MaxRows; i++)
                yield return new[] { i.ToString("D5"), Text(200), Text(500) };
        }
        await Assert.ThrowsAsync<InvalidDataException>(() => _service.ExportAsync(Rows(), _directory));
        Assert.Empty(Directory.GetFiles(_directory));
    }

    [Fact]
    public async Task Exports_use_unique_paths_and_csv_contract_preserves_formula_like_text()
    {
        var a = await _service.ExportAsync([new[] { "001", "=1+2", "'literal" }], _directory);
        var b = await _service.ExportAsync([new[] { "002", "中文", "" }], _directory);
        Assert.NotEqual(a, b);
        var result = await _service.ImportAsync(a);
        var csv = CsvWriter.Build(result.Rows[0], result.Rows.Skip(1));
        var back = CsvReader.Parse(csv);
        Assert.Equal(result.Rows[1], back[1]);
    }

    [Theory]
    [InlineData("=1+2")]
    [InlineData("'=1+2")]
    [InlineData("'+123")]
    [InlineData("'@SUM(A1:A2)")]
    [InlineData("'\t=1")]
    [InlineData("'literal")]
    [InlineData("''double")]
    [InlineData("中文😀,\"引号\"\r\n第二行")]
    [InlineData("  中文  ")]
    public async Task Preview_batch_conversion_preserves_Demo_trimmed_values(string value)
    {
        var source = await _service.ExportAsync([new[] { "001", value, value }], _directory);
        var batch = await _service.ImportAsync(source);
        var (csv, sha256) = await _service.PrepareConfirmedImportCsvAsync(batch, _directory);
        try
        {
            var bytes = await File.ReadAllBytesAsync(csv);
            Assert.Equal(Convert.ToHexString(SHA256.HashData(bytes)), sha256);
            Assert.Equal(new byte[] { 0xef, 0xbb, 0xbf }, bytes.Take(3).ToArray());
            var converted = await CsvReader.ReadFileAsync(csv, "utf-8", expectedSha256: sha256);
            Assert.Empty(converted.Errors);
            Assert.Equal(value.Trim(), converted.Rows[1][1].Trim());
            Assert.Equal(value.Trim(), converted.Rows[1][2].Trim());
        }
        finally { File.Delete(csv); }
    }

    [Fact]
    public async Task Conversion_rejects_invalid_batches_and_honors_cancellation_without_files()
    {
        var invalid = new CsvReadResult(
            [new[] { "Code", "Name", "Remark" }, new[] { "001", "中文", "" }],
            [new CsvIssue(2, "TYPE", "invalid")]);
        await Assert.ThrowsAsync<InvalidDataException>(() => _service.PrepareImportCsvAsync(invalid, _directory));
        await Assert.ThrowsAsync<InvalidDataException>(() => _service.PrepareConfirmedImportCsvAsync(invalid, _directory));
        using var cancelled = new CancellationTokenSource();
        cancelled.Cancel();
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() =>
            _service.PrepareImportCsvAsync(invalid, _directory, cancelled.Token));
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() =>
            _service.PrepareConfirmedImportCsvAsync(invalid, _directory, cancelled.Token));
        Assert.Empty(Directory.GetFiles(_directory));
    }

    [Theory]
    [InlineData(",")]
    [InlineData(";")]
    [InlineData("\t")]
    [InlineData("|")]
    public async Task Writer_hash_covers_BOM_quoting_protection_and_all_serialized_bytes(string delimiter)
    {
        var path = Path.Combine(_directory, "hashed.csv");
        IReadOnlyList<string> header = ["编码", "名称", "备注"];
        IReadOnlyList<string>[] rows =
        [
            ["001", "=1+2", "中文,\"引号\";\t|换行\r\n第二行"],
            ["002", "'=1", "''字面单引号"],
            ["003", "@SUM(A1:A2)", new string('中', 4096)],
        ];
        var enumerations = 0;
        IEnumerable<IReadOnlyList<string>> Once()
        {
            Assert.Equal(1, ++enumerations);
            foreach (var row in rows) yield return row;
        }
        var hash = await CsvWriter.WriteFileWithHashAsync(path, header, Once(), delimiter: delimiter);
        var bytes = await File.ReadAllBytesAsync(path);
        var expected = new UTF8Encoding(true).GetPreamble()
            .Concat(Encoding.UTF8.GetBytes(CsvWriter.Build(header, rows, delimiter: delimiter))).ToArray();
        Assert.Equal(expected, bytes);
        Assert.Equal(Convert.ToHexString(SHA256.HashData(bytes)), hash);
        var read = await CsvReader.ReadFileAsync(path, "utf-8", delimiter, expectedSha256: hash);
        Assert.Empty(read.Errors);
        Assert.Equal(header, read.Rows[0]);
        for (var i = 0; i < rows.Length; i++) Assert.Equal(rows[i], read.Rows[i + 1]);

        var compatible = Path.Combine(_directory, "compatible.csv");
        await CsvWriter.WriteFileAsync(compatible, header, rows, delimiter: delimiter);
        Assert.Equal(bytes, await File.ReadAllBytesAsync(compatible));
        Assert.Empty(Directory.GetFiles(_directory, "*.tmp"));
    }

    [Fact]
    public async Task Writer_hash_cancellation_and_enumeration_failure_leave_no_partial_files()
    {
        var path = Path.Combine(_directory, "cancelled.csv");
        using var cancelled = new CancellationTokenSource();
        cancelled.Cancel();
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() =>
            CsvWriter.WriteFileWithHashAsync(path, ["Code", "Name", "Remark"],
                new[] { (IReadOnlyList<string>)new[] { "001", "中文", "" } }, cancelled.Token));
        using var mid = new CancellationTokenSource();
        IEnumerable<IReadOnlyList<string>> Cancelling()
        {
            yield return new[] { "001", new string('中', 4096), "" };
            mid.Cancel();
            // Cancellation after the last row must also prevent publication.
        }
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() =>
            CsvWriter.WriteFileWithHashAsync(path, ["Code", "Name", "Remark"], Cancelling(), mid.Token));
        IEnumerable<IReadOnlyList<string>> Failing()
        {
            yield return new[] { "001", "中文", "" };
            throw new IOException("Enumeration failed.");
        }
        await Assert.ThrowsAsync<IOException>(() =>
            CsvWriter.WriteFileWithHashAsync(path, ["Code", "Name", "Remark"], Failing()));
        Assert.Empty(Directory.GetFiles(_directory));
    }

    [Fact]
    public async Task Confirmed_conversion_rejects_oversized_serialized_CSV_and_cleans_up()
    {
        // Valid field/row limits can still exceed 5 MiB after UTF-8 serialization.
        var rows = new List<IReadOnlyList<string>> { new[] { "Code", "Name", "Remark" } };
        rows.AddRange(Enumerable.Range(0, 2600).Select(i =>
            (IReadOnlyList<string>)new[] { i.ToString("D5"), new string('中', 200), new string('文', 500) }));
        var batch = new CsvReadResult(rows, []);
        await Assert.ThrowsAsync<InvalidDataException>(() =>
            _service.PrepareConfirmedImportCsvAsync(batch, _directory));
        Assert.Empty(Directory.GetFiles(_directory));
    }

    [Fact]
    public async Task Confirmed_conversion_hash_rejects_an_independently_valid_replacement()
    {
        var source = await _service.ExportAsync([new[] { "A001", "预览内容", "'=1+2" }], _directory);
        var batch = await _service.ImportAsync(source);
        File.Delete(source);
        var (path, sha256) = await _service.PrepareConfirmedImportCsvAsync(batch, _directory);
        var original = await CsvReader.ReadFileAsync(path, "utf-8", expectedSha256: sha256);
        Assert.Empty(original.Errors);
        Assert.Equal("A001", original.Rows[1][0]);
        await File.WriteAllTextAsync(path, CsvWriter.Build(
            ["Code", "Name", "Remark"], [new[] { "B001", "合法替换", "" }]), new UTF8Encoding(true));
        Assert.Empty((await CsvReader.ReadFileAsync(path, "utf-8")).Errors);
        var error = await Assert.ThrowsAsync<InvalidDataException>(() =>
            CsvReader.ReadFileAsync(path, "utf-8", expectedSha256: sha256));
        Assert.Equal("CSV_CHANGED_AFTER_PREVIEW", error.Message);
    }

    [Fact]
    public async Task Real_excel_macro_workbook_is_rejected()
    {
        var sample = Environment.GetEnvironmentVariable("PLINTH_XLSM_SAMPLE");
        Assert.SkipWhen(string.IsNullOrWhiteSpace(sample) || !File.Exists(sample),
            "Set PLINTH_XLSM_SAMPLE to a real Excel macro-enabled workbook for the optional Office check.");
        await Assert.ThrowsAsync<InvalidDataException>(() => _service.ImportAsync(sample!));
    }
}
