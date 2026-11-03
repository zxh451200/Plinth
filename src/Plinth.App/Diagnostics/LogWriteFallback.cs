using System;
using System.Collections.Generic;
using System.IO;
using System.Threading;
using Serilog.Core;
using Serilog.Events;

namespace Plinth.App.Diagnostics;

/// <summary>同一日志管线的进程内兜底。不保留模板、异常、路径或自由文本，不提供 UI/导出入口。</summary>
internal sealed class LogWriteFallback(TextWriter error) : ILogEventSink
{
    internal const int Capacity = 128;
    internal const string FailureMarker = "PLINTH_LOG_WRITE_FAILED: bounded in-memory metadata only.";
    private readonly object _gate = new();
    private readonly Queue<Entry> _entries = new(Capacity);
    private int _failed;

    internal readonly record struct Entry(
        DateTimeOffset Timestamp, LogEventLevel Level, Guid? CorrelationId, Guid? TaskId);

    // 始终保留最近元数据，所以文件 sink 报错前后的事件都在有界窗口内。
    public void Emit(LogEvent logEvent)
    {
        var entry = new Entry(logEvent.Timestamp, logEvent.Level,
            ReadId(logEvent, "CorrelationId"), ReadId(logEvent, "TaskId"));
        lock (_gate)
        {
            if (_entries.Count == Capacity)
                _entries.Dequeue();
            _entries.Enqueue(entry);
        }
    }

    internal Entry[] Snapshot()
    {
        lock (_gate)
            return _entries.ToArray();
    }

    // SelfLog 原文可能含路径、异常和事件内容；只使用故障信号，绝不再次写 Serilog。
    internal void ReportFailure(string? ignored = null)
    {
        if (Interlocked.Exchange(ref _failed, 1) != 0)
            return;
        try { error.WriteLine(FailureMarker); }
        catch (Exception) { /* stderr 本身不可写时也不得产生第二次日志异常。 */ }
    }

    private static Guid? ReadId(LogEvent logEvent, string name)
    {
        if (!logEvent.Properties.TryGetValue(name, out var value) || value is not ScalarValue scalar)
            return null;
        return scalar.Value switch
        {
            Guid id => id,
            string text when text.Length is 32 or 36 && Guid.TryParse(text, out var id) => id,
            _ => null
        };
    }
}
