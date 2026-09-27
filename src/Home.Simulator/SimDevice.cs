using System.Buffers;
using System.IO.Pipelines;
using System.Net;
using System.Net.Sockets;
using System.Security.Cryptography;
using System.Text;
using Home.Protocol;

namespace Home.Simulator;

public sealed class SimOptions
{
    public string Host { get; set; } = "127.0.0.1";
    public int Port { get; set; } = ProtoInfo.TcpPort;
    public string Id { get; set; } = "5e0000000001";
    public string ModelName { get; set; } = "co2";
    public string Version { get; set; } = "1.0.0";
    public string? Name { get; set; }
    public TimeSpan ReportInterval { get; set; } = TimeSpan.FromSeconds(10);
    /// <summary>Simulate a firmware that fails to start: after OTA the device comes back with the old version.</summary>
    public bool OtaFail { get; set; }
    public TimeSpan RebootDelay { get; set; } = TimeSpan.FromSeconds(1);
}

/// <summary>
/// Emulated Home device speaking the real protocol over TCP. Used by `home-sim`, integration tests
/// and for developing the server and the Mini App without hardware.
/// </summary>
public sealed class SimDevice
{
    private readonly SimOptions _o;
    private readonly Action<string> _log;
    private readonly List<PointDef> _points;
    private readonly Dictionary<byte, Value> _values = new();
    private readonly Random _rnd;
    private readonly SemaphoreSlim _sendLock = new(1, 1);
    private NetworkStream? _stream;
    private ushort _reqId;
    private string _version;
    private BootPartition _boot = BootPartition.Ota0;
    private bool _pendingVerify;
    private DeviceConfig _config;

    // OTA state
    private MemoryStream? _ota;
    private byte[]? _otaSha;
    private string? _otaVersion;
    private uint _otaSize;

    public SimDevice(SimOptions o, Action<string>? log = null)
    {
        _o = o;
        _log = log ?? (_ => { });
        _version = o.Version;
        _rnd = new Random(o.Id.GetHashCode());
        _points = Models.Points(o.ModelName);
        foreach (var p in _points) _values[p.Id!.Value] = Models.Initial(p);
        _config = new DeviceConfig
        {
            Name = o.Name, WifiSsid = "SimWiFi", ServerHost = o.Host, ServerPort = (ushort)o.Port,
            Net = new NetConfig { IpMode = IpMode.Dhcp, Hostname = $"home-sim-{o.Id[^4..]}" },
            BleMode = BleMode.Always, LogLevel = Home.Protocol.LogLevel.Info,
        };
    }

    public string Id => _o.Id;
    public string Version => _version;
    public AdoptState? State { get; private set; }
    public bool Connected => _stream != null;
    public int Identified { get; private set; }
    public IReadOnlyDictionary<byte, Value> Values => _values;
    public ResetMode? LastReset { get; private set; }
    public NetConfig? LastNet { get; private set; }

    public async Task RunAsync(CancellationToken ct)
    {
        var backoff = TimeSpan.FromSeconds(1);
        while (!ct.IsCancellationRequested)
        {
            try
            {
                await ConnectOnceAsync(ct);
                backoff = TimeSpan.FromSeconds(1);
            }
            catch (OperationCanceledException) when (ct.IsCancellationRequested) { break; }
            catch (RebootException r)
            {
                _log($"{_o.Id}: reboot ({r.Message})");
                await Task.Delay(_o.RebootDelay, ct);
                continue;
            }
            catch (Exception e)
            {
                _log($"{_o.Id}: connection lost: {e.Message}");
            }
            finally
            {
                _stream = null;
            }
            await Task.Delay(backoff, ct).ContinueWith(_ => { });
            backoff = TimeSpan.FromSeconds(Math.Min(backoff.TotalSeconds * 2, 30));
        }
    }

    private sealed class RebootException(string why) : Exception(why);

    private async Task ConnectOnceAsync(CancellationToken ct)
    {
        using var tcp = new TcpClient { NoDelay = true };
        await tcp.ConnectAsync(_o.Host, _o.Port, ct);
        _stream = tcp.GetStream();
        using var linked = CancellationTokenSource.CreateLinkedTokenSource(ct);
        var reader = PipeReader.Create(_stream);

        var helloId = ++_reqId;
        await SendAsync(new HelloReq
        {
            DeviceId = _o.Id, Model = Models.ModelOf(_o.ModelName), HwRev = 1, FwVersion = _version,
            ProtoMajor = ProtoInfo.Major, ProtoMinor = ProtoInfo.Minor, BootPartition = _boot, ResetReason = 1,
            UptimeS = 1, Name = _o.Name, IdfVersion = "sim", PendingVerify = _pendingVerify,
        }, helloId);

        Task? reporter = null;
        try
        {
            while (!ct.IsCancellationRequested)
            {
                var result = await reader.ReadAsync(linked.Token);
                var buf = result.Buffer;
                while (TcpFrame.TryDecode(ref buf, out var raw))
                {
                    var msg = Codec.Decode(raw!);
                    if (msg.Header.IsResponse)
                    {
                        if (msg.Header.ReqId == helloId && msg.Body is HelloResp hr)
                        {
                            State = hr.State;
                            _log($"{_o.Id}: connected, state {hr.State}");
                            if (_pendingVerify)
                            {
                                _pendingVerify = false; // mark the new firmware valid
                                _log($"{_o.Id}: firmware {_version} confirmed");
                            }
                            if (hr.State == AdoptState.Blocked) return;
                            await SendAsync(new StateReq { Samples = Snapshot() }, ++_reqId, Flags.Noack);
                            reporter ??= ReportLoopAsync(linked.Token);
                        }
                        continue;
                    }
                    await HandleAsync(msg);
                }
                reader.AdvanceTo(buf.Start, buf.End);
                if (result.IsCompleted) break;
            }
        }
        finally
        {
            linked.Cancel();
            if (reporter != null) await reporter.ContinueWith(_ => { });
            await reader.CompleteAsync();
        }
    }

    private List<Sample> Snapshot() => _values.Select(kv => new Sample { Point = kv.Key, Value = kv.Value }).ToList();

    private async Task ReportLoopAsync(CancellationToken ct)
    {
        while (!ct.IsCancellationRequested)
        {
            await Task.Delay(_o.ReportInterval, ct);
            var changed = Models.Step(_o.ModelName, _values, _rnd);
            if (changed.Count == 0) continue;
            await SendAsync(new ReportReq { Samples = changed.Select(id => new Sample { Point = id, Value = _values[id] }).ToList() }, ++_reqId, Flags.Noack);
        }
    }

    private async Task SendAsync(IProtoMessage m, ushort reqId, Flags extra = 0)
    {
        var s = _stream ?? throw new IOException("not connected");
        var frame = TcpFrame.Encode(Codec.Encode(m, reqId, extra));
        await _sendLock.WaitAsync();
        try
        {
            await s.WriteAsync(frame);
        }
        finally
        {
            _sendLock.Release();
        }
    }

    private async Task ErrorAsync(Header h, ErrorCode code, string text)
    {
        var s = _stream ?? throw new IOException("not connected");
        var frame = TcpFrame.Encode(Codec.EncodeError(h.Type, h.ReqId, code, text));
        await _sendLock.WaitAsync();
        try
        {
            await s.WriteAsync(frame);
        }
        finally
        {
            _sendLock.Release();
        }
    }

    private async Task HandleAsync(Message msg)
    {
        var h = msg.Header;
        var id = h.ReqId;
        switch (msg.Body)
        {
            case PingReq p:
                await SendAsync(new PingResp { TimeMs = p.TimeMs }, id);
                break;
            case DescribeReq:
                await SendAsync(new DescribeResp { Points = _points }, id);
                break;
            case GetReq g:
                var want = g.Points.Count == 0 ? _values.Keys.ToList() : g.Points;
                await SendAsync(new GetResp { Samples = want.Where(_values.ContainsKey).Select(p => new Sample { Point = p, Value = _values[p] }).ToList() }, id);
                break;
            case SetReq s:
                var pd = _points.FirstOrDefault(p => p.Id == s.Point);
                if (pd == null) { await ErrorAsync(h, ErrorCode.NotFound, "no such point"); break; }
                if (pd.Kind is PointKind.Sensor or PointKind.Action) { await ErrorAsync(h, ErrorCode.Forbidden, "read-only"); break; }
                var v = s.Value ?? new Value();
                if (v.F is { } f && ((pd.Min is { } min && f < min) || (pd.Max is { } max && f > max))) { await ErrorAsync(h, ErrorCode.InvalidValue, "out of range"); break; }
                _values[s.Point!.Value] = v;
                await SendAsync(new SetResp { Value = v }, id);
                break;
            case InvokeReq inv:
                var ap = _points.FirstOrDefault(p => p.Id == inv.Point && p.Kind == PointKind.Action);
                if (ap == null) { await ErrorAsync(h, ErrorCode.NotFound, "no such action"); break; }
                await SendAsync(new InvokeResp { Text = $"{ap.Key} done" + (inv.Arg is { } a ? $" ({a})" : "") }, id);
                break;
            case IdentifyReq:
                Identified++;
                await SendAsync(new IdentifyResp(), id);
                break;
            case CfgGetReq:
                await SendAsync(new CfgGetResp { Config = new DeviceConfig
                {
                    Name = _config.Name, Room = _config.Room, WifiSsid = _config.WifiSsid, Net = _config.Net,
                    ServerHost = _config.ServerHost, ServerPort = _config.ServerPort, BleMode = _config.BleMode, LogLevel = _config.LogLevel,
                } }, id);
                break;
            case CfgSetReq cs:
                var c = cs.Config ?? new DeviceConfig();
                if (c.Name != null) _config.Name = c.Name;
                if (c.Room != null) _config.Room = c.Room;
                if (c.Net != null) { _config.Net = c.Net; LastNet = c.Net; }
                if (c.WifiSsid != null) _config.WifiSsid = c.WifiSsid;
                await SendAsync(new CfgSetResp(), id);
                break;
            case WifiScanReq:
                await SendAsync(new WifiScanResp { Networks = { new WifiNet { Ssid = "SimWiFi", Rssi = -48, Auth = WifiAuth.Wpa2, Channel = 6 } } }, id);
                break;
            case NetStatusReq:
                await SendAsync(new NetStatusResp { Status = new NetStatus
                {
                    State = LinkState.Online, Rssi = -48, Ssid = _config.WifiSsid, Server = $"{_o.Host}:{_o.Port}",
                    Mac = string.Join(':', Enumerable.Range(0, 6).Select(i => _o.Id.Substring(i * 2, 2).ToUpperInvariant())),
                    Current = new NetConfig { IpMode = _config.Net?.IpMode ?? IpMode.Dhcp, Ip = BitConverter.ToUInt32(IPAddress.Loopback.GetAddressBytes()), Mask = 0x00FFFFFF, Gw = BitConverter.ToUInt32(IPAddress.Loopback.GetAddressBytes()) },
                } }, id);
                break;
            case OtaBeginReq ob:
                if (ob.Model != Models.ModelOf(_o.ModelName)) { await ErrorAsync(h, ErrorCode.OtaWrongModel, "wrong model"); break; }
                if (_ota != null && _otaSha != null && ob.Sha256 != null && _otaSha.AsSpan().SequenceEqual(ob.Sha256))
                {
                    await SendAsync(new OtaBeginResp { Chunk = ProtoInfo.OtaChunk, ResumeFrom = (uint)_ota.Length }, id);
                    break;
                }
                _ota = new MemoryStream();
                _otaSha = ob.Sha256;
                _otaVersion = ob.Version;
                _otaSize = ob.Size ?? 0;
                await SendAsync(new OtaBeginResp { Chunk = ProtoInfo.OtaChunk, ResumeFrom = 0 }, id);
                break;
            case OtaDataReq od:
                if (_ota == null || od.Offset != _ota.Length) { await ErrorAsync(h, ErrorCode.BadRequest, $"expected offset {_ota?.Length}"); break; }
                _ota.Write(od.Data ?? Array.Empty<byte>());
                await SendAsync(new OtaDataResp { NextOffset = (uint)_ota.Length }, id);
                break;
            case OtaEndReq:
                if (_ota == null || _ota.Length != _otaSize) { await ErrorAsync(h, ErrorCode.OtaBadImage, "incomplete image"); break; }
                if (!SHA256.HashData(_ota.ToArray()).AsSpan().SequenceEqual(_otaSha)) { await ErrorAsync(h, ErrorCode.OtaHashMismatch, "sha256 mismatch"); _ota = null; break; }
                await SendAsync(new OtaEndResp(), id);
                if (!_o.OtaFail)
                {
                    _version = _otaVersion ?? _version;
                    _boot = _boot == BootPartition.Ota0 ? BootPartition.Ota1 : BootPartition.Ota0;
                    _pendingVerify = true;
                }
                _ota = null;
                throw new RebootException(_o.OtaFail ? "new firmware crashed, rolled back" : $"booting {_version}");
            case OtaAbortReq:
                _ota = null;
                await SendAsync(new OtaAbortResp(), id);
                break;
            case OtaStatusReq:
                await SendAsync(new OtaStatusResp { State = _ota == null ? OtaState.Idle : OtaState.Receiving, Received = (uint)(_ota?.Length ?? 0) }, id);
                break;
            case RebootReq:
                await SendAsync(new RebootResp(), id);
                throw new RebootException("reboot requested");
            case FactoryResetReq fr:
                LastReset = fr.Mode;
                await SendAsync(new FactoryResetResp(), id);
                if (fr.Mode is ResetMode.Firmware or ResetMode.All) { _version = _o.Version; _boot = BootPartition.Factory; }
                throw new RebootException($"factory reset {fr.Mode}");
            default:
                if ((h.Flags & Flags.Noack) == 0) await ErrorAsync(h, ErrorCode.Unsupported, "not supported by simulator");
                break;
        }
    }

    public static async Task<(string Host, int Port)?> DiscoverAsync(TimeSpan timeout, int port = ProtoInfo.DiscoverPort)
    {
        using var udp = new UdpClient { EnableBroadcast = true };
        var q = Encoding.ASCII.GetBytes("HOME?");
        await udp.SendAsync(q, q.Length, new IPEndPoint(IPAddress.Broadcast, port));
        using var cts = new CancellationTokenSource(timeout);
        try
        {
            var r = await udp.ReceiveAsync(cts.Token);
            var text = Encoding.ASCII.GetString(r.Buffer).Split(' ');
            return text.Length == 2 && text[0] == "HOME" && int.TryParse(text[1], out var p) ? (r.RemoteEndPoint.Address.ToString(), p) : null;
        }
        catch (OperationCanceledException)
        {
            return null;
        }
    }
}

/// <summary>Point sets of the simulated models (the CO2 set mirrors the real co2-egg firmware).</summary>
public static class Models
{
    public static Model ModelOf(string name) => name switch
    {
        "relay" => Model.Relay,
        _ => Model.Co2Egg,
    };

    public static List<PointDef> Points(string model) => model switch
    {
        "relay" => new()
        {
            new() { Id = 1, Key = "power", Title = "Питание", Kind = PointKind.Actuator, Type = PointType.Bool, Ui = UiHint.Switch, Flags = (byte)PointFlags.History },
            new() { Id = 2, Key = "power_w", Title = "Мощность", Kind = PointKind.Sensor, Type = PointType.F32, Unit = "W", Ui = UiHint.Gauge, Flags = (byte)PointFlags.History },
            new() { Id = 10, Key = "auto_off", Title = "Автовыключение", Kind = PointKind.Setting, Type = PointType.I32, Unit = "min", Min = 0, Max = 240, Step = 5, Ui = UiHint.Slider, Flags = (byte)PointFlags.Persist },
            new() { Id = 20, Key = "toggle", Title = "Переключить", Kind = PointKind.Action, Type = PointType.Bool, Ui = UiHint.Button },
        },
        _ => new()
        {
            new() { Id = 1, Key = "co2", Title = "CO2", Kind = PointKind.Sensor, Type = PointType.F32, Unit = "ppm", Min = 400, Max = 5000, Step = 1, Ui = UiHint.Gauge, Flags = (byte)PointFlags.History, ThrWarn = 800, ThrAlarm = 1200 },
            new() { Id = 2, Key = "sensor_temp", Title = "Температура датчика", Kind = PointKind.Sensor, Type = PointType.F32, Unit = "°C", Flags = (byte)(PointFlags.History | PointFlags.Advanced) },
            new() { Id = 3, Key = "status", Title = "Состояние", Kind = PointKind.Sensor, Type = PointType.Enum, Options = { "preheat", "ok", "sensor_error" }, Ui = UiHint.Text },
            new() { Id = 4, Key = "sensor_model", Title = "Датчик", Kind = PointKind.Sensor, Type = PointType.Str, Flags = (byte)PointFlags.Advanced, Ui = UiHint.Text },
            new() { Id = 10, Key = "report_interval", Title = "Интервал отправки", Kind = PointKind.Setting, Type = PointType.I32, Unit = "s", Min = 5, Max = 600, Step = 5, Ui = UiHint.Slider, Flags = (byte)(PointFlags.Persist | PointFlags.Advanced) },
            new() { Id = 11, Key = "report_delta", Title = "Порог изменения", Kind = PointKind.Setting, Type = PointType.I32, Unit = "ppm", Min = 5, Max = 500, Step = 5, Ui = UiHint.Slider, Flags = (byte)(PointFlags.Persist | PointFlags.Advanced) },
            new() { Id = 12, Key = "auto_calibration", Title = "Автокалибровка", Kind = PointKind.Setting, Type = PointType.Bool, Ui = UiHint.Switch, Flags = (byte)PointFlags.Persist },
            new() { Id = 13, Key = "display_mode", Title = "Экран", Kind = PointKind.Setting, Type = PointType.Enum, Options = { "co2", "co2_graph", "night", "off" }, Ui = UiHint.Select, Flags = (byte)PointFlags.Persist },
            new() { Id = 14, Key = "display_brightness", Title = "Яркость экрана", Kind = PointKind.Setting, Type = PointType.I32, Unit = "%", Min = 0, Max = 100, Step = 5, Ui = UiHint.Slider, Flags = (byte)PointFlags.Persist },
            new() { Id = 15, Key = "thr_warn", Title = "Порог «внимание»", Kind = PointKind.Setting, Type = PointType.I32, Unit = "ppm", Min = 400, Max = 5000, Step = 50, Ui = UiHint.Slider, Flags = (byte)(PointFlags.Persist | PointFlags.Advanced) },
            new() { Id = 16, Key = "thr_alarm", Title = "Порог «тревога»", Kind = PointKind.Setting, Type = PointType.I32, Unit = "ppm", Min = 400, Max = 5000, Step = 50, Ui = UiHint.Slider, Flags = (byte)(PointFlags.Persist | PointFlags.Advanced) },
            new() { Id = 17, Key = "led_alarm", Title = "Мигать при тревоге", Kind = PointKind.Setting, Type = PointType.Bool, Ui = UiHint.Switch, Flags = (byte)PointFlags.Persist },
            new() { Id = 20, Key = "calibrate", Title = "Калибровка", Kind = PointKind.Action, Type = PointType.F32, Unit = "ppm", Min = 400, Max = 5000, Step = 1, Ui = UiHint.Button, HasArg = true, ArgDefault = 420 },
            new() { Id = 21, Key = "sensor_reset", Title = "Сброс датчика", Kind = PointKind.Action, Type = PointType.Bool, Ui = UiHint.Button, Flags = (byte)PointFlags.Advanced },
        },
    };

    public static Value Initial(PointDef p) => p.Key switch
    {
        "co2" => new() { F = 650 },
        "sensor_temp" => new() { F = 27.5f },
        "status" => new() { I = 1 },
        "sensor_model" => new() { S = "SIM-ACD1200" },
        "report_interval" => new() { I = 30 },
        "report_delta" => new() { I = 25 },
        "auto_calibration" => new() { B = true },
        "display_mode" => new() { I = 0 },
        "display_brightness" => new() { I = 60 },
        "thr_warn" => new() { I = 800 },
        "thr_alarm" => new() { I = 1200 },
        "led_alarm" => new() { B = true },
        "power" => new() { B = false },
        "power_w" => new() { F = 0 },
        "auto_off" => new() { I = 0 },
        _ => new() { B = false },
    };

    /// <summary>Random walk of sensor values; returns ids of changed points.</summary>
    public static List<byte> Step(string model, Dictionary<byte, Value> values, Random rnd)
    {
        var changed = new List<byte>();
        if (model == "relay")
        {
            var on = values[1].B == true;
            values[2] = new() { F = on ? (float)Math.Round(40 + rnd.NextDouble() * 5, 1) : 0 };
            changed.Add(2);
            return changed;
        }
        var co2 = values[1].F ?? 650;
        co2 = Math.Clamp(co2 + (float)((rnd.NextDouble() - 0.45) * 40), 400, 2500);
        values[1] = new() { F = (float)Math.Round(co2) };
        var t = values[2].F ?? 27;
        values[2] = new() { F = (float)Math.Round(Math.Clamp(t + (rnd.NextDouble() - 0.5) * 0.2, 20, 35), 1) };
        changed.Add(1);
        changed.Add(2);
        return changed;
    }
}
