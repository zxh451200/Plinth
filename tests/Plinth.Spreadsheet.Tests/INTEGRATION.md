# 可选 XLSX 接入

规范 §5 指定 ClosedXML **0.105.1**（不是 0.105.0），服务按此版本编写。
服务、新增 `SpreadsheetViewModel`、`SpreadsheetView` code-behind 和页面测试均以
`PLINTH_XLSX` 编译。没有修改共享包、App 项目、DI、已有 Demo VM 或共享 resx。
AXAML 本身不能使用 C# 预处理：采用无 `x:Class` / 无可选 CLR 类型引用的纯资源，
由条件编译的 `SpreadsheetView` 通过资源 URI 加载。这让关闭符号时也能编译资源，
不必修改共享项目的 AXAML Include/Remove 配置。

当前宿主已接入下面的依赖与导航，独立测试项目也已加入解决方案。以下说明保留为二开接入参考：

1. 在中央包管理中锁定 ClosedXML `0.105.1`，App 添加无版本的 `PackageReference`，
   并在启用 XLSX 的构建配置追加 `DefineConstants`：`$(DefineConstants);PLINTH_XLSX`。
   更新依赖锁和许可证清单（ClosedXML MIT 及传递依赖），保留原 CSV 回归。
2. 主集成代理在现有模块注册处添加下面代码，导航 ID 为 `demo.spreadsheet`。
   `DemoService`、`TaskService`、`SensitiveOperationService` 使用现有注册；后两者必须注入，
   不提供绕过授权/任务中心的回退。生产确认使用现有 `ConfirmProductionAsync` 接线。

```csharp
#if PLINTH_XLSX
services.AddTransient<SpreadsheetService>();
services.AddTransient<SpreadsheetViewModel>();
services.AddSingleton<NavigationItem>(provider => new NavigationItem(
    SpreadsheetViewModel.PageId, new SpreadsheetLabels()["Title"],
    () => provider.GetRequiredService<SpreadsheetViewModel>()));
#endif
```

   所需 using 与现有 Demo 注册一致：`Modules.Demo.Services`、`Modules.Demo.ViewModels`、
   `Navigation`、`Microsoft.Extensions.DependencyInjection`（均在 `Plinth.App` 命名空间下，
   最后一项除外）。ViewLocator 的命名约定自动解析新增 View，无需修改定位器。

3. 页面已实现本地 XLSX 选择、前 20 条样本、完整校验错误、勾选确认、整批导入、
   搜索条件导出、目录选择、可复制的完成路径、任务取消和页面关闭取消。
   `PreviewFromAsync` 使用 `ImportAsync` 读取完整有界批次并保留不可变结果；
   确认时导入同一批次，不再读取源路径，文件在预览后变化不会改变本次提交。
   任何错误阻止确认，成功后清除可提交批次，避免重复点击再次导入。
4. `ImportAsync` 是解析/校验，返回与 CSV 共用的 `CsvReadResult` / `CsvIssue`，
   不直接写库。首行为表头，随后三列按编码、名称、备注排列，`RowNumbers` 为工作表行号。
   仅接受英文 `Code,Name,Remark`（忽略大小写）或中文 `编码,名称,备注`；
   编码/名称必填，长度分别 50/200/500，编码去首尾空白后忽略大小写查重。
   返回值保留原文，现有 Demo 写库会 Trim。空白行也参与验证，不静默丢弃。
5. 确认在 UI 线程调用 `SensitiveOperationService.ConfirmAsync`，随后排入 `TaskService`，
   在任务内部通过 `ExecuteAsync("demo:import", "Demo", "xlsx.import", ...)` 再次授权。
   `PrepareImportCsvAsync` 将预览批次写入唯一临时目录，检查转换后的 5 MiB 限制，
   然后实际调用 `DemoService.ImportCsvAsync(csv, "utf-8", ",", [0,1,2], token)`。
   复用其事务整批提交；`finally` 清理整个临时目录，清理失败有日志及页面提醒。
   转换会补偿现有 CSV 读取器对原文 `'=1` 等前缀的歧义，最终值按 Demo 的 Trim 语义保留。
6. 预览也通过 `demo:import` 授权（审计 operation 为 `xlsx.preview`，不写库）。
   导出通过生产确认和 `demo:export` 授权（`xlsx.export`），查询和文件生成均在授权回调内。
   每次调用 `SearchPageAsync` 最多取 200 条，超过 5000 条拒绝导出；
   分页总数变化或重复 ID 会拒绝并允许重试。现有分页查询不是数据库一致性快照，
   同数量的并发编辑仍可能反映在不同页中；页面不宣称快照导出。
7. 新页面文字独立放在 `Modules/Demo/Resources/SpreadsheetResources.resx`（英文）
   和 `SpreadsheetResources.zh-CN.resx`，资源键及内容可直接交由主代理统一管理。
   `SpreadsheetLabels` 跟随启动语言设置，不需要增加共享 `Strings` 键。

安全及产品边界：

- 最多 5000 条数据、压缩文件 5 MiB、声明解压总量 32 MiB、256 个 ZIP 部件；
  读入前做 ZIP/XML 预检，禁止路径穿越、重复部件、DTD、外部关系、公式和非 XML 部件。
  仅支持一个基本工作表。单元格必须是文本/空值；数值、日期、布尔和公式报错，
  不能从已被 Excel 舍入的数字中恢复长编码；请用户先将源列设为文本再录入。
- XLSX 使用包内 XML 的 Unicode，不提供 CSV 的 GBK/BOM 选择。
  所有导出值严格写为文本；公式样式内容和开头的单引号保持原文，不产生公式。
- 预览验证整个有界文件，因此样本以外错误也阻止导入。错误最多保存 100 条。
  类型/字段错误通过结果返回；文件结构、超限、安全问题抛 `InvalidDataException`；
  IO/权限错误和 `OperationCanceledException` 交由现有任务中心处理。
- 临时导出文件在目标目录中，关闭写流并校验后才移动到唯一最终文件；异常/取消清理临时文件。
  进程被杀或文件系统禁止删除时无法保证清理，调用方应按自己的任务暂存目录策略清理遗留文件。
- ClosedXML 没有可取消的 load/save API；服务通过 `Task.Run` 避免阻塞 UI，
  在文件读取、XML 预检、每行以及 load/save 前后检查取消。load/save 期间取消会延迟响应，
  但取消检查失败不会发布导出文件；不宣称硬中断或固定内存上限。

独立测试项目直接链接实际服务及现有 CSV 源码，不依赖 Avalonia；已加入解决方案：

```sh
dotnet test /home/work/data/xxx/Plinth-main/tests/Plinth.Spreadsheet.Tests/Plinth.Spreadsheet.Tests.csproj --configuration Release
```

覆盖中文、长数字、日期拒绝、公式注入、单引号、字段/表头、预览全量校验、损坏包、
行数边界、大小/ZIP 炸弹/路径/DTD/外链、取消、失败清理和 CSV 契约往返。
新增转换回归覆盖原文单引号及 Demo 的 Trim 语义。
新增 `SpreadsheetPageTests`（同符号）覆盖真实 SQLite 导入、预览后源文件删除、
重复确认保护、样本外错误阻断、提交时再次授权、真实 View 加载及筛选导出：

```sh
dotnet test /home/work/data/xxx/Plinth-main/tests/Plinth.App.Tests/Plinth.App.Tests.csproj --configuration Release -p:DefineConstants=PLINTH_XLSX --filter FullyQualifiedName~SpreadsheetPageTests
```

本次已尝试上述命令，但环境返回 `dotnet: command not found`（退出码 127），未进入编译/测试。
页面测试命令也已尝试，结果相同。已实际通过 XML 解析、43 个中英文资源键/占位符一致性、
页面资源引用、文件选择/表格名称及 C# 可选编译边界的静态检查；不等同于页面运行验证。
官方 SDK 下载地址的提权网络重试也超时，故没有运行通过记录。
已核对规范版本、NuGet 版本页面及 ClosedXML 0.105.1 的文本转换/单引号处理源码；
这些静态核对不能替代在具备仓库指定 .NET SDK 10.0.403 的环境执行上述测试。
正式启用前仍需在目标桌面用 Excel/LibreOffice 检查兼容性，并测量目标机器峰值内存。
