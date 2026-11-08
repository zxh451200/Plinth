# 只读部署与发布材料审计

核对日期：2026-11-07。本文件记录只读审计的事实边界，不是发布批准、许可证批准或真实环境验收报告。审计不读取仓库外凭据，不输出环境变量值，不连接未授权服务，不执行数据库迁移、不登录 IdP、不触发远端 Windows 操作，也不生成伪造产物。

可重复入口：`python scripts/offline_checks.py` 用于当前 Python/源码离线检查；`python scripts/pilot_checks.py --mode locked --pilot all` 用于具备 SDK 和已审查锁文件后的独立 pilot 验收。两者都不联网发现服务，也不能把静态检查或合成测试当成 C# 构建、Windows、真实服务或许可证验收。

## 当前发现

| 项目 | 结果 | 判定 |
| --- | --- | --- |
| API | `deployment.example.json` 的 `DemoApiBaseUrl` 为空；`docs/REMOTE_DEMO.md` 的地址是明确标注的占位地址 | 未发现可用 API |
| IdP | 示例配置中 `Authentication.Enabled=false`，Authority 为示例域名，ClientId 为示例值 | 未发现可用 IdP 或端到端授权 |
| PostgreSQL | 仅发现测试从 `PLINTH_TEST_POSTGRES` 环境变量读取连接串，没有授权值或结果 | 未执行；不能宣称支持 |
| MySQL | 仅发现测试从 `PLINTH_TEST_MYSQL` 环境变量读取连接串，没有授权值或结果 | 未执行；不能宣称支持 |
| Windows 远端执行 | CI 声明 `windows-latest` job；没有 WinRM、SSH、RDP、主机或令牌配置 | 未发现远端设施 |
| 发布产物 | 未发现 ZIP、EXE、NUPKG、发布 `.deps.json` 或 `project.assets.json` | 未发现真实产物 |
| 第三方材料 | 未发现补充库存；生成器需要真实还原资产和发布目录 | 未生成候选报告 |

当前工作区包含新增的 Dock、runtime-plugin、offline-sync、sample 和 pilot 项目。它们有源码和测试入口，但不能沿用历史 App 测试结果；截至本次核对，只有 `src/Plinth.App`、`tests/Plinth.App.Tests`、`tests/Plinth.Spreadsheet.Tests` 存在非空锁文件，其余项目仍需真实 restore 生成锁文件。仓库当前也没有 `artifacts/` 真实发布产物。

示例配置不能用于连接示例域名、任意 API 或生产数据库。`docs/release/RELEASE.md` 的历史交接叙述也不能替代当前工作区中可复核的产物、日志和测试报告；本次没有把它作为本地执行证据。

## 测试安全边界

`DatabaseProviderIntegrationTests` 在没有环境变量时显式 Skip；有值时会初始化和迁移数据库，插入一条随机记录后清理。只有连接明确属于隔离测试库且获得授权时才能执行。当前没有授权，因此不运行。

远程示例测试使用内存 HTTP handler，只验证客户端行为，不验证真实 API、TLS、IdP、服务端权限或审计。

## 获得授权后的证据命令

只在隔离测试环境和受控秘密注入下执行；占位符不是可运行值，不得写入文件或日志：

```powershell
# 由受控秘密存储注入，不打印连接串
$env:PLINTH_TEST_POSTGRES = '<controlled-secret>'
dotnet test tests/Plinth.App.Tests/Plinth.App.Tests.csproj -c Release --no-build --no-restore --filter FullyQualifiedName~DatabaseProviderIntegrationTests.PostgreSql
$env:PLINTH_TEST_MYSQL = '<controlled-secret>'
dotnet test tests/Plinth.App.Tests/Plinth.App.Tests.csproj -c Release --no-build --no-restore --filter FullyQualifiedName~DatabaseProviderIntegrationTests.MySql
```

执行后保留脱敏 TRX/控制台摘要，核对测试记录已删除；Skip 必须记为未执行。真实 API/IdP 按 `docs/REMOTE_DEMO.md` 执行登录、刷新、换号、分页、拒权、ETag 冲突和超时后的权威查询，只使用隔离测试数据并清理。不得归档令牌、Cookie、连接串、Authorization header 或原始认证跳转 URL。

独立 pilot 的锁与运行入口：`python scripts/pilot_checks.py --mode generate-locks --pilot all` 仅在已授权且可审查的 NuGet 环境生成真实锁；随后执行 `python scripts/pilot_checks.py --mode locked --pilot all`。当前审计没有运行这两个入口。locked 模式会要求锁文件预先存在，并记录 SDK、restore、format、build、test 和 TRX；它不能替代 Windows 安装、真实 API/IdP、真实数据库或人工 UI 验收。

在真实 Windows 机器上归档原始输出、TRX、发布目录清单、包大小和 SHA-256：

```powershell
dotnet --info
pwsh --version
makensis /VERSION
dotnet restore Plinth.sln --locked-mode --configfile scripts/nuget.config
dotnet build Plinth.sln -c Release --no-restore -warnaserror:nullable
dotnet publish src/Plinth.App/Plinth.App.csproj -c Release -r win-x64 --self-contained true --no-restore -o artifacts/publish/win-x64
pwsh scripts/acceptance.ps1 -Package -PythonExecutable (Get-Command python).Source
```

Windows UI、IME、DPI 和安装器/升级/卸载在干净账户下的完整矩阵仍须按 `docs/WINDOWS_TEST_PLAN.md` 实测；应用在标准用户下的首启与独立小卷磁盘满（启动/CLI 备份）已于 2026-11-08 实测，见[实现记录](../IMPLEMENTATION_STATUS.md)。源码、CI YAML、`bin/Release`、`obj` 和测试源码不能代替交付物证据。

## 第三方候选报告

只有真实还原资产和发布目录存在时运行：

```powershell
python scripts/third_party.py --assets src/Plinth.App/obj/project.assets.json --publish-directory artifacts/publish/win-x64 --output artifacts/compliance
python scripts/release_checks.py package artifacts/Plinth-<version>-win-x64.zip
```

有人工核对的 runtime/native/font/icon/NSIS 材料时，使用受控的 `--supplemental-compliance-file` 输入。notices、SBOM 和 `coverage-report.json` 只能是候选证据；`license_review_approved` 与 `distribution_inventory_complete` 不得自动改成批准。

## 工具边界与运行限制

- `acceptance.ps1`、`release_checks.py` 和 `third_party.py` 检查构建、文件、哈希和输入证据，不发现真实 API/IdP、数据库授权或 Windows 远端主机。
- CI 的 `windows-latest` 是声明式矩阵；没有实际 job 日志和归档产物时，不能证明 Windows 运行成功。
- NuGet 自动范围不覆盖 SDK runtime pack、原生载荷、字体、图标、NSIS 和压缩模块，缺材料只能报告未覆盖。
- 当前宿主 glibc、缓存 SDK 启动限制和旧 Docker 运行时限制阻断真实 .NET/Windows 发布验证；不通过放宽安全策略、特权容器或重复无效下载规避。

## 当前可执行清单

以下项目必须在具备对应授权和环境后逐项执行；跳过或缺少证据仍标记为“未执行”：

| 前置材料/环境 | 当前状态 | 执行入口与必要证据 |
| --- | --- | --- |
| .NET SDK 10.0.403、`dotnet` | 当前宿主不可用；缓存 SDK 受 glibc/运行时策略阻塞 | `dotnet --info`、solution 与 pilot 的 locked restore、Release build、TRX |
| PowerShell 7、`pwsh` | 当前宿主不可用 | `pwsh --version`、`scripts/acceptance.ps1` 原始日志 |
| NSIS `makensis` | 当前宿主不可用 | `makensis /VERSION`、`acceptance.ps1 -Package`、Setup SHA-256 |
| Windows 11 实机与干净账户 | 实机与标准用户首启已执行；安装器/升级/DPI/IME/辅助功能矩阵未完成 | 按 `WINDOWS_TEST_PLAN.md` 执行安装/升级/卸载、DPI/IME/辅助功能和故障矩阵 |
| 真实 HTTPS API 与隔离 IdP | 未发现配置或授权 | 按 `REMOTE_DEMO.md` 执行登录、刷新、换号、权限、ETag、超时；不得归档令牌或原始跳转 URL |
| 隔离 PostgreSQL/MySQL 测试库 | 未发现连接串或授权 | 仅注入受控秘密后执行 `DatabaseProviderIntegrationTests`；保留脱敏 TRX，不记录连接串 |
| 实际发布目录与 `project.assets.json` | 当前未发现 | 完成真实 publish 后运行 `third_party.py`、`release_checks.py package` |
| runtime/native/font/icon/NSIS 等第三方正文 | 未发现补充库存 | 人工核对后提供 `--supplemental-compliance-file`；不得用占位正文或自动结论代替审查 |

本清单不修改 `deployment.example.json`，不填入随机 API、IdP、数据库或账号。完成后应把脱敏命令摘要、TRX、发布目录清单和哈希放入受控证据目录，再更新状态；不能仅凭脚本退出 0 或静态源码检查宣称通过。

本审计未修改部署配置、测试连接、中央组合根、主窗口或其他 agent 功能文件，未提交、推送或发送外部消息。
