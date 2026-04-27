namespace Pixeval.Network.Maho.Ech;

public static class NativeInteropEchEnabledHttpMessageHandlerFactory
{
    public static HttpMessageHandler GetNativeInteropEchEnabledHandler(INativeInteropDnsResolver dnsResolver, INativeInteropLogger logger)
    {
        return new NativeInteropHttpMessageHandler(dnsResolver, logger);
    }
}
