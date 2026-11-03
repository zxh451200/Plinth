# CSV / SQLite 性能 PoC

此 PoC 直接调用真实 `TestDatabase`（临时文件 SQLite）和 `DemoService`，
不开桌面窗口、不使用模拟服务、不新增依赖。默认构建通过
`PlinthRunPoc != true` 的 `Compile Remove` 排除此测试，只有显式开启才编译。

## 显式 Release 运行

在装有 .NET 10 SDK 的机器上执行：

```sh
dotnet test /home/work/data/xxx/Plinth-main/tests/Plinth.App.Tests/Plinth.App.Tests.csproj --configuration Release -p:PlinthRunPoc=true --filter "FullyQualifiedName~Plinth.App.Tests.PerformancePocTests" --logger "console;verbosity=detailed"
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
