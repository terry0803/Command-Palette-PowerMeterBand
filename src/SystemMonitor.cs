using System.Diagnostics;
using System.Globalization;
using System.Management;
using System.Runtime.CompilerServices;
using System.Runtime.InteropServices;
using System.Text;
using Microsoft.CommandPalette.Extensions;
using Microsoft.CommandPalette.Extensions.Toolkit;
using Microsoft.Win32;
using Windows.Storage;
using static PowerMeterBand.Pdh;

namespace PowerMeterBand;

/// <summary>
/// Which value the combined CPU / GPU / memory band shows.
/// </summary>
internal enum MonitorMetric
{
    Cpu,
    Gpu,
    Memory,
}

/// <summary>
/// The user's choice, kept in the package's local settings so it survives restarts.
/// </summary>
internal static class MonitorSettings
{
    private const string MetricKey = "MonitorMetric";

    private static MonitorMetric? _metric;

    public static MonitorMetric Metric
    {
        get => _metric ??= Load();
        set
        {
            _metric = value;
            try
            {
                ApplicationData.Current.LocalSettings.Values[MetricKey] = value.ToString();
            }
            catch
            {
            }
        }
    }

    public static string Describe(MonitorMetric metric) => metric switch
    {
        MonitorMetric.Gpu => "GPU",
        MonitorMetric.Memory => "記憶體",
        _ => "CPU",
    };

    // The same Segoe Fluent glyphs as the built-in Performance Monitor bands.
    public static IconInfo Icon(MonitorMetric metric) => new(char.ConvertFromUtf32(metric switch
    {
        MonitorMetric.Gpu => 0xE950, // Component
        MonitorMetric.Memory => 0xE964, // Memory
        _ => 0xE9D9, // Diagnostic
    }));

    private static MonitorMetric Load()
    {
        try
        {
            if (ApplicationData.Current.LocalSettings.Values[MetricKey] is string s && Enum.TryParse(s, out MonitorMetric value))
            {
                return value;
            }
        }
        catch
        {
        }

        return MonitorMetric.Cpu;
    }
}

/// <summary>
/// Details-page button that makes the band show another metric.
/// </summary>
internal sealed partial class ShowMetricCommand : InvokableCommand
{
    private readonly MonitorMetric _metric;
    private readonly Action _changed;

    public ShowMetricCommand(MonitorMetric metric, Action changed)
    {
        _metric = metric;
        _changed = changed;
        Id = $"com.terry.powermeter.monitor.show{metric}";
        Name = $"Dock 改顯示 {MonitorSettings.Describe(metric)}";
        Icon = MonitorSettings.Icon(metric);
    }

    public override ICommandResult Invoke()
    {
        MonitorSettings.Metric = _metric;
        _changed();
        return CommandResult.KeepOpen();
    }
}

internal sealed record SystemSnapshot(
    float? CpuPercent,
    float? CpuGhz,
    float? GpuPercent,
    double? Temperature,
    ulong? MemoryTotal,
    ulong? MemoryUsed,
    float? MemoryCommitted,
    float? MemoryCommitLimit,
    float? MemoryCached)
{
    public float? MemoryPercent => MemoryTotal is > 0 && MemoryUsed is ulong used ? 100f * used / MemoryTotal.Value : null;

    public float? Percent(MonitorMetric metric) => metric switch
    {
        MonitorMetric.Gpu => GpuPercent,
        MonitorMetric.Memory => MemoryPercent,
        _ => CpuPercent,
    };
}

/// <summary>
/// Reads CPU, GPU and memory use the same way as the built-in Performance Monitor
/// (its DevHome helpers), plus the processor's ACPI thermal zone, and keeps chart history.
/// </summary>
internal sealed class SystemSampler : IDisposable
{
    private const int CpuTime = 0;
    private const int CpuPerformance = 1;
    private const int CpuFrequency = 2;
    private const int Committed = 3;
    private const int CommitLimit = 4;
    private const int Cached = 5;

    // The built-in band's CPU and memory counters, read together in one query.
    private readonly CounterSet _counters = new(
        @"\Processor Information(_Total)\% Processor Time",
        @"\Processor Information(_Total)\% Processor Performance",
        @"\Processor Information(_Total)\Processor Frequency",
        @"\Memory\Committed Bytes",
        @"\Memory\Commit Limit",
        @"\Memory\Cache Bytes");

    private readonly GpuUsage _gpu = new();

    // Found on first use and looked for again every 30 seconds until found: the thermal provider
    // can come up after this extension does.
    private RetryingCounter? _temperature;
    private long _nextZoneLookup;

    public ChartHistory CpuChart { get; } = new();

    public ChartHistory GpuChart { get; } = new();

    public ChartHistory MemoryChart { get; } = new();

    public SystemSnapshot Read()
    {
        // Unknown rather than 0 MB if Windows can't say.
        var memory = new MemoryStatus { Length = (uint)Marshal.SizeOf<MemoryStatus>() };
        var hasMemory = GlobalMemoryStatusEx(ref memory);
        ulong? total = hasMemory ? memory.TotalPhysical : null;
        ulong? used = hasMemory ? memory.TotalPhysical - memory.AvailablePhysical : null;

        // "% Processor Performance" is the frequency relative to base, so it can go past 100% while boosting.
        _counters.Collect();
        var frequency = _counters.Value(CpuFrequency);
        var performance = _counters.Value(CpuPerformance);
        var tenthsOfKelvin = ReadTemperature();

        var snapshot = new SystemSnapshot(
            _counters.Value(CpuTime) is float cpu ? Math.Clamp(cpu, 0, 100) : null,
            frequency is float mhz && performance is float percent ? mhz * percent / 100 / 1000 : null,
            _gpu.Read(),
            tenthsOfKelvin is float t and > 0 ? (t / 10.0) - 273.15 : null,
            total,
            used,
            _counters.Value(Committed),
            _counters.Value(CommitLimit),
            _counters.Value(Cached));

        CpuChart.Add(snapshot.CpuPercent);
        GpuChart.Add(snapshot.GpuPercent);
        MemoryChart.Add(snapshot.MemoryPercent);
        return snapshot;
    }

    private float? ReadTemperature()
    {
        if (_temperature is null && Environment.TickCount64 >= _nextZoneLookup)
        {
            _nextZoneLookup = Environment.TickCount64 + 30_000;
            if (ThermalZone() is string zone)
            {
                _temperature = new("Thermal Zone Information", "High Precision Temperature", zone);
            }
        }

        return _temperature?.Read();
    }

    // Only a zone that stands for the processor: TZ00 (it follows CPU load within seconds, 53 -> 67 °C
    // under load on the author's PC, while TZ01 is a slow chassis zone) or one named for the CPU. Any other
    // zone (the chassis, the battery) would put a wrong number under CPU, so then the page shows "--".
    private static string? ThermalZone()
    {
        try
        {
            var zones = new PerformanceCounterCategory("Thermal Zone Information").GetInstanceNames();
            return zones.FirstOrDefault(z => z.EndsWith("tz00", StringComparison.OrdinalIgnoreCase))
                ?? zones.FirstOrDefault(z => z.Contains("cpu", StringComparison.OrdinalIgnoreCase));
        }
        catch
        {
            return null;
        }
    }

    public void Dispose()
    {
        _counters.Dispose();
        _temperature?.Dispose();
        _gpu.Dispose();
    }

    [DllImport("kernel32.dll")]
    private static extern bool GlobalMemoryStatusEx(ref MemoryStatus status);

    [StructLayout(LayoutKind.Sequential)]
    private struct MemoryStatus
    {
        public uint Length;
        public uint MemoryLoad;
        public ulong TotalPhysical;
        public ulong AvailablePhysical;
        public ulong TotalPageFile;
        public ulong AvailablePageFile;
        public ulong TotalVirtual;
        public ulong AvailableVirtual;
        public ulong AvailableExtendedVirtual;
    }

    /// <summary>
    /// GPU use like Task Manager's headline number: the busiest engine of any type (3D, video
    /// decode, compute...), each summed over processes. The built-in band only counts 3D, which
    /// reads near 0% while, say, a video is being decoded. One PDH query on "GPU Engine(*)" stays
    /// open (it picks up processes as they come and go) and reads into a reused buffer, which
    /// costs far less each second than reading the whole category into new objects.
    /// </summary>
    private sealed unsafe class GpuUsage : IDisposable
    {
        private const uint FormatDouble = 0x200;
        private const uint FormatNoCap100 = 0x8000;
        private const uint MoreData = 0x800007D2;
        private const uint InvalidArgument = 0xC0000BBD;
        private const uint MaxBufferSize = 8u << 20; // Far more than every engine of every process needs
        private const long RetryAfterMs = 30_000;

        // Summed use per engine, keyed by the instance name from "_luid_" on. The keys (one per
        // engine of each GPU) stay between reads, so a read doesn't allocate.
        private readonly Dictionary<string, StrongBox<double>> _engines = new(StringComparer.Ordinal);
        private IntPtr _query;
        private IntPtr _counter;
        private IntPtr _buffer;
        private uint _bufferSize;
        private bool _primed;
        private long _retryAt;

        public float? Read()
        {
            if (_query == IntPtr.Zero && !Open())
            {
                return null;
            }

            if (PdhCollectQueryData(_query) != 0)
            {
                // Start over from scratch later (the GPU driver restarting, say), not every second.
                Close();
                _retryAt = Environment.TickCount64 + RetryAfterMs;
                return null;
            }

            // A rate needs two samples; the first collect only sets the baseline.
            if (!_primed)
            {
                _primed = true;
                return null;
            }

            if (!Format(out var count))
            {
                // Something's wrong with the query itself; start over in a while.
                Close();
                _retryAt = Environment.TickCount64 + RetryAfterMs;
                return null;
            }

            foreach (var sum in _engines.Values)
            {
                sum.Value = 0;
            }

            var lookup = _engines.GetAlternateLookup<ReadOnlySpan<char>>();
            var items = (Item*)_buffer;
            var withRate = 0;
            for (var i = 0; i < count; i++)
            {
                // pid_<pid>_luid_<luid>_phys_0_eng_<n>_engtype_<type>: the engine is everything after the pid.
                // A process that just appeared has no rate yet (status other than valid or new data).
                var item = items[i];
                var name = MemoryMarshal.CreateReadOnlySpanFromNullTerminated((char*)item.Name);
                var at = name.IndexOf("_luid_", StringComparison.Ordinal);
                if (item.Status > 1 || at < 0)
                {
                    continue;
                }

                withRate++;
                if (!(item.Value > 0))
                {
                    continue;
                }

                if (!lookup.TryGetValue(name[at..], out var engine))
                {
                    engine = new StrongBox<double>();
                    lookup.TryAdd(name[at..], engine);
                }

                engine.Value += item.Value;
            }

            // Nothing with a rate yet means unknown, not idle.
            if (withRate == 0)
            {
                return null;
            }

            var busiest = 0.0;
            foreach (var sum in _engines.Values)
            {
                busiest = Math.Max(busiest, sum.Value);
            }

            return (float)Math.Clamp(busiest, 0, 100);
        }

        // Fills the buffer with every instance's value. When the buffer is too small, PDH may answer "more
        // data" or just "invalid argument", and the size it gives can be out of date by the next call
        // (processes come and go), so ask for the size afresh, grow with room to spare and try a few times.
        // Without a believable size it stops there (and Read backs off) rather than keep growing.
        private bool Format(out uint count)
        {
            count = 0;
            for (var attempt = 0; attempt < 4; attempt++)
            {
                var size = _bufferSize;
                var status = PdhGetFormattedCounterArray(_counter, FormatDouble | FormatNoCap100, ref size, out count, _buffer);
                if (status == 0)
                {
                    return true;
                }

                var needed = 0u;
                if (status is not (MoreData or InvalidArgument)
                    || PdhGetFormattedCounterArray(_counter, FormatDouble | FormatNoCap100, ref needed, out _, IntPtr.Zero) != MoreData
                    || needed == 0 || needed > MaxBufferSize)
                {
                    return false;
                }

                // The new buffer comes before the old one goes, so a failed allocation leaves a valid buffer.
                var grown = Math.Min(MaxBufferSize, Math.Max(needed + (needed / 2), 16u * 1024));
                if (grown > _bufferSize)
                {
                    var buffer = Marshal.AllocHGlobal((int)grown);
                    Marshal.FreeHGlobal(_buffer);
                    (_buffer, _bufferSize) = (buffer, grown);
                }
            }

            return false;
        }

        private bool Open()
        {
            if (Environment.TickCount64 < _retryAt)
            {
                return false;
            }

            if (PdhOpenQuery(null, IntPtr.Zero, out _query) == 0
                && PdhAddEnglishCounter(_query, @"\GPU Engine(*)\Utilization Percentage", IntPtr.Zero, out _counter) == 0)
            {
                return true;
            }

            // No GPU counters (yet): don't retry every second.
            Close();
            _retryAt = Environment.TickCount64 + RetryAfterMs;
            return false;
        }

        private void Close()
        {
            if (_query != IntPtr.Zero)
            {
                PdhCloseQuery(_query);
            }

            (_query, _counter, _primed) = (IntPtr.Zero, IntPtr.Zero, false);
        }

        public void Dispose()
        {
            Close();
            Marshal.FreeHGlobal(_buffer);
            (_buffer, _bufferSize) = (IntPtr.Zero, 0);
        }

        // PDH_FMT_COUNTERVALUE_ITEM_W with a double value, laid out for 64-bit Windows.
        [StructLayout(LayoutKind.Sequential)]
        private struct Item
        {
            public IntPtr Name;
            public uint Status;
            public double Value;
        }
    }
}

/// <summary>
/// Several performance counters in one PDH query, read together once a second without allocating.
/// A counter that can't be added reads null and is added again every 30 seconds; if the query can't be
/// read at all, it's opened again after 30 seconds (counters can be missing right after sign-in). A rate
/// (like % Processor Time) needs two samples, so it reads null on the first.
/// </summary>
internal sealed class CounterSet(params string[] englishPaths) : IDisposable
{
    private const uint Format = 0x200 | 0x8000; // PDH_FMT_DOUBLE | PDH_FMT_NOCAP100
    private const long RetryAfterMs = 30_000;

    private readonly IntPtr[] _counters = new IntPtr[englishPaths.Length];
    private IntPtr _query;
    private long _retryAt;
    private long _addMissingAt;
    private bool _collected;

    public void Collect()
    {
        _collected = false;
        if (_query == IntPtr.Zero)
        {
            if (Environment.TickCount64 < _retryAt)
            {
                return;
            }

            if (PdhOpenQuery(null, IntPtr.Zero, out _query) != 0)
            {
                _query = IntPtr.Zero;
                _retryAt = Environment.TickCount64 + RetryAfterMs;
                return;
            }

            _addMissingAt = 0;
        }

        // A counter that couldn't be added (not there yet) is tried again every 30 seconds.
        if (Environment.TickCount64 >= _addMissingAt && Array.IndexOf(_counters, IntPtr.Zero) >= 0)
        {
            for (var i = 0; i < englishPaths.Length; i++)
            {
                if (_counters[i] == IntPtr.Zero && PdhAddEnglishCounter(_query, englishPaths[i], IntPtr.Zero, out var counter) == 0)
                {
                    _counters[i] = counter;
                }
            }

            _addMissingAt = Environment.TickCount64 + RetryAfterMs;
        }

        if (PdhCollectQueryData(_query) != 0)
        {
            Close();
            _retryAt = Environment.TickCount64 + RetryAfterMs;
            return;
        }

        _collected = true;
    }

    // The value from the last Collect, or null.
    public float? Value(int index) =>
        _collected && _counters[index] != IntPtr.Zero
        && PdhGetFormattedCounterValue(_counters[index], Format, out _, out var value) == 0 && value.Status <= 1 // valid or new data
            ? (float)value.Value
            : null;

    private void Close()
    {
        if (_query != IntPtr.Zero)
        {
            PdhCloseQuery(_query);
        }

        _query = IntPtr.Zero;
        Array.Clear(_counters);
        _collected = false;
    }

    public void Dispose() => Close();
}

/// <summary>
/// The PDH functions the counter readers share.
/// </summary>
internal static class Pdh
{
    [DllImport("pdh.dll", EntryPoint = "PdhOpenQueryW", CharSet = CharSet.Unicode)]
    public static extern uint PdhOpenQuery(string? dataSource, IntPtr userData, out IntPtr query);

    [DllImport("pdh.dll", EntryPoint = "PdhAddEnglishCounterW", CharSet = CharSet.Unicode)]
    public static extern uint PdhAddEnglishCounter(IntPtr query, string path, IntPtr userData, out IntPtr counter);

    [DllImport("pdh.dll")]
    public static extern uint PdhCollectQueryData(IntPtr query);

    [DllImport("pdh.dll", EntryPoint = "PdhGetFormattedCounterArrayW")]
    public static extern uint PdhGetFormattedCounterArray(IntPtr counter, uint format, ref uint bufferSize, out uint itemCount, IntPtr buffer);

    [DllImport("pdh.dll")]
    public static extern uint PdhGetFormattedCounterValue(IntPtr counter, uint format, out uint type, out CounterValue value);

    [DllImport("pdh.dll")]
    public static extern uint PdhCloseQuery(IntPtr query);

    // PDH_FMT_COUNTERVALUE with a double value, laid out for 64-bit Windows.
    [StructLayout(LayoutKind.Sequential)]
    public struct CounterValue
    {
        public uint Status;
        public double Value;
    }
}

/// <summary>
/// The last values of one metric, drawn like the built-in Performance Monitor's charts.
/// </summary>
internal sealed class ChartHistory
{
    // Smaller than the built-in 268 x 86 so all three charts fit in the Dock's flyout.
    private const int MaxValues = 34;
    private const int Height = 60;
    private const int Width = 202;
    private const int Step = 6;

    // Null where a reading failed: the chart leaves a gap there rather than drawing 0%.
    private readonly List<float?> _values = [];

    public void Add(float? value)
    {
        lock (_values)
        {
            if (_values.Count >= MaxValues)
            {
                _values.RemoveAt(0);
            }

            _values.Add(value);
        }
    }

    // An SVG data URI: a line over a gradient fill inside a light-grey box, newest value on the right.
    // Each unbroken run of readings gets its own line and fill.
    public string ImageUrl(string lineColor, string fillTop, string fillBottom)
    {
        var shapes = new StringBuilder();
        lock (_values)
        {
            var startX = 2 + ((MaxValues - _values.Count) * Step);
            var run = new StringBuilder();
            int? runStart = null;
            var runEnd = 0;
            for (var i = 0; i <= _values.Count; i++)
            {
                var x = startX + (i * Step);
                if (i < _values.Count && _values[i] is float value)
                {
                    // A full chart's oldest point also reaches back to the box's left edge.
                    if (runStart is null && x == 2)
                    {
                        run.Append(CultureInfo.InvariantCulture, $"1,{Y(value)} ");
                    }

                    runStart ??= x == 2 ? 1 : x;
                    runEnd = x;
                    run.Append(CultureInfo.InvariantCulture, $"{x},{Y(value)} ");
                }
                else if (runStart is int left)
                {
                    var line = run.ToString().TrimEnd();
                    shapes.Append(CultureInfo.InvariantCulture, $"<polyline points=\"{line} {runEnd},{Height - 1} {left},{Height - 1}\" style=\"fill:url(#gradientId);stroke:transparent\" />");
                    shapes.Append(CultureInfo.InvariantCulture, $"<polyline points=\"{line}\" style=\"fill:none;stroke:{lineColor};stroke-width:1\" />");
                    run.Clear();
                    runStart = null;
                }
            }
        }

        return "data:image/svg+xml;utf8," +
            $"<svg height=\"{Height}\" width=\"{Width}\">" +
            "<defs><linearGradient x1=\"0%\" x2=\"0%\" y1=\"0%\" y2=\"100%\" id=\"gradientId\">" +
            $"<stop offset=\"0%\" style=\"stop-color:{fillTop};stop-opacity:0.4\" />" +
            $"<stop offset=\"95%\" style=\"stop-color:{fillBottom};stop-opacity:0.25\" />" +
            "</linearGradient></defs>" +
            shapes +
            $"<rect height=\"{Height}\" width=\"{Width}\" style=\"fill:none;stroke:lightgrey;stroke-width:1\" />" +
            "</svg>";

        static double Y(float percent) => ((100 - Math.Clamp(percent, 0, 100)) * Height / 100.0) - 1;
    }
}

/// <summary>
/// What the hardware is, read once in the background (WMI takes a moment).
/// </summary>
internal sealed record HardwareInfo(string Cpu, string Gpu, string Memory, bool GpuIsIntegrated)
{
    public static readonly HardwareInfo Unknown = new("--", "--", "--", false);

    public static HardwareInfo Read() => new(ReadCpu(), ReadGpu(), ReadMemory(), Dxgi.SoleGpuIsIntegrated());

    private static string ReadCpu()
    {
        var name = Registry.GetValue(@"HKEY_LOCAL_MACHINE\HARDWARE\DESCRIPTION\System\CentralProcessor\0", "ProcessorNameString", null) as string;
        var cores = Query("SELECT NumberOfCores, NumberOfLogicalProcessors FROM Win32_Processor")
            .Select(p => (Cores: Convert.ToInt32(p.GetValueOrDefault("NumberOfCores")), Threads: Convert.ToInt32(p.GetValueOrDefault("NumberOfLogicalProcessors"))))
            .FirstOrDefault();
        var detail = cores.Cores == 0 ? string.Empty
            : cores.Cores == cores.Threads ? $"（{cores.Cores} 核心）"
            : $"（{cores.Cores} 核心 / {cores.Threads} 執行緒）";
        return Tidy(name ?? "--") + detail;
    }

    private static string ReadGpu() =>
        string.Join("、", Query("SELECT Name FROM Win32_VideoController")
            .Select(v => v.GetValueOrDefault("Name") as string)
            .Where(n => !string.IsNullOrWhiteSpace(n) && !n.Contains("Basic", StringComparison.OrdinalIgnoreCase))
            .Select(n => Tidy(n!)))
        is { Length: > 0 } names ? names : "--";

    private static string ReadMemory()
    {
        var modules = Query("SELECT Capacity, Speed, ConfiguredClockSpeed, SMBIOSMemoryType, Manufacturer FROM Win32_PhysicalMemory");
        if (modules.Count == 0)
        {
            return "--";
        }

        var gigabytes = modules.Sum(m => Convert.ToDouble(m.GetValueOrDefault("Capacity") ?? 0)) / (1L << 30);

        // The module's rating tells LPDDR5X from LPDDR5; the speed shown is what it actually runs at
        // (lower than the rating if the firmware slows it down), when Windows reports that.
        var rated = modules.Max(m => Convert.ToInt32(m.GetValueOrDefault("Speed") ?? 0));
        var configured = modules.Max(m => Convert.ToInt32(m.GetValueOrDefault("ConfiguredClockSpeed") ?? 0));
        var speed = configured > 0 ? configured : rated;
        var type = Convert.ToInt32(modules[0].GetValueOrDefault("SMBIOSMemoryType") ?? 0) switch
        {
            26 => "DDR4",
            30 => "LPDDR4",
            34 => "DDR5",
            35 => rated > 6400 ? "LPDDR5X" : "LPDDR5", // SMBIOS has no LPDDR5X type; LPDDR5 tops out at 6400 MT/s.
            _ => string.Empty,
        };
        var maker = (modules[0].GetValueOrDefault("Manufacturer") as string)?.Trim();
        var text = $"{gigabytes:0} GB {type} {(speed > 0 ? $"{speed} MT/s" : string.Empty)}".Replace("  ", " ").Trim();
        return string.IsNullOrEmpty(maker) || maker.StartsWith("Unknown", StringComparison.OrdinalIgnoreCase) ? text : $"{text}（{maker}）";
    }

    // "Intel(R) Core(TM) Ultra X7 358H" -> "Intel Core Ultra X7 358H".
    private static string Tidy(string name) => name.Replace("(R)", string.Empty).Replace("(TM)", string.Empty).Replace("  ", " ").Trim();

    // Each result's properties, copied out so the WMI objects can be released right away.
    private static List<Dictionary<string, object?>> Query(string wql)
    {
        var rows = new List<Dictionary<string, object?>>();
        try
        {
            using var searcher = new ManagementObjectSearcher(wql);
            using var results = searcher.Get();
            foreach (var result in results)
            {
                using (result)
                {
                    rows.Add(result.Properties.Cast<PropertyData>().ToDictionary(p => p.Name, p => (object?)p.Value));
                }
            }
        }
        catch
        {
        }

        return rows;
    }
}
