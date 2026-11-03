using System;
using System.IO;
using System.Runtime.CompilerServices;

namespace Plinth.App.Tests;

/// <summary>
/// 测试开始前把数据目录指到一个临时文件夹,这样启动程序时建的数据库不会落到真实的用户数据目录里。
/// 示例模块(Demo/Notes)默认不注册,测试的 Shell 仍依赖这些页面,所以在读取
/// DeploymentConfiguration.Current 之前用环境变量显式打开。
/// ModuleInitializer 在测试程序集加载时就执行,早于任何读取 AppPaths / 部署配置的代码。
/// </summary>
internal static class TestEnvironment
{
    [ModuleInitializer]
    internal static void Initialize()
    {
        Environment.SetEnvironmentVariable(
            AppPaths.DataDirectoryVariable,
            Path.Combine(Path.GetTempPath(), "plinth-tests-" + Guid.NewGuid().ToString("N")));
        Environment.SetEnvironmentVariable("PLINTH_IncludeSampleModules", "true");
    }
}
