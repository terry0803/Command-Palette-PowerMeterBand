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

    public PowerMeterExtension(ManualResetEvent disposedEvent)
    {
        _disposedEvent = disposedEvent;
    }

    public object? GetProvider(ProviderType providerType) =>
        providerType == ProviderType.Commands ? _provider : null;

    public void Dispose()
    {
        _provider.Dispose();
        _disposedEvent.Set();
    }
}
