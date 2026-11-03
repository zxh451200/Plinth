using System;
using System.Globalization;
using Plinth.App.Data;
using Plinth.App.Localization;
using Plinth.App.Services;
using Xunit;

namespace Plinth.App.Tests;

public sealed class FormattingServiceTests
{
    [Fact]
    public void Region_is_independent_of_language_and_does_not_change_thread_culture()
    {
        var before = CultureInfo.CurrentCulture;
        try
        {
            Loc.Initialize("zh-CN");
            var format = new DisplayFormat("en-US", "UTC");
            Assert.Equal(new DateOnly(2026, 11, 1).ToString("d", CultureInfo.GetCultureInfo("en-US")),
                format.FormatDate(new(2026, 11, 1)));
            Loc.Initialize("en-US");
            Assert.Equal("1,234.50", format.FormatNumber(1234.5m));
            Assert.Same(before, CultureInfo.CurrentCulture);
        }
        finally { Loc.Initialize(Loc.DefaultLanguage); }
    }

    [Fact]
    public void Invalid_choices_fall_back_and_empty_choices_follow_system()
    {
        var invalid = new DisplayFormat("!", "Plinth/Nonexistent");
        Assert.Equal(CultureInfo.InvariantCulture, invalid.Culture);
        Assert.Equal(TimeZoneInfo.Utc, invalid.TimeZone);
        var system = new DisplayFormat();
        Assert.Equal(CultureInfo.CurrentCulture.Name, system.Culture.Name);
        Assert.Equal(TimeZoneInfo.Local, system.TimeZone);
    }

    [Fact]
    public void Utc_and_unspecified_database_values_are_the_same_instant()
    {
        var format = new DisplayFormat("en-GB", "Asia/Shanghai");
        var utc = new DateTime(2026, 10, 31, 20, 0, 0, DateTimeKind.Utc);
        Assert.Equal("01/11/2026 04:00 +08:00", format.FormatUtcInstant(utc));
        Assert.Equal(format.FormatUtcInstant(utc),
            format.FormatUtcInstant(DateTime.SpecifyKind(utc, DateTimeKind.Unspecified)));
        Assert.Throws<ArgumentException>(() => format.FormatUtcInstant(DateTime.SpecifyKind(utc, DateTimeKind.Local)));
        Assert.Equal(string.Empty, format.FormatUtcInstant(default));
    }

    [Fact]
    public void Repeated_daylight_saving_times_remain_distinguishable_and_dates_do_not_shift()
    {
        var format = new DisplayFormat("en-GB", "America/New_York");
        Assert.Equal("01/11/2026 01:30 -04:00",
            format.FormatInstant(new(2026, 11, 1, 5, 30, 0, TimeSpan.Zero)));
        Assert.Equal("01/11/2026 01:30 -05:00",
            format.FormatInstant(new(2026, 11, 1, 6, 30, 0, TimeSpan.Zero)));
        Assert.Equal("01/11/2026", format.FormatDate(new(2026, 11, 1)));
    }

    [Fact]
    public void Entity_display_uses_selected_format_without_mutating_storage()
    {
        var original = FormattingService.Current;
        try
        {
            FormattingService.Initialize("en-GB", "Asia/Shanghai");
            var utc = new DateTime(2026, 10, 31, 20, 0, 0, DateTimeKind.Unspecified);
            var entity = new Entity { UpdatedAtUtc = utc };
            Assert.Equal("01/11/2026 04:00 +08:00", entity.UpdatedAtText);
            Assert.Equal(utc, entity.UpdatedAtUtc);
            Assert.Equal(DateTimeKind.Unspecified, entity.UpdatedAtUtc.Kind);
        }
        finally { FormattingService.Initialize(original.Culture.Name, original.TimeZone.Id); }
    }

    private sealed class Entity : AuditableEntity { }
}
