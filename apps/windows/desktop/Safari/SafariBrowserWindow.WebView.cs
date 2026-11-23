using System.Globalization;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;
using System.Windows;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using Microsoft.Web.WebView2.Core;
using Color = System.Windows.Media.Color;
using MessageBox = System.Windows.MessageBox;
using WpfWebView2 = Microsoft.Web.WebView2.Wpf.WebView2;

namespace PrivacyBrowser.Desktop.Safari;

internal sealed partial class SafariBrowserWindow
{
  private Task<CoreWebView2Environment> EnvironmentForProfileAsync(string profileId)
  {
    if (_environments.TryGetValue(profileId, out var existing)) return existing;
    var digest = Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(profileId))).ToLowerInvariant();
    var folder = Path.Combine(BrowserStateStore.StateDirectory, "profiles", digest);
    Directory.CreateDirectory(folder);
    var options = new CoreWebView2EnvironmentOptions
    {
      AllowSingleSignOnUsingOSPrimaryAccount = false,
      AreBrowserExtensionsEnabled = true,
    };
    var created = CoreWebView2Environment.CreateAsync(null, folder, options);
    _environments[profileId] = created;
    return created;
  }

  private async Task ActivateCurrentTabAsync()
  {
    var tab = ActiveTab();
    if (tab is null)
    {
      ApplySurfaceMode();
      return;
    }

    _readerAvailable = false;
    _readerActive = false;
    _distractionActive = false;
    _distractionHiddenCount = 0;
    UpdateReaderAndDistractionPill();

    if (!_addressInput.IsKeyboardFocusWithin)
      _addressInput.Text = DisplayAddress(tab.Url);

    if (!_views.TryGetValue(tab.Id, out var view))
    {
      if (tab.Url == "about:blank")
      {
        ApplySurfaceMode();
        UpdateNavigationButtons();
        UpdateBookmarkStarState();
        UpdateStatusText();
        return;
      }
      view = await CreateWebViewForTabAsync(tab);
    }

    foreach (var (id, candidate) in _views)
      candidate.Visibility = id == tab.Id && !_showingOverview && tab.Url != "about:blank"
          ? Visibility.Visible
          : Visibility.Collapsed;

    ApplySurfaceMode();
    UpdateNavigationButtons();
    UpdateBookmarkStarState();
    UpdateStatusText();

    if (view.CoreWebView2 is not null && tab.Url != "about:blank")
      _ = ProbePageMetadataAsync(view.CoreWebView2);
  }

  private async Task<WpfWebView2> CreateWebViewForTabAsync(TabEntry tab)
  {
    var view = new WpfWebView2
    {
      DefaultBackgroundColor = System.Drawing.Color.FromArgb(255, 18, 19, 23),
      Visibility = Visibility.Collapsed,
    };
    _views[tab.Id] = view;
    _webViewLayer.Children.Add(view);

    var env = await EnvironmentForProfileAsync(tab.ProfileId);
    if (_closing || !_views.ContainsKey(tab.Id)) return view;

    var options = env.CreateCoreWebView2ControllerOptions();
    options.IsInPrivateModeEnabled = tab.GroupId == PrivateGroupId;
    await view.EnsureCoreWebView2Async(env, options);
    if (_closing || view.CoreWebView2 is null) return view;

    var core = view.CoreWebView2;
    core.Settings.AreDefaultContextMenusEnabled = true;
    core.Settings.IsStatusBarEnabled = false;
    core.Settings.AreBrowserAcceleratorKeysEnabled = true;
    core.Settings.IsSwipeNavigationEnabled = true;

    WireWebViewEvents(tab.Id, view, core);

    if (tab.Url != "about:blank")
      core.Navigate(tab.Url);

    if (!string.IsNullOrWhiteSpace(_preferences.AudioOutputDeviceId))
      _ = ApplyAudioOutputToAllAsync();

    return view;
  }

  private void WireWebViewEvents(string tabId, WpfWebView2 view, CoreWebView2 core)
  {
    core.NavigationStarting += (_, e) =>
    {
      if (_closing) return;
      if (_state.ActiveTabId == tabId)
      {
        _isLoading = true;
        _readerAvailable = false;
        _readerActive = false;
        _distractionActive = false;
        _distractionHiddenCount = 0;
        UpdateReaderAndDistractionPill();
        UpdateNavigationButtons();
        StartLoadingShimmer();
      }
      if (!string.IsNullOrWhiteSpace(e.Uri) && e.Uri != "about:blank")
      {
        UpdateTab(tabId, t => t with { Url = e.Uri });
        if (_state.ActiveTabId == tabId && !_addressInput.IsKeyboardFocusWithin)
          _addressInput.Text = DisplayAddress(e.Uri);
      }
    };

    core.SourceChanged += (_, _) =>
    {
      if (_closing) return;
      var url = core.Source ?? "about:blank";
      UpdateTab(tabId, t => t with { Url = url });
      if (_state.ActiveTabId == tabId)
      {
        if (!_addressInput.IsKeyboardFocusWithin)
          _addressInput.Text = DisplayAddress(url);
        UpdateNavigationButtons();
        UpdateBookmarkStarState();
      }
    };

    core.DocumentTitleChanged += (_, _) =>
    {
      if (_closing) return;
      var title = string.IsNullOrWhiteSpace(core.DocumentTitle) ? HostName(core.Source) : core.DocumentTitle;
      UpdateTab(tabId, t => t with { Title = title });
      PopulateTabStrip();
      if (_sidebarOpen) PopulateSidebar();
      if (_state.ActiveTabId == tabId) UpdateStatusText();
    };

    core.NavigationCompleted += async (_, e) =>
    {
      if (_closing) return;
      var url = core.Source ?? "about:blank";
      var title = string.IsNullOrWhiteSpace(core.DocumentTitle) ? HostName(url) : core.DocumentTitle;
      UpdateTab(tabId, t => t with { Url = url, Title = title });

      if (e.IsSuccess && url.StartsWith("http", StringComparison.OrdinalIgnoreCase) &&
          ActiveTab()?.GroupId != PrivateGroupId)
      {
        BrowserHistory.Record(url, title);
      }

      if (_state.ActiveTabId == tabId)
      {
        _isLoading = false;
        FinishLoadingShimmer();
        UpdateNavigationButtons();
        UpdateBookmarkStarState();
        UpdateStatusText();
        await ProbePageMetadataAsync(core);
      }

      if (ExtensionManagerForm.IsSupportedStoreOrPackageUrl(url))
      {
        try { await core.ExecuteScriptAsync(SafariWebScripts.ExtensionStoreBridgeScript); } catch { }
      }

      if (_preferences.ShowTabThumbnails && _state.ActiveTabId == tabId && url != "about:blank")
      {
        _pendingThumbnailTabId = tabId;
        _thumbnailTimer.Stop();
        _thumbnailTimer.Start();
      }
    };

    core.FaviconChanged += async (_, _) =>
    {
      if (_closing) return;
      try
      {
        using var stream = await core.GetFaviconAsync(CoreWebView2FaviconImageFormat.Png);
        if (stream is null || stream.Length == 0) return;
        using var copy = new MemoryStream();
        await stream.CopyToAsync(copy);
        copy.Position = 0;
        var bytes = copy.ToArray();
        if (bytes.Length == 0) return;

        var url = core.Source ?? string.Empty;
        if (!string.IsNullOrWhiteSpace(url))
        {
          using var saveStream = new MemoryStream(bytes);
          _ = BookmarkFaviconStore.SaveAsync(url, saveStream);
        }

        using var bmpStream = new MemoryStream(bytes);
        var img = new BitmapImage();
        img.BeginInit();
        img.CacheOption = BitmapCacheOption.OnLoad;
        img.StreamSource = bmpStream;
        img.EndInit();
        img.Freeze();
        _tabFavicons[tabId] = img;
        PopulateTabStrip();
      }
      catch { }
    };

    core.NewWindowRequested += (_, e) =>
    {
      if (_closing) return;
      if (_preferences.OpenLinksInNewTab && !string.IsNullOrWhiteSpace(e.Uri))
      {
        e.Handled = true;
        _ = CreateTabAsync(e.Uri);
      }
    };

    core.DownloadStarting += (_, e) =>
    {
      var uri = e.DownloadOperation?.Uri ?? string.Empty;
      var lower = uri.ToLowerInvariant();
      if (lower.EndsWith(".crx") || lower.Contains(".crx?") || lower.EndsWith(".xpi") || lower.Contains(".xpi?"))
      {
        e.Cancel = true;
        e.Handled = true;
        _ = InstallExtensionFromStoreUrlAsync(uri, view);
      }
    };

    core.WebMessageReceived += (_, e) =>
    {
      if (_closing) return;
      try
      {
        using var doc = JsonDocument.Parse(e.WebMessageAsJson);
        var root = doc.RootElement;
        if (!root.TryGetProperty("channel", out var chEl)) return;
        var channel = chEl.GetString();
        if (channel == "omyaru-extension-install")
        {
          var targetUrl = root.TryGetProperty("url", out var urlEl) ? urlEl.GetString() : core.Source;
          if (!string.IsNullOrWhiteSpace(targetUrl))
            _ = InstallExtensionFromStoreUrlAsync(targetUrl!, view);
        }
        else if (channel == "omyaru-page-meta" && _state.ActiveTabId == tabId)
        {
          var cssColor = root.TryGetProperty("color", out var cEl) ? cEl.GetString() : null;
          ApplyAdaptiveChromeTint(cssColor);
          _readerAvailable = root.TryGetProperty("readerAvailable", out var ra) && ra.ValueKind == JsonValueKind.True;
          _readerActive = root.TryGetProperty("readerActive", out var rac) && rac.ValueKind == JsonValueKind.True;
          _distractionHiddenCount = root.TryGetProperty("hiddenCount", out var hc) && hc.TryGetInt32(out var n) ? n : 0;
          UpdateReaderAndDistractionPill();
        }
        else if (channel == "omyaru-reader-state" && _state.ActiveTabId == tabId)
        {
          _readerActive = root.TryGetProperty("active", out var act) && act.ValueKind == JsonValueKind.True;
          UpdateReaderAndDistractionPill();
        }
        else if (channel == "omyaru-distraction-state" && _state.ActiveTabId == tabId)
        {
          _distractionActive = root.TryGetProperty("active", out var act) && act.ValueKind == JsonValueKind.True;
          _distractionHiddenCount = root.TryGetProperty("hiddenCount", out var hc) && hc.TryGetInt32(out var n) ? n : 0;
          UpdateReaderAndDistractionPill();
        }
      }
      catch { }
    };
  }

  private async Task ProbePageMetadataAsync(CoreWebView2 core)
  {
    try { await core.ExecuteScriptAsync(SafariWebScripts.PageMetadataProbe); } catch { }
  }

  private void ApplyAdaptiveChromeTint(string? cssColor)
  {
    if (TryParseCssColor(cssColor, out var parsed))
    {
      // macOS Safari subtle adaptive tinting: blend webpage color softly into smoky glass base
      var blended = SafariTheme.Blend(SafariTheme.ToolbarBaseColor, parsed, 0.24);
      SafariTheme.AnimateColor(_chromeTintBrush, Color.FromArgb(240, blended.R, blended.G, blended.B), 280);
    }
    else
    {
      SafariTheme.AnimateColor(_chromeTintBrush, SafariTheme.ToolbarBaseColor, 260);
    }
  }

  private static bool TryParseCssColor(string? raw, out Color color)
  {
    color = default;
    if (string.IsNullOrWhiteSpace(raw)) return false;
    var s = raw.Trim();
    if (s.StartsWith('#'))
    {
      try
      {
        var hex = s.Length == 4
            ? $"#{s[1]}{s[1]}{s[2]}{s[2]}{s[3]}{s[3]}"
            : s;
        if (System.Windows.Media.ColorConverter.ConvertFromString(hex) is Color c)
        {
          color = c;
          return true;
        }
      }
      catch { }
      return false;
    }
    var m = Regex.Match(s, @"rgba?\(\s*(\d+)\s*,\s*(\d+)\s*,\s*(\d+)(?:\s*,\s*([0-9.]+))?\s*\)");
    if (m.Success &&
        byte.TryParse(m.Groups[1].Value, out var r) &&
        byte.TryParse(m.Groups[2].Value, out var g) &&
        byte.TryParse(m.Groups[3].Value, out var b))
    {
      if (m.Groups[4].Success && double.TryParse(m.Groups[4].Value, CultureInfo.InvariantCulture, out var a) && a < 0.2)
        return false;
      color = Color.FromRgb(r, g, b);
      return true;
    }
    return false;
  }

  private void StartLoadingShimmer()
  {
    _loadingProgressBar.Opacity = 1;
    var maxW = Math.Max(120, _addressCapsule.ActualWidth * 0.72);
    SafariTheme.AnimateDouble(_loadingProgressBar, FrameworkElement.WidthProperty, maxW, 650);
  }

  private void FinishLoadingShimmer()
  {
    var fullW = Math.Max(120, _addressCapsule.ActualWidth);
    SafariTheme.AnimateDouble(_loadingProgressBar, FrameworkElement.WidthProperty, fullW, 140);
    SafariTheme.AnimateDouble(_loadingProgressBar, OpacityProperty, 0, 260);
  }

  internal async Task NavigateToAsync(string destination)
  {
    if (_state.ActiveTabId is null)
    {
      await CreateTabAsync(destination);
      return;
    }
    _showingOverview = false;
    _overviewLayer.Visibility = Visibility.Collapsed;
    UpdateTab(_state.ActiveTabId, t => t with { Url = destination });
    if (!_addressInput.IsKeyboardFocusWithin)
      _addressInput.Text = DisplayAddress(destination);

    if (destination == "about:blank")
    {
      ApplySurfaceMode();
      UpdateNavigationButtons();
      UpdateBookmarkStarState();
      return;
    }

    if (!_views.TryGetValue(_state.ActiveTabId, out var view))
    {
      var tab = ActiveTab();
      if (tab is null) return;
      view = await CreateWebViewForTabAsync(tab);
    }
    else if (view.CoreWebView2 is not null)
    {
      view.CoreWebView2.Navigate(destination);
    }
    ApplySurfaceMode();
  }

  internal async Task CreateTabAsync(string url = "about:blank")
  {
    if (_state.Tabs.Count >= SyncSnapshot.MaxTabs)
    {
      MessageBox.Show(this, $"탭은 최대 {SyncSnapshot.MaxTabs}개까지 열 수 있습니다.", "탭 제한");
      return;
    }
    var tab = new TabEntry(Guid.NewGuid().ToString("N"), url == "about:blank" ? "새 탭" : HostName(url), url, _state.ActiveProfileId, _state.ActiveGroupId);
    _state.Tabs.Add(tab);
    _state.ActiveTabId = tab.Id;
    _showingOverview = false;
    _overviewLayer.Visibility = Visibility.Collapsed;
    MarkLocalChange();
    RefreshWorkspaceUi();
    await ActivateCurrentTabAsync();
    if (url == "about:blank")
    {
      _addressInput.Focus();
      _addressInput.SelectAll();
    }
  }

  private async Task SelectTabAsync(string tabId)
  {
    var target = _state.Tabs.FirstOrDefault(t => t.Id == tabId);
    if (target is null) return;
    _state.ActiveProfileId = target.ProfileId;
    _state.ActiveGroupId = target.GroupId;
    _state.ActiveTabId = target.Id;
    RefreshWorkspaceUi();
    await ActivateCurrentTabAsync();
  }

  private async Task CloseTabAsync(string tabId)
  {
    var idx = _state.Tabs.FindIndex(t => t.Id == tabId);
    if (idx < 0) return;
    var closed = _state.Tabs[idx];
    if (closed.GroupId != PrivateGroupId) _closedTabs.Push(closed);

    if (_views.Remove(tabId, out var view))
    {
      _webViewLayer.Children.Remove(view);
      try { view.Dispose(); } catch { }
    }
    _thumbnails.Remove(tabId);
    _tabFavicons.Remove(tabId);
    DesktopThumbnailStore.Delete(tabId);
    _state.Tabs.RemoveAt(idx);

    var remaining = VisibleTabs();
    if (remaining.Count == 0)
    {
      var fresh = new TabEntry(Guid.NewGuid().ToString("N"), "새 탭", "about:blank", _state.ActiveProfileId, _state.ActiveGroupId);
      _state.Tabs.Add(fresh);
      _state.ActiveTabId = fresh.Id;
    }
    else if (_state.ActiveTabId == tabId)
    {
      _state.ActiveTabId = remaining[Math.Min(remaining.Count - 1, Math.Max(0, idx - 1))].Id;
    }

    MarkLocalChange();
    RefreshWorkspaceUi();
    await ActivateCurrentTabAsync();
  }

  private async Task DuplicateTabAsync(string sourceTabId)
  {
    if (_state.Tabs.Count >= SyncSnapshot.MaxTabs) return;
    var idx = _state.Tabs.FindIndex(t => t.Id == sourceTabId);
    if (idx < 0) return;
    var src = _state.Tabs[idx];
    var copy = new TabEntry(Guid.NewGuid().ToString("N"), src.Title, src.Url, src.ProfileId, src.GroupId);
    _state.Tabs.Insert(idx + 1, copy);
    _state.ActiveTabId = copy.Id;
    MarkLocalChange();
    RefreshWorkspaceUi();
    await ActivateCurrentTabAsync();
  }

  private async Task ReopenClosedTabAsync()
  {
    if (_closedTabs.Count == 0 || _state.Tabs.Count >= SyncSnapshot.MaxTabs) return;
    var restored = _closedTabs.Pop() with { Id = Guid.NewGuid().ToString("N") };
    _state.Tabs.Add(restored);
    _state.ActiveProfileId = restored.ProfileId;
    _state.ActiveGroupId = restored.GroupId;
    _state.ActiveTabId = restored.Id;
    MarkLocalChange();
    RefreshWorkspaceUi();
    await ActivateCurrentTabAsync();
  }

  private void SwitchTabGroup(string? groupId)
  {
    _state.ActiveGroupId = groupId;
    EnsureWorkspaceTab();
    RefreshWorkspaceUi();
    _ = ActivateCurrentTabAsync();
  }

  private void SwitchRelativeTab(int delta)
  {
    var list = VisibleTabs();
    if (list.Count <= 1) return;
    var cur = list.FindIndex(t => t.Id == _state.ActiveTabId);
    var next = (cur + delta + list.Count) % list.Count;
    _ = SelectTabAsync(list[next].Id);
  }

  private void UpdateTab(string tabId, Func<TabEntry, TabEntry> update)
  {
    var idx = _state.Tabs.FindIndex(t => t.Id == tabId);
    if (idx < 0) return;
    var updated = update(_state.Tabs[idx]);
    if (updated == _state.Tabs[idx]) return;
    _state.Tabs[idx] = updated;
    MarkLocalChange();
  }

  private void GoBack() { if (ActiveWebView()?.CoreWebView2 is { CanGoBack: true } core) core.GoBack(); }
  private void GoForward() { if (ActiveWebView()?.CoreWebView2 is { CanGoForward: true } core) core.GoForward(); }

  private void ReloadOrStopActiveTab()
  {
    var core = ActiveWebView()?.CoreWebView2;
    if (core is null) return;
    if (_isLoading) core.Stop();
    else core.Reload();
  }

  private void AdjustZoom(double delta)
  {
    var view = ActiveWebView();
    if (view is null) return;
    view.ZoomFactor = Math.Clamp(view.ZoomFactor + delta, 0.5, 2.5);
    _syncStatus = $"확대/축소 {Math.Round(view.ZoomFactor * 100)}%";
    UpdateStatusText();
  }

  private void ResetZoom()
  {
    var view = ActiveWebView();
    if (view is null) return;
    view.ZoomFactor = 1.0;
    _syncStatus = "확대/축소 100%";
    UpdateStatusText();
  }

  private async Task ToggleReaderModeAsync()
  {
    var core = ActiveWebView()?.CoreWebView2;
    if (core is null) return;
    try
    {
      await core.ExecuteScriptAsync(SafariWebScripts.ToggleReaderScript(_readerTheme, _readerFont, _readerFontSize));
    }
    catch { }
  }

  private async Task UpdateReaderStyleAsync()
  {
    var core = ActiveWebView()?.CoreWebView2;
    if (core is null || !_readerActive) return;
    try
    {
      await core.ExecuteScriptAsync(SafariWebScripts.UpdateReaderStyleScript(_readerTheme, _readerFont, _readerFontSize));
    }
    catch { }
  }

  private async Task ToggleDistractionControlAsync()
  {
    var core = ActiveWebView()?.CoreWebView2;
    if (core is null) return;
    try { await core.ExecuteScriptAsync(SafariWebScripts.ToggleDistractionControlScript); } catch { }
  }

  private async Task RestoreDistractionHiddenAsync()
  {
    var core = ActiveWebView()?.CoreWebView2;
    if (core is null) return;
    try { await core.ExecuteScriptAsync(SafariWebScripts.RestoreDistractionHiddenScript); } catch { }
  }

  private void OpenFindBar()
  {
    _findPopup.HorizontalOffset = Math.Max(16, _contentHost.ActualWidth - 330);
    _findPopup.VerticalOffset = 10;
    _findPopup.IsOpen = true;
    _findInput.Focus();
    _findInput.SelectAll();
  }

  private async Task FindInActiveTabAsync(string term, bool backwards)
  {
    var core = ActiveWebView()?.CoreWebView2;
    if (core is null || string.IsNullOrEmpty(term)) return;
    try
    {
      var q = JsonSerializer.Serialize(term);
      await core.ExecuteScriptAsync($"window.find({q}, false, {(backwards ? "true" : "false")}, true, false, false, false);");
    }
    catch { }
  }

  private async Task CaptureTabThumbnailAsync(string tabId)
  {
    if (_closing || !_views.TryGetValue(tabId, out var view) || view.CoreWebView2 is null || view.Visibility != Visibility.Visible)
      return;
    try
    {
      using var ms = new MemoryStream();
      await view.CoreWebView2.CapturePreviewAsync(CoreWebView2CapturePreviewImageFormat.Png, ms).WaitAsync(TimeSpan.FromMilliseconds(900));
      ms.Position = 0;
      using var full = System.Drawing.Image.FromStream(ms);
      DesktopThumbnailStore.Save(tabId, full);
      using var saved = DesktopThumbnailStore.Load(tabId);
      var src = DwmBackdrop.FromDrawingImage(saved ?? full);
      if (src is not null) _thumbnails[tabId] = src;
    }
    catch { }
  }

  // Live Tall-Page Region Capture (shared pure CoreWebView2 engine!)
  private async Task CaptureSelectedRegionAsync(CaptureAction action)
  {
    if (_captureInProgress) return;
    var view = ActiveWebView();
    if (view?.CoreWebView2 is null || view.Visibility != Visibility.Visible)
    {
      MessageBox.Show(this, "캡처할 수 있는 활성 페이지가 없습니다.", "화면 캡처");
      return;
    }
    var core = view.CoreWebView2;
    var priorStatus = _syncStatus;
    _captureInProgress = true;
    try
    {
      _syncStatus = "실시간 페이지에서 캡처할 영역을 선택하세요";
      UpdateStatusText();
      await WebViewLiveCapture.PreparePageForCaptureAsync(core);
      var selection = await WebViewLiveCapture.SelectLiveCaptureRegionAsync(core, BookshelfStore.Load(), action, showBookshelfPicker: true);
      if (selection is null)
      {
        _syncStatus = priorStatus;
        UpdateStatusText();
        return;
      }
      _syncStatus = $"선택 영역 {Math.Round(selection.Width):N0} × {Math.Round(selection.Height):N0} 캡처 중…";
      UpdateStatusText();
      using var bitmap = await WebViewLiveCapture.CapturePageRegionBitmapAsync(core, selection);
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
          System.Windows.Forms.Clipboard.SetImage(bitmap);
          _syncStatus = "선택 영역을 클립보드에 복사함";
          UpdateStatusText();
          break;
        case CaptureAction.AddToBookshelf:
          var latestCatalog = BookshelfStore.Load();
          if (latestCatalog.Folders.Contains(selection.Folder, StringComparer.CurrentCultureIgnoreCase))
          {
            latestCatalog.ActiveFolder = latestCatalog.Folders.First(f => f.Equals(selection.Folder, StringComparison.CurrentCultureIgnoreCase));
            BookshelfStore.Save(latestCatalog);
          }
          var item = BookshelfStore.AddImage(latestCatalog, bitmap, ActiveTab()?.Url ?? string.Empty);
          if (_bookshelfForm is { IsDisposed: false }) _bookshelfForm.ReloadCatalogFromExternalChange();
          _syncStatus = $"책장 ‘{item.Folder}’에 선택 영역 추가함";
          UpdateStatusText();
          break;
      }
    }
    catch (Exception ex)
    {
      _syncStatus = priorStatus;
      UpdateStatusText();
      MessageBox.Show(this, "실시간 영역 캡처 오류:\n" + ex.Message, "화면 캡처");
    }
    finally
    {
      await WebViewLiveCapture.RemoveLiveCaptureOverlayAsync(core);
      await WebViewLiveCapture.RestorePageAfterCaptureAsync(core);
      _captureInProgress = false;
    }
  }

  private async Task CaptureActiveTabAsync()
  {
    var core = ActiveWebView()?.CoreWebView2;
    if (core is null)
    {
      MessageBox.Show(this, "캡처할 수 있는 활성 페이지가 없습니다.", "화면 캡처");
      return;
    }
    try
    {
      using var ms = new MemoryStream();
      await core.CapturePreviewAsync(CoreWebView2CapturePreviewImageFormat.Png, ms);
      ms.Position = 0;
      using var img = System.Drawing.Image.FromStream(ms);
      System.Windows.Forms.Clipboard.SetImage(img);
      _syncStatus = "현재 화면을 클립보드에 복사했습니다";
      UpdateStatusText();
      if (MessageBox.Show(this, "화면이 클립보드에 복사되었습니다.\nPNG 파일로도 저장할까요?", "화면 캡처 완료", MessageBoxButton.YesNo, MessageBoxImage.Information) == MessageBoxResult.Yes)
      {
        using var picker = new System.Windows.Forms.SaveFileDialog
        {
          Filter = "PNG 이미지 (*.png)|*.png",
          FileName = $"Omyaru_Capture_{DateTime.Now:yyyyMMdd_HHmmss}.png",
        };
        if (picker.ShowDialog(DwmBackdrop.OwnerOf(this)) == System.Windows.Forms.DialogResult.OK)
        {
          File.WriteAllBytes(picker.FileName, ms.ToArray());
          _syncStatus = "PNG 저장 · " + picker.FileName;
          UpdateStatusText();
        }
      }
    }
    catch (Exception ex)
    {
      MessageBox.Show(this, "화면 캡처 오류: " + ex.Message, "캡처 오류");
    }
  }

  private void SaveCapturePng(System.Drawing.Image bitmap)
  {
    var dir = _preferences.LastCaptureDirectory;
    if (string.IsNullOrWhiteSpace(dir) || !Directory.Exists(dir))
    {
      using var picker = new System.Windows.Forms.FolderBrowserDialog
      {
        Description = "Omyaru 화면 캡처를 저장할 폴더를 선택하세요.",
        UseDescriptionForTitle = true,
        InitialDirectory = Environment.GetFolderPath(Environment.SpecialFolder.MyPictures),
      };
      if (picker.ShowDialog(DwmBackdrop.OwnerOf(this)) != System.Windows.Forms.DialogResult.OK) return;
      dir = picker.SelectedPath;
      _preferences.LastCaptureDirectory = dir;
      DesktopPreferencesStore.Save(_preferences);
    }
    Directory.CreateDirectory(dir);
    var title = ActiveTab()?.Title ?? "capture";
    var safe = new string(title.Select(c => Path.GetInvalidFileNameChars().Contains(c) ? '_' : c).ToArray()).Trim();
    if (safe.Length > 48) safe = safe[..48];
    if (safe.Length == 0) safe = "capture";
    var path = Path.Combine(dir, $"{DateTime.Now:yyyyMMdd-HHmmss}-{safe}.png");
    bitmap.Save(path, System.Drawing.Imaging.ImageFormat.Png);
    _syncStatus = "PNG 저장 · " + path;
    UpdateStatusText();
  }

  private async Task RemoveWatermarkFromActiveTabAsync()
  {
    var core = ActiveWebView()?.CoreWebView2;
    if (core is null)
    {
      await CreateTabAsync("https://www.watermarkremover.io/ko/upload");
      return;
    }
    try
    {
      var tempDir = Path.Combine(Path.GetTempPath(), "OmyaruCaptures");
      Directory.CreateDirectory(tempDir);
      var tempPath = Path.Combine(tempDir, $"capture_{DateTime.Now:yyyyMMdd_HHmmss}.png");
      using (var fs = new FileStream(tempPath, FileMode.Create))
        await core.CapturePreviewAsync(CoreWebView2CapturePreviewImageFormat.Png, fs);
      using (var img = System.Drawing.Image.FromFile(tempPath))
        System.Windows.Forms.Clipboard.SetImage(img);
      await CreateTabAsync("https://www.watermarkremover.io/ko/upload");
      MessageBox.Show(this,
          $"현재 화면을 클립보드에 복사했습니다.\n새로 열린 페이지에서 Ctrl+V로 붙여넣거나 아래 파일을 업로드하세요:\n{tempPath}",
          "워터마크 삭제 — 캡처 완료", MessageBoxButton.OK, MessageBoxImage.Information);
    }
    catch (Exception ex)
    {
      MessageBox.Show(this, "워터마크 삭제 캡처 오류: " + ex.Message, "오류");
    }
  }

  private async Task InstallExtensionFromStoreUrlAsync(string storeOrPackageUrl, WpfWebView2? sourceView = null)
  {
    var view = sourceView ?? ActiveWebView();
    if (view?.CoreWebView2 is null)
    {
      await NavigateToAsync("https://chromewebstore.google.com/");
      view = ActiveWebView();
      if (view?.CoreWebView2 is null) return;
    }
    try
    {
      _syncStatus = "확장 프로그램 다운로드 및 설치 중…";
      UpdateStatusText();
      var installed = await ExtensionManagerForm.InstallFromStoreOrUrlAsync(
          view.CoreWebView2.Profile,
          _state.ActiveProfileId,
          storeOrPackageUrl,
          progress => { _syncStatus = progress; UpdateStatusText(); });
      _syncStatus = $"확장 프로그램 '{installed.Name}' 설치 완료";
      UpdateStatusText();
      if (view.CoreWebView2 is not null)
        _ = view.CoreWebView2.ExecuteScriptAsync($"window.__omyaruMarkExtensionInstalled?.({JsonSerializer.Serialize(installed.Name)});");
      MessageBox.Show(this, $"'{installed.Name}' 확장 프로그램이 설치되었습니다.\n상단 퍼즐(🧩) 아이콘에서 실행하거나 관리할 수 있습니다.", "확장 프로그램 설치 완료");
    }
    catch (Exception ex)
    {
      _syncStatus = "확장 프로그램 설치 실패 · " + ex.Message;
      UpdateStatusText();
      if (view.CoreWebView2 is not null)
        _ = view.CoreWebView2.ExecuteScriptAsync($"window.__omyaruMarkExtensionFailed?.({JsonSerializer.Serialize(ex.Message)});");
      MessageBox.Show(this, "확장 프로그램을 설치하지 못했습니다.\n\n" + ex.Message, "확장 프로그램 설치 실패", MessageBoxButton.OK, MessageBoxImage.Warning);
    }
  }

  private async Task ApplyAudioOutputToAllAsync(string? selectedDeviceId = null)
  {
    var deviceId = selectedDeviceId ?? _preferences.AudioOutputDeviceId;
    var pids = _views.Values
        .Where(v => v.CoreWebView2 is not null)
        .Select(v => (int)v.CoreWebView2.BrowserProcessId)
        .Distinct()
        .ToArray();
    if (pids.Length == 0) return;
    var routing = await Task.Run(() =>
    {
      var ok = WindowsAudioRouter.TryRoute(pids, deviceId, out var err);
      return (ok, err);
    });
    if (!routing.ok)
    {
      _syncStatus = "출력 장치 적용 실패 · " + routing.err;
      UpdateStatusText();
    }
  }

  // Automated Smoke-Test Verification Hooks (runs real WebView2 + Extension + Tall-Page Capture!)
  internal async Task<bool> VerifySafariSmokeAsync()
  {
    await NavigateToAsync("https://example.com/");
    var wait = DateTime.UtcNow.AddSeconds(12);
    while (!HasInitializedWebView && DateTime.UtcNow < wait)
      await Task.Delay(100);
    var view = ActiveWebView();
    if (view?.CoreWebView2 is null) return false;

    // 1. Verify Sidebar & Tab Overview GPU toggles cleanly
    ToggleSidebar();
    ToggleSidebar();
    ToggleTabOverview();
    ToggleTabOverview();

    // 2. Verify Chrome MV3 + Firefox-metadata MV3 extension install/remove on real WebView2
    var profile = view.CoreWebView2.Profile;
    var ffDir = Path.Combine(BrowserStateStore.StateDirectory, "safari-ext-smoke-" + Guid.NewGuid().ToString("N"));
    CoreWebView2BrowserExtension? ext = null;
    try
    {
      Directory.CreateDirectory(ffDir);
      Directory.CreateDirectory(Path.Combine(ffDir, "META-INF"));
      Directory.CreateDirectory(Path.Combine(ffDir, "_metadata"));
      File.WriteAllText(Path.Combine(ffDir, "META-INF", "manifest.mf"), "Manifest-Version: 1.0");
      File.WriteAllText(Path.Combine(ffDir, "_metadata", "verified_contents.json"), "{}");
      File.WriteAllText(Path.Combine(ffDir, "manifest.json"),
          """{"manifest_version":3,"name":"Safari Smoke Ext","version":"1.0","browser_specific_settings":{"gecko":{"id":"smoke@omyaru.test"}},"background":{"scripts":["bg.js"]},"content_scripts":[{"matches":["https://example.com/*"],"js":["c.js"]}]}""");
      File.WriteAllText(Path.Combine(ffDir, "bg.js"), "console.log('ok');");
      File.WriteAllText(Path.Combine(ffDir, "c.js"), "console.log('ok');");
      ext = await ExtensionManagerForm.InstallFromLocalPathAsync(profile, _state.ActiveProfileId, ffDir, isPackageFile: false);
      if (!(await profile.GetBrowserExtensionsAsync()).Any(x => x.Id == ext.Id)) return false;
      await ext.EnableAsync(false);
      await ext.EnableAsync(true);
      if (!ext.IsEnabled) return false;
    }
    finally
    {
      if (ext is not null) try { await ext.RemoveAsync(); } catch { }
      try { Directory.Delete(ffDir, true); } catch { }
    }

    // 3. Verify live tall-page capture engine on CoreWebView2
    var core = view.CoreWebView2;
    var loaded = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
    void Done(object? s, CoreWebView2NavigationCompletedEventArgs args) => loaded.TrySetResult(args.IsSuccess);
    core.NavigationCompleted += Done;
    try
    {
      core.NavigateToString("""
          <!doctype html><html><body style="margin:0;background:#101828">
          <div style="width:720px;height:26000px;margin:0 auto;background:repeating-linear-gradient(180deg,#ff6b6b 0 120px,#ffd166 120px 240px,#06d6a0 240px 360px,#4cc9f0 360px 480px)"></div>
          </body></html>
          """);
      await Task.WhenAny(loaded.Task, Task.Delay(8000));
    }
    finally { core.NavigationCompleted -= Done; }

    await Task.Delay(250);
    await WebViewLiveCapture.PreparePageForCaptureAsync(core);
    using var captured = await WebViewLiveCapture.CapturePageRegionBitmapAsync(
        core,
        new WebViewLiveCapture.LiveCaptureSelection(360, 24000, 719, 1476, "bookshelf", CaptureAction.AddToBookshelf));
    await WebViewLiveCapture.RestorePageAfterCaptureAsync(core);
    return captured is { Width: >= 719, Height: >= 1476 } && !WebViewLiveCapture.IsNearlyUniformColor(captured);
  }
}
