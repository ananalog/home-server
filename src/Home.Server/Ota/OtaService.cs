using System.Collections.Concurrent;
using System.Threading.Channels;
using Home.Client;
using Home.Protocol;
using Home.Server.Data;
using Home.Server.Devices;
using Home.Server.Live;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Options;

namespace Home.Server.Ota;

/// <summary>Firmware storage and OTA rollouts over the device protocol.</summary>
public sealed class OtaService(
    IDbContextFactory<HomeDb> dbf,
    DeviceManager devices,
    EventBus bus,
    EventLog events,
    INotifier notifier,
    IOptions<HomeOptions> options,
    ILogger<OtaService> log) : BackgroundService
{
    public static TimeSpan RebootTimeout { get; set; } = TimeSpan.FromMinutes(3);
    private readonly Channel<long> _jobs = Channel.CreateUnbounded<long>();
    private readonly ConcurrentDictionary<long, CancellationTokenSource> _running = new();

    private string FirmwareDir => Path.Combine(options.Value.DataDir, "firmware");

    // ------------------------------------------------------------------ firmware store

    public async Task<FirmwareDto> UploadAsync(byte[] image, string? channel, string? notes, string actor)
    {
        var info = FirmwareImage.Parse(image);
        await using var db = await dbf.CreateDbContextAsync();
        var existing = await db.Firmware.FirstOrDefaultAsync(f => f.Sha256 == info.Sha256);
        if (existing != null) return ToDto(existing);
        Directory.CreateDirectory(FirmwareDir);
        var file = $"{ProtoMap.ModelKey(info.Model)}-{Sanitize(info.Version)}-{info.Sha256[..8]}.bin";
        await File.WriteAllBytesAsync(Path.Combine(FirmwareDir, file), image);
        var e = new FirmwareEntity
        {
            Model = info.Model, Version = info.Version, Project = info.Project, Sha256 = info.Sha256, Size = info.Size,
            FileName = file, Channel = channel is "beta" ? "beta" : "stable", Notes = notes, UploadedAt = Clock.NowMs,
        };
        db.Firmware.Add(e);
        await db.SaveChangesAsync();
        await events.AddAsync(null, "firmware", $"Загружена прошивка {ProtoMap.ModelKey(info.Model)} {info.Version}", actor);
        return ToDto(e);
    }

    private static string Sanitize(string s) => new(s.Select(c => char.IsLetterOrDigit(c) || c is '.' or '-' or '_' ? c : '_').ToArray());

    public async Task<List<FirmwareDto>> ListFirmwareAsync()
    {
        await using var db = await dbf.CreateDbContextAsync();
        var list = await db.Firmware.AsNoTracking().ToListAsync();
        return list.OrderBy(f => f.Model).ThenByDescending(f => f.UploadedAt).Select(ToDto).ToList();
    }

    public async Task DeleteFirmwareAsync(long id)
    {
        await using var db = await dbf.CreateDbContextAsync();
        var f = await db.Firmware.FindAsync(id) ?? throw new KeyNotFoundException("firmware not found");
        db.Firmware.Remove(f);
        await db.SaveChangesAsync();
        var path = Path.Combine(FirmwareDir, f.FileName);
        if (File.Exists(path)) File.Delete(path);
    }

    public static FirmwareDto ToDto(FirmwareEntity f) => new()
    {
        Id = f.Id, ModelId = f.Model, Model = ProtoMap.ModelKey(f.Model), Version = f.Version, Project = f.Project,
        Sha256 = f.Sha256, Size = f.Size, Channel = f.Channel, Notes = f.Notes, UploadedAt = Clock.FromMs(f.UploadedAt),
    };

    // ------------------------------------------------------------------ jobs

    public async Task<OtaJobDto> StartAsync(OtaJobRequest r, string actor)
    {
        await using var db = await dbf.CreateDbContextAsync();
        var fw = await db.Firmware.FindAsync(r.FirmwareId) ?? throw new KeyNotFoundException("firmware not found");
        List<DeviceEntity> targets;
        if (r.AllOfModel)
            targets = await db.Devices.Where(d => d.Model == fw.Model && d.State == DeviceState.Adopted).ToListAsync();
        else
        {
            var ids = (r.DeviceIds ?? new()).Select(i => i.Trim().ToLowerInvariant()).ToHashSet();
            targets = new();
            foreach (var id in ids)
            {
                var d = await devices.FindAsync(id) ?? throw new KeyNotFoundException($"device '{id}' not found");
                targets.Add(d);
            }
        }
        if (targets.Count == 0) throw new ArgumentException("no devices to update");
        foreach (var d in targets)
        {
            if (d.Model != fw.Model)
                throw new ArgumentException($"{d.Name}: firmware is for {ProtoMap.ModelKey(fw.Model)}, device is {ProtoMap.ModelKey(d.Model)}");
            if (d.State != DeviceState.Adopted) throw new ArgumentException($"{d.Name} is not adopted");
        }
        var job = new OtaJobEntity { FirmwareId = fw.Id, Status = OtaStatus.Pending, Canary = r.Canary, CreatedAt = Clock.NowMs, CreatedBy = actor };
        job.Items = targets.Select(d => new OtaItemEntity { DeviceId = d.Id, Status = OtaStatus.Pending, FromVersion = d.FwVersion, UpdatedAt = Clock.NowMs }).ToList();
        db.OtaJobs.Add(job);
        await db.SaveChangesAsync();
        await events.AddAsync(null, "ota", $"Обновление {ProtoMap.ModelKey(fw.Model)} до {fw.Version}: {targets.Count} устр.", actor);
        _jobs.Writer.TryWrite(job.Id);
        return (await GetJobAsync(job.Id))!;
    }

    public async Task CancelAsync(long id)
    {
        if (_running.TryGetValue(id, out var cts)) cts.Cancel();
        await using var db = await dbf.CreateDbContextAsync();
        var job = await db.OtaJobs.Include(j => j.Items).FirstOrDefaultAsync(j => j.Id == id) ?? throw new KeyNotFoundException("job not found");
        foreach (var i in job.Items.Where(i => i.Status == OtaStatus.Pending)) i.Status = OtaStatus.Cancelled;
        if (job.Status is OtaStatus.Pending) job.Status = OtaStatus.Cancelled;
        await db.SaveChangesAsync();
    }

    public async Task<List<OtaJobDto>> ListJobsAsync(int limit = 20)
    {
        await using var db = await dbf.CreateDbContextAsync();
        var ids = await db.OtaJobs.OrderByDescending(j => j.Id).Select(j => j.Id).Take(limit).ToListAsync();
        var list = new List<OtaJobDto>();
        foreach (var id in ids) list.Add((await GetJobAsync(id))!);
        return list;
    }

    public async Task<OtaJobDto?> GetJobAsync(long id)
    {
        await using var db = await dbf.CreateDbContextAsync();
        var job = await db.OtaJobs.AsNoTracking().Include(j => j.Items).FirstOrDefaultAsync(j => j.Id == id);
        if (job == null) return null;
        var fw = await db.Firmware.AsNoTracking().FirstOrDefaultAsync(f => f.Id == job.FirmwareId);
        var ids = job.Items.Select(i => i.DeviceId).ToList();
        var names = await db.Devices.AsNoTracking().Where(d => ids.Contains(d.Id)).ToDictionaryAsync(d => d.Id, d => d.Name);
        return new OtaJobDto
        {
            Id = job.Id, FirmwareId = job.FirmwareId, Model = ProtoMap.ModelKey(fw?.Model ?? 0), Version = fw?.Version ?? "?",
            Status = job.Status, Canary = job.Canary, CreatedAt = Clock.FromMs(job.CreatedAt), CreatedBy = job.CreatedBy,
            Items = job.Items.OrderBy(i => i.Id).Select(i => new OtaItemDto(i.DeviceId, names.GetValueOrDefault(i.DeviceId, i.DeviceId), i.Status, i.Progress, i.Error, i.FromVersion)).ToList(),
        };
    }

    protected override async Task ExecuteAsync(CancellationToken ct)
    {
        // Jobs interrupted by a restart cannot be resumed safely: mark them failed.
        await using (var db = await dbf.CreateDbContextAsync(ct))
        {
            var stale = await db.OtaJobs.Include(j => j.Items).Where(j => j.Status == OtaStatus.Running || j.Status == OtaStatus.Pending).ToListAsync(ct);
            foreach (var j in stale)
            {
                j.Status = OtaStatus.Failed;
                foreach (var i in j.Items.Where(i => i.Status is OtaStatus.Running or OtaStatus.Pending or OtaStatus.Rebooting))
                {
                    i.Status = OtaStatus.Failed;
                    i.Error = "server restarted";
                }
            }
            await db.SaveChangesAsync(ct);
        }

        await foreach (var jobId in _jobs.Reader.ReadAllAsync(ct))
        {
            using var cts = CancellationTokenSource.CreateLinkedTokenSource(ct);
            _running[jobId] = cts;
            try
            {
                await RunJobAsync(jobId, cts.Token);
            }
            catch (Exception e)
            {
                log.LogError(e, "OTA job {Job} crashed", jobId);
            }
            finally
            {
                _running.TryRemove(jobId, out _);
            }
        }
    }

    private async Task RunJobAsync(long jobId, CancellationToken ct)
    {
        FirmwareEntity fw;
        List<string> deviceIds;
        bool canary;
        await using (var db = await dbf.CreateDbContextAsync(ct))
        {
            var job = await db.OtaJobs.Include(j => j.Items).FirstAsync(j => j.Id == jobId, ct);
            if (job.Status == OtaStatus.Cancelled) return;
            fw = await db.Firmware.FirstAsync(f => f.Id == job.FirmwareId, ct);
            job.Status = OtaStatus.Running;
            await db.SaveChangesAsync(ct);
            deviceIds = job.Items.Where(i => i.Status == OtaStatus.Pending).OrderBy(i => i.Id).Select(i => i.DeviceId).ToList();
            canary = job.Canary;
        }
        await PublishJobAsync(jobId);
        var image = await File.ReadAllBytesAsync(Path.Combine(FirmwareDir, fw.FileName), ct);

        var ok = true;
        var queue = new Queue<string>(deviceIds);
        if (canary && queue.Count > 1)
        {
            ok = await UpdateDeviceAsync(jobId, queue.Dequeue(), fw, image, ct);
            if (!ok)
            {
                await MarkRemainingAsync(jobId, OtaStatus.Cancelled, "canary failed");
                queue.Clear();
            }
        }
        const int parallel = 3;
        while (queue.Count > 0 && !ct.IsCancellationRequested)
        {
            var batch = new List<Task<bool>>();
            while (batch.Count < parallel && queue.Count > 0) batch.Add(UpdateDeviceAsync(jobId, queue.Dequeue(), fw, image, ct));
            ok &= (await Task.WhenAll(batch)).All(x => x);
        }

        await using (var db = await dbf.CreateDbContextAsync(CancellationToken.None))
        {
            var job = await db.OtaJobs.Include(j => j.Items).FirstAsync(j => j.Id == jobId);
            job.Status = ct.IsCancellationRequested ? OtaStatus.Cancelled
                : job.Items.All(i => i.Status == OtaStatus.Done) ? OtaStatus.Done : OtaStatus.Failed;
            await db.SaveChangesAsync();
            var done = job.Items.Count(i => i.Status == OtaStatus.Done);
            notifier.Notify($"{(job.Status == OtaStatus.Done ? "✅" : "⚠️")} Обновление {ProtoMap.ModelKey(fw.Model)} до {fw.Version}: {done} из {job.Items.Count} успешно", adminsOnly: true);
        }
        await PublishJobAsync(jobId);
    }

    private async Task MarkRemainingAsync(long jobId, OtaStatus status, string error)
    {
        await using var db = await dbf.CreateDbContextAsync();
        foreach (var i in await db.OtaItems.Where(i => i.JobId == jobId && i.Status == OtaStatus.Pending).ToListAsync())
        {
            i.Status = status;
            i.Error = error;
        }
        await db.SaveChangesAsync();
    }

    private async Task SetItemAsync(long jobId, string deviceId, OtaStatus status, int progress, string? error, FirmwareEntity fw)
    {
        await using (var db = await dbf.CreateDbContextAsync())
        {
            var item = await db.OtaItems.FirstAsync(i => i.JobId == jobId && i.DeviceId == deviceId);
            item.Status = status;
            item.Progress = progress;
            item.Error = error;
            item.UpdatedAt = Clock.NowMs;
            await db.SaveChangesAsync();
        }
        devices.SetOta(deviceId, status == OtaStatus.Done ? null : new OtaProgressDto(jobId, fw.Version, status, progress, error));
        await PublishJobAsync(jobId);
    }

    private async Task PublishJobAsync(long jobId)
    {
        var dto = await GetJobAsync(jobId);
        if (dto != null) bus.Publish("ota", dto);
    }

    /// <summary>Sends the image to one device, waits for the reboot and checks the version it comes back with.</summary>
    private async Task<bool> UpdateDeviceAsync(long jobId, string deviceId, FirmwareEntity fw, byte[] image, CancellationToken ct)
    {
        DeviceSession? session = null;
        try
        {
            session = devices.SessionOf(deviceId);
            await SetItemAsync(jobId, deviceId, OtaStatus.Running, 0, null, fw);
            var begin = await session.RequestAsync<OtaBeginResp>(new OtaBeginReq
            {
                Size = (uint)image.Length,
                Sha256 = Convert.FromHexString(fw.Sha256),
                Version = fw.Version,
                Model = (Model)fw.Model,
            }, TimeSpan.FromSeconds(60)); // erasing the slot takes a few seconds
            var chunk = Math.Clamp((int)(begin.Chunk ?? ProtoInfo.OtaChunk), 256, 1400);
            long offset = begin.ResumeFrom ?? 0;
            var inflight = new Queue<(long End, Task<OtaDataResp> Task)>();
            var lastReported = -1;
            while (offset < image.Length || inflight.Count > 0)
            {
                ct.ThrowIfCancellationRequested();
                while (inflight.Count < ProtoInfo.OtaWindow && offset < image.Length)
                {
                    var n = (int)Math.Min(chunk, image.Length - offset);
                    var req = new OtaDataReq { Offset = (uint)offset, Data = image.AsSpan((int)offset, n).ToArray() };
                    inflight.Enqueue((offset + n, session.RequestAsync<OtaDataResp>(req, TimeSpan.FromSeconds(30))));
                    offset += n;
                }
                var (end, task) = inflight.Dequeue();
                var resp = await task;
                if (resp.NextOffset is { } next && next != end)
                    throw new DeviceException(ErrorCode.BadRequest, $"device expects offset {next}, sent up to {end}");
                var pct = (int)(end * 100 / image.Length);
                if (pct >= lastReported + 5)
                {
                    lastReported = pct;
                    await SetItemAsync(jobId, deviceId, OtaStatus.Running, Math.Min(pct, 99), null, fw);
                }
            }
            // Wait for the HELLO after the reboot; registered before OTA_END so a fast reboot is not missed.
            var helloTask = devices.WaitForHelloAsync(deviceId, RebootTimeout, ct);
            await session.RequestAsync(new OtaEndReq(), TimeSpan.FromSeconds(60));
            await SetItemAsync(jobId, deviceId, OtaStatus.Rebooting, 100, null, fw);

            var hello = await helloTask;
            if (hello.FwVersion == fw.Version)
            {
                await SetItemAsync(jobId, deviceId, OtaStatus.Done, 100, null, fw);
                await events.AddAsync(deviceId, "ota-done", $"Прошивка обновлена до {fw.Version}");
                return true;
            }
            await SetItemAsync(jobId, deviceId, OtaStatus.RolledBack, 100, $"device came back with {hello.FwVersion}", fw);
            await events.AddAsync(deviceId, "ota-rollback", $"Откат прошивки: устройство вернулось с {hello.FwVersion}");
            return false;
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested)
        {
            if (session is { IsClosed: false }) _ = session.RequestAsync(new OtaAbortReq()).ContinueWith(_ => { }, TaskScheduler.Default);
            await SetItemAsync(jobId, deviceId, OtaStatus.Cancelled, 0, "cancelled", fw);
            return false;
        }
        catch (TimeoutException)
        {
            await SetItemAsync(jobId, deviceId, OtaStatus.Failed, 100, "device did not come back after reboot", fw);
            return false;
        }
        catch (Exception e)
        {
            log.LogWarning("OTA of {Device} failed: {Error}", deviceId, e.Message);
            if (session is { IsClosed: false }) _ = session.RequestAsync(new OtaAbortReq()).ContinueWith(_ => { }, TaskScheduler.Default);
            await SetItemAsync(jobId, deviceId, OtaStatus.Failed, 0, e.Message, fw);
            return false;
        }
    }
}
