using System.Net;
using System.Net.Sockets;
using Home.Client;
using Home.Simulator;
using Microsoft.AspNetCore.Builder;
using Microsoft.Extensions.Configuration;

namespace Home.Server.Tests;

/// <summary>A real server (Kestrel, TCP gateway, SQLite in a temp dir) for integration tests.</summary>
public sealed class ServerFixture : IAsyncDisposable
{
    public required WebApplication App { get; init; }
    public required string Dir { get; init; }
    public required string Socket { get; init; }
    public required int HttpPort { get; init; }
    public required int DevicePort { get; init; }
    private readonly List<CancellationTokenSource> _sims = new();

    public static int FreePort()
    {
        var l = new TcpListener(IPAddress.Loopback, 0);
        l.Start();
        var port = ((IPEndPoint)l.LocalEndpoint).Port;
        l.Stop();
        return port;
    }

    public static async Task<ServerFixture> StartAsync(string? botToken = null, long? bootstrapAdmin = null)
    {
        var dir = Path.Combine(Path.GetTempPath(), "home-test-" + Guid.NewGuid().ToString("N")[..8]);
        Directory.CreateDirectory(dir);
        var socket = Path.Combine("/tmp", "hs-" + Guid.NewGuid().ToString("N")[..8] + ".sock");
        var http = FreePort();
        var dev = FreePort();
        var cfg = new Dictionary<string, string?>
        {
            ["Home:DataDir"] = dir,
            ["Home:UnixSocket"] = socket,
            ["Home:HttpUrls:0"] = $"http://127.0.0.1:{http}",
            ["Home:DevicePort"] = dev.ToString(),
            ["Home:DiscoveryPort"] = FreePort().ToString(),
            ["Home:Mdns"] = "false",
            ["Home:Telegram:BotEnabled"] = "false",
            ["Home:Telegram:BotToken"] = botToken,
            ["Home:Telegram:BotTokenFile"] = "",
            ["Home:BootstrapAdmin"] = bootstrapAdmin?.ToString(),
            ["Logging:LogLevel:Default"] = "Warning",
        };
        var app = HomeApp.Build(Array.Empty<string>(), b => b.Configuration.AddInMemoryCollection(cfg));
        await HomeApp.InitAsync(app);
        await app.StartAsync();
        return new ServerFixture { App = app, Dir = dir, Socket = socket, HttpPort = http, DevicePort = dev };
    }

    /// <summary>Client over the unix socket (local admin).</summary>
    public HomeApiClient Admin() => HomeApiClient.Create("unix:" + Socket);

    public HomeApiClient Http(string? token = null) => HomeApiClient.Create($"http://127.0.0.1:{HttpPort}", token);

    public SimDevice StartSim(string id, string model = "co2", bool otaFail = false)
    {
        var sim = new SimDevice(new SimOptions
        {
            Host = "127.0.0.1", Port = DevicePort, Id = id, ModelName = model, OtaFail = otaFail,
            ReportInterval = TimeSpan.FromMilliseconds(200), RebootDelay = TimeSpan.FromMilliseconds(100),
        });
        var cts = new CancellationTokenSource();
        _sims.Add(cts);
        _ = sim.RunAsync(cts.Token);
        return sim;
    }

    public static async Task Eventually(Func<Task<bool>> cond, int seconds = 10, string? what = null)
    {
        var until = DateTime.UtcNow.AddSeconds(seconds);
        while (DateTime.UtcNow < until)
        {
            try
            {
                if (await cond()) return;
            }
            catch (HomeApiException) { }
            await Task.Delay(100);
        }
        throw new TimeoutException($"condition not met in {seconds}s: {what}");
    }

    public async ValueTask DisposeAsync()
    {
        foreach (var c in _sims) c.Cancel();
        await App.StopAsync();
        await App.DisposeAsync();
        try { Directory.Delete(Dir, true); } catch { }
        try { File.Delete(Socket); } catch { }
    }
}
