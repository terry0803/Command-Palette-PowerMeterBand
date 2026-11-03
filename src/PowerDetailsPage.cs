using System.Text.Json.Nodes;
using Microsoft.CommandPalette.Extensions;
using Microsoft.CommandPalette.Extensions.Toolkit;
using Windows.System.Power;

namespace PowerMeterBand;

/// <summary>
/// Details page opened from the Dock band, laid out like the built-in
/// Performance Monitor pages (an Adaptive Card whose data refreshes every second).
/// </summary>
internal sealed partial class PowerDetailsPage : ContentPage
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
                    { "type": "TextBlock", "text": "整機功率", "size": "small", "isSubtle": true },
                    { "type": "TextBlock", "text": "${systemPower}", "size": "extraLarge", "weight": "bolder" }
                  ]
                },
                {
                  "type": "Column",
                  "items": [
                    { "type": "TextBlock", "text": "電源狀態", "size": "small", "isSubtle": true, "horizontalAlignment": "right" },
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
                    { "type": "TextBlock", "text": "CPU 封裝功率", "size": "small", "isSubtle": true },
                    { "type": "TextBlock", "text": "${cpuPower}", "size": "medium" }
                  ]
                },
                {
                  "type": "Column",
                  "items": [
                    { "type": "TextBlock", "text": "GPU 功率", "size": "small", "isSubtle": true, "horizontalAlignment": "right" },
                    { "type": "TextBlock", "text": "${gpuPower}", "size": "medium", "horizontalAlignment": "right" }
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
                    { "type": "TextBlock", "text": "電池充放電", "size": "small", "isSubtle": true },
                    { "type": "TextBlock", "text": "${batteryRate}", "size": "medium" }
                  ]
                },
                {
                  "type": "Column",
                  "items": [
                    { "type": "TextBlock", "text": "電池電量", "size": "small", "isSubtle": true, "horizontalAlignment": "right" },
                    { "type": "TextBlock", "text": "${batteryLevel}", "size": "medium", "wrap": true, "horizontalAlignment": "right" }
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
                    { "type": "TextBlock", "text": "${timeRemaining}", "size": "medium" }
                  ]
                },
                {
                  "type": "Column",
                  "items": [
                    { "type": "TextBlock", "text": "插電時 Dock 顯示", "size": "small", "isSubtle": true, "horizontalAlignment": "right" },
                    { "type": "TextBlock", "text": "${pluggedInDisplay}", "size": "medium", "horizontalAlignment": "right" }
                  ]
                }
              ]
            },
            {
              "type": "TextBlock",
              "spacing": "extraLarge",
              "text": "整機功率來自 ACPI Power Meter，只在使用電池時有數值；CPU、GPU 功率來自 Intel RAPL。每秒更新。",
              "size": "small",
              "isSubtle": true,
              "wrap": true
            }
          ]
        }
        """;

    private readonly FormContent _form = new() { TemplateJson = Template };

    public PowerDetailsPage()
    {
        Id = "com.terry.powermeter.details";
        Name = "查看詳細資訊";
        Title = "即時耗電功率";
        Icon = PowerDockBand.BoltIcon;
        Commands =
        [
            new CommandContextItem(new TogglePluggedInDisplayCommand()),
            new CommandContextItem(new OpenUrlCommand("ms-settings:batterysaver-usagedetails") { Name = "開啟電池使用量設定" }),
        ];
        Update(new PowerSnapshot(null, null, null, null, null, null, null, false));
    }

    private string? _lastJson;

    public override IContent[] GetContent() => [_form];

    public void Update(PowerSnapshot s)
    {
        var data = new JsonObject
        {
            ["systemPower"] = s.SystemWatts is null && s.PluggedIn == true ? "插電時無法量測" : FormatWatts(s.SystemWatts),
            ["cpuPower"] = FormatWatts(s.CpuWatts),
            ["gpuPower"] = FormatWatts(s.GpuWatts),
            ["pluggedInDisplay"] = DisplaySettings.Describe(DisplaySettings.PluggedIn),
            ["status"] = s.BatteryStatus switch
            {
                BatteryStatus.Charging => "充電中",
                BatteryStatus.Discharging when s.PluggedIn == true => "插電（電池補電）",
                BatteryStatus.Discharging when s.PluggedIn is null => "放電中（電源狀態不明）",
                BatteryStatus.Discharging => "使用電池",
                BatteryStatus.Idle when s.PluggedIn == true => "接上電源",
                BatteryStatus.Idle => "未充放電", // Idle only says the battery's rate is zero.
                BatteryStatus.NotPresent => "沒有電池",
                _ => "--",
            },
            ["batteryRate"] = s.BatteryRateMilliwatts switch
            {
                int r when r > 0 => $"充電 {r / 1000.0:0.0} W",
                int r when r < 0 => $"放電 {-r / 1000.0:0.0} W",
                0 => "0 W",
                _ => "--",
            },
            ["batteryLevel"] = s is { RemainingMilliwattHours: int rem, FullChargeMilliwattHours: int full and > 0 }
                ? $"{100.0 * rem / full:0}%（{rem / 1000.0:0.0} / {full / 1000.0:0.0} Wh）"
                : "--",
        };

        var (label, time) = EstimateTime(s);
        data["timeLabel"] = label;
        data["timeRemaining"] = time;

        // Skip unchanged data so the host doesn't re-render the card for nothing.
        var json = data.ToJsonString();
        if (json == _lastJson)
        {
            return;
        }

        _form.DataJson = json;
        _lastJson = json;
    }

    private static string FormatWatts(double? watts) => watts is double w ? $"{w:0.0} W" : "--";

    private static (string Label, string Time) EstimateTime(PowerSnapshot s)
    {
        if (s is not { RemainingMilliwattHours: int rem, BatteryRateMilliwatts: int rate })
        {
            return ("預估剩餘時間", "--");
        }

        if (rate < 0)
        {
            return ("預估剩餘時間", FormatHours((double)rem / -rate));
        }

        if (rate > 0 && s.FullChargeMilliwattHours is int full && full > rem)
        {
            return ("預估充飽時間", FormatHours((double)(full - rem) / rate));
        }

        return ("預估剩餘時間", "--");
    }

    private static string FormatHours(double hours)
    {
        var minutes = (int)Math.Round(hours * 60);
        return minutes >= 60 ? $"{minutes / 60} 小時 {minutes % 60} 分" : $"{minutes} 分";
    }
}
