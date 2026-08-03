using System.Runtime.InteropServices;

namespace Dslt.Managed.Core.Interop;

internal static class NativeMethods
{
    private const string Library = "dslt_core";

    [UnmanagedFunctionPointer(CallingConvention.Cdecl)]
    internal delegate int ProgressCallback(float progress, nint userData);

    [DllImport(Library, CallingConvention = CallingConvention.Cdecl)]
    internal static extern uint dslt_get_abi_version();

    [DllImport(Library, CallingConvention = CallingConvention.Cdecl)]
    internal static extern NativeStatus dslt_create(out nint handle);

    [DllImport(Library, CallingConvention = CallingConvention.Cdecl)]
    internal static extern void dslt_destroy(nint handle);

    [DllImport(Library, CallingConvention = CallingConvention.Cdecl)]
    internal static extern NativeStatus dslt_get_backend_info(
        DsltSafeHandle handle,
        out NativeBackendInfo info);

    [DllImport(Library, CallingConvention = CallingConvention.Cdecl)]
    internal static extern NativeStatus dslt_set_volume_f32(
        DsltSafeHandle handle,
        in NativeVolumeDescriptor descriptor,
        [In] float[] data,
        ulong elementCount);

    [DllImport(Library, CallingConvention = CallingConvention.Cdecl)]
    internal static extern NativeStatus dslt_run_operation(
        DsltSafeHandle handle,
        in NativeOperationRequest request,
        ProgressCallback? progress,
        nint userData,
        out NativeOperationResult result);

    [DllImport(Library, CallingConvention = CallingConvention.Cdecl)]
    internal static extern NativeStatus dslt_copy_output_f32(
        DsltSafeHandle handle,
        [Out] float[] destination,
        ulong elementCount);

    [DllImport(Library, CallingConvention = CallingConvention.Cdecl)]
    internal static extern NativeStatus dslt_copy_labels_i32(
        DsltSafeHandle handle,
        [Out] int[] destination,
        ulong elementCount);

    [DllImport(Library, CallingConvention = CallingConvention.Cdecl)]
    internal static extern NativeStatus dslt_get_last_error(
        DsltSafeHandle handle,
        nint destination,
        nuint destinationSize,
        out nuint requiredSize);
}

