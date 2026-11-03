using System;
using System.Diagnostics;
using System.IO;
using System.Text.Json;
using Plinth.App.Diagnostics;
using Plinth.App.Services;
using Serilog;
using Serilog.Core;
using Serilog.Events;
using Serilog.Formatting.Compact;
using Xunit;

namespace Plinth.App.Tests;

public sealed class RedactingLogSinkTests
{
    [Fact]
    public void Raw_output_excludes_secrets_business_fields_paths_and_exception_text()
    {
        using var output = new StringWriter();
        using (var logger = new LoggerConfiguration()
            .WriteTo.Sink(new RedactingLogSink(new LoggerConfiguration()
                .WriteTo.Sink(new FormattedSink(output)).CreateLogger())).CreateLogger())
        {
            logger.ForContext("AccessToken", "unlabelled-secret")
                .ForContext("Code", "private-business-code")
                .ForContext("Payload", new { Value = "private-body" }, destructureObjects: true)
                .ForContext("Details", new { Password = "nested-secret", Detail = "token=nested-token" },
                    destructureObjects: true)
                .Error(new IOException("unlabelled-exception-secret /private/data.db"),
                    "Operation failed {Detail}", "Bearer abc.def /private/file.csv user@example.com");
        }
        var raw = output.ToString();
        foreach (var forbidden in new[] { "unlabelled-secret", "private-business-code", "private-body",
                     "nested-secret", "nested-token", "unlabelled-exception-secret", "/private/",
                     "abc.def", "user@example.com" })
            Assert.DoesNotContain(forbidden, raw);
        using var json = JsonDocument.Parse(raw);
        Assert.False(json.RootElement.TryGetProperty("@x", out _));
        Assert.Equal(typeof(IOException).FullName, json.RootElement.GetProperty("ExceptionType").GetString());
    }

    [Fact]
    public void Binding_warnings_keep_the_failing_property_and_expression()
    {
        using var output = new StringWriter();
        using (var logger = new LoggerConfiguration()
            .WriteTo.Sink(new RedactingLogSink(new LoggerConfiguration()
                .WriteTo.Sink(new FormattedSink(output)).CreateLogger())).CreateLogger())
        {
            logger.ForContext("AvaloniaArea", "Binding").ForContext("AvaloniaSource", "Button")
                .Warning("An error occurred binding {Property} to {Expression} at {ExpressionErrorPoint}: {Message}",
                    "IsEnabled", "SelectedItem.CanCancel", "IsEnabled",
                    "Could not convert '(null)' (null) to 'System.Boolean'.");
            logger.ForContext("AvaloniaArea", "Layout")
                .Warning("Paint failed {Message}", "private-business-text");
        }

        var lines = output.ToString().Split(Environment.NewLine, StringSplitOptions.RemoveEmptyEntries);
        using var binding = JsonDocument.Parse(lines[0]);
        Assert.Equal("IsEnabled", binding.RootElement.GetProperty("Property").GetString());
        Assert.Equal("SelectedItem.CanCancel", binding.RootElement.GetProperty("Expression").GetString());
        Assert.Contains("System.Boolean", binding.RootElement.GetProperty("Message").GetString());
        // 放行只限 Binding 区域,其它区域的 Message 仍然丢弃。
        using var other = JsonDocument.Parse(lines[1]);
        Assert.False(other.RootElement.TryGetProperty("Message", out _));
        Assert.DoesNotContain("private-business-text", lines[1]);
    }

    [Fact]
    public void Written_events_remain_readable_with_audit_classification_and_ids()
    {
        using var output = new StringWriter();
        var id = Guid.NewGuid();
        using (var logger = new LoggerConfiguration()
            .WriteTo.Sink(new RedactingLogSink(new LoggerConfiguration()
                .WriteTo.Sink(new FormattedSink(output)).CreateLogger())).CreateLogger())
        {
            logger.ForContext("EventKind", "SecurityAudit")
                .ForContext("Module", "Demo").ForContext("TaskId", id)
                .ForContext("CorrelationId", id).ForContext("Actor", "local-user")
                .Information("SecurityAudit {Operation} {Outcome}", "demo:save", "succeeded");
        }
        var entry = Assert.IsType<LogEntry>(LogReader.Parse(output.ToString()));
        Assert.Equal(LogEventKind.SecurityAudit, entry.EventKind);
        Assert.Equal(id.ToString("D"), entry.TaskId);
        Assert.Equal(id.ToString("D"), entry.CorrelationId);
        Assert.Equal("Demo", entry.Module);
        Assert.Contains("demo:save", entry.Message);
        Assert.Contains("succeeded", entry.Message);
    }

    [Theory]
    [InlineData("00000000-0000-0000-0000-000000000001")]
    [InlineData("00000000000000000000000000000001")]
    public void String_ids_and_audit_classification_survive_many_unknown_properties(string text)
    {
        using var output = new StringWriter();
        using var destination = new LoggerConfiguration().WriteTo.Sink(new FormattedSink(output)).CreateLogger();
        using var sink = new RedactingLogSink(destination);
        var properties = new System.Collections.Generic.List<LogEventProperty>();
        for (var i = 0; i < 100; i++)
            properties.Add(new LogEventProperty("Business" + i, new ScalarValue("private-business-text")));
        properties.Add(new LogEventProperty("CorrelationId", new ScalarValue(text)));
        properties.Add(new LogEventProperty("TaskId", new ScalarValue(text)));
        properties.Add(new LogEventProperty("EventId", new StructureValue(
            [new LogEventProperty("Name", new ScalarValue("SecurityAudit"))])));
        properties.Add(new LogEventProperty("Details", new StructureValue(
            [new LogEventProperty("Description", new ScalarValue("private-description"))])));
        sink.Emit(new LogEvent(DateTimeOffset.UtcNow, LogEventLevel.Information, null,
            new Serilog.Parsing.MessageTemplateParser().Parse("Operation finished"), properties));

        var raw = output.ToString();
        Assert.DoesNotContain("private-business-text", raw);
        Assert.DoesNotContain("private-description", raw);
        var entry = Assert.IsType<LogEntry>(LogReader.Parse(raw));
        Assert.Equal(LogEventKind.SecurityAudit, entry.EventKind);
        Assert.Equal(Guid.Parse(text).ToString("D"), entry.CorrelationId);
        Assert.Equal(Guid.Parse(text).ToString("D"), entry.TaskId);
    }

    [Fact]
    public void Trace_context_and_safe_exception_frames_are_preserved_without_exception_body()
    {
        using var output = new StringWriter();
        using var destination = new LoggerConfiguration().WriteTo.Sink(new FormattedSink(output)).CreateLogger();
        using var sink = new RedactingLogSink(destination);
        var trace = ActivityTraceId.CreateRandom();
        var span = ActivitySpanId.CreateRandom();
        Exception? failure = null;
        try { ThrowPrivateFailure(); }
        catch (IOException exception) { failure = exception; }
        Assert.NotNull(failure);
        sink.Emit(new LogEvent(DateTimeOffset.UtcNow, LogEventLevel.Error, failure,
            new Serilog.Parsing.MessageTemplateParser().Parse("Operation failed"), [], trace, span));

        var raw = output.ToString();
        using var json = JsonDocument.Parse(raw);
        Assert.Equal(trace.ToString(), json.RootElement.GetProperty("@tr").GetString());
        Assert.Equal(span.ToString(), json.RootElement.GetProperty("@sp").GetString());
        Assert.DoesNotContain("private-exception-body", raw);
        Assert.DoesNotContain("/private/", raw);
        var entry = Assert.IsType<LogEntry>(LogReader.Parse(raw));
        Assert.Contains(nameof(ThrowPrivateFailure), entry.Exception);
        Assert.Contains(typeof(IOException).FullName!, entry.Exception);
    }

    [System.Runtime.CompilerServices.MethodImpl(System.Runtime.CompilerServices.MethodImplOptions.NoInlining)]
    private static void ThrowPrivateFailure() =>
        throw new IOException("private-exception-body /private/data.db");

    [Theory]
    [InlineData("server-operation-1")]
    [InlineData("12345678901234567890")]
    public void Remote_correlation_ids_use_the_protocol_rules_without_phone_redaction(string correlation)
    {
        using var output = new StringWriter();
        using var logger = new LoggerConfiguration()
            .WriteTo.Sink(new RedactingLogSink(new LoggerConfiguration()
                .WriteTo.Sink(new FormattedSink(output)).CreateLogger())).CreateLogger();
        logger.ForContext("CorrelationId", correlation)
            .ForContext("Operation", "private business text")
            .ForContext("Result", "private-result")
            .Information("Remote operation finished");
        var raw = output.ToString();
        var entry = Assert.IsType<LogEntry>(LogReader.Parse(raw));
        Assert.Equal(correlation, entry.CorrelationId);
        Assert.DoesNotContain("private business text", raw);
        Assert.DoesNotContain("private-result", raw);
    }

    [Fact]
    public void Unsupported_id_objects_are_not_formatted()
    {
        using var output = new StringWriter();
        using var sink = new RedactingLogSink(new LoggerConfiguration()
            .WriteTo.Sink(new FormattedSink(output)).CreateLogger());
        sink.Emit(new LogEvent(DateTimeOffset.UtcNow, LogEventLevel.Warning, null,
            new Serilog.Parsing.MessageTemplateParser().Parse("Operation failed"),
            [new LogEventProperty("TaskId", new ScalarValue(new ThrowingValue()))]));
        Assert.NotEmpty(output.ToString());
        Assert.DoesNotContain("private-object", output.ToString());
    }

    private sealed class ThrowingValue
    {
        public override string ToString() => throw new InvalidOperationException("private-object");
    }

    [Theory]
    [InlineData(LogEventLevel.Debug)]
    [InlineData(LogEventLevel.Information)]
    [InlineData(LogEventLevel.Warning)]
    [InlineData(LogEventLevel.Error)]
    [InlineData(LogEventLevel.Fatal)]
    public void Technical_threshold_filters_events_without_suppressing_security_audit(LogEventLevel minimum)
    {
        using var output = new StringWriter();
        using var logger = new LoggerConfiguration().MinimumLevel.Verbose()
            .WriteTo.Sink(new RedactingLogSink(new LoggerConfiguration().MinimumLevel.Verbose()
                .WriteTo.Sink(new FormattedSink(output)).CreateLogger(), minimum)).CreateLogger();
        logger.Debug("Debug detail");
        logger.Information("Technical information");
        logger.ForContext("EventKind", "SecurityAudit").Information("Permission decision");
        logger.Information("SecurityAudit Legacy decision");
        logger.ForContext("EventId", new { Id = 4100, Name = "SecurityAudit" }, true)
            .Information("Legacy event decision");
        logger.ForContext("TaskId", Guid.NewGuid()).Information("Task progress");
        logger.Fatal("Fatal failure");
        var lines = output.ToString().Split(Environment.NewLine, StringSplitOptions.RemoveEmptyEntries);
        var entries = Array.ConvertAll(lines, line => LogReader.Parse(line)!);
        Assert.Equal(3, Array.FindAll(entries, entry => entry.EventKind == LogEventKind.SecurityAudit).Length);
        Assert.Contains(entries, entry => entry.Message == "Fatal failure");
        Assert.Equal(minimum <= LogEventLevel.Debug, output.ToString().Contains("Debug detail"));
        Assert.Equal(minimum <= LogEventLevel.Information, output.ToString().Contains("Technical information"));
        Assert.Equal(minimum <= LogEventLevel.Information, output.ToString().Contains("Task progress"));
    }

    private sealed class FormattedSink(TextWriter output) : ILogEventSink
    {
        private readonly CompactJsonFormatter _formatter = new();
        public void Emit(LogEvent logEvent) => _formatter.Format(logEvent, output);
    }
}
