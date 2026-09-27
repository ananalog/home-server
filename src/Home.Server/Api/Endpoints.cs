using System.Security.Claims;
using System.Text;
using Home.Client;
using Home.Protocol;
using Home.Server.Auth;
using Home.Server.Bot;
using Home.Server.Data;
using Home.Server.Devices;
using Home.Server.Live;
using Home.Server.Ota;
using Home.Server.Telemetry;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Options;

namespace Home.Server.Api;

public static class Endpoints
{
    public static void MapHomeApi(this WebApplication app)
    {
        var api = app.MapGroup("/api/v1");

        // ---------------------------------------------------------------- auth
        api.MapPost("/auth/telegram", async (TelegramAuthRequest req, IOptions<HomeOptions> o, UserService users, TokenService tokens) =>
        {
            var botToken = o.Value.Telegram.ResolveToken() ?? throw new InvalidOperationException("bot token is not configured on the server");
            var tg = TelegramInitData.Validate(req.InitData, botToken, TimeSpan.FromHours(o.Value.Telegram.InitDataMaxAgeHours));
            var user = await users.LoginAsync(tg);
            var (token, exp) = tokens.IssueSession(user.TelegramId);
            return Results.Ok(new AuthResponse(token, UserService.ToDto(user), exp));
        }).AllowAnonymous().RequireRateLimiting("auth");

        api.MapPost("/auth/dev", async (IOptions<HomeOptions> o, IWebHostEnvironment env, UserService users, TokenService tokens) =>
        {
            if (!env.IsDevelopment() || o.Value.DevUserId is not { } id) return Results.NotFound();
            var user = await users.LoginAsync(new TelegramUser(id, "Developer", null, "dev"));
            var (token, exp) = tokens.IssueSession(user.TelegramId);
            return Results.Ok(new AuthResponse(token, UserService.ToDto(user), exp));
        }).AllowAnonymous();

        api.MapGet("/me", (ClaimsPrincipal u) => new MeDto(u.TelegramId(), u.Actor(), u.Role(), u.IsLocal()))
            .RequireAuthorization(Policies.Viewer);

        api.MapPatch("/me", async (ClaimsPrincipal u, UserUpsertRequest r, UserService users) =>
        {
            if (u.TelegramId() is not { } id) return Results.BadRequest(new ErrorDto("not a Telegram user"));
            return Results.Ok(await users.UpsertAsync(new UserUpsertRequest(id, null, null, r.Notify), u.Actor()));
        }).RequireAuthorization(Policies.Viewer);

        api.MapGet("/system", async (IOptions<HomeOptions> o, DeviceManager dm, BotService bot, StartInfo start) =>
        {
            var list = await dm.ListAsync();
            var dbPath = Path.Combine(o.Value.DataDir, "home.db");
            long free = 0;
            try { free = new DriveInfo(Path.GetFullPath(o.Value.DataDir)).AvailableFreeSpace; } catch { }
            return new SystemDto
            {
                Version = start.Version,
                StartedAt = start.StartedAt,
                Devices = list.Count(d => d.State == DeviceState.Adopted),
                Online = list.Count(d => d.State == DeviceState.Adopted && d.Online),
                NewDevices = list.Count(d => d.State == DeviceState.New),
                DbSize = File.Exists(dbPath) ? new FileInfo(dbPath).Length : 0,
                DiskFree = free,
                PublicUrl = o.Value.PublicUrl,
                BotConfigured = bot.Configured,
                BotUsername = bot.Username,
                DevicePort = o.Value.DevicePort,
                ProtocolMajor = ProtoInfo.Major,
                ProtocolMinor = ProtoInfo.Minor,
            };
        }).RequireAuthorization(Policies.Viewer);

        // ---------------------------------------------------------------- devices
        var dev = api.MapGroup("/devices");

        dev.MapGet("", (DeviceManager dm) => dm.ListAsync()).RequireAuthorization(Policies.Viewer);

        dev.MapGet("/{id}", async (string id, DeviceManager dm) =>
            await dm.GetAsync(id) is { } d ? Results.Ok(d) : Results.NotFound(new ErrorDto($"device '{id}' not found")))
            .RequireAuthorization(Policies.Viewer);

        dev.MapPatch("/{id}", async (string id, DeviceUpdateRequest r, DeviceManager dm, ClaimsPrincipal u) =>
            await dm.UpdateAsync(await Resolve(dm, id), r, u.Actor())).RequireAuthorization(Policies.Admin);

        dev.MapPost("/{id}/adopt", async (string id, DeviceManager dm, ClaimsPrincipal u) =>
        {
            await dm.SetStateAsync(await Resolve(dm, id), DeviceState.Adopted, u.Actor());
            return Results.NoContent();
        }).RequireAuthorization(Policies.Admin);

        dev.MapPost("/{id}/reject", async (string id, DeviceManager dm, ClaimsPrincipal u) =>
        {
            await dm.SetStateAsync(await Resolve(dm, id), DeviceState.Blocked, u.Actor());
            return Results.NoContent();
        }).RequireAuthorization(Policies.Admin);

        dev.MapDelete("/{id}", async (string id, DeviceManager dm, ClaimsPrincipal u) =>
        {
            await dm.RemoveAsync(await Resolve(dm, id), u.Actor());
            return Results.NoContent();
        }).RequireAuthorization(Policies.Admin);

        dev.MapPost("/{id}/points/{key}", async (string id, string key, SetValueRequest r, DeviceManager dm, ClaimsPrincipal u) =>
            await dm.SetAsync(await Resolve(dm, id), key, r.Value, u.Actor())).RequireAuthorization(Policies.User);

        dev.MapPost("/{id}/actions/{key}", async (string id, string key, InvokeRequest r, DeviceManager dm, ClaimsPrincipal u) =>
            new InvokeResponse(await dm.InvokeAsync(await Resolve(dm, id), key, r.Arg, u.Actor()))).RequireAuthorization(Policies.User);

        dev.MapPost("/{id}/reboot", async (string id, DeviceManager dm, ClaimsPrincipal u) =>
        {
            await dm.RebootAsync(await Resolve(dm, id), u.Actor());
            return Results.NoContent();
        }).RequireAuthorization(Policies.Admin);

        dev.MapPost("/{id}/identify", async (string id, [FromBody] IdentifyRequest? r, DeviceManager dm) =>
        {
            await dm.IdentifyAsync(await Resolve(dm, id), r?.Seconds ?? 10);
            return Results.NoContent();
        }).RequireAuthorization(Policies.User);

        dev.MapPost("/{id}/factory-reset", async (string id, FactoryResetRequest r, DeviceManager dm, ClaimsPrincipal u) =>
        {
            await dm.FactoryResetAsync(await Resolve(dm, id), r.Mode, u.Actor());
            return Results.NoContent();
        }).RequireAuthorization(Policies.Admin);

        dev.MapGet("/{id}/network", async (string id, DeviceManager dm) => await dm.NetStatusAsync(await Resolve(dm, id)))
            .RequireAuthorization(Policies.Admin);

        dev.MapPut("/{id}/network", async (string id, NetworkDto n, DeviceManager dm, ClaimsPrincipal u) =>
        {
            await dm.SetNetworkAsync(await Resolve(dm, id), n, u.Actor());
            return Results.NoContent();
        }).RequireAuthorization(Policies.Admin);

        dev.MapGet("/{id}/config", async (string id, DeviceManager dm) => await dm.ConfigAsync(await Resolve(dm, id)))
            .RequireAuthorization(Policies.Admin);

        dev.MapGet("/{id}/history", async (string id, string point, long? from, long? to, long? step, DeviceManager dm, HistoryService history) =>
        {
            var devId = await Resolve(dm, id);
            var p = dm.PointOf(devId, point);
            return await history.QueryAsync(devId, p.Id, p.Key, from ?? 0, to ?? 0, step ?? 0);
        }).RequireAuthorization(Policies.Viewer);

        dev.MapGet("/{id}/logs", async (string id, DeviceManager dm) => dm.Logs(await Resolve(dm, id)))
            .RequireAuthorization(Policies.Admin);

        api.MapGet("/events", async (string? device, int? limit, IDbContextFactory<HomeDb> dbf, DeviceManager dm) =>
        {
            await using var db = await dbf.CreateDbContextAsync();
            var q = db.Events.AsNoTracking();
            if (device != null)
            {
                var devId = await Resolve(dm, device);
                q = q.Where(e => e.DeviceId == devId);
            }
            var list = await q.OrderByDescending(e => e.Id).Take(Math.Clamp(limit ?? 100, 1, 1000)).ToListAsync();
            return list.Select(EventLog.ToDto).ToList();
        }).RequireAuthorization(Policies.Viewer);

        // ---------------------------------------------------------------- rooms
        api.MapGet("/rooms", async (IDbContextFactory<HomeDb> dbf) =>
        {
            await using var db = await dbf.CreateDbContextAsync();
            return (await db.Rooms.AsNoTracking().ToListAsync()).OrderBy(r => r.Sort).ThenBy(r => r.Name).Select(r => new RoomDto(r.Id, r.Name, r.Sort)).ToList();
        }).RequireAuthorization(Policies.Viewer);

        api.MapPost("/rooms", async (RoomDto r, IDbContextFactory<HomeDb> dbf) =>
        {
            var id = r.Id.Trim().ToLowerInvariant();
            if (id.Length == 0 || !id.All(c => char.IsAsciiLetterOrDigit(c) || c is '-' or '_')) throw new ArgumentException("room id: latin letters, digits, '-' or '_'");
            await using var db = await dbf.CreateDbContextAsync();
            var e = await db.Rooms.FindAsync(id);
            if (e == null) db.Rooms.Add(e = new RoomEntity { Id = id });
            e.Name = string.IsNullOrWhiteSpace(r.Name) ? id : r.Name.Trim();
            e.Sort = r.Sort;
            await db.SaveChangesAsync();
            return new RoomDto(e.Id, e.Name, e.Sort);
        }).RequireAuthorization(Policies.Admin);

        api.MapDelete("/rooms/{id}", async (string id, IDbContextFactory<HomeDb> dbf) =>
        {
            await using var db = await dbf.CreateDbContextAsync();
            var e = await db.Rooms.FindAsync(id) ?? throw new KeyNotFoundException("room not found");
            db.Rooms.Remove(e);
            await db.Devices.Where(d => d.Room == id).ExecuteUpdateAsync(s => s.SetProperty(d => d.Room, (string?)null));
            await db.SaveChangesAsync();
            return Results.NoContent();
        }).RequireAuthorization(Policies.Admin);

        // ---------------------------------------------------------------- users and tokens
        api.MapGet("/users", (UserService users) => users.ListAsync()).RequireAuthorization(Policies.Admin);
        api.MapPost("/users", (UserUpsertRequest r, UserService users, ClaimsPrincipal u) => users.UpsertAsync(r, u.Actor())).RequireAuthorization(Policies.Admin);
        api.MapDelete("/users/{id:long}", async (long id, UserService users, ClaimsPrincipal u) =>
        {
            await users.RemoveAsync(id, u.Actor());
            return Results.NoContent();
        }).RequireAuthorization(Policies.Admin);

        api.MapGet("/access-requests", (UserService users) => users.RequestsAsync()).RequireAuthorization(Policies.Admin);
        api.MapPost("/access-requests/{id:long}/approve", (long id, [FromBody] UserUpsertRequest? r, UserService users, ClaimsPrincipal u) =>
            users.UpsertAsync(new UserUpsertRequest(id, r?.Name, r?.Role ?? Role.User, r?.Notify), u.Actor())).RequireAuthorization(Policies.Admin);
        api.MapPost("/access-requests/{id:long}/deny", async (long id, UserService users, ClaimsPrincipal u) =>
        {
            await users.DenyAsync(id, u.Actor());
            return Results.NoContent();
        }).RequireAuthorization(Policies.Admin);

        api.MapGet("/tokens", async (IDbContextFactory<HomeDb> dbf) =>
        {
            await using var db = await dbf.CreateDbContextAsync();
            return (await db.ApiTokens.AsNoTracking().ToListAsync()).Select(TokenService.ToDto).ToList();
        }).RequireAuthorization(Policies.Admin);
        api.MapPost("/tokens", (TokenCreateRequest r, TokenService tokens) => tokens.CreateApiTokenAsync(r.Name, r.Role)).RequireAuthorization(Policies.Admin);
        api.MapDelete("/tokens/{id:long}", async (long id, IDbContextFactory<HomeDb> dbf) =>
        {
            await using var db = await dbf.CreateDbContextAsync();
            var n = await db.ApiTokens.Where(t => t.Id == id).ExecuteDeleteAsync();
            return n == 0 ? Results.NotFound(new ErrorDto("token not found")) : Results.NoContent();
        }).RequireAuthorization(Policies.Admin);

        // ---------------------------------------------------------------- firmware and OTA
        api.MapGet("/firmware", (OtaService ota) => ota.ListFirmwareAsync()).RequireAuthorization(Policies.Viewer);
        api.MapPost("/firmware", async (HttpRequest req, OtaService ota, ClaimsPrincipal u) =>
        {
            if (!req.HasFormContentType) throw new ArgumentException("multipart form with a 'file' field expected");
            var form = await req.ReadFormAsync();
            var file = form.Files["file"] ?? throw new ArgumentException("no 'file' in the form");
            if (file.Length > 4 * 1024 * 1024) throw new ArgumentException("image is larger than 4 MB");
            using var ms = new MemoryStream();
            await file.CopyToAsync(ms);
            return await ota.UploadAsync(ms.ToArray(), form["channel"], form["notes"], u.Actor());
        }).RequireAuthorization(Policies.Admin).DisableAntiforgery();
        api.MapDelete("/firmware/{id:long}", async (long id, OtaService ota) =>
        {
            await ota.DeleteFirmwareAsync(id);
            return Results.NoContent();
        }).RequireAuthorization(Policies.Admin);

        api.MapPost("/ota/jobs", (OtaJobRequest r, OtaService ota, ClaimsPrincipal u) => ota.StartAsync(r, u.Actor())).RequireAuthorization(Policies.Admin);
        api.MapGet("/ota/jobs", (OtaService ota) => ota.ListJobsAsync()).RequireAuthorization(Policies.Viewer);
        api.MapGet("/ota/jobs/{id:long}", async (long id, OtaService ota) =>
            await ota.GetJobAsync(id) is { } j ? Results.Ok(j) : Results.NotFound(new ErrorDto("job not found"))).RequireAuthorization(Policies.Viewer);
        api.MapPost("/ota/jobs/{id:long}/cancel", async (long id, OtaService ota) =>
        {
            await ota.CancelAsync(id);
            return Results.NoContent();
        }).RequireAuthorization(Policies.Admin);

        // ---------------------------------------------------------------- live stream (SSE)
        api.MapGet("/stream", async (HttpContext ctx, EventBus bus, DeviceManager dm, string? logs) =>
        {
            string? logsOf = null;
            if (logs != null)
            {
                if (!ctx.User.IsInRole(nameof(Role.Admin))) { ctx.Response.StatusCode = 403; return; }
                logsOf = await Resolve(dm, logs);
            }
            ctx.Response.Headers.ContentType = "text/event-stream";
            ctx.Response.Headers.CacheControl = "no-cache";
            ctx.Response.Headers["X-Accel-Buffering"] = "no";
            using var sub = bus.Subscribe(logsOf);
            var ct = ctx.RequestAborted;
            await ctx.Response.WriteAsync(": connected\n\n", ct);
            await ctx.Response.Body.FlushAsync(ct);
            while (!ct.IsCancellationRequested)
            {
                using var wait = CancellationTokenSource.CreateLinkedTokenSource(ct);
                wait.CancelAfter(TimeSpan.FromSeconds(20));
                var sb = new StringBuilder();
                try
                {
                    if (await sub.Reader.WaitToReadAsync(wait.Token))
                        while (sub.Reader.TryRead(out var e)) sb.Append("event: ").Append(e.Type).Append("\ndata: ").Append(e.Json).Append("\n\n");
                }
                catch (OperationCanceledException) when (!ct.IsCancellationRequested)
                {
                    sb.Append(": ping\n\n");
                }
                catch (OperationCanceledException) { break; }
                if (sb.Length == 0) continue;
                await ctx.Response.WriteAsync(sb.ToString(), ct);
                await ctx.Response.Body.FlushAsync(ct);
            }
        }).RequireAuthorization(Policies.Viewer);
    }

    private static async Task<string> Resolve(DeviceManager dm, string idOrName) =>
        (await dm.FindAsync(idOrName))?.Id ?? throw new KeyNotFoundException($"device '{idOrName}' not found");
}

/// <summary>Maps exceptions to JSON errors with sensible status codes.</summary>
public sealed class ErrorMiddleware(RequestDelegate next, ILogger<ErrorMiddleware> log)
{
    public async Task InvokeAsync(HttpContext ctx)
    {
        try
        {
            await next(ctx);
        }
        catch (Exception e) when (!ctx.Response.HasStarted)
        {
            var (status, msg) = e switch
            {
                NoAccessException na => (403, $"no_access:{na.TelegramId}"),
                UnauthorizedAccessException => (401, e.Message),
                KeyNotFoundException => (404, e.Message),
                ArgumentException or InvalidDataException or BadHttpRequestException or FormatException => (400, e.Message),
                ProtoException => (400, e.Message),
                DeviceException { Code: ErrorCode.Timeout } => (504, e.Message),
                DeviceException { Code: ErrorCode.Forbidden } => (409, e.Message),
                DeviceException { Code: ErrorCode.InvalidValue or ErrorCode.BadRequest or ErrorCode.NotFound } => (400, $"device: {e.Message}"),
                DeviceException => (502, $"device: {e.Message}"),
                InvalidOperationException => (503, e.Message),
                OperationCanceledException => (499, "cancelled"),
                _ => (500, "internal error"),
            };
            if (status == 500) log.LogError(e, "request {Path} failed", ctx.Request.Path);
            ctx.Response.StatusCode = status;
            await ctx.Response.WriteAsJsonAsync(new ErrorDto(msg), HomeJson.Default.ErrorDto);
        }
    }
}

public sealed record StartInfo(string Version, DateTimeOffset StartedAt);
