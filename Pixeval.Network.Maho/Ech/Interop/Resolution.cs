using System.Runtime.InteropServices;

namespace Pixeval.Network.Maho.Ech.Interop;

[UnmanagedFunctionPointer(CallingConvention.Cdecl)]
public delegate void ManagedDnsResolutionCallback(
    long requestToken, 
    [MarshalAs(UnmanagedType.LPUTF8Str)] string hostname);

public static partial class Resolution
{
    [LibraryImport("pixeval_ech")]
    public static partial InteropOperationResult complete_resolution(
        nint clientHandle,
        long requestToken,
        nint ipAddresses,
        nuint ipLen);
    
    [LibraryImport("pixeval_ech")]
    public static partial InteropOperationResult complete_resolution_failure(
        nint clientHandle,
        long requestToken,
        [MarshalAs(UnmanagedType.LPUTF8Str)] string errorReason);
}