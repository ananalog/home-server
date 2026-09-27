using System.Threading.Channels;
using Home.Client;
using Home.Server.Data;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Options;

namespace Home.Server.Telemetry;

/// <summary>
/// Stores history: raw samples (batched inserts, kept RawHistoryDays) and hourly aggregates (kept forever).
/// </summary>
public sealed class HistoryService(IDbContextFactory<HomeDb> dbf, IOptions<HomeOptions> options, ILogger<HistoryService> log) : BackgroundService
{
    private const long HourMs = 3_600_000;
    private readonly Channel<SampleEntity> _queue = Channel.CreateBounded<SampleEntity>(new BoundedChannelOptions(50_000) { FullMode = BoundedChannelFullMode.DropOldest });

    public void Enqueue(SampleEntity s) => _queue.Writer.TryWrite(s);

    protected override async Task ExecuteAsync(CancellationToken ct)
    {
        var nextMaintenance = DateTime.UtcNow.AddMinutes(1);
        var batch = new List<SampleEntity>();
        while (!ct.IsCancellationRequested)
        {
            try
            {
                using var tick = new CancellationTokenSource(TimeSpan.FromSeconds(1));
                using var linked = CancellationTokenSource.CreateLinkedTokenSource(ct, tick.Token);
                try
                {
                    while (batch.Count < 1000 && await _queue.Reader.WaitToReadAsync(linked.Token))
                        while (batch.Count < 1000 && _queue.Reader.TryRead(out var s)) batch.Add(s);
                }
                catch (OperationCanceledException) when (!ct.IsCancellationRequested) { }

                if (batch.Count > 0)
                {
                    await using var db = await dbf.CreateDbContextAsync(ct);
                    db.Samples.AddRange(batch);
                    await db.SaveChangesAsync(ct);
                    batch.Clear();
                }
                if (DateTime.UtcNow >= nextMaintenance)
                {
                    await MaintainAsync(ct);
                    nextMaintenance = DateTime.UtcNow.AddMinutes(10);
                }
            }
            catch (OperationCanceledException) when (ct.IsCancellationRequested) { }
            catch (Exception e)
            {
                log.LogWarning(e, "history write failed");
                batch.Clear();
                await Task.Delay(1000, ct).ContinueWith(_ => { });
            }
        }
    }

    /// <summary>Rolls finished hours into SampleHours and deletes raw samples older than the retention.</summary>
    public async Task MaintainAsync(CancellationToken ct = default)
    {
        await using var db = await dbf.CreateDbContextAsync(ct);
        var currentHour = Clock.NowMs / HourMs * HourMs;
        var lastDone = await db.SampleHours.MaxAsync(h => (long?)h.Hour, ct) ?? 0;
        var from = lastDone == 0 ? 0 : lastDone + HourMs;
        var rows = await db.Samples.AsNoTracking()
            .Where(s => s.Ts >= from && s.Ts < currentHour)
            .Select(s => new { s.DeviceId, s.Point, s.Ts, s.Value })
            .ToListAsync(ct);
        foreach (var g in rows.GroupBy(r => (r.DeviceId, r.Point, Hour: r.Ts / HourMs * HourMs)))
        {
            var existing = await db.SampleHours.FindAsync(new object[] { g.Key.DeviceId, g.Key.Point, g.Key.Hour }, ct);
            if (existing != null) continue;
            db.SampleHours.Add(new SampleHourEntity
            {
                DeviceId = g.Key.DeviceId, Point = g.Key.Point, Hour = g.Key.Hour,
                Min = g.Min(x => x.Value), Max = g.Max(x => x.Value), Sum = g.Sum(x => x.Value), Count = g.Count(),
            });
        }
        await db.SaveChangesAsync(ct);
        var cutoff = Clock.NowMs - options.Value.RawHistoryDays * 24 * HourMs;
        await db.Samples.Where(s => s.Ts < cutoff).ExecuteDeleteAsync(ct);
    }

    /// <summary>History of one point bucketed by step (auto ≈ 300 points when step is 0).</summary>
    public async Task<HistoryDto> QueryAsync(string deviceId, int point, string key, long from, long to, long step)
    {
        if (to <= 0) to = Clock.NowMs;
        if (from <= 0 || from >= to) from = to - 24 * HourMs;
        if (step <= 0) step = Math.Max(60_000, (to - from) / 300);
        var rawFrom = Clock.NowMs - options.Value.RawHistoryDays * 24 * HourMs;
        await using var db = await dbf.CreateDbContextAsync();
        var buckets = new SortedDictionary<long, (double Min, double Max, double Sum, int Count)>();

        void Add(long t, double min, double max, double sum, int count)
        {
            var b = (t - from) / step * step + from;
            buckets[b] = buckets.TryGetValue(b, out var x)
                ? (Math.Min(x.Min, min), Math.Max(x.Max, max), x.Sum + sum, x.Count + count)
                : (min, max, sum, count);
        }

        // Hourly aggregates cover finished hours; raw samples cover the rest (and everything when step < 1h).
        long rawStart;
        if (step >= HourMs || from < rawFrom)
        {
            var hourTo = step >= HourMs ? to : rawFrom;
            var hours = await db.SampleHours.AsNoTracking()
                .Where(h => h.DeviceId == deviceId && h.Point == point && h.Hour >= from / HourMs * HourMs && h.Hour < hourTo)
                .ToListAsync();
            foreach (var h in hours) Add(Math.Max(h.Hour, from), h.Min, h.Max, h.Sum, h.Count);
            var lastHourEnd = hours.Count > 0 ? hours.Max(h => h.Hour) + HourMs : from;
            rawStart = step >= HourMs ? Math.Max(lastHourEnd, rawFrom) : rawFrom;
        }
        else rawStart = from;
        rawStart = Math.Max(rawStart, from);
        var raw = await db.Samples.AsNoTracking()
            .Where(s => s.DeviceId == deviceId && s.Point == point && s.Ts >= rawStart && s.Ts < to)
            .Select(s => new { s.Ts, s.Value })
            .ToListAsync();
        foreach (var r in raw) Add(r.Ts, r.Value, r.Value, r.Value, 1);

        var points = buckets.Select(b => new HistoryPoint(b.Key, Math.Round(b.Value.Min, 3), Math.Round(b.Value.Max, 3),
            Math.Round(b.Value.Sum / b.Value.Count, 3))).ToList();
        return new HistoryDto(key, from, to, step, points);
    }
}
