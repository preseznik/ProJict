using System.IO;
using System.Runtime.InteropServices;
using System.Windows;
using System.Windows.Interop;
using System.Windows.Media;
using System.Windows.Media.Imaging;

namespace ProJict.App.Services;

/// <summary>
/// The preview Windows' File Explorer shows for a file: a frame of a video, a picture's thumbnail. Only real thumbnails
/// (not icons); null when Windows has none. Call on the UI thread.
/// </summary>
public static class ShellThumbnails
{
    private const int ThumbnailOnly = 0x8;

    public static ImageSource? Get(string path, int size)
    {
        try
        {
            var iid = typeof(IShellItemImageFactory).GUID;
            SHCreateItemFromParsingName(path, IntPtr.Zero, ref iid, out var factory);
            try
            {
                if (factory.GetImage(new NativeSize { Width = size, Height = size }, ThumbnailOnly, out var bitmap) != 0 || bitmap == IntPtr.Zero) return null;
                try
                {
                    var source = Imaging.CreateBitmapSourceFromHBitmap(bitmap, IntPtr.Zero, Int32Rect.Empty, BitmapSizeOptions.FromEmptyOptions());
                    source.Freeze();
                    return source;
                }
                finally { DeleteObject(bitmap); }
            }
            finally { Marshal.ReleaseComObject(factory); }
        }
        catch (Exception ex) when (ex is COMException or FileNotFoundException or UnauthorizedAccessException or ArgumentException)
        {
            return null;
        }
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct NativeSize
    {
        public int Width;
        public int Height;
    }

    [ComImport, Guid("bcc18b79-ba16-442f-80c4-8a59c30c463b"), InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
    private interface IShellItemImageFactory
    {
        [PreserveSig] int GetImage(NativeSize size, int flags, out IntPtr bitmap);
    }

    [DllImport("shell32.dll", CharSet = CharSet.Unicode, PreserveSig = false)]
    private static extern void SHCreateItemFromParsingName(string path, IntPtr bindContext, ref Guid iid, out IShellItemImageFactory factory);

    [DllImport("gdi32.dll")]
    private static extern bool DeleteObject(IntPtr handle);
}
