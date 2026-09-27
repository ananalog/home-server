using System.Net;
using System.Net.Http.Json;
using System.Text.Json.Nodes;
using Home.Client;
using Home.Server.Auth;
using Xunit;
using static Home.Server.Tests.ServerFixture;

namespace Home.Server.Tests;

public class AuthTests
{
    private const string Bot = "123456:TEST-TOKEN";

    [Fact]
    public void InitDataValidation()
    {
        var user = new TelegramUser(42, "Маша", null, "masha");
        var data = TelegramInitData.Sign(user, Bot, DateTimeOffset.UtcNow);
        Assert.Equal(42, TelegramInitData.Validate(data, Bot, TimeSpan.FromHours(1)).Id);
        Assert.Throws<UnauthorizedAccessException>(() => TelegramInitData.Validate(data, "other:token", TimeSpan.FromHours(1)));
        Assert.Throws<UnauthorizedAccessException>(() => TelegramInitData.Validate(data.Replace("masha", "evil"), Bot, TimeSpan.FromHours(1)));
        var old = TelegramInitData.Sign(user, Bot, DateTimeOffset.UtcNow.AddDays(-2));
        Assert.Throws<UnauthorizedAccessException>(() => TelegramInitData.Validate(old, Bot, TimeSpan.FromHours(24)));
    }

    [Fact]
    public async Task TelegramLoginRolesAndAccessRequests()
    {
        await using var srv = await StartAsync(Bot, bootstrapAdmin: 1001);
        using var anon = srv.Http();

        var unauthorized = await Assert.ThrowsAsync<HomeApiException>(() => anon.DevicesAsync());
        Assert.Equal(HttpStatusCode.Unauthorized, unauthorized.Status);

        // Unknown person: 403 + access request.
        var stranger = TelegramInitData.Sign(new TelegramUser(2002, "Петя", null, "petya"), Bot, DateTimeOffset.UtcNow);
        var denied = await Assert.ThrowsAsync<HomeApiException>(() => anon.AuthTelegramAsync(stranger));
        Assert.Equal(HttpStatusCode.Forbidden, denied.Status);
        Assert.Contains("no_access:2002", denied.Message);

        // Bootstrap admin.
        var adminLogin = await anon.AuthTelegramAsync(TelegramInitData.Sign(new TelegramUser(1001, "Админ", null, null), Bot, DateTimeOffset.UtcNow));
        Assert.Equal(Role.Admin, adminLogin.User.Role);
        using var admin = srv.Http(adminLogin.Token);
        var requests = await admin.AccessRequestsAsync();
        Assert.Contains(requests, r => r.TelegramId == 2002);

        await admin.ApproveAsync(2002, Role.Viewer);
        Assert.Empty(await admin.AccessRequestsAsync());
        var viewerLogin = await anon.AuthTelegramAsync(stranger);
        Assert.Equal(Role.Viewer, viewerLogin.User.Role);
        using var viewer = srv.Http(viewerLogin.Token);
        Assert.NotNull(await viewer.DevicesAsync());
        var forbidden = await Assert.ThrowsAsync<HomeApiException>(() => viewer.UsersAsync());
        Assert.Equal(HttpStatusCode.Forbidden, forbidden.Status);

        // Add a user by chat id from the CLI (local socket).
        using var local = srv.Admin();
        await local.UpsertUserAsync(new UserUpsertRequest(3003, "Оля", Role.User, null));
        Assert.Contains(await local.UsersAsync(), u => u.TelegramId == 3003 && u.Role == Role.User);

        // Last admin cannot be removed.
        var lastAdmin = await Assert.ThrowsAsync<HomeApiException>(() => local.RemoveUserAsync(1001));
        Assert.Equal(HttpStatusCode.BadRequest, lastAdmin.Status);

        // Removing a user revokes the session immediately.
        await local.RemoveUserAsync(2002);
        var revoked = await Assert.ThrowsAsync<HomeApiException>(() => viewer.DevicesAsync());
        Assert.Equal(HttpStatusCode.Unauthorized, revoked.Status);
    }

    [Fact]
    public async Task ApiTokens()
    {
        await using var srv = await StartAsync();
        using var local = srv.Admin();
        Assert.True((await local.MeAsync()).Local);
        var t = await local.CreateTokenAsync("laptop", Role.Admin);
        using var remote = srv.Http(t.Token);
        Assert.Equal(Role.Admin, (await remote.MeAsync()).Role);
        await local.RevokeTokenAsync(t.Info.Id);
        var ex = await Assert.ThrowsAsync<HomeApiException>(() => remote.MeAsync());
        Assert.Equal(HttpStatusCode.Unauthorized, ex.Status);
    }

    [Fact]
    public async Task LiveStreamDeliversValues()
    {
        await using var srv = await StartAsync();
        using var api = srv.Admin();
        using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(15));
        var got = new TaskCompletionSource<string>();
        _ = Task.Run(async () =>
        {
            await foreach (var e in api.StreamAsync(null, cts.Token))
                if (e.Type == "values") { got.TrySetResult(e.Data!["deviceId"]!.GetValue<string>()); break; }
        });
        await Task.Delay(300);
        srv.StartSim("5e00000000d1");
        Assert.Equal("5e00000000d1", await got.Task.WaitAsync(cts.Token));
    }
}
