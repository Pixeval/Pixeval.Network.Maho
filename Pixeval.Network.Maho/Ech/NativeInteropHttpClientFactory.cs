using System.Net;
using System.Text.RegularExpressions;

namespace Pixeval.Network.Maho.Ech;

public static class NativeInteropEchEnabledHttpClientFactory
{
    public static HttpClient GetNativeInteropEchEnabledClient(IDnsResolver dnsResolver, string logPath)
    {
        return new HttpClient(new NativeInteropHttpMessageHandler(dnsResolver, logPath));
    }
}