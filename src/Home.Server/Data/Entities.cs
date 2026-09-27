using System.ComponentModel.DataAnnotations;
using System.ComponentModel.DataAnnotations.Schema;
using Home.Client;
using Microsoft.EntityFrameworkCore;

namespace Home.Server.Data;

// Times are stored as unix milliseconds (SQLite has no native time type).

public sealed class DeviceEntity
{
    [Key, MaxLength(12)] public string Id { get; set; } = "";
    public int Model { get; set; }
    public int HwRev { get; set; }
    public string Name { get; set; } = "";
    public string? Room { get; set; }
    public DeviceState State { get; set; }
    public string? FwVersion { get; set; }
    public string? BootPartition { get; set; }
    public string? IdfVersion { get; set; }
    public string? Ip { get; set; }
    public long FirstSeen { get; set; }
    public long? LastSeen { get; set; }
    /// <summary>Cached DESCRIBE result (JSON list of PointDto) and the firmware it belongs to.</summary>
    public string? PointsJson { get; set; }
    public string? PointsFw { get; set; }
}

[Index(nameof(DeviceId), nameof(Point), nameof(Ts))]
public sealed class SampleEntity
{
    public long Id { get; set; }
    [MaxLength(12)] public string DeviceId { get; set; } = "";
    public int Point { get; set; }
    public long Ts { get; set; }
    public double Value { get; set; }
}

[PrimaryKey(nameof(DeviceId), nameof(Point), nameof(Hour))]
public sealed class SampleHourEntity
{
    [MaxLength(12)] public string DeviceId { get; set; } = "";
    public int Point { get; set; }
    public long Hour { get; set; }
    public double Min { get; set; }
    public double Max { get; set; }
    public double Sum { get; set; }
    public int Count { get; set; }
}

public sealed class UserEntity
{
    [Key, DatabaseGenerated(DatabaseGeneratedOption.None)] public long TelegramId { get; set; }
    public string Name { get; set; } = "";
    public Role Role { get; set; }
    public bool Notify { get; set; } = true;
    public long CreatedAt { get; set; }
    public string? AddedBy { get; set; }
}

public sealed class AccessRequestEntity
{
    [Key, DatabaseGenerated(DatabaseGeneratedOption.None)] public long TelegramId { get; set; }
    public string Name { get; set; } = "";
    public string? Username { get; set; }
    public long RequestedAt { get; set; }
}

[Index(nameof(Hash), IsUnique = true)]
public sealed class ApiTokenEntity
{
    public long Id { get; set; }
    public string Name { get; set; } = "";
    public string Hash { get; set; } = "";
    public Role Role { get; set; }
    public long CreatedAt { get; set; }
    public long? LastUsedAt { get; set; }
}

public sealed class RoomEntity
{
    [Key] public string Id { get; set; } = "";
    public string Name { get; set; } = "";
    public int Sort { get; set; }
}

public sealed class FirmwareEntity
{
    public long Id { get; set; }
    public int Model { get; set; }
    public string Version { get; set; } = "";
    public string Project { get; set; } = "";
    public string Sha256 { get; set; } = "";
    public long Size { get; set; }
    public string FileName { get; set; } = "";
    public string Channel { get; set; } = "stable";
    public string? Notes { get; set; }
    public long UploadedAt { get; set; }
}

public sealed class OtaJobEntity
{
    public long Id { get; set; }
    public long FirmwareId { get; set; }
    public OtaStatus Status { get; set; }
    public bool Canary { get; set; }
    public long CreatedAt { get; set; }
    public string? CreatedBy { get; set; }
    public List<OtaItemEntity> Items { get; set; } = new();
}

public sealed class OtaItemEntity
{
    public long Id { get; set; }
    public long JobId { get; set; }
    [MaxLength(12)] public string DeviceId { get; set; } = "";
    public OtaStatus Status { get; set; }
    public int Progress { get; set; }
    public string? Error { get; set; }
    public string? FromVersion { get; set; }
    public long UpdatedAt { get; set; }
}

[Index(nameof(Ts))]
public sealed class EventEntity
{
    public long Id { get; set; }
    public long Ts { get; set; }
    [MaxLength(12)] public string? DeviceId { get; set; }
    public string Kind { get; set; } = "";
    public string Text { get; set; } = "";
    public string? Actor { get; set; }
}

public sealed class HomeDb(DbContextOptions<HomeDb> options) : DbContext(options)
{
    public DbSet<DeviceEntity> Devices => Set<DeviceEntity>();
    public DbSet<SampleEntity> Samples => Set<SampleEntity>();
    public DbSet<SampleHourEntity> SampleHours => Set<SampleHourEntity>();
    public DbSet<UserEntity> Users => Set<UserEntity>();
    public DbSet<AccessRequestEntity> AccessRequests => Set<AccessRequestEntity>();
    public DbSet<ApiTokenEntity> ApiTokens => Set<ApiTokenEntity>();
    public DbSet<RoomEntity> Rooms => Set<RoomEntity>();
    public DbSet<FirmwareEntity> Firmware => Set<FirmwareEntity>();
    public DbSet<OtaJobEntity> OtaJobs => Set<OtaJobEntity>();
    public DbSet<OtaItemEntity> OtaItems => Set<OtaItemEntity>();
    public DbSet<EventEntity> Events => Set<EventEntity>();

    protected override void OnModelCreating(ModelBuilder b)
    {
        b.Entity<OtaJobEntity>().HasMany(j => j.Items).WithOne().HasForeignKey(i => i.JobId).OnDelete(DeleteBehavior.Cascade);
    }
}

public static class Clock
{
    public static long NowMs => DateTimeOffset.UtcNow.ToUnixTimeMilliseconds();
    public static DateTimeOffset FromMs(long ms) => DateTimeOffset.FromUnixTimeMilliseconds(ms);
}
