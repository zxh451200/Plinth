# 实现与验收状态

更新：2026-11-09。源码接入不等于构建通过或产品验收通过。

阅读口径：本文件包含历史实施记录。下文旧条目中的“缺少 SDK／锁文件”“C# 尚未运行”等描述，已由本页“2026-11-04 Windows 实机执行”“2026-11-08 Windows 实机构建与测试收口”“2026-11-08 真实数据库、升级与签名验收”和 [当前需求核对表](REQUIREMENTS_STATUS.md) 中的执行记录更新，不再代表当前全局状态。后续新增变更仍须另行验证。

## 2026-11-09 启动失败安全报错与故障演练

- **启动失败不再裸崩**：`Program.Main` 的启动 catch 原先 `Log.Fatal` 后 rethrow（磁盘满演练实测进程 0xE0434352 终止、无图形提示）。现生成 `STARTUP_FAILED` 安全摘要（双语 `startup.error.body`／`startup.error.next`＋错误码＋关联 ID，标记可重试），Fatal 日志带同一关联 ID 与受控异常栈；Windows 交互会话用原生消息框展示（新增 `StartupErrorReporter`；主窗口尚未创建，ErrorDialog 不可用），非交互环境写 stderr；进程受控退出 **1**。弹窗不含原始异常、路径或凭据。
- **测试与门禁**：`UnhandledErrorReporterTests` 新增 3 项（双语摘要／唯一关联 ID／可重试；弹窗文本含正文＋建议＋错误码＋关联 ID）。负控制把 `BuildText` 变异为只返回正文，目标用例如期转红，按字节还原（md5 一致）后复跑回绿。Release 构建 0 错误；App 全量 **1195 通过／2 项按设计跳过／0 失败**；`dotnet format --verify-no-changes`、源码契约 0 错误、532 双语键／460 引用键资源检查通过。
- **启动失败前后对照演练**（同一注入、同一仪器）：当前用户非管理员，无法挂载满卷 VHD；改用数据目录 ACL 拒写（icacls deny WD/AD）注入，`Migrator.Migrate` 抛 `SqliteException`，与磁盘满共用同一异常→用户路径。旧发布包（2026-11-09 重打包产物）：无对话框、退出 -532462766（0xE0434352），复现原缺陷；新构建：原生消息框显示「应用启动失败…错误码 STARTUP_FAILED／关联 ID」，关闭后退出 **1**，当日日志 Fatal 行关联 ID 与弹窗一致（`983fdce2…`），异常栈只含受控方法帧。演练后 ACL 已还原。证据：`D:\tmp\plinth-startup-drill\result.txt`／`result2.txt`。边界：这是 ACL 拒写注入，不是物理满卷；磁盘满的旧行为记录见下文，二者走同一 catch。
- **日志目录权限拒绝降级（§13.3 权限不足项）**：对 `%LOCALAPPDATA%\Plinth\logs` ACL 拒写后以临时数据目录启动新构建：stderr 恰好一次 `PLINTH_LOG_WRITE_FAILED: bounded in-memory metadata only.`，进程 14 秒后仍存活，数据目录正常生成 plinth.db／WAL／SHM 与 settings.json；ACL 测后还原（前后一致）。证据：`D:\tmp\plinth-logdeny-drill\result.txt`。边界：日志卷磁盘满的**运行中**写失败仍需可提权的满卷环境，未覆盖。
- **交互式升级写入失败（§13.3 安装/升级）：不覆盖、备份保留、退 7**：机器演练前无 Plinth 安装与用户数据。`/S` 安装 0.1.0（退 0，`plinth.install.lock` 存在），首次 GUI 启动生成 `plinth.db` 后正常退出；对 `%LOCALAPPDATA%\Programs\Plinth` 加**目录级** ACL（`icacls /deny user:(WD,AD)`，不继承到既有文件，故锁与旧 EXE 的写句柄检查仍通过）。交互升级依次确认「请先保存业务修改」和「备份校验通过」（自动 ZIP：Application 0.1.0、Schema `20261108125013_DemoTypedColumns`、Files/Bytes/UTC），`install_begin` 的 `GetTempFileName $INSTDIR` 失败进入 `install_failed`；MUI 停在「安装中止」页，点「取消／关闭」后 `.onInstFailed` 删除 `plinth.install.lock`、显示「安装未完成。用户数据未删除…」，进程退 **7**。293 个程序文件 SHA-256 前后一致（diff 0；232,159,345 B），备份 ZIP 保留在 `%LOCALAPPDATA%\Plinth-Upgrade-Backups`，`plinth.db` 未动；还原 ACL 后旧 `Plinth.App.exe --version` 退 0。随后 `/S` 卸载退 0（程序目录／卸载键／开始菜单清理、用户数据保留）；演练数据目录与备份目录已删除，机器恢复到演练前（无安装、无数据目录、无进程）。两次有效轮次终态一致；另有 1 轮驱动未关闭中止页时停在该页（MUI 等待用户确认，非死锁）。边界：ACL 目录拒写注入，不是物理满卷；中止页由用户手动关闭；旧版（不支持备份命令）升级路径与真实整包回滚仍未演练。证据：`D:\tmp\plinth-upgrade-drill\result-run1.txt`、`result.txt`、`inventory-before.txt`、`inventory-after.txt`。
- **进程意外退出与 WAL 恢复（§13.3 进程意外退出）**：Release 构建＋临时数据目录启动，确认库就绪后由独立连接写入并提交 1 条 `demo_records` 探针记录（`PRAGMA integrity_check=ok`、迁移 4 条），再 `Stop-Process -Force` 模拟非正常终止（退出 -1；库 4,096 B、WAL 86,552 B、SHM 32,768 B 留盘）。重启后应用正常启动（无错误框、进程稳定），`integrity_check=ok`、迁移仍 4 条、探针记录仍为 **1** 条——已提交数据经 WAL 恢复、未重建库；随后正常退出（0）。边界：进程强杀模拟，不是物理断电，未覆盖写入瞬间的磁盘故障。
- **备份损坏门禁（§13.3 备份损坏）**：同一构建生成升级前 ZIP（2,466 B），`--inspect-backup-before-upgrade` 退出 0 并写出报告（Application 0.1.0、Schema `20261108125013_DemoTypedColumns`、Files 3）。按字节翻转 ZIP 中段 → 退出 **8**；ZIP 重写把 `database.db` 改为 8 字节 → 退出 **8**；两个失败用例均未产生报告文件。安装器 `upgrade_verify_backup` 要求该命令退出 0，非 0 即转 `upgrade_backup_failed` 并停止覆盖。证据：`D:\tmp\plinth-crash-backup-drill\result.txt`（探针轮）、`result-run0.txt`。边界：损坏样本为本地构造，不是磁盘坏道；安装器手选备份的交互未在本轮自动化。

## 2026-11-09 真实 GitHub CI

- **真实 GitHub CI**：提交 `0ae0af8` 推送触发 Actions run [`42525487305`](https://github.com/zxh451200/Plinth/actions/runs/42525487305)；`gate (windows-latest)`、`gate (ubuntu-latest)` 和 6 个 pilot job（dock／runtime-plugins／offline-sync × Windows／Ubuntu）全部通过。Windows gate：Python 工具 107（1 项环境跳过）、SQLite smoke 15、restore／format／build、App **1192 通过／2 跳过**、Spreadsheet 48、XLSX 36；Ubuntu gate：Python 107（2 项环境跳过）、SQLite 15、App **1191 通过／3 跳过**、Spreadsheet 48、XLSX 36。Pilot：Dock 15、RuntimePlugins 36（Linux 跳过 18 个进程隔离用例，保留构建/校验路径）、OfflineSync 8；TRX/xunit 日志与 pilot 证据作为 Actions artifact 归档。
- **跨平台门禁修复**：Python 3.12 显式 UTF-8 读取（Windows GBK）；pilot 项目 `dotnet format` 与 xunit v3 `--report-xunit-trx`；Linux 单实例停止将命名管道 `SocketException` 视为正常关闭；runtime-plugins 进程隔离测试在 Linux 动态 skip；Ubuntu 区域标题缓存键包含 UI culture、缺少中文显示数据时 skip；gate 结果目录改为绝对路径以归档 TRX/日志。
- **干净账户安装器冒烟（2026-11-09）**：在本地标准用户 `PlinthClean`（非管理员）上用现有 0.1.0 Setup（2026-11-08 产物，per-user NSIS）执行 `/S` 安装：退出 0、安装 302 文件、`Plinth.App.exe --version` 退出 0；HKCU 卸载键、开始菜单、AUMID 与 `plinth://` 协议写入。`/S` 卸载退出 0，程序目录、卸载键、开始菜单、AUMID、协议删除；`%LOCALAPPDATA%\Plinth` 数据标记保留。演练后账户与配置文件删除。边界：这是 0.1.0 安装器在干净账户下的安装/卸载冒烟；登录门禁在当前源码/应用级测试中，不在这份旧安装包；安装器/升级在磁盘满、旧版升级/恢复与真实无 SDK 机器的完整矩阵仍未覆盖。证据：`D:\tmp\clean-install-orchestrator.txt`、`D:\tmp\clean-install-profile-cleanup.txt`。
- **磁盘满安装器（2026-11-09）**：PlinthClean 标准用户的 Local AppData shell 文件夹重定向到独立 300MB VHD（X:），填到 0 可用；`/S` 安装 0.1.0 Setup 退出 **7**，X: 安装目录仅 1 个部分文件、`Plinth.App.exe` 不存在，HKCU 卸载键/开始菜单/AUMID/`plinth://` 协议均未写，C: 未产生安装。随后解挂/删除 VHD、删除账户与配置文件。边界：这是首次安装在磁盘满目标上的失败路径；安装器/升级在磁盘满下的升级/回滚、旧版创建备份后升级/恢复仍未覆盖；Setup 是 0.1.0 产物。证据：`D:\tmp\installer-diskfull-result.txt`、`D:\tmp\installer-diskfull-child.out.txt`。

## 2026-11-09 桌面启动性能

- 在 Release 构建、非交互会话、临时数据目录实测（进程 StartTime 到日志事件，UTC 对齐）：冷启动（空目录，含 4 个迁移）应用启动日志 0.87s、数据库就绪 6.10s、主窗口打开 9.51s；暖启动应用启动日志 0.66s、数据库就绪 4.47s、主窗口打开 7.45s。
- 拆分：暖启动数据库初始化约 3.8s、主窗口初始化约 3.0s；冷启动迁移把数据库就绪增加约 1.6s。`SettingsViewModel.Prewarm` 已在后台线程，不阻塞窗口；`App` 新增“数据库已就绪”与“主窗口已打开”诊断日志。
- 边界：非交互会话只测到主窗口 `Opened`，未测首帧绘制、滚动/编辑内存、低配设备与长期资源趋势；单机数字不代表 GA 性能门槛。后续可评估把编译期模型/迁移检查从每次启动移到测试/CI，或延迟/缓存窗口初始化。

## 2026-11-09 发布补充许可库存

- `reviewed-materials/inventory.json`（schema 1）实际列出 8 个组件及正文/哈希：runtimepack.Microsoft.NETCore.App.Runtime.win-x64 10.0.14（MIT + 包内 ThirdPartyNotices）、NSIS 3.11（zlib/libpng）、Skia 3.119.4（BSD-3-Clause + ThirdPartyNotices）、ANGLE 2.1.27548.20260419（BSD-3-Clause）、HarfBuzz 8.3.1.3（Old MIT + HarfBuzzSharp MIT）、SQLite 3.53.3（Public Domain）、Roboto Light 2.137（Apache-2.0）、CarlitoBare 1.104（OFL-1.1）；`supplemental_compliance.py --validate` 通过。
- runtime pack 直接取自 NuGet 包 `Microsoft.NETCore.App.Runtime.win-x64/10.0.14` 的 `LICENSE.TXT`（MIT）与 `THIRD-PARTY-NOTICES.TXT`；A) 曾使用 SDK 安装目录的 Microsoft .NET Library 条款，已确认不是 runtime pack 正文并替换。生成器新增严格对账：身份/版本一致、`category=runtime`、同时含 LICENSE/NOTICE，且正文与包内 nuspec 许可及根目录 LICENSE/NOTICE 逐字节一致、deps 列出的 187 个 runtime/native 文件在发布目录中非空存在时，才把缺口记入 `reconciled_publish_packages`（含文件路径与 SHA-256），不影响 `distribution_inventory_complete`／`license_review_approved=false`；缺包目录、nuspec/正文哈希不符、发布文件缺失或为空时仍为 uncovered。
- 字体核查推翻了“纯系统字体”的旧结论：`Avalonia.Dialogs.dll` 内嵌 Roboto Light 2.137，`ClosedXML.dll` 内嵌 CarlitoBare 1.104 四字重，两个 DLL 均在发布 ZIP 中；版本取自从 DLL 提取的字体 name 表。Carlito 的 OFL 含 Reserved Font Name "Carlito"，派生名 "CarlitoBare" 是否满足 RFN 条款属法务判断；图标仍为应用自有 `plinth.ico`。
- 实跑 `third_party.py --assets src/Plinth.App/obj/project.assets.json --output artifacts/compliance-supplemental --publish-directory artifacts/publish/win-x64 --supplemental-compliance-file reviewed-materials/inventory.json`：生成 91 个 NuGet 包 + 8 个补充组件的 notices/SBOM/coverage（528,949／506,086／142,305 B；该次 ClosedXML 仍在发布图中，默认包最终 83 NuGet，见下）；补充材料专项测试 34 项、发布工具测试 12 项通过；`runtimepack.Microsoft.NETCore.App.Runtime.win-x64/10.0.14` 由 uncovered 转为 reconciled（包内许可/通知与 187 个发布文件逐项对账，仍非许可审查通过）。工具只验证身份/路径/原文/哈希，不代替法务审查。

## 2026-11-09 发布包重打包与法务审查证据

- 安装 NSIS 3.11（`D:\tmp\nsis`）后用 `pwsh` 7 与 `publish.ps1 -SupplementalComplianceFile reviewed-materials/inventory.json -RequireInstaller` 重建默认 0.1.0（XLSX 关闭）：ZIP 82,484,177 B（SHA256 `25af0958d64aaa54ea8e0285d17758caf152bfe99fa1da19d1f1c1737aefee6a`）、Setup 55,889,831 B（SHA256 `d44869a1c7f00d7e0f95e7b4a10c7e216534f782ca0966d9f755c63c81268229`）；`release_checks.py package` 两个产物通过。安装器随后为重编译加入旧版残留清理（ZIP 未变）。
- 默认关闭 XLSX 时对 ClosedXML 使用 `ExcludeAssets=runtime`，`publish.ps1` 增加“关闭时不得出现 ClosedXML.dll／启用时不得缺失”门禁；发布目录、ZIP、安装器与安装后目录均无 `ClosedXML.dll`，报告为 83 NuGet + 8 补充组件，`excluded_restored_packages` 含 ClosedXML 家族。
- ZIP 内报告 `uncovered=[]`、`reconciled=1`，两个批准标志仍为 false；安装器内嵌 notices/SBOM/coverage 的 SHA-256 与 `artifacts/compliance` 一致（`3b91…`／`321a…`／`fcfb…`）。
- 安装器实机冒烟：安装 `/S` 退出 0、`Plinth.App.exe --version` 输出 `Plinth 0.1.0`、`/D=` 其他目录退出 4、运行中再安装退出 5、静默覆盖安装退出 6、应用退出后卸载退出 0；`unknown-user-file.txt` 与用户数据保留，卸载注册表键与 `plinth://` 协议清理，无开始菜单/桌面快捷方式残留。运行中静默卸载在 26 s 轮询内文件/注册表不变（拒绝），但 NSIS 卸载 stub 返回 0，0 不是成功信号，验收以目录/注册表状态为准。旧版残留清理：新包不含 ClosedXML 时安装器删除旧版 `ClosedXML.dll`／`ClosedXML.Parser.dll`／`DocumentFormat.OpenXml*.dll`／`ExcelNumberFormat.dll`；实测卸载保留 dummy `ClosedXML.dll`，重装后删除且 `unknown-user-file.txt` 保留。
- 法务审查证据包 [LEGAL_REVIEW.md](release/LEGAL_REVIEW.md) 已更新：默认包已排除 ClosedXML/Carlito，RFN 风险仅影响 `-EnableXlsx` 变体；签核栏为空。代码签名方面 `signtool` 可用但 Setup `NotSigned`，仅有测试证书；正式 CA 签名、`-EnableXlsx` 变体法务结论与成功交互升级（需旧候选）仍待。

## 2026-11-09 XLSX 真实宏样本

- 临时将 Excel 12.0 的 `AccessVBOM` 设为 1，用 Excel COM 创建带 VBA 模块与 `MsgBox` 的真实 `.xlsm`（13,640 B），随后恢复原设置（值不存在）。
- 产品 `SpreadsheetService.ImportAsync` 按扩展名拒绝该真实样本；`SpreadsheetServiceTests.Real_excel_macro_workbook_is_rejected` 在设置 `PLINTH_XLSM_SAMPLE` 时执行并通过（Spreadsheet 49/49），CI 无样本变量时按设计 skip，不计为通过。产品不导入、不执行宏；ClosedXML 许可终审仍属发布决策。

## 2026-11-09 本地登录门禁

- 新增受信任部署开关 `LocalLogin.Enabled`（默认 false，`deployment.example.json` 已含 `LocalLogin`）。关闭时启动路径与既有版本一致；开启时 `App` 先显示登录窗口，登录成功后才创建主窗口，关闭登录窗口视为退出；`LocalLogin.Username` 只作为首次设置的用户名预填。
- 首次开启且数据目录没有 `login.json` 时显示设置界面（用户名、密码、确认）；密码使用 PBKDF2-HMAC-SHA256（210,000 次、16 字节盐、32 字节哈希）保存为 `pbkdf2-sha256$...`，不存明文，验证使用固定时间比较。凭据文件位于数据目录 `login.json`（Unix 权限 0600，原子替换），`BackupService` 的备份清单不包含它；删除该文件可重置为首次设置。
- 用户名 1–64 字符、密码 8–128 字符，拒绝控制字符；错误按用户名/密码规则、错误凭据、凭据损坏/读写失败分别提示。登录门禁是桌面端使用门槛，不加密数据库/设置，也不改变远程 OIDC 身份、权限、审计中的当前用户；需要数据保护仍依赖操作系统账户、磁盘加密或远程身份。
- 测试：`LocalLoginTests` 覆盖首次设置、哈希不含明文、正确/错误密码、用户名和密码规则、损坏凭据重建、VM 设置/登录/不一致密码；`DeploymentConfigurationTests` 覆盖开关与非法用户名；全量 App 1192 通过、2 项按设计跳过、0 失败（Windows 本地），源码契约 0 错误、改动文件 `dotnet format` 通过。应用级启动（`PLINTH_LocalLogin__Enabled=true` + 临时数据目录）日志出现“本地登录门禁已启用，等待登录”，进程保持运行且数据库/设置已创建；登录窗口在非交互会话无可见窗口标题，UI 细节由 ViewModel/服务测试覆盖。

## 2026-11-08 类型化单元格编辑补齐

- Demo 记录新增 `Quantity`（int）、`DueDate`（DateOnly?）、`Priority`（枚举，按名字存储）、`Active`（bool）。表格单元格分别使用 Avalonia 核心 `NumericUpDown`、应用内 `DateField`、`ComboBox` 与 `CheckBox`；表单提供同名字段。四类编辑器与文本单元格共用一份草稿快照，显式保存／取消、脏状态、版本冲突和字段级错误（数量范围、日期范围、枚举合法性）与其他列一致，失焦不提交。
- 迁移：SQLite `20261108125013_DemoTypedColumns`（固定 `ALTER TABLE`，旧行默认 0／空／Normal／true）、MySQL `20261108125056_DemoTypedColumns`、PostgreSQL `20261108125128_DemoTypedColumns`（增列并设默认值）；三个 Provider 的模型快照同步。SQLite 启动结构校验增加最新版本分支，历史库仍先建安全副本再向前迁移。
- CSV 导出扩展为 7 列（数量、`yyyy-MM-dd` 日期、枚举名、`true|false`），导入仍只映射编码／名称／备注三列，不静默改写类型列；旧三列文件不受影响。PDF 固定模板保持原有列。
- 回归：`DemoTypedInlineEditingTests` 覆盖草稿隔离、保存持久化、越界字段拒绝、取消丢弃和仅选中行渲染；`DatabaseSafetyTests` 断言旧库升级后的类型列默认值；`sqlite_migration_smoke.py` 新增增列迁移用例（15 项）。全量 App 1183 项（1181 通过、2 项按设计跳过）、`dotnet format`、510 双语键／452 引用键资源检查及源码契约 0 错误。负控制把更新分支的 `Active` 固定为 `true`，专项测试如期转红，随后按字节还原并核对 md5。

## 2026-11-08 XLSX 真实 Office 样本复核

- 用本机 Excel COM（`Visible=false`、`DisplayAlerts=false`、`AutomationSecurity=ForceDisable`）生成真实 Office 文件：文本工作簿、外部链接工作簿、宏启用扩展。服务实测：文本工作簿导入 2 行 0 错误；外部链接工作簿被 `External workbook relationships are not supported.` 拒绝；`.xlsm` 被扩展名拒绝。
- 发现并修复真实 Excel 包被误拒：Office 会写 `xl/`、`docProps/` 等零长度目录条目，旧白名单只允许 `.xml`／`.rels`／`.psmdcp`，导致所有真实 Excel 保存的 xlsx 被判“非法部件”。现在零长度目录条目跳过，带数据的同名目录条目仍拒绝；`SpreadsheetServiceTests` 新增回归（套件 48 项），负控制移除跳过后如期转红，按字节还原。
- 2026-11-09 已解除真实宏样本阻塞，见下方“XLSX 真实宏样本”；合成 `vbaProject.bin` 的拒绝路径仍由现有测试覆盖。
- 许可证据：`.nuget/packages/closedxml/0.105.1/closedxml.nuspec` 为 `<license type="expression">MIT</license>`，包内无独立 LICENSE 文件；notices／SBOM 由现有发布工具生成。终审仍属发布决策。

- 测试稳定性：全量并行执行时，`SettingsTests.Region_titles_follow_the_interface_language` 会被并发用例把进程级 `Loc` 改回英文而偶发红。现把 19 个会调用 `Loc.Initialize` 的测试类收进 `[CollectionDefinition("Localization", DisableParallelization = true)]` 集合，与其余集合串行隔离；随后全量 1183 项（1181 通过、2 跳过）连续两次通过（约 5／9 分钟），未再出现该竞争。

## 2026-11-08 二开文档照抄验证

- 把当前工作区（排除 `.git`／`bin`／`obj`／`artifacts`）复制到 `D:\tmp\plinth-newcomer`，严格按 GETTING_STARTED 执行：`dotnet restore --configfile scripts/nuget.config` 成功；`dotnet run --project src/Plinth.App -- --version` 输出 `Plinth 0.1.0`、退出 0。
- 按「新增一个页面」新增 `NewcomerProbeViewModel`／`NewcomerProbeView`、中英资源、`AddTransient` 和 `NavigationItem` 注册；定向回归 `LocalizationTests`、`NavigationStructureTests`、`ShellTests` 共 53 项通过。视图命名与 ViewModel 的 `ViewLocator` 对应关系工作正常。
- 按文档 `dotnet ef migrations add ... --project ... --startup-project ...` 实际执行失败：`More than one DbContext was found`。项目有 SQLite／MySQL／PostgreSQL 三个上下文，命令必须带 `--context AppDbContext --output-dir Migrations`；改正后生成成功。`pwsh scripts/add-migration.ps1 NewcomerScript`（`dotnet-ef` 在 PATH）也成功。
- 据此修正文档：GETTING_STARTED 的迁移命令、页面注册示例（`Loc.Get`＋`TitleKey`＋`AddTransient`＋ViewLocator 命名）、项目结构范围说明，以及“不能编译／POC 未跑／Toast 未实现”等过期状态；SECOND_MODULE 的迁移命令与测试执行状态同步修正。临时副本在验证后删除，不进入仓库。

## 2026-11-08 100k／1m 真实分页 POC 执行

- 用本机已受信任的 ASP.NET 开发证书（`localhost`，未改动证书库）在 `127.0.0.1:5443` 起临时 Node HTTPS 只读 API，契约按 REMOTE_DEMO：`GET /api/demo-records?page&pageSize&search&sortField&descending`，只返回 `page/pageSize/totalCount/items`，记录为 `id/code/name/remark/version`。数据集按索引确定性生成，总量分别设为 100000／1000000；Token 只放在进程环境，不写入文件或日志。
- 按文档执行 Release POC（`-p:PlinthRunPoc=true`，`RemotePagingPocTests`），两档均通过：100k 首／中／末页（1／1000／2000）**3496.8／22.0／9.2ms**；1m（1／10000／20000）**2710.2／14.8／3.9ms**。每次 50 行，总数与页元数据一致，采样页 ID 不重复；测试宿主工作集约 46→58 MB，托管内存约 1.1→1.7 MB。首次请求含 TLS 握手与 JIT。
- 首轮数据集把第一条记录的 ID 发成空 Guid，客户端按契约拒绝并判失败；修正测试数据后通过。这是测试服务端的数据生成错误，产品代码未改动，同时验证了 `RemoteRecord` 对 Guid.Empty／字段集合的校验。
- 边界：临时本机 API 不是外部生产服务，结果只证明客户端只取当前页并对齐契约，不代表服务端延迟／吞吐或桌面性能达标；OIDC、冷启动、首屏、滚动与长期资源趋势仍未测。测试服务、证书私钥与临时文件在验证后删除。

## 2026-11-08 真实数据库、升级与签名验收

- **真实 PostgreSQL 服务器验收**：便携版 PostgreSQL 17.5（EDB binaries zip，解压临时目录、独立实例端口 5433、专用账号与密码）上初始化专用库 `plinth_integration`，显式设置 `PLINTH_TEST_DATABASE_ISOLATED=true` 与 `PLINTH_TEST_DATABASE_PURPOSE=plinth-integration` 后，`DatabaseProviderIntegrationTests.PostgreSql_migrates_and_round_trips_a_demo_record` 真实执行通过（24 秒）：独立迁移集应用到真实服务器、重复初始化幂等、`HasPendingModelChanges` 无模型漂移、Demo 记录写入读回（CodeKey 与中文）、本运行数据清理。
- **真实 MySQL 服务器验收**：官方 MySQL 8.0.46 winx64 zip 便携实例（独立端口、`--initialize-insecure` 仅限本机、专用库 `plinth_integration`；3307 端口与 phpStudy 的 MariaDB 冲突改用高位端口），`MySql_migrates_and_round_trips_a_demo_record` 真实执行通过（1 分 24 秒），同上覆盖迁移、幂等、模型漂移与 CRUD。MariaDB 不等于 MySQL，未用于 MySQL 验收；本机 phpStudy/MariaDB 的 root 凭据不可用，未尝试绕过。
- **升级与升级前备份完整演练**：以当前源码构建 0.1.0 与 0.1.1（同一提交、仅版本号提升）两个 NSIS 安装包。0.1.0 静默安装并首次启动创建默认数据目录（`%LOCALAPPDATA%\Plinth`）；应用运行中执行安装被拒绝（退出 5，程序文件与数据不变）；空闲后静默升级被拒（退出 6，`plinth.db` SHA256 `F4256F56…` 不变）；交互升级（键盘驱动向导）退出 0：安装器调用旧版 `--backup-before-upgrade` 生成 ZIP，新版 `--inspect-backup-before-upgrade` 校验通过（Application 0.1.0、Schema `20261102000000_CodeIdentityKeys`、3 文件 50015 字节），覆盖安装后版本 0.1.1、数据库哈希不变、备份保留在 `%LOCALAPPDATA%\Plinth-Upgrade-Backups`。演练后卸载并删除数据目录，恢复机器原状。
- **签名管线**：`New-SelfSignedCertificate`（CurrentUser，CodeSigningCert）+ `Set-AuthenticodeSignature`（SHA256）对 Setup 与 EXE 签名，自签根加入当前用户受信任根后 `Get-AuthenticodeSignature` 返回 **Valid**；修改已签名文件单字节后签名失效。边界：这是自签名管线验证，不是受信任 CA 的正式分发签名（签名方案属正式分发决策，见根规范）；ZIP 不属于 Authenticode 签名对象。
- **XLSX 收尾**：宏（`xl/vbaProject.bin`）、外部关系（`evil.rels`）、公式缓存（`.FormulaA1`）与 `.xlsm` 扩展名的导入拒绝测试在套件中真实通过（Spreadsheet 47/47 的一部分）；ClosedXML 0.105.1 许可已进入 NuGet 库存与 notices/SBOM 生成，终审仍属发布前人工项。
- **通知横幅演练观察（2026-11-08）**：0.1.0 静默安装后经随包 `notify.ps1` 发送 toast 成功（exit 0，AUMID `Plinth`）；UIA 桌面搜索未发现弹出横幅（CoreWindow 0、目标文本不存在）——本机疑似开启勿扰/焦点助手，通知只进入系统通知中心，符合文档既有口径"横幅物理点击与勿扰策略下的人工体验需人工验收"。强行自动化通知中心点击属于模拟而非物理点击，不记为已验收。演练后卸载，`plinth://` 协议键与 AUMID 注册表项均被精确清理（卸载清单契约再次验证）。
- **干净账户与独立小卷磁盘满演练（2026-11-08）**：以本地标准用户 `PlinthClean`（仅 Users 组，非管理员）运行自包含发布版 `artifacts/publish/win-x64/Plinth.App.exe`，数据目录放在独立 300MB 固定 VHDX（NTFS，卷标 `PLINTH_DRILL`，挂载 X:）；运行前清空该用户的 `%LOCALAPPDATA%\Plinth` 与 `%APPDATA%\Plinth`。干净首启：`--version` 退出 0；GUI 首次启动应用 3 个迁移，`X:\clean-drill` 生成 `plinth.db` 4,096 B、`plinth.db-wal` 74,192 B、`plinth.db-shm` 32,768 B 与 `settings.json` 330 B，进程 25 秒后仍在运行（演练主动结束；非交互会话，未做窗口内容验收）。空卷上以同一账户执行 `--backup-before-upgrade`：退出 0，生成 1,820 B ZIP，源库 SHA-256 `52A37144…` 不变。
- **磁盘满演练结果**：把 X: 填到报告可用空间 0 B 后，GUI 启动在 `DatabaseInitializer.Initialize` 的 `Migrator.Migrate` 处抛出 `Microsoft.Data.Sqlite.SqliteException`（`SQLite Error 13: 'database or disk is full'`），进程以 .NET 未处理异常码 `-532462766`（`0xE0434352`）退出，留下 0 字节 `plinth.db`；未捕获到产品自身的图形化错误提示（进程直接终止，Windows Error Reporting 的交互行为未评估）。同卷 CLI 备份退出 **8**（`升级前备份创建或校验失败。`），不生成 ZIP，源库 SHA-256 不变，只留下空的 `data` 目录。释放空间后，修复前再次启动被该 0 字节库以 `InvalidDataException`（`数据库不符合已知六列或完整审计历史结构，拒绝自动建立迁移基线。`，`ValidateLegacySchema`）拒绝，并同样以 `0xE0434352` 退出。
- **0 字节库恢复修复（2026-11-09）**：`DatabaseInitializer` 在启动时把 0 字节且无 `-wal`／`-shm`／`-journal` 的库视为未完成初始化，记录 Warning 后删除并重新迁移；只要存在任一 SQLite 日志就继续拒绝，避免丢弃可能含已提交数据的日志。`DatabaseSafetyTests` 新增 1 个重建用例和 3 个带 sidecar 的拒绝用例，`--filter FullyQualifiedName~DatabaseSafetyTests` 31 项通过；以当前源码 Release 构建做应用级复测：0 字节 `plinth.db` 启动后重建为 4,096 B（WAL 74,192 B、SHM 32,768 B），CLI 备份退出 0（1,912 B ZIP），日志记录 `检测到 0 字节数据库且无 SQLite 日志，按未完成初始化处理并重新建库。`。
- **日志目录故障降级（2026-11-09）**：将当前用户的 `%LOCALAPPDATA%\Plinth\logs` 临时移走并用同名文件占位（模拟目录创建 IO 故障），以当前源码 Release 构建启动应用并把数据目录设在临时目录：应用保持运行并正常生成 `plinth.db` 与 `settings.json`，stderr 仅输出一次 `PLINTH_LOG_WRITE_FAILED: bounded in-memory metadata only.`；测试后原日志目录已恢复。另以只读日志文件（`plinth-YYYYMMDD.ndjson` + `ReadOnly`）重复验证：应用同样保持运行、成功建库/设置，stderr 标记出现，原日志目录恢复。边界：日志目录创建 IO 故障与只读日志文件已实测；日志卷磁盘满未单独测。证据：`D:\tmp\plinth-logfail-result.txt`、`D:\tmp\plinth-logro-result.txt`。
- **结论与边界**：CLI 备份在磁盘满时安全失败（非零退出、无产物、源库不变）；GUI 启动为 fail-fast，不以坏库继续运行，但不是图形化优雅失败。失败留下的 0 字节库曾阻塞后续启动，现已按未完成初始化自动重建（见上条修复）。日志目录位于该用户的 `%LOCALAPPDATA%`（C: 有空间），日志目录创建 IO 故障与只读日志文件已实测；日志卷磁盘满未覆盖；安装器在磁盘满下的升级/回滚、旧版升级与重装恢复未覆盖（干净账户安装/卸载与磁盘满首次安装失败已实测）；演练机仍装有 SDK，不是“真实无 SDK 机器”。证据：`D:\tmp\drill-all-result.txt`、`D:\tmp\drill2-result.txt`、`D:\tmp\drill3-result.txt`、`D:\tmp\plinthclean-logs3\`、`D:\tmp\plinth-logfail-result.txt`、`D:\tmp\plinth-logro-result.txt`（演练后账户与卷已删除）。
- **XLSX 内存 POC**：满额上限负载（5000 行 × 3 列 ~450 字符文本）经真实服务 PreviewAsync/ImportAsync，测试进程分页内存峰值 **61 MiB**（基线 43 MiB、增量 18 MiB）——有界，符合 5000 行／5 MiB／32 MiB 展开上限的预检设计。POC 用临时测试采样，跑完已删除，数字如实记录于此。
- **自建本地 OIDC IdP 与契约 API 的真实端到端**：为关闭"真实 IdP 验收"清单，按"没有环境就自建测试租户"的口径，在 `D:\tmp\idp`（不入库）实现了本地专用 IdP（HTTPS 5443、自签证书、RS256/JWKS、S256 PKCE、授权码一次性、刷新令牌轮换与登出吊销）及符合 [REMOTE_DEMO 契约](REMOTE_DEMO.md) 的 API（服务端分页/排序/搜索、强 If-Match、`demo:read/save/delete` 权限、409/412/401）。使用产品同库的 Duende OidcClient + 产品 `SignedIdentityTokenValidator`（RS256/issuer/audience/过期），经 **13 项断言全部通过**：discovery、授权码+PKCE 交换、id_token 签名/audience/nonce 校验、身份与权限 claim、UserInfo、API 新建 201/分页 200/If-Match 更新/旧版本 412/无令牌 401、刷新轮换且旧令牌拒绝、end_session state 校验、登出后刷新令牌吊销。loopback 回调由测试 IBrowser 承载（产品注释认可的注入方式）：真实 HTTPS authorize 302 + 真实 loopback 监听捕获；真实浏览器 UI 环节（人输入账号）以自动同意测试租户替代。调试中修正的三个 IdP 自身缺陷（id_token aud 误用 API audience、iat 序列化为 ISO 字符串违反 NumericDate 规范、API 排序键大小写）均发生在测试服务端，产品代码未改动。浏览器内人工输入、桌面 UI 登录流程与真实第三方 IdP 租户管理不在本演练范围。

**仍未验收**：日志卷磁盘满（日志目录创建 IO 故障、只读日志文件与目录 ACL 拒写已实测）与安装器/升级在磁盘满下的升级/回滚、旧版升级/恢复（应用启动、CLI 备份、安装器干净账户安装/卸载、磁盘满首次安装失败与目录拒写注入下的交互升级写入失败已实测——后者退 7、程序文件哈希不变、备份保留、锁移除，见「启动失败安全报错与故障演练」节；GUI 启动失败裸崩已于 2026-11-09 修复为安全报错）；非 NuGet 许可的法务签核（证据包已出；默认包已排除 ClosedXML/Carlito，`-EnableXlsx` 变体仍待）；正式 CA 代码签名；真实 OS DPI/多显示器/高对比度/IME/Narrator 朗读；通知横幅物理点击；真实第三方 IdP 与符合 REMOTE_DEMO 契约的外部 API 端到端（远程模式未交付时为条件项，本仓库不附带生产 API/IdP）。

## 2026-11-08 Windows 实机构建与测试收口

本轮在 Windows 11（SDK 10.0.403）对 2026-11-04 之后全部未编译增量完成 restore、Release 构建、全套测试、格式与离线门禁。此前记录的“新增 C#／XAML 未编译”状态就此关闭；以下修复为本次实机执行暴露后逐一修正的结果。

- 编译错误修复：`AdvancedTableRepository` 对数组使用 `.Count` 被解析为方法组（改 `.Length`）；`OfflineSyncViewModel.ReconcileAsync` 因 pilot 返回非泛型 `Task` 无法推断 `ExecuteAsync<T>` 类型参数（lambda 显式返回 bool）。
- 警告与路由修复：`DemoListViewModel` 分部方法参数名与生成声明不一致（CS8826）；`ViewLocator.CreateView` 收紧为非空参数（CS8602）；Demo 列表右键菜单改 Tunnel 路由——行内编辑把编辑态单元格换成 TextBox，其类处理器在 Bubble 阶段吞掉 ContextRequested，导致右键已选中行不再打开记录菜单（§2.1/§2.2 回归）。
- 测试缺陷修复（8 组 29 项）：CommandLineTests 升级备份检查在 WAL 连持时以共享读替代 `File.ReadAllBytes`；DatabaseConfigurationTests 脱敏断言改用 `error.Message`（ToString 会含测试方法名）；ShortcutTests 更新为 9 个快捷键（含 ToggleSidebar）；DockOptionalBoundaryTests 异常断言放宽为基类 `ThrowsAny`；NavigationStructureTests 高级表格项按试点门控语义直测 `AddAdvancedTableModule` 并断言默认关闭；UiAccessibilityTests 主操作按钮改取可见项（行内编辑器与表单各有同文案保存按钮）；DemoListPreferencesTests 列宽拖拽按下点移出 Avalonia 12 TableView 分隔线原生死区（实测仅分隔线左侧按压启动拖拽，产品行为符合 §6.2）；RuntimePlugins 超时／取消用例的 PID 文件读端容忍写端独享窗口、夹具写端带重试（Windows 共享冲突）。
- 试点宿主修复：`tools/Plinth.PluginPilot`／`tools/Plinth.OfflineSyncPilot` 共享源码改为显式文件清单，排除耦合 Shell／本地化／权限的模块注册文件；`Plinth.DockDemo` 关闭复核集合改为 `IDockBusinessPane` 接口类型；DockUiTests 对齐真实窗口时序（窗口挂树后再赋 Factory/Layout），修复文档窗格在 headless 下不渲染内容的问题。
- 格式：`dotnet format` 修复上一轮新增文件的全部空白格式问题并通过 `--verify-no-changes` 复验。

执行结果：Release 构建 0 错误；App 1174 项（1172 通过、2 项按设计跳过——`PLINTH_TEST_POSTGRES`／`PLINTH_TEST_MYSQL` 未配置不算通过、0 失败）；Spreadsheet、XLSX 启用变体、Dock 15 项、RuntimePlugins 36 项、OfflineSyncPilot 试点套件全部通过；源码契约 0 错误、498 双语键／440 引用键／39 XML 文件检查通过；Python 工具测试 107 项通过（1 项跳过）、SQLite smoke 14 项通过。修复后全量套件复跑仍全绿。本轮环境曾出现 C 盘零空闲导致 Dock 包恢复失败，清理用户 Temp 与 NuGet HTTP 缓存后恢复；不代表磁盘满故障矩阵已验收。

## 2026-11-08 发布与安装链路实机执行

- `publish.ps1`（无 `-RequireInstaller`）完整执行：11 个项目锁校验、solution 与 win-x64 locked restore、自包含发布、91 个还原 NuGet 包的 notices/SBOM/coverage 生成（NuGet-only 候选；runtimepack 覆盖缺口按设计显式列出，不静默）、ZIP 候选包 `Plinth-0.1.0-win-x64.zip`（SHA256 `2d11cff0…`）通过 `release_checks package`。
- 发布目录命令行契约复验：`--version` 输出 `Plinth 0.1.0` 退出 0；未知参数本地化提示退出 2。
- 安装器：`installer/plinth.nsi` 工作区副本曾被编辑工具剥掉 UTF-8 BOM（HEAD 版本带 BOM），已恢复；便携版 NSIS 3.11（解压到临时目录，未改系统）编译 `Plinth-Setup-0.1.0-win-x64.exe` 成功（1 个未使用标签警告），SHA256 `57a29f98…`，通过 `release_checks package`。
- 安装/卸载冒烟（开发者账户，非干净账户）：静默安装 301 个文件含锁资源、notices/SBOM、`notify.ps1`，`--version` 正常；放置未知文件后静默卸载，程序文件与注册表卸载键删除、未知文件保留。0.0.9→0.1.0 升级与升级前备份演练需要历史版本安装包，仍未执行。
- 性能 POC 复跑：`-p:PlinthRunPoc=true` 的 `PerformancePocTests`（真实 SQLite／CSV 10k 分页／筛选／保存／导出与 5000 行往返）全部场景通过；本轮未重录阶段耗时，最新分阶段数字仍以 [性能 POC](PERFORMANCE_POC.md) 的既有记录为准。
- XLSX Office POC（本机已装 Microsoft Office Excel，"仅有 WPS"的旧记录不再成立）：按产品纯文本语义用发布目录同版本 ClosedXML 生成 fixture（中文、18 位长数字文本、日期文本、公式注入文本、`@` 文本格式），真实 Excel COM 打开读取全部正确（18 位长数字无损——数值单元格则受 xlsx 双精度限制只保留 15 位有效数字，产品按设计只接受文本单元格并拒绝数值单元格）；Excel 另存 xlsx 后由 ClosedXML 读回全部无损；截断损坏文件被 ClosedXML 拒绝。宏／外部关系在真实 Office 中的行为、内存 POC 与许可审查未完成，故 XLSX 仍保持默认关闭，不据此启用 `PlinthEnableXlsx`。POC 脚本与产物在 `D:\tmp\xlsx-poc\`（临时目录，不入库）。
- PowerShell 透传与完整验收入口：本机已有 PowerShell 后，Python 工具测试 107 项全部真实执行（原 8 项因缺 PowerShell 跳过的用例已执行，仅剩 `test_nsis_compiles_with_a_generated_publish_manifest` 一项因 PATH 无 makensis 跳过——把便携版 NSIS 3.11 加入 PATH 后该项也真实执行通过，含生成清单编译与升级备份门禁语义的 13 项安装器测试）；`scripts/acceptance.ps1` 完整验收入口（与 CI 同门禁：工具单测→源码契约→资源集成→SQLite smoke→11 项目锁包→restore→构建→Spreadsheet 47/47→App 1174 项 0 失败）首次整体实机执行通过。内存维度按服务预检上限（5000 行／5 MiB／32 MiB 展开）的拒绝路径已有测试覆盖，进程峰值内存采样未做。

仍然未验收：真实 IdP 端到端、真实 MySQL／PostgreSQL 服务器迁移与 CRUD、干净账户／独立小容量卷磁盘满／代码签名、真实 OS DPI／多显示器／高对比度／IME／Narrator、真实文件选择器／剪贴板／托盘／更新浏览器、XLSX 在 Office 中的兼容 POC、通知横幅物理点击、真实 CI 运行与升级前备份／恢复完整演练。列表见 [需求交付核对表](REQUIREMENTS_STATUS.md)。

## 2026-11-04 继续补齐需求

- 安装器升级前自动备份：交互确认后在目录独占锁下调用旧版 `--backup-before-upgrade`，复用 SQLite 一致性 ZIP、权限与生产确认；校验成功并重新取得旧 EXE 写句柄后才覆盖。失败停止且保留备份，静默升级仍返回 6。只覆盖继承的单个数据目录，历史旧版不支持命令时拒绝。Python 离线检查通过（93 项工具测试含 8 项跳过、14 项 SQLite smoke）；本机缺 dotnet／pwsh／makensis，新增 C# 与 NSIS 编译、真实 Windows 升级路径均未执行，历史 Windows 记录不覆盖本次改动，详见 [升级操作](UPGRADING.md)。
- 列宽调整（§6.2 Must）：发现全局样式禁用拖拽并隐藏把手，现恢复 `CanUserResizeColumns`，把手只透明绘制而保留命中区。新增真实指针拖拽、偏好落盘与新视图恢复回归，并保留无竖线表头效果。独立静态核对未发现确定问题；尚未执行 Avalonia 回归。
- 备份辅助升级：新增预览后“确认备份并安全退出以便升级”，复用授权、生产确认、全局备份、未保存确认和退出收尾。失败／取消／离页不自动退出；快照后的写入不属于该备份。它不是安装器自动备份，详见 [升级操作](UPGRADING.md)。新增备份及真实窗口回归仍需运行验证。
- 真实远端分页 POC：新增默认不编译、必须显式配置后才联网的只读入口，使用真实客户端读取 100k／1m 总量下的首中末页，记录逐页耗时和宿主阶段内存，缺配置明确未执行。不是模拟 API，也不代表已经完成性能验收，运行方式见 [性能 POC](PERFORMANCE_POC.md)。
- 许可材料：新增 nuspec 直接版权归属保留与 `coverage-report.json`，关联输入、包、声明、正文哈希、排除包及未覆盖项；不推造缺失材料或审批结论。公开报告使用逻辑路径，已接入 publish／ZIP／NSIS 文件链，ZIP 检查要求报告存在且结构有效。
- 独立核验已修正退出预检前版本捕获时序，并补真实窗口重绑／拒绝后重试回归；真实远端 POC 删除按主机名猜测环境的限制，仍要求显式开关、专用测试配置与 HTTPS。
- 此轮最终离线检查：工具测试 92 项中 84 项通过、8 项因 PowerShell 条件跳过；SQLite smoke 14 项通过；源码契约 0 错误，461 个双语键／414 个引用键／28 个 XML 文件检查通过；`git diff --check` 通过。工具夹具不证明真实发布许可审查完成。
- 当前新增 C#／XAML／PowerShell 未在本机编译执行，本机仍缺 dotnet／pwsh。此前 Windows 测试结果不能覆盖这些增量。

## 2026-11-04 并行修复增量

- 数据库配置修复：局部 `database.json` 不再用默认 SQLite 覆盖部署 Provider；服务器连接串按对应驱动校验，失败不暴露原始异常；SQLite 路径使用连接串构建器转义。
- 迁移保护修复：服务器初始化拒绝未知 Provider，已应用迁移必须是已知列表的连续前缀；SQLite 复用历史校验。服务器 Provider 尚缺独立迁移集与真实数据库验收，不能宣称已正式支持。
- 数据库回归入口补齐：覆盖配置继承、连接参数秘密不外泄、迁移历史；服务器冒烟改走实际初始化入口并通过新上下文读回。尝试运行 C# 测试因本机 `dotnet` 不存在退出 127，本轮 C# 修改尚未编译。
- 发布工具修复：SPDX 正文只读取固定上游及按来源隔离的缓存，拒用旧无版本缓存，移除未固定版本备用正文。新增 6 项离线回归；合成正文只验证工具行为。
- 本轮最终离线检查：工具测试发现 69 项，61 项通过、8 项因缺 PowerShell 跳过；SQLite smoke 14 项通过；源码契约 0 错误；458 个双语键、411 个引用键、28 个 XML 文件检查通过。上述结果不替代 Windows／EF／真实许可及发布包验收。

### 后续实现轮次（验证结果另记）

- `RemoteDemoTests.Large_remote_total_keeps_only_the_requested_page_when_navigating` 新增 100k／1m 总量、50／200 条每页四种客户端回归。使用真实服务与 ViewModel，经受控 HTTP 传输连续向前／向后翻页，核对请求参数、一次操作只请求一页、当前页条数／记录范围及总页数。每次只构造当前页数据；这不是外部服务性能、峰值内存或百万行数据实测。本机缺少 dotnet，新增 C# 用例尚未编译执行。
- MySQL／PostgreSQL 已各自新增迁移上下文、独立初始迁移 ID、固定目标模型与快照及离线设计时工厂。业务 CRUD 保留 AppDbContext；SQLite 的三段历史 SQL／迁移 ID／目标模型不变。服务器启动按对应上下文检查历史及模型漂移；旧试验库中的 SQLite 迁移 ID 被拒绝，不自动改写。源码已实现，EF 编译、模型一致性及真实服务器迁移仍未验收。
- 发布库存过滤前核对全部 `.deps.json` 的包身份，普通包缺少 assets 对应时阻断；有限识别的 SDK runtime pack 缺口逐项写入声明、SBOM 元数据与终端，既不静默丢失也不生成虚假覆盖组件。新增 12 项离线回归。
- 依据固定版本公开 nuspec 与上游正文，许可政策增加 `PostgreSQL`；生成仍检查实际还原声明、正文和 hash，不代表最终包材料审查完成，来源见 [许可记录](RELEASE_COMPLIANCE.md)。
- 恢复故障回归新增 3 个场景：回滚中最后一项写入受阻（原设置存在／不存在）、已提交恢复的清理受阻，核对连续失败保留事务与安全副本、解除故障后重试及启动幂等。未确认需修改的服务缺陷，仅补回归源码；尚未编译运行。
- 此轮离线检查：工具测试发现 81 项，73 项通过、8 项缺 PowerShell 跳过；SQLite smoke 14 项通过；源码契约与资源检查通过；`git diff --check` 通过。C#、真实 EF／服务器、Windows 发布与安装尚未在此轮执行。
- 独立核验后继续修正：真实 `.deps.json` 的 `runtimepack` 类型现在进入未覆盖检查，普通 `package` 不能靠相似名称绕过缺包阻断；迁移上下文借用外部连接时不接管所有权，新增四组连接生命周期回归；服务器模型版本元数据改为实际 EF Core 9.0.22。迁移脚本新增 Provider 参数并显式指定上下文，避免多个上下文时默认命令失败。C#／PowerShell 均尚未运行。
- 修正后的最终离线结果：工具测试发现 83 项，75 项通过、8 项因 PowerShell／Windows 条件跳过；SQLite smoke 14 项通过；源码契约 0 错误、458 个双语键／411 个引用键／28 个 XML 文件检查通过；`git diff --check` 通过。真实发布库存、服务器及 Windows 验收仍保持未完成。

按规范章节核对的完整交付状态与剩余阻塞见 [需求交付核对表](REQUIREMENTS_STATUS.md)；当前仍不能宣称所有需求完成或可以发布。

## 2026-11-03 交付覆盖快照

- **列表偏好**：Demo 列表已落盘搜索文本、分页、排序及列显隐／宽度偏好；恢复默认会在未保存确认后清空搜索、回到第 1 页、每页 50 条、Code 升序并恢复默认列。当前只有源码和静态证据，C#／Avalonia／Windows 重开、重置及写入受阻场景尚未执行。
- **删除版本并发**：本地 Demo 删除使用编辑时 `Version` 作为原子条件；版本过期返回冲突，记录不存在才返回 gone，避免旧页面按 Id 删除后来保存的记录。已补回归测试源码，尚未执行 C#／EF 或桌面验证。
- **性能 PoC**：真实 SQLite／CSV 的 10k 建库、分页／筛选、保存、全量导出及 5000 行新增／更新往返入口已落盘，默认不编译，须显式 `-p:PlinthRunPoc=true`。当前没有耗时、内存或性能达标结论；本环境因 `dotnet` 缺失未编译运行。
- **补充许可**：显式 runtime／native／font／icon／NSIS 库存的路径、正文、UTF-8、原始字节 SHA-256、重复项及 NuGet 身份冲突校验，以及 notices／CycloneDX SBOM 合并已落盘。补充许可专项 17 项通过，但材料为合成夹具，PowerShell 透传仅静态检查，不能证明真实库存、来源、许可审查或发布包覆盖完整。
- **当前实际数字与环境阻塞**：Python 工具测试 63 项全部通过（含 8 项 PowerShell 脚本测试），SQLite migration smoke 14 项通过，源码契约 0 个错误。
- **2026-11-04 Windows 实机执行**：SDK 10.0.403 下 restore、`win-x64` locked restore、Release 构建（0 错误）通过；低危及以上 NuGet 审计无告警；App 测试 917/917、Spreadsheet 47/47、XLSX 启用构建 956/956，TRX 已归档；10k CSV／SQLite 性能 POC 通过；Release ZIP 通过 `release_checks`；空目录首次启动、二次启动与命令行退出码符合契约；导航 P1（宿主分区、设置底部、示例模块开关）、P2（图标、36px 密度、48px 折叠轨道、`SidebarCompact` 持久化）与 P3（内缩圆角选中态、Ctrl+K、Alt+数字、系统组权限显隐）已落地，并在 125% 实机截图复核。`dotnet format` 已通过。安装器通过拒写（5）、提取失败（7）、中途被杀后恢复、静默升级拒绝（6）、交互取消（1）、0.0.9→0.1.0 交互升级、占用卸载拒绝与重装卸载保留数据；系统 Toast 已接入并进入 Windows 通知历史，`plinth://tasks` 冷启动和已有实例均进入任务中心，安装器写入并在卸载时清理 AUMID／协议。UIA 树已实测，快速打开结果的读屏名称修复为页面标题；100/125/150/200% 渲染缩放断言通过。Demo 页新增固定模板 PDF 导出/打印与二维码；SkiaSharp（MIT）+ QRCoder（MIT），文字转矢量轮廓避免嵌入整套 CJK 字体。数据库改为 SQLite 默认、MySQL（Pomelo 9）／PostgreSQL（Npgsql 9）可选，EF Core 固定在 9.0.x；服务器库启动向前迁移，SQLite 历史库路径不变，应用内备份页明确只支持 SQLite。剩余：干净账户／磁盘满矩阵、代码签名、真实 OS DPI／IME／Narrator、真实 API／IdP、通知横幅物理点击、真实打印机与 PDF 阅读器兼容。
- **2026-11-04 选中底色通栏**：侧栏导航行、快速打开结果、设置页 5 个可搜索下拉的选中／悬停底色改为左右通栏（去掉列表内边距、行内缩进、行圆角与弹层上下留白），悬浮滚动条不占列宽；Fluent 原生 ComboBox／菜单弹层的首尾 4px 内边距用 `ComboBoxDropdownContentMargin`／`MenuFlyoutScrollerMargin` 资源归零。日期字段未选时显示字段名（起始日期／截止日期）并带日历图标，选中值按当前区域短日期格式（`d`）显示，不再暴露 `<yyyy/M/d>` 格式串。`ShellTests`、`SearchSelectTests`、`PopupBleedTests`、`DateFieldTests` 各加门禁，旧样式下均转红。此前 P3 的“内缩圆角选中态”作废。
- **2026-11-04 交互细节收口**：字段按钮（下拉／日期）按下态改用应用浅灰 #F2F4F7，不再闪 Fluent 的 40% 黑底（白底上呈 #999）；侧栏汉堡图标改为矢量三横线，展开态与导航图标左缘对齐（22），展开／折叠纵向位置一致，消除字体字形的亚像素跳动；移除子菜单弹层的 -4px 手工垂直偏移，面板顶边与父项顶边齐平。

## 已接入源码

- 非 NuGet 补充许可材料接入 `supplemental_compliance.py`，接口与格式见 [发布补充许可材料](RELEASE_COMPLIANCE.md)。显式库存支持 runtime／native／font／icon／nsis，校验严格字段、非空身份／版本／HTTP(S) 来源和许可声明，拒绝重复 JSON 键、身份及同组件正文路径；与 NuGet 同名同版本也拒绝合并。每个组件至少提供 LICENSE，正文须为非空 UTF-8（可含 BOM），按原始字节验证 SHA-256；路径限制在库存所在目录树内，拒绝绝对路径、Windows 盘符、反斜线、`..` 和越界符号链接。
- 补充组件的身份、来源、许可声明、LICENSE／NOTICE 正文及 hash 合并进 `THIRD_PARTY_NOTICES.md` 和 CycloneDX 1.5 `sbom.cdx.json`；SBOM 使用独立 `urn:plinth:supplemental:` 引用，LICENSE 与全部正文证据以 base64 保留原始字节，证据 hash 不代表组件二进制 hash。验收入口仅在 `-Package` 时接受 `-SupplementalComplianceFile`，发布入口在还原／构建及替换候选包之前验证、生成时复验，无效显式输入不回退；现有复制步骤将两份产物纳入 ZIP 和安装器输入。无补充输入仍为 NuGet-only 候选；有输入也仅覆盖真实还原的 NuGet 与显式组件，不证明分发库存完整、来源真实或许可审查通过，不改变 NuGet 许可策略，不把未知许可转换成 MIT。PowerShell 透传仅完成静态检查，真实发布与包内容尚未验收。

- Demo 列表搜索文本随工作区偏好保存并在重开时恢复，输入、偏好反序列化及备份规范化共用 500 长度上限（按 C# string.Length，超长截断）。恢复默认命令先经过未保存确认，再清空搜索、回到第 1 页、每页 50 条、Code 升序并移除列覆盖以恢复默认可见性和宽度，保存后重新查询。这里只记录已有代码能力，C#／Avalonia 运行验收尚未执行。

- 关于页更新提示增加显式下载入口：仅新版本结果显示按钮，点击前重新校验 HTTPS／无账号／无片段，复用现有浏览器启动接口；失败给固定安全提示，允许再次打开。开始新检查撤下旧链接，关页取消并忽略迟到响应；导航工厂直接创建，由 Shell 释放页面。`AboutUpdateTests` 新增 11 个参数场景，覆盖真实按钮、非法地址、失败／最新／未配置结果、关页迟到响应及重开；C# 和真实浏览器行为尚未验收。

- 主窗口异步关闭核对发起时的工作区与上下文版本：等待确认期间换绑或换出再换回后，旧请求不再批准窗口关闭，旧异常不回填当前状态；重复关闭仍共用待完成预检，已同步完成的关闭保留原时序。旧工作区已开始的预检不会撤回，新工作区需要另行发起关闭。新增 `WorkspaceNavigationTests` 六个真实窗口参数场景，覆盖允许／拒绝／异常、换绑和换回，尚未执行 C#／桌面验收。

- 可选模块在启动后首次打开失败时，立即保存已清理的收藏与最近访问；启动恢复尚未加载偏好时不提前保存空集合。保存失败仍禁用本次运行中的入口，保留磁盘原件并提示，其他页面偏好不受影响。新增成功落盘／写入受阻回归源码；补回缺失的英文模块失败资源。源码检查不能代替尚未执行的 C# 回归。

- 规范 §3.9 可选远程模块失败隔离接入：RemoteDemo 的服务、命令、导航和帮助先完整注册再加入主集合，失败不留下部分入口；首次 ViewModel 构造、View 查找或同步视图构造（含构造期间的 XAML 初始化）失败后禁用相关导航及命令。视图失败通过 UI 队列移除标签并释放页面，关闭后忽略迟到通知；等待关闭确认的旧操作不能再次释放或选中已移除页面。独立失败提示保留模块名、安全说明、关联 ID 和日志入口。核心配置、共享依赖和数据库错误仍传播，内存耗尽和取消即使被反射包装也继续传播。新增反射视图和真实 Shell 渲染回归源码，C# 尚未运行；XAML 异常用构造函数抛出 XmlException 代表，不能替代真实 XAML 验收。不覆盖构造完成后的绑定、事件、异步业务故障或任意模块运行时卸载。

- 验收与发布入口共用 Python 版本探测，拒绝 Python 2／低于 3.8 的解释器，自动寻找合格版本并复验完整路径。验收调用发布传递同一解释器，显式无效路径不回退；保留各步骤失败即停止。新增 8 项 PowerShell 执行测试源码，本环境无 PowerShell，均明确跳过，尚不能证明脚本可运行。

- 设置认证状态随共享会话变化刷新：登录、刷新验证和退出发布身份变化，设置页异步投递 UI 通知，关闭时退订并忽略已排队回调及迟到操作结果。关闭后已开始的共享认证继续，新页面接收最终身份；正常刷新等待 UserInfo 时保持原身份。观察者异常不改变认证结果。新增门闩、失败、退出、释放及通知隔离回归源码，尚未执行 C# 或真实 IdP 验收。

- 规范 §3.3／§4.5 设置健康状态与 XLSX 选择器补齐：修改连接字段、开始新检查或关闭设置页均使旧健康结果失效；待授权保存的输入不冒充已提交连接，失败回填后重新显示待检查状态。设置导航直接创建页面，释放归 Shell 管理，避免根 DI 留存已关闭实例。XLSX 两个原生选择入口共用防重入，并核对打开时的视图版本、上下文、宿主及交互状态，脱离后重挂不接受旧返回；已入队任务继续。新增受控响应及真实 headless 按钮回归源码，C#、原生选择器及 XLSX POC 尚未执行。

- 规范 §3.5／§4.5 局部任务与选择器边界补齐：日志查询统一捕获超长行等读取故障，仅当前有效请求显示安全状态，不回写故障日志；普通表格选行观察未保存确认的异常，保留旧选择与草稿，释放后不再报告。CSV 预览关闭、页面脱离或更换时取消局部读取，忽略晚返回；错误报告及诊断保存选择器核对打开时的页面版本，脱离后重挂也不能启动旧任务。正常 CSV 确认保留独立快照，已入队全局导入继续。新增日志、选行及真实 headless 视图／可控选择器回归源码；原生选择器与 C# 尚未执行，定向测试仍因缺少 dotnet 返回 127。

- 规范 §4.5 单实例退出不再同步等待监听任务：Dispose 只请求取消，监听结束后观察异常并释放 CTS；StopAsync／DisposeAsync 可显式等待清理完成。启动前捕获 token，重复启动不创建第二个监听，停止后禁止重启，重复／并发释放幂等。新增阻塞回调门闩、取消监听、启动后立即释放、并发释放、回调内释放及故障传播测试源码，尚未执行 C#。正在执行的同步激活回调不能强制中断，异步退出会等待它返回。

- 规范 §4.5 界面事件异常边界补齐：Demo 上下文菜单／CSV 文件选择、日志重新打开、许可证加载、便笺加载及错误弹窗日志跳转捕获取消和异常，意外错误复用现有安全错误报告。CSV 选择器和首次读取晚返回时再次核对页面及宿主，避免关页后弹出旧预览。新增真实右键事件中异步确认失败、不逸出 UI 未处理异常入口且可重试的回归测试源码；尚未执行 C#／Avalonia。

- 确认后授权边界补齐：Demo 五个敏感入口、Notes 保存、管理设置、日志清理、直接备份与远程写入在确认后复核原 subject／会话版本及当前权限；外观修改保持无生产确认。排队备份、恢复、诊断和 XLSX 导出在 UI 调用链上准备确认对象，出队再次复核，不重复弹窗；确认准备单独记录 `.confirm` 审计，不代表业务成功。新增换号、同账号重登、撤权、拒绝、排队和迟到取消回归测试源码，尚未编译或执行。

- XLSX 确认内容绑定补齐：CsvWriter 在标准序列化写出时计算包含 BOM 的 SHA-256，SpreadsheetService 返回中转位置与摘要，确认导入传入 expectedSha256；不再允许确认期间另一份合法 CSV 替换已预览内容。保留旧写入／转换 API，增加字节摘要、转义往返、合法篡改拒绝及失败清理测试源码；数据库业务拒绝另以 SQLite trigger 验证整批无提交，尚未运行 C#。

- 规范 §12 可选 XLSX 全局结果补齐：确认导入／导出改用 EnqueueWithResult，保留新增／更新／业务拒绝计数、自动错误报告及自选 XLSX 输出位置；拒绝正常返回并将外层审计标记 rejected，不再以异常丢弃结果。后台回调只使用服务与输入快照，关页后仍可查看结果；预览仍是无提交结果的局部任务。导入透传 CSV 读取进度，导出加载按实际行数报告比例，转换及 ClosedXML 保存使用 null，去掉固定阶段伪百分比。新增关闭、授权、生产确认、拒绝与迟到取消回归测试源码；C# 和 XLSX POC 尚未执行。

- 规范 §12 自选 CSV 报告位置补齐：原错误报告可保存至任意用户选择的目录，但任务结果仅接受 exports 路径，造成文件已生成却不显示位置。现为显式选择的 CSV 报告提供内部结果入口，默认导出／自动报告仍限制 exports；共同执行长度、扩展名、控制字符及符号链接检查。TaskOutcome 增加 FilePath，旧 CsvPath 仅返回 CSV，结果仍不进入日志／诊断包。新增自选目录端到端调用及路径约束回归测试源码，C# 未执行。

- 规范 §8 恢复预览一致性修复：原先分别按源路径执行哈希、清单解析、再次哈希，Linux 上路径 A→B→A 替换可能显示 B 清单却确认 A。现改一次打开源流，流式复制到数据目录私有快照；同一独占快照流提供清单与确认哈希，预览结束清理，暂存仍核对实际副本哈希。新增正常预览、真实路径替换、复制取消、预取消和无效包清理五项回归测试源码，C# 未执行；源目录只读不再阻碍预览工作目录创建，数据目录需额外磁盘空间。

- 规范 §3.3／§7 设置与布局版本回退补齐：未知版本整份保留为 `.unsupported` 后使用默认值，损坏文件保留 `.bad`；已有副本不覆盖，原件保留失败则当前实例阻止保存。工作区备份规范化也检查根版本和布局版本，拒绝未来格式；相关回归测试源码已补，C# 未执行。

- 规范 §8／§13 创建备份补齐只读内容／大小／排除项预览和显式确认。预览仅授权，真正创建时生产确认一次并重新检查目标；迟到选择器与预览不更新失效页面，全局创建关页继续。ZIP 流式压缩接受取消并按实际读取字节报告阶段比例，快照和发布显示不确定进度；提交前清理临时内容，发布后不撤回。SQLite 快照及 ZIP 收尾等同步阶段不能即时取消；新增备份进度与取消测试源码，尚无 C#／桌面运行证据。

- 规范 §3.5／§12 CSV 补齐流式读取和全局结果：不再复制完整文件，预览仅保留前 20 行，仍严格检查完整尾部、同一句柄三遍哈希与确认内容。读取按各遍实际字节、导出按同一读取事务内行数报告比例；未知阶段为 null。TaskOutcome／EnqueueWithResult 保留新增、更新、去重的已发现错误行数及 exports 内 CSV 位置，截断计数显示下界，未知不伪造为零；结果不进入任务日志或诊断包。新增服务、取消、重试、关页、通知和路径约束回归源码，未执行 C#。

- 规范 §11.2 日志等级配置补齐：部署 `MinimumLogLevel`／`PLINTH_MinimumLogLevel` 控制技术及任务日志阈值，默认 Information，六种级别严格校验，修改后重启。外层和文件管线显式保留低级别日志通路，较高技术阈值不关闭 Information 安全审计；新增配置与分类筛选回归测试源码，尚未执行 C#。已回读确认 Program 的文件输出接入 RedactingLogSink。

- 规范 §10.3／§11 写时脱敏补齐：文件 Sink 前采用固定标量白名单，丢弃业务结构，异常只保留类型及有界、无路径的方法调用帧；继续使用标准 CompactJsonFormatter。保留新旧审计分类、GUID／远程协议关联 ID、任务 ID 与 trace/span。源码门禁新增直接日志插值检查及 4 项工具测试，未知结构、字符串 GUID、超量属性、远程 ID、异常及追踪回归测试源码已补，C# 未执行。模板和白名单字段仍必须按定义使用，不能依赖正则识别任意业务正文。

- 规范 §3.5／§8 恢复暂存取消补齐：任务令牌进入复制、哈希、解压及最终发布前检查，取消异常向上传播；只清理本次临时内容，保留源文件及已有 pending。发布成功后取消保持成功，不误称回滚。新增中途取消、旧包保留、哈希／解压、任务终态回归测试源码；底层数据库同步检查仅在前后响应取消，尚未执行 C# 或 Windows 恢复验收。

- 规范 §14／§15／§20 交付入口补齐：本地 acceptance 与 CI 共用工具单测、源码／资源检查、SQLite smoke 及测试平台参数；显式 EnableXlsx 先验证页面变体再传递给打包。应用项目声明 win-x64 还原目标，锁生成流程取消 solution 临时 RID 覆盖并增加发布 RID 的 locked restore 检查。Python 工具 30 项与迁移 smoke 14 项通过，PowerShell、真实还原和打包仍未执行。

- 规范 §3.3 部署环境覆盖补齐：保留 deployment.json 默认值，使用标准 Microsoft.Extensions.Configuration Binder／EnvironmentVariables 绑定 PLINTH_ 前缀和双下划线嵌套键，叠加后执行校验。非法类型使用固定错误，避免原值进入异常；新增覆盖优先级、字段保留、类型错误及不安全地址测试源码。两个新增依赖仍待真实 restore／锁包及许可核验。

- 正常令牌刷新不再在等待 UserInfo 时短暂发布匿名身份：先保存轮换凭据，保留原身份至验证完成，失败仍清理会话；补充成功／失败门闩测试源码，验证待处理期间身份和会话版本不变。

- 规范 §2／§3／§6 远程页标准交互补齐：使用 TableView 与标准表单，贡献现有 Shell 新增／保存／删除／取消命令，Esc 优先取消删除确认，Ctrl+F 定位搜索，提供显式编辑和查询空态。加载、失败及会话失效不误报无数据；新增 6 项命令、状态、取消及真实 Shell／TableView 回归测试源码，未执行 C#／Avalonia 验收。

- 规范 §3.5 全局任务并发上限补齐：同时最多执行 2 个业务委托，其余保持 Waiting；等待可取消且不进入业务执行。失败／取消释放槽位，完成观察者不占业务槽位，退出停止入队并取消、等待排队与运行任务。手动重试沿用同一上限，200 条历史／容量限制保留，新增确定性门闩回归测试源码。

- 规范 §3.2／§6 字段级业务错误补齐：Demo 提供类型化保存输入／字段结果，旧 SaveAsync 字符串接口兼容；重复编码、必填与长度等错误定位字段，无效 UTF-16 在构造实体前校验。编辑对应字段清除旧业务提示，重试前清理旧结果，异步结果绑定提交输入快照；冲突保留为表单错误。CSV 继续复用同一服务校验，生产确认和业务审计每次一次，授权在确认前后复核，新增服务／ViewModel 回归测试源码。

- 规范 §8／§10 远程数据示例补齐：独立 RemoteDemo 模块通过 HTTPS API 实现服务端分页／排序／筛选和标准表单 CRUD，不混入本地库。受信任部署 DemoApiBaseUrl 默认空，非空须身份配置开启；服务检查权限和账号，生产写入确认，更新／删除带强 If-Match 版本，响应验证容量、字段与页信息，原始错误正文不展示、写操作不自动重试。新增内存 HTTP handler 与部署配置回归测试源码，实际服务端／IdP 端到端验收仍缺失。契约见 [REMOTE_DEMO.md](REMOTE_DEMO.md)。

- 规范 §12 XLSX 启用门槛修正：默认构建关闭页面，完成兼容／内存／许可 POC 后显式使用 PlinthEnableXlsx=true 或发布开关 EnableXlsx。ClosedXML 保留统一依赖图并仍需许可审查；独立服务测试始终编译 XLSX，CI 另接启用变体页面测试。没有把可选构建配置当作 POC 通过证据。

- 规范 §3.5／§4／§13／§20 页面关闭释放继续补齐：日志、诊断、许可证页接入 Shell 已有的 `IDisposable`，覆盖单标签、全部标签和工作区释放，取消局部读取、清除结果并阻止迟到发布。临时视图脱离仍允许重用页面，日志切回恢复被中断查询。导航工厂用 ActivatorUtilities 创建这三类页面，避免根 DI 容器持有已关闭实例；剪贴板／文件选择器的异常回调经过释放检查。生产确认期间关闭诊断页不再启动导出，已入队全局任务继续运行；不引入新的生命周期框架。新增 8 项页面生命周期回归测试源码，源码契约与集成检查通过，C#／Avalonia 和 Windows 行为尚未运行验收。

- 规范 §11.3 日志写失败降级已接入：同一 Serilog 管线保留最近 128 条定长元数据（时间、级别、可解析为 Guid 的关联／任务 ID），不持有原始事件、模板、异常或业务字段。SelfLog 只触发一次固定 stderr 提示，不输出其原文；stderr 自身失败不再抛出或递归记录。日志目录创建／文件初始化的 IO 或权限故障允许只使用内存缓冲，数据目录不可用仍中止启动；启动恢复异常也进入监听器和日志清理。内存随进程退出丢失，不自动重放到磁盘，不提供页面、复制或诊断导出入口，也不替代权威审计。新增 6 项有界／并发／脱敏／故障 sink 测试源码，尚未执行 C# 或真实磁盘满／只读目录验收。

- 规范 §8.6 编码比较语义与模块可移植性补齐：Demo／Notes 模型移除 `NOCASE`，保留 Code 原文，由共享 CodeIdentity 生成 ASCII 大小写折叠后的 UTF-8 十六进制 CodeKey，独立唯一索引；保存、CSV 覆盖更新、XLSX 三个去重入口、便笺查找统一按键，默认查询／分页／CSV 导出按键加 ID 排序。仅 ASCII 折叠，Unicode 大小写与组合形式仍区分；无效 UTF-16 不静默替换。新增 CodeIdentityKeys 向前迁移、固定目标／快照和最新结构校验，历史迁移不改，旧数据原文及审计值保留。新增 C# 唯一性／Unicode／CSV／排序和旧库升级回归源码，修正历史测试夹具不再用新实体写旧表。模块源码门禁新增 Provider、专属模型配置与原生 SQL 依赖检查；仍是窄静态规则。搜索 LIKE、其他字段排序及未来 Provider 行为仍需真实数据库验证，未宣称多数据库支持或 EF 验收通过。

- 规范 §15 安装安全继续补齐：固定当前用户安装目录、拒绝目录链接；Windows 应用以随包只读资源持有共享句柄，安装／卸载独占，覆盖同一程序目录的多个数据实例；旧 EXE 占用检查及改名后解包，避免强杀与占用时覆盖。交互升级提示应用内 ZIP 备份和位置，静默升级拒绝；失败回调保留用户数据并阻止未完成新版本启动。卸载改为真实 publish 生成的明确文件清单和非递归空目录删除，保留未知文件，不再递归删除安装根。新增清单生成测试与应用句柄测试源码，ZIP 检查要求锁资源。已在 Windows 11 上编译并由 NSIS 完成安装/升级拒绝/占用拒绝/卸载冒烟；不提供安装器自动备份或整包自动回滚，断电／强杀与干净账户链接测试仍待演练。

- 规范 §3.6／§10／§11 日志事件分类与查看授权已接入：技术／任务使用 `logs:read`，安全审计使用 `audit:read`，同一滚动存储按类别筛选，兼容旧审计模板／EventId。全局警告与错误合并技术、任务，排除审计。查询前后及复制重新检查权限和账号，页面本地计时刷新失权状态；详情改普通文本和受控复制按钮。打开原始日志及包含它的父目录要求两类查看权限，手动清理另需管理权限、生产确认和操作审计，每个待删除文件前复核。诊断预览／导出检查 `diagnostics:export` 与所需日志权限，快照绑定账号，最终发布前复核，始终排除审计事件。新增分类、权限分离、清理确认／换号、目录入口、诊断快照与页面失权测试源码，并补导航回归断言；尚未执行 C#／桌面验证。业务历史仍由具体模块定义，未新增审计数据库或 OS 文件隔离。

- 规范 §2.2／§2.4 当前页批量选择已接入：显式批量模式复用 TableView 原生多选，进入前未保存确认，关闭编辑表单；提供当前页全选／Ctrl+A、清空、计数及 CSV 批量复制／Ctrl+C，公式保护与转义复用现有 CsvWriter。单条右键和删除不作用于批量选择，新增退出批量模式。分页、查询、刷新、切标签及数据上下文切换清除 ID 与视觉选择，忙碌／释放后禁止复制。新增 6 项批量选择 headless 测试源码，尚未编译或运行；无跨页全选或批量删除。

- 规范 §3.5 显式任务手动重试已接入：默认禁止，模块声明允许后仅失败终态可重排队；同一原任务并发点击只创建一个新 ID，保留原失败及前后尝试关联。任务中心可重试、选中新任务、前后定位；历史裁剪缺项给提示，不承诺跨重启恢复。Demo CSV 导出捕获搜索条件／服务，重试读取当时数据并生成新文件，每次重新授权／生产确认。服务主动 OperationCanceledException 映射取消；退出等待工作、完成通知及取消回调，历史裁剪不移除尚未完全结束的执行。新增 8 项 TaskRetryTests 与真实 CSV 生产确认重跑测试源码，尚未执行 .NET 验证。

- 规范 §2.1／§2.2 快捷键集中维护与冲突检测已接入：八组固定手势共用 `AppShortcuts`，菜单引用同一 KeyGesture，窗口仅分发未处理冒泡事件；移除 Demo 重复按键处理。文本 Delete／Esc 不进入页面命令，标准 Ctrl+C/V/X/A/Z/Y 不允许注册为应用快捷键。窗口构造检查重复手势／动作及框架等价键，同页重复动作贡献在 Shell 构造时明确失败，跨页面标准动作允许复用。新增默认匹配、冲突、输入优先级、菜单显示与单次真实分发测试源码；原 CRUD／未保存快捷键测试保留。未执行 .NET／Windows 验证。

- 规范 §15 命令行入口补齐：单独 `--version` 输出统一程序集版本并返回 0；未知／额外／重复参数给出本地化使用提示并返回 2，输出管道 IOException 返回 1。参数分支先于单实例、目录、日志、配置恢复和 UI，不回显原始参数。Windows WinExe 按需连接已有终端并保留既有输出重定向；无参数不连接控制台。已补解析、输出失败及无显示器真实托管入口的测试源码。尚未执行 .NET 测试，Windows 原生 EXE 与控制台附加／单路重定向另待实测。

- 规范 §2.1／§2.2 示例列表右键菜单已接入编辑、复制和删除，并支持系统键盘上下文菜单。右键目标行沿用未保存确认，取消不对旧记录开菜单，空白处不复用旧选择；保存刷新后重新定位当前记录，以稳定表格控件打开弹层。忙碌时禁用记录操作，认证模式按编辑／删除权限禁用入口，写入仍走原服务授权与删除确认。数据上下文变化或视图卸载关闭菜单，剪贴板失败给安全提示。已补 7 项 headless 回归测试源码，尚未编译或执行。

- 规范 §3.6／§11.3 日志与错误中心补充警告／错误预设、清空筛选和 Ctrl+F。意外错误弹窗按关联 ID、任务中心按任务 ID 打开日志，替换旧日期／等级／搜索等条件且保留信息级上下文；复用已有日志标签。查询开始清除旧详情，取消或迟到请求不再发布结果。操作与日期栏支持换行；无关联 ID 时禁用弹窗日志按钮。已补导航、筛选重置、上下文记录和搜索焦点回归测试源码，尚未执行 .NET／桌面验证。

- 主窗口状态栏补充用户／部署环境／活动任务数与进度／最近一次显式健康检查结果，连接状态进入设置、任务数打开任务面板。复用窗口计时器只读本地快照，不引入自动联网或会话刷新；关闭窗口停止刷新。健康快照绑定本次检查配置，拒绝迟到旧响应覆盖新结果，取消不发布成功；状态栏不展示远程地址和异常详情。已补无自动请求、配置变化、并发迟到、用户变化、任务统计及入口回归测试源码，未执行 .NET 验证。

- 规范 §3.1／§3.7 离线帮助中心已接入：F1 按当前页面 ID 定位主题，支持标题／正文／主题键／错误码搜索、版本显示和无结果提示；Demo、Notes 及可选 XLSX 通过普通 DI 集合注册帮助。任务诊断和意外错误弹窗可跳到错误帮助，未知主题回退入门说明。原“关于”继续保留独立菜单。
- 帮助菜单与关于页增加第三方许可证声明入口，仅读取发布目录的 `THIRD_PARTY_NOTICES.md`，按纯文本展示且可复制；有界读取最多 2 MiB，支持取消，缺失／超限／不可读明确提示。继续由真实依赖还原后的发布工具生成声明，未生成或伪造依赖清单。帮助与许可服务、生命周期及导航回归测试源码已补，未执行 .NET 验证。

- 规范 §3.1 最近页面与收藏已接入文件／窗口菜单：最近访问最多 10 项、收藏最多 20 项，去重、清空历史、当前页收藏勾选、复用现有标签；启动恢复不重排历史，移除或隐藏的页面按当前导航注册过滤。独立 Navigation 偏好只含页面 ID，随工作区 ZIP 白名单恢复，布局重置不清空。偏好保存改为落盘成功后发布内存，失败保留原收藏与历史并提示。持久化、失败、导航与 ZIP 往返回归测试源码已补，未执行 .NET 测试。

- 规范 §7.1 固定右侧详情／任务面板已接入：菜单与工具栏入口、折叠后列宽归零、拖动宽度、布局恢复和重置；右栏偏好通过现有工作区白名单进入 ZIP 备份。任务在当前业务页旁按秒刷新，可请求取消并定位完整诊断页。详情由模块显式提供 `IPageDetails` 字段，Demo 和 Notes 已示例接入；切换页解绑旧订阅，窗口关闭停止计时器。相关持久化、页面通知和 headless 回归测试源码已补，尚未执行 .NET 测试。

- 界面密度提供标准／紧凑／舒适三档，保存后通过 DynamicResource 即时更新控件、表格行、导航和页面间距；启动及 ZIP 备份恢复接线已完成。可选字段沿用 settings schema 2，旧设置缺字段使用标准，未知密度安全回退。字体、表格列宽、用户窗口和侧栏布局独立保留。
- 全局任务终态通过 Shell 独立提示条显示，支持关闭提示、进入任务中心并选中对应任务；后续完成会替换提示，完整记录留在任务中心。观察者异常隔离、UI 线程更新及工作区释放退订已接入，相关 C# 回归测试源码已补。这里实现的是应用内提示，系统 Toast 尚未实现。

- 规范 §16.1 模块门禁接入源码契约检查：直接跨模块引用、可审计实体显式主键/版本映射、模块服务权限字面量及同文件普通方法写入路径。另检查实体在 snapshot 和历史迁移目标中的声明存在性，防止迁移文件漏交。13 项正反例夹具已提供；不宣称替代编译器、EF 模型比较或覆盖跨文件/动态调用。
- CSV 导入导出及预览错误报告的授权/生产确认移至服务公开入口，检查先于文件和数据库访问；任务调用复用入口，避免重复确认。新增直接调用权限拒绝、生产取消、单次确认和业务拒绝审计的 C# 回归测试源码。

- 新增 Notes 最小第二模块：独立表和追加迁移、权限与审计、生产保存确认、菜单命令、未保存确认。组合根一行接入，不修改 Shell 业务分支；使用和移除规则见 SECOND_MODULE.md。
- 设置保存等待全部修改，失败拦住本次关闭；同步保存与异步修改共用非阻塞互斥。三种关闭入口受阻时选中对应页面。地区/时区模型与页面补齐，沿用启动格式化及备份白名单。
- 变更记录和升级回退说明已补齐，当前仍未发布，不声称已通过安装或真实 EF 验证。

- 组合根注册共享 WorkspacePreferencesService、UnsavedChangesService、TaskService、DiagnosticService 和 TaskDiagnosticsViewModel，任务诊断可导航进入。Overview 为首项；已有工作区按保存状态恢复。
- 托盘退出调用主窗口 Close；系统 ShutdownRequested 转入未保存确认关闭路径；服务在 lifetime Exit 后释放。关闭前停止接收全局任务、请求取消并等待活动任务。
- BrandingOptions / BrandingService 从可选 branding.json 加载；支持产品名、描述、HTTPS 支持链接、PNG Logo、ICO 窗口／托盘图标和强调色。显式品牌打包携带引用图片；嵌入 EXE 的图标仍是编译资源。见 BRANDING.md。
- CsvHelper 33.1.0、Duende.IdentityModel.OidcClient 7.1.1、Microsoft.Extensions.Http.Resilience 10.11.0、System.IdentityModel.Tokens.Jwt 8.19.2 已纳入中央包版本和应用引用。实际代码分别用于 CSV、OIDC、HTTP 弹性与签名令牌校验。34.0.0 是 API 重写版（无 `CsvHelper.Configuration`／`CsvParser`），源码按 33.x 编写，故固定 33.1.0；版本已由真实 restore 验证。
- deployment.json 驱动身份启用及 Development/Test/Production Profile。设置页提供登录、会话刷新和退出，凭据库、会话、权限和审计为共享服务。已配置身份时，健康请求可携带会话；默认离线不构造远程身份客户端、不改变本地 CRUD。
- 普通保存／删除、导入／导出、备份／恢复和诊断导出接入用例授权与审计；离线使用 OS 用户，登录后实体审计使用已验证 subject。生产敏感操作需确认；业务拒绝与取消单独记录。服务端仍必须独立授权。
- CSV 页面提供编码、分隔符、源列映射、最多 20 行预览和明确确认；完整文件 SHA-256 防止确认后换文件，预览错误可独立导出。正式导入完整校验后同一事务提交。导出在同一读取事务中每页最多 200 条，逐行写入临时文件，成功后发布。原有单引号与公式保护前缀有往返转义。导入导出为全局任务，关闭页面继续执行。
- ZIP 白名单覆盖数据库、settings.json 和 workspace-preferences.json；显示版本、UTC 时间、文件、大小和 SHA-256，确认后暂存同一校验内容。下次启动恢复并保留安全副本、managed journal 与回滚记录。用户设置 schema 2、地区／时区及规范化工作区偏好随包恢复；旧包缺少工作区文件时恢复默认偏好。部署身份配置、凭据和任意未知字段不包含在包中。
- 用户设置 schema 1 升级为 2，新增日志保留天数与文件数上限，Serilog 启动读取限制；未知版本保留整份原文件后以默认值启动，保留失败时禁止保存。已补齐工作区、任务、诊断、CSV、身份及远程错误的中英文资源。
- 设置通过 ChangeAsync 授权、候选副本和串行保存提交；持久化失败／拒绝／取消时 UI 回填原值并提示。外观和管理权限分别检查，生产敏感修改要求确认；登录、刷新、退出不要求设置管理权限。
- 地区与时区可独立选择，重启后应用；审计字段和日志时间按所选时区及地区格式显示，包含偏移量，存储保持 UTC。手动日志清理读取配置保留天数。
- ClosedXML 服务、可选 XLSX 页面、双语资源、条件导航及独立测试项目已接入，页面默认关闭。仅支持有界纯文本表格，导入使用完整验证后的预览快照，拒绝公式／宏／外部关系。关页取消预览，已启动导入导出继续由任务中心管理。
- 编辑／窗口菜单、应用内日志与诊断入口、模块命令 DI 集合和 Ctrl+N/S/F 等页面按键已接入；文本编辑按键保持控件语义。未处理异常显示安全说明、错误码、建议和关联 ID，日志只保留受控异常类型与方法调用帧。
- CI/SQLite smoke、dependency-locks workflow、发布工具、无障碍测试源码及 UI_ACCEPTANCE.md 均已落盘。安全边界与部署模板见根目录 SECURITY_MODEL.md 和 deployment.example.json。

编辑已有记录时，类型化保存输入显式要求记录存在；即便版本仍为 0，被其他操作删除后也返回表单级冲突，不重新插入。新增服务与旧表单保存回归测试源码，重复编码的旧测试断言同步改为字段提示。

本地 Demo 删除沿用编辑页打开时的 `Version` 作为原子条件；记录已被修改时返回冲突并保留新版本，记录已不存在时才返回 gone，避免旧页面误删后来保存的数据。新增服务回归测试，尚未执行 C#。

远程页面与写入前后另检查会话版本，覆盖隐藏期间注销后重新登录同一账号；普通令牌刷新不推进版本，避免误清草稿。新增会话边界、同账号重登、查询关闭、严格 204 与写入中断结果核对的回归测试源码。

手动性能入口 `PerformancePocTests` 默认不编译，显式 `-p:PlinthRunPoc=true` 开启。真实 SQLite／DemoService 场景包括两批 5000 行建立 10k、首中末页／中文筛选／保存／全量导出，以及独立 5000 行新增更新和另一数据库逐字段往返。输出阶段耗时及测试宿主内存采样，不代表桌面峰值或性能达标；本环境尚未编译运行。运行命令与边界见 [性能 POC](PERFORMANCE_POC.md)。

## 已实际执行

- Python 工具单元测试：发现 63 项，55 项通过，8 项 PowerShell 执行测试因缺少 PowerShell 明确跳过。已运行部分覆盖发布／安装清单、模块契约（含 ExecuteConfirmedAsync 正反例）、直接日志插值正反例及补充许可材料；许可正文使用模拟夹具，不证明真实依赖合规。
- 补充许可材料专项：17 项通过（包含在上述工具测试中），覆盖正文与原始字节合并、hash／路径／UTF-8 校验、重复键／身份／路径拒绝、NuGet 身份冲突、无补充输入候选及 CLI 失败路径；PowerShell 选项透传和产物复制仅为静态检查，不代表真实脚本、ZIP 或安装器运行通过。
- Python SQLite migration smoke：14 项通过，覆盖三次迁移 DDL、历史值及 Unicode 保留、比较键回填、UTF-16 拒绝、失败回滚、WAL 一致性副本及固定模型声明一致性；不是 EF Core 运行测试。
- scripts/integration_checks.py：408 个双语资源键、367 个引用键、26 个 XML 文件、Python AST 和中央包引用检查通过。
- scripts/check_source_contracts.py：源码契约检查 0 个错误。不检查 C# 类型或 Avalonia 绑定能否编译。

## 剩余阻塞与未验收

- 环境 .NET/NuGet 不可用；未执行 restore、format、C# build、xUnit、Avalonia XAML 编译、headless 测试及发布。构造参数和引用只完成源码核对，不能证明依赖 API 兼容或运行时 DI 成功。
- 再次尝试固定 SDK 官方下载地址，IPv6 与强制 IPv4 均连接失败（退出码 7），未取得 SDK。完整 dotnet test 尝试返回 127；真实锁文件检查仍明确阻断（退出码 1），没有生成虚假锁文件或依赖声明。
- 缺少真实 packages.lock.json。需在可联网且有指定 SDK 的环境执行真实还原，复核传递依赖，提交锁文件并运行 locked restore。不得手工伪造锁文件或把手动 workflow 存在视为成功。
- CSV、身份、HTTP 韧性、令牌校验、ClosedXML 及配置绑定等依赖的版本可用性、漏洞、许可证与分发 notice/SBOM 均待实际还原后核实。
- 真实 IdP 的 discovery、PKCE、state/nonce、签名校验、loopback 浏览器回调、轮换刷新、UserInfo、退出及失效凭据清理尚未验收；Windows Credential Manager／Linux Secret Service 尚未实际运行。
- 远程示例仍需部署符合契约的实际 API，并完成大总量分页、权限／并发、超时后核对及换号清理实测。内存 handler 用例不是外部服务验收；本仓库不附带生产 API 或 IdP。
- Windows 11 的 IME、DPI、键鼠、读屏、长标签、文件选择器、剪贴板、托盘／系统退出取消、关页继续任务、备份恢复／故障回滚和安装升级卸载待人工验收；见 UI_ACCEPTANCE.md。新增无障碍测试源码不等于通过。
- CSV 深分页采用 Skip/Take；大数据耗时、内存和 WAL 持有时间尚未实测。文件被替换会安全失败，目前任务中心显示通用失败说明。
- XLSX 的 Office 兼容、日期／长数字／中文／损坏文件和内存 POC 尚未执行；导出分页有行数与重复检查，但不是跨页数据库快照。当前代码和测试接入不能记作该 Should 项已验收。
- Current 设置对象仍可变，旧 Save() 路径在认证／生产模式下阻止用户字段未经授权持久化；不能视为防本机代码修改的安全边界。同步宿主持久化与异步设置修改的并发仍需运行验证。
- CI 配置存在但无本轮真实运行记录；NSIS、便携包、安装器、签名／校验及实际 runtime／native／font／icon／NSIS 库存与许可审查未验收。补充材料校验和合并的合成测试不证明实际发布覆盖完整。
- 系统 Toast 尚未实现，仍为 Should。可靠实现依赖真实还原的通知 SDK／投影、Windows Runtime 初始化、通知注册及安装／激活验证；unpackaged 官方路径不要求先改为 MSIX。接入依据和阻塞见 [通知接入记录](WINDOWS_NOTIFICATIONS.md)，应用内提示不能替代系统通知。
