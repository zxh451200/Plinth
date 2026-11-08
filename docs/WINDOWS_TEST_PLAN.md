# Windows 完整测试方案

本文是 Plinth Windows 验收的主方案。它定义测试环境、执行顺序、测试内容、通过标准和证据要求。源码存在、静态脚本通过或 CI 配置存在，都不能替代本文要求的真实 Windows 结果。

## 一、测试环境和准备

使用干净的 Windows 11 测试机。若产品声明支持 Windows 10，必须另外使用受支持的 Windows 10 版本执行同一矩阵。

测试开始前记录：

- Windows 版本、补丁级别、系统架构；
- CPU、内存、磁盘类型及剩余空间；
- .NET SDK `dotnet --info` 输出，必须符合 `global.json`；
- PowerShell 7、Python 3.8+、NSIS 版本；
- 显示器数量、分辨率、缩放比例和系统字体；
- Windows 显示语言、输入法、应用语言和主题；
- 测试数据目录、源码归档标识或构建版本；
- 测试人员、开始时间和结束时间。

创建专用测试目录，并确保不使用开发者真实数据：

```powershell
$env:PLINTH_DATA_DIR = "$pwd\artifacts\windows-test-data"
$env:PLINTH_IncludeSampleModules = "true"   # 功能验收需要示例数据/便笺
New-Item -ItemType Directory -Force $env:PLINTH_DATA_DIR
```

测试数据至少准备三组：

1. 空数据目录，用于首次启动、迁移和默认值测试；
2. 普通数据目录，包含 Demo、Notes、收藏、最近页面、工作区布局和日志；
3. 升级数据目录，使用旧版本生成或准备旧 schema、旧数据库和旧设置。

测试开始前备份测试目录。涉及安装、升级、卸载和恢复的场景都使用副本，不能直接修改唯一数据。

## 二、执行门禁

所有命令在仓库根目录执行。前一步失败时停止后续步骤，保留原始输出。

### 1. 工具和静态门禁

```powershell
dotnet --info
pwsh --version
python --version
makensis /VERSION

python scripts/check_source_contracts.py
python scripts/integration_checks.py
python -m unittest discover -s scripts -p "test_*.py"
```

通过标准：

- SDK 与 `global.json` 一致；
- 源码契约和资源检查无错误；
- Python 测试失败为零；
- 缺少 PowerShell 的 skipped 项不能记为通过；
- 许可证测试使用合成材料时，结果只能记为工具逻辑通过。

### 2. 依赖还原和锁文件

```powershell
dotnet restore Plinth.sln --configfile scripts/nuget.config
dotnet restore src/Plinth.App/Plinth.App.csproj -r win-x64 `
  --configfile scripts/nuget.config --locked-mode
python scripts/release_checks.py locks
```

通过标准：

- 真实还原成功；
- 应用项目的 `packages.lock.json` 存在且可用于 locked restore；
- 传递依赖、版本、SHA512、许可证和安全审计结果已复核；
- 不手工创建、修改或伪造锁文件；
- runtime、native、font、icon、NSIS 等 NuGet 自动发现范围外的材料已另行核对，或明确记录为未覆盖。

### 3. 格式、编译和自动化测试

```powershell
dotnet format Plinth.sln --verify-no-changes
dotnet build Plinth.sln -c Release --no-restore
dotnet test Plinth.sln -c Release --no-build `
  -p:TestingPlatformDotnetTestSupport=true `
  --logger "trx;LogFileName=plinth-tests.trx"
```

测试范围包括：

- `DemoServiceTests`、`DemoFieldValidationTests`、`DemoEditingProtectionTests`；
- `DemoPagingTests`、`DemoListPreferencesTests`、批量选择和右键菜单测试；
- CSV 导入、导出、预览、错误报告、取消和跨页测试；
- 备份、恢复、恢复取消、版本回退和 WAL 一致性测试；
- 任务并发、取消、退出、手动重试和任务结果测试；
- 权限、审计、生产确认、换号、失权和迟到结果测试；
- 日志、诊断、许可证、更新检查和页面生命周期测试；
- `AvaloniaFact`、Shell、导航、绑定、快捷键和窗口关闭测试；
- 真实 EF/SQLite 测试；
- 默认构建中不应误编译性能 POC。

通过标准：

- `Release build` 成功；
- 全量测试成功；
- TRX、控制台输出和失败堆栈已归档；
- 不能用删除测试、跳过失败测试或改成静态检查来获得通过。

## 三、性能和可选构建

### 1. SQLite/CSV 性能 POC

```powershell
dotnet test tests/Plinth.App.Tests/Plinth.App.Tests.csproj `
  -c Release -p:PlinthRunPoc=true `
  --filter "FullyQualifiedName~PerformancePocTests" `
  --logger "console;verbosity=detailed"
```

验证内容：

- 两批各 5000 行 CSV 导入，建立 10000 条真实 SQLite 数据；
- 第 1、100、200 页查询；
- 中文筛选；
- 一次保存和版本变化；
- 10000 条全量导出；
- 独立 5000 条新增、更新、导出并导入另一数据库；
- 中文、50 位前导零编码、逗号、引号、换行和公式式文本；
- 阶段耗时、工作集、托管内存、累计分配和 GC 采样。

结果要求记录原始输出。阶段采样不代表桌面程序峰值、首屏耗时或性能达标。

### 2. XLSX 变体

```powershell
dotnet test tests/Plinth.App.Tests/Plinth.App.Tests.csproj `
  -c Release -p:PlinthEnableXlsx=true `
  --filter "FullyQualifiedName~Spreadsheet" `
  --logger "console;verbosity=detailed"
```

另测：

- 中文和长数字；
- 日期和文本类型；
- 公式、宏、外部关系和损坏文件拒绝；
- 预览后文件替换拒绝；
- 选择器关闭、换页、重挂和迟到结果；
- 取消、任务结果和跨页数据；
- Office 实际打开和保存；
- 内存和文件大小。

XLSX 只有在兼容性、内存、许可和发布包检查全部通过后才允许启用。

## 四、发布构建和产物

### 1. Release 编译

```powershell
dotnet build Plinth.sln -c Release --no-restore
```

这一步确认整个解决方案、C#、Avalonia XAML、源生成器、迁移和默认条件编译可以构建。它不是最终交付包。

### 2. Windows 发布目录

```powershell
dotnet publish src/Plinth.App/Plinth.App.csproj `
  -c Release -r win-x64 --self-contained true --no-restore `
  -o artifacts/publish/win-x64
```

发布目录必须包含应用入口、运行时依赖、资源、迁移和 `plinth.install.lock`，不能包含测试程序集、源码、测试夹具和开发配置。

### 3. 候选 ZIP 和安装器

```powershell
pwsh scripts/acceptance.ps1 -Package `
  -PythonExecutable (Get-Command python).Source
```

需要补充许可材料时：

```powershell
pwsh scripts/acceptance.ps1 -Package `
  -SupplementalComplianceFile .\reviewed-materials\inventory.json `
  -PythonExecutable (Get-Command python).Source
```

检查：

- ZIP 和 EXE 文件；
- `THIRD_PARTY_NOTICES.md`；
- `sbom.cdx.json`；
- 安装清单；
- `plinth.install.lock`；
- 每个产物的 SHA-256；
- 版本、RID 和 self-contained 配置；
- 发布目录不含测试和开发文件。

## 五、功能验收

### 1. 启动和命令行

- 空数据目录首次启动；
- 已有数据目录启动；
- `--version` 输出、退出码和不启动窗口；
- 未知参数、重复参数和额外参数；
- stdout/stderr 单独及同时重定向；
- 主窗口已运行时再次执行版本查询；
- 无显示器托管入口；
- 数据目录、日志目录和配置目录副作用。

### 2. 工作区、导航和窗口

- 概览、Demo、Notes、设置、日志、诊断、帮助和关于页面；
- 打开、切换、关闭标签；
- 单个页面关闭、全部页面关闭和窗口退出；
- 未保存确认的保存、放弃和取消；
- 页面关闭期间异步请求；
- 迟到结果不能回写旧页面；
- 最近页面和收藏；
- 布局、侧栏、右侧详情、任务面板保存和恢复；
- 布局重置不清除收藏和最近记录；
- 多显示器、最大化、还原、窗口位置恢复。

### 3. Demo 列表和表单

- 搜索编码和名称；
- `%`、`_`、反斜线等搜索字符；
- 分页、排序、降序和空结果；
- 搜索词、分页、排序、列显隐和列宽重启恢复；
- 恢复默认值；
- 偏好保存失败提示和重试；
- 新增、编辑、保存、取消和删除；
- 必填、长度、非法 UTF-16、重复编码和版本冲突；
- 旧页面删除已被修改记录时必须保留新版本；
- 右键菜单、批量选择、当前页复制；
- 中英文和长标签。

### 4. CSV、任务和错误中心

- UTF-8、UTF-16、GBK/GB18030 和 BOM；
- 分隔符和列映射；
- 引号、逗号、换行和空行；
- 预览只显示有限行但完整校验文件；
- 预览后换文件拒绝；
- 公式式文本保护和往返；
- 5000 行/5 MiB 上限；
- 整批拒绝不写数据库；
- 导入新增、更新、导出和错误报告；
- 页面关闭后任务继续；
- 排队取消、运行取消、任务重试；
- 任务结果数量、文件位置和关联日志；
- 错误中心筛选、复制和关联日志跳转。

### 5. 设置、权限和审计

- 主题、语言、地区、时区和密度；
- 设置保存成功、拒绝、取消和落盘失败；
- 远程配置和健康检查；
- 登录、刷新、退出、换号、失权；
- 保存、删除、导入、导出、备份、恢复和诊断授权；
- 生产确认前后权限和会话复核；
- 审计结果区分成功、拒绝、取消和失败；
- 不能把客户端通过当作服务端授权通过。

### 6. 备份、恢复、更新和许可证

- 备份预览、确认、取消和失败清理；
- ZIP 文件清单和 SHA-256；
- 恢复预览使用一致快照；
- 旧 schema、未知字段和未来版本；
- 恢复后工作区、设置和数据；
- 关于页手动检查更新；
- 非法更新地址、浏览器失败和重试；
- 第三方许可证声明读取、复制、取消和超限；
- Toast：已接入固定状态系统通知；显示、冷／热任务中心路由与安装卸载清理见 [通知接入记录](WINDOWS_NOTIFICATIONS.md)，物理点击横幅仍需人工复核。不能用应用内提示条冒充系统通知。

## 六、Windows UI 矩阵

按 [UI_ACCEPTANCE.md](UI_ACCEPTANCE.md) 执行：

- DPI：100%、125%、150%、200%；
- 分辨率：960×560、1200×720、1600×1000；
- 中文、英文和中英混合长文案；
- 浅色、深色和高对比度；
- 单显示器和多显示器；
- 键盘 Tab/Shift+Tab、方向键、Enter、Space、Escape、F1、F5、Ctrl+W；
- IME、长文本、错误信息、长路径；
- UIA、Narrator/NVDA、焦点和 AutomationProperties；
- 文件选择器、剪贴板、系统托盘和窗口关闭；
- 任务完成提示、右侧面板和滚动区域。

通过标准是主操作可见、可操作、焦点顺序合理、没有明显重叠或裁剪，错误不只依赖颜色，读屏能获得控件名称和状态。

## 七、安装、升级和卸载

在干净用户账户中测试：

1. 首次安装和首次启动；
2. 已有用户数据升级；
3. 升级前备份和取消；
4. 升级过程中占用、锁文件、失败和取消；
5. 静默安装、静默升级和错误退出码；
6. 便携 ZIP 解压后启动；
7. 卸载后用户数据、未知文件和未知目录保留；
8. 重装后数据恢复；
9. 两个程序实例或两个数据目录的锁保护；
10. 安装目录和交付文件被替换为链接时拒绝；
11. 无 SDK、无开发工具的最终用户机器启动。

新增自动备份路径必须重新验收，不能引用此前手动备份升级的结果：

- 使用实现 `--backup-before-upgrade` 的旧版，保存数据并退出，交互升级成功后核对提示的 ZIP、manifest、哈希、设置与已提交 WAL 行，并在隔离目录恢复。
- 自定义 `PLINTH_DATA_DIR`、含空格／中文路径、多数据目录、备份目标目录链接、旧版命令不支持和旧 EXE 缺失；不误备份另一数据目录，不沿链接导出。
- 磁盘满、目标已存在、坏设置、坏库、未知 schema、权限不足、认证或 Production 无法确认、服务器模式：退出非零且程序文件不覆盖。
- 备份时尝试普通启动和第二个安装器，备份子进程退出后制造旧 EXE 占用；保持目录锁，失败不覆盖、不强杀。已完成的 ZIP 在后续安装失败时仍保留。
- `/S` 升级始终返回 6，交互取消返回 1，首次 `/S` 安装仍可用；没有开关可以忽略备份失败。

## 八、证据、缺陷和最终判定

每个测试项记录：

- 编号和测试目的；
- 环境和构建版本；
- 执行命令或操作步骤；
- 通过/失败/阻塞；
- 控制台日志、TRX、截图或录屏路径；
- 产物文件名、大小和 SHA-256；
- 缺陷编号和复现步骤。

最终交付必须同时满足：

- Release build 成功；
- 全量 C# 测试成功；
- 性能 POC 和启用变体结果已单独记录；
- 候选 ZIP/安装器通过文件和哈希检查；
- Windows UI、安装升级卸载和用户数据保留通过；
- 所有未执行项明确标记为未执行；
- 没有用静态检查、源码存在、跳过测试或应用内提示条替代真实验收。

