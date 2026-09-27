using System.Globalization;
using System.Text.Json;
using System.Text.Json.Nodes;
using Home.Client;

namespace Home.Cli;

/// <summary>Where homectl connects: flags → env (HOME_SERVER/HOME_TOKEN) → ~/.config/homectl/config.json → local unix socket.</summary>
public static class Config
{
    public static string Path => System.IO.Path.Combine(
        Environment.GetEnvironmentVariable("XDG_CONFIG_HOME") ?? System.IO.Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), ".config"),
        "homectl", "config.json");

    public static ClientConfig? Load()
    {
        try
        {
            return File.Exists(Path) ? JsonSerializer.Deserialize(File.ReadAllText(Path), HomeJson.Default.ClientConfig) : null;
        }
        catch (JsonException)
        {
            return null;
        }
    }

    public static void Save(ClientConfig c)
    {
        Directory.CreateDirectory(System.IO.Path.GetDirectoryName(Path)!);
        File.WriteAllText(Path, JsonSerializer.Serialize(c, HomeJson.Default.ClientConfig));
        if (!OperatingSystem.IsWindows()) File.SetUnixFileMode(Path, UnixFileMode.UserRead | UnixFileMode.UserWrite);
    }

    public static HomeApiClient CreateClient(string? server, string? token)
    {
        server ??= Environment.GetEnvironmentVariable("HOME_SERVER");
        token ??= Environment.GetEnvironmentVariable("HOME_TOKEN");
        if (server == null)
        {
            var saved = Load();
            if (saved?.Server != null)
            {
                server = saved.Server;
                token ??= saved.Token;
            }
        }
        if (server == null && File.Exists(HomeApiClient.DefaultSocket)) server = "unix:" + HomeApiClient.DefaultSocket;
        if (server == null)
            throw new ArgumentException("no server: run on the server itself (unix socket), use `homectl login <url> --token <token>` or --server");
        return HomeApiClient.Create(server, token);
    }
}

public static class Table
{
    public static void Print(string[] header, IEnumerable<string[]> rows)
    {
        var all = rows.ToList();
        if (all.Count == 0)
        {
            Console.WriteLine("(пусто)");
            return;
        }
        var w = header.Select((h, i) => Math.Max(h.Length, all.Max(r => i < r.Length ? r[i].Length : 0))).ToArray();
        Console.WriteLine(string.Join("  ", header.Select((h, i) => h.PadRight(w[i]))).TrimEnd());
        foreach (var r in all) Console.WriteLine(string.Join("  ", r.Select((c, i) => c.PadRight(w[i]))).TrimEnd());
    }
}

public static class Fmt
{
    public static string Bytes(long b) => b switch
    {
        >= 1L << 30 => $"{b / (double)(1L << 30):0.0} GB",
        >= 1L << 20 => $"{b / (double)(1L << 20):0.0} MB",
        >= 1L << 10 => $"{b / 1024.0:0.0} KB",
        _ => $"{b} B",
    };

    public static string Value(PointDto p, JsonNode? v) => PointFormat.Format(p, v);

    public static string MainValue(DeviceDto d)
    {
        var p = d.Points.FirstOrDefault(x => x.Kind is "sensor" or "actuator" && !x.Advanced && x.Ui != "hidden" && x.Type != "str");
        return p != null && d.Values.TryGetValue(p.Key, out var v) ? $"{p.Key}={Value(p, v.Value)}" : "";
    }

    public static TimeSpan Duration(string s)
    {
        s = s.Trim().ToLowerInvariant();
        if (s.Length < 2) throw new ArgumentException($"bad duration '{s}'");
        var n = double.Parse(s[..^1], CultureInfo.InvariantCulture);
        return s[^1] switch
        {
            's' => TimeSpan.FromSeconds(n),
            'm' => TimeSpan.FromMinutes(n),
            'h' => TimeSpan.FromHours(n),
            'd' => TimeSpan.FromDays(n),
            'w' => TimeSpan.FromDays(n * 7),
            _ => throw new ArgumentException($"bad duration '{s}' (use 30m, 24h, 7d)"),
        };
    }

    public static string Log(LogLineDto l) =>
        $"{DateTimeOffset.FromUnixTimeMilliseconds(l.Ts).ToLocalTime():HH:mm:ss} {l.Level[..1].ToUpperInvariant()} {l.Tag}: {l.Text}";

    public static bool Confirm(string question)
    {
        Console.Write($"{question} [y/N] ");
        var a = Console.ReadLine()?.Trim().ToLowerInvariant();
        return a is "y" or "yes" or "д" or "да";
    }
}

public static class Ota
{
    public static void Print(OtaJobDto j)
    {
        Console.WriteLine($"job #{j.Id}: {j.Model} {j.Version} — {j.Status}{(j.Canary ? " (canary)" : "")}, by {j.CreatedBy}");
        Table.Print(new[] { "DEVICE", "NAME", "FROM", "STATUS", "PROGRESS", "ERROR" },
            j.Items.Select(i => new[] { i.DeviceId, i.DeviceName, i.FromVersion ?? "", i.Status.ToString(), $"{i.Progress}%", i.Error ?? "" }));
    }

    public static async Task<int> WaitAsync(HomeApiClient api, long jobId, CancellationToken ct)
    {
        var last = "";
        while (!ct.IsCancellationRequested)
        {
            var j = await api.OtaJobAsync(jobId, ct);
            var line = string.Join("  ", j.Items.Select(i => $"{i.DeviceName}: {i.Status} {i.Progress}%"));
            if (line != last)
            {
                Console.WriteLine(line);
                last = line;
            }
            if (j.Status is OtaStatus.Done or OtaStatus.Failed or OtaStatus.Cancelled)
            {
                Print(j);
                return j.Status == OtaStatus.Done ? 0 : 1;
            }
            await Task.Delay(1000, ct);
        }
        return 1;
    }
}
