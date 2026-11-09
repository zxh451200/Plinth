# 发布前法务审查证据包（待签核）

状态：证据、工具校验与发布包重打包已完成；**法务签核未完成**，不构成本项目或本工具的“审查通过”结论。
`coverage-report.json` 的 `license_review_approved` 与 `distribution_inventory_complete` 仍为 `false`。

## 审查对象

- 版本：0.1.0（win-x64，self-contained）
- 产物：`artifacts/Plinth-0.1.0-win-x64.zip`、`artifacts/Plinth-Setup-0.1.0-win-x64.exe`
- 证据：`reviewed-materials/inventory.json`、`artifacts/compliance/THIRD_PARTY_NOTICES.md`、`sbom.cdx.json`、`coverage-report.json`
- 说明：notices/SBOM/coverage 同时存在于 ZIP 与安装器中；安装器内三个文件的 SHA-256 与 `artifacts/compliance` 逐字节一致。

## 组件与待决问题

| 组件 | 版本 | 类别 | 许可声明 | 证据 | 待法务确认 |
| --- | --- | --- | --- | --- | --- |
| runtimepack.Microsoft.NETCore.App.Runtime.win-x64 | 10.0.14 | runtime | MIT（nuspec + 包内 LICENSE.TXT） | `runtimepack/LICENSE.TXT`、`THIRD-PARTY-NOTICES.TXT`；deps 列出的 187 个 runtime/native 文件已对账 | SDK 安装目录的 Microsoft .NET Library 条款不是 runtime pack 正文；确认自包含分发按包内 MIT 处理 |
| NSIS | 3.11 | nsis | zlib/libpng；bzip2；LZMA CPL 1.0 | `nsis/COPYING` | 确认在 notices 中附 COPYING 即满足 zlib/bzip2/CPL 的分发要求 |
| Skia | 3.119.4 | native | BSD-3-Clause | `skia/LICENSE` + SkiaSharp 原生包 `THIRD-PARTY-NOTICES.txt` | 确认 Skia 静态打包的第三方组件已由该 notices 覆盖 |
| ANGLE | 2.1.27548.20260419 | native | BSD-3-Clause | `angle/LICENSE`（Avalonia 原生包） | 确认二进制分发保留版权与免责声明即可 |
| HarfBuzz | 8.3.1.3 | native | Old MIT（上游）+ MIT（HarfBuzzSharp 包装） | `harfbuzz/COPYING`、`HarfBuzzSharp-LICENSE.txt` | 确认两份正文覆盖原生与包装代码 |
| SQLite | 3.53.3 | native | Public Domain | `sqlite/LICENSE.txt`（sqlite.org 公共领域声明） | 确认公共领域分发不需要额外署名 |
| Roboto Light | 2.137 | font | Apache-2.0 | `roboto/LICENSE.txt` | 上游仓库未发现 NOTICE 文件；确认无 NOTICE 附加义务 |
| CarlitoBare | 1.104 | font | OFL-1.1，含 Reserved Font Name "Carlito" | `carlito/OFL.txt`；字体 name 表为 `CarlitoBare` 1.104 | **OPEN：派生名 "CarlitoBare" 是否满足 RFN 条款**；若不满足，需改名/移除/取得许可 |

## 重点风险

1. **Carlito RFN（发布阻断候选）**：OFL 声明保留字体名 "Carlito"，而 `ClosedXML.dll` 内嵌的派生字体名为 `CarlitoBare`。若法务认定该名仍使用保留名，需要替换字体资源、移除 ClosedXML 分发或取得书面许可。当前默认构建仍会把 `ClosedXML.dll` 放入发布包，即使 XLSX 功能默认关闭。
2. **runtime pack 许可取源**：A) 曾误用 SDK 安装目录的 Microsoft .NET Library 条款；B/C) 已改为 runtime pack 包内 MIT 正文并完成 nuspec/文件对账。法务需确认自包含分发按 MIT 处理。
3. **原生产物**：Skia/ANGLE/HarfBuzz/SQLite 的包级 metadata 不足以单独证明原生代码许可；已附上游/原生包正文与 notices，仍需人工确认覆盖范围。
4. **图标与品牌**：`plinth.ico`、应用名称与界面文案为项目自有；不使用第三方商标。法务可抽查品牌材料。

## 证据校验记录（工具，不代替审查）

- `supplemental_compliance.py --validate reviewed-materials/inventory.json`：8 个组件通过。
- `third_party.py ... --publish-directory artifacts/publish/win-x64 --supplemental-compliance-file reviewed-materials/inventory.json`：91 NuGet + 8 补充组件；`uncovered=[]`、`reconciled=1`；两个批准标志为 `false`。
- 补充材料测试 34 项、发布工具测试 12 项通过；删除 nuspec 许可、包内正文哈希、发布文件守卫时测试均变红。
- `release_checks.py package`：ZIP 与安装器校验通过。
  - ZIP SHA256 `64d8f00467f400ca1a0141610ccf61ab914eef5777c2bd6fb5cadb32c16b96f6`
  - Setup SHA256 `0793a363700618776d4b0d1bf7daab01d3d129458efce966305fc7fc3a8d3a32`
- 安装器内嵌 notices/SBOM/coverage 的 SHA-256 与 `artifacts/compliance` 一致：
  - notices `132657b531842273442719248df22e0f0746a795263ae58aa0528bf8bc3b14c6`
  - SBOM `2d0335b33b5ca9e1bc0fde5137b931801064f54b8579fd98ef9a5a928dcd6c12`
  - coverage `f18494ce518c9b8e7a3db36ea7117fb9abe23af1f70884fb5251e87751127708`

## 签核

| 审查人 | 角色 | 日期 | 结论（通过／有条件通过／驳回） | 备注 |
| --- | --- | --- | --- | --- |
|  |  |  |  |  |

签核前不得把本证据包、SBOM 或生成器输出表述为“法务审查通过”。安装、升级、卸载与真实机器验收仍属独立发布门禁。
