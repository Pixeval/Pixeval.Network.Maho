using System.ComponentModel;
using System.Diagnostics.CodeAnalysis;
using System.Net.Sockets;
using System.Runtime.CompilerServices;
using System.Runtime.InteropServices;
using Microsoft.Win32.SafeHandles;

#pragma warning disable CS9123 // '&' 运算符不应用于异步方法中的参数或局部变量。


#if WINDOWS_XP_OR_LATER
using Windows.Win32;
using Windows.Win32.Foundation;
using Windows.Win32.Security;
using Windows.Win32.Storage.FileSystem;
#elif OSX
using System.IO.MemoryMappedFiles;
#endif

#pragma warning disable CS8981 // 该类型名称仅包含小写 ascii 字符。此类名称可能会成为该语言的保留值。

namespace Pixeval.Network.Maho.Desync;

[SuppressMessage("Interoperability", "CA1416:Valider la compatibilité de la plateforme")]
// ReSharper disable InconsistentNaming
// ReSharper disable IdentifierTypo

// The two branches, OSX and Windows, share a little semantic difference
// through my test, if you wait for an overlapped `TransmitFile` on Windows, the completion event will only be triggered after the `ACK` is received from the host
// which means that, if the transmission failed, the `TransmitFile` will not complete until it has maxed out the retries.

// However, on OSX, the completion event is triggered as soon as the data is sent to the kernel, the kernel handles transmissions and retransmission, the transmission failure 
// is notified through another event.
public static partial class SocketDesynchronizer
{

#if WINDOWS_XP_OR_LATER

    private const uint GENERIC_READ = 0x80000000; 
    private const uint GENERIC_WRITE = 0x40000000;
    private const uint ERROR_IO_PENDING = 0x3E5;

    private static SafeFileHandle CreateTempFile(string tempFile)
    {
        return PInvoke.CreateFile(
            tempFile,
            GENERIC_READ | GENERIC_WRITE,
            FILE_SHARE_MODE.FILE_SHARE_READ | FILE_SHARE_MODE.FILE_SHARE_WRITE,
            default(SECURITY_ATTRIBUTES),
            FILE_CREATION_DISPOSITION.CREATE_ALWAYS,
            0
        );
    }
    
    public static async Task DesyncAsync(
        Socket socket,
        int fakeTtl,
        int realTtl,
        int sleep,
        int transmitTimeoutMs,
        ReadOnlyMemory<byte> fakeContent,
        ReadOnlyMemory<byte> realContent)
    {
        var tempFile = Path.Combine(Path.GetTempPath(), Path.GetRandomFileName());
        var fileHandle = CreateTempFile(tempFile);
        if (fileHandle.IsInvalid)
        {
            var error = Marshal.GetLastWin32Error();
            throw new DesynchronizationException($"Failed to create temporary file with error code: {error}");
        }

        IntPtr evt = 0;
        try
        {
            evt = PInvoke.CreateEvent((SECURITY_ATTRIBUTES?) null, true, false).DangerousGetHandle();

            if (evt <= 0)
            {
                var error = Marshal.GetLastWin32Error();
                throw new DesynchronizationException($"Failed to create the event handler with error code: {error}");
            }


            // set the file pointer to begin for the writing of the fake content
            SetFilePointerToBegin(fileHandle, "fake write");

            // write fake data
            var fakeWriteRes = PInvoke.WriteFile(fileHandle, fakeContent.Span, out _, ref Unsafe.NullRef<NativeOverlapped>());
            if (fakeWriteRes == false)
            {
                var error = Marshal.GetLastWin32Error();
                if (error != ERROR_IO_PENDING)
                {
                    throw new DesynchronizationException($"Failed to write fake content to the file pointer with error code: {error}");
                }
            }

            if (!PInvoke.SetEndOfFile(fileHandle))
            {
                throw new DesynchronizationException($"Failed to set the end of file for fake write with error code: {Marshal.GetLastWin32Error()}");
            }

            socket.SetSocketOption(SocketOptionLevel.IP, SocketOptionName.IpTimeToLive, fakeTtl);
            // reset the file pointer to the head of the file, prepare for the read of the TransmitFile
            SetFilePointerToBegin(fileHandle, "fake transmit");

            var transmitFileTask = new TaskCompletionSource();
            unsafe
            {
                void Callback(uint errorCode, uint numBytes, NativeOverlapped* ov)
                {
                    try
                    {
                        if (errorCode != 0)
                        {
                            transmitFileTask.TrySetException(new Win32Exception((int) errorCode));
                        }
                        else
                        {
                            transmitFileTask.TrySetResult();
                        }
                    }
                    finally
                    {
                        Overlapped.Free(ov);
                    }
                }

                var overlapped = new Overlapped
                {
                    EventHandleIntPtr = evt,
                    OffsetLow = 0,
                    OffsetHigh = 0
                };

                // Pack pins the memory and returns the native pointer
                var pNative = overlapped.Pack(Callback, null);

                ref var nativedOverlapped = ref Unsafe.AsRef<NativeOverlapped>(pNative);
                PInvoke.TransmitFile(
                    socket.SafeHandle,
                    fileHandle,
                    (uint)fakeContent.Length,
                    (uint)fakeContent.Length,
                    ref nativedOverlapped,
                    null,
                    PInvoke.TF_WRITE_BEHIND | PInvoke.TF_USE_KERNEL_APC);
            }

            await Task.Delay(sleep);

            SetFilePointerToBegin(fileHandle, "real write");
            // write fake data
            var realWriteRes = PInvoke.WriteFile(fileHandle, realContent.Span, out _, ref Unsafe.NullRef<NativeOverlapped>());
            if (realWriteRes == false)
            {
                var error = Marshal.GetLastWin32Error();
                if (error != ERROR_IO_PENDING)
                {
                    throw new DesynchronizationException($"Failed to write real content to the file pointer with error code: {error}");
                }
            }

            if (!PInvoke.SetEndOfFile(fileHandle))
            {
                throw new DesynchronizationException($"Failed to set the end of file for real write with error code: {Marshal.GetLastWin32Error()}");
            }
            
            // reset the file pointer to the head of the file, prepare for the read of the TransmitFile
            SetFilePointerToBegin(fileHandle, "real transmit");
            // we need to make sure every file-manipulations happen before the ttl is set to real, this prevents the corrupted data being sent.
            socket.SetSocketOption(SocketOptionLevel.IP, SocketOptionName.IpTimeToLive, realTtl);
            await transmitFileTask.Task.WaitAsync(TimeSpan.FromMilliseconds(transmitTimeoutMs));
        }
        catch (Exception e)
        {
            if (e is DesynchronizationException)
            {
                throw;
            }
            else
            {
                throw new DesynchronizationException($"Failed to desync with error code: {Marshal.GetLastWin32Error()} and exception {e}");
            }
        }
        finally
        {
            PInvoke.CloseHandle(new HANDLE(evt));
            PInvoke.CloseHandle(new HANDLE(fileHandle.DangerousGetHandle()));
            File.Delete(tempFile);
        }

        return;

        static void SetFilePointerToBegin(SafeHandle fh, string stage)
        {
            // reset the file pointer to the head of the file, prepare for the read of the TransmitFile
            if (PInvoke.SetFilePointer(
                    fh, 
                    0, 
                    ref Unsafe.NullRef<int>(), 
                    SET_FILE_POINTER_MOVE_METHOD.FILE_BEGIN) == PInvoke.INVALID_SET_FILE_POINTER)
            {
                var error = Marshal.GetLastWin32Error();
                throw new DesynchronizationException($"Failed to set the file pointer for the {stage} with error code: {error}");
            }
        }
    }

#elif OSX
    private class KQueueCompleter(int kq, Func<int, Task> onEvent)
    {
        private static readonly object _lock = new();
        
        private readonly Dictionary<ulong, TaskCompletionSource> _completionPool = new();

        private bool _closed;
        
        [MethodImpl(MethodImplOptions.Synchronized)]
        private void Stop()
        {
            _closed = true;
            close(kq);
        }

        public unsafe void PushEvent(KEvent64* changelist,
                                     int nchanges,
                                     KEvent64* eventlist,
                                     int nevents,
                                     KEvent64Flag flags,
                                     timespec* timeout,
                                     out int affectedEvents)
        {
            affectedEvents = kevent64(
                kq,
                changelist,
                nchanges,
                eventlist,
                nevents,
                flags,
                timeout);
            lock (_lock)
            {
                if (nchanges > 0 && !_completionPool.ContainsKey(changelist->ident))
                {
                    _completionPool[changelist->ident] = new TaskCompletionSource();
                }

            }
        }

        public TaskCompletionSource GetCompletionSource(ulong socketFd, bool isCompletion = false)
        {
            lock (_lock)
            {
                if (isCompletion)
                {
                    _completionPool.Remove(socketFd, out var res);
                    return res!;
                }

                return _completionPool[socketFd];
            }
        }
        
        public void StartListen()
        {
            Task.Factory.StartNew(async () =>
            {
                while (!_closed)
                {
                    await onEvent(kq);
                }
            }, TaskCreationOptions.LongRunning);
        }
    }
    
    private static readonly Lazy<KQueueCompleter> _completer = new(() =>
    {
        var completer = new KQueueCompleter(kqueue(),  _ => OnKQueueEvent());
        completer.StartListen();
        return completer;
    });
    
    // see relevant header files under /Library/Developer/CommandLineTools/SDKs/MacOSX.sdk/usr/include/sys/
    private const int F_GETFL = 3;
    private const int F_SETFL = 4;
    private const int O_NONBLOCK = 0x00000004;
    private const int EAGAIN = 35;
    private const int EINTR = 4; // system call interrupted
    
    [LibraryImport("libSystem.dylib", SetLastError = true)]
    private static partial int ftruncate(int fd, long length);
    
    [LibraryImport("libSystem.dylib", SetLastError = true)]
    private static partial int fcntl(int fd, int cmd);

    [LibraryImport("libSystem.dylib", SetLastError = true)]
    private static partial int fcntl(int fd, int cmd, int arg);
    
    [LibraryImport("libSystem.dylib", SetLastError = true)]
    private static partial int sendfile(int fd, int s, long offset, ref long len, nint hdtr, int flags);

    [LibraryImport("libSystem.dylib", SetLastError = true)]
    private static partial int kqueue();
    
    [LibraryImport("libSystem.B.dylib", SetLastError = true)]
    internal static partial int close(int fd);
    
    [LibraryImport("libSystem.dylib", SetLastError = true)]
    private static unsafe partial int kevent64(
        int kq,
        KEvent64* changelist,
        int nchanges,
        KEvent64* eventlist,
        int nevents,
        KEvent64Flag flags,
        timespec* timeout);

    [StructLayout(LayoutKind.Sequential)]
    private sealed record SendFileContext(
        int FileDescriptor,
        int SocketDescriptor,
        ReadOnlyMemory<byte> FakeContent,
        ReadOnlyMemory<byte> RealContent,
        int SleepMs,
        int RealTtl,
        long Offset,
        long Remaining,
        nint MmapStream)
    {
        public long Offset { get; set; } = Offset;
        
        public long Remaining { get; set; } = Remaining;
        
        public bool FakeSendCompleted { get; set; }
        
        public bool TimerScheduled { get; set; }
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct timespec
    {
        public nint tv_sec;
        public nint tv_nsec;
    }

    [Flags]
    private enum KEvent64Flag : uint
    {
        KEVENT_FLAG_NONE = 0x000000
    }
    
    [Flags]
    private enum KEventFlag : ushort
    {
        EV_ADD = 0x0001,
        EV_DELETE = 0x0002,
        EV_ENABLE = 0x0004,
        EV_EOF = 0x8000
    }

    [Flags]
    private enum KEventFilter : short
    {
        EVFILT_WRITE = -2,
        EVFILT_TIMER = -7
    }

    [Flags]
    private enum KEventTimerUnit : uint
    {
        NOTE_USECONDS = 0x00000002
    }
    
    [StructLayout(LayoutKind.Sequential)]
    private struct KEvent64
    {
        public ulong ident;
        public KEventFilter filter;
        public KEventFlag flags;
        public uint fflags;
        public long data;
        public nint udata;
        public nint ext0;
        public nint ext1;
    }

    private static SafeFileHandle CreateTempFile(string tempFile)
    {
       return File.OpenHandle(
           tempFile,
            FileMode.CreateNew,
            FileAccess.ReadWrite,
            FileShare.None,
            FileOptions.DeleteOnClose);
    }

    public static async Task DesyncAsync(
        Socket socket,
        int fakeTtl,
        int realTtl,
        int sleep,
        int transmitTimeoutMs,
        ReadOnlyMemory<byte> fakeContent,
        ReadOnlyMemory<byte> realContent)
    {
        var tempFile = Path.Combine(Path.GetTempPath(), Path.GetRandomFileName());
        Console.WriteLine(tempFile);
        var socketFdRefAdded = false;
        var tempFileFdRefAdded = false;
        var socketSafeHandle = socket.SafeHandle;
        var tempFileSafeHandle = CreateTempFile(tempFile);
        GCHandle accessorStreamHandle = default;
        GCHandle sendFileContextHandle = default;
        try
        {
            socketSafeHandle.DangerousAddRef(ref socketFdRefAdded);
            if (!socketFdRefAdded)
            {
                throw new DesynchronizationException("Failed to increase reference to the socket file descriptor");
            }
            var socketRawFd = socketSafeHandle.DangerousGetHandle();
            
            tempFileSafeHandle.DangerousAddRef(ref tempFileFdRefAdded);
            if (!tempFileFdRefAdded)
            {
                throw new DesynchronizationException("Failed to increase reference to the temporary file file descriptor");
            }
            var tempFileRawFd = tempFileSafeHandle.DangerousGetHandle();
            
            RandomAccess.SetLength(tempFileSafeHandle, fakeContent.Length);
            using var memoryMappedFile = MemoryMappedFile.CreateFromFile(tempFileSafeHandle, null, fakeContent.Length, MemoryMappedFileAccess.ReadWrite,
                HandleInheritability.None, false);
            await using var accessor = memoryMappedFile.CreateViewStream();
            accessor.Seek(0, SeekOrigin.Begin);
            // write the fake content
            accessor.Write(fakeContent.Span);
            if (SetNonblocking(socketRawFd) is -1)
            {
                throw new DesynchronizationException($"Failed to set the socket to non-blocking mode with error code: {Marshal.GetLastPInvokeError()}");
            }
            
            accessorStreamHandle = GCHandle.Alloc(accessor);
            var sendFileContext = new SendFileContext(
                tempFileRawFd.ToInt32(),
                socketRawFd.ToInt32(),
                fakeContent,
                realContent, 
                sleep,
                realTtl,
                0,
                fakeContent.Length,
                GCHandle.ToIntPtr(accessorStreamHandle));
            sendFileContextHandle = GCHandle.Alloc(sendFileContext);
            
            socket.SetSocketOption(SocketOptionLevel.IP, SocketOptionName.IpTimeToLive, fakeTtl);
            
            await SendFileAsync(GCHandle.ToIntPtr(sendFileContextHandle), transmitTimeoutMs, socketRawFd);
        }
        finally
        {
            File.Delete(tempFile);
            if (accessorStreamHandle.IsAllocated)
            {
                accessorStreamHandle.Free();
            }

            if (sendFileContextHandle.IsAllocated)
            {
                sendFileContextHandle.Free();
            }
            if (socketFdRefAdded)
            {
                socketSafeHandle.DangerousRelease();
            }

            if (tempFileFdRefAdded)
            {
                tempFileSafeHandle.DangerousRelease();
            }
        }
    }

    private static async Task OnKQueueEvent()
    {
        KEvent64 pollKev;
        int n;
        unsafe
        {
            _completer.Value.PushEvent(null,
                0,
                &pollKev,
                1,
                KEvent64Flag.KEVENT_FLAG_NONE,
                null,
                out n);
        }
        var tcs = _completer.Value.GetCompletionSource(pollKev.ident);
        switch (n)
        {
            case -1 when Marshal.GetLastPInvokeError() == EINTR:
                return;
            case -1:
                tcs.TrySetException(new DesynchronizationException("Failed to poll the sendfile completion with error code: " + Marshal.GetLastPInvokeError()));
                return;
            case 0:
                tcs.TrySetException(new DesynchronizationException("Failed to poll the sendfile completion with unknown error"));
                return;
        }

        if (pollKev.flags.HasFlag(KEventFlag.EV_EOF))
        {
            tcs.TrySetException(new DesynchronizationException("The socket was closed before the sendfile completed"));
        }

        switch (pollKev.filter)
        {
            case KEventFilter.EVFILT_TIMER:
            {
                // fake sent slept, now write the real data
                var ctx = GetCtxForEvent(pollKev);

                var socketDescriptor = ctx.SocketDescriptor;
                var socket = new Socket(new SafeSocketHandle(socketDescriptor, false));
                
                var mmapedStreamHandle = GetMMapStreamForContext(ctx);
                mmapedStreamHandle.Seek(0, SeekOrigin.Begin);
                await mmapedStreamHandle.WriteAsync(ctx.RealContent);

                if (ctx.FakeContent.Length > ctx.RealContent.Length)
                {
                    if (ftruncate(ctx.FileDescriptor, ctx.RealContent.Length) != 0)
                    {
                        throw new DesynchronizationException("Failed to truncate the file to the real content length with error code: " + Marshal.GetLastPInvokeError());
                    }
                }
                
                // we need to make sure every file-manipulations happen before the ttl is set to real, this prevents the corrupted data being sent.
                socket.SetSocketOption(SocketOptionLevel.IP, SocketOptionName.IpTimeToLive, 100);
                
                _completer.Value.GetCompletionSource((ulong) ctx.SocketDescriptor, true).TrySetResult();
                break;
            }
            // we are ready for a new write
            case KEventFilter.EVFILT_WRITE:
            {
                var ctx = GetCtxForEvent(pollKev);
                var sendFileRes = SendFileNonblocking(ctx);
                if (sendFileRes != 0)
                {
                    _completer.Value.GetCompletionSource((ulong) ctx.SocketDescriptor)
                        .TrySetException(new DesynchronizationException("Failed to send file in non-blocking mode with error code: " + sendFileRes));
                }

                if (ctx.FakeSendCompleted) // the completion says we have sent the fake data to the kernel buffer
                {
                    if (!ctx.TimerScheduled)
                    {
                        ctx.TimerScheduled = true;
                        var timerKev = new KEvent64
                        {
                            ident = (ulong) ctx.SocketDescriptor,
                            filter = KEventFilter.EVFILT_TIMER,
                            flags = KEventFlag.EV_ADD | KEventFlag.EV_ENABLE,
                            fflags = (uint) KEventTimerUnit.NOTE_USECONDS, // microsecond, macOS is such a trash system that it does not support NOTE_MSECONDS and honestly no one knows why.
                            data = ctx.SleepMs * 1000,
                            udata = pollKev.udata
                        };
                        unsafe
                        {
                            _completer.Value.PushEvent(
                                &timerKev, 
                                1, 
                                null, 
                                0, 
                                KEvent64Flag.KEVENT_FLAG_NONE, 
                                null, 
                                out _);
                        }
                    }
   
                    var completeKev = new KEvent64
                    {
                        ident = (ulong) ctx.SocketDescriptor,
                        filter = KEventFilter.EVFILT_WRITE,
                        flags = KEventFlag.EV_DELETE,
                        fflags = 0,
                        data = 0,
                        udata = 0
                    };
                    unsafe
                    {
                        _completer.Value.PushEvent(&completeKev,
                            1,
                            null,
                            0,
                            KEvent64Flag.KEVENT_FLAG_NONE,
                            null,
                            out _);
                    }
                }
                break;
            }
            default:
                throw new ArgumentOutOfRangeException();
        }

        return;
        
        static MemoryMappedViewStream GetMMapStreamForContext(SendFileContext ctx)
        {
            return GCHandle.FromIntPtr(ctx.MmapStream).Target as MemoryMappedViewStream ?? throw new DesynchronizationException("Failed to retrieve the memory mapped stream from the context");
        }

        static SendFileContext GetCtxForEvent(KEvent64 pollKev)
        {
            return GCHandle.FromIntPtr(pollKev.udata).Target switch
            {
                SendFileContext c => c,
                _ => throw new DesynchronizationException("Failed to retrieve the sendfile context from the event data")
            };
        }
    }

    private static int SendFileNonblocking(SendFileContext ctx)
    {
        while (ctx.Remaining > 0)
        {
            var lengthSent = ctx.Remaining;
            var rc = sendfile(
                ctx.FileDescriptor,
                ctx.SocketDescriptor,
                ctx.Offset,
                ref lengthSent, 
                0, 
                0);

            if (lengthSent > 0)
            {
                ctx.Offset += lengthSent;
                ctx.Remaining -= lengthSent;
            }

            if (rc == 0)
            {
                if (ctx.Remaining != 0)
                    continue;
                ctx.FakeSendCompleted = true;
                return 0;
            }

            switch (Marshal.GetLastPInvokeError())
            {
                // the buffer is full, the sendfile returns without blocking, we return and wait for the next writable event.
                case EAGAIN:
                    return 0;
                // ignore the interruption and retry, EINTR happens when no data is written, thus no state changed in ctx
                case EINTR:
                    continue;
                default:
                    return Marshal.GetLastPInvokeError();
            }
        }

        ctx.FakeSendCompleted = true;
        return 0;
    }

    private static Task SendFileAsync(
        nint ctxHandle,
        int transmitTimeoutMs,
        nint socketFd)
    {
        var writeKev = new KEvent64
        {
            ident = (ulong) socketFd,
            filter = KEventFilter.EVFILT_WRITE,
            flags = KEventFlag.EV_ADD | KEventFlag.EV_ENABLE,
            fflags = 0,
            data = 0,
            udata = ctxHandle
        };
        unsafe
        {
            // register and enable the event
            _completer.Value.PushEvent(&writeKev,
                1,
                null,
                0,
                KEvent64Flag.KEVENT_FLAG_NONE,
                null,
                out _);
        }
        return _completer.Value.GetCompletionSource(writeKev.ident).Task.WaitAsync(TimeSpan.FromMilliseconds(transmitTimeoutMs));
    }

    private static int SetNonblocking(nint fd)
    {
        var flags = fcntl(fd.ToInt32(), F_GETFL);
        if (flags == -1)
        {
            var errno = Marshal.GetLastPInvokeError();
            throw new DesynchronizationException($"Failed to get the file descriptor flags with error code: {errno}");
        }

        return fcntl(fd.ToInt32(), F_SETFL, flags | O_NONBLOCK);
    }
#elif LINUX

#endif
}
