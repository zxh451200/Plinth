using System.Threading.Tasks;
using CommunityToolkit.Mvvm.Input;
using Plinth.App.Navigation;
using Plinth.App.Services;

namespace Plinth.App.ViewModels;

public partial class MainViewModel
{
    [RelayCommand]
    private Task ShowModuleFailureLogsAsync() => _optionalModule?.CorrelationId is { } id
        ? OpenLogQueryAsync(new LogFilter(CorrelationId: id, EventKind: LogEventKind.Technical))
        : Task.CompletedTask;

    private async Task OpenLogQueryAsync(LogFilter filter)
    {
        if (_released || _closingTabs || _exitPrepared) return;
        OpenRegisteredPage(ShellNavigation.LogsId);
        if (SelectedTab?.Content is LogViewModel logs) await logs.OpenQueryAsync(filter);
    }

    [RelayCommand]
    private Task ShowErrorsAsync() => OpenLogQueryAsync(new LogFilter(OnlyProblems: true, IncludeTaskEvents: true));

    [RelayCommand]
    private Task ShowErrorLogsAsync(TaskError? error) => error is null || string.IsNullOrWhiteSpace(error.CorrelationId)
        ? Task.CompletedTask : OpenLogQueryAsync(new LogFilter(CorrelationId: error.CorrelationId,
            EventKind: error.Code == "TASK_FAILED" ? LogEventKind.Task : LogEventKind.Technical));

    [RelayCommand]
    private Task ShowTaskLogsAsync(WorkItem? item) => item is null ? Task.CompletedTask
        : OpenLogQueryAsync(new LogFilter(TaskId: item.Id.ToString("D"), EventKind: LogEventKind.Task));
}
