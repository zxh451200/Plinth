using Microsoft.Extensions.DependencyInjection;
using Plinth.App.Modules.Demo.Services;
using Plinth.App.Modules.Demo.ViewModels;
using Plinth.App.Navigation;
using Plinth.App.Localization;

namespace Plinth.App.Modules.Demo;

/// <summary>
/// 示例模块的注册入口,演示"列表 + 表单 + 增删改查"怎么写,不含任何业务。
/// 新模块照这个样子写:注册自己的服务和 ViewModel,最后加一条 NavigationItem,页面就会出现在左侧导航里。
/// 不需要示例时:删除 Modules/Demo 文件夹,并去掉 ServiceCollectionExtensions 里的 AddDemoModule() 一行。
/// </summary>
public static class DemoModuleRegistration
{
    public const string PageId = "demo.list";

    public static IServiceCollection AddDemoModule(this IServiceCollection services)
    {
        services.AddTransient<DemoService>();
        services.AddTransient<DemoListViewModel>();
        services.AddSingleton<HelpTopic>(_ => new("demo.editing", Loc.Get("help.demo.title"),
            Loc.Get("help.demo.body"), PageId)
        { TitleKey = "help.demo.title", BodyKey = "help.demo.body" });

        // 只绑定现有页面的命令；New / Cancel 保留 VM 的 dirty 确认，Delete 只进入确认态。
        services.AddSingleton(new CommandMenuContribution(PageId, "demo.new",
            page => page is DemoListViewModel { IsSaving: false } vm ? vm.NewCommand : null,
            PageCommandAction.New));
        services.AddSingleton(new CommandMenuContribution(PageId, "demo.save",
            page => page is DemoListViewModel { IsEditing: true, IsSaving: false } vm ? vm.SaveCommand : null,
            PageCommandAction.Save));
        services.AddSingleton(new CommandMenuContribution(PageId, "demo.delete",
            page => page is DemoListViewModel { CanDelete: true, IsSaving: false } vm ? vm.AskDeleteCommand : null,
            PageCommandAction.Delete));
        services.AddSingleton(new CommandMenuContribution(PageId, "demo.cancel",
            page => page is not DemoListViewModel { IsEditing: true, IsSaving: false } vm ? null
                : vm.IsConfirmingDelete ? vm.CancelDeleteCommand : vm.CancelCommand,
            PageCommandAction.Cancel));
        services.AddSingleton(new CommandMenuContribution(PageId, "demo.export",
            page => page is DemoListViewModel { IsSaving: false } vm ? vm.ExportCommand : null));

        // NavigationItem 放在最后一步注册
        services.AddSingleton<NavigationItem>(provider => new NavigationItem(
            PageId,
            Loc.Get("nav.demo"),
            () => provider.GetRequiredService<DemoListViewModel>())
        { Order = 200, Icon = "\uE8A5", TitleKey = "nav.demo" });

#if PLINTH_XLSX
        services.AddTransient<SpreadsheetService>();
        services.AddTransient<SpreadsheetViewModel>();
        services.AddSingleton<HelpTopic>(_ => new("demo.spreadsheet", Loc.Get("help.spreadsheet.title"),
            Loc.Get("help.spreadsheet.body"), SpreadsheetViewModel.PageId)
        { TitleKey = "help.spreadsheet.title", BodyKey = "help.spreadsheet.body" });
        services.AddSingleton<NavigationItem>(provider => new NavigationItem(
            SpreadsheetViewModel.PageId,
            new SpreadsheetLabels()["Title"],
            () => provider.GetRequiredService<SpreadsheetViewModel>())
        { Order = 230, Icon = "\uE80A" });
#endif

        return services;
    }
}
