# 发布前法务审查证据包（待签核）

状态：证据、工具校验、默认包重打包与安装/卸载冒烟已完成；**法务签核未完成**，不构成本项目或本工具的“审查通过”结论。
`coverage-report.json` 的 `license_review_approved` 与 `distribution_inventory_complete` 仍为 `false`。

## 审查对象

- 版本：0.1.0（win-x64，self-contained，XLSX 默认关闭）
- 产物：`artifacts/Plinth-0.1.0-win-x64.zip`（82,484,177 B，SHA256 `25af0958d64aaa54ea8e0285d17758caf152bfe99fa1da19d1f1c1737aefee6a`）
- 安装器：`artifacts/Plinth-Setup-0.1.0-win-x64.exe`（55,889,831 B，SHA256 `d44869a1c7f00d7e0f95e7b4a10c7e216534f782ca0966d9f755c63c81268229`）
- 证据：`reviewed-materials/inventory.json`、`artifacts/compliance/THIRD_PARTY_NOTICES.md`、`sbom.cdx.json`、`coverage-report.json`
- 说明：notices/SBOM/coverage 同时存在于 ZIP 与安装器中；安装器内三个文件的 SHA-256 与 `artifacts/compliance` 逐字节一致。

## 默认包范围与 Carlito RFN

- `Plinth.App.csproj` 在 `PlinthEnableXlsx != true` 时对 ClosedXML 使用 `ExcludeAssets=runtime`；`publish.ps1` 另加门禁：XLSX 关闭时发布目录不得出现 `ClosedXML.dll`，启用时不得缺失。
- 已核验默认发布目录、ZIP、安装器与安装后的目录均无 `ClosedXML.dll`；默认包为 **83 NuGet + 8 补充组件**，`excluded_restored_packages` 含 ClosedXML/ClosedXML.Parser/DocumentFormat.OpenXml/ExcelNumberFormat。默认应用 `--version` 正常。
- 因此 Carlito RFN 风险**不再适用于默认 0.1.0 包**；显式 `-EnableXlsx` 构建仍会分发 `ClosedXML.dll` 及其内嵌 `CarlitoBare`，该变体在法务确认前不得发布。
- notices 仍保守列出 Carlito（补充库存为单一输入）；默认包中没有其字体字节。

## 组件与待决问题

| 组件 | 版本 | 类别 | 许可声明 | 证据 | 待法务确认 |
| --- | --- | --- | --- | --- | --- |
| runtimepack.Microsoft.NETCore.App.Runtime.win-x64 | 10.0.14 | runtime | MIT（nuspec + 包内 LICENSE.TXT） | `runtimepack/LICENSE.TXT`、`THIRD-PARTY-NOTICES.TXT`；187 个 runtime/native 文件已对账 | SDK 安装目录的 Microsoft .NET Library 条款不是 runtime pack 正文；确认自包含分发按包内 MIT 处理 |
| NSIS | 3.11 | nsis | zlib/libpng；bzip2；LZMA CPL 1.0 | `nsis/COPYING` | 确认在 notices 中附 COPYING 即满足 zlib/bzip2/CPL 的分发要求 |
| Skia | 3.119.4 | native | BSD-3-Clause | `skia/LICENSE` + SkiaSharp 原生包 `THIRD-PARTY-NOTICES.txt` | 确认 Skia 静态打包的第三方组件已由该 notices 覆盖 |
| ANGLE | 2.1.27548.20260419 | native | BSD-3-Clause | `angle/LICENSE`（Avalonia 原生包） | 确认二进制分发保留版权与免责声明即可 |
| HarfBuzz | 8.3.1.3 | native | Old MIT（上游）+ MIT（HarfBuzzSharp 包装） | `harfbuzz/COPYING`、`HarfBuzzSharp-LICENSE.txt` | 确认两份正文覆盖原生与包装代码 |
| SQLite | 3.53.3 | native | Public Domain | `sqlite/LICENSE.txt`（sqlite.org 公共领域声明） | 确认公共领域分发不需要额外署名 |
| Roboto Light | 2.137 | font | Apache-2.0 | `roboto/LICENSE.txt` | 上游仓库未发现 NOTICE 文件；确认无 NOTICE 附加义务 |
| CarlitoBare | 1.104 | font | OFL-1.1，含 Reserved Font Name "Carlito" | `carlito/OFL.txt`；字体 name 表为 `CarlitoBare` 1.104 | 默认包不包含该字体；仅 `-EnableXlsx` 变体需要 RFN 结论 |

## 重点风险

1. **XLSX 变体的 Carlito RFN**：默认包已通过排除 ClosedXML 消除；若发布 `-EnableXlsx` 变体，OFL 保留字体名 "Carlito" 与内嵌 `CarlitoBare` 的冲突仍需法务决定。
2. **runtime pack 许可取源**：A) 曾误用 SDK 安装目录条款；B/C) 已改为 runtime pack 包内 MIT 正文并完成 nuspec/文件对账。法务需确认自包含分发按 MIT 处理。
3. **原生产物**：Skia/ANGLE/HarfBuzz/SQLite 的包级 metadata 不足以单独证明原生代码许可；已附上游/原生包正文与 notices，仍需人工确认覆盖范围。
4. **Roboto NOTICE**：上游 `googlefonts/roboto-2` 未发现 NOTICE 文件；确认 Apache-2.0 下无额外 NOTICE 义务。
5. **图标与品牌**：`plinth.ico`、应用名称与界面文案为项目自有；不使用第三方商标。法务可抽查品牌材料。

## 证据校验记录（工具，不代替审查）

- `supplemental_compliance.py --validate reviewed-materials/inventory.json`：8 个组件通过。
- 默认包生成：83 NuGet + 8 补充组件；`uncovered=[]`、`reconciled=1`；两个批准标志为 `false`。
- 补充材料测试 35 项、发布工具测试 12 项通过；删除 nuspec 许可、包内正文哈希、发布文件守卫时测试均变红。
- `release_checks.py package`：ZIP 与安装器校验通过。
- 安装器实机冒烟：安装 `/S` 退出 0；`Plinth.App.exe --version` 输出 `Plinth 0.1.0`；`/D=` 其他目录退出 4；运行中再安装退出 5；静默覆盖安装退出 6；应用退出后卸载退出 0；未知用户文件保留；用户数据保留；卸载注册表键与 `plinth://` 协议清理；安装目录/ZIP/安装器均无 `ClosedXML.dll`。运行中静默卸载在 26 s 内不改文件/注册表（拒绝），但 NSIS 卸载 stub 返回 0，不能把该 0 当作成功。新包不含 ClosedXML 时，安装/升级路径会删除旧版残留的 `ClosedXML.dll`／`ClosedXML.Parser.dll`／`DocumentFormat.OpenXml*.dll`／`ExcelNumberFormat.dll`；已用“卸载保留 dummy ClosedXML.dll、重装后删除、未知用户文件保留”验证。
- 安装器内嵌 notices/SBOM/coverage 的 SHA-256 与 `artifacts/compliance` 一致：
  - notices `3b913feba019613d4ca4dbf5598fde71a0745a9ecf26b7cc37048f117ac87eaf`
  - SBOM `321a6de4b4438619e67fa15b3f5bc5dd6df6419966d5b465ec4ff9eb967146d0`
  - coverage `fcfb181759bf54d8f32a2aa1424d9241599f451c6a4f2d57054a13f981340d15`
- 代码签名：`signtool.exe` 可用（Windows Kits 10.0.26100），当前 Setup `NotSigned`；本机仅有测试证书 `CN=Plinth Test Signing`，不是 CA 证书，正式分发仍需采购/使用 CA 代码签名证书。

## 签核

| 审查人 | 角色 | 日期 | 结论（通过／有条件通过／驳回） | 备注 |
| --- | --- | --- | --- | --- |
|  |  |  |  |  |

签核前不得把本证据包、SBOM 或生成器输出表述为“法务审查通过”。安装/升级/卸载的成功交互升级（需旧候选）与真实机器桌面验收仍属独立发布门禁。
