using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Globalization;
using System.Drawing.Imaging;
using System.Drawing.Drawing2D;
using System.Runtime.InteropServices;
using Microsoft.Web.WebView2.Core;
using Microsoft.Web.WebView2.WinForms;

namespace PrivacyBrowser.Desktop;

internal sealed class BrowserShellForm : Form
{
  // Apple Safari (macOS Sequoia/Sonoma & iOS/iPadOS) inspired dark glass surface system.
  private static readonly Color Chrome = Color.FromArgb(28, 28, 30);
  private static readonly Color ChromeRaised = Color.FromArgb(44, 44, 48);
  private static readonly Color PageBackground = Color.FromArgb(18, 18, 20);
  private static readonly Color Surface = Color.FromArgb(44, 44, 48);
  private static readonly Color Field = Color.FromArgb(20, 20, 22);
  private static readonly Color Outline = Color.FromArgb(58, 58, 64);
  private static readonly Color OverviewControl = Color.FromArgb(38, 38, 42);
  private static readonly Color TextColor = Color.FromArgb(245, 245, 247);
  private static readonly Color MutedText = Color.FromArgb(152, 152, 157);
  private static readonly Color Accent = Color.FromArgb(10, 132, 255);
  private static readonly Color Danger = Color.FromArgb(255, 69, 58);

  private SyncSnapshot _state = BrowserStateStore.Load();
  private DesktopPreferences _preferences = DesktopPreferencesStore.Load();
  private readonly TextBox _address = new();
  private readonly TextBox _overviewSearch = new();
  private readonly FlowLayoutPanel _tabStrip = new();
  private readonly FlowLayoutPanel _overviewGrid = new();
  private readonly Panel _browserHost = new();
  private readonly GradientPanel _startPage = new();
  private readonly GradientPanel _tabOverview = new();
  private readonly TopCropPictureBox _tabActivationPreview = new();
  private readonly Label _statusText = new();
  private readonly Label _overviewTitle = new();
  private readonly Label _privacyReport = new();
  private readonly Dictionary<string, WebView2> _views = new(StringComparer.Ordinal);
  private readonly Dictionary<string, Image> _previews = new(StringComparer.Ordinal);
  private readonly Dictionary<string, Task<CoreWebView2Environment>> _environments = new(StringComparer.Ordinal);
  private readonly Label _toolbarZoomLabel = new();
  private readonly Stack<TabEntry> _closedTabs = new();
  private BookmarkCatalog _bookmarks = BookmarkStore.Load();
  private readonly System.Windows.Forms.Timer _autoSyncTimer = new() { Interval = 1_000 };
  private readonly System.Windows.Forms.Timer _tabHoverTimer = new() { Interval = 280 };
  private readonly System.Windows.Forms.Timer _previewCaptureTimer = new() { Interval = 1_100 };
  private readonly System.Windows.Forms.Timer _overviewSnapshotTimer = new() { Interval = 120 };
  private readonly System.Windows.Forms.Timer _windowSnapshotTimer = new() { Interval = 1_000 };
  private readonly System.Windows.Forms.Timer _resizeSettleTimer = new() { Interval = 95 };
  private readonly PictureBox _overviewTransitionSurface = new();
  private readonly AnimatedSnapshotOverlay _windowTransitionSurface = new();
  private Bitmap? _overviewSnapshot;
  private Bitmap? _windowSnapshot;
  private TableLayoutPanel _rootLayout = null!;
  private readonly bool _enableAutomaticSync;
  private PillButton _backButton = null!;
  private PillButton _forwardButton = null!;
  private PillButton _bookmarkStarButton = null!;
  private PillButton _securityBadge = null!;
  private PillButton _toolbarProfileButton = null!;
  private RoundedPanel _addressHost = null!;
  private PillButton _overviewProfileButton = null!;
  private PillButton _overviewGroupButton = null!;
  private PillButton _overviewSearchButton = null!;
  private WindowCaptionButton _maximizeButton = null!;
  private RoundedPanel _overviewSearchHost = null!;
  private FlowLayoutPanel _favoritesBar = null!;
  private FlowLayoutPanel _startPageFavorites = null!;
  private FlowLayoutPanel _toolbarWorkspace = null!;
  private CachedPopupForm? _mainMenu;
  private Label? _mainMenuZoomLabel;
  private bool _closing;
  private bool _stateDirty;
  private bool _autoSyncBusy;
  private readonly CancellationTokenSource _driveCancellation = new();
  private DateTimeOffset _driveNextCheck;
  private int _driveFailures;
  private bool _showingOverview;
  private bool _captureInProgress;
  private bool _fullScreen;
  private FormWindowState _windowedState;
  private Rectangle _windowedBounds;
  private string _syncStatus = "로컬 저장 · 분석/광고 SDK 없음";
  private TabStripItemControl? _pendingHoverTab;
  private TabHoverPreviewWindow? _tabHoverPreview;
  private string? _pendingPreviewTabId;
  private int _previewCaptureGeneration;
  private FormWindowState _lastWindowState = FormWindowState.Normal;
  private Size _lastClientSize;
  private bool _handlingWindowTransition;
  private BookshelfForm? _bookshelfForm;

  internal bool HasInitializedWebView => _views.Values.Any(view => view.CoreWebView2 is not null);
  internal bool HasInitializedTransitionCaches =>
      (_windowSnapshot is { Width: > 0, Height: > 0 } || _overviewSnapshot is { Width: > 0, Height: > 0 }) &&
      _mainMenu?.HasSnapshot == true;
  internal bool HasValidChromeLayout =>
      _addressHost is { IsDisposed: false, Height: 36, Left: 6, Top: >= 6, Bottom: <= 50, Width: >= 240 } &&
      _address is { TextAlign: HorizontalAlignment.Left } &&
      _addressHost.Parent is { ClientSize.Height: <= 60 } &&
      _startPageFavorites is { IsDisposed: false } &&
      _startPageFavorites.Controls.Count > 0 &&
      _startPageFavorites.Controls[0] is { Width: >= 80, Height: >= 88 };

  internal async Task<bool> VerifyBrowserExtensionsAsync()
  {
    if (_state.ActiveTabId is null || !_views.TryGetValue(_state.ActiveTabId, out var view) ||
        view.CoreWebView2 is null) return false;
    var folder = Path.Combine(BrowserStateStore.StateDirectory, "extension-smoke-" + Guid.NewGuid().ToString("N"));
    var progress = Path.Combine(BrowserStateStore.StateDirectory, "extension-smoke-progress.txt");
    Directory.CreateDirectory(folder);
    CoreWebView2BrowserExtension? extension = null;
    try
    {
      File.WriteAllText(Path.Combine(folder, "manifest.json"),
          """{"manifest_version":3,"name":"Omyaru Extension Smoke","version":"1.0","description":"Temporary extension API verification","background":{"service_worker":"background.js"}}""");
      File.WriteAllText(Path.Combine(folder, "background.js"),
          "chrome.runtime.onInstalled.addListener(() => {});");
      var profile = view.CoreWebView2.Profile;
      File.WriteAllText(progress, "adding");
      extension = await profile.AddBrowserExtensionAsync(folder);
      File.WriteAllText(progress, "listing");
      if (!(await profile.GetBrowserExtensionsAsync()).Any(item => item.Id == extension.Id)) return false;
      File.WriteAllText(progress, "disabling");
      await extension.EnableAsync(false);
      if (extension.IsEnabled) return false;
      File.WriteAllText(progress, "enabling");
      await extension.EnableAsync(true);
      if (!extension.IsEnabled) return false;

      // Also verify Firefox-metadata MV3 background.scripts + content_scripts normalization
      // produces no leading-underscore filenames rejected by WebView2 (0x80070005).
      var firefoxFolder = Path.Combine(BrowserStateStore.StateDirectory, "extension-ff-smoke-" + Guid.NewGuid().ToString("N"));
      CoreWebView2BrowserExtension? firefoxExtension = null;
      try
      {
        Directory.CreateDirectory(firefoxFolder);
        Directory.CreateDirectory(Path.Combine(firefoxFolder, "META-INF"));
        Directory.CreateDirectory(Path.Combine(firefoxFolder, "_metadata"));
        File.WriteAllText(Path.Combine(firefoxFolder, "META-INF", "manifest.mf"), "Manifest-Version: 1.0");
        File.WriteAllText(Path.Combine(firefoxFolder, "_metadata", "verified_contents.json"), "{}");
        File.WriteAllText(Path.Combine(firefoxFolder, "manifest.json"),
            """{"manifest_version":3,"name":"Omyaru Firefox Compat Smoke","version":"1.0","browser_specific_settings":{"gecko":{"id":"smoke@omyaru.test"}},"background":{"scripts":["bg.js"]},"content_scripts":[{"matches":["https://example.com/*"],"js":["content.js"]}]}""");
        File.WriteAllText(Path.Combine(firefoxFolder, "bg.js"), "console.log('bg');");
        File.WriteAllText(Path.Combine(firefoxFolder, "content.js"), "console.log('content');");
        File.WriteAllText(progress, "adding-firefox-compat");
        firefoxExtension = await ExtensionManagerForm.InstallFromLocalPathAsync(profile, _state.ActiveProfileId, firefoxFolder, isPackageFile: false);
        if (string.IsNullOrWhiteSpace(firefoxExtension.Id)) return false;
      }
      finally
      {
        if (firefoxExtension is not null)
        {
          try { await firefoxExtension.RemoveAsync(); } catch { }
        }
        try { Directory.Delete(firefoxFolder, true); } catch { }
      }

      File.WriteAllText(progress, "done");
      return extension.IsEnabled;
    }
    catch (Exception error) { File.WriteAllText(progress, error.GetType().Name + ": " + error.Message); return false; }
    finally
    {
      if (extension is not null)
      {
        try { await extension.RemoveAsync(); } catch { }
      }
      try { Directory.Delete(folder, true); } catch { }
    }
  }

  internal async Task<bool> VerifyTallPageRegionCaptureAsync()
  {
    var view = _views.Values.FirstOrDefault(candidate => candidate.CoreWebView2 is not null);
    if (view?.CoreWebView2 is null) return false;
    _startPage.Visible = false;
    view.Visible = true;
    view.BringToFront();
    await view.CoreWebView2.ExecuteScriptAsync("""
        (() => {
          document.documentElement.style.cssText = 'margin:0;padding:0;background:#1e1f21;';
          document.body.style.cssText = 'margin:0;padding:0;background:#1e1f21;height:30000px;position:relative;';
          document.body.innerHTML = '';
          const header = document.createElement('div');
          header.style.cssText = 'position:fixed;top:0;left:0;right:0;height:56px;background:#111;z-index:999;';
          document.body.appendChild(header);
          const canvas = document.createElement('canvas');
          canvas.width = 720;
          canvas.height = 1600;
          canvas.style.cssText = 'position:absolute;left:360px;top:23900px;width:720px;height:1600px;display:block;';
          const ctx = canvas.getContext('2d');
          for (let y = 0; y < 1600; y += 80) {
            ctx.fillStyle = y % 160 === 0 ? '#ff5e57' : '#0fbcf9';
            ctx.fillRect(0, y, 720, 80);
            ctx.fillStyle = '#ffd32a';
            ctx.fillRect(120, y + 16, 480, 48);
          }
          document.body.appendChild(canvas);
          window.scrollTo(0, 24000);
        })();
        """);
    await Task.Delay(80);
    await PreparePageForCaptureAsync(view);
    using var captured = await CapturePageRegionBitmapAsync(
        view,
        new LiveCaptureSelection(360, 24000, 719, 1476, "bookshelf", CaptureAction.AddToBookshelf));
    await RestorePageAfterCaptureAsync(view);
    return captured is { Width: >= 719, Height: >= 1476 } && !IsNearlyUniformColor(captured);
  }

  internal BrowserShellForm(bool enableAutomaticSync = true)
  {
    _enableAutomaticSync = enableAutomaticSync;
    Text = "Omyaru — Windows";
    StartPosition = FormStartPosition.CenterScreen;
    MinimumSize = new Size(980, 680);
    Size = new Size(1440, 900);
    AutoScaleMode = AutoScaleMode.Dpi;
    FormBorderStyle = FormBorderStyle.None;
    DoubleBuffered = true;
    ResizeRedraw = true;
    BackColor = Chrome;
    ForeColor = TextColor;
    Font = new Font("Segoe UI", 10F);
    BuildInterface();
    PrepareMainMenu();
    LoadPersistedPreviews();
    _tabHoverTimer.Tick += (_, _) =>
    {
      _tabHoverTimer.Stop();
      if (_pendingHoverTab is { IsDisposed: false } item) ShowTabHoverPreview(item);
    };
    _previewCaptureTimer.Tick += async (_, _) =>
    {
      _previewCaptureTimer.Stop();
      var tabId = _pendingPreviewTabId;
      var generation = _previewCaptureGeneration;
      if (!string.IsNullOrWhiteSpace(tabId)) await CaptureTabPreviewAsync(tabId, generation);
    };
    _overviewSnapshotTimer.Tick += (_, _) =>
    {
      _overviewSnapshotTimer.Stop();
      RefreshOverviewSnapshot();
    };
    _windowSnapshotTimer.Tick += (_, _) => RefreshWindowSnapshot();
    _resizeSettleTimer.Tick += async (_, _) =>
    {
      _resizeSettleTimer.Stop();
      await CompleteWindowLayoutTransitionAsync();
    };
    _autoSyncTimer.Tick += async (_, _) => await FlushAndAutoSyncAsync();
    GoogleDriveAuthorization.SessionChanged += OnDriveSessionChanged;
    Shown += async (_, _) =>
    {
      EnsureWorkspaceTab();
      RefreshWorkspaceUi();
      await ActivateCurrentTabAsync();
      _lastWindowState = WindowState;
      _lastClientSize = ClientSize;
      BeginInvoke(new Action(() =>
      {
        RefreshWindowSnapshot();
        if (_windowSnapshot is null) RefreshOverviewSnapshot();
        _windowSnapshotTimer.Start();
      }));
      if (_enableAutomaticSync)
      {
        _autoSyncTimer.Start();
        await FlushAndAutoSyncAsync();
      }
    };
    FormClosing += (_, _) =>
    {
      _closing = true;
      GoogleDriveAuthorization.SessionChanged -= OnDriveSessionChanged;
      _driveCancellation.Cancel();
      _autoSyncTimer.Stop();
      _tabHoverTimer.Stop();
      _previewCaptureTimer.Stop();
      _overviewSnapshotTimer.Stop();
      _windowSnapshotTimer.Stop();
      _resizeSettleTimer.Stop();
      _previewCaptureTimer.Dispose();
      _overviewSnapshotTimer.Dispose();
      _windowSnapshotTimer.Dispose();
      _resizeSettleTimer.Dispose();
      _tabHoverPreview?.Close();
      _tabHoverPreview?.Dispose();
      if (_bookshelfForm is { IsDisposed: false })
      {
        _bookshelfForm.Close();
        _bookshelfForm.Dispose();
        _bookshelfForm = null;
      }
      _mainMenu?.Dispose();
      _overviewTransitionSurface.Image = null;
      _overviewSnapshot?.Dispose();
      _overviewSnapshot = null;
      _windowSnapshot?.Dispose();
      _windowSnapshot = null;
      if (_stateDirty) BrowserStateStore.SaveChanged(_state);
      foreach (var view in _views.Values) view.Dispose();
      foreach (var preview in _previews.Values) preview.Dispose();
    };
    ResizeBegin += (_, _) => BeginWindowLayoutTransition();
    ResizeEnd += (_, _) => ScheduleWindowLayoutCompletion();
    Resize += (_, _) => HandleWindowResize();
    LocationChanged += (_, _) => UpdateMaximizedBounds();
  }

  protected override CreateParams CreateParams
  {
    get
    {
      const int WsClipChildren = 0x02000000;
      var parameters = base.CreateParams;
      parameters.Style |= WsClipChildren;
      return parameters;
    }
  }

  private void BuildInterface()
  {
    var root = new TableLayoutPanel
    {
      Dock = DockStyle.Fill,
      BackColor = Chrome,
      ColumnCount = 1,
      RowCount = 5,
      Margin = Padding.Empty,
      Padding = Padding.Empty,
    };
    _rootLayout = root;
    root.RowStyles.Add(new RowStyle(SizeType.Absolute, 46));
    root.RowStyles.Add(new RowStyle(SizeType.Absolute, _preferences.CompactToolbar ? 52 : 56));
    root.RowStyles.Add(new RowStyle(SizeType.Absolute, !string.IsNullOrEmpty(_preferences.ToolbarShortcuts) ? 50 : _preferences.ShowFavoritesBar ? 34 : 0));
    root.RowStyles.Add(new RowStyle(SizeType.Percent, 100));
    root.RowStyles.Add(new RowStyle(SizeType.Absolute, _preferences.ShowStatusBar ? 24 : 0));
    Controls.Add(root);

    root.Controls.Add(BuildTabChrome(), 0, 0);
    root.Controls.Add(BuildNavigationChrome(), 0, 1);
    root.Controls.Add(BuildFavoritesBar(), 0, 2);

    var surface = new Panel { Dock = DockStyle.Fill, BackColor = PageBackground, Margin = Padding.Empty };
    _browserHost.Dock = DockStyle.Fill;
    _browserHost.BackColor = PageBackground;
    _tabActivationPreview.Dock = DockStyle.Fill;
    _tabActivationPreview.BackColor = PageBackground;
    _tabActivationPreview.Visible = false;
    _tabActivationPreview.TabStop = false;
    _browserHost.Controls.Add(_tabActivationPreview);
    BuildStartPage();
    BuildTabOverview();
    surface.Controls.Add(_browserHost);
    surface.Controls.Add(_startPage);
    root.Controls.Add(surface, 0, 3);

    _statusText.Dock = DockStyle.Fill;
    _statusText.BackColor = Chrome;
    _statusText.ForeColor = MutedText;
    _statusText.Font = new Font("Segoe UI", 8.75F);
    _statusText.Padding = new Padding(14, 0, 0, 0);
    _statusText.TextAlign = ContentAlignment.MiddleLeft;
    _statusText.Click += async (_, _) =>
    {
      if (GoogleDriveAuthorization.NeedsReauthentication || _syncStatus.Contains("Drive", StringComparison.Ordinal) || _syncStatus.Contains("Google", StringComparison.Ordinal))
        await ShowSyncDialogAsync();
    };
    root.Controls.Add(_statusText, 0, 4);

    // Keep the overview alive and fully laid out behind the browsing chrome. Switching to it
    // is now only a z-order change; there is no first-visible layout/paint pause on click.
    Controls.Add(_tabOverview);
    _tabOverview.Dock = DockStyle.None;
    UpdateTabOverviewBounds();
    _tabOverview.Visible = true;
    _tabOverview.Enabled = false;
    _tabOverview.SendToBack();

    _windowTransitionSurface.Dock = DockStyle.Fill;
    _windowTransitionSurface.Visible = false;
    Controls.Add(_windowTransitionSurface);
    _windowTransitionSurface.BringToFront();
  }

  private void UpdateTabOverviewBounds()
  {
    const int titleBarHeight = 46;
    _tabOverview.Bounds = new Rectangle(
        0,
        titleBarHeight,
        Math.Max(0, ClientSize.Width),
        Math.Max(0, ClientSize.Height - titleBarHeight));
  }

  private Control BuildTabChrome()
  {
    var chrome = new Panel { Dock = DockStyle.Fill, BackColor = Chrome, Margin = Padding.Empty };

    var captions = new FlowLayoutPanel
    {
      Dock = DockStyle.Fill,
      BackColor = Chrome,
      FlowDirection = FlowDirection.LeftToRight,
      Margin = Padding.Empty,
      Padding = Padding.Empty,
      WrapContents = false,
    };
    var minimize = new WindowCaptionButton { Width = 46, Height = 46, Glyph = CaptionGlyph.Minimize };
    _maximizeButton = new WindowCaptionButton { Width = 46, Height = 46, Glyph = CaptionGlyph.Maximize };
    var close = new WindowCaptionButton { Width = 46, Height = 46, Glyph = CaptionGlyph.Close, IsCloseButton = true };
    minimize.Click += (_, _) => MinimizeWindow();
    _maximizeButton.Click += (_, _) => ToggleMaximizeWindow();
    close.Click += (_, _) => Close();
    captions.Controls.Add(minimize);
    captions.Controls.Add(_maximizeButton);
    captions.Controls.Add(close);

    var actions = new FlowLayoutPanel
    {
      Dock = DockStyle.Fill,
      BackColor = Chrome,
      FlowDirection = FlowDirection.LeftToRight,
      Padding = new Padding(2, 5, 4, 3),
      WrapContents = false,
    };
    actions.Controls.Add(IconButton(PillGlyph.Add, async (_, _) => await CreateTabAsync(), 36, "새 탭 (Ctrl+T)"));
    actions.Controls.Add(IconButton(PillGlyph.Tabs, async (_, _) => await ToggleOverviewAsync(), 36, "탭 개요 보기"));

    var rightHost = new TableLayoutPanel
    {
      Dock = DockStyle.Right,
      Width = 222,
      BackColor = Chrome,
      ColumnCount = 2,
      RowCount = 1,
      Margin = Padding.Empty,
      Padding = Padding.Empty,
    };
    rightHost.ColumnStyles.Add(new ColumnStyle(SizeType.Absolute, 84));
    rightHost.ColumnStyles.Add(new ColumnStyle(SizeType.Absolute, 138));
    rightHost.RowStyles.Add(new RowStyle(SizeType.Percent, 100));
    rightHost.Controls.Add(actions, 0, 0);
    rightHost.Controls.Add(captions, 1, 0);
    chrome.Controls.Add(rightHost);

    _tabStrip.Dock = DockStyle.Fill;
    _tabStrip.BackColor = Chrome;
    _tabStrip.FlowDirection = FlowDirection.LeftToRight;
    _tabStrip.WrapContents = false;
    _tabStrip.AutoScroll = false;
    _tabStrip.Padding = new Padding(10, 5, 2, 4);
    _tabStrip.SizeChanged += (_, _) => UpdateTabStripItemWidths();
    _tabStrip.MouseDown += (_, eventArgs) =>
    {
      if (eventArgs.Button == MouseButtons.Left) BeginWindowDrag();
    };
    _tabStrip.DoubleClick += (_, _) => ToggleMaximizeWindow();
    chrome.MouseDown += (_, eventArgs) =>
    {
      if (eventArgs.Button == MouseButtons.Left) BeginWindowDrag();
    };
    chrome.DoubleClick += (_, _) => ToggleMaximizeWindow();
    chrome.Controls.Add(_tabStrip);
    _tabStrip.BringToFront();
    return chrome;
  }

  private void BeginWindowDrag()
  {
    if (WindowState == FormWindowState.Maximized)
    {
      var cursorRatio = ClientSize.Width <= 0 ? 0.5F : Math.Clamp((float)PointToClient(Cursor.Position).X / ClientSize.Width, 0.08F, 0.92F);
      var restoreBounds = RestoreBounds;
      WindowState = FormWindowState.Normal;
      Location = new Point(
          Cursor.Position.X - (int)Math.Round(restoreBounds.Width * cursorRatio),
          Cursor.Position.Y - 18);
    }
    ReleaseCapture();
    SendMessage(Handle, WmNcLButtonDown, HtCaption, IntPtr.Zero);
  }

  private void MinimizeWindow()
  {
    RefreshWindowSnapshot();
    _windowTransitionSurface.HideFrame();
    WindowState = FormWindowState.Minimized;
  }

  private void ToggleMaximizeWindow()
  {
    if (_fullScreen) return;
    BeginWindowLayoutTransition();
    UpdateMaximizedBounds();
    WindowState = WindowState == FormWindowState.Maximized
        ? FormWindowState.Normal
        : FormWindowState.Maximized;
    UpdateMaximizeButton();
    ScheduleWindowLayoutCompletion();
  }

  private void UpdateMaximizeButton()
  {
    if (_maximizeButton is null || _maximizeButton.IsDisposed) return;
    _maximizeButton.Glyph = WindowState == FormWindowState.Maximized
        ? CaptionGlyph.Restore
        : CaptionGlyph.Maximize;
    _maximizeButton.AccessibleName = WindowState == FormWindowState.Maximized ? "이전 크기로 복원" : "최대화";
    _maximizeButton.Invalidate();
  }

  private void UpdateMaximizedBounds()
  {
    if (!IsHandleCreated || _fullScreen) return;
    MaximizedBounds = Screen.FromHandle(Handle).WorkingArea;
  }

  private void HandleWindowResize()
  {
    if (_closing) return;
    UpdateTabOverviewBounds();
    UpdateMaximizeButton();
    if (WindowState == FormWindowState.Minimized)
    {
      _lastWindowState = WindowState;
      return;
    }

    var stateChanged = _lastWindowState != WindowState;
    var sizeChanged = _lastClientSize != ClientSize;
    if (_lastWindowState == FormWindowState.Minimized)
      BeginWindowLayoutTransition(preferCachedFrame: true);
    else if ((stateChanged || sizeChanged) && !_handlingWindowTransition)
      BeginWindowLayoutTransition(preferCachedFrame: true);

    _lastWindowState = WindowState;
    _lastClientSize = ClientSize;
    if (stateChanged || sizeChanged || _handlingWindowTransition) ScheduleWindowLayoutCompletion();
  }

  private void BeginWindowLayoutTransition(bool preferCachedFrame = false)
  {
    if (_closing || WindowState == FormWindowState.Minimized || ClientSize.Width < 2 || ClientSize.Height < 2) return;
    RefreshWindowSnapshot();
  }

  private void ScheduleWindowLayoutCompletion()
  {
    if (_closing || WindowState == FormWindowState.Minimized) return;
    _resizeSettleTimer.Stop();
    _resizeSettleTimer.Start();
  }

  private async Task CompleteWindowLayoutTransitionAsync()
  {
    if (_closing || WindowState == FormWindowState.Minimized) return;
    _rootLayout.PerformLayout();
    _browserHost.PerformLayout();
    if (_state.ActiveTabId is not null && _views.TryGetValue(_state.ActiveTabId, out var activeView))
    {
      activeView.Bounds = _browserHost.ClientRectangle;
      activeView.BringToFront();
    }
    _windowTransitionSurface.HideFrame();
    _handlingWindowTransition = false;
    RefreshWindowSnapshot();
    await Task.CompletedTask;
  }

  private async Task AnimateSurfaceTransitionAwayAsync(int duration = 150)
  {
    _windowTransitionSurface.HideFrame();
    _handlingWindowTransition = false;
    RefreshWindowSnapshot();
    await Task.CompletedTask;
  }

  private static async Task NextUiFramesAsync(int count)
  {
    for (var index = 0; index < count; index++)
    {
      await Task.Yield();
      await Task.Delay(16);
    }
  }

  private Bitmap? CloneWindowSnapshot()
  {
    try
    {
      return _windowSnapshot is { Width: > 0, Height: > 0 } snapshot ? new Bitmap(snapshot) : null;
    }
    catch
    {
      return null;
    }
  }

  private Bitmap? TakeWindowSnapshot()
  {
    var snapshot = _windowSnapshot;
    _windowSnapshot = null;
    return snapshot;
  }

  private Bitmap? CaptureClientFrame()
  {
    if (_closing || !Visible || !IsHandleCreated || WindowState == FormWindowState.Minimized ||
        ClientSize.Width < 2 || ClientSize.Height < 2) return null;
    try
    {
      var screenBounds = RectangleToScreen(ClientRectangle);
      var frame = new Bitmap(ClientSize.Width, ClientSize.Height, PixelFormat.Format32bppPArgb);
      using var graphics = Graphics.FromImage(frame);
      graphics.CopyFromScreen(screenBounds.Location, Point.Empty, ClientSize, CopyPixelOperation.SourceCopy);
      return frame;
    }
    catch
    {
      return null;
    }
  }

  private void RefreshWindowSnapshot()
  {
    if (_closing || _handlingWindowTransition || _windowTransitionSurface.Visible ||
        _mainMenu?.Visible == true || WindowState == FormWindowState.Minimized || !ContainsFocus) return;
    var next = CaptureClientFrame();
    if (next is null) return;
    var previous = _windowSnapshot;
    _windowSnapshot = next;
    previous?.Dispose();
  }

  protected override void WndProc(ref Message message)
  {
    const int wmNcHitTest = 0x0084;
    if (message.Msg == wmNcHitTest && WindowState == FormWindowState.Normal)
    {
      base.WndProc(ref message);
      if ((int)message.Result == 1)
      {
        const int grip = 7;
        var point = PointToClient(Cursor.Position);
        var left = point.X <= grip;
        var right = point.X >= ClientSize.Width - grip;
        var top = point.Y <= grip;
        var bottom = point.Y >= ClientSize.Height - grip;
        message.Result = (IntPtr)(top && left ? 13 : top && right ? 14 : bottom && left ? 16 : bottom && right ? 17 :
            left ? 10 : right ? 11 : top ? 12 : bottom ? 15 : 1);
      }
      return;
    }
    base.WndProc(ref message);
  }

  private const int WmNcLButtonDown = 0x00A1;
  private static readonly IntPtr HtCaption = (IntPtr)2;

  [DllImport("user32.dll")]
  private static extern bool ReleaseCapture();

  [DllImport("user32.dll")]
  private static extern IntPtr SendMessage(IntPtr windowHandle, int message, IntPtr wParam, IntPtr lParam);

  private Control BuildNavigationChrome()
  {
    var navigation = new TableLayoutPanel
    {
      Dock = DockStyle.Fill,
      BackColor = Chrome,
      ColumnCount = 3,
      RowCount = 1,
      Margin = Padding.Empty,
      Padding = new Padding(12, 0, 12, 0),
    };
    navigation.ColumnStyles.Add(new ColumnStyle(SizeType.AutoSize));
    navigation.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 100));
    navigation.ColumnStyles.Add(new ColumnStyle(SizeType.AutoSize));
    navigation.RowStyles.Add(new RowStyle(SizeType.Percent, 100F));

    var history = new FlowLayoutPanel
    {
      Dock = DockStyle.Fill,
      BackColor = Chrome,
      WrapContents = false,
      AutoSize = true,
      AutoSizeMode = AutoSizeMode.GrowAndShrink,
      Margin = Padding.Empty,
      Padding = new Padding(0, _preferences.CompactToolbar ? 8 : 10, 4, 0),
    };
    _backButton = IconButton(PillGlyph.Back, async (_, _) => await ActiveViewActionAsync(v => v.GoBack(), v => v.CanGoBack), 36, "뒤로");
    _forwardButton = IconButton(PillGlyph.Forward, async (_, _) => await ActiveViewActionAsync(v => v.GoForward(), v => v.CanGoForward), 36, "앞으로");
    var overviewToggle = IconButton(PillGlyph.Tabs, async (_, _) => await ToggleOverviewAsync(), 36, "탭 개요 (Safari 스타일)");

    _toolbarProfileButton = new PillButton
    {
      Width = 96,
      Height = 34,
      BackColor = ChromeRaised,
      HoverBackColor = Color.FromArgb(58, 58, 64),
      ForeColor = TextColor,
      CornerRadius = 10,
      Font = new Font("Segoe UI", 9F, FontStyle.Bold),
      BorderColor = Outline,
      BorderWidth = 1F,
      Margin = new Padding(4, 1, 2, 0),
      Padding = new Padding(4, 0, 6, 0),
      AccessibleName = "프로필 전환",
    };
    new ToolTip().SetToolTip(_toolbarProfileButton, "활성 브라우징 프로필 전환");
    _toolbarProfileButton.Click += (_, _) => ShowOverviewProfileMenu(_toolbarProfileButton);

    _backButton.LongPress += (_, _) => ShowToolbarEditor();
    _backButton.RightClick += (_, _) => ShowToolbarEditor();
    _forwardButton.LongPress += (_, _) => ShowToolbarEditor();
    _forwardButton.RightClick += (_, _) => ShowToolbarEditor();
    overviewToggle.LongPress += (_, _) => ShowToolbarEditor();
    overviewToggle.RightClick += (_, _) => ShowToolbarEditor();
    history.Controls.Add(_backButton);
    history.Controls.Add(_forwardButton);
    history.Controls.Add(overviewToggle);
    history.Controls.Add(_toolbarProfileButton);
    navigation.Controls.Add(history, 0, 0);

    var addressHost = new RoundedPanel
    {
      Dock = DockStyle.None,
      Height = 36,
      BackColor = Field,
      BorderColor = Outline,
      BorderWidth = 1F,
      CornerRadius = 11,
      Margin = Padding.Empty,
      Padding = new Padding(6, 2, 6, 2),
      ClipContent = true,
    };
    _addressHost = addressHost;
    var addressLayout = new TableLayoutPanel
    {
      Dock = DockStyle.None,
      BackColor = Color.Transparent,
      ColumnCount = 5,
      RowCount = 1,
      Margin = Padding.Empty,
      Padding = Padding.Empty,
    };
    addressLayout.ColumnStyles.Add(new ColumnStyle(SizeType.Absolute, 30));
    addressLayout.ColumnStyles.Add(new ColumnStyle(SizeType.Absolute, 22));
    addressLayout.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 100));
    addressLayout.ColumnStyles.Add(new ColumnStyle(SizeType.Absolute, 30));
    addressLayout.ColumnStyles.Add(new ColumnStyle(SizeType.Absolute, 30));
    addressLayout.RowStyles.Add(new RowStyle(SizeType.Percent, 100));

    var pageToolsButton = new PillButton
    {
      Width = 28,
      Height = 28,
      Anchor = AnchorStyles.None,
      CornerRadius = 7,
      Glyph = PillGlyph.Extension,
      BackColor = Color.Transparent,
      HoverBackColor = Color.FromArgb(46, 46, 52),
      ForeColor = MutedText,
      BorderWidth = 0F,
      Margin = Padding.Empty,
      AccessibleName = "페이지 도구 및 확장 기능",
    };
    new ToolTip().SetToolTip(pageToolsButton, "페이지 도구 및 확장 기능");
    pageToolsButton.Click += (_, _) => ShowPageToolsMenu(pageToolsButton);
    pageToolsButton.LongPress += (_, _) => ShowPageToolsMenu(pageToolsButton);
    pageToolsButton.RightClick += (_, _) => ShowPageToolsMenu(pageToolsButton);
    addressLayout.Controls.Add(pageToolsButton, 0, 0);

    _securityBadge = new PillButton
    {
      Width = 20,
      Height = 28,
      Anchor = AnchorStyles.None,
      CornerRadius = 6,
      Glyph = PillGlyph.Lock,
      BackColor = Color.Transparent,
      HoverBackColor = Color.Transparent,
      ForeColor = MutedText,
      BorderWidth = 0F,
      Margin = Padding.Empty,
      Cursor = Cursors.Default,
      AccessibleName = "보안 연결 상태",
    };
    new ToolTip().SetToolTip(_securityBadge, "HTTPS 암호화 연결 · 추적기 차단 활성");
    addressLayout.Controls.Add(_securityBadge, 1, 0);

    ConfigureTextBox(_address);
    _address.AutoSize = false;
    _address.Dock = DockStyle.None;
    _address.Anchor = AnchorStyles.None;
    _address.PlaceholderText = "검색 또는 웹사이트 이름 입력";
    _address.TextAlign = HorizontalAlignment.Left;
    _address.Margin = Padding.Empty;
    _address.KeyDown += async (_, eventArgs) =>
    {
      if (eventArgs.KeyCode != Keys.Enter) return;
      eventArgs.SuppressKeyPress = true;
      await NavigateFromAddressAsync();
    };
    _address.Enter += (_, _) =>
    {
      _address.TextAlign = HorizontalAlignment.Left;
      _addressHost.BorderColor = Accent;
      _addressHost.BorderWidth = 1.6F;
      _addressHost.Invalidate();
      var tab = _state.Tabs.FirstOrDefault(item => item.Id == _state.ActiveTabId);
      if (tab?.Url is { } url && url != "about:blank") _address.Text = url;
    };
    _address.Leave += (_, _) =>
    {
      _address.TextAlign = HorizontalAlignment.Left;
      _addressHost.BorderColor = Outline;
      _addressHost.BorderWidth = 1F;
      _addressHost.Invalidate();
      var tab = _state.Tabs.FirstOrDefault(item => item.Id == _state.ActiveTabId);
      _address.Text = DisplayAddress(tab?.Url ?? "about:blank");
    };
    var addressTextCell = new Panel
    {
      Dock = DockStyle.Fill,
      BackColor = Field,
      Margin = Padding.Empty,
      Padding = Padding.Empty,
    };
    addressTextCell.Controls.Add(_address);
    addressTextCell.MouseDown += (_, _) => _address.Focus();
    void LayoutAddressText()
    {
      var measuredHeight = TextRenderer.MeasureText(
          "Ag",
          _address.Font,
          new Size(int.MaxValue, int.MaxValue),
          TextFormatFlags.NoPadding | TextFormatFlags.SingleLine).Height;
      var naturalHeight = Math.Clamp(measuredHeight + 3, 18, Math.Max(18, addressTextCell.ClientSize.Height));
      var centeredTop = Math.Max(0, (addressTextCell.ClientSize.Height - naturalHeight) / 2);
      var opticalTop = Math.Min(
          Math.Max(0, addressTextCell.ClientSize.Height - naturalHeight),
          centeredTop + 1);
      _address.SetBounds(
          4,
          opticalTop,
          Math.Max(0, addressTextCell.ClientSize.Width - 8),
          naturalHeight);
    }
    addressTextCell.SizeChanged += (_, _) => LayoutAddressText();
    addressTextCell.HandleCreated += (_, _) => LayoutAddressText();
    addressTextCell.Layout += (_, _) => LayoutAddressText();
    addressLayout.Controls.Add(addressTextCell, 2, 0);

    _bookmarkStarButton = new PillButton
    {
      Width = 28,
      Height = 28,
      Anchor = AnchorStyles.None,
      CornerRadius = 7,
      Glyph = PillGlyph.Star,
      BackColor = Color.Transparent,
      HoverBackColor = Color.FromArgb(46, 46, 52),
      ForeColor = MutedText,
      BorderWidth = 0F,
      Margin = Padding.Empty,
      AccessibleName = "현재 페이지 북마크 추가/관리",
    };
    new ToolTip().SetToolTip(_bookmarkStarButton, "북마크에 추가 (우클릭: 북마크 관리자)");
    _bookmarkStarButton.Click += async (_, _) => await ToggleCurrentPageBookmarkAsync();
    _bookmarkStarButton.RightClick += async (_, _) => await ShowBookmarkManagerAsync();
    addressLayout.Controls.Add(_bookmarkStarButton, 3, 0);

    var reloadInPill = new PillButton
    {
      Width = 28,
      Height = 28,
      Anchor = AnchorStyles.None,
      CornerRadius = 7,
      Glyph = PillGlyph.Reload,
      BackColor = Color.Transparent,
      HoverBackColor = Color.FromArgb(46, 46, 52),
      ForeColor = MutedText,
      BorderWidth = 0F,
      Margin = Padding.Empty,
      AccessibleName = "새로고침 (Ctrl+R)",
    };
    new ToolTip().SetToolTip(reloadInPill, "새로고침 (Ctrl+R)");
    reloadInPill.Click += async (_, _) => await ActiveViewActionAsync(v => v.Reload());
    reloadInPill.LongPress += (_, _) => ShowToolbarEditor();
    reloadInPill.RightClick += (_, _) => ShowToolbarEditor();
    addressLayout.Controls.Add(reloadInPill, 4, 0);

    addressHost.Controls.Add(addressLayout);
    void LayoutAddressContents()
    {
      const int horizontalInset = 6;
      const int verticalInset = 3;
      addressLayout.Bounds = new Rectangle(
          horizontalInset,
          verticalInset,
          Math.Max(0, addressHost.ClientSize.Width - horizontalInset * 2),
          Math.Max(0, addressHost.ClientSize.Height - verticalInset * 2));
      addressLayout.PerformLayout();
      LayoutAddressText();
    }
    addressHost.SizeChanged += (_, _) => LayoutAddressContents();
    addressHost.Layout += (_, _) => LayoutAddressContents();
    addressHost.HandleCreated += (_, _) =>
    {
      LayoutAddressContents();
      addressHost.BeginInvoke(new Action(LayoutAddressContents));
    };
    var addressCell = new Panel
    {
      Dock = DockStyle.Fill,
      Size = new Size(400, 56),
      BackColor = Chrome,
      Margin = Padding.Empty,
      Padding = Padding.Empty,
    };
    void LayoutSmartSearchPill()
    {
      var rowHeight = _preferences.CompactToolbar ? 52 : 56;
      var cellHeight = addressCell.ClientSize.Height > 0 && addressCell.ClientSize.Height <= 64
          ? addressCell.ClientSize.Height
          : rowHeight;
      const int left = 6;
      const int rightMargin = 8;
      var pillWidth = Math.Max(240, addressCell.ClientSize.Width - left - rightMargin);
      const int pillHeight = 36;
      var top = Math.Max(6, (cellHeight - pillHeight) / 2);
      addressHost.SetBounds(left, top, pillWidth, pillHeight);
    }
    addressCell.SizeChanged += (_, _) => LayoutSmartSearchPill();
    addressCell.Layout += (_, _) => LayoutSmartSearchPill();
    navigation.SizeChanged += (_, _) => LayoutSmartSearchPill();
    addressCell.Controls.Add(addressHost);
    LayoutSmartSearchPill();
    navigation.Controls.Add(addressCell, 1, 0);

    _toolbarWorkspace = new FlowLayoutPanel
    {
      Dock = DockStyle.Fill,
      BackColor = Chrome,
      FlowDirection = FlowDirection.RightToLeft,
      WrapContents = false,
      AutoSize = true,
      AutoSizeMode = AutoSizeMode.GrowAndShrink,
      Margin = Padding.Empty,
      Padding = new Padding(0, _preferences.CompactToolbar ? 8 : 10, 0, 0),
    };
    PopulateToolbarWorkspace();
    navigation.Controls.Add(_toolbarWorkspace, 2, 0);
    return navigation;
  }

  private void PopulateToolbarWorkspace()
  {
    if (_toolbarWorkspace.IsDisposed) return;
    _toolbarWorkspace.SuspendLayout();
    _toolbarWorkspace.Controls.Clear();

    var raw = _preferences.DesktopToolbarButtons;
    var buttonIds = (raw ?? "sync,watermark,capture,audio,bookshelf,menu")
        .Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
    if (buttonIds.Length == 0)
    {
      buttonIds = new[] { "sync", "watermark", "capture", "audio", "bookshelf", "menu" };
    }

    // In FlowDirection.RightToLeft, controls added first end up on the far right.
    // To match list order (first item closest to address bar, last item at far right),
    // we add the items in reverse order.
    for (int i = buttonIds.Length - 1; i >= 0; i--)
    {
      var button = CreateToolbarButton(buttonIds[i]);
      if (button != null)
      {
        _toolbarWorkspace.Controls.Add(button);
      }
    }

    _toolbarWorkspace.ResumeLayout(true);
  }

  private void ShowToolbarEditor()
  {
    using var dialog = new DesktopToolbarEditorDialog(
        _preferences.DesktopToolbarButtons,
        newButtons =>
        {
          _preferences.DesktopToolbarButtons = newButtons;
          DesktopPreferencesStore.Save(_preferences);
          PopulateToolbarWorkspace();
        });
    dialog.ShowDialog(this);
  }

  private PillButton? CreateToolbarButton(string id)
  {
    PillButton button = null!;
    switch (id.Trim().ToLowerInvariant())
    {
      case "menu":
        button = IconButton(PillGlyph.More, (_, _) => ShowMainMenu(button), 36, "메뉴");
        break;
      case "sync":
        button = IconButton(PillGlyph.Sync, async (_, _) => await ShowSyncDialogAsync(), 36, "Android/Windows 동기화");
        break;
      case "watermark":
        button = IconButton(PillGlyph.Watermark, async (_, _) => await RemoveWatermarkFromActiveTabAsync(), 36, "현재 화면 캡처 후 워터마크 삭제");
        break;
      case "capture":
        button = IconButton(PillGlyph.Capture, (_, _) => ShowCaptureMenu(button), 36, "화면 캡처");
        break;
      case "audio":
        button = IconButton(PillGlyph.Music, async (_, _) => await ShowAudioOutputMenuAsync(button), 36, "소리 출력 장치");
        break;
      case "newtab":
        button = IconButton(PillGlyph.Add, async (_, _) => await CreateTabAsync(), 36, "새 탭");
        break;
      case "tabs":
        button = IconButton(PillGlyph.Tabs, async (_, _) => await ToggleOverviewAsync(), 36, "탭 목록");
        break;
      case "bookmarks":
        button = IconButton(PillGlyph.Bookmarks, async (_, _) => await ShowBookmarkManagerAsync(), 36, "북마크 관리자");
        break;
      case "extensions":
        button = IconButton(PillGlyph.Extension, async (_, _) => await ShowExtensionManagerAsync(), 36, "확장 프로그램");
        break;
      case "bookshelf":
        button = IconButton(PillGlyph.Bookshelf, (_, _) => ShowBookshelf(), 36, "책장");
        break;
      case "downloads":
        button = IconButton(PillGlyph.Downloads, (_, _) => OpenDownloads(), 36, "다운로드");
        break;
      case "print":
        button = IconButton(PillGlyph.Print, (_, _) => PrintActivePage(), 36, "웹페이지 인쇄");
        break;
      case "settings":
        button = IconButton(PillGlyph.Settings, async (_, _) => await ShowSettingsDialogAsync(), 36, "설정");
        break;
      default:
        return null;
    }
    button.LongPress += (_, _) => ShowToolbarEditor();
    button.RightClick += (_, _) => ShowToolbarEditor();
    return button;
  }

  private async Task ToggleOverviewAsync()
  {
    if (_showingOverview) await ShowBrowsingAsync();
    else await ShowTabOverviewAsync();
  }

  private async void ShowPageToolsMenu(Control anchor)
  {
    var menu = CreateAndroidMenu();
    menu.MinimumSize = new Size(280, 0);

    var tab = _state.Tabs.FirstOrDefault(item => item.Id == _state.ActiveTabId);
    var currentUrl = tab?.Url ?? string.Empty;
    var host = currentUrl is { Length: > 0 } ? HostName(currentUrl) : "현재 페이지";

    var header = menu.Items.Add($"페이지 도구 ({host})");
    header.Enabled = false;
    menu.Items.Add(new ToolStripSeparator());

    if (ExtensionManagerForm.IsSupportedStoreOrPackageUrl(currentUrl))
    {
      var installHere = menu.Items.Add("＋ 현재 스토어 페이지 확장 프로그램 설치", CreateMenuGlyph("🧩", Accent));
      installHere.Click += async (_, _) => await InstallExtensionFromStoreUrlAsync(currentUrl);
      menu.Items.Add(new ToolStripSeparator());
    }

    var printItem = menu.Items.Add("페이지 인쇄 (Ctrl+P)", CreateMenuGlyph("🖨", TextColor));
    printItem.Click += (_, _) => PrintActivePage();

    var findItem = menu.Items.Add("페이지 내 찾기 (Ctrl+F)", CreateMenuGlyph("🔍", TextColor));
    findItem.Click += (_, _) =>
    {
      if (_state.ActiveTabId is not null && _views.TryGetValue(_state.ActiveTabId, out var view))
      {
        view.Focus();
        SendKeys.Send("^f");
      }
    };

    var devToolsItem = menu.Items.Add("개발자 도구 (F12)", CreateMenuGlyph("🛠", TextColor));
    devToolsItem.Click += (_, _) => OpenDeveloperTools();

    menu.Items.Add(new ToolStripSeparator());

    if (_state.ActiveTabId is not null &&
        _views.TryGetValue(_state.ActiveTabId, out var activeView) &&
        activeView.CoreWebView2 is not null)
    {
      try
      {
        var installed = await activeView.CoreWebView2.Profile.GetBrowserExtensionsAsync();
        var enabledExtensions = installed
            .Where(ext => ext.IsEnabled)
            .OrderBy(ext => ext.Name, StringComparer.CurrentCultureIgnoreCase)
            .ToList();
        if (enabledExtensions.Count > 0)
        {
          var extHeader = menu.Items.Add($"설치된 확장 프로그램 ({enabledExtensions.Count})");
          extHeader.Enabled = false;
          foreach (var ext in enabledExtensions)
          {
            var endpoints = ExtensionManagerForm.GetExtensionEndpoints(_state.ActiveProfileId, ext.Id);
            var targetUrl = endpoints.PopupUrl ?? endpoints.OptionsUrl;
            var label = targetUrl is not null ? ext.Name : $"{ext.Name} (자동 실행 중)";
            var item = menu.Items.Add(label, CreateMenuGlyph("🧩", TextColor));
            item.Click += async (_, _) =>
            {
              if (targetUrl is not null)
                await CreateTabAsync(targetUrl);
              else
                await ShowExtensionManagerAsync();
            };
          }
          menu.Items.Add(new ToolStripSeparator());
        }
      }
      catch
      {
        // Ignore extension query errors while building the quick menu.
      }
    }

    var chromeStore = menu.Items.Add("Chrome 웹 스토어 열기", CreateMenuGlyph("🌐", TextColor));
    chromeStore.Click += async (_, _) => await CreateTabAsync("https://chromewebstore.google.com/");

    var firefoxStore = menu.Items.Add("Firefox 부가기능 스토어 열기", CreateMenuGlyph("🦊", TextColor));
    firefoxStore.Click += async (_, _) => await CreateTabAsync("https://addons.mozilla.org/ko/firefox/extensions/");

    var extensions = menu.Items.Add("확장 프로그램 관리...", CreateMenuGlyph("🧩", Accent));
    extensions.Click += async (_, _) => await ShowExtensionManagerAsync();

    DisposeMenuAfterClose(menu);
    menu.Show(anchor, new Point(0, anchor.Height + 6));
  }

  private async Task ShowExtensionManagerAsync()
  {
    if (_state.ActiveTabId is null) return;
    if (!_views.TryGetValue(_state.ActiveTabId, out var view) || view.CoreWebView2 is null)
    {
      await ActivateCurrentTabAsync();
      if (!_views.TryGetValue(_state.ActiveTabId, out view) || view.CoreWebView2 is null) return;
    }
    var profile = view.CoreWebView2.Profile;
    using var dialog = new ExtensionManagerForm(
        profile,
        _state.ActiveProfileId,
        ActiveProfileName(),
        openUrlInTab: url => CreateTabAsync(url),
        initialStoreUrl: view.Source?.ToString());
    dialog.ShowDialog(this);
  }

  private async Task InstallExtensionFromStoreUrlAsync(string storeOrPackageUrl, WebView2? sourceView = null)
  {
    if (_state.ActiveTabId is null) return;
    var view = sourceView;
    if (view?.CoreWebView2 is null)
    {
      if (!_views.TryGetValue(_state.ActiveTabId, out view) || view.CoreWebView2 is null)
      {
        await ActivateCurrentTabAsync();
        if (!_views.TryGetValue(_state.ActiveTabId, out view) || view.CoreWebView2 is null) return;
      }
    }
    try
    {
      _syncStatus = "확장 프로그램 다운로드 및 설치 준비 중...";
      UpdateStatusText();
      var installed = await ExtensionManagerForm.InstallFromStoreOrUrlAsync(
          view.CoreWebView2.Profile,
          _state.ActiveProfileId,
          storeOrPackageUrl,
          progress =>
          {
            _syncStatus = progress;
            UpdateStatusText();
          });
      _syncStatus = $"확장 프로그램 '{installed.Name}' 설치 완료";
      UpdateStatusText();
      if (view.CoreWebView2 is not null)
      {
        var nameJson = JsonSerializer.Serialize(installed.Name);
        _ = view.CoreWebView2.ExecuteScriptAsync($"window.__omyaruMarkExtensionInstalled?.({nameJson});");
      }
      MessageBox.Show(
          this,
          $"'{installed.Name}' 확장 프로그램이 현재 프로필에 설치되었습니다.\n주소창 왼쪽 퍼즐(🧩) 아이콘에서 확인하거나 실행할 수 있습니다.",
          "확장 프로그램 설치 완료",
          MessageBoxButtons.OK,
          MessageBoxIcon.Information);
    }
    catch (Exception error)
    {
      _syncStatus = "확장 프로그램 설치 실패 · " + error.Message;
      UpdateStatusText();
      if (view.CoreWebView2 is not null)
      {
        var errJson = JsonSerializer.Serialize(error.Message);
        _ = view.CoreWebView2.ExecuteScriptAsync($"window.__omyaruMarkExtensionFailed?.({errJson});");
      }
      MessageBox.Show(
          this,
          "확장 프로그램을 설치하지 못했습니다.\n\n" + error.Message,
          "확장 프로그램 설치 실패",
          MessageBoxButtons.OK,
          MessageBoxIcon.Warning);
    }
  }

  private Control BuildFavoritesBar()
  {
    _favoritesBar = new FlowLayoutPanel
    {
      Dock = DockStyle.Fill,
      BackColor = Chrome,
      FlowDirection = FlowDirection.LeftToRight,
      WrapContents = false,
      Padding = new Padding(12, 2, 6, 2),
      AutoScroll = !string.IsNullOrEmpty(_preferences.ToolbarShortcuts),
      Margin = Padding.Empty,
    };
    _favoritesBar.Paint += (_, eventArgs) =>
    {
      using var pen = new Pen(Color.FromArgb(44, 44, 50), 1F);
      eventArgs.Graphics.DrawLine(pen, 0, _favoritesBar.ClientSize.Height - 1, _favoritesBar.ClientSize.Width, _favoritesBar.ClientSize.Height - 1);
    };
    _favoritesBar.MouseUp += (_, eventArgs) =>
    {
      if (eventArgs.Button == MouseButtons.Right)
        ShowFavoritesContextMenu(_favoritesBar, eventArgs.Location);
    };
    PopulateFavoritesBar();
    return _favoritesBar;
  }

  private void PopulateFavoritesBar()
  {
    if (_favoritesBar.IsDisposed) return;
    _favoritesBar.SuspendLayout();
    foreach (Control control in _favoritesBar.Controls.Cast<Control>().ToArray()) control.Dispose();
    _favoritesBar.Controls.Clear();
    _favoritesBar.AutoScroll = !string.IsNullOrEmpty(_preferences.ToolbarShortcuts);
    foreach (var label in DesktopToolbarShortcuts.Labels.Where(label => _preferences.ToolbarShortcuts.Split(',').Contains(label)))
    {
      var shortcut = ChromeButton(label, async (_, _) =>
      {
        switch (label)
        {
          case "새 탭": await CreateTabAsync(); break;
          case "탭 목록": await ShowTabOverviewAsync(); break;
          case "동기화": await ShowSyncDialogAsync(); break;
          case "다운로드": OpenDownloads(); break;
          case "북마크": await ShowBookmarkManagerAsync(); break;
          case "화면 캡처": await CapturePreviewFromMenuAsync(); break;
          case "책장": ShowBookshelf(); break;
          case "인쇄": PrintActivePage(); break;
          case "현재 링크 복사": CopyActiveAddress(); break;
          case "설정": await ShowSettingsDialogAsync(); break;
        }
      }, Math.Max(72, label.Length * 13 + 18), label);
      shortcut.Height = 26;
      shortcut.CornerRadius = 8;
      shortcut.BackColor = ChromeRaised;
      shortcut.BorderWidth = 0F;
      _favoritesBar.Controls.Add(shortcut);
    }
    foreach (var bookmark in _bookmarks.Items.Where(_ => _preferences.ShowFavoritesBar))
    {
      var contextGesture = false;
      var showName = !_preferences.BookmarkIconsOnly;
      using var measureFont = new Font("Segoe UI", 8.5F, FontStyle.Regular);
      var width = showName
          ? Math.Clamp(TextRenderer.MeasureText(bookmark.Name, measureFont, Size.Empty,
              TextFormatFlags.NoPadding | TextFormatFlags.SingleLine).Width + 44, 78, 168)
          : 32;
      var tooltip = bookmark.Name + "\n" + HostName(bookmark.Url);
      var button = ChromeButton(showName ? bookmark.Name : string.Empty, (_, _) => { }, width, tooltip);
      button.Image = BookmarkFaviconStore.LoadOrCreate(bookmark, 18);
      button.Font = new Font("Segoe UI", 8.5F, FontStyle.Regular);
      button.Height = 26;
      button.CornerRadius = 7;
      button.BackColor = Chrome;
      button.HoverBackColor = Color.FromArgb(48, 48, 54);
      button.Padding = showName ? new Padding(4, 0, 4, 0) : Padding.Empty;
      button.ForeColor = Color.FromArgb(220, 220, 226);
      button.BorderColor = Color.Transparent;
      button.BorderWidth = 0F;
      button.MouseDown += (_, eventArgs) => contextGesture = eventArgs.Button == MouseButtons.Right;
      button.Click += async (_, _) =>
      {
        if (!contextGesture) await NavigateToAsync(bookmark.Url);
      };
      button.MouseUp += (_, eventArgs) =>
      {
        if (eventArgs.Button == MouseButtons.Right)
        {
          ShowFavoritesContextMenu(button, eventArgs.Location, bookmark);
          BeginInvoke(new Action(() => contextGesture = false));
        }
      };
      _favoritesBar.Controls.Add(button);
    }
    _favoritesBar.ResumeLayout(true);
  }

  private void ShowFavoritesContextMenu(Control anchor, Point location, BookmarkEntry? selected = null)
  {
    var count = _bookmarks.Items.Count;
    var menu = CreateAndroidMenu();
    menu.ShowImageMargin = false;
    menu.MinimumSize = new Size(330, 0);

    AddCommand($"{count}개 모두 열기(O)", async () => await OpenAllFavoritesAsync());
    AddCommand($"새 창에서 {count}개 모두 열기(N)", () => ShowUnavailableAsync(
        "새 창", "새 창 분리는 데스크톱 다중 창 단계에서 연결됩니다."));
    AddCommand($"시크릿 창에서 URL {count}개 모두 열기", () => ShowUnavailableAsync(
        "시크릿 창", "시크릿 창은 데스크톱 개인정보 보호 모드와 함께 연결됩니다."));
    AddCommand($"새 탭 그룹에서 {count}개 모두 열기", async () => await OpenAllFavoritesAsync(createGroup: true));
    menu.Items.Add(new ToolStripSeparator());
    if (selected is not null)
    {
      AddCommand("이름·주소 수정...", () => EditBookmarkAsync(selected.Id));
      AddCommand("삭제", () => DeleteBookmarkAsync(selected.Id));
      menu.Items.Add(new ToolStripSeparator());
    }
    AddCommand("현재 페이지 추가...", AddCurrentPageBookmarkAsync);
    menu.Items.Add(new ToolStripSeparator());
    AddCommand("북마크 관리자 열기", ShowBookmarkManagerAsync);
    AddCommand("북마크바 설정", ShowSettingsDialogAsync);

    DisposeMenuAfterClose(menu);
    menu.Show(anchor, location);

    void AddCommand(string text, Func<Task> action)
    {
      var item = menu.Items.Add(text);
      item.Click += async (_, _) => await action();
    }

    Task ShowUnavailableAsync(string title, string message)
    {
      ShowFeatureNotice(title, message);
      return Task.CompletedTask;
    }
  }

  private async Task ToggleCurrentPageBookmarkAsync()
  {
    var tab = _state.Tabs.FirstOrDefault(item => item.Id == _state.ActiveTabId);
    if (tab is null || tab.Url == "about:blank" || !BookmarkStore.TryNormalizeUrl(tab.Url, out var url))
    {
      await ShowBookmarkManagerAsync();
      return;
    }
    var existing = _bookmarks.Items.FirstOrDefault(item => item.Url.Equals(url, StringComparison.OrdinalIgnoreCase));
    if (existing is not null)
    {
      await EditBookmarkAsync(existing.Id);
      return;
    }
    await AddCurrentPageBookmarkAsync();
  }

  private void UpdateBookmarkStarState()
  {
    if (_bookmarkStarButton is null || _bookmarkStarButton.IsDisposed) return;
    var tab = _state.Tabs.FirstOrDefault(item => item.Id == _state.ActiveTabId);
    var url = tab?.Url ?? "about:blank";
    var isHttps = url.StartsWith("https://", StringComparison.OrdinalIgnoreCase);
    if (_securityBadge is { IsDisposed: false })
    {
      _securityBadge.ForeColor = isHttps ? Color.FromArgb(148, 150, 158) : Color.FromArgb(96, 98, 106);
      _securityBadge.Invalidate();
    }
    var isBookmarked = url != "about:blank" &&
        BookmarkStore.TryNormalizeUrl(url, out var normalized) &&
        _bookmarks.Items.Any(item => item.Url.Equals(normalized, StringComparison.OrdinalIgnoreCase));
    _bookmarkStarButton.Glyph = isBookmarked ? PillGlyph.StarFilled : PillGlyph.Star;
    _bookmarkStarButton.ForeColor = isBookmarked ? Accent : MutedText;
    _bookmarkStarButton.Invalidate();
  }

  private Task AddCurrentPageBookmarkAsync()
  {
    var tab = _state.Tabs.FirstOrDefault(item => item.Id == _state.ActiveTabId);
    if (tab is null || tab.Url == "about:blank" || !BookmarkStore.TryNormalizeUrl(tab.Url, out var url))
    {
      MessageBox.Show(this, "북마크에 추가할 웹 페이지를 먼저 열어 주세요.", "북마크 추가");
      return Task.CompletedTask;
    }
    var duplicate = _bookmarks.Items.FirstOrDefault(item => item.Url.Equals(url, StringComparison.OrdinalIgnoreCase));
    if (duplicate is not null)
    {
      MessageBox.Show(this, $"이미 ‘{duplicate.Name}’ 이름으로 저장된 페이지입니다.", "북마크 추가");
      return Task.CompletedTask;
    }
    var name = InputDialog.Show(this, "현재 페이지 북마크", "이름", tab.Title);
    if (string.IsNullOrWhiteSpace(name)) return Task.CompletedTask;
    _bookmarks.Items.Add(new BookmarkEntry(
        Guid.NewGuid().ToString("N"),
        name.Trim(),
        url,
        BookmarkStore.GlyphFor(name),
        BookmarkStore.ColorFor(_bookmarks.Items.Count)));
    SaveAndRefreshBookmarks();
    return Task.CompletedTask;
  }

  private Task EditBookmarkAsync(string bookmarkId)
  {
    var index = _bookmarks.Items.FindIndex(item => item.Id == bookmarkId);
    if (index < 0) return Task.CompletedTask;
    var current = _bookmarks.Items[index];
    var name = InputDialog.Show(this, "북마크 수정", "이름", current.Name);
    if (string.IsNullOrWhiteSpace(name)) return Task.CompletedTask;
    var address = InputDialog.Show(this, "북마크 수정", "웹 주소", current.Url);
    if (!BookmarkStore.TryNormalizeUrl(address, out var url))
    {
      MessageBox.Show(this, "올바른 HTTP 또는 HTTPS 주소를 입력하세요.", "북마크 수정");
      return Task.CompletedTask;
    }
    _bookmarks.Items[index] = current with
    {
      Name = name.Trim(),
      Url = url,
      Glyph = BookmarkStore.GlyphFor(name),
    };
    SaveAndRefreshBookmarks();
    return Task.CompletedTask;
  }

  private Task DeleteBookmarkAsync(string bookmarkId)
  {
    var bookmark = _bookmarks.Items.FirstOrDefault(item => item.Id == bookmarkId);
    if (bookmark is null) return Task.CompletedTask;
    if (MessageBox.Show(this, $"‘{bookmark.Name}’ 북마크를 삭제할까요?", "북마크 삭제",
        MessageBoxButtons.YesNo, MessageBoxIcon.Warning) != DialogResult.Yes) return Task.CompletedTask;
    _bookmarks.Items.RemoveAll(item => item.Id == bookmarkId);
    SaveAndRefreshBookmarks();
    return Task.CompletedTask;
  }

  private Task ShowBookmarkManagerAsync()
  {
    using var manager = new BookmarkManagerForm(_bookmarks);
    if (manager.ShowDialog(this) == DialogResult.OK)
    {
      _bookmarks = manager.Result;
      SaveAndRefreshBookmarks();
    }
    return Task.CompletedTask;
  }

  private void SaveAndRefreshBookmarks()
  {
    BookmarkStore.Save(_bookmarks);
    PopulateFavoritesBar();
    PopulateStartPageFavorites();
    UpdateBookmarkStarState();
  }

  private async Task OpenAllFavoritesAsync(bool createGroup = false)
  {
    if (_state.Tabs.Count + _bookmarks.Items.Count > SyncSnapshot.MaxTabs)
    {
      MessageBox.Show(this, $"즐겨찾기를 모두 열려면 탭을 일부 닫아 주세요. (최대 {SyncSnapshot.MaxTabs}개)", "탭 제한");
      return;
    }

    if (createGroup)
    {
      var group = new TabGroupEntry(Guid.NewGuid().ToString("N"), _state.ActiveProfileId, "북마크 모음");
      _state.Groups.Add(group);
      _state.ActiveGroupId = group.Id;
    }

    TabEntry? last = null;
    foreach (var favorite in _bookmarks.Items)
    {
      last = new TabEntry(
          Guid.NewGuid().ToString("N"),
          favorite.Name,
          favorite.Url,
          _state.ActiveProfileId,
          _state.ActiveGroupId);
      _state.Tabs.Add(last);
    }

    if (last is not null) _state.ActiveTabId = last.Id;
    _showingOverview = false;
    MarkLocalChange();
    RefreshWorkspaceUi();
    await ActivateCurrentTabAsync();
  }

  private void BuildStartPage()
  {
    _startPage.Dock = DockStyle.Fill;
    _startPage.BackColor = PageBackground;
    _startPage.StartColor = Color.FromArgb(22, 26, 36);
    _startPage.EndColor = Color.FromArgb(14, 16, 22);
    _startPage.AutoScroll = true;
    var content = new TableLayoutPanel
    {
      Width = 900,
      Height = 680,
      BackColor = Color.Transparent,
      ColumnCount = 1,
      RowCount = 7,
      Padding = new Padding(0, 34, 0, 32),
    };
    content.RowStyles.Add(new RowStyle(SizeType.Absolute, 42));
    content.RowStyles.Add(new RowStyle(SizeType.Absolute, 146));
    content.RowStyles.Add(new RowStyle(SizeType.Absolute, 42));
    content.RowStyles.Add(new RowStyle(SizeType.Absolute, 118));
    content.RowStyles.Add(new RowStyle(SizeType.Absolute, 42));
    content.RowStyles.Add(new RowStyle(SizeType.Absolute, 198));
    content.RowStyles.Add(new RowStyle(SizeType.Percent, 100));

    content.Controls.Add(SectionTitle("즐겨찾기"), 0, 0);

    _startPageFavorites = new FlowLayoutPanel
    {
      Dock = DockStyle.Fill,
      BackColor = Color.Transparent,
      FlowDirection = FlowDirection.LeftToRight,
      WrapContents = true,
      AutoScroll = true,
      Padding = new Padding(0, 6, 0, 4),
    };
    PopulateStartPageFavorites();
    content.Controls.Add(_startPageFavorites, 0, 1);

    content.Controls.Add(SectionTitle("개인정보 보호 리포트"), 0, 2);

    var privacy = new RoundedPanel
    {
      Dock = DockStyle.Fill,
      BackColor = Color.FromArgb(32, 35, 44),
      BorderColor = Color.FromArgb(58, 62, 74),
      BorderWidth = 1F,
      CornerRadius = 18,
      Padding = new Padding(20, 16, 20, 16),
      Margin = new Padding(0, 4, 0, 10),
    };
    var privacyGrid = new TableLayoutPanel
    {
      Dock = DockStyle.Fill,
      BackColor = Color.Transparent,
      ColumnCount = 3,
      RowCount = 1,
      Margin = Padding.Empty,
      Padding = Padding.Empty,
    };
    privacyGrid.ColumnStyles.Add(new ColumnStyle(SizeType.Absolute, 58));
    privacyGrid.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 100));
    privacyGrid.ColumnStyles.Add(new ColumnStyle(SizeType.AutoSize));
    var shieldIcon = new PillButton
    {
      Width = 46,
      Height = 46,
      CornerRadius = 14,
      Glyph = PillGlyph.Shield,
      BackColor = Color.FromArgb(24, 52, 92),
      HoverBackColor = Color.FromArgb(24, 52, 92),
      ForeColor = Color.FromArgb(74, 168, 255),
      BorderWidth = 0F,
      Anchor = AnchorStyles.Left,
      Cursor = Cursors.Default,
    };
    _privacyReport.Dock = DockStyle.Fill;
    _privacyReport.ForeColor = TextColor;
    _privacyReport.Font = new Font("Segoe UI", 10F);
    _privacyReport.TextAlign = ContentAlignment.MiddleLeft;
    var quickPills = new FlowLayoutPanel
    {
      Dock = DockStyle.Fill,
      BackColor = Color.Transparent,
      FlowDirection = FlowDirection.LeftToRight,
      WrapContents = false,
      AutoSize = true,
      AutoSizeMode = AutoSizeMode.GrowAndShrink,
      Padding = new Padding(6, 14, 0, 0),
    };
    var restoreClosedButton = ChromeButton("↺ 닫은 탭 복원", async (_, _) => await RestoreLastClosedTabAsync(), 118, "최근 닫은 탭 다시 열기 (Ctrl+Shift+T)");
    restoreClosedButton.Height = 34;
    restoreClosedButton.CornerRadius = 10;
    var openShelfButton = ChromeButton("▤ 책장 열기", (_, _) => ShowBookshelf(), 104, "책장 라이브러리 열기");
    openShelfButton.Height = 34;
    openShelfButton.CornerRadius = 10;
    openShelfButton.BackColor = Accent;
    openShelfButton.ForeColor = Color.White;
    openShelfButton.BorderWidth = 0F;
    quickPills.Controls.Add(restoreClosedButton);
    quickPills.Controls.Add(openShelfButton);
    privacyGrid.Controls.Add(shieldIcon, 0, 0);
    privacyGrid.Controls.Add(_privacyReport, 1, 0);
    privacyGrid.Controls.Add(quickPills, 2, 0);
    privacy.Controls.Add(privacyGrid);
    content.Controls.Add(privacy, 0, 3);

    content.Controls.Add(SectionTitle("빠른 도구 및 라이브러리"), 0, 4);

    var welcome = new RoundedPanel
    {
      Dock = DockStyle.Fill,
      BackColor = Color.FromArgb(30, 32, 40),
      BorderColor = Color.FromArgb(54, 58, 68),
      BorderWidth = 1F,
      CornerRadius = 18,
      Margin = new Padding(0, 4, 0, 12),
      Padding = new Padding(18, 14, 18, 14),
    };
    var features = new TableLayoutPanel
    {
      Dock = DockStyle.Fill,
      BackColor = Color.Transparent,
      ColumnCount = 2,
      RowCount = 2,
      Padding = Padding.Empty,
      Margin = Padding.Empty,
    };
    features.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 50));
    features.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 50));
    features.RowStyles.Add(new RowStyle(SizeType.Percent, 50));
    features.RowStyles.Add(new RowStyle(SizeType.Percent, 50));
    features.Controls.Add(Feature("▤", "책장 라이브러리", "캡처·이미지를 폴더 트리로 정리하고 뷰어로 열람", () => ShowBookshelf()), 0, 0);
    features.Controls.Add(Feature("▣", "실시간 영역 캡처", "웹페이지 원하는 영역만 지정해 PNG 저장 또는 책장 추가", () => _ = CapturePreviewFromMenuAsync()), 1, 0);
    features.Controls.Add(Feature("✨", "워터마크 제거", "현재 화면을 즉시 캡처하고 워터마크 제거 도구로 연결", () => _ = RemoveWatermarkFromActiveTabAsync()), 0, 1);
    features.Controls.Add(Feature("⇄", "Android · PC 동기화", "종단간 암호화 및 Google Drive로 탭·북마크·책장 연동", () => _ = ShowSyncDialogAsync()), 1, 1);
    welcome.Controls.Add(features);
    content.Controls.Add(welcome, 0, 5);

    _startPage.Controls.Add(content);
    void CenterContent()
    {
      content.Left = Math.Max(24, (_startPage.ClientSize.Width - content.Width) / 2);
      content.Top = 0;
    }
    _startPage.SizeChanged += (_, _) => CenterContent();
    CenterContent();
  }

  private void BuildTabOverview()
  {
    _tabOverview.Dock = DockStyle.Fill;
    _tabOverview.BackColor = PageBackground;
    _tabOverview.StartColor = Color.FromArgb(22, 24, 30);
    _tabOverview.EndColor = PageBackground;
    _tabOverview.Visible = false;

    var layout = new TableLayoutPanel
    {
      Dock = DockStyle.Fill,
      BackColor = Color.Transparent,
      ColumnCount = 1,
      RowCount = 3,
      Margin = Padding.Empty,
      Padding = Padding.Empty,
    };
    layout.RowStyles.Add(new RowStyle(SizeType.Absolute, 82));
    layout.RowStyles.Add(new RowStyle(SizeType.Percent, 100));
    layout.RowStyles.Add(new RowStyle(SizeType.Absolute, 92));
    _tabOverview.Controls.Add(layout);

    var header = new TableLayoutPanel
    {
      Dock = DockStyle.Fill,
      BackColor = Color.Transparent,
      ColumnCount = 3,
      RowCount = 1,
      Padding = new Padding(26, 16, 26, 12),
      Margin = Padding.Empty,
    };
    header.ColumnStyles.Add(new ColumnStyle(SizeType.Absolute, 80));
    header.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 100));
    header.ColumnStyles.Add(new ColumnStyle(SizeType.Absolute, 80));

    PillButton more = new()
    {
      Anchor = AnchorStyles.Left,
      Width = 42,
      Height = 42,
      Margin = Padding.Empty,
      Text = string.Empty,
      Glyph = PillGlyph.More,
      BackColor = OverviewControl,
      HoverBackColor = Color.FromArgb(56, 56, 62),
      ForeColor = TextColor,
      CornerRadius = 12,
      BorderColor = Outline,
      BorderWidth = 1F,
      AccessibleName = "탭 목록 메뉴",
    };
    more.Click += (_, _) => ShowMainMenu(more);
    header.Controls.Add(more, 0, 0);

    var center = new Panel { Dock = DockStyle.Fill, BackColor = Color.Transparent };
    _overviewProfileButton = new PillButton
    {
      Width = 220,
      Height = 42,
      BackColor = OverviewControl,
      HoverBackColor = Color.FromArgb(56, 56, 62),
      ForeColor = TextColor,
      CornerRadius = 12,
      Font = new Font("Segoe UI", 10.5F, FontStyle.Bold),
      TextImageRelation = TextImageRelation.ImageBeforeText,
      ImageAlign = ContentAlignment.MiddleLeft,
      TextAlign = ContentAlignment.MiddleCenter,
      BorderColor = Outline,
      BorderWidth = 1F,
      AccessibleName = "프로필 변경",
    };
    _overviewProfileButton.Click += (_, _) => ShowOverviewProfileMenu(_overviewProfileButton);
    center.Controls.Add(_overviewProfileButton);

    _overviewSearchHost = new RoundedPanel
    {
      Width = 380,
      Height = 42,
      BackColor = Field,
      BorderColor = Accent,
      BorderWidth = 1.4F,
      CornerRadius = 12,
      Padding = new Padding(16, 6, 16, 4),
      Visible = false,
    };
    ConfigureTextBox(_overviewSearch);
    _overviewSearch.Dock = DockStyle.Fill;
    _overviewSearch.BackColor = Field;
    _overviewSearch.Margin = Padding.Empty;
    _overviewSearch.PlaceholderText = "탭 제목 또는 사이트 검색";
    _overviewSearch.TextAlign = HorizontalAlignment.Center;
    _overviewSearch.TextChanged += (_, _) => RefreshTabOverview();
    _overviewSearchHost.Controls.Add(_overviewSearch);
    center.Controls.Add(_overviewSearchHost);
    center.SizeChanged += (_, _) => CenterOverviewHeader(center);
    header.Controls.Add(center, 1, 0);

    _overviewSearchButton = new PillButton
    {
      Anchor = AnchorStyles.Right,
      Width = 42,
      Height = 42,
      Margin = Padding.Empty,
      Text = string.Empty,
      Glyph = PillGlyph.Search,
      BackColor = OverviewControl,
      HoverBackColor = Color.FromArgb(56, 56, 62),
      ForeColor = TextColor,
      CornerRadius = 12,
      BorderColor = Outline,
      BorderWidth = 1F,
      AccessibleName = "탭 제목 검색",
    };
    _overviewSearchButton.Click += (_, _) =>
    {
      _overviewSearchHost.Visible = !_overviewSearchHost.Visible;
      _overviewProfileButton.Visible = !_overviewSearchHost.Visible;
      CenterOverviewHeader(center);
      if (_overviewSearchHost.Visible) _overviewSearch.Focus();
      else
      {
        _overviewSearch.Clear();
        _overviewProfileButton.Focus();
      }
    };
    header.Controls.Add(_overviewSearchButton, 2, 0);
    layout.Controls.Add(header, 0, 0);

    _overviewGrid.Dock = DockStyle.Fill;
    _overviewGrid.AutoScroll = true;
    _overviewGrid.FlowDirection = FlowDirection.LeftToRight;
    _overviewGrid.WrapContents = true;
    _overviewGrid.Padding = new Padding(26, 10, 26, 18);
    _overviewGrid.BackColor = Color.Transparent;
    _overviewGrid.SizeChanged += (_, _) => ResizeOverviewCards();
    layout.Controls.Add(_overviewGrid, 0, 1);

    var footer = new TableLayoutPanel
    {
      Dock = DockStyle.Fill,
      BackColor = Color.Transparent,
      ColumnCount = 4,
      RowCount = 1,
      Padding = new Padding(26, 14, 26, 22),
      Margin = Padding.Empty,
    };
    footer.ColumnStyles.Add(new ColumnStyle(SizeType.Absolute, 54));
    footer.ColumnStyles.Add(new ColumnStyle(SizeType.Absolute, 148));
    footer.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 100));
    footer.ColumnStyles.Add(new ColumnStyle(SizeType.Absolute, 92));
    var add = new PillButton
    {
      Anchor = AnchorStyles.Top | AnchorStyles.Left,
      Width = 44,
      Height = 44,
      Margin = Padding.Empty,
      Text = string.Empty,
      Glyph = PillGlyph.Add,
      BackColor = OverviewControl,
      HoverBackColor = Color.FromArgb(56, 56, 62),
      ForeColor = TextColor,
      CornerRadius = 12,
      BorderColor = Outline,
      BorderWidth = 1F,
      AccessibleName = "새 탭",
    };
    add.Click += async (_, _) => await CreateTabAsync();
    footer.Controls.Add(add, 0, 0);

    var closeAll = new PillButton
    {
      Anchor = AnchorStyles.Top | AnchorStyles.Left,
      Width = 136,
      Height = 44,
      Margin = Padding.Empty,
      Text = "모든 탭 닫기",
      BackColor = OverviewControl,
      HoverBackColor = Color.FromArgb(64, 34, 36),
      ForeColor = Danger,
      CornerRadius = 12,
      Font = new Font("Segoe UI", 9.5F, FontStyle.Bold),
      BorderColor = Color.FromArgb(110, 48, 50),
      BorderWidth = 1F,
      AccessibleName = "모든 탭 닫기 확인",
    };
    closeAll.Click += async (_, _) => await CloseAllTabsWithCountdownAsync();
    footer.Controls.Add(closeAll, 1, 0);

    var groupHost = new Panel { Dock = DockStyle.Fill, BackColor = Color.Transparent };
    _overviewGroupButton = new PillButton
    {
      Width = 240,
      Height = 44,
      BackColor = OverviewControl,
      HoverBackColor = Color.FromArgb(56, 56, 62),
      ForeColor = TextColor,
      CornerRadius = 12,
      Font = new Font("Segoe UI", 10F, FontStyle.Bold),
      BorderColor = Outline,
      BorderWidth = 1F,
      AccessibleName = "탭 그룹 변경",
    };
    _overviewGroupButton.Click += (_, _) => ShowOverviewGroupMenu(_overviewGroupButton);
    groupHost.Controls.Add(_overviewGroupButton);
    groupHost.SizeChanged += (_, _) =>
    {
      _overviewGroupButton.Left = Math.Max(0, (groupHost.ClientSize.Width - _overviewGroupButton.Width) / 2);
      _overviewGroupButton.Top = Math.Max(0, (groupHost.ClientSize.Height - _overviewGroupButton.Height) / 2);
    };
    footer.Controls.Add(groupHost, 2, 0);

    var done = new PillButton
    {
      Anchor = AnchorStyles.Top | AnchorStyles.Right,
      Width = 84,
      Height = 44,
      Margin = Padding.Empty,
      Text = "완료",
      BackColor = Accent,
      HoverBackColor = Color.FromArgb(36, 150, 255),
      ForeColor = Color.White,
      CornerRadius = 12,
      Font = new Font("Segoe UI", 10F, FontStyle.Bold),
      AccessibleName = "탭 목록 닫기",
    };
    done.Click += async (_, _) => await ShowBrowsingAsync();
    footer.Controls.Add(done, 3, 0);
    layout.Controls.Add(footer, 0, 2);

    // WinForms lays out a nested overview over several paint messages. Keep a single,
    // already-composited frame above it during the z-order swap so no empty skeleton
    // can leak through on the first visible frame.
    _overviewTransitionSurface.Dock = DockStyle.Fill;
    _overviewTransitionSurface.BackColor = PageBackground;
    _overviewTransitionSurface.SizeMode = PictureBoxSizeMode.StretchImage;
    _overviewTransitionSurface.Margin = Padding.Empty;
    _overviewTransitionSurface.TabStop = false;
    _overviewTransitionSurface.Visible = false;
    _tabOverview.Controls.Add(_overviewTransitionSurface);
    _overviewTransitionSurface.BringToFront();
    _tabOverview.SizeChanged += (_, _) => ScheduleOverviewSnapshot();
  }

  private Control Feature(string glyph, string title, string subtitle, Action? onClick = null)
  {
    var card = new RoundedPanel
    {
      Dock = DockStyle.Fill,
      BackColor = Color.FromArgb(40, 43, 52),
      BorderColor = Color.FromArgb(62, 66, 78),
      BorderWidth = 1F,
      CornerRadius = 14,
      Margin = new Padding(6),
      Padding = new Padding(12, 8, 12, 8),
      Cursor = onClick is not null ? Cursors.Hand : Cursors.Default,
    };
    var panel = new TableLayoutPanel
    {
      Dock = DockStyle.Fill,
      BackColor = Color.Transparent,
      Margin = Padding.Empty,
      ColumnCount = 2,
      RowCount = 1,
      Cursor = card.Cursor,
    };
    panel.ColumnStyles.Add(new ColumnStyle(SizeType.Absolute, 42));
    panel.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 100));
    var iconLabel = new Label
    {
      Dock = DockStyle.Fill,
      Text = glyph,
      ForeColor = Accent,
      Font = new Font("Segoe UI Symbol", 16F),
      TextAlign = ContentAlignment.MiddleCenter,
      Cursor = card.Cursor,
    };
    var textLabel = new Label
    {
      Dock = DockStyle.Fill,
      Text = title + "\n" + subtitle,
      ForeColor = TextColor,
      Font = new Font("Segoe UI", 9.25F, FontStyle.Bold),
      Padding = new Padding(4, 6, 0, 0),
      Cursor = card.Cursor,
    };
    if (onClick is not null)
    {
      card.Click += (_, _) => onClick();
      panel.Click += (_, _) => onClick();
      iconLabel.Click += (_, _) => onClick();
      textLabel.Click += (_, _) => onClick();
    }
    panel.Controls.Add(iconLabel, 0, 0);
    panel.Controls.Add(textLabel, 1, 0);
    card.Controls.Add(panel);
    return card;
  }

  private static Label SectionTitle(string text) => new()
  {
    Dock = DockStyle.Fill,
    Text = text,
    ForeColor = TextColor,
    Font = new Font("Segoe UI", 13.5F, FontStyle.Bold),
    TextAlign = ContentAlignment.BottomLeft,
    Padding = new Padding(2, 0, 0, 4),
  };

  private void PopulateStartPageFavorites()
  {
    if (_startPageFavorites.IsDisposed) return;
    _startPageFavorites.SuspendLayout();
    foreach (Control control in _startPageFavorites.Controls.Cast<Control>().ToArray()) control.Dispose();
    _startPageFavorites.Controls.Clear();
    foreach (var bookmark in _bookmarks.Items)
      _startPageFavorites.Controls.Add(FavoriteTile(bookmark));
    var addTile = new SafariStartTileControl
    {
      Size = new Size(84, 92),
      Title = "추가",
      GlyphText = "＋",
      IsAddTile = true,
      AccentColor = OverviewControl,
      Margin = new Padding(0, 0, 14, 12),
    };
    addTile.Click += async (_, _) => await ShowBookmarkManagerAsync();
    _startPageFavorites.Controls.Add(addTile);
    _startPageFavorites.ResumeLayout(true);
  }

  private Control FavoriteTile(BookmarkEntry favorite)
  {
    var tile = new SafariStartTileControl
    {
      Size = new Size(84, 92),
      Title = favorite.Name,
      IconImage = BookmarkFaviconStore.LoadOrCreate(favorite, 36),
      AccentColor = Color.FromArgb(favorite.ColorArgb),
      Margin = new Padding(0, 0, 14, 12),
      AccessibleName = favorite.Name,
    };
    new ToolTip().SetToolTip(tile, favorite.Name + "\n" + favorite.Url);
    tile.Click += async (_, _) => await NavigateToAsync(favorite.Url);
    tile.MouseUp += (_, eventArgs) =>
    {
      if (eventArgs.Button == MouseButtons.Right)
        ShowFavoritesContextMenu(tile, eventArgs.Location, favorite);
    };
    return tile;
  }

  private async Task DuplicateTabAsync(string sourceTabId)
  {
    if (_state.Tabs.Count >= SyncSnapshot.MaxTabs)
    {
      MessageBox.Show(this, $"탭은 최대 {SyncSnapshot.MaxTabs}개까지 열 수 있습니다.", "탭 제한");
      return;
    }
    var sourceIndex = _state.Tabs.FindIndex(t => t.Id == sourceTabId);
    if (sourceIndex < 0) return;
    var source = _state.Tabs[sourceIndex];
    var duplicated = new TabEntry(
        Guid.NewGuid().ToString("N"),
        source.Title,
        source.Url,
        source.ProfileId,
        source.GroupId);
    _state.Tabs.Insert(sourceIndex + 1, duplicated);
    _state.ActiveTabId = duplicated.Id;
    _showingOverview = false;
    MarkLocalChange();
    RefreshTabsUi();
    await ActivateCurrentTabAsync();
  }

  private async Task CreateTabAsync(string url = "about:blank")
  {
    if (_state.Tabs.Count >= SyncSnapshot.MaxTabs)
    {
      MessageBox.Show(this, $"탭은 최대 {SyncSnapshot.MaxTabs}개까지 열 수 있습니다.", "탭 제한");
      return;
    }
    var tab = new TabEntry(Guid.NewGuid().ToString("N"), "새 탭", url, _state.ActiveProfileId, _state.ActiveGroupId);
    _state.Tabs.Add(tab);
    _state.ActiveTabId = tab.Id;
    _showingOverview = false;
    MarkLocalChange();
    RefreshTabsUi();
    await ActivateCurrentTabAsync();
    if (url == "about:blank") _address.Focus();
  }

  private async Task CloseTabAsync(string tabId)
  {
    var closedTab = _state.Tabs.FirstOrDefault(t => t.Id == tabId);
    if (closedTab is not null) _closedTabs.Push(closedTab);

    if (_views.Remove(tabId, out var view))
    {
      _browserHost.Controls.Remove(view);
      view.Dispose();
    }
    if (_previews.Remove(tabId, out var preview)) preview.Dispose();
    DesktopThumbnailStore.Delete(tabId);
    _state.Tabs.RemoveAll(item => item.Id == tabId);
    EnsureWorkspaceTab();
    MarkLocalChange();
    RefreshTabsUi();
    await ActivateCurrentTabAsync();
    UpdateToolbarZoom();
  }

  private async Task CloseAllTabsWithCountdownAsync()
  {
    using var dialog = new CloseAllTabsCountdownDialog();
    if (dialog.ShowDialog(this) != DialogResult.OK) return;

    var visible = VisibleTabs().ToList();
    foreach (var tab in visible)
    {
      _closedTabs.Push(tab);
      if (_views.Remove(tab.Id, out var view))
      {
        _browserHost.Controls.Remove(view);
        view.Dispose();
      }
      if (_previews.Remove(tab.Id, out var preview)) preview.Dispose();
      DesktopThumbnailStore.Delete(tab.Id);
      _state.Tabs.RemoveAll(item => item.Id == tab.Id);
    }
    EnsureWorkspaceTab();
    MarkLocalChange();
    RefreshTabsUi();
    await ActivateCurrentTabAsync();
    UpdateToolbarZoom();
    if (_showingOverview) await ShowBrowsingAsync();
  }

  private async Task CloseOtherTabsAsync(string keepTabId)
  {
    var toClose = VisibleTabs().Where(t => t.Id != keepTabId).ToList();
    if (toClose.Count == 0) return;
    foreach (var tab in toClose)
    {
      _closedTabs.Push(tab);
      if (_views.Remove(tab.Id, out var view))
      {
        _browserHost.Controls.Remove(view);
        view.Dispose();
      }
      if (_previews.Remove(tab.Id, out var preview)) preview.Dispose();
      DesktopThumbnailStore.Delete(tab.Id);
      _state.Tabs.RemoveAll(item => item.Id == tab.Id);
    }
    _state.ActiveTabId = keepTabId;
    MarkLocalChange();
    RefreshTabsUi();
    await ActivateCurrentTabAsync();
    UpdateToolbarZoom();
  }

  private async Task RestoreLastClosedTabAsync()
  {
    if (_closedTabs.Count == 0)
    {
      MessageBox.Show(this, "다시 열 수 있는 최근 닫은 탭이 없습니다.", "닫은 탭 복원");
      return;
    }
    var tab = _closedTabs.Pop();
    await CreateTabAsync(tab.Url);
  }

  private void UpdateToolbarZoom()
  {
    if (_toolbarZoomLabel.IsDisposed) return;
    _toolbarZoomLabel.Text = ActiveZoomPercent() + "%";
  }

  private void ResetZoom()
  {
    if (_state.ActiveTabId is null || !_views.TryGetValue(_state.ActiveTabId, out var view)) return;
    view.ZoomFactor = 1.0;
  }

  private async Task CaptureActiveTabAsync()
  {
    if (_state.ActiveTabId is null || !_views.TryGetValue(_state.ActiveTabId, out var view) || view.CoreWebView2 is null)
    {
      MessageBox.Show(this, "캡처할 수 있는 활성 페이지가 없습니다.", "화면 캡처");
      return;
    }
    try
    {
      using var ms = new MemoryStream();
      await view.CoreWebView2.CapturePreviewAsync(CoreWebView2CapturePreviewImageFormat.Png, ms);
      ms.Position = 0;
      using var image = Image.FromStream(ms);
      Clipboard.SetImage(image);

      var answer = MessageBox.Show(
          this,
          "화면이 클립보드에 복사되었습니다.\n이미지 파일로도 저장하시겠습니까?",
          "화면 캡처 완료",
          MessageBoxButtons.YesNo,
          MessageBoxIcon.Information);

      if (answer == DialogResult.Yes)
      {
        using var picker = new SaveFileDialog
        {
          Filter = "PNG 이미지 (*.png)|*.png",
          FileName = $"Omyaru_Capture_{DateTime.Now:yyyyMMdd_HHmmss}.png",
        };
        if (picker.ShowDialog(this) == DialogResult.OK)
        {
          File.WriteAllBytes(picker.FileName, ms.ToArray());
          MessageBox.Show(this, "저장되었습니다:\n" + picker.FileName, "저장 완료");
        }
      }
    }
    catch (Exception ex)
    {
      MessageBox.Show(this, "화면 캡처 중 오류가 발생했습니다: " + ex.Message, "캡처 오류");
    }
  }

  private async Task RemoveWatermarkFromActiveTabAsync()
  {
    if (_state.ActiveTabId is null || !_views.TryGetValue(_state.ActiveTabId, out var view) || view.CoreWebView2 is null)
    {
      MessageBox.Show(this, "캡처할 수 있는 활성 페이지가 없습니다.", "워터마크 삭제");
      return;
    }
    try
    {
      var tempDir = Path.Combine(Path.GetTempPath(), "OmyaruCaptures");
      Directory.CreateDirectory(tempDir);
      var tempPath = Path.Combine(tempDir, $"capture_{DateTime.Now:yyyyMMdd_HHmmss}.png");

      using (var fs = new FileStream(tempPath, FileMode.Create))
      {
        await view.CoreWebView2.CapturePreviewAsync(CoreWebView2CapturePreviewImageFormat.Png, fs);
      }

      using (var image = Image.FromFile(tempPath))
      {
        Clipboard.SetImage(image);
      }

      await CreateTabAsync("https://www.watermarkremover.io/ko/upload");

      MessageBox.Show(
          this,
          "현재 화면이 캡처되어 클립보드에 복사되었습니다.\n\n" +
          "새로 열린 watermarkremover.io 화면에서 [Ctrl + V]로 바로 붙여넣거나,\n" +
          "업로드 창에서 아래 임시 파일을 선택하여 워터마크를 제거할 수 있습니다:\n" +
          tempPath,
          "워터마크 삭제 — 캡처 완료",
          MessageBoxButtons.OK,
          MessageBoxIcon.Information);
    }
    catch (Exception ex)
    {
      MessageBox.Show(this, "워터마크 삭제 캡처 중 오류가 발생했습니다: " + ex.Message, "워터마크 삭제 오류");
    }
  }

  protected override bool ProcessCmdKey(ref Message msg, Keys keyData)
  {
    if (keyData == (Keys.Control | Keys.T))
    {
      _ = CreateTabAsync();
      return true;
    }
    if (keyData == (Keys.Control | Keys.W))
    {
      if (_state.ActiveTabId is not null) _ = CloseTabAsync(_state.ActiveTabId);
      return true;
    }
    if (keyData == (Keys.Control | Keys.Shift | Keys.T))
    {
      _ = RestoreLastClosedTabAsync();
      return true;
    }
    if (keyData == (Keys.Control | Keys.L) || keyData == (Keys.Alt | Keys.D))
    {
      _address.Focus();
      _address.SelectAll();
      return true;
    }
    if (keyData == (Keys.Control | Keys.R) || keyData == Keys.F5)
    {
      _ = ActiveViewActionAsync(v => v.Reload());
      return true;
    }
    if (keyData == (Keys.Control | Keys.Oemplus) || keyData == (Keys.Control | Keys.Add))
    {
      AdjustZoom(0.1);
      UpdateToolbarZoom();
      return true;
    }
    if (keyData == (Keys.Control | Keys.OemMinus) || keyData == (Keys.Control | Keys.Subtract))
    {
      AdjustZoom(-0.1);
      UpdateToolbarZoom();
      return true;
    }
    if (keyData == (Keys.Control | Keys.D0) || keyData == (Keys.Control | Keys.NumPad0))
    {
      ResetZoom();
      UpdateToolbarZoom();
      return true;
    }
    if (keyData == (Keys.Control | Keys.Shift | Keys.S))
    {
      _ = CaptureActiveTabAsync();
      return true;
    }
    return base.ProcessCmdKey(ref msg, keyData);
  }

  private void CreateGroup()
  {
    if (_state.Groups.Count(group => group.ProfileId == _state.ActiveProfileId) >= 20)
    {
      MessageBox.Show(this, "프로필마다 탭 그룹을 최대 20개까지 만들 수 있습니다.");
      return;
    }
    var name = InputDialog.Show(this, "새 탭 그룹", "그룹 이름");
    if (string.IsNullOrWhiteSpace(name)) return;
    var group = new TabGroupEntry(Guid.NewGuid().ToString("N"), _state.ActiveProfileId, name.Trim());
    _state.Groups.Add(group);
    _state.ActiveGroupId = group.Id;
    EnsureWorkspaceTab();
    MarkLocalChange();
    RefreshWorkspaceUi();
    _ = ActivateCurrentTabAsync();
  }

  private void CreateProfile()
  {
    if (_state.Profiles.Count >= 8)
    {
      MessageBox.Show(this, "프로필은 최대 8개까지 만들 수 있습니다.");
      return;
    }
    var name = InputDialog.Show(this, "새 프로필", "프로필 이름");
    if (string.IsNullOrWhiteSpace(name)) return;
    var colors = new uint[] { 0xFF48BFA8, 0xFFF09B58, 0xFFD76B94, 0xFF9B7DE3, 0xFF59A8D8 };
    var profile = new ProfileEntry(Guid.NewGuid().ToString("N"), name.Trim(), "PERSON", colors[_state.Profiles.Count % colors.Length]);
    _state.Profiles.Add(profile);
    _state.ActiveProfileId = profile.Id;
    _state.ActiveGroupId = null;
    EnsureWorkspaceTab();
    MarkLocalChange();
    RefreshWorkspaceUi();
    _ = ActivateCurrentTabAsync();
  }

  private void PrepareMainMenu()
  {
    if (_mainMenu is not null) return;
    CachedPopupForm? popup = null;
    var content = CreateSettingsStyleMainMenu(() => popup?.Hide());
    popup = new CachedPopupForm(content, new Size(384, 836), 22);
    _mainMenu = popup;
  }

  private void ShowMainMenu(Control anchor)
  {
    PrepareMainMenu();
    var menu = _mainMenu!;
    if (menu.Visible)
    {
      menu.Hide();
      return;
    }
    if (_mainMenuZoomLabel is not null) _mainMenuZoomLabel.Text = ActiveZoomPercent() + "%";
    menu.RefreshSnapshot();
    var anchorScreen = anchor.PointToScreen(Point.Empty);
    var x = anchorScreen.X > Bounds.Left + Width / 2 ? anchorScreen.X + anchor.Width - menu.Width : anchorScreen.X;
    menu.ShowAt(this, anchor, new Point(x, anchorScreen.Y + anchor.Height + 6));
  }

  private Control CreateSettingsStyleMainMenu(Action closeMenu)
  {
    var panel = new RoundedPanel
    {
      Size = new Size(384, 836),
      BackColor = OverviewControl,
      BorderColor = Outline,
      BorderWidth = 1F,
      CornerRadius = 22,
      ClipContent = true,
      Padding = new Padding(12),
    };
    var layout = new TableLayoutPanel
    {
      Dock = DockStyle.Fill,
      BackColor = Color.Transparent,
      ColumnCount = 1,
      RowCount = 9,
      Margin = Padding.Empty,
      Padding = Padding.Empty,
    };
    layout.RowStyles.Add(new RowStyle(SizeType.Absolute, 78));
    layout.RowStyles.Add(new RowStyle(SizeType.Absolute, 8));
    layout.RowStyles.Add(new RowStyle(SizeType.Absolute, 50));
    layout.RowStyles.Add(new RowStyle(SizeType.Absolute, 10));
    layout.RowStyles.Add(new RowStyle(SizeType.Absolute, 258));
    layout.RowStyles.Add(new RowStyle(SizeType.Absolute, 10));
    layout.RowStyles.Add(new RowStyle(SizeType.Absolute, 216));
    layout.RowStyles.Add(new RowStyle(SizeType.Absolute, 10));
    layout.RowStyles.Add(new RowStyle(SizeType.Absolute, 174));
    layout.Controls.Add(CreateSettingsMenuActions(closeMenu), 0, 0);
    layout.Controls.Add(CreateSettingsZoomRow(closeMenu), 0, 2);
    layout.Controls.Add(CreateSettingsMenuSection(closeMenu,
        new("◷", "방문 기록", () => RunAsync(ShowHistory)),
        new("⇩", "다운로드", () => RunAsync(OpenDownloads)),
        new("☆", "북마크", ShowBookmarkManagerAsync),
        new("🧩", "확장 프로그램", ShowExtensionManagerAsync)), 0, 4);
    layout.Controls.Add(CreateSettingsMenuSection(closeMenu,
        new("✨", "워터마크 삭제", RemoveWatermarkFromActiveTabAsync),
        new("▣", "화면 캡처", CapturePreviewFromMenuAsync),
        new("▤", "책장", () => RunAsync(ShowBookshelf)),
        new("▤", "인쇄", () => RunAsync(PrintActivePage)),
        new("⌁", "현재 링크 복사", () => RunAsync(CopyActiveAddress)),
        new("◈", "개인정보 보호", () => ShowNoticeAsync("개인정보 보호", "분석·광고 SDK 없이 프로필 데이터와 썸네일을 로컬에 저장합니다."))), 0, 6);
    layout.Controls.Add(CreateSettingsMenuSection(closeMenu,
        new("🗑", "모든 탭 닫기 (5초)", CloseAllTabsWithCountdownAsync, Destructive: true),
        new("?", "도움말", () => ShowNoticeAsync("도움말", "탭 목록에서 프로필과 탭 그룹을 변경할 수 있습니다.")),
        new("⚙", "설정", ShowSettingsDialogAsync),
        new("⏻", "종료", () => RunAsync(Close), Destructive: true)), 0, 8);
    panel.Controls.Add(layout);
    return panel;

    Task RunAsync(Action action)
    {
      action();
      return Task.CompletedTask;
    }

    Task ShowNoticeAsync(string title, string message)
    {
      ShowFeatureNotice(title, message);
      return Task.CompletedTask;
    }
  }

  private Control CreateSettingsMenuActions(Action closeMenu)
  {
    var actions = new RoundedPanel
    {
      Dock = DockStyle.Fill,
      BackColor = Surface,
      BorderColor = Outline,
      BorderWidth = 1F,
      CornerRadius = 17,
      ClipContent = true,
      Padding = new Padding(5),
    };
    var grid = new TableLayoutPanel
    {
      Dock = DockStyle.Fill,
      BackColor = Color.Transparent,
      ColumnCount = 3,
      RowCount = 1,
      Margin = Padding.Empty,
      Padding = Padding.Empty,
    };
    grid.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 33.33F));
    grid.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 33.33F));
    grid.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 33.34F));
    grid.Controls.Add(SettingsMenuActionButton("＋", "새 탭", async () => await CreateTabAsync(), closeMenu), 0, 0);
    grid.Controls.Add(SettingsMenuActionButton("▦", "탭 목록", ShowTabOverviewAsync, closeMenu), 1, 0);
    grid.Controls.Add(SettingsMenuActionButton("⇄", "동기화", ShowSyncDialogAsync, closeMenu), 2, 0);
    actions.Controls.Add(grid);
    return actions;
  }

  private Control SettingsMenuActionButton(string glyph, string caption, Func<Task> action, Action closeMenu)
  {
    var button = new MenuActionControl
    {
      Dock = DockStyle.Fill,
      Glyph = glyph,
      Caption = caption,
      BackColor = Surface,
      HoverBackColor = OverviewControl,
      ForeColor = TextColor,
      Margin = new Padding(2),
      AccessibleName = caption,
    };
    button.Click += async (_, _) =>
    {
      closeMenu();
      await action();
    };
    return button;
  }

  private Control CreateSettingsZoomRow(Action closeMenu)
  {
    var row = new RoundedPanel
    {
      Dock = DockStyle.Fill,
      BackColor = Surface,
      BorderColor = Outline,
      BorderWidth = 1F,
      CornerRadius = 16,
      ClipContent = true,
      Padding = new Padding(7, 4, 7, 4),
    };
    var grid = new TableLayoutPanel
    {
      Dock = DockStyle.Fill,
      BackColor = Color.Transparent,
      ColumnCount = 5,
      RowCount = 1,
      Margin = Padding.Empty,
      Padding = Padding.Empty,
    };
    grid.ColumnStyles.Add(new ColumnStyle(SizeType.Absolute, 40));
    grid.ColumnStyles.Add(new ColumnStyle(SizeType.Absolute, 66));
    grid.ColumnStyles.Add(new ColumnStyle(SizeType.Absolute, 40));
    grid.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 100));
    grid.ColumnStyles.Add(new ColumnStyle(SizeType.Absolute, 126));
    var zoomLabel = new Label
    {
      Dock = DockStyle.Fill,
      Text = ActiveZoomPercent() + "%",
      ForeColor = TextColor,
      TextAlign = ContentAlignment.MiddleCenter,
    };
    _mainMenuZoomLabel = zoomLabel;
    var minus = SettingsMenuRoundButton("−", () =>
    {
      AdjustZoom(-0.10);
      zoomLabel.Text = ActiveZoomPercent() + "%";
    });
    var plus = SettingsMenuRoundButton("＋", () =>
    {
      AdjustZoom(0.10);
      zoomLabel.Text = ActiveZoomPercent() + "%";
    });
    var fullScreen = new PillButton
    {
      Dock = DockStyle.Fill,
      Text = "⛶  전체 화면",
      BackColor = Surface,
      HoverBackColor = OverviewControl,
      ForeColor = TextColor,
      CornerRadius = 14,
      Font = new Font("Segoe UI", 9.5F),
      Margin = new Padding(2),
    };
    fullScreen.Click += (_, _) =>
    {
      closeMenu();
      ToggleFullScreen();
    };
    grid.Controls.Add(minus, 0, 0);
    grid.Controls.Add(zoomLabel, 1, 0);
    grid.Controls.Add(plus, 2, 0);
    grid.Controls.Add(fullScreen, 4, 0);
    row.Controls.Add(grid);
    return row;
  }

  private static PillButton SettingsMenuRoundButton(string text, Action action)
  {
    var button = new PillButton
    {
      Anchor = AnchorStyles.None,
      Width = 36,
      Height = 36,
      MinimumSize = new Size(36, 36),
      MaximumSize = new Size(36, 36),
      Text = text,
      BackColor = Surface,
      HoverBackColor = OverviewControl,
      ForeColor = TextColor,
      CornerRadius = 18,
      Font = new Font("Segoe UI", 12F),
      Margin = Padding.Empty,
    };
    button.Click += (_, _) => action();
    return button;
  }

  private Control CreateSettingsMenuSection(Action closeMenu, params SettingsMenuEntry[] entries)
  {
    var section = new RoundedPanel
    {
      Dock = DockStyle.Fill,
      BackColor = Surface,
      BorderColor = Outline,
      BorderWidth = 1F,
      CornerRadius = 17,
      ClipContent = true,
      Padding = new Padding(4, 3, 4, 3),
    };
    var stack = new TableLayoutPanel
    {
      Dock = DockStyle.Fill,
      BackColor = Color.Transparent,
      ColumnCount = 1,
      RowCount = entries.Length,
      Margin = Padding.Empty,
      Padding = Padding.Empty,
    };
    for (var index = 0; index < entries.Length; index++)
    {
      stack.RowStyles.Add(new RowStyle(SizeType.Percent, 100F / entries.Length));
      var entry = entries[index];
      var row = new SettingsMenuRowControl
      {
        Dock = DockStyle.Fill,
        BackColor = Surface,
        HoverBackColor = OverviewControl,
        ForeColor = entry.Destructive ? Danger : TextColor,
        Glyph = entry.Glyph,
        Caption = entry.Caption,
        Shortcut = entry.Shortcut ?? string.Empty,
        ShowDivider = index < entries.Length - 1,
        Margin = Padding.Empty,
        AccessibleName = entry.Caption,
      };
      row.Click += async (_, _) =>
      {
        closeMenu();
        await entry.Action();
      };
      stack.Controls.Add(row, 0, index);
    }
    section.Controls.Add(stack);
    return section;
  }

  private int ActiveZoomPercent() => _state.ActiveTabId is not null && _views.TryGetValue(_state.ActiveTabId, out var view)
      ? (int)Math.Round(view.ZoomFactor * 100)
      : 100;

  private void AdjustZoom(double delta)
  {
    if (_state.ActiveTabId is null || !_views.TryGetValue(_state.ActiveTabId, out var view)) return;
    view.ZoomFactor = Math.Clamp(view.ZoomFactor + delta, 0.25, 5.0);
  }

  private void ToggleFullScreen()
  {
    if (!_fullScreen)
    {
      _windowedState = WindowState;
      _windowedBounds = Bounds;
      BeginWindowLayoutTransition();
      WindowState = FormWindowState.Normal;
      Bounds = Screen.FromControl(this).Bounds;
      _fullScreen = true;
      ScheduleWindowLayoutCompletion();
      return;
    }
    BeginWindowLayoutTransition();
    WindowState = _windowedState;
    if (_windowedState == FormWindowState.Normal) Bounds = _windowedBounds;
    _fullScreen = false;
    UpdateMaximizedBounds();
    ScheduleWindowLayoutCompletion();
  }

  private void OpenDownloads()
  {
    if (_state.ActiveTabId is not null && _views.TryGetValue(_state.ActiveTabId, out var view))
      view.CoreWebView2?.OpenDefaultDownloadDialog();
  }

  private void PrintActivePage()
  {
    if (_state.ActiveTabId is not null && _views.TryGetValue(_state.ActiveTabId, out var view))
      view.CoreWebView2?.ShowPrintUI();
  }

  private void OpenDeveloperTools()
  {
    if (_state.ActiveTabId is not null && _views.TryGetValue(_state.ActiveTabId, out var view))
      view.CoreWebView2?.OpenDevToolsWindow();
  }

  private void CopyActiveAddress()
  {
    var tab = _state.Tabs.FirstOrDefault(item => item.Id == _state.ActiveTabId);
    if (tab?.Url is { Length: > 0 } url && url != "about:blank") Clipboard.SetText(url);
  }

  private async Task CapturePreviewFromMenuAsync()
  {
    var catalog = BookshelfStore.Load();
    using var actions = new CaptureActionsForm(_preferences.LastCaptureDirectory, catalog.ActiveFolder);
    if (actions.ShowDialog(this) != DialogResult.OK) return;
    if (actions.SelectedAction == CaptureAction.OpenBookshelf)
    {
      ShowBookshelf();
      return;
    }
    await CaptureSelectedRegionAsync(actions.SelectedAction, catalog);
  }

  private void ShowCaptureMenu(Control anchor)
  {
    var menu = CreateAndroidMenu();
    menu.MinimumSize = new Size(326, 0);
    var full = menu.Items.Add("화면 전체 캡처 (클립보드/저장)", CreateMenuGlyph("📷", Accent));
    full.Click += async (_, _) => await CaptureActiveTabAsync();
    AddCaptureItem("영역 지정 후 PNG 저장", "▣", CaptureAction.SavePng);
    AddCaptureItem("영역 지정 후 복사", "▢", CaptureAction.Copy);
    AddCaptureItem("영역 지정 후 책장에 추가", "▤", CaptureAction.AddToBookshelf);
    menu.Items.Add(new ToolStripSeparator());
    var wm = menu.Items.Add("✨ 워터마크 삭제 (선-캡처 자동 이동)", null);
    wm.Click += async (_, _) => await RemoveWatermarkFromActiveTabAsync();
    var shelf = menu.Items.Add("책장 열기", CreateMenuGlyph("▤", Accent));
    shelf.Click += (_, _) => ShowBookshelf();
    DisposeMenuAfterClose(menu);
    menu.Show(anchor, new Point(anchor.Width - Math.Max(menu.MinimumSize.Width, menu.PreferredSize.Width), anchor.Height + 6));

    void AddCaptureItem(string text, string glyph, CaptureAction action)
    {
      var item = menu.Items.Add(text, CreateMenuGlyph(glyph, TextColor));
      item.Click += async (_, _) => await CaptureSelectedRegionAsync(action, BookshelfStore.Load());
    }
  }

  private async Task CaptureSelectedRegionAsync(CaptureAction action, BookshelfCatalog catalog)
  {
    if (action is CaptureAction.Cancel or CaptureAction.OpenBookshelf || _captureInProgress) return;
    if (_state.ActiveTabId is null || !_views.TryGetValue(_state.ActiveTabId, out var view) ||
        view.CoreWebView2 is null || !view.Visible) return;
    var priorStatus = _syncStatus;
    var previousCapturePadding = _browserHost.Padding;
    _captureInProgress = true;
    try
    {
      _syncStatus = "실시간 페이지에서 캡처할 영역을 선택하세요";
      UpdateStatusText();
      UseWaitCursor = false;
      if (_preferences.CaptureSideMargins)
      {
        _browserHost.Padding = new Padding(Math.Max(16, (int)(24 * DeviceDpi / 96F)), 0, Math.Max(16, (int)(24 * DeviceDpi / 96F)), 0);
        _browserHost.PerformLayout();
      }
      await PreparePageForCaptureAsync(view);
      var selection = await SelectLiveCaptureRegionAsync(view, catalog, action, showBookshelfPicker: true);
      if (selection is null)
      {
        _syncStatus = priorStatus;
        UpdateStatusText();
        return;
      }

      _syncStatus = $"선택 영역 {Math.Round(selection.Width):N0} × {Math.Round(selection.Height):N0} 캡처 중…";
      UpdateStatusText();
      UseWaitCursor = true;
      using var bitmap = await CapturePageRegionBitmapAsync(view, selection);
      UseWaitCursor = false;
      if (bitmap is null)
      {
        _syncStatus = priorStatus;
        UpdateStatusText();
        MessageBox.Show(this, "선택한 페이지 영역을 캡처할 수 없습니다.", "화면 캡처");
        return;
      }
      switch (selection.Action)
      {
        case CaptureAction.SavePng:
          SaveCapturePng(bitmap);
          break;
        case CaptureAction.Copy:
          Clipboard.SetImage(bitmap);
          _syncStatus = "선택 영역을 클립보드에 복사함";
          UpdateStatusText();
          break;
        case CaptureAction.AddToBookshelf:
          var latestCatalog = BookshelfStore.Load();
          if (latestCatalog.Folders.Contains(selection.Folder, StringComparer.CurrentCultureIgnoreCase))
          {
            latestCatalog.ActiveFolder = latestCatalog.Folders.First(folder =>
                folder.Equals(selection.Folder, StringComparison.CurrentCultureIgnoreCase));
            BookshelfStore.Save(latestCatalog);
          }
          var source = _state.Tabs.FirstOrDefault(tab => tab.Id == _state.ActiveTabId)?.Url ?? string.Empty;
          var item = BookshelfStore.AddImage(latestCatalog, bitmap, source);
          if (_bookshelfForm is { IsDisposed: false })
            _bookshelfForm.ReloadCatalogFromExternalChange();
          _syncStatus = $"책장 ‘{item.Folder}’에 선택 영역 추가함";
          UpdateStatusText();
          break;
      }
    }
    catch (Exception error)
    {
      _syncStatus = priorStatus;
      UpdateStatusText();
      MessageBox.Show(this, "실시간 영역 캡처를 시작할 수 없습니다.\n" + error.Message, "화면 캡처");
    }
    finally
    {
      UseWaitCursor = false;
      await RemoveLiveCaptureOverlayAsync(view);
      await RestorePageAfterCaptureAsync(view);
      _browserHost.Padding = previousCapturePadding;
      _browserHost.PerformLayout();
      _captureInProgress = false;
    }
  }

  internal sealed record LiveCaptureSelection(
      double X,
      double Y,
      double Width,
      double Height,
      string Folder,
      CaptureAction Action);

  private sealed record CaptureViewportState(
      double ScrollX,
      double ScrollY,
      double ViewportWidth,
      double ViewportHeight);

  private async Task<LiveCaptureSelection?> SelectLiveCaptureRegionAsync(
      WebView2 view,
      BookshelfCatalog catalog,
      CaptureAction fallbackAction,
      bool showBookshelfPicker)
  {
    if (view.CoreWebView2 is null) return null;
    var token = Guid.NewGuid().ToString("N");
    var completion = new TaskCompletionSource<LiveCaptureSelection?>(TaskCreationOptions.RunContinuationsAsynchronously);
    var lensFrame = await CaptureLiveLensFrameAsync(view);
    var lensRefreshBusy = 0;

    async Task RefreshLensFrameAsync(double scrollX, double scrollY)
    {
      if (Interlocked.Exchange(ref lensRefreshBusy, 1) != 0) return;
      try
      {
        if (view.CoreWebView2 is null || completion.Task.IsCompleted) return;
        await view.CoreWebView2.ExecuteScriptAsync($$"""
            window.__omyaruPrepareCaptureLensFrame?.({{JsonSerializer.Serialize(token)}});
            """);
        var frame = await CaptureLiveLensFrameAsync(view);
        if (string.IsNullOrWhiteSpace(frame) || view.CoreWebView2 is null || completion.Task.IsCompleted) return;
        await view.CoreWebView2.ExecuteScriptAsync($$"""
            window.__omyaruSetCaptureLensFrame?.(
              {{JsonSerializer.Serialize(token)}},
              {{JsonSerializer.Serialize(frame)}},
              {{scrollX.ToString(CultureInfo.InvariantCulture)}},
              {{scrollY.ToString(CultureInfo.InvariantCulture)}});
            """);
      }
      catch
      {
        // A navigation or teardown can race a debounced lens refresh.
      }
      finally
      {
        try
        {
          if (view.CoreWebView2 is not null && !completion.Task.IsCompleted)
            await view.CoreWebView2.ExecuteScriptAsync($$"""
                window.__omyaruRestoreCaptureLensFrame?.({{JsonSerializer.Serialize(token)}});
                """);
        }
        catch { }
        Interlocked.Exchange(ref lensRefreshBusy, 0);
      }
    }

    void Received(object? sender, CoreWebView2WebMessageReceivedEventArgs eventArgs)
    {
      try
      {
        using var message = JsonDocument.Parse(eventArgs.WebMessageAsJson);
        var root = message.RootElement;
        if (!root.TryGetProperty("channel", out var channel) || channel.GetString() != "omyaru-live-capture" ||
            !root.TryGetProperty("token", out var messageToken) || messageToken.GetString() != token ||
            !root.TryGetProperty("action", out var action)) return;
        switch (action.GetString())
        {
          case "cancel":
            completion.TrySetResult(null);
            break;
          case "refresh-lens":
            var scrollX = root.TryGetProperty("scrollX", out var scrollXValue) ? scrollXValue.GetDouble() : 0D;
            var scrollY = root.TryGetProperty("scrollY", out var scrollYValue) ? scrollYValue.GetDouble() : 0D;
            _ = RefreshLensFrameAsync(scrollX, scrollY);
            break;
          case "complete":
            var operation = root.TryGetProperty("operation", out var operationValue)
                ? operationValue.GetString()
                : null;
            var selectedAction = operation switch
            {
              "save" => CaptureAction.SavePng,
              "copy" => CaptureAction.Copy,
              "bookshelf" => CaptureAction.AddToBookshelf,
              _ => fallbackAction,
            };
            var selection = new LiveCaptureSelection(
                root.GetProperty("x").GetDouble(),
                root.GetProperty("y").GetDouble(),
                root.GetProperty("width").GetDouble(),
                root.GetProperty("height").GetDouble(),
                root.TryGetProperty("folder", out var folder) ? folder.GetString() ?? catalog.ActiveFolder : catalog.ActiveFolder,
                selectedAction);
            completion.TrySetResult(selection.Width >= 2 && selection.Height >= 2 ? selection : null);
            break;
        }
      }
      catch
      {
        // Ignore unrelated or incomplete page messages while the selector is active.
      }
    }

    void Navigating(object? sender, CoreWebView2NavigationStartingEventArgs eventArgs) => completion.TrySetResult(null);
    view.CoreWebView2.WebMessageReceived += Received;
    view.CoreWebView2.NavigationStarting += Navigating;
    try
    {
      await InjectLiveCaptureOverlayAsync(
          view,
          token,
          catalog.Folders,
          catalog.ActiveFolder,
          showBookshelfPicker,
          lensFrame);
      return await completion.Task;
    }
    finally
    {
      view.CoreWebView2.WebMessageReceived -= Received;
      view.CoreWebView2.NavigationStarting -= Navigating;
      await RemoveLiveCaptureOverlayAsync(view, token);
    }
  }

  internal static async Task<Bitmap?> CapturePageRegionBitmapAsync(WebView2 view, LiveCaptureSelection selection)
  {
    if (view.CoreWebView2 is null || selection.Width < 2 || selection.Height < 2) return null;
    try
    {
      var initialState = await GetCaptureViewportStateAsync(view);
      if (initialState is null) return null;
      await Task.Delay(24);

      using var initialFrame = await CaptureViewportBitmapAsync(view) ?? await CaptureViewportViaDevToolsAsync(view);
      if (initialFrame is null || initialFrame.Width < 2 || initialFrame.Height < 2)
      {
        return await CaptureDomMediaRegionAsync(view, selection);
      }

      var scaleX = initialFrame.Width / Math.Max(1D, initialState.ViewportWidth);
      var scaleY = initialFrame.Height / Math.Max(1D, initialState.ViewportHeight);

      var fitsInCurrentViewport =
          selection.X >= initialState.ScrollX - 1.5 &&
          selection.Y >= initialState.ScrollY - 1.5 &&
          selection.X + selection.Width <= initialState.ScrollX + initialState.ViewportWidth + 1.5 &&
          selection.Y + selection.Height <= initialState.ScrollY + initialState.ViewportHeight + 1.5;

      if (fitsInCurrentViewport)
      {
        var cropped = CropViewportBitmap(
            initialFrame,
            selection.X - initialState.ScrollX,
            selection.Y - initialState.ScrollY,
            selection.Width,
            selection.Height,
            scaleX,
            scaleY);
        if (IsNearlyUniformColor(cropped))
        {
          using var devToolsFrame = await CaptureViewportViaDevToolsAsync(view);
          if (devToolsFrame is not null)
          {
            var devScaleX = devToolsFrame.Width / Math.Max(1D, initialState.ViewportWidth);
            var devScaleY = devToolsFrame.Height / Math.Max(1D, initialState.ViewportHeight);
            var altCropped = CropViewportBitmap(
                devToolsFrame,
                selection.X - initialState.ScrollX,
                selection.Y - initialState.ScrollY,
                selection.Width,
                selection.Height,
                devScaleX,
                devScaleY);
            if (!IsNearlyUniformColor(altCropped))
            {
              cropped.Dispose();
              return altCropped;
            }
            altCropped.Dispose();
          }
          var domFallback = await CaptureDomMediaRegionAsync(view, selection);
          if (domFallback is not null && !IsNearlyUniformColor(domFallback))
          {
            cropped.Dispose();
            return domFallback;
          }
          domFallback?.Dispose();
        }
        return cropped;
      }

      var outWidth = Math.Max(2, (int)Math.Round(selection.Width * scaleX));
      var outHeight = Math.Max(2, (int)Math.Round(selection.Height * scaleY));
      var stitched = new Bitmap(outWidth, outHeight, PixelFormat.Format32bppPArgb);
      try
      {
        using var graphics = Graphics.FromImage(stitched);
        graphics.CompositingMode = System.Drawing.Drawing2D.CompositingMode.SourceCopy;
        graphics.InterpolationMode = System.Drawing.Drawing2D.InterpolationMode.NearestNeighbor;
        graphics.PixelOffsetMode = System.Drawing.Drawing2D.PixelOffsetMode.Half;

        var selectionBottom = selection.Y + selection.Height;
        var currentDocY = selection.Y;
        var targetScrollX = selection.X >= initialState.ScrollX &&
                            selection.X + selection.Width <= initialState.ScrollX + initialState.ViewportWidth
            ? initialState.ScrollX
            : Math.Max(0D, selection.X);
        var previousScrollY = double.NaN;

        for (var sliceIndex = 0; sliceIndex < 48 && currentDocY < selectionBottom - 0.5; sliceIndex++)
        {
          var targetScrollY = Math.Max(0D, currentDocY);
          var sliceState = await ScrollAndSettleForCaptureAsync(view, targetScrollX, targetScrollY);
          if (sliceState is null) break;
          await Task.Delay(28);

          using var sliceFrame = await CaptureBestViewportFrameAsync(view);
          if (sliceFrame is null || IsNearlyUniformColor(sliceFrame)) break;

          var sliceScaleX = sliceFrame.Width / Math.Max(1D, sliceState.ViewportWidth);
          var sliceScaleY = sliceFrame.Height / Math.Max(1D, sliceState.ViewportHeight);

          var cssLeft = Math.Max(0D, selection.X - sliceState.ScrollX);
          var cssTop = currentDocY - sliceState.ScrollY;
          if (cssTop < 0D)
          {
            currentDocY -= cssTop;
            cssTop = 0D;
          }
          var availableCssHeight = sliceState.ViewportHeight - cssTop;
          var takeCssHeight = Math.Min(selectionBottom - currentDocY, availableCssHeight);
          if (takeCssHeight <= 0.5) break;

          var destTopPx = Math.Clamp((int)Math.Round((currentDocY - selection.Y) * scaleY), 0, outHeight - 1);
          var destBottomPx = Math.Clamp((int)Math.Round((currentDocY + takeCssHeight - selection.Y) * scaleY), destTopPx + 1, outHeight);
          var destHeightPx = destBottomPx - destTopPx;

          var srcLeftPx = Math.Clamp((int)Math.Round(cssLeft * sliceScaleX), 0, Math.Max(0, sliceFrame.Width - 1));
          var srcTopPx = Math.Clamp((int)Math.Round(cssTop * sliceScaleY), 0, Math.Max(0, sliceFrame.Height - 1));
          var srcWidthPx = Math.Clamp((int)Math.Round(selection.Width * sliceScaleX), 1, Math.Max(1, sliceFrame.Width - srcLeftPx));
          var srcHeightPx = Math.Clamp(destHeightPx, 1, Math.Max(1, sliceFrame.Height - srcTopPx));

          graphics.DrawImage(
              sliceFrame,
              new Rectangle(0, destTopPx, outWidth, destHeightPx),
              new Rectangle(srcLeftPx, srcTopPx, srcWidthPx, srcHeightPx),
              GraphicsUnit.Pixel);

          currentDocY += takeCssHeight;
          if (currentDocY < selectionBottom - 0.5 &&
              !double.IsNaN(previousScrollY) &&
              Math.Abs(sliceState.ScrollY - previousScrollY) < 0.5 &&
              cssTop > 0.5)
          {
            break;
          }
          previousScrollY = sliceState.ScrollY;
        }

        if (IsNearlyUniformColor(stitched))
        {
          var domFallback = await CaptureDomMediaRegionAsync(view, selection);
          if (domFallback is not null && !IsNearlyUniformColor(domFallback))
          {
            stitched.Dispose();
            return domFallback;
          }
          domFallback?.Dispose();
        }

        return stitched;
      }
      catch
      {
        stitched.Dispose();
        throw;
      }
      finally
      {
        _ = ScrollAndSettleForCaptureAsync(view, initialState.ScrollX, initialState.ScrollY);
      }
    }
    catch
    {
      return null;
    }
  }

  private static Bitmap CropViewportBitmap(
      Bitmap source,
      double cssLeft,
      double cssTop,
      double cssWidth,
      double cssHeight,
      double scaleX,
      double scaleY)
  {
    var srcLeft = Math.Clamp((int)Math.Round(cssLeft * scaleX), 0, Math.Max(0, source.Width - 1));
    var srcTop = Math.Clamp((int)Math.Round(cssTop * scaleY), 0, Math.Max(0, source.Height - 1));
    var srcWidth = Math.Clamp((int)Math.Round(cssWidth * scaleX), 1, Math.Max(1, source.Width - srcLeft));
    var srcHeight = Math.Clamp((int)Math.Round(cssHeight * scaleY), 1, Math.Max(1, source.Height - srcTop));
    var cropped = new Bitmap(srcWidth, srcHeight, PixelFormat.Format32bppPArgb);
    using var graphics = Graphics.FromImage(cropped);
    graphics.CompositingMode = System.Drawing.Drawing2D.CompositingMode.SourceCopy;
    graphics.InterpolationMode = System.Drawing.Drawing2D.InterpolationMode.NearestNeighbor;
    graphics.PixelOffsetMode = System.Drawing.Drawing2D.PixelOffsetMode.Half;
    graphics.DrawImage(
        source,
        new Rectangle(0, 0, srcWidth, srcHeight),
        new Rectangle(srcLeft, srcTop, srcWidth, srcHeight),
        GraphicsUnit.Pixel);
    return cropped;
  }

  private static async Task<CaptureViewportState?> GetCaptureViewportStateAsync(WebView2 view)
  {
    if (view.CoreWebView2 is null) return null;
    try
    {
      var json = await view.CoreWebView2.ExecuteScriptAsync("""
          (() => {
            document.getElementById('__omyaru_live_capture_host')?.remove();
            window.__omyaruHideFixedBars?.();
            const scroller = window.__omyaruActiveScroller;
            return {
              scrollX: scroller ? scroller.scrollLeft : window.scrollX,
              scrollY: scroller ? scroller.scrollTop : window.scrollY,
              viewportWidth: Math.max(1, window.innerWidth),
              viewportHeight: Math.max(1, window.innerHeight)
            };
          })()
          """);
      await Task.Delay(32);
      using var doc = JsonDocument.Parse(json);
      var root = doc.RootElement;
      if (root.ValueKind != JsonValueKind.Object) return null;
      return new CaptureViewportState(
          root.GetProperty("scrollX").GetDouble(),
          root.GetProperty("scrollY").GetDouble(),
          root.GetProperty("viewportWidth").GetDouble(),
          root.GetProperty("viewportHeight").GetDouble());
    }
    catch
    {
      return null;
    }
  }

  private static async Task<CaptureViewportState?> ScrollAndSettleForCaptureAsync(
      WebView2 view,
      double targetScrollX,
      double targetScrollY)
  {
    if (view.CoreWebView2 is null) return null;
    try
    {
      var xLiteral = targetScrollX.ToString(CultureInfo.InvariantCulture);
      var yLiteral = targetScrollY.ToString(CultureInfo.InvariantCulture);
      await view.CoreWebView2.ExecuteScriptAsync($$"""
          (() => {
            const scroller = window.__omyaruActiveScroller;
            if (scroller) {
              scroller.scrollLeft = {{xLiteral}};
              scroller.scrollTop = {{yLiteral}};
              scroller.dispatchEvent(new Event('scroll'));
            } else {
              window.scrollTo({{xLiteral}}, {{yLiteral}});
              window.dispatchEvent(new Event('scroll'));
            }
            for (const img of document.images) {
              try { if (img.loading === 'lazy') img.loading = 'eager'; } catch (_) {}
            }
            window.__omyaruHideFixedBars?.();
          })()
          """);
      CaptureViewportState? latestState = null;
      for (var attempt = 0; attempt < 8; attempt++)
      {
        await Task.Delay(32);
        var json = await view.CoreWebView2.ExecuteScriptAsync("""
            (() => {
              window.__omyaruHideFixedBars?.();
              const scroller = window.__omyaruActiveScroller;
              const visibleImages = [...document.images].filter(img => {
                const rect = img.getBoundingClientRect();
                return rect.bottom > 0 && rect.top < window.innerHeight && rect.width > 16 && rect.height > 16;
              });
              const ready = visibleImages.every(img => img.complete);
              return {
                ready,
                scrollX: scroller ? scroller.scrollLeft : window.scrollX,
                scrollY: scroller ? scroller.scrollTop : window.scrollY,
                viewportWidth: Math.max(1, window.innerWidth),
                viewportHeight: Math.max(1, window.innerHeight)
              };
            })()
            """);
        using var doc = JsonDocument.Parse(json);
        var root = doc.RootElement;
        if (root.ValueKind != JsonValueKind.Object) continue;
        latestState = new CaptureViewportState(
            root.GetProperty("scrollX").GetDouble(),
            root.GetProperty("scrollY").GetDouble(),
            root.GetProperty("viewportWidth").GetDouble(),
            root.GetProperty("viewportHeight").GetDouble());
        if (root.TryGetProperty("ready", out var readyProp) && readyProp.GetBoolean())
          break;
      }
      await Task.Delay(24);
      return latestState;
    }
    catch
    {
      return null;
    }
  }

  private static async Task<Bitmap?> CaptureViewportViaDevToolsAsync(WebView2 view)
  {
    if (view.CoreWebView2 is null) return null;
    try
    {
      var parameters = JsonSerializer.Serialize(new
      {
        format = "png",
        fromSurface = true,
        captureBeyondViewport = false,
      });
      var resultJson = await view.CoreWebView2
          .CallDevToolsProtocolMethodAsync("Page.captureScreenshot", parameters)
          .WaitAsync(TimeSpan.FromMilliseconds(850));
      using var result = JsonDocument.Parse(resultJson);
      var encoded = result.RootElement.GetProperty("data").GetString();
      if (string.IsNullOrWhiteSpace(encoded)) return null;
      using var stream = new MemoryStream(Convert.FromBase64String(encoded));
      using var source = Image.FromStream(stream);
      return new Bitmap(source);
    }
    catch
    {
      return null;
    }
  }

  private static async Task<Bitmap?> CaptureBestViewportFrameAsync(WebView2 view)
  {
    var preview = await CaptureViewportBitmapAsync(view);
    if (preview is not null && !IsNearlyUniformColor(preview))
      return preview;
    var devTools = await CaptureViewportViaDevToolsAsync(view);
    if (devTools is not null && !IsNearlyUniformColor(devTools))
    {
      preview?.Dispose();
      return devTools;
    }
    if (preview is not null)
    {
      devTools?.Dispose();
      return preview;
    }
    return devTools;
  }

  internal static bool IsNearlyUniformColor(Bitmap bitmap)
  {
    if (bitmap.Width < 2 || bitmap.Height < 2) return true;
    var first = bitmap.GetPixel(bitmap.Width / 2, bitmap.Height / 2);
    const int stepsX = 8;
    const int stepsY = 10;
    for (var iy = 0; iy < stepsY; iy++)
    {
      var y = Math.Clamp((int)((iy + 0.5) * bitmap.Height / stepsY), 0, bitmap.Height - 1);
      for (var ix = 0; ix < stepsX; ix++)
      {
        var x = Math.Clamp((int)((ix + 0.5) * bitmap.Width / stepsX), 0, bitmap.Width - 1);
        var c = bitmap.GetPixel(x, y);
        if (Math.Abs(c.R - first.R) > 3 ||
            Math.Abs(c.G - first.G) > 3 ||
            Math.Abs(c.B - first.B) > 3)
        {
          return false;
        }
      }
    }
    return true;
  }

  private static async Task<Bitmap?> CaptureDomMediaRegionAsync(WebView2 view, LiveCaptureSelection selection)
  {
    if (view.CoreWebView2 is null) return null;
    try
    {
      var selX = selection.X.ToString(CultureInfo.InvariantCulture);
      var selY = selection.Y.ToString(CultureInfo.InvariantCulture);
      var selW = selection.Width.ToString(CultureInfo.InvariantCulture);
      var selH = selection.Height.ToString(CultureInfo.InvariantCulture);
      var json = await view.CoreWebView2.ExecuteScriptAsync($$"""
          (() => {
            try {
              const scroller = window.__omyaruActiveScroller;
              const sx = scroller ? scroller.scrollLeft : window.scrollX;
              const sy = scroller ? scroller.scrollTop : window.scrollY;
              const selLeft = {{selX}};
              const selTop = {{selY}};
              const selWidth = {{selW}};
              const selHeight = {{selH}};
              const dpr = Math.min(2, Math.max(1, window.devicePixelRatio || 1));
              const outCanvas = document.createElement('canvas');
              outCanvas.width = Math.max(2, Math.round(selWidth * dpr));
              outCanvas.height = Math.max(2, Math.round(selHeight * dpr));
              const ctx = outCanvas.getContext('2d');
              if (!ctx) return '';
              ctx.scale(dpr, dpr);
              const bodyBg = getComputedStyle(document.body || document.documentElement).backgroundColor || '#1e1f21';
              ctx.fillStyle = bodyBg;
              ctx.fillRect(0, 0, selWidth, selHeight);
              let drawn = 0;
              for (const node of document.querySelectorAll('img, canvas')) {
                const style = getComputedStyle(node);
                if (style.display === 'none' || style.visibility === 'hidden' || Number.parseFloat(style.opacity) === 0) continue;
                const rect = node.getBoundingClientRect();
                if (rect.width < 8 || rect.height < 8) continue;
                const docLeft = rect.left + sx;
                const docTop = rect.top + sy;
                if (docLeft + rect.width <= selLeft || docLeft >= selLeft + selWidth ||
                    docTop + rect.height <= selTop || docTop >= selTop + selHeight) continue;
                try {
                  ctx.drawImage(node, docLeft - selLeft, docTop - selTop, rect.width, rect.height);
                  drawn++;
                } catch (_) {}
              }
              if (drawn === 0) return '';
              return outCanvas.toDataURL('image/png');
            } catch (_) {
              return '';
            }
          })()
          """);
      var dataUrl = JsonSerializer.Deserialize<string>(json);
      if (string.IsNullOrWhiteSpace(dataUrl)) return null;
      const string prefix = "data:image/png;base64,";
      if (!dataUrl.StartsWith(prefix, StringComparison.OrdinalIgnoreCase)) return null;
      var bytes = Convert.FromBase64String(dataUrl[prefix.Length..]);
      using var stream = new MemoryStream(bytes);
      using var source = Image.FromStream(stream);
      return new Bitmap(source);
    }
    catch
    {
      return null;
    }
  }

  private static async Task<string?> CaptureLiveLensFrameAsync(WebView2 view)
  {
    if (view.CoreWebView2 is null) return null;
    try
    {
      // The lens uses one already-composited viewport frame and moves it entirely
      // in JavaScript. Capturing on every pointer event caused both visible flashes
      // and a several-frame delay on image-heavy pages.
      using var previewStream = new MemoryStream();
      await view.CoreWebView2.CapturePreviewAsync(CoreWebView2CapturePreviewImageFormat.Png, previewStream);
      return "data:image/png;base64," + Convert.ToBase64String(previewStream.ToArray());
    }
    catch
    {
      return null;
    }
  }

  private static async Task InjectLiveCaptureOverlayAsync(
      WebView2 view,
      string token,
      IReadOnlyList<string> folders,
      string activeFolder,
      bool showBookshelfPicker,
      string? lensFrame)
  {
    if (view.CoreWebView2 is null) return;
    var tokenLiteral = JsonSerializer.Serialize(token);
    var foldersLiteral = JsonSerializer.Serialize(folders);
    var activeFolderLiteral = JsonSerializer.Serialize(activeFolder);
    var showBookshelfPickerLiteral = showBookshelfPicker ? "true" : "false";
    var lensFrameLiteral = JsonSerializer.Serialize(lensFrame ?? string.Empty);
    await view.CoreWebView2.ExecuteScriptAsync($$"""
        (() => {
          window.__omyaruRemoveCaptureOverlay?.();
          const token = {{tokenLiteral}};
          const shelfFolders = {{foldersLiteral}};
          const activeShelf = {{activeFolderLiteral}};
          const showBookshelfPicker = {{showBookshelfPickerLiteral}};
          const lensFrame = {{lensFrameLiteral}};
          const host = document.createElement('div');
          host.id = '__omyaru_live_capture_host';
          host.style.cssText = 'position:fixed;inset:0;z-index:2147483647;pointer-events:auto;';
          document.documentElement.appendChild(host);
          const shadow = host.attachShadow({ mode: 'open' });
          shadow.innerHTML = `
            <style>
              * { box-sizing: border-box; }
              #stage { position:fixed; inset:0; cursor:crosshair; user-select:none; touch-action:none; font-family:"Segoe UI",sans-serif; }
              #selection { display:none; position:fixed; border:1px solid #41ceff; border-radius:3px; pointer-events:none; z-index:4; }
              .mask { display:none; position:fixed; background:rgba(3,7,12,.48); pointer-events:none; z-index:2; }
              #guide-x,#guide-y { display:none; position:fixed; background:rgba(65,206,255,.9); pointer-events:none; z-index:5; }
              #guide-x { left:0; right:0; height:1px; }
              #guide-y { top:0; bottom:0; width:1px; }
              #lens { display:none; position:fixed; width:122px; height:122px; border:3px solid white; border-radius:50%; overflow:hidden; background:#191d22; box-shadow:0 8px 28px rgba(0,0,0,.45); pointer-events:none; z-index:8; contain:strict; transform:translateZ(0); backface-visibility:hidden; }
              #lens img { position:absolute; display:block; max-width:none; max-height:none; image-rendering:auto; transform:translateZ(0); will-change:left,top; }
              #lens::before,#lens::after { content:""; position:absolute; background:#41ceff; z-index:2; pointer-events:none; }
              #lens::before { left:0; right:0; top:50%; height:1px; }
              #lens::after { top:0; bottom:0; left:50%; width:1px; }
              #size { display:none; position:fixed; min-width:116px; height:34px; padding:0 16px; border-radius:17px; background:#328fff; color:white; font:600 14px/34px "Segoe UI",sans-serif; text-align:center; box-shadow:0 5px 18px rgba(0,0,0,.32); pointer-events:none; z-index:9; }
              #hint { position:fixed; top:18px; left:50%; transform:translateX(-50%); min-width:420px; padding:12px 18px; border:1px solid #4a525c; border-radius:20px; background:rgba(45,50,57,.97); color:#f4f0ee; text-align:center; box-shadow:0 12px 34px rgba(0,0,0,.4); pointer-events:none; z-index:10; }
              #hint strong { display:block; font:700 15px/20px "Segoe UI",sans-serif; }
              #hint span { color:#b7bcc5; font:400 12px/18px "Segoe UI",sans-serif; }
              #actions { position:fixed; right:22px; bottom:22px; display:flex; align-items:center; gap:8px; padding:7px; border:1px solid #4a525c; border-radius:23px; background:rgba(36,41,47,.98); box-shadow:0 12px 34px rgba(0,0,0,.45); z-index:11; }
              select { height:42px; min-width:170px; max-width:260px; padding:0 36px 0 15px; border:1px solid #4a525c; border-radius:19px; background:#30363d; color:#f4f0ee; font:600 14px "Segoe UI",sans-serif; outline:none; }
              button { height:42px; min-width:88px; padding:0 19px; border:1px solid #4a525c; border-radius:19px; background:#30363d; color:#f4f0ee; font:600 14px "Segoe UI",sans-serif; cursor:pointer; }
              button.primary { border-color:#5b92ff; background:#5b92ff; color:white; }
              button:disabled { opacity:.45; cursor:default; }
            </style>
            <div id="stage">
              <div id="mask-top" class="mask"></div><div id="mask-left" class="mask"></div>
              <div id="mask-right" class="mask"></div><div id="mask-bottom" class="mask"></div>
              <div id="selection"></div><div id="guide-x"></div><div id="guide-y"></div>
              <div id="lens"><img alt=""></div><div id="size"></div>
            </div>
            <div id="hint"><strong>실시간 페이지에서 캡처할 영역을 드래그하세요</strong><span>마우스 휠로 이동하거나 가장자리까지 드래그하면 페이지가 계속 스크롤됩니다.</span></div>
            <div id="actions"><select id="folder" aria-label="저장할 책장"></select><button id="cancel">취소</button><button id="copy" disabled>복사</button><button id="save" disabled>저장</button><button id="complete" class="primary" disabled>책장에 저장</button></div>
          `;

          const stage = shadow.getElementById('stage');
          const selectionElement = shadow.getElementById('selection');
          const guideX = shadow.getElementById('guide-x');
          const guideY = shadow.getElementById('guide-y');
          const lens = shadow.getElementById('lens');
          const lensImage = lens.querySelector('img');
          const sizeBadge = shadow.getElementById('size');
          const complete = shadow.getElementById('complete');
          const copyButton = shadow.getElementById('copy');
          const saveButton = shadow.getElementById('save');
          const folderSelect = shadow.getElementById('folder');
          folderSelect.style.display = showBookshelfPicker ? '' : 'none';
          for (const folder of shelfFolders) {
            const option = document.createElement('option');
            option.value = folder;
            option.textContent = `▤  ${folder}`;
            option.selected = folder === activeShelf;
            folderSelect.appendChild(option);
          }
          const masks = {
            top: shadow.getElementById('mask-top'), left: shadow.getElementById('mask-left'),
            right: shadow.getElementById('mask-right'), bottom: shadow.getElementById('mask-bottom')
          };
          let start = null;
          let end = null;
          let dragging = false;
          let lastClient = { x: 0, y: 0 };
          let animationFrame = 0;
          let lensRefreshTimer = 0;
          const scroller = window.__omyaruActiveScroller || null;
          const getScrollX = () => scroller ? scroller.scrollLeft : window.scrollX;
          const getScrollY = () => scroller ? scroller.scrollTop : window.scrollY;
          const scrollByDelta = (dx, dy) => {
            if (scroller) scroller.scrollBy({ left: dx, top: dy, behavior: 'auto' });
            else window.scrollBy({ left: dx, top: dy, behavior: 'auto' });
          };
          let lensOriginScrollX = getScrollX();
          let lensOriginScrollY = getScrollY();
          const lensZoom = 2.5;
          if (lensFrame) lensImage.src = lensFrame;

          const post = payload => chrome.webview.postMessage({ channel:'omyaru-live-capture', token, ...payload });
          const pagePoint = (clientX, clientY) => ({ x: clientX + getScrollX(), y: clientY + getScrollY() });
          const selected = () => {
            if (!start || !end) return null;
            const left = Math.min(start.x, end.x);
            const top = Math.min(start.y, end.y);
            return { left, top, width:Math.abs(end.x - start.x), height:Math.abs(end.y - start.y) };
          };
          const placeNearPointer = (element, width, height, x, y, gap) => {
            let left = x + gap;
            let top = y + gap;
            if (left + width > innerWidth - 12) left = x - width - gap;
            if (top + height > innerHeight - 12) top = y - height - gap;
            element.style.left = `${Math.max(12, Math.min(innerWidth - width - 12, left))}px`;
            element.style.top = `${Math.max(12, Math.min(innerHeight - height - 12, top))}px`;
          };
          const render = () => {
            const rect = selected();
            if (!rect || rect.width < 1 || rect.height < 1) {
              selectionElement.style.display = 'none';
              sizeBadge.style.display = 'none';
              complete.disabled = copyButton.disabled = saveButton.disabled = true;
              return;
            }
            const viewportLeft = rect.left - getScrollX();
            const viewportTop = rect.top - getScrollY();
            selectionElement.style.display = 'block';
            selectionElement.style.left = `${viewportLeft}px`;
            selectionElement.style.top = `${viewportTop}px`;
            selectionElement.style.width = `${rect.width}px`;
            selectionElement.style.height = `${rect.height}px`;
            complete.disabled = copyButton.disabled = saveButton.disabled = rect.width < 2 || rect.height < 2;
            sizeBadge.textContent = `▣  ${Math.round(rect.width).toLocaleString()} × ${Math.round(rect.height).toLocaleString()}`;
            sizeBadge.style.display = 'block';
            placeNearPointer(sizeBadge, Math.max(116, sizeBadge.offsetWidth), 34, lastClient.x, lastClient.y, 14);

            const visibleLeft = Math.max(0, viewportLeft);
            const visibleTop = Math.max(0, viewportTop);
            const visibleRight = Math.min(innerWidth, viewportLeft + rect.width);
            const visibleBottom = Math.min(innerHeight, viewportTop + rect.height);
            const showMask = !dragging && visibleRight > visibleLeft && visibleBottom > visibleTop;
            for (const mask of Object.values(masks)) mask.style.display = showMask ? 'block' : 'none';
            if (showMask) {
              Object.assign(masks.top.style, { left:'0px', top:'0px', width:'100vw', height:`${visibleTop}px` });
              Object.assign(masks.bottom.style, { left:'0px', top:`${visibleBottom}px`, width:'100vw', height:`${Math.max(0, innerHeight-visibleBottom)}px` });
              Object.assign(masks.left.style, { left:'0px', top:`${visibleTop}px`, width:`${visibleLeft}px`, height:`${visibleBottom-visibleTop}px` });
              Object.assign(masks.right.style, { left:`${visibleRight}px`, top:`${visibleTop}px`, width:`${Math.max(0, innerWidth-visibleRight)}px`, height:`${visibleBottom-visibleTop}px` });
            }
          };
          const updateLens = (clientX, clientY) => {
            const centerX = lens.clientWidth / 2;
            const centerY = lens.clientHeight / 2;
            const sourceX = clientX + getScrollX() - lensOriginScrollX;
            const sourceY = clientY + getScrollY() - lensOriginScrollY;
            lensImage.style.width = `${innerWidth * lensZoom}px`;
            lensImage.style.height = `${innerHeight * lensZoom}px`;
            lensImage.style.left = `${centerX - sourceX * lensZoom}px`;
            lensImage.style.top = `${centerY - sourceY * lensZoom}px`;
          };
          const scheduleLensRefresh = () => {
            clearTimeout(lensRefreshTimer);
            lensRefreshTimer = setTimeout(() => post({
              action:'refresh-lens',
              scrollX:getScrollX(),
              scrollY:getScrollY()
            }), 280);
          };
          const updatePointer = (clientX, clientY, updateSelection = true) => {
            lastClient = { x:clientX, y:clientY };
            const point = pagePoint(clientX, clientY);
            if (updateSelection) end = point;
            guideX.style.display = guideY.style.display = 'block';
            guideX.style.top = `${clientY}px`;
            guideY.style.left = `${clientX}px`;
            lens.style.display = 'block';
            placeNearPointer(lens, 122, 122, clientX, clientY, 28);
            updateLens(clientX, clientY);
            render();
          };
          const autoScroll = () => {
            if (!dragging) return;
            const edge = 64;
            let deltaX = 0;
            let deltaY = 0;
            if (lastClient.x < edge) deltaX = -Math.ceil((edge-lastClient.x)/edge*34)-6;
            else if (lastClient.x > innerWidth-edge) deltaX = Math.ceil((lastClient.x-(innerWidth-edge))/edge*34)+6;
            if (lastClient.y < edge) deltaY = -Math.ceil((edge-lastClient.y)/edge*38)-7;
            else if (lastClient.y > innerHeight-edge) deltaY = Math.ceil((lastClient.y-(innerHeight-edge))/edge*38)+7;
            if (deltaX || deltaY) {
              scrollByDelta(deltaX, deltaY);
              updatePointer(lastClient.x, lastClient.y);
            }
            animationFrame = requestAnimationFrame(autoScroll);
          };
          const pointerDown = event => {
            if (event.button === 2) {
              event.preventDefault();
              event.stopPropagation();
              cancel();
              return;
            }
            if (event.button !== 0) return;
            event.preventDefault();
            dragging = true;
            start = pagePoint(event.clientX, event.clientY);
            end = start;
            lastClient = { x:event.clientX, y:event.clientY };
            stage.setPointerCapture(event.pointerId);
            updatePointer(event.clientX, event.clientY);
            cancelAnimationFrame(animationFrame);
            animationFrame = requestAnimationFrame(autoScroll);
          };
          const pointerMove = event => {
            if (dragging) event.preventDefault();
            updatePointer(event.clientX, event.clientY, dragging);
          };
          const pointerUp = event => {
            if (!dragging) return;
            event.preventDefault();
            updatePointer(event.clientX, event.clientY);
            dragging = false;
            cancelAnimationFrame(animationFrame);
            render();
          };
          const pointerLeave = () => {
            if (dragging) return;
            guideX.style.display = guideY.style.display = lens.style.display = 'none';
          };
          const wheel = event => {
            event.preventDefault();
            scrollByDelta(event.deltaX, event.deltaY);
            render();
          };
          const submit = operation => {
            const rect = selected();
            if (!rect || rect.width < 2 || rect.height < 2) return;
            complete.disabled = copyButton.disabled = saveButton.disabled = true;
            host.style.display = 'none';
            requestAnimationFrame(() => requestAnimationFrame(() => post({
              action:'complete',
              operation,
              x:rect.left,
              y:rect.top,
              width:rect.width,
              height:rect.height,
              folder:folderSelect.value
            })));
          };
          const cancel = () => post({ action:'cancel' });
          const keyDown = event => {
            if (event.key === 'Escape') { event.preventDefault(); cancel(); }
            else if (event.key === 'Enter' && !complete.disabled) { event.preventDefault(); submit('bookshelf'); }
          };
          const contextMenu = event => {
            event.preventDefault();
            event.stopPropagation();
            cancel();
          };
          const scroll = () => {
            render();
            updateLens(lastClient.x, lastClient.y);
            scheduleLensRefresh();
          };
          stage.addEventListener('pointerdown', pointerDown);
          stage.addEventListener('pointermove', pointerMove);
          stage.addEventListener('pointerleave', pointerLeave);
          stage.addEventListener('pointerup', pointerUp);
          stage.addEventListener('pointercancel', pointerUp);
          stage.addEventListener('wheel', wheel, { passive:false });
          host.addEventListener('contextmenu', contextMenu, true);
          shadow.getElementById('cancel').addEventListener('click', cancel);
          copyButton.addEventListener('click', () => submit('copy'));
          saveButton.addEventListener('click', () => submit('save'));
          complete.addEventListener('click', () => submit('bookshelf'));
          window.addEventListener('keydown', keyDown, true);
          window.addEventListener('scroll', scroll, { passive:true });
          scroller?.addEventListener('scroll', scroll, { passive:true });

          window.__omyaruPrepareCaptureLensFrame = requestedToken => {
            if (requestedToken !== token) return;
            host.style.opacity = '0';
          };
          window.__omyaruRestoreCaptureLensFrame = requestedToken => {
            if (requestedToken !== token) return;
            host.style.opacity = '1';
          };
          window.__omyaruSetCaptureLensFrame = (requestedToken, dataUrl, originX, originY) => {
            if (requestedToken !== token || !dataUrl) return;
            lensOriginScrollX = originX;
            lensOriginScrollY = originY;
            lensImage.src = dataUrl;
            host.style.opacity = '1';
            updateLens(lastClient.x, lastClient.y);
          };
          updatePointer(innerWidth / 2, innerHeight / 2, false);

          window.__omyaruRemoveCaptureOverlay = requestedToken => {
            if (requestedToken && requestedToken !== token) return;
            cancelAnimationFrame(animationFrame);
            clearTimeout(lensRefreshTimer);
            window.removeEventListener('keydown', keyDown, true);
            window.removeEventListener('scroll', scroll);
            scroller?.removeEventListener('scroll', scroll);
            host.remove();
            delete window.__omyaruPrepareCaptureLensFrame;
            delete window.__omyaruRestoreCaptureLensFrame;
            delete window.__omyaruSetCaptureLensFrame;
            delete window.__omyaruRemoveCaptureOverlay;
          };
        })();
        """);
  }

  private static async Task RemoveLiveCaptureOverlayAsync(WebView2 view, string? token = null)
  {
    if (view.CoreWebView2 is null) return;
    try
    {
      var tokenLiteral = token is null ? "undefined" : JsonSerializer.Serialize(token);
      await view.CoreWebView2.ExecuteScriptAsync($$"""
          window.__omyaruRemoveCaptureOverlay?.({{tokenLiteral}});
          """);
    }
    catch
    {
      // The page may already have navigated away from the injected selector.
    }
  }

  private static async Task PreparePageForCaptureAsync(WebView2 view)
  {
    if (view.CoreWebView2 is null) return;
    await view.CoreWebView2.ExecuteScriptAsync("""
        (() => {
          try {
            if (window.__omyaruCaptureState) {
              const previous = window.__omyaruCaptureState;
              previous.items?.forEach(({ element, style }) => {
                if (!element) return;
                if (style === null) element.removeAttribute('style');
                else element.setAttribute('style', style);
              });
              previous.styleElement?.remove();
            }

            const detectPrimaryScroller = () => {
              const doc = document.scrollingElement || document.documentElement;
              const docRange = doc ? Math.max(0, doc.scrollHeight - window.innerHeight) : 0;
              let bestEl = null;
              let bestRange = docRange;
              for (const el of document.querySelectorAll('div, main, section, article')) {
                const style = getComputedStyle(el);
                if (style.overflowY !== 'auto' && style.overflowY !== 'scroll' && style.overflowY !== 'overlay') continue;
                const range = el.scrollHeight - el.clientHeight;
                if (range <= 8) continue;
                const rect = el.getBoundingClientRect();
                if (rect.width < window.innerWidth * 0.4 || rect.height < window.innerHeight * 0.4) continue;
                if (range > bestRange) {
                  bestRange = range;
                  bestEl = el;
                }
              }
              return bestEl;
            };
            const scroller = detectPrimaryScroller();
            window.__omyaruActiveScroller = scroller;

            for (const img of document.images) {
              try {
                if (img.loading === 'lazy') img.loading = 'eager';
              } catch (_) {}
            }

            const state = {
              items: [],
              styleElement: null,
              scroller,
              scrollX: scroller ? scroller.scrollLeft : window.scrollX,
              scrollY: scroller ? scroller.scrollTop : window.scrollY
            };
            window.__omyaruCaptureState = state;
            const remembered = new WeakSet();
            const hide = (element) => {
              if (!(element instanceof HTMLElement) || remembered.has(element)) return;
              remembered.add(element);
              state.items.push({ element, style: element.getAttribute('style') });
              // Keep the original layout stable while the selector is open. Collapsing
              // ad/sticky nodes forced a full reflow and made the page visibly flash.
              element.style.setProperty('visibility', 'hidden', 'important');
              element.style.setProperty('pointer-events', 'none', 'important');
            };

            const style = document.createElement('style');
            style.id = '__omyaru_capture_style';
            style.textContent = `
              ins.adsbygoogle,
              [data-ad-slot], [data-ad-client],
              iframe[src*="doubleclick"], iframe[src*="googlesyndication"],
              iframe[src*="adservice"], iframe[src*="adnxs"],
              [class~="advertisement"], [class*="advertisement"],
              [id*="advertisement"], [class^="ad-banner"], [id^="ad-banner"],
              [class^="ads-banner"], [id^="ads-banner"] {
                visibility: hidden !important;
                pointer-events: none !important;
              }
            `;
            (document.head || document.documentElement).appendChild(style);
            state.styleElement = style;

            const pageHost = location.hostname.toLowerCase();
            const isTokiReader = /^toki\d*\./i.test(pageHost) || /(^|\.)newtoki\./i.test(pageHost);
            const adWords = /(casino|bet(ting)?|banner|advert|ads?|sportsbook|jackpot|slot|토토|카지노|배팅|베팅|가입|첫충|충전)/i;
            const imageCandidates = [...document.images];
            const bannerImages = new WeakSet();
            const bannerOwners = [];
            for (const image of imageCandidates) {
              const anchor = image.closest('a');
              const owner = anchor || image;
              const rectangle = image.getBoundingClientRect();
              const width = image.naturalWidth || rectangle.width;
              const height = image.naturalHeight || rectangle.height;
              const ratio = height > 0 ? width / height : 0;
              const sourceText = `${image.currentSrc || image.src || ''} ${image.alt || ''} ${anchor?.href || ''}`;
              let externalLink = false;
              if (anchor?.href) {
                try { externalLink = new URL(anchor.href, location.href).hostname !== pageHost; } catch (_) {}
              }
              const compactWideBanner = ratio >= 2.15 && height > 0 && height <= 320;
              if (isTokiReader && compactWideBanner && (externalLink || adWords.test(sourceText))) {
                bannerImages.add(image);
                bannerOwners.push(owner);
              }
            }

            if (isTokiReader && bannerOwners.length > 1) {
              const wrapperCounts = new Map();
              for (const owner of bannerOwners) {
                let wrapper = owner.parentElement;
                for (let depth = 0; wrapper && depth < 4 && wrapper !== document.body; depth += 1, wrapper = wrapper.parentElement) {
                  wrapperCounts.set(wrapper, (wrapperCounts.get(wrapper) || 0) + 1);
                }
              }
              const wrappers = [...wrapperCounts.entries()]
                .sort((left, right) => right[0].contains(left[0]) ? -1 : left[0].contains(right[0]) ? 1 : 0);
              for (const [wrapper, count] of wrappers) {
                if (count < 2) continue;
                const images = [...wrapper.querySelectorAll('img')];
                const bannerCount = images.filter(image => bannerImages.has(image)).length;
                if (bannerCount >= 2 && bannerCount / Math.max(1, images.length) >= 0.65) hide(wrapper);
              }
            }
            for (const owner of bannerOwners) hide(owner);

            if (isTokiReader) {
              const adAnchors = [];
              for (const anchor of document.querySelectorAll('a[href]')) {
                const rectangle = anchor.getBoundingClientRect();
                const ratio = rectangle.height > 0 ? rectangle.width / rectangle.height : 0;
                let externalLink = false;
                try { externalLink = new URL(anchor.href, location.href).hostname !== pageHost; } catch (_) {}
                if (rectangle.width >= 180 && rectangle.height > 0 && rectangle.height <= 260 &&
                    ratio >= 2.15 && (externalLink || adWords.test(`${anchor.href} ${anchor.textContent || ''}`))) {
                  adAnchors.push(anchor);
                }
              }
              for (const anchor of adAnchors) hide(anchor);
            }

            const hideFixedBars = () => {
              const positionedCandidates = new Set([
                ...(document.body?.children || []),
                ...document.querySelectorAll('header, nav, footer, aside, [class*="header" i], [class*="footer" i], [class*="navbar" i], [class*="toolbar" i], [class*="floating" i], [style*="position: fixed" i], [style*="position:fixed" i], [style*="position: sticky" i], [style*="position:sticky" i]')
              ]);
              const maxBarHeight = Math.min(180, innerHeight * 0.24);
              const maxSideWidth = Math.min(240, innerWidth * 0.24);
              for (const element of positionedCandidates) {
                if (!(element instanceof HTMLElement) || element.id === '__omyaru_live_capture_host') continue;
                if (element === scroller || (scroller && element.contains(scroller))) continue;
                const computed = getComputedStyle(element);
                if (computed.position !== 'fixed' && computed.position !== 'sticky') continue;
                if (element.scrollHeight > element.clientHeight + 32) continue;
                const rectangle = element.getBoundingClientRect();
                if (rectangle.width <= 0 || rectangle.height <= 0) continue;
                const isThinBarOrWidget = rectangle.height <= maxBarHeight || rectangle.width <= maxSideWidth;
                if (!isThinBarOrWidget) continue;
                const containsLargeMedia = [...element.querySelectorAll('img, canvas')].some(media => {
                  const r = media.getBoundingClientRect();
                  return r.width >= 240 && r.height >= 200;
                });
                if (containsLargeMedia) continue;
                hide(element);
              }
            };
            window.__omyaruHideFixedBars = hideFixedBars;
            hideFixedBars();

            if (scroller) {
              scroller.scrollLeft = state.scrollX;
              scroller.scrollTop = state.scrollY;
            } else {
              window.scrollTo(state.scrollX, state.scrollY);
            }
            return { hidden: state.items.length, host: pageHost };
          } catch (_) {
            return { hidden: 0 };
          }
        })();
        """);
    await Task.Delay(24);
  }

  private static async Task RestorePageAfterCaptureAsync(WebView2 view)
  {
    if (view.CoreWebView2 is null) return;
    try
    {
      await view.CoreWebView2.ExecuteScriptAsync("""
          (() => {
            const state = window.__omyaruCaptureState;
            if (!state) return;
            state.items?.forEach(({ element, style }) => {
              if (!element) return;
              if (style === null) element.removeAttribute('style');
              else element.setAttribute('style', style);
            });
            state.styleElement?.remove();
            if (state.scroller) {
              state.scroller.scrollLeft = state.scrollX || 0;
              state.scroller.scrollTop = state.scrollY || 0;
            } else {
              window.scrollTo(state.scrollX || 0, state.scrollY || 0);
            }
            delete window.__omyaruCaptureState;
            delete window.__omyaruActiveScroller;
            delete window.__omyaruHideFixedBars;
          })();
          """);
    }
    catch
    {
      // Navigation can complete while the selector is being prepared. In that case the old document is gone already.
    }
  }

  private static async Task<Bitmap?> CaptureViewportBitmapAsync(WebView2 view)
  {
    try
    {
      using var stream = new MemoryStream();
      await view.CoreWebView2!
          .CapturePreviewAsync(CoreWebView2CapturePreviewImageFormat.Png, stream)
          .WaitAsync(TimeSpan.FromMilliseconds(850));
      stream.Position = 0;
      using var source = Image.FromStream(stream);
      return new Bitmap(source);
    }
    catch
    {
      return null;
    }
  }

  private void SaveCapturePng(Image bitmap)
  {
    var directory = _preferences.LastCaptureDirectory;
    if (string.IsNullOrWhiteSpace(directory) || !Directory.Exists(directory))
    {
      using var picker = new FolderBrowserDialog
      {
        Description = "Omyaru 화면 캡처를 저장할 폴더를 선택하세요. 다음부터 이 경로를 사용합니다.",
        UseDescriptionForTitle = true,
        InitialDirectory = Environment.GetFolderPath(Environment.SpecialFolder.MyPictures),
      };
      if (picker.ShowDialog(this) != DialogResult.OK) return;
      directory = picker.SelectedPath;
      _preferences.LastCaptureDirectory = directory;
      DesktopPreferencesStore.Save(_preferences);
    }
    Directory.CreateDirectory(directory);
    var title = _state.Tabs.FirstOrDefault(tab => tab.Id == _state.ActiveTabId)?.Title ?? "capture";
    var safeTitle = new string(title.Select(character => Path.GetInvalidFileNameChars().Contains(character) ? '_' : character).ToArray());
    safeTitle = safeTitle.Trim();
    if (safeTitle.Length > 48) safeTitle = safeTitle[..48];
    if (safeTitle.Length == 0) safeTitle = "capture";
    var path = Path.Combine(directory, $"{DateTime.Now:yyyyMMdd-HHmmss}-{safeTitle}.png");
    bitmap.Save(path, System.Drawing.Imaging.ImageFormat.Png);
    _syncStatus = "PNG 저장 · " + path;
    UpdateStatusText();
  }

  private void ShowBookshelf()
  {
    if (_bookshelfForm is { IsDisposed: false })
    {
      if (_bookshelfForm.WindowState == FormWindowState.Minimized)
        _bookshelfForm.WindowState = FormWindowState.Normal;
      _bookshelfForm.ReloadCatalogFromExternalChange();
      _bookshelfForm.BringToFront();
      _bookshelfForm.Activate();
      return;
    }
    var bookshelf = new BookshelfForm();
    bookshelf.FormClosed += (sender, _) =>
    {
      if (ReferenceEquals(_bookshelfForm, sender))
        _bookshelfForm = null;
    };
    _bookshelfForm = bookshelf;
    bookshelf.Show();
  }

  private void ShowHistory()
  {
    using var dialog = new Form { Text = "Omyaru Browser · 방문 기록", Size = new Size(880, 660),
        MinimumSize = new Size(620, 420), StartPosition = FormStartPosition.CenterParent,
        BackColor = Chrome, ForeColor = TextColor, Font = new Font("Segoe UI", 10F), Padding = new Padding(18) };
    var search = new TextBox { Dock = DockStyle.Top, Height = 38, PlaceholderText = "제목 또는 주소 검색" };
    var list = new ListView { Dock = DockStyle.Fill, View = View.Details, FullRowSelect = true,
        BackColor = ChromeRaised, ForeColor = TextColor, BorderStyle = BorderStyle.None };
    list.Columns.Add("제목", 330); list.Columns.Add("주소", 360); list.Columns.Add("방문", 125);
    void Refresh()
    {
      list.BeginUpdate(); list.Items.Clear();
      foreach (var entry in BrowserHistory.Load().Where(x => x.Title.Contains(search.Text, StringComparison.OrdinalIgnoreCase)
          || x.Url.Contains(search.Text, StringComparison.OrdinalIgnoreCase)).Take(2000))
      {
        var row = new ListViewItem(entry.Title) { Tag = entry.Url };
        row.SubItems.Add(entry.Url);
        row.SubItems.Add(DateTimeOffset.FromUnixTimeMilliseconds(entry.VisitedAtEpochMs).LocalDateTime.ToString("g"));
        list.Items.Add(row);
      }
      list.EndUpdate();
    }
    search.TextChanged += (_, _) => Refresh();
    list.DoubleClick += async (_, _) =>
    {
      if (list.SelectedItems.Count == 0 || list.SelectedItems[0].Tag is not string url) return;
      dialog.Close();
      await CreateTabAsync(url);
    };
    dialog.Controls.Add(list); dialog.Controls.Add(search);
    Refresh(); dialog.ShowDialog(this);
  }

  private async Task ShowAudioOutputMenuAsync(Control anchor)
  {
    var outputs = await EnumerateAudioOutputsAsync();
    var menu = CreateAndroidMenu();
    menu.MinimumSize = new Size(300, 0);
    foreach (var output in outputs)
    {
      var selected = string.Equals(_preferences.AudioOutputDeviceId, output.DeviceId, StringComparison.OrdinalIgnoreCase)
          || (string.IsNullOrWhiteSpace(_preferences.AudioOutputDeviceId)
              && string.Equals(_preferences.AudioOutputLabel, output.DisplayLabel, StringComparison.CurrentCultureIgnoreCase));
      var item = new ToolStripMenuItem(output.DisplayLabel)
      {
        Checked = selected,
        CheckOnClick = false,
        ForeColor = TextColor,
        BackColor = Surface,
        Height = 42,
      };
      item.Click += async (_, _) =>
      {
        _preferences.AudioOutputDeviceId = output.DeviceId;
        _preferences.AudioOutputLabel = output.DisplayLabel;
        DesktopPreferencesStore.Save(_preferences);
        await ApplyAudioOutputToAllAsync(output.DeviceId, output.DisplayLabel);
        _syncStatus = "소리 출력 · " + output.DisplayLabel;
        UpdateStatusText();
      };
      menu.Items.Add(item);
    }
    menu.Items.Add(new ToolStripSeparator());
    var settings = new ToolStripMenuItem("Windows 소리 설정 열기");
    settings.Click += (_, _) => System.Diagnostics.Process.Start(new System.Diagnostics.ProcessStartInfo
    {
      FileName = "ms-settings:sound",
      UseShellExecute = true,
    });
    menu.Items.Add(settings);
    DisposeMenuAfterClose(menu);
    menu.Show(anchor, new Point(anchor.Width - menu.PreferredSize.Width, anchor.Height + 6));
  }

  private async Task ChooseAudioOutputFromSettingsAsync(DesktopPreferences target)
  {
    var outputs = await EnumerateAudioOutputsAsync();
    using var dialog = new Form
    {
      Text = "소리 출력 장치 — Omyaru",
      StartPosition = FormStartPosition.CenterParent,
      FormBorderStyle = FormBorderStyle.FixedDialog,
      MinimizeBox = false,
      MaximizeBox = false,
      ClientSize = new Size(480, 370),
      BackColor = Chrome,
      ForeColor = TextColor,
      Font = new Font("Segoe UI", 10F),
    };
    var title = new Label
    {
      Dock = DockStyle.Top,
      Height = 68,
      Padding = new Padding(22, 10, 10, 0),
      Text = "출력 장치\n페이지의 오디오·비디오에 바로 적용됩니다.",
      ForeColor = TextColor,
      Font = new Font("Segoe UI", 12F, FontStyle.Bold),
    };
    var list = new ListBox
    {
      Dock = DockStyle.Fill,
      BackColor = Surface,
      ForeColor = TextColor,
      BorderStyle = BorderStyle.None,
      Font = new Font("Segoe UI", 11F),
      IntegralHeight = false,
      ItemHeight = 38,
    };
    list.Items.AddRange(outputs.Cast<object>().ToArray());
    var selectedIndex = outputs.FindIndex(output =>
        string.Equals(output.DeviceId, target.AudioOutputDeviceId, StringComparison.OrdinalIgnoreCase)
        || (string.IsNullOrWhiteSpace(target.AudioOutputDeviceId)
            && string.Equals(output.DisplayLabel, target.AudioOutputLabel, StringComparison.CurrentCultureIgnoreCase)));
    list.SelectedIndex = selectedIndex >= 0 ? selectedIndex : 0;
    var buttons = new FlowLayoutPanel
    {
      Dock = DockStyle.Bottom,
      Height = 64,
      FlowDirection = FlowDirection.RightToLeft,
      BackColor = Chrome,
      Padding = new Padding(8, 10, 14, 8),
    };
    var apply = new Button { Text = "적용", Width = 88, Height = 36, DialogResult = DialogResult.OK };
    var cancel = new Button { Text = "취소", Width = 88, Height = 36, DialogResult = DialogResult.Cancel };
    buttons.Controls.Add(apply);
    buttons.Controls.Add(cancel);
    dialog.Controls.Add(list);
    dialog.Controls.Add(buttons);
    dialog.Controls.Add(title);
    dialog.AcceptButton = apply;
    dialog.CancelButton = cancel;
    if (dialog.ShowDialog(Form.ActiveForm ?? this) != DialogResult.OK || list.SelectedItem is not AudioOutputOption output) return;
    target.AudioOutputDeviceId = output.DeviceId;
    target.AudioOutputLabel = output.DisplayLabel;
    if (ReferenceEquals(target, _preferences)) DesktopPreferencesStore.Save(_preferences);
    await ApplyAudioOutputToAllAsync(output.DeviceId, output.DisplayLabel);
  }

  private static async Task<List<AudioOutputOption>> EnumerateAudioOutputsAsync()
  {
    var result = new List<AudioOutputOption> { new(string.Empty, "시스템 기본 장치") };
    var outputs = await Task.Run(WindowsAudioRouter.EnumerateOutputs);
    result.AddRange(outputs.Select(output => new AudioOutputOption(output.DeviceId, output.DisplayName)));
    return result;
  }

  private async Task ApplyAudioOutputToAllAsync(string? selectedDeviceId = null, string? selectedLabel = null)
  {
    var deviceId = selectedDeviceId ?? _preferences.AudioOutputDeviceId;
    var label = selectedLabel ?? _preferences.AudioOutputLabel;
    var processIds = _views.Values
        .Where(view => view.CoreWebView2 is not null)
        .Select(view => (int)view.CoreWebView2.BrowserProcessId)
        .Distinct()
        .ToArray();
    var routing = await Task.Run(() =>
    {
      var success = WindowsAudioRouter.TryRoute(processIds, deviceId, out var error);
      return (success, error);
    });
    foreach (var view in _views.Values)
      await ApplyAudioOutputAsync(view, label);
    if (!routing.success)
    {
      _syncStatus = "출력 장치를 적용하지 못했습니다 · " + routing.error;
      UpdateStatusText();
    }
  }

  private static async Task ApplyAudioOutputAsync(WebView2 view, string selectedLabel)
  {
    if (view.CoreWebView2 is null) return;
    var labelLiteral = JsonSerializer.Serialize(selectedLabel);
    try
    {
      await view.CoreWebView2.ExecuteScriptAsync($$"""
          (() => {
            const desiredLabel = {{labelLiteral}};
            let pending = false;
            let sinkId = '';
            const applyMedia = media => {
              if (typeof media?.setSinkId !== 'function' || media.sinkId === sinkId) return;
              media.setSinkId(sinkId).catch(() => {});
            };
            const resolveSink = async () => {
              try {
                const devices = await navigator.mediaDevices.enumerateDevices();
                const outputs = devices.filter(device => device.kind === 'audiooutput');
                const selected = desiredLabel
                  ? outputs.find(device => device.label === desiredLabel)
                  : outputs.find(device => device.deviceId === 'default');
                sinkId = selected ? selected.deviceId : '';
              } catch (_) {
                sinkId = '';
              }
            };
            window.__omyaruAudioObserver?.disconnect();
            window.__omyaruAudioObserver = new MutationObserver(records => {
              if (pending) return;
              pending = true;
              setTimeout(() => {
                pending = false;
                for (const record of records) {
                  for (const node of record.addedNodes) {
                    if (!(node instanceof Element)) continue;
                    if (node.matches('audio, video')) applyMedia(node);
                    node.querySelectorAll?.('audio, video').forEach(applyMedia);
                  }
                }
              }, 250);
            });
            if (document.documentElement) {
              window.__omyaruAudioObserver.observe(document.documentElement, { childList: true, subtree: true });
            }
            resolveSink().then(() => document.querySelectorAll('audio, video').forEach(applyMedia));
          })();
          """);
    }
    catch
    {
      // Pages can reject device enumeration; the system default remains active.
    }
  }

  private sealed record AudioOutputOption(string DeviceId, string DisplayLabel)
  {
    public override string ToString() => DisplayLabel;
  }

  private void ShowFeatureNotice(string title, string message) =>
      MessageBox.Show(this, message, title, MessageBoxButtons.OK, MessageBoxIcon.Information);

  private void EnsureWorkspaceTab()
  {
    var visible = VisibleTabs().ToList();
    if (visible.Count == 0)
    {
      var tab = new TabEntry(Guid.NewGuid().ToString("N"), "새 탭", "about:blank", _state.ActiveProfileId, _state.ActiveGroupId);
      _state.Tabs.Add(tab);
      _state.ActiveTabId = tab.Id;
      return;
    }
    if (!visible.Any(tab => tab.Id == _state.ActiveTabId)) _state.ActiveTabId = visible[0].Id;
  }

  private IEnumerable<TabEntry> VisibleTabs() => _state.Tabs.Where(tab =>
      tab.ProfileId == _state.ActiveProfileId && tab.GroupId == _state.ActiveGroupId);

  private void RefreshWorkspaceUi()
  {
    RefreshTabsUi();
  }

  private void RefreshTabsUi()
  {
    RefreshTabStripUi();
    RefreshTabOverview();
    UpdateBookmarkStarState();
    var normalTabs = _state.Tabs.Count;
    _privacyReport.Text = $"사용 분석·광고 SDK 및 서드파티 추적기를 원천 차단합니다.\n현재 {normalTabs}개의 탭과 {_state.Profiles.Count}개의 브라우징 프로필이 암호화/로컬 보호 상태로 유지됩니다.";
    UpdateStatusText();
  }

  private void RefreshTabStripUi()
  {
    DismissTabHoverPreview();
    var tabs = VisibleTabs().ToList();
    var visibleIds = tabs.Select(tab => tab.Id).ToHashSet(StringComparer.Ordinal);
    var existing = _tabStrip.Controls.OfType<TabStripItemControl>()
        .ToDictionary(item => item.TabId, StringComparer.Ordinal);
    _tabStrip.SuspendLayout();
    foreach (var item in existing.Values.Where(item => !visibleIds.Contains(item.TabId)).ToArray())
    {
      _tabStrip.Controls.Remove(item);
      item.Dispose();
    }
    for (var index = 0; index < tabs.Count; index++)
    {
      var tab = tabs[index];
      if (!existing.TryGetValue(tab.Id, out var item))
      {
        item = (TabStripItemControl)CreateTabStripItem(tab);
        _tabStrip.Controls.Add(item);
      }
      UpdateTabStripItemVisual(item, tab);
      _tabStrip.Controls.SetChildIndex(item, index);
    }
    _tabStrip.ResumeLayout(true);
    UpdateTabStripItemWidths();
  }

  private void UpdateTabStripItemWidths()
  {
    if (_tabStrip.IsDisposed || _tabStrip.ClientSize.Width <= 0) return;
    var items = _tabStrip.Controls.OfType<TabStripItemControl>().ToArray();
    if (items.Length == 0) return;
    var available = Math.Max(1, _tabStrip.ClientSize.Width - _tabStrip.Padding.Horizontal - 2);
    var tabWidth = Math.Min(208, Math.Max(1,
        (available - items.Sum(item => item.Margin.Horizontal)) / items.Length));
    _tabStrip.SuspendLayout();
    foreach (var item in items)
      if (item.Width != tabWidth) item.Width = tabWidth;
    _tabStrip.ResumeLayout();
  }

  private void UpdateTabStripItemVisual(TabStripItemControl item, TabEntry tab)
  {
    var active = tab.Id == _state.ActiveTabId;
    item.Text = tab.Title;
    item.Active = active;
    item.ForeColor = active ? TextColor : MutedText;
    item.Favicon = CreateTabFaviconBadge(tab, 16);
    item.AccessibleName = tab.Title;
    item.Invalidate();
  }

  private Image CreateTabFaviconBadge(TabEntry tab, int size)
  {
    if (tab.Url != "about:blank")
    {
      var bookmarkMatch = _bookmarks.Items.FirstOrDefault(b => BookmarkFaviconStore.SameSite(b.Url, tab.Url));
      if (bookmarkMatch is not null)
        return BookmarkFaviconStore.LoadOrCreate(bookmarkMatch, size);
      var host = HostName(tab.Url);
      var synthetic = new BookmarkEntry("tab", host, tab.Url, BookmarkStore.GlyphFor(host), BookmarkStore.ColorFor(Math.Abs(host.GetHashCode())));
      return BookmarkFaviconStore.LoadOrCreate(synthetic, size);
    }
    var badge = new Bitmap(size, size);
    using var g = Graphics.FromImage(badge);
    g.SmoothingMode = SmoothingMode.AntiAlias;
    using var pen = new Pen(MutedText, 1.3F);
    g.DrawEllipse(pen, 2, 2, size - 5, size - 5);
    return badge;
  }

  private Control CreateTabStripItem(TabEntry tab)
  {
    var item = new TabStripItemControl
    {
      Width = 208,
      Height = 36,
      TabId = tab.Id,
      Text = tab.Title,
      Active = false,
      BackColor = Chrome,
      ActiveBackColor = Color.FromArgb(50, 50, 56),
      ForeColor = MutedText,
      AccentColor = Color.FromArgb(74, 74, 82),
      InactiveBorderColor = Color.FromArgb(46, 46, 52),
      Margin = new Padding(2, 0, 2, 0),
      AccessibleName = tab.Title,
    };
    item.SelectRequested += async (_, _) => await SelectTabAsync(tab.Id);
    item.CloseRequested += async (_, _) => await CloseTabAsync(tab.Id);
    item.DuplicateRequested += async (_, _) => await DuplicateTabAsync(tab.Id);
    item.CloseOtherRequested += async (_, _) => await CloseOtherTabsAsync(tab.Id);
    item.CloseAllRequested += async (_, _) => await CloseAllTabsWithCountdownAsync();
    item.RestoreClosedRequested += async (_, _) => await RestoreLastClosedTabAsync();
    item.NewTabRequested += async (_, _) => await CreateTabAsync();
    item.ReorderRequested += (_, eventArgs) => MoveDesktopTabBeside(
        eventArgs.SourceTabId,
        eventArgs.TargetTabId,
        eventArgs.PlaceAfter);
    item.ReorderPreviewRequested += (_, eventArgs) => PreviewDesktopTabMove(_tabStrip, eventArgs);
    item.DragCompleted += (_, eventArgs) =>
    {
      DismissTabHoverPreview(item);
      if (!eventArgs.Committed) RefreshTabStripUi();
    };
    item.HoverPreviewRequested += (_, _) => ScheduleTabHoverPreview(item);
    item.HoverPreviewDismissed += (_, _) => DismissTabHoverPreview(item);
    UpdateTabStripItemVisual(item, tab);
    return item;
  }

  private void ScheduleTabHoverPreview(TabStripItemControl item)
  {
    if (_showingOverview || item.IsDisposed) return;
    if (!ReferenceEquals(_pendingHoverTab, item))
    {
      _tabHoverPreview?.Close();
      _tabHoverPreview?.Dispose();
      _tabHoverPreview = null;
    }
    _pendingHoverTab = item;
    _tabHoverTimer.Stop();
    _tabHoverTimer.Start();
  }

  private void ShowTabHoverPreview(TabStripItemControl item)
  {
    if (_showingOverview || item.IsDisposed || !item.Visible) return;
    var tab = _state.Tabs.FirstOrDefault(candidate => candidate.Id == item.TabId);
    if (tab is null) return;
    _tabHoverPreview?.Close();
    _tabHoverPreview?.Dispose();
    Image? preview = _previews.TryGetValue(tab.Id, out var image) ? new Bitmap(image) : null;
    _tabHoverPreview = new TabHoverPreviewWindow(tab.Title, tab.Url, preview);
    var location = item.PointToScreen(new Point(0, item.Height + 7));
    var screen = Screen.FromControl(item).WorkingArea;
    location.X = Math.Clamp(location.X, screen.Left + 8, Math.Max(screen.Left + 8, screen.Right - _tabHoverPreview.Width - 8));
    location.Y = Math.Clamp(location.Y, screen.Top + 8, Math.Max(screen.Top + 8, screen.Bottom - _tabHoverPreview.Height - 8));
    _tabHoverPreview.Location = location;
    _tabHoverPreview.Show(this);
  }

  private void DismissTabHoverPreview(TabStripItemControl? item = null)
  {
    if (item is not null && !ReferenceEquals(_pendingHoverTab, item)) return;
    _tabHoverTimer.Stop();
    _pendingHoverTab = null;
    _tabHoverPreview?.Close();
    _tabHoverPreview?.Dispose();
    _tabHoverPreview = null;
  }

  private static void PreviewDesktopTabMove(Control host, TabReorderEventArgs eventArgs)
  {
    var source = host.Controls.Cast<Control>().FirstOrDefault(control => control switch
    {
      TabStripItemControl item => item.TabId == eventArgs.SourceTabId,
      OverviewTabCardControl card => card.TabId == eventArgs.SourceTabId,
      _ => false,
    });
    var target = host.Controls.Cast<Control>().FirstOrDefault(control => control switch
    {
      TabStripItemControl item => item.TabId == eventArgs.TargetTabId,
      OverviewTabCardControl card => card.TabId == eventArgs.TargetTabId,
      _ => false,
    });
    if (source is null || target is null || ReferenceEquals(source, target)) return;
    var sourceIndex = host.Controls.GetChildIndex(source);
    var targetIndex = host.Controls.GetChildIndex(target);
    var insertionIndex = targetIndex + (eventArgs.PlaceAfter ? 1 : 0);
    if (sourceIndex < insertionIndex) insertionIndex--;
    insertionIndex = Math.Clamp(insertionIndex, 0, host.Controls.Count - 1);
    if (insertionIndex == sourceIndex) return;
    host.SuspendLayout();
    host.Controls.SetChildIndex(source, insertionIndex);
    host.ResumeLayout(true);
  }

  private void MoveDesktopTabBeside(string sourceTabId, string targetTabId, bool placeAfter)
  {
    if (sourceTabId == targetTabId) return;
    var sourceIndex = _state.Tabs.FindIndex(tab => tab.Id == sourceTabId);
    if (sourceIndex < 0) return;
    var source = _state.Tabs[sourceIndex];
    var target = _state.Tabs.FirstOrDefault(tab => tab.Id == targetTabId);
    if (target is null || source.ProfileId != target.ProfileId || source.GroupId != target.GroupId) return;
    _state.Tabs.RemoveAt(sourceIndex);
    var targetIndex = _state.Tabs.FindIndex(tab => tab.Id == targetTabId);
    var insertionIndex = Math.Clamp(targetIndex + (placeAfter ? 1 : 0), 0, _state.Tabs.Count);
    _state.Tabs.Insert(insertionIndex, source);
    MarkLocalChange();
    RefreshTabsUi();
  }

  private void RefreshTabOverview()
  {
    if (_overviewGrid.IsDisposed) return;
    var query = _overviewSearch.Text.Trim();
    var tabs = VisibleTabs().ToList();
    var matchedCount = tabs.Count(tab => query.Length == 0 ||
        tab.Title.Contains(query, StringComparison.CurrentCultureIgnoreCase));
    var profile = _state.Profiles.FirstOrDefault(item => item.Id == _state.ActiveProfileId) ?? _state.Profiles[0];
    _overviewTitle.Text = $"{profile.Name} · {ActiveGroupName()} · {matchedCount}개의 탭";
    if (_overviewProfileButton is not null)
    {
      _overviewProfileButton.Text = $"  {profile.Name} ⌄";
      var oldImage = _overviewProfileButton.Image;
      _overviewProfileButton.Image = CreateProfileBadge(profile, 24);
      oldImage?.Dispose();
    }
    if (_toolbarProfileButton is { IsDisposed: false })
    {
      _toolbarProfileButton.Text = Shorten(profile.Name, 8);
      var oldToolbarBadge = _toolbarProfileButton.Image;
      _toolbarProfileButton.Image = CreateProfileBadge(profile, 18);
      oldToolbarBadge?.Dispose();
    }
    if (_overviewGroupButton is not null)
      _overviewGroupButton.Text = _state.ActiveGroupId is null
          ? $"▱  탭 {matchedCount}개"
          : $"▱  {ActiveGroupName()} · {matchedCount}개";
    var visibleIds = tabs.Select(tab => tab.Id).ToHashSet(StringComparer.Ordinal);
    var existing = _overviewGrid.Controls.OfType<OverviewTabCardControl>()
        .ToDictionary(card => card.TabId, StringComparer.Ordinal);
    _overviewGrid.SuspendLayout();
    foreach (var card in existing.Values.Where(card => !visibleIds.Contains(card.TabId)).ToArray())
    {
      _overviewGrid.Controls.Remove(card);
      card.Dispose();
    }
    for (var index = 0; index < tabs.Count; index++)
    {
      var tab = tabs[index];
      if (!existing.TryGetValue(tab.Id, out var card))
      {
        card = (OverviewTabCardControl)CreateOverviewCard(tab);
        _overviewGrid.Controls.Add(card);
      }
      UpdateOverviewCardVisual(card, tab);
      card.Visible = query.Length == 0 || tab.Title.Contains(query, StringComparison.CurrentCultureIgnoreCase);
      _overviewGrid.Controls.SetChildIndex(card, index);
    }
    _overviewGrid.ResumeLayout(true);
    ResizeOverviewCards();
    ScheduleOverviewSnapshot();
  }

  private void UpdateOverviewCardVisual(OverviewTabCardControl card, TabEntry tab)
  {
    var active = tab.Id == _state.ActiveTabId;
    card.BorderColor = active ? Accent : Outline;
    card.BorderWidth = active ? 2F : 1F;
    card.TitleText = Shorten(tab.Title, 48);
    card.AddressText = tab.Url == "about:blank" ? "새 탭" : Shorten(tab.Url, 58);
    card.EmptyText = tab.Url == "about:blank" ? "＋" : HostName(tab.Url);
    card.Invalidate();
  }

  private void UpdateOverviewCardPreview(string tabId, Image preview)
  {
    var card = _overviewGrid.Controls.OfType<OverviewTabCardControl>()
        .FirstOrDefault(candidate => candidate.TabId == tabId);
    if (card is null || card.IsDisposed) return;
    card.Preview = new Bitmap(preview);
    card.Invalidate();
    ScheduleOverviewSnapshot();
  }

  private void CenterOverviewHeader(Panel center)
  {
    var control = _overviewSearchHost.Visible ? (Control)_overviewSearchHost : _overviewProfileButton;
    control.Left = Math.Max(0, (center.ClientSize.Width - control.Width) / 2);
    control.Top = Math.Max(0, (center.ClientSize.Height - control.Height) / 2);
  }

  private void ShowOverviewProfileMenu(Control anchor)
  {
    var menu = CreateAndroidMenu();
    foreach (var profile in _state.Profiles)
    {
      var prefix = profile.Id == _state.ActiveProfileId ? "✓  " : "    ";
      var item = menu.Items.Add(prefix + profile.Name, CreateProfileBadge(profile, 22));
      item.Click += async (_, _) =>
      {
        _state.ActiveProfileId = profile.Id;
        _state.ActiveGroupId = null;
        EnsureWorkspaceTab();
        MarkLocalChange();
        RefreshWorkspaceUi();
        await ActivateCurrentTabAsync();
      };
    }
    menu.Items.Add(new ToolStripSeparator());
    menu.Items.Add("새 프로필", CreateMenuGlyph("＋", Accent), (_, _) => CreateProfile());
    DisposeMenuAfterClose(menu);
    var x = (anchor.Width - Math.Max(menu.MinimumSize.Width, menu.PreferredSize.Width)) / 2;
    menu.Show(anchor, new Point(x, anchor.Height + 6));
  }

  private void ShowOverviewGroupMenu(Control anchor)
  {
    var menu = CreateAndroidMenu();
    AddGroupChoice(null, "탭 그룹 없음");
    foreach (var group in _state.Groups.Where(item => item.ProfileId == _state.ActiveProfileId))
      AddGroupChoice(group.Id, group.Name);
    menu.Items.Add(new ToolStripSeparator());
    menu.Items.Add("새 탭 그룹", CreateMenuGlyph("＋", Accent), (_, _) => CreateGroup());
    DisposeMenuAfterClose(menu);
    var x = (anchor.Width - Math.Max(menu.MinimumSize.Width, menu.PreferredSize.Width)) / 2;
    menu.Show(anchor, new Point(x, -menu.PreferredSize.Height - 6));

    void AddGroupChoice(string? groupId, string name)
    {
      var selected = groupId == _state.ActiveGroupId;
      var item = menu.Items.Add(name, CreateMenuGlyph(selected ? "✓" : "▱", selected ? Accent : MutedText));
      item.Click += async (_, _) =>
      {
        _state.ActiveGroupId = groupId;
        EnsureWorkspaceTab();
        MarkLocalChange();
        RefreshWorkspaceUi();
        await ActivateCurrentTabAsync();
      };
    }
  }

  private static ContextMenuStrip CreateAndroidMenu()
  {
    var menu = new ContextMenuStrip
    {
      BackColor = OverviewControl,
      ForeColor = TextColor,
      ShowImageMargin = true,
      ImageScalingSize = new Size(24, 24),
      Font = new Font("Segoe UI", 10.5F),
      Padding = new Padding(7),
      DropShadowEnabled = true,
      MinimumSize = new Size(284, 0),
      Renderer = new AndroidMenuRenderer(),
    };
    menu.Opening += (_, _) =>
    {
      var itemWidth = Math.Max(menu.MinimumSize.Width - 16, menu.PreferredSize.Width - 16);
      foreach (ToolStripItem item in menu.Items)
      {
        item.AutoSize = false;
        item.Width = itemWidth;
        if (item is ToolStripControlHost host)
        {
          item.Height = host.Control.Height;
          host.Control.Width = itemWidth;
          continue;
        }
        item.Height = item is ToolStripSeparator ? 10 : 46;
        if (item is ToolStripMenuItem menuItem)
        {
          item.Padding = new Padding(8, 0, 14, 0);
          item.TextAlign = ContentAlignment.MiddleLeft;
          item.ImageAlign = ContentAlignment.MiddleCenter;
          menuItem.ImageScaling = ToolStripItemImageScaling.SizeToFit;
        }
      }
    };
    return menu;
  }

  private void DisposeMenuAfterClose(ContextMenuStrip menu)
  {
    menu.Closed += (_, _) =>
    {
      if (IsDisposed || !IsHandleCreated)
      {
        menu.Dispose();
        return;
      }
      BeginInvoke(new Action(menu.Dispose));
    };
  }

  private static Bitmap CreateMenuGlyph(string glyph, Color color, int size = 24)
  {
    var bitmap = new Bitmap(size, size);
    using var graphics = Graphics.FromImage(bitmap);
    graphics.SmoothingMode = System.Drawing.Drawing2D.SmoothingMode.AntiAlias;
    graphics.PixelOffsetMode = System.Drawing.Drawing2D.PixelOffsetMode.HighQuality;
    graphics.TextRenderingHint = System.Drawing.Text.TextRenderingHint.AntiAliasGridFit;
    using var font = new Font("Segoe UI Symbol", size * 0.78F, FontStyle.Regular, GraphicsUnit.Pixel);
    using var brush = new SolidBrush(color);
    using var format = new StringFormat { Alignment = StringAlignment.Center, LineAlignment = StringAlignment.Center };
    graphics.DrawString(glyph, font, brush, new RectangleF(0, 0, size, size), format);
    return bitmap;
  }

  private static Bitmap CreateProfileBadge(ProfileEntry profile, int size)
  {
    var bitmap = new Bitmap(size, size);
    using var graphics = Graphics.FromImage(bitmap);
    graphics.SmoothingMode = System.Drawing.Drawing2D.SmoothingMode.AntiAlias;
    var color = Color.FromArgb(unchecked((int)profile.Color));
    using var brush = new SolidBrush(color);
    graphics.FillEllipse(brush, 1, 1, size - 2, size - 2);
    using var font = new Font("Segoe UI Symbol", size * 0.43F, FontStyle.Bold, GraphicsUnit.Pixel);
    using var textBrush = new SolidBrush(Color.White);
    var glyph = profile.Icon.ToUpperInvariant() switch
    {
      "WORK" => "▣",
      "STUDY" => "▤",
      "TRAVEL" => "✈",
      "GAMING" => "◆",
      "STAR" => "★",
      _ => "●",
    };
    var format = new StringFormat { Alignment = StringAlignment.Center, LineAlignment = StringAlignment.Center };
    graphics.DrawString(glyph, font, textBrush, new RectangleF(0, 0, size, size), format);
    return bitmap;
  }

  private Control CreateOverviewCard(TabEntry tab)
  {
    var active = tab.Id == _state.ActiveTabId;
    var card = new OverviewTabCardControl
    {
      Height = 300,
      BackColor = Surface,
      BorderColor = active ? Accent : Outline,
      BorderWidth = active ? 2F : 1F,
      CornerRadius = 16,
      Margin = new Padding(10),
      Tag = "overview-card",
      Cursor = Cursors.Hand,
      // Each visible card owns a stable image copy. The shared cache can then be
      // refreshed after navigation without invalidating an image mid-paint.
      Preview = _previews.TryGetValue(tab.Id, out var image) ? new Bitmap(image) : null,
      TitleText = Shorten(tab.Title, 48),
      AddressText = tab.Url == "about:blank" ? "새 탭" : Shorten(tab.Url, 58),
      EmptyText = tab.Url == "about:blank" ? "＋" : HostName(tab.Url),
      PreviewBackColor = PageBackground,
      FooterBackColor = Surface,
      TextColor = TextColor,
      MutedTextColor = MutedText,
      CloseBackColor = Color.FromArgb(228, 38, 38, 44),
      TabId = tab.Id,
    };
    card.ActivateRequested += async (_, _) => await SelectOverviewTabAsync(card, tab.Id);
    card.CloseRequested += async (_, _) => await CloseTabAsync(tab.Id);
    card.MouseUp += (_, eventArgs) =>
    {
      if (eventArgs.Button != MouseButtons.Right) return;
      var menu = CreateAndroidMenu();
      menu.ShowImageMargin = false;
      menu.MinimumSize = new Size(240, 0);
      menu.Items.Add("탭 복제", null, async (_, _) => await DuplicateTabAsync(tab.Id));
      menu.Items.Add("선택한 탭 닫기", null, async (_, _) => await CloseTabAsync(tab.Id));
      menu.Items.Add("다른 탭 모두 닫기", null, async (_, _) => await CloseOtherTabsAsync(tab.Id));
      DisposeMenuAfterClose(menu);
      menu.Show(card, eventArgs.Location);
    };
    card.ReorderPreviewRequested += (_, eventArgs) => PreviewDesktopTabMove(_overviewGrid, eventArgs);
    card.ReorderRequested += (_, eventArgs) => MoveDesktopTabBeside(
        eventArgs.SourceTabId,
        eventArgs.TargetTabId,
        eventArgs.PlaceAfter);
    card.DragCompleted += (_, eventArgs) =>
    {
      if (!eventArgs.Committed) RefreshTabOverview();
    };
    UpdateOverviewCardVisual(card, tab);
    return card;
  }

  private Control CreateNewTabCard()
  {
    var button = new PillButton
    {
      Height = 238,
      Text = "＋\n\n새 탭",
      BackColor = Color.FromArgb(75, 76, 80),
      ForeColor = TextColor,
      Font = new Font("Segoe UI", 16F),
      Margin = new Padding(8),
      Tag = "overview-card",
      Cursor = Cursors.Hand,
      CornerRadius = 16,
      BorderColor = Color.FromArgb(95, 96, 101),
      BorderWidth = 1F,
      AccessibleName = "새 탭",
    };
    button.Click += async (_, _) => await CreateTabAsync();
    return button;
  }

  private void ResizeOverviewCards()
  {
    if (_overviewGrid.ClientSize.Width <= 0) return;
    var available = Math.Max(600, _overviewGrid.ClientSize.Width - 60);
    var columns = available >= 1220 ? 4 : available >= 900 ? 3 : 2;
    var width = Math.Max(250, (available - columns * 18) / columns);
    var cardHeight = Math.Clamp((int)(width * 0.78F), 270, 360);
    foreach (Control control in _overviewGrid.Controls)
      if (Equals(control.Tag, "overview-card"))
      {
        control.Width = width;
        control.Height = cardHeight;
      }
    var visibleCount = _overviewGrid.Controls.Cast<Control>().Count(control => control.Visible);
    var rows = Math.Max(1, (int)Math.Ceiling((double)visibleCount / columns));
    var cardContentHeight = rows * (cardHeight + 18) + 22;
    const int topPadding = 14;
    if (_overviewGrid.Padding.Top != topPadding)
      _overviewGrid.Padding = new Padding(24, topPadding, 24, 18);
    _overviewGrid.AutoScroll = cardContentHeight > _overviewGrid.ClientSize.Height;
  }

  private async Task ShowTabOverviewAsync()
  {
    if (_showingOverview) return;
    _overviewSnapshotTimer.Stop();
    _overviewTransitionSurface.Visible = false;
    _overviewTransitionSurface.Image = null;
    _showingOverview = true;
    ApplySurfaceMode();
    _tabOverview.Invalidate(true);
    _handlingWindowTransition = false;
    if (!_showingOverview || IsDisposed) return;
    if (_overviewSearchHost.Visible) _overviewSearch.Focus();
    else _overviewProfileButton.Focus();
    await Task.CompletedTask;
  }

  private async Task ShowBrowsingAsync()
  {
    _overviewTransitionSurface.Visible = false;
    _overviewTransitionSurface.Image = null;
    WebView2? existing = null;
    if (_state.ActiveTabId is not null && _views.TryGetValue(_state.ActiveTabId, out existing))
    {
      existing.Visible = true;
      existing.BringToFront();
    }
    else
    {
      await ActivateCurrentTabAsync();
    }
    _showingOverview = false;
    ApplySurfaceMode();
    _handlingWindowTransition = false;
    existing?.Focus();
    await Task.CompletedTask;
  }

  private async Task SelectOverviewTabAsync(OverviewTabCardControl card, string tabId)
  {
    await SelectTabAsync(tabId);
  }

  private async Task SelectTabAsync(string tabId)
  {
    _overviewTransitionSurface.Visible = false;
    _overviewTransitionSurface.Image = null;
    _state.ActiveTabId = tabId;
    MarkLocalChange();
    var target = _state.Tabs.FirstOrDefault(tab => tab.Id == tabId);
    if (target is not null)
    {
      _address.Text = DisplayAddress(target.Url);
      RefreshTabStripUi();
    }
    _showingOverview = false;
    ApplySurfaceMode();
    await ActivateCurrentTabAsync();
    UpdateToolbarZoom();
    if (_views.TryGetValue(tabId, out var view)) view.Focus();
  }

  private void ShowTabActivationPreview(string tabId)
  {
    _tabActivationPreview.Image = _previews.TryGetValue(tabId, out var preview) ? preview : null;
    _tabActivationPreview.Visible = true;
    _tabActivationPreview.BringToFront();
  }

  private void HideTabActivationPreview()
  {
    _tabActivationPreview.Visible = false;
    _tabActivationPreview.Image = null;
  }

  private void ScheduleOverviewSnapshot()
  {
    // A full DrawToBitmap pass stalls the UI thread and was the source of the
    // half-painted overview frame. Window-level snapshots now cover transitions.
  }

  private void RefreshOverviewSnapshot()
  {
    if (_closing || _showingOverview || _tabOverview.IsDisposed ||
        _tabOverview.ClientSize.Width < 2 || _tabOverview.ClientSize.Height < 2) return;
    Bitmap? next = null;
    try
    {
      _overviewTransitionSurface.Visible = false;
      _overviewTransitionSurface.Image = null;
      _tabOverview.PerformLayout();
      _overviewGrid.PerformLayout();
      next = new Bitmap(_tabOverview.ClientSize.Width, _tabOverview.ClientSize.Height);
      _tabOverview.DrawToBitmap(next, new Rectangle(Point.Empty, next.Size));
      var previous = _overviewSnapshot;
      _overviewSnapshot = next;
      next = null;
      previous?.Dispose();
    }
    catch (Exception)
    {
      // A resize can invalidate a DrawToBitmap target between layout and paint. The
      // next scheduled pass will refresh the cache; the live overview remains usable.
    }
    finally
    {
      next?.Dispose();
    }
  }

  private void ScheduleTabPreviewCapture(string tabId)
  {
    if (_closing || string.IsNullOrWhiteSpace(tabId)) return;
    _pendingPreviewTabId = tabId;
    _previewCaptureGeneration++;
    _previewCaptureTimer.Stop();
    _previewCaptureTimer.Start();
  }

  private async Task CaptureTabPreviewAsync(string tabId, int generation)
  {
    if (!_views.TryGetValue(tabId, out var view) ||
        view.CoreWebView2 is null || !view.Visible) return;
    try
    {
      using var stream = new MemoryStream();
      await view.CoreWebView2.CapturePreviewAsync(CoreWebView2CapturePreviewImageFormat.Png, stream);
      if (_closing || generation != _previewCaptureGeneration) return;
      var encoded = stream.ToArray();
      var snapshot = await Task.Run(() => DesktopThumbnailStore.SavePreview(tabId, encoded));
      if (snapshot is null) return;
      if (_closing || generation != _previewCaptureGeneration || !_state.Tabs.Any(tab => tab.Id == tabId))
      {
        snapshot.Dispose();
        return;
      }
      if (_previews.Remove(tabId, out var previous)) previous.Dispose();
      _previews[tabId] = snapshot;
      if (_pendingPreviewTabId == tabId) _pendingPreviewTabId = null;
      MarkLocalChange();
      UpdateOverviewCardPreview(tabId, snapshot);
    }
    catch (Exception)
    {
      // A hidden or navigating WebView may reject a preview; the card falls back to title and URL.
    }
  }

  private async Task ActivateCurrentTabAsync()
  {
    if (_closing) return;
    var tab = _state.Tabs.FirstOrDefault(item => item.Id == _state.ActiveTabId);
    if (tab is null) return;
    foreach (var existing in _views.Values) existing.Visible = false;
    if (!_views.TryGetValue(tab.Id, out var view))
    {
      view = await CreateViewAsync(tab);
      if (_closing) return;
      _views[tab.Id] = view;
    }
    view.Visible = true;
    view.BringToFront();
    _address.Text = DisplayAddress(tab.Url);
    _backButton.Enabled = view.CoreWebView2?.CanGoBack == true;
    _forwardButton.Enabled = view.CoreWebView2?.CanGoForward == true;
    UpdateBookmarkStarState();
    RefreshTabStripUi();
    UpdateStatusText();
    ApplySurfaceMode();
  }

  private void ApplySurfaceMode()
  {
    var tab = _state.Tabs.FirstOrDefault(item => item.Id == _state.ActiveTabId);
    var start = !_showingOverview && tab?.Url == "about:blank";
    if (_showingOverview)
    {
      // The overview is kept laid out while it is behind the browser. A z-order swap is
      // enough; forcing nested layouts and synchronous Update calls caused each surface
      // to paint as a separate, visibly stepped frame.
      _tabOverview.Enabled = true;
      _tabOverview.BringToFront();
      _tabOverview.Invalidate();
      return;
    }

    _tabOverview.SendToBack();
    _tabOverview.Enabled = false;
    if (start)
    {
      _browserHost.Visible = false;
      _startPage.Visible = true;
      _startPage.BringToFront();
      return;
    }

    _startPage.Visible = false;
    _browserHost.Visible = true;
    _browserHost.BringToFront();
  }

  private void ApplyChromePreferencesLayout()
  {
    _rootLayout.SuspendLayout();
    _rootLayout.RowStyles[0].Height = 46;
    _rootLayout.RowStyles[1].Height = _preferences.CompactToolbar ? 52 : 56;
    _rootLayout.RowStyles[2].Height = !string.IsNullOrEmpty(_preferences.ToolbarShortcuts) ? 50 : _preferences.ShowFavoritesBar ? 34 : 0;
    _rootLayout.RowStyles[4].Height = _preferences.ShowStatusBar ? 24 : 0;
    var topPad = _preferences.CompactToolbar ? 8 : 10;
    if (_backButton.Parent is { } historyHost)
      historyHost.Padding = new Padding(0, topPad, 4, 0);
    if (!_toolbarWorkspace.IsDisposed)
      _toolbarWorkspace.Padding = new Padding(0, topPad, 0, 0);
    _rootLayout.ResumeLayout(false);
    _rootLayout.PerformLayout();
    _addressHost.Parent?.PerformLayout();
  }

  private async Task<WebView2> CreateViewAsync(TabEntry tab)
  {
    var view = new WebView2
    {
      Dock = DockStyle.Fill,
      Visible = false,
      BackColor = PageBackground,
      DefaultBackgroundColor = PageBackground,
    };
    _browserHost.Controls.Add(view);
    var environment = await EnvironmentForProfileAsync(tab.ProfileId);
    await view.EnsureCoreWebView2Async(environment);
    if (!string.IsNullOrWhiteSpace(_preferences.AudioOutputDeviceId))
    {
      _ = Task.Run(() => WindowsAudioRouter.TryRoute(
          [(int)view.CoreWebView2.BrowserProcessId],
          _preferences.AudioOutputDeviceId,
          out _));
    }
    var settings = view.CoreWebView2.Settings;
    settings.IsStatusBarEnabled = false;
    settings.AreDevToolsEnabled = true;
    settings.IsPasswordAutosaveEnabled = false;
    settings.IsGeneralAutofillEnabled = false;
    view.CoreWebView2.DocumentTitleChanged += (_, _) =>
    {
      var title = view.CoreWebView2.DocumentTitle;
      UpdateTab(tab.Id, current => current with { Title = string.IsNullOrWhiteSpace(title) ? "새 탭" : title });
    };
    view.CoreWebView2.SourceChanged += (_, _) =>
    {
      var source = view.Source?.ToString() ?? "about:blank";
      UpdateTab(tab.Id, current => current with { Url = SyncSnapshot.NormalizeUrl(source) });
      if (_state.ActiveTabId == tab.Id)
      {
        _address.Text = _address.Focused ? (source == "about:blank" ? string.Empty : source) : DisplayAddress(source);
        UpdateBookmarkStarState();
        ApplySurfaceMode();
      }
      _ = InjectExtensionStoreBridgeAsync(view);
    };
    view.CoreWebView2.HistoryChanged += (_, _) =>
    {
      if (_state.ActiveTabId != tab.Id) return;
      _backButton.Enabled = view.CoreWebView2.CanGoBack;
      _forwardButton.Enabled = view.CoreWebView2.CanGoForward;
    };
    view.CoreWebView2.NavigationCompleted += (_, eventArgs) =>
    {
      if (_closing) return;
      if (eventArgs.IsSuccess && tab.GroupId != "__private_workspace__")
        BrowserHistory.Record(view.Source?.ToString() ?? string.Empty, view.CoreWebView2.DocumentTitle);
      if (!string.IsNullOrWhiteSpace(_preferences.AudioOutputDeviceId))
        _ = ApplyAudioOutputAsync(view, _preferences.AudioOutputLabel);
      _ = InjectExtensionStoreBridgeAsync(view);
      if (_showingOverview || _state.ActiveTabId != tab.Id) return;
      ScheduleTabPreviewCapture(tab.Id);
    };
    view.CoreWebView2.WebMessageReceived += (_, eventArgs) => OnStoreWebMessageReceived(view, eventArgs);
    view.CoreWebView2.DownloadStarting += (_, eventArgs) => OnExtensionPackageDownloadStarting(view, eventArgs);
    view.CoreWebView2.FaviconChanged += async (_, _) =>
    {
      if (_closing || view.CoreWebView2 is null) return;
      var pageUrl = view.Source?.ToString() ?? string.Empty;
      if (!_bookmarks.Items.Any(bookmark => BookmarkFaviconStore.SameSite(bookmark.Url, pageUrl))) return;
      try
      {
        using var favicon = await view.CoreWebView2.GetFaviconAsync(CoreWebView2FaviconImageFormat.Png);
        if (!await BookmarkFaviconStore.SaveAsync(pageUrl, favicon) || _closing || IsDisposed) return;
        BeginInvoke(new Action(() =>
        {
          if (_closing || IsDisposed) return;
          PopulateFavoritesBar();
          PopulateStartPageFavorites();
        }));
      }
      catch
      {
        // Some pages deliberately expose no favicon. Keep the generated site badge.
      }
    };
    view.CoreWebView2.NewWindowRequested += async (_, eventArgs) =>
    {
      eventArgs.Handled = true;
      if (_preferences.OpenLinksInNewTab) await CreateTabAsync(eventArgs.Uri);
      else await NavigateToAsync(eventArgs.Uri);
    };
    view.CoreWebView2.Navigate(tab.Url);
    return view;
  }

  private void OnStoreWebMessageReceived(WebView2 view, CoreWebView2WebMessageReceivedEventArgs eventArgs)
  {
    try
    {
      using var doc = JsonDocument.Parse(eventArgs.WebMessageAsJson);
      var root = doc.RootElement;
      if (root.ValueKind != JsonValueKind.Object ||
          !root.TryGetProperty("channel", out var channel) ||
          channel.GetString() != "omyaru-extension-install") return;
      var targetUrl = root.TryGetProperty("url", out var urlProp) ? urlProp.GetString() : null;
      if (string.IsNullOrWhiteSpace(targetUrl))
        targetUrl = view.Source?.ToString();
      if (!string.IsNullOrWhiteSpace(targetUrl))
      {
        _ = InstallExtensionFromStoreUrlAsync(targetUrl, view);
      }
    }
    catch
    {
      // Ignore unrelated web messages.
    }
  }

  private void OnExtensionPackageDownloadStarting(WebView2 view, CoreWebView2DownloadStartingEventArgs eventArgs)
  {
    try
    {
      var uri = eventArgs.DownloadOperation?.Uri ?? string.Empty;
      var resultPath = eventArgs.ResultFilePath ?? string.Empty;
      var isCrxOrXpi =
          uri.EndsWith(".crx", StringComparison.OrdinalIgnoreCase) ||
          uri.EndsWith(".xpi", StringComparison.OrdinalIgnoreCase) ||
          uri.Contains(".xpi?", StringComparison.OrdinalIgnoreCase) ||
          uri.Contains(".crx?", StringComparison.OrdinalIgnoreCase) ||
          resultPath.EndsWith(".crx", StringComparison.OrdinalIgnoreCase) ||
          resultPath.EndsWith(".xpi", StringComparison.OrdinalIgnoreCase);
      if (!isCrxOrXpi) return;
      eventArgs.Cancel = true;
      eventArgs.Handled = true;
      _ = InstallExtensionFromStoreUrlAsync(uri, view);
    }
    catch
    {
      // Fall back to standard download behavior if inspection fails.
    }
  }

  private static async Task InjectExtensionStoreBridgeAsync(WebView2 view)
  {
    if (view.CoreWebView2 is null) return;
    var currentUrl = view.Source?.ToString() ?? string.Empty;
    if (!ExtensionManagerForm.IsSupportedStoreOrPackageUrl(currentUrl)) return;
    try
    {
      await view.CoreWebView2.ExecuteScriptAsync("""
          (() => {
            if (window.__omyaruStoreBridgeActive) {
              window.__omyaruRefreshStoreBanner?.();
              return;
            }
            window.__omyaruStoreBridgeActive = true;

            const isDetailUrl = () => {
              const href = location.href;
              return /chromewebstore\.google\.com\/detail\//i.test(href) ||
                     /chrome\.google\.com\/webstore\/detail\//i.test(href) ||
                     /microsoftedge\.microsoft\.com\/addons\/detail\//i.test(href) ||
                     /addons\.mozilla\.org\/[^/]+\/firefox\/addon\/[^/?#]+/i.test(href) ||
                     /addons\.mozilla\.org\/firefox\/addon\/[^/?#]+/i.test(href);
            };

            const requestInstall = (url) => {
              const badge = document.getElementById('__omyaru_store_install_btn');
              if (badge) {
                badge.textContent = '⏳ Omyaru에 확장 프로그램 설치 중...';
                badge.style.background = '#1c64f2';
              }
              chrome.webview.postMessage({
                channel: 'omyaru-extension-install',
                url: url || location.href
              });
            };

            window.__omyaruMarkExtensionInstalled = (name) => {
              const badge = document.getElementById('__omyaru_store_install_btn');
              if (badge) {
                badge.textContent = `✓ '${name}' Omyaru 설치 완료`;
                badge.style.background = '#10b981';
              }
            };

            window.__omyaruMarkExtensionFailed = (msg) => {
              const badge = document.getElementById('__omyaru_store_install_btn');
              if (badge) {
                badge.textContent = '🧩 Omyaru에 이 확장 프로그램 다시 설치';
                badge.style.background = '#0a84ff';
              }
            };

            const patchStoreButtons = () => {
              // Hide "Firefox is required" / "Switch to Chrome" notices on AMO and CWS
              for (const notice of document.querySelectorAll('.AMInstallButton-warning, .GetFirefoxButton, [class*=" FirefoxBanner" i]')) {
                if (notice instanceof HTMLElement) notice.style.display = 'none';
              }
              for (const anchor of document.querySelectorAll('a[href$=".xpi"], a[href*=".xpi?"], a.AMInstallButton-button')) {
                if (!(anchor instanceof HTMLAnchorElement) || anchor.dataset.omyaruHooked === '1') continue;
                anchor.dataset.omyaruHooked = '1';
                anchor.textContent = '🧩 Omyaru에 설치';
                anchor.style.background = '#0a84ff';
                anchor.style.color = '#ffffff';
                anchor.style.borderRadius = '10px';
                anchor.addEventListener('click', (e) => {
                  e.preventDefault();
                  e.stopPropagation();
                  requestInstall(anchor.href || location.href);
                }, true);
              }
              for (const btn of document.querySelectorAll('button')) {
                if (btn.dataset.omyaruHooked === '1') continue;
                const label = (btn.textContent || '').trim();
                if (/^(Chrome에 추가|Add to Chrome|Firefox에 추가|Add to Firefox|받기|Get)$/i.test(label)) {
                  btn.dataset.omyaruHooked = '1';
                  btn.disabled = false;
                  btn.removeAttribute('disabled');
                  btn.removeAttribute('aria-disabled');
                  btn.addEventListener('click', (e) => {
                    e.preventDefault();
                    e.stopPropagation();
                    requestInstall(location.href);
                  }, true);
                }
              }
            };

            const ensureBanner = () => {
              let host = document.getElementById('__omyaru_store_banner');
              if (!isDetailUrl()) {
                if (host) host.style.display = 'none';
                return;
              }
              if (!host) {
                host = document.createElement('div');
                host.id = '__omyaru_store_banner';
                host.style.cssText = 'position:fixed;right:22px;bottom:22px;z-index:2147483646;display:flex;align-items:center;gap:10px;padding:10px 14px;background:rgba(28,28,30,0.94);backdrop-filter:blur(16px);border:1px solid rgba(255,255,255,0.16);border-radius:16px;box-shadow:0 12px 32px rgba(0,0,0,0.45);font-family:"Segoe UI",system-ui,sans-serif;';
                const btn = document.createElement('button');
                btn.id = '__omyaru_store_install_btn';
                btn.type = 'button';
                btn.textContent = '🧩 Omyaru에 이 확장 프로그램 설치';
                btn.style.cssText = 'border:none;outline:none;cursor:pointer;padding:10px 18px;border-radius:12px;background:#0a84ff;color:#fff;font-size:14px;font-weight:600;letter-spacing:-0.2px;transition:transform 0.12s ease,background 0.15s ease;';
                btn.addEventListener('click', () => requestInstall(location.href));
                host.appendChild(btn);
                document.body.appendChild(host);
              } else {
                host.style.display = 'flex';
              }
              patchStoreButtons();
            };

            window.__omyaruRefreshStoreBanner = ensureBanner;
            ensureBanner();
            const observer = new MutationObserver(() => ensureBanner());
            observer.observe(document.documentElement, { childList: true, subtree: true });
          })();
          """);
    }
    catch
    {
      // Ignore script injection errors on restricted pages.
    }
  }

  private Task<CoreWebView2Environment> EnvironmentForProfileAsync(string profileId)
  {
    if (_environments.TryGetValue(profileId, out var environment)) return environment;
    var digest = Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(profileId))).ToLowerInvariant();
    var folder = Path.Combine(BrowserStateStore.StateDirectory, "profiles", digest);
    Directory.CreateDirectory(folder);
    var options = new CoreWebView2EnvironmentOptions
    {
      AllowSingleSignOnUsingOSPrimaryAccount = false,
      AreBrowserExtensionsEnabled = true,
    };
    environment = CoreWebView2Environment.CreateAsync(null, folder, options);
    _environments[profileId] = environment;
    return environment;
  }

  private void UpdateTab(string tabId, Func<TabEntry, TabEntry> update)
  {
    var index = _state.Tabs.FindIndex(tab => tab.Id == tabId);
    if (index < 0) return;
    var updated = update(_state.Tabs[index]);
    if (updated == _state.Tabs[index]) return;
    _state.Tabs[index] = updated;
    MarkLocalChange();
    var stripItem = _tabStrip.Controls.OfType<TabStripItemControl>()
        .FirstOrDefault(item => item.TabId == tabId);
    if (stripItem is not null) UpdateTabStripItemVisual(stripItem, updated);
    var overviewCard = _overviewGrid.Controls.OfType<OverviewTabCardControl>()
        .FirstOrDefault(card => card.TabId == tabId);
    if (overviewCard is not null) UpdateOverviewCardVisual(overviewCard, updated);
    if (_state.ActiveTabId == tabId) UpdateStatusText();
  }

  private void MarkLocalChange()
  {
    _stateDirty = true;
    _syncStatus = "로컬 변경 저장 대기";
    UpdateStatusText();
  }

  private void UpdateStatusText()
  {
    var tab = _state.Tabs.FirstOrDefault(item => item.Id == _state.ActiveTabId);
    _statusText.Text = $"{ActiveProfileName()} · {tab?.Title ?? "탭 없음"} · {_syncStatus}";
    var clickable = GoogleDriveAuthorization.NeedsReauthentication || _syncStatus.Contains("Drive", StringComparison.Ordinal) || _syncStatus.Contains("Google", StringComparison.Ordinal);
    _statusText.Cursor = clickable ? Cursors.Hand : Cursors.Default;
    _statusText.ForeColor = GoogleDriveAuthorization.NeedsReauthentication ? Color.FromArgb(255, 192, 110) : MutedText;
  }

  private string ActiveProfileName() =>
      _state.Profiles.FirstOrDefault(profile => profile.Id == _state.ActiveProfileId)?.Name ?? "프로필";

  private string ActiveGroupName() => _state.ActiveGroupId is null
      ? "그룹 없음"
      : _state.Groups.FirstOrDefault(group => group.Id == _state.ActiveGroupId)?.Name ?? "그룹 없음";

  private void OnDriveSessionChanged()
  {
    if (_closing || IsDisposed) return;
    if (InvokeRequired)
    {
      try { BeginInvoke(new Action(OnDriveSessionChanged)); } catch { }
      return;
    }
    _driveFailures = 0;
    _driveNextCheck = default;
    if (GoogleDriveAuthorization.Load() is null)
      _syncStatus = "로컬 저장 · 자동 동기화 꺼짐";
    else if (!GoogleDriveAuthorization.NeedsReauthentication)
      _syncStatus = "Google Drive 연결됨 · 동기화 확인 중…";
    UpdateStatusText();
    if (_enableAutomaticSync && !_autoSyncBusy)
      _ = FlushAndAutoSyncAsync();
  }

  private async Task FlushAndAutoSyncAsync()
  {
    if (_closing || _autoSyncBusy) return;
    var localChanged = _stateDirty;
    if (_stateDirty)
    {
      BrowserStateStore.SaveChanged(_state);
      _stateDirty = false;
    }
    if (!_enableAutomaticSync) return;
    if (GoogleDriveAuthorization.Load() is not null)
    {
      if (GoogleDriveAuthorization.NeedsReauthentication)
      {
        _syncStatus = "Google 로그인 만료됨 · 여기를 클릭해 다시 로그인";
        if (!_closing) UpdateStatusText();
        return;
      }
      if (_syncStatus.StartsWith("Google 로그인 만료됨", StringComparison.Ordinal))
      {
        _driveFailures = 0;
        _driveNextCheck = default;
      }
      if (DateTimeOffset.UtcNow < _driveNextCheck && !(localChanged && _driveFailures == 0)) return;
      var started = DateTimeOffset.UtcNow;
      _autoSyncBusy = true;
      try
      {
        var local = _state.Copy(false);
        var merged = await Task.Run(() => DesktopDriveSync.RunAsync(local, _driveCancellation.Token));
        if (_closing) return;
        if (!_stateDirty)
        {
          await ApplyDriveStateAsync(merged);
          // A navigation callback may have run while the new view was initializing.
          // Acknowledge exactly the merged state, not later local edits.
          await Task.Run(() => DesktopDriveSync.Acknowledge(merged));
        }
        var userDataChanged = await DesktopUserDataSync.RunAsync(_driveCancellation.Token);
        if (userDataChanged && !_closing)
        {
          _bookmarks = BookmarkStore.Load();
          PopulateFavoritesBar();
          PopulateStartPageFavorites();
        }
        _driveFailures = 0;
        _syncStatus = "Google Drive 자동 연동";
      }
      catch (OperationCanceledException) { }
      catch (GoogleDriveReauthenticationRequiredException)
      {
        _driveFailures = 6;
        _syncStatus = "Google 로그인 만료됨 · 여기를 클릭해 다시 로그인";
      }
      catch (Exception error)
      {
        _driveFailures = Math.Min(_driveFailures + 1, 6);
        _syncStatus = "Drive 연동 대기 · " + error.Message;
      }
      finally
      {
        _driveNextCheck = GoogleDriveAuthorization.NeedsReauthentication
            ? DateTimeOffset.UtcNow.AddHours(1)
            : (_driveFailures == 0 ? started : DateTimeOffset.UtcNow).AddSeconds(Math.Min(300, 3 * Math.Pow(2, _driveFailures)));
        _autoSyncBusy = false;
        if (!_closing) UpdateStatusText();
      }
      return;
    }
    var configuration = AutoSyncSettingsStore.Load();
    if (!configuration.Enabled)
    {
      _syncStatus = "로컬 저장 · 자동 동기화 꺼짐";
      UpdateStatusText();
      return;
    }

    _autoSyncBusy = true;
    _syncStatus = "자동 동기화 확인 중";
    UpdateStatusText();
    try
    {
      var syncSnapshot = CreateSyncSnapshot();
      var outcome = await Task.Run(() => DesktopAutoSync.Run(syncSnapshot));
      switch (outcome.Decision)
      {
        case AutoSyncDecision.Import when outcome.Imported is not null:
          if (_stateDirty)
          {
            AutoSyncSettingsStore.MarkConflict(AutoSyncSettingsStore.Load());
            _syncStatus = "자동 동기화 충돌 · 동기화 중 PC 데이터가 변경됨";
          }
          else
          {
            await ApplyImportedStateAsync(outcome.Imported);
            AutoSyncSettingsStore.UpdateBaseline(AutoSyncSettingsStore.Load(), outcome.Fingerprint!, outcome.Imported.ModifiedAtEpochMs);
            _syncStatus = "자동 동기화 · 파일에서 가져옴";
          }
          break;
        case AutoSyncDecision.Export:
          _syncStatus = "자동 동기화 · 파일로 내보냄";
          break;
        case AutoSyncDecision.Conflict:
          _syncStatus = "자동 동기화 충돌 · 동기화 버튼에서 유지할 쪽 선택";
          break;
        case AutoSyncDecision.NotReady:
          _syncStatus = outcome.Error ?? "자동 동기화 준비 필요";
          break;
        default:
          _syncStatus = "자동 동기화 연결됨";
          break;
      }
      if (!string.IsNullOrWhiteSpace(outcome.Error)) _syncStatus = "자동 동기화 오류 · " + outcome.Error;
      UpdateStatusText();
    }
    finally
    {
      _autoSyncBusy = false;
    }
  }

  private async Task NavigateFromAddressAsync() => await NavigateToAsync(ResolveInput(_address.Text));

  private async Task NavigateToAsync(string destination)
  {
    if (_state.ActiveTabId is null)
    {
      await CreateTabAsync(destination);
      return;
    }
    _showingOverview = false;
    UpdateTab(_state.ActiveTabId, tab => tab with { Url = destination });
    ApplySurfaceMode();
    if (_views.TryGetValue(_state.ActiveTabId, out var view) && view.CoreWebView2 is not null)
      view.CoreWebView2.Navigate(destination);
    else
      await ActivateCurrentTabAsync();
  }

  private string ResolveInput(string input)
  {
    var candidate = input.Trim();
    if (candidate.Length == 0) return "about:blank";
    if (Uri.TryCreate(candidate, UriKind.Absolute, out var absolute) &&
        (absolute.Scheme == Uri.UriSchemeHttps || absolute.Scheme == Uri.UriSchemeHttp))
    {
      if (_preferences.AutomaticHttps && absolute.Scheme == Uri.UriSchemeHttp)
      {
        var secureAddress = new UriBuilder(absolute) { Scheme = Uri.UriSchemeHttps, Port = -1 };
        return secureAddress.Uri.ToString();
      }
      return absolute.ToString();
    }
    if (!candidate.Contains(' ') && (candidate.Contains('.') || candidate.Equals("localhost", StringComparison.OrdinalIgnoreCase)))
      return (_preferences.AutomaticHttps ? "https://" : "http://") + candidate;
    var query = Uri.EscapeDataString(candidate);
    return _preferences.SearchEngine switch
    {
      "Google" => "https://www.google.com/search?q=" + query,
      "Bing" => "https://www.bing.com/search?q=" + query,
      _ => "https://duckduckgo.com/?q=" + query,
    };
  }

  private string DisplayAddress(string url)
  {
    if (url == "about:blank") return string.Empty;
    return _preferences.ShowFullUrls ? url : HostName(url);
  }

  private void ApplyPreferences(DesktopPreferences preferences)
  {
    var audioOutputChanged = _preferences.AudioOutputDeviceId != preferences.AudioOutputDeviceId
        || _preferences.AudioOutputLabel != preferences.AudioOutputLabel;
    var bookmarkAppearanceChanged = _preferences.BookmarkIconsOnly != preferences.BookmarkIconsOnly
        || _preferences.ToolbarShortcuts != preferences.ToolbarShortcuts || _preferences.ShowFavoritesBar != preferences.ShowFavoritesBar;
    _preferences = preferences;
    var tab = _state.Tabs.FirstOrDefault(item => item.Id == _state.ActiveTabId);
    if (!_address.Focused) _address.Text = DisplayAddress(tab?.Url ?? "about:blank");
    ApplyChromePreferencesLayout();
    if (bookmarkAppearanceChanged) PopulateFavoritesBar();
    ApplySurfaceMode();
    if (audioOutputChanged) _ = ApplyAudioOutputToAllAsync();
  }

  private Task ShowSettingsDialogAsync()
  {
    using var dialog = new SettingsForm(
        _preferences,
        ApplyPreferences,
        async () => await ShowSyncDialogAsync(),
        ChooseAudioOutputFromSettingsAsync);
    dialog.ShowDialog(this);
    return Task.CompletedTask;
  }

  private async Task ActiveViewActionAsync(Action<CoreWebView2> action, Func<CoreWebView2, bool>? allowed = null)
  {
    if (_state.ActiveTabId is null || !_views.TryGetValue(_state.ActiveTabId, out var view) || view.CoreWebView2 is null) return;
    if (allowed?.Invoke(view.CoreWebView2) == false) return;
    action(view.CoreWebView2);
    await Task.CompletedTask;
  }

  private async Task ShowSyncDialogAsync()
  {
    if (_autoSyncBusy) return;
    _autoSyncBusy = true;
    DialogResult result;
    try
    {
      using var dialog = new GoogleDriveSyncDialog();
      result = dialog.ShowDialog(this);
    }
    finally
    {
      _autoSyncBusy = false;
      _driveNextCheck = default;
    }
    if (result == DialogResult.OK || !GoogleDriveAuthorization.NeedsReauthentication)
    {
      _driveFailures = 0;
      if (GoogleDriveAuthorization.Load() is not null && !GoogleDriveAuthorization.NeedsReauthentication)
      {
        _syncStatus = "Google Drive 동기화 중…";
        UpdateStatusText();
      }
    }
    if (result == DialogResult.Retry) await ShowFileSyncDialogAsync();
    else await FlushAndAutoSyncAsync();
  }

  private async Task ShowFileSyncDialogAsync()
  {
    using var dialog = new SyncDialog(AutoSyncSettingsStore.Load());
    if (dialog.ShowDialog(this) != DialogResult.OK) return;
    try
    {
      if (dialog.Importing)
      {
        var imported = await Task.Run(() => SyncCodec.Read(dialog.SyncPath, dialog.Passphrase));
        await ApplyImportedStateAsync(imported);
        if (dialog.AutomaticSync)
          AutoSyncSettingsStore.Configure(dialog.SyncPath, dialog.Passphrase, SyncFileFingerprint.Compute(dialog.SyncPath), imported.ModifiedAtEpochMs);
        else
          AutoSyncSettingsStore.Disable();
        MessageBox.Show(this, "Android/Windows 동기화 파일을 가져왔습니다.", "동기화 완료");
      }
      else
      {
        BrowserStateStore.SaveChanged(_state);
        _stateDirty = false;
        var syncSnapshot = CreateSyncSnapshot();
        await Task.Run(() => SyncCodec.Write(dialog.SyncPath, syncSnapshot, dialog.Passphrase));
        if (dialog.AutomaticSync)
          AutoSyncSettingsStore.Configure(dialog.SyncPath, dialog.Passphrase, SyncFileFingerprint.Compute(dialog.SyncPath), _state.ModifiedAtEpochMs);
        else
          AutoSyncSettingsStore.Disable();
        MessageBox.Show(this, "프로필·탭 그룹·탭 순서·탭 미리보기를 암호화해 저장했습니다.", "동기화 완료");
      }
      _syncStatus = dialog.AutomaticSync ? "자동 동기화 연결됨" : "로컬 저장 · 자동 동기화 꺼짐";
      UpdateStatusText();
    }
    catch (Exception error)
    {
      MessageBox.Show(this, "동기화하지 못했습니다. 파일 권한과 암호를 확인하세요.\n\n" + error.Message, "동기화 오류");
    }
  }

  private async Task ApplyImportedStateAsync(SyncSnapshot imported)
  {
    foreach (var view in _views.Values)
    {
      _browserHost.Controls.Remove(view);
      view.Dispose();
    }
    _views.Clear();
    foreach (var preview in _previews.Values) preview.Dispose();
    _previews.Clear();
    _state = imported;
    EnsureWorkspaceTab();
    foreach (var tab in _state.Tabs)
    {
      if (DesktopThumbnailStore.ImportBase64(tab.Id, tab.ThumbnailBase64))
      {
        var restored = DesktopThumbnailStore.Load(tab.Id);
        if (restored is not null) _previews[tab.Id] = restored;
      }
      else
      {
        DesktopThumbnailStore.Delete(tab.Id);
      }
    }
    DesktopThumbnailStore.Prune(_state.Tabs.Select(tab => tab.Id));
    _state = _state.Copy(includeThumbnails: false);
    _stateDirty = false;
    BrowserStateStore.SaveImported(_state);
    RefreshWorkspaceUi();
    await ActivateCurrentTabAsync();
  }

  private async Task ApplyDriveStateAsync(SyncSnapshot imported)
  {
    var previous = _state;
    imported.ActiveProfileId = previous.ActiveProfileId;
    imported.ActiveGroupId = previous.ActiveGroupId;
    imported.ActiveTabId = previous.ActiveTabId;
    imported.Normalize();
    if (previous.Profiles.SequenceEqual(imported.Profiles) && previous.Groups.SequenceEqual(imported.Groups) &&
        previous.Tabs.Select(t => t with { ThumbnailBase64 = null }).SequenceEqual(imported.Tabs)) return;
    var liveIds = imported.Tabs.Select(t => t.Id).ToHashSet();
    foreach (var id in _views.Keys.Where(id => !liveIds.Contains(id)).ToArray())
    {
      var view = _views[id];
      _browserHost.Controls.Remove(view);
      _views.Remove(id);
      view.Dispose();
      if (_previews.Remove(id, out var preview)) preview.Dispose();
    }
    _state = imported.Copy(false);
    _stateDirty = false;
    BrowserStateStore.SaveImported(_state);
    foreach (var tab in _state.Tabs)
      if (previous.Tabs.FirstOrDefault(t => t.Id == tab.Id) is { } old && old.Url != tab.Url &&
          _views.TryGetValue(tab.Id, out var view) && view.CoreWebView2 is not null)
        view.CoreWebView2.Navigate(tab.Url);
    RefreshWorkspaceUi();
    // Unchanged active tabs keep their WebView/session and scroll position.
    if (_state.ActiveTabId is null || !_views.ContainsKey(_state.ActiveTabId))
    {
      var selected = _state.Tabs.FirstOrDefault(t => t.ProfileId == _state.ActiveProfileId && t.GroupId == _state.ActiveGroupId)
          ?? _state.Tabs.FirstOrDefault();
      if (selected is not null)
      {
        _state.ActiveTabId = selected.Id;
        _state.ActiveProfileId = selected.ProfileId;
        _state.ActiveGroupId = selected.GroupId;
        await ActivateCurrentTabAsync();
      }
      else { _address.Text = string.Empty; ApplySurfaceMode(); }
    }
    else ApplySurfaceMode();
  }

  private void LoadPersistedPreviews()
  {
    DesktopThumbnailStore.Prune(_state.Tabs.Select(tab => tab.Id));
    foreach (var tab in _state.Tabs)
    {
      var preview = DesktopThumbnailStore.Load(tab.Id);
      if (preview is not null) _previews[tab.Id] = preview;
    }
  }

  private SyncSnapshot CreateSyncSnapshot()
  {
    var snapshot = _state.Copy(includeThumbnails: false);
    snapshot.SchemaVersion = 2;
    snapshot.Tabs = snapshot.Tabs.Select((tab, index) => tab with
    {
      Order = index,
      ThumbnailBase64 = DesktopThumbnailStore.ExportBase64(tab.Id),
    }).ToList();
    return snapshot;
  }

  private static PillButton ChromeButton(string text, EventHandler action, int width, string tooltip)
  {
    var button = new PillButton
    {
      Text = text,
      Width = width,
      Height = 32,
      BackColor = ChromeRaised,
      HoverBackColor = Color.FromArgb(56, 56, 62),
      ForeColor = TextColor,
      Margin = new Padding(2, 0, 2, 0),
      TabStop = false,
      Cursor = Cursors.Hand,
      CornerRadius = 10,
      BorderColor = Outline,
      BorderWidth = 1F,
      AccessibleName = tooltip,
    };
    button.Click += action;
    new ToolTip().SetToolTip(button, tooltip);
    return button;
  }

  private static PillButton IconButton(PillGlyph glyph, EventHandler action, int diameter = 36, string tooltip = "")
  {
    if (diameter <= 0) diameter = 36;
    var button = new PillButton
    {
      Width = diameter,
      Height = diameter,
      MinimumSize = new Size(diameter, diameter),
      MaximumSize = new Size(diameter, diameter),
      Text = string.Empty,
      Glyph = glyph,
      BackColor = Chrome,
      HoverBackColor = Color.FromArgb(50, 50, 56),
      ForeColor = TextColor,
      BorderColor = Color.Transparent,
      BorderWidth = 0F,
      Margin = new Padding(1, 0, 1, 0),
      CornerRadius = 10,
      AccessibleName = tooltip,
    };
    button.Click += action;
    new ToolTip().SetToolTip(button, tooltip);
    return button;
  }

  private static void ConfigureTextBox(TextBox box)
  {
    box.Height = 34;
    box.BackColor = Field;
    box.ForeColor = TextColor;
    box.BorderStyle = BorderStyle.None;
    box.Margin = new Padding(4, 0, 4, 0);
  }

  private static string Shorten(string value, int limit) => value.Length <= limit ? value : value[..(limit - 1)] + "…";

  private static string HostName(string url) => Uri.TryCreate(url, UriKind.Absolute, out var uri)
      ? uri.Host.Replace("www.", "", StringComparison.OrdinalIgnoreCase)
      : "웹 페이지";

  private sealed record SettingsMenuEntry(
      string Glyph,
      string Caption,
      Func<Task> Action,
      string? Shortcut = null,
      bool Destructive = false);

}

internal sealed class GradientPanel : Panel
{
  internal Color StartColor { get; set; } = Color.FromArgb(50, 51, 53);
  internal Color EndColor { get; set; } = Color.FromArgb(34, 35, 38);

  internal GradientPanel()
  {
    DoubleBuffered = true;
  }

  protected override void OnPaintBackground(PaintEventArgs eventArgs)
  {
    using var brush = new System.Drawing.Drawing2D.LinearGradientBrush(ClientRectangle, StartColor, EndColor, 90F);
    eventArgs.Graphics.FillRectangle(brush, ClientRectangle);
  }
}

internal sealed class RoundedPanel : Panel
{
  private bool _clipContent;

  internal int CornerRadius { get; set; } = 14;
  internal Color BorderColor { get; set; } = Color.Transparent;
  internal float BorderWidth { get; set; }
  internal bool ClipContent
  {
    get => _clipContent;
    set
    {
      if (_clipContent == value) return;
      _clipContent = value;
      UpdateClippingRegion();
      Invalidate();
    }
  }

  internal RoundedPanel()
  {
    DoubleBuffered = true;
    ResizeRedraw = true;
  }

  protected override void OnResize(EventArgs eventArgs)
  {
    base.OnResize(eventArgs);
    UpdateClippingRegion();
  }

  private void UpdateClippingRegion()
  {
    var previous = Region;
    if (_clipContent && ClientSize.Width > 2 && ClientSize.Height > 2)
    {
      using var path = UiGeometry.RoundedRectangle(Rectangle.Inflate(ClientRectangle, -1, -1), CornerRadius);
      Region = new Region(path);
    }
    else
    {
      Region = null;
    }
    previous?.Dispose();
  }

  protected override void OnPaintBackground(PaintEventArgs eventArgs)
  {
    var graphics = eventArgs.Graphics;
    graphics.SmoothingMode = System.Drawing.Drawing2D.SmoothingMode.AntiAlias;
    graphics.PixelOffsetMode = System.Drawing.Drawing2D.PixelOffsetMode.HighQuality;
    graphics.CompositingQuality = System.Drawing.Drawing2D.CompositingQuality.HighQuality;
    graphics.CompositingMode = System.Drawing.Drawing2D.CompositingMode.SourceCopy;
    using var background = new SolidBrush(UiGeometry.ResolveBackground(Parent, BackColor));
    graphics.FillRectangle(background, ClientRectangle);
    graphics.CompositingMode = System.Drawing.Drawing2D.CompositingMode.SourceOver;
    var rectangle = new RectangleF(1.5F, 1.5F, Math.Max(0, ClientSize.Width - 3F), Math.Max(0, ClientSize.Height - 3F));
    using var path = UiGeometry.RoundedRectangle(rectangle, Math.Max(1F, CornerRadius - 1.5F));
    using var brush = new SolidBrush(BackColor);
    graphics.FillPath(brush, path);
  }

  protected override void OnPaint(PaintEventArgs eventArgs)
  {
    base.OnPaint(eventArgs);
    if (BorderWidth <= 0 || BorderColor == Color.Transparent) return;
    var graphics = eventArgs.Graphics;
    graphics.SmoothingMode = System.Drawing.Drawing2D.SmoothingMode.AntiAlias;
    graphics.PixelOffsetMode = System.Drawing.Drawing2D.PixelOffsetMode.HighQuality;
    graphics.CompositingQuality = System.Drawing.Drawing2D.CompositingQuality.HighQuality;
    var inset = 2F + BorderWidth / 2F;
    var rectangle = new RectangleF(inset, inset,
        Math.Max(0, ClientSize.Width - inset * 2F),
        Math.Max(0, ClientSize.Height - inset * 2F));
    using var path = UiGeometry.RoundedRectangle(rectangle, Math.Max(1F, CornerRadius - inset));
    using var pen = new Pen(BorderColor, BorderWidth)
    {
      StartCap = System.Drawing.Drawing2D.LineCap.Round,
      EndCap = System.Drawing.Drawing2D.LineCap.Round,
      LineJoin = System.Drawing.Drawing2D.LineJoin.Round,
    };
    graphics.DrawPath(pen, path);
  }
}

internal enum CaptionGlyph
{
  Minimize,
  Maximize,
  Restore,
  Close,
}

internal sealed class WindowCaptionButton : Control
{
  private readonly System.Windows.Forms.Timer _animationTimer = new() { Interval = 15 };
  private bool _hovered;
  private bool _pressed;
  private float _hoverAmount;
  private float _pressAmount;
  private CaptionGlyph _glyph;

  internal CaptionGlyph Glyph
  {
    get => _glyph;
    set
    {
      _glyph = value;
      Invalidate();
    }
  }

  internal bool IsCloseButton { get; set; }

  internal WindowCaptionButton()
  {
    SetStyle(ControlStyles.UserPaint | ControlStyles.AllPaintingInWmPaint |
        ControlStyles.OptimizedDoubleBuffer | ControlStyles.ResizeRedraw, true);
    BackColor = Color.FromArgb(20, 24, 29);
    ForeColor = Color.FromArgb(235, 238, 242);
    Cursor = Cursors.Hand;
    TabStop = false;
    Margin = Padding.Empty;
    _animationTimer.Tick += (_, _) => AdvanceAnimation();
  }

  protected override void OnMouseEnter(EventArgs eventArgs)
  {
    base.OnMouseEnter(eventArgs);
    _hovered = true;
    _animationTimer.Start();
  }

  protected override void OnMouseLeave(EventArgs eventArgs)
  {
    base.OnMouseLeave(eventArgs);
    _hovered = false;
    _pressed = false;
    _animationTimer.Start();
  }

  protected override void OnMouseDown(MouseEventArgs eventArgs)
  {
    base.OnMouseDown(eventArgs);
    if (eventArgs.Button == MouseButtons.Left)
    {
      _pressed = true;
      _animationTimer.Start();
    }
  }

  protected override void OnMouseUp(MouseEventArgs eventArgs)
  {
    base.OnMouseUp(eventArgs);
    _pressed = false;
    _animationTimer.Start();
  }

  private void AdvanceAnimation()
  {
    _hoverAmount += ((_hovered ? 1F : 0F) - _hoverAmount) * 0.34F;
    _pressAmount += ((_pressed ? 1F : 0F) - _pressAmount) * 0.42F;
    if (Math.Abs(_hoverAmount - (_hovered ? 1F : 0F)) < 0.015F &&
        Math.Abs(_pressAmount - (_pressed ? 1F : 0F)) < 0.015F)
    {
      _hoverAmount = _hovered ? 1F : 0F;
      _pressAmount = _pressed ? 1F : 0F;
      _animationTimer.Stop();
    }
    Invalidate();
  }

  protected override void OnPaint(PaintEventArgs eventArgs)
  {
    var graphics = eventArgs.Graphics;
    graphics.SmoothingMode = SmoothingMode.AntiAlias;
    var resting = UiGeometry.ResolveBackground(Parent, BackColor);
    var hover = IsCloseButton ? Color.FromArgb(196, 43, 28) : Color.FromArgb(57, 63, 72);
    var fill = UiGeometry.Blend(resting, hover, _hoverAmount);
    fill = UiGeometry.Blend(fill, ControlPaint.Dark(fill, 0.12F), _pressAmount);
    using (var brush = new SolidBrush(fill)) graphics.FillRectangle(brush, ClientRectangle);

    var centerX = ClientSize.Width / 2F;
    var centerY = ClientSize.Height / 2F + _pressAmount;
    using var pen = new Pen(ForeColor, 1.45F)
    {
      StartCap = LineCap.Square,
      EndCap = LineCap.Square,
      LineJoin = LineJoin.Miter,
    };
    switch (Glyph)
    {
      case CaptionGlyph.Minimize:
        graphics.DrawLine(pen, centerX - 6, centerY + 4, centerX + 6, centerY + 4);
        break;
      case CaptionGlyph.Maximize:
        graphics.DrawRectangle(pen, centerX - 6, centerY - 6, 12, 11);
        break;
      case CaptionGlyph.Restore:
        graphics.DrawRectangle(pen, centerX - 4, centerY - 6, 10, 9);
        graphics.DrawRectangle(pen, centerX - 7, centerY - 3, 10, 9);
        break;
      case CaptionGlyph.Close:
        graphics.DrawLine(pen, centerX - 6, centerY - 6, centerX + 6, centerY + 6);
        graphics.DrawLine(pen, centerX + 6, centerY - 6, centerX - 6, centerY + 6);
        break;
    }
  }

  protected override void Dispose(bool disposing)
  {
    if (disposing) _animationTimer.Dispose();
    base.Dispose(disposing);
  }
}

internal sealed class AnimatedSnapshotOverlay : Control
{
  private Bitmap? _frame;
  private TaskCompletionSource? _completion;

  internal AnimatedSnapshotOverlay()
  {
    SetStyle(ControlStyles.UserPaint | ControlStyles.AllPaintingInWmPaint |
        ControlStyles.OptimizedDoubleBuffer | ControlStyles.ResizeRedraw, true);
    BackColor = Color.FromArgb(13, 17, 22);
    TabStop = false;
    Enabled = false;
  }

  internal void Hold(Bitmap frame)
  {
    _completion?.TrySetResult();
    _completion = null;
    Region?.Dispose();
    Region = null;
    _frame?.Dispose();
    _frame = frame;
    Visible = false;
  }

  internal Task AnimateAwayAsync(int duration)
  {
    HideFrame();
    return Task.CompletedTask;
  }

  internal void HideFrame()
  {
    Region?.Dispose();
    Region = null;
    Visible = false;
    _frame?.Dispose();
    _frame = null;
    var completion = _completion;
    _completion = null;
    completion?.TrySetResult();
  }

  protected override void OnPaint(PaintEventArgs eventArgs)
  {
    eventArgs.Graphics.Clear(BackColor);
    if (_frame is null || ClientSize.Width < 1 || ClientSize.Height < 1) return;
    eventArgs.Graphics.InterpolationMode = InterpolationMode.HighQualityBilinear;
    eventArgs.Graphics.DrawImage(_frame, new Rectangle(0, 0, ClientSize.Width, ClientSize.Height));
  }

  protected override void Dispose(bool disposing)
  {
    if (disposing)
    {
      _frame?.Dispose();
      _frame = null;
      _completion?.TrySetResult();
      _completion = null;
    }
    base.Dispose(disposing);
  }
}

internal sealed class TabStripItemControl : Control
{
  private readonly System.Windows.Forms.Timer _interactionTimer = new() { Interval = 15 };
  private Image? _favicon;
  private bool _hovered;
  private bool _hoveringClose;
  private bool _suppressClick;
  private bool _dragging;
  private bool _pressed;
  private float _hoverAmount;
  private float _pressAmount;
  private Point _dragOrigin;

  internal string TabId { get; set; } = string.Empty;
  internal bool Active { get; set; }
  internal Color ActiveBackColor { get; set; } = Color.FromArgb(52, 52, 58);
  internal Color AccentColor { get; set; } = Color.FromArgb(72, 72, 82);
  internal Color InactiveBorderColor { get; set; } = Color.Transparent;
  internal Image? Favicon
  {
    get => _favicon;
    set
    {
      if (ReferenceEquals(_favicon, value)) return;
      _favicon?.Dispose();
      _favicon = value;
      Invalidate();
    }
  }
  internal event EventHandler? SelectRequested;
  internal event EventHandler? CloseRequested;
  internal event EventHandler? DuplicateRequested;
  internal event EventHandler? CloseOtherRequested;
  internal event EventHandler? CloseAllRequested;
  internal event EventHandler? RestoreClosedRequested;
  internal event EventHandler? NewTabRequested;
  internal event EventHandler<TabReorderEventArgs>? ReorderRequested;
  internal event EventHandler<TabReorderEventArgs>? ReorderPreviewRequested;
  internal event EventHandler<TabDragCompletedEventArgs>? DragCompleted;
  internal event EventHandler? HoverPreviewRequested;
  internal event EventHandler? HoverPreviewDismissed;

  internal TabStripItemControl()
  {
    SetStyle(ControlStyles.UserPaint | ControlStyles.AllPaintingInWmPaint |
        ControlStyles.OptimizedDoubleBuffer | ControlStyles.ResizeRedraw, true);
    Cursor = Cursors.Hand;
    TabStop = false;
    AccessibleRole = AccessibleRole.PageTab;
    AllowDrop = true;
    _interactionTimer.Tick += (_, _) =>
    {
      _hoverAmount += ((_hovered ? 1F : 0F) - _hoverAmount) * 0.34F;
      _pressAmount += ((_pressed ? 1F : 0F) - _pressAmount) * 0.44F;
      if (Math.Abs(_hoverAmount - (_hovered ? 1F : 0F)) < 0.015F &&
          Math.Abs(_pressAmount - (_pressed ? 1F : 0F)) < 0.015F)
      {
        _hoverAmount = _hovered ? 1F : 0F;
        _pressAmount = _pressed ? 1F : 0F;
        _interactionTimer.Stop();
      }
      Invalidate();
    };
  }

  protected override void OnMouseDown(MouseEventArgs eventArgs)
  {
    base.OnMouseDown(eventArgs);
    if (eventArgs.Button == MouseButtons.Left)
    {
      _pressed = true;
      _interactionTimer.Start();
    }
    else if (eventArgs.Button == MouseButtons.Middle)
    {
      CloseRequested?.Invoke(this, EventArgs.Empty);
      return;
    }
    else if (eventArgs.Button == MouseButtons.Right)
    {
      ShowTabContextMenu(eventArgs.Location);
      return;
    }
    if (eventArgs.Button == MouseButtons.Left && (ClientSize.Width < 72 || eventArgs.X < ClientSize.Width - 32))
      _dragOrigin = eventArgs.Location;
  }

  private void ShowTabContextMenu(Point point)
  {
    var menu = new ContextMenuStrip
    {
      Renderer = new AndroidMenuRenderer(),
      BackColor = Color.FromArgb(38, 38, 42),
      ForeColor = Color.FromArgb(245, 245, 247),
      ShowImageMargin = false,
      Font = new Font("Segoe UI", 9.5F),
    };
    menu.Items.Add("탭 복제", null, (_, _) => DuplicateRequested?.Invoke(this, EventArgs.Empty));
    menu.Items.Add(new ToolStripSeparator());
    menu.Items.Add("선택한 탭 닫기 (Ctrl+W)", null, (_, _) => CloseRequested?.Invoke(this, EventArgs.Empty));
    menu.Items.Add("다른 탭 모두 닫기", null, (_, _) => CloseOtherRequested?.Invoke(this, EventArgs.Empty));
    menu.Items.Add("모든 탭 닫기 (5초 확인)", null, (_, _) => CloseAllRequested?.Invoke(this, EventArgs.Empty));
    menu.Items.Add(new ToolStripSeparator());
    menu.Items.Add("닫은 탭 다시 열기 (Ctrl+Shift+T)", null, (_, _) => RestoreClosedRequested?.Invoke(this, EventArgs.Empty));
    menu.Items.Add("새 탭 (Ctrl+T)", null, (_, _) => NewTabRequested?.Invoke(this, EventArgs.Empty));
    foreach (ToolStripItem item in menu.Items) item.ForeColor = Color.FromArgb(245, 245, 247);
    menu.Show(this, point);
  }

  protected override void OnMouseUp(MouseEventArgs eventArgs)
  {
    base.OnMouseUp(eventArgs);
    _pressed = false;
    _interactionTimer.Start();
  }

  protected override void OnMouseEnter(EventArgs eventArgs)
  {
    base.OnMouseEnter(eventArgs);
    _hovered = true;
    _interactionTimer.Start();
    if (!_dragging) HoverPreviewRequested?.Invoke(this, EventArgs.Empty);
  }

  protected override void OnMouseMove(MouseEventArgs eventArgs)
  {
    base.OnMouseMove(eventArgs);
    var hoveringClose = ClientSize.Width >= 72 && eventArgs.X >= ClientSize.Width - 32;
    if (_hoveringClose != hoveringClose)
    {
      _hoveringClose = hoveringClose;
      Invalidate(new Rectangle(Math.Max(0, ClientSize.Width - 36), 0, 36, ClientSize.Height));
    }
    if (eventArgs.Button == MouseButtons.Left && !_hoveringClose && !_dragging && !string.IsNullOrWhiteSpace(TabId))
    {
      var dragBounds = new Rectangle(
          _dragOrigin.X - SystemInformation.DragSize.Width / 2,
          _dragOrigin.Y - SystemInformation.DragSize.Height / 2,
          SystemInformation.DragSize.Width,
          SystemInformation.DragSize.Height);
      if (!dragBounds.Contains(eventArgs.Location))
      {
        _suppressClick = true;
        _dragging = true;
        HoverPreviewDismissed?.Invoke(this, EventArgs.Empty);
        using var dragPreview = new DragPreviewWindow(this, _dragOrigin);
        using var followPointer = new System.Windows.Forms.Timer { Interval = 16 };
        followPointer.Tick += (_, _) => dragPreview.FollowPointer();
        try
        {
          dragPreview.Show(FindForm());
          followPointer.Start();
          var result = DoDragDrop(TabId, DragDropEffects.Move);
          DragCompleted?.Invoke(this, new TabDragCompletedEventArgs(result == DragDropEffects.Move));
        }
        finally
        {
          followPointer.Stop();
          dragPreview.Close();
          _dragging = false;
        }
      }
    }
  }

  protected override void OnMouseLeave(EventArgs eventArgs)
  {
    base.OnMouseLeave(eventArgs);
    HoverPreviewDismissed?.Invoke(this, EventArgs.Empty);
    _hovered = false;
    _pressed = false;
    _hoveringClose = false;
    _interactionTimer.Start();
  }

  protected override void OnMouseClick(MouseEventArgs eventArgs)
  {
    base.OnMouseClick(eventArgs);
    if (eventArgs.Button != MouseButtons.Left) return;
    if (_suppressClick)
    {
      _suppressClick = false;
      return;
    }
    if (ClientSize.Width >= 72 && eventArgs.X >= ClientSize.Width - 32) CloseRequested?.Invoke(this, EventArgs.Empty);
    else SelectRequested?.Invoke(this, EventArgs.Empty);
  }

  protected override void OnDragEnter(DragEventArgs eventArgs)
  {
    base.OnDragEnter(eventArgs);
    eventArgs.Effect = eventArgs.Data?.GetDataPresent(DataFormats.Text) == true
        ? DragDropEffects.Move
        : DragDropEffects.None;
  }

  protected override void OnDragOver(DragEventArgs eventArgs)
  {
    base.OnDragOver(eventArgs);
    eventArgs.Effect = eventArgs.Data?.GetDataPresent(DataFormats.Text) == true
        ? DragDropEffects.Move
        : DragDropEffects.None;
    var sourceTabId = eventArgs.Data?.GetData(DataFormats.Text) as string;
    if (eventArgs.Effect == DragDropEffects.Move && !string.IsNullOrWhiteSpace(sourceTabId) && sourceTabId != TabId)
    {
      var localPoint = PointToClient(new Point(eventArgs.X, eventArgs.Y));
      ReorderPreviewRequested?.Invoke(this,
          new TabReorderEventArgs(sourceTabId, TabId, localPoint.X >= ClientSize.Width / 2));
    }
  }

  protected override void OnDragDrop(DragEventArgs eventArgs)
  {
    base.OnDragDrop(eventArgs);
    var sourceTabId = eventArgs.Data?.GetData(DataFormats.Text) as string;
    if (string.IsNullOrWhiteSpace(sourceTabId) || string.IsNullOrWhiteSpace(TabId) || sourceTabId == TabId) return;
    var localPoint = PointToClient(new Point(eventArgs.X, eventArgs.Y));
    ReorderRequested?.Invoke(this, new TabReorderEventArgs(sourceTabId, TabId, localPoint.X >= ClientSize.Width / 2));
  }

  protected override void OnPaintBackground(PaintEventArgs eventArgs)
  {
    using var background = new SolidBrush(UiGeometry.ResolveBackground(Parent, BackColor));
    eventArgs.Graphics.FillRectangle(background, ClientRectangle);
  }

  protected override void OnPaint(PaintEventArgs eventArgs)
  {
    var graphics = eventArgs.Graphics;
    graphics.SmoothingMode = System.Drawing.Drawing2D.SmoothingMode.AntiAlias;
    graphics.PixelOffsetMode = System.Drawing.Drawing2D.PixelOffsetMode.HighQuality;
    graphics.CompositingQuality = System.Drawing.Drawing2D.CompositingQuality.HighQuality;
    graphics.InterpolationMode = System.Drawing.Drawing2D.InterpolationMode.HighQualityBicubic;

    var inset = 1.5F + _pressAmount * 0.8F;
    var rectangle = new RectangleF(inset, inset, Math.Max(0, ClientSize.Width - inset * 2F), Math.Max(0, ClientSize.Height - inset * 2F));
    using var path = UiGeometry.RoundedRectangle(rectangle, 10F);

    var restingFill = Active ? ActiveBackColor : BackColor;
    var hoverFill = Active
        ? Color.FromArgb(60, 60, 66)
        : Color.FromArgb(40, 40, 46);
    var fillColor = UiGeometry.Blend(restingFill, hoverFill, _hoverAmount);
    using (var fill = new SolidBrush(fillColor))
      graphics.FillPath(fill, path);

    if (Active)
    {
      using var border = new Pen(AccentColor, 1F)
      {
        StartCap = System.Drawing.Drawing2D.LineCap.Round,
        EndCap = System.Drawing.Drawing2D.LineCap.Round,
        LineJoin = System.Drawing.Drawing2D.LineJoin.Round,
      };
      graphics.DrawPath(border, path);
      using var topHighlight = new Pen(Color.FromArgb(28, 255, 255, 255), 1F);
      graphics.DrawLine(topHighlight, rectangle.Left + 8, rectangle.Top + 1F, rectangle.Right - 8, rectangle.Top + 1F);
    }
    else if (_hoverAmount < 0.1F)
    {
      using var sepPen = new Pen(Color.FromArgb(48, 48, 54), 1F);
      graphics.DrawLine(sepPen, ClientSize.Width - 1, 7, ClientSize.Width - 1, ClientSize.Height - 7);
    }

    var textLeft = 12;
    if (_favicon is not null)
    {
      var iconY = (ClientSize.Height - 16) / 2 + (int)Math.Round(_pressAmount * 0.5F);
      graphics.DrawImage(_favicon, new Rectangle(10, iconY, 16, 16));
      textLeft = 32;
    }

    var textRectangle = new Rectangle(textLeft, (int)Math.Round(_pressAmount * 0.5F), Math.Max(0, ClientSize.Width - textLeft - 30), ClientSize.Height);
    TextRenderer.DrawText(graphics, Text ?? string.Empty, Font, textRectangle, ForeColor,
        TextFormatFlags.Left | TextFormatFlags.VerticalCenter | TextFormatFlags.EndEllipsis |
        TextFormatFlags.SingleLine | TextFormatFlags.NoPadding);

    if (ClientSize.Width >= 72 && (Active || _hovered || ClientSize.Width >= 110))
    {
      var closeSize = 20;
      var closeRectangle = new Rectangle(ClientSize.Width - 26, (ClientSize.Height - closeSize) / 2, closeSize, closeSize);
      if (_hoveringClose)
      {
        using var hover = new SolidBrush(Color.FromArgb(72, 72, 80));
        using var closePath = UiGeometry.RoundedRectangle(closeRectangle, 6);
        graphics.FillPath(hover, closePath);
      }
      var closeAlpha = Active || _hovered ? 220 : 120;
      using var closePen = new Pen(Color.FromArgb(closeAlpha, ForeColor), 1.5F)
      {
        StartCap = System.Drawing.Drawing2D.LineCap.Round,
        EndCap = System.Drawing.Drawing2D.LineCap.Round,
      };
      var centerX = closeRectangle.Left + closeRectangle.Width / 2F;
      var centerY = closeRectangle.Top + closeRectangle.Height / 2F + _pressAmount * 0.5F;
      graphics.DrawLine(closePen, centerX - 3.5F, centerY - 3.5F, centerX + 3.5F, centerY + 3.5F);
      graphics.DrawLine(closePen, centerX + 3.5F, centerY - 3.5F, centerX - 3.5F, centerY + 3.5F);
    }
  }

  protected override void Dispose(bool disposing)
  {
    if (disposing)
    {
      _interactionTimer.Dispose();
      _favicon?.Dispose();
      _favicon = null;
    }
    base.Dispose(disposing);
  }
}

internal sealed class TabReorderEventArgs : EventArgs
{
  internal TabReorderEventArgs(string sourceTabId, string targetTabId, bool placeAfter)
  {
    SourceTabId = sourceTabId;
    TargetTabId = targetTabId;
    PlaceAfter = placeAfter;
  }

  internal string SourceTabId { get; }
  internal string TargetTabId { get; }
  internal bool PlaceAfter { get; }
}

internal sealed class TabDragCompletedEventArgs(bool committed) : EventArgs
{
  internal bool Committed { get; } = committed;
}

internal sealed class DragPreviewWindow : Form
{
  private readonly Bitmap _snapshot;
  private readonly Point _pointerOffset;

  internal DragPreviewWindow(Control source, Point pointerOffset)
  {
    _pointerOffset = pointerOffset;
    _snapshot = new Bitmap(Math.Max(1, source.Width), Math.Max(1, source.Height));
    source.DrawToBitmap(_snapshot, new Rectangle(Point.Empty, _snapshot.Size));
    AutoScaleMode = AutoScaleMode.None;
    BackColor = Color.Magenta;
    TransparencyKey = Color.Magenta;
    FormBorderStyle = FormBorderStyle.None;
    ShowInTaskbar = false;
    StartPosition = FormStartPosition.Manual;
    Size = source.Size;
    Opacity = 0.94;
    DoubleBuffered = true;
    FollowPointer();
  }

  protected override bool ShowWithoutActivation => true;

  protected override CreateParams CreateParams
  {
    get
    {
      const int WsExToolWindow = 0x00000080;
      const int WsExNoActivate = 0x08000000;
      const int WsExTransparent = 0x00000020;
      var parameters = base.CreateParams;
      parameters.ExStyle |= WsExToolWindow | WsExNoActivate | WsExTransparent;
      return parameters;
    }
  }

  internal void FollowPointer()
  {
    var cursor = Cursor.Position;
    Location = new Point(cursor.X - _pointerOffset.X, cursor.Y - _pointerOffset.Y);
  }

  protected override void OnPaint(PaintEventArgs eventArgs)
  {
    eventArgs.Graphics.SmoothingMode = SmoothingMode.AntiAlias;
    eventArgs.Graphics.InterpolationMode = InterpolationMode.HighQualityBicubic;
    var bounds = new RectangleF(1.5F, 1.5F, Math.Max(1, Width - 3F), Math.Max(1, Height - 3F));
    using var path = UiGeometry.RoundedRectangle(bounds, Math.Min(16F, Math.Max(8F, Height / 5F)));
    var state = eventArgs.Graphics.Save();
    eventArgs.Graphics.SetClip(path);
    eventArgs.Graphics.DrawImage(_snapshot, ClientRectangle);
    eventArgs.Graphics.Restore(state);
    using var border = new Pen(Color.FromArgb(91, 146, 255), 2F)
    {
      StartCap = LineCap.Round,
      EndCap = LineCap.Round,
      LineJoin = LineJoin.Round,
    };
    eventArgs.Graphics.DrawPath(border, path);
  }

  protected override void Dispose(bool disposing)
  {
    if (disposing) _snapshot.Dispose();
    base.Dispose(disposing);
  }
}

internal sealed class TabHoverPreviewWindow : Form
{
  private readonly string _title;
  private readonly string _address;
  private Image? _preview;

  internal TabHoverPreviewWindow(string title, string address, Image? preview)
  {
    _title = title;
    _address = address;
    _preview = preview;
    AutoScaleMode = AutoScaleMode.None;
    BackColor = Color.FromArgb(45, 50, 57);
    ForeColor = Color.FromArgb(244, 240, 238);
    FormBorderStyle = FormBorderStyle.None;
    ShowInTaskbar = false;
    StartPosition = FormStartPosition.Manual;
    Size = new Size(328, 224);
    DoubleBuffered = true;
  }

  protected override bool ShowWithoutActivation => true;

  protected override CreateParams CreateParams
  {
    get
    {
      const int WsExToolWindow = 0x00000080;
      const int WsExNoActivate = 0x08000000;
      const int WsExTransparent = 0x00000020;
      var parameters = base.CreateParams;
      parameters.ExStyle |= WsExToolWindow | WsExNoActivate | WsExTransparent;
      return parameters;
    }
  }

  protected override void OnPaint(PaintEventArgs eventArgs)
  {
    var graphics = eventArgs.Graphics;
    graphics.SmoothingMode = SmoothingMode.AntiAlias;
    graphics.InterpolationMode = InterpolationMode.HighQualityBicubic;
    var bounds = new RectangleF(1.5F, 1.5F, Width - 3F, Height - 3F);
    using var path = UiGeometry.RoundedRectangle(bounds, 16F);
    using var surface = new SolidBrush(Color.FromArgb(45, 50, 57));
    graphics.FillPath(surface, path);
    var state = graphics.Save();
    graphics.SetClip(path);
    var previewBounds = new Rectangle(2, 2, Width - 4, 164);
    using (var previewBackground = new SolidBrush(Color.FromArgb(25, 29, 34)))
      graphics.FillRectangle(previewBackground, previewBounds);
    if (_preview is { Width: > 0, Height: > 0 })
    {
      var scale = Math.Max((double)previewBounds.Width / _preview.Width, (double)previewBounds.Height / _preview.Height);
      var drawWidth = (int)Math.Ceiling(_preview.Width * scale);
      var drawHeight = (int)Math.Ceiling(_preview.Height * scale);
      graphics.DrawImage(_preview, new Rectangle(
          previewBounds.Left + (previewBounds.Width - drawWidth) / 2,
          previewBounds.Top,
          drawWidth,
          drawHeight));
    }
    graphics.Restore(state);
    using var titleFont = new Font("Segoe UI", 10F, FontStyle.Bold);
    TextRenderer.DrawText(graphics, _title, titleFont, new Rectangle(15, 171, Width - 30, 22), ForeColor,
        TextFormatFlags.Left | TextFormatFlags.VerticalCenter | TextFormatFlags.EndEllipsis | TextFormatFlags.NoPadding);
    using var addressFont = new Font("Segoe UI", 8.5F);
    TextRenderer.DrawText(graphics, _address, addressFont, new Rectangle(15, 196, Width - 30, 18),
        Color.FromArgb(183, 188, 197),
        TextFormatFlags.Left | TextFormatFlags.VerticalCenter | TextFormatFlags.EndEllipsis | TextFormatFlags.NoPadding);
    using var border = new Pen(Color.FromArgb(74, 82, 92), 1F)
    {
      StartCap = LineCap.Round,
      EndCap = LineCap.Round,
      LineJoin = LineJoin.Round,
    };
    graphics.DrawPath(border, path);
  }

  protected override void Dispose(bool disposing)
  {
    if (disposing)
    {
      _preview?.Dispose();
      _preview = null;
    }
    base.Dispose(disposing);
  }
}

internal sealed class PillButton : Control
{
  private Image? _image;
  private readonly System.Windows.Forms.Timer _interactionTimer = new() { Interval = 15 };
  private readonly System.Windows.Forms.Timer _longPressTimer = new() { Interval = 500 };
  private bool _longPressTriggered;

  internal event EventHandler? LongPress;
  internal event EventHandler? RightClick;

  internal int CornerRadius { get; set; } = 12;
  internal PillGlyph Glyph { get; set; }
  internal Color BorderColor { get; set; } = Color.Transparent;
  internal float BorderWidth { get; set; }
  internal Color HoverBackColor { get; set; } = Color.Empty;
  internal Color PressedBackColor { get; set; } = Color.Empty;
  internal ContentAlignment TextAlign { get; set; } = ContentAlignment.MiddleCenter;
  internal ContentAlignment ImageAlign { get; set; } = ContentAlignment.MiddleCenter;
  internal TextImageRelation TextImageRelation { get; set; } = TextImageRelation.Overlay;
  internal Image? Image
  {
    get => _image;
    set
    {
      if (ReferenceEquals(_image, value)) return;
      _image = value;
      Invalidate();
    }
  }
  private bool _hovered;
  private bool _pressed;
  private float _hoverAmount;
  private float _pressAmount;

  internal PillButton()
  {
    SetStyle(ControlStyles.UserPaint | ControlStyles.AllPaintingInWmPaint |
        ControlStyles.OptimizedDoubleBuffer | ControlStyles.ResizeRedraw |
        ControlStyles.SupportsTransparentBackColor, true);
    Cursor = Cursors.Hand;
    TabStop = false;
    Size = DefaultSize;
    _interactionTimer.Tick += (_, _) => AdvanceInteractionAnimation();
    _longPressTimer.Tick += (_, _) =>
    {
      _longPressTimer.Stop();
      if (_pressed)
      {
        _longPressTriggered = true;
        LongPress?.Invoke(this, EventArgs.Empty);
      }
    };
  }

  protected override Size DefaultSize => new(96, 34);

  public override Size GetPreferredSize(Size proposedSize)
  {
    var textSize = TextRenderer.MeasureText(
        string.IsNullOrEmpty(Text) ? " " : Text,
        Font,
        new Size(int.MaxValue, int.MaxValue),
        TextFormatFlags.SingleLine | TextFormatFlags.NoPadding);
    var width = Math.Max(36, textSize.Width + Padding.Horizontal + 20);
    var height = Height > 0 ? Height : Math.Max(32, textSize.Height + Padding.Vertical + 12);
    return new Size(width, height);
  }

  protected override void OnTextChanged(EventArgs eventArgs)
  {
    base.OnTextChanged(eventArgs);
    Invalidate();
  }

  protected override void OnMouseEnter(EventArgs eventArgs)
  {
    base.OnMouseEnter(eventArgs);
    _hovered = true;
    _interactionTimer.Start();
  }

  protected override void OnMouseLeave(EventArgs eventArgs)
  {
    base.OnMouseLeave(eventArgs);
    _longPressTimer.Stop();
    _hovered = false;
    _pressed = false;
    _interactionTimer.Start();
  }

  protected override void OnMouseDown(MouseEventArgs eventArgs)
  {
    base.OnMouseDown(eventArgs);
    if (eventArgs.Button == MouseButtons.Left)
    {
      _pressed = true;
      _longPressTriggered = false;
      _longPressTimer.Stop();
      _longPressTimer.Start();
    }
    _interactionTimer.Start();
  }

  protected override void OnMouseUp(MouseEventArgs eventArgs)
  {
    base.OnMouseUp(eventArgs);
    _longPressTimer.Stop();
    if (eventArgs.Button == MouseButtons.Right)
    {
      RightClick?.Invoke(this, EventArgs.Empty);
    }
    _pressed = false;
    _interactionTimer.Start();
  }

  protected override void OnClick(EventArgs e)
  {
    if (_longPressTriggered)
    {
      _longPressTriggered = false;
      return;
    }
    base.OnClick(e);
  }

  private void AdvanceInteractionAnimation()
  {
    _hoverAmount += ((_hovered ? 1F : 0F) - _hoverAmount) * 0.34F;
    _pressAmount += ((_pressed ? 1F : 0F) - _pressAmount) * 0.44F;
    if (Math.Abs(_hoverAmount - (_hovered ? 1F : 0F)) < 0.015F &&
        Math.Abs(_pressAmount - (_pressed ? 1F : 0F)) < 0.015F)
    {
      _hoverAmount = _hovered ? 1F : 0F;
      _pressAmount = _pressed ? 1F : 0F;
      _interactionTimer.Stop();
    }
    Invalidate();
  }

  protected override void OnPaintBackground(PaintEventArgs eventArgs)
  {
    eventArgs.Graphics.CompositingMode = System.Drawing.Drawing2D.CompositingMode.SourceCopy;
    using var brush = new SolidBrush(UiGeometry.ResolveBackground(Parent, BackColor));
    eventArgs.Graphics.FillRectangle(brush, ClientRectangle);
    eventArgs.Graphics.CompositingMode = System.Drawing.Drawing2D.CompositingMode.SourceOver;
  }

  protected override void OnPaint(PaintEventArgs eventArgs)
  {
    var graphics = eventArgs.Graphics;
    graphics.SmoothingMode = System.Drawing.Drawing2D.SmoothingMode.AntiAlias;
    graphics.PixelOffsetMode = System.Drawing.Drawing2D.PixelOffsetMode.HighQuality;
    graphics.CompositingQuality = System.Drawing.Drawing2D.CompositingQuality.HighQuality;
    var hoverColor = HoverBackColor == Color.Empty ? ControlPaint.Light(BackColor, 0.10F) : HoverBackColor;
    var pressedColor = PressedBackColor == Color.Empty ? ControlPaint.Dark(BackColor, 0.10F) : PressedBackColor;
    var fill = !Enabled
        ? ControlPaint.Dark(BackColor, 0.06F)
        : UiGeometry.Blend(UiGeometry.Blend(BackColor, hoverColor, _hoverAmount), pressedColor, _pressAmount);
    var inset = 2F + _pressAmount * 1.15F;
    var rectangle = new RectangleF(inset, inset,
        Math.Max(0, ClientSize.Width - inset * 2F),
        Math.Max(0, ClientSize.Height - inset * 2F));
    var radius = Math.Max(2F, Math.Min(CornerRadius - inset, Math.Min(rectangle.Width, rectangle.Height) / 2F));
    using var path = UiGeometry.RoundedRectangle(rectangle, radius);
    using var fillBrush = new SolidBrush(fill);
    graphics.FillPath(fillBrush, path);
    if (BorderWidth > 0 && BorderColor != Color.Transparent)
    {
      using var borderPen = new Pen(BorderColor, BorderWidth)
      {
        StartCap = System.Drawing.Drawing2D.LineCap.Round,
        EndCap = System.Drawing.Drawing2D.LineCap.Round,
        LineJoin = System.Drawing.Drawing2D.LineJoin.Round,
      };
      graphics.DrawPath(borderPen, path);
    }

    var contentColor = Enabled ? ForeColor : ControlPaint.Dark(ForeColor, 0.30F);
    if (Glyph == PillGlyph.None)
    {
      DrawContent(graphics, contentColor);
      return;
    }

    var centerX = ClientSize.Width / 2F;
    var centerY = ClientSize.Height / 2F + _pressAmount * 0.8F;
    using var pen = new Pen(contentColor, 2F)
    {
      StartCap = System.Drawing.Drawing2D.LineCap.Round,
      EndCap = System.Drawing.Drawing2D.LineCap.Round,
      LineJoin = System.Drawing.Drawing2D.LineJoin.Round,
    };
    using var brush = new SolidBrush(contentColor);
    switch (Glyph)
    {
      case PillGlyph.More:
        for (var offset = -7; offset <= 7; offset += 7)
          graphics.FillEllipse(brush, centerX + offset - 1.8F, centerY - 1.8F, 3.6F, 3.6F);
        break;
      case PillGlyph.Search:
        graphics.DrawEllipse(pen, centerX - 8, centerY - 8, 13, 13);
        graphics.DrawLine(pen, centerX + 3, centerY + 4, centerX + 9, centerY + 10);
        break;
      case PillGlyph.Add:
        graphics.DrawLine(pen, centerX - 8, centerY, centerX + 8, centerY);
        graphics.DrawLine(pen, centerX, centerY - 8, centerX, centerY + 8);
        break;
      case PillGlyph.Back:
        graphics.DrawLine(pen, centerX + 5, centerY - 8, centerX - 3, centerY);
        graphics.DrawLine(pen, centerX - 3, centerY, centerX + 5, centerY + 8);
        break;
      case PillGlyph.Forward:
        graphics.DrawLine(pen, centerX - 5, centerY - 8, centerX + 3, centerY);
        graphics.DrawLine(pen, centerX + 3, centerY, centerX - 5, centerY + 8);
        break;
      case PillGlyph.Reload:
      {
        var r = ClientSize.Width <= 30 ? 5.8F : 7.5F;
        graphics.DrawArc(pen, centerX - r, centerY - r, r * 2F, r * 2F, -55, 290);
        graphics.DrawLine(pen, centerX + r - 2F, centerY - r, centerX + r + 1F, centerY - r);
        graphics.DrawLine(pen, centerX + r + 1F, centerY - r, centerX + r + 1F, centerY - r + 3.5F);
        break;
      }
      case PillGlyph.Tabs:
        graphics.DrawRectangle(pen, centerX - 8, centerY - 6, 12, 12);
        graphics.DrawRectangle(pen, centerX - 3, centerY - 9, 12, 12);
        break;
      case PillGlyph.Sync:
        graphics.DrawArc(pen, centerX - 8, centerY - 7, 16, 13, 205, 205);
        graphics.DrawLine(pen, centerX - 8, centerY - 3, centerX - 9, centerY - 8);
        graphics.DrawLine(pen, centerX - 9, centerY - 8, centerX - 4, centerY - 7);
        graphics.DrawArc(pen, centerX - 8, centerY - 6, 16, 13, 25, 205);
        graphics.DrawLine(pen, centerX + 8, centerY + 3, centerX + 9, centerY + 8);
        graphics.DrawLine(pen, centerX + 9, centerY + 8, centerX + 4, centerY + 7);
        break;
      case PillGlyph.Music:
        // The flag makes this glyph visually right-heavy; shift its geometry left so
        // its optical center matches the geometric center of the circular button.
        graphics.DrawLine(pen, centerX + 2, centerY - 9, centerX + 2, centerY + 5);
        graphics.DrawLine(pen, centerX + 2, centerY - 9, centerX + 8, centerY - 7);
        graphics.FillEllipse(brush, centerX - 5, centerY + 2, 8, 6);
        break;
      case PillGlyph.Capture:
        using (var cameraPath = UiGeometry.RoundedRectangle(new RectangleF(centerX - 10, centerY - 7, 20, 15), 3F))
          graphics.DrawPath(pen, cameraPath);
        graphics.DrawEllipse(pen, centerX - 4, centerY - 4, 8, 8);
        graphics.DrawLine(pen, centerX - 5, centerY - 9, centerX + 2, centerY - 9);
        break;
      case PillGlyph.Go:
        graphics.DrawLine(pen, centerX - 4, centerY - 6, centerX + 3, centerY);
        graphics.DrawLine(pen, centerX + 3, centerY, centerX - 4, centerY + 6);
        break;
      case PillGlyph.Watermark:
        DrawSparkle(graphics, brush, centerX - 2, centerY, 8F, 3.5F);
        DrawSparkle(graphics, brush, centerX + 5, centerY - 6, 4.5F, 2F);
        break;
      case PillGlyph.Extension:
        using (var puzzlePath = new GraphicsPath())
        {
          float x = centerX - 6, y = centerY - 6, s = 12;
          puzzlePath.AddLine(x, y + 3, x + 4, y + 3);
          puzzlePath.AddArc(x + 4, y - 1, 4, 4, 180, -180);
          puzzlePath.AddLine(x + 8, y + 3, x + s, y + 3);
          puzzlePath.AddLine(x + s, y + 3, x + s, y + 5);
          puzzlePath.AddArc(x + s - 1, y + 5, 4, 4, -90, 180);
          puzzlePath.AddLine(x + s, y + 9, x + s, y + s);
          puzzlePath.AddLine(x + s, y + s, x, y + s);
          puzzlePath.AddLine(x, y + s, x, y + 3);
          puzzlePath.CloseFigure();
          graphics.DrawPath(pen, puzzlePath);
        }
        break;
      case PillGlyph.Bookmarks:
        using (var ribbonPath = new GraphicsPath())
        {
          float x = centerX - 5, y = centerY - 7, w = 10, h = 14;
          ribbonPath.AddLine(x, y, x + w, y);
          ribbonPath.AddLine(x + w, y, x + w, y + h);
          ribbonPath.AddLine(x + w, y + h, centerX, y + h - 4);
          ribbonPath.AddLine(centerX, y + h - 4, x, y + h);
          ribbonPath.CloseFigure();
          graphics.DrawPath(pen, ribbonPath);
        }
        break;
      case PillGlyph.Bookshelf:
        graphics.DrawRectangle(pen, centerX - 7, centerY - 7, 4, 14);
        graphics.DrawRectangle(pen, centerX - 2, centerY - 7, 4, 14);
        graphics.DrawRectangle(pen, centerX + 3, centerY - 7, 4, 14);
        graphics.DrawLine(pen, centerX - 7, centerY + 3, centerX + 7, centerY + 3);
        break;
      case PillGlyph.Downloads:
        graphics.DrawLine(pen, centerX, centerY - 7, centerX, centerY + 3);
        graphics.DrawLine(pen, centerX - 4, centerY - 1, centerX, centerY + 3);
        graphics.DrawLine(pen, centerX + 4, centerY - 1, centerX, centerY + 3);
        graphics.DrawLine(pen, centerX - 7, centerY + 3, centerX - 7, centerY + 7);
        graphics.DrawLine(pen, centerX - 7, centerY + 7, centerX + 7, centerY + 7);
        graphics.DrawLine(pen, centerX + 7, centerY + 7, centerX + 7, centerY + 3);
        break;
      case PillGlyph.Print:
        graphics.DrawRectangle(pen, centerX - 5, centerY - 8, 10, 4);
        using (var body = UiGeometry.RoundedRectangle(new RectangleF(centerX - 8, centerY - 4, 16, 9), 2F))
          graphics.DrawPath(pen, body);
        graphics.DrawLine(pen, centerX - 5, centerY + 5, centerX + 5, centerY + 5);
        graphics.DrawLine(pen, centerX - 5, centerY + 7, centerX + 5, centerY + 7);
        break;
      case PillGlyph.Settings:
        graphics.DrawEllipse(pen, centerX - 4, centerY - 4, 8, 8);
        for (int i = 0; i < 6; i++)
        {
          double angle = i * Math.PI / 3.0;
          float cos = (float)Math.Cos(angle);
          float sin = (float)Math.Sin(angle);
          graphics.DrawLine(pen, centerX + cos * 4F, centerY + sin * 4F, centerX + cos * 7.5F, centerY + sin * 7.5F);
        }
        break;
      case PillGlyph.Lock:
        using (var lockBody = UiGeometry.RoundedRectangle(new RectangleF(centerX - 5F, centerY - 1F, 10F, 7.5F), 2F))
          graphics.FillPath(brush, lockBody);
        using (var shacklePen = new Pen(contentColor, 1.5F))
          graphics.DrawArc(shacklePen, centerX - 3.2F, centerY - 6F, 6.4F, 6.5F, 180, 180);
        break;
      case PillGlyph.Star:
      case PillGlyph.StarFilled:
        DrawStar(graphics, pen, brush, centerX, centerY, 6.8F, 3.0F, Glyph == PillGlyph.StarFilled);
        break;
      case PillGlyph.Shield:
        using (var shieldPath = new GraphicsPath())
        {
          shieldPath.AddLines(new PointF[]
          {
            new(centerX, centerY - 7.5F),
            new(centerX + 6.5F, centerY - 4.8F),
            new(centerX + 5.5F, centerY + 2.2F),
            new(centerX, centerY + 7.5F),
            new(centerX - 5.5F, centerY + 2.2F),
            new(centerX - 6.5F, centerY - 4.8F),
          });
          shieldPath.CloseFigure();
          graphics.DrawPath(pen, shieldPath);
          using var leftHalf = new GraphicsPath();
          leftHalf.AddLines(new PointF[]
          {
            new(centerX, centerY - 7.5F),
            new(centerX, centerY + 7.5F),
            new(centerX - 5.5F, centerY + 2.2F),
            new(centerX - 6.5F, centerY - 4.8F),
          });
          leftHalf.CloseFigure();
          graphics.FillPath(brush, leftHalf);
        }
        break;
    }
  }

  private static void DrawStar(Graphics g, Pen pen, Brush brush, float cx, float cy, float outerR, float innerR, bool filled)
  {
    var pts = new PointF[10];
    for (int i = 0; i < 10; i++)
    {
      double angle = -Math.PI / 2.0 + i * Math.PI / 5.0;
      float r = (i % 2 == 0) ? outerR : innerR;
      pts[i] = new PointF(cx + (float)Math.Cos(angle) * r, cy + (float)Math.Sin(angle) * r);
    }
    if (filled) g.FillPolygon(brush, pts);
    else g.DrawPolygon(pen, pts);
  }

  private static void DrawSparkle(Graphics g, Brush brush, float cx, float cy, float r1, float r2)
  {
    var pts = new PointF[]
    {
      new(cx, cy - r1),
      new(cx + r2 * 0.35F, cy - r2 * 0.35F),
      new(cx + r1, cy),
      new(cx + r2 * 0.35F, cy + r2 * 0.35F),
      new(cx, cy + r1),
      new(cx - r2 * 0.35F, cy + r2 * 0.35F),
      new(cx - r1, cy),
      new(cx - r2 * 0.35F, cy - r2 * 0.35F)
    };
    g.FillPolygon(brush, pts);
  }

  private void DrawContent(Graphics graphics, Color contentColor)
  {
    var content = new Rectangle(
        Padding.Left,
        Padding.Top + (int)Math.Round(_pressAmount),
        Math.Max(0, ClientSize.Width - Padding.Horizontal),
        Math.Max(0, ClientSize.Height - Padding.Vertical));
    var text = Text ?? string.Empty;
    if (Image is not null && text.Trim().Length > 0)
    {
      var displayText = text.Trim();
      var measured = TextRenderer.MeasureText(displayText, Font, Size.Empty, TextFormatFlags.NoPadding | TextFormatFlags.SingleLine);
      var imageSize = Math.Min(Math.Min(Image.Width, Image.Height), Math.Max(16, content.Height - 8));
      var totalWidth = imageSize + 10 + measured.Width;
      var left = content.Left + Math.Max(0, (content.Width - totalWidth) / 2);
      graphics.DrawImage(Image, new Rectangle(left, content.Top + (content.Height - imageSize) / 2, imageSize, imageSize));
      var textRectangle = new Rectangle(left + imageSize + 10, content.Top, Math.Max(0, content.Right - left - imageSize - 10), content.Height);
      TextRenderer.DrawText(graphics, displayText, Font, textRectangle, contentColor,
          TextFormatFlags.Left | TextFormatFlags.VerticalCenter | TextFormatFlags.EndEllipsis | TextFormatFlags.NoPadding);
      return;
    }
    if (Image is not null)
    {
      var imageSize = Math.Min(Math.Min(Image.Width, Image.Height), Math.Max(16, content.Height - 8));
      graphics.DrawImage(Image, new Rectangle(
          content.Left + (content.Width - imageSize) / 2,
          content.Top + (content.Height - imageSize) / 2,
          imageSize,
          imageSize));
      return;
    }

    var flags = TextFormatFlags.VerticalCenter | TextFormatFlags.EndEllipsis | TextFormatFlags.NoPadding;
    flags |= TextAlign is ContentAlignment.MiddleLeft or ContentAlignment.TopLeft or ContentAlignment.BottomLeft
        ? TextFormatFlags.Left
        : TextAlign is ContentAlignment.MiddleRight or ContentAlignment.TopRight or ContentAlignment.BottomRight
            ? TextFormatFlags.Right
            : TextFormatFlags.HorizontalCenter;
    if (text.Contains('\n')) flags = (flags & ~TextFormatFlags.SingleLine) | TextFormatFlags.WordBreak;
    TextRenderer.DrawText(graphics, text, Font, content, contentColor, flags);
  }

  protected override void Dispose(bool disposing)
  {
    if (disposing)
    {
      _image?.Dispose();
      _image = null;
      _interactionTimer.Dispose();
      _longPressTimer.Dispose();
    }
    base.Dispose(disposing);
  }
}

internal sealed class MenuActionControl : Control
{
  private readonly System.Windows.Forms.Timer _interactionTimer = new() { Interval = 15 };
  private bool _hovered;
  private bool _pressed;
  private float _hoverAmount;
  private float _pressAmount;

  internal string Glyph { get; set; } = string.Empty;
  internal string Caption { get; set; } = string.Empty;
  internal Color HoverBackColor { get; set; } = Color.FromArgb(67, 77, 88);

  internal MenuActionControl()
  {
    SetStyle(ControlStyles.UserPaint | ControlStyles.AllPaintingInWmPaint |
        ControlStyles.OptimizedDoubleBuffer | ControlStyles.ResizeRedraw, true);
    Cursor = Cursors.Hand;
    TabStop = false;
    _interactionTimer.Tick += (_, _) => AdvanceInteraction();
  }

  protected override void OnMouseEnter(EventArgs eventArgs)
  {
    base.OnMouseEnter(eventArgs);
    _hovered = true;
    _interactionTimer.Start();
  }

  protected override void OnMouseLeave(EventArgs eventArgs)
  {
    base.OnMouseLeave(eventArgs);
    _hovered = false;
    _pressed = false;
    _interactionTimer.Start();
  }

  protected override void OnMouseDown(MouseEventArgs eventArgs)
  {
    base.OnMouseDown(eventArgs);
    if (eventArgs.Button == MouseButtons.Left) _pressed = true;
    _interactionTimer.Start();
  }

  protected override void OnMouseUp(MouseEventArgs eventArgs)
  {
    base.OnMouseUp(eventArgs);
    _pressed = false;
    _interactionTimer.Start();
  }

  private void AdvanceInteraction()
  {
    _hoverAmount += ((_hovered ? 1F : 0F) - _hoverAmount) * 0.34F;
    _pressAmount += ((_pressed ? 1F : 0F) - _pressAmount) * 0.44F;
    if (Math.Abs(_hoverAmount - (_hovered ? 1F : 0F)) < 0.015F &&
        Math.Abs(_pressAmount - (_pressed ? 1F : 0F)) < 0.015F)
    {
      _hoverAmount = _hovered ? 1F : 0F;
      _pressAmount = _pressed ? 1F : 0F;
      _interactionTimer.Stop();
    }
    Invalidate();
  }

  protected override void OnPaintBackground(PaintEventArgs eventArgs)
  {
    using var brush = new SolidBrush(UiGeometry.ResolveBackground(Parent, BackColor));
    eventArgs.Graphics.FillRectangle(brush, ClientRectangle);
  }

  protected override void OnPaint(PaintEventArgs eventArgs)
  {
    var graphics = eventArgs.Graphics;
    graphics.SmoothingMode = System.Drawing.Drawing2D.SmoothingMode.AntiAlias;
    graphics.PixelOffsetMode = System.Drawing.Drawing2D.PixelOffsetMode.HighQuality;
    if (_hoverAmount > 0.01F)
    {
      using var hoverPath = UiGeometry.RoundedRectangle(
          new RectangleF(1.5F, 1.5F, Math.Max(0, ClientSize.Width - 3F), Math.Max(0, ClientSize.Height - 3F)),
          12F);
      using var hoverBrush = new SolidBrush(UiGeometry.Blend(BackColor, HoverBackColor, _hoverAmount));
      graphics.FillPath(hoverBrush, hoverPath);
    }

    using var glyphFont = new Font("Segoe UI Symbol", 16F, FontStyle.Bold, GraphicsUnit.Pixel);
    var pressOffset = (int)Math.Round(_pressAmount);
    TextRenderer.DrawText(graphics, Glyph, glyphFont, new Rectangle(0, 9 + pressOffset, ClientSize.Width, 25), ForeColor,
        TextFormatFlags.HorizontalCenter | TextFormatFlags.VerticalCenter | TextFormatFlags.NoPadding);
    using var captionFont = new Font("Segoe UI", 10F, FontStyle.Bold, GraphicsUnit.Pixel);
    TextRenderer.DrawText(graphics, Caption, captionFont, new Rectangle(0, 37 + pressOffset, ClientSize.Width, 25), ForeColor,
        TextFormatFlags.HorizontalCenter | TextFormatFlags.VerticalCenter | TextFormatFlags.NoPadding);
  }

  protected override void Dispose(bool disposing)
  {
    if (disposing) _interactionTimer.Dispose();
    base.Dispose(disposing);
  }
}

internal sealed class SettingsMenuRowControl : Control
{
  private readonly System.Windows.Forms.Timer _interactionTimer = new() { Interval = 15 };
  private bool _hovered;
  private bool _pressed;
  private float _hoverAmount;
  private float _pressAmount;

  internal string Glyph { get; set; } = string.Empty;
  internal string Caption { get; set; } = string.Empty;
  internal string Shortcut { get; set; } = string.Empty;
  internal Color HoverBackColor { get; set; } = Color.FromArgb(57, 63, 72);
  internal bool ShowDivider { get; set; }

  internal SettingsMenuRowControl()
  {
    SetStyle(ControlStyles.UserPaint | ControlStyles.AllPaintingInWmPaint |
        ControlStyles.OptimizedDoubleBuffer | ControlStyles.ResizeRedraw, true);
    Cursor = Cursors.Hand;
    TabStop = false;
    Font = new Font("Segoe UI", 10.5F);
    _interactionTimer.Tick += (_, _) => AdvanceInteraction();
  }

  protected override void OnMouseEnter(EventArgs eventArgs)
  {
    base.OnMouseEnter(eventArgs);
    _hovered = true;
    _interactionTimer.Start();
  }

  protected override void OnMouseLeave(EventArgs eventArgs)
  {
    base.OnMouseLeave(eventArgs);
    _hovered = false;
    _pressed = false;
    _interactionTimer.Start();
  }

  protected override void OnMouseDown(MouseEventArgs eventArgs)
  {
    base.OnMouseDown(eventArgs);
    if (eventArgs.Button == MouseButtons.Left) _pressed = true;
    _interactionTimer.Start();
  }

  protected override void OnMouseUp(MouseEventArgs eventArgs)
  {
    base.OnMouseUp(eventArgs);
    _pressed = false;
    _interactionTimer.Start();
  }

  private void AdvanceInteraction()
  {
    _hoverAmount += ((_hovered ? 1F : 0F) - _hoverAmount) * 0.34F;
    _pressAmount += ((_pressed ? 1F : 0F) - _pressAmount) * 0.44F;
    if (Math.Abs(_hoverAmount - (_hovered ? 1F : 0F)) < 0.015F &&
        Math.Abs(_pressAmount - (_pressed ? 1F : 0F)) < 0.015F)
    {
      _hoverAmount = _hovered ? 1F : 0F;
      _pressAmount = _pressed ? 1F : 0F;
      _interactionTimer.Stop();
    }
    Invalidate();
  }

  protected override void OnPaintBackground(PaintEventArgs eventArgs)
  {
    using var brush = new SolidBrush(UiGeometry.ResolveBackground(Parent, BackColor));
    eventArgs.Graphics.FillRectangle(brush, ClientRectangle);
  }

  protected override void OnPaint(PaintEventArgs eventArgs)
  {
    var graphics = eventArgs.Graphics;
    graphics.SmoothingMode = System.Drawing.Drawing2D.SmoothingMode.AntiAlias;
    graphics.PixelOffsetMode = System.Drawing.Drawing2D.PixelOffsetMode.HighQuality;
    if (_hoverAmount > 0.01F)
    {
      using var path = UiGeometry.RoundedRectangle(
          new RectangleF(2F, 2F, Math.Max(0, ClientSize.Width - 4F), Math.Max(0, ClientSize.Height - 4F)),
          12F);
      using var hover = new SolidBrush(UiGeometry.Blend(BackColor, HoverBackColor, _hoverAmount));
      graphics.FillPath(hover, path);
    }

    var pressOffset = (int)Math.Round(_pressAmount);
    using var glyphFont = new Font("Segoe UI Symbol", 18F, FontStyle.Regular, GraphicsUnit.Pixel);
    TextRenderer.DrawText(graphics, Glyph, glyphFont, new Rectangle(5, pressOffset, 32, ClientSize.Height), ForeColor,
        TextFormatFlags.HorizontalCenter | TextFormatFlags.VerticalCenter | TextFormatFlags.NoPadding);
    TextRenderer.DrawText(graphics, Caption, Font, new Rectangle(40, pressOffset, Math.Max(0, ClientSize.Width - 113), ClientSize.Height), ForeColor,
        TextFormatFlags.Left | TextFormatFlags.VerticalCenter | TextFormatFlags.SingleLine |
        TextFormatFlags.EndEllipsis | TextFormatFlags.NoPadding);
    if (!string.IsNullOrWhiteSpace(Shortcut))
      TextRenderer.DrawText(graphics, Shortcut, Font, new Rectangle(ClientSize.Width - 92, 0, 78, ClientSize.Height), ForeColor,
          TextFormatFlags.Right | TextFormatFlags.VerticalCenter | TextFormatFlags.SingleLine | TextFormatFlags.NoPadding);
    if (ShowDivider)
    {
      using var divider = new Pen(Color.FromArgb(71, 78, 88));
      graphics.DrawLine(divider, 40, ClientSize.Height - 1, ClientSize.Width - 12, ClientSize.Height - 1);
    }
  }

  protected override void Dispose(bool disposing)
  {
    if (disposing) _interactionTimer.Dispose();
    base.Dispose(disposing);
  }
}

internal sealed class CachedPopupForm : Form, IMessageFilter
{
  private readonly Control _content;
  private readonly PictureBox _transitionSurface = new();
  private readonly int _cornerRadius;
  private Bitmap? _snapshot;
  private Form? _owner;
  private Control? _anchor;
  private bool _messageFilterInstalled;
  internal bool HasSnapshot => _snapshot is { Width: > 0, Height: > 0 };

  internal CachedPopupForm(Control content, Size size, int cornerRadius)
  {
    _content = content;
    _cornerRadius = cornerRadius;
    AutoScaleMode = AutoScaleMode.None;
    BackColor = Color.FromArgb(57, 63, 72);
    FormBorderStyle = FormBorderStyle.None;
    ShowInTaskbar = false;
    StartPosition = FormStartPosition.Manual;
    Size = size;
    Padding = Padding.Empty;
    DoubleBuffered = true;
    SetStyle(ControlStyles.AllPaintingInWmPaint | ControlStyles.OptimizedDoubleBuffer |
        ControlStyles.ResizeRedraw, true);
    _content.Dock = DockStyle.Fill;
    Controls.Add(_content);
    _transitionSurface.Dock = DockStyle.Fill;
    _transitionSurface.BackColor = BackColor;
    _transitionSurface.SizeMode = PictureBoxSizeMode.Normal;
    _transitionSurface.Margin = Padding.Empty;
    _transitionSurface.TabStop = false;
    _transitionSurface.Visible = false;
    Controls.Add(_transitionSurface);
    _transitionSurface.BringToFront();
    SizeChanged += (_, _) => UpdateRegion();
    CreateControl();
    CreateChildHandles(_content);
    _content.PerformLayout();
    RefreshSnapshot();
    UpdateRegion();
  }

  protected override CreateParams CreateParams
  {
    get
    {
      const int WsExToolWindow = 0x00000080;
      const int WsExNoActivate = 0x08000000;
      const int WsExComposited = 0x02000000;
      var parameters = base.CreateParams;
      parameters.ExStyle |= WsExToolWindow | WsExNoActivate | WsExComposited;
      return parameters;
    }
  }

  protected override bool ShowWithoutActivation => true;

  internal void RefreshSnapshot()
  {
    if (IsDisposed || ClientSize.Width < 2 || ClientSize.Height < 2) return;
    _content.Visible = true;
    _content.PerformLayout();
    var next = new Bitmap(ClientSize.Width, ClientSize.Height);
    _content.DrawToBitmap(next, new Rectangle(Point.Empty, next.Size));
    var previous = _snapshot;
    _snapshot = next;
    BackgroundImage = _snapshot;
    BackgroundImageLayout = ImageLayout.None;
    _transitionSurface.Image = _snapshot;
    previous?.Dispose();
  }

  internal void ShowAt(Form owner, Control anchor, Point requestedLocation)
  {
    if (!ReferenceEquals(_owner, owner))
    {
      if (_owner is not null) _owner.Deactivate -= OwnerDeactivated;
      _owner = owner;
      _owner.Deactivate += OwnerDeactivated;
    }
    _anchor = anchor;
    var workingArea = Screen.FromControl(owner).WorkingArea;
    Location = new Point(
        Math.Clamp(requestedLocation.X, workingArea.Left + 6, Math.Max(workingArea.Left + 6, workingArea.Right - Width - 6)),
        Math.Clamp(requestedLocation.Y, workingArea.Top + 6, Math.Max(workingArea.Top + 6, workingArea.Bottom - Height - 6)));
    _content.Visible = true;
    _content.BringToFront();
    if (!Visible) Show(owner);
    _content.Invalidate(true);
  }

  protected override void Dispose(bool disposing)
  {
    if (disposing)
    {
      if (_owner is not null) _owner.Deactivate -= OwnerDeactivated;
      RemoveMessageFilter();
      BackgroundImage = null;
      _transitionSurface.Image = null;
      _snapshot?.Dispose();
      _snapshot = null;
    }
    base.Dispose(disposing);
  }

  protected override void OnVisibleChanged(EventArgs eventArgs)
  {
    base.OnVisibleChanged(eventArgs);
    if (Visible && !_messageFilterInstalled)
    {
      Application.AddMessageFilter(this);
      _messageFilterInstalled = true;
    }
    else if (!Visible)
    {
      RemoveMessageFilter();
    }
  }

  protected override void WndProc(ref Message message)
  {
    const int WmMouseActivate = 0x0021;
    const int MaNoActivate = 3;
    if (message.Msg == WmMouseActivate)
    {
      message.Result = (IntPtr)MaNoActivate;
      return;
    }
    base.WndProc(ref message);
  }

  public bool PreFilterMessage(ref Message message)
  {
    if (!Visible || message.Msg is not (0x0201 or 0x0204 or 0x0207 or 0x020B or 0x00A1 or 0x00A4))
      return false;
    var cursor = Cursor.Position;
    if (Bounds.Contains(cursor)) return false;
    if (_anchor is { IsDisposed: false } && _anchor.RectangleToScreen(_anchor.ClientRectangle).Contains(cursor))
      return false;
    Hide();
    return false;
  }

  private void OwnerDeactivated(object? sender, EventArgs eventArgs) => Hide();

  private void RemoveMessageFilter()
  {
    if (!_messageFilterInstalled) return;
    Application.RemoveMessageFilter(this);
    _messageFilterInstalled = false;
  }

  private void UpdateRegion()
  {
    var previous = Region;
    if (ClientSize.Width > 2 && ClientSize.Height > 2)
    {
      using var path = UiGeometry.RoundedRectangle(new Rectangle(0, 0, Width, Height), _cornerRadius);
      Region = new Region(path);
    }
    previous?.Dispose();
  }

  private static void CreateChildHandles(Control root)
  {
    root.CreateControl();
    foreach (Control child in root.Controls) CreateChildHandles(child);
  }
}

internal sealed class BorderlessDropDownRenderer : ToolStripProfessionalRenderer
{
  internal BorderlessDropDownRenderer() : base(new AndroidMenuColorTable())
  {
    RoundedEdges = true;
  }

  protected override void OnRenderToolStripBorder(ToolStripRenderEventArgs eventArgs)
  {
    // The hosted settings card owns the anti-aliased border and clipping region.
  }
}

internal enum PillGlyph
{
  None,
  More,
  Search,
  Add,
  Back,
  Forward,
  Reload,
  Tabs,
  Sync,
  Music,
  Capture,
  Go,
  Watermark,
  Extension,
  Bookmarks,
  Bookshelf,
  Downloads,
  Print,
  Settings,
  Lock,
  Star,
  StarFilled,
  Shield,
}

internal sealed class AndroidMenuRenderer : ToolStripProfessionalRenderer
{
  internal AndroidMenuRenderer() : base(new AndroidMenuColorTable())
  {
    RoundedEdges = true;
  }

  protected override void OnRenderToolStripBorder(ToolStripRenderEventArgs eventArgs)
  {
    using var pen = new Pen(Color.FromArgb(64, 64, 72));
    var rectangle = Rectangle.Inflate(eventArgs.AffectedBounds, -1, -1);
    eventArgs.Graphics.DrawRectangle(pen, rectangle);
  }

  protected override void OnRenderSeparator(ToolStripSeparatorRenderEventArgs eventArgs)
  {
    using var pen = new Pen(Color.FromArgb(58, 58, 64));
    eventArgs.Graphics.DrawLine(pen, 12, eventArgs.Item.Height / 2, eventArgs.Item.Width - 12, eventArgs.Item.Height / 2);
  }

  protected override void OnRenderItemImage(ToolStripItemImageRenderEventArgs eventArgs)
  {
    if (eventArgs.Image is null) return;
    eventArgs.Graphics.InterpolationMode = System.Drawing.Drawing2D.InterpolationMode.HighQualityBicubic;
    eventArgs.Graphics.PixelOffsetMode = System.Drawing.Drawing2D.PixelOffsetMode.HighQuality;
    const int size = 20;
    var rectangle = new Rectangle(12, Math.Max(0, (eventArgs.Item.Height - size) / 2), size, size);
    eventArgs.Graphics.DrawImage(eventArgs.Image, rectangle);
  }

  protected override void OnRenderItemText(ToolStripItemTextRenderEventArgs eventArgs)
  {
    var hasImageMargin = eventArgs.ToolStrip is ToolStripDropDownMenu { ShowImageMargin: true };
    var left = hasImageMargin ? 42 : 14;
    var rectangle = new Rectangle(left, 0, Math.Max(0, eventArgs.Item.Width - left - 14), eventArgs.Item.Height);
    TextRenderer.DrawText(eventArgs.Graphics, eventArgs.Text, eventArgs.TextFont, rectangle, eventArgs.TextColor,
        TextFormatFlags.Left | TextFormatFlags.VerticalCenter | TextFormatFlags.SingleLine |
        TextFormatFlags.EndEllipsis | TextFormatFlags.NoPadding);
  }
}

internal sealed class AndroidMenuColorTable : ProfessionalColorTable
{
  private static readonly Color Menu = Color.FromArgb(38, 38, 42);
  private static readonly Color Selected = Color.FromArgb(10, 132, 255);
  public override Color ToolStripDropDownBackground => Menu;
  public override Color ImageMarginGradientBegin => Menu;
  public override Color ImageMarginGradientMiddle => Menu;
  public override Color ImageMarginGradientEnd => Menu;
  public override Color MenuBorder => Color.FromArgb(64, 64, 72);
  public override Color MenuItemBorder => Color.Transparent;
  public override Color MenuItemSelected => Selected;
  public override Color MenuItemSelectedGradientBegin => Selected;
  public override Color MenuItemSelectedGradientEnd => Selected;
  public override Color MenuItemPressedGradientBegin => Selected;
  public override Color MenuItemPressedGradientEnd => Selected;
  public override Color SeparatorDark => Color.FromArgb(58, 58, 64);
  public override Color SeparatorLight => Color.FromArgb(58, 58, 64);
}

internal sealed class OverviewTabCardControl : Control
{
  private readonly System.Windows.Forms.Timer _interactionTimer = new() { Interval = 15 };
  private bool _hoveringClose;
  private bool _suppressClick;
  private bool _dragging;
  private bool _pressed;
  private float _pressAmount;
  private Point _dragOrigin;
  private Image? _preview;

  internal string TabId { get; set; } = string.Empty;
  internal Image? Preview
  {
    get => _preview;
    set
    {
      if (ReferenceEquals(_preview, value)) return;
      _preview?.Dispose();
      _preview = value;
      Invalidate();
    }
  }
  internal string TitleText { get; set; } = string.Empty;
  internal string AddressText { get; set; } = string.Empty;
  internal string EmptyText { get; set; } = string.Empty;
  internal int CornerRadius { get; set; } = 16;
  internal float BorderWidth { get; set; } = 1F;
  internal Color BorderColor { get; set; } = Color.FromArgb(74, 82, 92);
  internal Color PreviewBackColor { get; set; } = Color.FromArgb(25, 29, 34);
  internal Color FooterBackColor { get; set; } = Color.FromArgb(45, 50, 57);
  internal Color TextColor { get; set; } = Color.White;
  internal Color MutedTextColor { get; set; } = Color.Silver;
  internal Color CloseBackColor { get; set; } = Color.FromArgb(220, 49, 56, 64);
  internal event EventHandler? ActivateRequested;
  internal event EventHandler? CloseRequested;
  internal event EventHandler<TabReorderEventArgs>? ReorderRequested;
  internal event EventHandler<TabReorderEventArgs>? ReorderPreviewRequested;
  internal event EventHandler<TabDragCompletedEventArgs>? DragCompleted;

  internal OverviewTabCardControl()
  {
    SetStyle(ControlStyles.UserPaint | ControlStyles.AllPaintingInWmPaint |
        ControlStyles.OptimizedDoubleBuffer | ControlStyles.ResizeRedraw |
        ControlStyles.SupportsTransparentBackColor, true);
    TabStop = false;
    AccessibleRole = AccessibleRole.PageTab;
    AllowDrop = true;
    _interactionTimer.Tick += (_, _) =>
    {
      _pressAmount += ((_pressed ? 1F : 0F) - _pressAmount) * 0.44F;
      if (Math.Abs(_pressAmount - (_pressed ? 1F : 0F)) < 0.015F)
      {
        _pressAmount = _pressed ? 1F : 0F;
        _interactionTimer.Stop();
      }
      Invalidate();
    };
  }

  protected override void Dispose(bool disposing)
  {
    if (disposing)
    {
      _interactionTimer.Dispose();
      Preview = null;
    }
    base.Dispose(disposing);
  }

  private Rectangle CloseBounds => new(Math.Max(8, ClientSize.Width - 46), 10, 36, 36);

  protected override void OnMouseDown(MouseEventArgs eventArgs)
  {
    base.OnMouseDown(eventArgs);
    if (eventArgs.Button == MouseButtons.Left)
    {
      _pressed = true;
      _interactionTimer.Start();
    }
    if (eventArgs.Button == MouseButtons.Left && !CloseBounds.Contains(eventArgs.Location))
      _dragOrigin = eventArgs.Location;
  }

  protected override void OnMouseUp(MouseEventArgs eventArgs)
  {
    base.OnMouseUp(eventArgs);
    _pressed = false;
    _interactionTimer.Start();
  }

  protected override void OnMouseMove(MouseEventArgs eventArgs)
  {
    base.OnMouseMove(eventArgs);
    var hovering = CloseBounds.Contains(eventArgs.Location);
    if (_hoveringClose != hovering)
    {
      _hoveringClose = hovering;
      Cursor = Cursors.Hand;
      Invalidate(CloseBounds);
    }
    if (eventArgs.Button == MouseButtons.Left && !_hoveringClose && !_dragging && !string.IsNullOrWhiteSpace(TabId))
    {
      var dragBounds = new Rectangle(
          _dragOrigin.X - SystemInformation.DragSize.Width / 2,
          _dragOrigin.Y - SystemInformation.DragSize.Height / 2,
          SystemInformation.DragSize.Width,
          SystemInformation.DragSize.Height);
      if (!dragBounds.Contains(eventArgs.Location))
      {
        _suppressClick = true;
        _dragging = true;
        using var dragPreview = new DragPreviewWindow(this, _dragOrigin);
        using var followPointer = new System.Windows.Forms.Timer { Interval = 16 };
        followPointer.Tick += (_, _) => dragPreview.FollowPointer();
        try
        {
          dragPreview.Show(FindForm());
          followPointer.Start();
          var result = DoDragDrop(TabId, DragDropEffects.Move);
          DragCompleted?.Invoke(this, new TabDragCompletedEventArgs(result == DragDropEffects.Move));
        }
        finally
        {
          followPointer.Stop();
          dragPreview.Close();
          _dragging = false;
        }
      }
    }
  }

  protected override void OnMouseLeave(EventArgs eventArgs)
  {
    base.OnMouseLeave(eventArgs);
    _pressed = false;
    _interactionTimer.Start();
    if (!_hoveringClose) return;
    _hoveringClose = false;
    Invalidate(CloseBounds);
  }

  protected override void OnMouseClick(MouseEventArgs eventArgs)
  {
    base.OnMouseClick(eventArgs);
    if (eventArgs.Button != MouseButtons.Left) return;
    if (_suppressClick)
    {
      _suppressClick = false;
      return;
    }
    if (CloseBounds.Contains(eventArgs.Location)) CloseRequested?.Invoke(this, EventArgs.Empty);
    else ActivateRequested?.Invoke(this, EventArgs.Empty);
  }

  protected override void OnDragEnter(DragEventArgs eventArgs)
  {
    base.OnDragEnter(eventArgs);
    eventArgs.Effect = eventArgs.Data?.GetDataPresent(DataFormats.Text) == true
        ? DragDropEffects.Move
        : DragDropEffects.None;
  }

  protected override void OnDragOver(DragEventArgs eventArgs)
  {
    base.OnDragOver(eventArgs);
    eventArgs.Effect = eventArgs.Data?.GetDataPresent(DataFormats.Text) == true
        ? DragDropEffects.Move
        : DragDropEffects.None;
    var sourceTabId = eventArgs.Data?.GetData(DataFormats.Text) as string;
    if (eventArgs.Effect != DragDropEffects.Move || string.IsNullOrWhiteSpace(sourceTabId) || sourceTabId == TabId) return;
    var localPoint = PointToClient(new Point(eventArgs.X, eventArgs.Y));
    ReorderPreviewRequested?.Invoke(this,
        new TabReorderEventArgs(sourceTabId, TabId, localPoint.X >= ClientSize.Width / 2));
  }

  protected override void OnDragDrop(DragEventArgs eventArgs)
  {
    base.OnDragDrop(eventArgs);
    var sourceTabId = eventArgs.Data?.GetData(DataFormats.Text) as string;
    if (string.IsNullOrWhiteSpace(sourceTabId) || sourceTabId == TabId) return;
    var localPoint = PointToClient(new Point(eventArgs.X, eventArgs.Y));
    ReorderRequested?.Invoke(this,
        new TabReorderEventArgs(sourceTabId, TabId, localPoint.X >= ClientSize.Width / 2));
  }

  protected override void OnPaintBackground(PaintEventArgs eventArgs)
  {
    using var background = new SolidBrush(UiGeometry.ResolveBackground(Parent, BackColor));
    eventArgs.Graphics.FillRectangle(background, ClientRectangle);
  }

  protected override void OnPaint(PaintEventArgs eventArgs)
  {
    var graphics = eventArgs.Graphics;
    graphics.SmoothingMode = System.Drawing.Drawing2D.SmoothingMode.AntiAlias;
    graphics.PixelOffsetMode = System.Drawing.Drawing2D.PixelOffsetMode.HighQuality;
    graphics.CompositingQuality = System.Drawing.Drawing2D.CompositingQuality.HighQuality;
    graphics.InterpolationMode = System.Drawing.Drawing2D.InterpolationMode.HighQualityBicubic;

    var inset = Math.Max(2F, BorderWidth / 2F + 1F) + _pressAmount;
    var bounds = new RectangleF(inset, inset,
        Math.Max(0, ClientSize.Width - inset * 2F),
        Math.Max(0, ClientSize.Height - inset * 2F));
    using var cardPath = UiGeometry.RoundedRectangle(bounds, Math.Max(2F, CornerRadius - inset));
    using var cardBrush = new SolidBrush(FooterBackColor);
    graphics.FillPath(cardBrush, cardPath);

    var footerHeight = Math.Min(66, Math.Max(54, ClientSize.Height / 4));
    var contentLeft = (int)Math.Ceiling(bounds.Left);
    var contentTop = (int)Math.Ceiling(bounds.Top);
    var contentRight = (int)Math.Floor(bounds.Right);
    var contentBottom = (int)Math.Floor(bounds.Bottom);
    var previewRect = Rectangle.FromLTRB(contentLeft, contentTop, contentRight, Math.Max(contentTop, contentBottom - footerHeight));
    var footerRect = Rectangle.FromLTRB(contentLeft, previewRect.Bottom, contentRight, contentBottom);

    var saved = graphics.Save();
    graphics.SetClip(cardPath);
    using (var previewBrush = new SolidBrush(PreviewBackColor)) graphics.FillRectangle(previewBrush, previewRect);
    if (Preview is not null && Preview.Width > 0 && Preview.Height > 0 && previewRect.Width > 0 && previewRect.Height > 0)
    {
      var scale = Math.Max((double)previewRect.Width / Preview.Width, (double)previewRect.Height / Preview.Height);
      var width = (int)Math.Ceiling(Preview.Width * scale);
      var height = (int)Math.Ceiling(Preview.Height * scale);
      var left = previewRect.Left + (previewRect.Width - width) / 2;
      graphics.DrawImage(Preview, new Rectangle(left, previewRect.Top, width, height));
    }
    else
    {
      using var emptyFont = new Font("Segoe UI", EmptyText == "＋" ? 34F : 17F, FontStyle.Bold);
      TextRenderer.DrawText(graphics, EmptyText, emptyFont, previewRect, MutedTextColor,
          TextFormatFlags.HorizontalCenter | TextFormatFlags.VerticalCenter | TextFormatFlags.EndEllipsis | TextFormatFlags.NoPadding);
    }
    using (var footerBrush = new SolidBrush(FooterBackColor)) graphics.FillRectangle(footerBrush, footerRect);
    graphics.Restore(saved);

    var closeRect = CloseBounds;
    using (var closeBrush = new SolidBrush(_hoveringClose ? ControlPaint.Light(CloseBackColor, 0.08F) : CloseBackColor))
      graphics.FillEllipse(closeBrush, closeRect);
    using (var closePen = new Pen(TextColor, 1.8F)
    {
      StartCap = System.Drawing.Drawing2D.LineCap.Round,
      EndCap = System.Drawing.Drawing2D.LineCap.Round,
    })
    {
      var cx = closeRect.Left + closeRect.Width / 2F;
      var cy = closeRect.Top + closeRect.Height / 2F;
      graphics.DrawLine(closePen, cx - 5, cy - 5, cx + 5, cy + 5);
      graphics.DrawLine(closePen, cx + 5, cy - 5, cx - 5, cy + 5);
    }

    var titleRect = new Rectangle(footerRect.Left + 13, footerRect.Top + 8, Math.Max(0, footerRect.Width - 26), 23);
    TextRenderer.DrawText(graphics, TitleText, Font, titleRect, TextColor,
        TextFormatFlags.Left | TextFormatFlags.VerticalCenter | TextFormatFlags.EndEllipsis |
        TextFormatFlags.SingleLine | TextFormatFlags.NoPadding);
    var addressRect = new Rectangle(footerRect.Left + 13, footerRect.Bottom - 27, Math.Max(0, footerRect.Width - 26), 18);
    using (var addressFont = new Font("Segoe UI", 8.5F))
      TextRenderer.DrawText(graphics, AddressText, addressFont, addressRect, MutedTextColor,
          TextFormatFlags.Left | TextFormatFlags.VerticalCenter | TextFormatFlags.EndEllipsis |
          TextFormatFlags.SingleLine | TextFormatFlags.NoPadding);

    using var border = new Pen(BorderColor, BorderWidth)
    {
      StartCap = System.Drawing.Drawing2D.LineCap.Round,
      EndCap = System.Drawing.Drawing2D.LineCap.Round,
      LineJoin = System.Drawing.Drawing2D.LineJoin.Round,
    };
    graphics.DrawPath(border, cardPath);
  }
}

internal sealed class SafariStartTileControl : Control
{
  private readonly System.Windows.Forms.Timer _interactionTimer = new() { Interval = 15 };
  private Image? _iconImage;
  private bool _hovered;
  private bool _pressed;
  private float _hoverAmount;
  private float _pressAmount;

  internal string Title { get; set; } = string.Empty;
  internal string GlyphText { get; set; } = string.Empty;
  internal bool IsAddTile { get; set; }
  internal Color AccentColor { get; set; } = Color.FromArgb(10, 132, 255);
  internal Image? IconImage
  {
    get => _iconImage;
    set
    {
      if (ReferenceEquals(_iconImage, value)) return;
      _iconImage?.Dispose();
      _iconImage = value;
      Invalidate();
    }
  }

  internal event EventHandler? RightClick;

  protected override Size DefaultSize => new(84, 92);

  internal SafariStartTileControl()
  {
    SetStyle(ControlStyles.UserPaint | ControlStyles.AllPaintingInWmPaint |
        ControlStyles.OptimizedDoubleBuffer | ControlStyles.ResizeRedraw |
        ControlStyles.SupportsTransparentBackColor, true);
    Size = new Size(84, 92);
    BackColor = Color.Transparent;
    Cursor = Cursors.Hand;
    TabStop = false;
    _interactionTimer.Tick += (_, _) =>
    {
      _hoverAmount += ((_hovered ? 1F : 0F) - _hoverAmount) * 0.34F;
      _pressAmount += ((_pressed ? 1F : 0F) - _pressAmount) * 0.44F;
      if (Math.Abs(_hoverAmount - (_hovered ? 1F : 0F)) < 0.015F &&
          Math.Abs(_pressAmount - (_pressed ? 1F : 0F)) < 0.015F)
      {
        _hoverAmount = _hovered ? 1F : 0F;
        _pressAmount = _pressed ? 1F : 0F;
        _interactionTimer.Stop();
      }
      Invalidate();
    };
  }

  protected override void OnMouseEnter(EventArgs e)
  {
    base.OnMouseEnter(e);
    _hovered = true;
    _interactionTimer.Start();
  }

  protected override void OnMouseLeave(EventArgs e)
  {
    base.OnMouseLeave(e);
    _hovered = false;
    _pressed = false;
    _interactionTimer.Start();
  }

  protected override void OnMouseDown(MouseEventArgs e)
  {
    base.OnMouseDown(e);
    if (e.Button == MouseButtons.Left)
    {
      _pressed = true;
      _interactionTimer.Start();
    }
  }

  protected override void OnMouseUp(MouseEventArgs e)
  {
    base.OnMouseUp(e);
    if (e.Button == MouseButtons.Right)
      RightClick?.Invoke(this, EventArgs.Empty);
    _pressed = false;
    _interactionTimer.Start();
  }

  protected override void OnPaintBackground(PaintEventArgs e)
  {
    using var bg = new SolidBrush(UiGeometry.ResolveBackground(Parent, Color.FromArgb(18, 20, 28)));
    e.Graphics.FillRectangle(bg, ClientRectangle);
  }

  protected override void OnPaint(PaintEventArgs e)
  {
    var g = e.Graphics;
    g.SmoothingMode = SmoothingMode.AntiAlias;
    g.PixelOffsetMode = PixelOffsetMode.HighQuality;
    g.InterpolationMode = InterpolationMode.HighQualityBicubic;

    float boxSize = 62F;
    float boxX = (ClientSize.Width - boxSize) / 2F;
    float boxY = 4F - _hoverAmount * 1.5F + _pressAmount * 1.5F;
    var boxRect = new RectangleF(boxX, boxY, boxSize, boxSize);

    using var path = UiGeometry.RoundedRectangle(boxRect, 16F);
    var baseColor = IsAddTile ? Color.FromArgb(32, 34, 42) : Color.FromArgb(42, 44, 52);
    var hoverColor = IsAddTile ? Color.FromArgb(46, 50, 62) : Color.FromArgb(56, 58, 68);
    var fill = UiGeometry.Blend(baseColor, hoverColor, _hoverAmount);
    using (var brush = new SolidBrush(fill))
      g.FillPath(brush, path);

    var borderColor = _hovered
        ? (IsAddTile ? Color.FromArgb(10, 132, 255) : Color.FromArgb(88, 92, 108))
        : Color.FromArgb(62, 64, 74);
    using (var pen = new Pen(borderColor, 1F))
      g.DrawPath(pen, path);

    using (var topHighlight = new Pen(Color.FromArgb(24, 255, 255, 255), 1F))
      g.DrawLine(topHighlight, boxRect.Left + 12F, boxRect.Top + 1F, boxRect.Right - 12F, boxRect.Top + 1F);

    if (_iconImage is not null)
    {
      float iconSize = 32F;
      g.DrawImage(_iconImage, new RectangleF(
          boxRect.Left + (boxRect.Width - iconSize) / 2F,
          boxRect.Top + (boxRect.Height - iconSize) / 2F,
          iconSize,
          iconSize));
    }
    else if (!string.IsNullOrWhiteSpace(GlyphText))
    {
      using var glyphFont = new Font("Segoe UI", IsAddTile ? 18F : 16F, FontStyle.Bold);
      var glyphColor = IsAddTile ? Color.FromArgb(10, 132, 255) : Color.FromArgb(245, 245, 247);
      TextRenderer.DrawText(g, GlyphText, glyphFont, Rectangle.Round(boxRect), glyphColor,
          TextFormatFlags.HorizontalCenter | TextFormatFlags.VerticalCenter | TextFormatFlags.NoPadding);
    }

    var labelRect = new Rectangle(2, (int)(boxY + boxSize + 5), ClientSize.Width - 4, 24);
    using var titleFont = new Font("Segoe UI", 8.75F, FontStyle.Regular);
    TextRenderer.DrawText(g, Title, titleFont, labelRect,
        IsAddTile ? Color.FromArgb(152, 152, 157) : Color.FromArgb(235, 235, 240),
        TextFormatFlags.HorizontalCenter | TextFormatFlags.Top | TextFormatFlags.EndEllipsis | TextFormatFlags.SingleLine | TextFormatFlags.NoPadding);
  }

  protected override void Dispose(bool disposing)
  {
    if (disposing)
    {
      _interactionTimer.Dispose();
      _iconImage?.Dispose();
      _iconImage = null;
    }
    base.Dispose(disposing);
  }
}

internal sealed class TopCropPictureBox : PictureBox
{
  internal TopCropPictureBox()
  {
    DoubleBuffered = true;
    ResizeRedraw = true;
  }

  protected override void OnPaint(PaintEventArgs eventArgs)
  {
    eventArgs.Graphics.Clear(BackColor);
    if (Image is null || ClientSize.Width <= 0 || ClientSize.Height <= 0) return;
    eventArgs.Graphics.InterpolationMode = System.Drawing.Drawing2D.InterpolationMode.HighQualityBicubic;
    eventArgs.Graphics.PixelOffsetMode = System.Drawing.Drawing2D.PixelOffsetMode.HighQuality;
    var scale = Math.Max((double)ClientSize.Width / Image.Width, (double)ClientSize.Height / Image.Height);
    var width = (int)Math.Ceiling(Image.Width * scale);
    var height = (int)Math.Ceiling(Image.Height * scale);
    var left = (ClientSize.Width - width) / 2;
    eventArgs.Graphics.DrawImage(Image, new Rectangle(left, 0, width, height));
  }
}

internal static class UiGeometry
{
  internal static Color Blend(Color from, Color to, float amount)
  {
    amount = Math.Clamp(amount, 0F, 1F);
    return Color.FromArgb(
        (int)Math.Round(from.A + (to.A - from.A) * amount),
        (int)Math.Round(from.R + (to.R - from.R) * amount),
        (int)Math.Round(from.G + (to.G - from.G) * amount),
        (int)Math.Round(from.B + (to.B - from.B) * amount));
  }

  internal static Color ResolveBackground(Control? control, Color fallback)
  {
    for (var current = control; current is not null; current = current.Parent)
    {
      var color = current.BackColor;
      if (color.A == 255 && color != Color.Transparent) return color;
    }
    return fallback;
  }

  internal static System.Drawing.Drawing2D.GraphicsPath RoundedRectangle(Rectangle rectangle, int radius)
      => RoundedRectangle(new RectangleF(rectangle.X, rectangle.Y, rectangle.Width, rectangle.Height), radius);

  internal static System.Drawing.Drawing2D.GraphicsPath RoundedRectangle(RectangleF rectangle, float radius)
  {
    var path = new System.Drawing.Drawing2D.GraphicsPath();
    if (rectangle.Width <= 0 || rectangle.Height <= 0) return path;
    var diameter = Math.Max(1F, Math.Min(radius * 2F, Math.Min(rectangle.Width, rectangle.Height)));
    var arc = new RectangleF(rectangle.X, rectangle.Y, diameter, diameter);
    path.AddArc(arc, 180, 90);
    arc.X = rectangle.Right - diameter;
    path.AddArc(arc, 270, 90);
    arc.Y = rectangle.Bottom - diameter;
    path.AddArc(arc, 0, 90);
    arc.X = rectangle.Left;
    path.AddArc(arc, 90, 90);
    path.CloseFigure();
    return path;
  }
}
