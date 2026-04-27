namespace Pixeval.Network.Maho.Ech;

public enum LoggerLevel : int
{
    Error = 0,
    Warn = 1,
    Info = 2,
    Debug = 3,
    Trace = 4
}

public interface INativeInteropLogger
{
    void Log(LoggerLevel level, string message);
}