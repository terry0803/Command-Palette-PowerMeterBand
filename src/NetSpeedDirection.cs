using Microsoft.CommandPalette.Extensions;
using Microsoft.CommandPalette.Extensions.Toolkit;
using Windows.Storage;

namespace PowerMeterBand;

/// <summary>
/// Which speed the network Dock band shows.
/// </summary>
internal enum NetSpeedDirection
{
    Download,
    Upload,
}

/// <summary>
/// The user's choice, kept in the package's local settings so it survives restarts.
/// </summary>
internal static class NetSpeedSettings
{
    private const string DirectionKey = "NetSpeedDirection";

    private static NetSpeedDirection? _direction;

    public static NetSpeedDirection Direction
    {
        get => _direction ??= Load();
        set
        {
            _direction = value;
            try
            {
                ApplicationData.Current.LocalSettings.Values[DirectionKey] = value.ToString();
            }
            catch
            {
            }
        }
    }

    public static string Describe(NetSpeedDirection value) =>
        value == NetSpeedDirection.Upload ? "上傳" : "下載";

    public static NetSpeedDirection Other(NetSpeedDirection value) =>
        value == NetSpeedDirection.Upload ? NetSpeedDirection.Download : NetSpeedDirection.Upload;

    private static NetSpeedDirection Load()
    {
        try
        {
            if (ApplicationData.Current.LocalSettings.Values[DirectionKey] is string s
                && Enum.TryParse(s, out NetSpeedDirection value))
            {
                return value;
            }
        }
        catch
        {
        }

        return NetSpeedDirection.Download;
    }
}

/// <summary>
/// Details-page button that switches the network band between upload and download.
/// </summary>
internal sealed partial class ToggleNetSpeedDirectionCommand : InvokableCommand
{
    public ToggleNetSpeedDirectionCommand()
    {
        Id = "com.terry.powermeter.toggleNetSpeedDirection";
        Icon = new IconInfo("\uE8AB"); // Switch
        UpdateName();
    }

    public override ICommandResult Invoke()
    {
        NetSpeedSettings.Direction = NetSpeedSettings.Other(NetSpeedSettings.Direction);
        UpdateName();
        return CommandResult.KeepOpen();
    }

    private void UpdateName() =>
        Name = $"Dock 改顯示{NetSpeedSettings.Describe(NetSpeedSettings.Other(NetSpeedSettings.Direction))}";
}
