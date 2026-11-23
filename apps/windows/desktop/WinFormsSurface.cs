using System.Reflection;
using System.Runtime.InteropServices;

namespace PrivacyBrowser.Desktop;

/// <summary>
/// Eliminates WinForms first-frame GDI tearing, light-gray WM_ERASEBKGND flashes,
/// transparent child-HWND punch-throughs, and bright white title bars on dark forms
/// by applying Windows 11 DWM Dark Mode, rounded corners, DWMWA_CLOAK atomic reveal,
/// and recursive double-buffering across containers, TreeViews, and ListViews.
/// </summary>
internal static class WinFormsSurface
{
  private const int DwmwaCloak = 13;
  private const int DwmwaUseImmersiveDarkMode = 20;
  private const int DwmwaWindowCornerPreference = 33;
  private const int DwmwaBorderColor = 34;
  private const int DwmwcpRound = 2;

  private const int WsClipChildren = 0x02000000;
  private const int WsExComposited = 0x02000000;

  private const int TvmSetExtendedStyle = 0x112C;
  private const int TvsExDoubleBuffer = 0x0004;
  private const int LvmSetExtendedListViewStyle = 0x1036;
  private const int LvsExDoubleBuffer = 0x00010000;

  private static readonly PropertyInfo? DoubleBufferedProperty =
      typeof(Control).GetProperty("DoubleBuffered", BindingFlags.Instance | BindingFlags.NonPublic);

  [DllImport("dwmapi.dll")]
  private static extern int DwmSetWindowAttribute(IntPtr hwnd, int attr, ref int attrValue, int attrSize);

  [DllImport("user32.dll")]
  private static extern IntPtr SendMessage(IntPtr hwnd, int msg, IntPtr wParam, IntPtr lParam);

  internal static CreateParams WithComposited(CreateParams cp)
  {
    cp.Style |= WsClipChildren;
    cp.ExStyle |= WsExComposited;
    return cp;
  }

  internal static void Attach(
      Form form,
      bool darkChrome = true,
      bool cloakFirstShow = true,
      bool doubleBufferChildren = true)
  {
    EnableDoubleBuffering(form);

    var cloaked = false;
    System.Windows.Forms.Timer? safetyTimer = null;

    void Uncloak()
    {
      if (safetyTimer is not null)
      {
        safetyTimer.Stop();
        safetyTimer.Dispose();
        safetyTimer = null;
      }
      if (!cloaked) return;
      cloaked = false;
      if (!form.IsDisposed && form.IsHandleCreated)
        SetCloak(form.Handle, false);
    }

    form.HandleCreated += (_, _) =>
    {
      if (form.IsDisposed || !form.IsHandleCreated) return;
      if (darkChrome) ApplyDarkChrome(form.Handle);
      if (doubleBufferChildren) EnableDoubleBufferingTree(form);
      if (cloakFirstShow && form.Opacity > 0 && form.Left > -10_000)
      {
        if (SetCloak(form.Handle, true))
        {
          cloaked = true;
          safetyTimer?.Dispose();
          safetyTimer = new System.Windows.Forms.Timer { Interval = 220 };
          safetyTimer.Tick += (_, _) => Uncloak();
          safetyTimer.Start();
        }
      }
    };

    form.Shown += (_, _) =>
    {
      if (form.IsDisposed) return;
      if (doubleBufferChildren) EnableDoubleBufferingTree(form);
      if (cloaked)
      {
        form.PerformLayout();
        form.Refresh();
        try { form.BeginInvoke(new Action(Uncloak)); }
        catch { Uncloak(); }
      }
    };

    form.FormClosed += (_, _) => Uncloak();
    form.Disposed += (_, _) => Uncloak();
  }

  internal static void ApplyDarkChrome(IntPtr hwnd)
  {
    if (hwnd == IntPtr.Zero) return;
    try
    {
      var dark = 1;
      _ = DwmSetWindowAttribute(hwnd, DwmwaUseImmersiveDarkMode, ref dark, sizeof(int));
      var corner = DwmwcpRound;
      _ = DwmSetWindowAttribute(hwnd, DwmwaWindowCornerPreference, ref corner, sizeof(int));
      var borderColor = 0x003A3634;
      _ = DwmSetWindowAttribute(hwnd, DwmwaBorderColor, ref borderColor, sizeof(int));
    }
    catch
    {
      // Older Windows 10 builds ignore Windows 11 DWM attributes cleanly.
    }
  }

  internal static bool SetCloak(IntPtr hwnd, bool cloak)
  {
    if (hwnd == IntPtr.Zero) return false;
    try
    {
      var value = cloak ? 1 : 0;
      return DwmSetWindowAttribute(hwnd, DwmwaCloak, ref value, sizeof(int)) == 0;
    }
    catch
    {
      return false;
    }
  }

  internal static void EnableDoubleBufferingTree(Control root)
  {
    EnableDoubleBuffering(root);
    foreach (Control child in root.Controls)
      EnableDoubleBufferingTree(child);
  }

  private static void EnableDoubleBuffering(Control control)
  {
    try
    {
      if (control is Form or Panel or TableLayoutPanel or FlowLayoutPanel or ListView or SplitContainer)
        DoubleBufferedProperty?.SetValue(control, true);

      if (control is TreeView tv)
      {
        void ApplyTreeBuffer()
        {
          if (!tv.IsDisposed && tv.IsHandleCreated)
            _ = SendMessage(tv.Handle, TvmSetExtendedStyle, (IntPtr)TvsExDoubleBuffer, (IntPtr)TvsExDoubleBuffer);
        }
        if (tv.IsHandleCreated) ApplyTreeBuffer();
        else tv.HandleCreated += (_, _) => ApplyTreeBuffer();
      }
      else if (control is ListView lv)
      {
        void ApplyListBuffer()
        {
          if (!lv.IsDisposed && lv.IsHandleCreated)
            _ = SendMessage(lv.Handle, LvmSetExtendedListViewStyle, (IntPtr)LvsExDoubleBuffer, (IntPtr)LvsExDoubleBuffer);
        }
        if (lv.IsHandleCreated) ApplyListBuffer();
        else lv.HandleCreated += (_, _) => ApplyListBuffer();
      }
    }
    catch
    {
      // Non-fatal: skip controls that do not support extended double-buffering.
    }
  }

  internal static void StyleDarkButton(Button button, bool primary = false)
  {
    button.FlatStyle = FlatStyle.Flat;
    button.FlatAppearance.BorderSize = primary ? 0 : 1;
    button.FlatAppearance.BorderColor = Color.FromArgb(64, 68, 78);
    button.BackColor = primary ? Color.FromArgb(10, 132, 255) : Color.FromArgb(44, 47, 56);
    button.ForeColor = Color.White;
    button.FlatAppearance.MouseOverBackColor = primary ? Color.FromArgb(34, 148, 255) : Color.FromArgb(58, 62, 74);
    button.FlatAppearance.MouseDownBackColor = primary ? Color.FromArgb(8, 110, 224) : Color.FromArgb(72, 76, 90);
    button.Cursor = Cursors.Hand;
    if (primary) button.Font = new Font("Segoe UI", 10F, FontStyle.Bold);
  }
}
