# OfflineSync 主应用接线说明

主应用当前不注册 OfflineSync。`EnableOfflineSyncPilot` 在部署配置中默认关闭，并且打开该标志不会绕过传输依赖；只有拥有生产可信的 `transportFactory` 后，宿主才能在可信部署的可选模块分支中调用：

```csharp
services.AddOfflineSyncModule(scope => localPilotTransport);
```

`scope` 由模块根据部署、当前账号和数据目录计算，传输工厂必须校验目标 scope。仓库现有真实实现是 `tools/Plinth.OfflineSyncPilot/LocalPilotTransport.cs`，仅是本地 SQLite 演示服务端，不是生产 HTTP 适配器；当前没有已证实的生产端点，不能自行补充。

页面将 Pending、Uncertain、Conflict 分开处理：发送一次最多处理一个 Pending；Uncertain 只能用针对该 OperationId 的权威终态和非敏感证据引用核对；Conflict 只能接受已保存服务端快照或填写合并值生成新 Pending。每项操作复核权限、账号和 SessionVersion，换号、注销或撤权会拒绝旧操作。

页面使用 transient `OfflineSyncPilot`，在页面创建时按当前已验证 subject 计算 scope；不能把匿名启动时创建的单例 Pilot 复用于登录后的账号。刷新在数据库读取前后复核页面生命周期和会话，页面关闭后不会回填集合或忙状态。主应用仍不会因为 `EnableOfflineSyncPilot` 自动注册本页，必须由可信宿主提供 `transportFactory`；当前仓库没有生产 HTTP 端点、认证协议或幂等查询契约。

## 生产 HTTP 接入依赖清单与验收入口

在没有真实协议前不得填写端点或伪造服务配置。未来适配器至少需要明确并单独验收：

- TLS/Authority、受众、租户/部署标识与 scope 组成规则，以及每次尝试的当前账号、权限和确认校验；
- 更新请求的 OperationId、记录 ID、ExpectedVersion、载荷绑定和服务端原子版本比较；
- Applied、Conflict（含删除墓碑）、Rejected、DefinitelyNotAppliedTransient 的权威判定条件；
- 精确 OperationId 终态查询、结果保留窗口、重复请求载荷绑定和响应丢失后的核对流程；
- 明确禁止把超时、取消、5xx、解析失败或查不到记录当成未提交，也禁止隐藏 HTTP resilience 重试；
- 权威审计、错误正文/凭据脱敏、撤权/换号行为和真实并发冲突证据。

本地可执行入口是 `python3 scripts/pilot_checks.py --mode generate-locks --pilot offline-sync`，人工审查锁文件后再执行 `--mode locked`；具备 .NET 10 SDK 后另执行 `dotnet test tests/Plinth.App.Tests/Plinth.App.Tests.csproj --filter FullyQualifiedName~OfflineSyncPilotTests`。这些入口不能替代真实 HTTP、IdP、桌面 Windows 或断电验收；当前宿主缺少 .NET SDK，未声称上述 C# 命令通过。
