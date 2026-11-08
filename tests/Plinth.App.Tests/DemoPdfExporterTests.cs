using System;
using System.IO;
using System.Linq;
using System.Text;
using Plinth.App.Localization;
using Plinth.App.Modules.Demo.Data;
using Plinth.App.Modules.Demo.Services;
using Plinth.App.Services;
using Xunit;

namespace Plinth.App.Tests;

/// <summary>PDF 模板、二维码载荷和打印动作的固定契约；不做通用报表引擎。</summary>
[Collection("Localization")]
public sealed class DemoPdfExporterTests
{
    [Fact]
    public void Render_writes_a_multi_page_pdf_for_more_rows_than_fit()
    {
        Loc.Initialize(Loc.DefaultLanguage);
        var records = Enumerable.Range(1, 60).Select(index => new DemoRecord
        {
            Code = $"A-{index:000}",
            Name = $"示例 {index}",
            Remark = index == 1 ? "中文备注与很长的说明" : "备注",
        }).ToArray();

        var bytes = DemoPdfExporter.Render(records, new DateTimeOffset(2026, 11, 4, 1, 0, 0, TimeSpan.Zero));

        Assert.True(bytes.Length > 1000, $"PDF 只有 {bytes.Length} 字节");
        Assert.True(bytes.Length < 2_000_000, $"PDF 不应嵌入整套 CJK 字体，实际 {bytes.Length} 字节");
        Assert.Equal("%PDF", Encoding.ASCII.GetString(bytes, 0, 4));
        var text = Encoding.Latin1.GetString(bytes);
        Assert.DoesNotContain("/FontFile", text, StringComparison.Ordinal);
        Assert.True(CountPages(text) >= 2, $"60 条记录应分页，实际页面数 {CountPages(text)}");
        var sample = Environment.GetEnvironmentVariable("PLINTH_PDF_SAMPLE");
        if (!string.IsNullOrWhiteSpace(sample)) File.WriteAllBytes(sample, bytes);
    }

    [Fact]
    public void Empty_selection_still_produces_a_header_page()
    {
        Loc.Initialize(Loc.DefaultLanguage);
        var bytes = DemoPdfExporter.Render([], DateTimeOffset.UnixEpoch);

        Assert.Equal("%PDF", Encoding.ASCII.GetString(bytes, 0, 4));
        Assert.True(CountPages(Encoding.Latin1.GetString(bytes)) >= 1);
    }

    [Fact]
    public void Qr_payload_describes_the_export_without_business_body()
    {
        var records = new[]
        {
            new DemoRecord { Code = "A-001", Name = "第一行", Remark = "备注" },
            new DemoRecord { Code = "B-002", Name = "第二行", Remark = "备注" },
        };
        var generated = new DateTimeOffset(2026, 11, 4, 1, 2, 3, TimeSpan.Zero);

        var payload = DemoPdfExporter.BuildQrPayload(records, generated);

        Assert.StartsWith("PLINTH-DEMO|", payload, StringComparison.Ordinal);
        Assert.Contains("count=2", payload, StringComparison.Ordinal);
        Assert.Contains("first=A-001", payload, StringComparison.Ordinal);
        Assert.Contains("last=B-002", payload, StringComparison.Ordinal);
        Assert.DoesNotContain("第一行", payload, StringComparison.Ordinal);
    }

    [Fact]
    public void Print_action_uses_the_system_print_verb()
    {
        var info = PdfPrinter.BuildStartInfo(@"C:\tmp\demo.pdf");

        Assert.True(info.UseShellExecute);
        Assert.Equal("print", info.Verb);
        Assert.Equal(@"C:\tmp\demo.pdf", info.FileName);
    }

    [Fact]
    public void Missing_file_is_reported_without_starting_a_process()
    {
        Assert.False(PdfPrinter.TryPrint(@"C:\tmp\plinth-missing-export.pdf", out var error));
        Assert.Equal(OperatingSystem.IsWindows() ? "missing" : "unsupported", error);
    }

    private static int CountPages(string pdf) =>
        System.Text.RegularExpressions.Regex.Matches(pdf, @"/Type\s*/Page(?!s)").Count;
}
