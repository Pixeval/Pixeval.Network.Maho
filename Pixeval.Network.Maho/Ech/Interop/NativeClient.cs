using System.Runtime.CompilerServices;
using System.Runtime.InteropServices;

namespace Pixeval.Network.Maho.Ech.Interop;

[StructLayout(LayoutKind.Sequential)]
public struct FFIHttpRequestMessage : IDisposable
{
    public ulong RequestId;
    public nint Url;
    public nint Method;
    public nint Headers;
    public nuint HeadersLength;
    public nint Body;
    public nuint BodyLength;

    public void Dispose()
    {
        Marshal.FreeHGlobal(Url);
        Marshal.FreeHGlobal(Method);
        Marshal.FreeHGlobal(Body);
        
        FreeHeaders();
    }
    
    private unsafe void FreeHeaders()
    {
        if (Headers == nint.Zero || HeadersLength <= 0)
        {
            return;
        }
        ref var currentPointer = ref Unsafe.AsRef<nint>(Headers.ToPointer());
        for (var i = 0; i < (int) HeadersLength; i++)
        {
            Marshal.FreeHGlobal(currentPointer);
            currentPointer = ref Unsafe.Add(ref currentPointer, 1);
        }
        Marshal.FreeHGlobal(Headers);
    }
}

[StructLayout(LayoutKind.Sequential)]
public struct FFIHttpResponseMessage
{
    public byte PrematureDeath;
    public nint PrematureDeathReason;
    public ushort StatusCode;
    public nint Headers;
    public nuint HeadersLength;
    public nint Body;
    public nuint BodyLength;
}

public static partial class NativeClient
{
    public delegate void ClientCreationCallback(bool success, nint clientHandle);

    public delegate void HttpCompletionCallback(long requestToken, FFIHttpResponseMessage response, nint userData);
    
    [LibraryImport("pixeval_ech")]
    public static partial void begin_create_client(
        [MarshalAs(UnmanagedType.LPUTF8Str)] string dnsServer,
        ManagedDnsResolutionCallback managedDnsResolutionCallback,
        ManagedLoggingCallback managedLoggingCallback,
        ClientCreationCallback callback);

    [LibraryImport("pixeval_ech")]
    public static partial void free_client(nint clientHandle);

    [LibraryImport("pixeval_ech")]
    public static partial void free_response(nint clientHandle, FFIHttpResponseMessage response);
    
    [LibraryImport("pixeval_ech")]
    public static partial void send_request(
        nint clientHandle,
        FFIHttpRequestMessage requestMessage,
        HttpCompletionCallback completionCallback,
        nint userData);
}
