using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text.Json;
using System.Text.RegularExpressions;
using System.Threading;
using Plinth.App.Localization;

namespace Plinth.App.Services;

public enum LogEventKind { Technical, SecurityAudit, Task }

/// <summary>一行日志,给日志页显示用。</summary>
public sealed record LogEntry(DateTimeOffset Time, string Level, string Message)
{
    public LogEventKind EventKind { get; init; }
    public string TimeText => FormattingService.Current.FormatInstant(Time, includeSeconds: true);
    public string LevelText => LogLevels.DisplayName(Level);
    public string EventKindText => EventKind switch
    {
        LogEventKind.SecurityAudit => Loc.Get("logs.kind.audit"),
        LogEventKind.Task => Loc.Get("logs.kind.task"),
        _ => Loc.Get("logs.kind.technical"),
    };
    public string Module { get; init; } = "";
    public string Component { get; init; } = "";
    public string TaskId { get; init; } = "";
    public string ErrorCode { get; init; } = "";
    public string CorrelationId { get; init; } = "";
    public string Exception { get; init; } = "";
    public string Source { get; init; } = "";
    public string Details => $"{Time:O} {LevelText} {EventKindText}\n{Module} {Component}\n{TaskId} {ErrorCode} {CorrelationId}\n{Message}\n{Exception}\n{Source}";
}

public sealed record LogFilter(DateTimeOffset? From = null, DateTimeOffset? To = null,
    string Level = "", string Module = "", string TaskId = "", string ErrorCode = "",
    string CorrelationId = "", string Search = "", bool OnlyProblems = false, LogEventKind? EventKind = null,
    bool IncludeTaskEvents = false);
public sealed record LogQueryResult(IReadOnlyList<LogEntry> Entries, int InvalidLines, bool Truncated);

/// <summary>日志里可能出现的级别，级别筛选下拉按此顺序取值（与写入端一致）。</summary>
public static class LogLevels
{
    public static readonly string[] Names = ["Verbose", "Debug", "Information", "Warning", "Error", "Fatal", "Critical"];

    /// <summary>级别写进日志时保持英文标记,界面按当前语言显示。</summary>
    public static string DisplayName(string level) => level switch
    {
        "Verbose" => Loc.Get("logs.level.verbose"),
        "Debug" => Loc.Get("logs.level.debug"),
        "Information" => Loc.Get("logs.level.information"),
        "Warning" => Loc.Get("logs.level.warning"),
        "Error" => Loc.Get("logs.level.error"),
        "Fatal" => Loc.Get("logs.level.fatal"),
        "Critical" => Loc.Get("logs.level.critical"),
        _ => level,
    };
}

/// <summary>
/// 读取 Serilog CompactJson 日志：跨文件有界查询，共享打开正在写入的文件，坏行跳过。
/// </summary>
public static partial class LogReader
{
    public const int DefaultMaxEntries = 500;

    /// <summary>Bounded streaming scan across rolling files. Filters run before the result cap.</summary>
    public static LogQueryResult Query(string directory, LogFilter filter, int limit = DefaultMaxEntries,
        CancellationToken cancellationToken = default)
    {
        if (limit is < 1 or > 10000) throw new ArgumentOutOfRangeException(nameof(limit));
        if (!Directory.Exists(directory)) return new([], 0, false);
        var entries = new PriorityQueue<LogEntry, DateTimeOffset>();
        long scanned = 0;
        int invalid = 0, matches = 0;
        bool truncated = false;
        foreach (var file in new DirectoryInfo(directory).EnumerateFiles("*.ndjson")
                     .OrderByDescending(x => x.Name, StringComparer.Ordinal))
        {
            cancellationToken.ThrowIfCancellationRequested();
            if ((file.Attributes & FileAttributes.ReparsePoint) != 0) continue;
            using var stream = new FileStream(file.FullName, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete);
            using var reader = new StreamReader(stream);
            while (ReadBoundedLine(reader, out var line, out var chars))
            {
                cancellationToken.ThrowIfCancellationRequested();
                scanned += chars;
                if (scanned > 32 * 1024 * 1024) { truncated = true; break; }
                var entry = line is null ? null : Parse(line);
                if (entry is null) { invalid++; continue; }
                entry = entry with { Source = DiagnosticRedactor.Redact(file.Name) };
                if (!Matches(entry, filter)) continue;
                matches++;
                entries.Enqueue(entry, entry.Time);
                if (entries.Count > limit) entries.Dequeue();
            }
            if (truncated) break;
        }
        return new(entries.UnorderedItems.Select(x => x.Element).OrderByDescending(x => x.Time).ToArray(),
            invalid, truncated || matches > limit);
    }

    internal static bool ReadBoundedLine(TextReader reader, out string? line, out int chars)
    {
        var builder = new System.Text.StringBuilder();
        chars = 0;
        int c;
        while ((c = reader.Read()) >= 0)
        {
            chars++;
            if (c == '\n') break;
            if (builder.Length < 65536) builder.Append((char)c);
            if (chars > 1024 * 1024) throw new InvalidDataException("Log line exceeds scan limit.");
        }
        line = chars > 65536 ? null : builder.ToString();
        return chars > 0;
    }

    private static bool Matches(LogEntry e, LogFilter f) =>
        (!f.EventKind.HasValue || e.EventKind == f.EventKind ||
            (f.EventKind == LogEventKind.Technical && f.IncludeTaskEvents && e.EventKind == LogEventKind.Task)) &&
        (!f.OnlyProblems || e.Level is "Warning" or "Error" or "Fatal" or "Critical") &&
        (!f.From.HasValue || e.Time >= f.From) && (!f.To.HasValue || e.Time <= f.To) &&
        Match(e.Level, f.Level) && Match(e.Module, f.Module) && Match(e.TaskId, f.TaskId) &&
        Match(e.ErrorCode, f.ErrorCode) && Match(e.CorrelationId, f.CorrelationId) &&
        (string.IsNullOrWhiteSpace(f.Search) || e.Details.Contains(f.Search, StringComparison.OrdinalIgnoreCase));

    private static bool Match(string value, string filter) =>
        string.IsNullOrWhiteSpace(filter) || string.Equals(value, filter, StringComparison.OrdinalIgnoreCase);

    /// <summary>返回最新日志文件里最后 maxEntries 条,新的在前。目录或文件不存在返回空列表。</summary>
    public static IReadOnlyList<LogEntry> ReadLatest(string directory, int maxEntries = DefaultMaxEntries)
    {
        if (maxEntries <= 0) throw new ArgumentOutOfRangeException(nameof(maxEntries));
        if (!Directory.Exists(directory))
        {
            return [];
        }

        var latest = new DirectoryInfo(directory)
            .GetFiles("*.ndjson")
            .OrderByDescending(file => file.Name, StringComparer.Ordinal) // 文件名带日期,名字大的就是最新的
            .FirstOrDefault();
        if (latest is null)
        {
            return [];
        }

        var lines = new Queue<string>();
        using (var stream = new FileStream(latest.FullName, FileMode.Open, FileAccess.Read, FileShare.ReadWrite))
        using (var reader = new StreamReader(stream))
        {
            while (ReadBoundedLine(reader, out var line, out _))
            {
                lines.Enqueue(line ?? "");
                if (lines.Count > maxEntries)
                {
                    lines.Dequeue();
                }
            }
        }

        return lines.Select(Parse).OfType<LogEntry>().Reverse().ToList();
    }

    /// <summary>删除指定目录中早于保留天数的日志。返回删除的文件数。</summary>
    public static int DeleteOlderThan(string directory, TimeSpan age, Action? beforeDelete = null)
    {
        if (!Directory.Exists(directory))
        {
            return 0;
        }

        var threshold = DateTime.UtcNow - age;
        var deleted = 0;
        foreach (var file in new DirectoryInfo(directory).GetFiles("*.ndjson"))
        {
            if (file.LastWriteTimeUtc >= threshold)
            {
                continue;
            }

            beforeDelete?.Invoke();
            try
            {
                file.Delete();
                deleted++;
            }
            catch (IOException)
            {
                // 某个日志被其他程序占用时保留它,继续处理其他文件。
            }
            catch (UnauthorizedAccessException)
            {
                // 没权限删除时保留它,页面会显示实际删除数量。
            }
        }

        return deleted;
    }

    /// <summary>解析一行。不是合法日志行返回 null。</summary>
    public static LogEntry? Parse(string line)
    {
        if (line.Length > 65536) return null;
        try
        {
            using var document = JsonDocument.Parse(line);
            var root = document.RootElement;
            if (root.ValueKind != JsonValueKind.Object || !root.TryGetProperty("@t", out var time) || !time.TryGetDateTimeOffset(out var at))
            {
                return null;
            }

            var level = Text(root, "@l");
            if (level.Length == 0) level = "Information";
            var template = Text(root, "@mt");
            var message = DiagnosticRedactor.Redact(Render(template, root));
            var text = DiagnosticRedactor.Redact(Text(root, "@x"));
            var exceptionType = Safe(root, "ExceptionType");
            var stack = Safe(root, "ExceptionStack");
            if (exceptionType.Length > 0)
                text += (text.Length == 0 ? "" : Environment.NewLine) + exceptionType;
            if (stack.Length > 0)
                text += Environment.NewLine + stack;
            if (text.Length > 0)
            {
                message += Environment.NewLine + text;
            }

            return new LogEntry(at, level, message)
            {
                EventKind = ReadEventKind(root, template),
                Module = Safe(root, "Module"),
                Component = Safe(root, "SourceContext"),
                TaskId = Safe(root, "TaskId"),
                ErrorCode = Safe(root, "ErrorCode"),
                CorrelationId = Safe(root, "CorrelationId"),
                Exception = text,
            };
        }
        catch (Exception ex) when (ex is JsonException or InvalidOperationException or FormatException)
        {
            return null;
        }
    }

    private static LogEventKind ReadEventKind(JsonElement root, string template)
    {
        // 保留旧版审计 EventId／固定模板的识别，不让旧审计混入技术日志。
        if (Text(root, "EventKind") == "SecurityAudit"
            || template.StartsWith("SecurityAudit ", StringComparison.Ordinal)
            || (root.TryGetProperty("EventId", out var eventId) && eventId.ValueKind == JsonValueKind.Object
                && (Text(eventId, "Name") == "SecurityAudit"
                    || (eventId.TryGetProperty("Id", out var id) && id.ValueKind == JsonValueKind.Number
                        && id.TryGetInt32(out var number) && number == 4100))))
            return LogEventKind.SecurityAudit;
        return Text(root, "EventKind") == "Task" || Text(root, "TaskId").Length > 0
            ? LogEventKind.Task : LogEventKind.Technical;
    }

    private static string Text(JsonElement root, string key) =>
        root.TryGetProperty(key, out var value) && value.ValueKind == JsonValueKind.String ? value.GetString() ?? "" : "";
    private static string Safe(JsonElement root, string key)
    {
        var value = Text(root, key);
        if ((key is "TaskId" or "CorrelationId") && Guid.TryParse(value, out var id))
            return id.ToString("D");
        if (key == "CorrelationId" && DiagnosticRedactor.IsSafeCorrelationId(value))
            return value;
        return DiagnosticRedactor.Redact(value);
    }

    /// <summary>把模板里的 {Name} 换成同名属性的值;找不到的原样保留。</summary>
    private static string Render(string template, JsonElement root) =>
        Placeholder().Replace(template, match =>
        {
            var name = match.Groups[1].Value;
            if (Regex.IsMatch(name, "password|token|secret|authorization|cookie|body", RegexOptions.IgnoreCase))
                return "[redacted]";
            // 写盘时被白名单丢弃的属性在模板里已折成 [redacted];旧文件里的缺值占位符也按同样方式显示。
            return root.TryGetProperty(name, out var value)
                ? (value.ValueKind == JsonValueKind.String ? value.GetString() ?? string.Empty : value.ToString())
                : "[redacted]";
        });

    [GeneratedRegex(@"\{@?\$?([A-Za-z0-9_]+)(?:[:,][^}]*)?\}")]
    private static partial Regex Placeholder();
}
