using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Media.Animation;
using Brushes = System.Windows.Media.Brushes;
using Color = System.Windows.Media.Color;
using Cursors = System.Windows.Input.Cursors;
using HorizontalAlignment = System.Windows.HorizontalAlignment;
using Orientation = System.Windows.Controls.Orientation;

namespace PrivacyBrowser.Desktop.Safari;

/// <summary>
/// Hardware-accelerated WPF macOS 27 Liquid Glass Settings window — replaces the
/// multi-HWND WinForms SettingsForm in the Safari shell so opening, scrolling,
/// and resizing never flash grey/transparent GDI rectangles.
/// </summary>
internal sealed class SafariSettingsWindow : Window
{
  private readonly DesktopPreferences _working;
  private readonly Action<DesktopPreferences> _apply;
  private readonly Func<Task> _openSync;
  private readonly Func<DesktopPreferences, Task<string?>> _chooseAudioOutput;
  private readonly StackPanel _sections = new();
  private TextBlock? _audioSubtitle;

  internal SafariSettingsWindow(
      Window owner,
      DesktopPreferences preferences,
      Action<DesktopPreferences> apply,
      Func<Task> openSync,
      Func<DesktopPreferences, Task<string?>> chooseAudioOutput)
  {
    _working = preferences.Copy();
    _apply = apply;
    _openSync = openSync;
    _chooseAudioOutput = chooseAudioOutput;

    Title = "설정 — Omyaru";
    Owner = owner;
    WindowStartupLocation = WindowStartupLocation.CenterOwner;
    Width = 780;
    Height = 760;
    MinWidth = 620;
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
        e.Handled = true;
        SaveAndClose();
      }
    };

    BuildVisualTree();
  }

  private void BuildVisualTree()
  {
    var root = new Grid { Background = SafariTheme.WindowBg };
    root.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
    root.RowDefinitions.Add(new RowDefinition { Height = new GridLength(1, GridUnitType.Star) });

    // Top Header
    var headerBar = new Border
    {
      BorderBrush = SafariTheme.Divider,
      BorderThickness = new Thickness(0, 0, 0, 1),
      Padding = new Thickness(28, 18, 28, 14),
      Background = new SolidColorBrush(Color.FromArgb(245, 22, 24, 30)),
    };
    var headerDock = new DockPanel();
    var doneBtn = SafariTheme.CreateGlassButton(
        SafariTheme.CreateGlyph("완료", 13, Brushes.White),
        SaveAndClose,
        "설정 저장 후 닫기 (Esc)",
        82, 34, 17, transparentIdle: false, accentFill: SafariTheme.AccentColor);
    DockPanel.SetDock(doneBtn, Dock.Right);
    headerDock.Children.Add(doneBtn);

    var titleRow = new StackPanel { Orientation = Orientation.Horizontal, VerticalAlignment = VerticalAlignment.Center };
    titleRow.Children.Add(SafariIcons.Create(SafariIcon.Settings, 22, SafariTheme.Accent, 1.7));
    titleRow.Children.Add(new TextBlock
    {
      Text = "설정",
      FontSize = 22,
      FontWeight = FontWeights.Bold,
      Foreground = SafariTheme.TextPrimary,
      Margin = new Thickness(10, 0, 0, 0),
      VerticalAlignment = VerticalAlignment.Center,
    });
    headerDock.Children.Add(titleRow);
    headerBar.Child = headerDock;
    Grid.SetRow(headerBar, 0);
    root.Children.Add(headerBar);

    // Scrollable Sections Body
    _sections.Margin = new Thickness(28, 12, 28, 32);
    var scroll = new ScrollViewer
    {
      VerticalScrollBarVisibility = ScrollBarVisibility.Auto,
      HorizontalScrollBarVisibility = ScrollBarVisibility.Disabled,
      Content = _sections,
    };
    Grid.SetRow(scroll, 1);
    root.Children.Add(scroll);

    AddSection("일반",
        ChoiceRow(SafariIcon.TabOverview, "시작 동작", "마지막 일반 탭 복원 또는 새 탭으로 시작",
            () => _working.RestoreTabs ? "복원" : "새 탭",
            () => { _working.RestoreTabs = !_working.RestoreTabs; ApplyLive(); return _working.RestoreTabs ? "복원" : "새 탭"; }),
        ToggleRow(SafariIcon.Undo, "마지막 탭 복원", "브라우저를 다시 열어도 탭과 미리보기를 유지합니다.",
            () => _working.RestoreTabs, value => { _working.RestoreTabs = value; ApplyLive(); }));

    AddSection("검색",
        ChoiceRow(SafariIcon.Search, "검색 엔진", "주소창에서 검색할 기본 검색 엔진",
            () => _working.SearchEngine,
            () => { var next = CycleSearchEngine(); ApplyLive(); return next; }));

    AddSection("모양",
        ToggleRow(SafariIcon.TabGroup, "탭 목록을 주소창 위로 배치", "켜면 탭 목록이 URL 입력창 위쪽에 표시됩니다.",
            () => _working.TabsAboveAddressBar, value => { _working.TabsAboveAddressBar = value; ApplyLive(); }),
        ToggleRow(SafariIcon.StarOutline, "주소창 아래 북마크 모음 표시", "끄면 URL 입력창 아래의 북마크 모음 막대를 숨깁니다.",
            () => _working.ShowFavoritesBar, value => { _working.ShowFavoritesBar = value; ApplyLive(); }),
        ToggleRow(SafariIcon.Globe, "북마크 아이콘만 표시", "북마크 막대에서 이름을 숨기고 사이트 아이콘만 표시합니다.",
            () => _working.BookmarkIconsOnly, value => { _working.BookmarkIconsOnly = value; ApplyLive(); }),
        ToggleRow(SafariIcon.Sidebar, "상태 막대", "프로필과 동기화 상태를 창 아래에 표시합니다.",
            () => _working.ShowStatusBar, value => { _working.ShowStatusBar = value; ApplyLive(); }),
        ToggleRow(SafariIcon.Capture, "캡처 화면 양옆 여백", "영역 선택 중 페이지 좌우에 여백을 표시합니다.",
            () => _working.CaptureSideMargins, value => { _working.CaptureSideMargins = value; ApplyLive(); }),
        ToggleRow(SafariIcon.PageMenu, "컴팩트 도구 모음", "탐색 영역의 세로 공간을 줄입니다.",
            () => _working.CompactToolbar, value => { _working.CompactToolbar = value; ApplyLive(); }),
        ActionRow(SafariIcon.Settings, "주소창 아래 메뉴 버튼 편집", "자주 쓰는 메뉴를 바로가기 버튼으로 선택합니다.", ShowToolbarShortcutsDialog));

    AddSection("탭",
        ToggleRow(SafariIcon.OpenExternal, "링크를 새 탭에서 열기", "새 창 요청을 현재 프로필의 새 탭으로 엽니다.",
            () => _working.OpenLinksInNewTab, value => { _working.OpenLinksInNewTab = value; ApplyLive(); }),
        ToggleRow(SafariIcon.Duplicate, "탭 미리보기", "탭 목록과 동기화 파일에 썸네일을 유지합니다.",
            () => _working.ShowTabThumbnails, value => { _working.ShowTabThumbnails = value; ApplyLive(); }));

    AddSection("브라우징",
        ToggleRow(SafariIcon.Link, "자동 HTTPS 업그레이드", "주소 입력 시 가능한 경우 HTTPS를 먼저 사용합니다.",
            () => _working.AutomaticHttps, value => { _working.AutomaticHttps = value; ApplyLive(); }),
        ToggleRow(SafariIcon.Reader, "전체 URL 표시", "주소창에 도메인 대신 전체 주소를 표시합니다.",
            () => _working.ShowFullUrls, value => { _working.ShowFullUrls = value; ApplyLive(); }));

    var audioRow = ActionRow(
        SafariIcon.Audio,
        "출력 장치",
        string.IsNullOrWhiteSpace(_working.AudioOutputLabel) ? "시스템 기본 장치" : _working.AudioOutputLabel,
        async () =>
        {
          var chosenLabel = await _chooseAudioOutput(_working);
          if (!string.IsNullOrWhiteSpace(chosenLabel) && _audioSubtitle is not null)
            _audioSubtitle.Text = chosenLabel;
          ApplyLive();
        },
        out _audioSubtitle);
    AddSection("소리", audioRow);

    AddSection("동기화",
        ActionRow(SafariIcon.Sync, "Android / iOS / Windows 동기화", "프로필·그룹·탭·순서·미리보기를 암호화해 동기화합니다.", async () =>
        {
          ApplyLive();
          await _openSync();
        }));

    AddSection("개인정보 보호",
        ToggleRow(SafariIcon.Private, "추적 방지", "분석·광고 추적 요청을 차단하는 보호 계층입니다.",
            () => _working.BlockTrackers, value => { _working.BlockTrackers = value; ApplyLive(); }),
        ToggleRow(SafariIcon.Distraction, "진단 데이터 수집", "끄면 앱 사용 통계나 진단 데이터를 전송하지 않습니다.",
            () => _working.CollectDiagnostics, value => { _working.CollectDiagnostics = value; ApplyLive(); }));

    AddSection("도움말 및 정보",
        InfoRow(SafariIcon.Settings, "Omyaru", "Windows 0.5.9 · macOS Safari 스타일 GPU 데스크톱"));

    Content = root;
  }

  private void AddSection(string title, params UIElement[] rows)
  {
    _sections.Children.Add(new TextBlock
    {
      Text = title,
      FontSize = 12,
      FontWeight = FontWeights.SemiBold,
      Foreground = SafariTheme.TextMuted,
      Margin = new Thickness(10, 14, 10, 6),
    });

    var stack = new StackPanel();
    for (var i = 0; i < rows.Length; i++)
    {
      stack.Children.Add(rows[i]);
      if (i < rows.Length - 1)
      {
        stack.Children.Add(new Border
        {
          Height = 1,
          Background = SafariTheme.Divider,
          Margin = new Thickness(52, 0, 14, 0),
        });
      }
    }

    _sections.Children.Add(new Border
    {
      CornerRadius = new CornerRadius(14),
      Background = SafariTheme.CardFill,
      BorderBrush = SafariTheme.CapsuleBorder,
      BorderThickness = new Thickness(1),
      Padding = new Thickness(4, 2, 4, 2),
      Child = stack,
    });
  }

  private static Grid BaseRow(SafariIcon icon, string title, string subtitle, out TextBlock subtitleBlock)
  {
    var grid = new Grid { MinHeight = 58, Margin = new Thickness(8, 2, 10, 2) };
    grid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(42) });
    grid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
    grid.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });

    var badge = new Border
    {
      Width = 30,
      Height = 30,
      CornerRadius = new CornerRadius(8),
      Background = SafariTheme.AccentSoft,
      VerticalAlignment = VerticalAlignment.Center,
      HorizontalAlignment = HorizontalAlignment.Left,
      Child = SafariIcons.Create(icon, 15, SafariTheme.Accent, 1.6),
    };
    Grid.SetColumn(badge, 0);
    grid.Children.Add(badge);

    var textStack = new StackPanel { VerticalAlignment = VerticalAlignment.Center, Margin = new Thickness(2, 6, 14, 6) };
    textStack.Children.Add(new TextBlock
    {
      Text = title,
      FontSize = 13.5,
      FontWeight = FontWeights.SemiBold,
      Foreground = SafariTheme.TextPrimary,
    });
    subtitleBlock = new TextBlock
    {
      Text = subtitle,
      FontSize = 11.5,
      Foreground = SafariTheme.TextMuted,
      TextWrapping = TextWrapping.Wrap,
      Margin = new Thickness(0, 2, 0, 0),
    };
    textStack.Children.Add(subtitleBlock);
    Grid.SetColumn(textStack, 1);
    grid.Children.Add(textStack);
    return grid;
  }

  private static UIElement ToggleRow(SafariIcon icon, string title, string subtitle, Func<bool> read, Action<bool> write)
  {
    var row = BaseRow(icon, title, subtitle, out _);
    var toggle = CreateMacToggle(read(), write);
    Grid.SetColumn(toggle, 2);
    row.Children.Add(toggle);
    return row;
  }

  private static UIElement ChoiceRow(SafariIcon icon, string title, string subtitle, Func<string> read, Func<string> advance)
  {
    var row = BaseRow(icon, title, subtitle, out _);
    var label = SafariTheme.CreateGlyph(read(), 12.5, SafariTheme.TextPrimary);
    var btn = SafariTheme.CreateGlassButton(label, () => label.Text = advance(), title, 118, 32, 16, transparentIdle: false);
    btn.BorderBrush = SafariTheme.SpecularBorder;
    btn.BorderThickness = new Thickness(1);
    Grid.SetColumn(btn, 2);
    row.Children.Add(btn);
    return row;
  }

  private static UIElement ActionRow(SafariIcon icon, string title, string subtitle, Func<Task> action) =>
      ActionRow(icon, title, subtitle, action, out _);

  private static UIElement ActionRow(SafariIcon icon, string title, string subtitle, Func<Task> action, out TextBlock subtitleBlock)
  {
    var row = BaseRow(icon, title, subtitle, out subtitleBlock);
    var btn = SafariTheme.CreateGlassButton(
        SafariIcons.Create(SafariIcon.ChevronRight, 14, SafariTheme.TextSecondary, 1.8),
        () => _ = action(),
        title,
        34, 30, 15, transparentIdle: false);
    Grid.SetColumn(btn, 2);
    row.Children.Add(btn);

    var wrap = new Border { Background = Brushes.Transparent, Cursor = Cursors.Hand, CornerRadius = new CornerRadius(10), Child = row };
    wrap.MouseLeftButtonUp += (_, e) => { e.Handled = true; _ = action(); };
    return wrap;
  }

  private static UIElement InfoRow(SafariIcon icon, string title, string subtitle) => BaseRow(icon, title, subtitle, out _);

  private static Border CreateMacToggle(bool initial, Action<bool> onChanged)
  {
    var isOn = initial;
    var trackBrush = new SolidColorBrush(isOn ? SafariTheme.AccentColor : Color.FromRgb(64, 67, 78));
    var thumbTranslate = new TranslateTransform(isOn ? 18 : 0, 0);

    var thumb = new Border
    {
      Width = 20,
      Height = 20,
      CornerRadius = new CornerRadius(10),
      Background = Brushes.White,
      HorizontalAlignment = HorizontalAlignment.Left,
      VerticalAlignment = VerticalAlignment.Center,
      Margin = new Thickness(3, 0, 0, 0),
      RenderTransform = thumbTranslate,
      Effect = SafariTheme.GlassShadow(6, 0.35, 1),
    };

    var track = new Border
    {
      Width = 44,
      Height = 26,
      CornerRadius = new CornerRadius(13),
      Background = trackBrush,
      Cursor = Cursors.Hand,
      VerticalAlignment = VerticalAlignment.Center,
      Child = thumb,
    };

    track.MouseLeftButtonUp += (_, e) =>
    {
      e.Handled = true;
      isOn = !isOn;
      SafariTheme.AnimateColor(trackBrush, isOn ? SafariTheme.AccentColor : Color.FromRgb(64, 67, 78), 150);
      thumbTranslate.BeginAnimation(TranslateTransform.XProperty, new DoubleAnimation(isOn ? 18 : 0, TimeSpan.FromMilliseconds(150))
      {
        EasingFunction = new CubicEase { EasingMode = EasingMode.EaseOut },
      });
      onChanged(isOn);
    };
    return track;
  }

  private Task ShowToolbarShortcutsDialog()
  {
    var dlg = new Window
    {
      Title = "도구 모음 버튼 — Omyaru",
      Owner = this,
      WindowStartupLocation = WindowStartupLocation.CenterOwner,
      Width = 360,
      Height = 460,
      ResizeMode = ResizeMode.NoResize,
      Background = SafariTheme.PopoverBg,
      Foreground = SafariTheme.TextPrimary,
      FontFamily = SafariTheme.UiFont,
    };
    SafariTheme.ApplyMacScrollBarStyles(dlg.Resources);
    dlg.SourceInitialized += (_, _) => DwmBackdrop.Apply(dlg, acrylic: true);

    var selected = _working.ToolbarShortcuts.Split(',', StringSplitOptions.RemoveEmptyEntries).ToHashSet();
    var listPanel = new StackPanel { Margin = new Thickness(12, 8, 12, 8) };
    foreach (var label in DesktopToolbarShortcuts.Labels)
    {
      var item = label;
      var cb = new System.Windows.Controls.CheckBox
      {
        Content = item,
        IsChecked = selected.Contains(item),
        Foreground = SafariTheme.TextPrimary,
        FontSize = 13,
        Margin = new Thickness(6, 6, 6, 6),
        Cursor = Cursors.Hand,
      };
      cb.Checked += (_, _) => selected.Add(item);
      cb.Unchecked += (_, _) => selected.Remove(item);
      listPanel.Children.Add(cb);
    }

    var grid = new Grid { Margin = new Thickness(12) };
    grid.RowDefinitions.Add(new RowDefinition { Height = new GridLength(1, GridUnitType.Star) });
    grid.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });

    var scroll = new ScrollViewer { VerticalScrollBarVisibility = ScrollBarVisibility.Auto, Content = listPanel };
    Grid.SetRow(scroll, 0);
    grid.Children.Add(scroll);

    var saveBtn = SafariTheme.CreateGlassButton(
        SafariTheme.CreateGlyph("저장", 12.5, Brushes.White),
        () =>
        {
          _working.ToolbarShortcuts = string.Join(",", DesktopToolbarShortcuts.Labels.Where(selected.Contains));
          ApplyLive();
          dlg.DialogResult = true;
        },
        "저장", double.NaN, 36, 10, false, SafariTheme.AccentColor);
    saveBtn.Margin = new Thickness(4, 10, 4, 2);
    Grid.SetRow(saveBtn, 1);
    grid.Children.Add(saveBtn);

    dlg.Content = grid;
    dlg.ShowDialog();
    return Task.CompletedTask;
  }

  private string CycleSearchEngine()
  {
    string[] engines = ["DuckDuckGo", "Google", "Bing"];
    var index = Array.IndexOf(engines, _working.SearchEngine);
    _working.SearchEngine = engines[(index + 1 + engines.Length) % engines.Length];
    return _working.SearchEngine;
  }

  private void ApplyLive()
  {
    DesktopPreferencesStore.Save(_working);
    _apply(_working.Copy());
  }

  private void SaveAndClose()
  {
    ApplyLive();
    if (System.Windows.Interop.ComponentDispatcher.IsThreadModal) DialogResult = true;
    else Close();
  }
}
