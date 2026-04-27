namespace Pixeval.Network.Maho.Ech;

public class NativeInteropHttpMessageHandler(
    INativeInteropDnsResolver dnsResolver,
    INativeInteropLogger logger) : HttpMessageHandler
{
    private readonly NativeClient _client = new(dnsResolver, logger);

    protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
    {
        await _client.InitClientAsync();
        return await _client.SendAsync(request);
    }

    protected override void Dispose(bool disposing)
    {
        if (disposing)
        {
            _client.Dispose();
        }

        base.Dispose(disposing);
    }
}
