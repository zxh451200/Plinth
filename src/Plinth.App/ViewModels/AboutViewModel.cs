namespace Plinth.App.ViewModels;

/// <summary>关于页:品牌与版本信息(§2.3 可换品牌入口,后续接入 BrandingConfig)。</summary>
public class AboutViewModel : ViewModelBase
{
    public string ProductName => AppInfo.ProductName;

    public string Version => AppInfo.Version;

    public string Framework => AppInfo.FrameworkDescription;

    public string OperatingSystem => AppInfo.OsDescription;
}
