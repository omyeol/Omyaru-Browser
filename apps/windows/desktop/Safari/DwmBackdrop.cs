using System.Runtime.InteropServices;
using System.Windows;
using System.Windows.Interop;
using System.Windows.Media.Imaging;

namespace PrivacyBrowser.Desktop.Safari;

internal static class DwmBackdrop
{
  private const int DwmwaUseImmersiveDarkMode = 20;
  private const int DwmwaWindowCornerPreference = 33;
  private const int DwmwaBorderColor = 34;
  private const int DwmwaSystemBackdropType = 38;
  private const int DwmwcpRound = 2;
  private const int DwmsbtMainWindow = 2;       // Windows 11 Mica
  private const int DwmsbtTransientWindow = 3;  // Windows 11 Acrylic

  [DllImport("dwmapi.dll")]
  private static extern int DwmSetWindowAttribute(IntPtr hwnd, int attr, ref int attrValue, int attrSize);

  [DllImport("gdi32.dll")]
  [return: MarshalAs(UnmanagedType.Bool)]
  private static extern bool DeleteObject(IntPtr hObject);

  internal static void Apply(Window window, bool acrylic = false)
  {
    var hwnd = new WindowInteropHelper(window).Handle;
    if (hwnd == IntPtr.Zero) return;
    try
    {
      var dark = 1;
      _ = DwmSetWindowAttribute(hwnd, DwmwaUseImmersiveDarkMode, ref dark, sizeof(int));
      var corner = DwmwcpRound;
      _ = DwmSetWindowAttribute(hwnd, DwmwaWindowCornerPreference, ref corner, sizeof(int));
      var backdrop = acrylic ? DwmsbtTransientWindow : DwmsbtMainWindow;
      _ = DwmSetWindowAttribute(hwnd, DwmwaSystemBackdropType, ref backdrop, sizeof(int));
      var borderColor = 0x003A3634; // Subtle translucent hairline border in BGR
      _ = DwmSetWindowAttribute(hwnd, DwmwaBorderColor, ref borderColor, sizeof(int));
    }
    catch
    {
      // Older Windows 10 builds cleanly ignore Windows 11 DWM backdrop attributes.
    }
  }

  internal static BitmapSource? FromDrawingImage(System.Drawing.Image? image)
  {
    if (image is null) return null;
    try
    {
      using var bmp = new System.Drawing.Bitmap(image);
      var hBitmap = bmp.GetHbitmap();
      try
      {
        var source = Imaging.CreateBitmapSourceFromHBitmap(
            hBitmap,
            IntPtr.Zero,
            Int32Rect.Empty,
            BitmapSizeOptions.FromEmptyOptions());
        source.Freeze();
        return source;
      }
      finally
      {
        DeleteObject(hBitmap);
      }
    }
    catch
    {
      return null;
    }
  }

  internal sealed class Win32WindowAdapter(IntPtr handle) : System.Windows.Forms.IWin32Window
  {
    public IntPtr Handle { get; } = handle;
  }

  internal static System.Windows.Forms.IWin32Window OwnerOf(Window window) =>
      new Win32WindowAdapter(new WindowInteropHelper(window).Handle);
}
