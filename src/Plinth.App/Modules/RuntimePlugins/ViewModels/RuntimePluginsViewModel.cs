using System.Collections.ObjectModel;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using Microsoft.Extensions.Logging;
using Plinth.App.Navigation;
using Plinth.App.Security;
using Plinth.App.Services;
using Plinth.App.Modules.RuntimePlugins;
using Plinth.App.ViewModels;
namespace Plinth.App.Modules.RuntimePlugins.ViewModels;

public sealed record RuntimePluginOperation(string PluginId, string Operation) { public string DisplayName => $"{PluginId} / {Operation}"; }
public partial class RuntimePluginsViewModel : ViewModelBase, IUnsavedChanges, IDisposable
{
    public const string InvokePermission = "runtimePlugins:invoke";
    private readonly RuntimePluginHost _host; private readonly ILogger<RuntimePluginsViewModel> _logger; private readonly IPermissionService _permissions; private readonly CancellationTokenSource _lifetime = new(); private CancellationTokenSource? _invocation; private bool _disposed;
    public RuntimePluginsViewModel(RuntimePluginHost host, RuntimePluginConfiguration configuration, ILogger<RuntimePluginsViewModel> logger, IPermissionService permissions)
    { _host = host; _logger = logger; _permissions = permissions; if (configuration.Enabled) foreach (var p in configuration.Allowlist) foreach (var op in p.Operations.Order(StringComparer.Ordinal)) Operations.Add(new(p.Id, op)); SelectedOperation = Operations.FirstOrDefault(); StatusText = RuntimePluginStrings.Get("ready"); }
    public ObservableCollection<RuntimePluginOperation> Operations { get; } = []; public bool IsConfigured => Operations.Count > 0; public bool IsDirty => false;
    public string Title => RuntimePluginStrings.Get("title"); public string Description => RuntimePluginStrings.Get("description"); public string DisabledText => RuntimePluginStrings.Get("disabled"); public string EmptyText => RuntimePluginStrings.Get("empty"); public string OperationLabel => RuntimePluginStrings.Get("operation"); public string InputLabel => RuntimePluginStrings.Get("input"); public string InputPlaceholder => RuntimePluginStrings.Get("placeholder"); public string InvokeText => RuntimePluginStrings.Get("invoke"); public string CancelText => RuntimePluginStrings.Get("cancel");
    [ObservableProperty][NotifyCanExecuteChangedFor(nameof(InvokeCommand))] public partial RuntimePluginOperation? SelectedOperation { get; set; }
    [ObservableProperty][NotifyCanExecuteChangedFor(nameof(InvokeCommand))] public partial string Input { get; set; } = string.Empty;
    [ObservableProperty][NotifyCanExecuteChangedFor(nameof(InvokeCommand))] public partial bool IsBusy { get; set; }
    [ObservableProperty] public partial string StatusText { get; set; }
    [ObservableProperty] public partial string Output { get; set; } = string.Empty;
    private bool CanInvoke() => !_disposed && !IsBusy && SelectedOperation is not null && Allowed();
    [RelayCommand(CanExecute = nameof(CanInvoke))]
    private async Task InvokeAsync()
    {
        if (SelectedOperation is not { } selected || !Allowed()) return;
        IsBusy = true;
        using var invocation = CancellationTokenSource.CreateLinkedTokenSource(_lifetime.Token);
        _invocation = invocation;
        try
        {
            var r = await _host.InvokeAsync(selected.PluginId, selected.Operation, Input, invocation.Token);
            if (_disposed) return;
            Output = r.Success ? r.Output ?? string.Empty : string.Empty;
            StatusText = r.Success ? RuntimePluginStrings.Get("success") : string.Format(RuntimePluginStrings.Get("error"), r.Error);
        }
        catch (OperationCanceledException)
        {
            if (!_disposed) StatusText = string.Format(RuntimePluginStrings.Get("error"), PluginError.Cancelled);
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Runtime plugin invocation failed");
            if (!_disposed) StatusText = string.Format(RuntimePluginStrings.Get("error"), PluginError.StartFailed);
        }
        finally
        {
            if (ReferenceEquals(_invocation, invocation)) _invocation = null;
            if (!_disposed) IsBusy = false;
        }
    }
    [RelayCommand] private void Cancel() => _invocation?.Cancel();
    public override void OnUiLanguageChanged() { StatusText = IsBusy ? StatusText : RuntimePluginStrings.Get("ready"); base.OnUiLanguageChanged(); }
    public Task<bool> TryLeaveAsync() { if (IsBusy) _invocation?.Cancel(); return Task.FromResult(true); }
    public void Dispose() { if (_disposed) return; _disposed = true; _lifetime.Cancel(); _invocation?.Cancel(); _lifetime.Dispose(); }
    private bool Allowed() => !DeploymentConfiguration.Current.Authentication.Enabled || _permissions.HasPermission(InvokePermission);
}
