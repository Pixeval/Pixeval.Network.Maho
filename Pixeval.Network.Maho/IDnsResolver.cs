using System.Net;

namespace Pixeval.Network.Maho;

public interface IDnsResolver
{
    Task<IPAddress[]> LookupAsync(string hostname);
}
