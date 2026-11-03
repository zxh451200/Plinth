using System;
using System.Collections.Generic;
using System.Linq;
using Plinth.App;
using Plinth.App.Localization;
using Plinth.App.Navigation;
using Plinth.App.Security;
using Plinth.App.Services;
using Plinth.App.ViewModels;
using Xunit;

namespace Plinth.App.Tests;

/// <summary>
/// 导航信息架构:分区由宿主拥有,模块只通过 Section / Order / GroupTitle 参与排序和分组;
/// 设置固定在最底部的 Footer。这里只测纯逻辑,不构造页面,也不开窗口。
/// </summary>
public class NavigationStructureTests
{
    /// <summary>工厂永远不该被调用:排序和分组只看元数据。</summary>
    private static NavigationItem Item(
        string id, string title, NavigationSection section, int order, string? groupTitle = null) =>
        new(id, title, () => throw new NotSupportedException())
        {
            Section = section,
            Order = order,
            GroupTitle = groupTitle,
        };

    /// <summary>按契约给出的九个导航项,故意打乱输入顺序。</summary>
    private static IReadOnlyList<NavigationItem> ContractMenu() => MainViewModel.BuildMenuItems(
        new[]
        {
            Item("settings", "Settings", NavigationSection.Footer, 900),
            Item("backup", "Backup", NavigationSection.System, 610),
            Item("logs", "Logs", NavigationSection.System, 600),
            Item("taskDiagnostics", "Tasks", NavigationSection.Runtime, 400),
            Item("spreadsheet", "Spreadsheet", NavigationSection.Business, 230),
            Item("remote", "Remote", NavigationSection.Business, 220),
            Item("notes", "Notes", NavigationSection.Business, 210),
            Item("demo", "Sample data", NavigationSection.Business, 200),
            Item("overview", "Overview", NavigationSection.Workbench, 100),
        });

    [Fact]
    public void BuildMenuItems_orders_by_order_then_title()
    {
        var ids = ContractMenu().Select(item => item.Id).ToArray();

        Assert.Equal(
            new[]
            {
                "overview", "demo", "notes", "remote", "spreadsheet",
                "taskDiagnostics", "logs", "backup", "settings",
            },
            ids);
    }

    [Fact]
    public void BuildMenuItems_breaks_order_ties_by_title()
    {
        var ids = MainViewModel.BuildMenuItems(new[]
        {
            Item("z", "Zeta", NavigationSection.Workbench, 100),
            Item("later", "Later", NavigationSection.Workbench, 200),
            Item("a", "Alpha", NavigationSection.Workbench, 100),
        }).Select(item => item.Id).ToArray();

        Assert.Equal(new[] { "a", "z", "later" }, ids);
    }

    [Fact]
    public void BuildMenuItems_puts_a_group_header_only_on_the_first_business_and_system_item()
    {
        var menu = ContractMenu().ToDictionary(item => item.Id);
        var business = Loc.Get("nav.group.business");
        var system = Loc.Get("nav.group.system");

        Assert.Equal(business, menu["demo"].DisplayGroupTitle);
        Assert.True(menu["demo"].ShowGroupHeader);
        Assert.Equal(business, menu["notes"].DisplayGroupTitle);
        Assert.False(menu["notes"].ShowGroupHeader);
        Assert.False(menu["remote"].ShowGroupHeader);
        Assert.False(menu["spreadsheet"].ShowGroupHeader);

        Assert.Equal(system, menu["logs"].DisplayGroupTitle);
        Assert.True(menu["logs"].ShowGroupHeader);
        Assert.Equal(system, menu["backup"].DisplayGroupTitle);
        Assert.False(menu["backup"].ShowGroupHeader);
    }

    [Theory]
    [InlineData("overview")]
    [InlineData("taskDiagnostics")]
    [InlineData("settings")]
    public void BuildMenuItems_leaves_workbench_runtime_and_footer_ungrouped(string id)
    {
        var item = ContractMenu().Single(menuItem => menuItem.Id == id);

        Assert.Null(item.DisplayGroupTitle);
        Assert.False(item.ShowGroupHeader);
    }

    [Fact]
    public void BuildMenuItems_keeps_a_module_group_title_and_starts_a_new_header()
    {
        var menu = MainViewModel.BuildMenuItems(new[]
        {
            Item("demo", "Sample data", NavigationSection.Business, 200),
            Item("notes", "Notes", NavigationSection.Business, 210, "Notes group"),
            Item("remote", "Remote", NavigationSection.Business, 220, "Notes group"),
            Item("spreadsheet", "Spreadsheet", NavigationSection.Business, 230),
        }).ToDictionary(item => item.Id);

        Assert.Equal(Loc.Get("nav.group.business"), menu["demo"].DisplayGroupTitle);
        Assert.True(menu["demo"].ShowGroupHeader);

        Assert.Equal("Notes group", menu["notes"].DisplayGroupTitle);
        Assert.True(menu["notes"].ShowGroupHeader);
        Assert.Equal("Notes group", menu["remote"].DisplayGroupTitle);
        Assert.False(menu["remote"].ShowGroupHeader);

        // 自定义组之后的默认 Business 项重新出现默认组头,而不是被并进上一个自定义组。
        Assert.Equal(Loc.Get("nav.group.business"), menu["spreadsheet"].DisplayGroupTitle);
        Assert.True(menu["spreadsheet"].ShowGroupHeader);
    }

    [Fact]
    public void Business_module_with_default_order_stays_between_workbench_and_runtime()
    {
        var ids = MainViewModel.BuildMenuItems(new[]
        {
            Item("task", "Tasks", NavigationSection.Runtime, 400),
            Item("logs", "Logs", NavigationSection.System, 600),
            Item("module", "Module", NavigationSection.Business, 500),
        }).Select(item => item.Id).ToArray();

        Assert.Equal(new[] { "module", "task", "logs" }, ids);
    }

    [Fact]
    public void Sample_module_gate_follows_the_deployment_flag()
    {
        Assert.False(ServiceCollectionExtensions.ShouldRegisterSampleModules(new DeploymentConfiguration()));
        Assert.True(ServiceCollectionExtensions.ShouldRegisterSampleModules(
            new DeploymentConfiguration { IncludeSampleModules = true }));
    }

    [Fact]
    public void Unknown_pages_are_preserved_only_while_sample_modules_are_disabled()
    {
        Assert.True(MainViewModel.ShouldPreserveUnknownNavigation(
            includeSamples: false, moduleFailed: false));
        Assert.False(MainViewModel.ShouldPreserveUnknownNavigation(
            includeSamples: true, moduleFailed: false));
        Assert.False(MainViewModel.ShouldPreserveUnknownNavigation(
            includeSamples: false, moduleFailed: true));
    }

    [Fact]
    public void Sample_modules_are_disabled_in_a_default_configuration()
    {
        Assert.False(new DeploymentConfiguration().IncludeSampleModules);
    }


    private sealed class FakePermissions(params string[] granted) : IPermissionService
    {
        public bool HasPermission(string permission) => granted.Contains(permission);
        public void Demand(string permission, string correlationId) => throw new NotSupportedException();
    }

    [Fact]
    public void Navigation_permissions_only_hide_when_identity_is_enabled()
    {
        var item = Item("logs", "Logs", NavigationSection.System, 600)
            with
        { RequiredAnyPermission = ["logs:read", "audit:read"] };
        var unrestricted = Item("overview", "Overview", NavigationSection.Workbench, 100);

        Assert.True(MainViewModel.IsNavigationAllowed(item, authenticationEnabled: false, permissions: null));
        Assert.True(MainViewModel.IsNavigationAllowed(item, authenticationEnabled: true, permissions: null));
        Assert.True(MainViewModel.IsNavigationAllowed(unrestricted, true, new FakePermissions()));
        Assert.True(MainViewModel.IsNavigationAllowed(item, true, new FakePermissions("audit:read")));
        Assert.False(MainViewModel.IsNavigationAllowed(item, true, new FakePermissions()));
    }
}
