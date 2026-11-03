using System;
using System.IO;
using System.Text;
using System.Threading.Tasks;
using Plinth.App.Diagnostics;
using Serilog;
using Serilog.Core;
using Serilog.Debugging;
using Serilog.Events;
using Xunit;

namespace Plinth.App.Tests;

[CollectionDefinition("Serilog fallback", DisableParallelization = true)]
public sealed class SerilogFallbackCollection;

[Collection("Serilog fallback")]
public sealed class LogWriteFallbackTests
{
    [Fact]
    public void Buffer_retains_only_bounded_metadata_and_canonical_ids()
    {
        using var error = new StringWriter();
        var fallback = new LogWriteFallback(error);
        using var logger = new LoggerConfiguration().WriteTo.Sink(fallback).CreateLogger();
        var id = Guid.NewGuid();
        for (var i = 0; i < LogWriteFallback.Capacity + 20; i++)
            logger.ForContext("CorrelationId", id.ToString("N"))
                .ForContext("TaskId", id)
                .Error(new IOException("secret exception"), "secret body {Payload}", new string('x', 100_000));

        var entries = fallback.Snapshot();
        Assert.Equal(LogWriteFallback.Capacity, entries.Length);
        Assert.All(entries, entry =>
        {
            Assert.Equal(LogEventLevel.Error, entry.Level);
            Assert.Equal(id, entry.CorrelationId);
            Assert.Equal(id, entry.TaskId);
            Assert.DoesNotContain("secret", entry.ToString());
        });
        Assert.Equal("", error.ToString());
    }

    [Fact]
    public void Non_id_properties_are_discarded()
    {
        var fallback = new LogWriteFallback(TextWriter.Null);
        using var logger = new LoggerConfiguration().WriteTo.Sink(fallback).CreateLogger();
        logger.ForContext("CorrelationId", "/private/token")
            .ForContext("TaskId", new { Secret = "value" }, destructureObjects: true)
            .Information("message");
        var entry = Assert.Single(fallback.Snapshot());
        Assert.Null(entry.CorrelationId);
        Assert.Null(entry.TaskId);
    }

    [Fact]
    public void Concurrent_failures_emit_one_fixed_marker_without_selflog_content()
    {
        using var error = new StringWriter();
        var fallback = new LogWriteFallback(error);
        Parallel.For(0, 1000, _ => fallback.ReportFailure("secret path and token"));
        Assert.Equal(LogWriteFallback.FailureMarker + error.NewLine, error.ToString());
    }

    [Fact]
    public void Broken_stderr_does_not_escape_or_retry()
    {
        using var error = new BrokenWriter();
        var fallback = new LogWriteFallback(error);
        fallback.ReportFailure("private");
        fallback.ReportFailure("private");
        Assert.Equal(1, error.Attempts);
    }

    [Fact]
    public void Sink_failure_in_same_pipeline_preserves_current_event_and_does_not_throw()
    {
        using var error = new StringWriter();
        var fallback = new LogWriteFallback(error);
        SelfLog.Enable(fallback.ReportFailure);
        try
        {
            using var logger = new LoggerConfiguration()
                .WriteTo.Sink(fallback).WriteTo.Sink(new BrokenSink()).CreateLogger();
            var id = Guid.NewGuid();
            logger.ForContext("CorrelationId", id).Error("private message");
            Assert.Equal(id, Assert.Single(fallback.Snapshot()).CorrelationId);
            Assert.Equal(LogWriteFallback.FailureMarker + error.NewLine, error.ToString());
        }
        finally { SelfLog.Disable(); }
    }

    [Fact]
    public void Concurrent_events_and_snapshots_remain_bounded()
    {
        var fallback = new LogWriteFallback(TextWriter.Null);
        using var logger = new LoggerConfiguration().WriteTo.Sink(fallback).CreateLogger();
        Parallel.For(0, 1000, _ =>
        {
            logger.Information("event");
            Assert.InRange(fallback.Snapshot().Length, 1, LogWriteFallback.Capacity);
        });
        Assert.Equal(LogWriteFallback.Capacity, fallback.Snapshot().Length);
    }

    private sealed class BrokenSink : ILogEventSink
    {
        public void Emit(LogEvent logEvent) => throw new IOException("secret path");
    }

    private sealed class BrokenWriter : TextWriter
    {
        public override Encoding Encoding => Encoding.UTF8;
        public int Attempts { get; private set; }
        public override void WriteLine(string? value)
        {
            Attempts++;
            throw new IOException("stderr closed");
        }
    }
}
