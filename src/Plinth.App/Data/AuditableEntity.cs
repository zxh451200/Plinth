using System;

namespace Plinth.App.Data;

/// <summary>
/// 业务表的公共基类:主键、版本号、创建和最后修改的时间与人。新增一张表时继承它,这些列就都有了,
/// 保存时由 <see cref="AppDbContext"/> 自动填写,业务代码不用管。
/// 版本号需要业务代码自己在修改时加 1(见 DemoService.SaveAsync),并在表结构配置里声明为并发令牌。
/// </summary>
public abstract class AuditableEntity
{
    public Guid Id { get; set; } = Guid.NewGuid();

    /// <summary>
    /// 版本号,每次修改加 1。两个人同时改同一条时,后保存的一方版本号对不上,会被拒绝(并发控制)。
    /// 用普通数字而不是数据库专有的行版本,换数据库也能用。
    /// </summary>
    public int Version { get; set; }

    /// <summary>创建时间,统一存 UTC。</summary>
    public DateTime CreatedAtUtc { get; set; }

    public string CreatedBy { get; set; } = string.Empty;

    /// <summary>最后修改时间,统一存 UTC。新建时等于创建时间。</summary>
    public DateTime UpdatedAtUtc { get; set; }

    public string UpdatedBy { get; set; } = string.Empty;

    /// <summary>按用户地区和时区显示，UTC 存储值保持不变。</summary>
    public string UpdatedAtText => Services.FormattingService.Current.FormatUtcInstant(UpdatedAtUtc);
}
