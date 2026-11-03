# 远程示例接入

`Modules/RemoteDemo` 是只访问远程 API 的独立示例，不依赖 Demo 模块、SQLite 或通用 Repository。默认不注册。它展示分页、查询、标准表单、授权、生产确认、并发冲突和关闭取消；本仓库不附带身份服务或生产业务服务端。

在受部署管理员控制的 `deployment.json` 中完整配置身份，并增加：

```json
{
  "Profile": "Test",
  "DemoApiBaseUrl": "https://api.example.com/api/v1/",
  "Authentication": {
    "Enabled": true,
    "Authority": "https://identity.example.com",
    "ClientId": "plinth-desktop",
    "Scope": "openid profile offline_access",
    "RedirectUri": "http://127.0.0.1:7890/callback/"
  }
}
```

以上是占位地址。API 地址必须为 HTTPS，以 `/` 结尾，没有 userinfo、query、fragment；空值保持离线默认行为。用户健康检查地址不能改变这个受信任 API 地址。访问令牌通过 Authorization header 发送，不放进 URL 或用户配置。

部署环境变量在 `deployment.json` 之后覆盖同名配置，前缀为 `PLINTH_`，嵌套字段用双下划线。例如：

```text
PLINTH_Profile=Production
PLINTH_MinimumLogLevel=Information
PLINTH_DemoApiBaseUrl=https://api.example.com/api/v1/
PLINTH_Authentication__Enabled=true
PLINTH_Authentication__Authority=https://identity.example.com
PLINTH_Authentication__ClientId=plinth-desktop
PLINTH_Authentication__RedirectUri=http://127.0.0.1:7890/callback/
```

未指定的字段保留文件值，最终配置仍执行身份、地址和超时校验；错误类型或不安全地址会阻止启动。`PLINTH_DATA_DIR` 继续只控制用户数据目录。这些变量由部署管理员控制，不从用户设置或备份导入，也不能保存令牌、密码或 client secret。

`MinimumLogLevel` 控制技术及任务日志，默认 `Information`，可选 `Verbose`／`Debug`／`Information`／`Warning`／`Error`／`Fatal`；修改后重启。提高阈值仍保留安全审计，所有级别均经过相同写时脱敏规则。

## HTTP 契约

路径相对于 `DemoApiBaseUrl`，部署地址包含 API 版本前缀（例如 `/api/v1/`），所有字段区分大小写。示例有意使用小而固定的协议；增加字段时应同步客户端校验和测试。

| 操作 | 请求 | 成功响应 |
| --- | --- | --- |
| 查询 | `GET demo-records?page=1&pageSize=50&search=&sortField=Code&descending=false` | 分页 JSON |
| 新建 | `POST demo-records`，JSON 为 code/name/remark | 完整记录 JSON |
| 更新 | `PUT demo-records/{id}`，JSON 同上，`If-Match: "3"` | 同一 ID、版本大于 3 的完整记录 JSON |
| 删除 | `DELETE demo-records/{id}`，`If-Match: "3"` | `204 No Content` |

查询 page 从 1 开始，pageSize 为 1–200；页面提供 25/50/100/200。sortField 只允许 Code、Name，search 最长 200 字符并做 URL 编码。服务端执行搜索、排序与分页，所有排序追加唯一 ID；不能把百万条记录一次返回给客户端再分页。

分页响应示例：

```json
{
  "page": 1,
  "pageSize": 50,
  "totalCount": 1,
  "items": [
    {
      "id": "be6f295b-081d-49c5-97de-a199e296df58",
      "code": "C1",
      "name": "示例",
      "remark": "",
      "version": 1
    }
  ]
}
```

totalCount 为非负 Int32；page、pageSize 回显请求。items 不超过 pageSize，数量应与该页和 totalCount 一致，ID 不重复。越界页返回该请求页码、总数及空 items，客户端不偷偷全量重查。记录 ID 是非空 Guid 的 D 格式，version 为正 Int32。Code／Name 必填，最长 50／200 字符；Remark 最长 500 字符，允许空串；字符串必须是合法 Unicode，不能含空字符。新建和更新返回 JSON 的 media type 必须为 application/json 或相应 `+json`。

## 服务端职责

服务端验证 access token，并分别落实 `demo:read`、`demo:save`、`demo:delete`；客户端检查不能代替服务端检查。编码唯一性与本地示例保持同样的 ASCII 大小写折叠语义，数据库约束必须兜底。服务端维护版本，更新和删除在一个原子操作中比较强 If-Match；条件不匹配返回 412 或 409，不覆盖新数据。缺失并发条件应拒绝，不能自动采用当前版本。

401／403／409／412 等状态映射到客户端固定文案。可以返回 `X-Correlation-ID`，或 JSON 中安全的 correlationId；客户端不展示原始错误正文。服务端保存权威审计、UTC 时间和操作者，客户端日志不是远程审计记录。

写请求不自动重试。网络中断、取消、响应格式错误或账号变化都不能证明服务端没有提交；先查询权威结果再决定下一次操作。本示例不提供离线同步、跨库合并、远程批量导入导出或远程备份。

## 页面与验证

搜索、排序和页大小先作为草稿，点击“应用搜索和排序”才请求。切换记录、翻页、搜索、新建和关闭均经过未保存确认。页面只持有当前页，不在本机数据库保存远程数据。注销／换号后清除旧结果和编辑内容，重新登录后显式刷新；迟到请求不能发布到新账号页面。

列表使用 TableView 和标准表单，提供列头、列宽调整与“编辑”入口。成功查询零条记录时显示空态；加载、查询失败和会话失效分别显示对应状态。菜单及 Shell 集中快捷键支持 Ctrl+N 新增、Ctrl+S 保存、Delete 进入删除确认、Esc 先取消删除确认再处理未保存表单；Ctrl+F 聚焦搜索。输入控件仍保留文本编辑语义。

会话版本在登录、注销及会话清理时递增，普通令牌刷新不改变版本。页面重新附着时会检查版本，因此隐藏期间注销再登录同一账号，也不会保留旧会话草稿。自定义 `IRemoteUserContext` 应实现同样的 `SessionVersion` 语义；默认值仅兼容固定身份上下文，不提供会话变化检测。

正常令牌刷新会先保存轮换凭据，再验证 UserInfo；等待验证时保持原身份，避免页面把刷新误认为注销并清除草稿。验证失败仍清理会话，验证成功才发布新的身份和权限。

`RemoteDemoTests` 使用内存 HTTP handler 覆盖 URL、分页校验、强 ETag、权限／确认、取消、账号变化和错误脱敏，`DeploymentConfigurationTests` 覆盖地址与身份门槛。测试源码存在不代表已执行；当前环境无 SDK，也没有真实 API／IdP。发布前须运行 C# 测试，再对实际服务执行登录、分页、权限拒绝、冲突、超时后核对、换号清理和 Windows 页面验收，保留实际证据。
