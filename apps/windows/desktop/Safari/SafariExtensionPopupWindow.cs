using System.Text.Json;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
using Microsoft.Web.WebView2.Core;
using Brushes = System.Windows.Media.Brushes;
using Color = System.Windows.Media.Color;
using HorizontalAlignment = System.Windows.HorizontalAlignment;
using Orientation = System.Windows.Controls.Orientation;
using Point = System.Windows.Point;
using WpfWebView2 = Microsoft.Web.WebView2.Wpf.WebView2;

namespace PrivacyBrowser.Desktop.Safari;

/// <summary>
/// Anchored frameless floating WebView2 popup for browser extensions (Chrome/Safari-style).
/// Opens directly beneath the Extensions toolbar button on top of the active website
/// instead of hijacking a separate full-screen browser tab.
/// Uses AllowsTransparency=false + Windows 11 DWM rounded corners/shadow so the hosted
/// Chromium WebView2 renders at full hardware speed with zero WPF airspace clipping.
/// </summary>
internal sealed class SafariExtensionPopupWindow : Window
{
  private const double HeaderHeight = 36;
  private const double MinPopupWidth = 320;
  private const double MaxPopupWidth = 640;
  private const double MinPopupHeight = 180;
  private const double MaxPopupHeight = 680;

  private readonly UIElement _anchor;
  private readonly CoreWebView2Environment _environment;
  private readonly bool _isPrivate;
  private readonly string _popupUrl;
  private readonly string? _optionsUrl;
  private readonly string _activeWebsiteUrl;
  private readonly string _activeWebsiteTitle;
  private readonly Func<string, Task> _openInTab;
  private readonly WpfWebView2 _webView = new();
  private readonly long _createdTick = Environment.TickCount64;
  private bool _closing;

  internal SafariExtensionPopupWindow(
      Window owner,
      UIElement anchor,
      CoreWebView2Environment environment,
      bool isPrivate,
      string extensionName,
      string popupUrl,
      string? optionsUrl,
      string activeWebsiteUrl,
      string activeWebsiteTitle,
      Func<string, Task> openInTab)
  {
    _anchor = anchor;
    _environment = environment;
    _isPrivate = isPrivate;
    _popupUrl = popupUrl;
    _optionsUrl = optionsUrl;
    _activeWebsiteUrl = activeWebsiteUrl;
    _activeWebsiteTitle = activeWebsiteTitle;
    _openInTab = openInTab;

    Title = extensionName;
    Owner = owner;
    WindowStyle = WindowStyle.None;
    AllowsTransparency = false;
    ResizeMode = ResizeMode.NoResize;
    ShowInTaskbar = false;
    WindowStartupLocation = WindowStartupLocation.Manual;
    Width = 390;
    Height = 460 + HeaderHeight;
    Background = SafariTheme.PopoverBg;
    Foreground = SafariTheme.TextPrimary;
    FontFamily = SafariTheme.UiFont;
    UseLayoutRounding = true;
    SnapsToDevicePixels = true;

    BuildVisualTree(extensionName);

    SourceInitialized += (_, _) => DwmBackdrop.Apply(this, acrylic: false);
    Loaded += async (_, _) =>
    {
      RepositionToAnchor();
      await InitializeWebViewAsync();
    };
    Deactivated += (_, _) =>
    {
      if (_closing) return;
      if (Environment.TickCount64 - _createdTick < 220) return;
      CloseSafely();
    };
    PreviewKeyDown += (_, e) =>
    {
      if (e.Key == Key.Escape)
      {
        e.Handled = true;
        CloseSafely();
      }
    };
    Closed += (_, _) =>
    {
      _closing = true;
      try { _webView.Dispose(); } catch { }
    };
  }

  private void BuildVisualTree(string extensionName)
  {
    var root = new Border
    {
      Background = SafariTheme.PopoverBg,
      BorderBrush = SafariTheme.SpecularBorder,
      BorderThickness = new Thickness(1),
    };

    var grid = new Grid();
    grid.RowDefinitions.Add(new RowDefinition { Height = new GridLength(HeaderHeight) });
    grid.RowDefinitions.Add(new RowDefinition { Height = new GridLength(1, GridUnitType.Star) });

    // Sleek compact macOS popover header bar
    var header = new Border
    {
      Background = new SolidColorBrush(Color.FromRgb(26, 28, 36)),
      BorderBrush = SafariTheme.Divider,
      BorderThickness = new Thickness(0, 0, 0, 1),
      Padding = new Thickness(10, 0, 6, 0),
    };
    var dock = new DockPanel { VerticalAlignment = VerticalAlignment.Center };

    var actions = new StackPanel { Orientation = Orientation.Horizontal, VerticalAlignment = VerticalAlignment.Center };
    if (!string.IsNullOrWhiteSpace(_optionsUrl))
    {
      actions.Children.Add(SafariTheme.CreateGlassButton(
          SafariIcons.Create(SafariIcon.Settings, 13.5, SafariTheme.TextSecondary, 1.55),
          () => { var url = _optionsUrl!; CloseSafely(); _ = _openInTab(url); },
          "확장 옵션 열기",
          26, 26, 8));
    }
    actions.Children.Add(SafariTheme.CreateGlassButton(
        SafariIcons.Create(SafariIcon.OpenExternal, 13, SafariTheme.TextSecondary, 1.55),
        () => { CloseSafely(); _ = _openInTab(_popupUrl); },
        "새 탭에서 열기",
        26, 26, 8));
    actions.Children.Add(SafariTheme.CreateGlassButton(
        SafariIcons.Create(SafariIcon.Close, 11, SafariTheme.TextSecondary, 1.8),
        CloseSafely,
        "닫기 (Esc)",
        26, 26, 8));
    DockPanel.SetDock(actions, Dock.Right);
    dock.Children.Add(actions);

    var titleStack = new StackPanel { Orientation = Orientation.Horizontal, VerticalAlignment = VerticalAlignment.Center };
    titleStack.Children.Add(SafariIcons.Create(SafariIcon.Extensions, 14, SafariTheme.Accent, 1.6));
    titleStack.Children.Add(new TextBlock
    {
      Text = extensionName,
      FontSize = 12,
      FontWeight = FontWeights.SemiBold,
      Foreground = SafariTheme.TextPrimary,
      TextTrimming = TextTrimming.CharacterEllipsis,
      VerticalAlignment = VerticalAlignment.Center,
      Margin = new Thickness(7, 0, 8, 0),
    });
    dock.Children.Add(titleStack);
    header.Child = dock;

    Grid.SetRow(header, 0);
    grid.Children.Add(header);

    _webView.DefaultBackgroundColor = System.Drawing.Color.FromArgb(255, 28, 30, 38);
    Grid.SetRow(_webView, 1);
    grid.Children.Add(_webView);

    root.Child = grid;
    Content = root;
  }

  private async Task InitializeWebViewAsync()
  {
    try
    {
      var options = _environment.CreateCoreWebView2ControllerOptions();
      options.IsInPrivateModeEnabled = _isPrivate;
      await _webView.EnsureCoreWebView2Async(_environment, options);
      if (_closing || _webView.CoreWebView2 is null) return;

      var core = _webView.CoreWebView2;
      core.Settings.AreDefaultContextMenusEnabled = true;
      core.Settings.IsStatusBarEnabled = false;

      // Provide active-website context + automatic popup size observation + link-in-tab forwarding
      await core.AddScriptToExecuteOnDocumentCreatedAsync(BuildPopupBridgeScript(_activeWebsiteUrl, _activeWebsiteTitle));

      core.WebMessageReceived += (_, e) =>
      {
        if (_closing) return;
        try
        {
          using var doc = JsonDocument.Parse(e.WebMessageAsJson);
          var root = doc.RootElement;
          if (!root.TryGetProperty("type", out var typeProp)) return;
          var type = typeProp.GetString();
          if (type == "omyaru-ext-popup-size")
          {
            var w = root.TryGetProperty("width", out var wp) && wp.ValueKind == JsonValueKind.Number ? wp.GetDouble() : 0;
            var h = root.TryGetProperty("height", out var hp) && hp.ValueKind == JsonValueKind.Number ? hp.GetDouble() : 0;
            if (w >= 140 && h >= 80)
              Dispatcher.BeginInvoke(() => ResizeToContent(w, h));
          }
          else if (type == "omyaru-ext-open-tab" && root.TryGetProperty("url", out var up) && up.GetString() is { Length: > 0 } dest)
          {
            Dispatcher.BeginInvoke(() =>
            {
              CloseSafely();
              _ = _openInTab(dest);
            });
          }
          else if (type == "omyaru-ext-close")
          {
            Dispatcher.BeginInvoke(CloseSafely);
          }
        }
        catch { }
      };

      core.NewWindowRequested += (_, e) =>
      {
        e.Handled = true;
        if (!string.IsNullOrWhiteSpace(e.Uri))
        {
          var uri = e.Uri;
          Dispatcher.BeginInvoke(() =>
          {
            CloseSafely();
            _ = _openInTab(uri);
          });
        }
      };

      core.WindowCloseRequested += (_, _) => Dispatcher.BeginInvoke(CloseSafely);
      core.Navigate(_popupUrl);
    }
    catch
    {
      // Non-fatal: keep popup open or let user close cleanly.
    }
  }

  private void ResizeToContent(double contentWidth, double contentHeight)
  {
    if (_closing) return;
    var targetW = Math.Clamp(Math.Ceiling(contentWidth) + 2, MinPopupWidth, MaxPopupWidth);
    var targetH = Math.Clamp(Math.Ceiling(contentHeight) + 2, MinPopupHeight, MaxPopupHeight) + HeaderHeight;
    if (Math.Abs(Width - targetW) < 4 && Math.Abs(Height - targetH) < 4) return;
    Width = targetW;
    Height = targetH;
    RepositionToAnchor();
  }

  internal void RepositionToAnchor()
  {
    if (_closing || Owner is null) return;
    try
    {
      var source = PresentationSource.FromVisual(Owner);
      if (source?.CompositionTarget is null) return;
      var toDip = source.CompositionTarget.TransformFromDevice;

      // Align popup snugly to the right edge of the browser window, right below the toolbar.
      var ownerTopRightDev = Owner.PointToScreen(new Point(Owner.ActualWidth, 0));
      var ownerTopRight = toDip.Transform(ownerTopRightDev);

      Point anchorBottomDev;
      if (_anchor is FrameworkElement fe && fe.IsLoaded && fe.ActualHeight > 0)
        anchorBottomDev = fe.PointToScreen(new Point(fe.ActualWidth, fe.ActualHeight));
      else
        anchorBottomDev = Owner.PointToScreen(new Point(Owner.ActualWidth, 48));
      var anchorBottom = toDip.Transform(anchorBottomDev);

      // Clamp cleanly inside the active monitor's working area
      var screen = System.Windows.Forms.Screen.FromPoint(
          new System.Drawing.Point((int)anchorBottomDev.X, (int)anchorBottomDev.Y));
      var waTopLeft = toDip.Transform(new Point(screen.WorkingArea.Left, screen.WorkingArea.Top));
      var waBottomRight = toDip.Transform(new Point(screen.WorkingArea.Right, screen.WorkingArea.Bottom));

      var visibleRightEdge = Math.Min(ownerTopRight.X, waBottomRight.X) - 10;
      var desiredLeft = visibleRightEdge - Width;
      var desiredTop = anchorBottom.Y + 8;

      Left = Math.Clamp(desiredLeft, waTopLeft.X + 8, Math.Max(waTopLeft.X + 8, waBottomRight.X - Width - 8));
      Top = Math.Clamp(desiredTop, waTopLeft.Y + 8, Math.Max(waTopLeft.Y + 8, waBottomRight.Y - Height - 8));
    }
    catch { }
  }

  internal void CloseSafely()
  {
    if (_closing) return;
    _closing = true;
    try { Close(); } catch { }
  }

  private static string BuildPopupBridgeScript(string activeWebsiteUrl, string activeWebsiteTitle)
  {
    var urlJson = JsonSerializer.Serialize(string.IsNullOrWhiteSpace(activeWebsiteUrl) ? "about:blank" : activeWebsiteUrl);
    var titleJson = JsonSerializer.Serialize(string.IsNullOrWhiteSpace(activeWebsiteTitle) ? "새 탭" : activeWebsiteTitle);
    return $$"""
      (function(){
        if (window.__omyaruExtPopupBridge) return;
        window.__omyaruExtPopupBridge = true;
        const hostUrl = {{urlJson}};
        const hostTitle = {{titleJson}};

        // 1. Report natural extension popup content size so the floating container fits tightly
        let lastW = 0, lastH = 0;
        function reportSize() {
          try {
            const de = document.documentElement;
            const body = document.body;
            if (!de || !body) return;
            const w = Math.max(de.scrollWidth || 0, body.scrollWidth || 0, body.offsetWidth || 0);
            const h = Math.max(de.scrollHeight || 0, body.scrollHeight || 0, body.offsetHeight || 0);
            if (w >= 140 && h >= 80 && (Math.abs(w - lastW) >= 4 || Math.abs(h - lastH) >= 4)) {
              lastW = w; lastH = h;
              window.chrome?.webview?.postMessage({ type: 'omyaru-ext-popup-size', width: w, height: h });
            }
          } catch {}
        }
        window.addEventListener('DOMContentLoaded', () => {
          reportSize();
          setTimeout(reportSize, 80);
          setTimeout(reportSize, 260);
          try {
            if (typeof ResizeObserver !== 'undefined' && document.body) {
              new ResizeObserver(reportSize).observe(document.body);
            }
          } catch {}
        });
        window.addEventListener('load', () => { reportSize(); setTimeout(reportSize, 150); });

        // 2. Active-website tab fallback when WebView2 chrome.tabs returns no website tab
        try {
          const fakeActiveTab = {
            id: 1, index: 0, windowId: 1, active: true, highlighted: true,
            url: hostUrl, title: hostTitle, status: 'complete', incognito: false
          };
          const api = window.chrome || window.browser;
          if (api && api.tabs) {
            if (typeof api.tabs.query === 'function') {
              const origQuery = api.tabs.query.bind(api.tabs);
              api.tabs.query = function(queryInfo, cb) {
                const wantsActive = !queryInfo || queryInfo.active || queryInfo.currentWindow;
                const fixTabs = (tabs) => {
                  if (wantsActive && hostUrl !== 'about:blank' && (!Array.isArray(tabs) || tabs.length === 0 || tabs.every(t => !t.url || t.url.startsWith('chrome-extension://')))) {
                    return [fakeActiveTab];
                  }
                  return tabs;
                };
                if (typeof cb === 'function') {
                  try { return origQuery(queryInfo, (tabs) => cb(fixTabs(tabs))); }
                  catch { cb(fixTabs([])); return; }
                }
                return Promise.resolve().then(() => origQuery(queryInfo)).then(fixTabs).catch(() => fixTabs([]));
              };
            }
            if (typeof api.tabs.create === 'function') {
              const origCreate = api.tabs.create.bind(api.tabs);
              api.tabs.create = function(props, cb) {
                if (props && typeof props.url === 'string' && props.url.length > 0) {
                  let resolved = props.url;
                  try { resolved = new URL(props.url, location.href).href; } catch {}
                  window.chrome?.webview?.postMessage({ type: 'omyaru-ext-open-tab', url: resolved });
                  const created = { id: 2, url: resolved, active: true };
                  if (typeof cb === 'function') { cb(created); return; }
                  return Promise.resolve(created);
                }
                return origCreate(props, cb);
              };
            }
          }
          if (typeof window.browser === 'undefined' && typeof window.chrome !== 'undefined') {
            window.browser = window.chrome;
          }
        } catch {}
      })();
      """;
  }
}
