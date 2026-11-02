using System.Diagnostics;
using Windows.Devices.Power;
using Windows.System.Power;

namespace PowerMeterBand;

internal sealed record PowerSnapshot(
    double? SystemWatts,
    double? CpuWatts,
    int? BatteryRateMilliwatts,
    int? RemainingMilliwattHours,
    int? FullChargeMilliwattHours,
    BatteryStatus? BatteryStatus);

/// <summary>
/// Reads power data. Prefers the ACPI "Power Meter" counter for whole-system draw
/// (works on AC and battery) and falls back to the battery discharge rate.
/// </summary>
internal sealed class PowerSampler : IDisposable
{
    private PerformanceCounter? _systemPower = TryCounter("Power Meter", "Power", "Power Meter (0)");
    private PerformanceCounter? _cpuPower = TryCounter("Energy Meter", "Power", "RAPL_Package0_PKG");

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
            rate,
            report?.RemainingCapacityInMilliwattHours,
            report?.FullChargeCapacityInMilliwattHours,
            report?.Status);
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
    }
}
