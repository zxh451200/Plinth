using System;
using System.IO;
using System.Linq;
using System.Text;
using System.Threading.Tasks;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging.Abstractions;
using Plinth.App.Data;
using Plinth.App.Modules.Demo.Data;
using Plinth.App.Modules.Demo.Services;
using Plinth.App.Modules.Notes.Data;
using Xunit;

namespace Plinth.App.Tests;

public sealed class CodeIdentityTests
{
    [Theory]
    [InlineData("A1", "6131")]
    [InlineData("a1", "6131")]
    [InlineData("Ä", "C384")]
    [InlineData("ä", "C3A4")]
    [InlineData("中文", "E4B8ADE69687")]
    [InlineData(" A ", "206120")]
    [InlineData("😀", "F09F9880")]
    public void Keys_have_fixed_ascii_only_case_rules(string code, string expected)
    {
        Assert.Equal(expected, CodeIdentity.Create(code));
        var record = new DemoRecord { Code = code };
        Assert.Equal(code, record.Code);
        Assert.Equal(expected, record.CodeKey);
    }

    [Fact]
    public void Unicode_variants_remain_distinct_and_invalid_utf16_cannot_be_silently_replaced()
    {
        foreach (var (first, second) in new[] { ("Ä", "ä"), ("Σ", "σ"), ("é", "e\u0301"), ("Ａ", "A") })
            Assert.NotEqual(CodeIdentity.Create(first), CodeIdentity.Create(second));
        Assert.False(CodeIdentity.IsValid("\ud800"));
        Assert.Throws<EncoderFallbackException>(() => CodeIdentity.Create("\ud800"));
        Assert.Equal(300, CodeIdentity.Create(new string('中', 50)).Length);
        var note = new Note();
        Assert.Equal(CodeIdentity.Create("default"), note.CodeKey);
        note.Code = "DEFAULT";
        Assert.Equal("DEFAULT", note.Code);
        Assert.Equal(CodeIdentity.Create("default"), note.CodeKey);
    }

    [Fact]
    public async Task Saving_unicode_pairs_and_renaming_updates_the_database_unique_key()
    {
        using var database = new TestDatabase();
        var service = new DemoService(database.Factory, NullLogger<DemoService>.Instance);
        foreach (var code in new[] { "A1", "Ä", "ä", "Σ", "σ", "é", "e\u0301" })
            Assert.Null(await service.SaveAsync(new DemoRecord { Code = code, Name = code }));
        Assert.NotNull(await service.SaveAsync(new DemoRecord { Code = "a1", Name = "duplicate" }));
        var records = await service.SearchAsync(null);
        Assert.Equal(7, records.Count);
        Assert.All(records, record => Assert.Equal(CodeIdentity.Create(record.Code), record.CodeKey));

        var renamed = records.Single(record => record.Code == "A1");
        renamed.Code = "B2";
        Assert.Null(await service.SaveAsync(renamed));
        Assert.Null(await service.SaveAsync(new DemoRecord { Code = "a1", Name = "released" }));
        Assert.NotNull(await service.SaveAsync(new DemoRecord { Code = "b2", Name = "duplicate" }));
        await using var db = await database.Factory.CreateDbContextAsync();
        db.Add(new DemoRecord { Code = "b2", Name = "direct EF bypass" });
        await Assert.ThrowsAsync<DbUpdateException>(() => db.SaveChangesAsync());
    }

    [Fact]
    public async Task Csv_updates_by_ascii_key_preserves_code_spelling_and_roundtrips_unicode()
    {
        using var database = new TestDatabase();
        var directory = Directory.CreateTempSubdirectory("plinth-code-").FullName;
        try
        {
            var service = new DemoService(database.Factory, NullLogger<DemoService>.Instance);
            await service.SaveAsync(new DemoRecord { Code = "AbC", Name = "old" });
            var input = Path.Combine(directory, "input.csv");
            await File.WriteAllTextAsync(input, "Code,Name,Remark\r\nabc,new,\r\nÄ,upper,\r\nä,lower,\r\n");
            var result = await service.ImportCsvAsync(input);
            Assert.True(result.Succeeded);
            Assert.Equal(2, result.Added);
            Assert.Equal(1, result.Updated);
            var records = await service.SearchAsync(null);
            Assert.Equal("new", records.Single(record => record.Code == "AbC").Name);
            var exported = await service.ExportCsvAsync(null, directory);
            var rows = await CsvReader.ReadFileAsync(exported, "utf-8");
            Assert.Equal(new[] { "AbC", "Ä", "ä" }.OrderBy(x => x, StringComparer.Ordinal),
                rows.Rows.Skip(1).Select(row => row[0]).OrderBy(x => x, StringComparer.Ordinal));

            await File.WriteAllTextAsync(input, "Code,Name,Remark\r\nX,same,\r\nx,duplicate,\r\n");
            Assert.False((await service.ImportCsvAsync(input)).Succeeded);
            Assert.Equal(3, (await service.SearchAsync(null)).Count);
        }
        finally { Directory.Delete(directory, true); }
    }

    [Fact]
    public async Task Default_paging_and_export_share_the_normalized_code_order()
    {
        using var database = new TestDatabase();
        var directory = Directory.CreateTempSubdirectory("plinth-order-").FullName;
        try
        {
            var service = new DemoService(database.Factory, NullLogger<DemoService>.Instance);
            foreach (var code in new[] { "c", "B", "a" })
                await service.SaveAsync(new DemoRecord { Code = code, Name = code });
            Assert.Equal(new[] { "a", "B", "c" }, (await service.SearchAsync(null)).Select(r => r.Code));
            var first = await service.SearchPageAsync(null, page: 1, pageSize: 2);
            var second = await service.SearchPageAsync(null, page: 2, pageSize: 2);
            Assert.Equal(new[] { "a", "B", "c" }, first.Items.Concat(second.Items).Select(r => r.Code));
            var export = await service.ExportCsvAsync(null, directory);
            var rows = await CsvReader.ReadFileAsync(export, "utf-8");
            Assert.Equal(new[] { "a", "B", "c" }, rows.Rows.Skip(1).Select(row => row[0]));
        }
        finally { Directory.Delete(directory, true); }
    }
}
