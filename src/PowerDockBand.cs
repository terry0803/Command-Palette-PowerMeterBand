using Microsoft.CommandPalette.Extensions.Toolkit;

namespace PowerMeterBand;

/// <summary>
/// Dock band showing whole-system power draw, refreshed every second.
/// Clicking it opens <see cref="PowerDetailsPage"/>.
/// </summary>
internal sealed partial class PowerDockBand : WrappedDockItem, IDisposable
{
    // The Dock gives every icon a fixed 16-DIP box. A bare bolt is too narrow for it and
    // leaves uneven gaps, so the SVG frames the bolt in a rounded square like the
    // Performance Monitor's CPU icon. The Fluent LightningBolt glyph looked too large.
    public static readonly IconInfo BoltIcon = new(
        IconHelpers.FromRelativePath("Assets\\bolt-light.svg").Light,
        IconHelpers.FromRelativePath("Assets\\bolt-dark.svg").Dark);

    // Fixed label slot sized so a two-digit value ("xx.x W") leaves the same 43px
    // (at 200% scale) trailing gap as the built-in Performance Monitor bands.
    private const double LabelWidthDips = 42.5;

    private readonly DockValueListItem _item;
    private readonly PowerSampler _sampler = new();
    private readonly Lock _lock = new();
    private bool _disposed;

    public PowerDetailsPage DetailsPage { get; } = new();

    public PowerDockBand()
        : base([], "com.terry.powermeter.band", "即時耗電功率")
    {
        Icon = BoltIcon;
        _item = new DockValueListItem(DetailsPage, LabelWidthDips)
        {
            Title = "-- W",
            Icon = BoltIcon,
        };
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

            // Plugged in, whole-system power can't be measured, so show what the user picked.
            // The battery flow is negative when the charger can't keep up and the battery helps.
            var pluggedIn = s.PluggedIn == true;
            var showCpu = pluggedIn && DisplaySettings.PluggedIn == PluggedInDisplay.CpuPower;
            var showFlow = pluggedIn && !showCpu && s.BatteryRateMilliwatts is not null;
            var flow = s.BatteryRateMilliwatts ?? 0;
            _item.Title = showCpu
                ? s.CpuWatts is double c ? $"{c:0.0} W" : "-- W"
                : showFlow
                    ? $"{(flow >= 0 ? "+" : "-")}{Math.Abs(flow) / 1000.0:0} W" // No decimal: "+xx.x W" overflows the fixed label slot.
                    : s.SystemWatts is double w ? $"{w:0.0} W" : "-- W";

            var parts = new List<string>();
            if (pluggedIn)
            {
                parts.Add(showCpu ? "插電中（顯示 CPU 功率）" : flow < 0 ? "插電中（電池補電）" : "插電中");
            }
            else if (s.PluggedIn is null)
            {
                parts.Add("電源狀態不明");
            }

            if (!showCpu && s.CpuWatts is double cpu)
            {
                parts.Add($"CPU {cpu:0.0} W");
            }

            // When showing the battery flow, the title already has the charge rate.
            if (!showFlow && s.BatteryRateMilliwatts is int rate && rate != 0)
            {
                parts.Add(rate > 0 ? $"充電 {rate / 1000.0:0.0} W" : $"放電 {-rate / 1000.0:0.0} W");
            }

            _item.Subtitle = string.Join(" · ", parts);
            DetailsPage.Update(s);
        }
    }

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
