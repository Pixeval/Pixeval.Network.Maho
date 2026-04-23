using System.Net.Sockets;

namespace Pixeval.Network.Maho.Fragmentation;

public static class TlsRecordFragmentationHttpClientFactory
{
    public static HttpClient GetTlsFragmentedHttpClient(IDnsResolver dnsResolver)
    {
        return new HttpClient(new SocketsHttpHandler
        {
            ConnectCallback = ConnectCallback,
            UseProxy = false
        });

        async ValueTask<Stream> ConnectCallback(SocketsHttpConnectionContext ctx, CancellationToken cancellationToken)
        {
            var socket = new Socket(SocketType.Stream, ProtocolType.Tcp);
            await socket.ConnectAsync(await dnsResolver.ResolveAsync(ctx.DnsEndPoint.Host), ctx.DnsEndPoint.Port, cancellationToken);
            var networkStream = new NetworkStream(socket, true);
            return new TlsRecordFragmentedStream(networkStream);
        }
    }
}