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
    // The readings behind a filed bucket, where they're known, so two records of the same five minutes
    // (one saved before, one taken since) add up instead of one hiding the other.
    private readonly Dictionary<long, (double Sum, int Count)> _sums = [];
    private readonly string? _file;
    // The file is there but couldn't be read: it isn't replaced until it has been, and its history added in.
    private bool _unread;
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
        }
        catch
        {
            // Not packaged: the history just starts empty and isn't saved.
            return;
        }

        Load();
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
            _sums[_bucket] = (_sum, _count);
            filed = true;
        }

        foreach (var old in _done.Keys.Where(b => b <= next - Buckets).ToList())
        {
            _done.Remove(old);
            _sums.Remove(old);
        }

        // Entering a bucket already filed (the clock was turned back) starts it afresh: its old average
        // would otherwise hide the readings coming in now.
        _done.Remove(next);
        _sums.Remove(next);
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

    // At the start, with nothing in yet: the same reading as a file that couldn't be read then, all of it parsed
    // before anything is added. A file held by something, or broken without a copy kept, is tried again before
    // the first save, and a half that did parse can't have been added already.
    private void Load()
    {
        _bucket = BucketOf(DateTime.UtcNow);
        _unread = true;
        CatchUp();
    }

    // A filed bucket as saved: [average, peak, seconds], [average, peak] before the seconds were kept, or
    // the first version's plain number, which is its own peak.
    private static (float Average, float Peak, int Count) Filed(JsonElement value) =>
        value.ValueKind == JsonValueKind.Array && value.GetArrayLength() is 2 or 3
            ? (value[0].GetSingle(), value[1].GetSingle(), value.GetArrayLength() == 3 ? value[2].GetInt32() : 0)
            : value.ValueKind == JsonValueKind.Number ? (value.GetSingle(), value.GetSingle(), 0) : (0f, 0f, 0);

    // With the lock held: adds readings for a filed bucket to what's already filed for it.
    private void Merge(long bucket, double sum, int count, float peak)
    {
        var (total, seconds) = _sums.TryGetValue(bucket, out var known) ? (known.Sum + sum, known.Count + count) : (sum, count);
        var top = Math.Max(peak, _done.TryGetValue(bucket, out var filed) ? filed.Peak : 0f);
        _sums[bucket] = (total, seconds);
        _done[bucket] = (seconds >= MinSecondsForAverage ? (float)(total / seconds) : 0f, top);
    }

    // A file that couldn't be read at the start, read now and added to what has come in since. False while
    // it still can't be read, so that it isn't replaced.
    private bool CatchUp()
    {
        if (!_unread || _file is null)
        {
            return true;
        }

        try
        {
            var saved = JsonSerializer.Deserialize<Dictionary<string, JsonElement>>(File.ReadAllText(_file)) ?? [];
            var now = BucketOf(DateTime.UtcNow);

            // All of it read first, nothing added yet: a file that breaks halfway must leave the history as it was,
            // or the next try would add the first half again.
            (long Bucket, double Sum, int Count, float Peak)? current = null;
            if (saved.TryGetValue("current", out var under) && under.ValueKind == JsonValueKind.Array && under.GetArrayLength() == 4)
            {
                current = (under[0].GetInt64(), under[1].GetDouble(), under[2].GetInt32(), (float)under[3].GetDouble());
            }

            var filed = new List<(long Bucket, double Sum, int Count, float Peak)>();
            foreach (var (key, value) in saved)
            {
                var (average, top, seconds) = Filed(value);
                // A filed copy of the bucket that was under way is older than "current", as when read at the start.
                if (key != "current" && long.TryParse(key, NumberStyles.None, CultureInfo.InvariantCulture, out var bucket)
                    && bucket > now - Buckets && bucket < now && Math.Max(average, top) > 0 && bucket != current?.Bucket)
                {
                    // Saved before the seconds were kept, a shown average stood for at least the minimum.
                    var known = seconds > 0 ? seconds : average > 0 ? MinSecondsForAverage : 0;
                    filed.Add((bucket, average * known, known, Math.Max(average, top)));
                }
            }

            lock (_lock)
            {
                foreach (var (bucket, sum, count, peak) in filed)
                {
                    Merge(bucket, sum, count, peak);
                }

                // The bucket that was under way then: added to this one if it's the same, filed if older.
                if (current is var (at, sum2, count2, peak2))
                {
                    if (at == _bucket)
                    {
                        (_sum, _count, _peak) = (_sum + sum2, _count + count2, Math.Max(_peak, peak2));
                    }
                    else if (at > now - Buckets && at < now && count2 > 0)
                    {
                        Merge(at, sum2, count2, peak2);
                    }
                }
            }
        }
        catch (Exception e) when (e is FileNotFoundException or DirectoryNotFoundException)
        {
            // Gone since: nothing to add, and nothing left to protect.
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException)
        {
            return false;
        }
        catch
        {
            if (!KeepBroken())
            {
                return false;
            }
        }

        _unread = false;
        return true;
    }

    // False if no copy could be kept, and then the file mustn't be replaced.
    private bool KeepBroken()
    {
        try
        {
            File.Copy(_file!, _file + ".bak", overwrite: true);
            return true;
        }
        catch
        {
            return false;
        }
    }

    private void Save()
    {
        if (_file is null || !CatchUp())
        {
            return;
        }

        try
        {
            string json;
            lock (_lock)
            {
                var data = _done.ToDictionary(
                    p => p.Key.ToString(CultureInfo.InvariantCulture),
                    p => _sums.TryGetValue(p.Key, out var known)
                        ? (object)new object[] { p.Value.Average, p.Value.Peak, known.Count }
                        : new[] { p.Value.Average, p.Value.Peak });
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
