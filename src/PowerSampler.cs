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
        double? system = pluggedIn
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

    public void Dispose()
    {
        _systemPower.Dispose();
        _cpuPower.Dispose();
        _gpuPower.Dispose();
    }

    /// <summary>
    /// A performance counter that is recreated after it fails to open or read
    /// (e.g. counters not ready right after sign-in), retrying every 30 seconds.
    /// </summary>
    private sealed class RetryingCounter(string category, string counter, string instance) : IDisposable
    {
        private static readonly TimeSpan RetryInterval = TimeSpan.FromSeconds(30);

        private PerformanceCounter? _counter;
        private DateTime _nextAttempt = DateTime.MinValue;

        // Power Meter / Energy Meter counters report milliwatts.
        public double? ReadWatts()
        {
            if (_counter is null)
            {
                if (DateTime.UtcNow < _nextAttempt)
                {
                    return null;
                }

                _counter = TryCreate();
                if (_counter is null)
                {
                    _nextAttempt = DateTime.UtcNow + RetryInterval;
                    return null;
                }
            }

            try
            {
                return _counter.NextValue() / 1000.0;
            }
            catch
            {
                _counter.Dispose();
                _counter = null;
                _nextAttempt = DateTime.UtcNow + RetryInterval;
                return null;
            }
        }

        private PerformanceCounter? TryCreate()
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

        public void Dispose() => _counter?.Dispose();
    }
}
