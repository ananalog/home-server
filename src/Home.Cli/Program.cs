using System.CommandLine;
using System.Globalization;
using System.Text.Json;
using System.Text.Json.Nodes;
using System.Text.Json.Serialization.Metadata;
using Home.Cli;
using Home.Client;

// homectl — command line for the Home server. Mirrors every function of the Mini App.

var serverOpt = new Option<string?>("--server", "-s") { Description = "Server: https://host, http://host:8080 or unix:/run/home/api.sock", Recursive = true };
var tokenOpt = new Option<string?>("--token") { Description = "API token (homectl tokens create)", Recursive = true };
var jsonOpt = new Option<bool>("--json") { Description = "Print JSON instead of tables", Recursive = true };

var root = new RootCommand("homectl — управление домом (Home) из командной строки");
root.Options.Add(serverOpt);
root.Options.Add(tokenOpt);
root.Options.Add(jsonOpt);

HomeApiClient Api(ParseResult p) => Config.CreateClient(p.GetValue(serverOpt), p.GetValue(tokenOpt));
bool Json(ParseResult p) => p.GetValue(jsonOpt);

Command Cmd(string name, string desc, Func<ParseResult, CancellationToken, Task<int>> action, params Symbol[] symbols)
{
    var c = new Command(name, desc);
    foreach (var s in symbols)
        switch (s)
        {
            case Argument a: c.Arguments.Add(a); break;
            case Option o: c.Options.Add(o); break;
        }
    c.SetAction(async (p, ct) =>
    {
        try
        {
            return await action(p, ct);
        }
        catch (HomeApiException e)
        {
            Console.Error.WriteLine($"error: {e.Message}");
            return 1;
        }
        catch (HttpRequestException e)
        {
            Console.Error.WriteLine($"error: cannot reach the server: {e.Message}");
            return 3;
        }
        catch (ArgumentException e)
        {
            Console.Error.WriteLine($"error: {e.Message}");
            return 2;
        }
    });
    return c;
}

Command Group(string name, string desc, params Command[] subs)
{
    var c = new Command(name, desc);
    foreach (var s in subs) c.Subcommands.Add(s);
    return c;
}

static JsonTypeInfo<T> Info<T>() => (JsonTypeInfo<T>)HomeJson.Default.GetTypeInfo(typeof(T))!;
static void PrintJson<T>(T value) => Console.WriteLine(JsonSerializer.Serialize(value, Info<T>()));

var devArg = new Argument<string>("device") { Description = "Id, конец id (4+ символа) или имя устройства" };
var pointArg = new Argument<string>("point") { Description = "Ключ точки (co2, power, …)" };

// ---------------------------------------------------------------- login / system
var loginServer = new Argument<string>("server") { Description = "https://myhome.duckdns.org" };
var loginToken = new Option<string>("--token") { Description = "API token", Required = true };
root.Subcommands.Add(Cmd("login", "Сохранить адрес сервера и токен в ~/.config/homectl/config.json", async (p, ct) =>
{
    var server = p.GetValue(loginServer)!;
    var token = p.GetValue(loginToken)!;
    using var api = HomeApiClient.Create(server, token);
    var me = await api.MeAsync(ct);
    Config.Save(new ClientConfig(server, token));
    Console.WriteLine($"ok: {me.Name}, role {me.Role}. Saved to {Config.Path}");
    return 0;
}, loginServer, loginToken));

root.Subcommands.Add(Cmd("logout", "Удалить сохранённые настройки", (p, ct) =>
{
    if (File.Exists(Config.Path)) File.Delete(Config.Path);
    Console.WriteLine("logged out");
    return Task.FromResult(0);
}));

root.Subcommands.Add(Group("system", "Сервер",
    Cmd("status", "Состояние сервера", async (p, ct) =>
    {
        using var api = Api(p);
        var s = await api.SystemAsync(ct);
        if (Json(p)) { PrintJson(s); return 0; }
        Console.WriteLine($"version        {s.Version}");
        Console.WriteLine($"started        {s.StartedAt.ToLocalTime():yyyy-MM-dd HH:mm}");
        Console.WriteLine($"devices        {s.Online}/{s.Devices} online" + (s.NewDevices > 0 ? $", {s.NewDevices} new (homectl devices list --new)" : ""));
        Console.WriteLine($"protocol       v{s.ProtocolMajor}.{s.ProtocolMinor}, device port {s.DevicePort}");
        Console.WriteLine($"public url     {s.PublicUrl ?? "-"}");
        Console.WriteLine($"telegram bot   {(!s.BotConfigured ? "not configured" : s.BotUsername != null ? "@" + s.BotUsername : "configured, not connected (check the token / internet)")}");
        Console.WriteLine($"database       {Fmt.Bytes(s.DbSize)}, disk free {Fmt.Bytes(s.DiskFree)}");
        return 0;
    }),
    Cmd("whoami", "Кто я для сервера", async (p, ct) =>
    {
        using var api = Api(p);
        var me = await api.MeAsync(ct);
        if (Json(p)) PrintJson(me);
        else Console.WriteLine($"{me.Name} ({me.Role}{(me.Local ? ", local socket" : "")})");
        return 0;
    })));

// ---------------------------------------------------------------- devices
var newOnly = new Option<bool>("--new") { Description = "Только новые (не принятые)" };
var offlineOnly = new Option<bool>("--offline") { Description = "Только не в сети" };
var roomFilter = new Option<string?>("--room") { Description = "Фильтр по комнате" };
var nameArg = new Argument<string>("name");
var roomArg = new Argument<string>("room") { Description = "Id комнаты ('' — без комнаты)" };

root.Subcommands.Add(Group("devices", "Устройства",
    Cmd("list", "Список устройств", async (p, ct) =>
    {
        using var api = Api(p);
        var list = await api.DevicesAsync(ct);
        if (p.GetValue(newOnly)) list = list.Where(d => d.State == DeviceState.New).ToList();
        if (p.GetValue(offlineOnly)) list = list.Where(d => !d.Online).ToList();
        if (p.GetValue(roomFilter) is { } r) list = list.Where(d => d.Room == r).ToList();
        if (Json(p)) { PrintJson(list); return 0; }
        Table.Print(new[] { "ID", "NAME", "MODEL", "ROOM", "STATE", "ONLINE", "FW", "VALUE" },
            list.Select(d => new[] { d.Id, d.Name, d.Model, d.Room ?? "", d.State.ToString().ToLowerInvariant(), d.Online ? "yes" : "no", d.FwVersion ?? "", Fmt.MainValue(d) }));
        return 0;
    }, newOnly, offlineOnly, roomFilter),
    Cmd("show", "Подробно об устройстве", async (p, ct) =>
    {
        using var api = Api(p);
        var d = await api.DeviceAsync(p.GetValue(devArg)!, ct);
        if (Json(p)) { PrintJson(d); return 0; }
        Console.WriteLine($"{d.Name}  [{d.Id}]  {d.Model} hw{d.HwRev}  {(d.Online ? "online" : "offline")}  {d.State.ToString().ToLowerInvariant()}");
        Console.WriteLine($"firmware {d.FwVersion} ({d.BootPartition}{(d.PendingVerify ? ", pending verify" : "")}), ip {d.Ip}, room {d.Room ?? "-"}, last seen {d.LastSeen?.ToLocalTime():yyyy-MM-dd HH:mm:ss}");
        if (d.Ota is { } o) Console.WriteLine($"OTA: {o.Version} {o.Status} {o.Progress}% {o.Error}");
        Console.WriteLine();
        Table.Print(new[] { "POINT", "TITLE", "KIND", "VALUE", "RANGE" },
            d.Points.Select(pt => new[]
            {
                pt.Key, pt.Title, pt.Kind + (pt.Advanced ? "*" : ""),
                d.Values.TryGetValue(pt.Key, out var v) ? Fmt.Value(pt, v.Value) : "",
                pt.Type == "enum" ? string.Join("|", pt.Options) : pt.Min != null || pt.Max != null ? $"{pt.Min}..{pt.Max}" : "",
            }));
        return 0;
    }, devArg),
    Cmd("rename", "Переименовать", async (p, ct) =>
    {
        using var api = Api(p);
        var d = await api.UpdateDeviceAsync(p.GetValue(devArg)!, new DeviceUpdateRequest(p.GetValue(nameArg), null), ct);
        Console.WriteLine($"ok: {d.Id} → {d.Name}");
        return 0;
    }, devArg, nameArg),
    Cmd("move", "Перенести в комнату", async (p, ct) =>
    {
        using var api = Api(p);
        var d = await api.UpdateDeviceAsync(p.GetValue(devArg)!, new DeviceUpdateRequest(null, p.GetValue(roomArg) ?? ""), ct);
        Console.WriteLine($"ok: {d.Name} → {d.Room ?? "(без комнаты)"}");
        return 0;
    }, devArg, roomArg),
    Cmd("adopt", "Принять новое устройство", async (p, ct) =>
    {
        using var api = Api(p);
        await api.AdoptAsync(p.GetValue(devArg)!, ct);
        Console.WriteLine("ok: adopted (device reconnects)");
        return 0;
    }, devArg),
    Cmd("reject", "Отклонить устройство", async (p, ct) =>
    {
        using var api = Api(p);
        await api.RejectAsync(p.GetValue(devArg)!, ct);
        Console.WriteLine("ok: blocked");
        return 0;
    }, devArg),
    Cmd("remove", "Удалить устройство и его историю", async (p, ct) =>
    {
        using var api = Api(p);
        await api.RemoveAsync(p.GetValue(devArg)!, ct);
        Console.WriteLine("ok: removed");
        return 0;
    }, devArg)));

// ---------------------------------------------------------------- values and actions
var pointsArg = new Argument<string[]>("points") { Arity = ArgumentArity.ZeroOrMore };
root.Subcommands.Add(Cmd("get", "Текущие значения", async (p, ct) =>
{
    using var api = Api(p);
    var d = await api.DeviceAsync(p.GetValue(devArg)!, ct);
    var want = p.GetValue(pointsArg) ?? Array.Empty<string>();
    var pts = d.Points.Where(x => want.Length == 0 ? x.Kind != "action" : want.Contains(x.Key)).ToList();
    if (Json(p))
    {
        var o = new JsonObject();
        foreach (var x in pts) o[x.Key] = d.Values.TryGetValue(x.Key, out var v) ? v.Value?.DeepClone() : null;
        Console.WriteLine(o.ToJsonString());
        return 0;
    }
    foreach (var x in pts) Console.WriteLine($"{x.Key,-20} {(d.Values.TryGetValue(x.Key, out var v) ? Fmt.Value(x, v.Value) : "-")}");
    return 0;
}, devArg, pointsArg));

var valueArg = new Argument<string>("value") { Description = "on/off, число или вариант из списка" };
root.Subcommands.Add(Cmd("set", "Установить значение", async (p, ct) =>
{
    using var api = Api(p);
    var raw = p.GetValue(valueArg)!;
    JsonNode? node = double.TryParse(raw, NumberStyles.Float, CultureInfo.InvariantCulture, out var num) ? JsonValue.Create(num) : JsonValue.Create(raw);
    var dev = await api.DeviceAsync(p.GetValue(devArg)!, ct);
    var v = await api.SetAsync(dev.Id, p.GetValue(pointArg)!, node, ct);
    var pt = dev.Points.FirstOrDefault(x => x.Key == p.GetValue(pointArg));
    Console.WriteLine($"ok: {p.GetValue(pointArg)} = {(pt != null ? Fmt.Value(pt, v.Value) : v.Value?.ToJsonString())}");
    return 0;
}, devArg, pointArg, valueArg));

var actionArg = new Argument<string>("action");
var argArg = new Argument<string[]>("args") { Arity = ArgumentArity.ZeroOrMore, Description = "Аргумент действия: число или key=число" };
root.Subcommands.Add(Cmd("invoke", "Выполнить действие (калибровка и т.п.)", async (p, ct) =>
{
    using var api = Api(p);
    double? arg = null;
    foreach (var a in p.GetValue(argArg) ?? Array.Empty<string>())
    {
        var s = a.Contains('=') ? a[(a.IndexOf('=') + 1)..] : a;
        arg = double.Parse(s, CultureInfo.InvariantCulture);
    }
    var r = await api.InvokeAsync(p.GetValue(devArg)!, p.GetValue(actionArg)!, arg, ct);
    Console.WriteLine($"ok{(r.Text != null ? ": " + r.Text : "")}");
    return 0;
}, devArg, actionArg, argArg));

var watchDev = new Argument<string?>("device") { Arity = ArgumentArity.ZeroOrOne };
root.Subcommands.Add(Cmd("watch", "Живой поток значений и событий", async (p, ct) =>
{
    using var api = Api(p);
    string? filter = null;
    if (p.GetValue(watchDev) is { } dv) filter = (await api.DeviceAsync(dv, ct)).Id;
    var names = (await api.DevicesAsync(ct)).ToDictionary(d => d.Id, d => d.Name);
    await foreach (var e in api.StreamAsync(null, ct))
    {
        var id = e.Data?["deviceId"]?.GetValue<string>() ?? e.Data?["id"]?.GetValue<string>();
        if (filter != null && id != filter) continue;
        if (Json(p)) { Console.WriteLine(new JsonObject { ["type"] = e.Type, ["data"] = e.Data?.DeepClone() }.ToJsonString()); continue; }
        var ts = DateTime.Now.ToString("HH:mm:ss");
        var who = id != null && names.TryGetValue(id, out var n) ? n : id;
        switch (e.Type)
        {
            case "values":
                var vals = e.Data?["values"]?.AsObject().Select(kv => $"{kv.Key}={kv.Value?["value"]?.ToJsonString()}") ?? Enumerable.Empty<string>();
                Console.WriteLine($"{ts} {who}: {string.Join(" ", vals)}");
                break;
            case "online":
                Console.WriteLine($"{ts} {who}: {(e.Data?["online"]?.GetValue<bool>() == true ? "online" : "offline")}");
                break;
            case "event":
                Console.WriteLine($"{ts} [{e.Data?["kind"]}] {e.Data?["text"]}");
                break;
            case "ota":
                Console.WriteLine($"{ts} OTA #{e.Data?["id"]} {e.Data?["status"]}");
                break;
            case "device":
                if (id != null && e.Data?["name"]?.GetValue<string>() is { } nm) names[id] = nm;
                break;
        }
    }
    return 0;
}, watchDev));

var sinceOpt = new Option<string>("--since") { Description = "Период: 1h, 24h, 7d, 30d", DefaultValueFactory = _ => "24h" };
var stepOpt = new Option<string?>("--step") { Description = "Шаг: 5m, 1h (по умолчанию — авто)" };
var csvOpt = new Option<bool>("--csv") { Description = "CSV вместо таблицы" };
root.Subcommands.Add(Cmd("history", "История значений", async (p, ct) =>
{
    using var api = Api(p);
    var to = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds();
    var from = to - (long)Fmt.Duration(p.GetValue(sinceOpt)!).TotalMilliseconds;
    var step = p.GetValue(stepOpt) is { } st ? (long)Fmt.Duration(st).TotalMilliseconds : 0;
    var h = await api.HistoryAsync(p.GetValue(devArg)!, p.GetValue(pointArg)!, from, to, step, ct);
    if (Json(p)) { PrintJson(h); return 0; }
    if (p.GetValue(csvOpt))
    {
        Console.WriteLine("time,min,max,avg");
        foreach (var x in h.Points) Console.WriteLine($"{DateTimeOffset.FromUnixTimeMilliseconds(x.T):O},{x.Min},{x.Max},{x.Avg}");
        return 0;
    }
    Table.Print(new[] { "TIME", "MIN", "AVG", "MAX" },
        h.Points.Select(x => new[] { DateTimeOffset.FromUnixTimeMilliseconds(x.T).ToLocalTime().ToString("MM-dd HH:mm"), $"{x.Min}", $"{x.Avg}", $"{x.Max}" }));
    return 0;
}, devArg, pointArg, sinceOpt, stepOpt, csvOpt));

var followOpt = new Option<bool>("--follow", "-f") { Description = "Следить за новыми строками" };
root.Subcommands.Add(Cmd("logs", "Логи устройства", async (p, ct) =>
{
    using var api = Api(p);
    var id = (await api.DeviceAsync(p.GetValue(devArg)!, ct)).Id;
    foreach (var l in await api.LogsAsync(id, ct)) Console.WriteLine(Fmt.Log(l));
    if (!p.GetValue(followOpt)) return 0;
    await foreach (var e in api.StreamAsync(id, ct))
        if (e.Type == "log" && e.Data?["line"] is { } line)
            Console.WriteLine(Fmt.Log(line.Deserialize(Info<LogLineDto>())!));
    return 0;
}, devArg, followOpt));

var eventsLimit = new Option<int>("--limit", "-n") { DefaultValueFactory = _ => 30 };
var eventsDev = new Option<string?>("--device");
root.Subcommands.Add(Cmd("events", "Журнал событий", async (p, ct) =>
{
    using var api = Api(p);
    var list = await api.EventsAsync(p.GetValue(eventsDev), p.GetValue(eventsLimit), ct);
    if (Json(p)) { PrintJson(list); return 0; }
    foreach (var e in list.AsEnumerable().Reverse())
        Console.WriteLine($"{e.Ts.ToLocalTime():MM-dd HH:mm:ss} {e.Kind,-14} {e.Text}{(e.Actor != null ? $"  ({e.Actor})" : "")}");
    return 0;
}, eventsDev, eventsLimit));

root.Subcommands.Add(Cmd("reboot", "Перезагрузить устройство", async (p, ct) =>
{
    using var api = Api(p);
    await api.RebootAsync(p.GetValue(devArg)!, ct);
    Console.WriteLine("ok: rebooting");
    return 0;
}, devArg));

var secondsOpt = new Option<int>("--seconds") { DefaultValueFactory = _ => 10 };
root.Subcommands.Add(Cmd("identify", "Мигнуть устройством", async (p, ct) =>
{
    using var api = Api(p);
    await api.IdentifyAsync(p.GetValue(devArg)!, p.GetValue(secondsOpt), ct);
    Console.WriteLine("ok");
    return 0;
}, devArg, secondsOpt));

var modeOpt = new Option<string>("--mode") { Description = "settings | firmware | all", Required = true };
modeOpt.AcceptOnlyFromAmong("settings", "firmware", "all");
var yesOpt = new Option<bool>("--yes", "-y") { Description = "Не спрашивать подтверждение" };
root.Subcommands.Add(Cmd("factory-reset", "Сброс устройства: настройки, прошивка к заводской или всё", async (p, ct) =>
{
    using var api = Api(p);
    var d = await api.DeviceAsync(p.GetValue(devArg)!, ct);
    if (!p.GetValue(yesOpt) && !Fmt.Confirm($"Сбросить {d.Name} ({p.GetValue(modeOpt)})?")) return 1;
    await api.FactoryResetAsync(d.Id, p.GetValue(modeOpt)!, ct);
    Console.WriteLine("ok: resetting");
    return 0;
}, devArg, modeOpt, yesOpt));

var dhcpOpt = new Option<bool>("--dhcp");
var ipOpt = new Option<string?>("--ip") { Description = "Адрес с префиксом: 192.168.1.50/24" };
var gwOpt = new Option<string?>("--gw");
var dnsOpt = new Option<string?>("--dns");
var dns2Opt = new Option<string?>("--dns2");
root.Subcommands.Add(Cmd("net", "Сеть устройства: показать или задать DHCP/статический IP", async (p, ct) =>
{
    using var api = Api(p);
    var dev = p.GetValue(devArg)!;
    if (!p.GetValue(dhcpOpt) && p.GetValue(ipOpt) == null)
    {
        var s = await api.NetStatusAsync(dev, ct);
        if (Json(p)) { PrintJson(s); return 0; }
        var c = s.Current;
        Console.WriteLine($"state   {s.State}\nssid    {s.Ssid} ({s.Rssi} dBm)\nmode    {c?.Mode}\nip      {c?.Ip}/{c?.Prefix}\ngateway {c?.Gateway}\ndns     {c?.Dns1} {c?.Dns2}\nserver  {s.Server}\nmac     {s.Mac}");
        return 0;
    }
    NetworkDto net;
    if (p.GetValue(dhcpOpt)) net = new NetworkDto { Mode = "dhcp" };
    else
    {
        var ip = p.GetValue(ipOpt)!.Split('/');
        if (ip.Length != 2) throw new ArgumentException("--ip must look like 192.168.1.50/24");
        var gw = p.GetValue(gwOpt) ?? throw new ArgumentException("--gw is required for a static address");
        net = new NetworkDto { Mode = "static", Ip = ip[0], Prefix = int.Parse(ip[1]), Gateway = gw, Dns1 = p.GetValue(dnsOpt) ?? gw, Dns2 = p.GetValue(dns2Opt) };
    }
    await api.SetNetworkAsync(dev, net, ct);
    Console.WriteLine("ok: device applies the new network settings (it rolls back itself if the server becomes unreachable)");
    return 0;
}, devArg, dhcpOpt, ipOpt, gwOpt, dnsOpt, dns2Opt));

// ---------------------------------------------------------------- firmware
var fileArg = new Argument<string>("file") { Description = "Файл прошивки .bin (образ приложения)" };
var channelOpt = new Option<string>("--channel") { DefaultValueFactory = _ => "stable" };
var notesOpt = new Option<string?>("--notes");
var fwDevs = new Argument<string[]>("devices") { Arity = ArgumentArity.ZeroOrMore };
var versionOpt = new Option<string?>("--version") { Description = "Версия прошивки (по умолчанию — последняя для модели)" };
var modelOpt = new Option<string?>("--model") { Description = "Модель (co2-egg, …) — вместе с --all" };
var allOpt = new Option<bool>("--all") { Description = "Все принятые устройства модели" };
var canaryOpt = new Option<bool>("--canary") { Description = "Сначала одно устройство, остальные — если оно обновилось" };
var waitOpt = new Option<bool>("--wait") { Description = "Ждать завершения и показывать прогресс" };
var jobArg = new Argument<long?>("job") { Arity = ArgumentArity.ZeroOrOne };
var fwIdArg = new Argument<long>("id");

root.Subcommands.Add(Group("fw", "Прошивки и обновление по воздуху",
    Cmd("list", "Загруженные прошивки", async (p, ct) =>
    {
        using var api = Api(p);
        var list = await api.FirmwareAsync(ct);
        if (Json(p)) { PrintJson(list); return 0; }
        Table.Print(new[] { "ID", "MODEL", "VERSION", "CHANNEL", "SIZE", "SHA256", "UPLOADED" },
            list.Select(f => new[] { f.Id.ToString(), f.Model, f.Version, f.Channel, Fmt.Bytes(f.Size), f.Sha256[..12], f.UploadedAt.ToLocalTime().ToString("yyyy-MM-dd HH:mm") }));
        return 0;
    }),
    Cmd("upload", "Загрузить прошивку на сервер", async (p, ct) =>
    {
        using var api = Api(p);
        var f = await api.UploadFirmwareAsync(p.GetValue(fileArg)!, p.GetValue(channelOpt)!, p.GetValue(notesOpt), ct);
        if (Json(p)) PrintJson(f);
        else Console.WriteLine($"ok: #{f.Id} {f.Model} {f.Version} ({Fmt.Bytes(f.Size)})");
        return 0;
    }, fileArg, channelOpt, notesOpt),
    Cmd("remove", "Удалить прошивку", async (p, ct) =>
    {
        using var api = Api(p);
        await api.RemoveFirmwareAsync(p.GetValue(fwIdArg), ct);
        Console.WriteLine("ok");
        return 0;
    }, fwIdArg),
    Cmd("flash", "Обновить устройства по воздуху", async (p, ct) =>
    {
        using var api = Api(p);
        var devs = p.GetValue(fwDevs) ?? Array.Empty<string>();
        var all = p.GetValue(allOpt);
        if (devs.Length == 0 && !all) throw new ArgumentException("name devices or use --model <model> --all");
        var firmware = await api.FirmwareAsync(ct);
        string model;
        var ids = new List<string>();
        if (all) model = p.GetValue(modelOpt) ?? throw new ArgumentException("--all needs --model");
        else
        {
            var first = await api.DeviceAsync(devs[0], ct);
            model = first.Model;
            foreach (var d in devs) ids.Add((await api.DeviceAsync(d, ct)).Id);
        }
        var candidates = firmware.Where(f => f.Model == model).OrderByDescending(f => f.UploadedAt).ToList();
        var fw = p.GetValue(versionOpt) is { } ver ? candidates.FirstOrDefault(f => f.Version == ver) : candidates.FirstOrDefault(f => f.Channel == "stable") ?? candidates.FirstOrDefault();
        if (fw == null) throw new ArgumentException($"no firmware for {model} (homectl fw upload …)");
        var job = await api.StartOtaAsync(new OtaJobRequest(fw.Id, all ? null : ids, all, p.GetValue(canaryOpt)), ct);
        Console.WriteLine($"OTA job #{job.Id}: {fw.Model} {fw.Version} → {job.Items.Count} device(s)");
        if (!p.GetValue(waitOpt)) { Console.WriteLine($"progress: homectl fw status {job.Id}"); return 0; }
        return await Ota.WaitAsync(api, job.Id, ct);
    }, fwDevs, versionOpt, modelOpt, allOpt, canaryOpt, waitOpt),
    Cmd("status", "Задания обновления", async (p, ct) =>
    {
        using var api = Api(p);
        if (p.GetValue(jobArg) is { } id)
        {
            var j = await api.OtaJobAsync(id, ct);
            if (Json(p)) { PrintJson(j); return 0; }
            Ota.Print(j);
            return 0;
        }
        var jobs = await api.OtaJobsAsync(ct);
        if (Json(p)) { PrintJson(jobs); return 0; }
        Table.Print(new[] { "JOB", "MODEL", "VERSION", "STATUS", "DEVICES", "CREATED", "BY" },
            jobs.Select(j => new[] { j.Id.ToString(), j.Model, j.Version, j.Status.ToString(), $"{j.Items.Count(i => i.Status == OtaStatus.Done)}/{j.Items.Count}", j.CreatedAt.ToLocalTime().ToString("MM-dd HH:mm"), j.CreatedBy ?? "" }));
        return 0;
    }, jobArg),
    Cmd("cancel", "Отменить задание", async (p, ct) =>
    {
        using var api = Api(p);
        await api.CancelOtaAsync(p.GetValue(jobArg) ?? throw new ArgumentException("job id required"), ct);
        Console.WriteLine("ok");
        return 0;
    }, jobArg)));

// ---------------------------------------------------------------- rooms
var roomIdArg = new Argument<string>("id") { Description = "Латиницей: kitchen, bedroom" };
var roomNameArg = new Argument<string?>("name") { Arity = ArgumentArity.ZeroOrOne, Description = "Название: Кухня" };
var sortOpt = new Option<int>("--sort");
root.Subcommands.Add(Group("rooms", "Комнаты",
    Cmd("list", "Список комнат", async (p, ct) =>
    {
        using var api = Api(p);
        var list = await api.RoomsAsync(ct);
        if (Json(p)) { PrintJson(list); return 0; }
        Table.Print(new[] { "ID", "NAME", "SORT" }, list.Select(r => new[] { r.Id, r.Name, r.Sort.ToString() }));
        return 0;
    }),
    Cmd("add", "Добавить или переименовать комнату", async (p, ct) =>
    {
        using var api = Api(p);
        var r = await api.AddRoomAsync(new RoomDto(p.GetValue(roomIdArg)!, p.GetValue(roomNameArg) ?? p.GetValue(roomIdArg)!, p.GetValue(sortOpt)), ct);
        Console.WriteLine($"ok: {r.Id} — {r.Name}");
        return 0;
    }, roomIdArg, roomNameArg, sortOpt),
    Cmd("remove", "Удалить комнату", async (p, ct) =>
    {
        using var api = Api(p);
        await api.RemoveRoomAsync(p.GetValue(roomIdArg)!, ct);
        Console.WriteLine("ok");
        return 0;
    }, roomIdArg)));

// ---------------------------------------------------------------- users
var tgIdArg = new Argument<long>("telegram-id") { Description = "Telegram id (в личном чате с ботом chat id = user id; узнать: /id в боте)" };
var roleOpt = new Option<Role>("--role") { Description = "admin | user | viewer", DefaultValueFactory = _ => Role.User };
var userNameOpt = new Option<string?>("--name");
var roleArg = new Argument<Role>("role") { Description = "admin | user | viewer" };
root.Subcommands.Add(Group("users", "Кто может управлять домом (по Telegram chat id)",
    Cmd("list", "Пользователи", async (p, ct) =>
    {
        using var api = Api(p);
        var list = await api.UsersAsync(ct);
        if (Json(p)) { PrintJson(list); return 0; }
        Table.Print(new[] { "TELEGRAM ID", "NAME", "ROLE", "NOTIFY", "ADDED", "BY" },
            list.Select(u => new[] { u.TelegramId.ToString(), u.Name, u.Role.ToString().ToLowerInvariant(), u.Notify ? "yes" : "no", u.CreatedAt.ToLocalTime().ToString("yyyy-MM-dd"), u.AddedBy ?? "" }));
        return 0;
    }),
    Cmd("add", "Дать доступ человеку", async (p, ct) =>
    {
        using var api = Api(p);
        var u = await api.UpsertUserAsync(new UserUpsertRequest(p.GetValue(tgIdArg), p.GetValue(userNameOpt), p.GetValue(roleOpt), null), ct);
        Console.WriteLine($"ok: {u.TelegramId} {u.Name} — {u.Role.ToString().ToLowerInvariant()}");
        return 0;
    }, tgIdArg, roleOpt, userNameOpt),
    Cmd("role", "Изменить роль", async (p, ct) =>
    {
        using var api = Api(p);
        var u = await api.UpsertUserAsync(new UserUpsertRequest(p.GetValue(tgIdArg), null, p.GetValue(roleArg), null), ct);
        Console.WriteLine($"ok: {u.TelegramId} — {u.Role.ToString().ToLowerInvariant()}");
        return 0;
    }, tgIdArg, roleArg),
    Cmd("remove", "Забрать доступ", async (p, ct) =>
    {
        using var api = Api(p);
        await api.RemoveUserAsync(p.GetValue(tgIdArg), ct);
        Console.WriteLine("ok");
        return 0;
    }, tgIdArg),
    Cmd("requests", "Запросы доступа (люди, написавшие боту)", async (p, ct) =>
    {
        using var api = Api(p);
        var list = await api.AccessRequestsAsync(ct);
        if (Json(p)) { PrintJson(list); return 0; }
        Table.Print(new[] { "TELEGRAM ID", "NAME", "USERNAME", "REQUESTED" },
            list.Select(r => new[] { r.TelegramId.ToString(), r.Name, r.Username != null ? "@" + r.Username : "", r.RequestedAt.ToLocalTime().ToString("yyyy-MM-dd HH:mm") }));
        return 0;
    }),
    Cmd("approve", "Одобрить запрос доступа", async (p, ct) =>
    {
        using var api = Api(p);
        var u = await api.ApproveAsync(p.GetValue(tgIdArg), p.GetValue(roleOpt), ct);
        Console.WriteLine($"ok: {u.Name} — {u.Role.ToString().ToLowerInvariant()}");
        return 0;
    }, tgIdArg, roleOpt),
    Cmd("deny", "Отклонить запрос доступа", async (p, ct) =>
    {
        using var api = Api(p);
        await api.DenyAsync(p.GetValue(tgIdArg), ct);
        Console.WriteLine("ok");
        return 0;
    }, tgIdArg)));

// ---------------------------------------------------------------- tokens
var tokenNameArg = new Argument<string>("name");
var tokenRoleOpt = new Option<Role>("--role") { DefaultValueFactory = _ => Role.Admin };
var tokenIdArg = new Argument<long>("id");
root.Subcommands.Add(Group("tokens", "Токены для homectl на других компьютерах",
    Cmd("list", "Токены", async (p, ct) =>
    {
        using var api = Api(p);
        var list = await api.TokensAsync(ct);
        if (Json(p)) { PrintJson(list); return 0; }
        Table.Print(new[] { "ID", "NAME", "ROLE", "CREATED", "LAST USED" },
            list.Select(t => new[] { t.Id.ToString(), t.Name, t.Role.ToString().ToLowerInvariant(), t.CreatedAt.ToLocalTime().ToString("yyyy-MM-dd"), t.LastUsedAt?.ToLocalTime().ToString("yyyy-MM-dd HH:mm") ?? "" }));
        return 0;
    }),
    Cmd("create", "Создать токен (показывается один раз)", async (p, ct) =>
    {
        using var api = Api(p);
        var t = await api.CreateTokenAsync(p.GetValue(tokenNameArg)!, p.GetValue(tokenRoleOpt), ct);
        Console.WriteLine(t.Token);
        Console.Error.WriteLine($"homectl login <server-url> --token {t.Token}");
        return 0;
    }, tokenNameArg, tokenRoleOpt),
    Cmd("revoke", "Отозвать токен", async (p, ct) =>
    {
        using var api = Api(p);
        await api.RevokeTokenAsync(p.GetValue(tokenIdArg), ct);
        Console.WriteLine("ok");
        return 0;
    }, tokenIdArg)));

return await root.Parse(args).InvokeAsync();
