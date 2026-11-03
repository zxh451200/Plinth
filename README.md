# Plinth Desktop

通用的桌面管理端底座(Avalonia 12 + .NET 10),给二次开发用:菜单、导航、标签页、状态栏、日志、主题已经有了,业务模块自己往上加。

## 30 秒跑起来

需要 .NET SDK 10.0.403(版本见 `global.json`)。

```bash
dotnet restore
dotnet run --project src/Plinth.App   # 启动;示例模块默认关闭,需设 deployment.json 的 IncludeSampleModules=true 才出现「示例数据」
dotnet test                            # 跑测试
```

## 该看哪里

| 想做什么 | 看这里 |
|---|---|
| 了解代码结构、怎么新增页面、哪些功能还没做 | [`GETTING_STARTED.md`](GETTING_STARTED.md) |
| 写自己的业务页面 | 照着 `src/Plinth.App/Modules/Demo/` 抄(列表 + 表单 + 注册 + 测试) |
| 抄一个最小独立模块 | [`docs/SECOND_MODULE.md`](docs/SECOND_MODULE.md)，单页便笺 + 权限 + 迁移 |
| 查看变更、升级和回退规则 | [`CHANGELOG.md`](CHANGELOG.md)、[`docs/UPGRADING.md`](docs/UPGRADING.md) |
| 更换品牌、Logo、图标和强调色 | [`docs/BRANDING.md`](docs/BRANDING.md) |
| 配置身份认证、权限与审计 | [`SECURITY_MODEL.md`](SECURITY_MODEL.md)、`deployment.example.json` |
| 了解设计目标和规则 | [`Plinth_Desktop_技术架构与开发规范_v4.0_*.md`](Plinth_Desktop_技术架构与开发规范_v4.0_架构基线与二开交付修订版.md) |

架构约束和目标以规范为准，使用方法见 `GETTING_STARTED.md`。实际完成度与尚未验收的项目见 `docs/IMPLEMENTATION_STATUS.md`；文档中的功能描述不代表构建、测试或真机验收已经通过。

## 说明

- 默认分支为 `main`。
- 仓库已开启 Issues 与 Wiki,便于协作与问题跟踪。
