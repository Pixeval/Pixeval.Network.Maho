using System.Collections.Concurrent;
using System.Runtime.InteropServices;

namespace Pixeval.Network.Maho.Ech;

[StructLayout(LayoutKind.Sequential)]
public struct NativeDnsResolutionToken
{
    public long RequestId;
    public unsafe byte** IpAddresses;
    public nuint IpLen;
}

public static partial class NativeResolution
{
    private static ConcurrentDictionary<long, >
    
    [LibraryImport("pixeval_ech")]
    [return: MarshalAs(UnmanagedType.U1)]
    private static unsafe partial bool register_dns_resolution_callback(
        delegate* unmanaged<NativeDnsResolutionToken, void> nativeDnsResolutionCallback);

    [LibraryImport("pixeval_ech")]
    private static unsafe partial void complete_resolution(long requestId, nint ipAddresses, nuint ipLen);

    public static unsafe bool RegisterDnsResolutionCallback(delegate* unmanaged<NativeDnsResolutionToken, void> nativeDnsResolutionCallback)
    {
        return register_dns_resolution_callback(nativeDnsResolutionCallback);
    }
}