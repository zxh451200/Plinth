# 快速入门

Plinth 是一个通用的桌面 Admin 底座(Avalonia + .NET 10),用来在上面开发自己的业务软件。

当前代码是按《Plinth_Desktop 技术架构与开发规范 v4.0》搭的**可运行骨架**:应用壳(菜单、工具栏、导航、标签、状态栏)、DI 组合根、Serilog 日志、导航注册机制已经跑通;业务数据层和示例 CRUD 还没做,清单见文末。

## 运行

需要 .NET SDK 10.0.403(版本见 `global.json`)。

```bash
dotnet restore
dotnet run --project src/Plinth.App
```

运行后会看到带菜单、左侧导航、标签页和状态栏的主窗口,启动时自动打开「概览」页。

## 测试

```bash
dotnet test
```

`tests/Plinth.App.Tests` 用 Avalonia headless 平台起真实 App:断言打在真实的可视树、真实的指针点击和真实按键上(拖分隔条、关标签、F5/F1/Ctrl+W、绑定是否解析得出 View),不开窗口也不弹界面。改 Shell 布局或导航注册后先跑它。

CI 在 `.github/workflows/ci.yml`:`gate`(Windows,Release 构建 + `dotnet test`)阻塞合并;`linux-probe` 是观察位,`continue-on-error` 开着——开发机没有 WSL/Docker,Linux 那条没在本地验证过,连续绿几次再升成正式门禁。

## 项目结构

```text
Plinth.sln
├─ global.json                  固定 SDK 版本
├─ Directory.Build.props        所有项目共用的编译设置(版本号也在这里)
├─ Directory.Packages.props     所有 NuGet 包版本(只在这里改)
├─ .github/workflows/ci.yml     门禁:Windows 阻塞 + Linux 观察位
├─ src/Plinth.App/
   ├─ Program.cs                程序入口:Serilog 配置、启动 Avalonia
   ├─ App.axaml / App.axaml.cs  建 DI 容器、注册异常钩子、打开主窗口
   ├─ ServiceCollectionExtensions.cs   组合根:AddPlinthModules 注册服务和导航项
   ├─ AppInfo.cs                版本与运行时信息
   ├─ AppPaths.cs               数据目录 / 日志目录(%LOCALAPPDATA%\Plinth)
   ├─ ViewLocator.cs            XxxViewModel 自动对应 XxxView(同程序集)
   ├─ Navigation/
   │  ├─ NavigationItem.cs      Shell 导航契约:Id / Title / VM 工厂
   │  ├─ ShellNavigation.cs     Shell 自带页面的 Id 常量(注册处与 Shell 命令共用)
   │  └─ IRefreshable.cs        页面支持 F5 刷新时实现
   ├─ Views/                    MainWindow、OverviewView、AboutView(.axaml)
   ├─ ViewModels/               MainViewModel、WorkspaceTabViewModel、OverviewViewModel、AboutViewModel
   ├─ Services/                 FolderService(打开目录等应用服务)
   ├─ Diagnostics/              AvaloniaSerilogSink(Avalonia 日志转发到 Serilog)
   ├─ Styles/DesignTokens.axaml 浅色/深色主题色令牌
   └─ Assets/                   图标等资源
└─ tests/Plinth.App.Tests/
   ├─ TestAppBuilder.cs         headless 平台起真实 App
   ├─ Shell.cs                  摆好主窗口 + 真指针/真键盘输入的夹具
   └─ ShellTests.cs             布局、导航、快捷键、日志桥接断言
```

## 新增一个页面

1. 在 `Views/` 新建 `XxxView.axaml`(`UserControl` + `x:DataType`),只写布局和绑定。
2. 在 `ViewModels/` 新建 `XxxViewModel.cs`,继承 `ViewModelBase`,用 `[ObservableProperty]` 和 `[RelayCommand]` 写状态和命令。
3. 需要业务逻辑时,在 `Services/` 加一个普通类,通过构造函数注入使用。
4. 在 `ServiceCollectionExtensions.cs` 里注册 ViewModel。
5. **加一条导航注册**,否则页面进不去:
   ```csharp
   services.AddSingleton<NavigationItem>(provider => new NavigationItem(
       "your.page.id",
       "页面标题",
       () => provider.GetRequiredService<XxxViewModel>()));
   ```
   然后在本文件的「项目结构」和 `MainViewModel` 都不用改——Shell 注入 `IEnumerable<NavigationItem>` 自动生成导航。
   页面注册了但不想占侧栏一行(比如只能从菜单或快捷键进的「关于」),再加一个 `ShowInMenu: false`。

## 键盘快捷键

| 快捷键 | 作用 |
|---|---|
| `Ctrl+W` | 关闭当前标签 |
| `F5` | 刷新当前页面(页面实现 `IRefreshable` 时) |
| `F1` | 打开「关于」 |

快捷键集中定义在 `MainWindow.axaml` 的 `Window.KeyBindings`,菜单项用 `InputGesture` 只做展示。

## 已知限制 / 尚未实现

按规范 §18,以下还没做:

- SQLite + EF Core 数据层(`Data/AppDbContext.cs` 未建)
- 列表(TableView)和编辑表单的样板页面
- 可运行的 Inventory CRUD 示例模块
- 主题切换 UI(令牌已备浅/深两套,界面跟随系统)
- 中英文资源(RESX)
- 单实例运行、安装包/便携包、`nuget.config` 与包锁文件(CI 已有 Windows 门禁 + Linux 观察位)
- 应用图标仍是 Avalonia 模板默认图标(`Assets/avalonia-logo.ico`),待品牌资源替换
