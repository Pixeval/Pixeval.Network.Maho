namespace Pixeval.Network.Maho.Ech;

public interface INativeInteropDnsResolver : IDnsResolver
{
    string BaseResolutionUrl { get; }
}
