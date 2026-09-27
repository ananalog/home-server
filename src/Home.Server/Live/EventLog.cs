using Home.Client;
using Home.Server.Data;
using Microsoft.EntityFrameworkCore;

namespace Home.Server.Live;

/// <summary>Sends a message to people (implemented by the Telegram bot; no-op without a bot).</summary>
public interface INotifier
{
    void Notify(string text, bool adminsOnly = false);
    void NotifyUser(long telegramId, string text);
}

public sealed class NullNotifier : INotifier
{
    public void Notify(string text, bool adminsOnly = false) { }
    public void NotifyUser(long telegramId, string text) { }
}

/// <summary>Journal of device events and user actions; persisted and pushed to live subscribers.</summary>
public sealed class EventLog(IDbContextFactory<HomeDb> dbf, EventBus bus, ILogger<EventLog> log)
{
    public async Task AddAsync(string? deviceId, string kind, string text, string? actor = null)
    {
        try
        {
            await using var db = await dbf.CreateDbContextAsync();
            var e = new EventEntity { Ts = Clock.NowMs, DeviceId = deviceId, Kind = kind, Text = text, Actor = actor };
            db.Events.Add(e);
            await db.SaveChangesAsync();
            bus.Publish("event", ToDto(e));
        }
        catch (Exception ex)
        {
            log.LogWarning(ex, "cannot write event");
        }
    }

    public static EventDto ToDto(EventEntity e) => new(e.Id, Clock.FromMs(e.Ts), e.DeviceId, e.Kind, e.Text, e.Actor);
}
