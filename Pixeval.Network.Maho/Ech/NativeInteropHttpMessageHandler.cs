using System.Net;
using System.Text.RegularExpressions;

namespace Pixeval.Network.Maho.Ech;

public class NativeInteropHttpMessageHandler(
    IDnsResolver dnsResolver, 
    string dnsResolutionUrl,
    string logPath = "") : HttpMessageHandler
{
    private readonly NativeClient _client = new(dnsResolver);

    protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
    {
        if (!_client.Initialized)
        {
            await _client.InitClientAsync(dnsResolutionUrl, logPath);
        }
        return await _client.SendAsync(request);
    }
}