using System.Runtime.InteropServices;

namespace Pixeval.Network.Maho.Ech.Interop;

public static partial class Marshaling
{
    [LibraryImport("pixecal_ech")]
    public static partial void free_c_string(nint clientHandle, nint ptr);
}