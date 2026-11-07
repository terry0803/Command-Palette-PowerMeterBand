using System.Globalization;
using System.Runtime.InteropServices;
using System.Text.Json.Nodes;
using Microsoft.CommandPalette.Extensions.Toolkit;
using Windows.Devices.Power;
using Windows.System.Power;

namespace PowerMeterBand;

/// <summary>
/// The battery, in place of the built-in battery band: Command Palette loads that one before any
/// extension, so after a restart it stood alone on the Dock for a moment. The icon and the number
/// work exactly like the built-in band's (see <see cref="BatteryReading"/> and <see cref="BatteryGlyphs"/>).
/// </summary>
internal sealed partial class BatteryBand : WrappedDockItem, IDisposable
{
    // The same reservation as the built-in percentage bands.
    private const string LabelWidth = "text:100%";

    // Battery readings change slowly, so rather than every second: right away when Windows reports the
    // charger plugged or unplugged, charging starting or stopping, or the percent changing, and every
    // 30 seconds for the rest (charge rate, time left).
    private const int RefreshMs = 30_000;

    private readonly DockValueListItem _item;
    private readonly Lock _lock = new();
    private readonly CoalescedWork _refresh;
    private readonly Timer _timer;
    private IconInfo? _shownIcon;
    private bool _disposed;

    public BatteryPage DetailsPage { get; } = new();

    public BatteryBand()
        : base([], "com.terry.powermeter.battery", "電池")
    {
        Icon = BatteryGlyphs.Full;
        _item = new DockValueListItem(DetailsPage, LabelWidth) { Title = "--", Icon = BatteryGlyphs.Full };
        Items = [_item];

        _refresh = new CoalescedWork(Update);
        try
        {
            PowerManager.PowerSupplyStatusChanged += OnPowerChanged;
            PowerManager.BatteryStatusChanged += OnPowerChanged;
            PowerManager.RemainingChargePercentChanged += OnPowerChanged;
        }
        catch
        {
            // Without the events, the 30-second refresh still keeps it current.
        }

        _timer = new Timer(_ => _refresh.Request(), null, 0, RefreshMs);
    }

    private void OnPowerChanged(object? sender, object e) => _refresh.Request();

    private void Update()
    {
        lock (_lock)
        {
            if (_disposed)
            {
                return;
            }

            var s = BatteryReading.Read();

            // The glyphs are cached, so this only redraws when the level or charging changes.
            var icon = BatteryGlyphs.For(s);
            if (!ReferenceEquals(icon, _shownIcon))
            {
                _item.Icon = icon;
                _shownIcon = icon;
            }

            _item.Title = s.HasBattery == false ? "沒有電池" : s.PercentText ?? "--";
            var (_, time) = BatteryPage.Time(s);
            _item.Subtitle = time == "--" ? BatteryPage.Status(s) : $"{BatteryPage.Status(s)} · {time}";
            DetailsPage.Update(s);
        }
    }

    public void Dispose()
    {
        lock (_lock)
        {
            _disposed = true;
            _timer.Dispose();
            try
            {
                PowerManager.PowerSupplyStatusChanged -= OnPowerChanged;
                PowerManager.BatteryStatusChanged -= OnPowerChanged;
                PowerManager.RemainingChargePercentChanged -= OnPowerChanged;
            }
            catch
            {
            }
        }
    }
}

/// <summary>
/// One look at the battery. Charge, charging and plugged-in come from GetSystemPowerStatus, as the
/// built-in band reads them; capacity and charge rate from Windows' battery report. What Windows can't
/// say stays null (shown as "--"), rather than read as "no battery" or "on battery".
/// </summary>
internal sealed record BatteryReading(
    bool? HasBattery,
    bool IsCharging,
    bool? IsOnAcPower,
    int? Percent,
    int? RemainingMilliwattHours,
    int? FullChargeMilliwattHours,
    int? DesignMilliwattHours,
    int? RateMilliwatts)
{
    private const byte Unknown = 255; // GetSystemPowerStatus's "unknown" for each byte
    private const byte BatteryFlagCharging = 0x08;
    private const byte BatteryFlagNoBattery = 0x80;

    // Windows' own whole percent. (The built-in band divides it into a float and truncates it back,
    // which turns 53% and 59% into 52% and 58%.)
    public string? PercentText => HasBattery == true && Percent is int p ? p.ToString(CultureInfo.InvariantCulture) + "%" : null;

    public static BatteryReading Read()
    {
        var hasStatus = GetSystemPowerStatus(out var status);

        BatteryReport? report = null;
        try
        {
            report = Battery.AggregateBattery.GetReport();
        }
        catch
        {
        }

        // "No battery" only when Windows says so; a battery report with a capacity also means there is one.
        bool? hasBattery = hasStatus && status.BatteryFlag != Unknown ? (status.BatteryFlag & BatteryFlagNoBattery) == 0 : null;
        if (hasBattery is null && report?.FullChargeCapacityInMilliwattHours > 0)
        {
            hasBattery = true;
        }

        return new BatteryReading(
            hasBattery,
            hasStatus && status.BatteryFlag != Unknown && (status.BatteryFlag & BatteryFlagCharging) != 0,
            hasStatus && status.ACLineStatus != Unknown ? status.ACLineStatus == 1 : null,
            hasStatus && status.BatteryLifePercent != Unknown ? status.BatteryLifePercent : null,
            report?.RemainingCapacityInMilliwattHours,
            report?.FullChargeCapacityInMilliwattHours,
            report?.DesignCapacityInMilliwattHours,
            report?.ChargeRateInMilliwatts);
    }

    [DllImport("kernel32.dll")]
    private static extern bool GetSystemPowerStatus(out SystemPowerStatus status);

    [StructLayout(LayoutKind.Sequential)]
    private struct SystemPowerStatus
    {
        public byte ACLineStatus;
        public byte BatteryFlag;
        public byte BatteryLifePercent;
        public byte SystemStatusFlag;
        public uint BatteryLifeTime;
        public uint BatteryFullLifeTime;
    }
}

/// <summary>
/// The built-in band's battery icons: Segoe's MobBattery glyphs, one per tenth of charge (the level
/// rounded, MobBattery0..10 at U+EBA0), the charging set while charging (U+EBAB), and a battery
/// with a question mark when there's none to read (U+EC02).
/// </summary>
internal static class BatteryGlyphs
{
    private static readonly IconInfo[] Discharging = Build(0xEBA0);
    private static readonly IconInfo[] Charging = Build(0xEBAB);
    private static readonly IconInfo Unknown = new(char.ConvertFromUtf32(0xEC02)); // MobBatteryUnknown

    public static IconInfo Full => Discharging[10];

    // The built-in band's math, float and all, so the level steps at the same charges.
    public static IconInfo For(BatteryReading s)
    {
        if (s.HasBattery != true || s.Percent is not int percent)
        {
            return Unknown;
        }

        double percent01 = percent / 100f;
        var level = (int)Math.Round(Math.Clamp(percent01, 0, 1) * 10);
        return s.IsCharging ? Charging[level] : Discharging[level];
    }

    private static IconInfo[] Build(int first) =>
        [.. Enumerable.Range(0, 11).Select(i => new IconInfo(char.ConvertFromUtf32(first + i)))];
}

/// <summary>
/// The battery's details: charge, state, time left or to full, charge rate, capacity and health.
/// </summary>
internal sealed partial class BatteryPage : LiveCardPage
{
    private const string Template = """
        {
          "type": "AdaptiveCard",
          "$schema": "http://adaptivecards.io/schemas/adaptive-card.json",
          "version": "1.5",
          "body": [
            {
              "type": "ColumnSet",
              "columns": [
                {
                  "type": "Column",
                  "items": [
                    { "type": "TextBlock", "text": "電量", "size": "small", "isSubtle": true },
                    { "type": "TextBlock", "text": "${charge}", "size": "extraLarge", "weight": "bolder" }
                  ]
                },
                {
                  "type": "Column",
                  "items": [
                    { "type": "TextBlock", "text": "狀態", "size": "small", "isSubtle": true, "horizontalAlignment": "right" },
                    { "type": "TextBlock", "text": "${status}", "size": "large", "weight": "bolder", "horizontalAlignment": "right" }
                  ]
                }
              ]
            },
            {
              "type": "ColumnSet",
              "spacing": "large",
              "columns": [
                {
                  "type": "Column",
                  "items": [
                    { "type": "TextBlock", "text": "${timeLabel}", "size": "small", "isSubtle": true },
                    { "type": "TextBlock", "text": "${time}", "size": "medium" }
                  ]
                },
                {
                  "type": "Column",
                  "items": [
                    { "type": "TextBlock", "text": "充放電", "size": "small", "isSubtle": true, "horizontalAlignment": "right" },
                    { "type": "TextBlock", "text": "${rate}", "size": "medium", "horizontalAlignment": "right" }
                  ]
                }
              ]
            },
            {
              "type": "ColumnSet",
              "spacing": "large",
              "columns": [
                {
                  "type": "Column",
                  "items": [
                    { "type": "TextBlock", "text": "目前容量", "size": "small", "isSubtle": true },
                    { "type": "TextBlock", "text": "${capacity}", "size": "medium" }
                  ]
                },
                {
                  "type": "Column",
                  "items": [
                    { "type": "TextBlock", "text": "電池健康度", "size": "small", "isSubtle": true, "horizontalAlignment": "right" },
                    { "type": "TextBlock", "text": "${health}", "size": "medium", "wrap": true, "horizontalAlignment": "right" }
                  ]
                }
              ]
            },
            {
              "type": "TextBlock",
              "spacing": "extraLarge",
              "text": "健康度是充飽時的容量和出廠設計容量的比例。插拔電源、開始或停止充電、電量改變時立即更新，其餘每 30 秒更新。",
              "size": "small",
              "isSubtle": true,
              "wrap": true
            }
          ]
        }
        """;

    public BatteryPage()
        : base(Template)
    {
        Id = "com.terry.powermeter.battery.details";
        Name = "查看詳細資訊";
        Title = "電池";
        Icon = BatteryGlyphs.Full;
        Commands =
        [
            new CommandContextItem(new OpenUrlCommand("ms-settings:batterysaver-usagedetails") { Name = "開啟電池使用量設定" }),
            new CommandContextItem(new OpenUrlCommand("ms-settings:batterysaver") { Name = "省電模式設定" }),
            new CommandContextItem(new OpenUrlCommand("ms-settings:powersleep") { Name = "電源與睡眠設定" }),
        ];
        Update(new BatteryReading(null, false, null, null, null, null, null, null));
    }

    // The built-in band's three states, from the same flags as its icon; "--" when Windows can't say.
    internal static string Status(BatteryReading s) =>
        s.HasBattery switch
        {
            false => "沒有電池",
            null => "--",
            _ when s.IsCharging => "充電中",
            _ => s.IsOnAcPower switch { true => "接上電源", false => "使用電池", null => "--" },
        };

    internal static (string Label, string Time) Time(BatteryReading s) =>
        s.HasBattery == true
            ? PowerDetailsPage.EstimateTime(s.RemainingMilliwattHours, s.FullChargeMilliwattHours, s.RateMilliwatts)
            : ("預估剩餘時間", "--");

    public void Update(BatteryReading s) => Show(() => Json(s));

    private static string Json(BatteryReading s)
    {
        var (timeLabel, time) = Time(s);
        var data = new JsonObject
        {
            ["charge"] = s.PercentText ?? "--",
            ["status"] = Status(s),
            ["timeLabel"] = timeLabel,
            ["time"] = time,
            ["rate"] = s.RateMilliwatts switch
            {
                int r when r > 0 => $"充電 {r / 1000.0:0.0} W",
                int r when r < 0 => $"放電 {-r / 1000.0:0.0} W",
                0 => "0 W",
                _ => "--",
            },
            ["capacity"] = s is { RemainingMilliwattHours: int rem, FullChargeMilliwattHours: int full and > 0 }
                ? $"{rem / 1000.0:0.0} / {full / 1000.0:0.0} Wh"
                : "--",
            ["health"] = s is { FullChargeMilliwattHours: int f, DesignMilliwattHours: int design and > 0 }
                ? $"{100.0 * f / design:0}%（設計 {design / 1000.0:0.0} Wh）"
                : "--",
        };

        return data.ToJsonString();
    }
}
