using System.Net;
using System.Net.Http.Headers;
using System.Runtime.CompilerServices;
using System.Runtime.InteropServices;
using System.Text;
using System.Text.RegularExpressions;

namespace Pixeval.Network.Maho.Ech;

public partial class NativeClient
{
    private int _requestIdCounter;

    public bool Initialized { get; private set; }

#if WINDOWS_XP_OR_LATER

    [LibraryImport("pixeval_ech.dll")]
    private static unsafe partial void init_client(
        NameResolution* nameResolutions,
        nuint nameResolutionsLength,
        nint dnsResolutionUrl,
        ClientInitializationCallback callback);
    
    [LibraryImport("pixeval_ech.dll")]
    private static partial void send_request(
        FFIHttpRequestMessage requestMessage,
        HttpCompletionCallback callback,
        nint userData);
    
    [LibraryImport("pixeval_ech.dll")]
    private static partial void free_response(FFIHttpResponseMessage response);

    [LibraryImport("pixeval_ech.dll")]
    private static partial LoggerConfigurationResult configure_logger_path([MarshalAs(UnmanagedType.LPUTF8Str)] string path);

    [LibraryImport("pixeval_ech.dll")]
    private static partial LoggerConfigurationResult configure_logger_level(LoggerLevel level);
#elif LINUX
    [LibraryImport("libpixeval_ech.so")]
    private static unsafe partial void init_client(
        NameResolution* nameResolutions,
        nuint nameResolutionsLength,
        nint dnsResolutionUrl,
        ClientInitializationCallback callback);
    
    [LibraryImport("libpixeval_ech.so")]
    private static partial void send_request(
        FFIHttpRequestMessage requestMessage,
        HttpCompletionCallback callback,
        nint userData);
    
    [LibraryImport("libpixeval_ech.so")]
    private static partial void free_response(FFIHttpResponseMessage response);

    [LibraryImport("libpixeval_ech.so")]
    private static partial LoggerConfigurationResult configure_logger_path([MarshalAs(UnmanagedType.LPUTF8Str)] string path);

    [LibraryImport("libpixeval_ech.so")]
    private static partial LoggerConfigurationResult configure_logger_level(LoggerLevel level);
#elif OSX    
    [LibraryImport("libpixeval_ech.so")]
    private static unsafe partial void init_client(
        NameResolution* nameResolutions,
        nuint nameResolutionsLength,
        nint dnsResolutionUrl,
        ClientInitializationCallback callback);
    
    [LibraryImport("libpixeval_ech.so")]
    private static partial void send_request(
        FFIHttpRequestMessage requestMessage,
        HttpCompletionCallback callback,
        nint userData);
    
    [LibraryImport("libpixeval_ech.so")]
    private static partial void free_response(FFIHttpResponseMessage response);

    [LibraryImport("libpixeval_ech.so")]
    private static partial LoggerConfigurationResult configure_logger_path([MarshalAs(UnmanagedType.LPUTF8Str)] string path);

    [LibraryImport("libpixeval_ech.so")]
    private static partial LoggerConfigurationResult configure_logger_level(LoggerLevel level);
#endif
    
    public static void SetLoggerLevel(LoggerLevel level)
    {
        var result = configure_logger_level(level);
        if (result.Success != 1)
        {
            throw new InvalidOperationException($"Failed to set the native client logger level: {result.ErrorReason}");
        }
    }
    
    [MethodImpl(MethodImplOptions.Synchronized)]
    public Task InitClientAsync(Dictionary<Regex, IPAddress[]> resolutionMap, string dnsResolutionUrl, string logPath = "")
    {
        if (Initialized)
        {
            return Task.CompletedTask;
        }

        Initialized = true;
        if (logPath != string.Empty)
        {
            var result = configure_logger_path(logPath);
            if (result.Success != 1)
            {
                throw new InvalidOperationException($"Failed to set the native client logger path: {result.ErrorReason}");
            }
        }
        var taskCompletionSource = new TaskCompletionSource();
        unsafe
        {
            var marshalledNameResolution = MarshalNameResolutionMap(resolutionMap);
            var marshalledDnsResolutionUrl = AllocateUtf8CString(dnsResolutionUrl);
            init_client(marshalledNameResolution, (nuint) resolutionMap.Count, marshalledDnsResolutionUrl, (success, errorMessage) =>
            {
                if (success)
                {
                    taskCompletionSource.SetResult();
                }
                else
                {
                    taskCompletionSource.SetException(new Exception(errorMessage));
                }

                FreeNameResolutionPointer(marshalledNameResolution, (nuint) resolutionMap.Count);
                Marshal.FreeHGlobal(marshalledDnsResolutionUrl);
            });
        }

        return taskCompletionSource.Task;
    }
    
    private static unsafe NameResolution* MarshalNameResolutionMap(Dictionary<Regex, IPAddress[]> resolutionMap)
    {
        var pFat = (NameResolution*) NativeMemory.AllocZeroed((nuint) (resolutionMap.Count * sizeof(NameResolution)));
        var list = resolutionMap.ToList();
        for (var i = 0; i < list.Count; i++)
        {
            var (key, ipAddresses) = list[i];
            var regexPtr = AllocateUtf8CString(key.ToString());

            var pAddresses = (nint*) NativeMemory.AllocZeroed((nuint) (ipAddresses.Length * sizeof(nint)));
            for (var j = 0; j < ipAddresses.Length; j++)
            {
                *(pAddresses + j) = AllocateUtf8CString(ipAddresses[j].ToString());
            }

            (pFat + i)->Regex = regexPtr;
            (pFat + i)->IpAddresses = (nint) pAddresses;
            (pFat + i)->IpLength = (nuint) ipAddresses.Length;
        }

        return pFat;
    }

    private static unsafe void FreeNameResolutionPointer(NameResolution* nameResolution, nuint len)
    {
        if (nameResolution == null || len == 0)
        {
            return;
        }
        
        for (nuint i = 0; i < len; i++)
        {
            var item = nameResolution + i;

            if (item->Regex != 0)
            {
                NativeMemory.Free((void*)item->Regex);
            }

            if (item->IpAddresses != 0)
            {
                var pIps = (nint*)item->IpAddresses;

                for (nuint j = 0; j < item->IpLength; j++)
                {
                    if (pIps[j] != 0)
                    {
                        NativeMemory.Free((void*)pIps[j]);
                    }
                }

                NativeMemory.Free(pIps);
            }
        }
        NativeMemory.Free(nameResolution);
    }

    public async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request)
    {
        if (!Initialized)
        {
            throw new InvalidOperationException("Native client is not initialized");
        }
        using var marshalledRequestMessage = await MarshalRequestMessageAsync(request);
        var taskCompletionSource = new TaskCompletionSource<FFIHttpResponseMessage>();
        send_request(marshalledRequestMessage, (_, response, _) => taskCompletionSource.SetResult(response), nint.Zero);
        var ffiResponse = await taskCompletionSource.Task;
        var result = ffiResponse.PrematureDeath != 0
            ? throw new HttpRequestException($"Request failed prematurely: {Marshal.PtrToStringUTF8(ffiResponse.PrematureDeathReason)}") 
            : UnmarshalHttpResponseMessage(ffiResponse);
        free_response(ffiResponse);
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
}
