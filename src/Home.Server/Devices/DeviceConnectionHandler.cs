using Home.Protocol;
using Microsoft.AspNetCore.Connections;

namespace Home.Server.Devices;

/// <summary>Kestrel handler of the device TCP port: framing, HELLO handshake, dispatch, keepalive.</summary>
public sealed class DeviceConnectionHandler(DeviceManager manager, ILogger<DeviceConnectionHandler> log) : ConnectionHandler
{
    public static readonly TimeSpan HelloTimeout = TimeSpan.FromSeconds(15);
    public static readonly TimeSpan PingInterval = TimeSpan.FromSeconds(30);

    public override async Task OnConnectedAsync(ConnectionContext connection)
    {
        var session = new DeviceSession(connection);
        log.LogDebug("device connection {Id} from {Ip}", connection.ConnectionId, session.RemoteIp);
        var keepalive = KeepaliveAsync(session);
        try
        {
            var input = connection.Transport.Input;
            while (true)
            {
                var result = await input.ReadAsync(connection.ConnectionClosed);
                var buffer = result.Buffer;
                while (TcpFrame.TryDecode(ref buffer, out var msg))
                {
                    session.LastRxMs = Data.Clock.NowMs;
                    await HandleAsync(session, msg!);
                }
                input.AdvanceTo(buffer.Start, buffer.End);
                if (result.IsCompleted || result.IsCanceled) break;
            }
        }
        catch (OperationCanceledException) { }
        catch (ConnectionResetException) { }
        catch (Exception e)
        {
            log.LogWarning(e, "device connection {Id} ({Device}) failed", connection.ConnectionId, session.DeviceId);
        }
        finally
        {
            session.Abort();
            session.FailPending();
            await manager.OnDisconnectedAsync(session);
            await keepalive;
        }
    }

    private async Task HandleAsync(DeviceSession session, byte[] raw)
    {
        Message msg;
        try
        {
            msg = Codec.Decode(raw);
        }
        catch (ProtoException e)
        {
            // Answer requests we could not parse; ignore broken responses.
            try
            {
                var h = Header.Read(raw);
                if (!h.IsResponse) await session.SendErrorAsync(h.Type, h.ReqId, e.Code, e.Message);
            }
            catch (ProtoException) { }
            log.LogDebug("bad message from {Device}: {Error}", session.DeviceId, e.Message);
            return;
        }

        var h2 = msg.Header;
        if (h2.IsResponse)
        {
            session.Complete(msg);
            return;
        }

        if (session.DeviceId == null && h2.Type != MsgType.Hello)
        {
            await session.SendErrorAsync(h2.Type, h2.ReqId, ErrorCode.Forbidden, "HELLO first");
            return;
        }

        switch (msg.Body)
        {
            case HelloReq hello:
                var resp = await manager.OnHelloAsync(session, hello);
                await session.SendAsync(resp, h2.ReqId);
                if (resp.State == AdoptState.Blocked) _ = session.CloseAsync();
                else manager.AfterHello(session);
                break;
            case PingReq ping:
                await session.SendAsync(new PingResp { TimeMs = ping.TimeMs }, h2.ReqId);
                break;
            case StateReq or ReportReq or EventReq or LogReq:
                manager.OnDeviceMessage(session, msg.Body);
                break;
            default:
                if ((h2.Flags & Flags.Noack) == 0)
                    await session.SendErrorAsync(h2.Type, h2.ReqId, ErrorCode.Unsupported, "not handled by server");
                break;
        }
    }

    private async Task KeepaliveAsync(DeviceSession session)
    {
        try
        {
            await Task.Delay(HelloTimeout, session.Closed);
            if (session.DeviceId == null)
            {
                log.LogInformation("no HELLO from {Ip}, closing", session.RemoteIp);
                session.Abort();
                return;
            }
            var misses = 0;
            while (!session.IsClosed)
            {
                await Task.Delay(PingInterval, session.Closed);
                if (Data.Clock.NowMs - session.LastRxMs < PingInterval.TotalMilliseconds) continue;
                try
                {
                    await session.RequestAsync(new PingReq { TimeMs = (ulong)Data.Clock.NowMs }, TimeSpan.FromSeconds(10));
                    misses = 0;
                }
                catch (DeviceException)
                {
                    if (++misses >= 2)
                    {
                        log.LogInformation("device {Device} does not answer pings, closing", session.DeviceId);
                        session.Abort();
                    }
                }
            }
        }
        catch (OperationCanceledException) { }
        catch (Exception e)
        {
            log.LogDebug(e, "keepalive stopped");
        }
    }
}
