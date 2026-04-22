using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Drawing;
using System.Linq;
using System.Threading;
using System.Windows.Threading;
using ScottPlot;
using ScottPlot.WPF;

namespace WindowsDefenderPerformanceTool;

public sealed class Plotter : IDisposable
{
    private const int ColumnCount = 16;
    private const int MaxNamedProcesses = 4;

    private readonly WpfPlot _wpfPlot;
    private readonly IDisposable _subscription;
    private readonly DispatcherTimer _timer;

    // 1-second buckets keyed by (DateTime.Ticks / TicksPerSecond)
    private readonly ConcurrentDictionary<long, ConcurrentDictionary<string, double>> _buckets = new();

    // Timestamp tracking — local time, matching data.TimeStamp
    private long _minEventTimestampTicks = long.MaxValue;
    private long _maxEventTimestampTicks = DateTime.MinValue.Ticks;
    private long _lastEventWallArrivalTicks = DateTime.MinValue.Ticks;

    // Palette: indices 0-3 for named processes, index 4 for "Other"
    private static readonly Color[] Palette = new Color[]
    {
        Color.Blue, Color.Red, Color.Green, Color.Orange, Color.Gray
    };

    public WpfPlot WpfPlot => _wpfPlot;

    public Plotter(IObservable<EventInfo> events)
    {
        _wpfPlot = new WpfPlot();
        ConfigurePlot();

        _subscription = events.Subscribe(OnEventReceived);

        _timer = new DispatcherTimer { Interval = TimeSpan.FromSeconds(1) };
        _timer.Tick += OnTimerTick;
        _timer.Start();
    }

    private void ConfigurePlot()
    {
        _wpfPlot.Plot.Title("Windows Defender Scan Durations");
        _wpfPlot.Plot.YLabel("Duration (s)");
        _wpfPlot.Plot.SetAxisLimitsX(-0.5, ColumnCount - 0.5);
        _wpfPlot.Plot.SetAxisLimitsY(0, 10);
    }

    private void OnEventReceived(EventInfo info)
    {
        long second = info.Timestamp.Ticks / TimeSpan.TicksPerSecond;
        _buckets.GetOrAdd(second, _ => new ConcurrentDictionary<string, double>())
                .AddOrUpdate(info.Process, info.DurationMsec, (_, v) => v + info.DurationMsec);

        // CAS loop for max timestamp
        var newTicks = info.Timestamp.Ticks;
        long cur;
        do {
            cur = Interlocked.Read(ref _maxEventTimestampTicks);
            if (newTicks <= cur) break;
        } while (Interlocked.CompareExchange(ref _maxEventTimestampTicks, newTicks, cur) != cur);

        // CAS loop for min timestamp
        do {
            cur = Interlocked.Read(ref _minEventTimestampTicks);
            if (newTicks >= cur) break;
        } while (Interlocked.CompareExchange(ref _minEventTimestampTicks, newTicks, cur) != cur);

        Interlocked.Exchange(ref _lastEventWallArrivalTicks, DateTime.Now.Ticks);
    }

    private void OnTimerTick(object? sender, EventArgs e)
    {
        var maxTs   = Interlocked.Read(ref _maxEventTimestampTicks);
        var minTs   = Interlocked.Read(ref _minEventTimestampTicks);
        var arrival = Interlocked.Read(ref _lastEventWallArrivalTicks);

        if (maxTs == DateTime.MinValue.Ticks)
            return; // No events yet — chart is in initial state

        var maxTimestamp  = new DateTime(maxTs);
        var minTimestamp  = new DateTime(minTs);
        var lastArrival   = new DateTime(arrival);
        var now           = DateTime.Now;

        // Replay detection: event timestamps are historically old compared to when they arrived
        var lag = (lastArrival - maxTimestamp).TotalSeconds;
        bool isReplay = lag > 30;

        Dictionary<string, double>[] columns;
        double bucketSizeSeconds;
        long leftSecond;

        if (isReplay)
        {
            // Show entire recording duration across ColumnCount columns
            double totalSec = Math.Max(1.0, (maxTimestamp - minTimestamp).TotalSeconds);
            bucketSizeSeconds = Math.Max(1.0, totalSec / ColumnCount);
            leftSecond = minTimestamp.Ticks / TimeSpan.TicksPerSecond;

            columns = BuildColumns(leftSecond, bucketSizeSeconds);
            // Don't prune — keep all recording buckets
        }
        else
        {
            // Live: 1-second sliding window
            bucketSizeSeconds = 1.0;
            var rightSecond = now.Ticks / TimeSpan.TicksPerSecond;
            leftSecond = rightSecond - ColumnCount + 1;

            columns = BuildColumns(leftSecond, 1.0);

            // Prune buckets well outside the live window
            long cutoff = rightSecond - ColumnCount - 10;
            foreach (var key in _buckets.Keys.Where(k => k < cutoff).ToList())
                _buckets.TryRemove(key, out _);
        }

        UpdatePlot(columns, isReplay, bucketSizeSeconds);
    }

    private Dictionary<string, double>[] BuildColumns(long leftSecond, double bucketSizeSeconds)
    {
        var columns = new Dictionary<string, double>[ColumnCount];
        for (int col = 0; col < ColumnCount; col++)
        {
            long start = leftSecond + (long)(col * bucketSizeSeconds);
            long end   = leftSecond + (long)((col + 1) * bucketSizeSeconds);

            var merged = new Dictionary<string, double>();
            for (long s = start; s < end; s++)
            {
                if (_buckets.TryGetValue(s, out var b))
                {
                    foreach (var kvp in b)
                    {
                        double existing;
                        merged.TryGetValue(kvp.Key, out existing);
                        merged[kvp.Key] = existing + kvp.Value;
                    }
                }
            }

            columns[col] = merged;
        }
        return columns;
    }

    private void UpdatePlot(Dictionary<string, double>[] columns, bool isReplay,
                            double bucketSizeSeconds)
    {
        _wpfPlot.Plot.Clear();

        // Aggregate totals per process across all columns
        var totals = new Dictionary<string, double>();
        foreach (var col in columns)
        {
            foreach (var kvp in col)
            {
                double existing;
                totals.TryGetValue(kvp.Key, out existing);
                totals[kvp.Key] = existing + kvp.Value;
            }
        }

        if (totals.Count == 0)
        {
            _wpfPlot.Refresh();
            return;
        }

        var ranked   = totals.OrderByDescending(kvp => kvp.Value).ToList();
        var namedSet = new HashSet<string>(ranked.Take(MaxNamedProcesses).Select(kvp => kvp.Key));
        bool hasOther = ranked.Count > MaxNamedProcesses;

        // Build series: one per named process + optional "Other"
        int seriesCount = Math.Min(ranked.Count, MaxNamedProcesses) + (hasOther ? 1 : 0);
        double yMax = 10;

        // Compute stacked offsets per column
        var offsets = new double[ColumnCount];

        for (int rank = 0; rank < ranked.Count && rank < MaxNamedProcesses; rank++)
        {
            var proc = ranked[rank].Key;
            var values = new double[ColumnCount];
            var positions = new double[ColumnCount];
            var valueOffsets = new double[ColumnCount];

            for (int col = 0; col < ColumnCount; col++)
            {
                positions[col] = col;
                double ms;
                if (columns[col].TryGetValue(proc, out ms) && ms > 0)
                {
                    var sec = ms / 1000.0;
                    values[col] = sec;
                    valueOffsets[col] = offsets[col];
                    offsets[col] += sec;
                }
            }

            var bar = _wpfPlot.Plot.AddBar(values, positions);
            bar.FillColor = Palette[rank];
            bar.ValueOffsets = valueOffsets;
            bar.Label = ranked[rank].Key;
        }

        // "Other" series
        if (hasOther)
        {
            var values = new double[ColumnCount];
            var positions = new double[ColumnCount];
            var valueOffsets = new double[ColumnCount];

            for (int col = 0; col < ColumnCount; col++)
            {
                positions[col] = col;
                double otherMs = 0;
                foreach (var kvp in columns[col])
                {
                    if (!namedSet.Contains(kvp.Key))
                        otherMs += kvp.Value;
                }
                if (otherMs > 0)
                {
                    var sec = otherMs / 1000.0;
                    values[col] = sec;
                    valueOffsets[col] = offsets[col];
                    offsets[col] += sec;
                }
            }

            var bar = _wpfPlot.Plot.AddBar(values, positions);
            bar.FillColor = Palette[MaxNamedProcesses];
            bar.ValueOffsets = valueOffsets;
            bar.Label = "Other";
        }

        // Compute yMax from stacked totals
        for (int col = 0; col < ColumnCount; col++)
            yMax = Math.Max(yMax, offsets[col] * 1.1);

        // X-axis labels
        string[] tickLabels;
        double[] tickPositions = Enumerable.Range(0, ColumnCount).Select(i => (double)i).ToArray();

        if (isReplay)
        {
            tickLabels = Enumerable.Range(0, ColumnCount).Select(i =>
            {
                double offsetSec = i * bucketSizeSeconds;
                return bucketSizeSeconds >= 60
                    ? $"+{offsetSec / 60:F0}m"
                    : $"+{offsetSec:F0}s";
            }).ToArray();
            _wpfPlot.Plot.XLabel("Time from recording start");
        }
        else
        {
            tickLabels = Enumerable.Range(0, ColumnCount)
                .Select(i => $"-{ColumnCount - 1 - i}s")
                .ToArray();
            _wpfPlot.Plot.XLabel("Seconds ago");
        }
        _wpfPlot.Plot.XTicks(tickPositions, tickLabels);

        // Legend
        _wpfPlot.Plot.Legend(location: Alignment.UpperRight);

        _wpfPlot.Plot.SetAxisLimitsY(0, yMax);
        _wpfPlot.Refresh();
    }

    public void Reset()
    {
        _buckets.Clear();
        Interlocked.Exchange(ref _minEventTimestampTicks, long.MaxValue);
        Interlocked.Exchange(ref _maxEventTimestampTicks, DateTime.MinValue.Ticks);
        Interlocked.Exchange(ref _lastEventWallArrivalTicks, DateTime.MinValue.Ticks);
        _wpfPlot.Plot.Clear();
        _wpfPlot.Refresh();
    }

    public void Dispose()
    {
        _timer.Stop();
        _subscription.Dispose();
    }
}
