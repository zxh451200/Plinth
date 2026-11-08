# CI、依赖与发布交接

本文覆盖 `.github`、`scripts`、`installer` 范围。自动化生成的是候选包，不会向 GitHub Release 或其他外部服务上传发布。

## 历史交接记录（2026-11-04，外部 Windows 实机）

本节是此前 Windows 实机交接记录的原样保留，不能作为当前工作区的执行证据。当前宿主缺少 、 和 ；本轮未重新执行 C# 构建、Windows 安装升级或 NSIS 编译。下文的历史数字必须在具备对应工具的 Windows 环境中重新生成并与日志、产物逐项核对后才能引用。

当前源码已在 Windows 11 + .NET SDK 10.0.403 上完成真实 restore、locked restore、Release 构建、自动测试、10k 性能 POC、ZIP 发布与安装器故障矩阵。固定 SDK、`pwsh`、NuGet 与 NSIS 3.13（便携解压在 `D:/tmp/plinth-boundary/nsis`）均可用。

- restore 生成 3 个 `packages.lock.json`（含应用 `win-x64` 目标）；solution 与 RID 的 locked restore 均通过；低危及以上 NuGet 审计无告警。
- Release 构建 0 错误。`dotnet format --verify-no-changes` 退出 0；测试项目中的 xUnit 建议类分析器（xUnit1051/2031/2017）在 `.editorconfig` 中降为建议，不再阻断格式门禁，仍保留 IDE 提示。
- App 测试 917/917、Spreadsheet 47/47 通过，TRX 已归档（`artifacts/test-results-final/`）；XLSX 启用构建 956/956 通过。此前 6 个失败已修复：标签条键盘可达、远程拒绝切换后表格回退、WAL 用例保持写连接、单实例通知测试内联续体、XLSX CRLF 归一化。
- 性能 POC 通过，数字见 [PERFORMANCE_POC.md](../PERFORMANCE_POC.md)。
- ZIP `5cc6669c…4ea3` 与 Setup.exe `e250690a…0f3f` 均已生成并通过 `release_checks`。
- 安装器矩阵已执行：安装 0、锁文件存在、`--version` 正常；`/D=` 拒绝（4）、运行中安装拒绝（5）、安装目录拒写（5）、提取阶段写入失败（7，不写注册表）、静默升级拒绝（6）、交互升级取消（1，文件不变）、真实 0.0.9→0.1.0 交互升级（0，数据 hash 不变）、junction 拒绝（4）、占用时卸载拒绝（20 秒内文件不变）、释放后卸载并保留用户数据、卸载后重装数据库 md5 不变；安装写入 AUMID／`plinth://tasks` 协议，卸载清理两者。
- 系统 Toast：固定状态文案进入 Windows 通知历史（`Setting=Enabled`），冷启动与已有实例均通过 `plinth://tasks` 进入任务中心，证据见 [通知接入记录](../WINDOWS_NOTIFICATIONS.md)。
- 便携包：ZIP 解压后 316 文件、无测试程序集，`--version`、GUI、两个数据目录并行启动通过；PATH 仅保留 System32 时版本与 GUI 仍可启动（近似无 SDK 环境，机器仍装有 SDK）。
- 卸装边界：未知文件与未知目录在卸载后保留、重装后仍在，数据库 hash 不变；两个安装器并发退出 0/5 且最终安装可用；目录 junction 拒绝 4 且目标零写入。环境、NuGet 审计与 SQLite smoke 已归档。
- 未执行：安装器/升级在干净用户账户与磁盘满下的完整矩阵（应用首启与 CLI 备份已于 2026-11-08 实测，见 [实现记录](../IMPLEMENTATION_STATUS.md)）、代码签名（本机无代码签名证书，产物 `NotSigned`）、真实 OS DPI 设置切换／IME／Narrator 朗读、真实 API／IdP、通知横幅物理点击。

## 提交级门禁

`CI / gate (windows-latest)` 和 `CI / gate (ubuntu-latest)` 均阻断失败，不再保留 Linux 的 `continue-on-error`。两端执行工具脚本测试、锁文件存在性检查、locked restore、format 校验、Release build 与 tests，TRX 作为 artifact 保存。Ubuntu 通过仅证明构建和自动测试通过，不能据此宣布 Linux 正式支持。

SDK 由根 `global.json` 指定。源由 `scripts/nuget.config` 固定为官方 NuGet，不读取未经确认的公司镜像。没有启用锁文件缓存，避免锁文件尚缺失时缓存初始化掩盖问题。

两平台 gate 还执行 `check_source_contracts.py` 与 `integration_checks.py`。前者检查双语资源/占位符、模块直接引用边界、可审计实体的显式主键/并发版本配置，以及当前模块服务的本文件写入路径和权限字面量；后者检查完整 XML、Python AST 和中央依赖接线。`test_module_contracts.py` 包含正反例，防止只因源码出现 ExecuteAsync 就误认为后续所有写入均受保护。这些是窄静态规则，跨文件/动态调用的授权仍由运行时测试负责。

源码门禁另核对模块实体在 snapshot 和迁移目标模型中的声明存在性，避免只交实体、漏交迁移。它不证明 EF 模型或 DDL 一致；真实 `HasPendingModelChanges`、数据库升级和恢复测试仍必须通过。

模块可移植性门禁检查 `Modules/**/*.cs` 中显式 Provider 类型、UseSqlite／UseNpgsql／UseMySql／UseSqlServer、UseCollation／HasColumnType 及 FromSql／ExecuteSql／SqlQuery 系列调用。注释和普通字符串示例不参与检查；模块内部不能通过新建 Infrastructure／Migrations 文件夹绕过。宿主 Data、备份基础设施和真实迁移在扫描范围之外，仍可处理本地 SQLite。它不证明 LINQ 可翻译或各库比较语义一致，反射、别名封装和跨文件行为仍需运行验证。

## 首次集成必须完成

仓库还提供手动 workflow `Generate dependency locks`（`dependency-locks.yml`）。它在有 SDK 和 NuGet 网络的 CI 上真实还原并上传锁文件供审查，不会自动提交或发布。锁文件未生成并提交前，常规 CI 的锁文件检查应保持失败，不能以无锁还原代替。

1. 在可访问 NuGet 且有指定 SDK 的环境执行真实还原：

   ```powershell
   dotnet restore Plinth.sln --configfile scripts/nuget.config
   dotnet restore src/Plinth.App/Plinth.App.csproj -r win-x64 --configfile scripts/nuget.config
   ```

   应用项目已声明 `RuntimeIdentifiers=win-x64`；锁生成 workflow、普通 CI 和发布均读取项目中的声明，不再只在生成锁时向整个 solution 传临时 RID 属性。生成 workflow 另验证实际发布所需的 `-r win-x64 --locked-mode` 还原。提交实际生成的各项目 `packages.lock.json`，复核版本、传递依赖与完整性值。两个还原命令仍须真实执行通过；配置一致不代表锁文件已经生成。不得手工捏造锁文件。
2. 修复真实 restore/build/test/format 暴露的问题，保留失败及复测日志。
3. 配置分支保护，选择上述两个实际矩阵检查名。旧的 `linux-probe` 检查名已移除。
4. 查看 nightly 的许可证拒绝项，完成许可证政策扩展和证据归档。未知许可必须失败，不能只为了绿灯将未知包标为 MIT。

## 夜间检查与第三方声明

`nightly.yml` 每日 UTC 02:23、手动以及相关 PR 变更触发。依赖任务对直接与传递依赖启用 NuGet audit，低危及以上、审计源不可用均按错误处理；更新清单只报告可用更新，不自动升级包。Windows 任务构建测试、要求 NSIS 并检查候选 ZIP/安装器的哈希。

```powershell
python scripts/third_party.py --output artifacts/compliance
```

默认扫描实际 `src/tests` 还原的 `obj/project.assets.json`，逐包读取本地缓存 `.nuspec`。单项目发布则只输入应用的 assets，避免把测试依赖误称为分发依赖。版本、SHA512 来自还原结果，不从候选版本表推断。输出：

- `THIRD_PARTY_NOTICES.md`：包标识、声明许可、选择许可、固定 SPDX 数据版本 v3.26.0 的正文来源及 SHA256，并原样附加缓存中的 LICENSE/NOTICE/COPYRIGHT 文件。
- `sbom.cdx.json`：CycloneDX 1.5 的 NuGet 组件、purl、许可和包 SHA512。这是组件清单，不声称已验证完整依赖关系图。

默认仅接受明确列入脚本的宽松 SPDX 标识和简单 OR 表达式；例如 CsvHelper 只有真实 nuspec 声明支持 Apache-2.0 时才能选择它。文件型、旧 licenseUrl、AND/WITH、复杂表达式或未知许可均失败，需要针对真实材料审查后再扩展脚本。SPDX 正文下载失败也阻断，不能用占位文本代替。自动脚本的合成测试使用 mock 正文，只验证工具逻辑。

**覆盖限制：** 自动发现范围仍为 NuGet，不能证明 SDK 隐式下载的 self-contained .NET runtime packs、native 二进制、图标/字体、NSIS 与 LZMA 模块的全部许可义务已履行。发布维护者可通过 `-SupplementalComplianceFile` 提供实际核对过的库存和 LICENSE/NOTICE 正文，将明确列出的组件合并进声明与 SBOM；输入结构、正文路径和原始字节摘要会被校验。格式及命令见 [补充许可材料](../RELEASE_COMPLIANCE.md)。无输入时继续生成注明 NuGet-only 的候选清单；有输入也不自动证明所有分发组件已覆盖或许可已审查。正式分发前仍须复核实际版本、材料和完整性。

## 发布与验收

缺少 PowerShell／SDK 时，可用 `python3 scripts/offline_checks.py` 执行 Python 离线检查子集，详见 [快速入门](../../GETTING_STARTED.md#测试)。它不替代下面的完整门禁；退出码 `0` 仅表示该子集完成，工具单测的 skipped 项仍属未执行，静态契约和 Python SQLite smoke 也不能记为真实 C#／EF／桌面验收通过。

需要 PowerShell 7、Python 3.8+（CI 使用 3.12）、固定 .NET SDK；安装器还需要 PATH 中的 `makensis`。

两个入口共用 `scripts/python.ps1`，实际探测 `python`、`python3` 及 Windows 的 `py -3`，并复验所选解释器的完整路径。后续只调用该路径，验收转入发布时也传递同一选择。可用 `-PythonExecutable` 显式指定，版本过低、空值或不可运行时立即失败，不回退，不修改全局 PATH。

```powershell
./scripts/acceptance.ps1
./scripts/acceptance.ps1 -Package -BrandingFile ./branding.example.json
./scripts/acceptance.ps1 -Package -EnableXlsx
./scripts/publish.ps1 -BrandingFile ./branding.example.json -RequireInstaller
./scripts/acceptance.ps1 -Package -SupplementalComplianceFile ./reviewed-materials/inventory.json
```

`publish.ps1` 仅支持首发目标 `win-x64`，先执行 solution/RID locked restore，再 self-contained publish（禁用裁剪与 NativeAOT）。品牌文件显式提供才复制为 `branding.json`；未提供时保持默认品牌，不会自动打包示例文件。品牌 JSON 语法错误会失败，产品字段的语义由应用实现和测试负责。

`acceptance.ps1` 先执行工具单测、源码契约、资源集成和 SQLite smoke，再执行锁包、还原、格式、编译及 C# 测试；任一步失败即停止。测试命令使用与 CI 一致的 `TestingPlatformDotnetTestSupport=true`。

默认候选包不启用 XLSX 页面。兼容、内存及许可验收通过后，显式传 `-EnableXlsx`；验收脚本先运行启用变体中名称包含 `Spreadsheet` 的测试，覆盖页面与选择器生命周期，随后将相同开关传给发布脚本，测试失败不打包。该开关传递 `PlinthEnableXlsx=true`，不是验收证明。ClosedXML 保留在同一依赖图中，避免两个构建变体使用不同的锁文件；它仍必须进入实际依赖许可核对。CI 使用相同筛选编译／测试启用变体，独立 Spreadsheet 测试项目始终覆盖服务；当前环境尚未执行这些 C# 检查。

每次清理本次 publish 目录，notice/SBOM 校验成功后才打 ZIP；有 NSIS 时生成带版本与 RID 的安装器。指定 `-RequireInstaller` 时缺少 NSIS 会失败。ZIP 和 EXE 各有 `<文件名>.sha256`，ZIP 另外检查完整性、应用入口、notice 和 SBOM 存在性。可独立运行：

```powershell
python scripts/release_checks.py locks
python scripts/release_checks.py package artifacts/Plinth-0.1.0-win-x64.zip
```

安装器检查仅覆盖编译与产物哈希，不执行交互安装。正式发布还需在干净 Windows 11 用户环境记录：安装与启动、品牌生效、升级前备份/迁移、升级后已有数据可用、卸载保留 `%LOCALAPPDATA%\Plinth` 数据、重装恢复；确认便携包无 SDK/运行时也能启动，归档机器/系统版本、命令、包体积、日志和实际截图。未执行项标为“未执行”。

安装包通过 `installer_manifest.py` 从实际 publish 目录生成 `installer-files.nsh`，随 `UNINSTALL_MANIFEST` 参数交给 NSIS；不能手工提供宽泛的删除规则。清单拒绝目录链接、NSIS 特殊字符及已知数据库／备份／用户配置文件，卸载只删除交付的明确文件，保留未知内容。程序资源 `plinth.install.lock` 必须进入 publish 和便携 ZIP，ZIP 检查已将它列为必需文件；安装器自身创建并保持该文件，不随解包替换。

安装／升级／卸载专项验收：

- 运行同一程序目录的两个不同数据目录实例，安装与卸载都应拒绝；退出一个仍拒绝，全部退出后才允许。安装期间启动新实例应返回 3，且不打开用户数据。复核旧版尚无锁文件时的 EXE 占用检查，以及成功升级时旧 EXE 临时文件清理。
- 交互升级先提示应用内 ZIP 备份及记录位置，取消后原程序可启动；再次备份并退出后升级。首次 `/S` 安装可用，已有安装的 `/S` 升级返回 6；安装目录无效返回 4，文件占用／锁不可用返回 5，安装执行失败返回 7。`/D=` 指向其他位置必须拒绝，用户数据不变。
- 在隔离账户及测试目录中，把 Programs、安装根、已交付子目录或 DLL／JSON 文件替换为链接，确认安装／卸载拒绝且链接目标不变。放入未知文本、ZIP 和自定义数据子目录，卸载后这些内容保留；普通系统用户数据目录同样保留。
- 模拟磁盘满、文件拒绝写入、用户中止及进程被杀，记录退出码与文件状态。正常失败回调应阻止新版本启动；失败后再次运行安装器并取消或静默拒绝，仍不得恢复混合版本启动。完整重装可恢复；断电／强杀不保证执行回调，不得据源码声称整包回滚或故障恢复已通过。检查旧版本文件残留及完整依赖加载。

2026-11-03 实机已有 `dotnet`、`pwsh` 与 NSIS 3.13 的运行证据；Setup.exe 已编译并通过基础安装/升级拒绝/占用拒绝/卸载冒烟，完整安装矩阵仍待干净账户与故障注入复测。升级细节见 [升级与回退](../UPGRADING.md)。

### 命令行入口验收

`CommandLineTests` 提供解析、输出失败和实际托管入口的无显示器回归测试源码；它使用 `dotnet exec` 且同时重定向两路输出，不代表 Windows WinExe 的终端附加已经验收。当前环境缺少 SDK，测试尚未执行。

发布前直接测试候选包的 `Plinth.App.exe`：

- 在已有 Windows 终端中运行 `--version`，确认只输出 `Plinth` 与正确版本、退出码为 0，不打开窗口；普通无参数启动不额外出现控制台。
- 输入未知参数、`--version --unknown` 和重复的 `--version`，确认使用提示走标准错误，退出码为 2，不启动桌面。不要将命令行原始参数回显到证据中。
- 分别只重定向标准输出、只重定向标准错误、同时重定向两路，使用有控制台的父进程等待原生 EXE 退出。核对版本输出和错误提示仍到达各自原目标，另一路输出正确。至少有一项实际经过父终端附加，不能只测试两路均已重定向的情况。
- 保持主窗口已运行，再调用版本查询或错误参数；确认不唤醒原窗口。使用独立临时数据目录，确认查询不创建数据目录；对已有目录另核对数据库、设置和待恢复包未变化。
- 在无显示器的构建环境运行托管入口用例，保留退出码、stdout/stderr 与目录副作用断言的结果。Windows 原生包与无显示器托管入口分别记录证据。

## 当前环境证据（2026-11-07）

- 宿主未发现 、 或 ；因此本轮没有运行 C# restore/build/test、PowerShell 发布、NSIS 编译或 Windows 安装器验收。
- 本轮只允许把 Python 契约测试和源码阅读结果报告为已完成；它们不能代替 C# 编译、真实服务、Windows 文件选择器或安装升级验收。

## 先前环境记录（2026-11-01）

- 宿主 `dotnet`、`pwsh` 不在 PATH，因此未运行应用 restore/format/build/test/publish、PowerShell 或 NSIS 实际执行。
- Docker client/server 均为 1.13.1，现有镜像列表没有 .NET SDK。
- 对 `https://api.nuget.org/v3/index.json` 的 curl HTTPS 检查在沙箱内外均返回连接超时（退出码 7），没有取得 NuGet 内容。
- 拉取 `mcr.microsoft.com/dotnet/sdk:10.0.403` 在沙箱内外均连接超时（退出码 1）。镜像拉取不成功不能证明该 tag 不存在，也不能证明版本包不可用。
- 本目录初始不是 Git 仓库，未生成 commit、锁文件或实际许可证/SBOM 证据；CI 运行结果须在主代理集成后的真实 GitHub 仓库获取。
- 实际执行工具单元测试：4 项通过。这些是临时目录内的合成工具夹具，SPDX 正文下载被 mock，不代表真实应用依赖已合规。
- 实际执行 Python AST、NuGet XML、两个 workflow YAML 的解析：通过。YAML 解析不能替代 GitHub Actions 执行，PowerShell/NSIS 语法尚未由对应运行时验证。
- 实际执行锁文件检查：退出码 1，明确报告缺少应用的真实 `packages.lock.json`，属于预期阻断。

2026-11-02 再次检查固定 SDK 的官方下载地址，IPv6 与强制 IPv4 的 HTTPS 请求均返回退出码 7，未取得 SDK；不能由连接失败判断版本不存在。本轮 Python 工具测试 30 项和 SQLite smoke 14 项通过，C# 编译、真实锁包与 Windows 安装验收仍未执行。

同日继续尝试官方 Blob SDK 下载地址 `dotnetcli.blob.core.windows.net`，强制 IPv4 的 HTTPS HEAD 请求仍连接超时（退出码 7）。未取得 SDK；新增配置绑定包同样没有真实还原、锁文件或许可核验证据。

同日再次检查 `builds.dotnet.microsoft.com`、官方备用 `dotnetcli.azureedge.net` 的固定 SDK URL 及 `api.nuget.org/v3/index.json`，均使用强制 IPv4、8 秒连接／15 秒总超时；三次请求均退出 7，未获得 HTTP 响应。宿主仍只有 curl／wget，未发现 dotnet／pwsh／csc／mono，不能据连接失败判断版本不可用。最近工具测试为 34 项、SQLite smoke 为 14 项；这些不替代真实构建。

同日补齐确认后会话复核及 XLSX 中转内容绑定后，Python 工具测试为 35 项、SQLite smoke 为 14 项，源码契约 0 错误；404 个双语键、363 个引用键和 26 个 XML 文件检查通过。再次调用 dotnet test 仍因找不到命令退出 127，新增授权、排队及 XLSX 回归测试只有源码，尚无执行结果。

同日补齐设置健康检查、认证通知与 XLSX 选择器生命周期后，源码契约仍为 0 错误，资源／项目文件检查通过，35 项 Python 工具测试通过。本地与 CI 的启用 XLSX 筛选扩大为 `FullyQualifiedName~Spreadsheet`，覆盖页面及选择器测试；PowerShell 和 CI 尚未运行。定向 C# 测试再次退出 127（`dotnet: command not found`），没有编译或执行新增回归用例。

同日补齐可选模块注册／首次页面构造隔离与 Python 解释器选择后，源码契约 0 错误，405 个双语键、364 个引用键及 26 个 XML 文件检查通过；工具测试 35 项通过、8 项 PowerShell 用例因缺解释器跳过，SQLite smoke 14 项通过。依赖锁检查仍退出 1，模块隔离定向 C# 测试退出 127；没有生成锁文件或安装包。再次请求固定 SDK 官方地址仍连接失败（退出 7）。

2026-11-03 稍后在 Windows 11 + SDK 10.0.403 上完成了真实执行，结果见文末“2026-11-03 Windows 实机证据”。此前“无 dotnet”的记录保留为当时状态。

## 2026-11-03 Windows 实机证据

环境：Windows 11 10.0.26200、Intel i5-10210U（4 核/8 逻辑核）、16 GB、平衡电源；.NET SDK 10.0.403（与 `global.json` 一致）、`pwsh` 7.6.9、Python 3.15、NSIS 3.13（`D:/tmp/nsis`）。原始日志在 `artifacts/acceptance/`。

| 步骤 | 命令 | 结果 |
| --- | --- | --- |
| 还原 | `dotnet restore Plinth.sln --configfile scripts/nuget.config`；应用 `-r win-x64` | 通过，生成 3 个 `packages.lock.json`（应用含 `net10.0/win-x64` 目标） |
| 锁还原 | solution 与 RID 均加 `--locked-mode` | 均通过 |
| 构建 | `dotnet build Plinth.sln -c Release --no-restore -warnaserror:nullable` | 0 错误 |
| 格式 | `dotnet format Plinth.sln --verify-no-changes --no-restore` | 退出 0；xUnit 建议类分析器已降为建议 |
| 测试 | `dotnet test Plinth.sln -c Release --no-build --no-restore -p:TestingPlatformDotnetTestSupport=true` | App 917/917、Spreadsheet 47/47；TRX 在 `artifacts/test-results/` |
| XLSX 变体 | `-p:PlinthEnableXlsx=true` + `--filter FullyQualifiedName~Spreadsheet` | 36/36 通过 |
| 依赖审计 | `dotnet restore ... -p:NuGetAudit=true -p:NuGetAuditMode=all -p:NuGetAuditLevel=low -p:TreatWarningsAsErrors=true` | 通过，低危及以上无告警 |
| 性能 | `-p:PlinthRunPoc=true` + `PerformancePocTests` | 通过，见 [PERFORMANCE_POC.md](../PERFORMANCE_POC.md) |
| 发布 | `pwsh scripts/publish.ps1 -BrandingFile ./branding.example.json -RequireInstaller` | ZIP `5cc6669c…4ea3`；Setup `e250690a…0f3f` |
| 安装冒烟 | Setup `/S`；`/D=` 其他目录；运行中再安装/卸载；拒写/提取失败/中途被杀/0.0.9 升级 | 安装 0、锁文件存在、`--version` 正常；`/D=` 4；运行中安装 5；拒写 5；提取失败 7；被杀后静默重装 0；静默升级 6；交互取消 1；0.0.9→0.1.0 升级 0 且数据不变；卸载保留数据并清理 AUMID／协议 |
| 包检查 | `python scripts/release_checks.py locks` / `package <zip>` | 均退出 0；发布目录 293 文件 / 227 MB，无测试程序集或源码 |
| 命令行 | 发布 EXE `--version`；`--version --unknown`；重复 `--version` | `Plinth 0.1.0`／退出 0；usage 走 stderr／退出 2；重复参数退出 2；不创建数据目录、不写日志 |
| 启动冒烟 | 空数据目录首次启动、同目录二次启动、窗口运行时查询版本 | 首次建库/设置/日志；二次启动设置不变；窗口运行时查询正常退出且不打断原窗口 |

许可策略：`Avalonia.Angle.Windows.Natives 2.1.27548.20260419` 的包内 `LICENSE` 为标准 BSD-3-Clause，已加入版本锁定的显式文件许可白名单；`third_party.py --publish-directory` 只统计发布 `deps.json` 中实际分发的 84 个包，Debug-only 的 `AvaloniaUI.DiagnosticsSupport` 不再进入清单。

未执行：代码签名、安装器在干净账户下的目录链接/磁盘满/失败回滚/旧版本升级/重装恢复全矩阵（应用首启与 CLI 备份已实测）、真实 UIA／IME／DPI／读屏与截图、真实 API／IdP、XLSX Office 抽查、系统 Toast。
