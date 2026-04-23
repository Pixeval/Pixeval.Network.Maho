using System.Net;
using System.Net.Sockets;
using System.Numerics;

namespace Pixeval.Network.Maho.Desync;

public class DefaultTtlSniffer : ITtlSniffer
{
    public int MaxTtl => 32;

    public int MinTtl => 1;
    
    private static async Task<T> AsyncBinarySearch<T>(T high, T low, T notFound, Func<T, Task<bool>> predicate)
        where T : IComparisonOperators<T, T, bool>,
        ISubtractionOperators<T, int, T>, 
        ISubtractionOperators<T, T, T>, 
        IDivisionOperators<T, int, T>,
        IAdditionOperators<T, T, T>,
        IAdditionOperators<T, int, T>
    {
        var answer = notFound;
        while (low <= high)
        {
            var mid = (high + low) / 2;
            if (await predicate(mid))
            {
                answer = mid;
                high = mid - 1;
            }
            else
            {
                low = mid + 1;
            }
        }

        return answer;
    }
    
    private static async Task<bool> ValidateTtlAsync(IPAddress address, int ttl, int timeout, int port)
    {
        using var socket = new Socket(AddressFamily.InterNetwork, SocketType.Stream, ProtocolType.Tcp);
        var cancellationTokenSource = new CancellationTokenSource(timeout);
        try
        {
            socket.SetSocketOption(SocketOptionLevel.IP, SocketOptionName.IpTimeToLive, ttl);
            socket.SendTimeout = timeout;
            socket.ReceiveTimeout = timeout;
            
            var remoteEp = new IPEndPoint(address, port);
            await socket.ConnectAsync(remoteEp, cancellationTokenSource.Token);
            await socket.SendAsync(new byte[] { 0x00 }, SocketFlags.None, cancellationTokenSource.Token);
            Console.WriteLine($"Sniffed ttl: {ttl}");
            return true;
        }
        catch 
        {
            return false;
        }
    }
    
    public Task<int> ResolveTtlAsync(IPAddress address, int timeout, int port)
    {
        return AsyncBinarySearch(MaxTtl, MinTtl, -1, ttl => ValidateTtlAsync(address, ttl, timeout, port));
    }
}