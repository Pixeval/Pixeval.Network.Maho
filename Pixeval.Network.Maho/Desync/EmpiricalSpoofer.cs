namespace Pixeval.Network.Maho.Desync;

public class EmpiricalTtlSpoofer : ITtlSpoofStrategy
{
    public static readonly EmpiricalTtlSpoofer Shared = new();
    
    public uint Spoof(uint realTtl)
    {
        return realTtl switch
        {
            > 0 and <= 3 => realTtl - 1,
            > 3 and <= 5 => 3,
            > 5 and <= 8 => realTtl - 1,
            > 8 and <= 13 => realTtl - 2,
            > 13 and <= 20 => realTtl - 3,
            _ => 18
        };
    }
}