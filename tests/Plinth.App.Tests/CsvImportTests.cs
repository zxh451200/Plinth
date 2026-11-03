using System;
using System.IO;
using System.Linq;
using System.Threading.Tasks;
using System.Text;
using System.Threading;
using System.Collections.Generic;
using System.Runtime.InteropServices;
using System.Security.Cryptography;
using Microsoft.Extensions.Logging.Abstractions;
using Plinth.App.Localization;
using Plinth.App.Modules.Demo.Data;
using Plinth.App.Modules.Demo.Services;
using Xunit;

namespace Plinth.App.Tests;

/// <summary>CSV 导入:解析规则、新增与更新、整批取消、导出再导入内容不变。</summary>
public sealed class CsvImportTests : IDisposable
{
    private readonly TestDatabase _database = new();
    private readonly string _directory = Path.Combine(Path.GetTempPath(), "plinth-import-" + Guid.NewGuid().ToString("N"));

    public CsvImportTests() => Directory.CreateDirectory(_directory);

    public void Dispose()
    {
        _database.Dispose();
        Directory.Delete(_directory, recursive: true);
    }

    private DemoService CreateService() => new(_database.Factory, NullLogger<DemoService>.Instance);

    private string WriteCsv(string content)
    {
        var path = Path.Combine(_directory, Guid.NewGuid().ToString("N") + ".csv");
        File.WriteAllText(path, content);
        return path;
    }

    [Fact]
    public void Reader_handles_quotes_embedded_newlines_bom_and_blank_lines()
    {
        var rows = CsvReader.Parse("\uFEFFa,\"b,1\",\"say \"\"hi\"\"\"\r\n\r\nx,\"l1\nl2\",\r\n");

        Assert.Equal(2, rows.Count);
        Assert.Equal(["a", "b,1", "say \"hi\""], rows[0]);
        Assert.Equal(["x", "l1\nl2", ""], rows[1]);
    }

    [Fact]
    public async Task Legacy_encoding_requires_choice_and_preserves_long_identifiers()
    {
        Encoding.RegisterProvider(CodePagesEncodingProvider.Instance);
        var path = Path.Combine(_directory, "legacy.csv");
        await File.WriteAllBytesAsync(path, Encoding.GetEncoding("gb18030").GetBytes("编号,名称\r\n12345678901234567890,苹果\r\n"));
        var auto = await CsvReader.PreviewAsync(path);
        Assert.True(auto.NeedsChoice);
        Assert.Null(auto.Content);
        var chosen = await CsvReader.PreviewAsync(path, "gb18030");
        Assert.Equal("苹果", chosen.Content!.Rows[1][1]);
        var read = await CsvReader.ReadFileAsync(path, "gb18030");
        Assert.Empty(read.Errors);
        Assert.Equal("12345678901234567890", read.Rows[1][0]);
    }

    [Fact]
    public void Malformed_quoting_and_column_mismatch_are_reported()
    {
        using var input = new StringReader("code,name\r\nA,\"unterminated");
        Assert.NotEmpty(CsvReader.Read(input).Errors);
        using var columns = new StringReader("a,b\r\none\r\n");
        Assert.Contains(CsvReader.Read(columns).Errors, e => e.Code == "COLUMN_COUNT" && e.Row == 2);
        Assert.Throws<InvalidDataException>(() => CsvReader.Parse("a,b\r\nx,\"broken"));
        using var cancellation = new CancellationTokenSource();
        cancellation.Cancel();
        Assert.Throws<OperationCanceledException>(() => CsvReader.Parse("a,b\n1,2", cancellation.Token));
    }

    [Fact]
    public void Mapping_validates_types_and_custom_delimiter_supports_newlines_and_row_limit()
    {
        using var input = new StringReader("a;b\r\n1;\"two\nlines\"\r\n2;three\r\n");
        var result = CsvReader.Read(input, ";", maxRows: 2);
        Assert.Equal("two\nlines", result.Rows[1][1]);
        Assert.Contains(result.Errors, e => e.Code == "ROW_LIMIT");
        var mapped = CsvReader.MapAndValidate(result, [1, 0], [typeof(long), typeof(string)]);
        Assert.Contains(mapped.Errors, e => e.Code == "TYPE" && e.Row == 2);
    }

    [Fact]
    public void Error_rows_follow_physical_lines_after_multiline_cells()
    {
        using var input = new StringReader("a,b\r\n1,\"two\nlines\"\r\nwrong\r\n");
        var result = CsvReader.Read(input);
        Assert.Contains(result.Errors, e => e.Code == "COLUMN_COUNT" && e.Row == 4);
    }

    [Theory]
    [InlineData("utf-8")]
    [InlineData("utf-16")]
    [InlineData("utf-16be")]
    [InlineData("gb18030")]
    [InlineData("gbk")]
    public async Task Streaming_reader_preserves_strict_encodings_bom_and_multiline_fields(string name)
    {
        Encoding.RegisterProvider(CodePagesEncodingProvider.Instance);
        var encoding = Encoding.GetEncoding(name, EncoderFallback.ExceptionFallback, DecoderFallback.ExceptionFallback);
        // Cross both byte and character buffer boundaries inside a quoted field.
        var cell = new string('x', 8191) + "苹果\r\nsay \"hi\"";
        var text = "code;name\r\n\r\n12345678901234567890;\"" + cell.Replace("\"", "\"\"") + "\"\r\nwrong\r\n";
        var bytes = encoding.GetPreamble().Concat(encoding.GetBytes(text)).ToArray();
        var path = Path.Combine(_directory, name + ".csv");
        await File.WriteAllBytesAsync(path, bytes);
        var preview = await CsvReader.PreviewAsync(path, name, ";");
        var result = await CsvReader.ReadFileAsync(path, name, ";", expectedSha256: preview.Sha256);
        Assert.Equal(Convert.ToHexString(SHA256.HashData(bytes)), preview.Sha256);
        Assert.Equal("12345678901234567890", result.Rows[1][0]);
        Assert.Equal(cell, result.Rows[1][1]);
        Assert.Equal(new long[] { 1, 3, 5 }, result.RowNumbers);
        Assert.Contains(result.Errors, e => e.Code == "COLUMN_COUNT" && e.Row == 5);
    }

    [Theory]
    [InlineData("utf-8", 0xC3, false)]
    [InlineData("utf-8", 0xC3, true)]
    [InlineData("utf-16", 0x61, true)]
    [InlineData("utf-16be", 0x61, true)]
    [InlineData("gb18030", 0x81, false)]
    [InlineData("gbk", 0x81, false)]
    public async Task Invalid_encoding_after_preview_window_is_never_silently_replaced(
        string name, int lastByte, bool withBom)
    {
        Encoding.RegisterProvider(CodePagesEncodingProvider.Instance);
        var encoding = Encoding.GetEncoding(name);
        var bytes = (withBom ? encoding.GetPreamble() : Array.Empty<byte>()).Concat(encoding.GetBytes(
            "code,name\n" + string.Concat(Enumerable.Repeat("A,value\n", 2000)))).Append((byte)lastByte).ToArray();
        var path = Path.Combine(_directory, "invalid.csv");
        await File.WriteAllBytesAsync(path, bytes);
        await Assert.ThrowsAsync<DecoderFallbackException>(() => CsvReader.PreviewAsync(path, name));
        await Assert.ThrowsAsync<DecoderFallbackException>(() => CsvReader.ReadFileAsync(path, name));
        if (name == "utf-8" && !withBom)
        {
            var automatic = await CsvReader.PreviewAsync(path);
            Assert.Null(automatic.Content);
            Assert.True(automatic.NeedsChoice);
            Assert.Equal(Convert.ToHexString(SHA256.HashData(bytes)), automatic.Sha256);
        }
        else if (withBom)
            await Assert.ThrowsAsync<DecoderFallbackException>(() => CsvReader.PreviewAsync(path));
    }

    [Fact]
    public async Task Bom_selection_is_strict_and_utf32_is_rejected()
    {
        var path = Path.Combine(_directory, "bom.csv");
        foreach (var encoding in new Encoding[] { new UTF8Encoding(true), new UnicodeEncoding(false, true),
                     new UnicodeEncoding(true, true) })
        {
            await File.WriteAllBytesAsync(path, encoding.GetPreamble().Concat(encoding.GetBytes("a,b\n1,苹果")).ToArray());
            var preview = await CsvReader.PreviewAsync(path);
            Assert.False(preview.NeedsChoice);
            Assert.Single(preview.Candidates);
            Assert.Equal("苹果", preview.Content!.Rows[1][1]);
            var conflict = encoding.CodePage == 65001 ? "utf-16" : "utf-8";
            await Assert.ThrowsAsync<InvalidDataException>(() => CsvReader.PreviewAsync(path, conflict));
            await Assert.ThrowsAsync<InvalidDataException>(() => CsvReader.ReadFileAsync(path, conflict));
            await File.WriteAllBytesAsync(path, encoding.GetPreamble()
                .Concat(encoding.GetBytes("\uFEFFa,b\n1,value")).ToArray());
            // Exactly one leading BOM is metadata; the next U+FEFF is field content.
            Assert.Equal("\uFEFFa", (await CsvReader.PreviewAsync(path)).Content!.Rows[0][0]);
            Assert.Equal("\uFEFFa", (await CsvReader.ReadFileAsync(path, preview.Encoding!)).Rows[0][0]);
        }
        foreach (var encoding in new[] { new UTF32Encoding(false, true), new UTF32Encoding(true, true) })
        {
            await File.WriteAllBytesAsync(path, encoding.GetPreamble().Concat(encoding.GetBytes("a,b")).ToArray());
            await Assert.ThrowsAsync<InvalidDataException>(() => CsvReader.PreviewAsync(path));
            await Assert.ThrowsAsync<InvalidDataException>(() => CsvReader.ReadFileAsync(path, "utf-16"));
        }
    }

    [Fact]
    public async Task Preview_retains_only_header_and_twenty_rows_but_hashes_the_entire_file()
    {
        var text = "code,name\n" + string.Concat(Enumerable.Range(0, 20).Select(i => $"A{i},value\n"));
        // The first unshown record is deliberately too large to parse.
        var path = WriteCsv(text + "Z," + new string('x', CsvReader.MaxFieldLength + 1));
        var preview = await CsvReader.PreviewAsync(path, "utf-8");
        Assert.Equal(21, preview.Content!.Rows.Count);
        Assert.Empty(preview.Content.Errors);
        Assert.Equal(Convert.ToHexString(SHA256.HashData(await File.ReadAllBytesAsync(path))), preview.Sha256);
        Assert.NotEmpty((await CsvReader.ReadFileAsync(path, "utf-8")).Errors);
        await File.AppendAllTextAsync(path, "changed");
        var changed = await Assert.ThrowsAsync<InvalidDataException>(() =>
            CsvReader.ReadFileAsync(path, "utf-8", expectedSha256: preview.Sha256));
        Assert.Equal("CSV_CHANGED_AFTER_PREVIEW", changed.Message);
    }

    [Fact]
    public async Task Streaming_limits_include_header_and_check_size_before_reading()
    {
        var path = WriteCsv("code,name\n" + string.Concat(Enumerable.Repeat("A,value\n", 5000)));
        var result = await CsvReader.ReadFileAsync(path, "utf-8");
        Assert.Equal(5001, result.Rows.Count);
        Assert.Empty(result.Errors);
        await File.AppendAllTextAsync(path, "extra,row\n");
        result = await CsvReader.ReadFileAsync(path, "utf-8");
        Assert.Equal(5001, result.Rows.Count);
        Assert.Contains(result.Errors, e => e.Code == "ROW_LIMIT");
        await using (var file = new FileStream(path, FileMode.Create, FileAccess.Write))
            file.SetLength(CsvReader.MaxBytes + 1L);
        await Assert.ThrowsAsync<InvalidDataException>(() => CsvReader.PreviewAsync(path));
        await Assert.ThrowsAsync<InvalidDataException>(() => CsvReader.ReadFileAsync(path, "utf-8"));
    }

    private sealed class InlineProgress(Action<double?> report) : IProgress<double?>
    {
        public void Report(double? value) => report(value);
    }

    [Theory]
    [InlineData(false, 1)]
    [InlineData(true, 1)]
    [InlineData(false, 2)]
    [InlineData(true, 2)]
    [InlineData(false, 3)]
    [InlineData(true, 3)]
    public async Task Cancellation_is_observed_during_each_streaming_pass(bool preview, int cancelPass)
    {
        var path = WriteCsv("code,name\n" + string.Concat(Enumerable.Repeat("A,value\n", 3000)));
        using var cancellation = new CancellationTokenSource();
        var pass = 0;
        var progress = new InlineProgress(value =>
        {
            if (value == 0) pass++;
            if (pass == cancelPass && value > 0) cancellation.Cancel();
        });
        await Assert.ThrowsAnyAsync<OperationCanceledException>(async () =>
        {
            if (preview) await CsvReader.PreviewAsync(path, "utf-8", cancellationToken: cancellation.Token, progress: progress);
            else await CsvReader.ReadFileAsync(path, "utf-8", cancellationToken: cancellation.Token, progress: progress);
        });
        // Cancellation must also release the open file.
        using var exclusive = new FileStream(path, FileMode.Open, FileAccess.ReadWrite, FileShare.None);
    }

    [Fact]
    public async Task Progress_reports_real_byte_fractions_and_precancelled_calls_do_not_open_files()
    {
        var path = WriteCsv("code,name\n" + string.Concat(Enumerable.Repeat("A,苹果\n", 3000)));
        var reports = new List<double?>();
        await CsvReader.PreviewAsync(path, "utf-8", progress: new InlineProgress(reports.Add));
        Assert.Contains(reports, p => p > 0 && p < 1);
        Assert.All(reports, p => Assert.True(p.HasValue && p.Value >= 0 && p.Value <= 1));
        Assert.Equal(3, reports.Count(p => p == 0));
        Assert.Equal(1d, reports[^1]);
        var previous = 0d;
        foreach (var report in reports)
        {
            if (report == 0) previous = 0;
            Assert.True(report >= previous);
            previous = report!.Value;
        }
        using var cancellation = new CancellationTokenSource();
        cancellation.Cancel();
        var missing = Path.Combine(_directory, "missing.csv");
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() =>
            CsvReader.PreviewAsync(missing, cancellationToken: cancellation.Token));
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() =>
            CsvReader.ReadFileAsync(missing, "utf-8", cancellationToken: cancellation.Token));
    }

    [Fact]
    public async Task Empty_file_has_complete_hash_and_does_not_report_a_made_up_fraction()
    {
        var path = WriteCsv("");
        var reports = new List<double?>();
        var preview = await CsvReader.PreviewAsync(path, "utf-8", progress: new InlineProgress(reports.Add));
        Assert.Empty(preview.Content!.Rows);
        Assert.Equal(Convert.ToHexString(SHA256.HashData(Array.Empty<byte>())), preview.Sha256);
        Assert.NotEmpty(reports);
        Assert.All(reports, p => Assert.Null(p));
        Assert.Empty((await CsvReader.ReadFileAsync(path, "utf-8", expectedSha256: preview.Sha256)).Errors);
    }

    // Native writes deliberately bypass .NET sharing on Linux to exercise the same-handle checks.
    [DllImport("libc", EntryPoint = "open", SetLastError = true)]
    private static extern int NativeOpen(string path, int flags);
    [DllImport("libc", EntryPoint = "pwrite", SetLastError = true)]
    private static extern nint NativeWrite(int fd, byte[] bytes, nuint count, long offset);
    [DllImport("libc", EntryPoint = "close")]
    private static extern int NativeClose(int fd);

    [Theory]
    [InlineData(false, false)]
    [InlineData(true, false)]
    [InlineData(false, true)]
    [InlineData(true, true)]
    public async Task Linux_in_place_changes_cannot_detach_parsed_content_from_hash(bool preview, bool restore)
    {
        if (!OperatingSystem.IsLinux()) return;
        var path = WriteCsv("code,name\n" + string.Concat(Enumerable.Repeat("A,value\n", 3000)));
        var fd = NativeOpen(path, 1); // O_WRONLY; intentionally ignores FileShare.Read.
        Assert.True(fd >= 0);
        try
        {
            var pass = 0;
            var changed = false;
            var restored = false;
            var progress = new InlineProgress(value =>
            {
                if (value == 0) pass++;
                if (pass != 2) return;
                // Either change bytes already buffered, or change the parse pass and restore
                // before the final hash. Both must be rejected.
                if (!changed && (restore ? value == 0 : value > 0))
                {
                    Assert.Equal((nint)1, NativeWrite(fd, [(byte)'X'], 1, 0));
                    changed = true;
                }
                if (restore && changed && !restored && value == 1)
                {
                    Assert.Equal((nint)1, NativeWrite(fd, [(byte)'c'], 1, 0));
                    restored = true;
                }
            });
            var error = await Assert.ThrowsAsync<InvalidDataException>(async () =>
            {
                if (preview) await CsvReader.PreviewAsync(path, "utf-8", progress: progress);
                else await CsvReader.ReadFileAsync(path, "utf-8", progress: progress);
            });
            Assert.True(changed);
            Assert.Equal(restore, restored);
            Assert.Equal("CSV_CHANGED_AFTER_PREVIEW", error.Message);
        }
        finally { NativeClose(fd); }
    }

    [Fact]
    public async Task Import_adds_new_codes_and_updates_existing_ones_ignoring_case()
    {
        var service = CreateService();
        await service.SaveAsync(new DemoRecord { Code = "A1", Name = "旧名", Remark = "旧备注" });

        var result = await service.ImportCsvAsync(WriteCsv("code,name,remark\r\na1,新名,新备注\r\nB1,香蕉,\r\n"));

        Assert.True(result.Succeeded);
        Assert.Equal(1, result.Added);
        Assert.Equal(1, result.Updated);
        var all = await service.SearchAsync(null);
        Assert.Equal(2, all.Count);
        var a1 = all.Single(record => record.Code == "A1");
        Assert.Equal("新名", a1.Name);
        Assert.Equal(1, a1.Version);
    }

    [Fact]
    public async Task Import_changes_nothing_when_any_row_is_invalid()
    {
        var service = CreateService();

        var result = await service.ImportCsvAsync(WriteCsv("code,name,remark\r\nA1,苹果,\r\n,没有编码,\r\nA1,重复,\r\n"));

        Assert.False(result.Succeeded);
        Assert.Contains(result.Errors, error => error.Contains(Loc.Get("demo.err.codeRequired")));
        Assert.Contains(result.Errors, error => error.Contains(Loc.Format("demo.import.dupInFile", "A1")));
        Assert.Empty(await service.SearchAsync(null));
    }

    [Fact]
    public async Task Import_rejects_a_file_with_only_a_header()
    {
        var result = await CreateService().ImportCsvAsync(WriteCsv("code,name,remark\r\n"));

        Assert.False(result.Succeeded);
    }

    [Fact]
    public async Task Exported_file_can_be_imported_back_unchanged()
    {
        var source = CreateService();
        await source.SaveAsync(new DemoRecord { Code = "A1", Name = "=危险,名称", Remark = "第一行\n第二行" });
        var path = await source.ExportCsvAsync(null, _directory);
        foreach (var record in await source.SearchAsync(null))
        {
            await source.DeleteAsync(record.Id, record.Version);
        }

        var result = await source.ImportCsvAsync(path);

        Assert.True(result.Succeeded, string.Join("; ", result.Errors));
        var back = Assert.Single(await source.SearchAsync(null));
        Assert.Equal("A1", back.Code);
        Assert.Equal("=危险,名称", back.Name);
        Assert.Equal("第一行\n第二行", back.Remark);
    }
}
