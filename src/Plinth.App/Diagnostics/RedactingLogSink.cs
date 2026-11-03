using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Linq;
using System.Text.RegularExpressions;
using Plinth.App.Services;
using Serilog;
using Serilog.Core;
using Serilog.Events;
using Serilog.Parsing;

namespace Plinth.App.Diagnostics;

/// <summary>只向文件管线传递安全事件；JSON 仍由标准 CompactJsonFormatter 输出。</summary>
internal sealed partial class RedactingLogSink(
    ILogger destination, LogEventLevel minimumLevel = LogEventLevel.Verbose) : ILogEventSink, IDisposable
{
    public void Emit(LogEvent logEvent)
    {
        // 技术阈值过滤普通事件；安全审计不受较高阈值抑制。
        if (logEvent.Level < minimumLevel && !IsAudit(logEvent)) return;
        // EF Core 每执行一条 SQL 都记 Information,而参数与命令文本按白名单全部脱敏,
        // 留下的一行没有任何诊断价值;命令失败等 Warning 及以上仍保留。
        if (logEvent.Level < LogEventLevel.Warning && IsEfCommand(logEvent)) return;
        // 固定白名单：分类和 ID 不受任意业务属性数量挤占。
        var properties = new List<LogEventProperty>();
        foreach (var name in new[] { "EventKind", "Module", "SourceContext", "TaskId", "RetryOf",
                     "CorrelationId", "ErrorCode", "Actor", "Operation", "Result", "Outcome",
                     "Status", "Timestamp", "ExceptionType", "Version", "Framework",
                     "AvaloniaArea", "AvaloniaSource" })
        {
            if (logEvent.Properties.TryGetValue(name, out var value))
                properties.Add(new LogEventProperty(name, Sanitize(name, value)));
        }
        if (IsAudit(logEvent))
        {
            properties.RemoveAll(property => property.Name == "EventKind");
            properties.Add(new LogEventProperty("EventKind", new ScalarValue("SecurityAudit")));
        }
        // 绑定告警不带参数就只剩一句模板,日志页无法定位控件与属性。
        // 仅 Avalonia Binding 区域放行这四个诊断字段:值是绑定路径、类型名和转换错误,不含业务正文。
        if (logEvent.Properties.TryGetValue("AvaloniaArea", out var area)
            && area is ScalarValue { Value: "Binding" })
        {
            foreach (var name in new[] { "Property", "Expression", "ExpressionErrorPoint", "Message" })
            {
                if (logEvent.Properties.TryGetValue(name, out var value))
                    properties.Add(new LogEventProperty(name, SanitizeBindingDetail(value)));
            }
        }
        // 不调用异常 Message、Data、InnerException 或 ToString。
        if (logEvent.Exception is { } exception)
        {
            properties.RemoveAll(property => property.Name == "ExceptionType");
            properties.Add(new LogEventProperty("ExceptionType",
                new ScalarValue(exception.GetType().FullName)));
            var frames = new StackTrace(exception, fNeedFileInfo: false).GetFrames();
            var stack = string.Join("\n", (frames ?? []).Take(32).Select(frame =>
            {
                var method = frame.GetMethod();
                return method is null ? "" : $"{method.DeclaringType?.FullName}.{method.Name}";
            }));
            properties.Add(new LogEventProperty("ExceptionStack",
                new ScalarValue(DiagnosticRedactor.Redact(stack))));
        }
        // 白名单外的属性会被丢弃;对应占位符也要收掉,否则日志页会显示字面量 {elapsed}。
        var kept = properties.Select(property => property.Name).ToHashSet(StringComparer.Ordinal);
        var tokens = logEvent.MessageTemplate.Tokens.Select(token => token switch
        {
            PropertyToken property when !kept.Contains(property.PropertyName) => new TextToken("[redacted]"),
            PropertyToken => token,
            TextToken text => new TextToken(DiagnosticRedactor.Redact(text.Text)),
            _ => token
        }).ToArray();
        var safe = new LogEvent(logEvent.Timestamp, logEvent.Level, null,
            new MessageTemplate(tokens),
            properties, logEvent.TraceId ?? default, logEvent.SpanId ?? default);
        destination.Write(safe);
    }

    public void Dispose() => (destination as IDisposable)?.Dispose();

    private static LogEventPropertyValue Sanitize(string name, LogEventPropertyValue value)
    {
        if (name is "CorrelationId" or "TaskId" or "RetryOf")
        {
            if (value is ScalarValue { Value: Guid id }) return new ScalarValue(id);
            if (value is ScalarValue { Value: string text })
            {
                if (Guid.TryParse(text, out var parsed)) return new ScalarValue(parsed);
                if (name == "CorrelationId" && DiagnosticRedactor.IsSafeCorrelationId(text))
                    return new ScalarValue(text);
            }
            return new ScalarValue("[redacted]");
        }
        if (name == "EventKind")
            return value is ScalarValue { Value: "Technical" or "SecurityAudit" or "Task" }
                ? value : new ScalarValue("Technical");
        if (name is "Result" or "Outcome")
            return value is ScalarValue
            {
                Value: "success" or "succeeded" or "failed"
                or "cancelled" or "rejected" or "denied"
            }
                ? value : new ScalarValue("[redacted]");
        if (name is "Operation" or "Module" or "ErrorCode" or "SourceContext")
            return value is ScalarValue { Value: string identifier } && identifier.Length <= 256
                && Identifier().IsMatch(identifier)
                ? new ScalarValue(DiagnosticRedactor.Redact(identifier)) : new ScalarValue("[redacted]");
        return value switch
        {
            ScalarValue { Value: string text } when text.Length <= 256 && SafeIdentifier().IsMatch(text)
                => new ScalarValue(DiagnosticRedactor.Redact(text)),
            ScalarValue
            {
                Value: null or bool or byte or sbyte or short or ushort or int or uint
                or long or ulong or float or double or decimal or Guid or DateTime or DateTimeOffset
            } => value,
            ScalarValue { Value: Enum status } when name == "Status" => new ScalarValue(status.ToString()),
            _ => new ScalarValue("[redacted]")
        };
    }

    private static LogEventPropertyValue SanitizeBindingDetail(LogEventPropertyValue value) =>
        value is ScalarValue { Value: string text }
            ? new ScalarValue(DiagnosticRedactor.Redact(text.Length > 256 ? text[..256] : text))
            : new ScalarValue("[redacted]");

    private static bool IsEfCommand(LogEvent entry) =>
        entry.Properties.TryGetValue("SourceContext", out var context)
        && context is ScalarValue { Value: "Microsoft.EntityFrameworkCore.Database.Command" };

    private static bool IsAudit(LogEvent entry) =>
        entry.MessageTemplate.Text.StartsWith("SecurityAudit ", StringComparison.Ordinal)
        || (entry.Properties.TryGetValue("EventKind", out var kind)
            && kind is ScalarValue { Value: "SecurityAudit" })
        || (entry.Properties.TryGetValue("EventId", out var eventId) && eventId is StructureValue structure
            && structure.Properties.Any(property =>
                (property.Name == "Name" && property.Value is ScalarValue { Value: "SecurityAudit" })
                || (property.Name == "Id" && property.Value is ScalarValue { Value: 4100 })));

    [GeneratedRegex(@"^[A-Za-z0-9_.:+@ -]+$")]
    private static partial Regex SafeIdentifier();

    [GeneratedRegex(@"^[A-Za-z0-9_.:-]+$")]
    private static partial Regex Identifier();
}
