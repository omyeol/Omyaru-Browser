using System.Diagnostics;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Input;
using System.Windows.Media;
using Brushes = System.Windows.Media.Brushes;
using Color = System.Windows.Media.Color;
using Cursors = System.Windows.Input.Cursors;
using HorizontalAlignment = System.Windows.HorizontalAlignment;
using MessageBox = System.Windows.MessageBox;
using Orientation = System.Windows.Controls.Orientation;

namespace PrivacyBrowser.Desktop.Safari;

internal sealed partial class SafariBrowserWindow
{
  private void ShowGlassPopup(UIElement anchor, UIElement content, double minWidth = 240, double maxWidth = 360)
  {
    _activePopup.IsOpen = false;
    _activePopup.PlacementTarget = anchor;
    _activePopup.Placement = PlacementMode.Bottom;
    _activePopup.VerticalOffset = 4;
    var shell = SafariTheme.CreatePopoverShell(content, minWidth, maxWidth);
    var slide = new TranslateTransform(0, -6);
    shell.RenderTransform = slide;
    shell.Opacity = 0;
    _activePopup.Child = shell;
    _activePopup.IsOpen = true;
    SafariTheme.AnimateDouble(shell, OpacityProperty, 1.0, 105);
    slide.BeginAnimation(TranslateTransform.YProperty, new System.Windows.Media.Animation.DoubleAnimation(0, TimeSpan.FromMilliseconds(125))
    {
      EasingFunction = new System.Windows.Media.Animation.CubicEase { EasingMode = System.Windows.Media.Animation.EasingMode.EaseOut },
    });
  }

  private void ClosePopup() => _activePopup.IsOpen = false;

  /// <summary>
  /// Closes the floating glass popup and yields to WPF's render pass (Background priority)
  /// BEFORE opening any modal dialog or companion window, preventing the popup from
  /// staying frozen behind the newly opened window.
  /// </summary>
  private void ClosePopupThen(Action action)
  {
    _activePopup.IsOpen = false;
    Dispatcher.BeginInvoke(action, System.Windows.Threading.DispatcherPriority.Background);
  }

  private void ClosePopupThenAsync(Func<Task> action)
  {
    _activePopup.IsOpen = false;
    Dispatcher.BeginInvoke(async () => await action(), System.Windows.Threading.DispatcherPriority.Background);
  }

  // 1. macOS 27 Safari Page Menu Popover (Reader Mode + Theme Swatches + Fonts + Distraction Control + Zoom!)
  private void ShowPageMenuPopup(UIElement anchor)
  {
    var panel = new StackPanel();

    // Primary macOS blue Reader Toggle Button (matches 14:23 in the video!)
    var readerBtn = SafariTheme.CreateGlassButton(
        SafariTheme.CreateGlyph(_readerActive ? "읽기 도구 끄기" : "읽기 도구 보기", 12.5, Brushes.White),
        () => { ClosePopup(); _ = ToggleReaderModeAsync(); },
        "읽기 도구 전환 (Alt+R)",
        double.NaN, 34, 10, transparentIdle: false, accentFill: SafariTheme.AccentColor);
    readerBtn.Margin = new Thickness(4, 2, 4, 8);
    panel.Children.Add(readerBtn);

    // 4 Circular Safari Reader Theme Swatches (White, Sepia, Charcoal, Black — matches 14:23!)
    var swatchRow = new StackPanel { Orientation = Orientation.Horizontal, HorizontalAlignment = HorizontalAlignment.Center, Margin = new Thickness(0, 2, 0, 8) };
    (string Key, string Name, Color Fill)[] themes =
    [
      ("white", "화이트", Color.FromRgb(250, 250, 252)),
      ("sepia", "세피아", Color.FromRgb(245, 236, 217)),
      ("charcoal", "차콜 그레이", Color.FromRgb(48, 50, 58)),
      ("black", "블랙", Color.FromRgb(14, 15, 18)),
    ];
    foreach (var (key, name, fill) in themes)
    {
      var selected = _readerTheme == key;
      var circle = new Border
      {
        Width = 30,
        Height = 30,
        CornerRadius = new CornerRadius(15),
        Background = new SolidColorBrush(fill),
        BorderBrush = selected ? SafariTheme.Accent : SafariTheme.SpecularBorder,
        BorderThickness = new Thickness(selected ? 2.5 : 1),
        Margin = new Thickness(6, 0, 6, 0),
        Cursor = Cursors.Hand,
        ToolTip = $"읽기 테마: {name}",
      };
      circle.MouseLeftButtonUp += async (_, e) =>
      {
        e.Handled = true;
        _readerTheme = key;
        if (!_readerActive) await ToggleReaderModeAsync();
        else await UpdateReaderStyleAsync();
        ShowPageMenuPopup(anchor);
      };
      swatchRow.Children.Add(circle);
    }
    panel.Children.Add(swatchRow);

    // Font Size + Font Selector Row
    var fontRow = new UniformGrid { Columns = 3, Margin = new Thickness(4, 0, 4, 6) };
    var smaller = SafariTheme.CreateGlassButton(SafariTheme.CreateGlyph("가-", 12), () => { _readerFontSize = Math.Max(13, _readerFontSize - 2); _ = UpdateReaderStyleAsync(); }, "글꼴 작게", double.NaN, 28, 8, false);
    smaller.Margin = new Thickness(0, 0, 3, 0);
    var cycleFont = SafariTheme.CreateGlassButton(SafariTheme.CreateGlyph(_readerFont == "Georgia" ? "명조" : "고딕", 11.5), () =>
    {
      _readerFont = _readerFont == "Segoe UI" ? "Georgia" : "Segoe UI";
      _ = UpdateReaderStyleAsync();
      ShowPageMenuPopup(anchor);
    }, "읽기 글꼴 전환", double.NaN, 28, 8, false);
    cycleFont.Margin = new Thickness(3, 0, 3, 0);
    var larger = SafariTheme.CreateGlassButton(SafariTheme.CreateGlyph("가+", 12.5), () => { _readerFontSize = Math.Min(28, _readerFontSize + 2); _ = UpdateReaderStyleAsync(); }, "글꼴 크게", double.NaN, 28, 8, false);
    larger.Margin = new Thickness(3, 0, 0, 0);
    fontRow.Children.Add(smaller);
    fontRow.Children.Add(cycleFont);
    fontRow.Children.Add(larger);
    panel.Children.Add(fontRow);

    panel.Children.Add(SafariTheme.CreatePopoverDivider());

    // macOS 27 Distraction Control (Particle Dissolve!)
    panel.Children.Add(SafariTheme.CreatePopoverRow(
        SafariIcon.Distraction, _distractionActive ? "방해 요소 가리기 완료" : "방해 요소 가리기 (Distraction Control)",
        () => { ClosePopup(); _ = ToggleDistractionControlAsync(); },
        isSelected: _distractionActive));
    if (_distractionHiddenCount > 0)
    {
      panel.Children.Add(SafariTheme.CreatePopoverRow(
          SafariIcon.Undo, $"숨긴 항목 {_distractionHiddenCount}개 모두 복원",
          () => { ClosePopup(); _ = RestoreDistractionHiddenAsync(); }));
    }

    panel.Children.Add(SafariTheme.CreatePopoverDivider());

    // Zoom Controls Row
    var zoomPct = Math.Round((ActiveWebView()?.ZoomFactor ?? 1.0) * 100);
    var zoomRow = new Grid { Margin = new Thickness(4, 2, 4, 2) };
    zoomRow.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
    zoomRow.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
    zoomRow.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
    zoomRow.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
    var zLabel = new TextBlock { Text = "페이지 확대/축소", FontSize = 12, Foreground = SafariTheme.TextSecondary, VerticalAlignment = VerticalAlignment.Center, Margin = new Thickness(4, 0, 0, 0) };
    var zMinus = SafariTheme.CreateGlassButton(SafariIcons.Create(SafariIcon.Minus, 13), () => { AdjustZoom(-0.1); ShowPageMenuPopup(anchor); }, "축소", 28, 26, 8, false);
    var zVal = SafariTheme.CreateGlassButton(SafariTheme.CreateGlyph($"{zoomPct}%", 11.5), () => { ResetZoom(); ShowPageMenuPopup(anchor); }, "100% 초기화", 48, 26, 8, false);
    zVal.Margin = new Thickness(3, 0, 3, 0);
    var zPlus = SafariTheme.CreateGlassButton(SafariIcons.Create(SafariIcon.Plus, 13), () => { AdjustZoom(+0.1); ShowPageMenuPopup(anchor); }, "확대", 28, 26, 8, false);
    Grid.SetColumn(zLabel, 0);
    Grid.SetColumn(zMinus, 1);
    Grid.SetColumn(zVal, 2);
    Grid.SetColumn(zPlus, 3);
    zoomRow.Children.Add(zLabel);
    zoomRow.Children.Add(zMinus);
    zoomRow.Children.Add(zVal);
    zoomRow.Children.Add(zPlus);
    panel.Children.Add(zoomRow);

    panel.Children.Add(SafariTheme.CreatePopoverDivider());
    panel.Children.Add(SafariTheme.CreatePopoverRow(SafariIcon.Search, "페이지 내 찾기…", () => { ClosePopup(); OpenFindBar(); }, "Ctrl+F"));
    panel.Children.Add(SafariTheme.CreatePopoverRow(SafariIcon.Link, "현재 링크 복사", () =>
    {
      ClosePopup();
      var url = ActiveTab()?.Url;
      if (!string.IsNullOrWhiteSpace(url) && url != "about:blank")
      {
        System.Windows.Clipboard.SetText(url);
        _syncStatus = "현재 링크를 클립보드에 복사했습니다";
        UpdateStatusText();
      }
    }));

    ShowGlassPopup(anchor, panel, 280, 310);
  }

  // 2. Color-Coded macOS Profile Switcher Popover
  private void ShowProfilePopup(UIElement anchor)
  {
    var panel = new StackPanel();
    panel.Children.Add(new TextBlock
    {
      Text = "프로필 전환",
      FontSize = 11.5,
      FontWeight = FontWeights.SemiBold,
      Foreground = SafariTheme.TextMuted,
      Margin = new Thickness(8, 4, 8, 6),
    });
    foreach (var profile in _state.Profiles)
    {
      var selected = profile.Id == _state.ActiveProfileId;
      panel.Children.Add(SafariTheme.CreatePopoverRow(
          selected ? SafariIcon.Check : SafariIcon.Profile,
          profile.Name,
          () =>
          {
            ClosePopup();
            _state.ActiveProfileId = profile.Id;
            _state.ActiveGroupId = null;
            EnsureWorkspaceTab();
            RefreshWorkspaceUi();
            _ = ActivateCurrentTabAsync();
          },
          isSelected: selected));
    }
    panel.Children.Add(SafariTheme.CreatePopoverDivider());
    panel.Children.Add(SafariTheme.CreatePopoverRow(SafariIcon.Plus, "새 프로필 만들기…", () => ClosePopupThen(PromptCreateProfile)));
    panel.Children.Add(SafariTheme.CreatePopoverRow(SafariIcon.Private, "비공개 브라우징 열기", () => { ClosePopup(); SwitchTabGroup(PrivateGroupId); }, "Ctrl+Shift+N"));
    ShowGlassPopup(anchor, panel, 230, 270);
  }

  // 3. Extensions Popover (1-Click Store Install + Installed Extensions Popup/Options)
  private async Task ShowExtensionsPopupAsync(UIElement anchor)
  {
    CloseExtensionPopup();
    var panel = new StackPanel();
    var currentUrl = ActiveTab()?.Url ?? string.Empty;
    if (ExtensionManagerForm.IsSupportedStoreOrPackageUrl(currentUrl))
    {
      var installRow = new StackPanel { Orientation = Orientation.Horizontal, HorizontalAlignment = HorizontalAlignment.Center, VerticalAlignment = VerticalAlignment.Center };
      installRow.Children.Add(SafariIcons.Create(SafariIcon.Plus, 13, Brushes.White, 1.9));
      installRow.Children.Add(new TextBlock { Text = "현재 스토어 페이지 확장 설치", FontSize = 12, Foreground = Brushes.White, Margin = new Thickness(6, 0, 0, 0), VerticalAlignment = VerticalAlignment.Center });
      var installNow = SafariTheme.CreateGlassButton(
          installRow,
          () => ClosePopupThenAsync(() => InstallExtensionFromStoreUrlAsync(currentUrl)),
          "현재 스토어 확장 바로 설치",
          double.NaN, 34, 10, transparentIdle: false, accentFill: SafariTheme.AccentColor);
      installNow.Margin = new Thickness(4, 2, 4, 8);
      panel.Children.Add(installNow);
    }

    var view = ActiveWebView()?.CoreWebView2 is not null
        ? ActiveWebView()
        : _views.Values.FirstOrDefault(v => v.CoreWebView2 is not null);
    if (view?.CoreWebView2 is not null)
    {
      try
      {
        var installed = await view.CoreWebView2.Profile.GetBrowserExtensionsAsync();
        var enabled = installed.Where(e => e.IsEnabled).OrderBy(e => e.Name, StringComparer.CurrentCultureIgnoreCase).ToList();
        panel.Children.Add(new TextBlock
        {
          Text = $"설치된 확장 프로그램 ({enabled.Count})",
          FontSize = 11.5,
          FontWeight = FontWeights.SemiBold,
          Foreground = SafariTheme.TextMuted,
          Margin = new Thickness(8, 2, 8, 6),
        });
        if (enabled.Count == 0)
        {
          panel.Children.Add(new TextBlock { Text = "설치된 확장이 없습니다.", FontSize = 12, Foreground = SafariTheme.TextMuted, Margin = new Thickness(10, 4, 10, 6) });
        }
        else
        {
          foreach (var ext in enabled.Take(12))
          {
            var ep = ExtensionManagerForm.GetExtensionEndpoints(_state.ActiveProfileId, ext.Id);
            var extName = ext.Name;
            var hasPopup = !string.IsNullOrWhiteSpace(ep.PopupUrl);
            var hasOptions = !string.IsNullOrWhiteSpace(ep.OptionsUrl);
            panel.Children.Add(SafariTheme.CreatePopoverRow(
                SafariIcon.Extensions, extName,
                () =>
                {
                  if (hasPopup)
                    ClosePopupThenAsync(() => ShowExtensionPopupWindowAsync(extName, ep.PopupUrl!, ep.OptionsUrl));
                  else if (hasOptions)
                  {
                    ClosePopup();
                    _ = CreateTabAsync(ep.OptionsUrl!);
                  }
                  else
                    ClosePopupThenAsync(ShowExtensionManagerAsync);
                },
                shortcut: hasPopup ? "팝업" : hasOptions ? "옵션" : null));
          }
        }
      }
      catch { }
    }

    panel.Children.Add(SafariTheme.CreatePopoverDivider());
    panel.Children.Add(SafariTheme.CreatePopoverRow(SafariIcon.Settings, "확장 프로그램 관리…", () => ClosePopupThenAsync(ShowExtensionManagerAsync)));
    panel.Children.Add(SafariTheme.CreatePopoverRow(SafariIcon.Globe, "Chrome 웹 스토어 열기", () => { ClosePopup(); _ = CreateTabAsync("https://chromewebstore.google.com/"); }));
    panel.Children.Add(SafariTheme.CreatePopoverRow(SafariIcon.OpenExternal, "Firefox 부가기능 열기", () => { ClosePopup(); _ = CreateTabAsync("https://addons.mozilla.org/ko/firefox/extensions/"); }));
    ShowGlassPopup(anchor, panel, 280, 340);
  }

  private void CloseExtensionPopup()
  {
    if (_extensionPopup is null) return;
    var popup = _extensionPopup;
    _extensionPopup = null;
    popup.CloseSafely();
  }

  private async Task ShowExtensionPopupWindowAsync(string extensionName, string popupUrl, string? optionsUrl)
  {
    CloseExtensionPopup();
    if (_closing) return;
    try
    {
      var env = await EnvironmentForProfileAsync(_state.ActiveProfileId);
      if (_closing) return;
      var activeTab = ActiveTab();
      var popup = new SafariExtensionPopupWindow(
          owner: this,
          anchor: _extensionsBtn,
          environment: env,
          isPrivate: _state.ActiveGroupId == PrivateGroupId,
          extensionName: extensionName,
          popupUrl: popupUrl,
          optionsUrl: optionsUrl,
          activeWebsiteUrl: activeTab?.Url ?? "about:blank",
          activeWebsiteTitle: activeTab?.Title ?? "새 탭",
          openInTab: url => CreateTabAsync(url));
      popup.Closed += (sender, _) =>
      {
        if (ReferenceEquals(_extensionPopup, sender)) _extensionPopup = null;
      };
      _extensionPopup = popup;
      popup.Show();
      popup.Activate();
    }
    catch
    {
      await CreateTabAsync(popupUrl);
    }
  }

  // 4. Capture & Watermark Popover
  private void ShowCapturePopup(UIElement anchor)
  {
    var panel = new StackPanel();
    panel.Children.Add(SafariTheme.CreatePopoverRow(SafariIcon.Capture, "영역 선택 → 책장에 추가", () => ClosePopupThenAsync(() => CaptureSelectedRegionAsync(CaptureAction.AddToBookshelf))));
    panel.Children.Add(SafariTheme.CreatePopoverRow(SafariIcon.Copy, "영역 선택 → 클립보드 복사", () => ClosePopupThenAsync(() => CaptureSelectedRegionAsync(CaptureAction.Copy))));
    panel.Children.Add(SafariTheme.CreatePopoverRow(SafariIcon.Save, "영역 선택 → PNG 파일 저장", () => ClosePopupThenAsync(() => CaptureSelectedRegionAsync(CaptureAction.SavePng))));
    panel.Children.Add(SafariTheme.CreatePopoverDivider());
    panel.Children.Add(SafariTheme.CreatePopoverRow(SafariIcon.Camera, "현재 보이는 화면 즉시 캡처", () => ClosePopupThenAsync(CaptureActiveTabAsync)));
    panel.Children.Add(SafariTheme.CreatePopoverRow(SafariIcon.WatermarkSparkles, "화면 캡처 후 워터마크 삭제", () => ClosePopupThenAsync(RemoveWatermarkFromActiveTabAsync)));
    panel.Children.Add(SafariTheme.CreatePopoverDivider());
    panel.Children.Add(SafariTheme.CreatePopoverRow(SafariIcon.Bookshelf, "내장 책장 열기", () => ClosePopupThen(OpenBookshelfWindow)));
    ShowGlassPopup(anchor, panel, 250, 300);
  }

  // 5. Audio Output Popover
  private async Task ShowAudioPopupAsync(UIElement anchor)
  {
    var outputs = await Task.Run(WindowsAudioRouter.EnumerateOutputs);
    var panel = new StackPanel();
    panel.Children.Add(new TextBlock
    {
      Text = "소리 출력 장치",
      FontSize = 11.5,
      FontWeight = FontWeights.SemiBold,
      Foreground = SafariTheme.TextMuted,
      Margin = new Thickness(8, 4, 8, 6),
    });

    var isDefault = string.IsNullOrWhiteSpace(_preferences.AudioOutputDeviceId);
    panel.Children.Add(SafariTheme.CreatePopoverRow(
        isDefault ? SafariIcon.Check : SafariIcon.Audio, "시스템 기본 장치",
        async () =>
        {
          ClosePopup();
          _preferences.AudioOutputDeviceId = string.Empty;
          _preferences.AudioOutputLabel = "시스템 기본 장치";
          DesktopPreferencesStore.Save(_preferences);
          await ApplyAudioOutputToAllAsync(string.Empty);
          _syncStatus = "소리 출력 · 시스템 기본 장치";
          UpdateStatusText();
        },
        isSelected: isDefault));

    foreach (var dev in outputs)
    {
      var selected = string.Equals(_preferences.AudioOutputDeviceId, dev.DeviceId, StringComparison.OrdinalIgnoreCase);
      panel.Children.Add(SafariTheme.CreatePopoverRow(
          selected ? SafariIcon.Check : SafariIcon.Audio, dev.DisplayName,
          async () =>
          {
            ClosePopup();
            _preferences.AudioOutputDeviceId = dev.DeviceId;
            _preferences.AudioOutputLabel = dev.DisplayName;
            DesktopPreferencesStore.Save(_preferences);
            await ApplyAudioOutputToAllAsync(dev.DeviceId);
            _syncStatus = "소리 출력 · " + dev.DisplayName;
            UpdateStatusText();
          },
          isSelected: selected));
    }

    panel.Children.Add(SafariTheme.CreatePopoverDivider());
    panel.Children.Add(SafariTheme.CreatePopoverRow(SafariIcon.Settings, "Windows 소리 설정 열기", () =>
    {
      ClosePopup();
      Process.Start(new ProcessStartInfo("ms-settings:sound") { UseShellExecute = true });
    }));
    ShowGlassPopup(anchor, panel, 280, 350);
  }

  // 6. Main Safari Menu Popover
  private void ShowMainMenuPopup(UIElement anchor)
  {
    var panel = new StackPanel();
    panel.Children.Add(SafariTheme.CreatePopoverRow(SafariIcon.Plus, "새 탭", () => { ClosePopup(); _ = CreateTabAsync(); }, "Ctrl+T"));
    panel.Children.Add(SafariTheme.CreatePopoverRow(SafariIcon.Private, "새 비공개 탭", () => { ClosePopup(); SwitchTabGroup(PrivateGroupId); _ = CreateTabAsync(); }, "Ctrl+Shift+N"));
    panel.Children.Add(SafariTheme.CreatePopoverRow(SafariIcon.Undo, "닫은 탭 다시 열기", () => { ClosePopup(); _ = ReopenClosedTabAsync(); }, "Ctrl+Shift+T"));
    panel.Children.Add(SafariTheme.CreatePopoverDivider());
    panel.Children.Add(SafariTheme.CreatePopoverRow(SafariIcon.Sidebar, _sidebarOpen ? "사이드바 숨기기" : "사이드바 표시", () => { ClosePopup(); ToggleSidebar(); }, "Ctrl+Shift+S"));
    panel.Children.Add(SafariTheme.CreatePopoverRow(SafariIcon.TabOverview, "탭 목록", () => { ClosePopup(); ToggleTabOverview(); }, "Ctrl+Shift+O"));
    panel.Children.Add(SafariTheme.CreatePopoverRow(SafariIcon.Bookshelf, "내장 책장 열기", () => ClosePopupThen(OpenBookshelfWindow)));
    panel.Children.Add(SafariTheme.CreatePopoverRow(SafariIcon.Sync, "Google Drive 동기화", () => ClosePopupThenAsync(ShowSyncDialogAsync)));
    panel.Children.Add(SafariTheme.CreatePopoverRow(SafariIcon.Extensions, "확장 프로그램 관리", () => ClosePopupThenAsync(ShowExtensionManagerAsync)));
    panel.Children.Add(SafariTheme.CreatePopoverDivider());
    panel.Children.Add(SafariTheme.CreatePopoverRow(SafariIcon.StarOutline, "현재 페이지 북마크 추가", () => ClosePopupThen(ToggleCurrentBookmark), "Ctrl+D"));
    panel.Children.Add(SafariTheme.CreatePopoverRow(SafariIcon.Folder, "북마크 관리자", () => ClosePopupThen(ShowBookmarkManagerWindow), "Ctrl+Shift+B"));
    panel.Children.Add(SafariTheme.CreatePopoverRow(SafariIcon.Settings, "Omyaru 설정", () => ClosePopupThen(ShowSettingsDialog)));
    ShowGlassPopup(anchor, panel, 260, 300);
  }

  // Context Popovers (Tab / Group / Bookmark)
  private void ShowTabContextPopup(UIElement anchor, TabEntry tab)
  {
    var panel = new StackPanel();
    panel.Children.Add(SafariTheme.CreatePopoverRow(SafariIcon.Link, "링크 복사하기", () =>
    {
      ClosePopup();
      if (string.IsNullOrWhiteSpace(tab.Url) || string.Equals(tab.Url, "about:blank", StringComparison.OrdinalIgnoreCase))
      {
        _syncStatus = "복사할 링크가 없습니다.";
        UpdateStatusText();
        return;
      }
      try
      {
        System.Windows.Clipboard.SetText(tab.Url);
        _syncStatus = "링크가 클립보드에 복사되었습니다.";
        UpdateStatusText();
      }
      catch { }
    }));
    panel.Children.Add(SafariTheme.CreatePopoverRow(
        tab.IsPinned ? SafariIcon.PinOff : SafariIcon.Pin,
        tab.IsPinned ? "탭 고정 해제" : "탭 고정",
        () =>
        {
          ClosePopup();
          TogglePinTab(tab.Id);
        }));
    panel.Children.Add(SafariTheme.CreatePopoverRow(SafariIcon.Duplicate, "탭 복제", () =>
    {
      ClosePopup();
      _ = DuplicateTabAsync(tab.Id);
    }));
    panel.Children.Add(SafariTheme.CreatePopoverRow(SafariIcon.Check, "탭 선택", () =>
    {
      ClosePopup();
      EnterOverviewSelectionMode(tab.Id);
    }));
    panel.Children.Add(SafariTheme.CreatePopoverRow(SafariIcon.Folder, "탭 이동", () =>
    {
      ClosePopup();
      Dispatcher.BeginInvoke(() => ShowMoveTabsPopup(anchor, [tab.Id]), System.Windows.Threading.DispatcherPriority.Input);
    }));
    panel.Children.Add(SafariTheme.CreatePopoverDivider());
    panel.Children.Add(SafariTheme.CreatePopoverRow(SafariIcon.Trash, "탭 닫기", () =>
    {
      ClosePopup();
      _ = CloseTabAsync(tab.Id);
    }, isDestructive: true));
    ShowGlassPopup(anchor, panel, 214, 300);
  }

  private void ShowMoveTabsPopup(UIElement anchor, IReadOnlyList<string> tabIds)
  {
    if (tabIds.Count == 0) return;
    var firstTab = _state.Tabs.FirstOrDefault(t => t.Id == tabIds[0]);
    var panel = new StackPanel();

    panel.Children.Add(new TextBlock
    {
      Text = tabIds.Count == 1 ? "이동할 위치 선택" : $"{tabIds.Count}개 탭을 이동할 위치 선택",
      FontSize = 11.5,
      FontWeight = FontWeights.SemiBold,
      Foreground = SafariTheme.TextMuted,
      Margin = new Thickness(10, 6, 10, 6),
    });

    foreach (var profile in _state.Profiles)
    {
      var p = profile;
      var pGroups = _state.Groups.Where(g => g.ProfileId == p.Id && g.Id != PrivateGroupId).ToList();
      if (_state.Profiles.Count > 1)
      {
        panel.Children.Add(new TextBlock
        {
          Text = p.Name,
          FontSize = 11,
          FontWeight = FontWeights.SemiBold,
          Foreground = SafariTheme.TextMuted,
          Margin = new Thickness(10, 6, 10, 2),
        });
      }

      var isCurrentDefault = firstTab is not null && firstTab.ProfileId == p.Id && firstTab.GroupId is null;
      var defaultLabel = _state.Profiles.Count > 1 ? $"{p.Name} · 기본 탭" : "기본 탭";
      panel.Children.Add(SafariTheme.CreatePopoverRow(
          isCurrentDefault ? SafariIcon.Check : SafariIcon.TabOverview,
          "기본 탭",
          () =>
          {
            ClosePopup();
            MoveTabsToDestination(tabIds, p.Id, null, defaultLabel);
          }));

      foreach (var group in pGroups)
      {
        var g = group;
        var isCurrentGroup = firstTab is not null && firstTab.ProfileId == p.Id && firstTab.GroupId == g.Id;
        var label = _state.Profiles.Count > 1 ? $"{p.Name} · {g.Name}" : g.Name;
        panel.Children.Add(SafariTheme.CreatePopoverRow(
            isCurrentGroup ? SafariIcon.Check : SafariIcon.TabGroup,
            g.Name,
            () =>
            {
              ClosePopup();
              MoveTabsToDestination(tabIds, p.Id, g.Id, label);
            }));
      }
    }

    panel.Children.Add(SafariTheme.CreatePopoverDivider());
    panel.Children.Add(SafariTheme.CreatePopoverRow(SafariIcon.Plus, "새 탭 그룹으로 이동…", () => ClosePopupThen(() =>
    {
      var name = ShowSimpleInputDialog("새 탭 그룹으로 이동", "새 탭 그룹 이름을 입력하세요:", "");
      if (string.IsNullOrWhiteSpace(name)) return;
      var group = new TabGroupEntry(Guid.NewGuid().ToString("N"), _state.ActiveProfileId, name.Trim());
      _state.Groups.Add(group);
      MoveTabsToDestination(tabIds, _state.ActiveProfileId, group.Id, group.Name);
    })));

    ShowGlassPopup(anchor, panel, 240, 380);
  }

  private void ShowGroupContextPopup(UIElement anchor, TabGroupEntry group)
  {
    var panel = new StackPanel();
    panel.Children.Add(SafariTheme.CreatePopoverRow(SafariIcon.Trash, $"'{group.Name}' 그룹 삭제", () =>
    {
      ClosePopup();
      _state.Groups.RemoveAll(g => g.Id == group.Id);
      for (var i = 0; i < _state.Tabs.Count; i++)
        if (_state.Tabs[i].GroupId == group.Id) _state.Tabs[i] = _state.Tabs[i] with { GroupId = null };
      if (_state.ActiveGroupId == group.Id) _state.ActiveGroupId = null;
      MarkLocalChange();
      RefreshWorkspaceUi();
    }, isDestructive: true));
    ShowGlassPopup(anchor, panel, 200, 260);
  }

  private void ShowBookmarkManagerWindow()
  {
    var mgr = new SafariBookmarkManagerWindow(
        this,
        _bookmarks,
        LoadFaviconSource,
        url => _ = CreateTabAsync(url),
        updated =>
        {
          _bookmarks = updated;
          RefreshWorkspaceUi();
          _driveNextCheck = default;
        });
    mgr.ShowDialog();
  }

  private void ShowBookmarkFolderPopup(UIElement anchor, BookmarkFolderEntry folder)
  {
    var panel = new StackPanel();
    panel.Children.Add(new TextBlock
    {
      Text = folder.Name,
      FontSize = 11.5,
      FontWeight = FontWeights.SemiBold,
      Foreground = SafariTheme.TextMuted,
      Margin = new Thickness(8, 3, 8, 6),
    });

    if (folder.ParentId is not null &&
        _bookmarks.Folders.FirstOrDefault(f => f.Id == folder.ParentId) is { } parentFolder)
    {
      panel.Children.Add(SafariTheme.CreatePopoverRow(SafariIcon.ChevronLeft, $"상위 폴더 ({parentFolder.Name})", () => ShowBookmarkFolderPopup(anchor, parentFolder)));
      panel.Children.Add(SafariTheme.CreatePopoverDivider());
    }

    var subFolders = _bookmarks.Folders
        .Where(f => f.ParentId == folder.Id)
        .OrderBy(f => f.Order)
        .ToList();
    foreach (var sub in subFolders)
    {
      var s = sub;
      var cnt = BookmarkStore.RecursiveBookmarkCount(_bookmarks, s.Id);
      panel.Children.Add(SafariTheme.CreatePopoverRow(SafariIcon.FolderFilled, s.Name, () => ShowBookmarkFolderPopup(anchor, s), shortcut: $"{cnt} ›"));
    }

    var items = _bookmarks.Items.Where(b => b.FolderId == folder.Id).ToList();
    foreach (var bm in items.Take(30))
    {
      var item = bm;
      panel.Children.Add(SafariTheme.CreatePopoverRow(SafariIcon.Globe, item.Name, () => { ClosePopup(); _ = NavigateToAsync(item.Url); }));
    }

    if (subFolders.Count == 0 && items.Count == 0)
    {
      panel.Children.Add(new TextBlock { Text = "비어 있는 폴더입니다.", FontSize = 12, Foreground = SafariTheme.TextMuted, Margin = new Thickness(10, 6, 10, 6) });
    }

    panel.Children.Add(SafariTheme.CreatePopoverDivider());
    panel.Children.Add(SafariTheme.CreatePopoverRow(SafariIcon.Folder, "북마크 관리자에서 편집…", () => ClosePopupThen(ShowBookmarkManagerWindow)));
    ShowGlassPopup(anchor, panel, 240, 340);
  }

  private void ShowBookmarkItemPopup(UIElement anchor, BookmarkEntry fav)
  {
    var panel = new StackPanel();
    panel.Children.Add(SafariTheme.CreatePopoverRow(SafariIcon.OpenExternal, "새 탭에서 열기", () => { ClosePopup(); _ = CreateTabAsync(fav.Url); }));
    panel.Children.Add(SafariTheme.CreatePopoverRow(SafariIcon.Folder, "북마크 관리자 열기…", () => ClosePopupThen(ShowBookmarkManagerWindow)));
    panel.Children.Add(SafariTheme.CreatePopoverDivider());
    panel.Children.Add(SafariTheme.CreatePopoverRow(SafariIcon.Trash, "북마크 삭제", () =>
    {
      ClosePopup();
      BookmarkStore.Remove(_bookmarks, fav);
      BookmarkStore.Save(_bookmarks);
      RefreshWorkspaceUi();
      _driveNextCheck = default;
    }, isDestructive: true));
    ShowGlassPopup(anchor, panel, 200, 250);
  }

  // Bookmarks / Profiles / Groups Prompts
  private void ToggleCurrentBookmark()
  {
    var tab = ActiveTab();
    if (tab is null || tab.Url == "about:blank" || !BookmarkStore.TryNormalizeUrl(tab.Url, out var normalized))
    {
      PromptAddBookmark();
      return;
    }
    var targetKey = BookmarkStore.CanonicalUrlKey(normalized);
    var existing = _bookmarks.Items.FirstOrDefault(b => string.Equals(BookmarkStore.CanonicalUrlKey(b.Url), targetKey, StringComparison.OrdinalIgnoreCase));
    if (existing is not null)
    {
      BookmarkStore.Remove(_bookmarks, existing);
      _syncStatus = $"북마크에서 '{existing.Name}' 삭제함";
    }
    else
    {
      var title = string.IsNullOrWhiteSpace(tab.Title) ? HostName(normalized) : tab.Title;
      BookmarkStore.AddOrUpdate(_bookmarks, title, normalized);
      _syncStatus = $"북마크에 '{title}' 추가함";
    }
    BookmarkStore.Save(_bookmarks);
    RefreshWorkspaceUi();
    _driveNextCheck = default;
  }

  private void PromptAddBookmark()
  {
    var name = ShowSimpleInputDialog("북마크 추가", "표시 이름을 입력하세요:", HostName(ActiveTab()?.Url ?? ""));
    if (string.IsNullOrWhiteSpace(name)) return;
    var urlRaw = ShowSimpleInputDialog("북마크 URL", "웹사이트 주소를 입력하세요:", ActiveTab()?.Url is { } u && u != "about:blank" ? u : "https://");
    if (string.IsNullOrWhiteSpace(urlRaw) || !BookmarkStore.TryNormalizeUrl(urlRaw, out var url)) return;
    BookmarkStore.AddOrUpdate(_bookmarks, name.Trim(), url);
    BookmarkStore.Save(_bookmarks);
    RefreshWorkspaceUi();
    _driveNextCheck = default;
  }

  private void PromptCreateTabGroup()
  {
    var name = ShowSimpleInputDialog("새 탭 그룹", "탭 그룹 이름을 입력하세요:", "");
    if (string.IsNullOrWhiteSpace(name)) return;
    var group = new TabGroupEntry(Guid.NewGuid().ToString("N"), _state.ActiveProfileId, name.Trim());
    _state.Groups.Add(group);
    _state.ActiveGroupId = group.Id;
    EnsureWorkspaceTab();
    MarkLocalChange();
    RefreshWorkspaceUi();
    _ = ActivateCurrentTabAsync();
  }

  private void PromptCreateProfile()
  {
    if (_state.Profiles.Count >= SyncSnapshot.MaxProfiles) return;
    var name = ShowSimpleInputDialog("새 프로필", "프로필 이름을 입력하세요:", "");
    if (string.IsNullOrWhiteSpace(name)) return;
    uint[] palette = [0xFF0A84FF, 0xFF9E7BFF, 0xFF30D158, 0xFFFF9F0A, 0xFFFF375F, 0xFF64D2FF];
    var color = palette[_state.Profiles.Count % palette.Length];
    var profile = new ProfileEntry(Guid.NewGuid().ToString("N"), name.Trim(), "STAR", color);
    _state.Profiles.Add(profile);
    _state.ActiveProfileId = profile.Id;
    _state.ActiveGroupId = null;
    EnsureWorkspaceTab();
    MarkLocalChange();
    RefreshWorkspaceUi();
    _ = ActivateCurrentTabAsync();
  }

  private string? ShowSimpleInputDialog(string title, string prompt, string defaultText)
  {
    var dlg = new Window
    {
      Title = title,
      Width = 400,
      Height = 175,
      WindowStartupLocation = WindowStartupLocation.CenterOwner,
      Owner = this,
      ResizeMode = ResizeMode.NoResize,
      Background = SafariTheme.PopoverBg,
      Foreground = SafariTheme.TextPrimary,
      FontFamily = SafariTheme.UiFont,
    };
    dlg.SourceInitialized += (_, _) => DwmBackdrop.Apply(dlg, acrylic: true);
    var box = new System.Windows.Controls.TextBox
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
      Children =
      {
        new TextBlock { Text = prompt, FontSize = 12.5, Foreground = SafariTheme.TextSecondary },
        box,
        btns,
      },
    };
    dlg.Loaded += (_, _) => { box.Focus(); box.SelectAll(); };
    return dlg.ShowDialog() == true ? result : null;
  }

  // Existing WinForms Companion Windows & Dialogs (Seamless Modal Hosting!)
  private void OpenBookshelfWindow()
  {
    System.Windows.Forms.Integration.WindowsFormsHost.EnableWindowsFormsInterop();
    if (_bookshelfForm is { IsDisposed: false })
    {
      if (_bookshelfForm.WindowState == System.Windows.Forms.FormWindowState.Minimized)
        _bookshelfForm.WindowState = System.Windows.Forms.FormWindowState.Normal;
      _bookshelfForm.ReloadCatalogFromExternalChange();
      _bookshelfForm.BringToFront();
      _bookshelfForm.Activate();
      return;
    }
    var shelf = new BookshelfForm();
    shelf.FormClosed += (sender, _) =>
    {
      if (ReferenceEquals(_bookshelfForm, sender)) _bookshelfForm = null;
    };
    _bookshelfForm = shelf;
    shelf.Show();
  }

  private async Task ShowExtensionManagerAsync()
  {
    var view = ActiveWebView();
    if (view?.CoreWebView2 is null)
    {
      var tab = ActiveTab();
      if (tab is null) return;
      view = await CreateWebViewForTabAsync(tab);
      if (view.CoreWebView2 is null) return;
    }
    using var dialog = new ExtensionManagerForm(
        view.CoreWebView2.Profile,
        _state.ActiveProfileId,
        ActiveProfile().Name,
        openUrlInTab: url => CreateTabAsync(url),
        initialStoreUrl: ActiveTab()?.Url,
        openPopup: (name, popupUrl, optionsUrl) => ShowExtensionPopupWindowAsync(name, popupUrl, optionsUrl));
    dialog.ShowDialog(DwmBackdrop.OwnerOf(this));
  }

  private void ShowSettingsDialog()
  {
    var settingsWindow = new SafariSettingsWindow(
        this,
        _preferences,
        updated =>
        {
          _preferences = updated;
          DesktopPreferencesStore.Save(_preferences);
          PopulateFavoritesBar();
          PopulateTabStrip();
          ApplyChromePreferences();
          if (!_addressInput.IsKeyboardFocusWithin)
            _addressInput.Text = DisplayAddress(ActiveTab()?.Url ?? "about:blank");
        },
        ShowSyncDialogAsync,
        async target => await ShowAudioPickerWindowAsync(target));
    settingsWindow.ShowDialog();
  }

  private async Task<string?> ShowAudioPickerWindowAsync(DesktopPreferences target)
  {
    var outputs = await Task.Run(WindowsAudioRouter.EnumerateOutputs);
    var choices = new List<(string Id, string Label)> { (string.Empty, "시스템 기본 장치") };
    choices.AddRange(outputs.Select(o => (o.DeviceId, o.DisplayName)));

    var owner = System.Windows.Application.Current?.Windows.OfType<Window>().FirstOrDefault(w => w.IsActive) ?? this;
    var dlg = new Window
    {
      Title = "소리 출력 장치 — Omyaru",
      Owner = owner,
      WindowStartupLocation = WindowStartupLocation.CenterOwner,
      Width = 420,
      Height = 360,
      ResizeMode = ResizeMode.NoResize,
      Background = SafariTheme.PopoverBg,
      Foreground = SafariTheme.TextPrimary,
      FontFamily = SafariTheme.UiFont,
    };
    SafariTheme.ApplyMacScrollBarStyles(dlg.Resources);
    dlg.SourceInitialized += (_, _) => DwmBackdrop.Apply(dlg, acrylic: true);

    (string Id, string Label)? picked = null;
    var listPanel = new StackPanel { Margin = new Thickness(10) };
    foreach (var choice in choices)
    {
      var item = choice;
      var selected = string.Equals(item.Id, target.AudioOutputDeviceId, StringComparison.OrdinalIgnoreCase);
      listPanel.Children.Add(SafariTheme.CreatePopoverRow(
          selected ? SafariIcon.Check : SafariIcon.Audio,
          item.Label,
          () => { picked = item; dlg.DialogResult = true; },
          isSelected: selected));
    }
    dlg.Content = new ScrollViewer { VerticalScrollBarVisibility = ScrollBarVisibility.Auto, Content = listPanel };
    if (dlg.ShowDialog() == true && picked is { } chosen)
    {
      target.AudioOutputDeviceId = chosen.Id;
      target.AudioOutputLabel = chosen.Label;
      await ApplyAudioOutputToAllAsync(chosen.Id);
      _syncStatus = "소리 출력 · " + chosen.Label;
      UpdateStatusText();
      return chosen.Label;
    }
    return null;
  }

  private System.Windows.Forms.IWin32Window ResolveModalOwner()
  {
    var activeOwner = System.Windows.Application.Current?.Windows
        .OfType<Window>()
        .FirstOrDefault(w => w.IsActive && w.IsVisible && new System.Windows.Interop.WindowInteropHelper(w).Handle != IntPtr.Zero)
        ?? this;
    return DwmBackdrop.OwnerOf(activeOwner);
  }

  private async Task ShowSyncDialogAsync()
  {
    if (_closing || _syncDialogOpen) return;
    _syncDialogOpen = true;
    System.Windows.Forms.DialogResult result = System.Windows.Forms.DialogResult.Cancel;
    try
    {
      using var dialog = new GoogleDriveSyncDialog();
      result = dialog.ShowDialog(ResolveModalOwner());
    }
    catch (Exception ex)
    {
      MessageBox.Show(this, "동기화 창을 열지 못했습니다:\n" + ex.Message, "Google Drive 동기화");
    }
    finally
    {
      _syncDialogOpen = false;
      _driveNextCheck = default;
    }
    if (result == System.Windows.Forms.DialogResult.OK || !GoogleDriveAuthorization.NeedsReauthentication)
    {
      _driveFailures = 0;
      if (GoogleDriveAuthorization.Load() is not null && !GoogleDriveAuthorization.NeedsReauthentication)
      {
        _syncStatus = "Google Drive 동기화 중…";
        UpdateStatusText();
      }
    }
    if (result == System.Windows.Forms.DialogResult.Retry)
      await ShowFileSyncDialogAsync();
    else
      await FlushAndAutoSyncAsync();
  }

  private async Task ShowFileSyncDialogAsync()
  {
    using var dialog = new SyncDialog(AutoSyncSettingsStore.Load());
    if (dialog.ShowDialog(DwmBackdrop.OwnerOf(this)) != System.Windows.Forms.DialogResult.OK) return;
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
        MessageBox.Show(this, "동기화 파일을 가져왔습니다.", "동기화 완료");
      }
      else
      {
        var snapshot = _state.Copy(includeThumbnails: false);
        await Task.Run(() => SyncCodec.Write(dialog.SyncPath, snapshot, dialog.Passphrase));
        if (dialog.AutomaticSync)
          AutoSyncSettingsStore.Configure(dialog.SyncPath, dialog.Passphrase, SyncFileFingerprint.Compute(dialog.SyncPath), snapshot.ModifiedAtEpochMs);
        else
          AutoSyncSettingsStore.Disable();
        MessageBox.Show(this, "동기화 파일을 저장했습니다.", "동기화 완료");
      }
    }
    catch (Exception ex)
    {
      MessageBox.Show(this, "동기화 오류:\n" + ex.Message, "동기화 오류");
    }
  }

  // Google Drive Live Auto-Sync (100% parity with v0.4.73 fixes!)
  private void OnDriveSessionChanged()
  {
    if (_closing) return;
    Dispatcher.BeginInvoke(() =>
    {
      if (_closing) return;
      _driveFailures = 0;
      _driveNextCheck = default;
      if (GoogleDriveAuthorization.Load() is null)
        _syncStatus = "로컬 저장 · 자동 동기화 꺼짐";
      else if (!GoogleDriveAuthorization.NeedsReauthentication)
        _syncStatus = "Google Drive 연결됨 · 동기화 확인 중…";
      UpdateStatusText();
      if (_enableAutomaticSync && !_autoSyncBusy)
        _ = FlushAndAutoSyncAsync();
    });
  }

  private async Task FlushAndAutoSyncAsync()
  {
    if (_closing || _autoSyncBusy || _syncDialogOpen) return;
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
          await Task.Run(() => DesktopDriveSync.Acknowledge(merged));
        }
        var userDataChanged = await DesktopUserDataSync.RunAsync(_driveCancellation.Token);
        if (userDataChanged && !_closing)
        {
          _bookmarks = BookmarkStore.Load();
          PopulateFavoritesBar();
          if (_sidebarOpen) PopulateSidebar();
          if (_startPageLayer.Visibility == Visibility.Visible) PopulateStartPage();
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

    var config = AutoSyncSettingsStore.Load();
    if (!config.Enabled)
    {
      _syncStatus = "로컬 저장 · 자동 동기화 꺼짐";
      UpdateStatusText();
      return;
    }

    _autoSyncBusy = true;
    try
    {
      var outcome = await Task.Run(() => DesktopAutoSync.Run(_state.Copy(false)));
      if (outcome.Decision == AutoSyncDecision.Import && outcome.Imported is not null && !_stateDirty)
      {
        await ApplyImportedStateAsync(outcome.Imported);
        AutoSyncSettingsStore.UpdateBaseline(AutoSyncSettingsStore.Load(), outcome.Fingerprint!, outcome.Imported.ModifiedAtEpochMs);
        _syncStatus = "자동 동기화 · 파일에서 가져옴";
      }
      else if (outcome.Decision == AutoSyncDecision.Export)
      {
        _syncStatus = "자동 동기화 · 파일로 내보냄";
      }
      else if (!string.IsNullOrWhiteSpace(outcome.Error))
      {
        _syncStatus = "자동 동기화 오류 · " + outcome.Error;
      }
      UpdateStatusText();
    }
    finally
    {
      _autoSyncBusy = false;
    }
  }

  private async Task ApplyDriveStateAsync(SyncSnapshot imported)
  {
    var previous = _state;
    var wasPrivateWorkspace = previous.ActiveGroupId == PrivateGroupId;
    var privateTabs = previous.Tabs.Where(t => t.GroupId == PrivateGroupId).ToList();
    var pinnedIds = previous.Tabs.Where(t => t.IsPinned).Select(t => t.Id).ToHashSet(StringComparer.Ordinal);

    imported.ActiveProfileId = previous.ActiveProfileId;
    imported.ActiveGroupId = wasPrivateWorkspace ? null : previous.ActiveGroupId;
    imported.ActiveTabId = previous.ActiveTabId;
    imported.Normalize();

    // Preserve local-only pin states (not synced in wire v1 schema) and local-only private tabs.
    imported.Tabs = imported.Tabs
        .Select(t => pinnedIds.Contains(t.Id) ? t with { IsPinned = true } : t)
        .Concat(privateTabs)
        .ToList();
    if (wasPrivateWorkspace)
    {
      imported.ActiveGroupId = PrivateGroupId;
      if (previous.ActiveTabId is not null && imported.Tabs.Any(t => t.Id == previous.ActiveTabId))
        imported.ActiveTabId = previous.ActiveTabId;
    }

    if (previous.Profiles.SequenceEqual(imported.Profiles) &&
        previous.Groups.SequenceEqual(imported.Groups) &&
        previous.Tabs.Select(t => t with { ThumbnailBase64 = null }).SequenceEqual(imported.Tabs.Select(t => t with { ThumbnailBase64 = null })))
      return;

    var liveIds = imported.Tabs.Select(t => t.Id).ToHashSet(StringComparer.Ordinal);
    foreach (var id in _views.Keys.Where(id => !liveIds.Contains(id)).ToArray())
    {
      var view = _views[id];
      _webViewLayer.Children.Remove(view);
      _views.Remove(id);
      try { view.Dispose(); } catch { }
      _thumbnails.Remove(id);
      _tabFavicons.Remove(id);
    }

    _state = imported.Copy(false);
    _stateDirty = false;
    BrowserStateStore.SaveImported(_state);

    foreach (var tab in _state.Tabs)
    {
      if (previous.Tabs.FirstOrDefault(t => t.Id == tab.Id) is { } old &&
          old.Url != tab.Url &&
          _views.TryGetValue(tab.Id, out var view) &&
          view.CoreWebView2 is not null)
      {
        view.CoreWebView2.Navigate(tab.Url);
      }
    }

    EnsureWorkspaceTab();
    RefreshWorkspaceUi();
    await ActivateCurrentTabAsync();
  }

  private async Task ApplyImportedStateAsync(SyncSnapshot imported)
  {
    foreach (var view in _views.Values)
    {
      _webViewLayer.Children.Remove(view);
      try { view.Dispose(); } catch { }
    }
    _views.Clear();
    _thumbnails.Clear();
    _tabFavicons.Clear();
    _state = imported.Copy(false);
    _stateDirty = false;
    EnsureWorkspaceTab();
    BrowserStateStore.SaveImported(_state);
    RefreshWorkspaceUi();
    await ActivateCurrentTabAsync();
  }
}
