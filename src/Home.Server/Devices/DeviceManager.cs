using System.Collections.Concurrent;
using System.Text.Json;
using System.Text.Json.Nodes;
using Home.Client;
using Home.Protocol;
using Home.Server.Data;
using Home.Server.Live;
using Home.Server.Telemetry;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Options;

namespace Home.Server.Devices;

/// <summary>Live state of a device that is not stored in the database.</summary>
public sealed class DeviceRuntime(string id)
{
    public string Id { get; } = id;
    public DeviceSession? Session { get; set; }
    public List<PointDto> Points { get; set; } = new();
    public ConcurrentDictionary<int, ValueDto> Values { get; } = new();
    public ConcurrentQueue<LogLineDto> Logs { get; } = new();
    public OtaProgressDto? Ota { get; set; }
    public bool PendingVerify { get; set; }
    public bool OfflineAlerted { get; set; }
    public bool Online => Session is { IsClosed: false };
}

/// <summary>Registry of devices: handshake, live values, commands, persistence.</summary>
public sealed class DeviceManager(
    IDbContextFactory<HomeDb> dbf,
    EventBus bus,
    EventLog events,
    HistoryService history,
    INotifier notifier,
    IOptions<HomeOptions> options,
    ILogger<DeviceManager> log)
{
    private const int MaxLogLines = 300;
    private readonly ConcurrentDictionary<string, DeviceRuntime> _rt = new();
    private readonly ConcurrentDictionary<string, List<TaskCompletionSource<HelloReq>>> _helloWaiters = new();

    public DeviceRuntime Runtime(string id) => _rt.GetOrAdd(id, i => new DeviceRuntime(i));
    public IEnumerable<DeviceRuntime> Runtimes => _rt.Values;

    // ------------------------------------------------------------------ handshake

    public async Task<HelloResp> OnHelloAsync(DeviceSession session, HelloReq hello)
    {
        var id = (hello.DeviceId ?? "").Trim().ToLowerInvariant();
        if (id.Length is < 4 or > 12 || !id.All(Uri.IsHexDigit))
            return new HelloResp { State = AdoptState.Blocked, TimeMs = (ulong)Clock.NowMs };

        await using var db = await dbf.CreateDbContextAsync();
        var dev = await db.Devices.FindAsync(id);
        var isNew = dev == null;
        if (dev == null)
        {
            dev = new DeviceEntity
            {
                Id = id,
                State = DeviceState.New,
                FirstSeen = Clock.NowMs,
                Name = string.IsNullOrWhiteSpace(hello.Name) ? $"{ProtoMap.ModelKey(hello.Model is { } m ? (int)m : 0)}-{id[^4..]}" : hello.Name!,
            };
            db.Devices.Add(dev);
        }
        dev.Model = (int)(hello.Model ?? Model.Unknown);
        dev.HwRev = hello.HwRev ?? 0;
        dev.FwVersion = hello.FwVersion;
        dev.BootPartition = ProtoMap.BootKey(hello.BootPartition);
        dev.IdfVersion = hello.IdfVersion;
        dev.Ip = session.RemoteIp;
        dev.LastSeen = Clock.NowMs;
        await db.SaveChangesAsync();

        session.DeviceId = id;
        session.Hello = hello;
        var rt = Runtime(id);
        var old = rt.Session;
        rt.Session = session;
        rt.PendingVerify = hello.PendingVerify ?? false;
        if (old != null && old != session) old.Abort();
        if (rt.Points.Count == 0 && dev.PointsJson != null) rt.Points = ParsePoints(dev.PointsJson);

        log.LogInformation("device {Id} ({Model} {Fw}) online from {Ip}, state {State}", id, ProtoMap.ModelKey(dev.Model), hello.FwVersion, session.RemoteIp, dev.State);
        if (isNew)
        {
            await events.AddAsync(id, "new-device", $"Найдено новое устройство {dev.Name} ({ProtoMap.ModelKey(dev.Model)}, {session.RemoteIp})");
            notifier.Notify($"🆕 Найдено новое устройство <b>{dev.Name}</b> ({ProtoMap.ModelKey(dev.Model)}). Примите его в приложении.", adminsOnly: true);
        }
        else if (rt.OfflineAlerted && dev.State == DeviceState.Adopted)
        {
            rt.OfflineAlerted = false;
            notifier.Notify($"✅ {dev.Name} снова в сети");
        }

        if (_helloWaiters.TryRemove(id, out var waiters))
            foreach (var w in waiters) w.TrySetResult(hello);

        bus.Publish("device", ToDto(dev, rt));
        return new HelloResp
        {
            TimeMs = (ulong)Clock.NowMs,
            State = dev.State switch
            {
                DeviceState.Adopted => AdoptState.Adopted,
                DeviceState.Blocked => AdoptState.Blocked,
                _ => AdoptState.New,
            },
            LogLevel = Home.Protocol.LogLevel.Info,
        };
    }

    /// <summary>After the HELLO answer: fetch the description and the current values in the background.</summary>
    public void AfterHello(DeviceSession session) => _ = Task.Run(async () =>
    {
        try
        {
            await RefreshDescriptionAsync(session);
        }
        catch (Exception e)
        {
            log.LogWarning("cannot describe {Id}: {Error}", session.DeviceId, e.Message);
        }
    });

    private async Task RefreshDescriptionAsync(DeviceSession session)
    {
        var id = session.DeviceId!;
        var rt = Runtime(id);
        rt.PendingVerify = false; // the device confirms its new firmware once it got the HELLO answer
        var desc = await session.RequestAsync<DescribeResp>(new DescribeReq(), TimeSpan.FromSeconds(15));
        rt.Points = desc.Points.Select(ProtoMap.ToDto).ToList();
        await using (var db = await dbf.CreateDbContextAsync())
        {
            var dev = await db.Devices.FindAsync(id);
            if (dev != null)
            {
                dev.PointsJson = JsonSerializer.Serialize(rt.Points, HomeJsonExt.PointList);
                dev.PointsFw = session.Hello?.FwVersion;
                await db.SaveChangesAsync();
            }
        }
        var values = await session.RequestAsync<GetResp>(new GetReq(), TimeSpan.FromSeconds(10));
        ApplySamples(id, values.Samples, fromReport: false);
        await PublishDeviceAsync(id);
    }

    public async Task OnDisconnectedAsync(DeviceSession session)
    {
        if (session.DeviceId is not { } id) return;
        var rt = Runtime(id);
        if (rt.Session != session) return; // replaced by a newer connection
        rt.Session = null;
        log.LogInformation("device {Id} offline", id);
        await using var db = await dbf.CreateDbContextAsync();
        var dev = await db.Devices.FindAsync(id);
        if (dev != null)
        {
            dev.LastSeen = Clock.NowMs;
            await db.SaveChangesAsync();
        }
        bus.Publish("online", new OnlineEvent(id, false));
    }

    public Task<HelloReq> WaitForHelloAsync(string id, TimeSpan timeout, CancellationToken ct)
    {
        var tcs = new TaskCompletionSource<HelloReq>(TaskCreationOptions.RunContinuationsAsynchronously);
        _helloWaiters.AddOrUpdate(id, _ => new() { tcs }, (_, l) => { lock (l) l.Add(tcs); return l; });
        return tcs.Task.WaitAsync(timeout, ct);
    }

    // ------------------------------------------------------------------ messages from devices

    public void OnDeviceMessage(DeviceSession session, IProtoBody body)
    {
        var id = session.DeviceId!;
        switch (body)
        {
            case StateReq s:
                ApplySamples(id, s.Samples, fromReport: false);
                break;
            case ReportReq r:
                ApplySamples(id, r.Samples, fromReport: true);
                break;
            case EventReq e:
                _ = HandleEventAsync(id, e);
                break;
            case LogReq l:
                var line = new LogLineDto((long)(l.TsMs ?? (ulong)Clock.NowMs), ProtoMap.LogLevelKey(l.Level), l.Tag ?? "", l.Text ?? "");
                var rt = Runtime(id);
                rt.Logs.Enqueue(line);
                while (rt.Logs.Count > MaxLogLines && rt.Logs.TryDequeue(out _)) { }
                bus.PublishLog(id, line);
                break;
        }
    }

    private void ApplySamples(string id, List<Sample> samples, bool fromReport)
    {
        if (samples.Count == 0) return;
        var rt = Runtime(id);
        var adopted = IsAdoptedCached(id);
        var changed = new Dictionary<string, ValueDto>();
        foreach (var s in samples)
        {
            if (s.Point is not { } pid) continue;
            var ts = s.TsMs is { } t and > 0 ? (long)t : Clock.NowMs;
            var dto = new ValueDto(ProtoMap.ToJson(s.Value), ts);
            rt.Values[pid] = dto;
            var point = rt.Points.FirstOrDefault(p => p.Id == pid);
            changed[point?.Key ?? $"p{pid}"] = dto;
            if (adopted && point is { History: true } && ProtoMap.ToNumber(s.Value) is { } num)
                history.Enqueue(new SampleEntity { DeviceId = id, Point = pid, Ts = ts, Value = num });
        }
        bus.Publish("values", new ValuesEvent(id, changed));
    }

    private readonly ConcurrentDictionary<string, DeviceState> _stateCache = new();

    private bool IsAdoptedCached(string id)
    {
        if (_stateCache.TryGetValue(id, out var st)) return st == DeviceState.Adopted;
        using var db = dbf.CreateDbContext();
        var s = db.Devices.Where(d => d.Id == id).Select(d => (DeviceState?)d.State).FirstOrDefault() ?? DeviceState.New;
        _stateCache[id] = s;
        return s == DeviceState.Adopted;
    }

    private async Task HandleEventAsync(string id, EventReq e)
    {
        var rt = Runtime(id);
        var point = rt.Points.FirstOrDefault(p => p.Id == e.Point);
        var name = await NameOfAsync(id);
        var valueText = ProtoMap.ToJson(e.Value)?.ToJsonString();
        var text = e.Text is { Length: > 0 } t ? t : $"{e.Kind}";
        switch (e.Kind)
        {
            case EventKind.Threshold:
                await events.AddAsync(id, "threshold", $"{name}: {text} ({point?.Title ?? point?.Key} = {valueText} {point?.Unit})");
                if (IsAdoptedCached(id)) notifier.Notify($"⚠️ <b>{name}</b>: {text}" + (valueText != null ? $" — {point?.Title ?? "значение"} {valueText} {point?.Unit}" : ""));
                break;
            case EventKind.SensorError:
                await events.AddAsync(id, "sensor-error", $"{name}: {text}");
                if (IsAdoptedCached(id)) notifier.Notify($"❗ <b>{name}</b>: ошибка датчика — {text}", adminsOnly: true);
                break;
            default:
                await events.AddAsync(id, (e.Kind?.ToString() ?? "event").ToLowerInvariant(), $"{name}: {text}");
                break;
        }
    }

    // ------------------------------------------------------------------ queries

    public static List<PointDto> ParsePoints(string json)
    {
        try
        {
            return JsonSerializer.Deserialize(json, HomeJsonExt.PointList) ?? new();
        }
        catch (JsonException)
        {
            return new();
        }
    }

    public DeviceDto ToDto(DeviceEntity d, DeviceRuntime? rt = null)
    {
        rt ??= Runtime(d.Id);
        if (rt.Points.Count == 0 && d.PointsJson != null) rt.Points = ParsePoints(d.PointsJson);
        var values = new Dictionary<string, ValueDto>();
        foreach (var (pid, v) in rt.Values)
            values[rt.Points.FirstOrDefault(p => p.Id == pid)?.Key ?? $"p{pid}"] = v;
        return new DeviceDto
        {
            Id = d.Id,
            ModelId = d.Model,
            Model = ProtoMap.ModelKey(d.Model),
            HwRev = d.HwRev,
            Name = d.Name,
            Room = d.Room,
            State = d.State,
            Online = rt.Online,
            FwVersion = d.FwVersion,
            BootPartition = d.BootPartition,
            PendingVerify = rt.PendingVerify,
            Ip = d.Ip,
            LastSeen = d.LastSeen is { } ls ? Clock.FromMs(ls) : null,
            FirstSeen = Clock.FromMs(d.FirstSeen),
            Points = rt.Points,
            Values = values,
            Ota = rt.Ota,
        };
    }

    public async Task<List<DeviceDto>> ListAsync()
    {
        await using var db = await dbf.CreateDbContextAsync();
        var list = await db.Devices.AsNoTracking().ToListAsync();
        return list.OrderBy(d => d.State).ThenBy(d => d.Room).ThenBy(d => d.Name).Select(d => ToDto(d)).ToList();
    }

    public async Task<DeviceDto?> GetAsync(string idOrName)
    {
        var d = await FindAsync(idOrName);
        return d == null ? null : ToDto(d);
    }

    /// <summary>Finds a device by id, id suffix or exact name (case-insensitive).</summary>
    public async Task<DeviceEntity?> FindAsync(string idOrName)
    {
        await using var db = await dbf.CreateDbContextAsync();
        var key = idOrName.Trim().ToLowerInvariant();
        var all = await db.Devices.AsNoTracking().ToListAsync();
        return all.FirstOrDefault(d => d.Id == key)
               ?? all.FirstOrDefault(d => d.Name.Equals(idOrName.Trim(), StringComparison.OrdinalIgnoreCase))
               ?? (key.Length >= 4 ? all.SingleOrDefault(d => d.Id.EndsWith(key, StringComparison.Ordinal)) : null);
    }

    private async Task<string> NameOfAsync(string id)
    {
        await using var db = await dbf.CreateDbContextAsync();
        return await db.Devices.Where(d => d.Id == id).Select(d => d.Name).FirstOrDefaultAsync() ?? id;
    }

    public async Task PublishDeviceAsync(string id)
    {
        await using var db = await dbf.CreateDbContextAsync();
        var d = await db.Devices.AsNoTracking().FirstOrDefaultAsync(x => x.Id == id);
        if (d != null) bus.Publish("device", ToDto(d));
    }

    // ------------------------------------------------------------------ administration

    public async Task<DeviceDto> UpdateAsync(string id, DeviceUpdateRequest r, string actor)
    {
        await using var db = await dbf.CreateDbContextAsync();
        var d = await db.Devices.FindAsync(id) ?? throw new KeyNotFoundException("device not found");
        if (r.Name is { } name)
        {
            name = name.Trim();
            if (name.Length is 0 or > 24) throw new ArgumentException("name must be 1..24 characters");
            d.Name = name;
        }
        if (r.Room != null) d.Room = r.Room.Length == 0 ? null : r.Room;
        await db.SaveChangesAsync();
        await events.AddAsync(id, "renamed", $"{d.Name}: имя/комната изменены", actor);
        // Best effort: tell the device its name so it can show it.
        if (Runtime(id).Session is { } s && d.State == DeviceState.Adopted)
            _ = s.RequestAsync(new CfgSetReq { Config = new DeviceConfig { Name = Codec.Truncate(d.Name, 48), Room = Codec.Truncate(d.Room, 48) } })
                .ContinueWith(_ => { }, TaskScheduler.Default);
        var dto = ToDto(d);
        bus.Publish("device", dto);
        return dto;
    }

    public async Task SetStateAsync(string id, DeviceState state, string actor)
    {
        await using var db = await dbf.CreateDbContextAsync();
        var d = await db.Devices.FindAsync(id) ?? throw new KeyNotFoundException("device not found");
        d.State = state;
        await db.SaveChangesAsync();
        _stateCache[id] = state;
        await events.AddAsync(id, state == DeviceState.Adopted ? "adopted" : "blocked",
            state == DeviceState.Adopted ? $"{d.Name} принято" : $"{d.Name} отклонено", actor);
        // Reconnect so the device learns its new state from the HELLO answer.
        Runtime(id).Session?.Abort();
        bus.Publish("device", ToDto(d));
    }

    public async Task RemoveAsync(string id, string actor)
    {
        await using var db = await dbf.CreateDbContextAsync();
        var d = await db.Devices.FindAsync(id) ?? throw new KeyNotFoundException("device not found");
        db.Devices.Remove(d);
        await db.Samples.Where(s => s.DeviceId == id).ExecuteDeleteAsync();
        await db.SampleHours.Where(s => s.DeviceId == id).ExecuteDeleteAsync();
        await db.SaveChangesAsync();
        _stateCache.TryRemove(id, out _);
        if (_rt.TryRemove(id, out var rt)) rt.Session?.Abort();
        await events.AddAsync(id, "removed", $"{d.Name} удалено", actor);
        bus.Publish("removed", new OnlineEvent(id, false));
    }

    // ------------------------------------------------------------------ commands

    public DeviceSession SessionOf(string id, bool requireAdopted = true)
    {
        var rt = Runtime(id);
        var s = rt.Session;
        if (s == null || s.IsClosed) throw new DeviceException(ErrorCode.Timeout, "device is offline");
        if (requireAdopted && !IsAdoptedCached(id)) throw new DeviceException(ErrorCode.Forbidden, "device is not adopted");
        return s;
    }

    public PointDto PointOf(string id, string key)
    {
        var rt = Runtime(id);
        return rt.Points.FirstOrDefault(p => p.Key.Equals(key, StringComparison.OrdinalIgnoreCase))
               ?? (int.TryParse(key, out var n) ? rt.Points.FirstOrDefault(p => p.Id == n) : null)
               ?? throw new KeyNotFoundException($"no point '{key}'");
    }

    public async Task<ValueDto> SetAsync(string id, string key, JsonNode? value, string actor)
    {
        var session = SessionOf(id);
        var p = PointOf(id, key);
        var v = ProtoMap.FromJson(p, value);
        var resp = await session.RequestAsync<SetResp>(new SetReq { Point = (byte)p.Id, Value = v });
        var dto = new ValueDto(ProtoMap.ToJson(resp.Value ?? v), Clock.NowMs);
        Runtime(id).Values[p.Id] = dto;
        bus.Publish("values", new ValuesEvent(id, new() { [p.Key] = dto }));
        await events.AddAsync(id, "set", $"{await NameOfAsync(id)}: {p.Title} = {PointFormat.Format(p, dto.Value)}", actor);
        return dto;
    }

    public async Task<string?> InvokeAsync(string id, string key, double? arg, string actor)
    {
        var session = SessionOf(id);
        var p = PointOf(id, key);
        if (p.Kind != "action") throw new ArgumentException($"'{p.Key}' is not an action");
        var req = new InvokeReq { Point = (byte)p.Id };
        if (p.HasArg) req.Arg = (float)(arg ?? p.ArgDefault ?? 0);
        var resp = await session.RequestAsync<InvokeResp>(req, TimeSpan.FromSeconds(30));
        await events.AddAsync(id, "invoke", $"{await NameOfAsync(id)}: {p.Title}" + (req.Arg is { } a ? $" ({a})" : ""), actor);
        return resp.Text;
    }

    public async Task RebootAsync(string id, string actor)
    {
        await SessionOf(id).RequestAsync(new RebootReq());
        await events.AddAsync(id, "reboot", $"{await NameOfAsync(id)}: перезагрузка", actor);
    }

    public Task IdentifyAsync(string id, int seconds) =>
        SessionOf(id, requireAdopted: false).RequestAsync(new IdentifyReq { Seconds = (byte)Math.Clamp(seconds, 1, 120) });

    public async Task FactoryResetAsync(string id, string mode, string actor)
    {
        var m = mode switch
        {
            "settings" => ResetMode.Settings,
            "firmware" => ResetMode.Firmware,
            "all" => ResetMode.All,
            _ => throw new ArgumentException("mode must be settings, firmware or all"),
        };
        await SessionOf(id).RequestAsync(new FactoryResetReq { Mode = m });
        await events.AddAsync(id, "factory-reset", $"{await NameOfAsync(id)}: сброс ({mode})", actor);
    }

    public async Task<NetStatusDto> NetStatusAsync(string id)
    {
        var r = await SessionOf(id).RequestAsync<NetStatusResp>(new NetStatusReq());
        var s = r.Status ?? new NetStatus();
        return new NetStatusDto
        {
            State = ProtoMap.LinkStateKey(s.State),
            Current = ProtoMap.ToDto(s.Current),
            Rssi = s.Rssi,
            Ssid = s.Ssid,
            Server = s.Server,
            Mac = s.Mac,
        };
    }

    public async Task SetNetworkAsync(string id, NetworkDto net, string actor)
    {
        var cfg = ProtoMap.FromDto(net);
        await SessionOf(id).RequestAsync(new CfgSetReq { Config = new DeviceConfig { Net = cfg } });
        await events.AddAsync(id, "network", $"{await NameOfAsync(id)}: сеть — {net.Mode}" + (net.Mode == "static" ? $" {net.Ip}/{net.Prefix}" : ""), actor);
    }

    public async Task<DeviceConfigDto> ConfigAsync(string id)
    {
        var r = await SessionOf(id).RequestAsync<CfgGetResp>(new CfgGetReq());
        var c = r.Config ?? new DeviceConfig();
        return new DeviceConfigDto
        {
            Name = c.Name,
            Room = c.Room,
            WifiSsid = c.WifiSsid,
            Network = ProtoMap.ToDto(c.Net),
            ServerHost = c.ServerHost,
            ServerPort = c.ServerPort,
            BleMode = c.BleMode?.ToString().ToLowerInvariant(),
            LogLevel = ProtoMap.LogLevelKey(c.LogLevel),
        };
    }

    public List<LogLineDto> Logs(string id) => Runtime(id).Logs.ToList();

    public void SetOta(string id, OtaProgressDto? ota)
    {
        var rt = Runtime(id);
        rt.Ota = ota;
        _ = PublishDeviceAsync(id);
    }

    public void ForgetStateCache(string id) => _stateCache.TryRemove(id, out _);

    public int OfflineAlertMinutes => options.Value.OfflineAlertMinutes;
}

internal static class HomeJsonExt
{
    public static System.Text.Json.Serialization.Metadata.JsonTypeInfo<List<PointDto>> PointList =>
        (System.Text.Json.Serialization.Metadata.JsonTypeInfo<List<PointDto>>)ServerJson.Default.GetTypeInfo(typeof(List<PointDto>))!;
}
