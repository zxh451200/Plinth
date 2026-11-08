# Dock 可选能力：独立真实 UI 项目

`src/Plinth.DockDemo/Plinth.DockDemo.csproj` 是显式启动的可选桌面程序，使用真实 DockControl、工具窗格、文档页签、分隔条和浮窗。它不引用 Plinth.App 项目，只通过 Compile Link 复用既有 DockLayoutStore / DockOptionalSession 源文件。主窗口、主 ViewModel 和主解决方案保持独立；普通主应用构建不会还原 Dock。没有自研 Dock 引擎。

## 已核实的信息

- 依赖：[wieslawsoltes/Dock v12.1.0.13](https://github.com/wieslawsoltes/Dock/releases/tag/v12.1.0.13)，发布记录明确主包使用 Avalonia 12.1.4；仓库有持续发布的 11/12 系列。
- 已读取固定标签 [LICENSE.TXT 全文](https://raw.githubusercontent.com/wieslawsoltes/Dock/v12.1.0.13/LICENSE.TXT)：MIT，Copyright (c) Wiesław Šoltés。独立程序输出包含 Dock.LICENSE.txt，保留版权和许可声明。最终包内许可证仍需在正式还原后交叉核对。
- [Avalonia 12.1.4 NuGet 页面](https://www.nuget.org/packages/Avalonia/12.1.4)确认目标版本可用；[Dock.Avalonia 12.1.0.13 页面](https://www.nuget.org/packages/Dock.Avalonia/12.1.0.13)明确列出 net8.0、net10.0 的 Avalonia >= 12.1.4 依赖，以及同版本 Dock 子包依赖。
- Directory.Packages.props 将 Dock.Avalonia、Dock.Model.Mvvm、Dock.Avalonia.Themes.Fluent、Dock.Serializer.SystemTextJson 中央固定为精确范围 `[12.1.0.13]`，只有独立项目引用这些包，Avalonia 保持 12.1.4。不手写锁文件，锁文件须由真实 restore 生成。

## 已按固定标签源码核对的 API

以下链接均为 v12.1.0.13，代码依据这些实现，不依据 master 或虚构调用：

- [Factory.cs](https://github.com/wieslawsoltes/Dock/blob/v12.1.0.13/src/Dock.Model.Mvvm/Factory.cs)：Dock.Model.Mvvm.Factory，CreateLayout 返回 IRootDock，CreateRootDock、CreateList 构造内置模型。
- [FactoryBase.Init.cs](https://github.com/wieslawsoltes/Dock/blob/v12.1.0.13/src/Dock.Model/FactoryBase.Init.cs)：InitLayout(IDockable)，递归设置 Owner / Factory / Context，并执行根布局 ShowWindows。
- [DockControl.axaml.cs](https://github.com/wieslawsoltes/Dock/blob/v12.1.0.13/src/Dock.Avalonia/Controls/DockControl.axaml.cs)：Dock.Avalonia.Controls.DockControl，Layout 为 IDock，Factory 为 IFactory；InitializeLayout / InitializeFactory 是布尔属性。手动 InitLayout 后两者设 false，避免控件重复初始化。Layout=null 或脱离视觉树会 DeInitialize、移除工厂登记并清理缓存和订阅。
- [DockSerializer.cs](https://github.com/wieslawsoltes/Dock/blob/v12.1.0.13/src/Dock.Serializer.SystemTextJson/DockSerializer.cs)：默认构造、Serialize<T>(T)、Deserialize<T>(string)。模型类型加载后再创建 serializer，以取得生成的契约注册快照。只使用内置模型，无应用派生类型或反射伪造契约。
- [DockableBase.cs](https://github.com/wieslawsoltes/Dock/blob/v12.1.0.13/src/Dock.Model.Mvvm/Core/DockableBase.cs)：Context / Factory 是 IgnoreDataMember，恢复时通过显式 ContextLocator 重建运行时内容。
- [FactoryBase.Events.cs](https://github.com/wieslawsoltes/Dock/blob/v12.1.0.13/src/Dock.Model/FactoryBase.Events.cs) 和 [FactoryBase.Dockable.cs](https://github.com/wieslawsoltes/Dock/blob/v12.1.0.13/src/Dock.Model/FactoryBase.Dockable.cs)：OnDockableClosing 可否决关闭，成功后才调用 OnDockableClosed，释放放在后者；浮窗整体关闭通过 OnWindowClosed 处理。
- [NavigateAdapter.cs](https://github.com/wieslawsoltes/Dock/blob/v12.1.0.13/src/Dock.Model/Adapters/NavigateAdapter.cs)：RootDock.ExitWindows 遍历浮窗，Save 后 Exit；退出资源顺序复用此命令。
- [DockFactory 示例](https://github.com/wieslawsoltes/Dock/blob/v12.1.0.13/samples/DockMvvmSample/ViewModels/DockFactory.cs)：HostWindowLocator 使用 nameof(IDockWindow) 键返回真实 HostWindow。

## 行为和寿命

默认布局包含两个只读文档页签、两个工具页签，可通过 Dock 拖动、合并、分割、浮动和关闭。应用级内容模板也供浮窗使用。重置会新建工厂和内容会话，释放旧窗格、浮窗和控件绑定。

DockWorkspace 在 UI 线程生成 JSON 快照，DockLayoutStore 负责版本信封及临时文件替换。保存前调用浮窗 Save 采集几何信息。布局路径为 LocalApplicationData/Plinth/DockDemo/layout.json，与主应用布局隔离。

缺失、坏 JSON、未知信封版本、未知窗格、重复窗格、错误 Dock 类型、结构循环或过深布局均回退默认布局。反序列化后先验证，才 InitLayout / 显示浮窗；恢复失败会释放本次工厂和内容，以全新会话回退。取消继续向上传播。原文件在用户保存或正常退出前保留。

正常关闭先等待保存成功，再关闭；保存失败保留窗口，用户可重试或选择“不保存并退出”。内容是只读示例，无业务未保存编辑状态。独立程序关闭 Dock 自带的“主窗口关闭时立即关闭浮窗”选项，由本窗口保证保存先于浮窗退出。资源会话去重、逆序释放且幂等；成功关闭单窗格、关闭浮窗和退出宿主均释放对应内容。

## 构建、测试和运行

需要 global.json 指定的 SDK 10.0.403：

```bash
python3 /home/work/data/xxx/Plinth/scripts/pilot_checks.py --mode generate-locks --pilot dock
# 审查真实生成的锁后，单独复验：
python3 /home/work/data/xxx/Plinth/scripts/pilot_checks.py --mode locked --pilot dock
dotnet run --project /home/work/data/xxx/Plinth/src/Plinth.DockDemo/Plinth.DockDemo.csproj
```

统一入口包含 restore、format、build、test，并保留命令日志和 TRX；
证据目录、Windows 调用方法与首次生成/locked 复验边界见
[独立试点验收说明](../GETTING_STARTED.md#独立试点的真实-net-验收入口)。

独立测试项目还链接复用原有 DockOptionalBoundaryTests，不需要构建主应用。新增 Avalonia headless 测试覆盖真实控件挂载与可见内容、工厂解绑、真实 serializer 往返、顺序和上下文恢复、坏 payload / 未知窗格回退、关闭否决与成功释放、取消，以及浮窗保存/恢复/退出。

当前环境 `dotnet --info` 返回 command not found（127），无法还原、编译、执行 headless 测试或启动 GUI。上述测试是已编写的验证，不是通过记录；未生成或伪造 packages.lock.json。仍需在有 SDK 的环境运行这些命令，并在 Windows 桌面验收拖拽分割、浮窗、重启恢复和磁盘不可写时的退出行为。
