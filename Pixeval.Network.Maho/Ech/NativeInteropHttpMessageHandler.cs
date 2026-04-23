using System.Net;
using System.Text.RegularExpressions;

namespace Pixeval.Network.Maho.Ech;

public class NativeInteropHttpMessageHandler(
    Dictionary<Regex, IPAddress[]> nameResolutionMap, 
    string dnsResolutionUrl,
    string logPath = "") : HttpMessageHandler
{
    private readonly NativeClient _client = new();

    protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
    {
        if (!_client.Initialized)
        {
            await _client.InitClientAsync(nameResolutionMap, dnsResolutionUrl, logPath);
        }
        return await _client.SendAsync(request);
    }
}