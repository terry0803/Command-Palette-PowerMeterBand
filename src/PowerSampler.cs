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
    private const int SystemPower = 0;
    private const int CpuPower = 1;
    private const int GpuPower = 2;

    // RAPL PP1 is the integrated GPU's power plane (verified: ~0.04 W idle, up to 13 W under a WebGL load).
    private readonly CounterSet _counters = new(
        @"\Power Meter(Power Meter (0))\Power",
        @"\Energy Meter(RAPL_Package0_PKG)\Power",
        @"\Energy Meter(RAPL_Package0_PP1)\Power");

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
        DischargeAverage.Add(rate);
        var pluggedIn = IsPluggedIn();
        _counters.Collect();
        var meter = Watts(SystemPower);

        // Only a sure "on battery" makes the meter or discharge rate the whole system's draw: plugged in
        // with the battery helping out, the discharge rate is only part of it.
        double? system = pluggedIn != false
            ? null
            : meter is double w && w > 0 ? w : rate < 0 ? -rate / 1000.0 : null;

        return new PowerSnapshot(
            system,
            Watts(CpuPower),
            Watts(GpuPower),
            rate,
            report?.RemainingCapacityInMilliwattHours,
            report?.FullChargeCapacityInMilliwattHours,
            report?.Status,
            pluggedIn);
    }

    // Power Meter and Energy Meter counters report milliwatts.
    private double? Watts(int counter) => _counters.Value(counter) / 1000.0;

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

    public void Dispose() => _counters.Dispose();
}

/// <summary>
/// The battery's discharge rate averaged over the last few minutes, for estimating the time left: the
/// reported rate jumps between 8 and 15 W in normal use, which moved the estimate by hours within a minute.
/// Fed once a second. Charging isn't averaged: it stops and starts (holding at a charge limit, say), and the
/// time to full should go away the moment it does.
/// </summary>
internal static class DischargeAverage
{
    private const double TimeConstantSeconds = 180;
    private const long GapMs = 10_000; // Longer than this (sleep, say) and the old average no longer applies.

    private static readonly Lock Gate = new();
    private static double _average;
    private static int _samples;
    private static long _lastAt;

    public static void Add(int? rate) => Add(rate, Environment.TickCount64);

    internal static void Add(int? rate, long now)
    {
        lock (Gate)
        {
            if (rate is not < 0 || now - _lastAt > GapMs)
            {
                _samples = 0;
            }

            _lastAt = now;
            if (rate is int value and < 0)
            {
                // A plain average to begin with, so a spike in the first reading doesn't stick around.
                _samples++;
                _average += (value - _average) * Math.Max(1.0 / _samples, 1 / TimeConstantSeconds);
            }
        }
    }

    // The rate to estimate with: the average while discharging, otherwise the reading itself.
    public static int? For(int? rate)
    {
        lock (Gate)
        {
            return rate < 0 && _samples > 0 ? (int)Math.Round(_average) : rate;
        }
    }
}
