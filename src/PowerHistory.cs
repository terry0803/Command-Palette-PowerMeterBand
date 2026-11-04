using System.Globalization;
using System.Text;
using System.Text.Json;
using Windows.Storage;

namespace PowerMeterBand;

/// <summary>
/// Whole-system power over the last six hours, as five-minute averages, for the power details page.
/// It only adds up the reading the band takes every second anyway (none on mains power, where Windows
/// has no system figure), keeps 72 numbers, and saves them to a small file once a minute while there
/// are readings (the five minutes under way included), so restarting PowerToys doesn't lose the history.
/// </summary>
internal sealed class PowerHistory
{
    private const int BucketMinutes = 5;
    private const int Buckets = 72; // Six hours.
    private const int MinSecondsForAverage = 30; // Fewer readings than that (asleep, plugged in) leave a gap.
    private const int Width = 400;
    private const int Height = 80;

    private readonly Lock _lock = new();
    // Bucket number (minutes since 1970 / 5) to its average and its highest one-second reading. An average
    // of 0 means too few readings for one (a short stretch on battery), though its peak still counts.
    private readonly Dictionary<long, (float Average, float Peak)> _done = [];
    private readonly string? _file;
    private long _bucket = -1;
    private double _sum;
    private int _count;
    private double _peak;
    private long _savedAt; // Environment.TickCount64 of the last save.
    private string? _chart;
    private string? _chartKey;

    public PowerHistory()
    {
        try
        {
            _file = Path.Combine(ApplicationData.Current.LocalFolder.Path, "power-history.json");
            Load();
        }
        catch
        {
            // Not packaged, or the folder can't be read: the history just starts empty and isn't saved.
        }
    }

    private static long BucketOf(DateTime utc) => (long)(utc - DateTime.UnixEpoch).TotalMinutes / BucketMinutes;

    // Once a second, with the system power if there is one.
    public void Add(double? watts)
    {
        var bucket = BucketOf(DateTime.UtcNow);
        var save = false;
        lock (_lock)
        {
            if (bucket != _bucket)
            {
                save = Finish(bucket);
            }

            if (watts is double w && w > 0)
            {
                _sum += w;
                _count++;
                _peak = Math.Max(_peak, w);

                // The five minutes under way too, so a restart doesn't drop them (or their peak).
                save |= Environment.TickCount64 - _savedAt >= 60_000;
            }
        }

        if (save)
        {
            Save();
        }
    }

    // With the lock held: files the five minutes that ended (if they had enough readings), forgets what's
    // older than six hours, and starts the new bucket. True if there's something new to save.
    private bool Finish(long next)
    {
        var filed = false;
        if (_bucket >= 0 && _count > 0)
        {
            _done[_bucket] = (_count >= MinSecondsForAverage ? (float)(_sum / _count) : 0f, (float)_peak);
            filed = true;
        }

        foreach (var old in _done.Keys.Where(b => b <= next - Buckets).ToList())
        {
            _done.Remove(old);
        }

        // Entering a bucket already filed (the clock was turned back) starts it afresh: its old average
        // would otherwise hide the readings coming in now.
        _done.Remove(next);
        (_bucket, _sum, _count, _peak) = (next, 0, 0, 0);
        return filed;
    }

    // The chart, its scale (the dashed line's value: SVG text isn't drawn in the card), the average of the
    // five-minute averages and the highest one-second reading. The SVG is only rebuilt when it changes.
    public (string Chart, int Top, string Average, string Peak) Describe()
    {
        lock (_lock)
        {
            var values = new float?[Buckets];
            // The six hours up to now, whether or not a reading has come in yet this session.
            var first = BucketOf(DateTime.UtcNow) - Buckets + 1;
            for (var i = 0; i < Buckets; i++)
            {
                var b = first + i;
                values[i] = _done.TryGetValue(b, out var v) ? (v.Average > 0 ? v.Average : null)
                    : b == _bucket && _count >= MinSecondsForAverage ? (float)(_sum / _count)
                    : null;
            }

            // Only the six hours up to now: after the clock is turned back, saved buckets can lie "ahead".
            var last = first + Buckets - 1;
            var peaks = _done.Where(p => p.Key >= first && p.Key <= last).Select(p => p.Value.Peak)
                .Append(_count > 0 && _bucket >= first && _bucket <= last ? (float)_peak : 0f).ToList();

            var known = values.Where(v => v is not null).Select(v => v!.Value).ToList();
            var top = known.Count == 0 ? 10 : Math.Max(10, (int)Math.Ceiling(known.Max() / 5) * 5);
            // The scale is part of what's drawn: values can stay the same to one decimal while it changes.
            var key = $"{top}:" + string.Join(',', values.Select(v => v is float f ? f.ToString("0.0", CultureInfo.InvariantCulture) : "-"));
            if (key != _chartKey)
            {
                _chart = Svg(values, top);
                _chartKey = key;
            }

            var average = known.Count == 0 ? "--" : $"{known.Average():0.0} W";
            var peak = peaks.Max() is var highest and > 0 ? $"{highest:0.0} W" : "--";
            return (_chart!, top, average, peak);
        }
    }

    // Like the monitor page's charts: a line over a faint fill, oldest on the left, gaps where there's no
    // reading, and a dashed line at the top of the scale.
    private static string Svg(float?[] values, int top)
    {
        var step = (Width - 2) / (double)(Buckets - 1);
        double X(int i) => 1 + (i * step);
        double Y(float w) => Height - 1 - (Math.Clamp(w, 0, top) / top * (Height - 12));

        var shapes = new StringBuilder();
        var run = new List<(double X, double Y)>();
        for (var i = 0; i <= values.Length; i++)
        {
            if (i < values.Length && values[i] is float w)
            {
                run.Add((X(i), Y(w)));
                continue;
            }

            if (run.Count > 0)
            {
                // A lone reading still shows, as a short flat line.
                if (run.Count == 1)
                {
                    run.Add((run[0].X + (step / 2), run[0].Y));
                }

                var line = string.Join(' ', run.Select(p => string.Create(CultureInfo.InvariantCulture, $"{p.X:0.#},{p.Y:0.#}")));
                shapes.Append(CultureInfo.InvariantCulture, $"<polyline points=\"{line} {run[^1].X:0.#},{Height - 1} {run[0].X:0.#},{Height - 1}\" style=\"fill:url(#g);stroke:transparent\" />");
                shapes.Append(CultureInfo.InvariantCulture, $"<polyline points=\"{line}\" style=\"fill:none;stroke:rgb(255,185,0);stroke-width:1.5\" />");
                run.Clear();
            }
        }

        return "data:image/svg+xml;utf8," +
            $"<svg height=\"{Height}\" width=\"{Width}\">" +
            "<defs><linearGradient x1=\"0%\" x2=\"0%\" y1=\"0%\" y2=\"100%\" id=\"g\">" +
            "<stop offset=\"0%\" style=\"stop-color:rgb(255,185,0);stop-opacity:0.4\" />" +
            "<stop offset=\"95%\" style=\"stop-color:rgb(120,80,0);stop-opacity:0.2\" />" +
            "</linearGradient></defs>" +
            $"<line x1=\"1\" y1=\"11\" x2=\"{Width - 1}\" y2=\"11\" style=\"stroke:grey;stroke-width:1;stroke-dasharray:3,3\" />" +
            shapes +
            $"<rect height=\"{Height}\" width=\"{Width}\" style=\"fill:none;stroke:lightgrey;stroke-width:1\" />" +
            "</svg>";
    }

    private void Load()
    {
        if (_file is null || !File.Exists(_file))
        {
            return;
        }

        // Each bucket is [average, peak]; a plain number (the first version's format) is its own peak.
        // "current" is the bucket under way when it was saved: [bucket, sum, count, peak].
        var saved = JsonSerializer.Deserialize<Dictionary<string, JsonElement>>(File.ReadAllText(_file));
        var now = BucketOf(DateTime.UtcNow);
        var oldest = now - Buckets;
        if (saved?.Remove("current", out var current) == true && current.ValueKind == JsonValueKind.Array && current.GetArrayLength() == 4)
        {
            (_bucket, _sum, _count, _peak) = (current[0].GetInt64(), current[1].GetDouble(), current[2].GetInt32(), current[3].GetDouble());

            // Restarted within the same five minutes: carry on adding to it. Later: file it as it stood.
            if (_bucket != now)
            {
                Finish(now);
            }
        }

        foreach (var (key, value) in saved ?? [])
        {
            var (average, peak) = value.ValueKind == JsonValueKind.Array && value.GetArrayLength() == 2
                ? (value[0].GetSingle(), value[1].GetSingle())
                : value.ValueKind == JsonValueKind.Number ? (value.GetSingle(), value.GetSingle()) : (0f, 0f);
            if (long.TryParse(key, NumberStyles.None, CultureInfo.InvariantCulture, out var bucket) && bucket > oldest && bucket <= now
                && Math.Max(average, peak) > 0)
            {
                // The bucket restored from "current" (filed above) is newer than a filed copy of it.
                _done.TryAdd(bucket, (average, Math.Max(average, peak)));
            }
        }

        // The bucket under way (restored above) is the live one: a filed copy of it, saved before the clock
        // was turned back, mustn't hide the readings coming in now.
        if (_bucket == now)
        {
            _done.Remove(now);
        }
    }

    private void Save()
    {
        if (_file is null)
        {
            return;
        }

        try
        {
            string json;
            lock (_lock)
            {
                var data = _done.ToDictionary(p => p.Key.ToString(CultureInfo.InvariantCulture), p => (object)new[] { p.Value.Average, p.Value.Peak });
                if (_count > 0)
                {
                    data["current"] = new object[] { _bucket, _sum, _count, _peak };
                }

                json = JsonSerializer.Serialize(data);
                _savedAt = Environment.TickCount64;
            }

            // Written beside it and swapped in, so a crash mid-write can't leave half a file.
            var temporary = _file + ".tmp";
            File.WriteAllText(temporary, json);
            File.Move(temporary, _file, overwrite: true);
        }
        catch
        {
            // A history that can't be saved still shows for as long as the extension runs.
        }
    }
}
