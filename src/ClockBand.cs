using System.Globalization;
using System.Text.Json.Nodes;
using Microsoft.CommandPalette.Extensions.Toolkit;
using Windows.System.UserProfile;

namespace PowerMeterBand;

/// <summary>
/// The time the way a Mac's menu bar shows it, e.g. "週二 下午 8:35", in place of the built-in
/// separate date and time bands. Clicking it opens <see cref="ClockPage"/>.
/// </summary>
internal sealed partial class ClockBand : WrappedDockItem, IDisposable
{
    internal static readonly CultureInfo Chinese = new("zh-TW");

    private readonly DockValueListItem _item;
    private int _zoneCheckMinute = -1;

    public ClockPage Page { get; } = new();

    public ClockBand()
        : base([], "com.terry.powermeter.clock", "日期與時間")
    {
        Icon = new IconInfo(char.ConvertFromUtf32(0xE787)); // Calendar, only shown when picking bands

        // No reserved width: the clock sits at the end of the Dock, where spare room would show as a gap.
        // Tabular digits still keep it from jittering as the minutes change.
        _item = new DockValueListItem(Page, titleWidth: null);
        Items = [_item];

        // Checking every second (instead of waiting a minute) catches up right after sleep or a clock change.
        DockTicker.Subscribe(Update);
    }

    private void Update()
    {
        // .NET keeps the local time zone once it has read it; forget it once a minute so a new time
        // zone set in Windows shows up here too.
        if (DateTime.UtcNow.Minute != _zoneCheckMinute)
        {
            _zoneCheckMinute = DateTime.UtcNow.Minute;
            TimeZoneInfo.ClearCachedData();
        }

        // No subtitle: PowerToys 0.103 squeezes this item's subtitle in under the time and clips it.
        var now = DateTime.Now;
        var title = now.ToString("ddd tt h:mm", Chinese); // "週二 下午 8:35"
        if (title != _item.Title)
        {
            _item.Title = title;
        }

        Page.Update(now);
    }

    public void Dispose() => DockTicker.Unsubscribe(Update);
}

/// <summary>
/// The full date and time: seconds, the lunar date, week and day of the year, time zone and region.
/// </summary>
internal sealed partial class ClockPage : LiveCardPage
{
    private const string Template = """
        {
          "type": "AdaptiveCard",
          "$schema": "http://adaptivecards.io/schemas/adaptive-card.json",
          "version": "1.5",
          "body": [
            { "type": "TextBlock", "text": "${time}", "size": "extraLarge", "weight": "bolder" },
            { "type": "TextBlock", "text": "${date}", "size": "medium", "spacing": "none" },
            { "type": "TextBlock", "text": "${lunar}", "isSubtle": true, "spacing": "small" },
            {
              "type": "ColumnSet",
              "spacing": "large",
              "separator": true,
              "columns": [
                { "type": "Column", "items": [ { "type": "TextBlock", "text": "週次", "size": "small", "isSubtle": true }, { "type": "TextBlock", "text": "${week}" } ] },
                { "type": "Column", "items": [ { "type": "TextBlock", "text": "今年", "size": "small", "isSubtle": true, "horizontalAlignment": "right" }, { "type": "TextBlock", "text": "${dayOfYear}", "horizontalAlignment": "right" } ] }
              ]
            },
            {
              "type": "ColumnSet",
              "spacing": "medium",
              "columns": [
                { "type": "Column", "items": [ { "type": "TextBlock", "text": "時區", "size": "small", "isSubtle": true }, { "type": "TextBlock", "text": "${timeZone}", "wrap": true } ] },
                { "type": "Column", "width": "auto", "items": [ { "type": "TextBlock", "text": "地區", "size": "small", "isSubtle": true, "horizontalAlignment": "right" }, { "type": "TextBlock", "text": "${region}", "horizontalAlignment": "right" } ] }
              ]
            },
            {
              "type": "ColumnSet",
              "spacing": "medium",
              "columns": [
                { "type": "Column", "items": [ { "type": "TextBlock", "text": "24 小時制", "size": "small", "isSubtle": true }, { "type": "TextBlock", "text": "${time24}" } ] },
                { "type": "Column", "items": [ { "type": "TextBlock", "text": "世界標準時間（UTC）", "size": "small", "isSubtle": true, "horizontalAlignment": "right" }, { "type": "TextBlock", "text": "${utc}", "horizontalAlignment": "right" } ] }
              ]
            }
          ]
        }
        """;

    private static readonly ChineseLunisolarCalendar Lunar = new();
    private static readonly string[] Stems = ["甲", "乙", "丙", "丁", "戊", "己", "庚", "辛", "壬", "癸"];
    private static readonly string[] Branches = ["子", "丑", "寅", "卯", "辰", "巳", "午", "未", "申", "酉", "戌", "亥"];
    private static readonly string[] Animals = ["鼠", "牛", "虎", "兔", "龍", "蛇", "馬", "羊", "猴", "雞", "狗", "豬"];
    private static readonly string[] Months = ["正", "二", "三", "四", "五", "六", "七", "八", "九", "十", "冬", "臘"];
    private static readonly string[] Digits = ["", "一", "二", "三", "四", "五", "六", "七", "八", "九", "十"];

    private string _region = Region();
    private long _regionReadAt = long.MinValue; // Environment.TickCount64 when the region was last read.
    private DateTime _day;
    private string _date = string.Empty;
    private string _lunar = string.Empty;
    private string _week = string.Empty;
    private string _dayOfYear = string.Empty;

    public ClockPage()
        : base(Template)
    {
        Id = "com.terry.powermeter.clock.details";
        Name = "查看詳細資訊";
        Title = "日期與時間";
        Icon = Glyphs.None; // Like a Mac's clock; the Dock would otherwise show this icon before the time.
        Commands =
        [
            new CommandContextItem(new OpenUrlCommand("ms-settings:dateandtime") { Name = "日期和時間設定" }),
            new CommandContextItem(new OpenUrlCommand("ms-actioncenter:") { Name = "通知中心與月曆" }),
            new CommandContextItem(new OpenUrlCommand("ms-clock:") { Name = "時鐘 App" }),
        ];
        Update(DateTime.Now);
    }

    // Given every second; made into the page's data when the page opens or a card can take it.
    public void Update(DateTime now) => Show(() => Json(now));

    private string Json(DateTime now)
    {
        // The region is read again when the last reading is a minute old, so a change in Windows' settings shows up.
        // By the time passed, not the minute on the clock: the data is only made while the page is up, and an hour
        // later the minute can be the same.
        var tick = Environment.TickCount64;
        if (_regionReadAt == long.MinValue || tick - _regionReadAt >= 60_000)
        {
            _regionReadAt = tick;
            _region = Region();
        }

        // What depends only on the date is worked out once a day.
        var zh = ClockBand.Chinese;
        if (now.Date != _day)
        {
            _day = now.Date;
            var daysInYear = DateTime.IsLeapYear(now.Year) ? 366 : 365;
            _date = now.ToString("yyyy年M月d日 dddd", zh);
            _lunar = LunarDate(now);
            _week = $"第 {ISOWeek.GetWeekOfYear(now)} 週";
            _dayOfYear = $"第 {now.DayOfYear} 天，還剩 {daysInYear - now.DayOfYear} 天";
        }

        var zone = TimeZoneInfo.Local;
        return new JsonObject
        {
            ["time"] = now.ToString("tt h:mm:ss", zh),
            ["date"] = _date,
            ["lunar"] = _lunar,
            ["week"] = _week,
            ["dayOfYear"] = _dayOfYear,
            ["timeZone"] = zone.IsDaylightSavingTime(now) ? zone.DaylightName : zone.DisplayName,
            ["region"] = _region,
            ["time24"] = now.ToString("HH:mm:ss", CultureInfo.InvariantCulture),
            ["utc"] = now.ToUniversalTime().ToString("M/d HH:mm", CultureInfo.InvariantCulture),
        }.ToJsonString();
    }

    // "農曆丙午年（馬）九月十三". A leap month repeats the month before it, as 閏六月.
    private static string LunarDate(DateTime date)
    {
        try
        {
            var year = Lunar.GetYear(date);
            var month = Lunar.GetMonth(date);
            var leapMonth = Lunar.GetLeapMonth(year);
            var isLeap = leapMonth > 0 && month == leapMonth;
            if (leapMonth > 0 && month >= leapMonth)
            {
                month--;
            }

            var cycle = Lunar.GetSexagenaryYear(date);
            var stem = Lunar.GetCelestialStem(cycle) - 1;
            var branch = Lunar.GetTerrestrialBranch(cycle) - 1;
            return $"農曆{Stems[stem]}{Branches[branch]}年（{Animals[branch]}）{(isLeap ? "閏" : string.Empty)}{Months[month - 1]}月{LunarDay(Lunar.GetDayOfMonth(date))}";
        }
        catch
        {
            return string.Empty;
        }
    }

    // 初一…初十, 十一…十九, 二十, 廿一…廿九, 三十.
    private static string LunarDay(int day) => day switch
    {
        <= 10 => "初" + Digits[day],
        < 20 => "十" + Digits[day - 10],
        20 => "二十",
        < 30 => "廿" + Digits[day - 20],
        _ => "三十",
    };

    // The country set in Windows' region settings; location itself would need the location permission.
    private static string Region()
    {
        try
        {
            return new RegionInfo(GlobalizationPreferences.HomeGeographicRegion).NativeName;
        }
        catch
        {
            return "--";
        }
    }
}
