using System.Collections.ObjectModel;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using Microsoft.Extensions.Logging;
using Plinth.App.Navigation;
using Plinth.App.Security;
using Plinth.App.Services;
using Plinth.App.ViewModels;

namespace Plinth.App.Modules.OfflineSync.ViewModels;

public partial class OfflineSyncViewModel : ViewModelBase, IPageDetails, IDisposable
{
    private const string EditPermission = "offlineSync:edit";
    private const string SyncPermission = "offlineSync:sync";
    private const string ResolvePermission = "offlineSync:resolve";
    private readonly OfflineSyncPilot pilot;
    private readonly SensitiveOperationService authorized;
    private readonly IPermissionService permissions;
    private readonly IRemoteUserContext users;
    private readonly ILogger<OfflineSyncViewModel> logger;
    private readonly CancellationTokenSource lifetime = new();
    private readonly string subject;
    private readonly long sessionVersion;
    private bool disposed;

    public OfflineSyncViewModel(OfflineSyncPilot pilot, SensitiveOperationService authorized,
        IPermissionService permissions, IRemoteUserContext users, ILogger<OfflineSyncViewModel> logger)
    {
        this.pilot = pilot; this.authorized = authorized; this.permissions = permissions;
        this.users = users; this.logger = logger; subject = users.CurrentUser.Subject; sessionVersion = users.SessionVersion;
        _ = RefreshAsync();
    }

    public ObservableCollection<PilotOperation> Items { get; } = [];
    public IReadOnlyList<PageDetail> Details => [new("范围", "当前部署与账号隔离范围已固定"), new("传输", "本地 SQLite 演示；未配置生产 HTTP")];
    public bool CanEdit => Allowed(EditPermission);
    public bool CanSync => Allowed(SyncPermission);
    public bool CanResolve => Allowed(ResolvePermission);
    public bool HasSelection => Selected is not null;
    public bool IsBusy { get; private set; }

    [ObservableProperty] private PilotOperation? selected;
    [ObservableProperty] private string recordIdText = "";
    [ObservableProperty] private string expectedVersionText = "1";
    [ObservableProperty] private string code = "";
    [ObservableProperty] private string name = "";
    [ObservableProperty] private string remark = "";
    [ObservableProperty] private string evidenceReference = "";
    [ObservableProperty] private PilotOutcome reconcileOutcome = PilotOutcome.Applied;
    [ObservableProperty] private string serverVersionText = "";
    [ObservableProperty] private string statusText = "";
    public PilotOutcome[] ReconcileOutcomes { get; } = [PilotOutcome.Applied, PilotOutcome.DefinitelyNotAppliedTransient, PilotOutcome.Rejected];

    partial void OnSelectedChanged(PilotOperation? value)
    {
        OnPropertyChanged(nameof(HasSelection));
        if (value is null) return;
        RecordIdText = value.RecordId.ToString("D"); ExpectedVersionText = value.ExpectedVersion.ToString();
        Code = value.Local.Code; Name = value.Local.Name; Remark = value.Local.Remark;
        ServerVersionText = value.Server?.Version.ToString() ?? "";
    }

    [RelayCommand]
    private async Task RefreshAsync()
    {
        if (!Guard()) return;
        try { IsBusy = true; await pilot.InitializeAsync(lifetime.Token); if (!Guard()) return; var rows = await pilot.ListAsync(token: lifetime.Token); if (!Guard()) return; Items.Clear(); foreach (var x in rows) Items.Add(x); StatusText = $"已加载 {Items.Count} 项。Pending 可发送，Uncertain 必须先核对。"; }
        catch (OperationCanceledException) when (disposed) { }
        catch (Exception ex) { if (!disposed) Fail(ex, "加载状态失败"); }
        finally { if (!disposed) IsBusy = false; }
    }

    [RelayCommand]
    private async Task EditOfflineAsync()
    {
        if (!Guard() || !CanEdit || !Guid.TryParse(RecordIdText, out var id) || !int.TryParse(ExpectedVersionText, out var version)) { StatusText = "请输入有效的记录 ID 和正整数版本。"; return; }
        try { IsBusy = true; await authorized.ExecuteAsync(EditPermission, "offlineSync", "edit", _ => pilot.EditOfflineAsync(id, version, new(Code, Name, Remark), Selected?.State == PilotState.Pending ? Selected.OperationId : null, lifetime.Token), lifetime.Token); StatusText = "离线草稿已持久化，尚未发送。"; await RefreshAsync(); }
        catch (OperationCanceledException) when (disposed) { }
        catch (Exception ex) { if (!disposed) Fail(ex, "离线编辑失败"); }
        finally { if (!disposed) IsBusy = false; }
    }

    [RelayCommand]
    private async Task SyncOneAsync()
    {
        if (!Guard() || !CanSync) return;
        try { IsBusy = true; var sent = await authorized.ExecuteAsync(SyncPermission, "offlineSync", "sync", _ => pilot.SyncOneAsync(lifetime.Token), lifetime.Token); StatusText = sent ? "已发送一个 Pending；未知结果请核对。" : "没有可安全发送的 Pending。"; await RefreshAsync(); }
        catch (OperationCanceledException) when (disposed) { }
        catch (Exception ex) { if (!disposed) Fail(ex, "同步失败；请刷新并核对 Uncertain"); }
        finally { if (!disposed) IsBusy = false; }
    }

    [RelayCommand]
    private async Task ReconcileAsync()
    {
        if (!Guard() || !CanResolve || Selected?.State != PilotState.Uncertain || string.IsNullOrWhiteSpace(EvidenceReference)) { StatusText = "请选择 Uncertain 并填写权威证据引用。"; return; }
        PilotRecord? server = null;
        if (ReconcileOutcome == PilotOutcome.Applied)
        {
            if (!Guid.TryParse(RecordIdText, out var id) || !int.TryParse(ServerVersionText, out var version) || version <= 0) { StatusText = "Applied 核对需要有效服务端记录 ID 和版本。"; return; }
            server = new(id, version, new(Code, Name, Remark));
        }
        try { IsBusy = true; await authorized.ExecuteAsync(ResolvePermission, "offlineSync", "reconcile", async _ => { await pilot.ReconcileAsync(Selected.OperationId, new(ReconcileOutcome, server), EvidenceReference, lifetime.Token); return true; }, lifetime.Token); StatusText = "不确定状态已按权威结果解除；不会自动重试。"; await RefreshAsync(); }
        catch (OperationCanceledException) when (disposed) { }
        catch (Exception ex) { if (!disposed) Fail(ex, "核对失败；仍保持原状态"); }
        finally { if (!disposed) IsBusy = false; }
    }

    [RelayCommand]
    private async Task AcceptServerAsync()
    {
        if (!Guard() || !CanResolve || Selected?.State != PilotState.Conflict) return;
        try { IsBusy = true; await authorized.ExecuteAsync(ResolvePermission, "offlineSync", "acceptServer", _ => pilot.ResolveConflictAsync(Selected.OperationId, null, lifetime.Token), lifetime.Token); StatusText = "已接受保存的服务端快照；未产生网络写入。"; await RefreshAsync(); }
        catch (OperationCanceledException) when (disposed) { }
        catch (Exception ex) { if (!disposed) Fail(ex, "接受服务端快照失败"); }
        finally { if (!disposed) IsBusy = false; }
    }

    [RelayCommand]
    private async Task MergeAsync()
    {
        if (!Guard() || !CanResolve || Selected?.State != PilotState.Conflict) return;
        try { IsBusy = true; await authorized.ExecuteAsync(ResolvePermission, "offlineSync", "merge", _ => pilot.ResolveConflictAsync(Selected.OperationId, new(Code, Name, Remark), lifetime.Token), lifetime.Token); StatusText = "已生成新的 Pending 合并操作；请单独发送。"; await RefreshAsync(); }
        catch (OperationCanceledException) when (disposed) { }
        catch (Exception ex) { if (!disposed) Fail(ex, "合并冲突失败"); }
        finally { if (!disposed) IsBusy = false; }
    }

    private bool Allowed(string permission) => !DeploymentConfiguration.Current.Authentication.Enabled || permissions.HasPermission(permission);
    private bool Guard()
    {
        if (disposed) return false;
        if (DeploymentConfiguration.Current.Authentication.Enabled && (!string.Equals(subject, users.CurrentUser.Subject, StringComparison.Ordinal) || sessionVersion != users.SessionVersion)) { Items.Clear(); Selected = null; StatusText = "账号或授权已变化，请重新打开页面。"; return false; }
        return true;
    }
    private void Fail(Exception ex, string text) { logger.LogWarning("Offline sync page operation failed. ExceptionType {ExceptionType}", ex.GetType().Name); StatusText = text; }
    public void Dispose() { if (disposed) return; disposed = true; lifetime.Cancel(); lifetime.Dispose(); }
}
