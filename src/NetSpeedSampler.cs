using System.Diagnostics;
using System.Globalization;
using System.Runtime.InteropServices;

namespace PowerMeterBand;

internal sealed record NetAdapterInfo(string Name, ulong LinkSpeedBitsPerSecond);

internal sealed record NetSpeedSnapshot(
    double? UploadBytesPerSecond,
    double? DownloadBytesPerSecond,
    IReadOnlyList<NetAdapterInfo>? ConnectedAdapters); // Null when the adapter table couldn't be read.

/// <summary>
/// Measures upload and download speed across all physical network adapters, the same way
/// as the built-in Performance Monitor: the interface table's byte counters, skipping virtual,
/// filter and endpoint interfaces (VPNs, Hyper-V switches, Wi-Fi Direct, ...).
/// </summary>
internal sealed class NetSpeedSampler
{
    private readonly Dictionary<ulong, (ulong Received, ulong Sent)> _previous = [];
    private readonly InterfaceTable _table = new();
    private long? _lastTimestamp;

    public NetSpeedSnapshot Read()
    {
        List<InterfaceRow> rows;
        try
        {
            rows = _table.ReadPhysical();
        }
        catch
        {
            return new NetSpeedSnapshot(null, null, null);
        }

        var timestamp = Stopwatch.GetTimestamp();
        var elapsed = _lastTimestamp is long last ? Stopwatch.GetElapsedTime(last, timestamp).TotalSeconds : 0;
        _lastTimestamp = timestamp;

        double sent = 0;
        double received = 0;
        var seen = new HashSet<ulong>();
        foreach (var row in rows)
        {
            seen.Add(row.Luid);
            if (elapsed > 0 && _previous.TryGetValue(row.Luid, out var prev))
            {
                // Counters restart when an adapter is reset; skip that sample instead of going negative.
                sent += row.SentBytes >= prev.Sent ? (row.SentBytes - prev.Sent) / elapsed : 0;
                received += row.ReceivedBytes >= prev.Received ? (row.ReceivedBytes - prev.Received) / elapsed : 0;
            }

            _previous[row.Luid] = (row.ReceivedBytes, row.SentBytes);
        }

        foreach (var luid in _previous.Keys.Where(k => !seen.Contains(k)).ToList())
        {
            _previous.Remove(luid);
        }

        var connected = rows
            .Where(r => r.IsUp)
            .Select(r => new NetAdapterInfo(r.Name, r.LinkSpeed))
            .ToList();

        // The first reading has nothing to compare with.
        return elapsed > 0
            ? new NetSpeedSnapshot(sent, received, connected)
            : new NetSpeedSnapshot(null, null, connected);
    }

    private readonly record struct InterfaceRow(ulong Luid, string Name, ulong ReceivedBytes, ulong SentBytes, ulong LinkSpeed, bool IsUp);

    /// <summary>
    /// Reads MIB_IF_TABLE2 by field offset, which avoids declaring the whole 1352-byte MIB_IF_ROW2. The table
    /// lists dozens of interfaces and only a few count, so it's read every 10 seconds, or as soon as one of
    /// them fails or none is connected; in between just the ones that count are read.
    /// </summary>
    private sealed class InterfaceTable
    {
        private const long FullReadMs = 10_000;
        private const int RowSize = 1352;
        private const int TableOffset = 8; // ULONG NumEntries, then rows aligned to 8 bytes.
        private const int LuidOffset = 0;
        private const int DescriptionOffset = 542;
        private const int AliasOffset = 28;
        private const int FlagsOffset = 1152;
        private const int OperStatusOffset = 1156;
        private const int TransmitLinkSpeedOffset = 1192;
        private const int ReceiveLinkSpeedOffset = 1200;
        private const int InOctetsOffset = 1208;
        private const int OutOctetsOffset = 1280;

        private const byte HardwareInterface = 0x01;
        private const byte FilterInterface = 0x02;
        private const byte EndPointInterface = 0x80;
        private const int OperStatusUp = 1;

        [DllImport("iphlpapi.dll")]
        private static extern uint GetIfTable2(out IntPtr table);

        [DllImport("iphlpapi.dll")]
        private static extern void FreeMibTable(IntPtr memory);

        [DllImport("iphlpapi.dll")]
        private static extern uint GetIfEntry2(IntPtr row);

        // Lives as long as the extension, like the sampler that owns it.
        private readonly IntPtr _row = Marshal.AllocHGlobal(RowSize);
        private List<ulong> _physical = [];
        private long _fullReadAt;

        public List<InterfaceRow> ReadPhysical()
        {
            if (Environment.TickCount64 < _fullReadAt && ReadKnown() is { } known && known.Any(row => row.IsUp))
            {
                return known;
            }

            var rows = ReadAll();
            _physical = [.. rows.Select(row => row.Luid)];
            _fullReadAt = Environment.TickCount64 + FullReadMs;
            return rows;
        }

        // Null if one is gone.
        private List<InterfaceRow>? ReadKnown()
        {
            var rows = new List<InterfaceRow>(_physical.Count);
            foreach (var luid in _physical)
            {
                Marshal.WriteInt64(_row, LuidOffset, (long)luid);
                if (GetIfEntry2(_row) != 0)
                {
                    return null;
                }

                rows.Add(Parse(_row));
            }

            return rows;
        }

        private static List<InterfaceRow> ReadAll()
        {
            var error = GetIfTable2(out var table);
            if (error != 0)
            {
                throw new System.ComponentModel.Win32Exception((int)error);
            }

            try
            {
                var count = Marshal.ReadInt32(table);
                var rows = new List<InterfaceRow>();
                for (var i = 0; i < count; i++)
                {
                    var row = table + TableOffset + (i * RowSize);
                    var flags = Marshal.ReadByte(row, FlagsOffset);
                    if ((flags & HardwareInterface) == 0 || (flags & (FilterInterface | EndPointInterface)) != 0)
                    {
                        continue;
                    }

                    rows.Add(Parse(row));
                }

                return rows;
            }
            finally
            {
                FreeMibTable(table);
            }
        }

        private static InterfaceRow Parse(IntPtr row)
        {
            var name = Marshal.PtrToStringUni(row + DescriptionOffset);
            if (string.IsNullOrWhiteSpace(name))
            {
                name = Marshal.PtrToStringUni(row + AliasOffset) ?? string.Empty;
            }

            return new InterfaceRow(
                (ulong)Marshal.ReadInt64(row, LuidOffset),
                name,
                (ulong)Marshal.ReadInt64(row, InOctetsOffset),
                (ulong)Marshal.ReadInt64(row, OutOctetsOffset),
                KnownLinkSpeed((ulong)Marshal.ReadInt64(row, ReceiveLinkSpeedOffset), (ulong)Marshal.ReadInt64(row, TransmitLinkSpeedOffset)),
                Marshal.ReadInt32(row, OperStatusOffset) == OperStatusUp);
        }

        // ulong.MaxValue means the speed is unknown.
        private static ulong KnownLinkSpeed(ulong receive, ulong transmit) =>
            Math.Max(receive == ulong.MaxValue ? 0 : receive, transmit == ulong.MaxValue ? 0 : transmit);
    }
}

/// <summary>
/// Formats speeds exactly like the built-in Performance Monitor's default unit
/// (bits per second, 1 Kbps = 1024 bps), so both read the same.
/// </summary>
internal static class NetSpeedFormat
{
    private static readonly string[] FullUnits = [" Kbps", " Mbps", " Gbps"];

    // "Kbps" alone is wider than a whole "76%", so the Dock drops "ps".
    // The lowercase b still says bits, not bytes.
    private static readonly string[] CompactUnits = [" Kb", " Mb", " Gb"];

    // "65.1 Kbps", for the details page and tooltip.
    public static string BitsPerSecond(double? bytesPerSecond) => Format(bytesPerSecond, FullUnits);

    // "65.1 Kb", for the Dock.
    public static string CompactBitsPerSecond(double? bytesPerSecond) => Format(bytesPerSecond, CompactUnits);

    private static string Format(double? bytesPerSecond, string[] units)
    {
        if (bytesPerSecond is not double value)
        {
            return "--";
        }

        value = value * 8 / 1024;
        var unit = 0;
        while (value >= 1024 && unit < units.Length - 1)
        {
            value /= 1024;
            unit++;
        }

        return string.Format(CultureInfo.InvariantCulture, value < 100 ? "{0:0.0}{1}" : "{0:0}{1}", value, units[unit]);
    }

    public static string LinkSpeed(ulong bitsPerSecond) => bitsPerSecond switch
    {
        0 => "--",
        >= 1_000_000_000 => $"{bitsPerSecond / 1e9:0.#} Gbps",
        _ => $"{bitsPerSecond / 1e6:0} Mbps",
    };
}
