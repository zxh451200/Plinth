# 发布补充许可材料

当前自动发现范围仍为真实还原的 NuGet 库存。没有补充输入时可继续生成候选包，
notices 与 SBOM 均明确注明 NuGet-only、非完整分发库存。提供补充输入后也只增加
显式列出的组件，不自动证明 runtime/native/font/icon/NSIS 覆盖完整，不生成审查通过结论。
NuGet 原有许可策略不变。

### 版权归属与覆盖证据报告

生成器同时输出 `coverage-report.json`，将 nuspec 的直接版权声明保留到 notices 与 SBOM，并关联每个包的来源 assets、nuspec／正文 SHA-256、许可声明、被发布过滤排除的包及待补材料。缺少版权时明确标为缺失，不用作者或 SPDX 模板推造声明。补充库存不会自动消除 runtime pack 的未覆盖状态；只有与 deps 中 runtimepack 身份、版本完全一致，类别为 `runtime`，同时提供 LICENSE 与 NOTICE 正文，且这些正文与包内 nuspec 许可声明及根目录 LICENSE/NOTICE 逐字节一致、deps 列出的每个 runtime/native 文件在发布目录中非空存在时，才会把该缺口记为 `reconciled`（对账），并把发布文件的路径与 SHA-256 写入报告/SBOM；报告仍保持 `distribution_inventory_complete=false`、`license_review_approved=false`。

公开报告使用编号 assets、发布相对路径与 `nuget/<包名>/<版本>/…` 等逻辑位置，不包含构建机工作区或包缓存的绝对根路径。报告与 notices／SBOM 一起进入发布目录，随 ZIP 和安装器交付；ZIP 检查要求非空报告、JSON 结构、证据路径及哈希格式有效。它提供证据对应及缺口清单，不证明完整分发库存或许可审查通过。

发布清单完整性：指定 `--publish-directory` 时，所有 `.deps.json` 中 `type=package` 的条目必须匹配还原库存，缺少即阻断。`type=runtimepack` 只有在名称、活动 RID target 及非空 runtime/native 条目均符合受限 SDK 规则时，才可作为未覆盖项披露；它不会自动变成已核验组件。未覆盖包的身份、deps 文件和 target 写入 notices、SBOM 的 `plinth:uncovered-publish-package` 元数据及终端输出，普通包不能用类似 runtimepack 的名称绕过检查。满足上述严格条件（含包内 nuspec 许可、根目录 LICENSE/NOTICE 字节及发布文件存在性）的补充组件会把缺口改写入 `reconciled_publish_packages` 与 `plinth:reconciled-publish-package`，并保留“非许可审查通过”说明；缺少 LICENSE/NOTICE、类别或版本不符、nuspec 或正文哈希不符、发布文件缺失或为空时仍保持 uncovered。

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

### 2026-11-09 已核对补充库存（reviewed-materials）

- `reviewed-materials/inventory.json`（schema 1）实际列出 8 个组件及正文/哈希：runtimepack.Microsoft.NETCore.App.Runtime.win-x64 10.0.14（MIT + 包内 ThirdPartyNotices）、NSIS 3.11（zlib/libpng；COPYING 与 3.11 源码包逐字节一致）、Skia 3.119.4（BSD-3-Clause + SkiaSharp 原生包 ThirdPartyNotices）、ANGLE 2.1.27548.20260419（BSD-3-Clause）、HarfBuzz 8.3.1.3（上游 Old MIT + HarfBuzzSharp 包装 MIT；其原生包附带的 ThirdPartyNotices 与 SkiaSharp 包逐字节相同，正文已列在 `skia/THIRD-PARTY-NOTICES.txt`）、SQLite 3.53.3（Public Domain，`e_sqlite3.dll`）、Roboto Light 2.137（Apache-2.0）、CarlitoBare 1.104（OFL-1.1）；`supplemental_compliance.py --validate` 通过（8 个组件）。
- B) runtime pack 核对：实际分发来自 NuGet 包 `Microsoft.NETCore.App.Runtime.win-x64/10.0.14`，包内 `LICENSE.TXT`（MIT）与 `THIRD-PARTY-NOTICES.TXT`（含 msquic、zlib-ng 等原生归属）已入库为 `runtimepack/`；此前 A) 使用的 SDK 安装目录 `LICENSE.txt` 是 Microsoft .NET Library 条款，并非 runtime pack 的许可正文，已替换并移出库存。
- C) 包内与发布内容对账：runtime pack 的 nuspec 许可为 MIT，根目录 `LICENSE.TXT`／`THIRD-PARTY-NOTICES.TXT` 与补充正文逐字节一致；deps 列出的 187 个 runtime/native 文件全部在 `artifacts/publish/win-x64` 中非空存在，路径与 SHA-256 写入 `reconciled_publish_packages` 与 SBOM。缺包目录、nuspec 或正文哈希不符、发布文件缺失/为空时仍回到 uncovered（负控制覆盖）。
- 字体不是纯系统字体：`Avalonia.Dialogs.dll`（Avalonia 12.1.4）内嵌 Roboto Light 2.137，`ClosedXML.dll`（0.105.1）内嵌 CarlitoBare 1.104 四字重；两个 DLL 都在 ZIP 与安装器发布清单中。组件版本取自从 DLL 提取的字体 name 表，Regular 提取件 SHA-256 分别为 `db02096a91c20ab62d459001a1059bc8d78caa35d637dc91494c4440815a6ac1`（Roboto）与 `ec7c1357b67ac184a52ce7e6b4d9f6f8cf3c9518864bfea0d0e88ba07e1183c8`（Carlito）。Carlito 的 OFL 含 Reserved Font Name "Carlito"，内嵌派生名 "CarlitoBare" 是否满足 RFN 条款属法务判断。图标仍为应用自有 `plinth.ico`，无第三方 icon 组件。
- 实跑 `python scripts/third_party.py --assets src/Plinth.App/obj/project.assets.json --output artifacts/compliance-supplemental --publish-directory artifacts/publish/win-x64 --supplemental-compliance-file reviewed-materials/inventory.json`：生成 91 个 NuGet 包 + 8 个补充组件的 notices/SBOM/coverage（`THIRD_PARTY_NOTICES.md` 528,949 B、`sbom.cdx.json` 506,086 B、`coverage-report.json` 142,305 B；该次 ClosedXML 仍在发布图中，默认包最终 83 NuGet，见 D 节）；补充材料专项测试 36 项通过，发布工具测试 12 项通过。
- 覆盖边界：publish deps 的 `runtimepack.Microsoft.NETCore.App.Runtime.win-x64/10.0.14` 不再列为 `uncovered`；生成器按严格条件（身份/版本、nuspec、根目录 LICENSE/NOTICE 字节、187 个发布文件）将其写入 `reconciled_publish_packages` 与 SBOM 的 `plinth:reconciled-publish-package`，coverage 明确“reconciled … not a license review approval”。`distribution_inventory_complete` 与 `license_review_approved` 仍为 false；缺少 NOTICE、类别或版本不符、nuspec/正文哈希不符、发布文件缺失或为空时仍回到 uncovered（正反例测试覆盖）。工具只验证身份/路径/原文/哈希，不代替法务审查；“审查通过”仍属发布决策。

### 2026-11-09 D) 发布包重打包与法务审查证据

- 使用 `pwsh` 7 + NSIS 3.11 与 `publish.ps1 -SupplementalComplianceFile ... -RequireInstaller` 重建默认 0.1.0（XLSX 关闭）：ZIP 82,484,177 B（SHA256 `25af0958d64aaa54ea8e0285d17758caf152bfe99fa1da19d1f1c1737aefee6a`）、Setup 55,889,831 B（SHA256 `d44869a1c7f00d7e0f95e7b4a10c7e216534f782ca0966d9f755c63c81268229`）；`release_checks.py package` 两者通过。安装器随后重编译加入旧版残留清理，ZIP 未变。
- 默认关闭 XLSX 时对 ClosedXML 使用 `ExcludeAssets=runtime`，`publish.ps1` 增加发布目录门禁；发布目录、ZIP、安装器与安装后目录均无 `ClosedXML.dll`，报告为 83 NuGet + 8 补充组件，`excluded_restored_packages` 含 ClosedXML/ClosedXML.Parser/DocumentFormat.OpenXml/ExcelNumberFormat。
- ZIP 内报告 `uncovered=[]`、`reconciled=1`，两个批准标志仍为 false；安装器内嵌 notices/SBOM/coverage 与 `artifacts/compliance` 逐字节一致（`3b91…`／`321a…`／`fcfb…`）。
- 安装器实机冒烟：安装 `/S` 退出 0、`--version` 输出 `Plinth 0.1.0`、`/D=` 其他目录退出 4、运行中再安装退出 5、静默覆盖安装退出 6、应用退出后卸载退出 0；未知用户文件与用户数据保留，卸载注册表键与 `plinth://` 协议清理。运行中静默卸载在 26 s 内不改文件/注册表（拒绝），但 NSIS 卸载 stub 返回 0，需按状态而不是退出码判定。新包不含 ClosedXML 时，安装器在安装/升级路径删除旧版残留的 `ClosedXML.dll`／`ClosedXML.Parser.dll`／`DocumentFormat.OpenXml*.dll`／`ExcelNumberFormat.dll`；已用“卸载保留 dummy ClosedXML.dll、重装后删除、未知用户文件保留”验证。
- 法务审查证据包见 [LEGAL_REVIEW.md](release/LEGAL_REVIEW.md)：默认包已排除 ClosedXML/Carlito，Carlito RFN 仅影响 `-EnableXlsx` 变体；签核栏为空，未宣称审查通过。
- 代码签名：`signtool` 可用，Setup 当前 `NotSigned`，仅有测试证书 `CN=Plinth Test Signing`；正式 CA 签名、`-EnableXlsx` 变体法务结论与成功交互升级（需旧候选）仍是独立门禁。
