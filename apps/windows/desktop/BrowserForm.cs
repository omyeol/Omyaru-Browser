using System.Drawing;
using System.IO;
using System.Security.Cryptography;
using System.Text;
using System.Windows.Forms;
using Microsoft.Web.WebView2.Core;
using Microsoft.Web.WebView2.WinForms;

namespace PrivacyBrowser.Desktop;

internal sealed class BrowserForm : Form
{
  private static readonly Color Background = Color.FromArgb(24, 29, 35);
  private static readonly Color Surface = Color.FromArgb(43, 50, 59);
  private static readonly Color Field = Color.FromArgb(53, 62, 73);
  private static readonly Color TextColor = Color.FromArgb(242, 244, 248);
  private static readonly Color MutedText = Color.FromArgb(171, 181, 193);
  private static readonly Color Accent = Color.FromArgb(109, 139, 255);

  private SyncSnapshot _state = BrowserStateStore.Load();
  private readonly ComboBox _profileSelector = new();
  private readonly ComboBox _groupSelector = new();
  private readonly TextBox _address = new();
  private readonly TextBox _tabSearch = new();
  private readonly ListBox _tabList = new();
  private readonly Panel _browserHost = new();
  private readonly ToolStripStatusLabel _statusText = new("로컬 저장 · 분석/광고 SDK 없음");
  private readonly Label _groupTabCountLabel = new();
  private readonly Label _zoomLabel = new();
  private readonly Stack<TabEntry> _closedTabs = new();
  private double _currentZoom = 1.0;
  private readonly Dictionary<string, WebView2> _views = new(StringComparer.Ordinal);
  private readonly Dictionary<string, Task<CoreWebView2Environment>> _environments = new(StringComparer.Ordinal);
  private readonly System.Windows.Forms.Timer _autoSyncTimer = new() { Interval = 2_000 };
  private readonly bool _enableAutomaticSync;
  private bool _updatingSelectors;
  private bool _closing;
  private bool _stateDirty;
  private bool _autoSyncBusy;
  private string _syncStatus = "로컬 저장 · 분석/광고 SDK 없음";
  internal bool HasInitializedWebView => _views.Values.Any(view => view.CoreWebView2 is not null);

  internal BrowserForm(bool enableAutomaticSync = true)
  {
    _enableAutomaticSync = enableAutomaticSync;
    Text = "Omyaru — Windows Preview";
    StartPosition = FormStartPosition.CenterScreen;
    MinimumSize = new Size(1000, 680);
    Size = new Size(1400, 880);
    BackColor = Background;
    ForeColor = TextColor;
    Font = new Font("Segoe UI", 10F);
    BuildInterface();
    _autoSyncTimer.Tick += async (_, _) => await FlushAndAutoSyncAsync();
    Shown += async (_, _) =>
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
    FormClosing += (_, _) =>
    {
      _closing = true;
      _autoSyncTimer.Stop();
      if (_stateDirty) BrowserStateStore.SaveChanged(_state);
      foreach (var view in _views.Values) view.Dispose();
    };
  }

  private void BuildInterface()
  {
    var toolbar = new FlowLayoutPanel
    {
      Dock = DockStyle.Top,
      Height = 58,
      BackColor = Surface,
      Padding = new Padding(10, 9, 10, 7),
      WrapContents = false,
      AutoScroll = true,
    };
    toolbar.Controls.Add(MakeButton("←", async (_, _) => await ActiveViewActionAsync(view => view.GoBack(), view => view.CanGoBack)));
    toolbar.Controls.Add(MakeButton("→", async (_, _) => await ActiveViewActionAsync(view => view.GoForward(), view => view.CanGoForward)));
    toolbar.Controls.Add(MakeButton("↻", async (_, _) => await ActiveViewActionAsync(view => view.Reload())));
    ConfigureTextBox(_address, 360);
    _address.PlaceholderText = "주소 또는 검색어";
    _address.KeyDown += async (_, eventArgs) =>
    {
      if (eventArgs.KeyCode != Keys.Enter) return;
      eventArgs.SuppressKeyPress = true;
      await NavigateFromAddressAsync();
    };
    toolbar.Controls.Add(_address);
    toolbar.Controls.Add(MakeButton("이동", async (_, _) => await NavigateFromAddressAsync(), 50));
    toolbar.Controls.Add(MakeButton("＋ 탭", async (_, _) => await CreateTabAsync(), 64));

    ConfigureCombo(_profileSelector, 125);
    _profileSelector.SelectedIndexChanged += async (_, _) =>
    {
      if (_updatingSelectors || _profileSelector.SelectedItem is not ProfileEntry profile) return;
      _state.ActiveProfileId = profile.Id;
      _state.ActiveGroupId = null;
      EnsureWorkspaceTab();
      MarkLocalChange();
      RefreshWorkspaceUi();
      await ActivateCurrentTabAsync();
    };
    toolbar.Controls.Add(_profileSelector);

    ConfigureCombo(_groupSelector, 130);
    _groupSelector.SelectedIndexChanged += async (_, _) =>
    {
      if (_updatingSelectors || _groupSelector.SelectedItem is not GroupChoice choice) return;
      _state.ActiveGroupId = choice.Id;
      EnsureWorkspaceTab();
      MarkLocalChange();
      RefreshWorkspaceUi();
      await ActivateCurrentTabAsync();
    };
    toolbar.Controls.Add(_groupSelector);
    toolbar.Controls.Add(MakeButton("그룹＋", (_, _) => CreateGroup(), 62));
    toolbar.Controls.Add(MakeButton("프로필＋", (_, _) => CreateProfile(), 72));
    toolbar.Controls.Add(MakeButton("동기화", async (_, _) => await ShowSyncDialogAsync(), 62));

    // 배율 제어 ([-] 100% [+])
    toolbar.Controls.Add(MakeButton("－", async (_, _) => await ZoomOutAsync(), 32));
    _zoomLabel.Text = "100%";
    _zoomLabel.Width = 52;
    _zoomLabel.Height = 38;
    _zoomLabel.TextAlign = ContentAlignment.MiddleCenter;
    _zoomLabel.BackColor = Field;
    _zoomLabel.ForeColor = TextColor;
    _zoomLabel.Margin = new Padding(2, 0, 2, 0);
    _zoomLabel.Cursor = Cursors.Hand;
    _zoomLabel.Click += async (_, _) => await ResetZoomAsync();
    toolbar.Controls.Add(_zoomLabel);
    toolbar.Controls.Add(MakeButton("＋", async (_, _) => await ZoomInAsync(), 32));

    // 화면 캡처 및 워터마크 삭제
    toolbar.Controls.Add(MakeButton("📷 캡처", async (_, _) => await CaptureActiveTabAsync(), 74));
    toolbar.Controls.Add(MakeButton("✨ 워터마크 삭제", async (_, _) => await RemoveWatermarkFromActiveTabAsync(), 118));
    Controls.Add(toolbar);

    var sidebar = new Panel { Dock = DockStyle.Left, Width = 280, BackColor = Surface, Padding = new Padding(10) };

    var topPanel = new Panel { Dock = DockStyle.Top, Height = 68, BackColor = Surface };
    _groupTabCountLabel.Dock = DockStyle.Top;
    _groupTabCountLabel.Height = 28;
    _groupTabCountLabel.TextAlign = ContentAlignment.MiddleLeft;
    _groupTabCountLabel.ForeColor = Accent;
    _groupTabCountLabel.Font = new Font("Segoe UI", 9.5F, FontStyle.Bold);
    topPanel.Controls.Add(_groupTabCountLabel);

    ConfigureTextBox(_tabSearch, 260);
    _tabSearch.Dock = DockStyle.Bottom;
    _tabSearch.PlaceholderText = "탭 제목 검색";
    _tabSearch.TextChanged += (_, _) => RefreshTabList();
    topPanel.Controls.Add(_tabSearch);
    sidebar.Controls.Add(topPanel);

    _tabList.Dock = DockStyle.Fill;
    _tabList.BackColor = Background;
    _tabList.ForeColor = TextColor;
    _tabList.BorderStyle = BorderStyle.None;
    _tabList.IntegralHeight = false;
    _tabList.ItemHeight = 34;
    _tabList.SelectedIndexChanged += async (_, _) =>
    {
      if (_updatingSelectors || _tabList.SelectedItem is not TabEntry tab) return;
      _state.ActiveTabId = tab.Id;
      MarkLocalChange();
      await ActivateCurrentTabAsync();
    };

    var contextMenu = new ContextMenuStrip();
    contextMenu.Items.Add("선택한 탭 닫기 (Ctrl+W)", null, async (_, _) => await CloseSelectedTabAsync());
    contextMenu.Items.Add("다른 탭 모두 닫기", null, async (_, _) => await CloseOtherTabsAsync());
    contextMenu.Items.Add("모든 탭 닫기 (5초 확인)", null, async (_, _) => await CloseAllTabsWithCountdownAsync());
    contextMenu.Items.Add(new ToolStripSeparator());
    contextMenu.Items.Add("닫은 탭 다시 열기 (Ctrl+Shift+T)", null, async (_, _) => await RestoreLastClosedTabAsync());
    contextMenu.Items.Add("새 탭 (Ctrl+T)", null, async (_, _) => await CreateTabAsync());
    _tabList.ContextMenuStrip = contextMenu;

    _tabList.MouseDown += async (s, e) =>
    {
      if (e.Button == MouseButtons.Right)
      {
        var idx = _tabList.IndexFromPoint(e.Location);
        if (idx >= 0) _tabList.SelectedIndex = idx;
      }
      else if (e.Button == MouseButtons.Middle)
      {
        var idx = _tabList.IndexFromPoint(e.Location);
        if (idx >= 0 && _tabList.Items[idx] is TabEntry tab)
        {
          _tabList.SelectedIndex = idx;
          await CloseSelectedTabAsync();
        }
      }
    };

    sidebar.Controls.Add(_tabList);
    _tabList.BringToFront();

    var bottomPanel = new FlowLayoutPanel
    {
      Dock = DockStyle.Bottom,
      Height = 118,
      BackColor = Surface,
      FlowDirection = FlowDirection.TopDown,
      WrapContents = false,
      Padding = new Padding(0, 4, 0, 0),
    };
    var closeTab = MakeButton("선택한 탭 닫기 (Ctrl+W)", async (_, _) => await CloseSelectedTabAsync(), 260);
    var closeAll = MakeButton("모든 탭 닫기 (5초 확인)", async (_, _) => await CloseAllTabsWithCountdownAsync(), 260);
    closeAll.ForeColor = Color.FromArgb(255, 140, 140);
    var restoreTab = MakeButton("닫은 탭 다시 열기 (Ctrl+Shift+T)", async (_, _) => await RestoreLastClosedTabAsync(), 260);
    restoreTab.Height = 32;
    bottomPanel.Controls.AddRange([closeTab, closeAll, restoreTab]);
    sidebar.Controls.Add(bottomPanel);

    Controls.Add(sidebar);

    _browserHost.Dock = DockStyle.Fill;
    _browserHost.BackColor = Background;
    Controls.Add(_browserHost);
    _browserHost.BringToFront();

    var status = new StatusStrip { Dock = DockStyle.Bottom, BackColor = Surface, ForeColor = MutedText };
    status.Items.Add(_statusText);
    Controls.Add(status);
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
    MarkLocalChange();
    RefreshTabList();
    await ActivateCurrentTabAsync();
    if (url == "about:blank") _address.Focus();
  }

  private async Task CloseSelectedTabAsync()
  {
    if (_tabList.SelectedItem is not TabEntry tab) return;
    _closedTabs.Push(tab);
    if (_views.Remove(tab.Id, out var view))
    {
      _browserHost.Controls.Remove(view);
      view.Dispose();
    }
    _state.Tabs.RemoveAll(item => item.Id == tab.Id);
    EnsureWorkspaceTab();
    MarkLocalChange();
    RefreshTabList();
    await ActivateCurrentTabAsync();
  }

  private async Task CloseOtherTabsAsync()
  {
    if (_tabList.SelectedItem is not TabEntry selectedTab) return;
    var toClose = VisibleTabs().Where(t => t.Id != selectedTab.Id).ToList();
    if (toClose.Count == 0) return;
    foreach (var tab in toClose)
    {
      _closedTabs.Push(tab);
      if (_views.Remove(tab.Id, out var view))
      {
        _browserHost.Controls.Remove(view);
        view.Dispose();
      }
      _state.Tabs.RemoveAll(item => item.Id == tab.Id);
    }
    MarkLocalChange();
    RefreshTabList();
    await ActivateCurrentTabAsync();
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
      _state.Tabs.RemoveAll(item => item.Id == tab.Id);
    }
    EnsureWorkspaceTab();
    MarkLocalChange();
    RefreshWorkspaceUi();
    await ActivateCurrentTabAsync();
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
    _updatingSelectors = true;
    try
    {
      _profileSelector.Items.Clear();
      _profileSelector.Items.AddRange(_state.Profiles.Cast<object>().ToArray());
      _profileSelector.SelectedItem = _state.Profiles.FirstOrDefault(profile => profile.Id == _state.ActiveProfileId);

      _groupSelector.Items.Clear();
      _groupSelector.Items.Add(new GroupChoice(null, "그룹 없음"));
      foreach (var group in _state.Groups.Where(group => group.ProfileId == _state.ActiveProfileId))
        _groupSelector.Items.Add(new GroupChoice(group.Id, group.Name));
      _groupSelector.SelectedItem = _groupSelector.Items.Cast<GroupChoice>()
          .First(choice => choice.Id == _state.ActiveGroupId);
      RefreshTabList();
    }
    finally
    {
      _updatingSelectors = false;
    }
  }

  private void RefreshTabList()
  {
    var query = _tabSearch.Text.Trim();
    var visible = VisibleTabs().ToList();
    var groupName = "그룹 없음";
    if (_state.ActiveGroupId != null)
    {
      var grp = _state.Groups.FirstOrDefault(g => g.Id == _state.ActiveGroupId);
      if (grp != null) groupName = grp.Name;
    }
    _groupTabCountLabel.Text = $"{groupName} · {visible.Count}개";

    var items = visible
        .Where(tab => query.Length == 0 || tab.Title.Contains(query, StringComparison.CurrentCultureIgnoreCase))
        .Cast<object>()
        .ToArray();
    var previous = _updatingSelectors;
    _updatingSelectors = true;
    _tabList.Items.Clear();
    _tabList.Items.AddRange(items);
    _tabList.SelectedItem = items.Cast<TabEntry>().FirstOrDefault(tab => tab.Id == _state.ActiveTabId);
    _updatingSelectors = previous;
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
    _address.Text = tab.Url == "about:blank" ? string.Empty : tab.Url;
    try { view.ZoomFactor = _currentZoom; } catch { }
    _zoomLabel.Text = $"{(int)Math.Round(_currentZoom * 100)}%";
    UpdateStatusText();
    RefreshTabList();
  }

  private async Task<WebView2> CreateViewAsync(TabEntry tab)
  {
    var view = new WebView2
    {
      Dock = DockStyle.Fill,
      Visible = false,
      BackColor = Background,
      DefaultBackgroundColor = Background,
    };
    _browserHost.Controls.Add(view);
    var environment = await EnvironmentForProfileAsync(tab.ProfileId);
    await view.EnsureCoreWebView2Async(environment);
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
      if (_state.ActiveTabId == tab.Id) _address.Text = source == "about:blank" ? string.Empty : source;
    };
    view.CoreWebView2.NewWindowRequested += async (_, eventArgs) =>
    {
      eventArgs.Handled = true;
      await CreateTabAsync(eventArgs.Uri);
    };
    view.CoreWebView2.Navigate(tab.Url);
    try { view.ZoomFactor = _currentZoom; } catch { }
    return view;
  }

  private Task<CoreWebView2Environment> EnvironmentForProfileAsync(string profileId)
  {
    if (_environments.TryGetValue(profileId, out var environment)) return environment;
    var digest = Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(profileId))).ToLowerInvariant();
    var folder = Path.Combine(BrowserStateStore.StateDirectory, "profiles", digest);
    Directory.CreateDirectory(folder);
    var options = new CoreWebView2EnvironmentOptions { AllowSingleSignOnUsingOSPrimaryAccount = false };
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
    if (_state.ActiveTabId == tabId) RefreshTabList();
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
    var profileName = tab is null
        ? _state.Profiles.FirstOrDefault()?.Name ?? "프로필"
        : _state.Profiles.FirstOrDefault(profile => profile.Id == tab.ProfileId)?.Name ?? "프로필";
    var title = tab?.Title ?? "탭 없음";
    _statusText.Text = $"{profileName} · {title} · {_syncStatus}";
  }

  private async Task FlushAndAutoSyncAsync()
  {
    if (_closing || _autoSyncBusy) return;
    if (_stateDirty)
    {
      BrowserStateStore.SaveChanged(_state);
      _stateDirty = false;
    }
    if (!_enableAutomaticSync) return;
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
      var outcome = await Task.Run(() => DesktopAutoSync.Run(_state.Copy()));
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
            AutoSyncSettingsStore.UpdateBaseline(
                AutoSyncSettingsStore.Load(),
                outcome.Fingerprint!,
                outcome.Imported.ModifiedAtEpochMs);
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

  private async Task NavigateFromAddressAsync()
  {
    var destination = ResolveInput(_address.Text);
    if (_state.ActiveTabId is null) await CreateTabAsync(destination);
    else if (_views.TryGetValue(_state.ActiveTabId, out var view) && view.CoreWebView2 is not null)
      view.CoreWebView2.Navigate(destination);
    else
    {
      UpdateTab(_state.ActiveTabId, tab => tab with { Url = destination });
      await ActivateCurrentTabAsync();
    }
  }

  private static string ResolveInput(string input)
  {
    var candidate = input.Trim();
    if (candidate.Length == 0) return "about:blank";
    if (Uri.TryCreate(candidate, UriKind.Absolute, out var absolute) &&
        (absolute.Scheme == Uri.UriSchemeHttps || absolute.Scheme == Uri.UriSchemeHttp)) return absolute.ToString();
    if (!candidate.Contains(' ') && (candidate.Contains('.') || candidate.Equals("localhost", StringComparison.OrdinalIgnoreCase)))
      return "https://" + candidate;
    return "https://duckduckgo.com/?q=" + Uri.EscapeDataString(candidate);
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
    using var dialog = new SyncDialog(AutoSyncSettingsStore.Load());
    if (dialog.ShowDialog(this) != DialogResult.OK) return;
    try
    {
      if (dialog.Importing)
      {
        var imported = await Task.Run(() => SyncCodec.Read(dialog.SyncPath, dialog.Passphrase));
        await ApplyImportedStateAsync(imported);
        if (dialog.AutomaticSync)
          AutoSyncSettingsStore.Configure(
              dialog.SyncPath,
              dialog.Passphrase,
              SyncFileFingerprint.Compute(dialog.SyncPath),
              imported.ModifiedAtEpochMs);
        else
          AutoSyncSettingsStore.Disable();
        MessageBox.Show(this, "Android/Windows 동기화 파일을 가져왔습니다.", "동기화 완료");
      }
      else
      {
        BrowserStateStore.SaveChanged(_state);
        _stateDirty = false;
        await Task.Run(() => SyncCodec.Write(dialog.SyncPath, _state.Copy(), dialog.Passphrase));
        if (dialog.AutomaticSync)
          AutoSyncSettingsStore.Configure(
              dialog.SyncPath,
              dialog.Passphrase,
              SyncFileFingerprint.Compute(dialog.SyncPath),
              _state.ModifiedAtEpochMs);
        else
          AutoSyncSettingsStore.Disable();
        MessageBox.Show(this, "현재 프로필·탭 그룹·일반 탭을 암호화해 저장했습니다.", "동기화 완료");
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
    _state = imported;
    EnsureWorkspaceTab();
    _stateDirty = false;
    BrowserStateStore.SaveImported(_state);
    RefreshWorkspaceUi();
    await ActivateCurrentTabAsync();
  }

  private async Task ZoomInAsync()
  {
    _currentZoom = Math.Min(3.0, Math.Round(_currentZoom + 0.1, 2));
    await ApplyZoomAsync();
  }

  private async Task ZoomOutAsync()
  {
    _currentZoom = Math.Max(0.3, Math.Round(_currentZoom - 0.1, 2));
    await ApplyZoomAsync();
  }

  private async Task ResetZoomAsync()
  {
    _currentZoom = 1.0;
    await ApplyZoomAsync();
  }

  private async Task ApplyZoomAsync()
  {
    _zoomLabel.Text = $"{(int)Math.Round(_currentZoom * 100)}%";
    if (_state.ActiveTabId is not null && _views.TryGetValue(_state.ActiveTabId, out var view))
    {
      try { view.ZoomFactor = _currentZoom; } catch { }
    }
    await Task.CompletedTask;
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
      _ = CloseSelectedTabAsync();
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
      _ = ZoomInAsync();
      return true;
    }
    if (keyData == (Keys.Control | Keys.OemMinus) || keyData == (Keys.Control | Keys.Subtract))
    {
      _ = ZoomOutAsync();
      return true;
    }
    if (keyData == (Keys.Control | Keys.D0) || keyData == (Keys.Control | Keys.NumPad0))
    {
      _ = ResetZoomAsync();
      return true;
    }
    if (keyData == (Keys.Control | Keys.Shift | Keys.S))
    {
      _ = CaptureActiveTabAsync();
      return true;
    }
    return base.ProcessCmdKey(ref msg, keyData);
  }

  private static Button MakeButton(string text, EventHandler action, int width = 46)
  {
    var button = new Button
    {
      Text = text,
      Width = width,
      Height = 38,
      FlatStyle = FlatStyle.Flat,
      BackColor = Field,
      ForeColor = TextColor,
      Margin = new Padding(3, 0, 3, 0),
      TabStop = false,
    };
    button.FlatAppearance.BorderSize = 0;
    button.Click += action;
    return button;
  }

  private static void ConfigureTextBox(TextBox box, int width)
  {
    box.Width = width;
    box.Height = 38;
    box.BackColor = Field;
    box.ForeColor = TextColor;
    box.BorderStyle = BorderStyle.FixedSingle;
    box.Margin = new Padding(4, 2, 4, 0);
  }

  private static void ConfigureCombo(ComboBox box, int width)
  {
    box.Width = width;
    box.Height = 38;
    box.DropDownStyle = ComboBoxStyle.DropDownList;
    box.BackColor = Field;
    box.ForeColor = TextColor;
    box.FlatStyle = FlatStyle.Flat;
    box.Margin = new Padding(4, 2, 4, 0);
  }

  private sealed record GroupChoice(string? Id, string Name)
  {
    public override string ToString() => Name;
  }
}

internal sealed class SyncDialog : Form
{
  protected override CreateParams CreateParams => WinFormsSurface.WithComposited(base.CreateParams);
  private readonly TextBox _path = new() { Dock = DockStyle.Fill, BackColor = Color.FromArgb(34, 37, 46), ForeColor = Color.White, BorderStyle = BorderStyle.FixedSingle };
  private readonly TextBox _passphrase = new() { Dock = DockStyle.Fill, UseSystemPasswordChar = true, BackColor = Color.FromArgb(34, 37, 46), ForeColor = Color.White, BorderStyle = BorderStyle.FixedSingle };
  private readonly CheckBox _automaticSync = new()
  {
    Text = "한쪽만 변경됐을 때 자동 동기화",
    AutoSize = true,
    Checked = true,
    Anchor = AnchorStyles.Left,
  };
  internal bool Importing { get; private set; }
  internal string SyncPath => _path.Text.Trim();
  internal string Passphrase => _passphrase.Text;
  internal bool AutomaticSync => _automaticSync.Checked;

  internal SyncDialog(AutoSyncConfiguration configuration)
  {
    SuspendLayout();
    Text = "Android ↔ Windows 암호화 동기화";
    StartPosition = FormStartPosition.CenterParent;
    FormBorderStyle = FormBorderStyle.FixedDialog;
    MaximizeBox = false;
    MinimizeBox = false;
    ClientSize = new Size(650, 265);
    BackColor = Color.FromArgb(24, 26, 32);
    ForeColor = Color.FromArgb(245, 245, 247);
    Font = new Font("Segoe UI", 9.5F);
    var layout = new TableLayoutPanel
    {
      Dock = DockStyle.Fill,
      Padding = new Padding(16),
      ColumnCount = 3,
      RowCount = 5,
    };
    layout.ColumnStyles.Add(new ColumnStyle(SizeType.Absolute, 105));
    layout.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 100));
    layout.ColumnStyles.Add(new ColumnStyle(SizeType.Absolute, 85));
    layout.Controls.Add(new Label { Text = "동기화 파일", AutoSize = true, Anchor = AnchorStyles.Left }, 0, 0);
    layout.Controls.Add(_path, 1, 0);
    var browse = new Button { Text = "찾아보기", Dock = DockStyle.Fill };
    WinFormsSurface.StyleDarkButton(browse);
    browse.Click += (_, _) => Browse();
    layout.Controls.Add(browse, 2, 0);
    layout.Controls.Add(new Label { Text = "동기화 암호", AutoSize = true, Anchor = AnchorStyles.Left }, 0, 1);
    layout.Controls.Add(_passphrase, 1, 1);
    layout.SetColumnSpan(_passphrase, 2);
    layout.Controls.Add(_automaticSync, 0, 2);
    layout.SetColumnSpan(_automaticSync, 3);
    var notice = new Label
    {
      Text = "자동 동기화 암호는 현재 Windows 사용자 계정의 DPAPI로만 보호됩니다.\n양쪽이 모두 바뀌면 덮어쓰지 않고 동기화를 멈춥니다.",
      AutoSize = true,
      ForeColor = Color.FromArgb(162, 167, 180),
    };
    layout.Controls.Add(notice, 0, 3);
    layout.SetColumnSpan(notice, 3);
    var buttons = new FlowLayoutPanel { Dock = DockStyle.Fill, FlowDirection = FlowDirection.RightToLeft };
    var cancelBtn = ActionButton("취소", (_, _) => DialogResult = DialogResult.Cancel);
    WinFormsSurface.StyleDarkButton(cancelBtn);
    var importBtn = ActionButton("파일에서 가져오기", (_, _) => Complete(importing: true));
    WinFormsSurface.StyleDarkButton(importBtn);
    var exportBtn = ActionButton("파일로 내보내기", (_, _) => Complete(importing: false));
    WinFormsSurface.StyleDarkButton(exportBtn, primary: true);
    buttons.Controls.Add(cancelBtn);
    buttons.Controls.Add(importBtn);
    buttons.Controls.Add(exportBtn);
    layout.Controls.Add(buttons, 0, 4);
    layout.SetColumnSpan(buttons, 3);
    Controls.Add(layout);
    if (!string.IsNullOrWhiteSpace(configuration.SyncPath)) _path.Text = configuration.SyncPath;
    _passphrase.Text = AutoSyncSettingsStore.UnprotectPassphrase(configuration) ?? string.Empty;
    _automaticSync.Checked = configuration.Enabled || string.IsNullOrWhiteSpace(configuration.SyncPath);
    ResumeLayout(true);
    WinFormsSurface.Attach(this);
  }

  private void Browse()
  {
    using var picker = new SaveFileDialog
    {
      Filter = "Omyaru 동기화 (*.json)|*.json|모든 파일 (*.*)|*.*",
      FileName = "privacy-browser-sync.json",
      OverwritePrompt = false,
    };
    if (picker.ShowDialog(this) == DialogResult.OK) _path.Text = picker.FileName;
  }

  private void Complete(bool importing)
  {
    if (string.IsNullOrWhiteSpace(SyncPath) || Passphrase.Length < 6)
    {
      MessageBox.Show(this, "동기화 파일과 6자 이상의 암호를 입력하세요.");
      return;
    }
    if (importing && !File.Exists(SyncPath))
    {
      MessageBox.Show(this, "가져올 동기화 파일이 없습니다.");
      return;
    }
    Importing = importing;
    DialogResult = DialogResult.OK;
  }

  private static Button ActionButton(string text, EventHandler action)
  {
    var button = new Button { Text = text, AutoSize = true, Height = 34 };
    button.Click += action;
    return button;
  }
}

internal static class InputDialog
{
  internal static string? Show(IWin32Window owner, string title, string prompt, string initialValue = "")
  {
    using var dialog = new Form
    {
      Text = title,
      StartPosition = FormStartPosition.CenterParent,
      FormBorderStyle = FormBorderStyle.FixedDialog,
      MinimizeBox = false,
      MaximizeBox = false,
      ClientSize = new Size(380, 135),
    };
    var label = new Label { Text = prompt, Left = 14, Top = 15, Width = 340 };
    var input = new TextBox { Left = 14, Top = 42, Width = 350, Text = initialValue };
    var okay = new Button { Text = "확인", Left = 208, Top = 82, Width = 75, DialogResult = DialogResult.OK };
    var cancel = new Button { Text = "취소", Left = 289, Top = 82, Width = 75, DialogResult = DialogResult.Cancel };
    dialog.Controls.AddRange([label, input, okay, cancel]);
    dialog.AcceptButton = okay;
    dialog.CancelButton = cancel;
    return dialog.ShowDialog(owner) == DialogResult.OK ? input.Text : null;
  }
}

internal sealed class CloseAllTabsCountdownDialog : Form
{
  private readonly System.Windows.Forms.Timer _timer = new() { Interval = 1000 };
  private readonly Button _confirmButton;
  private readonly Button _cancelButton;
  private readonly Label _messageLabel;
  private int _remainingSeconds = 5;

  internal CloseAllTabsCountdownDialog()
  {
    Text = "모든 탭 닫기 확인";
    StartPosition = FormStartPosition.CenterParent;
    FormBorderStyle = FormBorderStyle.FixedDialog;
    MaximizeBox = false;
    MinimizeBox = false;
    ClientSize = new Size(420, 160);
    BackColor = Color.FromArgb(24, 29, 35);
    ForeColor = Color.FromArgb(242, 244, 248);
    Font = new Font("Segoe UI", 9.5F);

    _messageLabel = new Label
    {
      Text = "현재 그룹의 모든 탭을 닫으시겠습니까?\n이 작업 후에는 새 탭 하나만 남게 됩니다.",
      Left = 24,
      Top = 22,
      Width = 370,
      Height = 48,
    };

    _confirmButton = new Button
    {
      Text = "확인 (5초)",
      Left = 205,
      Top = 95,
      Width = 100,
      Height = 36,
      FlatStyle = FlatStyle.Flat,
      BackColor = Color.FromArgb(60, 68, 80),
      ForeColor = Color.FromArgb(171, 181, 193),
      Enabled = false,
    };
    _confirmButton.FlatAppearance.BorderSize = 0;
    _confirmButton.Click += (_, _) =>
    {
      DialogResult = DialogResult.OK;
      Close();
    };

    _cancelButton = new Button
    {
      Text = "취소",
      Left = 315,
      Top = 95,
      Width = 80,
      Height = 36,
      FlatStyle = FlatStyle.Flat,
      BackColor = Color.FromArgb(53, 62, 73),
      ForeColor = Color.FromArgb(242, 244, 248),
      DialogResult = DialogResult.Cancel,
    };
    _cancelButton.FlatAppearance.BorderSize = 0;

    Controls.AddRange([_messageLabel, _confirmButton, _cancelButton]);
    CancelButton = _cancelButton;

    _timer.Tick += (_, _) =>
    {
      _remainingSeconds--;
      if (_remainingSeconds > 0)
      {
        _confirmButton.Text = $"확인 ({_remainingSeconds}초)";
      }
      else
      {
        _timer.Stop();
        _confirmButton.Text = "확인";
        _confirmButton.Enabled = true;
        _confirmButton.BackColor = Color.FromArgb(215, 60, 60);
        _confirmButton.ForeColor = Color.White;
        AcceptButton = _confirmButton;
      }
    };
    _timer.Start();

    FormClosing += (_, _) => _timer.Dispose();
  }
}

