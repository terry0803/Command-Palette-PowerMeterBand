using Microsoft.CommandPalette.Extensions;
using Microsoft.CommandPalette.Extensions.Toolkit;

namespace PowerMeterBand;

public partial class PowerMeterCommandsProvider : CommandProvider
{
    private readonly PowerDockBand _band = new();
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
        ];
    }

    public override ICommandItem[] TopLevelCommands() => _commands;

    public override ICommandItem[] GetDockBands() => [_band];

    public override void Dispose()
    {
        _band.Dispose();
        GC.SuppressFinalize(this);
        base.Dispose();
    }
}
