using System.Buffers;
using System.Diagnostics;
using System.Runtime.InteropServices;

namespace Pixeval.Network.Maho.Fragmentation;

public class TlsRecordFragmentedStream(Stream innerStream) : Stream
{
    private ClientHelloStateMachine _stateMachine;
    private const int ClientHelloTlsRecordHeaderLength = 5;

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
                    await SplitTlsRecordAndSendAsync(p.Memory, cancellationToken).ConfigureAwait(false);
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
        WriteAsyncInternal(buffer, offset, count, CancellationToken.None)
            .ConfigureAwait(false)
            .GetAwaiter()
            .GetResult();
    }

    public override void Write(ReadOnlySpan<byte> buffer)
    {
        if (buffer.Length == 0)
        {
            return;
        }

        var rented = ArrayPool<byte>.Shared.Rent(buffer.Length);
        try
        {
            buffer.CopyTo(rented);
            Write(rented, 0, buffer.Length);
        }
        finally
        {
            ArrayPool<byte>.Shared.Return(rented);
        }
    }

    public override Task WriteAsync(byte[] buffer, int offset, int count, CancellationToken cancellationToken)
    {
        return WriteAsyncInternal(buffer, offset, count, cancellationToken);
    }

    public override ValueTask WriteAsync(ReadOnlyMemory<byte> buffer, CancellationToken cancellationToken = default)
    {
        if (cancellationToken.IsCancellationRequested)
        {
            return ValueTask.FromCanceled(cancellationToken);
        }

        if (MemoryMarshal.TryGetArray(buffer, out var segment) && segment.Array is not null)
        {
            return new ValueTask(WriteAsync(segment.Array, segment.Offset, segment.Count, cancellationToken));
        }

        var rented = ArrayPool<byte>.Shared.Rent(buffer.Length);
        try
        {
            buffer.Span.CopyTo(rented);
            return new ValueTask(WriteAsync(rented, 0, buffer.Length, cancellationToken));
        }
        finally
        {
            ArrayPool<byte>.Shared.Return(rented);
        }
    }

    private async Task SplitTlsRecordAndSendAsync(Memory<byte> originalClientHelloPacket, CancellationToken cancellationToken)
    {
        var locator = new ServerNameLocator(originalClientHelloPacket.Span);
        if (locator.TryLocateServerName(out var result) is ServerNameLocatingResult.Located)
        {
            var cuts = result.SelectMany(loc => new[] { loc.hostnameStart, loc.hostnameStart + loc.hostnameLength / 2})
                .ToList();
            var recordHeaderBuffer = ArrayPool<byte>.Shared.Rent(ClientHelloTlsRecordHeaderLength);
            originalClientHelloPacket.Span[..ClientHelloTlsRecordHeaderLength].CopyTo(recordHeaderBuffer);

            try
            {
                var start = 0;
                for (var index = 0; index <= cuts.Count; index++)
                {
                    if (index != 0)
                    {
                        await Task.Delay(100, cancellationToken);
                    }
                    var end = index < cuts.Count ? cuts[index] : originalClientHelloPacket.Length;
                    if (end < start || end > originalClientHelloPacket.Length)
                    {
                        throw new ArgumentOutOfRangeException(nameof(result), "Offsets must be sorted and within bounds.");
                    }

                    var item = originalClientHelloPacket[start..end];
                    if (index == 0) // if it's the first slice, it contains the original record header
                    {
                        var fragmentLength = item.Length - ClientHelloTlsRecordHeaderLength;
                        if (item.Length < ClientHelloTlsRecordHeaderLength)
                        {
                            throw new InvalidOperationException("The ClientHello message is ill-formed: The first sliced does not contain a full record header");
                        }

                        // change the minor version of the record header: the state machine of the firewall currently does not flow to the correct state when the minor version it
                        // sees is not a standard 0x03 0x01
                        item.Span[1] = 0x03;
                        item.Span[2] = 0x09;
                        item.Span[3] = (byte) (fragmentLength >> 8);
                        item.Span[4] = (byte) fragmentLength;
                        Debug.WriteLine($"TlsRecordFragmentedStream.Write 1 hex={Convert.ToHexString(item.Span)}");
                    }
                    else
                    {
                        var fragmentLength = item.Length;
                        // change the minor version of the record header: the state machine of the firewall currently does not flow to the correct state when the minor version it
                        // sees is not a standard 0x03 0x01
                        recordHeaderBuffer[1] = 0x03;
                        recordHeaderBuffer[2] = 0x09;
                        recordHeaderBuffer[3] = (byte) (fragmentLength >> 8);
                        recordHeaderBuffer[4] = (byte) fragmentLength;
                        Debug.WriteLine($"TlsRecordFragmentedStream.Write 2 header={Convert.ToHexString(recordHeaderBuffer, 0, ClientHelloTlsRecordHeaderLength)} payload={Convert.ToHexString(item.Span)}");
                        await innerStream.WriteAsync(recordHeaderBuffer.AsMemory(0, ClientHelloTlsRecordHeaderLength), cancellationToken);
                        await innerStream.FlushAsync(cancellationToken);
                        await Task.Delay(100, cancellationToken);
                    }

                    await innerStream.WriteAsync(item, cancellationToken);
                    await innerStream.FlushAsync(cancellationToken);

                    start = end;
                }
            }
            finally
            {
                ArrayPool<byte>.Shared.Return(recordHeaderBuffer);
            }
        }
    }

    // Forward other members to _innerStream...
    public override bool CanRead => innerStream.CanRead;
    public override bool CanSeek => innerStream.CanSeek;
    public override bool CanWrite => innerStream.CanWrite;
    public override long Length => innerStream.Length;
    public override long Position { get => innerStream.Position; set => innerStream.Position = value; }
    public override void Flush() => innerStream.Flush();
    public override int Read(byte[] buffer, int offset, int count) => innerStream.Read(buffer, offset, count);
    public override long Seek(long offset, SeekOrigin origin) => innerStream.Seek(offset, origin);
    public override void SetLength(long value) => innerStream.SetLength(value);
    public override void Close()
    {
        innerStream.Close();
    }
}
