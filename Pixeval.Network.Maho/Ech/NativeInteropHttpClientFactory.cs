using System.Net;
using System.Text.RegularExpressions;

namespace Pixeval.Network.Maho.Ech;

public static class NativeInteropEchEnabledHttpClientFactory
{
    public static HttpClient GetNativeInteropEchEnabledClient(Dictionary<Regex, IPAddress[]> nameResolutionMap, string logPath)
    {
        return new HttpClient(new NativeInteropHttpMessageHandler(nameResolutionMap, logPath));
    }
}