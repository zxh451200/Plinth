# 发布补充许可材料

当前自动发现范围仍为真实还原的 NuGet 库存。没有补充输入时可继续生成候选包，
notices 与 SBOM 均明确注明 NuGet-only、非完整分发库存。提供补充输入后也只增加
显式列出的组件，不自动证明 runtime/native/font/icon/NSIS 覆盖完整，不生成审查通过结论。
NuGet 原有许可策略不变。

发布维护者应根据实际分发内容准备已核对的库存和上游 LICENSE/NOTICE 原文。
工具验证输入结构、身份、版本、来源 URL、正文路径和字节 hash，不代替内容审查，
不增加外部审批步骤，也不会把未知许可转换成 MIT。

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
