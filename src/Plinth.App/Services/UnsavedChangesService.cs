using System;
using System.Threading;
using System.Threading.Tasks;
using Plinth.App.Navigation;

namespace Plinth.App.Services;

/// <summary>由主窗口提供对话框；没有窗口时保守地取消，不静默丢弃。</summary>
public sealed class UnsavedChangesService
{
    public static UnsavedChangesService Default { get; } = new();
    private readonly SemaphoreSlim _promptGate = new(1, 1);
    public Func<Task<UnsavedChangesChoice>>? PromptAsync { get; set; }

    public async Task<UnsavedChangesChoice> AskAsync()
    {
        await _promptGate.WaitAsync();
        try
        {
            return PromptAsync is { } prompt ? await prompt() : UnsavedChangesChoice.Cancel;
        }
        finally
        {
            _promptGate.Release();
        }
    }
}
