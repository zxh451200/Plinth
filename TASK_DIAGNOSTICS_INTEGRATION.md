# 任务、日志、诊断和 CSV 接入

实现依据：架构规范 §3.5、§3.6、§10.4、§11、§12。未修改共享 DI、RESX、SettingsService、DemoService。

## 组合根与入口（由主集成方修改）

```csharp
services.AddSingleton<TaskService>();
services.AddSingleton<DiagnosticService>();
services.AddTransient<TaskDiagnosticsViewModel>();
services.AddSingleton<NavigationItem>(provider => new NavigationItem(
    "shell.taskDiagnostics",
    Loc.Get("nav.taskDiagnostics"),
    () => provider.GetRequiredService<TaskDiagnosticsViewModel>()));
```

使用 `Plinth.App.Services`、`Plinth.App.ViewModels`、`Plinth.App.Navigation`、`Plinth.App.Localization`。
现有 ViewLocator 按命名约定自动找到 TaskDiagnosticsView，不需要改映射。
工具菜单建议在“诊断工具”下放“日志与错误中心”和“任务与诊断”；保留现有 shell.logs。
TaskService 和 DiagnosticService 必须为 singleton，不能每个页面各建实例。

项目文件已增加 CsvHelper 34.0.0 的 VersionOverride 引用，避免修改共享中央包配置。
主集成方可把版本移到 Directory.Packages.props 并移除 VersionOverride。
发布前需还原并提交实际 lock 文件、归档所选 Apache-2.0 许可；本环境没有 dotnet，未生成锁文件或声称已编译通过。

## DemoService 接入建议

保持现有 Parse/Build 调用兼容；它们现在使用 CsvHelper，坏 CSV 会抛 InvalidDataException。
旧 DemoService 仍通过 ReadAllTextAsync 读取文件，并未接入编码选择、取消解析或新错误报告。
主集成方应把导入入口改为：

1. 文件选择后调用 CsvReader.PreviewAsync，展示 Content（最多 20 条数据加表头）、编码和候选。
2. 无 BOM 时始终要求用户确认编码；非法 UTF-8 不自动降级，允许显式选 GB18030/GBK 后再次预览。
3. 用户确认编码、分隔符和源列映射后调用 ReadFileAsync，再调用 MapAndValidate。
4. Errors 非空则整批拒绝入库；调用 CsvWriter.WriteErrorReportAsync 输出行号/错误码/说明。
5. 验证通过后按业务规则查重、校验长度和必填，分批处理但在同一事务内提交；取消必须回滚。
6. 导出改用 CsvWriter.WriteFileAsync，流式枚举行，默认 UTF-8 BOM；不覆盖已有目标，取消删除临时文件。

全局任务示例（不要把页面生命周期 token 传给全局任务）：

```csharp
var taskId = tasks.Enqueue(Loc.Get("demo.export"), "Demo", async (progress, token) =>
{
    progress.Report(null);
    await CsvWriter.WriteFileAsync(destination, header, rows, token);
});
```

业务方法返回失败对象而不抛异常时，调用方必须检查并转为任务失败，不能把失败返回误报为成功。
普通查询继续直接 await。任务状态是当前进程内的；最多保留 200 条（含活动任务），满额拒绝新任务。
进度范围为 0..1，null 表示不可计算；取消是协作式请求，已成功提交的操作保持成功。

## 安全与界面边界

日志页支持时间、等级、模块、任务、错误码、关联 ID 和全文搜索；修改条件后点搜索。
滚动文件流式扫描，保留最近 500 个匹配结果；扫描上限 32 Mi 个字符，超限或坏行会显示摘要。
详情和复制只使用经过第二层过滤的数据；堆栈也经过过滤。查询不访问服务器审计。
源头仍须用结构化 ILogger 字段，不应把业务自由文本或秘密写进消息；查询正则不能识别所有业务隐私。

诊断预览展示冻结后的文件名、精确未压缩字节数、字段和脱敏状态，以及明确排除项。
仅导出 logs.ndjson（UTC 时间、等级、合法 GUID 任务/关联 ID）与 runtime.json（运行时版本、平台）。
不导出消息模板、自由文本、堆栈、设置、业务数据库、附件或原始转储。
用户修改日期、等级、类别会使确认失效；服务生成的新预览会使旧预览 ID 失效。
确认后才构建 ZIP，重新打开 ZIP 检查条目、路径、大小、快照字节及字段类型后才移动到目标路径。
取消/失败清理临时文件，不覆盖原目标，不上传。错误详情使用通用说明，避免将路径/令牌放入 UI。
诊断包当前只支持以上两个安全类别，未承诺包含原始崩溃转储。

## 资源键

以下表格由主集成方合入 Strings.resx 和 Strings.zh-CN.resx；未合入前新增文案显示缺失键占位符，
LocalizationTests 的资源完整性检查也会失败，不应跳过或削弱该检查。

| Key | English | 中文 |
| --- | --- | --- |
| nav.taskDiagnostics | Tasks and diagnostics | 任务与诊断 |
| logs.search | Search | 搜索 |
| logs.module | Module | 模块 |
| logs.taskId | Task ID | 任务 ID |
| logs.errorCode | Error code | 错误码 |
| logs.correlationId | Correlation ID | 关联 ID |
| logs.from | From | 起始日期 |
| logs.to | Through | 截止日期 |
| logs.range.invalid | Start date must not exceed end date. | 起始日期不能晚于截止日期。 |
| logs.scan.summary | Invalid lines: {0}; limited: {1}. | 无效行：{0}；结果受限：{1}。 |
| logs.copyDetails | Copy details | 复制详情 |
| logs.copyId | Copy correlation ID | 复制关联 ID |
| logs.copy.failed | Unable to copy. | 无法复制到剪贴板。 |
| tasks.title | Task center | 任务中心 |
| tasks.cancel | Request cancellation | 请求取消 |
| tasks.name | Task | 任务 |
| tasks.status | Status | 状态 |
| tasks.progress | Progress | 进度 |
| tasks.elapsed | Elapsed | 耗时 |
| tasks.indeterminate | Unknown | 未知 |
| tasks.retry.allowed | Retry is safe. | 可以重试。 |
| tasks.retry.manual | Check the result before starting again. | 请检查处理结果后再重新发起。 |
| tasks.error.failed | The task failed. | 任务执行失败。 |
| tasks.error.next | Check logs using the correlation ID; verify the result before trying again. | 使用关联 ID 查看日志，确认处理结果后再操作。 |
| tasks.status.Waiting | Waiting | 等待 |
| tasks.status.Running | Running | 运行 |
| tasks.status.Succeeded | Succeeded | 成功 |
| tasks.status.Failed | Failed | 失败 |
| tasks.status.Cancelled | Cancelled | 已取消 |
| tasks.status.Cancelling | Cancellation requested | 正在取消 |
| diagnostics.title | Diagnostic package | 诊断包 |
| diagnostics.logs | Log metadata | 日志元数据 |
| diagnostics.runtime | Runtime metadata | 运行时元数据 |
| diagnostics.preview | Preview | 预览 |
| diagnostics.cancelPreview | Cancel preview | 取消预览 |
| diagnostics.file | File | 文件 |
| diagnostics.bytes | Bytes | 字节数 |
| diagnostics.fields | Included fields | 包含字段 |
| diagnostics.redaction | Redaction | 脱敏状态 |
| diagnostics.excluded | Excluded from package | 已排除内容 |
| diagnostics.confirm | I reviewed the file and field list and consent to export. | 我已核对文件和字段清单，同意导出。 |
| diagnostics.export | Export diagnostic package | 导出诊断包 |
| diagnostics.preview.summary | Payload: {0} bytes; invalid lines: {1}; limited: {2}. | 内容：{0} 字节；无效行：{1}；结果受限：{2}。 |
| diagnostics.confirmRequired | Preview and confirm the content first. | 请先预览并确认内容。 |
| diagnostics.cancelled | Cancelled; no package was saved. | 已取消，未保存诊断包。 |
| diagnostics.failed | Unable to complete. Check access, free space and whether the destination already exists. | 操作失败，请检查权限、剩余空间和目标文件是否已存在。 |
| diagnostics.exported | Exported; archive content passed the second scan. | 已导出，包内容通过二次扫描。 |

文件/字段名称、排除项和 metadata-only 是诊断清单的协议描述，不是 UI 操作标签。
首版页面使用简单表格；不需要新增主题资源或 SettingsService 属性。

## 验证

在现有 LogReaderTests、CsvImportTests、CsvExportTests 中增加了任务取消、错误隔离、诊断包敏感样本、
确认约束、冻结预览、失效预览、取消清理、CSV 编码选择、长数字、错误报告、列映射和公式防护测试。
本环境 dotnet --info 返回 command not found，未执行 C# 编译、xUnit 或 Avalonia 运行验证。
集成资源后在有 .NET 10 的环境运行 dotnet test Plinth.sln，并人工确认本地剪贴板、日期选择、文件保存和关页继续执行。
