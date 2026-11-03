using Microsoft.CommandPalette.Extensions;
using Microsoft.CommandPalette.Extensions.Toolkit;

namespace PowerMeterBand;

public partial class PowerMeterCommandsProvider : CommandProvider
{
    private readonly PowerDockBand _band = new();
    private readonly NetSpeedDockBand _netBand = new();
    private readonly SystemMenuBand _systemMenu = new();
    private readonly ActiveAppBand _activeApp = new();
    private readonly SystemMonitorBand _monitor = new();
    private readonly ClockBand _clock = new();
    private readonly ICommandItem[] _commands;

    public PowerMeterCommandsProvider()
    {
        Id = "PowerMeterBand";
        DisplayName = "即時耗電功率";
        Icon = PowerDockBand.BoltIcon;
        _commands =
        [
            new CommandItem(_band.DetailsPage)
            {
                Title = "即時耗電功率",
                Subtitle = "查看整機、CPU 與電池的即時功率",
            },
            new CommandItem(_netBand.DetailsPage)
            {
                Title = "即時網速",
                Subtitle = "查看上傳與下載速度",
            },
        ];
    }

    public override ICommandItem[] TopLevelCommands() => _commands;

    public override ICommandItem[] GetDockBands() => [_band, _netBand, _systemMenu, _activeApp, _monitor, _clock];

    public override void Dispose()
    {
        _band.Dispose();
        _netBand.Dispose();
        _activeApp.Dispose();
        _monitor.Dispose();
        _clock.Dispose();
        GC.SuppressFinalize(this);
        base.Dispose();
    }
}
