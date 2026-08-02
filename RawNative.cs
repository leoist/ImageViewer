using System.Runtime.InteropServices;

namespace ImageViewer;

internal static class RawNative
{
    private const string LibRaw = "libraw.dll";

    [DllImport(LibRaw)] private static extern IntPtr libraw_init(uint flags);
    [DllImport(LibRaw)] private static extern void libraw_close(IntPtr lr);
    [DllImport(LibRaw)] private static extern int libraw_open_file(IntPtr lr, [MarshalAs(UnmanagedType.LPStr)] string filename);
    [DllImport(LibRaw)] private static extern int libraw_unpack_thumb(IntPtr lr);
    [DllImport(LibRaw)] private static extern IntPtr libraw_dcraw_make_mem_thumb(IntPtr lr, out int errc);
    [DllImport(LibRaw)] private static extern void libraw_dcraw_clear_mem(IntPtr img);

    private enum ImageFormat { JPEG = 1, BITMAP = 2 }

    public static byte[]? DecodeThumbnail(string path)
    {
        IntPtr lr = IntPtr.Zero, thumb = IntPtr.Zero;
        try
        {
            lr = libraw_init(0);
            if (lr == IntPtr.Zero) return null;
            if (libraw_open_file(lr, path) != 0) return null;
            if (libraw_unpack_thumb(lr) != 0) return null;

            thumb = libraw_dcraw_make_mem_thumb(lr, out int err);
            if (thumb == IntPtr.Zero || err != 0) return null;

            var type = (ImageFormat)Marshal.ReadInt32(thumb, 0);
            if (type != ImageFormat.JPEG) return null;

            int dataSize = Marshal.ReadInt32(thumb, 12);
            IntPtr dataPtr = thumb + 16;
            var jpeg = new byte[dataSize];
            Marshal.Copy(dataPtr, jpeg, 0, dataSize);
            return jpeg;
        }
        catch { return null; }
        finally
        {
            if (thumb != IntPtr.Zero) libraw_dcraw_clear_mem(thumb);
            if (lr != IntPtr.Zero) libraw_close(lr);
        }
    }
}
