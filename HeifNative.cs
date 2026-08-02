using System.Runtime.InteropServices;

namespace ImageViewer;

internal static class HeifNative
{
    private const string LibHeif = "heif.dll";

    private enum heif_colorspace { RGB = 1 }
    private enum heif_chroma { Interleaved_RGBA = 11 }
    private enum heif_channel { Interleaved = 10 }

    [StructLayout(LayoutKind.Sequential)]
    private struct heif_error
    {
        public int code;
        public int subcode;
        public IntPtr message;
    }

    [DllImport(LibHeif)] private static extern IntPtr heif_context_alloc();
    [DllImport(LibHeif)] private static extern void heif_context_free(IntPtr ctx);
    [DllImport(LibHeif)] private static extern heif_error heif_context_read_from_file(IntPtr ctx, [MarshalAs(UnmanagedType.LPStr)] string filename, IntPtr reserved);
    [DllImport(LibHeif)] private static extern heif_error heif_context_get_primary_image_handle(IntPtr ctx, out IntPtr handle);
    [DllImport(LibHeif)] private static extern void heif_image_handle_release(IntPtr handle);
    [DllImport(LibHeif)] private static extern heif_error heif_decode_image(IntPtr handle, out IntPtr img, heif_colorspace colorspace, heif_chroma chroma, IntPtr options);
    [DllImport(LibHeif)] private static extern void heif_image_release(IntPtr img);
    [DllImport(LibHeif)] private static extern int heif_image_get_width(IntPtr img, heif_channel channel);
    [DllImport(LibHeif)] private static extern int heif_image_get_height(IntPtr img, heif_channel channel);
    [DllImport(LibHeif)] private static extern IntPtr heif_image_get_plane_readonly(IntPtr img, heif_channel channel, out int stride);

    public static (byte[] bgra, int width, int height)? Decode(string path)
    {
        IntPtr ctx = IntPtr.Zero, handle = IntPtr.Zero, img = IntPtr.Zero;
        try
        {
            ctx = heif_context_alloc();
            if (ctx == IntPtr.Zero) return null;

            var err = heif_context_read_from_file(ctx, path, IntPtr.Zero);
            if (err.code != 0) return null;

            err = heif_context_get_primary_image_handle(ctx, out handle);
            if (err.code != 0) return null;

            err = heif_decode_image(handle, out img, heif_colorspace.RGB, heif_chroma.Interleaved_RGBA, IntPtr.Zero);
            if (err.code != 0) return null;

            int w = heif_image_get_width(img, heif_channel.Interleaved);
            int h = heif_image_get_height(img, heif_channel.Interleaved);
            IntPtr plane = heif_image_get_plane_readonly(img, heif_channel.Interleaved, out int stride);
            if (plane == IntPtr.Zero || w <= 0 || h <= 0) return null;

            int bytesPerPixel = 4;
            var bgra = new byte[w * h * bytesPerPixel];

            for (int y = 0; y < h; y++)
            {
                Marshal.Copy(plane + y * stride, bgra, y * w * bytesPerPixel, w * bytesPerPixel);
            }

            for (int i = 0; i < bgra.Length; i += 4)
            {
                (bgra[i], bgra[i + 2]) = (bgra[i + 2], bgra[i]);
            }

            return (bgra, w, h);
        }
        catch
        {
            return null;
        }
        finally
        {
            if (img != IntPtr.Zero) heif_image_release(img);
            if (handle != IntPtr.Zero) heif_image_handle_release(handle);
            if (ctx != IntPtr.Zero) heif_context_free(ctx);
        }
    }
}
