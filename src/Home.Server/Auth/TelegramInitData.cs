using System.Security.Cryptography;
using System.Text;
using System.Text.Json;

namespace Home.Server.Auth;

public sealed record TelegramUser(long Id, string FirstName, string? LastName, string? Username)
{
    public string DisplayName => string.Join(' ', new[] { FirstName, LastName }.Where(s => !string.IsNullOrWhiteSpace(s)));
}

/// <summary>
/// Validates Telegram Mini App initData (https://core.telegram.org/bots/webapps#validating-data-received-via-the-mini-app):
/// secret = HMAC_SHA256(key="WebAppData", msg=bot_token); hash = HMAC_SHA256(key=secret, msg=data_check_string).
/// </summary>
public static class TelegramInitData
{
    public static TelegramUser Validate(string initData, string botToken, TimeSpan maxAge, DateTimeOffset? now = null)
    {
        var pairs = new List<(string Key, string Value)>();
        foreach (var part in initData.Split('&', StringSplitOptions.RemoveEmptyEntries))
        {
            var eq = part.IndexOf('=');
            if (eq <= 0) continue;
            pairs.Add((Uri.UnescapeDataString(part[..eq].Replace('+', ' ')), Uri.UnescapeDataString(part[(eq + 1)..].Replace('+', ' '))));
        }
        var hash = pairs.FirstOrDefault(p => p.Key == "hash").Value ?? throw new UnauthorizedAccessException("initData has no hash");
        var dataCheck = string.Join('\n', pairs.Where(p => p.Key != "hash").OrderBy(p => p.Key, StringComparer.Ordinal).Select(p => $"{p.Key}={p.Value}"));
        var secret = HMACSHA256.HashData(Encoding.UTF8.GetBytes("WebAppData"), Encoding.UTF8.GetBytes(botToken));
        var expected = HMACSHA256.HashData(secret, Encoding.UTF8.GetBytes(dataCheck));
        byte[] given;
        try
        {
            given = Convert.FromHexString(hash);
        }
        catch (FormatException)
        {
            throw new UnauthorizedAccessException("bad initData hash");
        }
        if (!CryptographicOperations.FixedTimeEquals(expected, given)) throw new UnauthorizedAccessException("initData signature mismatch");

        var authDate = long.TryParse(pairs.FirstOrDefault(p => p.Key == "auth_date").Value, out var ad) ? DateTimeOffset.FromUnixTimeSeconds(ad) : DateTimeOffset.MinValue;
        if ((now ?? DateTimeOffset.UtcNow) - authDate > maxAge) throw new UnauthorizedAccessException("initData is too old, reopen the Mini App");

        var userJson = pairs.FirstOrDefault(p => p.Key == "user").Value ?? throw new UnauthorizedAccessException("initData has no user");
        using var doc = JsonDocument.Parse(userJson);
        var u = doc.RootElement;
        return new TelegramUser(
            u.GetProperty("id").GetInt64(),
            u.TryGetProperty("first_name", out var fn) ? fn.GetString() ?? "" : "",
            u.TryGetProperty("last_name", out var ln) ? ln.GetString() : null,
            u.TryGetProperty("username", out var un) ? un.GetString() : null);
    }

    /// <summary>Builds signed initData (tests and local development).</summary>
    public static string Sign(TelegramUser user, string botToken, DateTimeOffset authDate)
    {
        var userJson = JsonSerializer.Serialize(new Dictionary<string, object?>
        {
            ["id"] = user.Id, ["first_name"] = user.FirstName, ["last_name"] = user.LastName, ["username"] = user.Username,
        });
        var fields = new SortedDictionary<string, string>(StringComparer.Ordinal)
        {
            ["auth_date"] = authDate.ToUnixTimeSeconds().ToString(),
            ["query_id"] = "AAE-test",
            ["user"] = userJson,
        };
        var dataCheck = string.Join('\n', fields.Select(p => $"{p.Key}={p.Value}"));
        var secret = HMACSHA256.HashData(Encoding.UTF8.GetBytes("WebAppData"), Encoding.UTF8.GetBytes(botToken));
        var hash = Convert.ToHexStringLower(HMACSHA256.HashData(secret, Encoding.UTF8.GetBytes(dataCheck)));
        return string.Join('&', fields.Select(p => $"{p.Key}={Uri.EscapeDataString(p.Value)}")) + $"&hash={hash}";
    }
}
