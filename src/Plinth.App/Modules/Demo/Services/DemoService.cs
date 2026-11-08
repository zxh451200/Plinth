using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;
using Plinth.App.Data;
using Plinth.App.Modules.Demo.Data;
using Plinth.App.Localization;
using Plinth.App.Services;
using System.Runtime.CompilerServices;

namespace Plinth.App.Modules.Demo.Services;

/// <summary>
/// 示例的业务逻辑:查询、保存、删除。ViewModel 只调用它,不自己碰数据库。
/// 每次操作从工厂拿一个新的 DbContext,用完就释放(桌面程序里不要长期持有 DbContext)。
/// </summary>
public class DemoService(IDbContextFactory<AppDbContext> dbFactory, ILogger<DemoService> logger,
    SensitiveOperationService? operations = null)
{
    private readonly SensitiveOperationService _operations = operations ?? SensitiveOperationService.CreateDefault(logger);
    public const int MaxCodeLength = 50;
    public const int MaxNameLength = 200;
    public const int MaxRemarkLength = 500;
    public const int MinQuantity = -1_000_000;
    public const int MaxQuantity = 1_000_000;

    /// <summary>日期单元格允许的范围；空值表示未设置。</summary>
    public static readonly DateOnly MinDueDate = new(1900, 1, 1);
    public static readonly DateOnly MaxDueDate = new(2100, 12, 31);

    private const int MaxImportRows = 5000;
    private const int MaxImportErrors = 20;
    private const long MaxImportBytes = 5 * 1024 * 1024;

    /// <summary>
    /// 按编码或名称模糊查询(SQLite 的 LIKE 不区分大小写),结果按编码排序。search 为空返回全部。
    /// 关键字里的 % _ \ 要转义,否则用户输入 "100%" 会被当成通配符。
    /// </summary>
    public async Task<IReadOnlyList<DemoRecord>> SearchAsync(string? search, CancellationToken cancellationToken = default)
    {
        await using var db = await dbFactory.CreateDbContextAsync(cancellationToken);
        var query = db.Set<DemoRecord>().AsNoTracking();

        var keyword = search?.Trim() ?? string.Empty;
        if (keyword.Length > 0)
        {
            var pattern = "%" + keyword.Replace("\\", "\\\\").Replace("%", "\\%").Replace("_", "\\_") + "%";
            query = query.Where(record => EF.Functions.Like(record.Code, pattern, "\\") || EF.Functions.Like(record.Name, pattern, "\\"));
        }

        // 最后加上 Id 保证顺序稳定
        return await query.OrderBy(record => record.CodeKey).ThenBy(record => record.Id).ToListAsync(cancellationToken);
    }

    /// <summary>只读取当前页；排序字段使用白名单，所有排序追加唯一键。</summary>
    public async Task<DemoSearchPage> SearchPageAsync(
        string? search, int page = 1, int pageSize = 50, string sortField = "Code",
        bool descending = false, CancellationToken cancellationToken = default)
    {
        pageSize = Math.Clamp(pageSize, 1, 200);
        await using var db = await dbFactory.CreateDbContextAsync(cancellationToken);
        var query = db.Set<DemoRecord>().AsNoTracking();
        var keyword = search?.Trim() ?? string.Empty;
        if (keyword.Length > 0)
        {
            var pattern = "%" + keyword.Replace("\\", "\\\\").Replace("%", "\\%").Replace("_", "\\_") + "%";
            query = query.Where(record => EF.Functions.Like(record.Code, pattern, "\\")
                || EF.Functions.Like(record.Name, pattern, "\\"));
        }

        var total = await query.CountAsync(cancellationToken);
        page = Math.Clamp(page, 1, Math.Max(1, (int)Math.Ceiling(total / (double)pageSize)));
        var ordered = (sortField, descending) switch
        {
            ("Name", false) => query.OrderBy(r => r.Name),
            ("Name", true) => query.OrderByDescending(r => r.Name),
            ("Remark", false) => query.OrderBy(r => r.Remark),
            ("Remark", true) => query.OrderByDescending(r => r.Remark),
            ("UpdatedAt", false) => query.OrderBy(r => r.UpdatedAtUtc),
            ("UpdatedAt", true) => query.OrderByDescending(r => r.UpdatedAtUtc),
            ("UpdatedBy", false) => query.OrderBy(r => r.UpdatedBy),
            ("UpdatedBy", true) => query.OrderByDescending(r => r.UpdatedBy),
            (_, true) => query.OrderByDescending(r => r.CodeKey),
            _ => query.OrderBy(r => r.CodeKey),
        };
        var items = await ordered.ThenBy(r => r.Id).Skip((page - 1) * pageSize).Take(pageSize)
            .ToListAsync(cancellationToken);
        return new DemoSearchPage(items, total, page, pageSize);
    }

    /// <summary>
    /// 新增或修改(按 Id 判断)。成功返回 null,失败返回给用户看的原因。
    /// 修改时 input.Version 必须是打开表单时读到的版本号,库里的已经变了就说明别人先改过。
    /// </summary>
    public async Task<string?> SaveAsync(DemoRecord input, CancellationToken cancellationToken = default)
        => (await SaveValidatedAsync(new DemoSaveInput(input.Id, input.Version,
            input.Code, input.Name, input.Remark, Quantity: input.Quantity, DueDate: input.DueDate,
            Priority: input.Priority, Active: input.Active), cancellationToken))?.Message;

    public async Task<DemoSaveError?> SaveValidatedAsync(DemoSaveInput input, CancellationToken cancellationToken = default)
        => await _operations.ExecuteConfirmedAsync("demo:save", "Demo", "save",
            ct => SaveCoreAsync(input, ct), cancellationToken, error => error is null);

    private async Task<DemoSaveError?> SaveCoreAsync(DemoSaveInput input, CancellationToken cancellationToken)
    {
        var code = input.Code.Trim();
        var name = input.Name.Trim();
        var remark = input.Remark.Trim();

        var invalid = ValidateFields(code, name, remark, input.Quantity, input.DueDate, input.Priority);
        if (invalid is not null)
        {
            return invalid;
        }

        await using var db = await dbFactory.CreateDbContextAsync(cancellationToken);
        var records = db.Set<DemoRecord>();

        var codeKey = CodeIdentity.Create(code);
        if (await records.AnyAsync(record => record.CodeKey == codeKey && record.Id != input.Id, cancellationToken))
        {
            return new(DemoSaveField.Code, DuplicateMessage(code));
        }

        var existing = await records.FindAsync([input.Id], cancellationToken);
        if (existing is null)
        {
            if (input.MustExist)
                return new(DemoSaveField.Form, Loc.Get("demo.err.conflict"));
            records.Add(new DemoRecord
            {
                Id = input.Id,
                Code = code,
                Name = name,
                Remark = remark,
                Quantity = input.Quantity,
                DueDate = input.DueDate,
                Priority = input.Priority,
                Active = input.Active,
            });
        }
        else
        {
            existing.Code = code;
            existing.Name = name;
            existing.Remark = remark;
            existing.Quantity = input.Quantity;
            existing.DueDate = input.DueDate;
            existing.Priority = input.Priority;
            existing.Active = input.Active;

            // 带上打开表单时的版本号:库里的版本已经变了,UPDATE 就会找不到这一行,下面会收到并发冲突
            db.Entry(existing).Property(record => record.Version).OriginalValue = input.Version;
            existing.Version = input.Version + 1;
        }

        try
        {
            await db.SaveChangesAsync(cancellationToken);
            return null;
        }
        catch (DbUpdateConcurrencyException)
        {
            return new(DemoSaveField.Form, Loc.Get("demo.err.conflict"));
        }
        catch (DbUpdateException ex)
        {
            // 两个人几乎同时保存同一个编码时,唯一索引会拦住后一个。
            // 不去解析数据库专有的错误码,而是重新查一次编码是否已被占用
            await using var check = await dbFactory.CreateDbContextAsync(cancellationToken);
            if (await check.Set<DemoRecord>().AnyAsync(record => record.CodeKey == codeKey && record.Id != input.Id, cancellationToken))
            {
                return new(DemoSaveField.Code, DuplicateMessage(code));
            }

            logger.LogError(ex, "保存示例记录失败");
            return new(DemoSaveField.Form, Loc.Get("demo.err.saveFailed"));
        }
    }

    /// <summary>按打开时的版本删除一条，版本过期时拒绝误删。</summary>
    public async Task<string?> DeleteAsync(Guid id, int expectedVersion,
        CancellationToken cancellationToken = default)
        => await _operations.ExecuteConfirmedAsync("demo:delete", "Demo", "delete",
            ct => DeleteCoreAsync(id, expectedVersion, ct), cancellationToken, error => error is null);

    private async Task<string?> DeleteCoreAsync(Guid id, int expectedVersion,
        CancellationToken cancellationToken)
    {
        await using var db = await dbFactory.CreateDbContextAsync(cancellationToken);
        var deleted = await db.Set<DemoRecord>()
            .Where(record => record.Id == id && record.Version == expectedVersion)
            .ExecuteDeleteAsync(cancellationToken);
        if (deleted == 1) return null;
        var exists = await db.Set<DemoRecord>().AsNoTracking()
            .AnyAsync(record => record.Id == id, cancellationToken);
        return exists ? Loc.Get("demo.err.conflict") : Loc.Get("demo.err.gone");
    }

    /// <summary>
    /// 把当前搜索条件下的记录导出成 CSV 文件(UTF-8 带 BOM,Excel 直接打开不乱码),返回文件完整路径。
    /// 先写临时文件再改名,中途失败不会留下写了一半的文件。
    /// </summary>
    public async Task<string> ExportCsvAsync(string? search, string directory, CancellationToken cancellationToken = default,
        IProgress<double?>? progress = null)
        => (await ExportCsvOperationAsync(search, directory, cancellationToken, progress)).Path;

    public async Task<TaskOutcome> ExportCsvWithResultAsync(string? search, string directory,
        CancellationToken cancellationToken = default, IProgress<double?>? progress = null)
    {
        var result = await ExportCsvOperationAsync(search, directory, cancellationToken, progress);
        return TaskOutcome.Export(result.Rows, result.Path);
    }

    private async Task<(string Path, int Rows)> ExportCsvOperationAsync(string? search, string directory,
        CancellationToken cancellationToken, IProgress<double?>? progress)
        => await _operations.ExecuteConfirmedAsync("demo:export", "Demo", "export",
            ct => ExportCsvCoreAsync(search, directory, ct, progress), cancellationToken);

    private sealed class ExportCount { public int Written; }

    private async Task<(string Path, int Rows)> ExportCsvCoreAsync(string? search, string directory,
        CancellationToken cancellationToken, IProgress<double?>? progress)
    {
        progress?.Report(null);
        Directory.CreateDirectory(directory);
        var path = Path.Combine(directory, $"demo-{DateTime.Now:yyyyMMdd-HHmmss}-{Guid.NewGuid():N}.csv");
        var count = new ExportCount();
        await CsvWriter.WriteFileAsync(path,
            [
                Loc.Get("demo.col.code"), Loc.Get("demo.col.name"), Loc.Get("demo.col.remark"),
                Loc.Get("demo.col.quantity"), Loc.Get("demo.col.dueDate"),
                Loc.Get("demo.col.priority"), Loc.Get("demo.col.active"),
            ],
            ExportRowsAsync(search, count, progress, cancellationToken), cancellationToken);
        return (path, count.Written);
    }

    private async IAsyncEnumerable<IReadOnlyList<string>> ExportRowsAsync(string? search, ExportCount count,
        IProgress<double?>? progress,
        [EnumeratorCancellation] CancellationToken token)
    {
        const int batchSize = 200;
        await using var db = await dbFactory.CreateDbContextAsync(token);
        // One read transaction fixes the snapshot across all bounded pages.
        await using var snapshot = await db.Database.BeginTransactionAsync(token);
        var query = db.Set<DemoRecord>().AsNoTracking();
        var keyword = search?.Trim() ?? "";
        if (keyword.Length > 0)
        {
            var pattern = "%" + keyword.Replace("\\", "\\\\").Replace("%", "\\%").Replace("_", "\\_") + "%";
            query = query.Where(r => EF.Functions.Like(r.Code, pattern, "\\") || EF.Functions.Like(r.Name, pattern, "\\"));
        }
        var total = await query.CountAsync(token);
        progress?.Report(total > 0 ? 0 : null);
        for (var offset = 0; ; offset = checked(offset + batchSize))
        {
            var page = await query.OrderBy(r => r.CodeKey).ThenBy(r => r.Id).Skip(offset).Take(batchSize)
                .Select(r => new { r.Code, r.Name, r.Remark, r.Quantity, r.DueDate, r.Priority, r.Active })
                .ToListAsync(token);
            foreach (var record in page)
            {
                token.ThrowIfCancellationRequested();
                // 类型列用固定格式导出,不跟随界面区域设置;导入仍只更新文本三列。
                yield return
                [
                    record.Code, record.Name, record.Remark,
                    record.Quantity.ToString(System.Globalization.CultureInfo.InvariantCulture),
                    record.DueDate?.ToString("yyyy-MM-dd", System.Globalization.CultureInfo.InvariantCulture)
                        ?? string.Empty,
                    record.Priority.ToString(),
                    record.Active ? "true" : "false",
                ];
                // The writer has completed NextRecordAsync before requesting the next row.
                count.Written++;
                progress?.Report(total > 0 ? (double)count.Written / total : null);
            }
            if (page.Count < batchSize) break;
        }
        // Flushing, disposal and the final rename have no measurable row fraction.
        progress?.Report(null);
    }

    /// <summary>
    /// 从 CSV 导入(第一行是表头,列顺序:编码、名称、备注)。编码已存在就更新名称和备注,不存在就新增。
    /// 数量/日期/优先级/启用属于类型列,不参与 CSV 导入,请在表单或单元格里维护。
    /// 要么全部成功,要么什么都不改:只要有一行不合格,就返回所有问题(最多 20 条)而不写库。
    /// </summary>
    public async Task<ImportResult> ImportCsvAsync(string path, CancellationToken cancellationToken = default,
        IProgress<double?>? progress = null)
        => await ImportCsvAsync(path, "utf-8", ",", [0, 1, 2], cancellationToken, progress: progress);

    public async Task<ImportResult> ImportCsvAsync(string path, string encoding, string delimiter,
        IReadOnlyList<int> columns, CancellationToken cancellationToken = default, string? expectedSha256 = null,
        IProgress<double?>? progress = null)
        => await _operations.ExecuteConfirmedAsync("demo:import", "Demo", "import",
            ct => ImportCsvCoreAsync(path, encoding, delimiter, columns, ct, expectedSha256, progress),
            cancellationToken, result => result.Succeeded);

    private async Task<ImportResult> ImportCsvCoreAsync(string path, string encoding, string delimiter,
        IReadOnlyList<int> columns, CancellationToken cancellationToken, string? expectedSha256,
        IProgress<double?>? progress)
    {
        if (new FileInfo(path).Length > MaxImportBytes)
        {
            return ImportResult.Failed(Loc.Format("demo.import.tooBig", MaxImportBytes / 1024 / 1024));
        }

        var source = await CsvReader.ReadFileAsync(path, encoding, delimiter, cancellationToken, expectedSha256, progress);
        progress?.Report(null);
        var mapped = CsvReader.MapAndValidate(source,
            columns, [typeof(string), typeof(string), typeof(string)], cancellationToken);
        if (mapped.Errors.Count > 0)
        {
            Directory.CreateDirectory(AppPaths.ExportDirectory);
            var report = Path.Combine(AppPaths.ExportDirectory, $"csv-errors-{Guid.NewGuid():N}.csv");
            await CsvWriter.WriteErrorReportAsync(report, mapped.Errors, cancellationToken);
            // Source syntax/limit errors may stop parsing early; mapping can add several
            // issues for the same physical row. Never count messages as failed rows.
            var headerRow = mapped.RowNumbers.Count > 0 ? mapped.RowNumbers[0] : 1;
            var errorRows = mapped.Errors.Where(issue => issue.Row != headerRow)
                .Select(issue => issue.Row).Distinct().Count();
            return ImportResult.Failed(Loc.Format("csv.errorReport", report)) with
            {
                ReportPath = report,
                DiscoveredErrorRows = errorRows > 0 ? errorRows : null,
                // Other rows have not passed business validation, even if mapping reached EOF.
                ErrorRowCountComplete = false,
            };
        }
        var rows = mapped.Rows;
        if (rows.Count < 2)
        {
            return ImportResult.Failed(Loc.Get("demo.import.empty"));
        }

        if (rows.Count - 1 > MaxImportRows)
        {
            return ImportResult.Failed(Loc.Format("demo.import.tooMany", MaxImportRows));
        }

        var errors = new List<string>();
        var issues = new List<CsvIssue>();
        var parsed = new List<(string Code, string Name, string Remark)>();
        var seen = new HashSet<string>(StringComparer.Ordinal);
        var nextRow = 1;
        for (; nextRow < rows.Count && errors.Count < MaxImportErrors; nextRow++)
        {
            cancellationToken.ThrowIfCancellationRequested();
            var i = nextRow;
            var row = rows[i];
            var code = Cell(row, 0);
            var name = Cell(row, 1);
            var remark = Cell(row, 2);

            var invalid = Validate(code, name, remark);
            if (invalid is null && !seen.Add(CodeIdentity.Create(code)))
            {
                invalid = Loc.Format("demo.import.dupInFile", code);
            }

            if (invalid is not null)
            {
                errors.Add(Loc.Format("demo.import.rowError", i + 1, invalid));
                issues.Add(new CsvIssue(i < mapped.RowNumbers.Count ? mapped.RowNumbers[i] : i + 1,
                    "VALIDATION", invalid));
                continue;
            }

            parsed.Add((code, name, remark));
        }

        if (errors.Count > 0)
        {
            Directory.CreateDirectory(AppPaths.ExportDirectory);
            var report = Path.Combine(AppPaths.ExportDirectory, $"csv-errors-{Guid.NewGuid():N}.csv");
            await CsvWriter.WriteErrorReportAsync(report,
                issues, cancellationToken);
            errors.Add(Loc.Format("csv.errorReport", report));
            return new ImportResult(0, 0, errors)
            {
                ReportPath = report,
                DiscoveredErrorRows = issues.Select(issue => issue.Row).Distinct().Count(),
                ErrorRowCountComplete = nextRow == rows.Count,
            };
        }

        await using var db = await dbFactory.CreateDbContextAsync(cancellationToken);
        await using var transaction = await db.Database.BeginTransactionAsync(cancellationToken);
        var keys = parsed.Select(item => CodeIdentity.Create(item.Code)).ToList();
        var existing = (await db.Set<DemoRecord>().Where(record => keys.Contains(record.CodeKey)).ToListAsync(cancellationToken))
            .ToDictionary(record => record.CodeKey, StringComparer.Ordinal);

        int added = 0, updated = 0;
        foreach (var (code, name, remark) in parsed)
        {
            if (existing.TryGetValue(CodeIdentity.Create(code), out var record))
            {
                record.Name = name;
                record.Remark = remark;
                record.Version++;
                updated++;
            }
            else
            {
                db.Set<DemoRecord>().Add(new DemoRecord { Code = code, Name = name, Remark = remark });
                added++;
            }
        }

        try
        {
            await db.SaveChangesAsync(cancellationToken);
            await transaction.CommitAsync(cancellationToken);
            return new ImportResult(added, updated, []);
        }
        catch (DbUpdateConcurrencyException)
        {
            return ImportResult.Failed(Loc.Get("demo.err.conflict"));
        }
        catch (DbUpdateException ex)
        {
            logger.LogError("导入示例数据失败。ExceptionType {ExceptionType}", ex.GetType().Name);
            return ImportResult.Failed(Loc.Get("demo.err.saveFailed"));
        }
    }

    public Task<string> ExportPreviewErrorsAsync(string path, IReadOnlyList<CsvIssue> issues,
        CancellationToken cancellationToken = default) =>
        _operations.ExecuteConfirmedAsync("demo:export", "Demo", "previewErrors", async ct =>
        {
            await CsvWriter.WriteErrorReportAsync(path, issues, ct);
            return path;
        }, cancellationToken);

    private static string Cell(IReadOnlyList<string> row, int index) => index < row.Count ? row[index].Trim() : string.Empty;

    private static string DuplicateMessage(string code) => Loc.Format("demo.err.duplicate", code);

    private static string? Validate(string code, string name, string remark)
        => ValidateFields(code, name, remark, 0, null, DemoPriority.Normal)?.Message;

    private static DemoSaveError? ValidateFields(string code, string name, string remark,
        int quantity, DateOnly? dueDate, DemoPriority priority)
    {
        if (code.Length == 0)
        {
            return new(DemoSaveField.Code, Loc.Get("demo.err.codeRequired"));
        }

        if (code.Length > MaxCodeLength)
        {
            return new(DemoSaveField.Code, Loc.Format("demo.err.codeTooLong", MaxCodeLength));
        }
        if (!CodeIdentity.IsValid(code)) return new(DemoSaveField.Code, Loc.Get("demo.err.codeInvalid"));

        if (name.Length == 0)
        {
            return new(DemoSaveField.Name, Loc.Get("demo.err.nameRequired"));
        }

        if (name.Length > MaxNameLength)
        {
            return new(DemoSaveField.Name, Loc.Format("demo.err.nameTooLong", MaxNameLength));
        }

        if (remark.Length > MaxRemarkLength)
        {
            return new(DemoSaveField.Remark, Loc.Format("demo.err.remarkTooLong", MaxRemarkLength));
        }

        if (quantity is < MinQuantity or > MaxQuantity)
        {
            return new(DemoSaveField.Quantity, Loc.Format("demo.err.quantityRange", MinQuantity, MaxQuantity));
        }

        if (dueDate is { } date && (date < MinDueDate || date > MaxDueDate))
        {
            return new(DemoSaveField.DueDate,
                Loc.Format("demo.err.dueDateRange", MinDueDate.ToString("d"), MaxDueDate.ToString("d")));
        }

        if (!Enum.IsDefined(priority))
        {
            return new(DemoSaveField.Priority, Loc.Get("demo.err.priorityInvalid"));
        }

        return null;
    }
}
