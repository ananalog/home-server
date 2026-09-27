using Home.Client;
using Home.Server.Data;
using Home.Server.Live;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Options;

namespace Home.Server.Auth;

public sealed class NoAccessException(long telegramId) : Exception("no access")
{
    public long TelegramId { get; } = telegramId;
}

/// <summary>Users (Telegram ids with roles) and access requests.</summary>
public sealed class UserService(IDbContextFactory<HomeDb> dbf, IOptions<HomeOptions> options, EventLog events, INotifier notifier)
{
    public static UserDto ToDto(UserEntity u) => new(u.TelegramId, u.Name, u.Role, u.Notify, Clock.FromMs(u.CreatedAt), u.AddedBy);

    /// <summary>Resolves a Telegram user at login; unknown users get an access request.</summary>
    public async Task<UserEntity> LoginAsync(TelegramUser tg)
    {
        await using var db = await dbf.CreateDbContextAsync();
        var user = await db.Users.FindAsync(tg.Id);
        if (user == null && options.Value.BootstrapAdmin == tg.Id && !await db.Users.AnyAsync())
        {
            user = new UserEntity { TelegramId = tg.Id, Name = tg.DisplayName, Role = Role.Admin, CreatedAt = Clock.NowMs, AddedBy = "bootstrap" };
            db.Users.Add(user);
            await db.SaveChangesAsync();
        }
        if (user == null)
        {
            await RequestAccessAsync(tg);
            throw new NoAccessException(tg.Id);
        }
        if (string.IsNullOrEmpty(user.Name) && tg.DisplayName.Length > 0)
        {
            user.Name = tg.DisplayName;
            await db.SaveChangesAsync();
        }
        return user;
    }

    public async Task<bool> RequestAccessAsync(TelegramUser tg)
    {
        await using var db = await dbf.CreateDbContextAsync();
        if (await db.Users.AnyAsync(u => u.TelegramId == tg.Id)) return false;
        var existing = await db.AccessRequests.FindAsync(tg.Id);
        if (existing != null) return false;
        db.AccessRequests.Add(new AccessRequestEntity { TelegramId = tg.Id, Name = tg.DisplayName, Username = tg.Username, RequestedAt = Clock.NowMs });
        await db.SaveChangesAsync();
        await events.AddAsync(null, "access-request", $"Запрос доступа: {tg.DisplayName} (@{tg.Username}, id {tg.Id})");
        notifier.Notify($"🔑 Запрос доступа: <b>{System.Net.WebUtility.HtmlEncode(tg.DisplayName)}</b> (@{tg.Username}, id <code>{tg.Id}</code>).\nРазрешить: <code>homectl users approve {tg.Id}</code> или в приложении.", adminsOnly: true);
        return true;
    }

    public async Task<List<UserDto>> ListAsync()
    {
        await using var db = await dbf.CreateDbContextAsync();
        return (await db.Users.AsNoTracking().ToListAsync()).OrderByDescending(u => u.Role).ThenBy(u => u.Name).Select(ToDto).ToList();
    }

    public async Task<List<AccessRequestDto>> RequestsAsync()
    {
        await using var db = await dbf.CreateDbContextAsync();
        return (await db.AccessRequests.AsNoTracking().ToListAsync()).OrderBy(r => r.RequestedAt)
            .Select(r => new AccessRequestDto(r.TelegramId, r.Name, r.Username, Clock.FromMs(r.RequestedAt))).ToList();
    }

    public async Task<UserDto> UpsertAsync(UserUpsertRequest r, string actor)
    {
        if (r.TelegramId <= 0) throw new ArgumentException("telegram id must be a positive number (for private chats chat id = user id)");
        await using var db = await dbf.CreateDbContextAsync();
        var u = await db.Users.FindAsync(r.TelegramId);
        var isNew = u == null;
        if (u == null)
        {
            var req = await db.AccessRequests.FindAsync(r.TelegramId);
            u = new UserEntity { TelegramId = r.TelegramId, Name = r.Name ?? req?.Name ?? "", Role = r.Role ?? Role.User, CreatedAt = Clock.NowMs, AddedBy = actor };
            db.Users.Add(u);
            if (req != null) db.AccessRequests.Remove(req);
        }
        else
        {
            if (r.Name != null) u.Name = r.Name;
            if (r.Role is { } role)
            {
                if (u.Role == Role.Admin && role != Role.Admin && await db.Users.CountAsync(x => x.Role == Role.Admin) == 1)
                    throw new ArgumentException("cannot demote the last admin");
                u.Role = role;
            }
        }
        if (r.Notify is { } n) u.Notify = n;
        await db.SaveChangesAsync();
        await events.AddAsync(null, "user", $"{(isNew ? "Добавлен" : "Изменён")} пользователь {u.Name} ({u.TelegramId}), роль {u.Role}", actor);
        if (isNew) notifier.NotifyUser(u.TelegramId, $"👋 Вам открыт доступ к дому (роль: {u.Role}). Откройте приложение кнопкой меню.");
        return ToDto(u);
    }

    public async Task RemoveAsync(long id, string actor)
    {
        await using var db = await dbf.CreateDbContextAsync();
        var u = await db.Users.FindAsync(id) ?? throw new KeyNotFoundException("user not found");
        if (u.Role == Role.Admin && await db.Users.CountAsync(x => x.Role == Role.Admin) == 1)
            throw new ArgumentException("cannot remove the last admin");
        db.Users.Remove(u);
        await db.SaveChangesAsync();
        await events.AddAsync(null, "user", $"Удалён пользователь {u.Name} ({u.TelegramId})", actor);
    }

    public async Task DenyAsync(long id, string actor)
    {
        await using var db = await dbf.CreateDbContextAsync();
        var r = await db.AccessRequests.FindAsync(id) ?? throw new KeyNotFoundException("request not found");
        db.AccessRequests.Remove(r);
        await db.SaveChangesAsync();
        await events.AddAsync(null, "user", $"Отклонён запрос доступа {r.Name} ({r.TelegramId})", actor);
    }
}
