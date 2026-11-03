using Avalonia.Controls.ApplicationLifetimes;
using Avalonia.Headless.XUnit;
using System.Collections.Immutable;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging.Abstractions;
using Plinth.App.Navigation;
using Plinth.App.Security;
using Plinth.App.Services;
using Plinth.App.ViewModels;
using Xunit;

namespace Plinth.App.Tests;

public sealed class PageLifetimeTests : IDisposable
{
    private readonly string _directory = Path.Combine(Path.GetTempPath(), "plinth-page-" + Guid.NewGuid().ToString("N"));

    public PageLifetimeTests() => Directory.CreateDirectory(_directory);
    public void Dispose() => Directory.Delete(_directory, recursive: true);

    [AvaloniaFact]
    public Task Closing_individual_tabs_releases_pages_without_attaching_views() => ClosePagesAsync("single");

    [AvaloniaFact]
    public Task Closing_all_tabs_releases_pages_without_attaching_views() => ClosePagesAsync("all");

    [AvaloniaFact]
    public Task Releasing_workspace_releases_pages_without_attaching_views() => ClosePagesAsync("workspace");

    private async Task ClosePagesAsync(string route)
    {
        await File.WriteAllTextAsync(Path.Combine(_directory, LicenseNoticeService.FileName), "synthetic notice");
        using var branding = new BrandingService(NullLogger<BrandingService>.Instance);
        var shell = CreateShell(branding);
        var tasks = new TaskService(NullLogger<TaskService>.Instance);
        using var logGate = new ReadGate("logs:read");
        using var previewGate = new ReadGate("diagnostics:export");
        using var logs = CreateLogs(logGate.Access);
        using var license = new LicenseViewModel(new LicenseNoticeService(_directory));
        using var diagnostics = CreateDiagnostics(tasks, previewGate.Access);
        ViewModelBase[] pages = [logs, license, diagnostics];
        foreach (var page in pages)
            shell.Tabs.Add(new WorkspaceTabViewModel(page.GetType().Name, "Page", page));
        var logRead = logs.LoadAsync();
        var preview = diagnostics.PreviewCommand.ExecuteAsync(null);
        await Task.WhenAll(logGate.Entered.Task, previewGate.Entered.Task).WaitAsync(TimeSpan.FromSeconds(5));
        // 不创建 View。后台读取由门闩阻塞，不依赖 Task.Run 的执行速度。
        var licenseRead = license.LoadAsync();
        Assert.True(license.IsBusy);
        Assert.True(logs.IsLoading);
        Assert.True(diagnostics.IsBusy);
        try
        {
            if (route == "single")
                foreach (var tab in shell.Tabs.ToArray())
                    await shell.CloseTabItemCommand.ExecuteAsync(tab);
            else if (route == "all")
                await shell.CloseAllTabsCommand.ExecuteAsync(null);
            else
                shell.ReleaseWorkspace();
        }
        finally
        {
            logGate.Release();
            previewGate.Release();
        }

        var changesAfterClose = 0;
        foreach (var page in pages)
            page.PropertyChanged += (_, _) => changesAfterClose++;
        await Task.WhenAll(licenseRead, logRead, preview);
        // 旧命令、计时器或迟到续体都不能重新发布到已经关闭的页面。
        await license.LoadAsync();
        await logs.LoadAsync();
        await diagnostics.PreviewCommand.ExecuteAsync(null);
        diagnostics.RefreshTasks();
        logs.ReportCopyFailure();
        diagnostics.ReportExportFailure();
        Assert.False(license.LoadCommand.CanExecute(null));
        Assert.False(license.IsBusy);
        Assert.Empty(license.Text);
        Assert.False(logs.IsLoading);
        Assert.Empty(logs.Entries);
        Assert.Null(logs.GetCopyText(false));
        Assert.False(diagnostics.IsBusy);
        Assert.False(diagnostics.HasPreview);
        Assert.False(diagnostics.Confirmed);
        Assert.Empty(diagnostics.Files);
        Assert.Empty(diagnostics.Items);
        Assert.Equal(0, changesAfterClose);
        shell.ReleaseWorkspace(); // 重复释放幂等。
        await tasks.StopAsync();
    }

    [AvaloniaFact]
    public async Task Temporary_detach_allows_log_reload_and_new_diagnostic_preview()
    {
        var tasks = new TaskService(NullLogger<TaskService>.Instance);
        using var logGate = new ReadGate("logs:read");
        using var previewGate = new ReadGate("diagnostics:export");
        using var logs = CreateLogs(logGate.Access);
        using var diagnostics = CreateDiagnostics(tasks, previewGate.Access);
        var logRead = logs.LoadAsync();
        var preview = diagnostics.PreviewCommand.ExecuteAsync(null);
        Task reloaded;
        try
        {
            await Task.WhenAll(logGate.Entered.Task, previewGate.Entered.Task).WaitAsync(TimeSpan.FromSeconds(5));
            logs.PageClosed();
            diagnostics.PageClosed();
            // 旧请求仍未退出时切回，新请求应独立运行，旧 finally 不得清除新状态。
            reloaded = logs.PageOpenedAsync();
            Assert.True(logs.IsLoading);
        }
        finally
        {
            logGate.Release();
            previewGate.Release();
        }
        await Task.WhenAll(logRead, preview, reloaded);
        await diagnostics.PreviewCommand.ExecuteAsync(null);
        Assert.False(logs.IsLoading);
        Assert.True(diagnostics.HasPreview);
        Assert.Single(diagnostics.Files);
        await tasks.StopAsync();
    }

    [AvaloniaFact]
    public async Task Closing_during_production_confirmation_does_not_enqueue_export()
    {
        var tasks = new TaskService(NullLogger<TaskService>.Instance);
        var audit = new SecurityAudit(NullLogger<SecurityAudit>.Instance);
        var users = new OfflineUserContext();
        var response = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
        var operations = new SensitiveOperationService(
            new AuthorizedOperation(new PermissionService(users, audit), users, audit), audit,
            authenticationEnabled: false, production: true)
        {
            ConfirmProductionAsync = () => response.Task,
        };
        using var page = new TaskDiagnosticsViewModel(tasks,
            new DiagnosticService(new LogAccessService(authenticationEnabled: false)), operations)
        {
            IncludeLogs = false,
        };
        await page.PreviewCommand.ExecuteAsync(null);
        Assert.True(page.HasPreview);
        page.Confirmed = true;
        var path = Path.Combine(_directory, "cancelled.zip");
        var exporting = page.ExportAsync(path);
        Assert.False(exporting.IsCompleted);
        page.Dispose();
        response.SetResult(true);
        await exporting;
        Assert.Empty(tasks.Snapshot());
        Assert.False(File.Exists(path));
        Assert.False(page.HasPreview);
        await tasks.StopAsync();
    }

    [AvaloniaFact]
    public async Task Closing_task_page_keeps_global_work_running()
    {
        var tasks = new TaskService(NullLogger<TaskService>.Instance);
        var started = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var release = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var id = tasks.Enqueue("Synthetic work", "Test", async (_, token) =>
        {
            started.SetResult();
            await release.Task.WaitAsync(token);
        });
        try
        {
            await started.Task.WaitAsync(TimeSpan.FromSeconds(5));
            using var page = CreateDiagnostics(tasks);
            page.SelectTask(id);
            page.Dispose();
            Assert.False(Assert.Single(tasks.Snapshot()).CancellationRequested);
            release.SetResult();
            await tasks.WaitAsync(id).WaitAsync(TimeSpan.FromSeconds(5));
            Assert.Equal(WorkStatus.Succeeded, Assert.Single(tasks.Snapshot()).Status);
            Assert.Empty(page.Items);
        }
        finally
        {
            release.TrySetResult();
            await tasks.StopAsync();
        }
    }

    [AvaloniaFact]
    public void Navigation_factory_does_not_give_root_container_ownership_of_page()
    {
        var services = new ServiceCollection();
        services.AddLogging();
        services.AddPlinthModules();
        var provider = services.BuildServiceProvider();
        using var page = Assert.IsType<LicenseViewModel>(provider
            .GetServices<NavigationItem>().Single(item => item.Id == ShellNavigation.LicensesId)
            .CreateViewModel());
        provider.Dispose();
        // CreateInstance 的页面不在根容器的释放列表；只由 Shell 关闭时释放。
        Assert.True(page.LoadCommand.CanExecute(null));
        page.Dispose();
        Assert.False(page.LoadCommand.CanExecute(null));
    }

    [AvaloniaFact]
    public async Task Settings_navigation_factory_leaves_page_disposal_to_shell()
    {
        var services = new ServiceCollection();
        services.AddLogging();
        services.AddPlinthModules();
        using var provider = services.BuildServiceProvider();
        provider.GetRequiredService<SettingsService>().Current.Remote.Enabled = false;
        using var page = Assert.IsType<SettingsViewModel>(provider
            .GetServices<NavigationItem>().Single(item => item.Id == ShellNavigation.SettingsId)
            .CreateViewModel());
        provider.Dispose();
        // Disabled health checks need no HTTP resources. They distinguish an active
        // page from one already disposed by the root container.
        page.RemoteStatus = "before";
        await page.CheckRemoteCommand.ExecuteAsync(null);
        Assert.Equal(Plinth.App.Localization.Loc.Get("remote.status.disabled"), page.RemoteStatus);
        page.Dispose();
        page.RemoteStatus = "closed";
        await page.CheckRemoteCommand.ExecuteAsync(null);
        Assert.Equal("closed", page.RemoteStatus);
    }

    [AvaloniaFact]
    public async Task Reopening_closed_page_creates_fresh_instance_but_released_shell_cannot_open()
    {
        using var branding = new BrandingService(NullLogger<BrandingService>.Instance);
        var created = 0;
        var item = new NavigationItem("license", "License", () =>
        {
            created++;
            return new LicenseViewModel(new LicenseNoticeService(_directory));
        });
        var shell = CreateShell(branding, item);
        var first = Assert.IsType<LicenseViewModel>(shell.SelectedTab!.Content);
        await shell.CloseAllTabsCommand.ExecuteAsync(null);
        shell.OpenTabCommand.Execute(item);
        var second = Assert.IsType<LicenseViewModel>(shell.SelectedTab!.Content);
        Assert.NotSame(first, second);
        Assert.False(first.LoadCommand.CanExecute(null));
        Assert.True(second.LoadCommand.CanExecute(null));
        shell.ReleaseWorkspace();
        shell.ReleaseWorkspace();
        shell.OpenTabCommand.Execute(new NavigationItem("other", "Other", () =>
        {
            created++;
            return new LicenseViewModel(new LicenseNoticeService(_directory));
        }));
        Assert.Equal(2, created);
    }

    private static MainViewModel CreateShell(BrandingService branding, params NavigationItem[] items) =>
        new(items, new ClassicDesktopStyleApplicationLifetime(),
            new FolderService(NullLogger<FolderService>.Instance), branding);

    private static LogViewModel CreateLogs(LogAccessService? access = null)
    {
        access ??= new LogAccessService(authenticationEnabled: false);
        return new(new FolderService(NullLogger<FolderService>.Instance, access), access);
    }

    private static TaskDiagnosticsViewModel CreateDiagnostics(TaskService tasks, LogAccessService? access = null) =>
        new(tasks, new DiagnosticService(access ?? new LogAccessService(authenticationEnabled: false)))
        { IncludeLogs = false };

    private sealed class ReadGate : IPermissionService, IDisposable
    {
        private readonly string _permission;
        private readonly TaskCompletionSource _release = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public TaskCompletionSource Entered { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public LogAccessService Access { get; }

        public ReadGate(string permission)
        {
            _permission = permission;
            Access = new LogAccessService(this, new TestUser(), authenticationEnabled: true);
        }

        public bool HasPermission(string permission) => true;
        public void Demand(string permission, string correlationId)
        {
            if (permission != _permission) return;
            Entered.TrySetResult();
            _release.Task.WaitAsync(TimeSpan.FromSeconds(10)).GetAwaiter().GetResult();
        }

        public void Release() => _release.TrySetResult();
        // 测试断言失败时也放行工作线程。
        public void Dispose() => Release();
    }

    private sealed class TestUser : IRemoteUserContext
    {
        public RemoteUserContext CurrentUser { get; } =
            new("synthetic", ImmutableHashSet<string>.Empty, ImmutableHashSet<string>.Empty);
    }
}
