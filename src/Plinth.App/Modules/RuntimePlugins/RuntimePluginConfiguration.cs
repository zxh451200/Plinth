using System.Collections.ObjectModel;
namespace Plinth.App.Modules.RuntimePlugins;
/// <summary>受信任部署输入；不从用户设置、清单、环境变量或 IPC 请求绑定。</summary>
public sealed class RuntimePluginConfiguration
{
    public bool Enabled { get; init; }
    public TimeSpan Timeout { get; init; } = TimeSpan.FromSeconds(5);
    public IReadOnlyList<AllowedPlugin> Allowlist { get; init; } = [];
    public RuntimePluginHost CreateHost() => !Enabled ? new RuntimePluginHost([]) :
        new RuntimePluginHost(new ReadOnlyCollection<AllowedPlugin>(Allowlist.ToArray()), Timeout);
    public static RuntimePluginConfiguration Disabled { get; } = new();
    /// <summary>只接受组合根已经选定的固定路径、批准哈希和操作，不执行发现。</summary>
    public static RuntimePluginConfiguration FromTrustedDeployment(IEnumerable<AllowedPlugin> allowlist, TimeSpan? timeout = null) => new()
    {
        Enabled = true,
        Timeout = timeout ?? TimeSpan.FromSeconds(5),
        Allowlist = new ReadOnlyCollection<AllowedPlugin>(allowlist.ToArray())
    };
}
