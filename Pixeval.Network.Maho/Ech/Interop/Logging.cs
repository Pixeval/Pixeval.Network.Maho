using System.Runtime.InteropServices;

namespace Pixeval.Network.Maho.Ech.Interop;

public delegate void ManagedLoggingCallback(
    LoggerLevel level, 
    [MarshalAs(UnmanagedType.LPUTF8Str)] string message);