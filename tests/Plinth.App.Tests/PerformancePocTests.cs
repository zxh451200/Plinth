using System.Diagnostics;
using System.Globalization;
using Microsoft.Extensions.Logging.Abstractions;
using Plinth.App.Modules.Demo.Services;
using Xunit;

namespace Plinth.App.Tests;

[CollectionDefinition("PerformancePoc", DisableParallelization = true)]
public sealed class PerformancePocCollection { }

[Collection("PerformancePoc")]
public sealed class PerformancePocTests(ITestOutputHelper output)
{
    [Fact]
    [Trait("Category", "PerformancePoc")]
    public async Task Real_sqlite_csv_paging_save_export_and_round_trip()
    {
        var directory = Path.Combine(Path.GetTempPath(), $"plinth-poc-{Guid.NewGuid():N}");
        Directory.CreateDirectory(directory);
        try
        {
            using var database = new TestDatabase();
            var service = CreateService(database);
#if DEBUG
            const string configuration = "Debug";
#else
            const string configuration = "Release";
#endif
            output.WriteLine($"Runtime={Environment.Version}; OS={Environment.OSVersion}; " +
                $"64bit={Environment.Is64BitProcess}; CPU={Environment.ProcessorCount}; " +
                $"configuration={configuration}; memory=stage samples only");

            // Fixture generation and correctness assertions are outside operation timings.
            for (var batch = 0; batch < 2; batch++)
            {
                var path = Path.Combine(directory, $"seed-{batch}.csv");
                await CsvWriter.WriteFileAsync(path, ["编码", "名称", "备注"],
                    Rows(batch * 5000, 5000, updated: false));
                CheckImport(await Measure($"10k/import-batch-{batch + 1}",
                    () => service.ImportCsvAsync(path)), 5000, 0);
            }

            foreach (var page in new[] { 1, 100, 200 })
            {
                var result = await Measure($"10k/page-{page}",
                    () => service.SearchPageAsync(null, page, 50));
                Assert.Equal(10000, result.TotalCount);
                Assert.Equal(page, result.Page);
                Assert.Equal(50, result.Items.Count);
                Assert.Equal(Code((page - 1) * 50), result.Items[0].Code);
                Assert.Equal(Code(page * 50 - 1), result.Items[^1].Code);
            }

            var filtered = await Measure("10k/chinese-filter",
                () => service.SearchPageAsync("中文筛选", 1, 50));
            Assert.Equal(1000, filtered.TotalCount);
            Assert.Equal(50, filtered.Items.Count);
            Assert.All(filtered.Items, item => Assert.Contains("中文筛选", item.Name));

            var record = (await service.SearchPageAsync(null, 100, 50)).Items[0];
            var version = record.Version;
            record.Name = "中文保存验证";
            Assert.Null(await Measure("10k/save-existing", () => service.SaveAsync(record)));
            var saved = Assert.Single((await service.SearchPageAsync(record.Code)).Items);
            Assert.Equal(record.Id, saved.Id);
            Assert.Equal("中文保存验证", saved.Name);
            Assert.Equal(version + 1, saved.Version);

            var fullExport = await Measure("10k/export-all",
                () => service.ExportCsvAsync(null, directory));
            // CsvReader intentionally caps imports at 5000 rows. Count the full export
            // with the existing streaming CSV dependency, respecting quoted newlines.
            using (var text = File.OpenText(fullExport))
            using (var csv = new CsvHelper.CsvReader(text, CultureInfo.InvariantCulture))
            {
                Assert.True(await csv.ReadAsync());
                var count = 0;
                while (await csv.ReadAsync())
                {
                    Assert.Equal(Code(count), csv.GetField(0));
                    count++;
                }
                Assert.Equal(10000, count);
            }

            // An independent 5000-row dataset stays within the real import limit.
            using var sourceDatabase = new TestDatabase();
            var source = CreateService(sourceDatabase);
            var importPath = Path.Combine(directory, "round-trip-5000.csv");
            await CsvWriter.WriteFileAsync(importPath, ["编码", "名称", "备注"],
                Rows(0, 5000, updated: false));
            CheckImport(await Measure("5k/import-new", () => source.ImportCsvAsync(importPath)), 5000, 0);
            var updatePath = Path.Combine(directory, "round-trip-5000-update.csv");
            await CsvWriter.WriteFileAsync(updatePath, ["编码", "名称", "备注"],
                Rows(0, 5000, updated: true));
            CheckImport(await Measure("5k/import-update", () => source.ImportCsvAsync(updatePath)), 0, 5000);
            var exportPath = await Measure("5k/export-all", () => source.ExportCsvAsync(null, directory));
            var exported = await CsvReader.ReadFileAsync(exportPath, "utf-8", ",");
            Assert.Empty(exported.Errors);
            Assert.Equal(5001, exported.Rows.Count);
            var expected = Rows(0, 5000, updated: true).ToArray();
            for (var i = 0; i < expected.Length; i++)
                Assert.Equal(expected[i].ToArray(), exported.Rows[i + 1].ToArray());

            using var targetDatabase = new TestDatabase();
            var target = CreateService(targetDatabase);
            CheckImport(await Measure("5k/import-into-other-database",
                () => target.ImportCsvAsync(exportPath)), 5000, 0);
            var sourceRows = await source.SearchAsync(null);
            var targetRows = await target.SearchAsync(null);
            Assert.Equal(5000, sourceRows.Count);
            Assert.Equal(5000, targetRows.Count);
            for (var i = 0; i < expected.Length; i++)
            {
                Assert.Equal(expected[i].ToArray(),
                    new[] { sourceRows[i].Code, sourceRows[i].Name, sourceRows[i].Remark });
                Assert.Equal(expected[i].ToArray(),
                    new[] { targetRows[i].Code, targetRows[i].Name, targetRows[i].Remark });
            }
        }
        finally
        {
            Directory.Delete(directory, recursive: true);
        }
    }

    private static DemoService CreateService(TestDatabase database) =>
        new(database.Factory, NullLogger<DemoService>.Instance);

    // Fifty numeric characters exercise leading zeros and codes beyond spreadsheet
    // numeric precision, at the actual service's maximum accepted code length.
    private static string Code(int index) => index.ToString("D50", CultureInfo.InvariantCulture);

    private static IEnumerable<IReadOnlyList<string>> Rows(int start, int count, bool updated)
    {
        for (var i = start; i < start + count; i++)
        {
            var name = $"{(i % 10 == 0 ? "中文筛选" : "中文名称")}{i:D5}{(updated ? "已更新" : "原始")}";
            var remark = (i % 6) switch
            {
                0 => "中文,逗号与\"双引号\"\r\n第二行",
                1 => "=SUM(1,2)",
                2 => "+1+2",
                3 => "-1+2",
                4 => "@SUM(A1)",
                _ => "'=保留前导单引号\n中文下一行",
            };
            yield return new[] { Code(i), name, remark };
        }
    }

    private static void CheckImport(ImportResult result, int added, int updated)
    {
        Assert.True(result.Succeeded, string.Join(Environment.NewLine, result.Errors));
        Assert.Equal(added, result.Added);
        Assert.Equal(updated, result.Updated);
    }

    private async Task<T> Measure<T>(string stage, Func<Task<T>> operation)
    {
        using var process = Process.GetCurrentProcess();
        process.Refresh();
        var workingSetBefore = process.WorkingSet64;
        var managedBefore = GC.GetTotalMemory(forceFullCollection: false);
        var collections = new[] { GC.CollectionCount(0), GC.CollectionCount(1), GC.CollectionCount(2) };
        var allocatedBefore = GC.GetTotalAllocatedBytes(precise: false);
        var watch = Stopwatch.StartNew();
        try
        {
            return await operation();
        }
        finally
        {
            watch.Stop();
            process.Refresh();
            var workingSetAfter = process.WorkingSet64;
            var managedAfter = GC.GetTotalMemory(forceFullCollection: false);
            output.WriteLine(
                $"{stage}: elapsedMs={watch.Elapsed.TotalMilliseconds.ToString("F3", CultureInfo.InvariantCulture)}; " +
                $"workingSetBytes(before/after)={workingSetBefore}/{workingSetAfter}; " +
                $"managedBytes(before/after)={managedBefore}/{managedAfter}; " +
                $"allocatedBytesDelta={GC.GetTotalAllocatedBytes(precise: false) - allocatedBefore}; " +
                $"gcCollectionsDelta(0/1/2)={GC.CollectionCount(0) - collections[0]}/" +
                $"{GC.CollectionCount(1) - collections[1]}/{GC.CollectionCount(2) - collections[2]}");
        }
    }
}
