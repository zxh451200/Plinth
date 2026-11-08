# 快速入门

Plinth 是一个通用的桌面 Admin 底座(Avalonia + .NET 10),用来在上面开发自己的业务软件。

当前源码包含应用壳、工作区持久化、未保存确认、任务与诊断、CSV 预览映射、ZIP 备份恢复及可选远程身份接入。Demo 默认使用本机 SQLite。本机已用 SDK 10.0.403 完成真实 restore、Release 构建与自动回归（App 1183 项、Spreadsheet 48 项，0 失败）；真实 IdP、OS 级 DPI/IME/读屏与安装升级人工矩阵仍未验收；本机标准用户首启与独立小卷磁盘满（启动/CLI 备份）已实测，边界见实现状态。实际限制见 [实现状态](docs/IMPLEMENTATION_STATUS.md)。

## 运行

需要 .NET SDK 10.0.403(版本见 `global.json`)。

```bash
dotnet restore --configfile scripts/nuget.config
dotnet run --project src/Plinth.App
```

首次启动默认打开「概览」页；已有工作区配置时恢复上次打开的页面。

示例模块(「示例数据」Demo 与「便笺」Notes)默认不注册。开发时在安装目录的 `deployment.json` 设 `"IncludeSampleModules": true`，或设环境变量 `PLINTH_IncludeSampleModules=true` 后启动。左侧导航按 Workbench、Business、Runtime、System 分区，业务模块只往 Business 分区追加条目；「设置」固定在最底部的 Footer 分区。

文件菜单的“最近页面”保留最近访问的 10 个页面，可以清空；窗口菜单的“收藏当前页面”可添加或取消收藏，最多 20 项，勾选状态表示已收藏。最近记录和收藏只保存页面 ID，重启及 ZIP 恢复后继续保留；移除模块后不再显示它的入口。重置布局不会清空收藏和最近记录，打开快捷入口仍走原有导航与服务权限检查。侧栏可用导航顶部的折叠按钮或“查看”菜单收起为 48px 图标轨道，再次切换恢复原宽度；折叠状态随工作区布局一并保存，重启后保持。

## 命令行版本查询

开发环境使用下面的命令；第二个 `--` 把参数交给应用：

```bash
dotnet run --project src/Plinth.App -- --version
```

输出格式为 `Plinth 0.1.0`，版本来自与关于页相同的程序集。仅单独的 `--version` 被接受；未知参数、多余参数和重复参数会向标准错误输出使用提示，并以退出码 `2` 结束。版本查询成功返回 `0`，输出管道不可写时返回 `1`。不带参数才启动桌面。

参数处理先于单实例通知、用户设置、日志和备份恢复，因此无显示器或用户配置损坏时也可查询版本，不会唤醒已有窗口或创建数据目录。命令行错误使用内置默认语言，不读取用户的界面语言设置。

Windows 发布包仍是窗口程序。从脚本查询版本并等待退出，可使用：

```powershell
$process = Start-Process .\Plinth.App.exe -ArgumentList '--version' -Wait -PassThru `
    -RedirectStandardOutput version.txt -RedirectStandardError version-error.txt
$process.ExitCode
Get-Content version.txt
```

普通桌面启动不会额外弹出控制台。发布目录的 `--version` 与未知参数退出码已实测；终端直连显示、单路重定向的 Windows 行为仍按发布验收清单复核。

## 离线帮助与许可证

按 F1 或使用“帮助 → 帮助中心”打开当前页对应的离线主题。支持搜索标题、正文、主题键和错误码；没有对应主题时打开入门说明。任务错误和意外错误弹窗也可跳到相关帮助，帮助页顶部显示当前版本。

模块注册帮助与注册页面一样，使用普通 DI 集合，内容放双语资源：

```csharp
services.AddSingleton<HelpTopic>(_ => new(
    "your.topic", Loc.Get("your.help.title"), Loc.Get("your.help.body"),
    PageId: "your.page.id", ErrorCodes: ["YOUR_ERROR_CODE"]));
```

主题键必须唯一，`PageId` 用于 F1 定位，`ErrorCodes` 可省略。此写法需要 `Plinth.App.Navigation` 和现有本地化命名空间，不需要增加帮助框架或网络请求。

帮助菜单和关于页提供“第三方许可证声明”。它只读取安装目录的 `THIRD_PARTY_NOTICES.md`，按纯文本显示，可选择复制；上限 2 MiB，页面关闭会取消读取。发布脚本根据真实还原的依赖生成此文件，开发构建缺少文件时会提示，不会用手写依赖清单代替发布核验。

runtime、字体、图标和安装器等不在 NuGet 自动发现范围内。发布时可显式传入 `-SupplementalComplianceFile`，合并实际核对过的组件库存与 LICENSE/NOTICE 正文，具体格式见 [补充许可材料](docs/RELEASE_COMPLIANCE.md)。工具会校验正文路径和摘要，但不会据此宣称许可审查或分发清单完整。

## 测试

### 独立试点的真实 .NET 验收入口

`scripts/acceptance.ps1` 验收主解决方案；Dock、RuntimePlugins、OfflineSync
在解决方案之外，使用同一跨平台入口 `scripts/pilot_checks.py`。
需要 Python 3.8+、`global.json` 指定的 .NET 10 SDK（10.0.403 / latestPatch）、
可访问配置中的 NuGet 源，以及插件测试所需全局安装的 .NET 10 运行时。
从仓库根目录运行，Windows 可将 `python3` 换为 `python`：

```bash
# 首次还原：由真实 NuGet restore 生成/更新锁，再执行 format/build/test
python3 scripts/pilot_checks.py --mode generate-locks
```

此命令依次覆盖 Dock 的 2 个、RuntimePlugins 的 4 个（包括示例和恶意测试夹具）、
OfflineSync 的 2 个项目引用图；根测试项目 restore/build 会遍历 ProjectReference，
不构建主应用。每个项目均执行现有 CI 的 format 检查，Release build 开启 nullable
警告门禁，test 使用 `--no-build --no-restore`，防止隐式还原混入后续阶段。
脚本不生成锁内容，只检查 restore 后每个引用项目的锁文件存在且非空；
依赖解析和锁内容有效性由真实 NuGet 负责。

首次命令成功后，人工审查这 8 个项目实际生成的 `packages.lock.json`，
核对直接/传递依赖和完整性值；不要手写锁文件。随后**另行**运行：

```bash
# 按现有锁复验：缺少任一引用项目的锁会在 restore 前失败
python3 scripts/pilot_checks.py --mode locked
```

`locked` 使用 `--locked-mode`，不自动回退到生成模式，也不自动批准或提交锁。
可加 `--pilot dock`、`--pilot runtime-plugins` 或 `--pilot offline-sync`
单独验收；默认 `all`。任一 SDK、restore、锁检查、format、build、test 或 TRX
检查失败，立即非零退出，不继续下一个阶段或试点。

每次调用在 `artifacts/pilot-checks/<mode>-<唯一目录>/` 保留 SDK 信息和每条命令的
合并 stdout/stderr、退出码；TRX 在该目录的 `<pilot>/test-results/`。
控制台显示日志位置，命令输出写入日志；失败前产生的日志和 TRX 保留。
可用 `--output <目录>` 修改证据父目录，重复调用不会覆盖先前证据。
CI 的 Windows/Ubuntu 试点矩阵复用此入口并始终上传证据；目前显式使用
`generate-locks` 做 bootstrap，**尚不是 locked 可复现性门禁**。
审查并提交真实锁后，应将试点 CI 的模式改为 `locked`。

本次仅验证 Python 编排测试，未执行真实 .NET restore/build/test，也未生成锁，
不能把脚本交付记为三个试点运行通过。自动测试不替代 Dock 实机拖拽/浮窗验收、
插件生产隔离或同步服务/断电验收；具体边界见各试点说明。

只有 Python 3.8+、尚无 PowerShell 或 .NET SDK 时，可先在仓库根目录运行离线检查：

```bash
python3 scripts/offline_checks.py
```

Windows 可用 `python` 替换 `python3`。脚本沿用启动它的解释器，并按脚本位置定位仓库，因此也可从其他目录通过绝对路径调用。它依次运行工具单测、源码契约、资源／项目静态检查和 Python SQLite smoke，失败立即停止并返回 `1`，全部完成返回 `0`。工具单测可能因缺少 PowerShell 等运行时而跳过，须保留输出中的 skipped 数量。该入口不执行锁包、还原、C# 编译测试、真实 EF 迁移、桌面或安装包验收；完整门禁仍使用 [发布交接](docs/release/RELEASE.md) 中的 `acceptance.ps1`。

```bash
dotnet test -p:TestingPlatformDotnetTestSupport=true
```

`tests/Plinth.App.Tests` 用 Avalonia headless 平台起真实 App:断言打在真实的可视树、真实的指针点击和真实按键上(拖分隔条、关标签、F5/F1/Ctrl+W、绑定是否解析得出 View),不开窗口也不弹界面。改 Shell 布局或导航注册后先跑它。

CI 的 Windows / Ubuntu `gate` 均阻断失败，包含源码契约、SQLite smoke、工具测试、locked restore、format、Release 构建和测试。手动 dependency-locks workflow 用于生成真实锁文件。工作流配置存在不代表已执行成功，详见 [发布交接](docs/release/RELEASE.md)。

本地真实 SQLite／CSV 的 10k 与 5000 行往返测量入口默认不编译；需要测量时按 [性能 POC](docs/PERFORMANCE_POC.md) 使用 Release 和 `-p:PlinthRunPoc=true` 显式运行。阶段内存采样来自测试进程，不能作为桌面峰值或性能达标证据；现有场景已执行过，最新数字见该文档。

## 项目结构

下面只列核心路径；业务模块都在 `src/Plinth.App/Modules/` 下（Demo、Notes、AdvancedTable、RemoteDemo、RuntimePlugins、OfflineSync），完整文件以源码为准。

```text
Plinth.sln
├─ global.json                  固定 SDK 版本
├─ Directory.Build.props        所有项目共用的编译设置(版本号也在这里)
├─ Directory.Packages.props     所有 NuGet 包版本(只在这里改)
├─ .github/workflows/ci.yml     门禁:Windows / Ubuntu 均阻塞
├─ src/Plinth.App/
   ├─ Program.cs                程序入口:Serilog 配置、启动 Avalonia
   ├─ App.axaml / App.axaml.cs  建 DI 容器、注册异常钩子、打开主窗口
   ├─ ServiceCollectionExtensions.cs   组合根:AddPlinthModules 注册服务和导航项
   ├─ AppInfo.cs                版本与运行时信息
   ├─ AppPaths.cs               数据目录 / 日志目录(%LOCALAPPDATA%\Plinth)
   ├─ ViewLocator.cs            XxxViewModel 自动对应 XxxView(同程序集)
   ├─ Modules/Demo/             示例模块:照着它写业务模块(下面有说明)
   ├─ Data/                     AppDbContext(整个程序唯一的数据库上下文)、启动时建库升级
   ├─ Localization/             Loc(取文字)、TExtension(XAML 里的 {loc:T key})
   ├─ Resources/                Strings.resx(英文)、Strings.zh-CN.resx(中文)
   ├─ Navigation/
   │  ├─ NavigationItem.cs      Shell 导航契约:Id / Title / VM 工厂
   │  ├─ ShellNavigation.cs     Shell 自带页面的 Id 常量(注册处与 Shell 命令共用)
   │  └─ IRefreshable.cs        页面支持 F5 刷新时实现
   ├─ Views/                    MainWindow、OverviewView、AboutView、SettingsView(.axaml)
   ├─ ViewModels/               MainViewModel、WorkspaceTabViewModel、OverviewViewModel、AboutViewModel、SettingsViewModel
   ├─ Services/                 FolderService(打开目录)、SettingsService(读写 settings.json、应用主题)、InstanceActivator(单实例)
   ├─ Diagnostics/              AvaloniaSerilogSink(Avalonia 日志转发到 Serilog)
   ├─ Styles/DesignTokens.axaml 浅色/深色主题色令牌
   └─ Assets/                   图标等资源
└─ tests/Plinth.App.Tests/
   ├─ TestAppBuilder.cs         headless 平台起真实 App
   ├─ Shell.cs                  摆好主窗口 + 真指针/真键盘输入的夹具
   ├─ ShellTests.cs             布局、导航、快捷键、日志桥接断言
   ├─ TestEnvironment.cs        测试开始前把数据目录指到临时文件夹
   ├─ TestDatabase.cs           每个测试一个临时 SQLite 文件
   ├─ LocalizationTests.cs      中英文 key 一致、占位符一致、用到的 key 都有定义
   ├─ RepoPaths.cs              测试里找仓库根目录
   ├─ InstanceActivatorTests.cs 单实例:第二次启动能通知第一个
   ├─ CsvExportTests.cs         CSV 导出:转义、防公式注入、文件内容
   ├─ CsvImportTests.cs         CSV 导入:解析、新增/更新、整批取消、导出再导入
   ├─ LogReaderTests.cs         日志页读取:解析、取最新文件最后 N 条、坏行跳过
   ├─ BackupTests.cs            备份与恢复:备份可用、坏文件被拒、启动时换库并保留旧库
   ├─ AuditTests.cs             审计字段:新增记创建人,修改记修改人
   ├─ UpdateCheckTests.cs       检查更新:版本比较、只接受 https、没配置不联网
   ├─ WindowPlacementTests.cs   窗口位置:屏幕外的位置不恢复
   ├─ BrandingTests.cs          品牌配置:默认值、字段限制、仅允许 HTTPS
   ├─ SettingsTests.cs          设置文件读写、坏文件处理、设置页保存主题
   ├─ DemoServiceTests.cs       示例模块的业务规则(真实数据库,不开窗口)
   └─ DemoListViewModelTests.cs 示例页的界面逻辑(真实数据库,不开窗口)
```

## 示例模块:示例数据(Demo)

示例模块默认关闭(见「运行」一节的 `IncludeSampleModules`)。`src/Plinth.App/Modules/Demo/` 是一个完整的增删改查示例,**不含任何业务**,只演示列表、表单、保存的写法。新模块照它抄；如需从产品中彻底移除示例，再删除组合根的 `services.AddDemoModule()` 与 `AddNotesModule()`。已有用户数据库时保留历史迁移，彻底删除实体或表必须通过新迁移演进，不能直接删除文件夹或数据库来绕过升级。

如果只需要单页编辑，可先看 [最小第二模块：便笺](docs/SECOND_MODULE.md)。它有独立表、服务、授权、命令贡献和未保存确认；便笺同样受 `IncludeSampleModules` 开关控制，组合根仅加一行 `AddNotesModule()`，无需在 Shell 加模块判断。

```text
Modules/Demo/
├─ DemoModuleRegistration.cs        注册服务和导航项(AddDemoModule),NavigationItem 放最后注册
├─ Data/DemoRecord.cs               一条记录是什么样,也是 demo_records 表的一行
├─ Data/DemoRecordConfiguration.cs  表结构配置(长度、唯一索引、版本号),AppDbContext 自动找到它
├─ Services/DemoService.cs          查询、保存、删除和校验规则(ViewModel 不直接碰数据库)
├─ ViewModels/DemoListViewModel.cs  搜索、选中、新增、保存、取消、删除(要二次确认)
└─ Views/DemoListView.axaml         左边列表(TableView),右边编辑表单
```

要点:
- 业务规则(必填、长度、编码不能重复)写在 `DemoService`,ViewModel 只显示它返回的错误原因,保存失败时用户填的内容保留。
- 数据库文件是数据目录下的 `plinth.db`(Windows 是 `%LOCALAPPDATA%\Plinth`)。环境变量 `PLINTH_DATA_DIR` 可以改数据目录,自动化测试用它隔离数据。
- 两个人同时改同一条:靠 `Version` 版本号,后保存的会被拒绝并提示刷新(见 `DemoService.SaveAsync`)。
- 查询、保存、删除都是异步的,不会卡住界面;换了搜索词或关闭页面时,还没读完的旧查询会被取消。
- 新模块的表:写一个 `IEntityTypeConfiguration<T>` 放在模块自己的 `Data/` 下就行,不用改 `AppDbContext`。
- 测试在 `tests/Plinth.App.Tests/DemoServiceTests.cs`(业务规则)和 `DemoListViewModelTests.cs`(界面逻辑),用真实的临时 SQLite 文件,不需要开窗口。

示例列表记住搜索词（最多 500 字符）、每页数量、排序及列显隐和宽度，关闭重开后恢复，工作区备份也包含这些偏好。搜索词以明文保存在本机工作区配置中。“恢复列表默认值”先处理未保存修改，允许后清空搜索、回到第一页、每页 50 条、编码升序，并恢复全部列及默认宽度；取消则保留草稿和偏好。偏好保存失败时显示提示，当前列表可继续使用，但重开仍读取上次成功保存的值。

列表右键菜单提供编辑、复制所选记录和删除。右键另一行会先确认当前未保存内容；取消时保留原表单且不打开菜单，保存后按刷新后的记录打开。空白处不操作上一条记录；表格获得焦点时可用 Shift+F10 或系统菜单键打开当前选择的菜单。编辑聚焦现有表单，复制使用已加载的记录值，删除仍需二次确认。保存、查询或未保存确认期间禁用记录操作；启用认证后，菜单按当前编辑／删除权限禁用相应入口，实际写入仍由服务重新授权。

二开列表可照 `DemoListView` 的普通 `ContextMenu` 和事件处理写法：先确定目标记录并等待现有选择确认，再打开菜单；异步确认后重新获取当前列表中的记录，以稳定的表格控件定位弹层。切换页面数据上下文或卸载视图时关闭菜单，不需要增加全局右键菜单框架。

“切换批量选择”进入独立的当前页多选模式。进入前处理未保存确认；取消则继续原编辑，进入后关闭编辑表单。点击行可添加或移除选择，也可用“全选当前页”或表格内的 Ctrl+A。界面始终显示“仅当前页已选择 N 条”，不会选择其他页或全部查询结果。

“复制所选记录为 CSV”及表格内 Ctrl+C 按当前页顺序复制编码、名称、备注，不含表头，沿用 CSV 转义和公式保护。复制的是已加载记录，不包括未保存输入；单条复制入口仍保留。批量模式不开放单条右键编辑／删除，新增会退出该模式。翻页、筛选、排序、刷新、切换标签或更换页面数据上下文会清空批量选择；查询和保存期间不可批量操作。退出批量模式后恢复普通单选编辑。

实现使用 `TableView` 继承的原生多选，ViewModel 仅保留当前页选中 ID。没有增加跨页选择令牌或批量删除；二开若需要这些写操作，应另外定义服务授权、确认、事务和审计语义。

表单的本地校验和服务业务校验都能定位到字段。重复编码显示在编码下方，修改编码后清除旧提示；版本冲突仍显示为表单错误并保留输入。服务使用带字段标识的保存结果，旧 `SaveAsync` 字符串返回接口保留，不能通过比较翻译后的错误文案来判断字段。

编辑已有记录时调用 `SaveValidatedAsync`，构造 `DemoSaveInput` 时传 `MustExist: true`，并携带打开表单时的 ID 和 Version；新增使用默认 `false`。这样即使原记录版本为 0，被删除后保存也会返回冲突，不会重新插入。旧 `SaveAsync` 为兼容保留按 ID 新增或更新的行为，新的编辑页面应照类型化入口编写。

编码原文保留大小写，保存入口会去掉首尾空白。唯一性只忽略英文字母 `A-Z` 大小写：`A1` 与 `a1` 重复，`Ä` 与 `ä`、不同 Unicode 组合形式仍分别保留。保存、CSV 和 XLSX 使用同一规则；CSV 按键更新已有记录时保留已有编码的拼写。内部 CodeKey 唯一索引负责兜底，界面和导出不展示该字段。默认编码排序、分页及 CSV 导出按同一比较键、再按 ID 排序；其他字段排序仍需在实际数据库验收。

CodeKey 由 `Data/CodeIdentity.cs` 计算，实体设置 Code 时同步更新；不要绕过属性直接改 backing field，或使用批量 SQL 单独修改 Code。当前模型不再使用 SQLite `NOCASE`，旧数据通过追加迁移回填比较键，原文和审计字段不变。搜索仍使用 EF 的转义 LIKE；SQLite 对 ASCII 忽略大小写，MySQL／PostgreSQL 的排序和比较行为不同，切换到服务器数据库后仍要按实际 Provider 验收搜索和排序。

历史库升级不会删除编码里的空白；应用输入和文件导入仍会 Trim，所以人为写入旧库的带首尾空白编码需要单独核对往返结果。外部创建的 UTF-16 SQLite 库会明确拒绝自动升级，不静默生成错误比较键。

## 数据库迁移

已包含 InitialSchema、NotesEntries、CodeIdentityKeys 迁移及固定模型快照。启动统一由 DatabaseInitializer 检查历史结构、先备份再建立基线或迁移，未知结构拒绝继续；不要删除现有数据库来规避升级。升级及回退步骤见 [升级说明](docs/UPGRADING.md)。后续改表才新增迁移：

```bash
dotnet tool install --global dotnet-ef        # 只需装一次
dotnet ef migrations add Demo_YourChange --project src/Plinth.App --context AppDbContext --output-dir Migrations
```

项目里有三个迁移上下文（SQLite／MySQL／PostgreSQL），不加 `--context` 会直接报 `More than one DbContext was found`。只生成 SQLite 迁移不能代表服务器库也已升级，服务器 Provider 的命令见下文。

也可以使用脚本,它会校验迁移名称并统一输出到 `src/Plinth.App/Migrations/`:

```powershell
pwsh scripts/add-migration.ps1 Demo_YourChange
```

迁移名建议带模块前缀。历史数据库基线、升级失败回滚与 WAL 一致性副本有 Python SQLite smoke；仍需执行真实 EF 迁移测试和 Windows 恢复演练。

### 选择 SQLite / MySQL / PostgreSQL

默认使用数据目录下的 `plinth.db`（SQLite）。要切换到服务器数据库，在数据目录创建 `database.json`：

```json
{
  "provider": "PostgreSql",
  "connectionString": "Host=localhost;Port=5432;Database=plinth;Username=plinth;Password=***"
}
```

`provider` 取值：`Sqlite`、`MySql`、`PostgreSql`。优先级为环境变量 > 数据目录 `database.json` > 安装目录 `deployment.json` > SQLite：

```powershell
$env:PLINTH_Database__Provider = "MySql"
$env:PLINTH_Database__ConnectionString = "Server=localhost;Port=3306;Database=plinth;User=plinth;Password=***"
```

服务器库需要用户先创建数据库和账号；应用只建表/迁移，不自动创建服务器或账号。MySQL 使用 Pomelo（MIT），PostgreSQL 使用 Npgsql；EF Core 固定在 9.0.x，因为 Pomelo 目前没有 EF Core 10 的 MIT Provider。连接串可能含密码，不要提交到仓库；应用日志与异常不会回显连接串。切换后重启程序生效，并且只应指向同一个逻辑数据库，不能把 SQLite 与服务器库当成同一份数据的自动同步副本。

服务器数据库的备份/恢复由服务器端负责，应用内“备份与恢复”页只支持 SQLite 文件；页面会明确显示该限制。SQLite 保留原有迁移历史，服务器 Provider 使用独立迁移上下文、迁移 ID 和模型快照。业务 CRUD 仍使用 `AppDbContext`，启动迁移由 `DatabaseInitializer` 选择对应迁移上下文，不在服务器库上执行 SQLite 专用 SQL。

修改业务模型后，分别生成并审查各 Provider 的迁移，不能只生成 SQLite 迁移就宣称服务器库也已升级：

```powershell
dotnet ef migrations add ChangeName --project src/Plinth.App --context AppDbContext --output-dir Migrations
dotnet ef migrations add ChangeName --project src/Plinth.App --context PostgreSqlMigrationContext --output-dir Migrations/PostgreSql
dotnet ef migrations add ChangeName --project src/Plinth.App --context MySqlMigrationContext --output-dir Migrations/MySql
```

服务器设计时工厂使用不含秘密的本地示例连接串，MySQL 使用固定设计时服务器版本，不自动连接探测。审查生成结果是否符合实际部署版本，再对专用测试数据库运行 Provider 集成测试。本轮独立迁移集尚未编译或在真实服务器执行，不能据源码声明正式支持。

Provider 集成测试会执行真实迁移并写入一条带 `PLINTH-INT-` 前缀的记录，因此只能指向可丢弃、可清理的专用测试库。连接串之外还必须由操作者显式设置 `PLINTH_TEST_DATABASE_ISOLATED=true` 和 `PLINTH_TEST_DATABASE_PURPOSE=plinth-integration`；缺任一变量时测试跳过，不会把 Skip 算作通过。测试结束按本次唯一前缀清理记录；数据库建库、账号创建和共享库隔离由部署方负责，测试不会自动创建或删除数据库。Provider 异常不会把原始消息或连接串写入测试结果。旧试验版本若在服务器库写入了 SQLite 的迁移 ID，启动会拒绝该历史；先备份并人工制定迁移方案，不自动清空数据库或改写迁移历史。

也可使用 `pwsh scripts/add-migration.ps1 ChangeName`（默认 SQLite），或加 `-Provider PostgreSql`／`-Provider MySql`。脚本显式选择上下文及输出目录，避免多个上下文时 EF 无法确定目标。

## 新增一个页面

1. 在 `Views/` 新建 `XxxView.axaml`(`UserControl` + `x:DataType`),只写布局和绑定。文件名必须和 ViewModel 去掉 `Model` 后缀一致：`XxxViewModel` 由 `ViewLocator` 自动找 `XxxView`，名字不一致会在打开页面时报找不到视图。
2. 在 `ViewModels/` 新建 `XxxViewModel.cs`,继承 `ViewModelBase`,用 `[ObservableProperty]` 和 `[RelayCommand]` 写状态和命令。
3. 需要业务逻辑时,在 `Services/` 加一个普通类,通过构造函数注入使用。
4. 在 `ServiceCollectionExtensions.cs` 里注册 ViewModel，然后加导航项：
   ```csharp
   services.AddTransient<XxxViewModel>();
   services.AddSingleton<NavigationItem>(provider => new NavigationItem(
       "your.page.id",
       Loc.Get("your.page.title"),
       () => provider.GetRequiredService<XxxViewModel>())
   { Section = NavigationSection.Business, Order = 500, TitleKey = "your.page.title" });
   ```
   标题文字放进两个 resx；`TitleKey` 让切换语言时侧栏标题一起刷新。`Section`／`Order`／`Icon` 只影响侧栏位置和图标，省略时默认 Business／500。本文件的「项目结构」和 `MainViewModel` 都不用改——Shell 注入 `IEnumerable<NavigationItem>` 自动生成导航。
   页面注册了但不想占侧栏一行(比如只能从菜单或快捷键进的「关于」),再加一个 `ShowInMenu: false`。

含页面查询的 ViewModel 实现 `IDisposable`，由 Shell 在关闭单个标签、全部标签或释放工作区时调用。`Dispose()` 应幂等，取消本页查询、清除局部结果，并阻止迟到请求更新页面；活跃查询在自己的 `finally` 中释放 CTS。已交给任务中心的导入／导出继续执行。

视图的 `DetachedFromVisualTree` 可能只是切换标签，不代表永久关闭，不能直接调用页面 `Dispose()`。日志页的 `PageClosed()` 只取消读取，切回时由 `PageOpenedAsync()` 恢复被中断的查询；许可证页也允许切回重读。对于 Shell 负责释放的页面，导航工厂使用 `ActivatorUtilities.CreateInstance<YourViewModel>(provider)` 注入依赖，避免根容器持续持有可释放的瞬态页面；日志、诊断和许可证页已有示例。

## CSV 导入导出、日志页、备份与恢复

- **CSV 导出**:按当前搜索条件，在同一个数据库读取事务内每页读取最多 200 条，逐行写到 `exports` 下的 UTF-8 BOM 文件，完成后才发布目标文件。公式前缀加单引号保护，原有单引号另行转义，便于导入往返保留文本。
- **CSV 导入**:选择文件后核对编码、分隔符和编码／名称／备注源列序号，预览最多 20 行后确认。预览对完整文件计算 SHA-256，导入校验同一份重读字节；文件改变后需重新预览。预览错误可独立导出报告。完整校验通过才在同一事务内新增或更新；上限 5000 行、5 MiB。导入导出进入任务中心，关闭页面继续执行；在任务中心核对完成结果后，使用 F5 刷新列表。
- **XLSX**:默认构建不注册此可选页面，CSV 保持可用。完成 Office 兼容、内存 POC 和许可验收后，用 `-p:PlinthEnableXlsx=true` 构建／运行，或用 `publish.ps1 -EnableXlsx` 打包。启用后的页面支持文本表格预览、校验、确认导入、取消和导出；导入上限 5000 行、5 MiB，拒绝公式、宏、外部关系和非文本单元格。ClosedXML 仍在统一依赖图中，默认关闭功能不免除其锁包和许可检查。Office 兼容（真实 Excel 保存的文本工作簿可导入、外部链接与 `.xlsm` 被拒）和 5000 行内存 POC（61 MiB）已执行；宏内容的真实 Office 样本受本机 VBA 工程访问设置阻塞，许可为 MIT 但终审仍属发布决策，因此默认仍关闭。
- **日志与错误中心**:默认显示技术日志，可切换任务日志、安全审计或技术与任务日志。“查看警告和错误”预设合并技术与任务事件，排除审计。跨文件查询支持时间、等级、模块、任务、错误码及关联 ID 筛选，通过按钮复制详情；Ctrl+F 聚焦搜索，“清空筛选”回到无条件的技术查询，手动清理读取设置的保留天数。意外错误弹窗按关联 ID、任务中心按任务 ID 跳转，替换旧筛选，保留对应类别的信息级上下文。没有匹配记录时明确显示空结果，不保证已经过保留期或尚未落盘的日志可查。
- **备份与恢复**:ZIP 包含一致性数据库副本、用户设置、工作区偏好和 SHA-256 manifest。预览并确认后暂存，下次启动恢复，保留安全副本与回滚记录。旧 ZIP 缺少工作区偏好时会清除当前偏好、回到默认布局；不接受裸 `.db` 直接恢复。部署身份配置与凭据不包含在包中。

CSV 读取使用文件流，不复制完整文件为字节数组或字符串。预览只保留表头和前 20 行，仍严格解码并校验完整尾部；正式导入再次核对 SHA-256。读取各遍按实际字节报告进度，每遍会从零开始；数据库处理等无法可靠计算比例的阶段显示不确定进度。导出在同一读取事务内计数，按实际写出行数报告比例，文件发布完成才算成功。

任务中心保留 CSV 新增／更新／写出数量和文件位置，关闭业务页后仍可查看。整批拒绝显示新增、更新均为零；错误行数按行去重，截断时显示“至少”，无法判断时显示“未知”。默认 CSV 导出及自动错误报告的位置限制在 `exports`；通过文件选择器明确选择的 CSV 报告位置也会保留。路径仍须通过长度、扩展名、控制字符及符号链接检查，以纯文本展示，不自动打开；结果随当前进程历史保留，不写入任务日志或诊断包。

显式启用 XLSX 后，确认导入及导出同样保留任务结果：导入显示新增／更新或整批拒绝摘要，导出显示实际写出行数和所选目录下的 `.xlsx` 位置。关闭页面后仍可在任务中心查看；预览只是校验，不报告为已导入。数据库处理和 ClosedXML 保存等无法计算比例的阶段显示不确定进度。该接线不代表 XLSX 兼容性、内存或许可 POC 已通过。

XLSX 导入按完整预览快照生成中转 CSV，并在写出时计算实际字节摘要。生产确认期间中转文件若被替换，导入会失败，不会改为导入未预览的内容。

创建备份时，先选择目标，再核对包含文件、估算大小和排除项，点击确认才入队；生产确认只在真正创建时出现。预览不会创建目录、临时包或执行数据库 checkpoint。大小来自当前文件元数据，主库大小不包含 WAL，最终包使用一致性快照，实际大小会因已提交数据、规范化和压缩变化。执行时重新检查目标，拒绝覆盖现有文件及源数据库、配置和待恢复包。压缩阶段按已读取的工作副本字节报告比例，快照、归档收尾和发布不能据此估算剩余时间。发布前取消清理本次临时内容，发布后不撤回文件。

启用认证后，技术和任务日志要求 `logs:read`，安全审计要求 `audit:read`；打开共用日志目录或包含日志的数据目录要求两者兼有，清理另需 `settings:manage` 和生产确认。离线模式继续允许本地操作。换号或失权后页面清除旧结果，复制立即复核；这些检查不代替 OS 文件权限。诊断预览和导出都需 `diagnostics:export`，包含日志时另需 `logs:read`，始终排除安全审计；换号或撤权后旧预览不能导出。详见 [安全模型](SECURITY_MODEL.md)。

日志文件无法写入时，进程保留最近 128 条时间、级别及有效 Guid 关联／任务 ID，并向已有 stderr 输出一次固定故障提示；不保存消息、异常或业务字段。内存记录退出即丢失，不进入日志页或诊断包，也不自动补写磁盘。Windows 无附加终端时可能看不到 stderr 提示。日志目录的 IO／权限故障允许降级，数据目录不可用仍中止启动；这不保证审计持久化或原生崩溃后可恢复。

恢复暂存时可在任务中心取消：复制、哈希、解压和发布前均检查令牌；提交前取消清理本次临时内容，保留源备份与已有待恢复包。包已发布成功后再取消不会撤回它，任务保持成功，下次启动仍按该包恢复。

恢复预览先在数据目录建立临时私有快照，清单与确认用 SHA-256 均读取该快照，预览结束即清理。源文件可以位于只读目录；数据目录需要容纳压缩包快照及解压校验文件。预览后源路径若换成其他内容，确认时会拒绝暂存，需重新预览。该流程避免多次打开路径导致显示内容与实际确认内容不一致，不证明备份来源可信。

业务代码继续使用 `ILogger<T>` 和固定消息模板，不拼接用户输入；只记录安全标识，不记录编码、名称、备注、请求正文或完整文件路径。文件写入前的 `RedactingLogSink` 只保留诊断／审计白名单标量字段，未知业务结构会被丢弃；异常保留类型和不含文件路径的有界方法调用帧。新增日志字段需明确语义并补测试，不能借已有白名单字段存业务正文。当前检查只覆盖直接日志插值，不能证明间接构造的模板安全；完整规则见 [安全模型](SECURITY_MODEL.md)。

## 任务手动重试

任务中心的“重试任务”只对模块明确允许的失败任务启用，不会自动重试。每条失败记录最多发起一次新尝试；新尝试获得独立任务 ID，保留原失败，通过“上一次尝试／下一次尝试”查看关联记录，新尝试再次失败后可继续手动重试。历史最多保留当前进程的 200 项，旧关联记录可能被清理，重启不恢复任务。

全局任务最多同时执行 2 个，其余保持“等待”；等待期间也能取消，取消后不进入业务执行。失败或取消会让出执行槽位，退出时停止入队并取消、等待全部活动和排队任务。并发上限与 200 条历史容量是两个不同限制，不提供跨进程调度或严格 FIFO 承诺。

当前接入的是 Demo CSV 导出。重试保留最初的搜索条件，读取重试时的数据库内容，并生成新文件，不覆盖之前的导出；每次仍经过服务权限和生产确认。拒绝确认按取消处理，取消任务和已成功任务不提供重试。导入、保存、删除、恢复及诊断包导出没有启用重试，需按失败提示核实结果。

二开时，只有确认可以重新执行的操作才使用 `TaskService.Enqueue(..., allowRetry: true)`。委托应捕获稳定的服务与输入副本，每次自行调用服务授权及确认；不要捕获已关闭页面的表单、一次性预览、流或 DbContext。任务中心只负责创建新的执行，不证明操作幂等，也不绕过服务检查。应用退出后停止接收新任务，并等待执行、完成通知和取消回调清理。

需要保留导入导出摘要时使用 `EnqueueWithResult`，回调返回固定类型的 `TaskOutcome`，例如 `TaskOutcome.Import(added, updated)`；原 `Enqueue` 仍可用。结果应描述已经提交的操作，提交前取消抛出 `OperationCanceledException`，成功提交后不要再用迟到的取消改写结果。不要把业务正文、原始异常或任意路径放进任务摘要。

## 审计字段、错误提示、检查更新、托盘与窗口位置

- **审计字段**:业务实体继承 AuditableEntity，AppDbContext 填写创建／修改时间与人。远程登录后使用已验证 subject，离线默认使用 OS 用户。新增实体字段必须生成迁移，不应删除数据库绕过升级。
- **未处理异常**:界面线程异常写入本地日志，弹窗只显示安全说明、下一步建议、错误码和关联 ID，不展示原始异常消息或机器路径。此处理不保证原生崩溃后可恢复运行。
- **检查更新**:关于页的「检查更新」按钮。默认关闭;管理员在 `settings.json` 里填 `"UpdateFeedUrl": "https://.../feed.json"` 才启用。只在点击后请求，发现新版本时显示版本、说明和地址；点击“打开下载地址”才交给默认浏览器，不自动安装。更新源和下载地址只接受 HTTPS，拒绝含账号或片段的地址。重新检查立即清除旧下载入口，关闭页面取消本页检查并忽略迟到结果。文件格式:`{ "version": "1.2.0", "url": "https://.../setup.exe", "notes": "更新说明" }`。浏览器及 Windows 行为仍待实测。
- **托盘图标**:点一下显示窗口,右键菜单有「显示窗口」「退出」。关闭窗口仍是退出程序。Linux 桌面没有托盘时只写一条警告日志。
- **窗口位置**:关闭时记下位置、大小、是否最大化(存在 `settings.json` 的 `Window`),下次启动恢复。保存的位置如果已不在任何屏幕上(换了显示器),就用默认位置。
- **设置页目录**:显示数据目录和日志目录,可以直接打开,方便排查问题和收集日志。

## 单实例

程序默认同时只能运行一个(两个进程同时写同一个 SQLite 文件容易出问题)。再次启动时,新进程会通知已经在运行的那个把窗口带到前台(最小化的会还原),然后自己退出。

- 判断依据是数据目录:数据目录不同(设了不同的 `PLINTH_DATA_DIR`)就算不同的实例,互不影响,自动化测试和便携使用靠这一点。
- 通知走本机的命名管道,只允许同一个 Windows 用户连接。
- 代码在 `Services/InstanceActivator.cs` 和 `Program.cs` 开头。

## 界面文字(中文 / 英文)

界面文字放在 `src/Plinth.App/Resources/`:`Strings.resx` 是英文(默认),`Strings.zh-CN.resx` 是中文。**两个文件的 key 要一一对应,`{0}` 占位符也要一致**,`LocalizationTests` 会检查,漏了测试就失败。

- XAML 里写 `Text="{loc:T demo.new}"`,文件顶部加 `xmlns:loc="using:Plinth.App.Localization"`。
- 代码里写 `Loc.Get("demo.new")`;带参数的写 `Loc.Format("demo.status.count", 3)`(资源里写 `共 {0} 条`)。
- key 的写法:`模块.位置.动作`,例如 `demo.col.code`。
- 左侧「设置」页可以切换语言,**切换后界面立即生效**(已打开的页面、导航和状态栏一起刷新)。默认是中文。
- 技术日志、给开发者看的提示(例如 `ViewLocator` 找不到页面)不翻译,仍是中文。
- 找不到 key 时界面显示 `[key]`,同时写一条警告日志。

新增页面的步骤:先在两个 resx 里各加文字,再在 XAML / 代码里引用。

## 发布

```powershell
pwsh scripts/publish.ps1
```

验收和发布脚本会实际检查解释器版本，选择 Python 3.8 或以上；系统的 `python` 仍为 Python 2 时会继续寻找 `python3`，Windows 还会尝试 `py -3`。也可以传 `-PythonExecutable "解释器完整路径"`；明确指定的解释器无效时直接报错，不改用其他版本。

生成在 `artifacts/` 下:
- `Plinth-<版本>-win-x64.zip`:便携包,解压即用(数据仍写在 `%LOCALAPPDATA%\Plinth`,不写程序文件夹)。
- `Plinth-Setup-<版本>.exe`:安装包。需要先安装 [NSIS](https://nsis.sourceforge.io/)(命令行里有 `makensis`),没装就只生成 ZIP。安装到当前用户自己的目录 `%LOCALAPPDATA%\Programs\Plinth`,不需要管理员权限;**卸载不会删除用户数据**。

升级前先保存业务修改并完全退出应用。安装器交互确认后调用旧版 `--backup-before-upgrade` 创建一致性 ZIP，保留到 `%LOCALAPPDATA%\Plinth-Upgrade-Backups` 并显示位置；失败即停止覆盖，不支持命令的旧版、认证或生产确认无法完成时也拒绝。静默升级仍一律返回 6。同一程序目录的所有实例都需退出，安装／卸载过程中不能启动新实例。随程序交付的 `plinth.install.lock` 用于协调，不要删除。卸载只删除发布清单中的程序文件，未知文件与非空目录保留；自定义数据目录、权限限制及待验收事项见 [升级与回退](docs/UPGRADING.md)。

版本号只在 `Directory.Build.props` 里改一处。

## 用户设置

左侧「设置」页可以切换主题(跟随系统、浅色、深色),改了立即生效并自动保存,不用点保存按钮。设置存在数据目录的 `settings.json` 里:

- 文件损坏(手改坏了)时,程序用默认设置启动,并把原文件改名为 `settings.json.bad` 留着,不会删掉。
- 值不认识(比如主题写成了别的)时换回默认值。
- `SchemaVersion` 记录文件格式的版本,以后加字段时用它升级旧文件。字段只增不改名。

设置包含主题、语言、地区格式、时区、窗口位置、远程健康检查及日志保留。地区格式独立于界面语言，不推断货币；时间点按所选时区显示，数据库仍存 UTC。语言切换立即生效；地区、时区和日志轮转配置重启后应用。schema 1 升级到 2，未知 schema 将整份原文件保留为 `settings.json.unsupported`，再以默认值启动。工作区未知根版本或布局版本也将整份原件保留为 `workspace-preferences.json.unsupported`，回到默认布局与导航偏好。

保留副本不会覆盖已有 `.unsupported` 或 `.bad`。如果保留原件失败，当前服务实例阻止后续保存，避免默认值覆盖未知格式；应先核对并妥善保管冲突文件，再重启应用。备份中的未知设置／工作区版本仍会被拒绝，不通过规范化悄悄丢弃未来字段。

用户编辑通过 `SettingsService.ChangeAsync` 授权并串行保存，成功后才发布新设置；拒绝、取消或落盘失败时回填原值并提示。主题和语言／地区等外观设置使用 `settings:appearance`，其余设置使用 `settings:manage`，生产环境敏感修改需确认。登录操作本身不受设置编辑权限限制。

## 远程连接

设置页支持 HTTPS 服务地址和健康检查路径，默认关闭；点击后才请求，不跟随重定向。远程 HTTP 总超时 30 秒、单次 8 秒，只有 GET/HEAD 允许有限重试。返回 2xx 仅表示该健康接口响应成功。

主窗口状态栏显示用户、运行环境、活动任务数及当前连接配置最近一次手动健康检查结果。点击健康状态打开设置，点击任务数展开右侧任务面板。状态栏按秒读取本地快照，不自动发起健康检查或刷新登录令牌；登录状态变化也在本地刷新中反映。

健康结果只保存在当前进程，地址或路径不匹配时显示“尚未检查”，迟到响应不会覆盖更新的检查。提示中包含上次检查时间，不能据此判断持续在线或所有业务接口可用。活动任务进度全部已知时才显示平均百分比，不用于估算剩余时间。

部署目录可放非秘密的 `deployment.json`，默认 Development 且认证关闭；修改需重启。配置示例：

```json
{
  "Profile": "Production",
  "Authentication": {
    "Enabled": true,
    "Authority": "https://identity.example.com",
    "ClientId": "plinth-desktop",
    "Scope": "openid profile offline_access",
    "RedirectUri": "http://127.0.0.1:7890/callback/"
  }
}
```

Profile 仅接受 Development、Test、Production；生产窗口标题有标识，保存／删除、导入／导出、备份／恢复、诊断与敏感设置有额外确认。启用认证后显示浏览器登录、刷新和退出按钮；refresh token 仅保存到 OS 凭据库。权限依赖已验证的 permission claim，包含 `demo:save`、`demo:delete`、`demo:import`、`demo:export`、`backup:export`、`backup:restore`、`diagnostics:export` 及上述设置权限。Demo 仍使用本地 SQLite。完整边界见 [安全模型](SECURITY_MODEL.md)，可复制 `deployment.example.json`；真实 IdP、OS 凭据库和 Windows UI 尚未验收。

二开敏感服务可照 Demo 使用 `ExecuteConfirmedAsync`：确认前授权，确认后复核原会话及当前权限，回调内只写业务逻辑。需要先在界面确认、再排队的操作照备份页使用 `PrepareConfirmedAsync`，将返回的确认对象交给队列执行；出队会再次复核，不重复弹窗。确认期间注销、换号或重新登录后，应重新发起操作；`.confirm` 审计只代表确认准备结果。

部署环境变量可以覆盖文件值，例如 `PLINTH_Profile=Test`、`PLINTH_Authentication__ClientId=plinth-test`；前缀为 `PLINTH_`，嵌套键使用双下划线。未指定字段保留文件值，覆盖后重新校验，修改进程环境需重启。环境变量由部署管理员控制，不接受用户设置或备份中的覆盖，不保存密码或令牌。

技术和任务日志默认 `Information`。部署文件的 `MinimumLogLevel` 或 `PLINTH_MinimumLogLevel` 接受 `Verbose`、`Debug`、`Information`、`Warning`、`Error`、`Fatal`，大小写必须一致；修改后重启。提高技术阈值不会关闭安全审计，也不改变写入字段的脱敏规则。

要接入远程示例，在同一部署文件增加 `"DemoApiBaseUrl": "https://api.example.com/api/v1/"`，并完整配置 `Authentication.Enabled=true`。地址必须为 HTTPS、以 `/` 结尾，不含账号、查询参数或片段。空值保持默认离线行为，不注册远程页面。远程页面是独立数据源，本机示例、便笺和备份仍使用本机库；不会把远程结果缓存到 SQLite。API 契约、权限和服务端要求见 [远程示例接入](docs/REMOTE_DEMO.md)。

远程模块注册、首次 ViewModel 构造、View 查找或同步视图构造（含构造期间的 XAML 初始化）失败时，本次运行禁用其入口，主窗口保留模块名、关联 ID 和日志入口，其他页面继续使用。视图失败后异步移除标签并释放页面，避免在渲染回调中修改标签集合。部署配置、核心服务及数据库错误仍会传播；不覆盖视图构造完成后的绑定、事件或异步业务故障，也不是适用于所有模块的运行时卸载机制，带本地实体迁移的模块不能直接照搬该隔离方式。相关 C# 和桌面验收尚未执行。

## 品牌配置

发布后的程序目录可以放一个 `branding.json`。它是可选文件,不存在时使用 Plinth 默认值:

```json
{
  "ProductName": "我的管理台",
  "Description": "面向内部团队的桌面管理工具",
  "SupportUrl": "https://example.com/support",
  "SupportText": "技术支持"
}
```

`ProductName` 会用于窗口标题和关于页名称,`Description` 会替换关于页描述,`SupportText` 会显示在关于页。填写 `SupportUrl` 后,关于页会出现打开支持页面的按钮,它只接受 `https`。字段为空、过长或格式不对时回退默认值。仓库里的 `branding.example.json` 是模板,复制并改名为 `branding.json` 即可。

还可配置 `LogoFile`、`IconFile` 和 `AccentColor`；图像随显式指定的品牌配置打包，详情见 [更换品牌](docs/BRANDING.md)。

## 键盘快捷键

| 快捷键 | 作用 |
|---|---|
| `Ctrl+W` | 关闭当前标签 |
| `Ctrl+N` / `Ctrl+S` | 当前页面新增 / 保存 |
| `Ctrl+F` | 聚焦当前页面搜索框 |
| `Delete` / `Esc` | 非文本输入区进入删除确认 / 取消编辑；脏数据仍经过保存确认 |
| `F5` | 刷新当前页面(页面实现 `IRefreshable` 时) |
| `F1` | 打开当前页离线帮助 |
| `Ctrl+K` | 快速打开页面（输入过滤，Enter 打开，Esc 关闭） |
| `Alt+1..9` / `Alt+0` | 打开第 1..9 个主页面 / 设置 |

固定手势在 `Navigation/AppShortcuts.cs` 集中维护，菜单的 `InputGesture` 直接引用这份定义，窗口在未处理的冒泡按键事件中分发。页面不再重复拦截这些快捷键；模块通过 `CommandMenuContribution` 的普通 DI 集合绑定当前页命令，照 `DemoModuleRegistration` 注册即可。文本框保留复制、粘贴、剪切、全选和撤销等标准按键行为，Delete／Esc 不转发到页面删除或取消。

创建窗口时检查固定手势是否重复，数字键盘和主键盘的等价加／减／小数点键也算冲突；同一动作不能重复定义，Ctrl+C/V/X/A/Z/Y 留给文本控件。构造 Shell 时还检查同一页面是否重复贡献同一新增／保存／删除／取消动作，错误明确指出页面和动作；不同页面复用 Ctrl+S 等标准手势是正常行为。数据菜单中的普通页面命令不占独立快捷键。首版没有用户自定义快捷键编辑器，扩展时仍修改这份固定定义，不增加新的快捷键注册框架。

## 已知限制 / 尚未实现

真实第三方 IdP、正式 CA 签名与 OS 级 DPI／IME／读屏人工矩阵仍未完成；安装升级、0.1.0→0.1.1 备份演练、本机标准用户首启与独立 300MB 卷磁盘满（启动/CLI 备份）已实测，边界与恢复行为见实现状态。完整清单见 [实现状态](docs/IMPLEMENTATION_STATUS.md) 和 [UI 验收](docs/UI_ACCEPTANCE.md)。不得将静态扫描、测试源码存在或 workflow 已配置记为运行通过。

按规范章节整理的剩余功能、条件项和验收阻塞见 [需求交付核对表](docs/REQUIREMENTS_STATUS.md)。系统 Toast 已接入并进入 Windows 通知历史；横幅物理点击和勿扰策略下的人工体验仍待验收，应用内任务提示不能替代这两项。
