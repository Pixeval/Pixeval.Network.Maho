namespace Pixeval.Network.Maho.Desync;

public interface ITtlSpoofStrategy
{
    uint Spoof(uint realTtl);
}