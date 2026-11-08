using System.Globalization;
namespace Plinth.App.Modules.RuntimePlugins;

internal static class RuntimePluginStrings
{
    public static string Get(string key) => IsChinese ? Chinese[key] : English[key];
    private static bool IsChinese => CultureInfo.CurrentUICulture.Name.StartsWith("zh", StringComparison.OrdinalIgnoreCase);
    private static readonly IReadOnlyDictionary<string, string> English = new Dictionary<string, string>
    { ["title"] = "Runtime plugins", ["description"] = "Only explicitly deployed, hash-approved executables can receive an operation.", ["disabled"] = "Runtime plugins are disabled by trusted deployment configuration.", ["empty"] = "No approved runtime plugin is deployed.", ["operation"] = "Operation", ["input"] = "Input", ["placeholder"] = "Text sent through the approved IPC operation", ["invoke"] = "Invoke", ["cancel"] = "Cancel", ["ready"] = "Ready", ["success"] = "Plugin completed successfully.", ["error"] = "Plugin operation failed: {0}" };
    private static readonly IReadOnlyDictionary<string, string> Chinese = new Dictionary<string, string>
    { ["title"] = "运行时插件", ["description"] = "只有受信任部署明确批准且哈希匹配的可执行文件可以接收操作。", ["disabled"] = "运行时插件由受信任部署配置默认关闭。", ["empty"] = "当前没有部署已批准的运行时插件。", ["operation"] = "操作", ["input"] = "输入", ["placeholder"] = "通过已批准 IPC 操作发送的文本", ["invoke"] = "执行", ["cancel"] = "取消", ["ready"] = "就绪", ["success"] = "插件操作已成功完成。", ["error"] = "插件操作失败：{0}" };
}
