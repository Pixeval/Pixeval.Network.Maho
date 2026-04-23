using System.Collections.Concurrent;
using System.Net;
using System.Net.Sockets;
using System.Text;

namespace Pixeval.Network.Maho.Desync;

public class DesynchronizationWrapperStream(
    NetworkStream innerStream,
    DnsEndPoint endPoint,
    Socket socket,
    ITtlSniffer ttlSniffer,
    ITtlSpoofStrategy spoofer,
    string fakeHttpContent,
    int ttlSniffTimeout,
    int sleepMs,
    int socketTimeoutMs) : Stream
{
    private static readonly ConcurrentDictionary<string, uint> TtlCache = [];

    private ClientHelloStateMachine _stateMachine;

    private static string TryPadFakeContent(string fakeContent, int desiredLength)
    {
        if (fakeContent.Length >= desiredLength)
        {
            return fakeContent;
        }

        var diff = desiredLength - fakeContent.Length;
        var hostMarker = fakeContent.IndexOf("Host:", StringComparison.Ordinal) + 5;
        var first = fakeContent[..hostMarker];
        var second = fakeContent[hostMarker..];
        var padding = new string(' ', diff);
        return first + padding + second;
    }

    private async Task WriteAsyncInternal(byte[] buffer, int offset, int count, CancellationToken cancellationToken)
    {
        if (count == 0)
        {
            return;
        }

        if (_stateMachine.Completed)
        {
            await innerStream.WriteAsync(buffer.AsMemory(offset, count), cancellationToken);
            return;
        }

        switch (_stateMachine.FlowState(buffer, offset, count, out var packet, out var rmnOffset, out var rmnSize))
        {
            case ClientHelloPacketCollectingState.Idle:
                await innerStream.WriteAsync(buffer.AsMemory(offset, count), cancellationToken);
                break;
            case ClientHelloPacketCollectingState.CollectingHeader:
            case ClientHelloPacketCollectingState.Collecting:
                break;
            case ClientHelloPacketCollectingState.Emitted:
                using (var p = packet!)
                {
                    if (p.Memory.Length < Encoding.UTF8.GetByteCount(fakeHttpContent))
                    {
                        throw new DesynchronizationException("Client hello packet is too small to be desynchronized");
                    }

                    var sniLocator = new ServerNameLocator(p.Span);
                    switch (sniLocator.TryLocateServerName(out var positions))
                    {
                        case ServerNameLocatingResult.Located:
                            // we support only request with a single sni in this mode
                            var sniPosition = positions.Single();
                            var cut = Math.Max(Encoding.UTF8.GetByteCount(fakeHttpContent),
                                sniPosition.hostnameStart + sniPosition.hostnameLength / 2);
                            var fakeSendPart = p.Memory[..cut];
                            var realSendPart = p.Memory[cut..];
                            // we have ensured that the fake send data is at least as long as the `fakeSendPart`, which is the real data needs to be fake sent.
                            var fakeSendData = Encoding.UTF8.GetBytes(TryPadFakeContent(fakeHttpContent, fakeSendPart.Length));

                            if (!TtlCache.TryGetValue(endPoint.Host, out var realTtl))
                            {
                                var ttl = await ttlSniffer.ResolveTtlAsync(((IPEndPoint) socket.RemoteEndPoint!).Address, ttlSniffTimeout,
                                    endPoint.Port);
                                if (ttl == -1)
                                {
                                    throw new DesynchronizationException($"Cannot find the ttl of the target host: {endPoint.Host}");
                                }

                                realTtl = (uint) ttl;
                                TtlCache[endPoint.Host] = realTtl;
                            }

                            var fakeTtl = spoofer.Spoof(realTtl);
                            await SocketDesynchronizer.DesyncAsync(
                                socket,
                                (int) fakeTtl,
                                (int) realTtl,
                                sleepMs,
                                socketTimeoutMs,
                                fakeSendData,
                                fakeSendPart
                            );
                            await innerStream.WriteAsync(realSendPart, cancellationToken);
                            break;
                        case ServerNameLocatingResult.ServerNameExtensionNotFound:
                            await innerStream.WriteAsync(p.Memory, cancellationToken);
                            break;
                        default:
                            throw new DesynchronizationException("Invalid client hello packet");
                    }

                    if (rmnOffset > 0 && rmnSize > 0)
                    {
                        await innerStream.WriteAsync(buffer.AsMemory(rmnOffset, rmnSize), cancellationToken);
                    }
                }

                break;
            default:
                throw new ArgumentOutOfRangeException();
        }
    }

    public override void Write(byte[] buffer, int offset, int count)
    {
        WriteAsyncInternal(buffer, offset, count, CancellationToken.None).GetAwaiter().GetResult();
    }

    public override void Flush()
    {
        innerStream.Flush();
    }

    public override int Read(byte[] buffer, int offset, int count)
    {
        return innerStream.Read(buffer, offset, count);
    }

    public override long Seek(long offset, SeekOrigin origin)
    {
        return innerStream.Seek(offset, origin);
    }

    public override void SetLength(long value)
    {
        innerStream.SetLength(value);
    }

    public override bool CanRead => innerStream.CanRead;

    public override bool CanSeek => innerStream.CanSeek;

    public override bool CanWrite => innerStream.CanWrite;

    public override long Length => innerStream.Length;

    public override long Position
    {
        get => innerStream.Position;
        set => innerStream.Position = value;
    }

    protected override void Dispose(bool disposing)
    {
        innerStream.Dispose();
    }

    public override ValueTask DisposeAsync()
    {
        return innerStream.DisposeAsync();
    }
}