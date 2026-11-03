# 更换应用品牌

复制根目录 `branding.example.json` 为自己的 `branding.json`。默认不需要此文件；缺失或无效配置会回退到 Plinth 默认值。品牌配置在启动时读取。

```json
{
  "ProductName": "My Admin",
  "Description": "团队管理工具",
  "SupportText": "联系支持团队",
  "SupportUrl": "https://example.com/help",
  "LogoFile": "my-logo.png",
  "IconFile": "my-icon.ico",
  "AccentColor": "#2563EB"
}
```

`LogoFile` 用于关于页，`IconFile` 用于运行中的窗口与托盘。两个文件放在配置文件旁边，单个不超过 2 MiB。文件名仅允许英文字母、数字、短横线、下划线及对应扩展名，首字符必须是字母或数字；不接受路径或网络地址。Logo 使用 PNG，图标使用 ICO。留空使用默认图标或产品名称。

`AccentColor` 接受六位十六进制颜色，覆盖浅色和深色 Fluent 调色板的强调色以及 `PlinthAccentBrush`。更换后应实际检查两个主题中的文字对比度与焦点可见性。

打包时执行 `./scripts/publish.ps1 -BrandingFile ./branding.json`，脚本会同时复制配置里引用的图像文件。缺失或超限的引用文件会使打包失败。EXE 在资源管理器中的嵌入图标属于编译资源，二开时另行替换 `Assets/plinth.ico` 后重新编译。

本环境尚未执行 Avalonia 渲染、Windows 图标加载或 PowerShell 打包验收。
