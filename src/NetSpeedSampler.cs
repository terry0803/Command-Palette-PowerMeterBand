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
/// as the built-in Performance Monitor: byte counters from GetIfTable2, skipping virtual,
/// filter and endpoint interfaces (VPNs, Hyper-V switches, Wi-Fi Direct, ...).
/// </summary>
internal sealed class NetSpeedSampler
{
    private readonly Dictionary<ulong, (ulong Received, ulong Sent)> _previous = [];
    private long? _lastTimestamp;

    public NetSpeedSnapshot Read()
    {
        List<InterfaceRow> rows;
        try
        {
            rows = InterfaceTable.ReadPhysical();
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
    /// Reads MIB_IF_TABLE2 by field offset, which avoids declaring the whole 1352-byte MIB_IF_ROW2.
    /// </summary>
    private static class InterfaceTable
    {
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

        public static List<InterfaceRow> ReadPhysical()
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

                    var name = Marshal.PtrToStringUni(row + DescriptionOffset);
                    if (string.IsNullOrWhiteSpace(name))
                    {
                        name = Marshal.PtrToStringUni(row + AliasOffset) ?? string.Empty;
                    }

                    rows.Add(new InterfaceRow(
                        (ulong)Marshal.ReadInt64(row, LuidOffset),
                        name,
                        (ulong)Marshal.ReadInt64(row, InOctetsOffset),
                        (ulong)Marshal.ReadInt64(row, OutOctetsOffset),
                        KnownLinkSpeed((ulong)Marshal.ReadInt64(row, ReceiveLinkSpeedOffset), (ulong)Marshal.ReadInt64(row, TransmitLinkSpeedOffset)),
                        Marshal.ReadInt32(row, OperStatusOffset) == OperStatusUp));
                }

                return rows;
            }
            finally
            {
                FreeMibTable(table);
            }
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
    public static string BitsPerSecond(double? bytesPerSecond)
    {
        if (bytesPerSecond is not double value)
        {
            return "--";
        }

        value = value * 8 / 1024;
        foreach (var unit in new[] { "Kbps", "Mbps" })
        {
            if (value < 1024)
            {
                return Format(value, unit);
            }

            value /= 1024;
        }

        return Format(value, "Gbps");
    }

    public static string LinkSpeed(ulong bitsPerSecond) => bitsPerSecond switch
    {
        0 => "--",
        >= 1_000_000_000 => $"{bitsPerSecond / 1e9:0.#} Gbps",
        _ => $"{bitsPerSecond / 1e6:0} Mbps",
    };

    private static string Format(double value, string unit) =>
        string.Format(CultureInfo.InvariantCulture, value < 100 ? "{0:0.0} {1}" : "{0:0} {1}", value, unit);
}
