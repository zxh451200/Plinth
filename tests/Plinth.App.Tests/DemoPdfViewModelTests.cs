using System;
using System.IO;
using System.Linq;
using System.Text;
using System.Threading.Tasks;
using Avalonia.Headless.XUnit;
using Plinth.App.Localization;
using Plinth.App.Modules.Demo;
using Plinth.App.Modules.Demo.Data;
using Plinth.App.Modules.Demo.ViewModels;
using Xunit;

namespace Plinth.App.Tests;

/// <summary>页面命令到文件落盘的最小闭环；文件选择器和系统打印仍由人工验收。</summary>
[Collection("Localization")]
public sealed class DemoPdfViewModelTests
{
    [AvaloniaFact]
    public async Task Export_command_writes_the_current_page_to_the_selected_path()
    {
        Loc.Initialize(Loc.DefaultLanguage);
        using var shell = new Shell();
        var nav = shell.ViewModel.NavigationItems.Single(item => item.Id == DemoModuleRegistration.PageId);
        shell.ViewModel.OpenTabCommand.Execute(nav);
        var viewModel = (DemoListViewModel)shell.ViewModel.SelectedTab!.Content;
        viewModel.Items.Add(new DemoRecord { Code = "PDF-001", Name = "导出验证", Remark = "中文备注" });

        var path = Path.Combine(Path.GetTempPath(), $"plinth-pdf-{Guid.NewGuid():N}.pdf");
        try
        {
            await viewModel.ExportPdfAsync(path);

            var bytes = await File.ReadAllBytesAsync(path);
            Assert.Equal("%PDF", Encoding.ASCII.GetString(bytes, 0, 4));
            Assert.True(bytes.Length > 1000);
        }
        finally
        {
            File.Delete(path);
        }
    }
}
