using System.Net;
using System.Reflection;
using System.Threading.RateLimiting;
using Home.Client;
using Home.Server;
using Home.Server.Api;
using Home.Server.Auth;
using Home.Server.Bot;
using Home.Server.Data;
using Home.Server.Devices;
using Home.Server.Discovery;
using Home.Server.Live;
using Home.Server.Ota;
using Home.Server.Telemetry;
using Microsoft.AspNetCore.Connections;
using Microsoft.AspNetCore.HttpOverrides;
using Microsoft.EntityFrameworkCore;

var app = HomeApp.Build(args);
await HomeApp.InitAsync(app);
await app.RunAsync();

public static class HomeApp
{
    public static WebApplication Build(string[] args, Action<WebApplicationBuilder>? configure = null)
    {
        var builder = WebApplication.CreateBuilder(new WebApplicationOptions { Args = args, ContentRootPath = AppContext.BaseDirectory });
        builder.Configuration.AddJsonFile(Environment.GetEnvironmentVariable("HOME_CONFIG") ?? "/etc/home/home.json", optional: true);
        builder.Configuration.AddEnvironmentVariables();
        configure?.Invoke(builder);

        var o = builder.Configuration.GetSection(HomeOptions.Section).Get<HomeOptions>() ?? new HomeOptions();
        builder.Services.Configure<HomeOptions>(builder.Configuration.GetSection(HomeOptions.Section));
        Directory.CreateDirectory(o.DataDir);

        builder.WebHost.ConfigureKestrel(k =>
        {
            foreach (var url in o.HttpUrls.Count > 0 ? o.HttpUrls : new List<string> { "http://127.0.0.1:8080" })
            {
                var u = new Uri(url.Replace("*", "0.0.0.0").Replace("+", "0.0.0.0"));
                if (u.Host is "localhost") k.ListenLocalhost(u.Port);
                else k.Listen(IPAddress.Parse(u.Host), u.Port);
            }
            k.ListenAnyIP(o.DevicePort, l => l.UseConnectionHandler<DeviceConnectionHandler>());
            if (!string.IsNullOrEmpty(o.UnixSocket) && !OperatingSystem.IsWindows())
            {
                Directory.CreateDirectory(Path.GetDirectoryName(o.UnixSocket)!);
                if (File.Exists(o.UnixSocket)) File.Delete(o.UnixSocket);
                k.ListenUnixSocket(o.UnixSocket, l => l.Use(next => ctx =>
                {
                    ctx.Items[HomeAuthHandler.UnixSocketItem] = true;
                    return next(ctx);
                }));
            }
            k.Limits.MaxRequestBodySize = 8 * 1024 * 1024;
        });

        var s = builder.Services;
        s.AddDbContextFactory<HomeDb>(db => db.UseSqlite($"Data Source={Path.Combine(o.DataDir, "home.db")}"));
        s.ConfigureHttpJsonOptions(j => j.SerializerOptions.TypeInfoResolverChain.Insert(0, HomeJson.Default));
        s.AddHttpClient();

        s.AddSingleton(new StartInfo(
            typeof(HomeApp).Assembly.GetCustomAttribute<AssemblyInformationalVersionAttribute>()?.InformationalVersion.Split('+')[0] ?? "dev",
            DateTimeOffset.UtcNow));
        s.AddSingleton<EventBus>();
        s.AddSingleton<EventLog>();
        s.AddSingleton<BotService>();
        s.AddSingleton<INotifier>(sp => sp.GetRequiredService<BotService>());
        s.AddHostedService(sp => sp.GetRequiredService<BotService>());
        s.AddSingleton<HistoryService>();
        s.AddHostedService(sp => sp.GetRequiredService<HistoryService>());
        s.AddSingleton<DeviceManager>();
        s.AddSingleton<OtaService>();
        s.AddHostedService(sp => sp.GetRequiredService<OtaService>());
        s.AddHostedService<OfflineMonitor>();
        s.AddHostedService<DiscoveryService>();
        s.AddSingleton<TokenService>();
        s.AddSingleton<UserService>();

        s.AddAuthentication(HomeAuthHandler.SchemeName).AddScheme<Microsoft.AspNetCore.Authentication.AuthenticationSchemeOptions, HomeAuthHandler>(HomeAuthHandler.SchemeName, null);
        s.AddAuthorizationBuilder()
            .AddPolicy(Policies.Viewer, p => p.RequireRole(nameof(Role.Viewer)))
            .AddPolicy(Policies.User, p => p.RequireRole(nameof(Role.User)))
            .AddPolicy(Policies.Admin, p => p.RequireRole(nameof(Role.Admin)));
        s.AddRateLimiter(r =>
        {
            r.RejectionStatusCode = 429;
            r.AddPolicy("auth", ctx => RateLimitPartition.GetFixedWindowLimiter(
                ctx.Connection.RemoteIpAddress?.ToString() ?? "local",
                _ => new FixedWindowRateLimiterOptions { PermitLimit = 30, Window = TimeSpan.FromMinutes(1) }));
        });
        s.Configure<ForwardedHeadersOptions>(f => f.ForwardedHeaders = ForwardedHeaders.XForwardedFor | ForwardedHeaders.XForwardedProto);

        var app = builder.Build();
        app.UseForwardedHeaders();
        app.UseMiddleware<ErrorMiddleware>();
        app.UseDefaultFiles();
        app.UseStaticFiles(new StaticFileOptions
        {
            OnPrepareResponse = c =>
            {
                // index.html must not be cached (new builds); hashed assets can be.
                c.Context.Response.Headers.CacheControl = c.File.Name == "index.html" ? "no-cache" : "public, max-age=31536000, immutable";
            },
        });
        app.UseRateLimiter();
        app.UseAuthentication();
        app.UseAuthorization();

        app.MapGet("/healthz", () => Results.Text("ok")).AllowAnonymous();
        app.MapHomeApi();
        app.MapFallback(async ctx =>
        {
            if (ctx.Request.Path.StartsWithSegments("/api"))
            {
                ctx.Response.StatusCode = 404;
                await ctx.Response.WriteAsJsonAsync(new ErrorDto("no such endpoint"), HomeJson.Default.ErrorDto);
                return;
            }
            var index = Path.Combine(app.Environment.WebRootPath ?? "wwwroot", "index.html");
            if (File.Exists(index))
            {
                ctx.Response.ContentType = "text/html; charset=utf-8";
                ctx.Response.Headers.CacheControl = "no-cache";
                await ctx.Response.SendFileAsync(index);
            }
            else await ctx.Response.WriteAsync("Home server is running. The Mini App is not built (web/miniapp).");
        });

        if (!string.IsNullOrEmpty(o.UnixSocket) && !OperatingSystem.IsWindows())
            app.Lifetime.ApplicationStarted.Register(() =>
            {
                try
                {
                    File.SetUnixFileMode(o.UnixSocket, UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.GroupRead | UnixFileMode.GroupWrite);
                }
                catch (Exception e)
                {
                    app.Logger.LogWarning("cannot chmod {Socket}: {Error}", o.UnixSocket, e.Message);
                }
            });
        return app;
    }

    /// <summary>Creates/migrates the database.</summary>
    public static async Task InitAsync(WebApplication app)
    {
        var dbf = app.Services.GetRequiredService<IDbContextFactory<HomeDb>>();
        await using var db = await dbf.CreateDbContextAsync();
        await db.Database.MigrateAsync();
        await db.Database.ExecuteSqlRawAsync("PRAGMA journal_mode=WAL;");
    }
}
