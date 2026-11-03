using Microsoft.CommandPalette.Extensions.Toolkit;

namespace PowerMeterBand;

/// <summary>
/// Dock band showing either upload or download speed (the user picks on the details page),
/// refreshed every second. Clicking it opens <see cref="NetSpeedDetailsPage"/>.
/// </summary>
internal sealed partial class NetSpeedDockBand : WrappedDockItem, IDisposable
{
    // Arrows framed in a rounded square, like the bolt, instead of the built-in bare arrows.
    public static readonly IconInfo UploadIcon = new(
        IconHelpers.FromRelativePath("Assets\\arrow-up-light.svg").Light,
        IconHelpers.FromRelativePath("Assets\\arrow-up-dark.svg").Dark);

    public static readonly IconInfo DownloadIcon = new(
        IconHelpers.FromRelativePath("Assets\\arrow-down-light.svg").Light,
        IconHelpers.FromRelativePath("Assets\\arrow-down-dark.svg").Dark);

    // Fits the widest value ("1023 Kbps", "99.9 Mbps") and leaves a typical "xx.x Kbps" the same
    // 43px (at 200% scale) trailing gap as the other bands. The built-in "10ch" left ~57px.
    private const string LabelWidth = "text:000.0 Kbps";

    private readonly DockValueListItem _item;
    private readonly NetSpeedSampler _sampler = new();
    private readonly Timer _timer;
    private readonly Lock _lock = new();
    private NetSpeedDirection? _shownDirection;
    private bool _disposed;

    public NetSpeedDetailsPage DetailsPage { get; } = new();

    public NetSpeedDockBand()
        : base([], "com.terry.powermeter.netspeed", "即時網速")
    {
        Icon = DownloadIcon;
        _item = new DockValueListItem(DetailsPage, LabelWidth)
        {
            Title = "--",
        };
        Items = [_item];

        _timer = new Timer(Update, null, TimeSpan.Zero, TimeSpan.FromSeconds(1));
    }

    public static IconInfo IconFor(NetSpeedDirection direction) =>
        direction == NetSpeedDirection.Upload ? UploadIcon : DownloadIcon;

    private void Update(object? state)
    {
        lock (_lock)
        {
            if (_disposed)
            {
                return;
            }

            var s = _sampler.Read();
            var direction = NetSpeedSettings.Direction;
            var other = NetSpeedSettings.Other(direction);

            // Only swap the icon when the choice changes, so the host doesn't redraw it every second.
            if (_shownDirection != direction)
            {
                _item.Icon = IconFor(direction);
                _shownDirection = direction;
            }

            _item.Title = NetSpeedFormat.BitsPerSecond(Pick(s, direction));
            _item.Subtitle = $"{NetSpeedSettings.Describe(direction)} · {NetSpeedSettings.Describe(other)} {NetSpeedFormat.BitsPerSecond(Pick(s, other))}";
            DetailsPage.Update(s);
        }
    }

    private static double? Pick(NetSpeedSnapshot s, NetSpeedDirection direction) =>
        direction == NetSpeedDirection.Upload ? s.UploadBytesPerSecond : s.DownloadBytesPerSecond;

    public void Dispose()
    {
        lock (_lock)
        {
            _disposed = true;
            _timer.Dispose();
        }
    }
}
