using System;
using System.IO;
using Plinth.App.Localization;

namespace Plinth.App.Services;

public enum TaskOutcomeKind { Import, Export, Report, Rejected }

/// <summary>Only structured counts and one bounded, validated output path may enter task history.</summary>
public sealed class TaskOutcome
{
    private TaskOutcome(TaskOutcomeKind kind, int? added, int? updated, int? failedRows,
        bool failureCountComplete, int? writtenRows, string? path,
        string extension = ".csv", bool userSelected = false)
    {
        Kind = kind;
        Added = Count(added);
        Updated = Count(updated);
        FailedRows = Count(failedRows);
        FailureCountComplete = failureCountComplete && FailedRows.HasValue;
        WrittenRows = Count(writtenRows);
        FilePath = SafeOutputPath(path, extension, userSelected);
    }

    public TaskOutcomeKind Kind { get; }
    public int? Added { get; }
    public int? Updated { get; }
    public int? FailedRows { get; }
    public bool FailureCountComplete { get; }
    public int? WrittenRows { get; }
    public string? FilePath { get; }
    // 保留现有 CSV 调用契约；XLSX 位置通过 FilePath / ResultText 获取。
    public string? CsvPath => string.Equals(Path.GetExtension(FilePath), ".csv", StringComparison.OrdinalIgnoreCase)
        ? FilePath : null;

    public static TaskOutcome Import(int added, int updated) =>
        new(TaskOutcomeKind.Import, added, updated, 0, true, null, null);
    public static TaskOutcome Export(int writtenRows, string path) =>
        new(TaskOutcomeKind.Export, null, null, null, false, writtenRows, path);
    public static TaskOutcome Report(int writtenRows, string path) =>
        new(TaskOutcomeKind.Report, null, null, null, false, writtenRows, path);
    public static TaskOutcome Rejected(int? failedRows = null, bool complete = false, string? reportPath = null) =>
        new(TaskOutcomeKind.Rejected, 0, 0, failedRows, complete, null, reportPath);

    // 仅用于用户明确选择的输出文件或目录；默认导出仍限制在 exports 中。
    internal static TaskOutcome ReportSelectedCsv(int writtenRows, string path) =>
        new(TaskOutcomeKind.Report, null, null, null, false, writtenRows, path, userSelected: true);
    internal static TaskOutcome ExportSelectedXlsx(int writtenRows, string path) =>
        new(TaskOutcomeKind.Export, null, null, null, false, writtenRows, path, ".xlsx", userSelected: true);

    public string Summary => Kind switch
    {
        TaskOutcomeKind.Import => Loc.Format("tasks.result.import", Display(Added), Display(Updated)),
        TaskOutcomeKind.Export => Loc.Format("tasks.result.export", Display(WrittenRows)),
        TaskOutcomeKind.Report => Loc.Format("tasks.result.report", Display(WrittenRows)),
        TaskOutcomeKind.Rejected => Loc.Format("tasks.result.rejected",
            FailedRows.HasValue && !FailureCountComplete
                ? Loc.Format("tasks.result.atLeast", FailedRows.Value) : Display(FailedRows)),
        _ => "",
    };

    public string ResultText => FilePath is { } path
        ? Summary + Environment.NewLine + Loc.Format("tasks.result.path", path) : Summary;

    private static int? Count(int? value) => value >= 0 ? value : null;
    private static object Display(int? value) => value.HasValue ? value.Value : Loc.Get("tasks.result.unknown");

    private static string? SafeOutputPath(string? path, string extension, bool userSelected)
    {
        if (string.IsNullOrWhiteSpace(path) || path.Length > 1024 || !Path.IsPathFullyQualified(path))
            return null;
        foreach (var c in path)
            if (char.IsControl(c)) return null;
        try
        {
            var full = Path.GetFullPath(path);
            var root = Path.TrimEndingDirectorySeparator(Path.GetFullPath(AppPaths.ExportDirectory));
            var comparison = OperatingSystem.IsWindows() ? StringComparison.OrdinalIgnoreCase : StringComparison.Ordinal;
            if ((!userSelected && !full.StartsWith(root + Path.DirectorySeparatorChar, comparison))
                || !string.Equals(Path.GetExtension(full), extension, StringComparison.OrdinalIgnoreCase))
                return null;
            // Reject symlinks/junctions in the artifact and all ancestors.
            for (string? current = full; current is not null; current = Path.GetDirectoryName(current))
            {
                try
                {
                    if ((File.GetAttributes(current) & FileAttributes.ReparsePoint) != 0) return null;
                }
                catch (FileNotFoundException) { }
                catch (DirectoryNotFoundException) { }
            }
            return full.Length <= 1024 ? full : null;
        }
        catch (Exception ex) when (ex is ArgumentException or IOException or UnauthorizedAccessException
                                   or NotSupportedException or System.Security.SecurityException)
        {
            return null;
        }
    }
}
