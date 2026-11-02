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

    // Fixed label slot sized so a two-digit value ("xx.x W") leaves a 44px
    // (at 200% scale) trailing gap, 1px more than the built-in Performance Monitor bands.
    private const double LabelWidthDips = 43;

    private readonly DockValueListItem _item;
    private readonly PowerSampler _sampler = new();
    private readonly Timer _timer;
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

        _timer = new Timer(Update, null, TimeSpan.Zero, TimeSpan.FromSeconds(1));
    }

    private void Update(object? state)
    {
        lock (_lock)
        {
            if (_disposed)
            {
                return;
            }

            var s = _sampler.Read();
            _item.Title = s.SystemWatts is double w ? $"{w:0.0} W" : "-- W";

            var parts = new List<string>();
            if (s.CpuWatts is double cpu)
            {
                parts.Add($"CPU {cpu:0.0} W");
            }

            if (s.BatteryRateMilliwatts is int rate && rate != 0)
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
            _timer.Dispose();
            _sampler.Dispose();
        }
    }
}
