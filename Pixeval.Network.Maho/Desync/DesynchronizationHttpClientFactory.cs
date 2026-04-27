using System.Net.Sockets;

namespace Pixeval.Network.Maho.Desync;

public static class DesynchronizationHttpClientFactory
{
    public static HttpClient GetDesynchronizationHttpClient(
        int realSendSleepMs,
        int socketTimeoutMs, 
        string fakeHttpContent,
        ITtlSniffer ttlSniffer,
        int ttlSniffTimeout,
        ITtlSpoofStrategy tltSpoofer,
        IDnsResolver dnsResolver)
    {
        return new HttpClient(new SocketsHttpHandler
        {
            ConnectCallback = ConnectCallback,
            UseProxy = false
        });

        async ValueTask<Stream> ConnectCallback(SocketsHttpConnectionContext ctx, CancellationToken cancellationToken)
        {
            var socket = new Socket(AddressFamily.InterNetwork, SocketType.Stream, ProtocolType.Tcp)
            {
                NoDelay = true
            };
            await socket.ConnectAsync(await dnsResolver.ResolveAsync(ctx.DnsEndPoint.Host), ctx.DnsEndPoint.Port, cancellationToken);
            var networkStream = new NetworkStream(socket, true);
            return new DesynchronizationWrapperStream(
                networkStream,
                ctx.DnsEndPoint,
                socket,
                ttlSniffer,
                tltSpoofer,
                fakeHttpContent,
                ttlSniffTimeout, 
                realSendSleepMs,
                socketTimeoutMs);
        }
    }
}
