using Microsoft.CommandPalette.Extensions;
using Microsoft.CommandPalette.Extensions.Toolkit;

namespace PowerMeterBand;

/// <summary>
/// A details page whose card follows live data. The Dock subscribes to a card each time it opens the page and
/// doesn't let go when the page closes, and it redraws the card for every subscription whenever its data
/// changes: measured, 20 openings of one page written every second cost it about 500 ms of CPU a second, for
/// as long as it ran. So each opening gets a new card, and a card is only written while Command Palette has
/// the foreground, which it takes to show a page (nothing tells the extension a page has closed: ItemsChanged
/// stays subscribed, and fetching the page again to check redraws the card, so the charts flicker).
/// </summary>
internal abstract partial class LiveCardPage(string template) : ContentPage
{
    // Guards the fields only. A card is never written with it held: writing calls into the Dock.
    private readonly Lock _lock = new();
    private FormContent? _card;
    private string _data = "{}";
    private string? _shown;

    public override IContent[] GetContent()
    {
        lock (_lock)
        {
            _card = new FormContent { TemplateJson = template, DataJson = _data };
            _shown = _data;
            return [_card];
        }
    }

    // The data to show, kept for the next opening when the page isn't up.
    protected void Show(string data)
    {
        FormContent card;
        lock (_lock)
        {
            _data = data;

            // Unchanged data is skipped so the Dock doesn't redraw the card for nothing.
            if (_card is null || data == _shown || !CommandPalette.HasForeground())
            {
                return;
            }

            card = _card;
            _shown = data;
        }

        card.DataJson = data;
    }
}

/// <summary>
/// Whether Command Palette (its Dock or its window) is the program in front.
/// </summary>
internal static class CommandPalette
{
    private static readonly Lock Gate = new();
    private static uint _lastProcess;
    private static bool _lastWasPalette;

    public static bool HasForeground()
    {
        Native.GetWindowThreadProcessId(Native.GetForegroundWindow(), out var pid);
        lock (Gate)
        {
            if (pid != _lastProcess)
            {
                _lastProcess = pid;
                _lastWasPalette = Path.GetFileName(Native.ProcessPath(pid))?.Equals("Microsoft.CmdPal.UI.exe", StringComparison.OrdinalIgnoreCase) == true;
            }

            return _lastWasPalette;
        }
    }
}
