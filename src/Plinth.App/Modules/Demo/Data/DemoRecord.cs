using System;
using System.Globalization;

namespace Plinth.App.Modules.Demo.Data;

/// <summary>优先级示例:演示枚举单元格的显示与编辑。</summary>
public enum DemoPriority { Low = 0, Normal = 1, High = 2 }

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

    /// <summary>数量示例:数字单元格。</summary>
    public int Quantity { get; set; }

    /// <summary>截止日期示例:日期单元格。只存日期,不带时间和时区。</summary>
    public DateOnly? DueDate { get; set; }

    public string DueDateText => DueDate is { } date
        ? date.ToString("d", CultureInfo.CurrentCulture)
        : string.Empty;

    /// <summary>优先级示例:枚举单元格。</summary>
    public DemoPriority Priority { get; set; } = DemoPriority.Normal;

    /// <summary>启用示例:布尔单元格。</summary>
    public bool Active { get; set; } = true;
}
