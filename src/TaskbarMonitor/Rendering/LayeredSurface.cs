using System.Drawing.Imaging;
using System.Runtime.InteropServices;
using TaskbarMonitor.Interop;

namespace TaskbarMonitor.Rendering;

/// <summary>
/// Backing store for one layered (per-pixel alpha) strip: a top-down 32bpp DIB section selected
/// into a memory DC, wrapped in a GDI+ <see cref="Graphics"/>.
///
/// The DIB is owned rather than obtained via <c>Bitmap.GetHbitmap</c> so the pixel format is
/// unambiguously <see cref="PixelFormat.Format32bppPArgb"/> — UpdateLayeredWindow with AC_SRC_ALPHA
/// requires premultiplied bytes, and GDI+ writes them directly into our buffer.
///
/// Allocated once per size and reused across repaints, so the 1 Hz refresh loop never churns GDI handles.
/// </summary>
internal sealed class LayeredSurface : IDisposable
{
    private IntPtr _screenDc;
    private IntPtr _memDc;
    private IntPtr _dib;
    private IntPtr _oldBitmap;
    private Bitmap? _bitmap;

    public Graphics? Graphics { get; private set; }
    public Size Size { get; private set; }

    /// <summary>Allocates (or reallocates) the surface. No-op when the size is unchanged.</summary>
    public bool Resize(int width, int height)
    {
        if (width <= 0 || height <= 0) return false;
        if (Graphics is not null && Size.Width == width && Size.Height == height) return true;

        Release();

        _screenDc = NativeMethods.GetDC(IntPtr.Zero);
        if (_screenDc == IntPtr.Zero) return false;

        _memDc = NativeMethods.CreateCompatibleDC(_screenDc);
        if (_memDc == IntPtr.Zero) { Release(); return false; }

        var bmi = new NativeMethods.BITMAPINFO
        {
            bmiHeader = new NativeMethods.BITMAPINFOHEADER
            {
                biSize = (uint)Marshal.SizeOf<NativeMethods.BITMAPINFOHEADER>(),
                biWidth = width,
                biHeight = -height,      // top-down
                biPlanes = 1,
                biBitCount = 32,
                biCompression = NativeMethods.BI_RGB,
            },
        };

        _dib = NativeMethods.CreateDIBSection(_screenDc, ref bmi, NativeMethods.DIB_RGB_COLORS,
            out IntPtr bits, IntPtr.Zero, 0);
        if (_dib == IntPtr.Zero || bits == IntPtr.Zero) { Release(); return false; }

        _oldBitmap = NativeMethods.SelectObject(_memDc, _dib);

        try
        {
            _bitmap = new Bitmap(width, height, width * 4, PixelFormat.Format32bppPArgb, bits);
            Graphics = Graphics.FromImage(_bitmap);
        }
        catch { Release(); return false; }

        Size = new Size(width, height);
        return true;
    }

    /// <summary>Pushes the rendered pixels to the window. Position is left to SetWindowPos (pptDst = NULL).</summary>
    public bool Commit(IntPtr hwnd)
    {
        if (Graphics is null || _memDc == IntPtr.Zero) return false;
        Graphics.Flush();

        var size = new NativeMethods.SIZE(Size.Width, Size.Height);
        var src = new NativeMethods.POINT(0, 0);
        var blend = new NativeMethods.BLENDFUNCTION
        {
            BlendOp = NativeMethods.AC_SRC_OVER,
            BlendFlags = 0,
            SourceConstantAlpha = 255,
            AlphaFormat = NativeMethods.AC_SRC_ALPHA,
        };

        return NativeMethods.UpdateLayeredWindow(hwnd, _screenDc, IntPtr.Zero, ref size,
            _memDc, ref src, 0, ref blend, NativeMethods.ULW_ALPHA);
    }

    private void Release()
    {
        Graphics?.Dispose();
        Graphics = null;
        _bitmap?.Dispose();          // does not own the pixel buffer
        _bitmap = null;

        if (_memDc != IntPtr.Zero && _oldBitmap != IntPtr.Zero)
            NativeMethods.SelectObject(_memDc, _oldBitmap);
        _oldBitmap = IntPtr.Zero;

        if (_dib != IntPtr.Zero) { NativeMethods.DeleteObject(_dib); _dib = IntPtr.Zero; }
        if (_memDc != IntPtr.Zero) { NativeMethods.DeleteDC(_memDc); _memDc = IntPtr.Zero; }
        if (_screenDc != IntPtr.Zero) { NativeMethods.ReleaseDC(IntPtr.Zero, _screenDc); _screenDc = IntPtr.Zero; }

        Size = Size.Empty;
    }

    public void Dispose() => Release();
}
