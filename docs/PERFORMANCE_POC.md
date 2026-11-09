# CSV / SQLite 性能 PoC

此 PoC 直接调用真实 `TestDatabase`（临时文件 SQLite）和 `DemoService`，
不开桌面窗口、不使用模拟服务、不新增依赖。默认构建通过
`PlinthRunPoc != true` 的 `Compile Remove` 排除此测试，只有显式开启才编译。

## 显式 Release 运行

在装有 .NET 10 SDK 的机器上执行：

```sh
dotnet test tests/Plinth.App.Tests/Plinth.App.Tests.csproj --configuration Release -p:PlinthRunPoc=true --filter "FullyQualifiedName~Plinth.App.Tests.PerformancePocTests" --logger "console;verbosity=detailed"
```

请保留完整测试输出，并记录 SDK、CPU、内存、操作系统、电源模式与磁盘环境。
PoC collection 禁止与其他测试并行；命令进一步只选择本 PoC。

## 场景与正确性

- 由真实 CSV writer 生成两批各 5000 行 CSV，经真实导入服务建立 10000 条记录，
  分别断言新增 5000、更新 0。遵守每次最多 5000 行的服务限制。
- 在 10000 条记录下分别计时查询第 1、100、200 页，每页 50 条，
  校验总数、页号及首尾编码；计时中文筛选并断言命中 1000 条。
- 读取实际记录及版本后，计时保存更新，重新查询验证名称、Id 与版本递增。
- 计时全量导出 10000 条，以已有 CSV 解析依赖流式检查全部编码顺序与数量，
  不按物理行计数，避免把引号内的换行误算成记录。
- 另建独立数据库，计时 5000 条新增、同一批 5000 条更新及全量导出，
  断言新增/更新计数分别为 5000/0 和 0/5000。
  将该导出文件导入另一个全新数据库，逐条比较源库、目标库与预期的编码、
  名称和备注。CSV 解码结果也逐字段核对，防止往返中静默损坏。
- 数据覆盖中文、50 位前导零数字编码（超过电子表格数值精度）、逗号、
  双引号、CRLF/LF 内嵌换行，以及以 `=`、`+`、`-`、`@` 开头的公式文本
  和原有前导单引号。公式按文本往返，不启动电子表格软件执行公式。

## 测量含义

每个服务操作使用 `Stopwatch` 单次计时。文件生成、数据库初始化和后续断言
不计入对应操作耗时；服务自身的校验、数据库读写、审计及导出落盘属于操作耗时。
没有预热、重复统计或耗时阈值；首批可能包含 JIT、EF 初始化和文件缓存影响，
不能据此报告稳定吞吐量、P95 或性能达标。

每个阶段开始和结束采样当前测试进程的 `WorkingSet64`、
`GC.GetTotalMemory(false)`、进程级累计分配字节差值及各代 GC 次数差值。
不主动触发 GC。工作集包括运行时、原生 SQLite、缓存及测试框架；
GC 数值只反映托管内存，累计分配差值不是存活内存。
这些是阶段边界采样，会漏掉阶段内部瞬时高点，**不是实际峰值内存**。
测试不开 Avalonia 桌面窗口，不能代表桌面程序的真实峰值、首屏耗时、
UI 响应或完整用户操作链路。需要桌面数据时须另行进行真实桌面进程测量。

## 当前验证状态

2026-11-03 Windows 11（10.0.26200，i5-10210U / 8 逻辑核 / 16 GB，平衡电源）已用
.NET SDK 10.0.403 实际编译并运行本 PoC，结果通过（单次采样，无阈值）：

| 阶段 | 耗时 ms | 工作集变化 MB |
| --- | ---: | ---: |
| 10k/import-batch-1 | 4629 | 105 → 145 |
| 10k/import-batch-2 | 2389 | 145 → 153 |
| 10k/page-1 | 107 | 153 → 153 |
| 10k/page-100 | 4.3 | 153 → 153 |
| 10k/page-200 | 3.8 | 153 → 153 |
| 10k/chinese-filter | 84 | 153 → 154 |
| 10k/save-existing | 74 | 155 → 155 |
| 10k/export-all | 300 | 155 → 156 |
| 5k/import-new | 984 | 159 → 159 |
| 5k/import-update | 1300 | 159 → 170 |
| 5k/export-all | 106 | 170 → 161 |
| 5k/import-into-other-database | 1040 | 156 → 166 |

首批包含 JIT／EF 初始化影响；数据仅为阶段边界采样，不能外推为稳定吞吐或达标结论。
原始输出见 `artifacts/acceptance/performance-poc-4.log` 与测试日志。

## 大总量下客户端只取当前页的回归

`RemoteDemoTests.Large_remote_total_keeps_only_the_requested_page_when_navigating`
覆盖总量 100,000／1,000,000、页大小 50／200 的四组组合，使用真实
RemoteDemoService、RemoteApiClient 和 ViewModel，通过受控 HTTP 传输只生成当前页。
连续向前、向后翻页核对每个请求的页码及大小、单次操作的请求次数、当前集合大小、
首尾记录及总页数，防止客户端把远端总量当成需要全部获取或累积的列表。

```powershell
dotnet test tests/Plinth.App.Tests/Plinth.App.Tests.csproj -c Release `
  -p:TestingPlatformDotnetTestSupport=true `
  --filter "FullyQualifiedName~Large_remote_total_keeps_only_the_requested_page_when_navigating"
```

2026-11-04 已补充测试源码并做独立静态核对；当前 Linux 环境没有 dotnet，
尚未编译执行。这不是实际 API 的大数据量实测，也不输出延迟、内存峰值或吞吐结论。

## 真实远端分页测量入口

`RemotePagingPocTests` 已提供真实网络入口：默认不编译，只有
`-p:PlinthRunPoc=true` 才纳入构建，且仍需显式运行开关与专用测试环境配置。
没有配置时明确跳过，不用模拟响应替代。通过真实 RemoteDemoService 与 Bearer
鉴权读取首、中、末三页，每页 50 条；总量分别在独立确认的 100k／1m 数据集上执行。
核对总数、页号、行数及采样页 ID 不重复，并记录逐页耗时、工作集、托管内存和分配增量。

运行前通过进程环境注入：

- `PLINTH_REMOTE_POC_ENABLED=true`；
- `PLINTH_REMOTE_POC_ENVIRONMENT=test` 或 `staging`；
- `PLINTH_REMOTE_POC_BASE_URL`：专用测试 API 的 HTTPS 基址，以 `/` 结尾；
- `PLINTH_REMOTE_POC_TOKEN`：短期只读 token，不放入命令参数、文档或日志；
- `PLINTH_REMOTE_POC_EXPECTED_TOTAL=100000` 或 `1000000`；
- `PLINTH_REMOTE_POC_DEVICE`：CPU 型号、物理 RAM、分辨率、缩放及设备档位。

```powershell
dotnet test tests/Plinth.App.Tests/Plinth.App.Tests.csproj -c Release `
  -p:PlinthRunPoc=true -p:TestingPlatformDotnetTestSupport=true `
  --filter "FullyQualifiedName~RemotePagingPocTests" `
  --logger "console;verbosity=detailed"
```

2026-11-08 已用专用只读 HTTPS 测试 API 实际执行（产品代码未改动）：

- 环境：Windows 11 10.0.26200，.NET SDK 10.0.403 / runtime 10.0.14，i5-10210U（8 逻辑核）/ 16 GB；
  页大小 50；客户端 Release；HTTPS 使用本机开发证书（当前用户已信任，未改动信任库）。
- 测试 API：临时 Node HTTPS 服务，只读 `GET /api/demo-records`；100k／1m 数据集按索引确定性生成
  （固定宽度 Code／Name，ID 为 1 基 Guid），总量独立设置为 100000／1000000；不写数据、不接 OIDC、
  不访问外部网络。首次运行发现数据集给第一条记录发了空 Guid，客户端按契约拒绝；修正测试数据后通过
  （说明客户端校验生效，不是产品缺陷）。
- 结果：100k 首／中／末页（1／1000／2000）耗时 **3496.8ms / 22.0ms / 9.2ms**；
  1m 首／中／末页（1／10000／20000）**2710.2ms / 14.8ms / 3.9ms**。每次均返回 50 行，总数、
  页元数据一致，采样页 ID 不重复。首次请求包含 TLS 握手与 JIT，中／末页更接近稳态；
  测试宿主工作集约 46→58 MB，托管内存约 1.1→1.7 MB。
- 边界：只证明客户端在大总量下只取当前页并对齐契约。服务端是临时本机 API，不是外部生产服务，
  不能据此声明延迟或吞吐达标；OIDC 登录、桌面冷启动／首屏／滚动／编辑及长期资源趋势仍未测量。

## 桌面启动实测（2026-11-09）

- 同一 Windows 11 机器、Release 构建、非交互会话、临时数据目录；从进程 StartTime 到日志事件（UTC 对齐）：
  | 场景 | 应用启动日志 | 数据库已就绪 | 主窗口已打开 |
  | --- | --- | --- | --- |
  | 冷启动（空目录，含 4 个迁移） | 0.87s | 6.10s | 9.51s |
  | 暖启动（已有数据库） | 0.66s | 4.47s | 7.45s |
- 拆分：暖启动数据库初始化约 3.8s、主窗口初始化约 3.0s；冷启动迁移把数据库就绪增加约 1.6s。`SettingsViewModel.Prewarm` 已在后台线程，不阻塞窗口。
- 边界：非交互会话只测到主窗口 `Opened`，未测首帧绘制、滚动/编辑内存、低配设备和长期资源趋势；单机数字不代表 GA 性能门槛。后续可评估把编译期模型/迁移检查从每次启动移到测试/CI，或延迟/缓存窗口初始化。
