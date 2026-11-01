using Avalonia;
using Avalonia.Headless;
using Avalonia.Headless.XUnit;
using Plinth.App;
using Xunit;

// 界面测试共享一个 Avalonia 应用实例,必须串行跑
[assembly: AvaloniaTestApplication(typeof(Plinth.App.Tests.TestAppBuilder))]
[assembly: CollectionBehavior(DisableTestParallelization = true)]

namespace Plinth.App.Tests;

/// <summary>用 headless 平台起真实 App(不弹窗口),让绑定/布局/输入都能被断言。</summary>
public static class TestAppBuilder
{
    public static AppBuilder BuildAvaloniaApp() => AppBuilder.Configure<App>()
        .UseSkia()
        .UseHeadless(new AvaloniaHeadlessPlatformOptions { UseHeadlessDrawing = false });
}
