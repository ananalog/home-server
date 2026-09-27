using Home.Simulator;

// home-sim — emulated Home devices for development and tests.
//   home-sim [--server host[:port]] [--model co2|relay] [--count N] [--id-prefix 5e00000000] [--interval 10] [--version 1.0.0] [--ota-fail]
// Without --server the simulator finds the server by UDP discovery (falls back to 127.0.0.1:7700).

string? server = null;
var model = "co2";
var count = 1;
var prefix = "5e00000000";
var interval = 10;
var version = "1.0.0";
var otaFail = false;
for (var i = 0; i < args.Length; i++)
{
    string Next() => i + 1 < args.Length ? args[++i] : throw new ArgumentException($"{args[i]} needs a value");
    switch (args[i])
    {
        case "--server": server = Next(); break;
        case "--model": model = Next(); break;
        case "--count": count = int.Parse(Next()); break;
        case "--id-prefix": prefix = Next(); break;
        case "--interval": interval = int.Parse(Next()); break;
        case "--version": version = Next(); break;
        case "--ota-fail": otaFail = true; break;
        case "-h" or "--help":
            Console.WriteLine("home-sim [--server host[:port]] [--model co2|relay] [--count N] [--id-prefix HEX10] [--interval SEC] [--version V] [--ota-fail]");
            return 0;
        default:
            Console.Error.WriteLine($"unknown argument {args[i]}");
            return 2;
    }
}

string host;
int port;
if (server != null)
{
    var parts = server.Split(':');
    host = parts[0];
    port = parts.Length > 1 ? int.Parse(parts[1]) : 7700;
}
else
{
    var found = await SimDevice.DiscoverAsync(TimeSpan.FromSeconds(2));
    (host, port) = found ?? ("127.0.0.1", 7700);
    Console.WriteLine(found != null ? $"found server {host}:{port}" : "server not discovered, using 127.0.0.1:7700");
}

using var cts = new CancellationTokenSource();
Console.CancelKeyPress += (_, e) => { e.Cancel = true; cts.Cancel(); };
var tasks = Enumerable.Range(1, count).Select(n =>
{
    var o = new SimOptions
    {
        Host = host, Port = port, ModelName = model, Version = version, OtaFail = otaFail,
        Id = (prefix + n.ToString("x2")).PadLeft(12, '0')[^12..],
        ReportInterval = TimeSpan.FromSeconds(interval),
    };
    return new SimDevice(o, Console.WriteLine).RunAsync(cts.Token);
}).ToList();
Console.WriteLine($"{count} simulated {model} device(s) → {host}:{port}; Ctrl+C to stop");
await Task.WhenAll(tasks);
return 0;
