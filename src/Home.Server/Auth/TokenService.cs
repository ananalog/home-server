using System.Security.Cryptography;
using System.Text;
using Home.Client;
using Home.Server.Data;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Options;

namespace Home.Server.Auth;

/// <summary>
/// Session tokens for Mini App users (HMAC-signed "tg.{id}.{expiry}", role looked up on every request)
/// and API tokens for homectl ("hk_..." stored as SHA-256 hash).
/// </summary>
public sealed class TokenService
{
    public static readonly TimeSpan SessionLifetime = TimeSpan.FromDays(7);
    private readonly byte[] _key;
    private readonly IDbContextFactory<HomeDb> _dbf;

    public TokenService(IOptions<HomeOptions> options, IDbContextFactory<HomeDb> dbf)
    {
        _dbf = dbf;
        var dir = options.Value.DataDir;
        Directory.CreateDirectory(dir);
        var path = Path.Combine(dir, "server.key");
        if (File.Exists(path)) _key = File.ReadAllBytes(path);
        else
        {
            _key = RandomNumberGenerator.GetBytes(32);
            File.WriteAllBytes(path, _key);
            if (!OperatingSystem.IsWindows()) File.SetUnixFileMode(path, UnixFileMode.UserRead | UnixFileMode.UserWrite);
        }
    }

    public (string Token, DateTimeOffset Expires) IssueSession(long telegramId)
    {
        var exp = DateTimeOffset.UtcNow.Add(SessionLifetime);
        var payload = $"tg.{telegramId}.{exp.ToUnixTimeSeconds()}";
        return ($"{payload}.{Sign(payload)}", exp);
    }

    public long? ValidateSession(string token)
    {
        var last = token.LastIndexOf('.');
        if (last <= 0) return null;
        var payload = token[..last];
        if (!CryptographicOperations.FixedTimeEquals(Encoding.ASCII.GetBytes(Sign(payload)), Encoding.ASCII.GetBytes(token[(last + 1)..]))) return null;
        var parts = payload.Split('.');
        if (parts.Length != 3 || parts[0] != "tg" || !long.TryParse(parts[1], out var id) || !long.TryParse(parts[2], out var exp)) return null;
        return DateTimeOffset.UtcNow.ToUnixTimeSeconds() < exp ? id : null;
    }

    private string Sign(string payload) =>
        Convert.ToBase64String(HMACSHA256.HashData(_key, Encoding.UTF8.GetBytes(payload))).TrimEnd('=').Replace('+', '-').Replace('/', '_');

    public static string Hash(string token) => Convert.ToHexStringLower(SHA256.HashData(Encoding.UTF8.GetBytes(token)));

    public async Task<TokenCreatedDto> CreateApiTokenAsync(string name, Role role)
    {
        if (string.IsNullOrWhiteSpace(name)) throw new ArgumentException("token name is required");
        var token = "hk_" + Convert.ToBase64String(RandomNumberGenerator.GetBytes(24)).TrimEnd('=').Replace('+', '-').Replace('/', '_');
        await using var db = await _dbf.CreateDbContextAsync();
        var e = new ApiTokenEntity { Name = name.Trim(), Hash = Hash(token), Role = role, CreatedAt = Clock.NowMs };
        db.ApiTokens.Add(e);
        await db.SaveChangesAsync();
        return new TokenCreatedDto(token, ToDto(e));
    }

    public async Task<ApiTokenEntity?> ValidateApiTokenAsync(string token)
    {
        await using var db = await _dbf.CreateDbContextAsync();
        var hash = Hash(token);
        var e = await db.ApiTokens.FirstOrDefaultAsync(t => t.Hash == hash);
        if (e == null) return null;
        if (e.LastUsedAt == null || Clock.NowMs - e.LastUsedAt > 60_000)
        {
            e.LastUsedAt = Clock.NowMs;
            await db.SaveChangesAsync();
        }
        return e;
    }

    public static TokenDto ToDto(ApiTokenEntity e) =>
        new(e.Id, e.Name, e.Role, Clock.FromMs(e.CreatedAt), e.LastUsedAt is { } l ? Clock.FromMs(l) : null);
}
