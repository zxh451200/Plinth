using System;
using System.Collections.Generic;
using Avalonia.Controls;
using Avalonia.Headless.XUnit;
using Plinth.App.Localization;
using Plinth.App.Services;
using Plinth.App.Views;
using Serilog;
using Serilog.Core;
using Serilog.Events;
using Xunit;

namespace Plinth.App.Tests;

public sealed class UnhandledErrorReporterTests
{
    [Theory]
    [InlineData("zh-CN")]
    [InlineData("en-US")]
    public void Safe_summary_is_localized_and_has_unique_correlation_ids(string language)
    {
        try
        {
            Loc.Initialize(language);
            var error = UnhandledErrorPresentation.Create();
            Assert.Equal("UI_UNEXPECTED", error.Code);
            Assert.Equal(Loc.Get("error.body"), error.Message);
            Assert.Equal(Loc.Get("tasks.error.next"), error.NextStep);
            Assert.False(error.CanRetry);
            Assert.True(Guid.TryParse(error.CorrelationId, out _));
            Assert.NotEqual(error.CorrelationId, UnhandledErrorPresentation.Create().CorrelationId);
            var text = UnhandledErrorPresentation.Format(error);
            Assert.Contains(Loc.Get("logs.errorCode") + ": UI_UNEXPECTED", text);
            Assert.Contains(Loc.Get("logs.correlationId") + ": " + error.CorrelationId, text);
        }
        finally { Loc.Initialize(Loc.DefaultLanguage); }
    }

    [Fact]
    public void Reporter_keeps_original_exception_only_in_correlated_log()
    {
        var sink = new CaptureSink();
        var previous = Log.Logger;
        using var logger = new LoggerConfiguration().WriteTo.Sink(sink).CreateLogger();
        var exception = new InvalidOperationException("token=secret /home/private/customer");
        try
        {
            Log.Logger = logger;
            UnhandledErrorReporter.Report(exception);
            var entry = Assert.Single(sink.Events.FindAll(e =>
                e.MessageTemplate.Text.StartsWith("UI unhandled error.", StringComparison.Ordinal)));
            Assert.Same(exception, entry.Exception);
            Assert.Equal("UI_UNEXPECTED", ((ScalarValue)entry.Properties["ErrorCode"]).Value);
            Assert.True(Guid.TryParse((string?)((ScalarValue)entry.Properties["CorrelationId"]).Value, out _));
            Assert.DoesNotContain("secret", entry.RenderMessage());
        }
        finally { Log.Logger = previous; }
    }

    [AvaloniaFact]
    public void Dialog_displays_only_safe_summary_and_hides_machine_path()
    {
        var error = UnhandledErrorPresentation.Create();
        var dialog = new ErrorDialog(error);
        var text = dialog.FindControl<TextBlock>("DetailText")!.Text!;
        Assert.Contains(error.Code, text);
        Assert.Contains(error.CorrelationId, text);
        Assert.Contains(error.NextStep, text);
        Assert.False(dialog.FindControl<TextBlock>("LogPathText")!.IsVisible);
        dialog.Close();
    }

    private sealed class CaptureSink : ILogEventSink
    {
        public List<LogEvent> Events { get; } = [];
        public void Emit(LogEvent logEvent) => Events.Add(logEvent);
    }
}
