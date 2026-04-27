using System.Runtime.InteropServices;
using Microsoft.Extensions.Logging;

namespace Pixeval.Network.Maho.Ech.Interop;

public delegate void ManagedLoggingCallback(
    LogLevel level, 
    [MarshalAs(UnmanagedType.LPUTF8Str)] string message);
