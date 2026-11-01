using Avalonia.Logging;
using Serilog;
using AvaloniaLevel = Avalonia.Logging.LogEventLevel;
using SerilogLevel = Serilog.Events.LogEventLevel;

namespace Plinth.App.Diagnostics;

/// <summary>
/// 把 Avalonia 自身日志转发给 Serilog(§11.3 启动时集中配置一次适配)。
/// 只放行 Warning 及以上:预览层 Debug 日志量很大,写进文件没有价值。
/// 每次写时现取 <see cref="Log.Logger"/>,不快照,这样换 logger 也不用重装 sink。
/// </summary>
internal sealed class AvaloniaSerilogSink : ILogSink
{
    public bool IsEnabled(AvaloniaLevel level, string area) => level >= AvaloniaLevel.Warning;

    public void Log(AvaloniaLevel level, string area, object? source, string messageTemplate)
        => Write(level, area, source, messageTemplate, []);

    public void Log(AvaloniaLevel level, string area, object? source, string messageTemplate, params object?[] propertyValues)
        => Write(level, area, source, messageTemplate, propertyValues);

    private void Write(AvaloniaLevel level, string area, object? source, string messageTemplate, object?[] propertyValues)
    {
        if (!IsEnabled(level, area))
        {
            return;
        }

        Serilog.Log.Logger
            .ForContext("AvaloniaArea", area)
            .ForContext("AvaloniaSource", source?.GetType().Name ?? "unknown")
            .Write(ToSerilogLevel(level), messageTemplate, propertyValues);
    }

    private static SerilogLevel ToSerilogLevel(AvaloniaLevel level) => level switch
    {
        AvaloniaLevel.Verbose => SerilogLevel.Verbose,
        AvaloniaLevel.Debug => SerilogLevel.Debug,
        AvaloniaLevel.Information => SerilogLevel.Information,
        AvaloniaLevel.Warning => SerilogLevel.Warning,
        AvaloniaLevel.Error => SerilogLevel.Error,
        AvaloniaLevel.Fatal => SerilogLevel.Fatal,
        _ => SerilogLevel.Information,
    };
}
