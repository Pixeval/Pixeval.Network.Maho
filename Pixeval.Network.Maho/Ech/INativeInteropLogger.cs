using Microsoft.Extensions.Logging;

namespace Pixeval.Network.Maho.Ech;

public interface INativeInteropLogger
{
    void Log(LogLevel level, string message);
}
