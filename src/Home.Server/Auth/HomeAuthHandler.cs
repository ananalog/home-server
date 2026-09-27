using System.Net.Sockets;
using System.Security.Claims;
using System.Text.Encodings.Web;
using Home.Client;
using Home.Server.Data;
using Microsoft.AspNetCore.Authentication;
using Microsoft.AspNetCore.Connections.Features;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Options;

namespace Home.Server.Auth;

/// <summary>
/// Authenticates: local unix-socket connections (admin), API tokens "hk_…", and Mini App session tokens.
/// Tokens come from "Authorization: Bearer" or the access_token query parameter (for EventSource).
/// Role claims are hierarchical: an admin also has User and Viewer.
/// </summary>
public sealed class HomeAuthHandler(
    IOptionsMonitor<AuthenticationSchemeOptions> options,
    ILoggerFactory logger,
    UrlEncoder encoder,
    TokenService tokens,
    IDbContextFactory<HomeDb> dbf) : AuthenticationHandler<AuthenticationSchemeOptions>(options, logger, encoder)
{
    public const string SchemeName = "home";
    public const string ClaimTelegramId = "tg";
    public const string ClaimLocal = "local";

    public const string UnixSocketItem = "home.unix";

    /// <summary>True for requests that came through the local unix socket (marked by a connection middleware).</summary>
    public static bool IsUnixSocket(HttpContext ctx) =>
        ctx.Features.Get<IConnectionItemsFeature>()?.Items.ContainsKey(UnixSocketItem) == true
        || ctx.Features.Get<IConnectionEndPointFeature>()?.LocalEndPoint is UnixDomainSocketEndPoint;

    protected override async Task<AuthenticateResult> HandleAuthenticateAsync()
    {
        if (IsUnixSocket(Context)) return Success("local", "root (local)", Role.Admin, null, local: true);

        string? token = null;
        var header = Request.Headers.Authorization.ToString();
        if (header.StartsWith("Bearer ", StringComparison.OrdinalIgnoreCase)) token = header[7..].Trim();
        else if (Request.Query.TryGetValue("access_token", out var q)) token = q.ToString();
        if (string.IsNullOrEmpty(token)) return AuthenticateResult.NoResult();

        if (token.StartsWith("hk_", StringComparison.Ordinal))
        {
            var t = await tokens.ValidateApiTokenAsync(token);
            return t == null ? AuthenticateResult.Fail("unknown token") : Success($"token:{t.Id}", $"token {t.Name}", t.Role, null);
        }

        var tgId = tokens.ValidateSession(token);
        if (tgId == null) return AuthenticateResult.Fail("invalid or expired session");
        await using var db = await dbf.CreateDbContextAsync();
        var user = await db.Users.AsNoTracking().FirstOrDefaultAsync(u => u.TelegramId == tgId);
        return user == null ? AuthenticateResult.Fail("access revoked") : Success($"tg:{user.TelegramId}", user.Name, user.Role, user.TelegramId);
    }

    private AuthenticateResult Success(string id, string name, Role role, long? telegramId, bool local = false)
    {
        var claims = new List<Claim> { new(ClaimTypes.NameIdentifier, id), new(ClaimTypes.Name, name) };
        claims.Add(new Claim(ClaimTypes.Role, nameof(Role.Viewer)));
        if (role >= Role.User) claims.Add(new Claim(ClaimTypes.Role, nameof(Role.User)));
        if (role >= Role.Admin) claims.Add(new Claim(ClaimTypes.Role, nameof(Role.Admin)));
        if (telegramId is { } tg) claims.Add(new Claim(ClaimTelegramId, tg.ToString()));
        if (local) claims.Add(new Claim(ClaimLocal, "1"));
        var principal = new ClaimsPrincipal(new ClaimsIdentity(claims, SchemeName));
        return AuthenticateResult.Success(new AuthenticationTicket(principal, SchemeName));
    }

    protected override Task HandleChallengeAsync(AuthenticationProperties properties)
    {
        Response.StatusCode = 401;
        return Response.WriteAsJsonAsync(new ErrorDto("unauthorized"), HomeJson.Default.ErrorDto);
    }

    protected override Task HandleForbiddenAsync(AuthenticationProperties properties)
    {
        Response.StatusCode = 403;
        return Response.WriteAsJsonAsync(new ErrorDto("forbidden: not enough rights"), HomeJson.Default.ErrorDto);
    }
}

public static class Policies
{
    public const string Viewer = "viewer";
    public const string User = "user";
    public const string Admin = "admin";

    public static string Actor(this ClaimsPrincipal p) => p.Identity?.Name ?? "?";
    public static long? TelegramId(this ClaimsPrincipal p) => long.TryParse(p.FindFirst(HomeAuthHandler.ClaimTelegramId)?.Value, out var id) ? id : null;
    public static bool IsLocal(this ClaimsPrincipal p) => p.HasClaim(HomeAuthHandler.ClaimLocal, "1");
    public static Role Role(this ClaimsPrincipal p) =>
        p.IsInRole(nameof(Home.Client.Role.Admin)) ? Home.Client.Role.Admin : p.IsInRole(nameof(Home.Client.Role.User)) ? Home.Client.Role.User : Home.Client.Role.Viewer;
}
