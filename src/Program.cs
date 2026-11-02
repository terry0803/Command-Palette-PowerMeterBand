using Microsoft.CommandPalette.Extensions;
using Shmuelie.WinRTServer.CsWinRT;

namespace PowerMeterBand;

public class Program
{
    [MTAThread]
    public static void Main(string[] args)
    {
        if (args.Length > 0 && args[0] == "-RegisterProcessAsComServer")
        {
            global::Shmuelie.WinRTServer.ComServer server = new();
            ManualResetEvent disposed = new(false);

            PowerMeterExtension instance = new(disposed);
            server.RegisterClass<PowerMeterExtension, IExtension>(() => instance);
            server.Start();

            disposed.WaitOne();
            server.Stop();
            server.UnsafeDispose();
        }
    }
}
