using System.Net.Http.Json;
using System.Text.Json;
using System.Text.Json.Nodes;

namespace Home.Server.Bot;

/// <summary>Minimal Telegram Bot API client (only the methods the home bot needs).</summary>
public sealed class TelegramApi(HttpClient http, string token)
{
    private readonly string _base = $"https://api.telegram.org/bot{token}/";

    public async Task<JsonNode?> CallAsync(string method, JsonObject? args = null, CancellationToken ct = default)
    {
        using var content = new StringContent((args ?? new JsonObject()).ToJsonString(), System.Text.Encoding.UTF8, "application/json");
        using var resp = await http.PostAsync(_base + method, content, ct);
        var body = await resp.Content.ReadAsStringAsync(ct);
        var node = JsonNode.Parse(body);
        if (node?["ok"]?.GetValue<bool>() != true)
            throw new InvalidOperationException($"telegram {method}: {node?["description"]?.GetValue<string>() ?? resp.StatusCode.ToString()}");
        return node["result"];
    }

    public Task<JsonNode?> GetMeAsync(CancellationToken ct) => CallAsync("getMe", null, ct);

    public async Task<JsonArray> GetUpdatesAsync(long offset, int timeoutSec, CancellationToken ct) =>
        (await CallAsync("getUpdates", new JsonObject
        {
            ["offset"] = offset,
            ["timeout"] = timeoutSec,
            ["allowed_updates"] = new JsonArray("message", "callback_query"),
        }, ct))?.AsArray() ?? new JsonArray();

    public Task SendMessageAsync(long chatId, string html, JsonObject? replyMarkup = null, CancellationToken ct = default)
    {
        var args = new JsonObject
        {
            ["chat_id"] = chatId,
            ["text"] = html,
            ["parse_mode"] = "HTML",
            ["link_preview_options"] = new JsonObject { ["is_disabled"] = true },
        };
        if (replyMarkup != null) args["reply_markup"] = replyMarkup;
        return CallAsync("sendMessage", args, ct);
    }

    public Task AnswerCallbackAsync(string id, string? text, CancellationToken ct) =>
        CallAsync("answerCallbackQuery", new JsonObject { ["callback_query_id"] = id, ["text"] = text }, ct);

    public Task EditMessageTextAsync(long chatId, long messageId, string html, CancellationToken ct) =>
        CallAsync("editMessageText", new JsonObject { ["chat_id"] = chatId, ["message_id"] = messageId, ["text"] = html, ["parse_mode"] = "HTML" }, ct);

    public Task SetMenuButtonAsync(string url, CancellationToken ct) =>
        CallAsync("setChatMenuButton", new JsonObject
        {
            ["menu_button"] = new JsonObject { ["type"] = "web_app", ["text"] = "Дом", ["web_app"] = new JsonObject { ["url"] = url } },
        }, ct);

    public Task SetCommandsAsync(CancellationToken ct) =>
        CallAsync("setMyCommands", new JsonObject
        {
            ["commands"] = new JsonArray(
                new JsonObject { ["command"] = "start", ["description"] = "Открыть дом" },
                new JsonObject { ["command"] = "status", ["description"] = "Состояние устройств" },
                new JsonObject { ["command"] = "co2", ["description"] = "Уровень CO2" },
                new JsonObject { ["command"] = "id", ["description"] = "Мой Telegram id" }),
        }, ct);
}
