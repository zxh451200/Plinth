# Runtime plugin 接线清单

运行时插件默认关闭。`AddRuntimePluginsModule()` 的无参调用注册空白名单，页面不会发现、读取或执行用户指定路径；页面也没有路径、哈希或命令编辑控件。

启用时只能由受信任组合根显式构造 `AllowedPlugin`，再传给 `RuntimePluginConfiguration.FromTrustedDeployment(...)`：

```csharp
var pluginPath = Path.Combine(AppContext.BaseDirectory, "plugins", "Plinth.EchoPlugin.exe");
var deployment = RuntimePluginConfiguration.FromTrustedDeployment([new AllowedPlugin("echo", pluginPath, "<deployment-approved SHA-256>", ["echo"])], TimeSpan.FromSeconds(5));
services.AddRuntimePluginsModule(deployment);
```

示例路径和哈希是接线占位，不能当作已发布插件或已验证结果。不得从用户设置、清单、环境变量、IPC 请求或页面输入生成 `AllowedPlugin`。接线后“执行”按钮调用现有 `RuntimePluginHost` 的真实进程外 framed JSON IPC；输入只作为协议数据，不作为命令行参数。

页面 VM 在 `Dispose` 中取消正在进行的调用；Host 会终止并回收子进程、关闭管道并释放串行闸门。页面只显示 Host 自有错误码，不显示子进程 stderr、路径或异常正文。

共享组合根通过受信任部署配置的 `EnableRuntimePlugins` 受控注册模块：

```json
{
  "EnableRuntimePlugins": true
}
```

该开关只调用 `AddRuntimePluginsModule()` 的无参形式，因此仍使用空白批准名单；它不会凭开关值生成插件路径或批准哈希。需要实际插件时，组合根必须改为显式传入
`RuntimePluginConfiguration.FromTrustedDeployment(...)`，并在同一受信任部署代码中提供固定路径、批准哈希和操作集合。

运行时插件页面的文字由模块自己的双语字符串表提供，主应用中央 RESX 不需要增加资源键。接线后应在真实目标系统验证批准哈希、可执行文件格式、IPC、取消和进程回收。

启用身份认证时，页面导航和调用入口都要求 `runtimePlugins:invoke`。权限变化后旧布局、快捷入口和已打开页面也不能绕过页面级检查；调用按钮只表示当前权限快照，Host 仍以批准名单和协议边界为最终执行条件。取消只取消当前调用，重新调用需要重新创建本次调用令牌；关闭页面或退出应用会取消页面生命周期令牌并等待 Host 的进程、管道和串行闸门清理。

## 未完成接线的验收入口

桌面组合根目前只按 `EnableRuntimePlugins` 注册默认拒绝配置，开关本身不会批准插件。真实接线必须同时提供：

- 受信任部署代码中的固定绝对路径、人工批准 SHA-256 和有限操作集合；
- `runtimePlugins:invoke` 的身份权限声明及服务端等价授权（若部署启用身份）；
- Windows/Linux 目标上的可执行格式、路径父级重解析点、哈希校验和 TOCTOU 部署目录审计；
- 正常响应、未知字段、额外 stdout、stderr 超限、非零退出、超时、取消、子进程树残留和页面关闭清理证据；
- `python3 scripts/pilot_checks.py --mode generate-locks --pilot runtime-plugins` 后人工审查锁文件，再执行 `--mode locked`；具备 .NET 10 SDK 后执行真实 restore/format/build/test。

当前宿主缺少 .NET SDK，以上入口没有在本环境执行，静态检查不能替代 C# 构建、Windows 进程回收或真实部署验收。
