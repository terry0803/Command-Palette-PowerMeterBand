using Microsoft.CommandPalette.Extensions;
using Microsoft.CommandPalette.Extensions.Toolkit;
using Windows.Storage;

namespace PowerMeterBand;

/// <summary>
/// What the Dock shows while plugged in. Whole-system power can't be measured on AC
/// (the ACPI meter only measures battery outflow), so the user picks a substitute.
/// </summary>
internal enum PluggedInDisplay
{
    ChargePower,
    CpuPower,
}

/// <summary>
/// The user's choice, kept in the package's local settings so it survives restarts.
/// </summary>
internal static class DisplaySettings
{
    private const string PluggedInKey = "PluggedInDisplay";

    private static PluggedInDisplay? _pluggedIn;

    public static PluggedInDisplay PluggedIn
    {
        get => _pluggedIn ??= Load();
        set
        {
            _pluggedIn = value;
            try
            {
                ApplicationData.Current.LocalSettings.Values[PluggedInKey] = value.ToString();
            }
            catch
            {
            }
        }
    }

    public static string Describe(PluggedInDisplay value) =>
        value == PluggedInDisplay.CpuPower ? "CPU 功率" : "充電功率";

    private static PluggedInDisplay Load()
    {
        try
        {
            if (ApplicationData.Current.LocalSettings.Values[PluggedInKey] is string s
                && Enum.TryParse(s, out PluggedInDisplay value))
            {
                return value;
            }
        }
        catch
        {
        }

        return PluggedInDisplay.ChargePower;
    }
}

/// <summary>
/// Details-page button that switches what the Dock shows while plugged in.
/// </summary>
internal sealed partial class TogglePluggedInDisplayCommand : InvokableCommand
{
    public TogglePluggedInDisplayCommand()
    {
        Id = "com.terry.powermeter.togglePluggedInDisplay";
        Icon = new IconInfo(""); // Switch
        UpdateName();
    }

    public override ICommandResult Invoke()
    {
        DisplaySettings.PluggedIn = DisplaySettings.PluggedIn == PluggedInDisplay.ChargePower
            ? PluggedInDisplay.CpuPower
            : PluggedInDisplay.ChargePower;
        UpdateName();
        return CommandResult.KeepOpen();
    }

    private void UpdateName()
    {
        var other = DisplaySettings.PluggedIn == PluggedInDisplay.ChargePower
            ? PluggedInDisplay.CpuPower
            : PluggedInDisplay.ChargePower;
        Name = $"插電時改顯示{DisplaySettings.Describe(other)}";
    }
}
