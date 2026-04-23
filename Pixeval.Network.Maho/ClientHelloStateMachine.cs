using System.Buffers;
using Pixeval.Network.Maho.Desync;

namespace Pixeval.Network.Maho;

public enum ClientHelloPacketCollectingState
{
    Idle,
    CollectingHeader,
    Collecting,
    Emitted
}

public struct ClientHelloStateMachine()
{
    private ClientHelloPacketCollectingState _clientHelloPacketCollectingState = ClientHelloPacketCollectingState.Idle;
    private const int ClientHelloMessageIdentifier = 0x16;
    private int _totallyCopiedClientHelloPacketSize = -1;
    private const int ClientHelloTlsRecordHeaderLength = 5;
    private int _clientHelloPacketSize = -1;
    private ManagedByteBuffer? _clientHelloPacket = null;

    public bool Completed { get; private set; }

    private void ReplaceClientHelloPacket(int size)
    {
        _clientHelloPacket?.Dispose();
        _clientHelloPacket = new ManagedByteBuffer(size);
    }

    private void ReplaceClientHelloPacket(int size, byte[] buffer)
    {
        _clientHelloPacket?.Dispose();
        _clientHelloPacket = new ManagedByteBuffer(size);
        _clientHelloPacket.Fill(buffer, 0, size);
    }

    public ClientHelloPacketCollectingState FlowState(
        byte[] buffer,
        int offset,
        int count,
        out ManagedByteBuffer? packet,
        out int rmnOffSet,
        out int rmnSize)
    {
        rmnOffSet = 0;
        rmnSize = 0;
        packet = null;
        if (_clientHelloPacketCollectingState == ClientHelloPacketCollectingState.Idle && buffer[offset] != ClientHelloMessageIdentifier)
        {
            return ClientHelloPacketCollectingState.Idle;
        }

        switch (_clientHelloPacketCollectingState)
        {
            case ClientHelloPacketCollectingState.Idle when buffer[offset] == ClientHelloMessageIdentifier:
                // If this `Write` gives enough information of the record, that is, the record header is fully included.
                if (count >= 5)
                {
                    // we need to separate into two cases
                    // 1. this buffer contains entire client hello packet, with potentially extra bytes
                    // 2. this buffer contains only a part of the client hello packet.

                    _clientHelloPacketCollectingState = ClientHelloPacketCollectingState.Collecting;
                    // reserve a byte array for the entire client hello packet (including the tls record header)
                    var clientHelloPayloadSize = (buffer[offset + 3] << 8) | buffer[offset + 4];
                    ReplaceClientHelloPacket(5 + clientHelloPayloadSize);

                    // Case 1: this buffer contains the entirety of the packet, with potential extra bytes.
                    if (count >= _clientHelloPacketSize)
                    {
                        _clientHelloPacket!.Fill(buffer, offset, count - _clientHelloPacketSize);
                        _totallyCopiedClientHelloPacketSize = _clientHelloPacketSize;
                        // we send the fragmented packet and flow to `Emitted` state immediately.
                        _clientHelloPacketCollectingState = ClientHelloPacketCollectingState.Emitted;
                        Completed = true;

                        packet = _clientHelloPacket;
                        // if there are extra data, then we need to send the remaining bytes in the current buffer.
                        if (count > _clientHelloPacketSize)
                        {
                            var remainingOffset = offset + _clientHelloPacketSize;
                            rmnOffSet = remainingOffset;
                            rmnSize = count - _clientHelloPacketSize;
                        }
                    }
                    // Case 2: this buffer does not contain the entirety of the packet.
                    else
                    {
                        _clientHelloPacket!.Fill(buffer, offset, count);
                        _totallyCopiedClientHelloPacketSize = count;
                        _clientHelloPacketCollectingState = ClientHelloPacketCollectingState.Collecting;
                    }
                }
                else
                {
                    _clientHelloPacketCollectingState = ClientHelloPacketCollectingState.CollectingHeader;
                    // if the current read gives less than 5 bytes, we don't obtain the actual length of the payload
                    // we allocate a small byte array to hold the metadata first.
                    ReplaceClientHelloPacket(5);
                    _clientHelloPacket!.Fill(buffer, offset, count);
                    _totallyCopiedClientHelloPacketSize = count;
                }
                break;
            case ClientHelloPacketCollectingState.CollectingHeader:
                // at this stage, _clientHelloPacket is guaranteed to be not null.
                // in that case, the last copy gives less length than 5, therefore not enough
                // to decide the length of the payload.

                // if this read still don't give the enough information we need
                if (_totallyCopiedClientHelloPacketSize + count < ClientHelloTlsRecordHeaderLength)
                {
                    _clientHelloPacket!.Fill(buffer, offset, count, _totallyCopiedClientHelloPacketSize);
                    _totallyCopiedClientHelloPacketSize += count;
                }
                else
                {
                    // flow to the collecting state, we have collected all header information
                    _clientHelloPacketCollectingState = ClientHelloPacketCollectingState.Collecting;
                    // we first fill the old buffer so that we can get the payload size
                    var headerRemaining = ClientHelloTlsRecordHeaderLength - _totallyCopiedClientHelloPacketSize;
                    _clientHelloPacket!.Fill(buffer, offset, headerRemaining, _totallyCopiedClientHelloPacketSize);
                    // now the old buffer has exactly 5 elements, we can check the payload size
                    var payloadSize = (_clientHelloPacket.Span[3] << 8) | _clientHelloPacket.Span[4];
                    // allocate a full-sized buffer
                    _clientHelloPacketSize = ClientHelloTlsRecordHeaderLength + payloadSize;
                    var newBuffer = ArrayPool<byte>.Shared.Rent(_clientHelloPacketSize);
                    // copy the old buffer to the new buffer. Old buffer contains only 5 bytes
                    _clientHelloPacket.Fill(newBuffer, 0, ClientHelloTlsRecordHeaderLength);
                    // since we've copied `headerRemaining` elements, the next copy should skip these elements
                    var newOffset = offset + headerRemaining;
                    // copied to the segment start with offset 5
                    var toBeCopiedCount = Math.Min(count - headerRemaining, _clientHelloPacketSize - ClientHelloTlsRecordHeaderLength);
                    Buffer.BlockCopy(buffer, newOffset, newBuffer, ClientHelloTlsRecordHeaderLength, toBeCopiedCount);
                    ReplaceClientHelloPacket(_clientHelloPacketSize, newBuffer);
                    // ---++
                    //    ++****
                    // "-": the old buffer element
                    // "+": header remaining draw from this write
                    // "*": elements in this write
                    _totallyCopiedClientHelloPacketSize = 5 + toBeCopiedCount;

                    // we check if this `Write` already fills all the client hello packet
                    if (_totallyCopiedClientHelloPacketSize == _clientHelloPacketSize)
                    {
                        _clientHelloPacketCollectingState = ClientHelloPacketCollectingState.Emitted;
                        Completed = true;
                        packet = _clientHelloPacket;
                        // now, we need to check if there are extra bytes
                        // totalRead = all elements up to `_clientHelloPacketSize` that aren't
                        // the ones in the original buffer,the count of elements in the original buffer is
                        // clientHelloTlsRecordHeaderLength - headerRemaining
                        var totalReadSizeFromBuffer = headerRemaining + toBeCopiedCount;
                        if (count > totalReadSizeFromBuffer)
                        {
                            var remainingOffset = offset + totalReadSizeFromBuffer;
                            rmnOffSet = remainingOffset;
                            rmnSize = count - totalReadSizeFromBuffer;
                        }
                    }
                }
                break;
            case ClientHelloPacketCollectingState.Collecting:
                var toCopyCount = Math.Min(count, _clientHelloPacketSize - _totallyCopiedClientHelloPacketSize);
                _clientHelloPacket!.Fill(buffer, offset, toCopyCount, _totallyCopiedClientHelloPacketSize);
                _totallyCopiedClientHelloPacketSize += toCopyCount;
                if (_totallyCopiedClientHelloPacketSize == _clientHelloPacketSize)
                {
                    _clientHelloPacketCollectingState = ClientHelloPacketCollectingState.Emitted;
                    Completed = true;
                    packet = _clientHelloPacket!;
                    // now, we need to check if there are extra bytes
                    if (count > toCopyCount)
                    {
                        var remainingOffset = offset + toCopyCount;
                        rmnOffSet = remainingOffset;
                        rmnSize = count - toCopyCount;
                    }
                }
                break;
            case ClientHelloPacketCollectingState.Emitted:
                break;
        }

        return _clientHelloPacketCollectingState;
    }
}