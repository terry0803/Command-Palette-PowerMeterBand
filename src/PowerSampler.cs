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
    bool? PluggedIn); // Null when Windows can't say

/// <summary>
/// Reads power data. Prefers the ACPI "Power Meter" counter for whole-system draw
/// and falls back to the battery discharge rate. Both only describe the whole system
/// while on battery: plugged in, the meter reads 0, and if the charger can't keep up
/// the battery only covers part of the draw, so whole-system power is left unknown.
/// </summary>
internal sealed class PowerSampler : IDisposable
{
    private readonly RetryingCounter _systemPower = new("Power Meter", "Power", "Power Meter (0)");
    private readonly RetryingCounter _cpuPower = new("Energy Meter", "Power", "RAPL_Package0_PKG");

    // RAPL PP1 is the integrated GPU's power plane (verified: ~0.04 W idle, up to 13 W under a WebGL load).
    private readonly RetryingCounter _gpuPower = new("Energy Meter", "Power", "RAPL_Package0_PP1");

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
        var pluggedIn = IsPluggedIn();
        var meter = _systemPower.ReadWatts();

        // Only a sure "on battery" makes the meter or discharge rate the whole system's draw: plugged in
        // with the battery helping out, the discharge rate is only part of it.
        double? system = pluggedIn != false
            ? null
            : meter is double w && w > 0 ? w : rate < 0 ? -rate / 1000.0 : null;

        return new PowerSnapshot(
            system,
            _cpuPower.ReadWatts(),
            _gpuPower.ReadWatts(),
            rate,
            report?.RemainingCapacityInMilliwattHours,
            report?.FullChargeCapacityInMilliwattHours,
            report?.Status,
            pluggedIn);
    }

    private static bool? IsPluggedIn()
    {
        try
        {
            return PowerManager.PowerSupplyStatus != PowerSupplyStatus.NotPresent;
        }
        catch
        {
            return null;
        }
    }

    public void Dispose()
    {
        _systemPower.Dispose();
        _cpuPower.Dispose();
        _gpuPower.Dispose();
    }
}

/// <summary>
/// A performance counter that is recreated after it fails to open or read
/// (e.g. counters not ready right after sign-in), retrying every 30 seconds.
/// </summary>
internal sealed class RetryingCounter(string category, string counter, string instance) : IDisposable
{
    // Milliseconds since boot, which setting the clock back doesn't stretch.
    private const long RetryIntervalMs = 30_000;

    private PerformanceCounter? _counter;
    private long _nextAttempt;

    // Power Meter / Energy Meter counters report milliwatts.
    public double? ReadWatts() => Read() / 1000.0;

    public float? Read()
    {
        if (_counter is null)
        {
            if (Environment.TickCount64 < _nextAttempt)
            {
                return null;
            }

            _counter = TryCreate();
            if (_counter is null)
            {
                _nextAttempt = Environment.TickCount64 + RetryIntervalMs;
                return null;
            }
        }

        try
        {
            return _counter.NextValue();
        }
        catch
        {
            _counter.Dispose();
            _counter = null;
            _nextAttempt = Environment.TickCount64 + RetryIntervalMs;
            return null;
        }
    }

    private PerformanceCounter? TryCreate()
    {
        PerformanceCounter? c = null;
        try
        {
            c = new PerformanceCounter(category, counter, instance, readOnly: true);
            c.NextValue();
            return c;
        }
        catch
        {
            c?.Dispose();
            return null;
        }
    }

    public void Dispose() => _counter?.Dispose();
}
