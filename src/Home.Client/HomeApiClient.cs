using System.Net;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Net.Sockets;
using System.Runtime.CompilerServices;
using System.Text.Json;
using System.Text.Json.Nodes;
using System.Text.Json.Serialization.Metadata;

namespace Home.Client;

public sealed record ClientConfig(string? Server, string? Token);

public sealed class HomeApiException(HttpStatusCode status, string message) : Exception(message)
{
    public HttpStatusCode Status { get; } = status;
}

/// <summary>Typed client of the Home HTTP API. Used by homectl and by integration tests.</summary>
public sealed class HomeApiClient : IDisposable
{
    public const string DefaultSocket = "/run/home/api.sock";
    private readonly HttpClient _http;

    public HomeApiClient(HttpClient http) => _http = http;

    /// <summary>
    /// server: "http(s)://host[:port]" or "unix:/path/to/api.sock". A unix socket needs no token
    /// (the server trusts local connections that can open the socket).
    /// </summary>
    public static HomeApiClient Create(string server, string? token = null)
    {
        HttpClient http;
        if (server.StartsWith("unix:", StringComparison.Ordinal))
        {
            var path = server[5..];
            var handler = new SocketsHttpHandler
            {
                UseProxy = false,
                ConnectCallback = async (_, ct) =>
                {
                    var socket = new Socket(AddressFamily.Unix, SocketType.Stream, ProtocolType.Unspecified);
                    await socket.ConnectAsync(new UnixDomainSocketEndPoint(path), ct);
                    return new NetworkStream(socket, ownsSocket: true);
                },
            };
            http = new HttpClient(handler) { BaseAddress = new Uri("http://localhost/") };
        }
        else
        {
            http = new HttpClient { BaseAddress = new Uri(server.TrimEnd('/') + "/") };
        }
        http.Timeout = TimeSpan.FromSeconds(120);
        if (!string.IsNullOrEmpty(token)) http.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", token);
        return new HomeApiClient(http);
    }

    public void Dispose() => _http.Dispose();

    private static JsonTypeInfo<T> Info<T>() => (JsonTypeInfo<T>)HomeJson.Default.GetTypeInfo(typeof(T))!;

    private static async Task Check(HttpResponseMessage resp, CancellationToken ct)
    {
        if (resp.IsSuccessStatusCode) return;
        string msg;
        try
        {
            var err = await resp.Content.ReadFromJsonAsync(Info<ErrorDto>(), ct);
            msg = err?.Error ?? resp.ReasonPhrase ?? "error";
        }
        catch
        {
            msg = resp.ReasonPhrase ?? "error";
        }
        throw new HomeApiException(resp.StatusCode, $"{(int)resp.StatusCode}: {msg}");
    }

    private async Task<T> Get<T>(string url, CancellationToken ct)
    {
        using var resp = await _http.GetAsync(url, ct);
        await Check(resp, ct);
        return (await resp.Content.ReadFromJsonAsync(Info<T>(), ct))!;
    }

    private async Task<TResp> Send<TReq, TResp>(HttpMethod method, string url, TReq body, CancellationToken ct)
    {
        using var req = new HttpRequestMessage(method, url) { Content = JsonContent.Create(body, Info<TReq>()) };
        using var resp = await _http.SendAsync(req, ct);
        await Check(resp, ct);
        return (await resp.Content.ReadFromJsonAsync(Info<TResp>(), ct))!;
    }

    private async Task Send<TReq>(HttpMethod method, string url, TReq body, CancellationToken ct)
    {
        using var req = new HttpRequestMessage(method, url) { Content = JsonContent.Create(body, Info<TReq>()) };
        using var resp = await _http.SendAsync(req, ct);
        await Check(resp, ct);
    }

    private async Task Send(HttpMethod method, string url, CancellationToken ct)
    {
        using var req = new HttpRequestMessage(method, url);
        using var resp = await _http.SendAsync(req, ct);
        await Check(resp, ct);
    }

    private static string E(string s) => Uri.EscapeDataString(s);

    // ---- auth / system
    public Task<MeDto> MeAsync(CancellationToken ct = default) => Get<MeDto>("api/v1/me", ct);
    public Task<SystemDto> SystemAsync(CancellationToken ct = default) => Get<SystemDto>("api/v1/system", ct);
    public Task<AuthResponse> AuthTelegramAsync(string initData, CancellationToken ct = default) =>
        Send<TelegramAuthRequest, AuthResponse>(HttpMethod.Post, "api/v1/auth/telegram", new(initData), ct);

    // ---- devices
    public Task<List<DeviceDto>> DevicesAsync(CancellationToken ct = default) => Get<List<DeviceDto>>("api/v1/devices", ct);
    public Task<DeviceDto> DeviceAsync(string id, CancellationToken ct = default) => Get<DeviceDto>($"api/v1/devices/{E(id)}", ct);
    public Task<DeviceDto> UpdateDeviceAsync(string id, DeviceUpdateRequest r, CancellationToken ct = default) =>
        Send<DeviceUpdateRequest, DeviceDto>(HttpMethod.Patch, $"api/v1/devices/{E(id)}", r, ct);
    public Task AdoptAsync(string id, CancellationToken ct = default) => Send(HttpMethod.Post, $"api/v1/devices/{E(id)}/adopt", ct);
    public Task RejectAsync(string id, CancellationToken ct = default) => Send(HttpMethod.Post, $"api/v1/devices/{E(id)}/reject", ct);
    public Task RemoveAsync(string id, CancellationToken ct = default) => Send(HttpMethod.Delete, $"api/v1/devices/{E(id)}", ct);
    public Task<ValueDto> SetAsync(string id, string point, JsonNode? value, CancellationToken ct = default) =>
        Send<SetValueRequest, ValueDto>(HttpMethod.Post, $"api/v1/devices/{E(id)}/points/{E(point)}", new(value), ct);
    public Task<InvokeResponse> InvokeAsync(string id, string action, double? arg, CancellationToken ct = default) =>
        Send<InvokeRequest, InvokeResponse>(HttpMethod.Post, $"api/v1/devices/{E(id)}/actions/{E(action)}", new(arg), ct);
    public Task RebootAsync(string id, CancellationToken ct = default) => Send(HttpMethod.Post, $"api/v1/devices/{E(id)}/reboot", ct);
    public Task IdentifyAsync(string id, int seconds = 10, CancellationToken ct = default) =>
        Send(HttpMethod.Post, $"api/v1/devices/{E(id)}/identify", new IdentifyRequest(seconds), ct);
    public Task FactoryResetAsync(string id, string mode, CancellationToken ct = default) =>
        Send(HttpMethod.Post, $"api/v1/devices/{E(id)}/factory-reset", new FactoryResetRequest(mode), ct);
    public Task<NetStatusDto> NetStatusAsync(string id, CancellationToken ct = default) => Get<NetStatusDto>($"api/v1/devices/{E(id)}/network", ct);
    public Task SetNetworkAsync(string id, NetworkDto net, CancellationToken ct = default) =>
        Send(HttpMethod.Put, $"api/v1/devices/{E(id)}/network", net, ct);
    public Task<DeviceConfigDto> ConfigAsync(string id, CancellationToken ct = default) => Get<DeviceConfigDto>($"api/v1/devices/{E(id)}/config", ct);
    public Task<HistoryDto> HistoryAsync(string id, string point, long from, long to, long step = 0, CancellationToken ct = default) =>
        Get<HistoryDto>($"api/v1/devices/{E(id)}/history?point={E(point)}&from={from}&to={to}&step={step}", ct);
    public Task<List<LogLineDto>> LogsAsync(string id, CancellationToken ct = default) => Get<List<LogLineDto>>($"api/v1/devices/{E(id)}/logs", ct);
    public Task<List<EventDto>> EventsAsync(string? deviceId = null, int limit = 100, CancellationToken ct = default) =>
        Get<List<EventDto>>($"api/v1/events?limit={limit}" + (deviceId != null ? $"&device={E(deviceId)}" : ""), ct);

    // ---- rooms
    public Task<List<RoomDto>> RoomsAsync(CancellationToken ct = default) => Get<List<RoomDto>>("api/v1/rooms", ct);
    public Task<RoomDto> AddRoomAsync(RoomDto room, CancellationToken ct = default) => Send<RoomDto, RoomDto>(HttpMethod.Post, "api/v1/rooms", room, ct);
    public Task RemoveRoomAsync(string id, CancellationToken ct = default) => Send(HttpMethod.Delete, $"api/v1/rooms/{E(id)}", ct);

    // ---- users
    public Task<List<UserDto>> UsersAsync(CancellationToken ct = default) => Get<List<UserDto>>("api/v1/users", ct);
    public Task<UserDto> UpsertUserAsync(UserUpsertRequest r, CancellationToken ct = default) => Send<UserUpsertRequest, UserDto>(HttpMethod.Post, "api/v1/users", r, ct);
    public Task RemoveUserAsync(long id, CancellationToken ct = default) => Send(HttpMethod.Delete, $"api/v1/users/{id}", ct);
    public Task<List<AccessRequestDto>> AccessRequestsAsync(CancellationToken ct = default) => Get<List<AccessRequestDto>>("api/v1/access-requests", ct);
    public Task<UserDto> ApproveAsync(long id, Role role = Role.User, CancellationToken ct = default) =>
        Send<UserUpsertRequest, UserDto>(HttpMethod.Post, $"api/v1/access-requests/{id}/approve", new(id, null, role, null), ct);
    public Task DenyAsync(long id, CancellationToken ct = default) => Send(HttpMethod.Post, $"api/v1/access-requests/{id}/deny", ct);

    // ---- tokens
    public Task<List<TokenDto>> TokensAsync(CancellationToken ct = default) => Get<List<TokenDto>>("api/v1/tokens", ct);
    public Task<TokenCreatedDto> CreateTokenAsync(string name, Role role, CancellationToken ct = default) =>
        Send<TokenCreateRequest, TokenCreatedDto>(HttpMethod.Post, "api/v1/tokens", new(name, role), ct);
    public Task RevokeTokenAsync(long id, CancellationToken ct = default) => Send(HttpMethod.Delete, $"api/v1/tokens/{id}", ct);

    // ---- firmware / OTA
    public Task<List<FirmwareDto>> FirmwareAsync(CancellationToken ct = default) => Get<List<FirmwareDto>>("api/v1/firmware", ct);

    public async Task<FirmwareDto> UploadFirmwareAsync(string path, string channel = "stable", string? notes = null, CancellationToken ct = default)
    {
        using var form = new MultipartFormDataContent();
        await using var file = File.OpenRead(path);
        var content = new StreamContent(file);
        content.Headers.ContentType = new MediaTypeHeaderValue("application/octet-stream");
        form.Add(content, "file", Path.GetFileName(path));
        form.Add(new StringContent(channel), "channel");
        if (notes != null) form.Add(new StringContent(notes), "notes");
        using var resp = await _http.PostAsync("api/v1/firmware", form, ct);
        await Check(resp, ct);
        return (await resp.Content.ReadFromJsonAsync(Info<FirmwareDto>(), ct))!;
    }

    public Task RemoveFirmwareAsync(long id, CancellationToken ct = default) => Send(HttpMethod.Delete, $"api/v1/firmware/{id}", ct);
    public Task<OtaJobDto> StartOtaAsync(OtaJobRequest r, CancellationToken ct = default) => Send<OtaJobRequest, OtaJobDto>(HttpMethod.Post, "api/v1/ota/jobs", r, ct);
    public Task<List<OtaJobDto>> OtaJobsAsync(CancellationToken ct = default) => Get<List<OtaJobDto>>("api/v1/ota/jobs", ct);
    public Task<OtaJobDto> OtaJobAsync(long id, CancellationToken ct = default) => Get<OtaJobDto>($"api/v1/ota/jobs/{id}", ct);
    public Task CancelOtaAsync(long id, CancellationToken ct = default) => Send(HttpMethod.Post, $"api/v1/ota/jobs/{id}/cancel", ct);

    // ---- live stream (server-sent events)
    public async IAsyncEnumerable<StreamEvent> StreamAsync(string? logsOf = null, [EnumeratorCancellation] CancellationToken ct = default)
    {
        var url = "api/v1/stream" + (logsOf != null ? $"?logs={E(logsOf)}" : "");
        using var req = new HttpRequestMessage(HttpMethod.Get, url);
        req.Headers.Accept.Add(new MediaTypeWithQualityHeaderValue("text/event-stream"));
        using var resp = await _http.SendAsync(req, HttpCompletionOption.ResponseHeadersRead, ct);
        await Check(resp, ct);
        await using var stream = await resp.Content.ReadAsStreamAsync(ct);
        using var reader = new StreamReader(stream);
        string? type = null;
        var data = new System.Text.StringBuilder();
        while (!ct.IsCancellationRequested)
        {
            var line = await reader.ReadLineAsync(ct);
            if (line == null) yield break;
            if (line.Length == 0)
            {
                if (type != null)
                {
                    JsonNode? node = null;
                    try { node = JsonNode.Parse(data.ToString()); } catch (JsonException) { }
                    yield return new StreamEvent(type, node);
                }
                type = null;
                data.Clear();
                continue;
            }
            if (line.StartsWith("event:", StringComparison.Ordinal)) type = line[6..].Trim();
            else if (line.StartsWith("data:", StringComparison.Ordinal)) data.Append(line[5..].TrimStart());
        }
    }
}
