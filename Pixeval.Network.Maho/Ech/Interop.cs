using System.Runtime.CompilerServices;
using System.Runtime.InteropServices;

namespace Pixeval.Network.Maho.Ech;


public enum LoggerLevel : int
{
    Error = 0,
    Warn = 1,
    Info = 2,
    Debug = 3,
    Trace = 4
}

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
public struct NameResolution
{
    public nint Regex;
    public nint IpAddresses;
    public nuint IpLength;
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

[StructLayout(LayoutKind.Sequential)]
public struct LoggerConfigurationResult
{
    public byte Success;
    public nint ErrorReason;
}

[UnmanagedFunctionPointer(CallingConvention.Cdecl)]
public delegate void HttpCompletionCallback(
    ulong id, 
    FFIHttpResponseMessage response,
    nint userData);
    
[UnmanagedFunctionPointer(CallingConvention.Cdecl)]
public delegate void ClientInitializationCallback(
    bool success,
    [MarshalAs(UnmanagedType.LPUTF8Str)] string errorMessage);