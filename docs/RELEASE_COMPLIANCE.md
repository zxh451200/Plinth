# 发布补充许可材料

当前自动发现范围仍为真实还原的 NuGet 库存。没有补充输入时可继续生成候选包，
notices 与 SBOM 均明确注明 NuGet-only、非完整分发库存。提供补充输入后也只增加
显式列出的组件，不自动证明 runtime/native/font/icon/NSIS 覆盖完整，不生成审查通过结论。
NuGet 原有许可策略不变。

### 版权归属与覆盖证据报告

生成器同时输出 `coverage-report.json`，将 nuspec 的直接版权声明保留到 notices 与 SBOM，并关联每个包的来源 assets、nuspec／正文 SHA-256、许可声明、被发布过滤排除的包及待补材料。缺少版权时明确标为缺失，不用作者或 SPDX 模板推造声明。补充库存不会自动消除 runtime pack 的未覆盖状态。

公开报告使用编号 assets、发布相对路径与 `nuget/<包名>/<版本>/…` 等逻辑位置，不包含构建机工作区或包缓存的绝对根路径。报告与 notices／SBOM 一起进入发布目录，随 ZIP 和安装器交付；ZIP 检查要求非空报告、JSON 结构、证据路径及哈希格式有效。它提供证据对应及缺口清单，不证明完整分发库存或许可审查通过。

发布清单完整性：指定 `--publish-directory` 时，所有 `.deps.json` 中 `type=package` 的条目必须匹配还原库存，缺少即阻断。`type=runtimepack` 只有在名称、活动 RID target 及非空 runtime/native 条目均符合受限 SDK 规则时，才可作为未覆盖项披露；它不会自动变成已核验组件。未覆盖包的身份、deps 文件和 target 写入 notices、SBOM 的 `plinth:uncovered-publish-package` 元数据及终端输出，普通包不能用类似 runtimepack 的名称绕过检查。

SPDX 正文只从固定版本 `license-list-data/v3.26.0` 获取，缓存按固定来源 URL 隔离。旧的无版本缓存不再读取，也不再回退到未固定版本的 spdx.org 正文，避免把其他来源正文标注成固定版本证据。首次使用或来源版本变化时需访问该上游；没有对应缓存且下载失败会阻断生成。缓存不是第三方许可审查结论。

当前额外接入的 PostgreSQL Provider 已完成下述固定版本上游许可及公开 NuGet nuspec 的核对，允许列表增加 SPDX `PostgreSQL`。生成时仍必须读取实际还原包的声明、包内正文及哈希；不把许可政策允许等同于真实发布库存审查通过。

2026-11-04 已读取当前锁定版本的上游正文：[Npgsql 9.0.3 LICENSE](https://raw.githubusercontent.com/npgsql/npgsql/v9.0.3/LICENSE)（2002–2024 Npgsql）与 [EFCore.PG 9.0.4 LICENSE](https://raw.githubusercontent.com/npgsql/efcore.pg/v9.0.4/LICENSE)（2002–2021 Npgsql）。正文授予无费用使用、复制、修改及分发权，要求保留版权、许可及免责段落，不提供担保或维护义务。同时读取 [Npgsql 9.0.3 nuspec](https://api.nuget.org/v3-flatcontainer/npgsql/9.0.3/npgsql.nuspec) 与 [Provider 9.0.4 nuspec](https://api.nuget.org/v3-flatcontainer/npgsql.entityframeworkcore.postgresql/9.0.4/npgsql.entityframeworkcore.postgresql.nuspec)，包身份／版本对应，声明 `PostgreSQL`。本次未执行实际 NuGet 还原及发布，后续仍须核对缓存中的实际材料与最终分发库存。

发布维护者应根据实际分发内容准备已核对的库存和上游 LICENSE/NOTICE 原文。
工具验证输入结构、身份、版本、来源 URL、正文路径和字节 hash，不代替内容审查，
不增加外部审批步骤，也不会把未知许可转换成 MIT。

当前新增的 PDF/条码示例依赖 `SkiaSharp 3.119.4`（NuGet 元数据 MIT）与
`QRCoder 1.8.1`（MIT）；`SkiaSharp.NativeAssets.Win32` 内含 Skia 原生二进制
（BSD-3-Clause）。生成器按 NuGet 元数据记录包许可，原生载荷的许可正文和分发审查
仍需按实际发布配置单独核对，不能把包级 MIT 当成原生代码审查通过。

数据库可选用 `Npgsql.EntityFrameworkCore.PostgreSQL 9.0.4`（PostgreSQL License）、
`Pomelo.EntityFrameworkCore.MySql 9.0.0`（MIT）及其传递依赖；EF Core 因 Pomelo 的
EF10 Provider 尚不存在而固定在 9.0.22。Oracle MySQL EF Provider 的 GPL 许可未采用。
这些包同样需要随最终发布重新生成 notices/SBOM 并复核传递依赖。

JSON 顶层必须恰好包含 `schema_version`（整数 1）与非空 `components` 数组。
每个组件必须恰好包含以下字段：

| 字段 | 含义 |
| --- | --- |
| `id` | 库存内唯一标识，1–200 字符，仅字母、数字、点、下划线、冒号、连字符，首字符为字母或数字 |
| `name`、`version` | 非空真实名称和版本；不自动推断 |
| `category` | `runtime`、`native`、`font`、`icon`、`nsis` 之一 |
| `source` | 对应版本的 HTTP(S) 上游来源 URL；工具不联网验证来源真实性 |
| `license` | 经核对的非空许可声明名称；按名称原样记录，不冒充已验证 SPDX 标识 |
| `texts` | 非空正文数组，至少一条 `LICENSE`，上游有 NOTICE 时也应列入 |

每条正文必须恰好有 `kind`（`LICENSE` 或 `NOTICE`）、`path`、`sha256`。
`path` 相对于库存 JSON 所在目录，使用 `/`；必须指向该目录树内的实际文件。
拒绝绝对路径、Windows 盘符、反斜线、`..` 和越界符号链接。
正文必须为非空 UTF-8（可含 BOM）；SHA256 为原始字节摘要，换行或 BOM 变化也需更新。
重复 id、名称/版本组合（不区分大小写）、同组件重复正文路径、与 NuGet 重复身份均失败。
未知字段、空库存、缺正文、错误 hash 均不会被静默忽略。
各层 JSON 对象的重复键均拒绝；单行字符串字段不允许首尾空白，
以免名称或版本通过空白绕过重复身份校验。

可在项目根使用下列接口，替换为实际库存路径（这里不提供可误作实际许可的占位材料）：

```powershell
./scripts/publish.ps1 -SupplementalComplianceFile ./reviewed-materials/inventory.json
./scripts/acceptance.ps1 -Package -SupplementalComplianceFile ./reviewed-materials/inventory.json
```

`acceptance.ps1` 只在 `-Package` 时接收该选项；相对库存路径在切换目录前解析。
`publish.ps1` 在还原/构建和替换候选包之前验证补充材料，生成时再次验证。
显式空选项、无效路径或无效材料导致失败，不回退为无补充输入。
也可直接调用 Python 的 `supplemental_compliance.py --validate` 或
`third_party.py --supplemental-compliance-file` 接口。

最终 `THIRD_PARTY_NOTICES.md` 包含组件身份、版本、类别、来源、许可声明、
正文及原始字节 SHA256。`sbom.cdx.json` 将其合并为 CycloneDX 1.5 组件：
`bom-ref` 使用 `urn:plinth:supplemental:` 前缀，LICENSE 在 `licenses[].license.text`
中以 base64 保留原始字节，所有 LICENSE/NOTICE 的路径、hash、原始字节另存于
`plinth:supplemental-evidence` 属性的 JSON 字符串内。该证据 hash 不是组件二进制 hash。
现有发布复制步骤将这两个文件纳入 ZIP 和安装器输入，因此无需额外正文目录，
也没有上次发布残留正文混入本次包的问题。

合成测试只验证工具行为，不证明任何真实依赖的许可正确或发布包覆盖完整。
在没有 PowerShell/.NET/Windows 的环境中，透传检查只是静态检查；
实际 Windows 发布、安装器及包内容的运行验收仍需在相应环境执行。
