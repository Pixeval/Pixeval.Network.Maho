using System.Net;
using System.Net.Http.Headers;
using System.Runtime.CompilerServices;
using System.Runtime.InteropServices;
using System.Text;
using Pixeval.Network.Maho.Ech.Interop;

namespace Pixeval.Network.Maho.Ech;

public class NativeClient(INativeInteropDnsResolver dnsResolver, INativeInteropLogger logger) : IDisposable
{
    private int _requestIdCounter;

    private nint _nativeClientHandle;

    public bool Initialized { get; private set; }
    
    [MethodImpl(MethodImplOptions.Synchronized)]
    public Task InitClientAsync()
    {
        if (Initialized)
        {
            return Task.CompletedTask;
        }

        Initialized = true;
        var taskCompletionSource = new TaskCompletionSource();
        Interop.NativeClient.begin_create_client(dnsResolver.BaseResolutionUrl, ManagedDnsResolutionCallback, ManagedLoggingCallback, (success, handle) =>
        {
            if (!success)
            {
                taskCompletionSource.SetException(new InvalidOperationException("Failed to create native client"));
            }
            else
            {
                _nativeClientHandle = handle;
                taskCompletionSource.SetResult();
            }
        });

        return taskCompletionSource.Task;
    }

    private void ManagedLoggingCallback(LoggerLevel level, string message)
    {
        logger.Log(level, message);
    }

    // ReSharper disable once AsyncVoidMethod
    private async void ManagedDnsResolutionCallback(long requestToken, string hostname)
    {
        InteropOperationResult interopResult;
        try
        {
            var result = await dnsResolver.LookupAsync(hostname);
            interopResult = Resolution.complete_resolution(_nativeClientHandle, requestToken, MarshalIpAddresses(result), (nuint) result.Length);
        }
        catch (Exception e)
        {
            interopResult = Resolution.complete_resolution_failure(_nativeClientHandle, requestToken, e.ToString());
        }

        if (interopResult.Success != 1)
        {
            // TODO log the error
            Console.WriteLine(Marshal.PtrToStringUTF8(interopResult.ErrorReason));
        }
    }
    
    

    private static unsafe nint MarshalIpAddresses(IReadOnlyList<IPAddress> addresses)
    {
        var pAddresses = (nint*) NativeMemory.AllocZeroed((nuint) (addresses.Count * sizeof(nint)));
        for (var j = 0; j < addresses.Count; j++)
        {
            *(pAddresses + j) = AllocateUtf8CString(addresses[j].ToString());
        }

        return new nint(pAddresses);
    }

    public async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request)
    {
        if (!Initialized)
        {
            throw new InvalidOperationException("Native client is not initialized");
        }
        using var marshalledRequestMessage = await MarshalRequestMessageAsync(request);
        var taskCompletionSource = new TaskCompletionSource<FFIHttpResponseMessage>();
        Interop.NativeClient.send_request(_nativeClientHandle, marshalledRequestMessage, (_, response, _) => taskCompletionSource.SetResult(response), nint.Zero);
        var ffiResponse = await taskCompletionSource.Task;
        var result = ffiResponse.PrematureDeath != 0
            ? throw new HttpRequestException($"Request failed prematurely: {Marshal.PtrToStringUTF8(ffiResponse.PrematureDeathReason)}") 
            : UnmarshalHttpResponseMessage(ffiResponse);
        Interop.NativeClient.free_response(_nativeClientHandle, ffiResponse);
        return result;
    }

    private static unsafe HttpResponseMessage UnmarshalHttpResponseMessage(FFIHttpResponseMessage ffiResponse)
    {
        var message = new HttpResponseMessage();
        message.StatusCode = (HttpStatusCode) ffiResponse.StatusCode;
        ref var currentHeader = ref Unsafe.AsRef<nint>(ffiResponse.Headers.ToPointer());
        for (var i = 0; i < (int) ffiResponse.HeadersLength; i++)
        {
            var headerLine = Marshal.PtrToStringUTF8(currentHeader)!;
            var separator = headerLine.IndexOf(":", StringComparison.OrdinalIgnoreCase);
            
            if (separator <= 0)
            {
                throw new HttpRequestException($"Invalid header format received from native code: {headerLine}");
            }
            
            var headerKey = headerLine[..separator].Trim();
            var headerValue = headerLine[(separator + 1)..].Trim();
            message.Headers.TryAddWithoutValidation(headerKey, headerValue);
        }
        var contentBytes = new byte[ffiResponse.BodyLength];
        Marshal.Copy(ffiResponse.Body, contentBytes, 0, (int) ffiResponse.BodyLength);
        message.Content = new ByteArrayContent(contentBytes);
        return message;
    }

    private async Task<FFIHttpRequestMessage> MarshalRequestMessageAsync(HttpRequestMessage request)
    {
        var id = Interlocked.Increment(ref _requestIdCounter);

        var urlPtr = AllocateUtf8CString(request.RequestUri!.ToString());
        var methodPtr = AllocateUtf8CString(request.Method.Method);
        var bodyPtr = nint.Zero;
        var bodyLength = nuint.Zero;

        if (request.Content != null && await request.Content.ReadAsByteArrayAsync() is { Length: > 0 } contentBytes)
        {
            bodyLength = (nuint) contentBytes.Length;
            bodyPtr = Marshal.AllocHGlobal(contentBytes.Length);
            Marshal.Copy(contentBytes, 0, bodyPtr, contentBytes.Length);
        }

        var headerLength = request.Headers.Sum(h => h.Value.Count()) + 
                           (request.Content?.Headers.Sum(h => h.Value.Count()) ?? 0);

        var headerPtr = MarshalHeaders(request);
        return new FFIHttpRequestMessage
        {
            RequestId = (ulong) id,
            Url = urlPtr,
            Method = methodPtr,
            Headers = headerPtr,
            HeadersLength = (nuint) headerLength,
            Body = bodyPtr,
            BodyLength = bodyLength
        };
    }

    private static nint AllocateUtf8CString(string value)
    {
        var bytes = Encoding.UTF8.GetBytes(value);
        var ptr = Marshal.AllocHGlobal(bytes.Length + 1);

        Marshal.Copy(bytes, 0, ptr, bytes.Length);
        Marshal.WriteByte(ptr, bytes.Length, 0);

        return ptr;
    }

    
    private static unsafe nint MarshalHeaders(HttpRequestMessage request)
    {
        var headers = new HttpHeaders?[] { request.Headers, request.Content?.Headers }
            .Where(h => h is not null)
            .SelectMany(h => h!).ToList();
        var pHeaders = Marshal.AllocHGlobal(headers.Sum(h => h.Value.Count()) * Marshal.SizeOf<nint>());
        ref var currentPointer = ref Unsafe.AsRef<nint>(pHeaders.ToPointer());
        foreach (var (key, values) in headers)
        {
            foreach (var value in values)
            {
                var joinedString = $"{key}: {value}";
                var pJoinedString = AllocateUtf8CString(joinedString);
                currentPointer = pJoinedString;
                currentPointer = ref Unsafe.Add(ref currentPointer, 1);
            }
        }

        return pHeaders;
    }

    public void Dispose()
    {
        if (_nativeClientHandle != nint.Zero)
        {
            Interop.NativeClient.free_client(_nativeClientHandle);
        }
    }
}
