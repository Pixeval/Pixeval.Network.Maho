using System.Net;

namespace Pixeval.Network.Maho;

public interface IDnsResolver
{
    Task<IPAddress[]> ResolveAsync(string hostname);
}