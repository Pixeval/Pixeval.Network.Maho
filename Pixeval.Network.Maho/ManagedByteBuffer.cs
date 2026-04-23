using System.Buffers;

namespace Pixeval.Network.Maho.Desync;

public class ManagedByteBuffer : IMemoryOwner<byte>
{
    private readonly byte[] _buffer;
    
    public Memory<byte> Memory { get; }

    public Span<byte> Span => Memory.Span;
    
    public ManagedByteBuffer(int size)
    {
        _buffer = ArrayPool<byte>.Shared.Rent(size);
        Memory = _buffer.AsMemory(0, size);
    }
    
    public void Fill(byte[] source, int sourceOffset, int count, int destinationOffset = 0)
    {
        Buffer.BlockCopy(source, sourceOffset, _buffer, destinationOffset, count);
    }
    
    public void Dispose()
    {
        ArrayPool<byte>.Shared.Return(_buffer);
    }
}