using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Reflection;
using System.Threading;
using System.Threading.Tasks;
using Avalonia.Controls;
using Avalonia.Headless.XUnit;
using Avalonia.Threading;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.EntityFrameworkCore;
using Plinth.App.Auth;
using Plinth.App.Data;
using Plinth.App.Localization;
using Plinth.App.Modules.Demo;
using Plinth.App.Modules.Notes;
using Plinth.App.Modules.RemoteDemo;
using Plinth.App.Modules.RemoteDemo.Services;
using Plinth.App.Modules.RemoteDemo.ViewModels;
using Plinth.App.Navigation;
using Plinth.App.Services;
using Plinth.App.ViewModels;
using Serilog;
using Serilog.Core;
using Serilog.Events;
using Xunit;

namespace Plinth.App.Tests;

public sealed class OptionalRemoteDemoTests
{
    private static readonly Uri Address = new("https://example.test/api/");

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void Registration_failure_discards_services_navigation_commands_and_help(bool failAfterHelp)
    {
        IServiceCollection services = new ServiceCollection();
        services.AddDemoModule();
        services.AddNotesModule();
        var original = services.ToArray();
        var state = CreateState();
        ServiceCollectionExtensions.RegisterOptionalRemoteDemo(services, state, staged =>
        {
            staged.AddRemoteDemoModule(Address);
            if (failAfterHelp) staged.AddSingleton(new HelpTopic("remoteDemo.test", "Help", "Body", RemoteDemoModuleRegistration.PageId));
            throw new InvalidOperationException("secret registration data");
        });
        Assert.True(state.Failed);
        Assert.Equal(original, services.ToArray());
        using var provider = services.BuildServiceProvider();
        var pages = provider.GetServices<NavigationItem>().ToArray();
        Assert.Contains(pages, page => page.Id == DemoModuleRegistration.PageId);
        Assert.Contains(pages, page => page.Id == NotesModuleRegistration.PageId);
        Assert.DoesNotContain(pages, page => page.Id == RemoteDemoModuleRegistration.PageId);
        Assert.DoesNotContain(provider.GetServices<CommandMenuContribution>(),
            command => command.PageId == RemoteDemoModuleRegistration.PageId);
        Assert.DoesNotContain(provider.GetServices<HelpTopic>(),
            topic => topic.PageId == RemoteDemoModuleRegistration.PageId);
        Assert.Null(provider.GetService<RemoteDemoService>());
    }

    [Fact]
    public void Successful_registration_keeps_lazy_descriptors_without_constructing_services()
    {
        IServiceCollection services = new ServiceCollection();
        var state = CreateState();
        var constructions = 0;
        ServiceCollectionExtensions.RegisterOptionalRemoteDemo(services, state, staged =>
        {
            staged.AddRemoteDemoModule(Address);
            staged.AddSingleton(new HelpTopic("remoteDemo.test", "Help", "Body", RemoteDemoModuleRegistration.PageId));
            staged.AddSingleton<Probe>(_ => { constructions++; return new Probe(); });
        });
        using var provider = services.BuildServiceProvider();
        Assert.False(state.Failed);
        Assert.Equal(0, constructions);
        Assert.Single(provider.GetServices<NavigationItem>());
        Assert.Equal(4, provider.GetServices<CommandMenuContribution>().Count());
        Assert.Single(provider.GetServices<HelpTopic>());
        Assert.Same(provider.GetRequiredService<Probe>(), provider.GetRequiredService<Probe>());
        Assert.Equal(1, constructions);
    }

    [Fact]
    public void Diagnostic_is_allowlisted_and_correlation_is_host_generated_and_searchable()
    {
        var sink = new Capture();
        using var logger = new LoggerConfiguration().WriteTo.Sink(sink).CreateLogger();
        var previous = Log.Logger;
        try
        {
            Log.Logger = logger;
            var state = CreateState();
            ServiceCollectionExtensions.RegisterOptionalRemoteDemo(new ServiceCollection(), state, _ =>
                throw new InvalidOperationException("token=TOP_SECRET /private/user https://secret.test",
                    new Exception("password=INNER_SECRET")));
            var entry = Assert.Single(sink.Events);
            Assert.Null(entry.Exception);
            Assert.Equal(new[] { "CorrelationId", "ErrorCode", "Module", "Operation" },
                entry.Properties.Keys.OrderBy(key => key).ToArray());
            Assert.Equal(state.ModuleId, ((ScalarValue)entry.Properties["Module"]).Value);
            Assert.Equal("Registration", ((ScalarValue)entry.Properties["Operation"]).Value);
            Assert.True(Guid.TryParseExact(state.CorrelationId, "D", out _));
            Assert.Equal(state.CorrelationId, ((ScalarValue)entry.Properties["CorrelationId"]).Value);
            Assert.DoesNotContain("SECRET", entry.RenderMessage());
            Assert.DoesNotContain("private", entry.RenderMessage());
            using var output = new StringWriter();
            new Serilog.Formatting.Compact.CompactJsonFormatter().Format(entry, output);
            var parsed = Assert.IsType<LogEntry>(LogReader.Parse(output.ToString()));
            Assert.Equal(state.CorrelationId, parsed.CorrelationId);
            Assert.Equal(state.ModuleId, parsed.Module);
            Assert.Equal(LogEventKind.Technical, parsed.EventKind);
        }
        finally { Log.Logger = previous; }
    }

    [AvaloniaFact]
    public async Task Lazy_module_construction_failure_disables_only_remote_and_has_persistent_view_entry()
    {
        using var shell = new Shell(services =>
        {
            services.AddSingleton<IAccessTokenProvider>(new TestTokens());
            ServiceCollectionExtensions.RegisterOptionalRemoteDemo(services, State(services), staged =>
            {
                staged.AddRemoteDemoModule(Address);
                // Runs on navigation, not during registration/provider creation.
                staged.AddTransient<RemoteDemoService>(_ => throw new InvalidOperationException("TOP_SECRET"));
            });
        });
        var remote = shell.ViewModel.NavigationItems.Single(page => page.Id == RemoteDemoModuleRegistration.PageId);
        Assert.False(shell.ViewModel.HasModuleFailure);
        var previous = shell.ViewModel.SelectedTab;
        shell.ViewModel.OpenTabCommand.Execute(remote);
        Assert.Same(previous, shell.ViewModel.SelectedTab);
        Assert.DoesNotContain(shell.ViewModel.Tabs, tab => tab.Id == remote.Id);
        Assert.DoesNotContain(shell.ViewModel.MenuItems, page => page.Id == remote.Id);
        Assert.DoesNotContain(shell.ViewModel.NavigationItems, page => page.Id == remote.Id);
        Assert.DoesNotContain(shell.ViewModel.RecentPages, page => page.Id == remote.Id);
        shell.ViewModel.OpenTabCommand.Execute(remote); // A stale shortcut cannot retry.
        shell.ViewModel.StatusMessage = "Ordinary status";
        shell.ViewModel.HasTaskNotification = true;
        shell.ViewModel.DismissTaskNotificationCommand.Execute(null);
        shell.Layout();
        Assert.True(shell.Window.FindControl<Border>("ModuleFailureBanner")!.IsVisible);
        var text = shell.Window.FindControl<TextBlock>("ModuleFailureText")!.Text!;
        Assert.Contains("RemoteDemo", text);
        Assert.DoesNotContain("TOP_SECRET", text);
        var button = shell.Window.FindControl<Button>("ModuleFailureLogsButton")!;
        Assert.True(button.IsEffectivelyVisible);
        Assert.Same(shell.ViewModel.ShowModuleFailureLogsCommand, button.Command);
        shell.ClickCenter(button);
        Assert.NotNull(shell.ViewModel.ShowModuleFailureLogsCommand.ExecutionTask);
        await shell.ViewModel.ShowModuleFailureLogsCommand.ExecutionTask!;
        var logs = Assert.IsType<LogViewModel>(shell.ViewModel.SelectedTab!.Content);
        Assert.Equal(shell.Provider.GetRequiredService<OptionalModuleState>().CorrelationId, logs.CorrelationId);
        shell.ViewModel.ShowAboutCommand.Execute(null);
        Assert.Equal(ShellNavigation.AboutId, shell.ViewModel.SelectedTab!.Id);
        Assert.True(shell.ViewModel.HasModuleFailure);
    }

    [AvaloniaFact]
    public void Successful_remote_page_uses_final_provider_and_other_pages_remain_available()
    {
        var tokenConstructions = 0;
        using var shell = new Shell(services =>
        {
            services.AddSingleton<IAccessTokenProvider>(_ =>
            {
                tokenConstructions++;
                return new TestTokens();
            });
            ServiceCollectionExtensions.RegisterOptionalRemoteDemo(services, State(services),
                staged => staged.AddRemoteDemoModule(Address));
        });
        Assert.Equal(0, tokenConstructions);
        var remote = shell.ViewModel.NavigationItems.Single(page => page.Id == RemoteDemoModuleRegistration.PageId);
        shell.ViewModel.OpenTabCommand.Execute(remote);
        Assert.IsType<RemoteDemoListViewModel>(shell.ViewModel.SelectedTab!.Content);
        Assert.Equal(1, tokenConstructions);
        shell.ViewModel.ShowAboutCommand.Execute(null);
        shell.ViewModel.OpenTabCommand.Execute(remote);
        Assert.Single(shell.ViewModel.Tabs, tab => tab.Id == remote.Id);
        Assert.Equal(1, tokenConstructions);
        Assert.False(shell.ViewModel.HasModuleFailure);
    }

    [AvaloniaFact]
    public void Registration_failure_is_visible_when_shell_first_opens()
    {
        using var shell = new Shell(services =>
            ServiceCollectionExtensions.RegisterOptionalRemoteDemo(services, State(services),
                staged => { staged.AddRemoteDemoModule(Address); throw new Exception("secret"); }));
        Assert.True(shell.ViewModel.HasModuleFailure);
        Assert.True(shell.Window.FindControl<Border>("ModuleFailureBanner")!.IsEffectivelyVisible);
        shell.ViewModel.ShowAboutCommand.Execute(null);
        Assert.Equal(ShellNavigation.AboutId, shell.ViewModel.SelectedTab!.Id);
    }

    [AvaloniaTheory]
    [InlineData(false)]
    [InlineData(true)]
    public void First_restored_page_failure_is_safe_before_navigation_initialization(bool restoreHealthyPage)
    {
        var path = Path.Combine(Path.GetTempPath(), "plinth-optional-restore-" + Guid.NewGuid().ToString("N") + ".json");
        var preferences = new WorkspacePreferencesService(path);
        try
        {
            var pageId = RemoteDemoModuleRegistration.PageId;
            Assert.True(preferences.SaveLayout(new WorkspaceLayoutV1
            {
                OpenTabIds = restoreHealthyPage ? [pageId, ShellNavigation.OverviewId] : [pageId],
                SelectedTabId = pageId,
            }));
            Assert.True(preferences.SaveNavigation(new PageNavigationPreferences
            {
                FavoriteIds = [pageId, ShellNavigation.OverviewId],
                RecentIds = [pageId, ShellNavigation.OverviewId],
            }));
            var constructions = 0;
            using var shell = new Shell(services =>
            {
                services.AddSingleton(preferences);
                services.AddSingleton<IAccessTokenProvider>(new TestTokens());
                ServiceCollectionExtensions.RegisterOptionalRemoteDemo(services, State(services), staged =>
                {
                    staged.AddRemoteDemoModule(Address);
                    staged.AddTransient<RemoteDemoService>(_ =>
                    {
                        constructions++;
                        throw new InvalidOperationException("secret restore failure");
                    });
                });
            });
            // Failure occurs inside MainViewModel's constructor, before
            // InitializePageNavigation has populated favorites/recent pages.
            Assert.Equal(1, constructions);
            Assert.True(shell.ViewModel.HasModuleFailure);
            Assert.DoesNotContain(shell.ViewModel.NavigationItems, item => item.Id == pageId);
            Assert.DoesNotContain(shell.ViewModel.MenuItems, item => item.Id == pageId);
            Assert.DoesNotContain(shell.ViewModel.Tabs, tab => tab.Id == pageId);
            Assert.Equal(ShellNavigation.OverviewId, Assert.Single(shell.ViewModel.FavoritePages).Id);
            Assert.Equal(ShellNavigation.OverviewId, Assert.Single(shell.ViewModel.RecentPages).Id);
            if (restoreHealthyPage)
            {
                Assert.Equal(ShellNavigation.OverviewId, Assert.Single(shell.ViewModel.Tabs).Id);
                Assert.Equal(ShellNavigation.OverviewId, shell.ViewModel.SelectedTab?.Id);
                Assert.True(shell.ViewModel.ToggleFavoriteCommand.CanExecute(null));
            }
            else
            {
                Assert.Empty(shell.ViewModel.Tabs);
                Assert.Null(shell.ViewModel.SelectedTab);
                Assert.False(shell.ViewModel.ToggleFavoriteCommand.CanExecute(null));
            }
            Assert.True(shell.ViewModel.ClearRecentPagesCommand.CanExecute(null));
            var saved = new WorkspacePreferencesService(path).Navigation;
            Assert.DoesNotContain(pageId, saved.FavoriteIds);
            Assert.DoesNotContain(pageId, saved.RecentIds);
            Assert.True(shell.Window.FindControl<Border>("ModuleFailureBanner")!.IsEffectivelyVisible);
            shell.ViewModel.ShowAboutCommand.Execute(null);
            Assert.Equal(ShellNavigation.AboutId, shell.ViewModel.SelectedTab?.Id);
            Assert.True(shell.ViewModel.HasModuleFailure);
        }
        finally { File.Delete(path); }
    }

    [AvaloniaFact]
    public void Shell_uses_host_metadata_for_an_unrelated_optional_page()
    {
        var state = new OptionalModuleState("optionalExample", "Optional example", "optionalExample.page");
        var attempts = 0;
        using var shell = new Shell(services =>
        {
            services.AddSingleton(state);
            services.AddSingleton(new NavigationItem(state.PageId, state.ModuleName,
                () => state.CreatePage(() => { }, () =>
                {
                    attempts++;
                    throw new InvalidOperationException("secret");
                })));
        });
        var page = shell.ViewModel.NavigationItems.Single(item => item.Id == state.PageId);
        shell.ViewModel.OpenTabCommand.Execute(page);
        shell.ViewModel.OpenTabCommand.Execute(page);
        shell.Layout();
        Assert.Equal(1, attempts);
        Assert.True(shell.ViewModel.HasModuleFailure);
        Assert.Contains(state.ModuleName, shell.ViewModel.ModuleFailureText);
        Assert.DoesNotContain("RemoteDemo", shell.ViewModel.ModuleFailureText);
        Assert.DoesNotContain(shell.ViewModel.NavigationItems, item => item.Id == state.PageId);
        Assert.DoesNotContain(shell.ViewModel.MenuItems, item => item.Id == state.PageId);
        Assert.DoesNotContain(shell.ViewModel.Tabs, tab => tab.Id == state.PageId);
        Assert.True(shell.Window.FindControl<Border>("ModuleFailureBanner")!.IsEffectivelyVisible);
        shell.ViewModel.ShowAboutCommand.Execute(null);
        Assert.Equal(ShellNavigation.AboutId, shell.ViewModel.SelectedTab?.Id);
    }

    [AvaloniaTheory]
    [InlineData(false)]
    [InlineData(true)]
    public void Failure_after_startup_persists_shortcut_removal_or_keeps_page_disabled_when_save_fails(bool saveFails)
    {
        var path = Path.Combine(Path.GetTempPath(), "plinth-optional-navigation-" + Guid.NewGuid().ToString("N") + ".json");
        var preferences = new WorkspacePreferencesService(path);
        var state = new OptionalModuleState("optionalExample", "Optional example", "optionalExample.page");
        var savedIds = new[] { state.PageId, ShellNavigation.OverviewId };
        try
        {
            Assert.True(preferences.SaveNavigation(new PageNavigationPreferences
            {
                FavoriteIds = savedIds,
                RecentIds = savedIds,
            }));
            using var shell = new Shell(services =>
            {
                services.AddSingleton(preferences);
                services.AddSingleton(state);
                services.AddSingleton(new NavigationItem(state.PageId, state.ModuleName,
                    () => state.CreatePage(() => { }, () => throw new InvalidOperationException("module failure"))));
            });
            Assert.Equal(savedIds, shell.ViewModel.FavoritePages.Select(item => item.Id));
            Assert.Equal(savedIds, shell.ViewModel.RecentPages.Select(item => item.Id));
            var page = shell.ViewModel.NavigationItems.Single(item => item.Id == state.PageId);
            if (saveFails) Directory.CreateDirectory(path + ".tmp");

            shell.ViewModel.OpenTabCommand.Execute(page);

            Assert.True(shell.ViewModel.HasModuleFailure);
            Assert.DoesNotContain(shell.ViewModel.NavigationItems, item => item.Id == state.PageId);
            Assert.Equal(ShellNavigation.OverviewId, Assert.Single(shell.ViewModel.FavoritePages).Id);
            Assert.Equal(ShellNavigation.OverviewId, Assert.Single(shell.ViewModel.RecentPages).Id);
            var saved = new WorkspacePreferencesService(path).Navigation;
            if (saveFails)
            {
                Assert.Equal(savedIds, saved.FavoriteIds);
                Assert.Equal(savedIds, saved.RecentIds);
                Assert.Equal(Plinth.App.Localization.Loc.Get("shell.navigation.saveFailed"), shell.ViewModel.StatusMessage);
            }
            else
            {
                Assert.Equal(new[] { ShellNavigation.OverviewId }, saved.FavoriteIds);
                Assert.Equal(new[] { ShellNavigation.OverviewId }, saved.RecentIds);
            }
            // A failed preference save must not re-enable the stale shortcut.
            shell.ViewModel.OpenTabCommand.Execute(page);
            Assert.DoesNotContain(shell.ViewModel.Tabs, tab => tab.Id == state.PageId);
            Assert.True(shell.ViewModel.HasModuleFailure);
        }
        finally
        {
            File.Delete(path);
            if (Directory.Exists(path + ".tmp")) Directory.Delete(path + ".tmp");
        }
    }

    [AvaloniaFact]
    public void Shared_lazy_dependency_failure_propagates_unchanged()
    {
        var failure = new InvalidDataException("Core identity configuration failed");
        using var shell = new Shell(services =>
        {
            ServiceCollectionExtensions.RegisterOptionalRemoteDemo(services, State(services),
                staged => staged.AddRemoteDemoModule(Address));
            services.AddSingleton<IAccessTokenProvider>(_ => throw failure);
        });
        var remote = shell.ViewModel.NavigationItems.Single(page => page.Id == RemoteDemoModuleRegistration.PageId);
        Assert.Same(failure, Assert.Throws<InvalidDataException>(() => shell.ViewModel.OpenTabCommand.Execute(remote)));
        Assert.False(shell.ViewModel.HasModuleFailure);
    }

    [AvaloniaFact]
    public void Other_page_failures_are_not_swallowed_and_configuration_still_validates()
    {
        using var shell = new Shell();
        var failure = new InvalidDataException("Core page");
        Assert.Same(failure, Assert.Throws<InvalidDataException>(() =>
            shell.ViewModel.OpenTabCommand.Execute(new NavigationItem("core.test", "Core", () => throw failure))));
        Assert.False(shell.ViewModel.HasModuleFailure);
        Assert.Throws<InvalidDataException>(() =>
            new DeploymentConfiguration { DemoApiBaseUrl = "http://unsafe.test/" }.Validate());
    }

    [Fact]
    public void Database_failure_still_propagates_after_optional_registration_failure()
    {
        IServiceCollection services = new ServiceCollection();
        services.AddLogging();
        services.AddPlinthModules();
        var state = State(services);
        ServiceCollectionExtensions.RegisterOptionalRemoteDemo(services, state, _ => throw new Exception());
        var failure = new InvalidDataException("Database initialization failed");
        services.AddSingleton<IDbContextFactory<AppDbContext>>(_ => throw failure);
        using var provider = services.BuildServiceProvider();
        Assert.Same(failure, Assert.Throws<InvalidDataException>(() => provider.EnsureDatabase()));
        Assert.True(state.Failed);
    }

    [AvaloniaTheory]
    [InlineData("missing")]
    [InlineData("ordinary")]
    [InlineData("xaml")]
    [InlineData("wrong")]
    public void Optional_view_reflection_failure_has_only_safe_localized_fallback_and_one_diagnostic(string kind)
    {
        var state = ViewState();
        var page = state.CreatePage(() => { }, () => ViewPage(kind));
        Assert.Same(state, page.OptionalModule);
        var notifications = 0;
        state.FailedChanged += () => notifications++;
        var sink = new Capture();
        using var logger = new LoggerConfiguration().WriteTo.Sink(sink).CreateLogger();
        var previous = Log.Logger;
        try
        {
            Log.Logger = logger;
            var locator = new ViewLocator();
            var fallback = Assert.IsType<TextBlock>(locator.Build(page));
            Assert.Equal(Loc.Format("shell.module.failed", state.ModuleName, state.CorrelationId), fallback.Text);
            Assert.Null(fallback.DataContext);
            Assert.Null(fallback.Tag);
            Assert.Equal("ViewConstruction", state.Operation);
            Assert.True(Guid.TryParseExact(state.CorrelationId, "D", out _));
            var correlation = state.CorrelationId;
            Assert.IsType<TextBlock>(locator.Build(page));
            Assert.Equal(correlation, state.CorrelationId);
            Assert.Equal(1, notifications);
            var entry = Assert.Single(sink.Events);
            Assert.Null(entry.Exception);
            Assert.Equal(new[] { "CorrelationId", "ErrorCode", "Module", "Operation" },
                entry.Properties.Keys.OrderBy(key => key).ToArray());
            Assert.Equal("ViewConstruction", ((ScalarValue)entry.Properties["Operation"]).Value);
            Assert.Equal(correlation, ((ScalarValue)entry.Properties["CorrelationId"]).Value);
            using var json = new StringWriter();
            new Serilog.Formatting.Compact.CompactJsonFormatter().Format(entry, json);
            Assert.DoesNotContain("TOP_SECRET", json.ToString());
            Assert.DoesNotContain("/private", json.ToString());
            Assert.DoesNotContain(page.GetType().FullName!, json.ToString());
        }
        finally { Log.Logger = previous; }
    }

    [AvaloniaFact]
    public void Optional_view_fallback_uses_both_existing_translations()
    {
        var state = ViewState();
        var page = state.CreatePage(() => { }, () => new MissingViewModel());
        try
        {
            Loc.Initialize("en-US");
            var english = Assert.IsType<TextBlock>(new ViewLocator().Build(page)).Text;
            Assert.Equal(Loc.Format("shell.module.failed", state.ModuleName, state.CorrelationId), english);
            Loc.Initialize("zh-CN");
            var chinese = Assert.IsType<TextBlock>(new ViewLocator().Build(page)).Text;
            Assert.Equal(Loc.Format("shell.module.failed", state.ModuleName, state.CorrelationId), chinese);
            Assert.NotEqual(english, chinese);
        }
        finally { Loc.Initialize(Loc.DefaultLanguage); }
    }

    [AvaloniaTheory]
    [InlineData("memory")]
    [InlineData("cancel")]
    [InlineData("nestedMemory")]
    [InlineData("nestedCancel")]
    public void Fatal_and_canceled_view_constructors_propagate_through_reflection_without_disabling(string kind)
    {
        var state = ViewState();
        var page = state.CreatePage(() => { }, () => ViewPage(kind));
        var failure = Assert.Throws<TargetInvocationException>(() => new ViewLocator().Build(page));
        Exception cause = failure;
        while (cause is TargetInvocationException { InnerException: { } inner }) cause = inner;
        if (kind.Contains("emory", StringComparison.Ordinal)) Assert.IsType<OutOfMemoryException>(cause);
        else Assert.IsAssignableFrom<OperationCanceledException>(cause);
        Assert.False(state.Failed);
        Assert.Null(state.Operation);
        Assert.Null(state.CorrelationId);
    }

    [AvaloniaFact]
    public void View_lookup_supports_vm_assembly_and_unmarked_core_behavior_is_unchanged()
    {
        var locator = new ViewLocator();
        var core = new HealthyViewModel();
        Assert.Null(core.OptionalModule);
        Assert.IsType<HealthyView>(locator.Build(core));
        var state = ViewState();
        Assert.IsType<HealthyView>(locator.Build(state.CreatePage(() => { }, () => new HealthyViewModel())));
        Assert.False(state.Failed);
        Assert.Null(locator.Build(null));
        Assert.False(locator.Match(new object()));
        Assert.Contains(nameof(MissingViewModel),
            Assert.IsType<TextBlock>(locator.Build(new MissingViewModel())).Text!);
        Assert.Throws<InvalidCastException>(() => locator.Build(new WrongViewModel()));
        var ordinary = Assert.Throws<TargetInvocationException>(() => locator.Build(new ThrowingViewModel()));
        Assert.IsType<InvalidOperationException>(ordinary.InnerException);
        var xaml = Assert.Throws<TargetInvocationException>(() => locator.Build(new XamlFailureViewModel()));
        Assert.IsType<System.Xml.XmlException>(xaml.InnerException);
        Assert.False(state.Failed);
    }

    [AvaloniaTheory]
    [InlineData("missing", false)]
    [InlineData("ordinary", false)]
    [InlineData("xaml", false)]
    [InlineData("wrong", false)]
    [InlineData("ordinary", true)]
    public async Task View_failure_is_cleaned_after_dispatch_releases_once_and_preserves_healthy_shell(
        string kind, bool selectHealthyBeforeCleanup)
    {
        var path = Path.Combine(Path.GetTempPath(), "plinth-view-failure-" + Guid.NewGuid().ToString("N") + ".json");
        var preferences = new WorkspacePreferencesService(path);
        var state = ViewState();
        var page = ViewPage(kind);
        try
        {
            Assert.True(preferences.SaveNavigation(new PageNavigationPreferences
            {
                FavoriteIds = [state.PageId, ShellNavigation.OverviewId],
                RecentIds = [state.PageId, ShellNavigation.OverviewId],
            }));
            using var shell = new Shell(services =>
            {
                services.AddSingleton(preferences);
                services.AddSingleton(state);
                services.AddSingleton(new NavigationItem(state.PageId, state.ModuleName,
                    () => state.CreatePage(() => { }, () => page)));
            });
            var healthy = shell.ViewModel.SelectedTab!;
            var navigation = shell.ViewModel.NavigationItems.Single(item => item.Id == state.PageId);
            shell.ViewModel.OpenTabCommand.Execute(navigation);
            var failedTab = shell.ViewModel.Tabs.Single(tab => tab.Id == state.PageId);
            // Build on the same stack as rendering; disposal/collection mutation must be deferred.
            Assert.IsType<TextBlock>(new ViewLocator().Build(page));
            Assert.Contains(failedTab, shell.ViewModel.Tabs);
            Assert.Contains(navigation, shell.ViewModel.NavigationItems);
            Assert.Equal(0, page.Disposals);
            Assert.Equal(0, page.LeaveCalls);
            if (selectHealthyBeforeCleanup) shell.ViewModel.SelectedTab = healthy;

            shell.Layout();

            Assert.DoesNotContain(failedTab, shell.ViewModel.Tabs);
            Assert.DoesNotContain(navigation, shell.ViewModel.NavigationItems);
            Assert.DoesNotContain(navigation, shell.ViewModel.MenuItems);
            Assert.DoesNotContain(navigation, shell.ViewModel.FavoritePages);
            Assert.DoesNotContain(navigation, shell.ViewModel.RecentPages);
            Assert.Same(healthy, shell.ViewModel.SelectedTab);
            Assert.Equal(1, page.Disposals);
            Assert.Equal(0, page.LeaveCalls);
            var saved = new WorkspacePreferencesService(path).Navigation;
            Assert.DoesNotContain(state.PageId, saved.FavoriteIds);
            Assert.DoesNotContain(state.PageId, saved.RecentIds);
            Assert.True(shell.Window.FindControl<Border>("ModuleFailureBanner")!.IsEffectivelyVisible);
            shell.ViewModel.StatusMessage = "Independent status";
            shell.ViewModel.HasTaskNotification = true;
            shell.ViewModel.DismissTaskNotificationCommand.Execute(null);
            await shell.ViewModel.ShowModuleFailureLogsCommand.ExecuteAsync(null);
            Assert.Equal(state.CorrelationId, Assert.IsType<LogViewModel>(shell.ViewModel.SelectedTab!.Content).CorrelationId);
            shell.ViewModel.OpenTabCommand.Execute(navigation); // stale shortcut cannot recreate the page
            Assert.DoesNotContain(shell.ViewModel.Tabs, tab => tab.Id == state.PageId);
            Assert.IsType<TextBlock>(new ViewLocator().Build(page));
            shell.Layout();
            shell.ViewModel.ReleaseWorkspace();
            shell.ViewModel.ReleaseWorkspace();
            Assert.Equal(1, page.Disposals);
        }
        finally { File.Delete(path); }
    }

    [AvaloniaTheory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task Closed_shell_ignores_pending_and_future_view_failure_notifications(bool failAfterClose)
    {
        var state = ViewState();
        var page = new HealthyViewModel();
        using var shell = new Shell(services =>
        {
            services.AddSingleton(state);
            services.AddSingleton(new NavigationItem(state.PageId, state.ModuleName,
                () => state.CreatePage(() => { }, () => page)));
        });
        shell.ViewModel.OpenTabCommand.Execute(shell.ViewModel.NavigationItems.Single(item => item.Id == state.PageId));
        shell.Layout();
        var failingPage = state.CreatePage(() => { }, () => new ThrowingViewModel());
        if (!failAfterClose) Assert.IsType<TextBlock>(new ViewLocator().Build(failingPage));
        Assert.True(await shell.ViewModel.PrepareExitAsync());
        shell.Window.Close();
        Assert.True(shell.Window.CloseApproved);
        Assert.Equal(1, page.Disposals);
        Assert.Equal(failAfterClose ? 1 : 0, page.LeaveCalls);
        var changes = 0;
        shell.ViewModel.PropertyChanged += (_, _) => changes++;
        var tabCount = shell.ViewModel.Tabs.Count;
        if (failAfterClose) Assert.IsType<TextBlock>(new ViewLocator().Build(failingPage));
        Dispatcher.UIThread.RunJobs();
        shell.ViewModel.ReleaseWorkspace();
        Assert.Equal(0, changes);
        Assert.Equal(tabCount, shell.ViewModel.Tabs.Count);
        Assert.Contains(shell.ViewModel.NavigationItems, item => item.Id == state.PageId);
        Assert.Equal(1, page.Disposals);
    }

    [AvaloniaTheory]
    [InlineData("missing")]
    [InlineData("ordinary")]
    [InlineData("xaml")]
    [InlineData("wrong")]
    public void Restored_optional_view_failure_is_handled_by_real_shell_rendering(string kind)
    {
        var path = Path.Combine(Path.GetTempPath(), "plinth-view-restore-" + Guid.NewGuid().ToString("N") + ".json");
        var preferences = new WorkspacePreferencesService(path);
        var state = ViewState();
        var page = ViewPage(kind);
        try
        {
            Assert.True(preferences.SaveLayout(new WorkspaceLayoutV1
            {
                OpenTabIds = [state.PageId, ShellNavigation.OverviewId],
                SelectedTabId = state.PageId,
            }));
            using var shell = new Shell(services =>
            {
                services.AddSingleton(preferences);
                services.AddSingleton(state);
                services.AddSingleton(new NavigationItem(state.PageId, state.ModuleName,
                    () => state.CreatePage(() => { }, () => page)));
            });
            // No direct Build call: Window.Show/Layout must invoke the application template.
            Assert.Equal("ViewConstruction", state.Operation);
            Assert.Equal(ShellNavigation.OverviewId, Assert.Single(shell.ViewModel.Tabs).Id);
            Assert.Equal(ShellNavigation.OverviewId, shell.ViewModel.SelectedTab?.Id);
            Assert.Equal(1, page.Disposals);
            Assert.Equal(0, page.LeaveCalls);
            Assert.DoesNotContain(shell.ViewModel.MenuItems, item => item.Id == state.PageId);
            Assert.True(shell.Window.FindControl<Border>("ModuleFailureBanner")!.IsEffectivelyVisible);
        }
        finally { File.Delete(path); }
    }

    private static OptionalModuleState ViewState() => new("viewProbe", "View probe", "viewProbe.page");

    private static ViewConstructionPage ViewPage(string kind) => kind switch
    {
        "missing" => new MissingViewModel(),
        "ordinary" => new ThrowingViewModel(),
        "xaml" => new XamlFailureViewModel(),
        "wrong" => new WrongViewModel(),
        "memory" => new MemoryFailureViewModel(),
        "cancel" => new CancellationFailureViewModel(),
        "nestedMemory" => new NestedMemoryFailureViewModel(),
        "nestedCancel" => new NestedCancellationFailureViewModel(),
        _ => throw new ArgumentOutOfRangeException(nameof(kind)),
    };

    // Public, genuinely matching names: ViewLocator must find these in the test VM's
    // assembly and invoke the actual constructor (not a mocked view factory).
    public abstract class ViewConstructionPage : ViewModelBase, IDisposable, IUnsavedChanges
    {
        public int Disposals { get; private set; }
        public int LeaveCalls { get; private set; }
        public bool IsDirty => true;
        public Task<bool> TryLeaveAsync() { LeaveCalls++; return Task.FromResult(true); }
        public void Dispose() => Disposals++;
    }
    public sealed class MissingViewModel : ViewConstructionPage { }
    public sealed class HealthyViewModel : ViewConstructionPage { }
    public sealed class HealthyView : Control { }
    public sealed class WrongViewModel : ViewConstructionPage { }
    public sealed class WrongView { }
    public sealed class ThrowingViewModel : ViewConstructionPage { }
    public sealed class ThrowingView : Control
    {
        public ThrowingView() => throw new InvalidOperationException("TOP_SECRET /private/user https://secret.test",
            new Exception("INNER_SECRET"));
    }
    public sealed class XamlFailureViewModel : ViewConstructionPage { }
    public sealed class XamlFailureView : Control
    {
        // Controlled representative of a failure while parsing/loading XAML in a view constructor.
        public XamlFailureView() => throw new System.Xml.XmlException("TOP_SECRET /private/view.axaml");
    }
    public sealed class MemoryFailureViewModel : ViewConstructionPage { }
    public sealed class MemoryFailureView : Control
    {
        public MemoryFailureView() => throw new OutOfMemoryException("synthetic; no actual allocation");
    }
    public sealed class CancellationFailureViewModel : ViewConstructionPage { }
    public sealed class CancellationFailureView : Control
    {
        public CancellationFailureView() => throw new TaskCanceledException();
    }
    public sealed class NestedMemoryFailureViewModel : ViewConstructionPage { }
    public sealed class NestedMemoryFailureView : Control
    {
        public NestedMemoryFailureView() => throw new TargetInvocationException(new OutOfMemoryException());
    }
    public sealed class NestedCancellationFailureViewModel : ViewConstructionPage { }
    public sealed class NestedCancellationFailureView : Control
    {
        public NestedCancellationFailureView() => throw new TargetInvocationException(new OperationCanceledException());
    }

    private static OptionalModuleState CreateState() =>
        new("remoteDemo", "RemoteDemo", RemoteDemoModuleRegistration.PageId);

    private static OptionalModuleState State(IServiceCollection services) =>
        (OptionalModuleState)services.Single(d => d.ServiceType == typeof(OptionalModuleState)).ImplementationInstance!;

    private sealed class Probe { }
    private sealed class TestTokens : IAccessTokenProvider
    {
        public Task<string> GetAccessTokenAsync(CancellationToken cancellationToken = default) =>
            throw new InvalidOperationException("Anonymous tests must never request a token.");
    }
    private sealed class Capture : ILogEventSink
    {
        public List<LogEvent> Events { get; } = [];
        public void Emit(LogEvent logEvent) => Events.Add(logEvent);
    }
}
