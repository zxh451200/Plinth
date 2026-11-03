namespace Plinth.App.Modules.Demo.Data;

/// <summary>
/// 示例记录,也是数据库里 demo_records 表的一行。只演示"列表 + 表单 + 保存"的写法,不含任何业务含义。
/// 主键、版本号、创建/修改的时间与人都来自基类 AuditableEntity。
/// </summary>
public class DemoRecord : Plinth.App.Data.AuditableEntity
{
    private string _code = string.Empty;
    /// <summary>保留显示大小写；唯一性只忽略 ASCII 英文字母大小写。</summary>
    public string Code
    {
        get => _code;
        set
        {
            var key = Plinth.App.Data.CodeIdentity.Create(value);
            _code = value;
            CodeKey = key;
        }
    }
    public string CodeKey { get; private set; } = string.Empty;

    public string Name { get; set; } = string.Empty;

    public string Remark { get; set; } = string.Empty;
}
