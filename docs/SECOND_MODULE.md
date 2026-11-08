# 最小第二模块：便笺

`src/Plinth.App/Modules/Notes` 演示一个无需完整列表和表单框架的独立模块。页面编辑本机共用便笺，最多 2000 字；它不是个人私密笔记，也不是跨设备同步服务。

## 从哪里开始抄

按下面的顺序读代码：

1. `NotesModuleRegistration.cs`：普通 `IServiceCollection` 扩展，注册服务、ViewModel、菜单贡献和导航。组合根只加 `services.AddNotesModule()`，Shell 和 ViewLocator 不需要加入便笺专用判断。
2. `Data/Note.cs`：实体与 `IEntityTypeConfiguration<Note>`。表名带模块前缀，Code 保留原文，CodeKey 使用唯一索引，Version 是并发令牌；审计字段由共享 AppDbContext 填写。比较键复用 `Data/CodeIdentity.cs`，不在模块配置里写数据库专属 collation。
3. `Services/NoteService.cs`：每次操作创建并释放 DbContext。查询使用 `AsNoTracking`；保存重新读取版本，拒绝覆盖其他编辑者的新内容，成功后版本加一。服务层检查权限、长度及生产确认。
4. `ViewModels/NoteViewModel.cs`：普通可观察属性和命令，处理加载、保存、错误与未保存确认。保存失败保留输入，重新加载会先确认未保存内容。
5. `Views/NoteView.axaml`：compiled binding、`x:DataType`、标准文本框和按钮。视图首次加载调用 ReloadCommand，不在构造函数启动无法等待的数据库任务。

页面标题通过导航注册进入侧栏。模块只能向 Business 分区追加条目，可设置 `GroupTitle` 让自己的条目单独成组，但不能新增顶层分区或顶栏菜单；分区与 Footer 由宿主拥有。示例模块默认不注册，仅在部署配置 `"IncludeSampleModules": true` 或环境变量 `PLINTH_IncludeSampleModules=true` 时打开。

```csharp
services.AddSingleton<NavigationItem>(provider => new NavigationItem(
    PageId, Loc.Get("notes.title"), () => provider.GetRequiredService<NoteViewModel>())
{
    Order = 240,
    GroupTitle = Loc.Get("notes.group"),
});
```

`Order` 决定 Business 分区内的先后，`GroupTitle` 的文字同样放进双语资源。Ctrl+S 和文件菜单转发同一个 SaveCommand；数据菜单的“重新加载便笺”转发 ReloadCommand。命令可执行状态由页面维护，Shell 不写模块业务逻辑。

`NotesModuleRegistration` 还通过普通 DI 集合注册 `HelpTopic`，将 `notes.editing` 主题关联到 `notes.editor` 页面。按 F1 可定位便笺帮助；主题标题和正文来自中英文资源。模块帮助只需这样注册，无需修改 Shell 或实现新的接口。

## 接入右侧详情面板

页面需要显示只读详情时，实现 `Navigation/IPageDetails`，返回 `PageDetail(Label, Value)` 列表，并在详情变化时发出 `Details` 属性通知。Shell 跟随当前标签订阅，不需要加入模块类型判断。

Demo 示例提供当前选中记录的五个字段；Notes 提供已保存的便笺文本。没有实现该接口或没有选中内容的页面显示空提示。只返回允许用户查看的字段，不通过反射枚举整个实体，不把凭据放入详情。详情面板只读，未保存输入仍由原表单管理。

用户从“查看 → 详情与任务面板”展开右栏，也可从工具栏“任务中心”直接打开任务面板；侧栏宽度、显示状态和所选标签随工作区保存并进入 ZIP 备份。重置布局会收起右栏，已打开的页面保留。

## 数据和权限

模块使用独立的 `notes_entries` 表，固定 Code 为 `default`。服务返回脱离上下文的实体，ViewModel 只保留文本和版本。新建竞争由唯一索引兜底，修改竞争由 Version 检查及 EF 并发令牌兜底。冲突提示要求先保留文本，再重新加载。

设置实体 Code 时同步计算只读 CodeKey；服务按 `default` 的比较键查询。规则只忽略 ASCII `A-Z` 大小写，其他字符保持区分，和 Demo 保存／CSV／XLSX 一致。比较键是折叠后 UTF-8 字节的十六进制文本，不展示、不导出，也不是加密。

默认离线允许本地操作。启用远程身份时需要 `notes:read` 查询、`notes:save` 保存；生产环境保存还需要确认。权限检查写在服务中，直接调用服务也会经过检查。正文不作为安全操作日志字段。

此表使用整个应用的 SQLite 文件，随现有 ZIP 数据库备份一同恢复，没有增加私有设置文件或另一个 DbContext。

## 表结构变更

建表历史保留 `Migrations/20261101010000_NotesEntries.cs`，编码键通过后续 `20261102000000_CodeIdentityKeys.cs` 追加，不修改旧迁移 DDL。最新 snapshot 包含两个模块及 CodeKey；新库执行全部迁移，旧库先保留安全副本，在候选副本上补齐新表与比较键。

今后改实体时在有 SDK 的环境生成新迁移，检查 Up/Down 与 snapshot，再运行真实迁移测试：

```bash
dotnet ef migrations add Notes_YourChange --project src/Plinth.App --context AppDbContext --output-dir Migrations
dotnet test tests/Plinth.App.Tests/Plinth.App.Tests.csproj -p:TestingPlatformDotnetTestSupport=true
```

项目有 SQLite／MySQL／PostgreSQL 三个迁移上下文，必须用 `--context` 指定；服务器 Provider 的迁移另按 [快速入门](../GETTING_STARTED.md) 逐项生成。

当前迁移使用明确固定的 SQLite DDL 和目标模型，未声称通过 `dotnet ef` 生成或真实 EF 验证。Python smoke 验证 DDL、旧数据保留、唯一索引与冻结模型声明；EF 模型一致性由 DatabaseSafetyTests 另行验证。

## 如何移除示例

示例模块默认关闭，需要时用部署配置 `"IncludeSampleModules": true` 或环境变量 `PLINTH_IncludeSampleModules=true` 打开。只隐藏功能时，删除组合根中的 `services.AddNotesModule()` 即可，表和历史迁移保留。已有用户数据后，不应删除实体、旧迁移或数据库文件来绕过升级。

彻底移除表时，需要有计划的数据处理与新的删除迁移，同时更新模型 snapshot、数据库结构校验和相关测试。尚未分发的新项目也建议保留原始迁移，再用新迁移演进，方便读懂历史。

测试入口：`NoteModuleTests.cs` 覆盖真实 SQLite 保存、并发、权限、生产取消、未保存确认、DI 和视图解析；`CloseNavigationTests.cs` 覆盖三种关闭入口的错误页面定位。两者已随全量回归通过（1181/1183，2 项为外部数据库未配置的跳过）；真实 Windows 桌面交互仍需人工验收。
