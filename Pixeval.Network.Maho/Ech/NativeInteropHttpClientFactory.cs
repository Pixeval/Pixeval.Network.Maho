using System.Net;
using System.Text.RegularExpressions;

namespace Pixeval.Network.Maho.Ech;

public static class NativeInteropEchEnabledHttpClientFactory
{
    public static HttpClient GetNativeInteropEchEnabledClient(INativeInteropDnsResolver dnsResolver, INativeInteropLogger logger)
    {
        return new HttpClient(new NativeInteropHttpMessageHandler(dnsResolver, logger));
    }
}