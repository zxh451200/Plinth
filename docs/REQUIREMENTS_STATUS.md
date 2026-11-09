# 需求交付核对表

核对日期：2026-11-04，2026-11-08 增补实机构建与测试记录。基线为根目录 v4.0 规范，档位以 §0.4 为准。下表区分“已有源码”和“已通过验收”；没有把规范中的验收复选框改成完成。详细实现记录见 [IMPLEMENTATION_STATUS.md](IMPLEMENTATION_STATUS.md)。Windows 构建、测试和安装结果为既有执行记录，不自动覆盖此后新增的源码改动。

当前交付快照：2026-11-04 已在 Windows 11 + SDK 10.0.403 完成真实 restore／locked restore／Release 构建／自动测试／10k 性能 POC／ZIP 发布。2026-11-08 在同一 SDK 环境对本轮全部未编译增量重新构建并测试：修复 2 处编译错误（AdvancedTableRepository、OfflineSyncViewModel）与 3 处签名／路由问题后 Release 构建 0 错误；App 自动回归 1183 项中 1181 通过、2 项按设计跳过（未配置 `PLINTH_TEST_POSTGRES`／`PLINTH_TEST_MYSQL` 不算通过）、0 失败；Spreadsheet、XLSX 启用变体、Dock／RuntimePlugins／OfflineSyncPilot 三个独立试点测试套件全部通过；`dotnet format --verify-no-changes` 通过；源码契约 0 错误、510 双语键／452 引用键资源检查通过；SQLite smoke 15 项通过（含新增增列迁移用例）。同日发布链路实机执行：`publish.ps1` 完成 11 项目锁校验、locked restore、自包含发布、91 包 notices/SBOM 与 ZIP 候选包（通过 release_checks）；命令行契约在发布目录复验（`--version` 退 0、未知参数退 2）；便携版 NSIS 3.11 编译 Setup 0.1.0 通过包检查，静默安装／卸载冒烟通过（精确清单删除、未知文件保留、注册表清理）；`scripts/acceptance.ps1` 完整验收入口（与 CI 同门禁）首次整体实机执行通过，工具测试 107 项 0 跳过（含 NSIS 编译用例）。同日第二批：真实 PostgreSQL 17.5 与官方 MySQL 8.0.46 专用实例上 Provider 集成测试通过（独立迁移集、幂等、模型漂移、CRUD）；0.1.0→0.1.1 交互升级演练通过（自动备份生成并校验、数据哈希不变、占用退 5／静默退 6）；自签代码签名管线验证通过（Valid、篡改失效）。真实 IdP/API、正式 CA 签名、OS DPI/IME/Narrator、通知横幅物理点击仍未验收；干净账户首启与独立 300MB 卷磁盘满（GUI 启动/CLI 备份）已于 2026-11-08 实机执行，边界与恢复行为见[实现记录](IMPLEMENTATION_STATUS.md)。2026-11-09 真实 GitHub CI run 42525487305（commit 0ae0af8）Windows/Ubuntu gate 与 6 个 pilot 全绿；同一提交加入可选本地登录门禁（`LocalLogin.Enabled` 默认关闭）。详细证据见[本环境无法关闭的阻塞](#本环境无法关闭的阻塞)。

同日（2026-11-08）按 §6.3 Should 补齐单元格内联编辑的类型矩阵：Demo 新增数量（整数）、截止日期（DateOnly）、优先级（枚举）、启用（布尔）四列，单元格分别使用 Avalonia 核心 NumericUpDown、应用内 DateField、ComboBox、CheckBox，与文本单元格共用同一份草稿、显式保存／取消、字段校验与版本冲突，不引入第三方网格或自研编辑器。SQLite／MySQL／PostgreSQL 各新增独立增列迁移（旧行默认 0／空／Normal／true，启动时仍校验结构与迁移版本一致），CSV 导出扩展为 7 列（导入仍只更新文本三列，类型列在表单或单元格维护）。新增类型化 headless 回归（草稿隔离、保存持久化、越界字段错误、取消丢弃、仅选中行渲染）及迁移默认值断言；负控制（漏存布尔列）如期转红后按字节还原。

## 可选功能本轮增量

本轮多 agent 已补 Demo 文本单元格行内编辑、安装器调用升级前一致性备份、独立高级表格合成数据试点、真实 DockControl 独立桌面示例、进程外插件协议与宿主、SQLite outbox 离线同步试点。以上为源码交付，尚未通过本轮 .NET／Windows 运行验收，不将历史 Windows 结果沿用到这些改动。

高级表格通过受信任部署项 `EnableAdvancedTablePilot` 独立启用，默认关闭。Dock 四项依赖统一在 Directory.Packages.props 固定版本，主应用不引用 Dock；独立项目仍需真实 restore 生成锁文件。插件和同步仍是独立试点，分别不提供 OS 沙箱保证和生产同步传输适配，不代表完整通用插件平台或离线业务系统。安装器自动备份要求旧版支持命令，失败即阻止覆盖，静默升级仍拒绝。

本地登录门禁（2026-11-09）：受信任部署项 `LocalLogin.Enabled` 默认关闭；开启后先登录再创建主窗口，首次启动设置本机用户名/密码。密码只保存 PBKDF2 哈希到数据目录 `login.json`，备份不包含；门禁不加密数据、不替代远程 OIDC 身份/权限。实现与测试见 IMPLEMENTATION_STATUS。

本轮复核修复了失效选行导致草稿丢失、重选草稿行未恢复、备份 ZIP 目标撞名、插件缺字段协议校验，以及高级表格初始化重试、晚到 DataContext 和页面释放问题。源码契约及资源集成检查已通过；实际编译、headless 测试与 NSIS 安装器编译已于 2026-11-08 补齐并全绿，Windows 升级前备份／恢复完整演练仍待历史版本安装包。具体边界见 ADVANCED_TABLE_PILOT.md、DOCK_OPTIONAL_POC.md、RUNTIME_PLUGIN_PILOT.md、OFFLINE_SYNC_PILOT.md 和 UPGRADING.md。

## Must 与实施约束

后续增量：高级表格新增显式 CSV 仓储适配，严格 UTF-8、8 MiB／10,000 行／16 组上限，输入完全校验后发布只读快照；默认合成数据入口保持原语义。离线同步新增 `tools/Plinth.OfflineSyncPilot` 及独立测试项目，使用本地 SQLite 传输演示版本冲突、持久回执、丢响应与显式核对，不代表生产 HTTP 协议已实现。独立 CI 现覆盖 Windows／Ubuntu × Dock／runtime-plugins／offline-sync，主解决方案及 pilot 引用图覆盖全部 11 个项目；CI 已实际执行（run 42525487305）：Windows/Ubuntu gate 与 6 个 pilot 全绿；锁文件已由真实 restore 生成并提交。

高级表格页面现增加原生 StorageProvider 显式 CSV 导入入口，严格复用上述仓储校验；完整导入、流释放及首页读取成功后才替换当前页快照并重置筛选。失败保留原数据、分组、分页及已应用范围；取消、切页、更换 DataContext 或关闭后的迟到选择器不能发布新数据。沿用 SensitiveOperationService 的授权、生产确认及会话复核；未配置生产权限映射时认证路径默认拒绝，没有新增或假定生产授权。界面区分合成／导入／宿主数据源，并接入现有中英文本地化。默认部署启用逻辑未变。已补 C#／headless 回归测试，源码契约和资源集成静态门禁已实际通过；当前环境无 dotnet，编译和桌面运行验收仍未完成。

环境复查发现相邻缓存 SDK 10.0.403，但启动缺少 `GLIBCXX_3.4.20`／`GLIBCXX_3.4.21`，官方 SDK 下载端点连接亦失败。因此运行验证仍阻塞，不能将新增 CSV、SQLite 本地传输及进程回归测试记为通过。

数据库支持措辞修正：上文历史快照的“三选一”表示 Provider 配置与初始化源码已接入。原规范 §8.6 要求服务器 Provider 有独立迁移集并通过真实数据库验收；本轮已新增独立迁移上下文、ID、目标模型和快照，但尚未编译及通过真实数据库验收，不能正式宣称 MySQL／PostgreSQL 已支持。本轮修复与历史 Windows 测试结果分别记录。

| 规范范围 | 当前实现证据 | 尚需完成 |
| --- | --- | --- |
| §2／§3.1 主窗口、菜单、工具栏、状态栏、标签、布局、快捷键 | MainWindow、MainViewModel、AppShortcuts、WorkspacePreferencesService | 真实 Windows 键鼠、IME、DPI、长标签、布局和焦点验收 |
| §3.2／§6 本地列表与可靠表单 | Demo 的分页／排序／搜索、500 长度上限的搜索偏好记忆、列显隐／宽度偏好、经过未保存确认的列表恢复默认（清空搜索、第 1 页、50 条、Code 升序、默认列）、当前页选择、字段错误、保存和删除均按编辑时版本处理冲突 | 已有 Windows 自动回归及 10k POC 记录；偏好恢复／重置／写入受阻的真实桌面交互仍需复核 |
| §3.5 全局任务与错误中心 | TaskService 并发上限 2、等待取消、手动重试、状态／关联 ID、CSV／可选 XLSX 结果计数与受控文件位置、自选报告位置、应用内完成提示 | 已有自动回归记录；并发、取消、重试、退出的真实桌面操作和故障矩阵仍需补证据 |
| §3.7 离线帮助、版本及许可入口 | HelpViewModel、模块 HelpTopic、命令行版本、LicenseNoticeService；关于页显式检查更新和打开 HTTPS 下载地址，新检查／关页清理旧入口 | C#／桌面及默认浏览器验收，真实第三方声明随包生成 |
| §4／§7 模块和页面生命周期 | 普通 DI、NavigationItem、ViewLocator、标准 IDisposable、独立第二模块、固定右栏 | 已有编译与自动回归记录；真实桌面关闭／选择器操作、反复开关页面的资源趋势仍需验收 |
| §4.5 异步与异常边界 | 单实例监听非阻塞 Dispose／可等待退出，页面异步事件及普通选行异常观察，日志查询故障反馈，CSV 预览关闭取消，CSV／诊断／XLSX 迟到选择器检查，设置健康检查版本与关闭保护 | 已有自动回归记录；真实管道和桌面关闭／原生选择器验收仍需补证据 |
| §5 依赖和许可证 | 中央版本、锁包检查、third_party.py、supplemental_compliance.py、CI／nightly 配置；显式非 NuGet 库存的正文路径／UTF-8／原始字节 SHA-256 校验及 notices／CycloneDX SBOM 合并，见 [发布补充许可材料](RELEASE_COMPLIANCE.md)；2026-11-09 已提供 `reviewed-materials`（runtimepack/NSIS/Skia/ANGLE/HarfBuzz/SQLite/Roboto/Carlito 原文+哈希，third_party.py 生成 91 NuGet + 8 supplemental；runtimepack 经 nuspec/包内正文/187 个发布文件严格对账转入 `reconciled_publish_packages`） | 版本可用性、安全审计、法务审查、Windows 发布和包内容验收仍待；对账不等于审查通过（两个批准标志仍为 false）；字体已补 Roboto/Carlito（嵌入已分发 DLL），图标自有；Carlito RFN 条款属法务判断，有输入仅增加显式组件，不代表完整分发库存或审查通过 |
| §8／§15 数据库、迁移、备份恢复 | 默认 SQLite；MySQL（Pomelo 9）／PostgreSQL（Npgsql 9）配置、独立迁移集与启动入口已接入；2026-11-08 两个 Provider 均通过真实服务器验收（便携版 PostgreSQL 17.5 与官方 MySQL 8.0.46 专用可清理实例：独立迁移集、重复初始化幂等、无模型漂移、CRUD 往返与数据清理，见 [实现记录](IMPLEMENTATION_STATUS.md)）。SQLite 保留候选副本/WAL/旧库升级；备份页明确只支持 SQLite | 配置继承、连接串校验、历史保护已有 C# 验收；独立小容量卷磁盘满的应用启动/CLI 备份已实测，日志卷磁盘满（日志目录创建 IO 故障与只读日志文件已实测）、安装器/升级在磁盘满下的完整矩阵与长期并发演练仍属交付后矩阵 |
| §9 深浅主题、中英、地区／时区 | RESX、Loc、FormattingService、主题与设置；未知设置／布局版本保留原件并回退，保留失败阻止保存 | 真实 UI／格式验收；静态资源一致性已检查 |
| §3.3／§8／§10 远程配置、API 与身份 | PLINTH_ 环境变量覆盖及启动校验、RemoteDemo 独立分页 CRUD、If-Match、OIDC+PKCE、凭据库、共享会话状态通知与设置页退订、权限入口、HTTPS／限流／超时处理 | 实际 API 与 IdP 对接、端到端登录／刷新／换号、权限及并发验收；默认不启用远程示例 |
| §10 基本权限、审计和敏感操作 | AuthorizedOperation、SensitiveOperationService、LogAccessService、服务入口授权、确认后原会话与权限复核、排队确认对象 | C# 拒绝路径测试、实际身份与服务端权威审计核验；客户端不是服务端安全边界 |
| §11 结构化日志、错误及诊断 | 三类事件、部署等级与独立审计、查看授权、受控复制、写时字段白名单／受控异常、日志有界保留／降级、诊断预览／二次扫描、直接插值门禁 | C# 测试、换号撤权、敏感数据与退出故障实测；日志目录创建 IO 故障（同名文件占位）与只读日志文件已实测并降级到内存/stderr；日志卷磁盘满未单独测（演练日志目录在 C:）；二开仍须约束模板及字段语义 |
| §12 CSV | 编码／分隔符／映射、流式预览及严格尾部解码、同句柄 SHA-256、防公式注入、错误报告与计数完整性、整批事务、分页导出及实际读取／写出进度 | C# 运行、中文／长数字往返与峰值内存 POC；输入有 5 MiB／5000 行上限 |
| §13 性能与资源 | 当前页读取、取消令牌、任务并发上限、后台 IO 和有界数据；真实 SQLite／CSV 10k 与 5000 行往返 [POC](PERFORMANCE_POC.md) 已有执行数字，2026-11-08 对当前源码重跑全部场景通过；2026-11-09 实测冷/暖启动到主窗口（冷 0.87/6.10/9.51s，暖 0.66/4.47/7.45s：启动日志/数据库就绪/窗口） | 桌面峰值／响应／虚拟化、首帧、滚动/编辑内存、低配设备和长期资源趋势仍待测；单次 POC 不代表桌面性能达标 |
| §14／§17／§20 构建与验收 | Windows／Ubuntu CI、源码工具、headless／单元测试、UI_ACCEPTANCE.md；2026-11-08 已对本轮全部增量重新 restore／构建／测试（App 1174 项 0 失败、Spreadsheet、XLSX 启用变体、三个试点独立套件、format、契约与资源检查），并修复暴露的 2 处编译错误与 6 组测试缺陷；同日 `scripts/acceptance.ps1` 完整验收入口（与 CI 同门禁）首次整体实机执行通过，工具测试 107 项 0 跳过（含 NSIS 编译用例真实执行） | 实际 CI 已通过（run 42525487305，TRX/xunit 日志与 pilot 证据归档）；未完成的人工矩阵 |
| §15 安装、升级、卸载 | NSIS 路径／锁保护、占用拒绝、升级备份提示、精确清单卸载、保留未知用户文件；2026-11-08 已用便携版 NSIS 3.11 实机编译 Setup（0.1.0，通过 release_checks 包检查），并完成静默安装（301 文件、版本契约）、放置未知文件后卸载（程序文件与注册表键删除、未知文件保留）冒烟 | 干净账户首启、独立小卷磁盘满（应用启动/CLI 备份）与安装器在干净账户下的安装/卸载冒烟已实测（0.1.0 Setup、302 文件、`--version` 0、卸载清理程序/注册表/快捷方式/AUMID/协议、用户数据保留）；安装器在磁盘满目标上的首次安装失败也已实测（退出 7、无程序/注册表）；真实无 SDK 机器、安装器/升级在磁盘满下的升级/回滚、旧版创建备份后升级／恢复仍待；不承诺整包自动回滚 |
| §16／§21 二开交付与简单架构 | GETTING_STARTED、SECOND_MODULE、REMOTE_DEMO、模块静态门禁；2026-11-08 已做一次受控照抄验证（临时副本新增页面、定向回归 53 项通过，迁移命令问题已修正） | 尚未参与开发者的真人上手记录；静态门禁不证明动态调用、EF 翻译或安全正确性 |

### 2026-11-04 并行推进后的复验入口

- `DatabaseConfigurationTests`：局部 `database.json` 继承部署 Provider，非法连接参数只返回固定错误，不泄漏密码。
- `DatabaseSafetyTests`：SQLite 原有候选副本、迁移、恢复保护；新增共享迁移历史检查不能削弱原有拒绝路径。
- `DatabaseProviderIntegrationTests`：2026-11-08 已在专用可清理实例（便携版 PostgreSQL 17.5 @5433、官方 MySQL 8.0.46 @33077）上分别真实执行通过；显式 `PLINTH_TEST_DATABASE_ISOLATED=true` 与 `PLINTH_TEST_DATABASE_PURPOSE=plinth-integration` 为必设条件，环境未配置仍是跳过不算通过。旧试验库若使用 SQLite 迁移 ID 会被拒绝，不能自动重写历史。
- `BackupTests`：新增回滚写入连续受阻、已提交事务清理受阻及解除故障后重试，核对安全副本保留与重启幂等。
- `Large_remote_total_keeps_only_the_requested_page_when_navigating`：新增 100k／1m 总量、50／200 条每页的客户端契约回归，不代表真实远端性能验收。

```powershell
dotnet test tests/Plinth.App.Tests/Plinth.App.Tests.csproj -c Release `
  -p:TestingPlatformDotnetTestSupport=true `
  --filter "FullyQualifiedName~DatabaseConfigurationTests|FullyQualifiedName~DatabaseSafetyTests|FullyQualifiedName~DatabaseProviderIntegrationTests|FullyQualifiedName~BackupTests|FullyQualifiedName~Large_remote_total_keeps_only_the_requested_page_when_navigating"
```

2026-11-04 该入口曾因缺少 dotnet 退出 127；2026-11-08 已在 Windows（SDK 10.0.403）实际执行上述过滤集及全套 App 回归，1174 项 0 失败（其中 DatabaseProviderIntegrationTests 各场景因环境变量未配置而跳过，不计为通过）。

## Should 与条件项

| 能力 | 状态与限制 |
| --- | --- |
| XLSX | 有服务、页面、全局导入导出结果及测试源码，默认关闭。任务保留计数、拒绝摘要、报告及自选 XLSX 文件位置；中转 CSV 写时摘要绑定完整预览内容。2026-11-08 Office POC：真实 Excel 保存的文本工作表现可被服务导入（修复了把 Office 目录条目当非法部件的缺陷并补回归），中文／18 位长数字文本／日期／公式注入文本无损往返、损坏文件拒绝均通过；真实 Excel 生成的外部链接工作簿被拒绝、`.xlsm` 扩展被拒绝，宏（vbaProject.bin）由合成包测试覆盖。内存 POC 峰值 61 MiB 已记录；宏内容的真实 Office 样本受本机 Excel「信任对 VBA 工程对象模型的访问」设置阻塞，未改动该安全设置。许可：ClosedXML 0.105.1 nuspec 声明 MIT，已进入 NuGet 库存与 notices/SBOM，终审仍属发布决策。独立服务测试与可选页面 CI 接线不等于正式启用 |
| PDF／打印／条码（Could 示例） | 已提供 Demo 固定模板：SkiaSharp 3.119.4（MIT）+ QRCoder 1.8.1（MIT），导出当前页 PDF（文字转矢量轮廓、二维码），打印调用系统 PDF 处理程序的 `print` 动作。固定模板仍为 Could；不是通用报表引擎，真实打印机与 PDF 阅读器兼容仍未验收；不将可检索文本或页码总数另立为首版 Must |
| 紧凑／舒适密度 | 三档已接入；headless 在 100/125/150/200% 渲染缩放下通过像素尺寸与工作区不越界断言，真实 OS DPI 设置切换和完整视觉调校仍待人工 |
| 无障碍辅助技术 | 有自动化名称／键盘测试矩阵；Windows UIA 树已实测（UIA 名称、导航与标签页），快速打开结果的读屏名称已改为页面标题；Narrator／NVDA 实际朗读未完成 |
| §3.9 可隔离模块失败 | RemoteDemo 注册采用暂存集合，完整成功后追加；注册、首次 ViewModel 构造、View 查找或同步视图构造（含构造期间的 XAML 初始化）失败禁用入口，显示独立提示及关联日志；视图失败异步移除并释放标签，迟到关闭确认不重复释放。核心配置、共享依赖和数据库错误仍传播。C#／桌面验收未执行；不覆盖构造完成后的绑定、事件、异步业务故障或任意模块运行时卸载 |
| 系统 Toast | 已接入并实测显示、冷启动／已有实例的任务中心路由及安装卸载清理，见 [通知接入记录](WINDOWS_NOTIFICATIONS.md)。通知只含固定状态文案；通过随包 `notify.ps1` 调用 Inbox WinRT，失败不改变任务结果。物理点击通知横幅与焦点助手／勿扰策略下的人工体验仍未验收 |
| 单元格内联编辑 | 已提供 Demo 的文本、数字、日期、枚举、布尔五类单元格编辑（Code／Name／Remark、数量、截止日期、优先级、启用），与标准表单共享草稿、显式保存／取消、字段校验和版本冲突处理；控件均为 Avalonia 核心控件（NumericUpDown／ComboBox／CheckBox）与应用内 DateField，无第三方网格。随 `IncludeSampleModules` 暴露，没有独立开关。2026-11-08 类型化 headless 回归与三 Provider 增列迁移默认值已通过；真实 Windows 日期弹层、下拉、键盘与读屏交互仍待人工验收，不据此宣称 Should 全部完成 |

Could（插件隔离、通用 Dock、服务器库应用内逻辑备份、通用 PDF／打印／条码引擎、离线同步等）没有自动升级为本轮交付范围，继续遵守规范“不提前堆叠框架”的约束。

## 剩余缺口（2026-11-04 核对）

### 未实现、可选交付与待验证事项

范围判定以根规范 §0.4 为准：只有 Must 阻断首版 GA。下表不是统一的 Must 缺陷列表。远程 OIDC 仅在本版本交付远程模式时成为 Must；首版只要求数据库可移植性编码规则，不要求完成 MySQL／PostgreSQL 支持；§15.2 的“可选自动备份”不能解释为所有升级必须由安装器自动备份，必须保障的是用户数据安全及可用、经过实测的备份恢复路径。签名预算／正式分发说明也不能直接替换成“必须购买证书”的功能需求。

| 项 | 档位 | 现状 |
| --- | --- | --- |
| 单元格内联编辑 | Should（有条件） | 五类编辑已提供（文本／数字／日期／枚举／布尔），类型化 headless 回归与旧库增列默认值已验证；仅剩真实 Windows 交互（日期弹层、下拉、键盘／读屏）待人工验收。标准表单仍保留，不扩展为完整网格 |
| XLSX 启用 | Should | 服务、页面和测试在，默认关闭。2026-11-08 Office POC：真实 Excel 保存的文本工作簿可导入（修复目录条目误拒并加回归）、真实外部链接工作簿与 `.xlsm` 被拒绝、宏／外部关系／公式缓存由服务测试覆盖；内存峰值 61 MiB；ClosedXML 0.105.1 nuspec 为 MIT 并进入库存／notices／SBOM。2026-11-09 已临时打开 Excel 12.0 `AccessVBOM` 生成真实宏 `.xlsm` 样本（设置已恢复），产品按扩展名拒绝；测试含可选真实样本用例（设置 `PLINTH_XLSM_SAMPLE` 时执行，CI 无变量时 skip）；仅剩许可终审（发布决策）；可显式以 `PlinthEnableXlsx=true` 构建，默认仍关闭 |
| 升级前自动备份 | §15.2 可选能力 | 应用内备份并退出入口保留；安装器交互升级调用旧版受控命令自动创建并校验一致性 ZIP，失败禁止覆盖，静默升级仍拒绝。2026-11-08 完整演练通过：0.1.0→0.1.1 交互升级退出 0（自动备份生成并通过新版校验，数据库哈希不变，备份保留在 `Plinth-Upgrade-Backups`）、静默升级退出 6 且文件不变、应用占用中安装退出 5 且文件不变。历史旧版（不支持命令）的拒绝路径与恢复演练仍属交付后矩阵 |
| 非 NuGet 许可库存与审查 | §5 发布门禁 | 2026-11-09 已提供 `reviewed-materials`（runtimepack.Microsoft.NETCore.App.Runtime.win-x64 10.0.14、NSIS 3.11、Skia 3.119.4、ANGLE、HarfBuzz、SQLite 3.53.3、Roboto Light 2.137、CarlitoBare 1.104 原文+哈希；third_party.py 生成 91 NuGet + 8 supplemental）；runtimepack 以包内 nuspec MIT、LICENSE/ThirdPartyNotices 字节及 187 个发布文件逐项对账，缺口转入 reconciled 而非审查通过；字体来自已分发 DLL 的内嵌资源而非系统字体，图标自有；Carlito OFL 的 RFN 条款、法务审查与正式发布包内容验收仍待 |
| 100k／1m 远端分页与桌面性能 | §6.4 POC／§13.2 建议指标 | 10k 本地 CSV／SQLite 已有实测。2026-11-08 已用专用只读 HTTPS 测试 API 执行 100k／1m 首中末页 POC：100k 为 3496.8／22.0／9.2ms，1m 为 2710.2／14.8／3.9ms，每次 50 行、总数与页元数据一致、ID 不重复（首次含 TLS/JIT；服务端是本机临时 API，不是外部生产服务）。2026-11-09 冷/暖启动到主窗口已实测（见 [性能 POC](PERFORMANCE_POC.md)）；首帧绘制、滚动／编辑内存及低配机仍未测；不把建议数值全部升级为 GA 硬门槛 |
| 新人照抄二开验证 | §4.6.5／§20 持续改进 | 2026-11-08 在临时副本按 GETTING_STARTED 实际照抄：restore、`--version`、新增页面（View／ViewModel／双语资源／DI／导航）、定向回归 53 项通过；按文档生成迁移发现缺 `--context` 会报多个 DbContext，已修正 GETTING_STARTED／SECOND_MODULE。仍缺未参与开发者的真人上手记录；新人上手时间仍是持续改进指标 |

### 已实现、未通过验收

- 真实 IdP 的 OIDC／PKCE 端到端（交付远程模式时为 Must；本地模式不适用，当前只有本地测试与模拟身份，不能据此宣称远程认证已验收）。本仓库不附带生产 API／IdP，搭建权威第三方 IdP 属交付远程模式的前置工作。
- OS 级 DPI 100／150／200、多显示器、高对比度、中文 IME、Narrator／NVDA 朗读。常见键盘／IME／DPI 的实机检查属于首版验收；辅助技术完整验收为 Should，不混为同一档位。headless 渲染缩放断言（100/125/150/200%）已通过，实机 OS 设置切换仍需人工。
- 真实文件选择器、剪贴板、托盘、更新浏览器、许可证复制／取消、长路径（升级前备份/升级演练已实测，见 [实现记录](IMPLEMENTATION_STATUS.md)）。
- 独立小容量卷磁盘满（应用启动/CLI 备份）已实测；日志目录创建 IO 故障与只读日志文件已实测并降级到内存/stderr；日志卷磁盘满、网络断开／超时、数据库忙、进程崩溃和备份损坏等可靠性矩阵仍待独立小容量卷实机（服务层拒绝路径已有测试）；0 字节库在磁盘满启动失败后会在下次启动按未完成初始化自动重建（已修复并有测试）。
- 干净用户账户首启已在本机标准用户 `PlinthClean` 实测（安装器/UI 全矩阵仍待）、正式 CA 代码签名（自签管线已验证，见 [实现记录](IMPLEMENTATION_STATUS.md)）、通知横幅物理点击与勿扰策略。

## 本环境无法关闭的阻塞

工具验证记录：Python 工具测试 63 项全部通过（含原先因缺 PowerShell 跳过的 8 项）；SQLite smoke 14 项通过；源码契约 0 错误，资源／项目检查通过。

2026-11-03 至 11-04 在 Windows 11（10.0.26200，i5-10210U／8 逻辑核／16 GB）与 .NET SDK 10.0.403 上实际执行：

- solution 与 `win-x64` RID 的 restore、locked restore 通过，已生成 3 个 `packages.lock.json`；低危及以上 NuGet 审计无告警；Release 构建 0 错误。
- App 自动回归 917/917、Spreadsheet 47/47、XLSX 启用构建 956/956，TRX 已归档。已修复标签条键盘可达、远程拒绝切换后的表格回退、WAL 用例写连接保持、单实例通知测试续体与 XLSX CRLF 归一化。
- `dotnet format --verify-no-changes` 已通过；测试项目中的 xUnit1051/2031/2017 在 `.editorconfig` 中降为建议，仍保留 IDE 提示。
- 10k CSV／SQLite 性能 POC 通过，各阶段耗时见 [PERFORMANCE_POC.md](PERFORMANCE_POC.md)。
- Release ZIP 与 Setup.exe 已生成并通过 `release_checks`；安装 0、锁文件存在、`--version` 正常；`/D=` 返回 4、运行中安装返回 5、静默升级返回 6、卸载保留用户数据。发布目录无测试程序集或源码。
- 发布 `Plinth.App.exe --version` 输出 `Plinth 0.1.0`、退出 0；未知与重复参数走 stderr、退出 2；空数据目录首次启动建库/设置/日志，二次启动设置不变，窗口运行时查询不打断原窗口。
- 安装器故障矩阵：目录拒写退出 5 且无残留；提取阶段写入失败退出 7 且不写注册表；进程中途被杀后重新静默安装可恢复；交互升级提示可取消（退出 1、文件不变）；真实 0.0.9→0.1.0 交互升级退出 0、锁文件保留、用户数据 hash 不变；带占用的卸载拒绝且 20 秒内不改文件，释放后可正常卸载并保留数据。
- 系统 Toast：固定状态文案进入 Windows 通知历史，`Setting=Enabled`；`plinth://tasks` 冷启动与已有实例均进入任务中心；安装器写入 AUMID／协议，卸载只删除仍指向本安装目录的协议项。
- 便携包与安装边界：ZIP 解压后 `--version`、GUI、两个数据目录同时启动均通过；把 PATH 缩到 System32（应用自包含）后版本与 GUI 仍可启动。未知文件/未知目录在卸载后保留、重装后仍在，数据库 hash 不变。两个安装器并发时退出码为 0/5，最终安装可用；目录 junction 拒绝退出 4 且目标零写入。`environment.txt`、NuGet 审计、SQLite smoke、TRX 已归档。

仍需关闭：安装器/升级在磁盘满下的升级/回滚与旧版升级/恢复（应用首启、CLI 备份、安装器干净账户安装/卸载与磁盘满首次安装失败已实测）、日志卷磁盘满、正式 CA 代码签名（自签管线已验证，正式签名属分发决策）、真实 OS DPI 设置切换／多显示器／高对比度／IME／Narrator 朗读，真实文件选择器／剪贴板／托盘／更新浏览器，真实 API／IdP（交付远程模式时为 Must，本仓库不附带生产 API／IdP），通知横幅物理点击与勿扰策略体验。服务器数据库（PostgreSQL 17.5、MySQL 8.0.46）、升级前备份与升级、签名管线已于 2026-11-08 实机验收，见 [实现记录](IMPLEMENTATION_STATUS.md)。

因此当前不能宣称“所有需求完成”或“可以发布”。可复现验证顺序与命令见 [发布交接](release/RELEASE.md)，桌面矩阵见 [UI_ACCEPTANCE.md](UI_ACCEPTANCE.md)，远程契约见 [REMOTE_DEMO.md](REMOTE_DEMO.md)。补齐上述环境和实际证据后，应继续处理真实构建／测试暴露的问题，而不是仅修改本表状态。

本轮并行核查已发现并修复数据库配置继承、Provider 连接串校验与迁移历史保护问题，说明此前“没有可直接推进的源码缺口”结论过强。后续继续推进数据安全故障验证、许可材料及资源释放检查；需要 Windows 或外部服务的项目保留真实验收状态。远程联调只在远程模式交付时适用，签名方案按正式分发决策说明，读屏／通知完整体验按 Should 处理。内联编辑已有文本／数字／日期／枚举／布尔五类示例，剩余为真实桌面交互验收，不再列为未实现；仍不得据此扩展自研网格或把条件项升级为首版 Must。
