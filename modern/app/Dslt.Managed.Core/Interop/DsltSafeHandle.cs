using Microsoft.Win32.SafeHandles;

namespace Dslt.Managed.Core.Interop;

internal sealed class DsltSafeHandle : SafeHandleZeroOrMinusOneIsInvalid
{
    private DsltSafeHandle() : base(ownsHandle: true)
    {
    }

    internal DsltSafeHandle(nint value) : this() => SetHandle(value);

    protected override bool ReleaseHandle()
    {
        NativeMethods.dslt_destroy(handle);
        return true;
    }
}

