using System.Net;

namespace Pixeval.Network.Maho.Ech;

public interface IDnsResolver
{
    Task<IPAddress[]> LookupAsync(string hostname);
}