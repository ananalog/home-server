using System.Net;
using System.Text.Json.Nodes;
using Home.Client;
using Home.Protocol;
using Xunit;
using static Home.Server.Tests.ServerFixture;

namespace Home.Server.Tests;

public class DeviceTests
{
    [Fact]
    public async Task NewDeviceIsAdoptedAndControlled()
    {
        await using var srv = await StartAsync();
        using var api = srv.Admin();
        var sim = srv.StartSim("5e00000000a1");

        await Eventually(async () => (await api.DevicesAsync()).Any(d => d.Id == "5e00000000a1" && d.Online && d.State == DeviceState.New), what: "new device online");
        // Not adopted: commands are refused, values are visible.
        var ex = await Assert.ThrowsAsync<HomeApiException>(() => api.SetAsync("00a1", "display_mode", JsonValue.Create("night")));
        Assert.Equal(HttpStatusCode.Conflict, ex.Status);

        await api.AdoptAsync("5e00000000a1");
        await Eventually(async () => sim.State == AdoptState.Adopted && (await api.DeviceAsync("00a1")) is { Online: true, State: DeviceState.Adopted, Points.Count: > 5 }, what: "adopted and described");

        var v = await api.SetAsync("5e00000000a1", "display_mode", JsonValue.Create("night"));
        Assert.Equal(2, v.Value!.GetValue<int>());
        Assert.Equal(2, sim.Values[13].I);

        await api.SetAsync("5e00000000a1", "auto_calibration", JsonValue.Create("off"));
        Assert.False(sim.Values[12].B);

        var bad = await Assert.ThrowsAsync<HomeApiException>(() => api.SetAsync("5e00000000a1", "co2", JsonValue.Create(5)));
        Assert.Equal(HttpStatusCode.BadRequest, bad.Status);
        var range = await Assert.ThrowsAsync<HomeApiException>(() => api.SetAsync("5e00000000a1", "display_brightness", JsonValue.Create(500)));
        Assert.Equal(HttpStatusCode.BadRequest, range.Status);

        var inv = await api.InvokeAsync("5e00000000a1", "calibrate", 420);
        Assert.Contains("420", inv.Text);

        await api.IdentifyAsync("5e00000000a1", 3);
        Assert.Equal(1, sim.Identified);

        var net = await api.NetStatusAsync("5e00000000a1");
        Assert.Equal("online", net.State);
        await api.SetNetworkAsync("5e00000000a1", new NetworkDto { Mode = "static", Ip = "192.168.1.50", Prefix = 24, Gateway = "192.168.1.1" });
        Assert.Equal(IpMode.Static, sim.LastNet!.IpMode);
        var badNet = await Assert.ThrowsAsync<HomeApiException>(() => api.SetNetworkAsync("5e00000000a1", new NetworkDto { Mode = "static", Ip = "192.168.1.50", Prefix = 24, Gateway = "10.0.0.1" }));
        Assert.Equal(HttpStatusCode.BadRequest, badNet.Status);

        var renamed = await api.UpdateDeviceAsync("5e00000000a1", new DeviceUpdateRequest("Спальня", "bedroom"));
        Assert.Equal("Спальня", renamed.Name);
        Assert.Equal("Спальня", (await api.DeviceAsync("Спальня")).Name); // lookup by name

        // History: reports arrive every 200 ms.
        await Eventually(async () =>
        {
            var now = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds();
            var h = await api.HistoryAsync("5e00000000a1", "co2", now - 60_000, now + 1000, 1000);
            return h.Points.Count >= 2;
        }, what: "history points");

        await api.FactoryResetAsync("5e00000000a1", "settings");
        Assert.Equal(ResetMode.Settings, sim.LastReset);

        var events = await api.EventsAsync("5e00000000a1");
        Assert.Contains(events, e => e.Kind == "adopted");

        await api.RemoveAsync("5e00000000a1");
        Assert.DoesNotContain(await api.DevicesAsync(), d => d.Id == "5e00000000a1" && d.State == DeviceState.Adopted);
    }

    [Fact]
    public async Task RejectedDeviceIsBlocked()
    {
        await using var srv = await StartAsync();
        using var api = srv.Admin();
        var sim = srv.StartSim("5e00000000b1", "relay");
        await Eventually(async () => (await api.DevicesAsync()).Any(d => d.Id == "5e00000000b1" && d.Online));
        await api.RejectAsync("00b1");
        await Eventually(() => Task.FromResult(sim.State == AdoptState.Blocked), what: "blocked");
        Assert.Equal(DeviceState.Blocked, (await api.DeviceAsync("00b1")).State);
    }

    [Fact]
    public async Task OtaUpdatesAndDetectsRollback()
    {
        Ota.OtaService.RebootTimeout = TimeSpan.FromSeconds(20);
        await using var srv = await StartAsync();
        using var api = srv.Admin();
        var good = srv.StartSim("5e00000000c1");
        var bad = srv.StartSim("5e00000000c2", otaFail: true);
        var relay = srv.StartSim("5e00000000c3", "relay");
        await Eventually(async () => (await api.DevicesAsync()).Count(d => d.Online) == 3, what: "3 online");
        foreach (var id in new[] { "5e00000000c1", "5e00000000c2", "5e00000000c3" }) await api.AdoptAsync(id);
        await Eventually(async () => (await api.DevicesAsync()).Count(d => d.Online && d.State == DeviceState.Adopted) == 3, what: "adopted");

        var path = Path.Combine(srv.Dir, "fw.bin");
        await File.WriteAllBytesAsync(path, Ota.FirmwareImage.BuildFake((int)Model.Co2Egg, "1.1.0", 200_000));
        var fw = await api.UploadFirmwareAsync(path);
        Assert.Equal("1.1.0", fw.Version);
        Assert.Equal("co2-egg", fw.Model);

        // Wrong model is refused before anything is sent.
        var wrong = await Assert.ThrowsAsync<HomeApiException>(() => api.StartOtaAsync(new OtaJobRequest(fw.Id, new() { "5e00000000c3" }))); 
        Assert.Equal(HttpStatusCode.BadRequest, wrong.Status);

        var job = await api.StartOtaAsync(new OtaJobRequest(fw.Id, null, AllOfModel: true));
        Assert.Equal(2, job.Items.Count);
        await Eventually(async () => (await api.OtaJobAsync(job.Id)).Status is OtaStatus.Done or OtaStatus.Failed, 40, "job finished");
        var done = await api.OtaJobAsync(job.Id);
        var summary = string.Join("; ", done.Items.Select(i => $"{i.DeviceId}:{i.Status}:{i.Error}"));
        Assert.True(done.Items.Single(i => i.DeviceId == "5e00000000c1").Status == OtaStatus.Done, summary);
        Assert.Equal(OtaStatus.Failed, done.Status);
        Assert.Equal(OtaStatus.Done, done.Items.Single(i => i.DeviceId == "5e00000000c1").Status);
        Assert.Equal(OtaStatus.RolledBack, done.Items.Single(i => i.DeviceId == "5e00000000c2").Status);
        Assert.Equal("1.1.0", good.Version);
        Assert.Equal("1.0.0", bad.Version);
        await Eventually(async () => (await api.DeviceAsync("00c1")).FwVersion == "1.1.0", what: "version stored");
    }

    [Fact]
    public async Task BadFirmwareIsRejected()
    {
        await using var srv = await StartAsync();
        using var api = srv.Admin();
        var path = Path.Combine(srv.Dir, "junk.bin");
        await File.WriteAllBytesAsync(path, new byte[4096]);
        var ex = await Assert.ThrowsAsync<HomeApiException>(() => api.UploadFirmwareAsync(path));
        Assert.Equal(HttpStatusCode.BadRequest, ex.Status);
    }
}
