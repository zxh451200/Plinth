using System;
using System.Globalization;
using System.Threading;

namespace Plinth.App.Services;

/// <summary>仅负责显示；地区独立于界面语言，不提供货币推断或存储转换。</summary>
public sealed class DisplayFormat
{
    public DisplayFormat(string? region = null, string? timeZoneId = null)
    {
        Culture = ResolveCulture(region);
        TimeZone = ResolveTimeZone(timeZoneId);
    }

    public CultureInfo Culture { get; }
    public TimeZoneInfo TimeZone { get; }

    public static CultureInfo ResolveCulture(string? region)
    {
        if (string.IsNullOrWhiteSpace(region))
            return CultureInfo.ReadOnly((CultureInfo)CultureInfo.CurrentCulture.Clone());
        try
        {
            var culture = CultureInfo.GetCultureInfo(region);
            return culture.IsNeutralCulture ? CultureInfo.InvariantCulture : culture;
        }
        catch (CultureNotFoundException) { return CultureInfo.InvariantCulture; }
    }

    /// <summary>空值跟随系统；无效或不可用的显式选择回退 UTC。</summary>
    public static TimeZoneInfo ResolveTimeZone(string? id)
    {
        if (string.IsNullOrWhiteSpace(id)) return TimeZoneInfo.Local;
        try { return TimeZoneInfo.FindSystemTimeZoneById(id); }
        catch (TimeZoneNotFoundException) { return TimeZoneInfo.Utc; }
        catch (InvalidTimeZoneException) { return TimeZoneInfo.Utc; }
    }

    /// <summary>UTC 数据库列可能被读成 Unspecified，仍按 UTC 解释，绝不按机器本地时间猜测。</summary>
    public string FormatUtcInstant(DateTime utc, bool includeSeconds = false)
    {
        if (utc == default) return string.Empty;
        if (utc.Kind == DateTimeKind.Local)
            throw new ArgumentException("A UTC database value is required.", nameof(utc));
        return FormatInstant(new DateTimeOffset(DateTime.SpecifyKind(utc, DateTimeKind.Utc)), includeSeconds);
    }

    /// <summary>带偏移量显示时间点，区分夏令时回拨时重复的钟面时间。</summary>
    public string FormatInstant(DateTimeOffset instant, bool includeSeconds = false)
    {
        var local = TimeZoneInfo.ConvertTime(instant, TimeZone);
        return local.ToString("d", Culture) + " " + local.ToString(includeSeconds ? "T" : "t", Culture)
            + " " + local.ToString("zzz", CultureInfo.InvariantCulture);
    }

    /// <summary>业务日期没有时区，不能经由午夜时间点转换。</summary>
    public string FormatDate(DateOnly date) => date.ToString("d", Culture);

    // 显示约定固定两位小数；部分 Windows/ICU 配置把 N 的默认小数位设为 3。
    public string FormatNumber(decimal value) => value.ToString("N2", Culture);
}

/// <summary>启动时在创建视图之前初始化一次；用户修改设置后重启生效。</summary>
public static class FormattingService
{
    private static DisplayFormat _current = new();
    public static DisplayFormat Current => Volatile.Read(ref _current);

    public static void Initialize(string? region, string? timeZoneId) =>
        Volatile.Write(ref _current, new DisplayFormat(region, timeZoneId));
}
