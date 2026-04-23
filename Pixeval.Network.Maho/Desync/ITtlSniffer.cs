using System.Net;

namespace Pixeval.Network.Maho.Desync;

public interface ITtlSniffer
{
    int MaxTtl { get; }
    
    int MinTtl { get; }
    
    Task<int> ResolveTtlAsync(IPAddress address, int timeout, int port);
}
