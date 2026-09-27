namespace Home.Server;

public sealed class HomeOptions
{
    public const string Section = "Home";

    /// <summary>Directory for the database, firmware images and the server key.</summary>
    public string DataDir { get; set; } = "/var/lib/home";

    /// <summary>HTTP listen addresses, e.g. "http://127.0.0.1:8080" (default when empty).</summary>
    public List<string> HttpUrls { get; set; } = new();

    /// <summary>Unix socket for the local CLI (no token needed). Empty to disable.</summary>
    public string? UnixSocket { get; set; } = "/run/home/api.sock";

    public int DevicePort { get; set; } = 7700;
    public int DiscoveryPort { get; set; } = 7701;
    public bool Mdns { get; set; } = true;

    /// <summary>Public HTTPS address of the Mini App, e.g. https://myhome.duckdns.org.</summary>
    public string? PublicUrl { get; set; }

    public TelegramOptions Telegram { get; set; } = new();

    /// <summary>Telegram id that becomes admin on first login while no users exist.</summary>
    public long? BootstrapAdmin { get; set; }

    /// <summary>Notify when an adopted device is offline longer than this (0 = off).</summary>
    public int OfflineAlertMinutes { get; set; } = 10;

    public int RawHistoryDays { get; set; } = 7;

    /// <summary>Development only: lets the Mini App log in as this Telegram id without initData.</summary>
    public long? DevUserId { get; set; }
}

public sealed class TelegramOptions
{
    /// <summary>Run the bot (long polling). The token is still used to verify Mini App logins when false.</summary>
    public bool BotEnabled { get; set; } = true;

    public string? BotToken { get; set; }
    public string? BotTokenFile { get; set; }

    /// <summary>How old Mini App initData may be.</summary>
    public int InitDataMaxAgeHours { get; set; } = 24;

    public string? ResolveToken()
    {
        if (!string.IsNullOrWhiteSpace(BotToken)) return BotToken.Trim();
        if (!string.IsNullOrWhiteSpace(BotTokenFile) && File.Exists(BotTokenFile)) return File.ReadAllText(BotTokenFile).Trim();
        return null;
    }
}
