using System;
using System.Collections.Generic;
using System.Linq;
using Avalonia.Controls;
using Avalonia.Headless.XUnit;
using Avalonia.Logging;
using Avalonia.VisualTree;
using Plinth.App.Localization;
using Xunit;
using AvaloniaLog = Avalonia.Logging.Logger;
using AvaloniaLogEventLevel = Avalonia.Logging.LogEventLevel;

namespace Plinth.App.Tests;

/// <summary>
/// 绑定告警门禁:打开每个页面和任务面板都不允许产生 Avalonia Binding 告警。
/// 告警会写进日志页,噪声会盖住真故障,所以按零容忍处理。
/// </summary>
[Collection("Localization")]
public sealed class BindingWarningTests
{
    [AvaloniaFact]
    public void Opening_every_page_and_panel_produces_no_binding_warnings()
    {
        Loc.Initialize(Loc.DefaultLanguage);
        var sink = new CollectingSink();
        var previous = AvaloniaLog.Sink;
        AvaloniaLog.Sink = sink;
        try
        {
            using var shell = new Shell();
            foreach (var item in shell.ViewModel.NavigationItems.ToArray())
            {
                shell.ViewModel.OpenTabCommand.Execute(item);
                shell.Layout();
            }

            // 选中一条记录后才会走详情区的绑定,空列表时测不到。
            var logs = shell.ViewModel.Tabs
                .Select(tab => tab.Content)
                .OfType<Plinth.App.ViewModels.LogViewModel>()
                .First();
            logs.Entries.Add(new Plinth.App.Services.LogEntry(DateTimeOffset.Now, "Information", "binding probe"));
            logs.SelectedEntry = logs.Entries[^1];
            shell.Layout();

            shell.ViewModel.ShowTaskPanelCommand.Execute(null);
            shell.Layout();
            shell.ViewModel.ShowLogsCommand.Execute(null);
            shell.Layout();
            // 工作区会保存侧栏折叠状态,启动时就可能是折叠态。
            shell.ViewModel.ToggleSidebarCompactCommand.Execute(null);
            shell.Layout();
            shell.ViewModel.ToggleSidebarCompactCommand.Execute(null);
            shell.Layout();
        }
        finally
        {
            AvaloniaLog.Sink = previous;
        }

        Assert.True(sink.BindingWarnings.Count == 0,
            "打开页面时产生了绑定告警:" + Environment.NewLine + string.Join(Environment.NewLine, sink.BindingWarnings));
    }

    [AvaloniaFact]
    public void Every_page_resolves_to_its_real_view()
    {
        Loc.Initialize(Loc.DefaultLanguage);
        var rendered = new List<string>();
        var previous = Serilog.Log.Logger;
        Serilog.Log.Logger = new Serilog.LoggerConfiguration()
            .MinimumLevel.Warning()
            .WriteTo.Sink(new MessageSink(rendered))
            .CreateLogger();
        try
        {
            using var shell = new Shell();
            foreach (var item in shell.ViewModel.NavigationItems.ToArray())
            {
                shell.ViewModel.OpenTabCommand.Execute(item);
                shell.Layout();
            }
        }
        finally
        {
            Serilog.Log.Logger = previous;
        }

        Assert.DoesNotContain(rendered, line => line.Contains("找不到页面", StringComparison.Ordinal));
    }

    private sealed class MessageSink(List<string> lines) : Serilog.Core.ILogEventSink
    {
        public void Emit(Serilog.Events.LogEvent logEvent) => lines.Add(logEvent.RenderMessage());
    }

    private sealed class CollectingSink : ILogSink
    {
        public List<string> BindingWarnings { get; } = [];

        public bool IsEnabled(AvaloniaLogEventLevel level, string area) => level >= AvaloniaLogEventLevel.Warning;

        public void Log(AvaloniaLogEventLevel level, string area, object? source, string messageTemplate) =>
            Record(level, area, source, messageTemplate, []);

        public void Log(AvaloniaLogEventLevel level, string area, object? source, string messageTemplate,
            params object?[] propertyValues) => Record(level, area, source, messageTemplate, propertyValues);

        private void Record(AvaloniaLogEventLevel level, string area, object? source, string messageTemplate,
            object?[] propertyValues)
        {
            if (!IsEnabled(level, area)) return;
            var where = source switch
            {
                Control control => $"{control.GetType().Name}"
                                   + (string.IsNullOrEmpty(control.Name) ? "" : $"#{control.Name}")
                                   + $" parent={control.GetVisualParent()?.GetType().Name ?? "-"}"
                                   + $" data={control.DataContext?.GetType().Name ?? "null"}",
                null => "unknown",
                _ => source.GetType().Name,
            };
            var values = propertyValues.Length == 0 ? "" : " [" + string.Join("; ", propertyValues.Select(v => v?.ToString())) + "]";
            var line = $"{area}/{level} {where}: {messageTemplate}{values}";
            if (area == "Binding") BindingWarnings.Add(line);
        }
    }
}
