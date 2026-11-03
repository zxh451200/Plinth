# 需求交付核对表

核对日期：2026-11-03。基线为根目录 v4.0 规范，档位以 §0.4 为准。下表区分“已有源码”和“已通过验收”；没有把规范中的验收复选框改成完成。详细实现记录见 [IMPLEMENTATION_STATUS.md](IMPLEMENTATION_STATUS.md)。

当前交付快照：2026-11-03 已在 Windows 11 + SDK 10.0.403 完成真实 restore／locked restore／Release 构建／自动测试／10k 性能 POC／ZIP 发布。App 897/897、Spreadsheet 47/47、XLSX 启用变体 36/36 通过，TRX 已归档；`dotnet format --verify-no-changes` 已通过（xUnit 建议类分析器降为建议）。安装器已编译并通过安装/升级拒绝/占用拒绝/卸载冒烟；干净账户/故障矩阵、真实 UIA／IME／DPI／读屏、真实 API／IdP 和系统 Toast 仍未验收。详细证据见[本环境无法关闭的阻塞](#本环境无法关闭的阻塞)。

## Must 与实施约束

| 规范范围 | 当前实现证据 | 尚需完成 |
| --- | --- | --- |
| §2／§3.1 主窗口、菜单、工具栏、状态栏、标签、布局、快捷键 | MainWindow、MainViewModel、AppShortcuts、WorkspacePreferencesService | 真实 Windows 键鼠、IME、DPI、长标签、布局和焦点验收 |
| §3.2／§6 本地列表与可靠表单 | Demo 的分页／排序／搜索、500 长度上限的搜索偏好记忆、列显隐／宽度偏好、经过未保存确认的列表恢复默认（清空搜索、第 1 页、50 条、Code 升序、默认列）、当前页选择、字段错误、保存和删除均按编辑时版本处理冲突 | C#／EF／Avalonia 测试执行、偏好恢复／重置／写入受阻桌面验收及真实数据规模 POC |
| §3.5 全局任务与错误中心 | TaskService 并发上限 2、等待取消、手动重试、状态／关联 ID、CSV／可选 XLSX 结果计数与受控文件位置、自选报告位置、应用内完成提示 | 并发、取消、退出和桌面任务操作测试执行 |
| §3.7 离线帮助、版本及许可入口 | HelpViewModel、模块 HelpTopic、命令行版本、LicenseNoticeService；关于页显式检查更新和打开 HTTPS 下载地址，新检查／关页清理旧入口 | C#／桌面及默认浏览器验收，真实第三方声明随包生成 |
| §4／§7 模块和页面生命周期 | 普通 DI、NavigationItem、ViewLocator、标准 IDisposable、独立第二模块、固定右栏 | C# 编译、页面取消／迟到响应与订阅释放测试执行 |
| §4.5 异步与异常边界 | 单实例监听非阻塞 Dispose／可等待退出，页面异步事件及普通选行异常观察，日志查询故障反馈，CSV 预览关闭取消，CSV／诊断／XLSX 迟到选择器检查，设置健康检查版本与关闭保护 | C# 门闩与事件测试执行、真实管道和桌面关闭／选择器验收 |
| §5 依赖和许可证 | 中央版本、锁包检查、third_party.py、supplemental_compliance.py、CI／nightly 配置；显式非 NuGet 库存的正文路径／UTF-8／原始字节 SHA-256 校验及 notices／CycloneDX SBOM 合并，见 [发布补充许可材料](RELEASE_COMPLIANCE.md) | 真实 restore 与 packages.lock.json、版本可用性、安全审计、真实许可正文及实际 runtime／native／font／icon／NSIS 库存覆盖与审查、Windows 发布和包内容验收；无输入为 NuGet-only 候选，有输入仅增加显式组件，不代表完整分发库存或审查通过 |
| §8／§15 本地数据库、迁移、备份恢复 | AuditableEntity、CodeKey、三次迁移、DatabaseInitializer 候选副本、ZIP 创建预览／白名单／校验、恢复预览同一私有快照与确认哈希、流式压缩进度、暂存取消与恢复保护 | 真实 EF 迁移、取消／失败恢复、WAL／并发测试和 Windows 恢复演练；未支持 PostgreSQL/MySQL |
| §9 深浅主题、中英、地区／时区 | RESX、Loc、FormattingService、主题与设置；未知设置／布局版本保留原件并回退，保留失败阻止保存 | 真实 UI／格式验收；静态资源一致性已检查 |
| §3.3／§8／§10 远程配置、API 与身份 | PLINTH_ 环境变量覆盖及启动校验、RemoteDemo 独立分页 CRUD、If-Match、OIDC+PKCE、凭据库、共享会话状态通知与设置页退订、权限入口、HTTPS／限流／超时处理 | 实际 API 与 IdP 对接、端到端登录／刷新／换号、权限及并发验收；默认不启用远程示例 |
| §10 基本权限、审计和敏感操作 | AuthorizedOperation、SensitiveOperationService、LogAccessService、服务入口授权、确认后原会话与权限复核、排队确认对象 | C# 拒绝路径测试、实际身份与服务端权威审计核验；客户端不是服务端安全边界 |
| §11 结构化日志、错误及诊断 | 三类事件、部署等级与独立审计、查看授权、受控复制、写时字段白名单／受控异常、日志有界保留／降级、诊断预览／二次扫描、直接插值门禁 | C# 测试、磁盘满／只读目录、换号撤权、敏感数据与退出故障实测；二开仍须约束模板及字段语义 |
| §12 CSV | 编码／分隔符／映射、流式预览及严格尾部解码、同句柄 SHA-256、防公式注入、错误报告与计数完整性、整批事务、分页导出及实际读取／写出进度 | C# 运行、中文／长数字往返与峰值内存 POC；输入有 5 MiB／5000 行上限 |
| §13 性能与资源 | 当前页读取、取消令牌、任务并发上限、后台 IO 和有界数据；默认排除、显式开启的真实 SQLite／CSV 10k 与 5000 行往返 [POC 入口](PERFORMANCE_POC.md)，包含阶段耗时及测试进程内存采样 | POC 编译运行与原始结果、桌面峰值／响应／虚拟化、100k／1m 远端总量实测；当前没有性能数字或达标结论，入口存在不代表性能通过 |
| §14／§17／§20 构建与验收 | Windows／Ubuntu CI、源码工具、headless／单元测试源码、UI_ACCEPTANCE.md | 指定 SDK 下 restore／format／build／test，真实 CI 日志、截图和 Windows 实机结果 |
| §15 安装、升级、卸载 | NSIS 路径／锁保护、占用拒绝、升级备份提示、精确清单卸载、保留未知用户文件 | NSIS 编译、无 SDK 的 ZIP／安装包启动，升级／故障／卸载／重装实测；不承诺整包自动回滚 |
| §16／§21 二开交付与简单架构 | GETTING_STARTED、SECOND_MODULE、REMOTE_DEMO、模块静态门禁 | 编译后新人照抄验证；静态门禁不证明动态调用、EF 翻译或安全正确性 |

## Should 与条件项

| 能力 | 状态与限制 |
| --- | --- |
| XLSX | 有服务、页面、全局导入导出结果及测试源码，默认关闭。任务保留计数、拒绝摘要、报告及自选 XLSX 文件位置；中转 CSV 写时摘要绑定完整预览内容。完成 Office／中文／长数字／日期／损坏文件／内存／许可 POC 后才能显式启用 `PlinthEnableXlsx=true`。独立服务测试与可选页面 CI 接线不等于已通过 POC |
| 紧凑／舒适密度 | 三档已接入，完整视觉、DPI 和无障碍调校尚未实测 |
| 无障碍辅助技术 | 有自动化名称／键盘测试源码与矩阵，Windows UIA、Narrator／目标读屏实测未完成 |
| §3.9 可隔离模块失败 | RemoteDemo 注册采用暂存集合，完整成功后追加；注册、首次 ViewModel 构造、View 查找或同步视图构造（含构造期间的 XAML 初始化）失败禁用入口，显示独立提示及关联日志；视图失败异步移除并释放标签，迟到关闭确认不重复释放。核心配置、共享依赖和数据库错误仍传播。C#／桌面验收未执行；不覆盖构造完成后的绑定、事件、异步业务故障或任意模块运行时卸载 |
| 系统 Toast | 尚未实现。已核对 Windows App SDK 官方方案及当前构建／运行时阻塞，见 [通知接入记录](WINDOWS_NOTIFICATIONS.md)。需要真实还原、许可核验、Windows Runtime 初始化、unpackaged 通知注册／激活及安装卸载实测；不默认要求迁移 MSIX，不用临时脚本替代 |
| 单元格内联编辑 | 尚未提供，规范前提是实际业务需求和已验证的成熟开源控件；目前使用标准表单，不能为了勾选条件项自研网格 |

Could（插件隔离、通用 Dock、多数据库客户端 Provider、PDF／打印／条码、离线同步等）没有自动升级为本轮交付范围，继续遵守规范“不提前堆叠框架”的约束。

## 本环境无法关闭的阻塞

工具验证记录：Python 工具测试 63 项全部通过（含原先因缺 PowerShell 跳过的 8 项）；SQLite smoke 14 项通过；源码契约 0 错误，资源／项目检查通过。

2026-11-03 在 Windows 11（10.0.26200，i5-10210U／8 逻辑核／16 GB）与 .NET SDK 10.0.403 上实际执行：

- solution 与 `win-x64` RID 的 restore、locked restore 通过，已生成 3 个 `packages.lock.json`；低危及以上 NuGet 审计无告警；Release 构建 0 错误。
- App 自动回归 897/897、Spreadsheet 47/47、XLSX 启用变体 36/36，TRX 已归档。已修复标签条键盘可达、远程拒绝切换后的表格回退、WAL 用例写连接保持、单实例通知测试续体与 XLSX CRLF 归一化。
- `dotnet format --verify-no-changes` 已通过；测试项目中的 xUnit1051/2031/2017 在 `.editorconfig` 中降为建议，仍保留 IDE 提示。
- 10k CSV／SQLite 性能 POC 通过，各阶段耗时见 [PERFORMANCE_POC.md](PERFORMANCE_POC.md)。
- Release ZIP 与 Setup.exe 已生成并通过 `release_checks`；安装 0、锁文件存在、`--version` 正常；`/D=` 返回 4、运行中安装返回 5、静默升级返回 6、卸载保留用户数据。发布目录无测试程序集或源码。
- 发布 `Plinth.App.exe --version` 输出 `Plinth 0.1.0`、退出 0；未知与重复参数走 stderr、退出 2；空数据目录首次启动建库/设置/日志，二次启动设置不变，窗口运行时查询不打断原窗口。

仍需关闭：干净账户下的目录链接/磁盘满/失败回滚/旧版本升级/重装恢复全矩阵、代码签名、Windows UIA／IME／DPI／读屏与截图、真实 API／IdP、XLSX Office 兼容抽查和系统 Toast。没有可用的实际 API／IdP 配置，远程端到端仍不能验收。

因此当前不能宣称“所有需求完成”或“可以发布”。可复现验证顺序与命令见 [发布交接](release/RELEASE.md)，桌面矩阵见 [UI_ACCEPTANCE.md](UI_ACCEPTANCE.md)，远程契约见 [REMOTE_DEMO.md](REMOTE_DEMO.md)。补齐上述环境和实际证据后，应继续处理真实构建／测试暴露的问题，而不是仅修改本表状态。

当前没有新的、可以脱离指定 SDK、Windows、真实 IdP、远端 API 和真实依赖还原直接完成的高价值 Must 源码缺口。下一阶段应按顺序执行指定 SDK 的 locked restore/build/test，随后运行 10k CSV 性能 POC 和 XLSX 变体，最后完成 Windows UIA、DPI、安装升级、Toast 及真实 API／IdP 验收。内联编辑继续保持未实现，避免在没有成熟控件、许可证和真实业务需求前自研网格。
