using System.Runtime.InteropServices;
using Microsoft.Win32;

namespace PowerMeterBand;

/// <summary>
/// Tells whether the PC's only GPU is built into the processor, the one case where the
/// processor's thermal zone is also the GPU's temperature. Windows doesn't report "integrated"
/// directly, so it takes all three signs: one GPU, the CPU's vendor, and next to no dedicated
/// video memory (built-in GPUs report about 128 MB; graphics cards report gigabytes).
/// When in doubt it says no, and the page shows "--" rather than a wrong temperature.
/// </summary>
internal static class Dxgi
{
    private const long MaxIntegratedMemory = 512L << 20;
    private const uint SoftwareAdapter = 2; // DXGI_ADAPTER_FLAG_SOFTWARE
    private const uint MicrosoftVendor = 0x1414; // Basic Render Driver

    private static readonly Guid FactoryId = new("770aae78-f26f-4dba-a829-253c83d1b387"); // IDXGIFactory1

    private delegate int EnumAdapters1(IntPtr factory, uint index, out IntPtr adapter);

    private delegate int GetDesc1(IntPtr adapter, IntPtr desc);

    public static bool SoleGpuIsIntegrated()
    {
        try
        {
            var gpus = Adapters();
            if (gpus.Count != 1)
            {
                return false;
            }

            var cpuVendor = Registry.GetValue(@"HKEY_LOCAL_MACHINE\HARDWARE\DESCRIPTION\System\CentralProcessor\0", "VendorIdentifier", null) as string;
            var (vendor, memory) = gpus[0];
            var sameVendor = (vendor, cpuVendor) is (0x8086, "GenuineIntel") or (0x1002, "AuthenticAMD");
            return sameVendor && memory <= MaxIntegratedMemory;
        }
        catch
        {
            return false;
        }
    }

    // Hardware adapters as (PCI vendor ID, dedicated video memory).
    private static List<(uint Vendor, long DedicatedMemory)> Adapters()
    {
        var result = new List<(uint, long)>();
        var factoryId = FactoryId;
        if (CreateDXGIFactory1(ref factoryId, out var factory) < 0)
        {
            return result;
        }

        var desc = IntPtr.Zero;
        try
        {
            desc = Marshal.AllocHGlobal(512); // DXGI_ADAPTER_DESC1 is 312 bytes.

            // IDXGIFactory1::EnumAdapters1 is vtable slot 12; IDXGIAdapter1::GetDesc1 is slot 10.
            var enumAdapters = Method<EnumAdapters1>(factory, 12);
            for (uint i = 0; enumAdapters(factory, i, out var adapter) >= 0; i++)
            {
                try
                {
                    if (Method<GetDesc1>(adapter, 10)(adapter, desc) < 0)
                    {
                        continue;
                    }

                    var vendor = (uint)Marshal.ReadInt32(desc, 256);
                    var dedicated = Marshal.ReadInt64(desc, 272);
                    var flags = (uint)Marshal.ReadInt32(desc, 304);
                    if ((flags & SoftwareAdapter) == 0 && vendor != MicrosoftVendor)
                    {
                        result.Add((vendor, dedicated));
                    }
                }
                finally
                {
                    Marshal.Release(adapter);
                }
            }
        }
        finally
        {
            Marshal.FreeHGlobal(desc);
            Marshal.Release(factory);
        }

        return result;
    }

    private static T Method<T>(IntPtr comObject, int slot)
        where T : Delegate =>
        Marshal.GetDelegateForFunctionPointer<T>(Marshal.ReadIntPtr(Marshal.ReadIntPtr(comObject), slot * IntPtr.Size));

    [DllImport("dxgi.dll")]
    private static extern int CreateDXGIFactory1(ref Guid riid, out IntPtr factory);
}
