using System.Text.Json.Nodes;
using System.Text.Json.Serialization;

namespace Home.Client;

// API contract shared by the server, homectl and tests. JSON is camelCase; enums are strings.

[JsonConverter(typeof(JsonStringEnumConverter<Role>))]
public enum Role { Viewer = 0, User = 1, Admin = 2 }

[JsonConverter(typeof(JsonStringEnumConverter<DeviceState>))]
public enum DeviceState { New = 0, Adopted = 1, Blocked = 2 }

[JsonConverter(typeof(JsonStringEnumConverter<OtaStatus>))]
public enum OtaStatus { Pending, Running, Rebooting, Done, Failed, RolledBack, Cancelled }

/// <summary>Description of a device point (sensor, actuator, setting, action).</summary>
public sealed record PointDto
{
    public int Id { get; init; }
    public string Key { get; init; } = "";
    public string Title { get; init; } = "";
    public string Kind { get; init; } = "sensor";      // sensor | actuator | setting | action
    public string Type { get; init; } = "f32";         // bool | i32 | f32 | str | enum
    public string? Unit { get; init; }
    public double? Min { get; init; }
    public double? Max { get; init; }
    public double? Step { get; init; }
    public List<string> Options { get; init; } = new();
    public string Ui { get; init; } = "auto";          // auto | gauge | switch | slider | button | select | text | hidden
    public bool History { get; init; }
    public bool ReadOnly { get; init; }
    public bool Advanced { get; init; }
    public bool Persist { get; init; }
    public double? ThrWarn { get; init; }
    public double? ThrAlarm { get; init; }
    public bool HasArg { get; init; }
    public double? ArgDefault { get; init; }
}

public sealed record ValueDto(JsonNode? Value, long Ts);

public sealed record OtaProgressDto(long JobId, string Version, OtaStatus Status, int Progress, string? Error);

public sealed record DeviceDto
{
    public string Id { get; init; } = "";
    public int ModelId { get; init; }
    public string Model { get; init; } = "";
    public int HwRev { get; init; }
    public string Name { get; init; } = "";
    public string? Room { get; init; }
    public DeviceState State { get; init; }
    public bool Online { get; init; }
    public string? FwVersion { get; init; }
    public string? BootPartition { get; init; }
    public bool PendingVerify { get; init; }
    public string? Ip { get; init; }
    public DateTimeOffset? LastSeen { get; init; }
    public DateTimeOffset FirstSeen { get; init; }
    public List<PointDto> Points { get; init; } = new();
    public Dictionary<string, ValueDto> Values { get; init; } = new();
    public OtaProgressDto? Ota { get; init; }
}

public sealed record DeviceUpdateRequest(string? Name, string? Room);
public sealed record SetValueRequest(JsonNode? Value);
public sealed record InvokeRequest(double? Arg);
public sealed record InvokeResponse(string? Text);
public sealed record FactoryResetRequest(string Mode); // settings | firmware | all
public sealed record IdentifyRequest(int Seconds = 10);

public sealed record NetworkDto
{
    public string Mode { get; init; } = "dhcp";        // dhcp | static
    public string? Ip { get; init; }
    public int? Prefix { get; init; }
    public string? Gateway { get; init; }
    public string? Dns1 { get; init; }
    public string? Dns2 { get; init; }
    public string? Hostname { get; init; }
}

public sealed record NetStatusDto
{
    public string State { get; init; } = "";
    public NetworkDto? Current { get; init; }
    public int? Rssi { get; init; }
    public string? Ssid { get; init; }
    public string? Server { get; init; }
    public string? Mac { get; init; }
}

public sealed record DeviceConfigDto
{
    public string? Name { get; init; }
    public string? Room { get; init; }
    public string? WifiSsid { get; init; }
    public NetworkDto? Network { get; init; }
    public string? ServerHost { get; init; }
    public int? ServerPort { get; init; }
    public string? BleMode { get; init; }
    public string? LogLevel { get; init; }
}

public sealed record HistoryPoint(long T, double? Min, double? Max, double? Avg);
public sealed record HistoryDto(string Point, long From, long To, long Step, List<HistoryPoint> Points);

public sealed record RoomDto(string Id, string Name, int Sort);

public sealed record UserDto(long TelegramId, string Name, Role Role, bool Notify, DateTimeOffset CreatedAt, string? AddedBy);
public sealed record UserUpsertRequest(long TelegramId, string? Name, Role? Role, bool? Notify);
public sealed record AccessRequestDto(long TelegramId, string Name, string? Username, DateTimeOffset RequestedAt);

public sealed record TokenDto(long Id, string Name, Role Role, DateTimeOffset CreatedAt, DateTimeOffset? LastUsedAt);
public sealed record TokenCreateRequest(string Name, Role Role = Role.Admin);
public sealed record TokenCreatedDto(string Token, TokenDto Info);

public sealed record FirmwareDto
{
    public long Id { get; init; }
    public int ModelId { get; init; }
    public string Model { get; init; } = "";
    public string Version { get; init; } = "";
    public string Project { get; init; } = "";
    public string Sha256 { get; init; } = "";
    public long Size { get; init; }
    public string Channel { get; init; } = "stable";
    public string? Notes { get; init; }
    public DateTimeOffset UploadedAt { get; init; }
}

public sealed record OtaItemDto(string DeviceId, string DeviceName, OtaStatus Status, int Progress, string? Error, string? FromVersion);

public sealed record OtaJobDto
{
    public long Id { get; init; }
    public long FirmwareId { get; init; }
    public string Model { get; init; } = "";
    public string Version { get; init; } = "";
    public OtaStatus Status { get; init; }
    public bool Canary { get; init; }
    public DateTimeOffset CreatedAt { get; init; }
    public string? CreatedBy { get; init; }
    public List<OtaItemDto> Items { get; init; } = new();
}

public sealed record OtaJobRequest(long FirmwareId, List<string>? DeviceIds, bool AllOfModel = false, bool Canary = false);

public sealed record EventDto(long Id, DateTimeOffset Ts, string? DeviceId, string Kind, string Text, string? Actor);
public sealed record LogLineDto(long Ts, string Level, string Tag, string Text);

public sealed record SystemDto
{
    public string Version { get; init; } = "";
    public DateTimeOffset StartedAt { get; init; }
    public int Devices { get; init; }
    public int Online { get; init; }
    public int NewDevices { get; init; }
    public long DbSize { get; init; }
    public long DiskFree { get; init; }
    public string? PublicUrl { get; init; }
    public bool BotConfigured { get; init; }
    public string? BotUsername { get; init; }
    public int DevicePort { get; init; }
    public int ProtocolMajor { get; init; }
    public int ProtocolMinor { get; init; }
}

public sealed record TelegramAuthRequest(string InitData);
public sealed record AuthResponse(string Token, UserDto User, DateTimeOffset ExpiresAt);
public sealed record MeDto(long? TelegramId, string Name, Role Role, bool Local);

public sealed record ErrorDto(string Error);

/// <summary>An event of the live stream (/api/v1/stream, server-sent events).</summary>
public sealed record StreamEvent(string Type, JsonNode? Data);

public sealed record ValuesEvent(string DeviceId, Dictionary<string, ValueDto> Values);
public sealed record OnlineEvent(string DeviceId, bool Online);
public sealed record LogEvent(string DeviceId, LogLineDto Line);
