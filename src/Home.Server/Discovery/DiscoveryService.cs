using System.Net;
using System.Net.Sockets;
using System.Text;
using Makaretu.Dns;
using Microsoft.Extensions.Options;

namespace Home.Server.Discovery;

/// <summary>
/// Lets devices find the server: mDNS service _home._tcp and a UDP responder
/// (device broadcasts "HOME?" to port 7701, the server answers "HOME <tcp-port>").
/// </summary>
public sealed class DiscoveryService(IOptions<HomeOptions> options, ILogger<DiscoveryService> log) : BackgroundService
{
    public const string Query = "HOME?";

    protected override async Task ExecuteAsync(CancellationToken ct)
    {
        var o = options.Value;
        ServiceDiscovery? sd = null;
        if (o.Mdns)
        {
            try
            {
                sd = new ServiceDiscovery();
                var profile = new ServiceProfile("home-server", "_home._tcp", (ushort)o.DevicePort);
                profile.AddProperty("v", Home.Protocol.ProtoInfo.Major.ToString());
                sd.Advertise(profile);
                log.LogInformation("mDNS: advertising _home._tcp on port {Port}", o.DevicePort);
            }
            catch (Exception e)
            {
                log.LogWarning("mDNS disabled: {Error}", e.Message);
            }
        }

        try
        {
            using var udp = new UdpClient(new IPEndPoint(IPAddress.Any, o.DiscoveryPort)) { EnableBroadcast = true };
            log.LogInformation("discovery: UDP {Port}", o.DiscoveryPort);
            var answer = Encoding.ASCII.GetBytes($"HOME {o.DevicePort}");
            while (!ct.IsCancellationRequested)
            {
                var r = await udp.ReceiveAsync(ct);
                if (Encoding.ASCII.GetString(r.Buffer).Trim() == Query)
                    await udp.SendAsync(answer, r.RemoteEndPoint, ct);
            }
        }
        catch (OperationCanceledException) { }
        catch (Exception e)
        {
            log.LogWarning("UDP discovery disabled: {Error}", e.Message);
        }
        finally
        {
            sd?.Dispose();
        }
    }
}
