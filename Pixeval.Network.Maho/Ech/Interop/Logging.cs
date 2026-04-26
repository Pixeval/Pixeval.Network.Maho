using System.Runtime.InteropServices;

namespace Pixeval.Network.Maho.Ech.Interop;

public enum LoggerLevel : int
{
    Error = 0,
    Warn = 1,
    Info = 2,
    Debug = 3,
    Trace = 4

}

[StructLayout(LayoutKind.Sequential)]
public struct LoggerConfigurationResult
{
    public byte Success;
    public nint ErrorReason;
}

public static partial class Logging
{
    [LibraryImport("pixeval_ech")]
    public static partial LoggerConfigurationResult configure_logger_path([MarshalAs(UnmanagedType.LPUTF8Str)] string path);

    [LibraryImport("pixeval_ech")]
    public static partial LoggerConfigurationResult configure_logger_level(LoggerLevel level);
}