using System.Net;
using System.Text;
using System.Text.Json.Nodes;
using System.Threading.Channels;
using Home.Client;
using Home.Server.Auth;
using Home.Server.Data;
using Home.Server.Devices;
using Home.Server.Live;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Options;

namespace Home.Server.Bot;

/// <summary>
/// Telegram bot: long polling (no inbound connections), Mini App menu button, access requests,
/// short text commands and notifications. Disabled when no bot token is configured.
/// </summary>
public sealed class BotService(
    IOptions<HomeOptions> options,
    IHttpClientFactory httpFactory,
    IDbContextFactory<HomeDb> dbf,
    IServiceProvider services,
    ILogger<BotService> log) : BackgroundService, INotifier
{
    private readonly Channel<(long? To, string Text, bool AdminsOnly)> _outbox = Channel.CreateBounded<(long?, string, bool)>(200);
    private TelegramApi? _api;

    public string? Username { get; private set; }
    public bool Configured => options.Value.Telegram.ResolveToken() != null;

    public void Notify(string text, bool adminsOnly = false) => _outbox.Writer.TryWrite((null, text, adminsOnly));
    public void NotifyUser(long telegramId, string text) => _outbox.Writer.TryWrite((telegramId, text, false));

    protected override async Task ExecuteAsync(CancellationToken ct)
    {
        var token = options.Value.Telegram.ResolveToken();
        if (token == null || !options.Value.Telegram.BotEnabled)
        {
            log.LogInformation("Telegram bot disabled: no token (Home:Telegram:BotToken or BotTokenFile)");
            return;
        }
        var http = httpFactory.CreateClient("telegram");
        http.Timeout = TimeSpan.FromSeconds(70);
        _api = new TelegramApi(http, token);

        while (!ct.IsCancellationRequested)
        {
            try
            {
                var me = await _api.GetMeAsync(ct);
                Username = me?["username"]?.GetValue<string>();
                log.LogInformation("Telegram bot @{Bot} started", Username);
                await _api.SetCommandsAsync(ct);
                if (options.Value.PublicUrl is { Length: > 0 } url && url.StartsWith("https://", StringComparison.OrdinalIgnoreCase))
                    await _api.SetMenuButtonAsync(url, ct);
                break;
            }
            catch (OperationCanceledException) { return; }
            catch (Exception e)
            {
                log.LogWarning("Telegram bot start failed: {Error}; retry in 30 s", e.Message);
                await Task.Delay(TimeSpan.FromSeconds(30), ct).ContinueWith(_ => { });
            }
        }

        await Task.WhenAll(PollAsync(ct), SendLoopAsync(ct));
    }

    private async Task PollAsync(CancellationToken ct)
    {
        long offset = 0;
        while (!ct.IsCancellationRequested)
        {
            try
            {
                var updates = await _api!.GetUpdatesAsync(offset, 50, ct);
                foreach (var u in updates)
                {
                    offset = Math.Max(offset, u!["update_id"]!.GetValue<long>() + 1);
                    try
                    {
                        if (u["message"] is JsonObject m) await OnMessageAsync(m, ct);
                        else if (u["callback_query"] is JsonObject cq) await OnCallbackAsync(cq, ct);
                    }
                    catch (Exception e)
                    {
                        log.LogWarning(e, "bot update failed");
                    }
                }
            }
            catch (OperationCanceledException) when (ct.IsCancellationRequested) { }
            catch (Exception e)
            {
                log.LogWarning("getUpdates failed: {Error}", e.Message);
                await Task.Delay(TimeSpan.FromSeconds(10), ct).ContinueWith(_ => { });
            }
        }
    }

    private async Task SendLoopAsync(CancellationToken ct)
    {
        await foreach (var (to, text, adminsOnly) in _outbox.Reader.ReadAllAsync(ct))
        {
            try
            {
                List<long> targets;
                if (to is { } id) targets = new() { id };
                else
                {
                    await using var db = await dbf.CreateDbContextAsync(ct);
                    targets = await db.Users.Where(u => u.Notify && (!adminsOnly || u.Role == Role.Admin)).Select(u => u.TelegramId).ToListAsync(ct);
                }
                foreach (var chat in targets)
                {
                    await _api!.SendMessageAsync(chat, text, null, ct);
                    await Task.Delay(50, ct); // stay well below Telegram rate limits
                }
            }
            catch (OperationCanceledException) when (ct.IsCancellationRequested) { }
            catch (Exception e)
            {
                log.LogWarning("notification failed: {Error}", e.Message);
            }
        }
    }

    private JsonObject? OpenAppMarkup()
    {
        var url = options.Value.PublicUrl;
        if (string.IsNullOrEmpty(url) || !url.StartsWith("https://", StringComparison.OrdinalIgnoreCase)) return null;
        return new JsonObject
        {
            ["inline_keyboard"] = new JsonArray(new JsonArray(new JsonObject { ["text"] = "🏠 Открыть дом", ["web_app"] = new JsonObject { ["url"] = url } })),
        };
    }

    private async Task OnMessageAsync(JsonObject m, CancellationToken ct)
    {
        var chat = m["chat"]!["id"]!.GetValue<long>();
        var from = m["from"] as JsonObject;
        if (from == null || m["chat"]!["type"]?.GetValue<string>() != "private") return;
        var tg = new TelegramUser(from["id"]!.GetValue<long>(), from["first_name"]?.GetValue<string>() ?? "", from["last_name"]?.GetValue<string>(), from["username"]?.GetValue<string>());
        var text = (m["text"]?.GetValue<string>() ?? "").Trim();
        var cmd = text.Split(' ', '@')[0].ToLowerInvariant();

        if (cmd == "/id")
        {
            await _api!.SendMessageAsync(chat, $"Ваш Telegram id: <code>{tg.Id}</code>", null, ct);
            return;
        }

        await using var db = await dbf.CreateDbContextAsync(ct);
        var user = await db.Users.AsNoTracking().FirstOrDefaultAsync(u => u.TelegramId == tg.Id, ct);
        if (user == null)
        {
            var users = services.GetRequiredService<UserService>();
            var created = await users.RequestAccessAsync(tg);
            await _api!.SendMessageAsync(chat,
                $"Доступа к дому пока нет.\nВаш id: <code>{tg.Id}</code>\n" +
                (created ? "Запрос отправлен администраторам." : "Запрос уже отправлен, ждите подтверждения."), null, ct);
            return;
        }

        switch (cmd)
        {
            case "/status":
                await _api!.SendMessageAsync(chat, await StatusTextAsync(co2Only: false), OpenAppMarkup(), ct);
                break;
            case "/co2":
                await _api!.SendMessageAsync(chat, await StatusTextAsync(co2Only: true), OpenAppMarkup(), ct);
                break;
            default:
                await _api!.SendMessageAsync(chat, $"Привет, {WebUtility.HtmlEncode(user.Name)}! Управление домом — в приложении (кнопка «Дом» слева от поля ввода).\n/status — устройства, /co2 — уровень CO2", OpenAppMarkup(), ct);
                break;
        }
    }

    private async Task<string> StatusTextAsync(bool co2Only)
    {
        var devices = await services.GetRequiredService<DeviceManager>().ListAsync();
        var sb = new StringBuilder();
        foreach (var d in devices.Where(d => d.State == DeviceState.Adopted))
        {
            if (co2Only && !d.Values.ContainsKey("co2")) continue;
            sb.Append(d.Online ? "🟢 " : "⚪ ").Append("<b>").Append(WebUtility.HtmlEncode(d.Name)).Append("</b>");
            if (!d.Online)
            {
                sb.Append(" — не в сети\n");
                continue;
            }
            var main = d.Points.Where(p => p.Kind is "sensor" or "actuator" && !p.Advanced && p.Ui != "hidden").Take(co2Only ? 1 : 3);
            foreach (var p in main)
                if (d.Values.TryGetValue(p.Key, out var v) && (!co2Only || p.Key == "co2"))
                    sb.Append(" · ").Append(WebUtility.HtmlEncode(p.Title)).Append(": ").Append(v.Value?.ToJsonString().Trim('"')).Append(p.Unit is { } u ? " " + u : "");
            sb.Append('\n');
        }
        return sb.Length == 0 ? "Устройств пока нет." : sb.ToString();
    }

    private async Task OnCallbackAsync(JsonObject cq, CancellationToken ct)
    {
        var id = cq["id"]!.GetValue<string>();
        await _api!.AnswerCallbackAsync(id, null, ct);
    }
}

/// <summary>Watches for devices that went offline and notifies once.</summary>
public sealed class OfflineMonitor(DeviceManager devices, IDbContextFactory<HomeDb> dbf, INotifier notifier) : BackgroundService
{
    protected override async Task ExecuteAsync(CancellationToken ct)
    {
        while (!ct.IsCancellationRequested)
        {
            await Task.Delay(TimeSpan.FromMinutes(1), ct).ContinueWith(_ => { });
            if (devices.OfflineAlertMinutes <= 0 || ct.IsCancellationRequested) continue;
            try
            {
                await using var db = await dbf.CreateDbContextAsync(ct);
                var adopted = await db.Devices.AsNoTracking().Where(d => d.State == DeviceState.Adopted).ToListAsync(ct);
                foreach (var d in adopted)
                {
                    var rt = devices.Runtime(d.Id);
                    if (rt.Online || rt.OfflineAlerted || d.LastSeen == null) continue;
                    var offlineMs = Clock.NowMs - d.LastSeen.Value;
                    if (offlineMs < devices.OfflineAlertMinutes * 60_000L) continue;
                    if (offlineMs > (devices.OfflineAlertMinutes + 60) * 60_000L) continue; // long gone (e.g. before a server restart)
                    rt.OfflineAlerted = true;
                    notifier.Notify($"📴 <b>{WebUtility.HtmlEncode(d.Name)}</b> не в сети больше {devices.OfflineAlertMinutes} мин.");
                }
            }
            catch (Exception) when (!ct.IsCancellationRequested) { }
        }
    }
}
