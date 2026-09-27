using System.Collections.Concurrent;
using System.Text.Json;
using System.Text.Json.Serialization.Metadata;
using System.Threading.Channels;
using Home.Client;

namespace Home.Server.Live;

/// <summary>Fan-out of live events to server-sent-event subscribers (Mini App, homectl watch).</summary>
public sealed class EventBus
{
    public sealed class Subscription : IDisposable
    {
        private readonly EventBus _bus;
        internal readonly Channel<(string Type, string Json)> Channel =
            System.Threading.Channels.Channel.CreateBounded<(string, string)>(new BoundedChannelOptions(512) { FullMode = BoundedChannelFullMode.DropOldest });

        internal Subscription(EventBus bus, string? logsOf)
        {
            _bus = bus;
            LogsOf = logsOf;
        }

        public string? LogsOf { get; }
        public ChannelReader<(string Type, string Json)> Reader => Channel.Reader;
        public void Dispose() => _bus._subs.TryRemove(this, out _);
    }

    private readonly ConcurrentDictionary<Subscription, byte> _subs = new();

    public int Subscribers => _subs.Count;

    public Subscription Subscribe(string? logsOf = null)
    {
        var s = new Subscription(this, logsOf);
        _subs[s] = 0;
        return s;
    }

    private static JsonTypeInfo<T> Info<T>() => (JsonTypeInfo<T>)HomeJson.Default.GetTypeInfo(typeof(T))!;

    public void Publish<T>(string type, T payload)
    {
        if (_subs.IsEmpty) return;
        var json = JsonSerializer.Serialize(payload, Info<T>());
        foreach (var s in _subs.Keys) s.Channel.Writer.TryWrite((type, json));
    }

    public void PublishLog(string deviceId, LogLineDto line)
    {
        if (_subs.IsEmpty) return;
        string? json = null;
        foreach (var s in _subs.Keys)
        {
            if (s.LogsOf != deviceId) continue;
            json ??= JsonSerializer.Serialize(new LogEvent(deviceId, line), Info<LogEvent>());
            s.Channel.Writer.TryWrite(("log", json));
        }
    }
}
