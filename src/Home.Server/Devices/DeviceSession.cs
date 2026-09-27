using System.Collections.Concurrent;
using System.Net;
using Home.Protocol;
using Microsoft.AspNetCore.Connections;

namespace Home.Server.Devices;

/// <summary>Raised when a device answers with an error or does not answer in time.</summary>
public sealed class DeviceException(ErrorCode code, string message) : Exception(message)
{
    public ErrorCode Code { get; } = code;
}

/// <summary>One TCP connection of a device: framed sends and request/response matching by req_id.</summary>
public sealed class DeviceSession
{
    private readonly ConnectionContext _conn;
    private readonly SemaphoreSlim _sendLock = new(1, 1);
    private readonly ConcurrentDictionary<ushort, TaskCompletionSource<Message>> _pending = new();
    private int _nextReqId;

    public DeviceSession(ConnectionContext conn)
    {
        _conn = conn;
        RemoteIp = (conn.RemoteEndPoint as IPEndPoint)?.Address.MapToIPv4().ToString();
    }

    public string ConnectionId => _conn.ConnectionId;
    public string? RemoteIp { get; }
    public string? DeviceId { get; set; }
    public HelloReq? Hello { get; set; }
    public long LastRxMs { get; set; } = Data.Clock.NowMs;
    public CancellationToken Closed => _conn.ConnectionClosed;
    public bool IsClosed => _conn.ConnectionClosed.IsCancellationRequested;

    public async Task SendRawAsync(byte[] msg)
    {
        var frame = TcpFrame.Encode(msg);
        await _sendLock.WaitAsync();
        try
        {
            await _conn.Transport.Output.WriteAsync(frame);
        }
        finally
        {
            _sendLock.Release();
        }
    }

    public Task SendAsync(IProtoMessage body, ushort reqId, Flags extra = 0) => SendRawAsync(Codec.Encode(body, reqId, extra));

    public Task SendErrorAsync(MsgType type, ushort reqId, ErrorCode code, string text) =>
        SendRawAsync(Codec.EncodeError(type, reqId, code, text));

    /// <summary>Sends a request and waits for the response with the same req_id.</summary>
    public async Task<IProtoBody> RequestAsync(IProtoMessage req, TimeSpan? timeout = null)
    {
        ushort id;
        TaskCompletionSource<Message> tcs;
        do
        {
            id = (ushort)Interlocked.Increment(ref _nextReqId);
            tcs = new TaskCompletionSource<Message>(TaskCreationOptions.RunContinuationsAsynchronously);
        } while (id == 0 || !_pending.TryAdd(id, tcs));

        try
        {
            if (_failed) throw new DeviceException(ErrorCode.Timeout, "device disconnected");
            await SendAsync(req, id);
            Message resp;
            try
            {
                // Not linked to ConnectionClosed: frames received before the close must still be delivered.
                // Pending requests are failed by FailPending() once the read loop has ended.
                resp = await tcs.Task.WaitAsync(timeout ?? TimeSpan.FromSeconds(10));
            }
            catch (TimeoutException)
            {
                throw new DeviceException(ErrorCode.Timeout, IsClosed ? "device disconnected" : $"no answer to {req.Type}");
            }
            if (resp.Body is ErrorBody err)
                throw new DeviceException(err.Code ?? ErrorCode.Internal, err.Text ?? err.Code?.ToString() ?? "device error");
            return resp.Body;
        }
        finally
        {
            _pending.TryRemove(id, out _);
        }
    }

    public async Task<T> RequestAsync<T>(IProtoMessage req, TimeSpan? timeout = null) where T : class, IProtoBody =>
        await RequestAsync(req, timeout) as T ?? throw new DeviceException(ErrorCode.BadRequest, $"unexpected answer to {req.Type}");

    private volatile bool _failed;

    /// <summary>Called after the read loop ended: every waiting request fails with "device disconnected".</summary>
    public void FailPending()
    {
        _failed = true;
        foreach (var (id, tcs) in _pending)
            if (_pending.TryRemove(id, out _)) tcs.TrySetException(new DeviceException(ErrorCode.Timeout, "device disconnected"));
    }

    /// <summary>Delivers a response to the waiting request. Returns false if nobody waits for it.</summary>
    public bool Complete(Message m) => _pending.TryRemove(m.Header.ReqId, out var tcs) && tcs.TrySetResult(m);

    public void Abort() => _conn.Abort();

    /// <summary>Graceful close: flushes what was sent, then drops the connection.</summary>
    public async Task CloseAsync()
    {
        await _sendLock.WaitAsync();
        try
        {
            await _conn.Transport.Output.CompleteAsync();
        }
        finally
        {
            _sendLock.Release();
        }
        await Task.Delay(500);
        _conn.Abort();
    }
}
