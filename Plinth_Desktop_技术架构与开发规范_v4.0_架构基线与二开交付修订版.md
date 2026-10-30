# Plinth Desktop Framework：通用原生桌面 Admin 框架

> **品牌：** Plinth（柱础） — *One Foundation. Many Applications.（一个可靠底座，无限业务扩展。）*
> **标识体系：** Plinth Core（必要共享类型） · Plinth UI（桌面外壳与通用控件） · Plinth Data（本地 SQLite/EF Core；为服务端 PostgreSQL/MySQL 预留可移植边界） · Plinth Modules（业务模块）
> **文档版本：v4.0（架构基线与二开交付修订版）**
> 最后更新：2026-10-31（依赖版本快照核验日：2026-10-29）
> 首发目标：Windows 11 x64；Linux x64 从 Phase 0 开始持续构建并在正式发布前独立验收
> 目标读者：项目负责人、架构师、AI 编码 Agent、后续二次开发者
> 文档地位：**本文件是当前实施基线。以 Avalonia 官方模板和标准 .NET 写法为起点，只为真实需要增加清楚、易理解的扩展；不自创框架，不为未来堆叠空接口、空项目或审批流程。版本快照见 §5，正式锁包前仍需复核。

---

## 目录

- §0 决策摘要与扩展原则
- §1 产品边界 · §2 桌面交互与视觉 · §3 通用功能
- §4 简单架构与官方项目结构 · §5 依赖与许可证 · §6 数据网格 · §7 工作区
- §8 数据库与数据归属 · §9 国际化 · §10 安全 · §11 日志诊断 · §12 导入导出
- §13 性能可靠性 · §14 测试与发布 · §15 安装 · §16 扩展路线
- §17 开工验证 · §18 计划 · §19 风险 · §20 验收
- §21 Agent 指令 · §22 主要架构决策 · §23 官方参考 · §24 结论

---

## 修订说明：v4.0 架构基线变更（不占章节号）

本版在 v3.9 基础上修正两项影响长期维护的决定，并同步清理相关章节：

- **数据网格不再默认选用 `Avalonia.Controls.DataGrid`。** Avalonia 官方已将其标记为 Deprecated、只接收 Bug 修复；只读列表首选 Avalonia 12 的 `TableView`，记录编辑统一走普通表单。首版不承诺单元格内联编辑。
- **不默认采用商业版 TreeDataGrid/Accelerate。** 旧版开源 TreeDataGrid 仓库已归档、不再维护，许可仍为 MIT 但不能把它当作有持续上游维护的默认方案。
- **明确二开交付为“源码优先、NuGet 可选”。** 源码仓库和可运行模板始终是开发与调试的权威来源；共享核心库达到稳定边界后可以发布 NuGet，但不得强迫所有二开者只使用黑盒二进制。
- 更新 Phase 0、依赖表、首版范围、风险表、ADR 和 Agent 指令，删除旧 DataGrid 默认决策残留。
- 不因此自研完整网格、不提前建立通用控件适配框架，也不增加审批层级。常见 CRUD 通过列表选择记录、打开表单、保存/取消完成。
- **2026-10-31 勘核修订（不影响 v4.0 决策本身）：** 对照本机 Avalonia.Templates 12.1.4 实际生成结果修正 MVVM 模板文件名（`ViewModels/MainViewModel.cs`，§4.6.1/§4.6.2），并补全模板生成文件清单、官方 DI 组合根与 `ServiceCollectionExtensions` 命名（§4.2/§4.3/§4.6）；统一“密度”档位口径（§3.4 与 §0.4/§2.3 对齐）；删除 `Delete` 快捷键表述中不存在的“撤销保护”（§2.2）；补充 `Serilog.Formatting.Compact` 依赖与 JSON formatter 选型（§0.1/§5）；修正 TableView 列可见性描述（`TableViewColumn.IsVisible` 存在，但没有内置列菜单，§6.2/§6.4）；补充 MySQL Provider 许可候选复核说明（§8.4）；新增成熟 .NET 开发者对照表（§4.6.6）。

**版本原则：** 采用当前受支持、兼容且许可证合格的稳定版本，不追 Preview/RC。候选版本是锁包前的核验起点，不代表已经完成编译或实机验收。

**本版决策已经更新：** §0、§5、§6、§16.3、§17、§19、§21、§22 与 §24 必须以 v4.0 口径为准；文档中不再把 `Avalonia.Controls.DataGrid` 写成 v1 默认首选。

---

## 0. 最重要的决策摘要

Plinth 是一套**真正的桌面 Admin 框架**，用来反复开发进销存、ERP、文件管理、数据分析、工业管理和内部业务软件。它不是网站后台模板，不使用 WebView/Electron/Tauri Web 页面伪装桌面程序。应用 UI 由 **Avalonia 以 Skia 自绘**在桌面运行时绘制，交付包含真实的 `Plinth.exe` 和运行所需文件的可安装/可分发包。

### 0.1 技术选择与边界

| 领域 | v1 默认方案 | 说明 |
|---|---|---|
| 桌面 UI | **Avalonia + C#/.NET 10 LTS + XAML** | Windows 11 x64 首发；不是 WebView 壳，但也不声称是 OS 原生控件。Avalonia 当前核心候选为 12.1.4；锁版以 POC 和依赖兼容性为准。 |
| UI / MVVM | Avalonia 标准控件与主题 + CommunityToolkit.Mvvm | 使用常见 XAML、ViewModel、DI 模式；不另造路由 DSL、状态框架或控件协议。 |
| 依赖注入 | Microsoft.Extensions.DependencyInjection | 按 Avalonia 官方 DI 指南在 `App.axaml.cs` 建容器；只在组合根注册，业务模块通过构造函数注入。 |
| 数据列表 | **Avalonia 12 `TableView` 作为只读列表首选；编辑走标准表单** | `TableView` 属于 Avalonia 核心控件，官方定位为可配置列的只读表格。v1 不把单元格内联编辑作为必需能力。DataGrid 已被官方标记为 Deprecated，仅接收 Bug 修复，不再作为默认新项目选型。 |
| 工作区 | 单主窗口 + 标签页 + 固定可调分栏 | 首版不做自由停靠、多主窗口和运行时插件；不自研 Dock 引擎。 |
| 本地数据 | **SQLite + EF Core（纯本地桌面模式）** | 仅承担本机数据/偏好/缓存。v1 不同时维护 Dapper；先优化查询、索引和投影。SQLite 专属类型不得泄漏到 Core/业务模型。 |
| 后续服务端数据库 | **PostgreSQL + MySQL 均列为目标；v1 不随桌面客户端打包 Provider** | 远程模式只走 HTTPS API。未来服务端在组合根选择 EF Core Provider；共用业务规则与可移植模型，各数据库拥有独立迁移和集成测试。实际支持以 Provider 许可与 POC 为准。 |
| 远程数据 | HTTPS REST/JSON API | 客户端不直连多人共享生产数据库、不携带生产库高权限密码；服务端负责最终鉴权与业务校验。 |
| HTTP 韧性 | HttpClient + `Microsoft.Extensions.Http.Resilience` | 一套超时、取消、Correlation ID 和可控重试配置；非幂等写操作默认不自动重试。远程功能需要时才引入此包。 |
| 国际化 | .NET RESX / ResourceManager + 标准 CultureInfo | v1 完整支持 `zh-CN`、`en-US`；格式化、时区、货币分别处理。使用框架和运行时能力，不额外自建翻译引擎。 |
| 认证 | 远程模式采用 OIDC Authorization Code + PKCE + 系统浏览器 | 通用候选 `Duende.IdentityModel.OidcClient`（Apache-2.0）。MSAL 只在确定需要 Microsoft Entra ID 专用能力时按需加入；本地单机模式不强制带认证包。 |
| 凭据 | Windows Credential Manager；Linux 正式支持时使用 Secret Service/libsecret | 秘密不写普通 JSON、SQLite 或日志。缺少安全存储设施时明确提示，不降级明文。Linux 仍是后续平台验收范围。 |
| CSV | CsvHelper `34.0.0`（MS-PL 或 Apache-2.0 双许可） | 已有稳定版发布（2026-10-28）；采用并归档 Apache-2.0 许可文本。锁定前跑中文编码、行级错误、长数字、取消、公式注入回归。 |
| XLSX | ClosedXML（MIT，候选） | Should；通过中文、长数字、日期、损坏文件和内存 POC 后启用；否则先提供完整 CSV，不能为 Excel 强加商用依赖。 |
| PDF / 打印 / 条码 | v1 不进入核心默认依赖 | Could。SkiaSharp 是绘图底层，不是通用排版/分页引擎；未来确有需求再选合格的布局库或由业务模块使用固定模板。 |
| 图表 / 本地分析 | 不放入核心默认依赖 | 只有具体业务需要、且数据量与组件许可通过评估后，再作为可选模块引入一个实现。 |
| 日志 | Microsoft.Extensions.Logging API + Serilog 实现 | `Serilog` + `Serilog.Extensions.Logging` + `Serilog.Sinks.File` + `Serilog.Formatting.Compact`；JSON 输出统一用 `CompactJsonFormatter`（NDJSON/CLEF）；业务代码统一使用 `ILogger<T>`，日志容量有上限。 |
| 测试 | **xUnit v3 + Avalonia Headless**（用于适合的 UI 冒烟） | 用标准测试工具；真实窗口、IME、DPI、安装等仍要人工实测。测试栈的版本与「为什么不能用模板默认 v2」见 §5。 |
| 发布 | self-contained 应用目录 + Windows 安装包（NSIS 候选）+ 便携 ZIP | v1 不做 NativeAOT、不主动裁剪、不做自研自动更新器；安装包/压缩模块许可须在发布前核查。MSIX 作为后续选项。 |
| 许可证 | 默认 MIT / BSD / Apache-2.0 / SIL OFL 等已核验许可证 | 每次正式发布核对直接与传递依赖的许可正文；禁止默认引入需购买授权或有非商业/营收门槛的依赖。 |

**版本管理规则（最新稳定版，不追预览版）：** “最新”指 2026-10-29 可核验、适配当前 LTS 且满足许可/兼容约束的最新稳定版，不是每个包各自追最高数字，更不是 Preview/RC。以本表为候选快照，实际合并前由 NuGet/上游发布页再次复核；随后通过 `Directory.Packages.props` 集中声明包版本、用 `global.json` 固定 SDK，并为使用 NuGet 的项目生成并提交各自的 `packages.lock.json`。所有直接依赖/传递依赖必须由构建与许可扫描确认，不得靠手工猜版本。每月至少检查一次安全补丁；有影响本项目的高危安全更新时提前处理。

### 0.2 防返工与低学习成本原则

1. **先按用户熟悉的方式做，再讨论创新交互。** Windows 桌面用户常见的菜单、工具栏、快捷键、表格、表单、对话框和错误提示优先；不把隐藏手势当主路径。
2. **复用成熟库，但不要为了“通用”造一层新框架。** 使用标准 MVVM、DI、EF Core、RESX 和 HttpClient；新接口、新配置语言、事件总线、通用查询 DSL 必须能证明确实减少复杂度，否则不加。
3. **每项能力只有一条默认实现。** 不同时维护 EF Core 与 Dapper、两种 OIDC 客户端、两种图表、两种日志门面。为了未来数据库扩展只遵循可移植编码规则，不提前建万能 Repository/Provider 框架。可选能力按需安装，不默认塞入核心。
4. **先验证风险最高的少数项目。** Phase 0 聚焦桌面基本交互、TableView 列表、SQLite/EF Core、发布与凭据；单元格内联编辑、PDF、自由停靠、离线同步等后续功能不阻塞开工。
5. **用实际用户任务证明“上手容易”。** 让未参与设计的人完成常见任务，记录哪里找不到、哪里误操作；不能只以开发者自评或代码行数判断简单。
6. **默认保守、按数据风险加复杂度。** 查询分页、备份恢复、权限、脱敏必须可靠；高级报表、运行时插件、AOT 和通用可视化引擎没有真实需求就不引入。
7. **说明与样例随代码同步。** 不写大段脱离实现的空文档，但每个阶段都要保持快速入门、样板和关键限制可用；新人不能依靠翻聊天记录或读全部架构 ADR 才能新增一个页面。

### 0.3 明确不做

- 不用 WebView/Electron/Tauri Web 作为主界面；定位是 XAML 桌面 GUI，不宣称 Avalonia 使用 OS 原生控件。
- 不自研数据网格、图表引擎、SQL 数据库、Excel 解析引擎、通用停靠系统或通用 PDF 排版引擎。
- v1 不引入 CQRS/Mediator、事件溯源、全局事件总线、泛型 Repository 框架、通用过滤表达式 DSL、自研 DI / 路由 DSL。
- v1 不同时维护 EF Core 与 Dapper，不同时引入两套 OIDC 客户端，不同时引入两个图表方案。
- 不引入需购买商业授权、非商业许可或收入门槛许可的硬依赖；找不到合格组件时先缩小范围/延后功能。
- 不做任意代码热加载、插件商店、多窗口、自由停靠或通用离线双向同步。
- 不宣称一次性载入百万条记录；大数据必须用数据库查询、分页、流式导出和服务端聚合。
- 不把 SQLite 文件放网络共享盘当多人并发数据库。
- 不将 SQLite 专有 SQL、`rowid`、特有排序/大小写规则当作通用业务能力；不将 PostgreSQL/MySQL 驱动类型带进桌面 UI/Core。
- 不在 v1 声称已支持 PostgreSQL/MySQL；只声明数据模型与分层按可移植性规则设计。
- 不把权限藏在 UI；远程业务最终权限检查必须由服务端执行。
- 不将密码、令牌、完整敏感请求体或业务数据库写入普通日志/诊断包；不自动上传日志/崩溃报告。

### 0.4 首版范围三档（Must / Should / Could）

只有 Must 阻断首版 GA。**档位以本表为准**；正文中的“应有/建议/未来”不得自动升级为 GA 门槛。

**Must 里「远程模式 OIDC 接入点」的判定边界：** 若本版本交付形态包含远程模式，阻断项是「OIDC + PKCE 接入点已实现，并通过真实身份提供方（或经批准的语义等价桩）完成端到端冒烟」；若首版只交付本地模式，整条不适用并按 §17 推迟，不得把「已预留接口」写成「已支持远程认证」。

| 档位 | 首版能力 | 失败时的处理 |
|---|---|---|
| **Must** | 主窗口、常见菜单/工具栏/状态栏、标签工作区；深浅主题；`zh-CN`/`en-US`；SQLite + EF Core 迁移与备份恢复；TableView 列表/标准表单 CRUD；分页/排序/基本筛选；可靠表单编辑；CSV；任务/错误中心；结构化日志与双阶段诊断脱敏；基本权限；远程模式 OIDC 接入点；Windows 安装/升级/卸载安全；常见快捷键；**数据库可移植性编码规则** | 任一关键数据安全、基本 CRUD、安装恢复或可移植性规则不合格，不发布 |
| **Should** | 在有真实业务需求且找到维护状态、许可证合格的成熟控件后，提供基本单元格内联编辑；XLSX（ClosedXML）；无障碍辅助技术验收；系统 Toast；紧凑/舒适密度的完整调校 | 延后；首版继续使用标准表单编辑，不因此自研完整网格或引入商用依赖 |
| **Could** | 分组小计/合计、冻结列等高级网格能力；PDF/打印/条码；Linux 正式支持（持续构建和基础冒烟仍应做）；Dock 自由停靠、多窗口/托盘；运行时插件；离线双向同步；图表/本地分析引擎；NativeAOT/主动裁剪 | 不影响 Windows 首版发布，按真实项目需求单独立项 |

首版重点不是“功能列表尽量长”，而是把普通用户每天要做的 **查找 → 查看/新增/编辑 → 保存/取消 → 导入/导出 → 查看错误** 做到直观、稳定、一致。

### 0.5 通用底座的扩展原则

Plinth 是供多个业务应用二次开发的底座。现在要守好数据、安全和兼容边界，但不提前实现全部未来能力。详细触发条件统一见 §16.5。

- **现在定规则：** 稳定 ID、时间/金额语义、API DTO 与数据库实体分离、权限边界、配置升级、文件逻辑标识、任务取消、日志脱敏。
- **现在不写死：** 数据库厂商、身份提供方、文件物理路径、缓存实现、操作系统专属 API；平台差异放在少量基础设施代码中。
- **需要时再加设施：** PostgreSQL/MySQL Provider、对象存储、分布式缓存/队列、全文搜索、OpenTelemetry 导出器、运行时插件、自动更新和离线同步。
- **不以接口数量衡量扩展性：** 一种实现先直接采用主流写法；出现真实的第二种实现或明确复用需求时，再提取最小接口。
- **保留开发自由：** 只对安全、数据正确性、公开兼容和许可证设硬约束；其他实现细节允许开发者按实际需求调整，并保持常规代码评审。
- **源码优先、NuGet 可选：** 不为了包交付提前拆分项目；可复用边界稳定后再出包，任何包都能定位到对应源码和样例。

---

## 1. 产品边界与交付目标

### 1.1 框架目标

Plinth 首版交付一个可运行、可二次开发的 Windows 桌面基础产品：

- 桌面应用壳：顶部菜单、工具栏、标签、左侧导航、可调整工作区、状态栏。
- 通用列表 + 表单 CRUD；标准查询、分页、排序、基本筛选、选择、列设置。
- 清楚的新增/编辑/查看/删除流程，脏状态提示、保存/取消和字段校验。
- SQLite 本地数据、EF Core 迁移、备份恢复和用户偏好。
- CSV 导入导出、后台任务、进度/取消、可搜索的日志与错误中心。
- 本地单机与远程 API 两种常见数据模式；远程模式接入 OIDC 和服务端权限。
- 模块通过常规 C# 注册入口扩展菜单、页面、服务、翻译资源和迁移。
- 深浅主题、双语、常见快捷键、清晰的帮助与版本信息。
- 一个可运行示例模块和短教程，让新开发者不用读完整架构文档即可完成第一个 CRUD。
- 安全默认值、关键回归测试、CI、依赖许可证清单、Windows 安装/升级/卸载验证。

### 1.2 不属于通用核心的业务

具体商品/订单/客户、仓库批次和序列号、财务记账规则、设备协议、工业控制、特定行业审批流，均通过业务模块实现。核心提供表格、表单、权限、任务、CSV 导入导出、日志与必要审计等通用能力；打印/PDF 等仅在具体项目需要时添加，不将业务模型写死在框架内核。

### 1.3 平台承诺

- **首发平台：** Windows 11 x64。
- **Linux：** 保持可构建目标并尽早做启动、字体、输入法和文件/凭据路径测试；未完成目标桌面环境的实机验证前，不对外承诺正式支持。
- **未来平台：** 路径、配置、文件、凭据和系统集成使用标准 .NET API，并将确有必要的平台差异集中处理。ARM64、macOS、移动端及信创发行版均按实际需求另行评估，不提前承诺交付时间。

“代码没有写死某个平台”只代表降低迁移成本，不等于该平台已经兼容或通过验收。

---

## 2. 专业桌面软件 UI 与交互规范

Plinth 采用专业桌面软件的工作流，参考 IDE、CAD、数据管理工具的操作模型，而不是网页后台布局。

### 2.1 桌面窗口结构

```text
┌─────────────────────────────────────────────────────────────────────────┐
│ 标题栏：应用名称 / 当前文档                              最小化 最大化 关闭 │
├─────────────────────────────────────────────────────────────────────────┤
│ 文件  编辑  查看  数据  工具  窗口  帮助                                  │
├─────────────────────────────────────────────────────────────────────────┤
│ 工具栏：新建 保存 编辑 删除 刷新 搜索/筛选 导入/导出                  │
├──────────────┬───────────────────────────────────────┬──────────────────┤
│ 导航/收藏面板 │ 文档标签：概览 | 数据列表 | 编辑器        │ 属性/详情/任务面板 │
│ 模块树        ├───────────────────────────────────────┤                  │
│ 最近项目      │ 当前工作区：专业表格 / 表单 / 报表        │ 可折叠/可调整宽度  │
│ 收藏          │ 键盘导航、右键菜单、多选、批量操作         │                  │
├──────────────┴───────────────────────────────────────┴──────────────────┤
│ 状态栏：连接/同步状态 | 任务进度 | 当前选择数 | 缩放 | 用户/环境            │
└─────────────────────────────────────────────────────────────────────────┘
```

- 标题栏默认保留系统窗口能力；应用菜单可由 Avalonia 绘制。Avalonia 在 Win/Linux 上通常不暴露 OS 原生 HMENU 菜单，须实现桌面菜单行为与快捷键，**不得声称它就是原生系统菜单**。
- 主工作区默认单主窗口、多标签、可调整分栏（薄自研）。**自由拖拽停靠不在首版**（Dock 为 Could，见 §7.1），不引入额外 docking 概念。
- 所有主操作支持键盘和鼠标；提供菜单加速键、快捷键冲突检测、右键上下文菜单、焦点视觉状态、表单未保存提示。
- 关闭标签必须有明确行为：已保存直接关闭，脏文档询问保存/丢弃/取消；任务处理不能留下悬挂监听。
- 多显示器、高 DPI、125%/150% 缩放、窗口尺寸变化和字体回退必须验证。

### 2.2 菜单和工具栏规则

- 菜单至少包含：文件、编辑、查看、数据、工具、窗口、帮助；不同业务模块通过命令注册机制贡献菜单项。
- 工具栏由当前工作区上下文决定；按钮需有图标、文字/提示、禁用状态和键盘入口，不能只显示无法识别的图标。
- v1 不强制提供命令面板；常用命令直接放在菜单/工具栏，业务搜索用于查找客户、订单等数据。若后续命令很多，再评估命令搜索。
- v1 的常用快捷键在菜单/命令定义处集中维护，并显示在菜单项或提示中；不提供用户自定义快捷键编辑器。未来确有插件冲突问题再扩展。
- 右键菜单必须遵循当前选择与权限状态，不可对不可授权操作仅隐藏而无服务端检查。

#### 让用户“来了就会用”的桌面交互约定

设计依据以 Windows 常见桌面交互为主，不发明 Plinth 专属操作习惯。参考微软的 [Windows app best practices](https://learn.microsoft.com/en-us/windows/apps/get-started/best-practices) 和 [Keyboard interactions](https://learn.microsoft.com/en-us/windows/apps/design/input/keyboard-interactions)。

- **重要操作有可见入口**：新增、编辑、删除、保存、取消、刷新、搜索/筛选、导入导出可从菜单或工具栏找到；双击和右键是快捷方式，不是唯一入口。
- **不改常用快捷键含义**：至少遵循 `Ctrl+N` 新建、`Ctrl+S` 保存、`Ctrl+F` 查找/聚焦搜索、`Ctrl+C/V/X/A` 文本与表格常用操作、`Ctrl+Z/Y` 当前输入控件支持的撤销/重做、`F5` 刷新、`Delete` 删除当前选中项（有确认保护）、`Esc` 取消/关闭当前轻量操作、`F1` 帮助。`Enter` 的作用必须随当前上下文清楚一致。避免全局快捷键冲突，不把跨业务撤销作为首版功能。
- **表格行为有预期**：单击选中；双击或显式“编辑”进入编辑；列宽可拖动；隐藏/排序等设置可从列菜单找到；复制优先支持当前选中单元格/行。`Ctrl+A` 只选当前页时必须明确显示，不得暗示选中全部查询结果。
- **表单行为一致**：清楚区分查看和编辑；必填项标识一致；错误显示在对应字段附近并说明修正方法；保存/取消按钮固定、名称明确；取消未保存修改时提示；保存失败保留用户输入。
- **对话框不设陷阱**：打开后焦点落在最合理的控件；`Tab` 顺序自然；`Esc` 关闭/取消（涉及未保存数据先确认）；关闭后焦点回到触发控件；用户不需要猜测可点击区域。
- **反馈要解释结果**：成功/失败/无权限/无数据/加载中/任务排队都使用一致状态；错误至少告诉用户“发生了什么、下一步怎么做”，不能只显示异常堆栈或短暂 Toast。
- **少记忆、多提示**：专业术语首次出现提供简短解释；危险操作给出影响范围；保留用户上次的筛选/列布局，但提供一键恢复默认。
- **可用性实测目标**：首个可用版本尽量邀请 3 名未参与设计的目标用户完成“查找、编辑保存、取消修改、导出 CSV、查看错误”等任务，并记录卡点。80% 无提示完成可作为改进目标，不单独作为机械的发布拦截线；无法完成核心任务、误删/误覆盖或反复误操作则必须修正。

### 2.3 视觉与设计系统

- 建立 `DesignTokens`：颜色、字体、字号、行高、间距、边框、圆角、阴影、图标尺寸、动效持续时间和焦点环。
- 浅色与深色主题均为正式主题；不是简单反转颜色。Avalonia FluentTheme 可作基础，再按桌面使用密度调整。
- **主题即时切换机制：** 使用 Avalonia `ThemeVariant` 与 `DynamicResource` 实现浅色/深色主题；高对比度按当前控件和主题能力单独验证，不假定存在开箱即用的完整方案。品牌色、状态色、字体层级及核心间距使用 `DesignTokens`；局部控件尺寸允许按实际布局设置，不要求所有数字都抽成 Token。切换主题不得要求重建工作区或重启。CI 只检查明显的品牌色硬编码和模块主题越界，不做脆弱的全量正则扫描。
- 标准密度是首版默认值。紧凑/舒适密度属于可选增强；只有实际用户需要时再完善，不为此额外建设复杂的全局布局系统。
- 颜色以石墨/钛灰/珍珠白为主，少量品牌色强调状态与操作。避免满屏发光、无意义卡片墙、过多空白和持续动画。
- 尊重系统"减少动画"设置。非必要动画不阻碍焦点和交互响应。
- 优先使用 Windows 系统字体和系统回退，减少安装包体积。若真实 Windows 测试出现缺字/排版问题，再评估随包提供 Noto Sans CJK 等开源字体；必须留存字体许可证并实测包体积。
- PDF/打印不是 v1 核心功能。将来启用时，再对字体嵌入、换机显示和打印机输出做单独验收；当前不做 PDF 字体子集化或排版引擎的预研。
- 可换品牌：Logo、应用名称、品牌强调色、About 页、帮助链接和支持信息通过 `BrandingConfig` 配置。

### 2.4 必须形成的 UI 规格

在完整 UI 开发前，先形成 **3 个可运行的核心界面样板**，不要求先做六张独立设计稿，也不把设计稿另算成项目外的大项：

1. 主工作区：菜单、工具栏、导航、标签、状态栏。
2. 数据列表：搜索/筛选、分页、列菜单、批量选择、空/加载/错误状态。
3. 数据编辑表单：字段校验、脏状态、保存/取消、错误反馈。

日志中心、设置中心和模块管理直接复用这三类页面的设计规范。样板应覆盖正常、空、加载、错误等常见状态，并结合 §2.2 的用户任务测试持续改进；最终界面必须是实际 Avalonia 控件，不得以静态图替代实现。

## 3. 通用功能范围

### 3.1 应用壳与工作区

- 菜单、工具栏、常见快捷键和命令注册；v1 不强制做命令面板。
- 多标签工作区、脏状态、关闭确认、最近项目/页面、收藏、布局保存与重置。
- 可调节分栏、导航面板、属性/详情面板、任务面板（**首版不支持自由拖拽停靠**，见 §7.1）。
- 系统主题适配、深浅主题、标准密度、高 DPI 和多显示器基本适配；紧凑/舒适密度可按需求补齐。
- 应用信息、开源许可证、版本更新提示、F1 帮助中心。

### 3.2 通用数据管理

- 标准列表/详情/新增/编辑/删除；表单分组、必填、字段校验、脏状态、保存/取消。
- 表单和导入共用业务校验方法；不额外建一个可视化校验 DSL。优先使用 .NET 常规校验方式；只有多个模块反复出现复杂规则时，再评估 FluentValidation。
- 列表支持分页、常见排序、基本筛选、列显示/隐藏/宽度与布局保存。分组小计、复杂多列过滤、冻结列不作为 v1 阻断项。
- 主要编辑路径是“选中记录 → 编辑表单 → 保存/取消”。单元格内联编辑不属于首版必需功能；未来只有在有明确业务需求、成熟开源控件通过验证时才纳入 Should，必须有清晰的提交/取消和错误反馈，不能失焦后悄悄提交。
- 远程列表使用服务端分页/排序/筛选，客户端不得拉取全部数据后再过滤。
- 查询可以取消；新请求发出后，旧请求结果不得覆盖新条件；慢任务显示进度和可恢复错误。
- CSV 是首版必需；XLSX 通过 POC 后作为 Should；失败行报告、取消和重复处理要有明确语义。
- “全选查询结果”必须使用服务端查询条件/令牌；只选当前页时在 UI 中明确说明。
- 查询字段和排序字段必须从模块白名单映射；禁止把 UI 传来的字段名/过滤文本直接拼进 SQL。

### 3.3 用户、身份和权限

- 本地单机模式、远程 API 模式；远程模式支持 OIDC SSO + PKCE 适配接口。
- 用户、角色、权限点、模块权限、操作权限模型和用户上下文；权限点按“资源 + 动作”稳定命名，不直接使用 UI 文案当权限 ID。
- 本地 UI 权限判断仅用于操作体验；远程 API 最终授权必须在服务端执行。
- 如服务端支持多租户，租户范围由服务器根据登录身份和成员关系解析/校验；客户端传入的租户选择不是授权依据。v1 单机数据模型不要求所有实体盲目添加 TenantId，但服务端必须把租户过滤集中在可信的数据访问层，不得依赖客户端传入值或散落为可选条件。
- 支持明确的退出登录、会话过期和重新登录流程；空闲自动锁定按具体客户安全要求决定，不给所有本地单机应用强加登录流程。
- 审计记录操作者、时间、模块、操作、结果和关联 ID；不记录秘密值。

### 3.4 设置、配置和连接

- v1 设置先覆盖语言、地区/时区、主题、窗口/工作区布局和日志保留；密度只保留首版标准密度（紧凑/舒适为 Should，见 §0.4）；可配置快捷键、字体选择等不列为首版必需。
- 环境 Profile：开发、测试、生产；生产有视觉标识和操作确认，严禁把真实密钥保存在可提交配置中。
- 连接配置保存 endpoint、环境名称等非秘密字段；token/password 放 OS credential store。
- 配置按功能分组并使用标准 Options/配置绑定；启动时校验必需项。远程部署可用环境变量覆盖，首版不做远程配置中心。
- 设置有 schema 版本，升级时迁移；配置损坏时恢复默认值但先备份原始文件并写诊断事件。任何持久化用户偏好都不得依赖当前版本特定的 UI 内存对象格式。

### 3.5 后台任务和通知

- 提供轻量任务中心：等待、运行、成功、失败、取消；任务显示进度（如可计算）、耗时、发起模块和可读错误。
- 导入导出等长任务可取消；重试由任务或服务明确提供，不做“所有任务自动重试”。写操作只有在确认幂等后才允许重试。
- 使用一个简单的全局并发上限和队列即可；首版不做按模块配额、公平调度算法等高级调度功能，除非实测出现某模块长期占满队列。
- 长任务在后台执行，UI 可继续操作；任务结束时用应用内提示，系统 Toast 为 Should。
- 页面关闭时，属于页面的查询可取消；已开始的全局导入/导出任务由任务中心接管，避免随标签关闭而被意外丢弃。任务中心首版仅对当前进程负责，不承诺跨进程/重启恢复；若未来需要持久任务，另行定义任务存储、幂等、租约/重试和执行器，不直接把本地队列扩展成分布式队列。

### 3.6 日志、审计和诊断

- 工具菜单提供"诊断工具 → 日志与错误中心"。
- 结构化日志：时间 UTC、级别、组件、模块、事件码、关联 ID、任务 ID、错误和堆栈（安全过滤后）。
- 技术日志、安全审计、业务历史、任务历史在事件类型和查看权限上明确区分；v1 不强制拆成四套存储。普通技术日志使用有容量上限的滚动文件，远程业务审计以服务端记录为准。
- 日志按大小/日期轮转、有容量上限；默认不上传数据。
- 诊断包导出前可选日期、日志等级和文件类别；显示实际文件清单、字段、大小和脱敏状态；构建后再次扫描脱敏并验证包内容。
- 默认排除 token/password、请求体、数据库文件、附件、个人信息和完整查询参数；导出用户需确认。
- 远程 API 使用 `correlationId` 关联客户端与服务端日志；如果服务端启用分布式追踪，再按 W3C Trace Context 传递 trace context。客户端无权自动浏览服务器安全审计。OpenTelemetry 的采集与导出器不是桌面核心默认依赖。

### 3.7 文件、帮助和数据可携带

- 使用系统文件选择器；对大文件流式读取/分批写出。拖放导入可作为便利功能，但不替代清楚可见的“导入”按钮。
- 文件导入防路径穿越、压缩炸弹、文件类型伪装和超限；CSV 导出防公式注入。
- 业务模块需要引用文件时，保存文件 ID/逻辑存储 key 和经过校验的元数据，不把本机绝对路径当业务字段；首版实际文件仍存用户数据目录，暂不建立云对象存储接口。若未来需远程附件或对象存储，再引入小型存储接口及本地/对象存储实现。
- 帮助中心支持 F1 上下文 topic key、模块帮助、搜索、版本和常见错误跳转；离线有基础本地帮助。
- 本地设置和用户数据提供清楚的备份/恢复/迁出路径；卸载不得静默删除用户数据（见 §15）。

### 3.8 导入导出与打印的边界

- **CSV 是 v1 必需能力；XLSX 为 Should；PDF、打印和条码为 Could。** 能力档位以 §0.4 为准，不在核心中提前引入绘图、排版或打印依赖。
- 将来加入 PDF/打印时，必须选择许可证与维护状态合格的方案，并在真实 PDF 阅读器/打印机上验证分页、字体、纸张大小和中文输出；若只是个别固定单据，可由业务模块单独实现，不能把一两张模板包装成“通用报表引擎”。
- XLSX 不执行宏、不信任外部公式缓存作为事实值；任何导入格式都要有大小限制、取消能力和逐行错误反馈。

### 3.9 模块与扩展

- v1 只支持**编译期 C# 模块**，随应用构建；不做运行时发现、下载、热加载或插件商店。
- 每个模块提供一个普通 C# 注册类/扩展方法，注册自己的服务、菜单/页面、翻译资源、权限点和 EF Core 迁移；不额外维护重复描述同一信息的 JSON Manifest。
- 模块 ID、显示名称和注册入口在普通 C# 代码中明确写出，并配一个真实可运行的示例。不做反射扫描，也不要求另写一份 JSON Manifest。
- 模块初始化失败时要显示哪个模块启动失败及诊断入口；如果该模块可以隔离，禁用它的菜单/页面并让其他模块继续启动。数据库迁移失败或核心依赖失败则应安全停止，不得假装全局可用。
- 后续若确实要支持第三方运行时插件，单独设计进程隔离、签名、权限清单和 IPC；不在 v1 预建复杂协议。

## 4. 架构分层与模块边界

```text
Avalonia Views / ViewModels
          ↓
业务模块 Service（负责用例、校验和流程）
          ↓
EF Core + SQLite（本地模式） / 类型化 HttpClient（远程模式）
          ↓
文件、凭据、日志等平台能力
```

只保留足以说明依赖方向的层次，不为了“架构完整”拆出空项目或为每个类加接口。每项能力先按常规 C# 写法实现，出现真实复用/隔离需求后再抽象。

### 4.1 简单、清楚的依赖方向

- ViewModel 不直接访问 DbContext、文件系统或 HttpClient；调用模块 Service。
- 模块 Service 负责清晰的用例和校验；本地模式使用 EF Core，远程模式使用类型化 API client。**不为了统一两种模式而强制做一个万能 Repository**。
- Core 仅放跨模块必需的稳定数据类型和服务契约；UI 组件可直接使用 Avalonia 控件。
- 模块之间不访问对方的私有类/数据库表；确需协作时通过明确公开的服务接口。
- 依赖方向和模块引用边界由项目引用规则检查；只限制明确会造成耦合/安全问题的边界，不为每个目录建立一套抽象。

### 4.2 v1 公共契约：只公开二开真实需要的东西

v1 不要求开发者学习一套完整的模块框架。业务模块由应用中的一个普通 `ServiceCollectionExtensions.cs` 显式注册；这是 .NET 里最常见的 `IServiceCollection` 扩展类命名，模块自己的服务也用同样的扩展方法添加，菜单/页面按清楚的注册清单接入：

```csharp
// src/Plinth.App/ServiceCollectionExtensions.cs：普通 C#，随应用一起编译（示意）
public static class ServiceCollectionExtensions
{
    public static IServiceCollection AddPlinthModules(this IServiceCollection services)
    {
        services.AddTransient<MainViewModel>();
        services.AddInventoryModule();   // 业务模块的标准 IServiceCollection 扩展方法（§3.9）
        return services;
    }
}
```

首版只为确实需要被多个模块共享的能力提供小接口：

- `ITaskService`：仅用于需要进入全局任务中心的长任务；普通异步查询直接 `await`。
- `IPermissionService`：应用启用权限时用于界面体验；远程模式的最终授权仍由服务端执行。
- 日志直接使用标准 `ILogger<T>`；翻译直接使用 `RESX`/`ResourceManager`；导航与工作区注册由 Shell 内部实现，不额外公开 `IWorkspaceRegistry`、`ILogService`、`ILocalizationCatalog` 或 `IUiDispatcher`。

分页模型保持简单：

- `PageRequest`：页码/页大小、可选搜索词、排序字段与方向。排序字段由模块白名单校验。
- `PageResult<T>`：当前页项目和总记录数；后端无法低成本准确计算时，明确标记未知。
- 每个模块自己的筛选项使用普通强类型 Request DTO，不建立通用过滤表达式 DSL。
- 错误返回用户可理解的提示、错误码与关联 ID；不把异常堆栈直接展示给普通用户。

v1 不公开泛型 Repository 框架、`IDataSourceRegistry`、全局事件总线、全局撤销/重做服务、通用过滤 DSL 或运行时 JSON Manifest。数据库扩展采用标准 EF Core Provider + 组合根注册，不自研 `IDatabaseProvider`。只有在真实二开场景中重复出现、且直接做法确实难以维护时，再用一页 ADR 评估提取公共契约。

### 4.3 状态与生命周期

- ViewModel 使用 CommunityToolkit.Mvvm 管理界面状态；DI 容器只管理服务生命周期，不用服务定位器。
- **组合根落点（照 Avalonia 官方 DI 指南，只改模板 `App.axaml.cs` 一处）：** `ServiceCollectionExtensions` 注册服务，`App.axaml.cs` 建一次 `ServiceProvider` 并解析主窗口 ViewModel；业务代码只接收构造函数注入，不通过静态容器取依赖。Provider 跟随应用生命周期，退出时如有 `IDisposable` 单例再挂到 `desktop.Exit` 释放。
  ```csharp
  // App.axaml.cs
  public override void OnFrameworkInitializationCompleted()
  {
      if (ApplicationLifetime is IClassicDesktopStyleApplicationLifetime desktop)
      {
          var services = new ServiceCollection();
          services.AddPlinthModules();
          var provider = services.BuildServiceProvider();

          desktop.MainWindow = new MainWindow
          {
              DataContext = provider.GetRequiredService<MainViewModel>(),
          };
      }

      base.OnFrameworkInitializationCompleted();
  }
  ```
- 页面发起的可取消操作持有自己的 `CancellationTokenSource`；页面关闭时取消未完成查询并释放订阅/计时器等 `IDisposable` 资源。
- 普通服务按 singleton/scoped/transient 的常见 DI 生命周期注册；不要为每个标签默认建立一套自定义资源容器。
- 全局导入/导出等任务由任务服务接管，不随页面关闭而丢弃；页面查询应取消。
- 异步结果返回时确认页面仍有效，防止关闭页面后迟到结果更新 UI。优先使用 `CancellationToken`，只有确有竞态时才增加额外状态标志。

### 4.4 撤销/重做

- v1 不承诺跨模块、跨表单的全局撤销/重做。工具栏不默认显示全局撤销/重做按钮。
- 文本框等标准控件保留系统常见的撤销/重做行为；表单编辑过程提供明确的保存、取消和未保存提示。
- 若未来某个专业模块需要多步编辑历史，先在该模块内实现并验证，再考虑提取公共命令栈；已提交到远程服务器的写操作不能假装被本地 Ctrl+Z 回滚。

### 4.5 线程 / UI 编组 / async 约定（Avalonia 最高频 bug 类的硬规则）

UI 更新必须遵守单线程规则，但不应为普通异步代码增加多余的线程切换：

- **UI 线程亲和：** ViewModel、绑定集合和控件状态只能在 UI 线程修改。从 UI 事件或 ViewModel 发起异步操作时，正常使用 `await` 通常会回到原 UI 上下文；不要给每个 `await` 都套一层 Dispatcher。
- **何时使用 Dispatcher：** 当代码来自原生后台线程、回调、显式 `Task.Run`，或之前使用了 `ConfigureAwait(false)` 导致不再处于 UI 上下文时，才用 `Dispatcher.UIThread.Post/InvokeAsync` 切回 UI 线程。不额外定义 `IUiDispatcher` 抽象。
- **`ObservableCollection` 跨线程禁令：** 绑定到 UI 的 `ObservableCollection<T>` 只能在 UI 线程增删改；后台批量更新须整体编组回 UI 或用只读快照替换，禁止后台线程直接触发集合变更事件。
- **async 规范：** 禁 `.Result`/`.Wait()`/`.GetAwaiter().GetResult()` 阻塞；`async void` 仅用于框架要求的事件处理器且必须捕获异常。`CancellationToken` 从 UI 请求传到可取消的 DB/HTTP/后台操作。I/O 直接使用 async API；`Task.Run` 仅用于确属 CPU 密集型且需要移出 UI 线程的工作，并受简单并发上限约束。首版不自建通用线程池或复杂调度算法。
- 把容易导致死锁、跨线程 UI 更新或丢失取消的规则写进工程说明；能由现成分析器可靠检查的交给分析器，其余通过代码评审和针对性测试，不自建复杂静态分析器。

### 4.6 工程约定：官方模板优先，普通 .NET 开发者打开就能改

**不可妥协的原则：先遵循官方模板和生态主流习惯，再做最少量的 Plinth 定制。** Plinth 是通用底座，不是让开发者学习一套新架构才能开发业务的“框架框架”。目录、入口文件、命名和依赖注册优先沿用 Avalonia / .NET 的常见方式；开发者仍可按业务特点组织代码，只要结构清楚、易找且符合标准 .NET 惯例。凡是 ASP.NET Core / WPF 中已经成立的成熟写法，在 Plinth 中原样保留；Plinth 特有概念只有个位数，集中列在 §4.6.6，各看一节即可上手。

#### 4.6.1 官方模板是项目结构基准

- `Plinth.App` 必须从当前锁定 Avalonia 版本的官方 MVVM 模板创建，不允许 Agent 凭经验重新手搓一个不同的启动框架。官方参考：<https://docs.avaloniaui.net/docs/get-started/create-your-first-project>；模板仓库：<https://github.com/AvaloniaUI/Avalonia.Templates>。
- 基准命令（模板包与 Avalonia 版本都锁定，避免 `dotnet new install` 拉到未来版本）：
  ```bash
  dotnet new install Avalonia.Templates::12.1.4
  dotnet new avalonia.mvvm -o src/Plinth.App -n Plinth.App -m CommunityToolkit -av 12.1.4
  ```
- 12.1.4 模板实际生成的文件（2026-10-31 本机核验）：`Program.cs`、`App.axaml`、`App.axaml.cs`、`ViewLocator.cs`、`app.manifest`、`Assets/avalonia-logo.ico`、`Views/MainWindow.axaml`、`Views/MainWindow.axaml.cs`、`ViewModels/MainViewModel.cs`、`ViewModels/ViewModelBase.cs`、`Plinth.App.csproj`。**注意窗口叫 `MainWindow`，ViewModel 叫 `MainViewModel`；这是模板原样，不要为了对称改成 `MainWindowViewModel`。** 某个文件若当前官方模板不再生成，不要为了符合旧文档而硬加回去；以锁定版本实际生成结果为准。
- 模板默认 `-m` 已是 `CommunityToolkit`，上面仍显式写出，避免模板升级后默认值变化；当前 `Avalonia.Templates` 包没有单独的控件库项目模板，`Plinth.UI` 需要时用 `dotnet new classlib` 加 Avalonia 包引用，不另造模板。
- 使用 `.axaml` 与 `.axaml.cs` 的官方配对规则；XAML 的 `x:Class`、C# 命名空间和类名必须一致。不要自创 XAML 加载器、视图发现机制、启动器或路由 DSL。
- 官方模板升级时，先检查新模板差异并手动评估；不得静默覆盖已有文件，也不得因为升级模板就重写整个工程。

#### 4.6.2 标准目录与命名约定

`Plinth.App` 在官方模板基础上逐步增加必要目录，默认约定如下。**目录仅在有实际文件时创建，不提前铺设空目录。** 解决方案基线用 `dotnet new sln -n Plinth -f sln` 显式生成 `.sln`（.NET 10 SDK 默认是 `.slnx`；为兼容现有 VS/Rider/CI 工具链先保留 `.sln`，团队确认工具链后可切换并在 README 写明）。

```text
Plinth.sln
├─ src/
│  ├─ Plinth.App/                 # 官方 Avalonia MVVM 应用项目
│  │  ├─ Program.cs               # Avalonia 启动入口；模板原样
│  │  ├─ App.axaml
│  │  ├─ App.axaml.cs             # 建 ServiceProvider，解析主窗口 ViewModel
│  │  ├─ ViewLocator.cs           # 模板生成；XxxViewModel → XxxView（同程序集）
│  │  ├─ app.manifest
│  │  ├─ ServiceCollectionExtensions.cs  # AddPlinthModules
│  │  ├─ Views/
│  │  │  ├─ MainWindow.axaml
│  │  │  └─ MainWindow.axaml.cs
│  │  ├─ ViewModels/
│  │  │  ├─ MainViewModel.cs      # 模板生成名
│  │  │  └─ ViewModelBase.cs      # 模板生成；ViewLocator 只匹配它
│  │  ├─ Models/                  # 仅放应用层模型；不要复制数据库实体充数
│  │  ├─ Services/                # 明确的应用服务/业务用例
│  │  ├─ Data/                    # 仅当数据代码暂留应用项目时使用
│  │  ├─ Assets/                  # 图标、图片等资源（替换模板 logo）
│  │  ├─ Styles/                  # 应用样式和主题
│  │  └─ Plinth.App.csproj
│  ├─ Plinth.Core/                # 仅放确实需要跨项目共享的纯 C# 类型
│  ├─ Plinth.Data/                # 数据访问、DbContext、配置、迁移
│  └─ Plinth.UI/                  # 可复用 Avalonia 控件/主题；确认需要跨项目共享后保留
├─ tests/
│  ├─ Plinth.App.Tests/
│  ├─ Plinth.Core.Tests/          # 对应项目存在且有可测逻辑时才创建
│  └─ Plinth.Data.Tests/          # 对应项目存在且有可测逻辑时才创建
├─ samples/
│  └─ Plinth.Sample/              # 一个可以实际运行的 CRUD 样例
│     └─ Modules/Inventory/       # 示例模块：注册类 + Views/ViewModels/Services/Data/Resources
├─ docs/
│  └─ adr/                        # 只放影响长期维护的简短决策记录
├─ .github/workflows/ci.yml       # ubuntu + windows 两个 job（§14.4）
├─ .editorconfig                  # 换行/缩进/格式基线（§14.4）
├─ .gitattributes                 # 统一 LF 换行
├─ .gitignore
├─ Directory.Build.props
├─ Directory.Packages.props
├─ global.json                    # SDK 10.0.403，rollForward latestPatch
├─ nuget.config                   # 固定包源/镜像策略（§19）
├─ GETTING_STARTED.md             # 30 分钟跑通 + 新增 CRUD 文件位置
├─ THIRD_PARTY_NOTICES.md
├─ SECURITY_MODEL.md
├─ CHANGELOG.md
└─ README.md
```

这棵树是**约定示例，不是要求一次性创建所有项目和目录**。项目边界按以下规则决定：

- `Plinth.App`：桌面入口、Shell、Views、ViewModels、页面命令和组合根。保留官方模板可识别的结构。
- `Plinth.Core`：只放真正被多个项目共享的纯 C# 领域类型/公共契约，不引用 Avalonia、EF Core Provider 或操作系统 API；若没有真实共享内容就不建空项目。
- `Plinth.Data`：当数据访问独立出来确实能隔离 EF Core、迁移和 Provider 时使用普通 .NET 类库；不得建立自研 ORM 或万能 Repository。
- `Plinth.UI`：仅当通用控件、Design Tokens、主题资源确实要跨应用/项目复用时建立普通 Avalonia 控件库（控件主题放 `Themes/`，这是 Avalonia 控件库惯例）；否则先放在 `Plinth.App` 的 `Styles/` 与 `Views/Controls/` 中，避免无效拆分。
- Tests 按被测项目和测试类型采用标准 xUnit v3 项目（见 §5）；不要求每个文件夹配一个测试项目。`samples` 必须是可运行的示例，而非只有片段的展示目录。
- 版本与编译设置集中在 `Directory.Build.props`（`TargetFramework`、`Nullable`、`ImplicitUsings`、`LangVersion`）、`Directory.Packages.props`（包版本）和 `global.json`（SDK），各 csproj 不重复；使用 NuGet 的项目提交 `packages.lock.json`。

**模板落地偏差（除下表外保持官方模板原样）：**

| 模板生成 | Plinth 调整 | 原因 / 位置 |
|---|---|---|
| `App.axaml.cs` 直接 `new MainViewModel()` | 从容器解析主窗口 ViewModel | 官方 DI 指南；§4.3 |
| `Program.cs` 的 `.LogToTrace()` | 换成 Serilog/`ILogger<T>` 管线 | §11.3 |
| `Avalonia.Fonts.Inter` + `.WithInterFont()` | 删除，优先系统字体 | §2.3；Linux 缺字再按 §2.3 加 Noto |
| `Assets/avalonia-logo.ico`、窗口标题 | 替换为 Plinth 品牌 | `BrandingConfig`；§2.3 |
| `AvaloniaUI.DiagnosticsSupport`、`app.manifest` | 保留；DevTools 仅 Debug | 模板默认已是 Debug-only |

**业务模块目录（示例模块与真实业务模块同一形状；真实模块放在业务应用仓库的 `Modules/` 下）：**

```text
Modules/Inventory/
├─ InventoryModuleRegistration.cs   # AddInventoryModule：服务、菜单/页面、资源、迁移
├─ Views/                           # 模块自己的 AXAML
├─ ViewModels/
├─ Services/
├─ Data/                            # 模块的 DbContext/迁移（如需要）
└─ Resources/                       # 模块 RESX
```

模板的 `ViewLocator` 只按 `XxxViewModel → XxxView` 在**同一程序集**内反射解析；跨程序集模块页面的 View/VM 对应关系在模块注册代码里显式给出（`DataTemplate` 或导航项直接持有 View 工厂），不要依赖默认 ViewLocator 跨程序集查找。

#### 4.6.3 一个页面的主流实现路径

新增一个“客户列表”页面时，使用常见 MVVM 结构，不走自创注册体系：

1. `Views/Customers/CustomerListView.axaml`：只负责布局、控件和数据绑定；必要的 UI 事件放少量 code-behind，不把业务/数据库逻辑写在里面。
2. `ViewModels/Customers/CustomerListViewModel.cs`：继承模板生成的 `ViewModelBase`（ViewLocator 只匹配它），用 `[ObservableProperty]`、`[RelayCommand]` 管理可观察状态和用户命令；不要另建一套 BaseViewModel 层级。
3. `Services/Customers/CustomerService.cs`：只在存在明确业务流程时增加；以普通构造函数注入依赖，不为每个方法生成一个 Handler/UseCase 类。
4. 数据访问放在 `Plinth.Data` 的标准 EF Core 写法中（如 `DbContext`、配置类和迁移）；简单查询无需为了形式增加 Repository 接口。ViewModel 不直接拼 SQL 或持有 DbContext。
5. 服务通过标准 `IServiceCollection` 在应用组合根显式注册；不使用反射扫描、自动注册魔法或私有注册 DSL。

按需扩展时，可以在 `Views/Customers/`、`ViewModels/Customers/` 等标准层目录下增加一层业务子目录；不要同时叠加 `Features/Customers/Presentation/View/Application/UseCases/Handlers/Adapters/Infrastructure/Providers/Abstractions` 等多层结构。一个类型放在哪里，开发者应当能按文件名和标准目录直接找到。

#### 4.6.4 普通 C# 约定与禁止事项

- 类型/方法 PascalCase、参数/局部变量 camelCase、私有字段 `_camelCase`；异步方法 `Async` 结尾，正确传递 `CancellationToken`；文件名与主要公共类型名一致。
- 依赖注入使用 `Microsoft.Extensions.DependencyInjection` 和构造函数注入；MVVM 使用 `CommunityToolkit.Mvvm` 的常见能力；异步使用 `async/await`；测试使用 xUnit；数据访问使用 EF Core。相关官方约定优先于本文件自创规则。
- 首版默认不采用 CQRS/Mediator、事件溯源、微服务式分层、自研 DI/服务定位器、自研导航 DSL、通用过滤 DSL 或全局事件总线。具体业务确有需要时，可采用成熟方案或局部实现；只有影响公共契约、核心依赖或维护方式的重大变化才需要简短决策记录。
- **禁止跨生态照搬目录**：不得把 Go/Gin 项目的 `cmd/`、`internal/`、`handler/`、`router/` 等约定机械套用到 Avalonia/C#；也不得照抄 Java/Node 项目的结构。跨语言只借鉴原则，不照搬项目骨架。
- 不要仅以“更清洁”“更专业”“更可扩展”为理由大规模更名、搬文件或拆项目。普通小范围整理走正常代码评审；涉及公开契约、数据库迁移、安全边界、发布方式或大规模结构变化时，补一条简短决策记录并说明收益与迁移影响。

#### 4.6.5 新人可识别性验收

- 让熟悉基本 C# / MVVM 的开发者快速找到入口、页面、服务和数据访问位置，是结构设计的目标。30 分钟运行样例、半天完成 CRUD 可作为上手测试目标，不是对每个开发者的硬性工时承诺。
- 若新人经常找不到代码或需要学习大量私有概念，优先简化项目/目录/注册步骤，或补可运行示例；不能用“先学习我们的架构”回避问题。
- `README.md` / `GETTING_STARTED.md` 必须写清官方模板来源、运行命令、项目入口和新增 CRUD 的文件位置；代码结构或关键命令变化时同步更新，不要求新人翻聊天记录找答案。

#### 4.6.6 成熟 .NET 开发者对照表（学习成本控制）

Plinth 不发明新架构、不要求先学一套概念再写业务。除最后 5 行外，下表左侧的写法在 Plinth 中原样成立；熟练的 C# 开发者（含有 ASP.NET Core / WPF 前后端经验的人）应按既有习惯直接开发，只有 Plinth 特有的少数概念需要查一处文档。

| 已经熟悉的写法 | 在 Plinth 中 | 位置 / 说明 |
|---|---|---|
| `ServiceCollectionExtensions` + `IServiceCollection` 注册服务 | 完全相同（官方 DI 指南） | `App.axaml.cs` 建 provider；`ServiceCollectionExtensions.cs` 注册 |
| Service 层 + 构造函数注入 | 完全相同 | `Services/` 下普通类 |
| View + ViewModel + `x:DataType` 编译绑定 | 完全相同 | `Views/*.axaml` + `ViewModels/*.cs` |
| `[ObservableProperty]` / `[RelayCommand]` | 完全相同 | CommunityToolkit.Mvvm |
| EF Core `DbContext`、迁移、LINQ | 完全相同 | `Plinth.Data` |
| `appsettings.json` + Options 模式 | 完全相同 | 标准配置绑定（§3.4） |
| `ILogger<T>` | 完全相同 | Serilog 仅是实现（§5） |
| `.resx` + `ResourceManager` | 完全相同 | 按模块组织（§9.2） |
| xUnit 单元 / 集成测试 | 完全相同 | `tests/` 标准结构 |
| `Directory.Packages.props` 集中管版本 | 完全相同 | 加锁文件（§14.5） |
| **以下为 Plinth 特有概念，各读一节即可** | | |
| 模块注册入口 | `ServiceCollectionExtensions.AddPlinthModules` | §4.2 |
| 组合根 DI 落点 | `App.axaml.cs` 建 provider | §4.3 |
| 长任务统一入口 | `ITaskService` | §4.2 / §3.5 |
| 界面权限判断 | `IPermissionService` | §4.2 / §3.3 |
| 布局持久化格式 | `WorkspaceLayoutV1` | §7.1 |

### 4.7 需要从首版遵守的通用规则

这些规则用于避免常见迁移问题，不要求为每一条新建接口或项目：

- **模型：** API DTO、界面模型与 EF Core 实体按职责使用，不把数据库实体直接作为外部 API 契约。
- **时间与金额：** 时间点统一使用 UTC 并保留业务所需的时区语义；纯日期与时间点分开；金额用 `decimal` 或明确的最小货币单位，并保留货币码。
- **配置与秘密：** 使用标准 .NET Configuration/Options；非秘密默认配置可文件化，秘密放凭据/密钥设施，不写日志或提交到 Git。
- **文件与缓存：** 业务数据保存逻辑文件标识与必要元数据，不保存机器绝对路径；缓存允许清空重建，不是权威数据源。
- **任务与外部副作用：** 页面查询可取消；导入/导出等长任务有状态与错误反馈；邮件、上传、第三方调用等副作用写在清晰的服务流程中，不藏在 ORM 钩子里。
- **数据库与平台：** 先用标准 EF Core/LINQ 和 .NET API；确需数据库专属 SQL 或平台专属功能时，放在对应实现附近并说明适用范围。更完整的未来扩展条件见 §16.5。

不要为了这些规则一次性建立通用抽象层；直接代码清楚且暂时只有一种实现时，就先直接实现。

## 5. 开源依赖基线与许可证审查

下面是 **2026-10-29 的候选快照**，不是永远固定的版本表。锁定前核对目标框架、稳定版本、完整许可证正文、传递依赖、维护状况及平台行为；锁定后集中管理版本，不在各模块中手工分散指定版本。

| 用途 | 采用方向 | 当前候选 / 许可证 | 规则 |
|---|---|---|---|
| .NET / SDK | .NET 10 LTS | Runtime `10.0.14`；SDK 候选 `10.0.403`，MIT | 这是截至 2026-10-29 可核验的稳定补丁快照；.NET 11 仍为 RC，不作为生产基线。`global.json` 固定 SDK；及时接收安全补丁。 |
| UI | Avalonia | 12.1.4，MIT | Windows 首发；Linux 构建/冒烟持续进行，正式支持另行验收。 |
| MVVM | CommunityToolkit.Mvvm | 8.4.2，MIT | 不再叠加另一套状态管理框架。 |
| 数据列表 | Avalonia `TableView`（Avalonia 核心） | 随已锁定的 Avalonia 核心版本；MIT | 官方定位为只读表格，v1 列表默认使用。Phase 0 验证列设置、选择、键盘、中文输入法、分页承载方式和大数据列表体验。 |
| 旧 DataGrid | `Avalonia.Controls.DataGrid` | 12.1.2，MIT；仅作对比候选，不作为默认依赖 | 官方标记 Deprecated，仅接收 Bug 修复。除非 POC 证明 TableView 无法满足首版核心列表需求且有明确回退理由，否则不新引入。 |
| 旧 TreeDataGrid | Avalonia 开源旧版 | MIT；仓库已归档 | 官方仓库于 2025-10-13 归档，不再接收维护；可供研究或有能力者 fork，不作为默认生产依赖。当前商业版组件不符合本项目默认不使用付费依赖的原则。 |
| ORM / 本地数据库 | EF Core SQLite Provider + Microsoft.Data.Sqlite | EF Core 与 Microsoft.Data.Sqlite 均使用 `10.0.14`（MIT） | EF Core 及其 Provider 保持同一兼容版本线；ADO.NET SQLite 包只在直接 API 需要时显式引用。运行时、EF Core 和各 Provider 按各自发布与兼容矩阵选择版本，不要求补丁号机械一致；EF Core 及其 Provider 必须满足官方兼容要求。 |
| PostgreSQL（未来服务端） | `Npgsql.EntityFrameworkCore.PostgreSQL` | `10.0.3`，PostgreSQL License（候选；发布前复核许可证正文） | 与 EF Core 10 版本线兼容；不是 v1 桌面客户端默认依赖。真正加入服务端时做部署、迁移、性能及许可回归。 |
| MySQL（未来服务端） | Provider 暂不锁定 | Oracle `MySql.EntityFrameworkCore 10.0.12` 已声明支持 EF Core 10，但 NuGet 标注 `GPL-2.0-only WITH Universal-FOSS-exception-1.0` 许可组合，需按实际分发方式审查 | 按当前默认偏好宽松许可证的准入规则，不直接批准为默认依赖；应依据实际发行方式核对许可证义务。服务端 MySQL 实施前，复核届时最新稳定 Provider、完整许可正文、传递依赖及 EF Core 10+ 兼容性；不允许静默使用预览版。 |
| HTTP 韧性 | Microsoft.Extensions.Http.Resilience | `10.11.0`，MIT | 截至快照日的稳定候选；仅远程 API 模式引入；不额外维护第二套 Polly 管线。 |
| 通用 OIDC | Duende.IdentityModel.OidcClient | `7.1.1`，Apache-2.0 | 截至快照日的稳定候选；系统浏览器 + PKCE；MSAL 不作为默认依赖。 |
| 凭据 | Windows Credential Manager；Linux Secret Service | 平台原生方案 | Windows 先做；Linux 正式发布前验收，不允许明文降级。 |
| CSV | CsvHelper | `34.0.0`，MS-PL 或 Apache-2.0 双许可 | 已有稳定版发布（2026-10-28）；采用并归档 Apache-2.0 许可文本。锁定前跑中文编码、行级错误、长数字、取消、公式注入回归。 |
| XLSX | ClosedXML | `0.105.1`，MIT | Should；当前候选稳定版；中文、长数字、日期/损坏文件、内存 POC 通过才启用。 |
| 日志 | Microsoft.Extensions.Logging + Serilog | `Serilog 4.4.0`、`Serilog.Extensions.Logging 10.0.0`、`Serilog.Sinks.File 7.0.0`、`Serilog.Formatting.Compact`（候选，Apache-2.0） | `ILogger<T>` 是业务代码唯一日志 API；适配包负责桥接，File Sink 负责文件输出；JSON 统一用 `Serilog.Formatting.Compact` 的 `CompactJsonFormatter` 写 NDJSON/CLEF，不自写 formatter、不混用第二种 JSON 格式。只保留这一套日志管线。 |
| 测试 | xUnit v3 + Avalonia.Headless.XUnit | `xunit.v3 3.2.2`、`Avalonia.Headless.XUnit 12.1.4`、`Microsoft.NET.Test.Sdk 17.14.1`（Apache-2.0 / MIT） | **测试栈统一用 xunit v3。** `Avalonia.Headless.XUnit 12.1.4` 传递引入 xunit.v3，而 `dotnet new xunit` 模板默认给 xunit 2.9.3；两者同时存在时 `FactAttribute` 同名冲突，直接 CS0433 编译失败（2026-10-31 在同一 SDK 实测：v2 混装失败，改用 xunit.v3 3.2.2 后无头用例通过）。运行方式固定为 `dotnet test -p:TestingPlatformDotnetTestSupport=true`。 |
| 安装器 | NSIS | 3.13 候选；主程序许可为 zlib/libpng，压缩模块需单独核验 | 只使用已批准的压缩模块；不引入复杂自更新逻辑。 |
| 字体 | Noto Sans CJK（按需） | SIL OFL | 明确字体文件、子集和许可证；安装包体积先实测。 |
| PDF/图表/条码/分析 | 不属于核心默认依赖 | 后续按需求评估 | 只为实际使用场景选择一个组件；不能仅因“未来可能会用”而引入。 |

### 5.1 组件准入规则

1. 默认使用 MIT、BSD、Apache-2.0、SIL OFL 等已确认可满足项目分发方式的宽松许可证。
2. GPL/AGPL/LGPL、SSPL、非商业许可、收入/人数门槛社区许可和需购买授权的组件默认阻断；例外必须单独核对法务/分发影响并书面批准。
3. Avalonia Pro/Accelerate、Syncfusion 商业控件、EPPlus 等不作生产依赖。
4. 每次正式发布扫描直接与传递依赖，包含 native 二进制、字体、图标和安装器实际使用的压缩模块；生成 `THIRD_PARTY_NOTICES.md` 与 SBOM。
5. 以包内/上游 LICENSE 正文为准，包页标签只能作线索；无法确认再分发权利的依赖不得进入 GA。
6. 如果没有合格开源组件，先降低能力范围或延期，不得用收费组件临时顶替。

## 6. 数据列表：使用官方 TableView，编辑走标准表单

### 6.1 选择原则

Avalonia 官方将 `TableView` 定位为只读表格，并已将 `Avalonia.Controls.DataGrid` 标记为 Deprecated、仅接收 Bug 修复。Plinth v1 默认使用 Avalonia 核心中的 `TableView` 展示列表；新增、查看和编辑记录走普通表单。这符合常见企业桌面软件使用习惯，也避免把首版绑定在停止新增功能的控件上。

旧版开源 `TreeDataGrid` 虽然保留 MIT 许可，但其官方仓库已归档，不再接收修复和依赖更新；商业版 TreeDataGrid/Accelerate 不属于本项目默认选型。以上均不得被描述为“正在积极维护的免费高级网格”。

**首版不自研完整网格引擎，也不建设通用网格适配框架。** 如果未来出现必须单元格编辑、冻结列或复杂树表的业务需求，再单独比较当时仍在维护、许可证合格的成熟实现。POC 不通过时，优先保留标准表单编辑、缩小列表能力；不得为了追求类似 Excel 的交互直接开工造网格。

### 6.2 v1 列表能力

**Must：**

- 列表显示、行选择、分页、基本排序、基本筛选/搜索、刷新，以及加载/空/错误状态。
- 列宽调整、列显示/隐藏和用户列设置保存；列可见性用 `TableViewColumn.IsVisible`，列菜单/持久化由应用层实现（见本节边界），不为此建立复杂控件框架。
- 明确的新增、查看、编辑、删除入口；新增和编辑使用标准表单，明确保存/取消和字段级错误提示。
- 复制当前选择或选中文本；实现方式保持简单。粘贴仅在有业务需求时提供，并先校验、显示失败原因，不能静默丢掉不合法数据。
- 键盘导航和常用快捷键；鼠标和键盘均可完成基本 CRUD。

**Should：** 在有明确业务需求且找到维护状态、许可证合格的成熟控件后，提供有限的单元格内联编辑（文本、数字、日期、布尔/枚举）。必须明确提交/取消与校验错误，并有退出编辑时的脏状态规则。

**Could：** 分组/小计、冻结列、多列高级筛选、复杂树表、范围多选编辑、填充柄、公式引擎、协同编辑。

树形主数据 v1 使用标准 `TreeView`。若业务要求表格内树形展示，先用缩进列和展开/折叠操作；不因此引入商业 TreeDataGrid，也不自研完整树形网格。

**TableView 控件边界（2026-10-31 对 Avalonia 12.1.4 核验，写进 POC 记录）：** `TableView` 位于核心包、只读、行虚拟化、列宽可拖拽（`CanUserResizeColumns` 默认 `true`）；`TableViewColumn.IsVisible` 属性存在（默认 `true`），但控件**没有内置列排序，也没有内置的列菜单/列显示隐藏 UI，列本身不虚拟化**。因此“基本排序”属于应用层/数据层自建；列显示/隐藏可由应用层切换 `IsVisible` 并持久化用户列设置，但列菜单和持久化仍要自己写。列数较多的页面需实测渲染与滚动表现。Phase 0 不得把这些能力当默认存在而漏测，也不得为补它们去改控件或自研网格。

### 6.3 分页与数据规则

- UI 只保留当前页及必要的视图状态；不得把百万级查询结果全部放入内存。
- 排序必须形成唯一稳定顺序（添加唯一键作为 tie-breaker），避免翻页重复或漏行。
- 普通页面可用 offset 分页；大批量导出和“全选所有查询结果”采用服务端流式处理/游标分页，不在客户端构造数十万条 ID。
- 查询可取消；旧查询的迟到结果不能覆盖用户刚修改的新条件。
- 远程权限、数据过滤与导出字段权限由服务端最终执行；前端隐藏列不是安全措施。
- 不把汇总行、分组行塞进普通分页结果契约。高级分组报表由具体业务模块实现，避免影响分页总数、复制、导出和键盘导航。

### 6.4 POC 验收

以 Windows 11 x64 为主测试环境，用官方模板验证 `TableView` + 标准编辑表单的真实流程：10,000 条本地数据浏览、100,000 条远端总记录分页、1,000,000 条远端总量但只加载当前页；测试中文 IME、选择/复制、自建列排序与列菜单/列设置持久化（若启用）、取消查询、表单编辑、错误反馈和快速切换标签。若某项能力不是首版必须，不为通过该项而自研网格。

通过标准：无错行、丢数据或未处理异常；筛选、翻页和打开编辑表单有明确反馈；取消有效；连续操作没有持续增长的未释放资源；保留测试设备和结果。需要单元格编辑时另做独立的技术/许可证/维护状态评估，不以只读列表 POC 代替验证。
## 7. 桌面工作区与窗口系统

### 7.1 首版边界

- 单主窗口、多标签、导航/属性/任务面板、可调整分栏、布局保存和恢复为首版要求。
- **首版只做固定标签 + 可调分栏（薄自研，人人会）：** 不引入自由拖拽停靠，避免二开者额外学一套 docking 概念。**Dock(MIT) 自由停靠降为 Could（§0.4），按 §18「后续阶段」在真实需求成立后再评估**；外部浮动窗口、多主窗口、跨窗口拖拽同样不是首版承诺。
- `WorkspaceLayoutV1` 由 Plinth 定义并版本化，不直接把第三方 docking 内部 JSON 当持久数据标准。
- 标签 + 分栏由 Plinth 薄自研（布局走 `WorkspaceLayoutV1` 版本化持久化），属允许的薄自研，不违"不自研完整 docking 引擎"；**Dock 的布局/焦点/IME/拖放风险不在首版关键路径上**。
- v1 直接使用 Avalonia 的窗口 API；只实现主窗口尺寸/最大化状态、关闭确认与 DPI 处理，不对外提供没有第二个实现的窗口服务接口。

### 7.2 工作区关闭流程

1. 检查脏状态和未完成任务。
2. 询问保存/丢弃/取消（仅在需要时）。
3. 停止定时器、订阅、长轮询和未完成请求（CancellationToken）。
4. 取消允许取消的工作；不可取消工作转由 `ITaskService` 持有，不再依附已销毁页面。
5. 释放控制器、临时文件、局部缓存；全局缓存由 LRU/容量策略控制。
6. 持久化布局/用户偏好；写入结构化关闭事件。
7. 清除指向已销毁工作区的异步回调，拒绝迟到结果更新 UI。

---

## 8. 数据库与数据归属

### 8.1 数据归属（首版选择简单明确）

每个应用/业务模块在配置中选择一个权威数据源：本地 SQLite 或远程 API。首版不承诺同一模块无缝切换数据源，也不自动合并本地与远程数据。**本地 SQLite、远程 API、服务端 PostgreSQL/MySQL 是三种不同边界**：客户端远程模式不直连 PostgreSQL/MySQL；未来服务端可以选择 PostgreSQL 或 MySQL，但必须通过本章可移植性规则与对应数据库集成测试后，才可宣称正式支持。

- 纯本地模式：SQLite 是权威数据源，适合本地独立工具和单机业务。**首版完整支持。**
- 远程服务端模式：服务器是权威数据源，本地只保存偏好、缓存和必要查询状态；用于远程企业业务。**首版支持。**
- 离线双向同步模式：离线可写并双向同步；需要 outbox、幂等键、实体版本、删除墓碑、冲突界面、重试策略和同步审计。**该能力真实有价值，但首版核心不提供 outbox/冲突中心/同步引擎，也不预建空服务。首版只遵守稳定 ID、UTC 时间、并发版本和 API 幂等等通用规则；未来由具体业务需求驱动，再单独设计同步模型和冲突策略。**

### 8.2 SQLite + EF Core

- 数据库文件放系统用户数据目录，不放安装目录或网络共享盘。Windows 使用当前用户 ACL；Linux 正式支持时使用用户专属目录权限。
- **v1 统一使用 EF Core + Microsoft.Data.Sqlite**：实体映射、LINQ 查询、迁移、事务用一套模式。不要把 EF Core 与 Dapper 同时当默认方案。
- 使用短生命周期 `DbContext`：通过 `IDbContextFactory<T>` 创建每个操作/工作单元所需的 context，用完释放；禁止把 `DbContext` 注册成单例或跨线程共享。
- 数据库查询/写入应使用 async API 并通过后台服务执行；不要在 UI 线程同步等待数据库。
- 写事务边界明确；批量导入分批提交并可取消。SQLite 写锁争用时配置合理的 busy timeout；有限重试只用于明确可重试的锁冲突，并给用户可见的排队/失败提示。
- 按实测启用 WAL；不把 WAL 数据库放到不适合 SQLite 的网络文件系统。
- EF Core Migrations 作为唯一默认迁移方式。模块新增表须有独立迁移并验证组合应用的升级顺序；失败时保留原库/备份并阻止启动，不做静默清空或自动反向 DDL。
- 本地缓存可重建，不作为唯一事实来源；框架表和业务表命名清晰区分。
- **从首版开始遵循 §8.6 数据库可移植性规则**。业务模型不使用 SQLite 专有字段/类型/函数作为业务契约，业务查询优先写标准 LINQ。该规则不意味着 v1 已支持其他数据库。

### 8.3 本地数据库表建议

以下是按功能需要创建的候选表，不是首版必须一次性建齐的空表清单：

- `app_settings`：需要持久化的非秘密设置及配置版本。
- `workspace_layouts`：启用布局保存时记录用户布局。
- `task_records`：任务中心需要保留历史时记录任务状态/关联 ID，不存秘密载荷。
- `notifications`：需要保留应用内通知历史时再创建。
- `saved_queries`：只有实现“保存查询”功能时再创建。
- `module_state`：确需记录模块版本/状态时再创建；数据库 schema 版本仍以 EF Core Migrations 为准。

不要将日志全文、秘密或可重建缓存无边界地写进 SQLite；详细日志优先写有容量上限的滚动文件。

### 8.4 远程数据库与 API

- 远程业务默认通过 HTTPS REST/JSON API；客户端不携带生产数据库账号、不能直接连共享生产数据库。
- **服务端目标数据库：PostgreSQL 与 MySQL 均预留为正式目标**，具体项目可以按部署和现存生态选择。PostgreSQL 的 EF Core 10 Provider 候选为 `Npgsql.EntityFrameworkCore.PostgreSQL 10.0.3`；MySQL Provider 必须在实施时重新核对兼容性与完整许可证——按 §5.1 的宽松许可默认规则，优先复核 MIT 候选（如 Pomelo，入口见 §23），再与 Oracle 官方 Provider（GPL-2.0 + Universal FOSS 例外）比较分发义务；当前不把不满足准入规则的 Provider 带入核心依赖。
- 客户端只依赖版本化 HTTPS REST/JSON 契约，不引用 `Npgsql`、`MySqlConnector` 或服务端连接字符串。服务端数据库升级不能要求修改桌面 UI。
- 将来服务端优先使用 EF Core 标准 `DbContext`、LINQ 和事务。Provider 在启动组合根选择；不自研跨库 ORM/通用 Repository。Provider 特有查询集中在对应数据实现中，并提供可移植的普通查询/业务逻辑回退。
- API 路径带版本；统一分页、排序、基本筛选、错误格式、Correlation ID、幂等键和乐观并发版本。复杂分组/报表由业务 API 明确提供，不强塞进通用列表契约。
- 错误结构建议采用 RFC 9457 `application/problem+json` 思路。
- 服务端必须执行身份、租户、角色、记录级权限和业务校验。

### 8.5 数据类型规则

- 跨模块/远程传输的实体 ID 使用 `Guid`，对外 JSON 以字符串序列化；具体数据库存储形式由 EF Core/provider 管理，不把 ID 生成算法暴露给 UI。
- 时间点以 UTC 存储和传输，使用 ISO 8601/RFC 3339；纯日期、当地时间和时区分别建模。服务端若需保留未来某地区“当地时间”的计划任务，需同时保存当地时区 ID，不只存固定 UTC 偏移；涉及定时/过期测试时优先使用 .NET `TimeProvider`。
- 金额使用 `decimal` 或最小货币单位整数，并伴随 ISO 4217 货币码；禁止用 `double` 累计金额。
- 远程实体使用版本号/ETag 做并发校验；冲突必须可见，不能静默覆盖。
- 查询字段与排序字段必须是模块明确允许的字段，映射为 EF Core 属性表达式；禁止拼接未经校验的 SQL。

### 8.6 PostgreSQL / MySQL 可移植性规则（从首版生效）

本节是**代码约束，不是首版多数据库功能清单**。现在就按这些规则建模，目的是将来增加服务端 PostgreSQL 或 MySQL 时，主要工作是添加 Provider、独立迁移集和集成测试，而不是重写业务层。不要为了做到这一点先创建一整套抽象框架。

**必须遵守：**

1. **数据库类型不越界**：Core、业务规则、ViewModel 和 API DTO 不得公开 `SqliteConnection`、`NpgsqlConnection`、`MySqlConnection`、Provider 专属异常或 Provider 专属类型。当前本地存取可以直接用标准 EF Core；未来服务端同样使用 EF Core 标准 API。
2. **使用可移植数据类型**：主键/跨模块 ID 使用 `Guid`；时间点按 UTC 存储；金额使用明确精度/小数位的 `decimal`，并存货币码；字符串设置明确长度与唯一约束；枚举使用明确定义的稳定编码。不要依赖 SQLite 的动态类型行为来容纳不规范数据。
3. **不把自增实现当业务 ID**：不得把 `rowid`、`last_insert_rowid()` 或特定数据库的 identity 序列暴露给业务、API 或导入导出契约。数据库内部生成列可以使用，但生成策略必须留在持久化配置内；默认跨模块 ID 仍使用 `Guid`。
4. **SQL 优先可移植**：查询优先用 EF Core LINQ；禁止在共用业务服务中拼接 SQL 或依赖 SQLite 专用函数、`INSERT OR REPLACE`、`rowid`、专有 upsert、隐式 bool/日期转换。确需特定 SQL、全文检索、JSON 操作或批量写入优化时，隔离到对应 Provider 实现并保留普通实现/明确不支持提示。
5. **不假设各库排序与文本比较相同**：大小写、重音、空字符串、NULL 排序、字符长度与唯一索引的行为需显式定义。用户可见的排序必须有稳定顺序，最后追加唯一键作为 tiebreaker；分页/导出不得依赖无序结果。
6. **金额、日期、并发写清楚**：金额字段明确 precision/scale；时间点和纯日期分别建模；并发更新使用普通数值版本/并发令牌和显式冲突处理，不把 SQL Server `rowversion` 等特定语义当通用方案。
7. **迁移按 Provider 分开**：SQLite 本地迁移只服务 SQLite。未来添加 PostgreSQL/MySQL 时，为每种 Provider 建立各自的 EF Core Migration 集（目录或独立程序集按项目规模选择），并验证全新建库、逐版本升级、失败恢复；不得把 SQLite Migration 复制后假定可在服务器运行。
8. **配置只在组合根切换**：未来服务器通过配置选择数据库类型和连接池参数；连接字符串/密码来自部署密钥或环境配置，不能进入桌面包、Git、常规日志或诊断包。业务模块不自行读取环境变量或拼接连接字符串。
9. **按真实数据库验收**：正式宣称支持 PostgreSQL/MySQL 前，CI 必须对相应数据库运行迁移、CRUD、事务、并发冲突、分页/排序、唯一约束、大小写/时区/decimal、备份与恢复等集成测试。SQLite 测试通过不能代替服务端数据库测试。

**首版范围说明：** v1 只交付本地 SQLite 与远程 API 两种模式。PostgreSQL/MySQL 当前状态为“架构按可移植性预留，服务端 Provider 未纳入桌面核心，正式支持需单独验收”。任何 Agent、README、营销文案都不得把“已预留升级空间”描述成“已经通过多数据库兼容测试”。

### 8.7 备份、恢复和升级/降级

- 备份必须列出实际包含项。默认包含本地数据库、必要设置和布局；排除 OS 凭据、token、可重建缓存及原始日志。用户在执行前可看到保存位置、文件清单和大致大小。
- 备份包记录应用版本、数据库 schema 版本、UTC 时间和 SHA-256 校验值。恢复前先校验文件和数据库完整性，并自动为当前数据建立可恢复的安全副本。
- 数据库升级前先备份。迁移失败时保留原库和错误信息，禁止静默清空或覆盖；只有迁移成功并通过完整性检查后才继续启动。
- 默认只支持向前迁移。旧版本不能直接打开新 schema；降级时从旧版本兼容的备份恢复，不自动执行反向 DDL。无法明确迁移路径的备份必须拒绝恢复并给出可读原因。
- 用户可查看备份时间、文件、大小与校验状态。SQLite 默认不等于加密；敏感凭据进入 OS 凭据设施。只有具体业务明确要求数据库加密时才单独评估兼容、密钥保护和许可证。

## 9. 国际化与全球化

### 9.1 首版语言

GA 正式支持 `zh-CN`、`en-US`，两种语言必须完成核心 UI、错误、菜单、帮助和关键导入导出提示的翻译；**`ja-JP` 移入后续版本路线图**，不以缺失翻译的预览版宣称正式支持。

### 9.2 技术实现

- 使用 .NET `RESX`/`ResourceManager` 与标准 `CultureInfo`；按模块组织资源。复杂语言数据格式使用 .NET/系统能力，不额外建立一套国际化运行时。
- key 规则：`moduleId.section.action`，例如 `inventory.list.refresh`；禁止不同模块占用同一全局 key。
- 核心资源编译期生成；不假设运行时能自动热加载任意外部模块翻译。
- v1 的翻译资源随模块编译交付；不支持动态下载/热更新语言包。将来真有外部语言包需求时，再设计版本与完整性校验。
- **回退链：缺失时先回退到 `en-US` 基准，再占位并产生诊断记录；不直接回退到"模块默认语言"**（避免英文用户看到某中文模块默认文案）。冲突规则：核心命名空间不可被第三方覆盖；模块只能在自身 namespace 注册。
- 日期、数字、货币、时区分别处理；时间点用 UTC 存储，显示按用户选定时区转换；用户语言不能推断其国家/货币。
- **RTL：首版仅 LTR，不做 RTL，不实现不验收。** Avalonia 提供 `FlowDirection` 能力，仅作架构预留；将来接阿拉伯语/希伯来语客户再单开 ADR，不在首版付镜像布局成本。

### 9.3 CI 多语言门禁

- 核心 RESX 与各模块资源必须有合法格式、唯一资源 key 和一致的格式化占位符。需要单复数区分的文案使用明确的资源 key；v1 不另外建立通用 ICU/MessageFormat 引擎。
- 对生产 UI 的字符串做 lint：扫描菜单、按钮、表单标签、对话框、空/错误/加载状态；允许协议名称、日志、测试数据和用户输入等带审计理由的例外。
- 不采用"出现任何字符串一律阻断"的粗暴扫描，避免误报。
- CI 输出具体文件、行号、资源 key 和缺失语言；GA 缺失项阻断构建。
- Golden 与集成测试覆盖中英文长文本、125%/150% 缩放、字体回退、日期/金额格式与 IME。

---

## 10. 网络、身份、权限与安全

### 10.1 身份认证

- **本地单机应用可以不启用登录**；远程企业模式才启用 OIDC Authorization Code + PKCE 和系统浏览器。
- OIDC 按标准校验 `state`、`nonce`、issuer、redirect URI、token 签发方/有效期；认证客户端只选一种通用 OIDC 库。Microsoft Entra ID 专有功能确实需要时，才额外评估 MSAL。
- 优先使用经过验证的 loopback 或平台支持的回调方案；选定安装方式后做真实打包登录测试。v1 Windows 使用传统安装包，避免默认引入 MSIX 容器权限分支。
- refresh token 等秘密放 OS 凭据设施，不存普通设置 JSON、SQLite 或日志。
- 令牌失效、权限撤销和会话锁定时，取消受保护的后台请求并清理敏感 UI 状态。
- 本地 UI 权限判断只是操作体验；远程权限必须由服务端校验。

### 10.2 安全基线

- TLS 证书校验不得关闭，不允许生产环境默认信任任意自签名证书。
- 统一超时、取消与有限重试；非幂等写操作不自动重试，除非 API 有幂等键和服务端保证。写操作的幂等键只在有重试/重复提交风险的用例中落地，不为每个 GET 或所有接口增加无意义协议字段。
- 默认拒绝、最小权限；服务端检查用户、租户、角色、记录级权限和业务规则。
- 检查文件路径穿越、压缩炸弹、CSV 公式注入、异常 XLSX/JSON、超大文件和超长文本。
- 企业代理或证书异常给可读说明，不因用户处于公司网络而关闭 TLS 校验。
- v1 不运行远程下载的程序集、脚本或插件代码。未来若有外部插件，另做进程隔离与 IPC 安全设计。
- 服务端 API 从一开始执行对象级/租户级授权与字段级导出裁剪；不要只在菜单显隐层或桌面端校验。单机应用的本地权限配置不等同于防御拥有本机管理员权限的攻击者。
- 依赖版本锁定、漏洞检查、SBOM 与许可证清单进入发布流程。
- **审计保持简单且不可混淆**：技术日志与安全/业务审计使用不同事件类别和访问权限；远程业务以服务端审计为权威。v1 本地日志文件限制写权限与保留期；不默认建第二个审计数据库或 HMAC 链。只有法规/客户威胁模型明确要求防篡改审计时才单独设计，并明确本地管理员可绕过哪些保护。

### 10.3 本地凭据与密钥

- 统一 `ICredentialStore` 接口，封装 Windows/Linux 原生后端。
- Linux 无 Secret Service/keyring 时，明确提示无法安全保存并让用户选择重试/不保存；不能退回明文。
- 不自己发明密码加密算法，不将加密密钥与密文放在同一普通配置文件。
- 日志脱敏在生成日志时做第一层，在日志查询/诊断包导出时做第二层；禁止将 raw token 暂存到 UI state 供"预览"。

### 10.4 威胁模型

Phase 0 保留一份简短的安全检查记录即可：列出凭据、本地数据库、诊断包、导入文件、更新包和远程 API 等资产，列出最主要的滥用场景、默认缓解措施和残余风险。不要把完整威胁建模流程做成额外工具或复杂审批制度；如果面向高合规客户，再针对具体要求扩展。`SECURITY_MODEL.md` 随基本登录、权限、诊断导出实现一起维护。

## 11. 日志、错误中心和后台诊断

### 11.1 统一错误模型

面向用户的错误信息只需统一包含：错误码、用户能理解的说明、下一步建议、是否可重试和关联 ID。时间、模块、任务和受控堆栈留在结构化技术日志中；不要求普通模块重复构造一份庞大的错误对象。

### 11.2 日志等级和事件类型

- 技术日志通过标准 `ILogger<T>` 输出，级别使用 Debug/Information/Warning/Error/Critical；开发构建可更细，生产有等级开关。
- 安全审计：登录成功/失败、权限拒绝、敏感设置变更、导出/恢复等重要操作；运行时插件不是 v1 功能。
- 业务历史：由业务模块明确哪些操作需留痕，不能把普通 debug 日志当审计日志。
- 任务历史：任务起止、进度、部分失败、取消原因和关联日志 ID。

### 11.3 存储和 UI

- 使用 Serilog 文件 Sink + JSON formatter 写入滚动 NDJSON 文件；按大小/日期轮转，有总量上限和保留策略。业务代码只依赖 `ILogger<T>`，启动时集中配置一次日志适配。
- 索引按需存于 SQLite，查询采用流式/分页，不把全部日志文件一次加载到内存。
- 日志中心可按时间、级别、模块、任务、错误码、关联 ID 过滤；提供堆栈详情、复制 ID、打开日志目录、诊断包预览。
- **异常捕获（本地落盘，不自动上报）：** 在锁定的 .NET 10 + Avalonia 12.1.4 上，三个钩子的名称与签名已实测存在且可编译（2026-10-31，0 警告 0 错误）：`Dispatcher.UIThread.UnhandledException`（UI 线程未处理异常，`e.Handled` 可写回）、`TaskScheduler.UnobservedTaskException`（未观察到的 Task 异常，`e.SetObserved()`）、`AppDomain.CurrentDomain.UnhandledException`（AppDomain 级），各自落盘。**本轮只验证了 API 名称与标志位可写，各异常来源的实际覆盖仍须用真实故障样例逐个验证**；换 Avalonia/.NET 版本后必须重新对照实际引用核验，不得复制未经编译验证的 API 名称。**原生（engine/运行时）崩溃尽力而为**（如依赖 .NET 运行时崩溃报告/本地 dump），其覆盖范围限制**如实写入文档**，不承诺"单 handler 全覆盖原生层"。**不引入 Sentry/自动上传**；崩溃文件随诊断包在用户确认后导出。
- 无法写日志时走受限的 stderr/系统事件/内存缓冲降级，不能因日志磁盘满而二次崩溃。

### 11.4 诊断包的双阶段脱敏

> **前提（比字段白名单更靠前）：日志只允许写结构化字段，禁止把业务文本拼进消息。** 模式匹配抓不到"客户张三手机 138XXXX 又下了一单"这类自由文本——所以源头禁止 `$"订单 {order.CustomerName} 提交成功"`，只允许 `LogInformation("Order {OrderId} submitted", id)` + 白名单字段。**这条不做，下面两阶段脱敏只是心理安慰。**

1. 用户选时间范围、级别、模块和允许的数据类别。
2. 先生成文件/字段清单，展示脱敏状态、预计大小与排除项。
3. 只按字段白名单构建；默认不含数据库、业务附件、请求体、token/password、个人信息、崩溃原始转储中的敏感段。
4. 打包后再次扫描敏感模式、路径和未授权文件；校验失败则停止导出。
5. 用户最终确认后保存到用户选择位置；默认不自动上传。

必须测试日志含模拟 token、密码、邮箱、查询参数、路径和业务文本时，最终包不泄漏敏感数据。

---

## 12. 导入导出、Excel、PDF 与条码

### 12.1 首版范围

- **CSV：Must。** 支持分隔符/引号规则、列映射、类型预览、行级错误报告、取消和分批处理。
- CSV 编码导入先检查 BOM，再尝试严格 UTF-8；若不合法或内容疑似乱码，向用户提供 GB18030/GBK 等候选并允许预览切换。无 BOM 的旧中文编码无法保证自动识别准确，不能静默宣称“已正确识别”。导出默认 UTF-8 with BOM。
- 长单号/编码等声明为字符串的字段必须按文本处理；不经过 `double`，防止科学计数法和尾数丢失。
- 大文件流式读写，阈值根据 POC 设备实测确定；禁止为了显示进度而把完整文件载入内存。
- CSV 导出防公式注入；如允许原样导出，必须明确提示风险。远程导出字段权限在服务端执行。
- **XLSX：Should。** ClosedXML 只做基础读写和数据导入导出，不承诺 Excel 公式/宏引擎。损坏文件、日期、长数字、中文工作表名与内存测试不合格时，首版保持 CSV 并延后 XLSX。
- **PDF/打印和条码：Could。** 首版不依赖 SkiaSharp 原语自建通用 PDF 排版。如果具体业务要求 PDF，单独选择许可证合格、维护情况可靠的布局方案，或由模块实现一至两张固定模板，并在真实打印机/PDF 阅读器上验收。
- 校验规则应复用业务方法，但不建一个需要单独学习的通用导入 DSL。
- 所有导入导出均在后台执行；显示成功、部分成功、失败行数量和文件位置，不能静默丢弃错误行。

### 12.2 组件决策门

- CSV 解析/写出优先采用成熟的 CsvHelper（当前稳定版候选 `34.0.0`，MS-PL/Apache-2.0 双许可，项目选择并归档 Apache-2.0 许可文本），不自写通用 CSV 解析器。锁包前做兼容性测试；不要仅因版本新就跳过回归。编码识别单独处理，并用包含中文、BOM、GB18030/GBK、引号、换行、空值、超长字段和恶意公式的样本测试。
- ClosedXML 通过许可证正文、Office 兼容、长数字、日期、内存和损坏文件 POC 后才作为 Should 启用。
- PDF、条码等未来能力必须重新核对候选库当前版本、许可证正文、依赖、输出质量和维护状态；不得因为早期 ADR 曾提到某库就默认批准。

## 13. 性能、内存与可靠性

### 13.1 通用渲染和运行时规则

- 优先用已有控件虚拟化和分页；只更新变化的数据，避免整页重建。
- 新页面默认使用 Avalonia 编译绑定和正确的 `x:DataType`；必要时通过编译错误/测试修复，而不是引入额外绑定框架。
- **v1 使用标准 .NET self-contained JIT 发布，不把 NativeAOT/主动裁剪列为前置决策。** 不因为猜测包体积而把数据访问从 EF Core 换成 Dapper。先测真实安装包体积；只有实际超出用户接受范围，才单独评估框架依赖发布、压缩或裁剪，并通过运行时反射、数据库迁移和完整功能回归。
- 不强制覆盖 CLR 的 GC/PGO/ReadyToRun 默认配置。先用真实发布版测启动、内存和滚动性能，只有测量证明有收益时才改变运行时设置。
- 不在 UI 线程同步执行 I/O、大型文件解析、排序或复杂计算；可取消任务在请求结束或工作区关闭时正确取消。
- 图片限制解码尺寸、按容量缓存；关闭工作区时释放订阅、计时器、请求和局部引用。不要无差别清空全局缓存，也不要预先开发自定义内存管理器。
- 进度更新适度节流，任务应可取消并给出部分成功/失败状态。

### 13.2 建议验收指标

指标先建立实测基线，再决定是否成为硬门槛；不要用未经同一设备验证的数字作为发布承诺。

- 记录冷启动、常规启动（数据库已建立）、首屏可交互时间；首次建库/迁移时显示进度或明确的等待状态。
- 用户点击后应立即获得视觉反馈；耗时操作要有加载、取消或进度状态。
- 10,000 条本地数据和 100,000/1,000,000 条远程总记录按当前页加载，记录首屏时间、滚动、筛选、编辑和内存。
- 工作区反复打开/关闭、长时间导入导出后检查事件订阅、任务、句柄和内存趋势；允许有受限缓存，但不能出现持续无界增长。
- 使用 `dotnet-counters`、`dotnet-trace` 等工具定位实际瓶颈；不要把截图、平均 FPS 或单次空载数字当作性能报告。
- 在常见办公 Windows 设备和一台较低配置设备上记录测试结果，注明 CPU、内存、分辨率、缩放和 Release 设置。
- 批量导入/导出时 UI 必须保持响应；若某项性能目标超标，先看 SQL、索引、数据量、虚拟化与资源释放，再决定是否需要更重方案。

### 13.3 可靠性测试

至少覆盖网络断开、API 超时、取消请求、数据库忙、磁盘满、配置损坏、日志目录只读、任务中途取消、关窗时有未保存数据、安装升级中断、进程意外退出、备份损坏和权限不足；对断电场景优先通过事务/备份中断模拟验证，不把人工拔电当作唯一测试。

---

## 14. 测试、CI 与发布门禁

### 14.0 测试原则

测试重点是守住数据、安全和常见操作，不追求形式化的测试数量。v1 至少覆盖：

1. CRUD、保存/取消和表单验证；
2. 分页/稳定排序、筛选和取消迟到结果；
3. 数据库迁移、备份恢复和失败时保留原数据；
4. CSV 行级错误、中文编码和长数字；
5. 日志/诊断包脱敏；
6. 导出字段权限；
7. 工作区关闭时取消页面请求、释放订阅；
8. 用户常用键盘/鼠标流程与清楚的错误反馈。

覆盖率可作为趋势指标，不把一个百分比当作测试质量的替代品。关键数据安全规则要有自动化回归。

### 14.1 测试层级

- 单元测试：分页、校验、错误映射、权限裁剪、脱敏、ID/日期/金额处理。
- 集成测试：EF Core/SQLite、迁移、备份恢复、CSV 导入导出、远程 API/令牌。
- Avalonia Headless：用于能在无窗口环境稳定验证的绑定、状态和简单交互冒烟，不假装覆盖真实桌面。
- 真机人工清单：真实窗口、IME、键盘快捷键、DPI/多显示器、安装升级卸载；Phase 0 与每个 GA 候选执行。
- 用户任务可用性检查：建议邀请未参与设计的目标用户完成 §2.2 的任务并记录卡点；可用性检查不代替编译、数据安全和真实桌面验证。

### 14.2 UI 回归基线

首版最多维护 6–10 个最有价值的页面状态截图/快照，优先主窗口、列表、表单、日志中心。字体和系统环境造成的不稳定差异先人工判断；不为追求全矩阵截图而使 CI 常常误报。

### 14.3 键盘与焦点

- 常见任务可以仅用键盘完成；快捷键不改变 Windows 用户熟悉的含义。
- 对话框焦点进入合理控件，Tab 顺序自然，取消后焦点回到发起位置；有未保存内容时先确认。
- 删除、覆盖、恢复等高风险操作有清楚的对象/影响范围确认；普通查询不弹无意义确认。

### 14.4 CI 门禁

- 每次提交：`dotnet format --verify-no-changes`（若所用 SDK 命令不支持则使用已锁定 SDK 对应形式）、构建、关键单元测试、项目引用边界检查。**前置条件：** 仓库必须提交 `.editorconfig` 与 `.gitattributes` 统一换行与缩进，并在骨架首次提交前对模板生成代码跑一次 `dotnet format` 并纳入同一提交——实测 Avalonia.Templates 12.1.4 的未改动工程在 `ViewLocator.cs` 就有 2 处 WHITESPACE 报错（`--verify-no-changes` 退出码 2），不先建立格式化基线会让提交级 CI 从第一天恒红；模板在 Windows 上生成 CRLF，首次 `dotnet format` 会按 `.editorconfig` 统一，把这批格式改动和模板文件放在同一个初始提交。
- 不强制对所有外部分析器警告一刀切 `TreatWarningsAsErrors`；Nullable 与编译错误应认真修复，新增/忽略警告要有明确理由。只对确认会造成安全或数据错误的规则设阻断。
- PR/夜间：集成测试、Headless 冒烟、UI 回归截图、依赖漏洞/许可证/SBOM 扫描、Release 构建和安装包检查。
- **CI 平台矩阵：** 提交级 CI 至少含 ubuntu 与 windows 两个 job——ubuntu 只证「Linux 可构建 + 无头/单元测试通过」，是 §1.3 与 §17「Linux 从早期持续构建」这条承诺的执行载体；windows 负责安装包、凭据与真机相关检查。ubuntu job 通过不等于 Linux 正式支持，正式支持仍按 §1.3 独立实机验收。
- CI 必须报告实际执行命令和结果；未执行的项目标明未执行，不允许 Agent 将计划当结果。

### 14.5 发布门禁

- 固定 .NET SDK、Avalonia 版本；集中管理 NuGet 版本，并提交各项目的锁文件。
- 许可证正文、第三方声明和 SBOM 齐全。
- Windows 安装/卸载、数据保留、备份恢复和签名/文件校验通过。
- 真机人工清单和用户任务检查完成；Linux 只有独立平台验收通过后才能标记正式支持。

## 15. 安装、更新与系统集成

### 15.1 交付解释

- 应用生成 `Plinth.exe` 与必要的运行文件。v1 默认 self-contained JIT，便于目标用户不单独安装 .NET 运行时；是否将来采用框架依赖发布由真实安装包和运维环境决定。
- 交付两种形式：Windows 安装包 + 便携 ZIP。ZIP 和安装包必须包含完整运行依赖，不承诺单文件 EXE。
- 先实测真实体积，不设“必须裁剪才能达到”的硬门槛。体积超出目标时先压缩资源、删去未使用依赖或调整分发方式；不因此默认切换 Dapper/SQL 脚本。
- v1 不开发自动更新服务。用户通过可信发行渠道获取新安装包，升级前先备份用户数据；未来若客户需要自动更新，单独设计签名验证、回滚和故障恢复。

### 15.2 Windows 发布通道

- **首版默认传统安装包，候选为 NSIS**（官方文档：https://nsis.sourceforge.io/）。只使用许可核验通过的压缩模块；NSIS 的不同模块可能有不同许可证，必须按实际构建配置归档。
- 另提供便携 ZIP，适合试用、排障或免安装分发；两种形式都把用户数据放用户数据目录，不与程序安装目录混在一起。
- v1 不默认采用 MSIX/App Installer，也不实现两条自动更新通道。若企业客户明确要求 MSIX，再针对文件系统虚拟化、卸载数据保留、凭据和 OIDC 回调建立单独 POC/ADR。
- 安装/升级/卸载不能静默删除用户数据；升级前可选自动备份并提示备份位置。更新包只从可信渠道分发，不运行未经验证的远程脚本。
- Windows 代码签名、SmartScreen 信誉及正式分发费用需在 GA 预算中说明；开源安装器不等于代码签名免费。

### 15.3 数据目录与权限

- 应用安装目录只放程序和只读资源。
- 用户设置/数据库/日志/缓存分别进入系统推荐目录。**Linux 日志目录须由平台适配器提供 `~/.local/state`（XDG_STATE_HOME）——.NET/path 类库不内置该目录**，不可误写进只读缓存目录。
- 不要求日常运行管理员权限；需要提权时明确告知并缩小操作范围。
- **默认单实例运行**，避免同一用户意外开启多个进程同时操作同一 SQLite。第二次启动可以提示已有实例，或将打开文件/命令转交主实例；v1 不提供“多实例共用数据库”。
- **便携 ZIP 的数据归属**：ZIP 只表示程序免安装，不代表数据库自动随包携带。安装包和 ZIP 均写入系统用户数据目录；v1 不额外提供“数据随程序目录”模式，备份/迁移走统一流程。
- **命令行参数**：v1 只需支持 `--version`；未知参数返回可读错误，不在无窗口环境里直接崩溃。更多自动化参数有真实需求后再加。
- 文件关联不属于 v1 必需能力；如果具体产品以后需要双击文件打开，再单独实现并验收安装注册、路径校验与卸载清理。

---

## 16. 扩展协议与未来演进

### 16.1 首版扩展机制

- 业务模块是普通 C# 项目/程序集，使用明确的注册类或 `IServiceCollection` 扩展方法注册服务、页面/菜单、资源和迁移。
- 共享分页类型和任务/权限能力仅在模块实际需要时使用；日志直接用 `ILogger<T>`，翻译使用 `RESX`/`ResourceManager`。模块内部按普通 C# 方式组织服务与数据访问。
- v1 不要求 JSON Manifest，不运行时发现模块，不建立插件商店，也不强制模块支持任意卸载/热加载。新增模块只需在应用的注册入口添加服务和菜单/页面。
- CI 检查模块引用边界、重复资源 key、迁移与权限声明；只检查确实会造成跨模块耦合的规则。

### 16.2 后续路线（按扩展触发条件排序）

**第一组：出现第二个真实实现时再提取小接口**
- 文件/附件存储出现本地文件系统以外的第二种实现时，提取 `IFileStorage` 等最小契约并测试迁移；当前只按 §4.7 保存逻辑 key/元数据。
- 出现第二个身份提供方或需要统一登出/令牌刷新时，检查现有 OIDC 使用方式并抽取认证适配；不预先构建身份平台。
- 有多个业务模块真正重复的任务、权限或配置行为时，再提取共享能力，必须保留简单默认用法。

**第二组：只有规模/可靠性需求明确后才引入基础设施**
- 服务端持久后台任务、队列/作业调度器；多实例缓存；全文搜索；OpenTelemetry 导出器/指标后端；对象存储；邮件/消息/第三方集成。
- 一旦异步事件要求“数据库提交成功但消息也不能丢”，以事务 Outbox/幂等消费为基础评估，而不是先上 Kafka/RabbitMQ。
- 多租户、组织目录/SCIM、高级审计、密钥轮换与企业策略按具体客户的威胁模型和部署规模落实。

**第三组：独立产品能力**
- 离线双向同步模式：同步队列、幂等、实体版本、删除标记、冲突中心和同步审计；首版不做、不预建空服务。首版只守稳定 ID/UTC/并发版本等一般规则。
- 受控外部进程插件、版本协商 IPC、包签名、能力清单与操作系统隔离。
- DuckDB 本地分析、专业图表/报表、工作流/审批、打印模板设计器、多窗口、系统托盘、自动更新、插件商店、AI Provider/工具审批、更多语言和 RTL。

未来能力由真实场景触发。添加新依赖时检查许可证与兼容性；影响长期架构的变化记录简短决策，必要时用小样例验证。不得仅因路线图出现一个名词就提前添加依赖或空项目。

### 16.3 二开交付形态：源码优先，NuGet 可选

- **源码仓库是权威来源。** 提供可直接运行的应用模板、核心实现源码和完整 CRUD 示例；开发者可以调试、阅读和 fork，不要求先接受黑盒框架。
- **NuGet 是可选的便利分发方式，不是唯一开发方式。** 当某个共享库的边界稳定、公开 API 有测试、版本策略明确后，可从同一源码构建并发布 NuGet 包。初期不为了发布包而提前拆分项目。
- **两种用法并存：** 普通业务项目可以引用稳定 NuGet；需要深入调试或定制底层的团队可以使用源码项目引用或 fork。包版本、标签源码和样例必须一一对应，不能出现“包能用但找不到对应源码”的情况。
- **业务代码默认独立于框架核心。** 菜单、页面和业务 Service 按标准 C# 写法添加；需要改核心时可以 fork，但文档要说明后续跟进上游更新的成本。不得用“禁止改源码”限制开源使用者的自由。
- 是否提供独立二进制分发、源码开放范围和支持承诺属于产品发布决策；在核心 API 尚未稳定前，先把源码、模板和示例做好。
### 16.4 兼容策略

- 公共契约语义化版本；**v1.x 内不破坏公开面**：只增不改签名、不改默认语义；确需破坏时走 major + 一页迁移说明 + `[Obsolete]` 标注并**至少保留一个 minor 周期**（示例代码同步更新）。
- 破坏性改动发布 major 版本并提供迁移工具/说明；弃用与移除记录进 CHANGELOG（含"替代写法"一行）。
- 运行时插件协议（若未来启用）采用版本协商，未知必需能力拒绝加载。
- DB migrations 只向前推进为默认；回滚必须是经过证明的明确脚本/备份恢复，不自动运行反向 SQL。
- 所有用户配置与布局有 `schemaVersion`；旧配置迁移失败时备份并恢复可用默认值，不静默丢失。

---

### 16.5 扩展预留的实施规则与触发条件

| 扩展面 | 首版最小准备 | 达到什么条件才开始实现 | 明确禁止的过度设计 |
|---|---|---|---|
| 多数据库 | §8.6 编码规则、业务 DTO/实体分离、独立迁移策略 | 实际出现服务端 PG/MySQL 交付目标 | 预建空 Provider 项目、万能数据库抽象、用 SQLite 测试冒充跨库验证 |
| 多身份源/SSO | OIDC 标准 + 身份上下文不泄漏厂商 SDK | 第二个身份提供方或企业特性真实需要 | 自研 OAuth/OIDC 协议、首版同时安装多个客户端库 |
| 多租户 | 服务端权限/数据访问流程要求可信租户上下文 | 产品正式提供多租户部署 | 只靠客户端 TenantId、在每个本地实体无差别加租户字段 |
| API 版本兼容 | URL 主版本或明确的版本策略、稳定错误码/分页约定、契约测试 | 公开 API 面向外部二开者或需要独立升级 | 对每个接口做独立版本号、同时维护多套重复 DTO 但没有兼容需求 |
| 文件/对象存储 | 文件逻辑 key 与物理路径分离、文件元数据单独管理 | 第二种存储后端或远程附件成为产品需求 | 首版同时引入本地、S3、云厂商三套 SDK |
| 可恢复后台任务 | 任务状态/进度/取消语义明确；区分页面请求和长任务 | 任务必须跨重启恢复或服务端多实例执行 | 将内存队列包装成“分布式任务系统”、没有幂等就自动重试写操作 |
| 缓存 | 缓存明确可重建；有数据来源和失效规则 | 性能剖析证明数据库/API 已是瓶颈 | 为“以后数据量大”默认依赖 Redis 或将缓存作为权威数据 |
| 搜索 | 常规查询、筛选与分页，不绑定特定全文引擎 | 搜索需求超过数据库索引与 LIKE/标准查询能力并有基准证明 | 在首版同时实现 SQLite FTS、Elastic/OpenSearch、独立搜索 DSL |
| 事件/集成 | 应用服务显式执行副作用、错误可见、关联 ID 可追踪 | 跨系统事件需可靠投递或数据库与消息一致性成为要求 | 一开始建立全局事件总线，或把外部调用塞进 EF 事件钩子 |
| 观测 | 结构化日志、脱敏、correlation ID | 多服务排障/性能指标要求分布式追踪 | 首版绑定特定商业 APM、默认自动上报业务 payload |
| 自动更新 | 应用版本、签名/许可清单、数据与安装目录分离 | 有明确更新通道、安全签名和回滚方案 | 先写自研 updater，再决定打包与分发策略 |
| 服务端扩容 | 服务端代码不把权威业务状态只保存在进程内存，数据状态以数据库/API 为准 | 确实需要多实例/高可用，且压测证明单实例无法满足目标 | 没有容量证据前不拆微服务、不上容器编排、不增加共享基础设施 |
| 功能开关/套餐差异 | 普通显式配置与编译期模块注册 | 有灰度/租户分组需求后再设计可审计开关 | 不在 v1 自建动态表达式引擎或全局 FeatureFlag 平台 |
| 隐私/数据生命周期 | 敏感字段分类、诊断脱敏、备份/导出/删除路径明确 | 市场或合同确实提出数据保留、删除、驻留或审计要求 | 不提前宣称满足所有法规，也不提前搭建无需求的合规平台 |
| 运行时插件 | v1 编译期模块引用边界 | 需要第三方独立发布、隔离或单独升级模块 | 直接反射加载不可信 DLL、远程下载执行代码 |

**准备增加较重的扩展能力时，先简要说明实际使用场景、当前写法的局限、复杂度成本和验证方式。** 普通业务实现不必为此填写表格或走额外审批。

## 17. 开工前验证：先做少量关键试验

目的不是把后续功能提前做完，而是先验证会影响技术路线的少数风险。Windows 11 x64 为首要测试环境。

1. **桌面列表与编辑：** 使用官方模板验证 Avalonia 12.1.4 的 `TableView` + 标准编辑表单，测试中文输入法、键盘、分页、排序、复制、标签切换和必要的列设置。DataGrid 已 Deprecated，不再作为默认新项目选型；只有发现 TableView 无法满足 Must 清单的具体缺口时，才把它作为对比候选。
2. **SQLite 与数据导入：** 验证 EF Core CRUD、迁移、事务、备份恢复、取消、写锁处理，以及 CSV 中文编码、长数字、行级错误和公式注入。只检查数据库可移植性规则，不为未来 PG/MySQL 安装 Provider。
3. **发布与用户数据：** 验证 self-contained 程序、安装包与 ZIP、单实例、用户数据目录、升级前备份、卸载保留数据和故障恢复；记录实际包体积。**起点基线（2026-10-31 实测）：** 官方 MVVM 模板空壳在 SDK 10.0.403 + Avalonia 12.1.4 下 `dotnet publish -c Release -r win-x64 --self-contained` 为 **205.8 MB / 225 个文件**，产物可直接启动窗口；Plinth 的业务依赖、资源与按需字体（§2.3）只在此之上增加，Phase 0 以此为参照而不是从零猜。
4. **远程模式（启用时）：** 验证系统浏览器 OIDC + PKCE、凭据存储、权限拒绝和 token 失效；如果当前版本只交付本地模式，此项可推迟，不阻塞本地基础功能开发。

Linux 从早期持续构建并做基础冒烟；真实桌面环境、输入法、凭据和安装流程通过实机测试后，才可宣称正式支持。XLSX、PDF、条码、Dock、自动更新和插件等可选能力，不做首版开工门槛。

每项试验只需保留运行环境、执行步骤、结果、已知问题和结论。没有必要为每个小功能创建独立 POC 编号或 ADR。

## 18. 分阶段建设计划与工作量

以下是用于规划的粗略区间，不是承诺日期。先完成 Phase 0，再按实际 POC 结果调整排期。文档、样例、用户任务测试和发布演练都是阶段内工作，不再统统列为“另计”。

| 阶段 | 主要交付 | 粗略工作量 | 结束条件 |
|---|---|---:|---|
| Phase 0 | 桌面/网格、SQLite/CSV、发布；远程模式启用时验证 OIDC | 约 5–10 工程日；遇到实际技术阻塞时重新估算 | 桌面与网格基本路径可用；EF/SQLite 与 CSV 数据正确；安装/恢复流程通过；远程模式按范围验证 OIDC |
| Phase 1 | 主窗口、主题、双语、标签工作区、官方 Avalonia MVVM 模板基础上的标准项目结构、快速入门和第一个 CRUD 示例 | 15–25 工程日 | 新人可按指南运行样例并完成 CRUD；提交级 CI 基本可用 |
| Phase 2 | 列表/表单、服务层、分页、CSV、任务中心、日志/诊断、权限与真实示例模块 | 20–35 工程日 | 一条真实业务流程端到端通过，常见操作和安全回归通过 |
| Phase 3 | 备份恢复完善、安装/升级/卸载、依赖许可证/SBOM、发布候选、帮助与模块开发指南 | 15–25 工程日 | Windows GA 验收清单通过，失败项有清楚的延后说明 |
| 后续阶段 | XLSX、PDF/打印、Linux 正式支持、自由停靠、离线同步、多窗口、插件等 | 逐项按需求估算 | 只在具体业务场景和技术验证成立后纳入范围 |

**估算原则：** 由实际参与开发的人按 POC 调整；不要机械使用区间下限，也不要无依据给所有阶段统一乘以某个倍数。UI 样板、可用性测试、入门文档与示例已经包含在阶段内。

### 18.1 决策责任

- **产品/项目负责人**：Must/Should/Could 范围、用户体验目标、发布/延期决定。
- **技术负责人**：技术选型、公开契约、依赖与模块边界；重要更改记录 ADR。
- **安全/发布复核人**：凭据、脱敏、权限、许可证、备份恢复和发布安全。小团队可以一人兼任多个职责；对许可证例外、敏感数据处理和破坏性迁移，尽可能由另一人复核。

ADR 只需记录背景、最终选择、为什么不选其他方案、影响、日期和责任人；不要求每个普通实现细节都写 ADR。

## 19. 主要风险

| 风险 | 处理方式 |
|---|---|
| TableView 的列表能力不足，或业务后来要求单元格编辑 | v1 以 TableView + 表单编辑为默认；列出具体缺口后评估维护活跃、许可证合格的成熟控件。不默认使用 Deprecated DataGrid/归档 TreeDataGrid，也不自研完整网格。 |
| 依赖升级带来兼容或许可变化 | 统一管理版本；升级时核查上游发布、许可证和关键回归。 |
| 查询/导入导出阻塞 UI 或内存持续增长 | 分页、流式处理、取消与真实数据剖析；不要提前自建性能框架。 |
| 数据迁移/恢复造成数据丢失 | 迁移前备份、恢复演练、失败保留原数据；不自动反向 DDL。 |
| 日志/诊断包泄漏敏感信息 | 结构化字段、白名单、用户预览、导出后扫描和回归测试。 |
| OIDC/权限或租户边界实现错误 | 远程服务端最终授权；按启用的身份方式做端到端测试。 |
| 安装/卸载破坏用户数据 | 数据与安装目录分离；安装、升级、卸载及恢复进行真实测试。 |
| PG/MySQL 后续落地时出现数据库差异 | 首版遵守 §8.6；正式支持前分别验证 Provider、迁移、事务、排序/分页和数据类型。 |
| AI 代码不符合主流写法或假报完成 | 先读官方模板与现有样例；要求真实构建/测试结果；关键变更由开发者复核。 |
| 功能不少但用户不知道怎么用 | 使用常见桌面交互；通过真实用户任务发现并修复阻断和高风险误操作。 |
| 构建依赖 nuget.org，公司网络下大包获取不稳 | 实测本机 `api.nuget.org` 被 302 重定向到 `nuget.azure.cn` 后，首包 restore 用时 6.14 分钟，`Microsoft.*.App.Runtime.win-x64` 等 runtime pack 频繁超时、甚至 SSL 连接被强制断开；而 self-contained 发布对这些包是硬依赖。发布与 CI 须预取或缓存 runtime pack（本地包文件夹，或来源与许可经过核验的镜像源），并把实际使用的源写进构建说明；仓库提交 `nuget.config` 固定源，CI 与本机共用同一份，镜像源须核验来源与许可；本地能过不等于 CI 能过，未取到包必须显式标记未执行。 |

## 20. 首版验收清单

以下清单聚焦首版必须正确的事项。Should/Could 能力如未交付，应在发布说明中如实列出，不额外阻断 GA。

- [ ] Windows 11 x64 正常运行；菜单、工具栏、标签、列表和表单符合常见桌面使用习惯；键盘、中文 IME 和常见 DPI 流程经过实机检查。
- [ ] 新增、查看、编辑、删除、保存/取消、字段错误和失败后保留输入行为正确；常见操作有清晰可见入口。
- [ ] 深浅主题、`zh-CN`/`en-US` 核心 UI 和关键错误文案完整；未完成语言不宣称正式支持。
- [ ] SQLite + EF Core 的 CRUD、事务、迁移、备份恢复和失败保护通过测试；数据库访问不阻塞 UI。
- [ ] 数据模型遵守 §8.6 的可移植性规则；清楚声明 PostgreSQL/MySQL 尚未完成 Provider 与服务端集成验收。
- [ ] CSV 中文编码、长数字、行级错误、取消和公式注入防护通过；错误行不会被静默丢弃。
- [ ] 远程模式启用时，OIDC/凭据存储与服务端权限校验通过；服务端不得信任客户端传来的租户标识作为授权依据。
- [ ] 日志/诊断包有容量限制、字段白名单、导出前预览和导出后脱敏检查；模拟敏感数据不会泄漏。
- [ ] 页面关闭会取消页面查询、释放订阅和局部资源；长任务有状态和错误反馈。
- [ ] 安装包与便携 ZIP 可运行；升级/卸载不静默删除用户数据；数据备份和恢复路径经过实测。
- [ ] 构建、关键自动化测试、依赖许可证和 SBOM 检查结果真实可复现；未执行的测试明确标记。
- [ ] 有可运行的 CRUD 示例和简短开发指南。新人上手时间、用户任务完成率作为持续改进指标；若核心任务无法完成或有误删/误覆盖风险，必须先修正。


## 21. 给 AI 编码 Agent 的开发约定

以本文件 v4.0 和仓库当前代码为准。首要目标是可理解、可维护、符合 Avalonia/.NET 主流习惯，而不是增加抽象数量。开始修改前先检查现有实现与官方文档；能在现有结构内清楚解决，就不重构整体架构。

### 21.1 开发规则

1. **沿用官方模板和常规写法。** `Plinth.App` 从当前选定版本的 `dotnet new avalonia.mvvm` 模板起步，保留官方常见入口与 `Views/`、`ViewModels/` 等结构；不把 Gin、Java、Node 目录机械搬进 C# 项目。
2. **避免过度设计。** 不预建空项目、空接口或空目录；不为了单一实现创建多层包装、通用 Repository、事件总线、过滤 DSL 或自研 DI。真实复用发生时再考虑提取。
3. **明确代码职责。** View 负责 UI；ViewModel 负责界面状态和命令；普通 Service 负责业务流程；EF Core/Data 负责数据访问。无需把简单 CRUD 拆成 Handler/UseCase/Adapter 等多层。
4. **数据库有升级意识。** 本地使用 EF Core + SQLite；按 §8.6 编码，避免把 SQLite 特有行为写进业务规则。PostgreSQL/MySQL Provider 未通过实际许可与集成测试前，不得声称已支持。
5. **安全和数据正确性优先。** 服务器执行最终授权；不将秘密写入日志/配置；诊断包必须脱敏；迁移前考虑备份；错误不能被静默吞掉；非幂等写请求不自动重试。
6. **遵循普通 .NET 写法。** 使用构造函数注入、`async/await`、取消令牌和标准配置；I/O 不放 UI 线程；绑定集合只在 UI 线程更新。只在真实需要时增加接口。
7. **依赖使用适配当前 LTS 的稳定版本。** 版本由中央包管理和锁文件控制；新增/升级依赖时检查兼容、许可证和安全公告，不因追求数字新而跳过测试。
8. **用户操作直观。** 常用操作有可见入口；采用常见 Windows 快捷键和一致的表格/表单行为；错误告诉用户发生了什么以及下一步怎么做。
9. **允许合理的业务差异。** 本文件定义公共约定，不要求所有业务页面长得一模一样，也不禁止为真实业务需求使用成熟组件或采用更合适的局部结构。新技术只需说明用途、影响与维护成本。
10. **修改结构保持克制。** 普通局部整理走正常代码评审；影响公开契约、数据迁移、安全、重要依赖或发布方式时，补简短决策记录。不得只凭“更整洁”就全仓库搬文件或重命名。
11. **用真实结果报告进度。** 交付真实代码、执行的构建/测试命令和结果；未执行的测试如实说明。不得用静态截图代替真实界面、内存数据冒充服务端分页或用占位按钮冒充已实现功能。
12. **预留不等于提前建造。** 对象存储、Redis、消息队列、全文搜索、分布式任务、运行时插件、自动更新、AOT 和离线同步按 §16.5 的真实需求触发；不以空接口或占位项目“宣布已支持”。

### 21.2 每阶段的简要交付

- 可运行代码或可复现的验证样例。
- 简短运行方法与已知限制，开发者能在项目中找到入口和修改位置。
- 实际构建/测试结果、失败项与未执行项。
- 新增/升级依赖的版本和许可证复核；重大架构决定及其影响。


## 22. 主要架构决策

这里只记录会影响长期维护、数据、安全或发布方式的关键选择。普通实现细节、目录里的局部整理不需要 ADR。

| 编号 | 决策 | 当前状态 / 何时复核 |
|---|---|---|
| ADR-001 | Avalonia + C#/.NET 桌面应用，不用 WebView 作为主 UI | 主路线；如果实际体验/性能持续不合格再重评。 |
| ADR-002 | 官方 Avalonia MVVM 模板 + 标准 .NET 目录与命名 | 采用；仅在现有方式无法满足真实需求时调整。 |
| ADR-003 | Avalonia `TableView` + 标准表单编辑作为 v1 默认；不自研完整网格 | 采用；单元格编辑若成为真实需求，再评估维护状态与许可证合格的成熟方案。 |
| ADR-004 | 单主窗口、标签和可调分栏 | 首版采用；自由停靠和多窗口按需求评估。 |
| ADR-005 | 本地 SQLite + EF Core；未来服务端 PostgreSQL/MySQL 遵循可移植规则 | 本地已选；服务端 Provider 需分别验证许可证、迁移和集成测试。 |
| ADR-006 | 远程数据使用 HTTPS REST/JSON，认证按需接入 OIDC | 主路线；新协议/身份源由明确需求驱动。 |
| ADR-007 | 依赖优先选择分发许可清晰的开源组件 | 持续执行；每次新增/升级检查完整许可证与传递依赖。 |
| ADR-008 | CSV 为首版格式；XLSX/PDF/打印/条码为可选能力 | 首版按范围实现；只在用户需求和组件验证成立时加入。 |
| ADR-009 | 结构化日志、错误中心和脱敏诊断包；默认不自动上报 | 首版实现；不能为调试方便移除敏感数据防护。 |
| ADR-010 | 只为真实复用增加抽象和扩展设施 | 持续执行；出现第二种实现、外部二开或规模问题时复核。 |


## 23. 官方参考与核验链接

以下链接作为复核入口。版本快照按 2026-10-29 检查，正式锁包和发布时仍须重新核对。

### Avalonia / .NET / 用户交互

- Avalonia 文档：https://docs.avaloniaui.net/
- Avalonia 官方创建项目指南（MVVM 模板及标准文件结构）：https://docs.avaloniaui.net/docs/get-started/create-your-first-project
- Avalonia 官方模板仓库（`dotnet new avalonia.mvvm`）：https://github.com/AvaloniaUI/Avalonia.Templates
- Avalonia MVVM 常见模式：https://docs.avaloniaui.net/docs/how-to/mvvm-how-to
- Avalonia 官方 DI 指南（`ServiceCollectionExtensions` + `App.axaml.cs`）：https://docs.avaloniaui.net/docs/guides/implementation-guides/how-to-implement-dependency-injection
- Avalonia View Locator（`ViewModel`→`View` 命名约定、反射限制）：https://docs.avaloniaui.net/docs/concepts/view-locator
- Microsoft CommunityToolkit.Mvvm 官方说明（轻量、无需强制特定项目架构）：https://learn.microsoft.com/en-us/dotnet/communitytoolkit/mvvm/
- Avalonia GitHub（核心许可与发布）：https://github.com/AvaloniaUI/Avalonia
- Avalonia 12.1.4 发布记录：https://github.com/AvaloniaUI/Avalonia/releases
- Avalonia TableView 官方 API（只读表格，属于 Avalonia 核心）：https://docs.avaloniaui.net/api/avalonia/controls/tableview
- Avalonia.Controls.DataGrid（官方已标记 Deprecated，仅 Bug 修复）：https://github.com/AvaloniaUI/Avalonia.Controls.DataGrid
- 旧版开源 TreeDataGrid（MIT，但仓库已归档）：https://github.com/AvaloniaUI/Avalonia.Controls.TreeDataGrid
- Avalonia 定价与组件层级：https://www.avaloniaui.net/pricing
- .NET 支持周期与当前补丁：https://dotnet.microsoft.com/en-us/platform/support/policy/dotnet-core
- .NET 配置与 Options 模式：https://learn.microsoft.com/en-us/dotnet/core/extensions/configuration ／ https://learn.microsoft.com/en-us/dotnet/core/extensions/options
- .NET TimeProvider（时间与可测试性）：https://learn.microsoft.com/en-us/dotnet/standard/datetime/timeprovider-overview
- .NET 可观测性与 OpenTelemetry：https://learn.microsoft.com/en-us/dotnet/core/diagnostics/observability-with-otel
- EF Core Provider 兼容性原则：https://learn.microsoft.com/en-us/ef/core/providers/
- .NET 10.0.14 / SDK 发布记录：https://github.com/dotnet/core/tree/main/release-notes/10.0/10.0.14
- EF Core 发布/支持矩阵：https://learn.microsoft.com/en-us/ef/core/what-is-new/
- Windows 应用最佳实践：https://learn.microsoft.com/en-us/windows/apps/get-started/best-practices
- Windows 键盘交互指南：https://learn.microsoft.com/en-us/windows/apps/design/input/keyboard-interactions
- Windows 设计指南：https://learn.microsoft.com/en-us/windows/apps/design/guidelines-overview

### 数据 / 网络 / 认证 / 发布

- Microsoft.Data.Sqlite：https://www.nuget.org/packages/Microsoft.Data.Sqlite/
- EF Core：https://learn.microsoft.com/ef/core/
- Npgsql EF Core Provider（PostgreSQL，10.x 版本线）：https://www.npgsql.org/efcore/
- Npgsql NuGet：https://www.nuget.org/packages/Npgsql.EntityFrameworkCore.PostgreSQL/
- MySQL 官方 EF Core Provider（版本/许可复核入口）：https://www.nuget.org/packages/MySql.EntityFrameworkCore/
- MySQL Connector/NET EF Core 兼容矩阵：https://dev.mysql.com/doc/connector-net/en/connector-net-entityframework-core.html
- Pomelo MySQL EF Core Provider（兼容性/许可复核入口）：https://github.com/PomeloFoundation/Pomelo.EntityFrameworkCore.MySql
- EF Core 高效查询与分页：https://learn.microsoft.com/en-us/ef/core/performance/efficient-querying
- SQLite WAL 与并发约束：https://www.sqlite.org/wal.html
- Microsoft.Extensions.Http.Resilience：https://www.nuget.org/packages/Microsoft.Extensions.Http.Resilience
- Serilog：https://www.nuget.org/packages/Serilog/
- Serilog.Extensions.Logging：https://www.nuget.org/packages/Serilog.Extensions.Logging/
- Serilog.Sinks.File：https://www.nuget.org/packages/Serilog.Sinks.File/
- Duende IdentityModel OIDC Client：https://www.nuget.org/packages/Duende.IdentityModel.OidcClient
- MSAL（仅在 Microsoft Entra ID 专用场景评估）：https://github.com/AzureAD/microsoft-authentication-library-for-dotnet
- NSIS 官方主页与许可：https://nsis.sourceforge.io/Main_Page ／ https://nsis.sourceforge.io/Docs/AppendixI.html
- CSV（CsvHelper）：https://www.nuget.org/packages/CsvHelper（34.0.0 于 2026-10-28 发布；锁定前执行回归）
- ClosedXML：https://github.com/ClosedXML/ClosedXML
- SQLitePCLRaw（native SQLite 依赖，若包含需核许可证）：https://www.nuget.org/packages/SQLitePCLRaw.bundle_e_sqlite3/

### 日志 / 安全 / 可选能力

- OWASP Logging Cheat Sheet：https://cheatsheetseries.owasp.org/cheatsheets/Logging_Cheat_Sheet.html
- W3C Trace Context（跨服务关联标准）：https://www.w3.org/TR/trace-context/
- Problem Details for HTTP APIs（RFC 9457）：https://www.rfc-editor.org/rfc/rfc9457
- OWASP File Upload Cheat Sheet：https://cheatsheetseries.owasp.org/cheatsheets/File_Upload_Cheat_Sheet.html
- Avalonia Headless 测试：https://docs.avaloniaui.net/docs/testing/headless-xunit
- PDF、图表、条码等可选组件：纳入具体版本时重新核查上游许可正文、维护状态和再分发要求；本版不预先批准任何单一实现。

## 24. 最终结论

Plinth v4.0 采用 **Avalonia + C#/.NET 10 LTS、官方 MVVM 模板、CommunityToolkit.Mvvm、Microsoft DI、EF Core + 本地 SQLite、RESX、Microsoft.Extensions.Logging + Serilog** 作为首版主路线。数据展示默认使用 Avalonia 核心的 `TableView`，新增/编辑使用标准表单；`Avalonia.Controls.DataGrid` 已被官方标为 Deprecated，旧版 TreeDataGrid 仓库已归档，因此二者不再是默认新项目依赖。其他依赖版本快照截至 2026-10-29，锁包时仍需重新核实稳定版、平台兼容与许可证，不把文档快照当永久最新。

本底座以常规桌面工作流和主流 .NET 开发习惯为基础：官方模板起步，目录名称保持熟悉；菜单、工具栏、列表、表单和快捷键符合用户预期；使用常见 MVVM、构造函数注入、async/await 和 EF Core。只把真正跨业务共享的能力放入独立项目，不要求开发者先学习一套 Plinth 专有架构。

扩展策略是：现在守住数据类型、稳定 ID、时间/金额语义、API/权限边界、配置/文件迁移、日志脱敏等规则；服务端 PostgreSQL/MySQL、对象存储、分布式缓存/队列、搜索、插件和自动更新等设施按真实需求加入。SQLite 是本地数据库，远程桌面客户端访问服务端 API；PostgreSQL/MySQL 作为未来服务端目标，各自通过 Provider 许可、迁移、事务、排序/分页和数据类型测试后再宣称正式支持。

硬要求集中于数据安全、权限、许可证、迁移/恢复、基本 CRUD 和真实发布正确性。其他实现保持开发自由：普通局部改动走正常代码评审，只有影响公开契约、数据、安全、关键依赖或发布模式时才记录重要决策。文档是实施规划；兼容性、性能与平台支持仍须由可复现测试和真实发布包确认。
