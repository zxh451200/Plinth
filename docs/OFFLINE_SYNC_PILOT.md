# 独立离线同步试点

`Modules/OfflineSync` 提供实际的文件 SQLite outbox 与显式调用的同步服务。
默认不注册、不联网，不改变 RemoteDemo 的 HTTP 写入、权限、确认或 UI。
它只试点**已有记录的更新**；不含新建、删除、全量下载、自动定时器或生产 HTTP 适配器。
服务端被删除的记录可以作为冲突墓碑被显式接受，不能由本试点自动重建。

## 已读契约与隔离

RemoteDemo 契约要求服务端原子比较强 If-Match，版本不匹配返回 409/412；
取消、连接中断和无效成功响应均不能证明写入未提交。现有接口没有操作去重确认协议。
试点采用相同的正整数版本和 Code/Name/Remark 长度、Unicode 校验规则，
但使用自己的 DTO 和 `IPilotTransport`，没有调用或修改 RemoteDemoService。

业务 AppDbContext 通过程序集扫描 IEntityTypeConfiguration 注册表；因此试点只在独立
PilotDbContext 内显式建模，不提供会被业务上下文扫描到的配置类。
独立数据库文件必须是绝对路径，文件名固定为 `offline-sync-pilot.db`。
父目录由宿主创建并限制访问；不要指向业务数据库、备份恢复候选库或不可信符号链接。
EnsureCreated 只用于本试点 v1 新库，不能充当将来的 schema 升级器。
Provider 接线单独放在基础设施辅助类 `Data/PilotDatabaseOptions.cs`，不改变现有 DI 或初始化路径。
未增加依赖或修改现有 provider 迁移集。

## 使用服务

宿主必须提供试点传输实现；除测试内的可控模拟传输外，仓库现提供下文独立入口的
`LocalPilotTransport`（真实文件 SQLite 模拟服务端），没有生产网络写入实现。
scope 是由宿主固定生成的部署/API/租户/主体组合键，必须稳定且无歧义，不能来自任意用户输入。
更换账户或部署时创建新 scope 的服务，不能把旧 outbox 路由到新账户。

```csharp
var pilot = new OfflineSyncPilot(
    Path.Combine(pilotDirectory, "offline-sync-pilot.db"),
    scope, pilotTransport);
await pilot.InitializeAsync();

// 不联网；返回的是已落盘的草稿，同时也是 outbox 项。
var draft = await pilot.EditOfflineAsync(recordId, expectedVersion,
    new PilotInput("C1", "离线编辑", "备注"));

// 再次编辑 Pending 草稿必须提交自己读到的操作 ID，防止旧编辑器覆盖。
draft = await pilot.EditOfflineAsync(recordId, expectedVersion,
    new PilotInput("C1", "修订后的草稿", "备注"), draft.OperationId);

// 重新打开相同路径和 scope 后可 FindAsync 或分页 ListAsync 恢复草稿。
// 每次显式调用最多发送一个已到期操作；无定时器、休眠或无限循环。
await pilot.SyncOneAsync(cancellationToken);
```

修改载荷会生成新 Guid 操作 ID，并原子保留旧快照和 SuccessorId；
同一操作在安全重试、重启和核对中保持同一 ID、载荷和 ExpectedVersion。
单行同时充当本地可读草稿和 outbox，避免“本地编辑已保存但未入队”的双写间隙。
唯一 ActiveKey 限制每个 scope/记录只有一项未解决写入，Revision 比较防止迟到结果覆盖新状态。
读取分页最多 200 项；磁盘历史不自动清理，试点不承诺无限历史容量。
SQLite 忙、磁盘满或保存失败向调用方报错；失败不等于操作没落盘，应重新查询状态。

## 状态和重试语义

| 事件 | 持久状态及后续行为 |
| --- | --- |
| 离线编辑 | Pending，持久化原版本和本地载荷，无网络请求 |
| 到期领取 | 原子比较 Pending/Revision，先提交 Uncertain 和尝试计数，再调用传输 |
| 有效成功 | Succeeded，保存服务端快照，释放活动键 |
| 明确未提交的暂时失败 | Pending，持久化下次时间；总尝试最多 5 次，间隔 1/2/4/8 秒，之后 Exhausted |
| 版本冲突 | Conflict，保留本地载荷、原版本和服务端快照/删除墓碑，不重试 |
| 明确拒绝且未提交 | Rejected，不重试 |
| 超时、异常、发送中取消、未知结果、无效成功 | Uncertain，阻止后续写入与自动重试 |
| 发送中崩溃/重启 | 原 Uncertain 保持不变，不按启动次数或超时自动解锁 |

取消发生在领取之前时不发送；领取之后即使尚未实际发送也保守地保持 Uncertain。
收到结果后的本地持久化不使用调用方已取消的 token；若保存失败，磁盘仍保留 Uncertain。
同步时不会跳过服务端版本条件，也不会对 HTTP 状态码笼统推断“没有提交”。

`DefinitelyNotAppliedTransient` 和 `Rejected` 都必须是**有证据的未提交**；
连接失败、普通 5xx、空查询结果都不足以满足该条件。传输不得在内部或 HTTP resilience
管线中隐藏重试，也不得将响应解析失败映射成未提交。
每次发送必须重新校验目标、当前账户/会话、权限和必要的生产确认；
服务端负责原子版本比较、授权和权威审计。这些是未来适配器的前置契约，当前没有生产适配器实现。

稳定 OperationId 仅供关联与核对，不能证明服务端去重。
**本试点不承诺 exactly-once，也不对不确定写入提供自动重试。**
即使未来支持幂等，也必须先定义并验证服务端原子去重、载荷绑定、结果查询和保留窗口，
不能仅因请求带了 ID/header 就放开重试。

## 显式核对和冲突解决

Uncertain 只能用 `ReconcileAsync` 显式解除。宿主必须先取得针对**该操作 ID** 的权威终态，
涵盖仍在执行中的原请求，再传入结果和不含秘密的审计/回执引用。
该方法不自行联网、不验证回执真伪；这是一项受信任宿主 API，不能直接暴露为用户“重试”按钮。
只看到相同文本、最新版本或“查不到操作”不能证明原操作已完成或未提交。
没有服务端核对能力就保持 Uncertain，交由人工协调服务端。

已确认提交可核对为 Applied；已确认未提交可核对为暂时失败或拒绝。
核对不会清空计数或绕开 5 次上限；安全重试沿用原操作 ID 和载荷。
迟到原请求结果通过 Revision 并发检查被拒绝，不能覆盖已核对状态。

Conflict 可以通过 `ResolveConflictAsync(id, null)` 接受服务端（包括删除），不产生网络写入；
也可传入用户明确合并后的 PilotInput。合并会在同一事务内保留旧冲突，
生成新 ID，并把已保存的服务端版本作为新的 ExpectedVersion。
服务端若再次改变，下一次发送仍应返回冲突，不强制覆盖。
Exhausted/Rejected 仅可由 `DiscardFailedAsync` 显式归档；历史载荷仍保留。
未提供能丢弃 Uncertain 并直接放行下一次写入的入口。

数据库含本地业务草稿和冲突快照，使用宿主用户目录权限保护；没有加密承诺。
不存 access token、原始异常或原始错误响应；evidenceReference 只能保存非敏感引用。
业务数据库备份不会包含这个独立试点库；需退出/协调试点写入后单独备份，不把它混入正式恢复路径。

## 验证状态

### 独立入口：本地 SQLite 服务端示例

`tools/Plinth.OfflineSyncPilot/Plinth.OfflineSyncPilot.csproj` 是无桌面启动依赖的控制台入口。
项目直接链接现有 `OfflineSyncPilot`、契约、上下文和 provider 辅助源码，不复制状态机；
仅引用中央已定义的 EF SQLite 包，不修改中央依赖、共享 CI、解决方案或正式 App 入口。
独立测试项目为 `tests/Plinth.OfflineSyncPilot.Tests/Plinth.OfflineSyncPilot.Tests.csproj`。

以下 Bash 示例需要 .NET 10 SDK 和 NuGet 依赖。选用仅供本试点的可写绝对目录，
不要复用正式数据目录。每条 `pilot` 命令均启动一个新进程；首次运行需要还原/构建：

```bash
pilot() {
  dotnet run --project /home/work/data/xxx/Plinth/tools/Plinth.OfflineSyncPilot/Plinth.OfflineSyncPilot.csproj -- "$@"
}
pilot --help

# 示例目录；运行前由当前用户保护其访问权限。换目录会得到另一套 scope 和数据库。
pilot /home/work/data/xxx/Plinth/.artifacts/offline-sync-demo seed \
  11111111-1111-1111-1111-111111111111 C1 initial ""

# 离线编辑只访问 offline-sync-pilot.db，不读取或打开 local-server.db。
pilot /home/work/data/xxx/Plinth/.artifacts/offline-sync-demo edit \
  11111111-1111-1111-1111-111111111111 1 C1 offline local
pilot /home/work/data/xxx/Plinth/.artifacts/offline-sync-demo list
```

输出为 JSON，枚举显示为名称；从 `edit`/`list` 输出取 `OperationId`。
后续 `show`/`list` 的新进程读取相同文件，恢复本地载荷和状态，不发送。
`list [skip] [take]` 默认 50 项，最多 200 项；`show <operation-id>` 查单项。
再次编辑 Pending 时必须把原 `OperationId` 放在 `edit` 命令最后，否则旧草稿保护会拒绝覆盖。

直接发送路径：执行一次 `pilot <目录> sync`，再 `show <operation-id>`，
应看到 `Succeeded`、`Attempts=1` 和服务端版本 2。再次 `sync` 输出 `{"Sent": false}`。
不要在下列冲突路径前发送；下列命令接着前面的 Pending 草稿执行：

```bash
# 模拟另一端先修改；仍然执行真实的原子版本比较。
pilot /home/work/data/xxx/Plinth/.artifacts/offline-sync-demo remote-edit \
  11111111-1111-1111-1111-111111111111 1 C1 remote server
pilot /home/work/data/xxx/Plinth/.artifacts/offline-sync-demo sync
pilot /home/work/data/xxx/Plinth/.artifacts/offline-sync-demo list

# 将下面的 <原操作ID> 替换为实际输出的 GUID（不要保留尖括号）。
pilot /home/work/data/xxx/Plinth/.artifacts/offline-sync-demo show <原操作ID>
pilot /home/work/data/xxx/Plinth/.artifacts/offline-sync-demo merge <原操作ID> C1 merged explicit
```

原操作应是 `Conflict`，同时保留 offline 本地快照和版本 2 的 remote 快照。
`merge` 返回新的 Pending 操作 ID，`ExpectedVersion=2`，原操作保留历史和 `SuccessorId`。
也可以改用 `accept <原操作ID>` 显式接受已保存的服务端快照/删除，
不会创建新操作或写服务端；不要对同一冲突同时执行 accept 和 merge。
服务端若在解决后又变化，下一次发送仍会冲突。

用合并返回的**新操作 ID** 演示“服务端已提交但响应丢失”：

```bash
pilot /home/work/data/xxx/Plinth/.artifacts/offline-sync-demo sync lose-response
# 上一条预期退出码 1；服务端数据和回执已提交，客户端保持 Uncertain。
pilot /home/work/data/xxx/Plinth/.artifacts/offline-sync-demo show <新操作ID>
pilot /home/work/data/xxx/Plinth/.artifacts/offline-sync-demo sync
# Sent=false，禁止自动重发；显式读取该操作的终态回执后核对。
pilot /home/work/data/xxx/Plinth/.artifacts/offline-sync-demo reconcile <新操作ID>
pilot /home/work/data/xxx/Plinth/.artifacts/offline-sync-demo server \
  11111111-1111-1111-1111-111111111111
```

核对后应为 `Succeeded`，版本为 3、内容为 merged，尝试计数仍为 1。
`sync` 每次只处理一个到期 Pending，不按指定 ID 发送；存在多个草稿时以队列顺序为准。
退出码 0 表示命令完成（`sync` 的 false 也属于完成），2 表示命令形状不合法，
1 表示执行失败；失败后应先查询状态，不把退出码当作“服务端未提交”的证据。
没有服务端库/表就发送也会保守进入 Uncertain；先 seed 准备服务端，或只做离线编辑。
`seed` 只创建版本 1 的既有记录，重复执行拒绝覆盖。
`discard` 只处理已确认失败的 Exhausted/Rejected，不能清除 Uncertain。

本地传输在独立 `local-server.db` 中使用 immediate SQLite 事务，
将版本检查、更新和终态回执一起提交。回执绑定 scope、操作 ID、记录 ID、
ExpectedVersion 和完整载荷，同 ID 不同请求被拒绝；精确重复返回原回执，不再写记录。
终态回执可以是成功或冲突（包括不存在记录的删除墓碑），不能用最新记录代替。
`reconcile` 不接受命令行伪造的 outcome 或版本，只读精确匹配的回执，再调用原 `ReconcileAsync`。
查不到回执就报错并保持 Uncertain；原状态机仍不会自动重试不确定操作。

这些去重/查询保证**仅属于本地示例 SQLite 实现**，不能推导成生产协议。
scope 由规范化的绝对服务端文件路径哈希和固定 `demo-user` 生成，不接受任意账户输入；
它用于本地路由隔离，不提供实际认证、授权、多租户或生产确认。
目录应受信任，不能移动未完成的库来改变目标，也不要通过符号链接或路径别名访问同一目录。
两个库均含业务文本，未加密、未限额、未自动清理回执，不具备 schema 升级和回执保留协议。
本地传输没有 HTTP、隐藏重试、定时器或正式应用 DI 注册。

### 测试与本次实际执行

```sh
python3 /home/work/data/xxx/Plinth/scripts/pilot_checks.py --mode generate-locks --pilot offline-sync
# 审查真实生成的锁后，单独复验：
python3 /home/work/data/xxx/Plinth/scripts/pilot_checks.py --mode locked --pilot offline-sync
```

统一入口覆盖独立 CLI 与测试项目的 restore、format、build、test，并保留日志和
TRX；首次生成锁与 locked 复验分开执行，详见
[独立试点验收说明](../GETTING_STARTED.md#独立试点的真实-net-验收入口)。
这不包含下文主应用项目中链接的 `OfflineSyncPilotTests`，后者仍需单独验收。

新增测试使用真实临时 SQLite 文件，覆盖离线 CLI 不访问服务端、错误命令无文件副作用、
并发服务端版本比较、持久回执重放/载荷绑定/目标隔离、缺少回执不解锁、
显式接受冲突；另有每条命令均启动新 OS 进程的
`Separate_cli_processes_restore_send_conflict_merge_and_reconcile_lost_response`，
串联离线编辑、新进程恢复、服务端并发修改、发送冲突、显式合并、
提交后丢响应、新进程禁止重发及回执核对。
该测试是正常进程退出/启动和响应丢失注入，不是强制杀进程或断电测试。

另有 `Killed_sync_process_keeps_uncertainty_until_exact_terminal_receipt` 两个参数场景：
父进程通过 xUnit v3 已有的可执行测试入口和精确方法过滤，启动测试专用
`ProcessTerminationFixture.Run`。传输包装器只编译在测试程序集，
不为 CLI、状态机或 `LocalPilotTransport` 增加故障参数。
包装器分别在调用真实服务端之前、真实服务端事务与回执提交后但尚未返回客户端之前，
通过独立命名管道握手并阻塞。父进程在子进程仍存活时，用独立连接确认
`Uncertain`、Attempts=1、完整草稿，以及对应的服务端版本和精确回执是否存在，
再强制终止子进程并等待退出、收完 stdout/stderr。
随后用新的 CLI 进程验证草稿恢复、禁止自动重发/覆盖/丢弃；
发送前终止场景因无回执而保持 Uncertain，提交后终止场景通过精确回执显式核对为
Succeeded，服务端版本不再次增长。握手有超时且会检测子进程提前退出，不依赖 sleep 猜测时序。
CLI 测试辅助方法也在超时或断言异常时执行异步清理，等待终止和输出收尾后才释放进程句柄，
避免仅发出 Kill 就立即删除临时数据库目录的竞态。
这些是**已编写、尚未运行**的真实进程终止/重启测试，不覆盖主机断电、内核崩溃、
存储设备缓存持久性或生产 HTTP 服务行为。

`OfflineSyncPilotTests` 使用独立临时目录和真实文件 SQLite，覆盖离线编辑与重启、
稳定 ID、旧草稿并发保护、有限退避、已提交但响应丢失、无效成功、取消、
独立 worker 领取、冲突合并/接受/删除、明确拒绝和 scope/业务模型隔离；
另含 SQLite trigger 注入的事务回滚/本地保存失败，以及迟到结果和收到成功后取消的竞态回归。
回归通过传输内重新打开数据库验证发送前的 Uncertain 已落盘；
这不是实际杀进程、断电、真实 API/IdP 或桌面端到端验收。

在具备 .NET 10 SDK 和已还原依赖的环境执行：

```sh
dotnet test /home/work/data/xxx/Plinth/tests/Plinth.App.Tests/Plinth.App.Tests.csproj --filter FullyQualifiedName~OfflineSyncPilotTests
```

此前（2026-11-06）的执行记录为 `dotnet --info` 和直接调用独立项目的
`dotnet test` 均返回 `dotnet: command not found`（退出码 127）。
2026-11-07 本轮补充进程终止恢复测试及子进程清理修复，执行 `dotnet --info`
仍返回 `dotnet: command not found`（退出码 127），未执行统一入口的真实 .NET 命令；
独立 CLI 与 C# 测试仍未在本轮编译/运行，
上文预期输出为复现验收标准，并非本环境运行成功记录。
静态检查不替代 EF/SQLite 运行验证。正式接入前还需编译测试、真实进程终止/重启、
磁盘写失败、适配器权限/换号/确认、真实服务原子并发与不确定结果核对验收。

本次实际执行 `scripts/check_source_contracts.py`（0 error）和 `scripts/integration_checks.py`
（PASS）；这两项只证明窄静态契约和项目接线通过，不能记录为 C# 测试通过。
