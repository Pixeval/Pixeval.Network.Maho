using System.Runtime.InteropServices;

namespace Pixeval.Network.Maho.Ech.Interop;

[StructLayout(LayoutKind.Sequential)]
public struct InteropOperationResult
{
    public byte Success;

    public nint ErrorReason;
}