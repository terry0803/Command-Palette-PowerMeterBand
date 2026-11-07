using System.Diagnostics;
using System.Text.Json.Nodes;
using Microsoft.CommandPalette.Extensions.Toolkit;

namespace PowerMeterBand;

/// <summary>
/// One Dock band in place of the built-in CPU, GPU and memory bands. It shows the metric the
/// user picks on <see cref="SystemMonitorPage"/>, with that metric's built-in icon.
/// </summary>
internal sealed partial class SystemMonitorBand : WrappedDockItem, IDisposable
{
    // The same reservation as the built-in percentage bands.
    private const string LabelWidth = "text:100%";

    private readonly DockValueListItem _item;
    private readonly SystemSampler _sampler = new();
    private readonly Lock _lock = new();
    private MonitorMetric? _shownMetric;
    private bool _disposed;

    public SystemMonitorPage DetailsPage { get; }

    public SystemMonitorBand()
        : base([], "com.terry.powermeter.monitor", "CPU、GPU、記憶體")
    {
        Icon = MonitorSettings.Icon(MonitorMetric.Cpu);
        DetailsPage = new SystemMonitorPage(_sampler);
        _item = new DockValueListItem(DetailsPage, LabelWidth) { Title = "--" };
        Items = [_item];

        DockTicker.Subscribe(Update, slowFirstRun: true);
    }

    private void Update()
    {
        lock (_lock)
        {
            if (_disposed)
            {
                return;
            }

            var s = _sampler.Read();
            var metric = MonitorSettings.Metric;

            // Only swap the icon when the choice changes, so the host doesn't redraw it every second.
            if (_shownMetric != metric)
            {
                _item.Icon = MonitorSettings.Icon(metric);
                _shownMetric = metric;
            }

            _item.Title = Percent(s.Percent(metric));
            _item.Subtitle = $"CPU {Percent(s.CpuPercent)} · GPU {Percent(s.GpuPercent)} · 記憶體 {Percent(s.MemoryPercent)}";
            DetailsPage.Update(s);
        }
    }

    // Truncated like the built-in bands, so 11.9% reads 11%.
    internal static string Percent(float? value) => value is float v ? $"{(int)v}%" : "--";

    public void Dispose()
    {
        lock (_lock)
        {
            _disposed = true;
            DockTicker.Unsubscribe(Update);
            _sampler.Dispose();
        }
    }
}

/// <summary>
/// CPU, GPU and memory on one page, each with the built-in Performance Monitor's chart and
/// numbers, plus what the hardware is and the processor temperature.
/// </summary>
internal sealed partial class SystemMonitorPage : LiveCardPage
{
    // Each metric is a chart with its numbers beside it, so all three fit in the Dock's flyout
    // without scrolling (it redraws every second, which would keep jumping back to the top).
    private const string Template = """
        {
          "type": "AdaptiveCard",
          "$schema": "http://adaptivecards.io/schemas/adaptive-card.json",
          "version": "1.5",
          "body": [
            {
              "type": "ColumnSet",
              "columns": [
                { "type": "Column", "width": "auto", "items": [ { "type": "TextBlock", "text": "CPU", "weight": "bolder" } ] },
                { "type": "Column", "width": "stretch", "items": [ { "type": "TextBlock", "text": "${cpuName}", "isSubtle": true, "horizontalAlignment": "right" } ] }
              ]
            },
            {
              "type": "ColumnSet",
              "spacing": "small",
              "columns": [
                { "type": "Column", "width": "auto", "items": [ { "type": "Image", "url": "${cpuChart}", "width": "202px", "height": "60px" } ] },
                {
                  "type": "Column", "width": "stretch", "verticalContentAlignment": "center",
                  "items": [
                    { "type": "TextBlock", "text": "${cpuUsage}", "size": "extraLarge", "weight": "bolder", "horizontalAlignment": "right" },
                    { "type": "TextBlock", "text": "速度 ${cpuSpeed}", "size": "small", "isSubtle": true, "spacing": "none", "horizontalAlignment": "right" },
                    { "type": "TextBlock", "text": "溫度 ${temperature}", "size": "small", "isSubtle": true, "spacing": "none", "horizontalAlignment": "right" }
                  ]
                }
              ]
            },
            {
              "type": "ColumnSet",
              "separator": true,
              "spacing": "medium",
              "columns": [
                { "type": "Column", "width": "auto", "items": [ { "type": "TextBlock", "text": "GPU", "weight": "bolder" } ] },
                { "type": "Column", "width": "stretch", "items": [ { "type": "TextBlock", "text": "${gpuName}", "isSubtle": true, "horizontalAlignment": "right" } ] }
              ]
            },
            {
              "type": "ColumnSet",
              "spacing": "small",
              "columns": [
                { "type": "Column", "width": "auto", "items": [ { "type": "Image", "url": "${gpuChart}", "width": "202px", "height": "60px" } ] },
                {
                  "type": "Column", "width": "stretch", "verticalContentAlignment": "center",
                  "items": [
                    { "type": "TextBlock", "text": "${gpuUsage}", "size": "extraLarge", "weight": "bolder", "horizontalAlignment": "right" },
                    { "type": "TextBlock", "text": "溫度 ${gpuTemperature}", "size": "small", "isSubtle": true, "spacing": "none", "horizontalAlignment": "right" },
                    { "type": "TextBlock", "text": "${gpuTemperatureNote}", "size": "small", "isSubtle": true, "spacing": "none", "horizontalAlignment": "right" }
                  ]
                }
              ]
            },
            {
              "type": "ColumnSet",
              "separator": true,
              "spacing": "medium",
              "columns": [
                { "type": "Column", "width": "auto", "items": [ { "type": "TextBlock", "text": "記憶體", "weight": "bolder" } ] },
                { "type": "Column", "width": "stretch", "items": [ { "type": "TextBlock", "text": "${memoryName}", "isSubtle": true, "horizontalAlignment": "right" } ] }
              ]
            },
            {
              "type": "ColumnSet",
              "spacing": "small",
              "columns": [
                { "type": "Column", "width": "auto", "items": [ { "type": "Image", "url": "${memoryChart}", "width": "202px", "height": "60px" } ] },
                {
                  "type": "Column", "width": "stretch", "verticalContentAlignment": "center",
                  "items": [
                    { "type": "TextBlock", "text": "${memoryUsage}", "size": "extraLarge", "weight": "bolder", "horizontalAlignment": "right" },
                    { "type": "TextBlock", "text": "使用中 ${memoryUsed}", "size": "small", "isSubtle": true, "spacing": "none", "horizontalAlignment": "right" },
                    { "type": "TextBlock", "text": "共 ${memoryTotal}", "size": "small", "isSubtle": true, "spacing": "none", "horizontalAlignment": "right" }
                  ]
                }
              ]
            },
            {
              "type": "ColumnSet",
              "spacing": "small",
              "columns": [
                { "type": "Column", "items": [ { "type": "TextBlock", "text": "已認可 ${memoryCommitted}", "size": "small", "isSubtle": true } ] },
                { "type": "Column", "width": "auto", "items": [ { "type": "TextBlock", "text": "已快取 ${memoryCached}", "size": "small", "isSubtle": true, "horizontalAlignment": "right" } ] }
              ]
            }
          ]
        }
        """;

    private readonly SystemSampler _sampler;
    private HardwareInfo _hardware = HardwareInfo.Unknown;

    public SystemMonitorPage(SystemSampler sampler)
        : base(Template)
    {
        _sampler = sampler;
        Id = "com.terry.powermeter.monitor.details";
        Name = "查看詳細資訊";
        Title = "CPU、GPU、記憶體";
        UpdateCommands();
        Task.Run(() => _hardware = HardwareInfo.Read());
    }

    // Made from the latest readings when the page opens or a card can take it, not every second regardless.
    public void Update(SystemSnapshot s) => Show(() => Json(s));

    private string Json(SystemSnapshot s)
    {
        var data = new JsonObject
        {
            ["cpuName"] = _hardware.Cpu,
            ["gpuName"] = _hardware.Gpu,
            ["memoryName"] = _hardware.Memory,
            ["cpuUsage"] = SystemMonitorBand.Percent(s.CpuPercent),
            ["cpuSpeed"] = s.CpuGhz is float ghz ? $"{ghz:0.00} GHz" : "--",
            ["temperature"] = s.Temperature is double t ? $"{t:0} °C" : "--",

            // The thermal zone is the processor chip, which only covers the GPU when it's built in.
            ["gpuTemperature"] = _hardware.GpuIsIntegrated && s.Temperature is double gt ? $"{gt:0} °C" : "--",
            // Windows offers no GPU sensor here; a built-in GPU gets the processor's thermal zone as a
            // reference, labeled as such (on Core Ultra it sits on its own tile, so it can differ).
            ["gpuTemperatureNote"] = _hardware.GpuIsIntegrated ? "ACPI 參考溫度，非 GPU 感測器" : "讀不到 GPU 溫度",
            ["gpuUsage"] = SystemMonitorBand.Percent(s.GpuPercent),
            ["memoryUsed"] = s.MemoryUsed is ulong memoryUsed ? Bytes(memoryUsed) : "--",
            ["memoryUsage"] = SystemMonitorBand.Percent(s.MemoryPercent),
            ["memoryTotal"] = s.MemoryTotal is ulong memoryTotal ? Bytes(memoryTotal) : "--",
            ["memoryCommitted"] = s is { MemoryCommitted: float c, MemoryCommitLimit: float limit } ? $"{Bytes(c)} / {Bytes(limit)}" : "--",
            ["memoryCached"] = s.MemoryCached is float cached ? Bytes(cached) : "--",

            // The built-in Performance Monitor's chart colors.
            ["cpuChart"] = _sampler.CpuChart.ImageUrl("rgb(57,184,227)", "rgb(57,184,227)", "rgb(0,86,110)"),
            ["gpuChart"] = _sampler.GpuChart.ImageUrl("rgb(222,104,242)", "rgb(222,104,242)", "rgb(125,0,138)"),
            ["memoryChart"] = _sampler.MemoryChart.ImageUrl("rgb(92,158,250)", "rgb(92,158,250)", "rgb(0,34,92)"),
        };

        return data.ToJsonString();
    }

    // Offers the two metrics the Dock isn't showing, under the icon of the one it is.
    private void UpdateCommands()
    {
        Icon = MonitorSettings.Icon(MonitorSettings.Metric);
        var others = Enum.GetValues<MonitorMetric>().Where(m => m != MonitorSettings.Metric);
        Commands =
        [
            .. others.Select(m => new CommandContextItem(new ShowMetricCommand(m, UpdateCommands))),
            new CommandContextItem(DockCommands.AfterDismiss(() => Process.Start(new ProcessStartInfo("taskmgr.exe") { UseShellExecute = true }), "開啟工作管理員")),
        ];
    }

    // Like Task Manager: two decimals, GB from 1 GB up.
    private static string Bytes(double bytes) =>
        bytes >= 1L << 30 ? $"{bytes / (1L << 30):0.00} GB" : $"{bytes / (1L << 20):0.00} MB";
}
