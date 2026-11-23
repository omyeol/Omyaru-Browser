using System.Collections.Concurrent;
using System.Security.Cryptography;
using System.Text;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Media.Animation;
using System.Windows.Media.Imaging;
using System.Windows.Shell;
using System.Windows.Threading;
using Microsoft.Web.WebView2.Core;
using Brush = System.Windows.Media.Brush;
using Brushes = System.Windows.Media.Brushes;
using Color = System.Windows.Media.Color;
using Cursors = System.Windows.Input.Cursors;
using HorizontalAlignment = System.Windows.HorizontalAlignment;
using Image = System.Windows.Controls.Image;
using KeyEventArgs = System.Windows.Input.KeyEventArgs;
using Orientation = System.Windows.Controls.Orientation;
using Panel = System.Windows.Controls.Panel;
using Point = System.Windows.Point;
using TextBox = System.Windows.Controls.TextBox;
using WpfWebView2 = Microsoft.Web.WebView2.Wpf.WebView2;

namespace PrivacyBrowser.Desktop.Safari;

internal sealed partial class SafariBrowserWindow : Window
{
  private const string PrivateGroupId = "__private_workspace__";
  private const double SidebarExpandedWidth = 268;

  private readonly bool _enableAutomaticSync;
  private SyncSnapshot _state = BrowserStateStore.Load();
  private DesktopPreferences _preferences = DesktopPreferencesStore.Load();
  private BookmarkCatalog _bookmarks = BookmarkStore.Load();

  private readonly Dictionary<string, WpfWebView2> _views = new(StringComparer.Ordinal);
  private readonly Dictionary<string, Task<CoreWebView2Environment>> _environments = new(StringComparer.Ordinal);
  private readonly Dictionary<string, BitmapSource> _thumbnails = new(StringComparer.Ordinal);
  private readonly Dictionary<string, BitmapSource> _tabFavicons = new(StringComparer.Ordinal);
  private readonly ConcurrentDictionary<string, BitmapSource> _faviconCache = new(StringComparer.OrdinalIgnoreCase);
  private readonly Stack<TabEntry> _closedTabs = new();

  // Timers (strictly lightweight: NO full-window bitmap snapshots!)
  private readonly DispatcherTimer _autoSyncTimer = new() { Interval = TimeSpan.FromSeconds(1) };
  private readonly DispatcherTimer _thumbnailTimer = new() { Interval = TimeSpan.FromMilliseconds(1400) };

  // Dynamic brushes for macOS Safari Adaptive Tinting & animations
  private readonly SolidColorBrush _chromeTintBrush = new(SafariTheme.ToolbarBaseColor);
  private readonly SolidColorBrush _addressBgBrush = new(SafariTheme.AddressFillColor);
  private readonly SolidColorBrush _addressBorderBrush = new(SafariTheme.SpecularBorderColor);
  private readonly SolidColorBrush _profileBadgeBgBrush = new(Color.FromArgb(190, 10, 132, 255));

  // Root layout & chrome elements
  private readonly Grid _rootGrid = new();
  private readonly Border _sidebarHost = new();
  private readonly ScrollViewer _sidebarScroll = new();
  private readonly StackPanel _sidebarContent = new();
  private readonly TextBox _sidebarSearchBox = new();
  private readonly TextBlock _sidebarProfileHeader = new();
  private readonly Border _toolbarHost = new();
  private readonly Border _favoritesBarHost = new();
  private readonly StackPanel _favoritesBarPanel = new();
  private readonly Border _tabStripHost = new();
  private readonly ScrollViewer _tabStripScroll = new();
  private readonly StackPanel _tabStripPanel = new();
  private readonly Grid _contentHost = new();
  private readonly Grid _webViewLayer = new();
  private readonly ScrollViewer _startPageLayer = new();
  private readonly Grid _overviewLayer = new();
  private readonly Border _statusBarHost = new();
  private readonly TextBlock _statusText = new();
  private readonly Border _statusSyncPill = new();
  private readonly TextBlock _statusSyncText = new();

  // Toolbar controls
  private TextBlock _profileBadgeLabel = null!;
  private Border _backBtn = null!;
  private Border _forwardBtn = null!;
  private Border _pageMenuBtn = null!;
  private Border _addressCapsule = null!;
  private TextBox _addressInput = null!;
  private Border _readerPill = null!;
  private TextBlock _readerPillText = null!;
  private Border _bookmarkStarBtn = null!;
  private ContentControl _bookmarkStarHost = null!;
  private ContentControl _reloadIconHost = null!;
  private Border _loadingProgressBar = null!;
  private Border _extensionsBtn = null!;
  private Border _captureBtn = null!;
  private Border _audioBtn = null!;
  private Border _syncBtn = null!;
  private ContentControl _syncIconHost = null!;
  private Border _menuBtn = null!;
  private Border _profileBadgeBtn = null!;
  private ContentControl _maximizeIconHost = null!;
  private bool? _lastSyncExpiredIcon;
  private bool? _lastStarredIcon;
  private bool? _lastLoadingIcon;

  // Overview controls (Canvas-positioned GPU-animated grid with long-press lift & 2D reorder)
  private const double OverviewCardWidth = 264;
  private const double OverviewCardHeight = 192;
  private const double OverviewCardGap = 16;
  private const double OverviewPadX = 28;
  private const double OverviewPadY = 12;

  private readonly TextBox _overviewSearchBox = new();
  private readonly StackPanel _overviewGroupPills = new();
  private readonly DockPanel _overviewNormalHeader = new();
  private readonly Border _overviewSelectionBar = new();
  private readonly TextBlock _overviewSelectionCountText = new();
  private Border _overviewMoveSelectedBtn = null!;
  private readonly ScrollViewer _overviewScroll = new();
  private readonly Canvas _overviewCanvas = new();
  private readonly ScaleTransform _overviewScale = new(1, 1);
  private readonly List<OverviewCardEntry> _overviewCards = new();
  private readonly DispatcherTimer _overviewLongPressTimer = new() { Interval = TimeSpan.FromMilliseconds(250) };
  private readonly HashSet<string> _overviewSelectedIds = new(StringComparer.Ordinal);
  private bool _overviewSelectionMode;
  private OverviewCardEntry? _overviewPendingCard;
  private OverviewCardEntry? _overviewLiftedCard;
  private Point _overviewPressCanvasPos;
  private double _overviewGrabOffsetX;
  private double _overviewGrabOffsetY;
  private bool _overviewDraggedMoved;
  private bool _overviewDragChangedOrder;
  private bool _overviewPressCancelled;

  // Top tab strip animated drag reorder state
  private readonly List<TabStripDragItem> _tabStripItems = new();
  private List<TabStripDragItem>? _stripLogicalOrder;
  private TabStripDragItem? _activeStripDrag;
  private Point _stripPressPanelPos;
  private double _stripGrabOffsetX;
  private bool _stripDragMoved;
  private bool _stripDragReordered;

  private sealed class OverviewCardEntry
  {
    public required TabEntry Tab { get; init; }
    public required Border Card { get; init; }
    public required SolidColorBrush Background { get; init; }
    public required ScaleTransform Scale { get; init; }
    public required TranslateTransform Translate { get; init; }
    public required bool IsActive { get; init; }
    public double TargetX { get; set; }
    public double TargetY { get; set; }
  }

  private sealed class TabStripDragItem
  {
    public required TabEntry Tab { get; init; }
    public required Border Pill { get; init; }
    public required TranslateTransform Translate { get; init; }
    public required bool IsActive { get; init; }
    public double HomeLeft { get; set; }
    public double SlotWidth { get; set; }
    public double TargetTranslateX { get; set; }
  }

  // Floating Glass Popup (native layered HWND -> renders cleanly ABOVE WebView2!)
  private readonly Popup _activePopup = new()
  {
    AllowsTransparency = true,
    StaysOpen = false,
    PopupAnimation = PopupAnimation.None,
  };
  private readonly Popup _findPopup = new()
  {
    AllowsTransparency = true,
    StaysOpen = true,
    Placement = PlacementMode.Relative,
  };
  private TextBox _findInput = null!;

  // State
  private BookshelfForm? _bookshelfForm;
  private SafariExtensionPopupWindow? _extensionPopup;
  private bool _sidebarOpen;
  private bool _showingOverview;
  private bool _closing;
  private bool _stateDirty;
  private bool _autoSyncBusy;
  private bool _syncDialogOpen;
  private bool _captureInProgress;
  private bool _isLoading;
  private bool _readerAvailable;
  private bool _readerActive;
  private bool _distractionActive;
  private int _distractionHiddenCount;
  private string _readerTheme = "charcoal";
  private string _readerFont = "Segoe UI";
  private int _readerFontSize = 18;
  private string? _pendingThumbnailTabId;
  private DateTimeOffset _driveNextCheck;
  private int _driveFailures;
  private string _syncStatus = "로컬 저장 · 분석/광고 SDK 없음";
  private readonly CancellationTokenSource _driveCancellation = new();

  // Smoke-test observables
  internal bool HasInitializedWebView => _state.ActiveTabId is not null &&
      _views.TryGetValue(_state.ActiveTabId, out var v) && v.CoreWebView2 is not null;
  internal bool HasValidSafariLayout => ActualWidth >= 900 && ActualHeight >= 600 &&
      _toolbarHost.ActualHeight >= 40 && _addressCapsule.ActualWidth >= 220;

  internal SafariBrowserWindow(bool enableAutomaticSync = true)
  {
    _enableAutomaticSync = enableAutomaticSync;
    Title = "Omyaru — Safari Style";
    Width = 1440;
    Height = 900;
    MinWidth = 960;
    MinHeight = 640;
    WindowStartupLocation = WindowStartupLocation.CenterScreen;
    Background = SafariTheme.WindowBg;
    Foreground = SafariTheme.TextPrimary;
    FontFamily = SafariTheme.UiFont;
    UseLayoutRounding = true;
    SnapsToDevicePixels = true;
    SafariTheme.ApplyMacScrollBarStyles(Resources);

    WindowChrome.SetWindowChrome(this, new WindowChrome
    {
      CaptionHeight = 46,
      CornerRadius = new CornerRadius(14),
      GlassFrameThickness = new Thickness(0),
      ResizeBorderThickness = new Thickness(6),
      UseAeroCaptionButtons = false,
    });

    BuildVisualTree();
    LoadPersistedThumbnails();

    SourceInitialized += (_, _) => DwmBackdrop.Apply(this, acrylic: false);
    StateChanged += (_, _) => UpdateWindowChromeState();
    LocationChanged += (_, _) => CloseExtensionPopup();
    SizeChanged += (_, _) => CloseExtensionPopup();
    PreviewKeyDown += OnWindowPreviewKeyDown;

    _thumbnailTimer.Tick += async (_, _) =>
    {
      _thumbnailTimer.Stop();
      if (!string.IsNullOrWhiteSpace(_pendingThumbnailTabId))
        await CaptureTabThumbnailAsync(_pendingThumbnailTabId);
    };
    _autoSyncTimer.Tick += async (_, _) => await FlushAndAutoSyncAsync();
    _overviewLongPressTimer.Tick += OnOverviewLongPressTick;
    GoogleDriveAuthorization.SessionChanged += OnDriveSessionChanged;

    Loaded += async (_, _) =>
    {
      EnsureWorkspaceTab();
      RefreshWorkspaceUi();
      await ActivateCurrentTabAsync();
      if (_enableAutomaticSync)
      {
        _autoSyncTimer.Start();
        await FlushAndAutoSyncAsync();
      }
    };

    Closing += (_, _) =>
    {
      _closing = true;
      GoogleDriveAuthorization.SessionChanged -= OnDriveSessionChanged;
      _driveCancellation.Cancel();
      _autoSyncTimer.Stop();
      _thumbnailTimer.Stop();
      _overviewLongPressTimer.Stop();
      _activePopup.IsOpen = false;
      _findPopup.IsOpen = false;
      CloseExtensionPopup();
      if (_bookshelfForm is { IsDisposed: false })
      {
        try { _bookshelfForm.Close(); _bookshelfForm.Dispose(); } catch { }
        _bookshelfForm = null;
      }
      if (_stateDirty)
      {
        try { BrowserStateStore.SaveChanged(_state); } catch { }
        _stateDirty = false;
      }
      foreach (var view in _views.Values)
      {
        try { view.Dispose(); } catch { }
      }
      _views.Clear();
    };
  }

  private void BuildVisualTree()
  {
    // Main horizontal split: [Sliding Safari Sidebar | Main Browser Column]
    _rootGrid.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
    _rootGrid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
    _rootGrid.Background = SafariTheme.WindowBg;
    Content = _rootGrid;

    // 1. Sliding macOS Safari Sidebar (smooth GPU width animation: 0 <-> 268)
    BuildSidebar();
    Grid.SetColumn(_sidebarHost, 0);
    _rootGrid.Children.Add(_sidebarHost);

    // 2. Right Main Browser Column: [Row 0: Glass Toolbar | Row 1: Favorites Bar | Row 2: Glass Tab Strip | Row 3: Viewport | Row 4: Status Bar]
    var mainColumn = new Grid();
    mainColumn.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
    mainColumn.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
    mainColumn.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
    mainColumn.RowDefinitions.Add(new RowDefinition { Height = new GridLength(1, GridUnitType.Star) });
    mainColumn.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
    Grid.SetColumn(mainColumn, 1);
    _rootGrid.Children.Add(mainColumn);

    BuildToolbar();
    Grid.SetRow(_toolbarHost, 0);
    mainColumn.Children.Add(_toolbarHost);

    BuildFavoritesBar();
    Grid.SetRow(_favoritesBarHost, 1);
    mainColumn.Children.Add(_favoritesBarHost);

    BuildTabStrip();
    Grid.SetRow(_tabStripHost, 2);
    mainColumn.Children.Add(_tabStripHost);

    // Viewport Stack: WebView Layer + GPU Start Page + GPU Tab Overview
    _contentHost.Background = SafariTheme.WindowBg;
    _contentHost.ClipToBounds = true;
    _contentHost.Children.Add(_webViewLayer);

    BuildStartPage();
    _contentHost.Children.Add(_startPageLayer);

    BuildTabOverview();
    _contentHost.Children.Add(_overviewLayer);

    BuildFindPopup();
    Grid.SetRow(_contentHost, 3);
    mainColumn.Children.Add(_contentHost);

    BuildStatusBar();
    Grid.SetRow(_statusBarHost, 4);
    mainColumn.Children.Add(_statusBarHost);

    ApplyChromePreferences();
  }

  private void BuildSidebar()
  {
    _sidebarHost.Width = 0;
    _sidebarHost.Background = SafariTheme.SidebarBg;
    _sidebarHost.BorderBrush = SafariTheme.Divider;
    _sidebarHost.BorderThickness = new Thickness(0, 0, 1, 0);
    _sidebarHost.ClipToBounds = true;

    var inner = new Grid { Width = SidebarExpandedWidth };
    inner.RowDefinitions.Add(new RowDefinition { Height = new GridLength(48) });
    inner.RowDefinitions.Add(new RowDefinition { Height = new GridLength(40) });
    inner.RowDefinitions.Add(new RowDefinition { Height = new GridLength(1, GridUnitType.Star) });
    inner.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });

    // Top Sidebar Header
    var header = new DockPanel { Margin = new Thickness(14, 10, 10, 4), VerticalAlignment = VerticalAlignment.Center };
    var closeBtn = SafariTheme.CreateGlassButton(SafariIcons.Create(SafariIcon.Sidebar, 15.5, SafariTheme.TextSecondary), ToggleSidebar, "사이드바 닫기 (Ctrl+Shift+S)", 30, 28, 9);
    WindowChrome.SetIsHitTestVisibleInChrome(closeBtn, true);
    DockPanel.SetDock(closeBtn, Dock.Right);
    header.Children.Add(closeBtn);

    _sidebarProfileHeader.FontSize = 14;
    _sidebarProfileHeader.FontWeight = FontWeights.SemiBold;
    _sidebarProfileHeader.Foreground = SafariTheme.TextPrimary;
    _sidebarProfileHeader.VerticalAlignment = VerticalAlignment.Center;
    _sidebarProfileHeader.TextTrimming = TextTrimming.CharacterEllipsis;
    header.Children.Add(_sidebarProfileHeader);
    Grid.SetRow(header, 0);
    inner.Children.Add(header);

    // Inline Search Filter Box (like Safari Sidebar search at 8:14)
    var searchBorder = new Border
    {
      Margin = new Thickness(12, 2, 12, 6),
      CornerRadius = new CornerRadius(10),
      Background = new SolidColorBrush(Color.FromArgb(180, 38, 41, 51)),
      BorderBrush = SafariTheme.CapsuleBorder,
      BorderThickness = new Thickness(1),
      Padding = new Thickness(9, 0, 10, 0),
    };
    _sidebarSearchBox.Background = Brushes.Transparent;
    _sidebarSearchBox.BorderThickness = new Thickness(0);
    _sidebarSearchBox.Foreground = SafariTheme.TextPrimary;
    _sidebarSearchBox.CaretBrush = SafariTheme.Accent;
    _sidebarSearchBox.FontSize = 12;
    _sidebarSearchBox.VerticalContentAlignment = VerticalAlignment.Center;
    _sidebarSearchBox.TextChanged += (_, _) => PopulateSidebar();
    var searchGrid = new Grid();
    searchGrid.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
    searchGrid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
    var searchIcon = SafariIcons.Create(SafariIcon.Search, 13, SafariTheme.TextMuted);
    if (searchIcon is FrameworkElement feSearch) feSearch.Margin = new Thickness(0, 0, 6, 0);
    Grid.SetColumn(searchIcon, 0);
    searchGrid.Children.Add(searchIcon);
    var searchInputHost = new Grid();
    var searchHint = new TextBlock
    {
      Text = "탭·그룹·북마크 검색",
      Foreground = SafariTheme.TextMuted,
      FontSize = 12,
      VerticalAlignment = VerticalAlignment.Center,
      IsHitTestVisible = false,
    };
    _sidebarSearchBox.TextChanged += (_, _) => searchHint.Visibility = string.IsNullOrEmpty(_sidebarSearchBox.Text) ? Visibility.Visible : Visibility.Collapsed;
    searchInputHost.Children.Add(searchHint);
    searchInputHost.Children.Add(_sidebarSearchBox);
    Grid.SetColumn(searchInputHost, 1);
    searchGrid.Children.Add(searchInputHost);
    searchBorder.Child = searchGrid;
    WindowChrome.SetIsHitTestVisibleInChrome(searchBorder, true);
    Grid.SetRow(searchBorder, 1);
    inner.Children.Add(searchBorder);

    // Scrollable Sidebar Body
    _sidebarContent.Margin = new Thickness(10, 4, 10, 14);
    _sidebarScroll.VerticalScrollBarVisibility = ScrollBarVisibility.Auto;
    _sidebarScroll.HorizontalScrollBarVisibility = ScrollBarVisibility.Disabled;
    _sidebarScroll.Content = _sidebarContent;
    Grid.SetRow(_sidebarScroll, 2);
    inner.Children.Add(_sidebarScroll);

    // Sidebar Bottom Quick Bar
    var footer = new Border
    {
      BorderBrush = SafariTheme.Divider,
      BorderThickness = new Thickness(0, 1, 0, 0),
      Padding = new Thickness(10, 8, 10, 10),
    };
    var footerRow = new UniformGrid { Columns = 2 };
    var newGroupContent = new StackPanel { Orientation = Orientation.Horizontal, HorizontalAlignment = HorizontalAlignment.Center, VerticalAlignment = VerticalAlignment.Center };
    newGroupContent.Children.Add(SafariIcons.Create(SafariIcon.Plus, 13, SafariTheme.TextPrimary));
    newGroupContent.Children.Add(new TextBlock { Text = "탭 그룹", FontSize = 12, Foreground = SafariTheme.TextPrimary, Margin = new Thickness(5, 0, 0, 0), VerticalAlignment = VerticalAlignment.Center });
    var newGroupBtn = SafariTheme.CreateGlassButton(
        newGroupContent,
        PromptCreateTabGroup,
        "새 탭 그룹 만들기",
        double.NaN, 30, 9, transparentIdle: false);
    newGroupBtn.Margin = new Thickness(0, 0, 4, 0);

    var shelfContent = new StackPanel { Orientation = Orientation.Horizontal, HorizontalAlignment = HorizontalAlignment.Center, VerticalAlignment = VerticalAlignment.Center };
    shelfContent.Children.Add(SafariIcons.Create(SafariIcon.Bookshelf, 13.5, SafariTheme.TextPrimary));
    shelfContent.Children.Add(new TextBlock { Text = "책장", FontSize = 12, Foreground = SafariTheme.TextPrimary, Margin = new Thickness(5, 0, 0, 0), VerticalAlignment = VerticalAlignment.Center });
    var openShelfBtn = SafariTheme.CreateGlassButton(
        shelfContent,
        OpenBookshelfWindow,
        "내장 책장 열기",
        double.NaN, 30, 9, transparentIdle: false);
    openShelfBtn.Margin = new Thickness(4, 0, 0, 0);
    footerRow.Children.Add(newGroupBtn);
    footerRow.Children.Add(openShelfBtn);
    footer.Child = footerRow;
    Grid.SetRow(footer, 3);
    inner.Children.Add(footer);

    _sidebarHost.Child = inner;
  }

  private void BuildToolbar()
  {
    _toolbarHost.Background = _chromeTintBrush;
    _toolbarHost.Padding = new Thickness(12, 8, 10, 7);

    var grid = new Grid();
    grid.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
    grid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
    grid.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });

    // LEFT CLUSTER: [Sidebar] [Color Profile Badge] [ ‹ | › ] — macOS 27 Liquid Glass
    var leftStack = new StackPanel { Orientation = Orientation.Horizontal, VerticalAlignment = VerticalAlignment.Center };

    var sidebarBtn = SafariTheme.CreateGlassButton(
        SafariIcons.Create(SafariIcon.Sidebar, 15.5, SafariTheme.TextPrimary, 1.55),
        ToggleSidebar,
        "사이드바 토글 (Ctrl+Shift+S)",
        30, 30, 15);
    var leftNavCapsule = SafariTheme.CreateGlassCapsule(sidebarBtn, 36, 18, new Thickness(2));
    leftNavCapsule.Width = 36;
    leftNavCapsule.Margin = new Thickness(0, 0, 8, 0);
    WindowChrome.SetIsHitTestVisibleInChrome(leftNavCapsule, true);
    leftStack.Children.Add(leftNavCapsule);

    // Color-coded macOS 27 Profile Pill Badge with crisp SF-Symbol person + vector chevron
    var profileRow = new StackPanel
    {
      Orientation = Orientation.Horizontal,
      VerticalAlignment = VerticalAlignment.Center,
      Margin = new Thickness(11, 0, 10, 0),
    };
    profileRow.Children.Add(SafariIcons.Create(SafariIcon.Profile, 14, Brushes.White, 1.7));
    _profileBadgeLabel = new TextBlock
    {
      Text = "개인",
      Foreground = Brushes.White,
      FontSize = 12,
      FontWeight = FontWeights.SemiBold,
      VerticalAlignment = VerticalAlignment.Center,
      Margin = new Thickness(5, 0, 5, 0),
    };
    profileRow.Children.Add(_profileBadgeLabel);
    profileRow.Children.Add(SafariIcons.Create(SafariIcon.ChevronDown, 11, Brushes.White, 1.9));

    _profileBadgeBtn = new Border
    {
      Height = 36,
      CornerRadius = new CornerRadius(18),
      Background = _profileBadgeBgBrush,
      BorderBrush = SafariTheme.SpecularBorder,
      BorderThickness = new Thickness(1),
      Cursor = Cursors.Hand,
      ToolTip = "프로필 전환 / 관리",
      Margin = new Thickness(0, 0, 8, 0),
      Child = profileRow,
    };
    _profileBadgeBtn.MouseLeftButtonUp += (_, e) => { e.Handled = true; ShowProfilePopup(_profileBadgeBtn); };
    WindowChrome.SetIsHitTestVisibleInChrome(_profileBadgeBtn, true);
    leftStack.Children.Add(_profileBadgeBtn);

    // Back / Forward Liquid Glass Capsule with centered vector chevrons + subtle divider
    var navRow = new StackPanel { Orientation = Orientation.Horizontal, VerticalAlignment = VerticalAlignment.Center };
    _backBtn = SafariTheme.CreateGlassButton(SafariIcons.Create(SafariIcon.ChevronLeft, 15, SafariTheme.TextPrimary, 1.85), GoBack, "뒤로 (Alt+←)", 32, 30, 15);
    _forwardBtn = SafariTheme.CreateGlassButton(SafariIcons.Create(SafariIcon.ChevronRight, 15, SafariTheme.TextPrimary, 1.85), GoForward, "앞으로 (Alt+→)", 32, 30, 15);
    navRow.Children.Add(_backBtn);
    navRow.Children.Add(SafariTheme.CreateCapsuleDivider());
    navRow.Children.Add(_forwardBtn);
    var navCapsule = SafariTheme.CreateGlassCapsule(navRow, 36, 18, new Thickness(3, 0, 3, 0));
    WindowChrome.SetIsHitTestVisibleInChrome(navCapsule, true);
    leftStack.Children.Add(navCapsule);

    Grid.SetColumn(leftStack, 0);
    grid.Children.Add(leftStack);

    // CENTER SMART SEARCH / ADDRESS CAPSULE (macOS 27 Liquid Glass Pill)
    var centerHost = new Grid { Margin = new Thickness(18, 0, 18, 0), MaxWidth = 660, HorizontalAlignment = HorizontalAlignment.Stretch };
    _addressCapsule = new Border
    {
      Height = 36,
      CornerRadius = new CornerRadius(18),
      Background = _addressBgBrush,
      BorderBrush = _addressBorderBrush,
      BorderThickness = new Thickness(1),
      ClipToBounds = true,
    };
    WindowChrome.SetIsHitTestVisibleInChrome(_addressCapsule, true);

    var addrInner = new Grid();
    addrInner.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
    addrInner.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
    addrInner.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
    addrInner.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
    addrInner.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });

    // Page Menu Button (authentic macOS 27 Safari Page Menu icon)
    _pageMenuBtn = SafariTheme.CreateGlassButton(
        SafariIcons.Create(SafariIcon.PageMenu, 14.5, SafariTheme.TextSecondary, 1.55),
        () => ShowPageMenuPopup(_pageMenuBtn),
        "페이지 메뉴 (읽기 도구 · 방해 요소 가리기 · 확대/축소)",
        32, 30, 15);
    _pageMenuBtn.Margin = new Thickness(3, 0, 2, 0);
    Grid.SetColumn(_pageMenuBtn, 0);
    addrInner.Children.Add(_pageMenuBtn);

    // Transient "읽기 도구" / "N개 숨김" Micro-Pill (from 15:15 & 19:17 in the video!)
    _readerPillText = new TextBlock
    {
      Text = "읽기 도구",
      Foreground = Brushes.White,
      FontSize = 11,
      FontWeight = FontWeights.SemiBold,
      VerticalAlignment = VerticalAlignment.Center,
    };
    _readerPill = new Border
    {
      Height = 22,
      CornerRadius = new CornerRadius(11),
      Background = SafariTheme.Accent,
      Padding = new Thickness(8, 0, 8, 0),
      Margin = new Thickness(2, 0, 4, 0),
      Cursor = Cursors.Hand,
      Visibility = Visibility.Collapsed,
      VerticalAlignment = VerticalAlignment.Center,
      Child = _readerPillText,
    };
    _readerPill.MouseLeftButtonUp += async (_, e) =>
    {
      e.Handled = true;
      if (_distractionActive) await ToggleDistractionControlAsync();
      else await ToggleReaderModeAsync();
    };
    Grid.SetColumn(_readerPill, 1);
    addrInner.Children.Add(_readerPill);

    // Smart Search TextBox + Placeholder
    var textHost = new Grid { VerticalAlignment = VerticalAlignment.Center };
    var placeholder = new TextBlock
    {
      Text = "검색 또는 웹사이트 주소 입력",
      Foreground = SafariTheme.TextMuted,
      FontSize = 13,
      HorizontalAlignment = HorizontalAlignment.Center,
      VerticalAlignment = VerticalAlignment.Center,
      IsHitTestVisible = false,
    };
    _addressInput = new TextBox
    {
      Background = Brushes.Transparent,
      BorderThickness = new Thickness(0),
      Foreground = SafariTheme.TextPrimary,
      CaretBrush = SafariTheme.Accent,
      SelectionBrush = SafariTheme.Accent,
      FontSize = 13,
      HorizontalContentAlignment = HorizontalAlignment.Center,
      VerticalContentAlignment = VerticalAlignment.Center,
      Padding = new Thickness(6, 0, 6, 0),
    };
    _addressInput.TextChanged += (_, _) =>
        placeholder.Visibility = string.IsNullOrEmpty(_addressInput.Text) ? Visibility.Visible : Visibility.Collapsed;
    _addressInput.GotKeyboardFocus += (_, _) =>
    {
      _addressInput.HorizontalContentAlignment = HorizontalAlignment.Left;
      var activeTab = ActiveTab();
      if (activeTab is not null && activeTab.Url != "about:blank")
        _addressInput.Text = activeTab.Url;
      Dispatcher.BeginInvoke(() => _addressInput.SelectAll());
      SafariTheme.AnimateColor(_addressBgBrush, SafariTheme.AddressFocusFillColor, 130);
      SafariTheme.AnimateColor(_addressBorderBrush, SafariTheme.AccentColor, 130);
    };
    _addressInput.LostKeyboardFocus += (_, _) =>
    {
      _addressInput.HorizontalContentAlignment = HorizontalAlignment.Center;
      var activeTab = ActiveTab();
      _addressInput.Text = DisplayAddress(activeTab?.Url ?? "about:blank");
      SafariTheme.AnimateColor(_addressBgBrush, SafariTheme.AddressFillColor, 160);
      SafariTheme.AnimateColor(_addressBorderBrush, SafariTheme.SpecularBorderColor, 160);
    };
    _addressInput.PreviewMouseLeftButtonDown += (_, e) =>
    {
      if (!_addressInput.IsKeyboardFocusWithin)
      {
        e.Handled = true;
        _addressInput.Focus();
      }
    };
    _addressInput.KeyDown += async (_, e) =>
    {
      if (e.Key == Key.Enter)
      {
        e.Handled = true;
        var dest = ResolveInput(_addressInput.Text);
        Keyboard.ClearFocus();
        await NavigateToAsync(dest);
      }
      else if (e.Key == Key.Escape)
      {
        e.Handled = true;
        Keyboard.ClearFocus();
      }
    };
    textHost.Children.Add(placeholder);
    textHost.Children.Add(_addressInput);
    Grid.SetColumn(textHost, 2);
    addrInner.Children.Add(textHost);

    // Bookmark Star (Vector SF Symbol)
    _bookmarkStarHost = new ContentControl
    {
      HorizontalAlignment = HorizontalAlignment.Center,
      VerticalAlignment = VerticalAlignment.Center,
      IsHitTestVisible = false,
      Content = SafariIcons.Create(SafariIcon.StarOutline, 14.5, SafariTheme.TextSecondary, 1.55),
    };
    _bookmarkStarBtn = SafariTheme.CreateGlassButton(_bookmarkStarHost, ToggleCurrentBookmark, "북마크 추가 / 해제 (Ctrl+D)", 30, 30, 15);
    Grid.SetColumn(_bookmarkStarBtn, 3);
    addrInner.Children.Add(_bookmarkStarBtn);

    // Reload / Stop (Vector SF Symbol)
    _reloadIconHost = new ContentControl
    {
      HorizontalAlignment = HorizontalAlignment.Center,
      VerticalAlignment = VerticalAlignment.Center,
      IsHitTestVisible = false,
      Content = SafariIcons.Create(SafariIcon.Reload, 14.5, SafariTheme.TextSecondary, 1.65),
    };
    var reloadBtn = SafariTheme.CreateGlassButton(_reloadIconHost, ReloadOrStopActiveTab, "새로고침 / 중지 (Ctrl+R)", 30, 30, 15);
    reloadBtn.Margin = new Thickness(0, 0, 3, 0);
    Grid.SetColumn(reloadBtn, 4);
    addrInner.Children.Add(reloadBtn);

    // Thin bottom GPU loading bar inside the capsule
    _loadingProgressBar = new Border
    {
      Height = 2,
      HorizontalAlignment = HorizontalAlignment.Left,
      VerticalAlignment = VerticalAlignment.Bottom,
      Background = SafariTheme.Accent,
      Width = 0,
      Opacity = 0,
      IsHitTestVisible = false,
    };
    Grid.SetColumnSpan(_loadingProgressBar, 5);
    addrInner.Children.Add(_loadingProgressBar);

    _addressCapsule.Child = addrInner;
    centerHost.Children.Add(_addressCapsule);
    Grid.SetColumn(centerHost, 1);
    grid.Children.Add(centerHost);

    // RIGHT CLUSTER: Two airy macOS 27 Liquid Glass Capsules + crisp vector Window Controls
    var rightStack = new StackPanel { Orientation = Orientation.Horizontal, VerticalAlignment = VerticalAlignment.Center };

    // Capsule 1: Omyaru Tools (Bookshelf · Capture · Extensions · Audio · Sync)
    var utilityRow = new StackPanel { Orientation = Orientation.Horizontal, VerticalAlignment = VerticalAlignment.Center };
    var bookshelfBtn = SafariTheme.CreateGlassButton(SafariIcons.Create(SafariIcon.Bookshelf, 15.5), OpenBookshelfWindow, "책장 열기", 33, 30, 15);
    _captureBtn = SafariTheme.CreateGlassButton(SafariIcons.Create(SafariIcon.Capture, 15.5), () => ShowCapturePopup(_captureBtn), "화면 캡처 · 워터마크 삭제", 33, 30, 15);
    _extensionsBtn = SafariTheme.CreateGlassButton(SafariIcons.Create(SafariIcon.Extensions, 15.5), () => _ = ShowExtensionsPopupAsync(_extensionsBtn), "확장 프로그램", 33, 30, 15);
    _audioBtn = SafariTheme.CreateGlassButton(SafariIcons.Create(SafariIcon.Audio, 15.5), () => _ = ShowAudioPopupAsync(_audioBtn), "소리 출력 장치", 33, 30, 15);
    _syncIconHost = new ContentControl
    {
      HorizontalAlignment = HorizontalAlignment.Center,
      VerticalAlignment = VerticalAlignment.Center,
      IsHitTestVisible = false,
      Content = SafariIcons.Create(SafariIcon.Sync, 15, SafariTheme.TextPrimary, 1.65),
    };
    _syncBtn = SafariTheme.CreateGlassButton(_syncIconHost, () => _ = ShowSyncDialogAsync(), "Google Drive 동기화", 33, 30, 15);

    utilityRow.Children.Add(bookshelfBtn);
    utilityRow.Children.Add(_captureBtn);
    utilityRow.Children.Add(_extensionsBtn);
    utilityRow.Children.Add(_audioBtn);
    utilityRow.Children.Add(_syncBtn);
    var utilityCapsule = SafariTheme.CreateGlassCapsule(utilityRow, 36, 18, new Thickness(3, 0, 3, 0));
    utilityCapsule.Margin = new Thickness(0, 0, 8, 0);
    WindowChrome.SetIsHitTestVisibleInChrome(utilityCapsule, true);
    rightStack.Children.Add(utilityCapsule);

    // Capsule 2: Safari Core Actions (+ New Tab · Overlapping Squares Tab Overview · More Menu)
    var safariRow = new StackPanel { Orientation = Orientation.Horizontal, VerticalAlignment = VerticalAlignment.Center };
    var newTabBtn = SafariTheme.CreateGlassButton(SafariIcons.Create(SafariIcon.Plus, 15.5), () => _ = CreateTabAsync(), "새 탭 (Ctrl+T)", 33, 30, 15);
    var overviewBtn = SafariTheme.CreateGlassButton(SafariIcons.Create(SafariIcon.TabOverview, 15.5), ToggleTabOverview, "탭 목록 (Ctrl+Shift+O)", 33, 30, 15);
    _menuBtn = SafariTheme.CreateGlassButton(SafariIcons.Create(SafariIcon.Ellipsis, 15.5), () => ShowMainMenuPopup(_menuBtn), "더보기 메뉴", 33, 30, 15);
    safariRow.Children.Add(newTabBtn);
    safariRow.Children.Add(overviewBtn);
    safariRow.Children.Add(_menuBtn);
    var safariCapsule = SafariTheme.CreateGlassCapsule(safariRow, 36, 18, new Thickness(3, 0, 3, 0));
    safariCapsule.Margin = new Thickness(0, 0, 10, 0);
    WindowChrome.SetIsHitTestVisibleInChrome(safariCapsule, true);
    rightStack.Children.Add(safariCapsule);

    // Crisp Vector Window Controls
    var winControls = new StackPanel { Orientation = Orientation.Horizontal, VerticalAlignment = VerticalAlignment.Center };
    var minBtn = SafariTheme.CreateGlassButton(SafariIcons.Create(SafariIcon.WindowMinimize, 13, SafariTheme.TextSecondary), () => WindowState = WindowState.Minimized, "최소화", 34, 30, 10);
    _maximizeIconHost = new ContentControl
    {
      HorizontalAlignment = HorizontalAlignment.Center,
      VerticalAlignment = VerticalAlignment.Center,
      IsHitTestVisible = false,
      Content = SafariIcons.Create(SafariIcon.WindowMaximize, 13, SafariTheme.TextSecondary),
    };
    var maxBtn = SafariTheme.CreateGlassButton(_maximizeIconHost, () => WindowState = WindowState == WindowState.Maximized ? WindowState.Normal : WindowState.Maximized, "최대화 / 복원", 34, 30, 10);
    var closeWinBtn = SafariTheme.CreateGlassButton(SafariIcons.Create(SafariIcon.WindowClose, 13, SafariTheme.TextSecondary), Close, "닫기", 34, 30, 10);
    closeWinBtn.MouseEnter += (_, _) =>
    {
      if (closeWinBtn.Background is SolidColorBrush b) SafariTheme.AnimateColor(b, SafariTheme.DangerRedColor, 90);
    };
    winControls.Children.Add(minBtn);
    winControls.Children.Add(maxBtn);
    winControls.Children.Add(closeWinBtn);
    WindowChrome.SetIsHitTestVisibleInChrome(winControls, true);
    rightStack.Children.Add(winControls);

    Grid.SetColumn(rightStack, 2);
    grid.Children.Add(rightStack);

    _toolbarHost.Child = grid;
  }

  private void BuildFavoritesBar()
  {
    _favoritesBarHost.Background = _chromeTintBrush;
    _favoritesBarHost.Padding = new Thickness(12, 0, 12, 5);
    _favoritesBarPanel.Orientation = Orientation.Horizontal;
    _favoritesBarPanel.HorizontalAlignment = HorizontalAlignment.Center;
    var scroll = new ScrollViewer
    {
      HorizontalScrollBarVisibility = ScrollBarVisibility.Hidden,
      VerticalScrollBarVisibility = ScrollBarVisibility.Disabled,
      Content = _favoritesBarPanel,
    };
    scroll.PreviewMouseWheel += (_, e) =>
    {
      scroll.ScrollToHorizontalOffset(scroll.HorizontalOffset - e.Delta * 0.5);
      e.Handled = true;
    };
    _favoritesBarHost.Child = scroll;
  }

  private void BuildTabStrip()
  {
    _tabStripHost.Background = _chromeTintBrush;
    _tabStripHost.BorderBrush = SafariTheme.Divider;
    _tabStripHost.BorderThickness = new Thickness(0, 0, 0, 1);
    _tabStripHost.Padding = new Thickness(10, 2, 10, 6);

    _tabStripPanel.Orientation = Orientation.Horizontal;
    _tabStripScroll.HorizontalScrollBarVisibility = ScrollBarVisibility.Hidden;
    _tabStripScroll.VerticalScrollBarVisibility = ScrollBarVisibility.Disabled;
    _tabStripScroll.Content = _tabStripPanel;
    _tabStripScroll.PreviewMouseWheel += (_, e) =>
    {
      _tabStripScroll.ScrollToHorizontalOffset(_tabStripScroll.HorizontalOffset - e.Delta * 0.6);
      e.Handled = true;
    };
    _tabStripHost.Child = _tabStripScroll;
  }

  private void BuildStartPage()
  {
    _startPageLayer.VerticalScrollBarVisibility = ScrollBarVisibility.Auto;
    _startPageLayer.HorizontalScrollBarVisibility = ScrollBarVisibility.Disabled;
    _startPageLayer.Visibility = Visibility.Collapsed;
    _startPageLayer.Background = new LinearGradientBrush(
        Color.FromRgb(20, 22, 30),
        Color.FromRgb(14, 15, 20),
        new Point(0, 0),
        new Point(1, 1));
  }

  private void BuildTabOverview()
  {
    _overviewLayer.Background = new SolidColorBrush(Color.FromArgb(248, 16, 17, 22));
    _overviewLayer.Visibility = Visibility.Collapsed;
    _overviewLayer.Opacity = 0;
    _overviewLayer.RenderTransformOrigin = new Point(0.5, 0.5);
    _overviewLayer.RenderTransform = _overviewScale;

    _overviewLayer.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
    _overviewLayer.RowDefinitions.Add(new RowDefinition { Height = new GridLength(1, GridUnitType.Star) });

    // Top Overview Header Container (switches between Normal Header and Multi-Select Action Bar)
    var headerHost = new Grid { Margin = new Thickness(28, 20, 28, 12) };

    var doneBtn = SafariTheme.CreateGlassButton(
        SafariTheme.CreateGlyph("완료", 12.5, Brushes.White),
        ToggleTabOverview,
        "탭 목록 닫기",
        68, 32, 16, transparentIdle: false, accentFill: SafariTheme.AccentColor);
    DockPanel.SetDock(doneBtn, Dock.Right);
    _overviewNormalHeader.Children.Add(doneBtn);

    var addRow = new StackPanel { Orientation = Orientation.Horizontal, HorizontalAlignment = HorizontalAlignment.Center, VerticalAlignment = VerticalAlignment.Center };
    addRow.Children.Add(SafariIcons.Create(SafariIcon.Plus, 13, SafariTheme.TextPrimary));
    addRow.Children.Add(new TextBlock { Text = "새 탭", FontSize = 12.5, Foreground = SafariTheme.TextPrimary, Margin = new Thickness(5, 0, 0, 0), VerticalAlignment = VerticalAlignment.Center });
    var addBtn = SafariTheme.CreateGlassButton(
        addRow,
        () => _ = CreateTabAsync(),
        "새 탭 열기",
        88, 32, 16, transparentIdle: false);
    addBtn.Margin = new Thickness(0, 0, 8, 0);
    DockPanel.SetDock(addBtn, Dock.Right);
    _overviewNormalHeader.Children.Add(addBtn);

    // Search Filter
    var ovSearch = new Border
    {
      Width = 240,
      Height = 32,
      CornerRadius = new CornerRadius(16),
      Background = SafariTheme.CapsuleFill,
      BorderBrush = SafariTheme.SpecularBorder,
      BorderThickness = new Thickness(1),
      Padding = new Thickness(11, 0, 12, 0),
      Margin = new Thickness(0, 0, 10, 0),
    };
    _overviewSearchBox.Background = Brushes.Transparent;
    _overviewSearchBox.BorderThickness = new Thickness(0);
    _overviewSearchBox.Foreground = SafariTheme.TextPrimary;
    _overviewSearchBox.CaretBrush = SafariTheme.Accent;
    _overviewSearchBox.FontSize = 12.5;
    _overviewSearchBox.VerticalContentAlignment = VerticalAlignment.Center;
    var ovSearchGrid = new Grid();
    ovSearchGrid.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
    ovSearchGrid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
    var ovSearchIcon = SafariIcons.Create(SafariIcon.Search, 13, SafariTheme.TextMuted);
    if (ovSearchIcon is FrameworkElement feOv) feOv.Margin = new Thickness(0, 0, 6, 0);
    Grid.SetColumn(ovSearchIcon, 0);
    ovSearchGrid.Children.Add(ovSearchIcon);
    var ovInputHost = new Grid();
    var ovHint = new TextBlock
    {
      Text = "열린 탭 검색",
      Foreground = SafariTheme.TextMuted,
      FontSize = 12.5,
      VerticalAlignment = VerticalAlignment.Center,
      IsHitTestVisible = false,
    };
    _overviewSearchBox.TextChanged += (_, _) =>
    {
      ovHint.Visibility = string.IsNullOrEmpty(_overviewSearchBox.Text) ? Visibility.Visible : Visibility.Collapsed;
      PopulateTabOverview();
    };
    ovInputHost.Children.Add(ovHint);
    ovInputHost.Children.Add(_overviewSearchBox);
    Grid.SetColumn(ovInputHost, 1);
    ovSearchGrid.Children.Add(ovInputHost);
    ovSearch.Child = ovSearchGrid;
    DockPanel.SetDock(ovSearch, Dock.Right);
    _overviewNormalHeader.Children.Add(ovSearch);

    _overviewGroupPills.Orientation = Orientation.Horizontal;
    _overviewGroupPills.VerticalAlignment = VerticalAlignment.Center;
    _overviewNormalHeader.Children.Add(_overviewGroupPills);

    // Multi-Select Action Bar (shown when user picks "탭 선택")
    var selDock = new DockPanel { LastChildFill = false };
    var selDoneBtn = SafariTheme.CreateGlassButton(
        SafariTheme.CreateGlyph("완료", 12.5, Brushes.White),
        ExitOverviewSelectionMode,
        "탭 선택 모드 종료",
        68, 32, 16, transparentIdle: false, accentFill: SafariTheme.AccentColor);
    DockPanel.SetDock(selDoneBtn, Dock.Right);
    selDock.Children.Add(selDoneBtn);

    var selCloseRow = new StackPanel { Orientation = Orientation.Horizontal, HorizontalAlignment = HorizontalAlignment.Center, VerticalAlignment = VerticalAlignment.Center };
    selCloseRow.Children.Add(SafariIcons.Create(SafariIcon.Trash, 13, Brushes.White, 1.65));
    selCloseRow.Children.Add(new TextBlock { Text = "선택 닫기", FontSize = 12.5, Foreground = Brushes.White, Margin = new Thickness(5, 0, 0, 0), VerticalAlignment = VerticalAlignment.Center });
    var selCloseBtn = SafariTheme.CreateGlassButton(
        selCloseRow,
        () => _ = CloseSelectedOverviewTabsAsync(),
        "선택한 탭 모두 닫기",
        98, 32, 16, transparentIdle: false, accentFill: SafariTheme.DangerRedColor);
    selCloseBtn.Margin = new Thickness(0, 0, 8, 0);
    DockPanel.SetDock(selCloseBtn, Dock.Right);
    selDock.Children.Add(selCloseBtn);

    var selMoveRow = new StackPanel { Orientation = Orientation.Horizontal, HorizontalAlignment = HorizontalAlignment.Center, VerticalAlignment = VerticalAlignment.Center };
    selMoveRow.Children.Add(SafariIcons.Create(SafariIcon.Folder, 13, SafariTheme.TextPrimary, 1.65));
    selMoveRow.Children.Add(new TextBlock { Text = "탭 이동", FontSize = 12.5, Foreground = SafariTheme.TextPrimary, Margin = new Thickness(5, 0, 0, 0), VerticalAlignment = VerticalAlignment.Center });
    _overviewMoveSelectedBtn = SafariTheme.CreateGlassButton(
        selMoveRow,
        () =>
        {
          if (_overviewSelectedIds.Count == 0) return;
          ShowMoveTabsPopup(_overviewMoveSelectedBtn, _overviewSelectedIds.ToList());
        },
        "선택한 탭을 다른 그룹/프로필로 이동",
        92, 32, 16, transparentIdle: false);
    _overviewMoveSelectedBtn.Margin = new Thickness(0, 0, 8, 0);
    DockPanel.SetDock(_overviewMoveSelectedBtn, Dock.Right);
    selDock.Children.Add(_overviewMoveSelectedBtn);

    var selAllBtn = SafariTheme.CreateGlassButton(
        SafariTheme.CreateGlyph("전체 선택", 12, SafariTheme.TextPrimary),
        () =>
        {
          var visibleIds = _overviewCards.Select(c => c.Tab.Id).ToList();
          if (visibleIds.All(id => _overviewSelectedIds.Contains(id)))
            _overviewSelectedIds.Clear();
          else
            foreach (var id in visibleIds) _overviewSelectedIds.Add(id);
          PopulateTabOverview();
        },
        "현재 목록 전체 선택 / 해제",
        84, 32, 16, transparentIdle: false);
    selAllBtn.Margin = new Thickness(0, 0, 8, 0);
    DockPanel.SetDock(selAllBtn, Dock.Right);
    selDock.Children.Add(selAllBtn);

    _overviewSelectionCountText.FontSize = 14;
    _overviewSelectionCountText.FontWeight = FontWeights.SemiBold;
    _overviewSelectionCountText.Foreground = SafariTheme.TextPrimary;
    _overviewSelectionCountText.VerticalAlignment = VerticalAlignment.Center;
    DockPanel.SetDock(_overviewSelectionCountText, Dock.Left);
    selDock.Children.Add(_overviewSelectionCountText);

    _overviewSelectionBar.Background = new SolidColorBrush(Color.FromArgb(200, 26, 32, 46));
    _overviewSelectionBar.BorderBrush = SafariTheme.Accent;
    _overviewSelectionBar.BorderThickness = new Thickness(1);
    _overviewSelectionBar.CornerRadius = new CornerRadius(18);
    _overviewSelectionBar.Padding = new Thickness(14, 6, 8, 6);
    _overviewSelectionBar.Child = selDock;
    _overviewSelectionBar.Visibility = Visibility.Collapsed;

    headerHost.Children.Add(_overviewNormalHeader);
    headerHost.Children.Add(_overviewSelectionBar);

    Grid.SetRow(headerHost, 0);
    _overviewLayer.Children.Add(headerHost);

    _overviewCanvas.Background = Brushes.Transparent;
    _overviewCanvas.HorizontalAlignment = HorizontalAlignment.Left;
    _overviewCanvas.VerticalAlignment = VerticalAlignment.Top;
    _overviewScroll.VerticalScrollBarVisibility = ScrollBarVisibility.Auto;
    _overviewScroll.HorizontalScrollBarVisibility = ScrollBarVisibility.Disabled;
    _overviewScroll.Content = _overviewCanvas;
    _overviewScroll.SizeChanged += (_, _) =>
    {
      if (_showingOverview && _overviewLiftedCard is null)
        LayoutOverviewCards(animate: false);
    };
    Grid.SetRow(_overviewScroll, 1);
    _overviewLayer.Children.Add(_overviewScroll);
  }

  private void BuildFindPopup()
  {
    _findInput = new TextBox
    {
      Width = 200,
      Background = Brushes.Transparent,
      BorderThickness = new Thickness(0),
      Foreground = SafariTheme.TextPrimary,
      CaretBrush = SafariTheme.Accent,
      FontSize = 12.5,
      VerticalContentAlignment = VerticalAlignment.Center,
    };
    _findInput.KeyDown += async (_, e) =>
    {
      if (e.Key == Key.Enter)
      {
        e.Handled = true;
        await FindInActiveTabAsync(_findInput.Text, backwards: Keyboard.Modifiers.HasFlag(ModifierKeys.Shift));
      }
      else if (e.Key == Key.Escape)
      {
        e.Handled = true;
        _findPopup.IsOpen = false;
      }
    };
    var row = new StackPanel { Orientation = Orientation.Horizontal, VerticalAlignment = VerticalAlignment.Center };
    row.Children.Add(SafariIcons.Create(SafariIcon.Search, 13.5, SafariTheme.TextMuted));
    _findInput.Margin = new Thickness(6, 0, 6, 0);
    row.Children.Add(_findInput);
    row.Children.Add(SafariTheme.CreateGlassButton(SafariIcons.Create(SafariIcon.ChevronUp, 12.5), () => _ = FindInActiveTabAsync(_findInput.Text, true), "이전 찾기", 26, 26, 13));
    row.Children.Add(SafariTheme.CreateGlassButton(SafariIcons.Create(SafariIcon.ChevronDown, 12.5), () => _ = FindInActiveTabAsync(_findInput.Text, false), "다음 찾기", 26, 26, 13));
    row.Children.Add(SafariTheme.CreateGlassButton(SafariIcons.Create(SafariIcon.Close, 11), () => _findPopup.IsOpen = false, "닫기 (Esc)", 26, 26, 13));

    _findPopup.PlacementTarget = _contentHost;
    _findPopup.Child = SafariTheme.CreatePopoverShell(row, 260, 360);
  }

  private void BuildStatusBar()
  {
    _statusBarHost.Background = new SolidColorBrush(Color.FromArgb(244, 18, 20, 26));
    _statusBarHost.BorderBrush = SafariTheme.Divider;
    _statusBarHost.BorderThickness = new Thickness(0, 1, 0, 0);
    _statusBarHost.Padding = new Thickness(14, 0, 14, 0);
    _statusBarHost.Height = 25;

    var dock = new DockPanel { VerticalAlignment = VerticalAlignment.Center };

    _statusSyncText.FontSize = 11;
    _statusSyncText.Foreground = SafariTheme.TextSecondary;
    _statusSyncText.VerticalAlignment = VerticalAlignment.Center;
    _statusSyncPill.CornerRadius = new CornerRadius(9);
    _statusSyncPill.Padding = new Thickness(8, 2, 8, 2);
    _statusSyncPill.Cursor = Cursors.Hand;
    _statusSyncPill.Child = _statusSyncText;
    _statusSyncPill.MouseLeftButtonUp += async (_, e) =>
    {
      e.Handled = true;
      await ShowSyncDialogAsync();
    };
    DockPanel.SetDock(_statusSyncPill, Dock.Right);
    dock.Children.Add(_statusSyncPill);

    _statusText.FontSize = 11;
    _statusText.Foreground = SafariTheme.TextMuted;
    _statusText.VerticalAlignment = VerticalAlignment.Center;
    _statusText.TextTrimming = TextTrimming.CharacterEllipsis;
    dock.Children.Add(_statusText);

    _statusBarHost.Child = dock;
  }

  private void UpdateWindowChromeState()
  {
    var maximized = WindowState == WindowState.Maximized;
    _maximizeIconHost.Content = SafariIcons.Create(
        maximized ? SafariIcon.WindowRestore : SafariIcon.WindowMaximize,
        13,
        SafariTheme.TextSecondary);
    _rootGrid.Margin = maximized ? new Thickness(7) : new Thickness(0);
  }

  private bool ShouldShowTabStrip()
  {
    if (_showingOverview) return false;
    if (_preferences.TabsAboveAddressBar) return true;
    var tabs = VisibleTabs();
    return !(tabs.Count == 1 && tabs[0].Url == "about:blank");
  }

  private void ApplyChromePreferences()
  {
    var showFavorites = !_showingOverview &&
        _preferences.ShowFavoritesBar &&
        (_bookmarks.Items.Count > 0 || _bookmarks.Folders.Count > 0);
    var showTabStrip = ShouldShowTabStrip();

    if (_showingOverview)
    {
      // When Tab List (탭 목록) is open, hide both the bookmark bar and tab strip so only the URL toolbar row stays visible.
      Grid.SetRow(_toolbarHost, 0);
      Grid.SetRow(_favoritesBarHost, 1);
      Grid.SetRow(_tabStripHost, 2);
      _toolbarHost.Padding = new Thickness(12, 8, 10, 7);
      _toolbarHost.BorderBrush = SafariTheme.Divider;
      _toolbarHost.BorderThickness = new Thickness(0, 0, 0, 1);
    }
    else if (_preferences.TabsAboveAddressBar)
    {
      Grid.SetRow(_tabStripHost, 0);
      Grid.SetRow(_toolbarHost, 1);
      Grid.SetRow(_favoritesBarHost, 2);

      _tabStripHost.Padding = new Thickness(12, 6, 12, 4);
      _tabStripHost.BorderThickness = new Thickness(0);

      _toolbarHost.Padding = new Thickness(12, 4, 10, 7);
      _toolbarHost.BorderBrush = SafariTheme.Divider;
      _toolbarHost.BorderThickness = new Thickness(0, 0, 0, showFavorites ? 0 : 1);

      _favoritesBarHost.BorderBrush = SafariTheme.Divider;
      _favoritesBarHost.BorderThickness = new Thickness(0, 0, 0, 1);
    }
    else
    {
      Grid.SetRow(_toolbarHost, 0);
      Grid.SetRow(_favoritesBarHost, 1);
      Grid.SetRow(_tabStripHost, 2);

      _toolbarHost.Padding = new Thickness(12, 8, 10, 7);
      _toolbarHost.BorderBrush = SafariTheme.Divider;
      _toolbarHost.BorderThickness = new Thickness(0, 0, 0, !showFavorites && !showTabStrip ? 1 : 0);

      _favoritesBarHost.BorderBrush = SafariTheme.Divider;
      _favoritesBarHost.BorderThickness = new Thickness(0, 0, 0, !showTabStrip ? 1 : 0);

      _tabStripHost.Padding = new Thickness(10, 2, 10, 6);
      _tabStripHost.BorderBrush = SafariTheme.Divider;
      _tabStripHost.BorderThickness = new Thickness(0, 0, 0, 1);
    }

    _favoritesBarHost.Visibility = showFavorites ? Visibility.Visible : Visibility.Collapsed;
    _tabStripHost.Visibility = showTabStrip ? Visibility.Visible : Visibility.Collapsed;
    _statusBarHost.Visibility = _preferences.ShowStatusBar ? Visibility.Visible : Visibility.Collapsed;

    if (WindowChrome.GetWindowChrome(this) is { } chrome)
      chrome.CaptionHeight = !_showingOverview && _preferences.TabsAboveAddressBar ? 40 : 46;
  }

  // Smooth GPU Sidebar Slide Animation (0 <-> 268px, 220ms CubicEase Out)
  internal void ToggleSidebar()
  {
    _sidebarOpen = !_sidebarOpen;
    if (_sidebarOpen) PopulateSidebar();
    SafariTheme.AnimateDouble(_sidebarHost, FrameworkElement.WidthProperty, _sidebarOpen ? SidebarExpandedWidth : 0, 220);
  }

  // Smooth GPU Tab List (탭 목록) Morph (Scale + Opacity, 210ms CubicEase Out)
  internal void ToggleTabOverview()
  {
    CancelOverviewInteraction();
    _overviewSelectionMode = false;
    _overviewSelectedIds.Clear();
    UpdateOverviewSelectionHeader();
    _showingOverview = !_showingOverview;
    ApplyChromePreferences();
    if (_showingOverview)
    {
      _webViewLayer.Visibility = Visibility.Collapsed;
      _startPageLayer.Visibility = Visibility.Collapsed;
      _overviewLayer.Visibility = Visibility.Visible;
      PopulateTabOverview();
      Dispatcher.BeginInvoke(() =>
      {
        if (_showingOverview && _overviewLiftedCard is null)
          LayoutOverviewCards(animate: false);
      }, DispatcherPriority.Loaded);
      _overviewScale.ScaleX = 0.94;
      _overviewScale.ScaleY = 0.94;
      _overviewLayer.Opacity = 0;
      SafariTheme.AnimateScale(_overviewScale, 1.0, 210);
      SafariTheme.AnimateDouble(_overviewLayer, OpacityProperty, 1.0, 190);
    }
    else
    {
      var fadeOut = new DoubleAnimation(0, TimeSpan.FromMilliseconds(150))
      {
        EasingFunction = new CubicEase { EasingMode = EasingMode.EaseIn },
      };
      fadeOut.Completed += (_, _) =>
      {
        if (!_showingOverview)
        {
          _overviewLayer.Visibility = Visibility.Collapsed;
          ApplySurfaceMode();
        }
      };
      SafariTheme.AnimateScale(_overviewScale, 0.96, 150);
      _overviewLayer.BeginAnimation(OpacityProperty, fadeOut);
    }
  }

  private void RefreshWorkspaceUi()
  {
    UpdateProfileBadge();
    PopulateFavoritesBar();
    PopulateTabStrip();
    if (_sidebarOpen) PopulateSidebar();
    if (_showingOverview) PopulateTabOverview();
    UpdateBookmarkStarState();
    UpdateNavigationButtons();
    UpdateStatusText();
    ApplyChromePreferences();
    ApplySurfaceMode();
  }

  private void UpdateProfileBadge()
  {
    var profile = ActiveProfile();
    var color = SafariTheme.FromArgbUint(profile.Color);
    var tinted = Color.FromArgb(205, color.R, color.G, color.B);
    SafariTheme.AnimateColor(_profileBadgeBgBrush, tinted, 200);
    var groupSuffix = _state.ActiveGroupId == PrivateGroupId
        ? " · 비공개"
        : _state.ActiveGroupId is not null
            ? " · " + ActiveGroupName()
            : string.Empty;
    _profileBadgeLabel.Text = profile.Name + groupSuffix;
    _sidebarProfileHeader.Text = $"{profile.Name} · {VisibleTabs().Count}개 탭";
  }

  private static readonly SolidColorBrush BookmarkFolderGold = SafariTheme.Frozen(Color.FromRgb(244, 196, 48));

  private void PopulateFavoritesBar()
  {
    _favoritesBarPanel.Children.Clear();

    // 1. Root-level Bookmark Folders (clicking opens nested Liquid Glass folder menu)
    foreach (var folder in _bookmarks.Folders.Where(f => f.ParentId is null).OrderBy(f => f.Order).Take(12))
    {
      var folderItem = folder;
      var row = new StackPanel { Orientation = Orientation.Horizontal, VerticalAlignment = VerticalAlignment.Center };
      row.Children.Add(SafariIcons.Create(SafariIcon.FolderFilled, 14, BookmarkFolderGold));
      row.Children.Add(new TextBlock
      {
        Text = folderItem.Name,
        FontSize = 11.5,
        Foreground = SafariTheme.TextSecondary,
        MaxWidth = 120,
        TextTrimming = TextTrimming.CharacterEllipsis,
        VerticalAlignment = VerticalAlignment.Center,
        Margin = new Thickness(6, 0, 3, 0),
      });
      row.Children.Add(SafariIcons.Create(SafariIcon.ChevronDown, 9.5, SafariTheme.TextMuted, 1.9));
      Border? chip = null;
      chip = SafariTheme.CreateGlassButton(row, () => ShowBookmarkFolderPopup(chip!, folderItem), $"{folderItem.Name} 폴더 열기", double.NaN, 24, 12);
      chip.Padding = new Thickness(8, 0, 7, 0);
      chip.Margin = new Thickness(2, 0, 2, 0);
      chip.MouseRightButtonUp += (_, e) =>
      {
        e.Handled = true;
        ShowBookmarkManagerWindow();
      };
      _favoritesBarPanel.Children.Add(chip);
    }

    // 2. Root-level Bookmarks
    var rootBookmarks = _bookmarks.Items.Where(b => b.FolderId is null).ToList();
    foreach (var fav in rootBookmarks.Take(24))
    {
      var row = new StackPanel { Orientation = Orientation.Horizontal, VerticalAlignment = VerticalAlignment.Center };
      var iconSrc = LoadFaviconSource(fav);
      if (iconSrc is not null)
      {
        row.Children.Add(new Image
        {
          Source = iconSrc,
          Width = 14,
          Height = 14,
          Margin = new Thickness(0, 0, _preferences.BookmarkIconsOnly ? 0 : 6, 0),
          VerticalAlignment = VerticalAlignment.Center,
        });
      }
      if (!_preferences.BookmarkIconsOnly)
      {
        row.Children.Add(new TextBlock
        {
          Text = fav.Name,
          FontSize = 11.5,
          Foreground = SafariTheme.TextSecondary,
          MaxWidth = 130,
          TextTrimming = TextTrimming.CharacterEllipsis,
          VerticalAlignment = VerticalAlignment.Center,
        });
      }
      var chip = SafariTheme.CreateGlassButton(row, () => _ = NavigateToAsync(fav.Url), $"{fav.Name}\n{fav.Url}", double.NaN, 24, 12);
      chip.Padding = new Thickness(8, 0, 8, 0);
      chip.Margin = new Thickness(2, 0, 2, 0);
      chip.MouseRightButtonUp += (_, e) =>
      {
        e.Handled = true;
        ShowBookmarkItemPopup(chip, fav);
      };
      _favoritesBarPanel.Children.Add(chip);
    }

    // Quick Bookmark Manager button at the trailing edge of the Bookmark Bar
    var mgrRow = new StackPanel { Orientation = Orientation.Horizontal, VerticalAlignment = VerticalAlignment.Center };
    mgrRow.Children.Add(SafariIcons.Create(SafariIcon.Folder, 12.5, SafariTheme.TextMuted, 1.55));
    mgrRow.Children.Add(new TextBlock
    {
      Text = "관리",
      FontSize = 11,
      Foreground = SafariTheme.TextMuted,
      VerticalAlignment = VerticalAlignment.Center,
      Margin = new Thickness(4, 0, 0, 0),
    });
    var mgrChip = SafariTheme.CreateGlassButton(mgrRow, ShowBookmarkManagerWindow, "북마크 관리자 열기 (Ctrl+Shift+B)", double.NaN, 24, 12);
    mgrChip.Padding = new Thickness(7, 0, 8, 0);
    mgrChip.Margin = new Thickness(6, 0, 2, 0);
    _favoritesBarPanel.Children.Add(mgrChip);

    _favoritesBarHost.Visibility = !_showingOverview &&
        _preferences.ShowFavoritesBar &&
        (_bookmarks.Items.Count > 0 || _bookmarks.Folders.Count > 0)
        ? Visibility.Visible
        : Visibility.Collapsed;
  }

  private void PopulateTabStrip()
  {
    _activeStripDrag = null;
    _stripLogicalOrder = null;
    _stripDragMoved = false;
    _stripDragReordered = false;
    _tabStripItems.Clear();
    _tabStripPanel.Children.Clear();

    var tabs = VisibleTabs();
    if (!ShouldShowTabStrip())
    {
      _tabStripHost.Visibility = Visibility.Collapsed;
      return;
    }
    _tabStripHost.Visibility = Visibility.Visible;
    Border? activePill = null;

    foreach (var tab in tabs)
    {
      var currentTab = tab;
      var isActive = currentTab.Id == _state.ActiveTabId;
      var idleColor = isActive ? SafariTheme.ActiveTabFillColor : Color.FromArgb(0, 44, 47, 57);
      var hoverColor = isActive ? SafariTheme.CapsuleHoverColor : SafariTheme.InactiveTabHoverColor;
      var bg = new SolidColorBrush(idleColor);
      var translate = new TranslateTransform(0, 0);

      var inner = new Grid();
      inner.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
      inner.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
      inner.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });

      var leadingIcons = new StackPanel { Orientation = Orientation.Horizontal, VerticalAlignment = VerticalAlignment.Center };
      if (currentTab.IsPinned)
      {
        var pinBadge = SafariIcons.Create(SafariIcon.Pin, 11, SafariTheme.Accent, 1.8);
        if (pinBadge is FrameworkElement fePin) fePin.Margin = new Thickness(0, 0, 5, 0);
        leadingIcons.Children.Add(pinBadge);
      }

      if (_tabFavicons.TryGetValue(currentTab.Id, out var favImg))
      {
        leadingIcons.Children.Add(new Image { Source = favImg, Width = 14, Height = 14, Margin = new Thickness(0, 0, 7, 0), VerticalAlignment = VerticalAlignment.Center });
      }
      else
      {
        leadingIcons.Children.Add(new Border
        {
          Width = 8,
          Height = 8,
          CornerRadius = new CornerRadius(4),
          Background = isActive ? SafariTheme.Accent : SafariTheme.TextMuted,
          Margin = new Thickness(2, 0, 8, 0),
          VerticalAlignment = VerticalAlignment.Center,
        });
      }
      Grid.SetColumn(leadingIcons, 0);
      inner.Children.Add(leadingIcons);

      var titleText = new TextBlock
      {
        Text = string.IsNullOrWhiteSpace(currentTab.Title) ? "새 탭" : currentTab.Title,
        FontSize = 12,
        FontWeight = isActive ? FontWeights.SemiBold : FontWeights.Normal,
        Foreground = isActive ? SafariTheme.TextPrimary : SafariTheme.TextSecondary,
        TextTrimming = TextTrimming.CharacterEllipsis,
        VerticalAlignment = VerticalAlignment.Center,
      };
      Grid.SetColumn(titleText, 1);
      inner.Children.Add(titleText);

      var closeBtn = SafariTheme.CreateGlassButton(
          SafariIcons.Create(SafariIcon.Close, 9.5, SafariTheme.TextSecondary, 1.9),
          () => _ = CloseTabAsync(currentTab.Id),
          "탭 닫기 (Ctrl+W)",
          18, 18, 9);
      closeBtn.Opacity = isActive && !currentTab.IsPinned ? 0.85 : 0;
      closeBtn.Margin = new Thickness(4, 0, 0, 0);
      Grid.SetColumn(closeBtn, 2);
      inner.Children.Add(closeBtn);

      var pill = new Border
      {
        MinWidth = currentTab.IsPinned ? 94 : 124,
        MaxWidth = currentTab.IsPinned ? 156 : 228,
        Height = 30,
        CornerRadius = new CornerRadius(11),
        Background = bg,
        BorderBrush = isActive ? SafariTheme.SpecularBorder : Brushes.Transparent,
        BorderThickness = new Thickness(1),
        Padding = new Thickness(10, 0, 6, 0),
        Margin = new Thickness(2, 0, 2, 0),
        Cursor = Cursors.Hand,
        RenderTransform = translate,
        ToolTip = currentTab.IsPinned ? $"[고정됨] {currentTab.Title}\n{currentTab.Url}" : $"{currentTab.Title}\n{currentTab.Url}",
        Child = inner,
      };
      WindowChrome.SetIsHitTestVisibleInChrome(pill, true);
      if (isActive)
      {
        pill.Effect = SafariTheme.GlassShadow(10, 0.25, 2);
        activePill = pill;
      }

      var entry = new TabStripDragItem
      {
        Tab = currentTab,
        Pill = pill,
        Translate = translate,
        IsActive = isActive,
      };
      _tabStripItems.Add(entry);

      pill.MouseEnter += (_, _) =>
      {
        if (_activeStripDrag is not null) return;
        SafariTheme.AnimateColor(bg, hoverColor, 100);
        SafariTheme.AnimateDouble(closeBtn, OpacityProperty, 0.95, 100);
      };
      pill.MouseLeave += (_, _) =>
      {
        if (_activeStripDrag is not null) return;
        SafariTheme.AnimateColor(bg, idleColor, 140);
        if (!isActive || currentTab.IsPinned) SafariTheme.AnimateDouble(closeBtn, OpacityProperty, 0, 120);
      };
      pill.MouseLeftButtonDown += (_, e) =>
      {
        if (IsDescendantOf(e.OriginalSource, closeBtn)) return;
        e.Handled = true;
        _activeStripDrag = entry;
        _stripDragMoved = false;
        _stripDragReordered = false;
        _stripPressPanelPos = e.GetPosition(_tabStripPanel);

        double x = 0;
        foreach (var item in _tabStripItems)
        {
          item.HomeLeft = x;
          item.SlotWidth = Math.Max(1, item.Pill.ActualWidth + item.Pill.Margin.Left + item.Pill.Margin.Right);
          item.TargetTranslateX = 0;
          x += item.SlotWidth;
        }
        _stripGrabOffsetX = _stripPressPanelPos.X - entry.HomeLeft;
        _stripLogicalOrder = new List<TabStripDragItem>(_tabStripItems);
        pill.CaptureMouse();
      };
      pill.MouseMove += (_, e) =>
      {
        if (!ReferenceEquals(_activeStripDrag, entry) || e.LeftButton != MouseButtonState.Pressed || _stripLogicalOrder is null)
          return;

        var curPos = e.GetPosition(_tabStripPanel);
        if (!_stripDragMoved)
        {
          if (Math.Abs(curPos.X - _stripPressPanelPos.X) < 5) return;
          _stripDragMoved = true;
          Panel.SetZIndex(pill, 100);
          pill.Effect = SafariTheme.GlassShadow(16, 0.44, 4);
          pill.BorderBrush = SafariTheme.Accent;
          SafariTheme.AnimateColor(bg, isActive ? SafariTheme.CapsuleHoverColor : SafariTheme.ActiveTabFillColor, 110);
        }

        // Compute group bounds (pinned tabs stay in pinned zone, unpinned stay in unpinned zone)
        var pinnedCount = _stripLogicalOrder.Count(i => i.Tab.IsPinned);
        var minIdx = currentTab.IsPinned ? 0 : pinnedCount;
        var maxIdx = currentTab.IsPinned ? pinnedCount - 1 : _stripLogicalOrder.Count - 1;
        if (maxIdx < minIdx) return;

        double minLeft = 0;
        for (var i = 0; i < minIdx; i++) minLeft += _stripLogicalOrder[i].SlotWidth;
        double maxRight = minLeft;
        for (var i = minIdx; i <= maxIdx; i++) maxRight += _stripLogicalOrder[i].SlotWidth;

        var desiredLeft = Math.Clamp(curPos.X - _stripGrabOffsetX, minLeft, Math.Max(minLeft, maxRight - entry.SlotWidth));
        translate.BeginAnimation(TranslateTransform.XProperty, null);
        translate.X = desiredLeft - entry.HomeLeft;

        // Live animated neighbor swap using exact midpoint + 6px hysteresis deadband
        var draggedCenterX = desiredLeft + entry.SlotWidth * 0.5;
        var swapped = false;
        while (true)
        {
          var curIdx = _stripLogicalOrder.IndexOf(entry);
          if (curIdx < 0) break;
          double slotLeft = 0;
          for (var i = 0; i < curIdx; i++) slotLeft += _stripLogicalOrder[i].SlotWidth;

          if (curIdx < maxIdx)
          {
            var rightNeighbor = _stripLogicalOrder[curIdx + 1];
            var rightThreshold = slotLeft + (entry.SlotWidth + rightNeighbor.SlotWidth) * 0.5 + 6.0;
            if (draggedCenterX > rightThreshold)
            {
              _stripLogicalOrder[curIdx] = rightNeighbor;
              _stripLogicalOrder[curIdx + 1] = entry;
              swapped = true;
              continue;
            }
          }
          if (curIdx > minIdx)
          {
            var leftNeighbor = _stripLogicalOrder[curIdx - 1];
            var leftThreshold = slotLeft + (entry.SlotWidth - leftNeighbor.SlotWidth) * 0.5 - 6.0;
            if (draggedCenterX < leftThreshold)
            {
              _stripLogicalOrder[curIdx] = leftNeighbor;
              _stripLogicalOrder[curIdx - 1] = entry;
              swapped = true;
              continue;
            }
          }
          break;
        }

        if (swapped)
        {
          _stripDragReordered = true;
          AnimateTabStripNeighbors(_stripLogicalOrder, entry);
        }
      };
      pill.MouseLeftButtonUp += async (_, e) =>
      {
        if (!ReferenceEquals(_activeStripDrag, entry)) return;
        e.Handled = true;
        var moved = _stripDragMoved;
        var order = _stripLogicalOrder;
        var reordered = _stripDragReordered;
        _activeStripDrag = null;
        _stripLogicalOrder = null;
        _stripDragMoved = false;
        _stripDragReordered = false;
        if (pill.IsMouseCaptured) pill.ReleaseMouseCapture();
        if (moved)
          FinishTabStripDrag(entry, order, reordered);
        else
          await SelectTabAsync(currentTab.Id);
      };
      pill.LostMouseCapture += (_, _) =>
      {
        if (!ReferenceEquals(_activeStripDrag, entry)) return;
        var moved = _stripDragMoved;
        var order = _stripLogicalOrder;
        var reordered = _stripDragReordered;
        _activeStripDrag = null;
        _stripLogicalOrder = null;
        _stripDragMoved = false;
        _stripDragReordered = false;
        if (moved)
          FinishTabStripDrag(entry, order, reordered);
      };
      pill.MouseDown += async (_, e) =>
      {
        if (e.ChangedButton == MouseButton.Middle)
        {
          e.Handled = true;
          await CloseTabAsync(currentTab.Id);
        }
      };
      pill.MouseRightButtonUp += (_, e) =>
      {
        e.Handled = true;
        ShowTabContextPopup(pill, currentTab);
      };
      _tabStripPanel.Children.Add(pill);
    }

    // Inline subtle "+" button right after the last tab
    var plusTab = SafariTheme.CreateGlassButton(SafariIcons.Create(SafariIcon.Plus, 13.5, SafariTheme.TextSecondary), () => _ = CreateTabAsync(), "새 탭 (Ctrl+T)", 28, 28, 10);
    plusTab.Margin = new Thickness(4, 0, 2, 0);
    WindowChrome.SetIsHitTestVisibleInChrome(plusTab, true);
    _tabStripPanel.Children.Add(plusTab);

    if (activePill is not null)
      Dispatcher.BeginInvoke(() => activePill.BringIntoView(), DispatcherPriority.Loaded);
  }

  private static void AnimateTabStripNeighbors(List<TabStripDragItem> logicalOrder, TabStripDragItem dragged)
  {
    double x = 0;
    foreach (var item in logicalOrder)
    {
      var targetTranslateX = x - item.HomeLeft;
      x += item.SlotWidth;
      if (ReferenceEquals(item, dragged)) continue;
      if (Math.Abs(targetTranslateX - item.TargetTranslateX) > 0.5)
      {
        item.TargetTranslateX = targetTranslateX;
        var anim = new DoubleAnimation(targetTranslateX, TimeSpan.FromMilliseconds(175))
        {
          EasingFunction = new CubicEase { EasingMode = EasingMode.EaseOut },
        };
        item.Translate.BeginAnimation(TranslateTransform.XProperty, anim);
      }
    }
  }

  private void FinishTabStripDrag(TabStripDragItem dragged, List<TabStripDragItem>? order, bool reordered)
  {
    if (order is null)
    {
      PopulateTabStrip();
      return;
    }

    double targetLeft = 0;
    foreach (var item in order)
    {
      if (ReferenceEquals(item, dragged)) break;
      targetLeft += item.SlotWidth;
    }

    var finalTranslateX = targetLeft - dragged.HomeLeft;
    var finalIds = order.Select(i => i.Tab.Id).ToList();
    var settleAnim = new DoubleAnimation(finalTranslateX, TimeSpan.FromMilliseconds(145))
    {
      EasingFunction = new CubicEase { EasingMode = EasingMode.EaseOut },
    };
    settleAnim.Completed += (_, _) =>
    {
      Panel.SetZIndex(dragged.Pill, 0);
      if (reordered) CommitVisibleTabOrder(finalIds);
      PopulateTabStrip();
      if (_sidebarOpen) PopulateSidebar();
    };
    dragged.Translate.BeginAnimation(TranslateTransform.XProperty, settleAnim);
  }

  private void CommitVisibleTabOrder(IReadOnlyList<string> orderedVisibleIds)
  {
    var visibleIndices = new List<int>();
    var visibleById = new Dictionary<string, TabEntry>(StringComparer.Ordinal);
    for (var i = 0; i < _state.Tabs.Count; i++)
    {
      var t = _state.Tabs[i];
      if (t.ProfileId == _state.ActiveProfileId && t.GroupId == _state.ActiveGroupId)
      {
        visibleIndices.Add(i);
        visibleById[t.Id] = t;
      }
    }
    if (visibleIndices.Count != orderedVisibleIds.Count) return;

    var changed = false;
    for (var k = 0; k < orderedVisibleIds.Count; k++)
    {
      if (!visibleById.TryGetValue(orderedVisibleIds[k], out var tab)) return;
      var slotIdx = visibleIndices[k];
      if (_state.Tabs[slotIdx].Id != tab.Id) changed = true;
      _state.Tabs[slotIdx] = tab;
    }
    if (!changed) return;

    for (var i = 0; i < _state.Tabs.Count; i++)
      _state.Tabs[i] = _state.Tabs[i] with { Order = i };
    MarkLocalChange();
  }

  private void TogglePinTab(string tabId)
  {
    var idx = _state.Tabs.FindIndex(t => t.Id == tabId);
    if (idx < 0) return;
    _state.Tabs[idx] = _state.Tabs[idx] with { IsPinned = !_state.Tabs[idx].IsPinned };
    var visible = VisibleTabs();
    CommitVisibleTabOrder(visible.Select(t => t.Id).ToList());
    MarkLocalChange();
    RefreshWorkspaceUi();
  }

  private static bool IsDescendantOf(object? source, DependencyObject ancestor)
  {
    var cur = source as DependencyObject;
    while (cur is not null)
    {
      if (ReferenceEquals(cur, ancestor)) return true;
      cur = cur is Visual or System.Windows.Media.Media3D.Visual3D
          ? VisualTreeHelper.GetParent(cur)
          : LogicalTreeHelper.GetParent(cur);
    }
    return false;
  }

  private void PopulateSidebar()
  {
    _sidebarContent.Children.Clear();
    var query = _sidebarSearchBox.Text.Trim();
    var profile = ActiveProfile();
    var tabs = VisibleTabs();
    _sidebarProfileHeader.Text = $"{profile.Name} · {tabs.Count}개 탭";

    void AddSectionTitle(string text)
    {
      _sidebarContent.Children.Add(new TextBlock
      {
        Text = text,
        FontSize = 11,
        FontWeight = FontWeights.SemiBold,
        Foreground = SafariTheme.TextMuted,
        Margin = new Thickness(8, 10, 8, 4),
      });
    }

    // 1. Open Tabs in Active Group
    var filteredTabs = string.IsNullOrEmpty(query)
        ? tabs
        : tabs.Where(t => t.Title.Contains(query, StringComparison.OrdinalIgnoreCase) || t.Url.Contains(query, StringComparison.OrdinalIgnoreCase)).ToList();
    AddSectionTitle($"열린 탭 ({filteredTabs.Count})");
    foreach (var tab in filteredTabs)
    {
      var isActive = tab.Id == _state.ActiveTabId;
      var row = SafariTheme.CreatePopoverRow(
          SafariIcon.Globe,
          string.IsNullOrWhiteSpace(tab.Title) ? "새 탭" : tab.Title,
          () => _ = SelectTabAsync(tab.Id),
          isSelected: isActive);
      row.ToolTip = tab.Url;
      row.MouseRightButtonUp += (_, e) => { e.Handled = true; ShowTabContextPopup(row, tab); };
      _sidebarContent.Children.Add(row);
    }

    // 2. Tab Groups
    AddSectionTitle("탭 그룹");
    var defaultCount = _state.Tabs.Count(t => t.ProfileId == _state.ActiveProfileId && t.GroupId is null);
    _sidebarContent.Children.Add(SafariTheme.CreatePopoverRow(
        SafariIcon.TabOverview, $"기본 탭 ({defaultCount})",
        () => SwitchTabGroup(null),
        isSelected: _state.ActiveGroupId is null));

    var privateCount = _state.Tabs.Count(t => t.ProfileId == _state.ActiveProfileId && t.GroupId == PrivateGroupId);
    _sidebarContent.Children.Add(SafariTheme.CreatePopoverRow(
        SafariIcon.Private, $"비공개 브라우징 ({privateCount})",
        () => SwitchTabGroup(PrivateGroupId),
        isSelected: _state.ActiveGroupId == PrivateGroupId));

    foreach (var group in _state.Groups.Where(g => g.ProfileId == _state.ActiveProfileId && g.Id != PrivateGroupId))
    {
      if (!string.IsNullOrEmpty(query) && !group.Name.Contains(query, StringComparison.OrdinalIgnoreCase)) continue;
      var cnt = _state.Tabs.Count(t => t.ProfileId == _state.ActiveProfileId && t.GroupId == group.Id);
      var groupRow = SafariTheme.CreatePopoverRow(
          SafariIcon.TabGroup, $"{group.Name} ({cnt})",
          () => SwitchTabGroup(group.Id),
          isSelected: _state.ActiveGroupId == group.Id);
      groupRow.MouseRightButtonUp += (_, e) => { e.Handled = true; ShowGroupContextPopup(groupRow, group); };
      _sidebarContent.Children.Add(groupRow);
    }

    // 3. Bookmarks (Folders + Items + Quick Manager entry)
    var filteredBookmarks = string.IsNullOrEmpty(query)
        ? _bookmarks.Items.Where(b => b.FolderId is null).Take(12).ToList()
        : _bookmarks.Items.Where(b => b.Name.Contains(query, StringComparison.OrdinalIgnoreCase) || b.Url.Contains(query, StringComparison.OrdinalIgnoreCase)).Take(20).ToList();
    AddSectionTitle($"북마크 ({_bookmarks.Items.Count})");
    _sidebarContent.Children.Add(SafariTheme.CreatePopoverRow(SafariIcon.Folder, "북마크 관리자 열기…", ShowBookmarkManagerWindow, "Ctrl+Shift+B"));
    if (string.IsNullOrEmpty(query))
    {
      foreach (var folder in _bookmarks.Folders.Where(f => f.ParentId is null).OrderBy(f => f.Order).Take(8))
      {
        var cnt = BookmarkStore.RecursiveBookmarkCount(_bookmarks, folder.Id);
        var fItem = folder;
        Border? fRow = null;
        fRow = SafariTheme.CreatePopoverRow(SafariIcon.FolderFilled, $"{fItem.Name} ({cnt})", () => ShowBookmarkFolderPopup(fRow!, fItem));
        _sidebarContent.Children.Add(fRow);
      }
    }
    foreach (var bm in filteredBookmarks)
    {
      _sidebarContent.Children.Add(SafariTheme.CreatePopoverRow(SafariIcon.StarOutline, bm.Name, () => _ = NavigateToAsync(bm.Url)));
    }

    // 4. Recent History
    var history = BrowserHistory.Load();
    var filteredHistory = string.IsNullOrEmpty(query)
        ? history.Take(10).ToList()
        : history.Where(h => h.Title.Contains(query, StringComparison.OrdinalIgnoreCase) || h.Url.Contains(query, StringComparison.OrdinalIgnoreCase)).Take(15).ToList();
    if (filteredHistory.Count > 0)
    {
      AddSectionTitle("최근 방문 기록");
      foreach (var item in filteredHistory)
      {
        _sidebarContent.Children.Add(SafariTheme.CreatePopoverRow(SafariIcon.HistoryClock, string.IsNullOrWhiteSpace(item.Title) ? HostName(item.Url) : item.Title, () => _ = NavigateToAsync(item.Url)));
      }
    }
  }

  private void PopulateStartPage()
  {
    var profile = ActiveProfile();
    var pColor = SafariTheme.FromArgbUint(profile.Color);
    _startPageLayer.Background = new RadialGradientBrush(
        Color.FromArgb(62, pColor.R, pColor.G, pColor.B),
        Color.FromRgb(15, 16, 21))
    {
      GradientOrigin = new Point(0.5, 0.08),
      Center = new Point(0.5, 0.1),
      RadiusX = 0.85,
      RadiusY = 0.75,
    };

    var container = new StackPanel
    {
      MaxWidth = 760,
      HorizontalAlignment = HorizontalAlignment.Center,
      Margin = new Thickness(32, 48, 32, 64),
    };

    // Greeting Header
    container.Children.Add(new TextBlock
    {
      Text = _state.ActiveGroupId == PrivateGroupId ? "비공개 브라우징" : $"{profile.Name} 시작 페이지",
      FontSize = 30,
      FontWeight = FontWeights.Bold,
      Foreground = SafariTheme.TextPrimary,
      Margin = new Thickness(4, 0, 0, 24),
    });

    // Section 1: Bookmarks Grid (macOS 27 Safari rounded-square tiles + Folder Manager button)
    var bmHeader = new DockPanel { Margin = new Thickness(4, 0, 4, 12) };
    var openBmMgrRow = new StackPanel { Orientation = Orientation.Horizontal, VerticalAlignment = VerticalAlignment.Center, Margin = new Thickness(8, 0, 6, 0) };
    openBmMgrRow.Children.Add(SafariIcons.Create(SafariIcon.Folder, 13, SafariTheme.TextSecondary, 1.55));
    openBmMgrRow.Children.Add(new TextBlock { Text = "북마크 관리자", FontSize = 11.5, Foreground = SafariTheme.TextSecondary, Margin = new Thickness(5, 0, 2, 0), VerticalAlignment = VerticalAlignment.Center });
    openBmMgrRow.Children.Add(SafariIcons.Create(SafariIcon.ChevronRight, 11, SafariTheme.TextMuted, 1.8));
    var openBmMgrBtn = SafariTheme.CreateGlassButton(openBmMgrRow, ShowBookmarkManagerWindow, "북마크 관리자 열기 (Ctrl+Shift+B)", double.NaN, 26, 13, transparentIdle: false);
    openBmMgrBtn.BorderBrush = SafariTheme.CapsuleBorder;
    openBmMgrBtn.BorderThickness = new Thickness(1);
    DockPanel.SetDock(openBmMgrBtn, Dock.Right);
    bmHeader.Children.Add(openBmMgrBtn);
    bmHeader.Children.Add(new TextBlock
    {
      Text = "북마크",
      FontSize = 15,
      FontWeight = FontWeights.SemiBold,
      Foreground = SafariTheme.TextSecondary,
      VerticalAlignment = VerticalAlignment.Center,
    });
    container.Children.Add(bmHeader);

    var favWrap = new WrapPanel { Margin = new Thickness(0, 0, 0, 28) };
    foreach (var folder in _bookmarks.Folders.Where(f => f.ParentId is null).OrderBy(f => f.Order).Take(6))
    {
      favWrap.Children.Add(CreateStartPageFolderTile(folder));
    }
    foreach (var fav in _bookmarks.Items.Where(b => b.FolderId is null).Take(16))
    {
      favWrap.Children.Add(CreateStartPageTile(fav));
    }
    favWrap.Children.Add(CreateAddFavoriteTile());
    container.Children.Add(favWrap);

    // Section 2: Quick Tools (2-column Safari Liquid Glass cards)
    container.Children.Add(new TextBlock
    {
      Text = "Omyaru 빠른 도구",
      FontSize = 15,
      FontWeight = FontWeights.SemiBold,
      Foreground = SafariTheme.TextSecondary,
      Margin = new Thickness(4, 0, 0, 12),
    });

    var toolsGrid = new UniformGrid { Columns = 2, Margin = new Thickness(0, 0, 0, 28) };
    toolsGrid.Children.Add(CreateStartPageCard(SafariIcon.Bookshelf, "내장 책장 열기", "원본 이미지 정리 · PDF 내보내기 · Google Drive 동기화", OpenBookshelfWindow));
    toolsGrid.Children.Add(CreateStartPageCard(SafariIcon.Sync, "Google Drive 동기화", GoogleDriveAuthorization.NeedsReauthentication ? "Google 로그인 만료됨 · 클릭해 다시 로그인" : "PC · Android · iOS 탭·북마크·책장 암호화 동기화", () => _ = ShowSyncDialogAsync()));
    toolsGrid.Children.Add(CreateStartPageCard(SafariIcon.Extensions, "확장 프로그램", "Chrome · Firefox · Edge 스토어 확장 설치 및 관리", () => _ = ShowExtensionManagerAsync()));
    toolsGrid.Children.Add(CreateStartPageCard(SafariIcon.WatermarkSparkles, "워터마크 제거", "이미지 워터마크 제거 도구 바로 열기", () => _ = CreateTabAsync("https://www.watermarkremover.io/ko/upload")));
    container.Children.Add(toolsGrid);

    // Section 3: Recent History Cards
    var recent = BrowserHistory.Load().Take(6).ToList();
    if (recent.Count > 0)
    {
      container.Children.Add(new TextBlock
      {
        Text = "최근 방문 기록",
        FontSize = 15,
        FontWeight = FontWeights.SemiBold,
        Foreground = SafariTheme.TextSecondary,
        Margin = new Thickness(4, 0, 0, 12),
      });
      var histGrid = new UniformGrid { Columns = 2 };
      foreach (var item in recent)
      {
        histGrid.Children.Add(CreateStartPageCard(
            SafariIcon.Globe,
            string.IsNullOrWhiteSpace(item.Title) ? HostName(item.Url) : item.Title,
            HostName(item.Url),
            () => _ = NavigateToAsync(item.Url)));
      }
      container.Children.Add(histGrid);
    }

    _startPageLayer.Content = container;
  }

  private UIElement CreateStartPageFolderTile(BookmarkFolderEntry folder)
  {
    var scale = new ScaleTransform(1, 1);
    var bg = new SolidColorBrush(SafariTheme.CardFillColor);
    var count = BookmarkStore.RecursiveBookmarkCount(_bookmarks, folder.Id);
    var box = new Border
    {
      Width = 68,
      Height = 68,
      CornerRadius = new CornerRadius(18),
      Background = bg,
      BorderBrush = SafariTheme.SpecularBorder,
      BorderThickness = new Thickness(1),
      Effect = SafariTheme.GlassShadow(14, 0.35, 4),
      RenderTransformOrigin = new Point(0.5, 0.5),
      RenderTransform = scale,
      Child = SafariIcons.Create(SafariIcon.FolderFilled, 30, BookmarkFolderGold),
    };
    var stack = new StackPanel { Width = 88, Margin = new Thickness(4, 0, 4, 14), Cursor = Cursors.Hand, ToolTip = $"{folder.Name} ({count}개)" };
    stack.Children.Add(box);
    stack.Children.Add(new TextBlock
    {
      Text = folder.Name,
      FontSize = 11.5,
      Foreground = SafariTheme.TextSecondary,
      TextAlignment = TextAlignment.Center,
      TextTrimming = TextTrimming.CharacterEllipsis,
      Margin = new Thickness(2, 7, 2, 0),
    });
    stack.MouseEnter += (_, _) =>
    {
      SafariTheme.AnimateColor(bg, SafariTheme.CardHoverColor, 110);
      SafariTheme.AnimateScale(scale, 1.06, 130);
    };
    stack.MouseLeave += (_, _) =>
    {
      SafariTheme.AnimateColor(bg, SafariTheme.CardFillColor, 150);
      SafariTheme.AnimateScale(scale, 1.0, 150);
    };
    stack.MouseLeftButtonUp += (_, _) => ShowBookmarkFolderPopup(box, folder);
    stack.MouseRightButtonUp += (_, e) => { e.Handled = true; ShowBookmarkManagerWindow(); };
    return stack;
  }

  private UIElement CreateStartPageTile(BookmarkEntry fav)
  {
    var scale = new ScaleTransform(1, 1);
    var bg = new SolidColorBrush(SafariTheme.CardFillColor);
    var iconSrc = LoadFaviconSource(fav);

    UIElement iconContent = iconSrc is not null
        ? new Image { Source = iconSrc, Width = 32, Height = 32, VerticalAlignment = VerticalAlignment.Center, HorizontalAlignment = HorizontalAlignment.Center }
        : new TextBlock
        {
          Text = BookmarkStore.GlyphFor(fav.Name),
          FontSize = 22,
          FontWeight = FontWeights.Bold,
          Foreground = SafariTheme.TextPrimary,
          VerticalAlignment = VerticalAlignment.Center,
          HorizontalAlignment = HorizontalAlignment.Center,
        };

    var box = new Border
    {
      Width = 68,
      Height = 68,
      CornerRadius = new CornerRadius(18),
      Background = bg,
      BorderBrush = SafariTheme.SpecularBorder,
      BorderThickness = new Thickness(1),
      Effect = SafariTheme.GlassShadow(14, 0.35, 4),
      RenderTransformOrigin = new Point(0.5, 0.5),
      RenderTransform = scale,
      Child = iconContent,
    };

    var stack = new StackPanel { Width = 88, Margin = new Thickness(4, 0, 4, 14), Cursor = Cursors.Hand };
    stack.Children.Add(box);
    stack.Children.Add(new TextBlock
    {
      Text = fav.Name,
      FontSize = 11.5,
      Foreground = SafariTheme.TextSecondary,
      TextAlignment = TextAlignment.Center,
      TextTrimming = TextTrimming.CharacterEllipsis,
      Margin = new Thickness(2, 7, 2, 0),
    });

    stack.MouseEnter += (_, _) =>
    {
      SafariTheme.AnimateColor(bg, SafariTheme.CardHoverColor, 110);
      SafariTheme.AnimateScale(scale, 1.06, 130);
    };
    stack.MouseLeave += (_, _) =>
    {
      SafariTheme.AnimateColor(bg, SafariTheme.CardFillColor, 150);
      SafariTheme.AnimateScale(scale, 1.0, 150);
    };
    stack.MouseLeftButtonUp += (_, _) => _ = NavigateToAsync(fav.Url);
    stack.MouseRightButtonUp += (_, e) => { e.Handled = true; ShowBookmarkItemPopup(box, fav); };
    return stack;
  }

  private UIElement CreateAddFavoriteTile()
  {
    var scale = new ScaleTransform(1, 1);
    var bg = new SolidColorBrush(Color.FromArgb(140, 36, 39, 48));
    var box = new Border
    {
      Width = 68,
      Height = 68,
      CornerRadius = new CornerRadius(18),
      Background = bg,
      BorderBrush = SafariTheme.CapsuleBorder,
      BorderThickness = new Thickness(1),
      RenderTransformOrigin = new Point(0.5, 0.5),
      RenderTransform = scale,
      Child = SafariIcons.Create(SafariIcon.Plus, 22, SafariTheme.Accent, 1.85),
    };
    var stack = new StackPanel { Width = 88, Margin = new Thickness(4, 0, 4, 14), Cursor = Cursors.Hand };
    stack.Children.Add(box);
    stack.Children.Add(new TextBlock
    {
      Text = "추가",
      FontSize = 11.5,
      Foreground = SafariTheme.TextMuted,
      TextAlignment = TextAlignment.Center,
      Margin = new Thickness(2, 7, 2, 0),
    });
    stack.MouseEnter += (_, _) =>
    {
      SafariTheme.AnimateColor(bg, SafariTheme.CardHoverColor, 110);
      SafariTheme.AnimateScale(scale, 1.06, 130);
    };
    stack.MouseLeave += (_, _) =>
    {
      SafariTheme.AnimateColor(bg, Color.FromArgb(140, 36, 39, 48), 150);
      SafariTheme.AnimateScale(scale, 1.0, 150);
    };
    stack.MouseLeftButtonUp += (_, _) => PromptAddBookmark();
    return stack;
  }

  private UIElement CreateStartPageCard(SafariIcon icon, string title, string subtitle, Action onClick)
  {
    var bg = new SolidColorBrush(SafariTheme.CardFillColor);
    var scale = new ScaleTransform(1, 1);
    var grid = new Grid();
    grid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(44) });
    grid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });

    var badge = new Border
    {
      Width = 36,
      Height = 36,
      CornerRadius = new CornerRadius(10),
      Background = SafariTheme.AccentSoft,
      Child = SafariIcons.Create(icon, 17, SafariTheme.Accent, 1.65),
      VerticalAlignment = VerticalAlignment.Center,
    };
    Grid.SetColumn(badge, 0);
    grid.Children.Add(badge);

    var textStack = new StackPanel { VerticalAlignment = VerticalAlignment.Center, Margin = new Thickness(8, 0, 0, 0) };
    textStack.Children.Add(new TextBlock { Text = title, FontSize = 13, FontWeight = FontWeights.SemiBold, Foreground = SafariTheme.TextPrimary, TextTrimming = TextTrimming.CharacterEllipsis });
    textStack.Children.Add(new TextBlock { Text = subtitle, FontSize = 11.5, Foreground = SafariTheme.TextMuted, TextTrimming = TextTrimming.CharacterEllipsis, Margin = new Thickness(0, 2, 0, 0) });
    Grid.SetColumn(textStack, 1);
    grid.Children.Add(textStack);

    var card = new Border
    {
      Height = 68,
      Margin = new Thickness(5),
      Padding = new Thickness(12, 10, 14, 10),
      CornerRadius = new CornerRadius(16),
      Background = bg,
      BorderBrush = SafariTheme.CapsuleBorder,
      BorderThickness = new Thickness(1),
      Cursor = Cursors.Hand,
      RenderTransformOrigin = new Point(0.5, 0.5),
      RenderTransform = scale,
      Child = grid,
    };
    card.MouseEnter += (_, _) =>
    {
      SafariTheme.AnimateColor(bg, SafariTheme.CardHoverColor, 110);
      SafariTheme.AnimateScale(scale, 1.015, 130);
    };
    card.MouseLeave += (_, _) =>
    {
      SafariTheme.AnimateColor(bg, SafariTheme.CardFillColor, 150);
      SafariTheme.AnimateScale(scale, 1.0, 150);
    };
    card.MouseLeftButtonUp += (_, _) => onClick();
    return card;
  }

  private void CancelOverviewInteraction()
  {
    _overviewLongPressTimer.Stop();
    if (_overviewPendingCard is { } pending && pending.Card.IsMouseCaptured)
      pending.Card.ReleaseMouseCapture();
    if (_overviewLiftedCard is { } lifted)
      RestoreOverviewCardVisual(lifted);
    _overviewPendingCard = null;
    _overviewLiftedCard = null;
    _overviewDraggedMoved = false;
    _overviewDragChangedOrder = false;
    _overviewPressCancelled = false;
  }

  private (int Cols, double StartX, double CanvasWidth, double CanvasHeight) GetOverviewGridMetrics(int count)
  {
    var availW = _overviewScroll.ViewportWidth > 80
        ? _overviewScroll.ViewportWidth
        : (_overviewScroll.ActualWidth > 80
            ? _overviewScroll.ActualWidth
            : Math.Max(640, ActualWidth - (_sidebarOpen ? SidebarExpandedWidth : 0)));
    var cols = Math.Max(1, (int)Math.Floor((availW - OverviewPadX * 2 + OverviewCardGap) / (OverviewCardWidth + OverviewCardGap)));
    var gridW = cols * OverviewCardWidth + Math.Max(0, cols - 1) * OverviewCardGap;
    var startX = Math.Max(OverviewPadX, (availW - gridW) * 0.5);
    var rows = count == 0 ? 1 : (count + cols - 1) / cols;
    var canvasH = Math.Max(260, OverviewPadY * 2 + rows * OverviewCardHeight + Math.Max(0, rows - 1) * OverviewCardGap + 36);
    var canvasW = Math.Max(availW, startX * 2 + gridW);
    return (cols, startX, canvasW, canvasH);
  }

  private static (double X, double Y) GetOverviewSlot(int index, int cols, double startX)
  {
    var row = index / cols;
    var col = index % cols;
    return (startX + col * (OverviewCardWidth + OverviewCardGap), OverviewPadY + row * (OverviewCardHeight + OverviewCardGap));
  }

  private void LayoutOverviewCards(bool animate, OverviewCardEntry? skipEntry = null)
  {
    var (cols, startX, canvasW, canvasH) = GetOverviewGridMetrics(_overviewCards.Count);
    _overviewCanvas.Width = canvasW;
    _overviewCanvas.Height = canvasH;

    for (var i = 0; i < _overviewCards.Count; i++)
    {
      var entry = _overviewCards[i];
      var (slotX, slotY) = GetOverviewSlot(i, cols, startX);
      if (ReferenceEquals(entry, skipEntry)) continue;

      if (!animate)
      {
        entry.Translate.BeginAnimation(TranslateTransform.XProperty, null);
        entry.Translate.BeginAnimation(TranslateTransform.YProperty, null);
        entry.Translate.X = slotX;
        entry.Translate.Y = slotY;
        entry.TargetX = slotX;
        entry.TargetY = slotY;
      }
      else if (Math.Abs(slotX - entry.TargetX) > 0.5 || Math.Abs(slotY - entry.TargetY) > 0.5)
      {
        entry.TargetX = slotX;
        entry.TargetY = slotY;
        var animX = new DoubleAnimation(slotX, TimeSpan.FromMilliseconds(220))
        {
          EasingFunction = new CubicEase { EasingMode = EasingMode.EaseOut },
        };
        var animY = new DoubleAnimation(slotY, TimeSpan.FromMilliseconds(220))
        {
          EasingFunction = new CubicEase { EasingMode = EasingMode.EaseOut },
        };
        entry.Translate.BeginAnimation(TranslateTransform.XProperty, animX);
        entry.Translate.BeginAnimation(TranslateTransform.YProperty, animY);
      }
    }
  }

  private static void RestoreOverviewCardVisual(OverviewCardEntry entry)
  {
    SafariTheme.AnimateScale(entry.Scale, 1.0, 175);
    SafariTheme.AnimateColor(entry.Background, SafariTheme.CardFillColor, 150);
    entry.Card.BorderBrush = entry.IsActive ? SafariTheme.Accent : SafariTheme.SpecularBorder;
    entry.Card.BorderThickness = new Thickness(entry.IsActive ? 2 : 1);
    entry.Card.Effect = SafariTheme.GlassShadow(18, 0.4, 6);
  }

  private void OnOverviewLongPressTick(object? sender, EventArgs e)
  {
    _overviewLongPressTimer.Stop();
    if (_overviewPendingCard is not { } entry || _overviewPressCancelled) return;
    if (!entry.Card.IsMouseCaptured) return;

    _overviewLiftedCard = entry;
    _overviewDraggedMoved = false;
    _overviewDragChangedOrder = false;

    Panel.SetZIndex(entry.Card, 100);
    entry.Card.BorderBrush = SafariTheme.Accent;
    entry.Card.BorderThickness = new Thickness(2);
    entry.Card.Effect = SafariTheme.GlassShadow(30, 0.62, 14);
    SafariTheme.AnimateScale(entry.Scale, 1.06, 175);
    SafariTheme.AnimateColor(entry.Background, SafariTheme.CardHoverColor, 140);
  }

  private void SettleLiftedOverviewCard(OverviewCardEntry entry, bool moved, bool changedOrder, bool openMenuIfUnmoved)
  {
    RestoreOverviewCardVisual(entry);

    if (moved)
    {
      var (cols, startX, _, _) = GetOverviewGridMetrics(_overviewCards.Count);
      var idx = _overviewCards.IndexOf(entry);
      if (idx >= 0)
      {
        var (slotX, slotY) = GetOverviewSlot(idx, cols, startX);
        entry.TargetX = slotX;
        entry.TargetY = slotY;
        var animX = new DoubleAnimation(slotX, TimeSpan.FromMilliseconds(185))
        {
          EasingFunction = new CubicEase { EasingMode = EasingMode.EaseOut },
        };
        var animY = new DoubleAnimation(slotY, TimeSpan.FromMilliseconds(185))
        {
          EasingFunction = new CubicEase { EasingMode = EasingMode.EaseOut },
        };
        animX.Completed += (_, _) => Panel.SetZIndex(entry.Card, 0);
        entry.Translate.BeginAnimation(TranslateTransform.XProperty, animX);
        entry.Translate.BeginAnimation(TranslateTransform.YProperty, animY);
      }
      else
      {
        Panel.SetZIndex(entry.Card, 0);
      }

      if (changedOrder && string.IsNullOrWhiteSpace(_overviewSearchBox.Text))
      {
        CommitVisibleTabOrder(_overviewCards.Select(c => c.Tab.Id).ToList());
        PopulateTabStrip();
        if (_sidebarOpen) PopulateSidebar();
      }
    }
    else
    {
      Panel.SetZIndex(entry.Card, 0);
      if (openMenuIfUnmoved)
      {
        Dispatcher.BeginInvoke(() =>
        {
          if (_showingOverview && _overviewCards.Contains(entry))
            ShowTabContextPopup(entry.Card, entry.Tab);
        }, DispatcherPriority.Input);
      }
    }
  }

  private void EnterOverviewSelectionMode(string? initialTabId = null)
  {
    if (!_showingOverview) ToggleTabOverview();
    _overviewSelectionMode = true;
    _overviewSelectedIds.Clear();
    if (!string.IsNullOrWhiteSpace(initialTabId))
      _overviewSelectedIds.Add(initialTabId);
    PopulateTabOverview();
  }

  private void ExitOverviewSelectionMode()
  {
    _overviewSelectionMode = false;
    _overviewSelectedIds.Clear();
    UpdateOverviewSelectionHeader();
    if (_showingOverview) PopulateTabOverview();
  }

  private void UpdateOverviewSelectionHeader()
  {
    _overviewNormalHeader.Visibility = _overviewSelectionMode ? Visibility.Collapsed : Visibility.Visible;
    _overviewSelectionBar.Visibility = _overviewSelectionMode ? Visibility.Visible : Visibility.Collapsed;
    _overviewSelectionCountText.Text = _overviewSelectedIds.Count > 0
        ? $"{_overviewSelectedIds.Count}개 탭 선택됨"
        : "이동하거나 닫을 탭을 선택하세요";
  }

  private async Task CloseSelectedOverviewTabsAsync()
  {
    var targets = _overviewSelectedIds.ToList();
    if (targets.Count == 0) return;
    _overviewSelectionMode = false;
    _overviewSelectedIds.Clear();
    UpdateOverviewSelectionHeader();
    foreach (var id in targets)
      await CloseTabAsync(id);
    if (_showingOverview) PopulateTabOverview();
  }

  private void MoveTabsToDestination(IReadOnlyList<string> tabIds, string targetProfileId, string? targetGroupId, string destinationLabel)
  {
    if (tabIds.Count == 0) return;
    var idSet = tabIds.ToHashSet(StringComparer.Ordinal);
    var moved = 0;
    for (var i = 0; i < _state.Tabs.Count; i++)
    {
      var t = _state.Tabs[i];
      if (!idSet.Contains(t.Id)) continue;
      if (t.ProfileId == targetProfileId && t.GroupId == targetGroupId) continue;
      _state.Tabs[i] = t with { ProfileId = targetProfileId, GroupId = targetGroupId };
      moved++;
    }
    if (moved == 0)
    {
      _syncStatus = $"이미 '{destinationLabel}'에 있는 탭입니다.";
      UpdateStatusText();
      return;
    }

    _overviewSelectionMode = false;
    _overviewSelectedIds.Clear();
    UpdateOverviewSelectionHeader();
    EnsureWorkspaceTab();
    MarkLocalChange();
    RefreshWorkspaceUi();
    _syncStatus = $"{moved}개 탭을 '{destinationLabel}'(으)로 이동했습니다.";
    UpdateStatusText();
  }

  private void PopulateTabOverview()
  {
    CancelOverviewInteraction();
    UpdateOverviewSelectionHeader();
    _overviewGroupPills.Children.Clear();
    void AddGroupPill(string label, string? groupId)
    {
      var selected = _state.ActiveGroupId == groupId;
      var pill = SafariTheme.CreateGlassButton(
          SafariTheme.CreateGlyph(label, 12, selected ? Brushes.White : SafariTheme.TextSecondary),
          () => { SwitchTabGroup(groupId); PopulateTabOverview(); },
          label,
          double.NaN, 30, 15, transparentIdle: !selected, accentFill: selected ? SafariTheme.AccentColor : null);
      pill.Padding = new Thickness(12, 0, 12, 0);
      pill.Margin = new Thickness(0, 0, 6, 0);
      _overviewGroupPills.Children.Add(pill);
    }
    AddGroupPill("기본 탭", null);
    AddGroupPill("비공개", PrivateGroupId);
    foreach (var g in _state.Groups.Where(g => g.ProfileId == _state.ActiveProfileId && g.Id != PrivateGroupId))
      AddGroupPill(g.Name, g.Id);

    _overviewCards.Clear();
    _overviewCanvas.Children.Clear();
    var query = _overviewSearchBox.Text.Trim();
    var tabs = VisibleTabs()
        .Where(t => string.IsNullOrEmpty(query) ||
                    t.Title.Contains(query, StringComparison.OrdinalIgnoreCase) ||
                    t.Url.Contains(query, StringComparison.OrdinalIgnoreCase))
        .ToList();

    foreach (var tab in tabs)
    {
      var currentTab = tab;
      var isActive = currentTab.Id == _state.ActiveTabId;
      var isSelected = _overviewSelectionMode && _overviewSelectedIds.Contains(currentTab.Id);
      var scale = new ScaleTransform(1, 1);
      var translate = new TranslateTransform(0, 0);
      var transforms = new TransformGroup();
      transforms.Children.Add(scale);
      transforms.Children.Add(translate);
      var cardBg = new SolidColorBrush(isSelected ? Color.FromArgb(240, 26, 44, 72) : SafariTheme.CardFillColor);

      var cardGrid = new Grid();
      cardGrid.RowDefinitions.Add(new RowDefinition { Height = new GridLength(34) });
      cardGrid.RowDefinitions.Add(new RowDefinition { Height = new GridLength(1, GridUnitType.Star) });

      // Card Header
      var topBar = new DockPanel { Margin = new Thickness(10, 0, 6, 0), VerticalAlignment = VerticalAlignment.Center };
      Border? closeCardBtn = null;
      if (_overviewSelectionMode)
      {
        var checkBadge = new Border
        {
          Width = 22,
          Height = 22,
          CornerRadius = new CornerRadius(11),
          Background = isSelected ? SafariTheme.Accent : new SolidColorBrush(Color.FromArgb(70, 255, 255, 255)),
          BorderBrush = isSelected ? SafariTheme.Accent : SafariTheme.SpecularBorder,
          BorderThickness = new Thickness(1.2),
          Child = isSelected ? SafariIcons.Create(SafariIcon.Check, 11, Brushes.White, 2.2) : null,
        };
        DockPanel.SetDock(checkBadge, Dock.Right);
        topBar.Children.Add(checkBadge);
      }
      else
      {
        closeCardBtn = SafariTheme.CreateGlassButton(SafariIcons.Create(SafariIcon.Close, 10, SafariTheme.TextSecondary, 1.9), () => { _ = CloseTabAsync(currentTab.Id); }, "탭 닫기", 22, 22, 11);
        DockPanel.SetDock(closeCardBtn, Dock.Right);
        topBar.Children.Add(closeCardBtn);
      }

      if (currentTab.IsPinned)
      {
        var pinIcon = SafariIcons.Create(SafariIcon.Pin, 11.5, SafariTheme.Accent, 1.85);
        if (pinIcon is FrameworkElement fePin) fePin.Margin = new Thickness(0, 0, 6, 0);
        DockPanel.SetDock(pinIcon, Dock.Left);
        topBar.Children.Add(pinIcon);
      }

      topBar.Children.Add(new TextBlock
      {
        Text = string.IsNullOrWhiteSpace(currentTab.Title) ? "새 탭" : currentTab.Title,
        FontSize = 12,
        FontWeight = FontWeights.SemiBold,
        Foreground = SafariTheme.TextPrimary,
        TextTrimming = TextTrimming.CharacterEllipsis,
        VerticalAlignment = VerticalAlignment.Center,
      });
      Grid.SetRow(topBar, 0);
      cardGrid.Children.Add(topBar);

      // Preview Surface
      UIElement previewContent;
      if (_thumbnails.TryGetValue(currentTab.Id, out var thumb))
      {
        previewContent = new Image { Source = thumb, Stretch = Stretch.UniformToFill };
      }
      else
      {
        previewContent = new StackPanel
        {
          VerticalAlignment = VerticalAlignment.Center,
          HorizontalAlignment = HorizontalAlignment.Center,
          Children =
          {
            SafariIcons.Create(SafariIcon.Globe, 28, SafariTheme.TextMuted, 1.4),
            new TextBlock
            {
              Text = HostName(currentTab.Url),
              FontSize = 11.5,
              Foreground = SafariTheme.TextMuted,
              HorizontalAlignment = HorizontalAlignment.Center,
              Margin = new Thickness(8, 6, 8, 0),
              TextTrimming = TextTrimming.CharacterEllipsis,
            },
          },
        };
      }
      var previewBorder = new Border
      {
        Background = new SolidColorBrush(Color.FromRgb(22, 24, 30)),
        CornerRadius = new CornerRadius(0, 0, 14, 14),
        ClipToBounds = true,
        Child = previewContent,
      };
      Grid.SetRow(previewBorder, 1);
      cardGrid.Children.Add(previewBorder);

      var card = new Border
      {
        Width = OverviewCardWidth,
        Height = OverviewCardHeight,
        CornerRadius = new CornerRadius(15),
        Background = cardBg,
        BorderBrush = (isSelected || isActive) ? SafariTheme.Accent : SafariTheme.SpecularBorder,
        BorderThickness = new Thickness((isSelected || isActive) ? 2 : 1),
        Effect = SafariTheme.GlassShadow(18, 0.4, 6),
        Cursor = Cursors.Hand,
        RenderTransformOrigin = new Point(0.5, 0.5),
        RenderTransform = transforms,
        ToolTip = _overviewSelectionMode ? "클릭: 선택 / 선택 해제" : "클릭: 탭 열기 · 길게 누르기: 순서 이동 / 메뉴",
        Child = cardGrid,
      };
      Canvas.SetLeft(card, 0);
      Canvas.SetTop(card, 0);

      var entry = new OverviewCardEntry
      {
        Tab = currentTab,
        Card = card,
        Background = cardBg,
        Scale = scale,
        Translate = translate,
        IsActive = isActive,
      };
      _overviewCards.Add(entry);
      _overviewCanvas.Children.Add(card);

      card.MouseEnter += (_, _) =>
      {
        if (_overviewPendingCard is not null || _overviewLiftedCard is not null) return;
        SafariTheme.AnimateScale(scale, 1.025, 130);
        SafariTheme.AnimateColor(cardBg, SafariTheme.CardHoverColor, 110);
      };
      card.MouseLeave += (_, _) =>
      {
        if (_overviewPendingCard is not null || _overviewLiftedCard is not null) return;
        SafariTheme.AnimateScale(scale, 1.0, 150);
        SafariTheme.AnimateColor(cardBg, isSelected ? Color.FromArgb(240, 26, 44, 72) : SafariTheme.CardFillColor, 140);
      };
      card.MouseLeftButtonDown += (_, e) =>
      {
        if (closeCardBtn is not null && IsDescendantOf(e.OriginalSource, closeCardBtn)) return;
        e.Handled = true;
        ClosePopup();
        if (_overviewSelectionMode)
        {
          if (!_overviewSelectedIds.Add(currentTab.Id))
            _overviewSelectedIds.Remove(currentTab.Id);
          PopulateTabOverview();
          return;
        }
        _overviewLongPressTimer.Stop();
        _overviewPendingCard = entry;
        _overviewLiftedCard = null;
        _overviewDraggedMoved = false;
        _overviewDragChangedOrder = false;
        _overviewPressCancelled = false;
        _overviewPressCanvasPos = e.GetPosition(_overviewCanvas);
        _overviewGrabOffsetX = _overviewPressCanvasPos.X - entry.TargetX;
        _overviewGrabOffsetY = _overviewPressCanvasPos.Y - entry.TargetY;
        SafariTheme.AnimateScale(scale, 0.985, 85);
        card.CaptureMouse();
        _overviewLongPressTimer.Start();
      };
      card.MouseMove += (_, e) =>
      {
        if (e.LeftButton != MouseButtonState.Pressed) return;

        if (ReferenceEquals(_overviewLiftedCard, entry))
        {
          var pos = e.GetPosition(_overviewCanvas);
          if (!_overviewDraggedMoved)
          {
            var dx = pos.X - _overviewPressCanvasPos.X;
            var dy = pos.Y - _overviewPressCanvasPos.Y;
            if (dx * dx + dy * dy <= 64) return;
            _overviewDraggedMoved = true;
          }

          var desiredX = pos.X - _overviewGrabOffsetX;
          var desiredY = pos.Y - _overviewGrabOffsetY;
          translate.BeginAnimation(TranslateTransform.XProperty, null);
          translate.BeginAnimation(TranslateTransform.YProperty, null);
          translate.X = desiredX;
          translate.Y = desiredY;

          if (string.IsNullOrWhiteSpace(_overviewSearchBox.Text))
          {
            var (cols, startX, _, _) = GetOverviewGridMetrics(_overviewCards.Count);
            var centerX = desiredX + OverviewCardWidth * 0.5;
            var centerY = desiredY + OverviewCardHeight * 0.5;
            var curIdx = _overviewCards.IndexOf(entry);
            var targetIdx = -1;
            for (var i = 0; i < _overviewCards.Count; i++)
            {
              if (_overviewCards[i].Tab.IsPinned != currentTab.IsPinned) continue;
              var (sx, sy) = GetOverviewSlot(i, cols, startX);
              var scx = sx + OverviewCardWidth * 0.5;
              var scy = sy + OverviewCardHeight * 0.5;
              if (Math.Abs(centerX - scx) <= OverviewCardWidth * 0.44 &&
                  Math.Abs(centerY - scy) <= OverviewCardHeight * 0.44)
              {
                targetIdx = i;
                break;
              }
            }
            if (targetIdx >= 0 && targetIdx != curIdx && curIdx >= 0)
            {
              _overviewCards.RemoveAt(curIdx);
              _overviewCards.Insert(targetIdx, entry);
              _overviewDragChangedOrder = true;
              LayoutOverviewCards(animate: true, skipEntry: entry);
            }
          }
        }
        else if (ReferenceEquals(_overviewPendingCard, entry) && !_overviewPressCancelled)
        {
          var pos = e.GetPosition(_overviewCanvas);
          var dx = pos.X - _overviewPressCanvasPos.X;
          var dy = pos.Y - _overviewPressCanvasPos.Y;
          if (dx * dx + dy * dy > 144)
          {
            _overviewLongPressTimer.Stop();
            _overviewPressCancelled = true;
            SafariTheme.AnimateScale(scale, 1.0, 120);
          }
        }
      };
      card.MouseLeftButtonUp += async (_, e) =>
      {
        if (!ReferenceEquals(_overviewPendingCard, entry) && !ReferenceEquals(_overviewLiftedCard, entry))
          return;
        e.Handled = true;
        _overviewLongPressTimer.Stop();
        var wasLifted = ReferenceEquals(_overviewLiftedCard, entry);
        var wasCancelled = _overviewPressCancelled;
        var moved = _overviewDraggedMoved;
        var changedOrder = _overviewDragChangedOrder;
        _overviewPendingCard = null;
        _overviewLiftedCard = null;
        _overviewDraggedMoved = false;
        _overviewDragChangedOrder = false;
        _overviewPressCancelled = false;
        if (card.IsMouseCaptured) card.ReleaseMouseCapture();

        if (wasLifted)
        {
          SettleLiftedOverviewCard(entry, moved, changedOrder, openMenuIfUnmoved: true);
        }
        else
        {
          SafariTheme.AnimateScale(scale, 1.0, 110);
          if (!wasCancelled)
          {
            _showingOverview = false;
            _overviewLayer.Visibility = Visibility.Collapsed;
            ApplyChromePreferences();
            await SelectTabAsync(currentTab.Id);
          }
        }
      };
      card.LostMouseCapture += (_, _) =>
      {
        if (ReferenceEquals(_overviewLiftedCard, entry))
        {
          _overviewLongPressTimer.Stop();
          var moved = _overviewDraggedMoved;
          var changedOrder = _overviewDragChangedOrder;
          _overviewPendingCard = null;
          _overviewLiftedCard = null;
          _overviewDraggedMoved = false;
          _overviewDragChangedOrder = false;
          _overviewPressCancelled = false;
          SettleLiftedOverviewCard(entry, moved, changedOrder, openMenuIfUnmoved: false);
        }
        else if (ReferenceEquals(_overviewPendingCard, entry))
        {
          _overviewLongPressTimer.Stop();
          _overviewPendingCard = null;
          _overviewPressCancelled = false;
          SafariTheme.AnimateScale(scale, 1.0, 110);
        }
      };
      card.MouseRightButtonUp += (_, e) =>
      {
        e.Handled = true;
        ShowTabContextPopup(card, currentTab);
      };
    }

    LayoutOverviewCards(animate: false);
  }

  private void ApplySurfaceMode()
  {
    if (_showingOverview) return;
    var active = ActiveTab();
    var isBlank = active is null || active.Url == "about:blank";
    if (isBlank)
    {
      _webViewLayer.Visibility = Visibility.Collapsed;
      PopulateStartPage();
      _startPageLayer.Visibility = Visibility.Visible;
      SafariTheme.AnimateColor(_chromeTintBrush, SafariTheme.ToolbarBaseColor, 260);
    }
    else
    {
      _startPageLayer.Visibility = Visibility.Collapsed;
      _webViewLayer.Visibility = Visibility.Visible;
      foreach (var (id, view) in _views)
        view.Visibility = id == _state.ActiveTabId ? Visibility.Visible : Visibility.Collapsed;
    }
  }

  private void UpdateStatusText()
  {
    var tab = ActiveTab();
    _statusText.Text = $"{ActiveProfile().Name} · {tab?.Title ?? "탭 없음"}";
    var expired = GoogleDriveAuthorization.NeedsReauthentication;
    _statusSyncText.Text = expired ? "Google 로그인 만료됨 · 여기를 클릭해 다시 로그인" : _syncStatus;
    _statusSyncText.Foreground = expired ? SafariTheme.WarningAmber : SafariTheme.TextSecondary;
    _statusSyncPill.Background = expired
        ? new SolidColorBrush(Color.FromArgb(46, 255, 192, 110))
        : Brushes.Transparent;
    if (_lastSyncExpiredIcon != expired)
    {
      _lastSyncExpiredIcon = expired;
      _syncIconHost.Content = SafariIcons.Create(SafariIcon.Sync, 15, expired ? SafariTheme.WarningAmber : SafariTheme.TextPrimary, 1.65);
    }
  }

  private void UpdateNavigationButtons()
  {
    var core = ActiveWebView()?.CoreWebView2;
    _backBtn.Opacity = core?.CanGoBack == true ? 1.0 : 0.36;
    _forwardBtn.Opacity = core?.CanGoForward == true ? 1.0 : 0.36;
    if (_lastLoadingIcon != _isLoading)
    {
      _lastLoadingIcon = _isLoading;
      _reloadIconHost.Content = SafariIcons.Create(
          _isLoading ? SafariIcon.Close : SafariIcon.Reload,
          _isLoading ? 12.5 : 14.5,
          SafariTheme.TextSecondary,
          1.65);
    }
  }

  private void UpdateBookmarkStarState()
  {
    var url = ActiveTab()?.Url;
    var key = !string.IsNullOrWhiteSpace(url) && url != "about:blank" ? BookmarkStore.CanonicalUrlKey(url) : string.Empty;
    var bookmarked = !string.IsNullOrEmpty(key) &&
        _bookmarks.Items.Any(item => string.Equals(BookmarkStore.CanonicalUrlKey(item.Url), key, StringComparison.OrdinalIgnoreCase));
    if (_lastStarredIcon != bookmarked)
    {
      _lastStarredIcon = bookmarked;
      _bookmarkStarHost.Content = SafariIcons.Create(
          bookmarked ? SafariIcon.StarFilled : SafariIcon.StarOutline,
          14.5,
          bookmarked ? SafariTheme.Accent : SafariTheme.TextSecondary,
          1.55);
    }
  }

  private void UpdateReaderAndDistractionPill()
  {
    if (_distractionActive || _distractionHiddenCount > 0)
    {
      _readerPill.Visibility = Visibility.Visible;
      _readerPill.Background = _distractionActive ? SafariTheme.Accent : SafariTheme.CapsuleHover;
      _readerPillText.Text = _distractionActive
          ? (_distractionHiddenCount > 0 ? $"{_distractionHiddenCount}개 숨김 · 완료" : "가릴 요소 클릭 · 완료")
          : $"{_distractionHiddenCount}개 숨김";
    }
    else if (_readerActive)
    {
      _readerPill.Visibility = Visibility.Visible;
      _readerPill.Background = SafariTheme.Accent;
      _readerPillText.Text = "읽기 도구 끄기";
    }
    else if (_readerAvailable)
    {
      _readerPill.Visibility = Visibility.Visible;
      _readerPill.Background = new SolidColorBrush(Color.FromArgb(190, 56, 62, 78));
      _readerPillText.Text = "읽기 도구";
    }
    else
    {
      _readerPill.Visibility = Visibility.Collapsed;
    }
  }

  private void OnWindowPreviewKeyDown(object sender, KeyEventArgs e)
  {
    var mods = Keyboard.Modifiers;
    if (mods == ModifierKeys.Control)
    {
      switch (e.Key)
      {
        case Key.T: e.Handled = true; _ = CreateTabAsync(); break;
        case Key.W: e.Handled = true; if (_state.ActiveTabId is not null) _ = CloseTabAsync(_state.ActiveTabId); break;
        case Key.L: e.Handled = true; _addressInput.Focus(); _addressInput.SelectAll(); break;
        case Key.R: e.Handled = true; ReloadOrStopActiveTab(); break;
        case Key.D: e.Handled = true; ToggleCurrentBookmark(); break;
        case Key.F: e.Handled = true; OpenFindBar(); break;
        case Key.Tab: e.Handled = true; SwitchRelativeTab(+1); break;
        case Key.OemPlus: case Key.Add: e.Handled = true; AdjustZoom(+0.1); break;
        case Key.OemMinus: case Key.Subtract: e.Handled = true; AdjustZoom(-0.1); break;
        case Key.D0: case Key.NumPad0: e.Handled = true; ResetZoom(); break;
        case >= Key.D1 and <= Key.D9:
          e.Handled = true;
          var idx = e.Key - Key.D1;
          var list = VisibleTabs();
          if (list.Count > 0) _ = SelectTabAsync(list[Math.Min(idx, list.Count - 1)].Id);
          break;
      }
    }
    else if (mods == (ModifierKeys.Control | ModifierKeys.Shift))
    {
      switch (e.Key)
      {
        case Key.T: e.Handled = true; _ = ReopenClosedTabAsync(); break;
        case Key.S: e.Handled = true; ToggleSidebar(); break;
        case Key.O: e.Handled = true; ToggleTabOverview(); break;
        case Key.B: e.Handled = true; ShowBookmarkManagerWindow(); break;
        case Key.N: e.Handled = true; SwitchTabGroup(PrivateGroupId); _ = CreateTabAsync(); break;
        case Key.Tab: e.Handled = true; SwitchRelativeTab(-1); break;
      }
    }
    else if (mods == ModifierKeys.Alt)
    {
      switch (e.Key)
      {
        case Key.Left: e.Handled = true; GoBack(); break;
        case Key.Right: e.Handled = true; GoForward(); break;
        case Key.D: e.Handled = true; _addressInput.Focus(); _addressInput.SelectAll(); break;
        case Key.R: e.Handled = true; _ = ToggleReaderModeAsync(); break;
      }
    }
    else if (e.Key == Key.F5)
    {
      e.Handled = true;
      ReloadOrStopActiveTab();
    }
    else if (e.Key == Key.F11)
    {
      e.Handled = true;
      WindowState = WindowState == WindowState.Maximized ? WindowState.Normal : WindowState.Maximized;
    }
    else if (e.Key == Key.Escape && _showingOverview)
    {
      e.Handled = true;
      if (_overviewSelectionMode) ExitOverviewSelectionMode();
      else ToggleTabOverview();
    }
  }

  private ProfileEntry ActiveProfile() =>
      _state.Profiles.FirstOrDefault(p => p.Id == _state.ActiveProfileId) ?? _state.Profiles[0];

  private string ActiveGroupName() => _state.ActiveGroupId switch
  {
    null => "기본 탭",
    PrivateGroupId => "비공개",
    _ => _state.Groups.FirstOrDefault(g => g.Id == _state.ActiveGroupId)?.Name ?? "그룹",
  };

  private TabEntry? ActiveTab() => _state.Tabs.FirstOrDefault(t => t.Id == _state.ActiveTabId);

  private List<TabEntry> VisibleTabs() => _state.Tabs
      .Where(t => t.ProfileId == _state.ActiveProfileId && t.GroupId == _state.ActiveGroupId)
      .OrderByDescending(t => t.IsPinned)
      .ToList();

  private WpfWebView2? ActiveWebView() =>
      _state.ActiveTabId is not null && _views.TryGetValue(_state.ActiveTabId, out var v) ? v : null;

  private void EnsureWorkspaceTab()
  {
    _state.Normalize();
    var visible = VisibleTabs();
    if (visible.Count == 0)
    {
      var fresh = new TabEntry(Guid.NewGuid().ToString("N"), "새 탭", "about:blank", _state.ActiveProfileId, _state.ActiveGroupId);
      _state.Tabs.Add(fresh);
      _state.ActiveTabId = fresh.Id;
    }
    else if (_state.ActiveTabId is null || !visible.Any(t => t.Id == _state.ActiveTabId))
    {
      _state.ActiveTabId = visible[0].Id;
    }
  }

  private void MarkLocalChange()
  {
    _stateDirty = true;
    if (!GoogleDriveAuthorization.NeedsReauthentication)
      _syncStatus = "로컬 변경 저장 대기";
    UpdateStatusText();
  }

  private string DisplayAddress(string url)
  {
    if (string.IsNullOrWhiteSpace(url) || url == "about:blank") return string.Empty;
    return _preferences.ShowFullUrls ? url : HostName(url);
  }

  private static string HostName(string url)
  {
    if (string.IsNullOrWhiteSpace(url) || url == "about:blank") return "시작 페이지";
    return Uri.TryCreate(url, UriKind.Absolute, out var uri) && !string.IsNullOrEmpty(uri.Host)
        ? uri.Host.TrimStart("www.".ToCharArray())
        : url;
  }

  private string ResolveInput(string input)
  {
    var candidate = (input ?? string.Empty).Trim();
    if (candidate.Length == 0) return "about:blank";
    if (Uri.TryCreate(candidate, UriKind.Absolute, out var absolute) &&
        (absolute.Scheme == Uri.UriSchemeHttps || absolute.Scheme == Uri.UriSchemeHttp || absolute.Scheme == "chrome-extension"))
    {
      if (_preferences.AutomaticHttps && absolute.Scheme == Uri.UriSchemeHttp)
        return new UriBuilder(absolute) { Scheme = Uri.UriSchemeHttps, Port = -1 }.Uri.ToString();
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

  private BitmapSource? LoadFaviconSource(BookmarkEntry fav)
  {
    var key = HostName(fav.Url);
    if (_faviconCache.TryGetValue(key, out var cached)) return cached;
    try
    {
      using var img = BookmarkFaviconStore.LoadOrCreate(fav);
      var src = DwmBackdrop.FromDrawingImage(img);
      if (src is not null) _faviconCache[key] = src;
      return src;
    }
    catch { return null; }
  }

  private void LoadPersistedThumbnails()
  {
    DesktopThumbnailStore.Prune(_state.Tabs.Select(t => t.Id));
    foreach (var tab in _state.Tabs)
    {
      try
      {
        using var img = DesktopThumbnailStore.Load(tab.Id);
        var src = DwmBackdrop.FromDrawingImage(img);
        if (src is not null) _thumbnails[tab.Id] = src;
      }
      catch { }
    }
  }
}
