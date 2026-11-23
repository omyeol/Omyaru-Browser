using System.Windows;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using Brushes = System.Windows.Media.Brushes;
using Color = System.Windows.Media.Color;
using Cursors = System.Windows.Input.Cursors;
using HorizontalAlignment = System.Windows.HorizontalAlignment;
using Image = System.Windows.Controls.Image;
using MessageBox = System.Windows.MessageBox;
using Orientation = System.Windows.Controls.Orientation;
using Point = System.Windows.Point;
using TextBox = System.Windows.Controls.TextBox;

namespace PrivacyBrowser.Desktop.Safari;

/// <summary>
/// Naver Whale-inspired, GPU-accelerated WPF hierarchical Bookmark Manager window.
/// Supports arbitrary nested folders, live search, sort, duplicate cleanup, drag-and-drop
/// bookmark organization, Netscape HTML import/export (Whale/Chrome/Edge/Safari compatible),
/// and automatic Google Drive folder + bookmark sync.
/// </summary>
internal sealed class SafariBookmarkManagerWindow : Window
{
  private static readonly SolidColorBrush FolderGold = SafariTheme.Frozen(Color.FromRgb(244, 196, 48));

  private enum SortMode
  {
    Custom,
    NameAsc,
    UrlAsc,
  }

  private readonly BookmarkCatalog _catalog;
  private readonly Func<BookmarkEntry, BitmapSource?> _loadFavicon;
  private readonly Action<string> _openUrl;
  private readonly Action<BookmarkCatalog> _onChanged;

  private readonly TextBox _searchBox = new();
  private readonly TextBlock _summaryText = new();
  private readonly TextBlock _toastText = new();
  private readonly StackPanel _folderTreePanel = new();
  private readonly StackPanel _breadcrumbPanel = new();
  private readonly StackPanel _listPanel = new();
  private readonly TextBlock _sortButtonText = new();
  private readonly Popup _rowPopup = new()
  {
    AllowsTransparency = true,
    StaysOpen = false,
    PopupAnimation = PopupAnimation.None,
  };

  private readonly HashSet<string> _expandedFolderIds = new(StringComparer.Ordinal);
  private string? _selectedFolderId; // null = PC 북마크 (최상위)
  private SortMode _sortMode = SortMode.Custom;
  private string? _draggingBookmarkId;

  internal SafariBookmarkManagerWindow(
      Window owner,
      BookmarkCatalog catalog,
      Func<BookmarkEntry, BitmapSource?> loadFavicon,
      Action<string> openUrl,
      Action<BookmarkCatalog> onChanged)
  {
    _catalog = catalog.Copy();
    _loadFavicon = loadFavicon;
    _openUrl = openUrl;
    _onChanged = onChanged;

    // Expand top-level folders by default so nested trees are visible immediately
    foreach (var f in _catalog.Folders.Where(f => f.ParentId is null))
      _expandedFolderIds.Add(f.Id);

    Title = "북마크 관리자 — Omyaru";
    Owner = owner;
    WindowStartupLocation = WindowStartupLocation.CenterOwner;
    Width = 1120;
    Height = 740;
    MinWidth = 820;
    MinHeight = 560;
    Background = SafariTheme.WindowBg;
    Foreground = SafariTheme.TextPrimary;
    FontFamily = SafariTheme.UiFont;
    UseLayoutRounding = true;
    SnapsToDevicePixels = true;
    SafariTheme.ApplyMacScrollBarStyles(Resources);

    SourceInitialized += (_, _) => DwmBackdrop.Apply(this, acrylic: false);
    PreviewKeyDown += (_, e) =>
    {
      if (e.Key == Key.Escape)
      {
        if (_rowPopup.IsOpen) { _rowPopup.IsOpen = false; e.Handled = true; }
        else { e.Handled = true; Close(); }
      }
      else if (Keyboard.Modifiers == ModifierKeys.Control && e.Key == Key.F)
      {
        e.Handled = true;
        _searchBox.Focus();
        _searchBox.SelectAll();
      }
    };

    BuildVisualTree();
    RefreshAll();
  }

  private void BuildVisualTree()
  {
    var root = new Grid { Background = SafariTheme.WindowBg };
    root.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
    root.RowDefinitions.Add(new RowDefinition { Height = new GridLength(1, GridUnitType.Star) });

    // 1. Top Header Bar (Whale-inspired clean navigation header + search input)
    var header = new Border
    {
      Background = new SolidColorBrush(Color.FromRgb(22, 24, 30)),
      BorderBrush = SafariTheme.Divider,
      BorderThickness = new Thickness(0, 0, 0, 1),
      Padding = new Thickness(28, 14, 28, 14),
    };
    var headerGrid = new Grid();
    headerGrid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
    headerGrid.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });

    var leftHeader = new StackPanel { Orientation = Orientation.Horizontal, VerticalAlignment = VerticalAlignment.Center };
    var titleWrap = new StackPanel { Margin = new Thickness(0, 0, 18, 0) };
    var titleRow = new StackPanel { Orientation = Orientation.Horizontal, VerticalAlignment = VerticalAlignment.Center };
    titleRow.Children.Add(SafariIcons.Create(SafariIcon.FolderFilled, 18, FolderGold));
    titleRow.Children.Add(new TextBlock
    {
      Text = "북마크",
      FontSize = 18,
      FontWeight = FontWeights.Bold,
      Foreground = SafariTheme.Accent,
      Margin = new Thickness(8, 0, 0, 0),
      VerticalAlignment = VerticalAlignment.Center,
    });
    titleWrap.Children.Add(titleRow);
    titleWrap.Children.Add(new Border
    {
      Height = 2,
      Background = SafariTheme.Accent,
      CornerRadius = new CornerRadius(1),
      Margin = new Thickness(0, 4, 0, 0),
    });
    leftHeader.Children.Add(titleWrap);

    _summaryText.FontSize = 12;
    _summaryText.Foreground = SafariTheme.TextMuted;
    _summaryText.VerticalAlignment = VerticalAlignment.Center;
    leftHeader.Children.Add(_summaryText);

    _toastText.FontSize = 11.5;
    _toastText.Foreground = SafariTheme.SuccessGreen;
    _toastText.VerticalAlignment = VerticalAlignment.Center;
    _toastText.Margin = new Thickness(16, 0, 0, 0);
    leftHeader.Children.Add(_toastText);
    Grid.SetColumn(leftHeader, 0);
    headerGrid.Children.Add(leftHeader);

    // Search Input (right)
    var searchBorder = new Border
    {
      Width = 280,
      Height = 34,
      CornerRadius = new CornerRadius(10),
      Background = SafariTheme.AddressFill,
      BorderBrush = SafariTheme.SpecularBorder,
      BorderThickness = new Thickness(1),
      Padding = new Thickness(11, 0, 10, 0),
    };
    var searchGrid = new Grid();
    searchGrid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
    searchGrid.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });

    var searchPlaceholder = new TextBlock
    {
      Text = "북마크 검색",
      FontSize = 12.5,
      Foreground = SafariTheme.TextMuted,
      VerticalAlignment = VerticalAlignment.Center,
      IsHitTestVisible = false,
    };
    _searchBox.Background = Brushes.Transparent;
    _searchBox.BorderThickness = new Thickness(0);
    _searchBox.Foreground = SafariTheme.TextPrimary;
    _searchBox.CaretBrush = SafariTheme.Accent;
    _searchBox.FontSize = 12.5;
    _searchBox.VerticalContentAlignment = VerticalAlignment.Center;
    _searchBox.TextChanged += (_, _) =>
    {
      searchPlaceholder.Visibility = string.IsNullOrEmpty(_searchBox.Text) ? Visibility.Visible : Visibility.Collapsed;
      RenderBookmarkList();
    };
    var searchInputHost = new Grid();
    searchInputHost.Children.Add(searchPlaceholder);
    searchInputHost.Children.Add(_searchBox);
    Grid.SetColumn(searchInputHost, 0);
    searchGrid.Children.Add(searchInputHost);

    var searchIcon = SafariIcons.Create(SafariIcon.Search, 14, SafariTheme.TextMuted);
    Grid.SetColumn(searchIcon, 1);
    searchGrid.Children.Add(searchIcon);
    searchBorder.Child = searchGrid;
    Grid.SetColumn(searchBorder, 1);
    headerGrid.Children.Add(searchBorder);

    header.Child = headerGrid;
    Grid.SetRow(header, 0);
    root.Children.Add(header);

    // 2. Main Split Body: [Left Folder Tree (280px) | Right Action Bar + Bookmark List]
    var body = new Grid { Margin = new Thickness(24, 18, 24, 18) };
    body.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(276) });
    body.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(22) });
    body.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });

    // Left Tree Column
    var treeCol = new Grid();
    treeCol.RowDefinitions.Add(new RowDefinition { Height = new GridLength(1, GridUnitType.Star) });
    treeCol.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });

    var treeScroll = new ScrollViewer
    {
      VerticalScrollBarVisibility = ScrollBarVisibility.Auto,
      HorizontalScrollBarVisibility = ScrollBarVisibility.Disabled,
      Content = _folderTreePanel,
    };
    Grid.SetRow(treeScroll, 0);
    treeCol.Children.Add(treeScroll);

    var addFolderRow = new StackPanel { Orientation = Orientation.Horizontal, HorizontalAlignment = HorizontalAlignment.Center, VerticalAlignment = VerticalAlignment.Center };
    addFolderRow.Children.Add(SafariIcons.Create(SafariIcon.FolderPlus, 14.5, SafariTheme.TextPrimary, 1.55));
    addFolderRow.Children.Add(new TextBlock { Text = "새 폴더", FontSize = 12, Foreground = SafariTheme.TextPrimary, Margin = new Thickness(6, 0, 0, 0), VerticalAlignment = VerticalAlignment.Center });
    var addFolderBtn = SafariTheme.CreateGlassButton(
        addFolderRow,
        () => PromptCreateFolder(_selectedFolderId),
        "현재 선택한 위치에 새 하위 폴더 만들기",
        double.NaN, 34, 10, transparentIdle: false);
    addFolderBtn.BorderBrush = SafariTheme.CapsuleBorder;
    addFolderBtn.BorderThickness = new Thickness(1);
    addFolderBtn.Margin = new Thickness(0, 10, 0, 0);
    Grid.SetRow(addFolderBtn, 1);
    treeCol.Children.Add(addFolderBtn);

    Grid.SetColumn(treeCol, 0);
    body.Children.Add(treeCol);

    // Right Content Column
    var rightCol = new Grid();
    rightCol.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
    rightCol.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
    rightCol.RowDefinitions.Add(new RowDefinition { Height = new GridLength(1, GridUnitType.Star) });

    // Action Bar (Exact Whale-style: [정렬 ⌄] [추가 ⌄]   ...   [북마크 정리] [북마크 내보내기] [북마크 가져오기])
    var actionBar = new Border
    {
      BorderBrush = SafariTheme.Divider,
      BorderThickness = new Thickness(0, 0, 0, 1),
      Padding = new Thickness(0, 0, 0, 12),
      Margin = new Thickness(0, 0, 0, 10),
    };
    var actionDock = new DockPanel();

    var rightActions = new StackPanel { Orientation = Orientation.Horizontal, VerticalAlignment = VerticalAlignment.Center };
    var cleanBtn = ActionPill("북마크 정리", RunCleanDuplicates);
    var exportBtn = ActionPill("북마크 내보내기", ExportHtmlBookmarks);
    var importBtn = ActionPill("북마크 가져오기", ImportHtmlBookmarks);
    exportBtn.Margin = new Thickness(6, 0, 6, 0);
    rightActions.Children.Add(cleanBtn);
    rightActions.Children.Add(exportBtn);
    rightActions.Children.Add(importBtn);
    DockPanel.SetDock(rightActions, Dock.Right);
    actionDock.Children.Add(rightActions);

    var leftActions = new StackPanel { Orientation = Orientation.Horizontal, VerticalAlignment = VerticalAlignment.Center };
    _sortButtonText.Text = "정렬";
    _sortButtonText.FontSize = 12;
    _sortButtonText.Foreground = SafariTheme.TextPrimary;
    _sortButtonText.VerticalAlignment = VerticalAlignment.Center;
    var sortInner = new StackPanel { Orientation = Orientation.Horizontal, VerticalAlignment = VerticalAlignment.Center, Margin = new Thickness(10, 0, 8, 0) };
    sortInner.Children.Add(_sortButtonText);
    var sortChevron = SafariIcons.Create(SafariIcon.ChevronDown, 11, SafariTheme.TextSecondary, 1.8);
    if (sortChevron is FrameworkElement feS) feS.Margin = new Thickness(5, 0, 0, 0);
    sortInner.Children.Add(sortChevron);
    Border? sortBtn = null;
    sortBtn = SafariTheme.CreateGlassButton(sortInner, () => ShowSortMenu(sortBtn!), "북마크 정렬 방식 선택", double.NaN, 32, 8, transparentIdle: false);
    sortBtn.BorderBrush = SafariTheme.CapsuleBorder;
    sortBtn.BorderThickness = new Thickness(1);
    sortBtn.Margin = new Thickness(0, 0, 8, 0);

    var addInner = new StackPanel { Orientation = Orientation.Horizontal, VerticalAlignment = VerticalAlignment.Center, Margin = new Thickness(10, 0, 8, 0) };
    addInner.Children.Add(new TextBlock { Text = "추가", FontSize = 12, Foreground = SafariTheme.TextPrimary, VerticalAlignment = VerticalAlignment.Center });
    var addChevron = SafariIcons.Create(SafariIcon.ChevronDown, 11, SafariTheme.TextSecondary, 1.8);
    if (addChevron is FrameworkElement feA) feA.Margin = new Thickness(5, 0, 0, 0);
    addInner.Children.Add(addChevron);
    Border? addBtn = null;
    addBtn = SafariTheme.CreateGlassButton(addInner, () => ShowAddMenu(addBtn!), "새 북마크 또는 폴더 추가", double.NaN, 32, 8, transparentIdle: false);
    addBtn.BorderBrush = SafariTheme.CapsuleBorder;
    addBtn.BorderThickness = new Thickness(1);

    leftActions.Children.Add(sortBtn);
    leftActions.Children.Add(addBtn);
    actionDock.Children.Add(leftActions);
    actionBar.Child = actionDock;
    Grid.SetRow(actionBar, 0);
    rightCol.Children.Add(actionBar);

    // Breadcrumb row
    _breadcrumbPanel.Orientation = Orientation.Horizontal;
    _breadcrumbPanel.Margin = new Thickness(2, 0, 2, 8);
    Grid.SetRow(_breadcrumbPanel, 1);
    rightCol.Children.Add(_breadcrumbPanel);

    // Scrollable Items Panel
    var listScroll = new ScrollViewer
    {
      VerticalScrollBarVisibility = ScrollBarVisibility.Auto,
      HorizontalScrollBarVisibility = ScrollBarVisibility.Disabled,
      Content = _listPanel,
    };
    Grid.SetRow(listScroll, 2);
    rightCol.Children.Add(listScroll);

    Grid.SetColumn(rightCol, 2);
    body.Children.Add(rightCol);

    Grid.SetRow(body, 1);
    root.Children.Add(body);
    Content = root;
  }

  private static Border ActionPill(string label, Action onClick)
  {
    var tb = new TextBlock
    {
      Text = label,
      FontSize = 12,
      Foreground = SafariTheme.TextPrimary,
      VerticalAlignment = VerticalAlignment.Center,
      Margin = new Thickness(12, 0, 12, 0),
    };
    var btn = SafariTheme.CreateGlassButton(tb, onClick, label, double.NaN, 32, 8, transparentIdle: false);
    btn.BorderBrush = SafariTheme.CapsuleBorder;
    btn.BorderThickness = new Thickness(1);
    return btn;
  }

  private void RefreshAll()
  {
    if (_selectedFolderId is not null && !_catalog.Folders.Any(f => f.Id == _selectedFolderId))
      _selectedFolderId = null;
    _summaryText.Text = $"총 {_catalog.Items.Count}개 북마크 · {_catalog.Folders.Count}개 폴더";
    RenderFolderTree();
    RenderBreadcrumbs();
    RenderBookmarkList();
  }

  private void PersistAndRefresh(string? toast = null)
  {
    BookmarkStore.Save(_catalog);
    _onChanged(_catalog.Copy());
    if (!string.IsNullOrWhiteSpace(toast)) _toastText.Text = toast;
    RefreshAll();
  }

  private void RenderFolderTree()
  {
    _folderTreePanel.Children.Clear();

    // Root node: PC 북마크
    var rootCount = _catalog.Items.Count(b => b.FolderId is null);
    _folderTreePanel.Children.Add(CreateTreeFolderRow(null, "PC 북마크", rootCount, depth: 0, hasChildren: false));

    void AddBranch(string? parentId, int depth)
    {
      var children = _catalog.Folders
          .Where(f => string.Equals(f.ParentId ?? string.Empty, parentId ?? string.Empty, StringComparison.Ordinal))
          .OrderBy(f => f.Order)
          .ThenBy(f => f.Name, StringComparer.CurrentCultureIgnoreCase)
          .ToList();
      foreach (var folder in children)
      {
        var hasSub = _catalog.Folders.Any(c => c.ParentId == folder.Id);
        var count = BookmarkStore.RecursiveBookmarkCount(_catalog, folder.Id);
        _folderTreePanel.Children.Add(CreateTreeFolderRow(folder.Id, folder.Name, count, depth, hasSub));
        if (hasSub && _expandedFolderIds.Contains(folder.Id))
          AddBranch(folder.Id, depth + 1);
      }
    }

    AddBranch(null, 0);
  }

  private Border CreateTreeFolderRow(string? folderId, string name, int count, int depth, bool hasChildren)
  {
    var isSelected = string.Equals(_selectedFolderId ?? string.Empty, folderId ?? string.Empty, StringComparison.Ordinal);
    var idle = isSelected ? Color.FromArgb(62, 255, 255, 255) : Color.FromArgb(0, 255, 255, 255);
    var hover = isSelected ? Color.FromArgb(74, 255, 255, 255) : Color.FromArgb(26, 255, 255, 255);
    var bg = new SolidColorBrush(idle);

    var grid = new Grid();
    grid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(18) });
    grid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(22) });
    grid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
    grid.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });

    if (hasChildren && folderId is not null)
    {
      var expanded = _expandedFolderIds.Contains(folderId);
      var chevBtn = SafariTheme.CreateGlassButton(
          SafariIcons.Create(expanded ? SafariIcon.ChevronDown : SafariIcon.ChevronRight, 10, SafariTheme.TextMuted, 1.9),
          () =>
          {
            if (!_expandedFolderIds.Add(folderId)) _expandedFolderIds.Remove(folderId);
            RenderFolderTree();
          },
          expanded ? "접기" : "펼치기",
          18, 22, 6);
      Grid.SetColumn(chevBtn, 0);
      grid.Children.Add(chevBtn);
    }

    var folderIcon = SafariIcons.Create(SafariIcon.FolderFilled, 15, FolderGold);
    Grid.SetColumn(folderIcon, 1);
    grid.Children.Add(folderIcon);

    var label = new TextBlock
    {
      Text = name,
      FontSize = 12.5,
      FontWeight = isSelected ? FontWeights.SemiBold : FontWeights.Normal,
      Foreground = isSelected ? SafariTheme.TextPrimary : SafariTheme.TextSecondary,
      TextTrimming = TextTrimming.CharacterEllipsis,
      VerticalAlignment = VerticalAlignment.Center,
      Margin = new Thickness(4, 0, 6, 0),
    };
    Grid.SetColumn(label, 2);
    grid.Children.Add(label);

    var countText = new TextBlock
    {
      Text = count.ToString(),
      FontSize = 11,
      Foreground = SafariTheme.TextMuted,
      VerticalAlignment = VerticalAlignment.Center,
      Margin = new Thickness(0, 0, 6, 0),
    };
    Grid.SetColumn(countText, 3);
    grid.Children.Add(countText);

    var row = new Border
    {
      Height = 32,
      CornerRadius = new CornerRadius(8),
      Background = bg,
      Padding = new Thickness(6 + depth * 16, 0, 6, 0),
      Margin = new Thickness(0, 1, 0, 1),
      Cursor = Cursors.Hand,
      AllowDrop = true,
      Child = grid,
    };

    row.MouseEnter += (_, _) => SafariTheme.AnimateColor(bg, hover, 90);
    row.MouseLeave += (_, _) => SafariTheme.AnimateColor(bg, idle, 120);
    row.MouseLeftButtonUp += (_, e) =>
    {
      if (e.Handled) return;
      e.Handled = true;
      _selectedFolderId = folderId;
      RefreshAll();
    };
    row.MouseRightButtonUp += (_, e) =>
    {
      e.Handled = true;
      ShowFolderContextMenu(row, folderId, name);
    };
    row.DragOver += (_, e) =>
    {
      if (_draggingBookmarkId is not null)
      {
        e.Effects = System.Windows.DragDropEffects.Move;
        e.Handled = true;
        SafariTheme.AnimateColor(bg, SafariTheme.AccentSoftColor, 80);
      }
    };
    row.DragLeave += (_, _) => SafariTheme.AnimateColor(bg, idle, 80);
    row.Drop += (_, e) =>
    {
      if (_draggingBookmarkId is { } bid)
      {
        e.Handled = true;
        if (BookmarkStore.MoveBookmarkToFolder(_catalog, bid, folderId))
          PersistAndRefresh($"‘{name}’ 폴더로 이동했습니다");
      }
    };
    return row;
  }

  private void RenderBreadcrumbs()
  {
    _breadcrumbPanel.Children.Clear();
    var chain = new List<(string? Id, string Name)>();
    var cur = _selectedFolderId;
    var guard = 0;
    while (!string.IsNullOrWhiteSpace(cur) && guard++ < 32)
    {
      var f = _catalog.Folders.FirstOrDefault(x => x.Id == cur);
      if (f is null) break;
      chain.Add((f.Id, f.Name));
      cur = f.ParentId;
    }
    chain.Add((null, "PC 북마크"));
    chain.Reverse();

    for (var i = 0; i < chain.Count; i++)
    {
      var (id, name) = chain[i];
      var isLast = i == chain.Count - 1;
      var btn = new TextBlock
      {
        Text = name,
        FontSize = 12,
        FontWeight = isLast ? FontWeights.SemiBold : FontWeights.Normal,
        Foreground = isLast ? SafariTheme.TextPrimary : SafariTheme.TextMuted,
        Cursor = isLast ? Cursors.Arrow : Cursors.Hand,
        VerticalAlignment = VerticalAlignment.Center,
      };
      if (!isLast)
      {
        btn.MouseLeftButtonUp += (_, _) => { _selectedFolderId = id; RefreshAll(); };
      }
      _breadcrumbPanel.Children.Add(btn);
      if (!isLast)
      {
        var sep = SafariIcons.Create(SafariIcon.ChevronRight, 11, SafariTheme.TextMuted, 1.8);
        if (sep is FrameworkElement fe) fe.Margin = new Thickness(5, 0, 5, 0);
        _breadcrumbPanel.Children.Add(sep);
      }
    }
  }

  private void RenderBookmarkList()
  {
    _listPanel.Children.Clear();
    var query = _searchBox.Text.Trim();
    var isSearching = !string.IsNullOrEmpty(query);

    if (!isSearching)
    {
      // Show subfolders of current folder first
      var subFolders = _catalog.Folders
          .Where(f => string.Equals(f.ParentId ?? string.Empty, _selectedFolderId ?? string.Empty, StringComparison.Ordinal))
          .OrderBy(f => _sortMode == SortMode.NameAsc ? f.Name : f.Order.ToString("D8"), StringComparer.CurrentCultureIgnoreCase)
          .ToList();
      foreach (var folder in subFolders)
        _listPanel.Children.Add(CreateListSubfolderRow(folder));
    }

    IEnumerable<BookmarkEntry> items = isSearching
        ? _catalog.Items.Where(b =>
            b.Name.Contains(query, StringComparison.OrdinalIgnoreCase) ||
            b.Url.Contains(query, StringComparison.OrdinalIgnoreCase))
        : _catalog.Items.Where(b => string.Equals(b.FolderId ?? string.Empty, _selectedFolderId ?? string.Empty, StringComparison.Ordinal));

    items = _sortMode switch
    {
      SortMode.NameAsc => items.OrderBy(b => b.Name, StringComparer.CurrentCultureIgnoreCase),
      SortMode.UrlAsc => items.OrderBy(b => b.Url, StringComparer.OrdinalIgnoreCase),
      _ => items,
    };

    var list = items.ToList();
    foreach (var entry in list)
      _listPanel.Children.Add(CreateListBookmarkRow(entry, showFolderTag: isSearching));

    if (_listPanel.Children.Count == 0)
    {
      _listPanel.Children.Add(new TextBlock
      {
        Text = isSearching ? $"‘{query}’에 일치하는 북마크가 없습니다." : "이 폴더에 북마크가 없습니다.\n상단 [추가] 또는 [북마크 가져오기]를 눌러 시작하세요.",
        FontSize = 13,
        Foreground = SafariTheme.TextMuted,
        TextAlignment = TextAlignment.Center,
        HorizontalAlignment = HorizontalAlignment.Center,
        Margin = new Thickness(0, 64, 0, 0),
      });
    }
  }

  private Border CreateListSubfolderRow(BookmarkFolderEntry folder)
  {
    var idle = Color.FromArgb(0, 255, 255, 255);
    var hover = Color.FromArgb(24, 255, 255, 255);
    var bg = new SolidColorBrush(idle);
    var count = BookmarkStore.RecursiveBookmarkCount(_catalog, folder.Id);

    var grid = new Grid();
    grid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(32) });
    grid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
    grid.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
    grid.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });

    var icon = SafariIcons.Create(SafariIcon.FolderFilled, 17, FolderGold);
    Grid.SetColumn(icon, 0);
    grid.Children.Add(icon);

    var title = new TextBlock
    {
      Text = folder.Name,
      FontSize = 13,
      FontWeight = FontWeights.SemiBold,
      Foreground = SafariTheme.TextPrimary,
      TextTrimming = TextTrimming.CharacterEllipsis,
      VerticalAlignment = VerticalAlignment.Center,
      Margin = new Thickness(4, 0, 12, 0),
    };
    Grid.SetColumn(title, 1);
    grid.Children.Add(title);

    var badge = new TextBlock
    {
      Text = $"{count}개",
      FontSize = 11.5,
      Foreground = SafariTheme.TextMuted,
      VerticalAlignment = VerticalAlignment.Center,
      Margin = new Thickness(0, 0, 10, 0),
    };
    Grid.SetColumn(badge, 2);
    grid.Children.Add(badge);

    Border? menuBtn = null;
    menuBtn = SafariTheme.CreateGlassButton(
        SafariIcons.Create(SafariIcon.MoreVertical, 14, SafariTheme.TextMuted),
        () => ShowFolderContextMenu(menuBtn!, folder.Id, folder.Name),
        "폴더 작업",
        28, 28, 8);
    Grid.SetColumn(menuBtn, 3);
    grid.Children.Add(menuBtn);

    var row = new Border
    {
      Height = 40,
      CornerRadius = new CornerRadius(8),
      Background = bg,
      Padding = new Thickness(8, 0, 6, 0),
      Cursor = Cursors.Hand,
      AllowDrop = true,
      Child = grid,
    };
    row.MouseEnter += (_, _) => SafariTheme.AnimateColor(bg, hover, 90);
    row.MouseLeave += (_, _) => SafariTheme.AnimateColor(bg, idle, 120);
    row.MouseLeftButtonUp += (_, e) =>
    {
      if (e.Handled) return;
      e.Handled = true;
      _expandedFolderIds.Add(folder.Id);
      if (folder.ParentId is not null) _expandedFolderIds.Add(folder.ParentId);
      _selectedFolderId = folder.Id;
      RefreshAll();
    };
    row.MouseRightButtonUp += (_, e) =>
    {
      e.Handled = true;
      ShowFolderContextMenu(row, folder.Id, folder.Name);
    };
    row.DragOver += (_, e) =>
    {
      if (_draggingBookmarkId is not null)
      {
        e.Effects = System.Windows.DragDropEffects.Move;
        e.Handled = true;
        SafariTheme.AnimateColor(bg, SafariTheme.AccentSoftColor, 80);
      }
    };
    row.DragLeave += (_, _) => SafariTheme.AnimateColor(bg, idle, 80);
    row.Drop += (_, e) =>
    {
      if (_draggingBookmarkId is { } bid)
      {
        e.Handled = true;
        if (BookmarkStore.MoveBookmarkToFolder(_catalog, bid, folder.Id))
          PersistAndRefresh($"‘{folder.Name}’ 폴더로 이동했습니다");
      }
    };
    return row;
  }

  private Border CreateListBookmarkRow(BookmarkEntry entry, bool showFolderTag)
  {
    var idle = Color.FromArgb(0, 255, 255, 255);
    var hover = Color.FromArgb(24, 255, 255, 255);
    var bg = new SolidColorBrush(idle);

    var grid = new Grid();
    grid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(32) });
    grid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
    grid.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
    grid.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });

    var favImg = _loadFavicon(entry);
    UIElement iconEl = favImg is not null
        ? new Image { Source = favImg, Width = 17, Height = 17, HorizontalAlignment = HorizontalAlignment.Center, VerticalAlignment = VerticalAlignment.Center }
        : SafariIcons.Create(SafariIcon.Globe, 15, SafariTheme.TextMuted);
    Grid.SetColumn(iconEl, 0);
    grid.Children.Add(iconEl);

    var titleRow = new StackPanel { Orientation = Orientation.Horizontal, VerticalAlignment = VerticalAlignment.Center, Margin = new Thickness(4, 0, 12, 0) };
    titleRow.Children.Add(new TextBlock
    {
      Text = entry.Name,
      FontSize = 13,
      Foreground = SafariTheme.TextPrimary,
      TextTrimming = TextTrimming.CharacterEllipsis,
      MaxWidth = 420,
      VerticalAlignment = VerticalAlignment.Center,
    });
    titleRow.Children.Add(new TextBlock
    {
      Text = entry.Url,
      FontSize = 11.5,
      Foreground = SafariTheme.TextMuted,
      TextTrimming = TextTrimming.CharacterEllipsis,
      MaxWidth = 260,
      Margin = new Thickness(12, 0, 0, 0),
      VerticalAlignment = VerticalAlignment.Center,
    });
    Grid.SetColumn(titleRow, 1);
    grid.Children.Add(titleRow);

    if (showFolderTag)
    {
      var folderName = entry.FolderId is null
          ? "PC 북마크"
          : _catalog.Folders.FirstOrDefault(f => f.Id == entry.FolderId)?.Name ?? "PC 북마크";
      var tag = new Border
      {
        CornerRadius = new CornerRadius(6),
        Background = SafariTheme.CapsuleFill,
        Padding = new Thickness(7, 2, 7, 2),
        Margin = new Thickness(0, 0, 8, 0),
        VerticalAlignment = VerticalAlignment.Center,
        Child = new TextBlock { Text = folderName, FontSize = 10.5, Foreground = SafariTheme.TextSecondary },
      };
      Grid.SetColumn(tag, 2);
      grid.Children.Add(tag);
    }

    Border? menuBtn = null;
    menuBtn = SafariTheme.CreateGlassButton(
        SafariIcons.Create(SafariIcon.MoreVertical, 14, SafariTheme.TextMuted),
        () => ShowBookmarkContextMenu(menuBtn!, entry),
        "북마크 작업",
        28, 28, 8);
    Grid.SetColumn(menuBtn, 3);
    grid.Children.Add(menuBtn);

    var row = new Border
    {
      Height = 38,
      CornerRadius = new CornerRadius(8),
      Background = bg,
      Padding = new Thickness(8, 0, 6, 0),
      Cursor = Cursors.Hand,
      ToolTip = $"{entry.Name}\n{entry.Url}\n(폴더로 드래그해 이동 가능)",
      Child = grid,
    };

    Point? dragOrigin = null;
    row.MouseEnter += (_, _) => SafariTheme.AnimateColor(bg, hover, 90);
    row.MouseLeave += (_, _) => SafariTheme.AnimateColor(bg, idle, 120);
    row.PreviewMouseLeftButtonDown += (_, e) => dragOrigin = e.GetPosition(row);
    row.MouseMove += (_, e) =>
    {
      if (e.LeftButton == MouseButtonState.Pressed && dragOrigin is { } origin)
      {
        var delta = e.GetPosition(row) - origin;
        if (Math.Abs(delta.X) > 10 || Math.Abs(delta.Y) > 10)
        {
          dragOrigin = null;
          _draggingBookmarkId = entry.Id;
          DragDrop.DoDragDrop(row, entry.Id, System.Windows.DragDropEffects.Move);
          _draggingBookmarkId = null;
        }
      }
    };
    row.MouseLeftButtonUp += (_, e) =>
    {
      dragOrigin = null;
      if (e.Handled) return;
      e.Handled = true;
      _openUrl(entry.Url);
    };
    row.MouseRightButtonUp += (_, e) =>
    {
      e.Handled = true;
      ShowBookmarkContextMenu(row, entry);
    };
    return row;
  }

  private void ShowGlassMenu(UIElement anchor, StackPanel panel, double minW = 190)
  {
    _rowPopup.IsOpen = false;
    _rowPopup.PlacementTarget = anchor;
    _rowPopup.Placement = PlacementMode.Bottom;
    _rowPopup.VerticalOffset = 3;
    _rowPopup.Child = SafariTheme.CreatePopoverShell(panel, minW, 280);
    _rowPopup.IsOpen = true;
  }

  private void ShowSortMenu(UIElement anchor)
  {
    var panel = new StackPanel();
    panel.Children.Add(SafariTheme.CreatePopoverRow(_sortMode == SortMode.Custom ? SafariIcon.Check : SafariIcon.Sort, "기본 순서", () => { _rowPopup.IsOpen = false; _sortMode = SortMode.Custom; _sortButtonText.Text = "정렬"; RenderBookmarkList(); }, isSelected: _sortMode == SortMode.Custom));
    panel.Children.Add(SafariTheme.CreatePopoverRow(_sortMode == SortMode.NameAsc ? SafariIcon.Check : SafariIcon.Sort, "이름순 (가나다)", () => { _rowPopup.IsOpen = false; _sortMode = SortMode.NameAsc; _sortButtonText.Text = "이름순"; RenderBookmarkList(); }, isSelected: _sortMode == SortMode.NameAsc));
    panel.Children.Add(SafariTheme.CreatePopoverRow(_sortMode == SortMode.UrlAsc ? SafariIcon.Check : SafariIcon.Sort, "주소순 (URL)", () => { _rowPopup.IsOpen = false; _sortMode = SortMode.UrlAsc; _sortButtonText.Text = "주소순"; RenderBookmarkList(); }, isSelected: _sortMode == SortMode.UrlAsc));
    ShowGlassMenu(anchor, panel, 180);
  }

  private void ShowAddMenu(UIElement anchor)
  {
    var panel = new StackPanel();
    panel.Children.Add(SafariTheme.CreatePopoverRow(SafariIcon.StarOutline, "새 북마크 추가…", () =>
    {
      _rowPopup.IsOpen = false;
      Dispatcher.BeginInvoke(() => PromptAddBookmark(_selectedFolderId), System.Windows.Threading.DispatcherPriority.Background);
    }));
    panel.Children.Add(SafariTheme.CreatePopoverRow(SafariIcon.FolderPlus, "새 폴더 만들기…", () =>
    {
      _rowPopup.IsOpen = false;
      Dispatcher.BeginInvoke(() => PromptCreateFolder(_selectedFolderId), System.Windows.Threading.DispatcherPriority.Background);
    }));
    ShowGlassMenu(anchor, panel, 190);
  }

  private void ShowFolderContextMenu(UIElement anchor, string? folderId, string folderName)
  {
    var panel = new StackPanel();
    panel.Children.Add(SafariTheme.CreatePopoverRow(SafariIcon.StarOutline, "이 폴더에 북마크 추가…", () =>
    {
      _rowPopup.IsOpen = false;
      Dispatcher.BeginInvoke(() => PromptAddBookmark(folderId), System.Windows.Threading.DispatcherPriority.Background);
    }));
    panel.Children.Add(SafariTheme.CreatePopoverRow(SafariIcon.FolderPlus, "하위 폴더 만들기…", () =>
    {
      _rowPopup.IsOpen = false;
      Dispatcher.BeginInvoke(() => PromptCreateFolder(folderId), System.Windows.Threading.DispatcherPriority.Background);
    }));
    if (folderId is not null)
    {
      panel.Children.Add(SafariTheme.CreatePopoverDivider());
      panel.Children.Add(SafariTheme.CreatePopoverRow(SafariIcon.Reader, "이름 바꾸기…", () =>
      {
        _rowPopup.IsOpen = false;
        Dispatcher.BeginInvoke(() =>
        {
          var renamed = PromptText("폴더 이름 바꾸기", "새 폴더 이름을 입력하세요:", folderName);
          if (!string.IsNullOrWhiteSpace(renamed) && BookmarkStore.RenameFolder(_catalog, folderId, renamed))
            PersistAndRefresh($"폴더 이름을 ‘{renamed.Trim()}’(으)로 변경했습니다");
        }, System.Windows.Threading.DispatcherPriority.Background);
      }));
      panel.Children.Add(SafariTheme.CreatePopoverRow(SafariIcon.Folder, "다른 폴더로 이동…", () =>
      {
        _rowPopup.IsOpen = false;
        Dispatcher.BeginInvoke(() =>
        {
          var dest = PromptChooseFolder("폴더 이동", excludeSubtreeOf: folderId);
          if (dest.Confirmed && BookmarkStore.MoveFolder(_catalog, folderId, dest.FolderId))
            PersistAndRefresh("폴더를 이동했습니다");
        }, System.Windows.Threading.DispatcherPriority.Background);
      }));
      panel.Children.Add(SafariTheme.CreatePopoverDivider());
      panel.Children.Add(SafariTheme.CreatePopoverRow(SafariIcon.Trash, "폴더 삭제", () =>
      {
        _rowPopup.IsOpen = false;
        Dispatcher.BeginInvoke(() =>
        {
          var count = BookmarkStore.RecursiveBookmarkCount(_catalog, folderId);
          var msg = count > 0
              ? $"‘{folderName}’ 폴더와 내부 북마크 {count}개를 모두 삭제할까요?"
              : $"‘{folderName}’ 폴더를 삭제할까요?";
          if (MessageBox.Show(this, msg, "폴더 삭제", MessageBoxButton.YesNo, MessageBoxImage.Warning) != MessageBoxResult.Yes)
            return;
          BookmarkStore.DeleteFolder(_catalog, folderId);
          if (_selectedFolderId == folderId) _selectedFolderId = null;
          PersistAndRefresh($"‘{folderName}’ 폴더를 삭제했습니다");
        }, System.Windows.Threading.DispatcherPriority.Background);
      }, isDestructive: true));
    }
    ShowGlassMenu(anchor, panel, 200);
  }

  private void ShowBookmarkContextMenu(UIElement anchor, BookmarkEntry entry)
  {
    var panel = new StackPanel();
    panel.Children.Add(SafariTheme.CreatePopoverRow(SafariIcon.OpenExternal, "새 탭에서 열기", () => { _rowPopup.IsOpen = false; _openUrl(entry.Url); }));
    panel.Children.Add(SafariTheme.CreatePopoverRow(SafariIcon.Reader, "북마크 편집…", () =>
    {
      _rowPopup.IsOpen = false;
      Dispatcher.BeginInvoke(() => PromptEditBookmark(entry), System.Windows.Threading.DispatcherPriority.Background);
    }));
    panel.Children.Add(SafariTheme.CreatePopoverRow(SafariIcon.Folder, "폴더 이동…", () =>
    {
      _rowPopup.IsOpen = false;
      Dispatcher.BeginInvoke(() =>
      {
        var dest = PromptChooseFolder("북마크 이동", excludeSubtreeOf: null);
        if (dest.Confirmed && BookmarkStore.MoveBookmarkToFolder(_catalog, entry.Id, dest.FolderId))
          PersistAndRefresh("북마크 폴더를 이동했습니다");
      }, System.Windows.Threading.DispatcherPriority.Background);
    }));
    panel.Children.Add(SafariTheme.CreatePopoverRow(SafariIcon.Link, "주소 복사", () =>
    {
      _rowPopup.IsOpen = false;
      System.Windows.Clipboard.SetText(entry.Url);
      _toastText.Text = "북마크 주소를 복사했습니다";
    }));
    panel.Children.Add(SafariTheme.CreatePopoverDivider());
    panel.Children.Add(SafariTheme.CreatePopoverRow(SafariIcon.ChevronUp, "위로 이동", () => { _rowPopup.IsOpen = false; ReorderBookmarkInFolder(entry, -1); }));
    panel.Children.Add(SafariTheme.CreatePopoverRow(SafariIcon.ChevronDown, "아래로 이동", () => { _rowPopup.IsOpen = false; ReorderBookmarkInFolder(entry, +1); }));
    panel.Children.Add(SafariTheme.CreatePopoverDivider());
    panel.Children.Add(SafariTheme.CreatePopoverRow(SafariIcon.Trash, "북마크 삭제", () =>
    {
      _rowPopup.IsOpen = false;
      BookmarkStore.Remove(_catalog, entry);
      PersistAndRefresh($"‘{entry.Name}’ 삭제됨");
    }, isDestructive: true));
    ShowGlassMenu(anchor, panel, 195);
  }

  private void ReorderBookmarkInFolder(BookmarkEntry entry, int delta)
  {
    var siblings = _catalog.Items
        .Select((item, idx) => (item, idx))
        .Where(x => string.Equals(x.item.FolderId ?? string.Empty, entry.FolderId ?? string.Empty, StringComparison.Ordinal))
        .ToList();
    var pos = siblings.FindIndex(x => x.item.Id == entry.Id);
    var targetPos = pos + delta;
    if (pos < 0 || targetPos < 0 || targetPos >= siblings.Count) return;
    var i1 = siblings[pos].idx;
    var i2 = siblings[targetPos].idx;
    (_catalog.Items[i1], _catalog.Items[i2]) = (_catalog.Items[i2], _catalog.Items[i1]);
    _sortMode = SortMode.Custom;
    _sortButtonText.Text = "정렬";
    PersistAndRefresh();
  }

  private void PromptCreateFolder(string? parentId)
  {
    var name = PromptText("새 북마크 폴더", "폴더 이름을 입력하세요:", "");
    if (string.IsNullOrWhiteSpace(name)) return;
    var created = BookmarkStore.CreateFolder(_catalog, name, parentId);
    if (parentId is not null) _expandedFolderIds.Add(parentId);
    _expandedFolderIds.Add(created.Id);
    PersistAndRefresh($"‘{created.Name}’ 폴더를 만들었습니다");
  }

  private void PromptAddBookmark(string? folderId)
  {
    var result = PromptBookmarkEditor("새 북마크 추가", "", "https://");
    if (result is null) return;
    BookmarkStore.AddOrUpdate(_catalog, result.Value.Name, result.Value.Url, folderId);
    PersistAndRefresh($"‘{result.Value.Name}’ 북마크를 추가했습니다");
  }

  private void PromptEditBookmark(BookmarkEntry entry)
  {
    var result = PromptBookmarkEditor("북마크 편집", entry.Name, entry.Url);
    if (result is null) return;
    var idx = _catalog.Items.FindIndex(x => x.Id == entry.Id);
    if (idx < 0) return;
    var newKey = BookmarkStore.CanonicalUrlKey(result.Value.Url);
    if (!string.IsNullOrEmpty(newKey)) _catalog.DeletedUrlKeys.Remove(newKey);
    _catalog.Items[idx] = entry with
    {
      Name = result.Value.Name,
      Url = result.Value.Url,
      Glyph = BookmarkStore.GlyphFor(result.Value.Name),
    };
    PersistAndRefresh("북마크를 수정했습니다");
  }

  private void RunCleanDuplicates()
  {
    var removed = BookmarkStore.CleanDuplicates(_catalog);
    PersistAndRefresh(removed > 0 ? $"중복 항목 {removed}개를 정리했습니다" : "정리할 중복 북마크가 없습니다");
  }

  private void ExportHtmlBookmarks()
  {
    using var picker = new System.Windows.Forms.SaveFileDialog
    {
      Title = "북마크 내보내기 (HTML)",
      Filter = "북마크 HTML (*.html)|*.html",
      FileName = $"Omyaru_Bookmarks_{DateTime.Now:yyyyMMdd}.html",
    };
    if (picker.ShowDialog(DwmBackdrop.OwnerOf(this)) != System.Windows.Forms.DialogResult.OK) return;
    try
    {
      BookmarkStore.ExportHtml(_catalog, picker.FileName);
      _toastText.Text = "HTML 북마크 내보내기 완료";
    }
    catch (Exception ex)
    {
      MessageBox.Show(this, "내보내기 오류:\n" + ex.Message, "북마크 내보내기");
    }
  }

  private void ImportHtmlBookmarks()
  {
    using var picker = new System.Windows.Forms.OpenFileDialog
    {
      Title = "북마크 가져오기 (Chrome · Whale · Edge · Safari HTML)",
      Filter = "북마크 HTML (*.html;*.htm)|*.html;*.htm|모든 파일 (*.*)|*.*",
      CheckFileExists = true,
    };
    if (picker.ShowDialog(DwmBackdrop.OwnerOf(this)) != System.Windows.Forms.DialogResult.OK) return;
    try
    {
      var (addedB, addedF) = BookmarkStore.ImportHtml(_catalog, picker.FileName, _selectedFolderId);
      foreach (var f in _catalog.Folders.Where(x => x.ParentId is null)) _expandedFolderIds.Add(f.Id);
      PersistAndRefresh($"북마크 {addedB}개 · 폴더 {addedF}개를 가져왔습니다");
    }
    catch (Exception ex)
    {
      MessageBox.Show(this, "가져오기 오류:\n" + ex.Message, "북마크 가져오기");
    }
  }

  private string? PromptText(string title, string prompt, string defaultText)
  {
    var dlg = new Window
    {
      Title = title,
      Owner = this,
      WindowStartupLocation = WindowStartupLocation.CenterOwner,
      Width = 400,
      Height = 176,
      ResizeMode = ResizeMode.NoResize,
      Background = SafariTheme.PopoverBg,
      Foreground = SafariTheme.TextPrimary,
      FontFamily = SafariTheme.UiFont,
    };
    dlg.SourceInitialized += (_, _) => DwmBackdrop.Apply(dlg, acrylic: true);
    var box = new TextBox
    {
      Text = defaultText,
      Height = 34,
      Background = SafariTheme.AddressFill,
      Foreground = SafariTheme.TextPrimary,
      CaretBrush = SafariTheme.Accent,
      BorderBrush = SafariTheme.SpecularBorder,
      VerticalContentAlignment = VerticalAlignment.Center,
      Padding = new Thickness(10, 0, 10, 0),
      Margin = new Thickness(0, 8, 0, 14),
    };
    string? result = null;
    var ok = SafariTheme.CreateGlassButton(SafariTheme.CreateGlyph("확인", 12.5, Brushes.White), () => { result = box.Text; dlg.DialogResult = true; }, "확인", 76, 32, 10, false, SafariTheme.AccentColor);
    var cancel = SafariTheme.CreateGlassButton(SafariTheme.CreateGlyph("취소", 12.5), () => dlg.DialogResult = false, "취소", 76, 32, 10, false);
    cancel.Margin = new Thickness(0, 0, 8, 0);
    box.KeyDown += (_, e) =>
    {
      if (e.Key == Key.Enter) { result = box.Text; dlg.DialogResult = true; }
      else if (e.Key == Key.Escape) dlg.DialogResult = false;
    };
    var btns = new StackPanel { Orientation = Orientation.Horizontal, HorizontalAlignment = HorizontalAlignment.Right };
    btns.Children.Add(cancel);
    btns.Children.Add(ok);
    dlg.Content = new StackPanel
    {
      Margin = new Thickness(18),
      Children = { new TextBlock { Text = prompt, FontSize = 12.5, Foreground = SafariTheme.TextSecondary }, box, btns },
    };
    dlg.Loaded += (_, _) => { box.Focus(); box.SelectAll(); };
    return dlg.ShowDialog() == true ? result : null;
  }

  private (string Name, string Url)? PromptBookmarkEditor(string title, string initialName, string initialUrl)
  {
    var dlg = new Window
    {
      Title = title,
      Owner = this,
      WindowStartupLocation = WindowStartupLocation.CenterOwner,
      Width = 440,
      Height = 236,
      ResizeMode = ResizeMode.NoResize,
      Background = SafariTheme.PopoverBg,
      Foreground = SafariTheme.TextPrimary,
      FontFamily = SafariTheme.UiFont,
    };
    dlg.SourceInitialized += (_, _) => DwmBackdrop.Apply(dlg, acrylic: true);
    var nameBox = new TextBox
    {
      Text = initialName,
      Height = 34,
      Background = SafariTheme.AddressFill,
      Foreground = SafariTheme.TextPrimary,
      CaretBrush = SafariTheme.Accent,
      BorderBrush = SafariTheme.SpecularBorder,
      VerticalContentAlignment = VerticalAlignment.Center,
      Padding = new Thickness(10, 0, 10, 0),
      Margin = new Thickness(0, 5, 0, 10),
    };
    var urlBox = new TextBox
    {
      Text = initialUrl,
      Height = 34,
      Background = SafariTheme.AddressFill,
      Foreground = SafariTheme.TextPrimary,
      CaretBrush = SafariTheme.Accent,
      BorderBrush = SafariTheme.SpecularBorder,
      VerticalContentAlignment = VerticalAlignment.Center,
      Padding = new Thickness(10, 0, 10, 0),
      Margin = new Thickness(0, 5, 0, 14),
    };
    (string, string)? output = null;
    void Accept()
    {
      if (!BookmarkStore.TryNormalizeUrl(urlBox.Text, out var url))
      {
        MessageBox.Show(dlg, "올바른 http:// 또는 https:// 주소를 입력하세요.", title);
        return;
      }
      var nm = string.IsNullOrWhiteSpace(nameBox.Text) ? url : nameBox.Text.Trim();
      output = (nm, url);
      dlg.DialogResult = true;
    }
    var ok = SafariTheme.CreateGlassButton(SafariTheme.CreateGlyph("저장", 12.5, Brushes.White), Accept, "저장", 76, 32, 10, false, SafariTheme.AccentColor);
    var cancel = SafariTheme.CreateGlassButton(SafariTheme.CreateGlyph("취소", 12.5), () => dlg.DialogResult = false, "취소", 76, 32, 10, false);
    cancel.Margin = new Thickness(0, 0, 8, 0);
    urlBox.KeyDown += (_, e) => { if (e.Key == Key.Enter) Accept(); else if (e.Key == Key.Escape) dlg.DialogResult = false; };
    nameBox.KeyDown += (_, e) => { if (e.Key == Key.Enter) urlBox.Focus(); else if (e.Key == Key.Escape) dlg.DialogResult = false; };
    var btns = new StackPanel { Orientation = Orientation.Horizontal, HorizontalAlignment = HorizontalAlignment.Right };
    btns.Children.Add(cancel);
    btns.Children.Add(ok);
    dlg.Content = new StackPanel
    {
      Margin = new Thickness(18),
      Children =
      {
        new TextBlock { Text = "이름", FontSize = 12, Foreground = SafariTheme.TextSecondary },
        nameBox,
        new TextBlock { Text = "URL", FontSize = 12, Foreground = SafariTheme.TextSecondary },
        urlBox,
        btns,
      },
    };
    dlg.Loaded += (_, _) => { nameBox.Focus(); nameBox.SelectAll(); };
    return dlg.ShowDialog() == true ? output : null;
  }

  private (bool Confirmed, string? FolderId) PromptChooseFolder(string title, string? excludeSubtreeOf)
  {
    var dlg = new Window
    {
      Title = title,
      Owner = this,
      WindowStartupLocation = WindowStartupLocation.CenterOwner,
      Width = 380,
      Height = 420,
      ResizeMode = ResizeMode.NoResize,
      Background = SafariTheme.PopoverBg,
      Foreground = SafariTheme.TextPrimary,
      FontFamily = SafariTheme.UiFont,
    };
    SafariTheme.ApplyMacScrollBarStyles(dlg.Resources);
    dlg.SourceInitialized += (_, _) => DwmBackdrop.Apply(dlg, acrylic: true);

    string? chosen = null;
    var confirmed = false;
    var list = new StackPanel { Margin = new Thickness(10) };

    void AddOption(string? id, string label, int depth)
    {
      var row = SafariTheme.CreatePopoverRow(SafariIcon.FolderFilled, new string(' ', depth * 3) + label, () =>
      {
        chosen = id;
        confirmed = true;
        dlg.DialogResult = true;
      });
      list.Children.Add(row);
    }

    AddOption(null, "PC 북마크 (최상위)", 0);
    void Walk(string? parentId, int depth)
    {
      foreach (var f in _catalog.Folders
          .Where(x => string.Equals(x.ParentId ?? string.Empty, parentId ?? string.Empty, StringComparison.Ordinal))
          .OrderBy(x => x.Order))
      {
        if (excludeSubtreeOf is not null && BookmarkStore.IsDescendantOrSelf(_catalog, f.Id, excludeSubtreeOf))
          continue;
        AddOption(f.Id, f.Name, depth);
        Walk(f.Id, depth + 1);
      }
    }
    Walk(null, 1);

    dlg.Content = new ScrollViewer { VerticalScrollBarVisibility = ScrollBarVisibility.Auto, Content = list };
    dlg.ShowDialog();
    return (confirmed, chosen);
  }
}
