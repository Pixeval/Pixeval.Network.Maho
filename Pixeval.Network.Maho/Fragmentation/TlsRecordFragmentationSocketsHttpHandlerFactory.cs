using System.Net.Sockets;

namespace Pixeval.Network.Maho.Fragmentation;

public static class TlsRecordFragmentationSocketsHttpHandlerFactory
{
    public static SocketsHttpHandler GetTlsFragmentedHandler(IDnsResolver dnsResolver)
    {
        return new SocketsHttpHandler
        {
            ConnectCallback = ConnectCallback,
            UseProxy = false
        };

        async ValueTask<Stream> ConnectCallback(SocketsHttpConnectionContext ctx, CancellationToken cancellationToken)
        {
            var socket = new Socket(SocketType.Stream, ProtocolType.Tcp);
            await socket.ConnectAsync(await dnsResolver.LookupAsync(ctx.DnsEndPoint.Host), ctx.DnsEndPoint.Port, cancellationToken);
            var networkStream = new NetworkStream(socket, true);
            return new TlsRecordFragmentedStream(networkStream);
        }
    }
}
