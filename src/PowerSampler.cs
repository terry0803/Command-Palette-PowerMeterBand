using System.Diagnostics;
using Windows.Devices.Power;
using Windows.System.Power;

namespace PowerMeterBand;

internal sealed record PowerSnapshot(
    double? SystemWatts,
    double? CpuWatts,
    double? GpuWatts,
    int? BatteryRateMilliwatts,
    int? RemainingMilliwattHours,
    int? FullChargeMilliwattHours,
    BatteryStatus? BatteryStatus,
    bool PluggedIn);

/// <summary>
/// Reads power data. Prefers the ACPI "Power Meter" counter for whole-system draw
/// and falls back to the battery discharge rate. The meter only reports while on
/// battery; plugged in it reads 0 and nothing measures whole-system draw.
/// </summary>
internal sealed class PowerSampler : IDisposable
{
    private PerformanceCounter? _systemPower = TryCounter("Power Meter", "Power", "Power Meter (0)");
    private PerformanceCounter? _cpuPower = TryCounter("Energy Meter", "Power", "RAPL_Package0_PKG");

    // RAPL PP1 is the integrated GPU's power plane (verified: ~0.04 W idle, up to 13 W under a WebGL load).
    private PerformanceCounter? _gpuPower = TryCounter("Energy Meter", "Power", "RAPL_Package0_PP1");

    public PowerSnapshot Read()
    {
        BatteryReport? report = null;
        try
        {
            report = Battery.AggregateBattery.GetReport();
        }
        catch
        {
        }

        var rate = report?.ChargeRateInMilliwatts;
        var system = ReadCounterWatts(ref _systemPower) is double w && w > 0
            ? w
            : rate < 0 ? -rate / 1000.0 : null;

        return new PowerSnapshot(
            system,
            ReadCounterWatts(ref _cpuPower),
            ReadCounterWatts(ref _gpuPower),
            rate,
            report?.RemainingCapacityInMilliwattHours,
            report?.FullChargeCapacityInMilliwattHours,
            report?.Status,
            IsPluggedIn());
    }

    private static bool IsPluggedIn()
    {
        try
        {
            return PowerManager.PowerSupplyStatus != PowerSupplyStatus.NotPresent;
        }
        catch
        {
            return false;
        }
    }

    private static PerformanceCounter? TryCounter(string category, string counter, string instance)
    {
        try
        {
            var c = new PerformanceCounter(category, counter, instance, readOnly: true);
            c.NextValue();
            return c;
        }
        catch
        {
            return null;
        }
    }

    // Power Meter / Energy Meter counters report milliwatts.
    private static double? ReadCounterWatts(ref PerformanceCounter? counter)
    {
        if (counter is null)
        {
            return null;
        }

        try
        {
            return counter.NextValue() / 1000.0;
        }
        catch
        {
            counter.Dispose();
            counter = null;
            return null;
        }
    }

    public void Dispose()
    {
        _systemPower?.Dispose();
        _cpuPower?.Dispose();
        _gpuPower?.Dispose();
    }
}
