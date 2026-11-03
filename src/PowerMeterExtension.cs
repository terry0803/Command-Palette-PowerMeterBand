using System.Diagnostics;
using System.Runtime.InteropServices;
using Microsoft.CommandPalette.Extensions;

namespace PowerMeterBand;

[ComVisible(true)]
[Guid("7C1E4F2A-9B3D-4E8A-A6C5-2D1F0B9E8A47")]
[ComDefaultInterface(typeof(IExtension))]
public sealed partial class PowerMeterExtension : IExtension, IDisposable
{
    private readonly ManualResetEvent _disposedEvent;
    private readonly PowerMeterCommandsProvider _provider = new();
    private readonly Lock _lock = new();
    private Process? _host;

    public PowerMeterExtension(ManualResetEvent disposedEvent)
    {
        _disposedEvent = disposedEvent;
    }

    public object? GetProvider(ProviderType providerType)
    {
        if (providerType != ProviderType.Commands)
        {
            return null;
        }

        lock (_lock)
        {
            WatchHost();
        }

        return _provider;
    }

    // If Command Palette dies, the change handlers it registered on the bands die with it, and
    // raising them blocks the bands' update timers for good: the Dock stays frozen even after
    // Command Palette comes back and reconnects to this process. So leave with the host; the next
    // Command Palette starts a fresh copy of this extension.
    private void WatchHost()
    {
        try
        {
            // The newest Command Palette in this sign-in session (another user's can't be ours).
            var session = Process.GetCurrentProcess().SessionId;
            var candidates = Process.GetProcessesByName("Microsoft.CmdPal.UI");
            var host = candidates.Where(p => p.SessionId == session).MaxBy(StartedAt);
            foreach (var other in candidates.Where(p => p != host))
            {
                other.Dispose();
            }

            if (host is null || host.Id == _host?.Id)
            {
                host?.Dispose();
                return;
            }

            // A new host took over: stop watching the old one, whose exit no longer matters.
            if (_host is not null)
            {
                _host.Exited -= OnHostExited;
                _host.Dispose();
            }

            // Subscribe before turning the event on: a host that exits in between would otherwise go unnoticed.
            // Turning it on throws for a host that has already exited, and then no event comes at all,
            // so whatever happens there, look directly too.
            _host = host;
            host.Exited += OnHostExited;
            try
            {
                host.EnableRaisingEvents = true;
            }
            catch
            {
            }

            if (HasExited(host))
            {
                Environment.Exit(0);
            }
        }
        catch
        {
        }
    }

    // A process that's gone by the time it's asked counts as the oldest.
    private static DateTime StartedAt(Process process)
    {
        try
        {
            return process.StartTime;
        }
        catch
        {
            return DateTime.MinValue;
        }
    }

    private static bool HasExited(Process process)
    {
        try
        {
            return process.HasExited;
        }
        catch
        {
            return false; // Can't tell; the event will say.
        }
    }

    private void OnHostExited(object? sender, EventArgs e)
    {
        lock (_lock)
        {
            if (sender == _host)
            {
                Environment.Exit(0);
            }
        }
    }

    public void Dispose()
    {
        _provider.Dispose();
        _disposedEvent.Set();
    }
}
