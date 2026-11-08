# Dock 可选业务工作区接线说明

`Plinth.DockDemo` 默认仍是独立真实 Dock 示例。主应用只有在构建时设置
`PlinthEnableDockBusiness=true` 才会条件加入 Dock 包，并链接同一组 Dock 核心文件和真实业务窗格适配层；
独立示例本身不反向引用主应用。
默认构建不增加主应用的 Dock 依赖，也不自动打开入口。

最小业务组合是：

- `DemoListViewModel` + `DemoListView`：真实列表、异步查询、编辑脏状态和关闭确认。
- `NoteViewModel` + `NoteView`：真实 Notes 加载、保存、并发错误和关闭确认。

主应用当前没有 Dock 导航项或按钮，也不修改 `MainWindow`。在构建时设置
`PlinthEnableDockBusiness=true` 后，集成方可从已有命令、菜单动作或其他受控宿主入口保留现有
`IServiceProvider`，按需创建：

```csharp
var launcher = new DockBusinessWorkspaceLauncher(provider);
var window = launcher.CreateWindow(layoutPath);
window.Show();
```

`DockBusinessWorkspaceLauncher` 不注册服务、不修改 `MainWindow` 或 `MainViewModel`。
调用方只需调用 `CreateWindow`，并保存返回的 `Window` 引用以遵循宿主的关闭策略。
默认构建不会编译该入口，也不会添加 Dock 依赖。

适配层复用 `DemoDockFactory`、`DockWorkspace`、`DockLayoutStore` 和
`DockOptionalSession`。业务页面作为 Dock context 注入，Dock 的真实布局、浮动窗口、
布局序列化和窗口退出路径保持不变。`DemoDockFactory` 在业务窗格关闭事件中先取消关闭，
异步执行页面的 `IUnsavedChanges.TryLeaveAsync()`；只有获准后才重新发起关闭，成功关闭事件
才释放 ViewModel。工作区退出会先逐个执行同一关闭保护，再保存布局；浮动窗口由既有
`ExitWindows`/`OnWindowClosed` 路径退出，资源释放和重复 Dispose 都是幂等的。

宿主构建示例：

```text
dotnet build src/Plinth.App/Plinth.App.csproj -p:PlinthEnableDockBusiness=true
```

本环境的 glibc 2.17 无法启动缓存的 .NET 10.0.403 SDK，因此没有宣称该命令或桌面真实
窗口已经运行通过。接入后应在可运行 SDK 的环境执行 DockDemo 测试，并在真实桌面环境验证
Demo/Notes 脏关闭、取消关闭、浮窗退出、反复打开关闭和布局恢复。
