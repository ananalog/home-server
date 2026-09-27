using System.Net;
using System.Text.Json;
using System.Text.Json.Nodes;
using Home.Client;
using Home.Protocol;
using PLogLevel = Home.Protocol.LogLevel;

namespace Home.Server.Devices;

/// <summary>Conversions between protocol types and API DTOs.</summary>
public static class ProtoMap
{
    public static string ModelKey(int model) => (Model)model switch
    {
        Model.Co2Egg => "co2-egg",
        Model.Relay => "relay",
        Model.BoardProbe => "board-probe",
        _ => $"model-{model}",
    };

    public static string KindKey(PointKind? k) => k switch
    {
        PointKind.Actuator => "actuator",
        PointKind.Setting => "setting",
        PointKind.Action => "action",
        _ => "sensor",
    };

    public static string TypeKey(PointType? t) => t switch
    {
        PointType.Bool => "bool",
        PointType.I32 => "i32",
        PointType.Str => "str",
        PointType.Enum => "enum",
        _ => "f32",
    };

    public static string UiKey(UiHint? u) => u switch
    {
        UiHint.Gauge => "gauge",
        UiHint.Switch => "switch",
        UiHint.Slider => "slider",
        UiHint.Button => "button",
        UiHint.Select => "select",
        UiHint.Text => "text",
        UiHint.Hidden => "hidden",
        _ => "auto",
    };

    public static PointDto ToDto(PointDef p)
    {
        var flags = (PointFlags)(p.Flags ?? 0);
        return new PointDto
        {
            Id = p.Id ?? 0,
            Key = p.Key ?? $"p{p.Id}",
            Title = string.IsNullOrEmpty(p.Title) ? p.Key ?? $"p{p.Id}" : p.Title,
            Kind = KindKey(p.Kind),
            Type = TypeKey(p.Type),
            Unit = string.IsNullOrEmpty(p.Unit) ? null : p.Unit,
            Min = p.Min, Max = p.Max, Step = p.Step,
            Options = p.Options.ToList(),
            Ui = UiKey(p.Ui),
            History = flags.HasFlag(PointFlags.History),
            ReadOnly = flags.HasFlag(PointFlags.Readonly),
            Advanced = flags.HasFlag(PointFlags.Advanced),
            Persist = flags.HasFlag(PointFlags.Persist),
            ThrWarn = p.ThrWarn, ThrAlarm = p.ThrAlarm,
            HasArg = p.HasArg ?? false,
            ArgDefault = p.ArgDefault,
        };
    }

    public static JsonNode? ToJson(Home.Protocol.Value? v) => v switch
    {
        null => null,
        { B: { } b } => JsonValue.Create(b),
        { I: { } i } => JsonValue.Create(i),
        { F: { } f } => JsonValue.Create(Math.Round((double)f, 3)),
        { S: { } s } => JsonValue.Create(s),
        _ => null,
    };

    /// <summary>Numeric value for history (bool -> 0/1); null for strings.</summary>
    public static double? ToNumber(Home.Protocol.Value? v) => v switch
    {
        { F: { } f } => f,
        { I: { } i } => i,
        { B: { } b } => b ? 1 : 0,
        _ => null,
    };

    /// <summary>Converts a JSON value from the API into a protocol value, validated against the point.</summary>
    public static Home.Protocol.Value FromJson(PointDto p, JsonNode? node)
    {
        if (p.Kind is "sensor" or "action" || p.ReadOnly)
            throw new ArgumentException($"point '{p.Key}' is read-only");
        if (node == null) throw new ArgumentException("value is required");
        var el = node.GetValueKind();
        switch (p.Type)
        {
            case "bool":
                if (el is JsonValueKind.True or JsonValueKind.False) return new() { B = node.GetValue<bool>() };
                var s = node.ToString().Trim().ToLowerInvariant();
                if (s is "on" or "true" or "1" or "yes") return new() { B = true };
                if (s is "off" or "false" or "0" or "no") return new() { B = false };
                throw new ArgumentException($"'{s}' is not a boolean (on/off)");
            case "str":
                return new() { S = node.ToString() };
            case "enum":
                if (el == JsonValueKind.Number)
                {
                    var idx = node.GetValue<int>();
                    if (idx < 0 || idx >= Math.Max(1, p.Options.Count)) throw new ArgumentException("enum index out of range");
                    return new() { I = idx };
                }
                var name = node.ToString();
                var pos = p.Options.IndexOf(name);
                if (pos < 0) throw new ArgumentException($"'{name}' is not one of: {string.Join(", ", p.Options)}");
                return new() { I = pos };
            default:
                double d;
                if (el == JsonValueKind.Number) d = node.GetValue<double>();
                else if (!double.TryParse(node.ToString(), System.Globalization.NumberStyles.Float, System.Globalization.CultureInfo.InvariantCulture, out d))
                    throw new ArgumentException($"'{node}' is not a number");
                if (p.Min is { } min && d < min) throw new ArgumentException($"value below minimum {min}");
                if (p.Max is { } max && d > max) throw new ArgumentException($"value above maximum {max}");
                return p.Type == "i32" ? new() { I = (int)Math.Round(d) } : new() { F = (float)d };
        }
    }

    public static string? Ip(uint? addr) => addr is { } a && a != 0 ? new IPAddress(a).ToString() : null;

    public static uint IpToU32(string ip)
    {
        var a = IPAddress.Parse(ip);
        if (a.AddressFamily != System.Net.Sockets.AddressFamily.InterNetwork) throw new ArgumentException($"'{ip}' is not IPv4");
        return BitConverter.ToUInt32(a.GetAddressBytes());
    }

    public static int PrefixFromMask(uint mask) => System.Numerics.BitOperations.PopCount(mask);

    public static uint MaskFromPrefix(int prefix)
    {
        if (prefix is < 0 or > 32) throw new ArgumentException("prefix must be 0..32");
        var hostOrder = prefix == 0 ? 0u : uint.MaxValue << (32 - prefix);
        var bytes = BitConverter.GetBytes(hostOrder);
        if (BitConverter.IsLittleEndian) Array.Reverse(bytes);
        return BitConverter.ToUInt32(bytes);
    }

    public static NetworkDto ToDto(NetConfig? c) => c == null ? new() : new()
    {
        Mode = c.IpMode == IpMode.Static ? "static" : "dhcp",
        Ip = Ip(c.Ip),
        Prefix = c.Mask is { } m && m != 0 ? PrefixFromMask(m) : null,
        Gateway = Ip(c.Gw),
        Dns1 = Ip(c.Dns1),
        Dns2 = Ip(c.Dns2),
        Hostname = c.Hostname,
    };

    public static NetConfig FromDto(NetworkDto n)
    {
        var cfg = new NetConfig { IpMode = n.Mode == "static" ? IpMode.Static : IpMode.Dhcp, Hostname = n.Hostname };
        if (cfg.IpMode == IpMode.Static)
        {
            if (n.Ip == null || n.Prefix == null || n.Gateway == null) throw new ArgumentException("static mode needs ip, prefix and gateway");
            cfg.Ip = IpToU32(n.Ip);
            cfg.Mask = MaskFromPrefix(n.Prefix.Value);
            cfg.Gw = IpToU32(n.Gateway);
            if ((cfg.Ip & cfg.Mask) != (cfg.Gw & cfg.Mask)) throw new ArgumentException("gateway is not in the device subnet");
            cfg.Dns1 = IpToU32(n.Dns1 ?? n.Gateway);
            if (n.Dns2 != null) cfg.Dns2 = IpToU32(n.Dns2);
        }
        return cfg;
    }

    public static string LinkStateKey(LinkState? s) => s switch
    {
        LinkState.NoConfig => "no-config",
        LinkState.WifiConnecting => "wifi-connecting",
        LinkState.IpOk => "ip-ok",
        LinkState.ServerConnecting => "server-connecting",
        LinkState.Online => "online",
        _ => "unknown",
    };

    public static string LogLevelKey(PLogLevel? l) => l switch
    {
        PLogLevel.Error => "error",
        PLogLevel.Warn => "warn",
        PLogLevel.Info => "info",
        PLogLevel.Debug => "debug",
        PLogLevel.Verbose => "verbose",
        _ => "none",
    };

    public static string BootKey(BootPartition? b) => b switch
    {
        BootPartition.Factory => "factory",
        BootPartition.Ota0 => "ota_0",
        BootPartition.Ota1 => "ota_1",
        _ => "unknown",
    };
}
