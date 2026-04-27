using System.Net;

namespace Pixeval.Network.Maho.Ech;

public interface INativeInteropDnsResolver
{
    string BaseResolutionUrl { get; }
    
    Task<IPAddress[]> LookupAsync(string hostname);
}