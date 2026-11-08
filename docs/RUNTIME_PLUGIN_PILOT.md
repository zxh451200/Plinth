# 运行时插件最小安全试点

插件通过独立进程与受控 stdin/stdout IPC 执行。宿主源码在
`src/Plinth.App/Modules/RuntimePlugins`，由桌面项目自动编译；没有接入共享 DI、资源、导航或
MainWindow。独立调用入口为 `tools/Plinth.PluginPilot`，独立示例为 `samples/Plinth.EchoPlugin`。
不扫描插件目录，不在主进程反射加载任意 DLL，不自动安装或启用插件。

桌面组合根不调用 `AddRuntimePluginModule`：当前没有该扩展方法定义，也没有桌面插件模块、
受信任配置入口或插件页面。已移除未完成接线遗留的调用及对应 using，避免未定义方法阻塞
桌面编译。现阶段请使用下述独立 CLI；后续桌面接入须另行实现默认关闭的受信任配置与安全入口。

## 协议及限制

- 默认空白名单拒绝所有调用。注册固定 ID、规范绝对可执行路径、SHA-256 和操作集合；
  复制操作集合，每次启动前重新检查路径全部层级的符号链接/重解析点和可执行文件哈希。
- Windows 仅支持具有 MZ 头的 .exe；Linux 仅支持具有 ELF 头的无扩展名文件。
  禁止脚本、DLL、PATH 查找、shell 和动态启动参数。CLI 只允许输入业务文本。
- 一次进程一次请求，stdin 写完关闭。stdout 必须恰好一帧并 EOF，退出码必须为 0。
  stderr 并行读取丢弃，最多 8 KiB，超限终止。
- 每帧是 4 字节小端正整数长度加 UTF-8 JSON；分配前检查长度，最多 64 KiB。
  JSON 最大深度 8，拒绝未知字段。请求字段 version/requestId/operation/input；
  响应字段 version/requestId/success/output。version 必须是 1，响应 requestId 必须匹配。
- input 和 output 分别最多 16 KiB UTF-8。宿主每实例最多一个调用，排队计入总超时。
  默认超时 5 秒，允许配置 50 毫秒至 30 秒；支持 CancellationToken。
- 故障、超时和取消时取消管道并 Kill(entireProcessTree: true)，最多等待退出 2 秒；
  管道各最多额外观察 2 秒后释放调用。清理失败返回 CleanupFailed。
- 清空继承环境，仅在 Windows 保留 SystemRoot/WINDIR，不传应用凭据、服务或数据库对象。
- 错误仅返回宿主 PluginError 枚举，丢弃异常文本、stderr 和失败响应内容。
  成功 output 仍是不可信数据；CLI 用 JSON 转义显示，UI 接入也必须作为纯文本展示。

## 信任边界

进程外执行不等于操作系统权限沙箱。插件使用当前用户权限，仍可访问该用户的文件和网络。
只批准经过审阅的插件，禁止派生后台进程。通用进程树清理不能保证捕获提前退出后遗留、
脱离或重归属的子进程；如需运行不可信第三方代码，应先补充 Windows Job Object/低权限令牌
或 Linux cgroup/namespace 隔离。

部署目录、所有父目录、白名单及 approved.sha256 必须由可信管理员控制，插件和非可信用户
不可修改。可执行文件及全部依赖需整体审阅；可执行文件哈希不能单独证明旁加载依赖完整性。
校验与启动之间存在 TOCTOU 窗口，因此不能批准可写目录中的插件。
本试点不提供自动下载、热更新、插件签名分发或桌面 UI 自动启用。

## 构建及运行

需要 .NET 10 SDK。在仓库根目录用 PowerShell 执行；Linux 将 RID 换成 linux-x64，
可执行文件名去掉 .exe，并用 sha256sum 生成 approved.sha256（文件中只存哈希）。
本地 artifacts 目录仅供验证，正式部署必须设置上面的目录权限。

```powershell
dotnet publish ./tools/Plinth.PluginPilot/Plinth.PluginPilot.csproj -c Release -r win-x64 --self-contained true -o ./artifacts/plugin-pilot
dotnet publish ./samples/Plinth.EchoPlugin/Plinth.EchoPlugin.csproj -c Release -r win-x64 --self-contained true -o ./artifacts/plugin-pilot/plugins/echo
# 人工审阅完整发布内容后，由可信操作者批准当前可执行文件。
$exe = Resolve-Path ./artifacts/plugin-pilot/plugins/echo/Plinth.EchoPlugin.exe
(Get-FileHash $exe -Algorithm SHA256).Hash | Set-Content ./artifacts/plugin-pilot/plugins/echo/approved.sha256 -Encoding ascii
./artifacts/plugin-pilot/Plinth.PluginPilot.exe '你好，插件'
```

CLI 固定 ID 为 sample.echo，唯一操作为 echo，可执行路径固定在入口目录下的 plugins/echo。
approved.sha256 缺失或无效时拒绝启动。禁止在宿主中自动计算并批准哈希。

```csharp
var host = new RuntimePluginHost([
    new AllowedPlugin("sample.echo", absoluteExecutablePath, approvedHash, ["echo"])
]);
var result = await host.InvokeAsync("sample.echo", "echo", "你好", cancellationToken);
```

## 测试及当前验证状态

```powershell
python ./scripts/pilot_checks.py --mode generate-locks --pilot runtime-plugins
# 审查真实生成的锁后，单独复验：
python ./scripts/pilot_checks.py --mode locked --pilot runtime-plugins
```

以上从仓库根目录调用，覆盖宿主、示例、恶意夹具和测试四个引用项目的
restore、format、build、test，保留日志和 TRX。前提、证据位置与验收限制见
[独立试点验收说明](../GETTING_STARTED.md#独立试点的真实-net-验收入口)。

独立测试不依赖桌面 UI 或数据库；自动构建示例与恶意测试进程并复制到测试输出目录。
覆盖正常调用、默认拒绝、操作集合快照、超限输入、协议版本/ID 不匹配、超长/截断/非法帧、
额外输出、stderr 洪泛、非零退出、错误脱敏、路径/哈希拒绝、Linux 符号链接、环境隔离、
超时/取消以及活动父子进程清理。Windows 符号链接用例因权限要求不执行。
测试 apphost 要求全局安装的 .NET 10 运行时（宿主清除 DOTNET_ROOT）；正式部署用自包含发布。
tests/Plinth.PluginFixture 是恶意测试夹具，禁止部署为应用插件。

当前实现环境 dotnet --info 返回 dotnet: command not found，尚未编译或执行 .NET 测试。
源码和 XML 检查不能视为运行验收通过。测试项目通过上面的显式命令运行，未修改共享解决方案。

可在无 SDK 环境执行 `python -m unittest scripts.test_pilot_checks`，其中静态边界检查
确认桌面组合根未接入插件，并确认独立 CLI 仍链接宿主和协议源码；这不替代真实 .NET 验收。
