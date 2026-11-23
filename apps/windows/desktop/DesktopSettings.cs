using System.Text.Json;

namespace PrivacyBrowser.Desktop;

internal sealed class DesktopPreferences
{
  public bool RestoreTabs { get; set; } = true;
  public string SearchEngine { get; set; } = "DuckDuckGo";
  public bool ShowFavoritesBar { get; set; } = true;
  public bool BookmarkIconsOnly { get; set; }
  public bool ShowStatusBar { get; set; } = true;
  public bool CompactToolbar { get; set; }
  public bool CaptureSideMargins { get; set; } = true;
  public string ToolbarShortcuts { get; set; } = string.Empty;
  public bool OpenLinksInNewTab { get; set; } = true;
  public bool ShowTabThumbnails { get; set; } = true;
  public bool AutomaticHttps { get; set; } = true;
  public bool ShowFullUrls { get; set; } = true;
  public bool BlockTrackers { get; set; } = true;
  public bool CollectDiagnostics { get; set; }
  public string AudioOutputDeviceId { get; set; } = string.Empty;
  public string AudioOutputLabel { get; set; } = string.Empty;
  public string LastCaptureDirectory { get; set; } = string.Empty;
  public string DesktopToolbarButtons { get; set; } = "sync,watermark,capture,audio,bookshelf,menu";

  internal DesktopPreferences Copy() => (DesktopPreferences)MemberwiseClone();
}

internal static class DesktopPreferencesStore
{
  private static readonly JsonSerializerOptions JsonOptions = new()
  {
    PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
    WriteIndented = true,
  };

  private static string SettingsPath => Path.Combine(BrowserStateStore.StateDirectory, "desktop-preferences.json");

  internal static DesktopPreferences Load()
  {
    try
    {
      if (!File.Exists(SettingsPath)) return new DesktopPreferences();
      return JsonSerializer.Deserialize<DesktopPreferences>(File.ReadAllText(SettingsPath), JsonOptions)
          ?? new DesktopPreferences();
    }
    catch
    {
      return new DesktopPreferences();
    }
  }

  internal static void Save(DesktopPreferences preferences)
  {
    Directory.CreateDirectory(BrowserStateStore.StateDirectory);
    var temporaryPath = SettingsPath + ".new";
    File.WriteAllText(temporaryPath, JsonSerializer.Serialize(preferences, JsonOptions));
    File.Move(temporaryPath, SettingsPath, true);
  }
}

internal sealed class SettingsForm : Form
{
  private static readonly Color Background = Color.FromArgb(18, 18, 20);
  private static readonly Color Card = Color.FromArgb(28, 28, 30);
  private static readonly Color CardHover = Color.FromArgb(44, 44, 48);
  private static readonly Color Outline = Color.FromArgb(58, 58, 64);
  private static readonly Color TextColor = Color.FromArgb(245, 245, 247);
  private static readonly Color Muted = Color.FromArgb(152, 152, 159);
  private static readonly Color Accent = Color.FromArgb(10, 132, 255);

  private readonly DesktopPreferences _working;
  private readonly Action<DesktopPreferences> _apply;
  private readonly Func<Task> _openSync;
  private readonly Func<DesktopPreferences, Task> _chooseAudioOutput;
  private readonly FlowLayoutPanel _content = new();

  protected override CreateParams CreateParams => WinFormsSurface.WithComposited(base.CreateParams);

  internal SettingsForm(
      DesktopPreferences preferences,
      Action<DesktopPreferences> apply,
      Func<Task> openSync,
      Func<DesktopPreferences, Task> chooseAudioOutput)
  {
    _working = preferences.Copy();
    _apply = apply;
    _openSync = openSync;
    _chooseAudioOutput = chooseAudioOutput;
    SuspendLayout();
    Text = "설정 — Omyaru";
    StartPosition = FormStartPosition.CenterParent;
    MinimumSize = new Size(700, 650);
    Size = new Size(820, 780);
    BackColor = Background;
    ForeColor = TextColor;
    Font = new Font("Segoe UI", 10F);
    BuildInterface();
    ResumeLayout(true);
    WinFormsSurface.Attach(this);
  }

  private void BuildInterface()
  {
    var root = new TableLayoutPanel
    {
      Dock = DockStyle.Fill,
      BackColor = Background,
      ColumnCount = 1,
      RowCount = 2,
      Margin = Padding.Empty,
      Padding = Padding.Empty,
    };
    root.RowStyles.Add(new RowStyle(SizeType.Absolute, 78));
    root.RowStyles.Add(new RowStyle(SizeType.Percent, 100));
    Controls.Add(root);

    var header = new Panel { Dock = DockStyle.Fill, BackColor = Background, Padding = new Padding(28, 16, 28, 10) };
    header.Controls.Add(new Label
    {
      Dock = DockStyle.Left,
      Width = 260,
      Text = "설정",
      ForeColor = TextColor,
      Font = new Font("Segoe UI", 22F, FontStyle.Bold),
      TextAlign = ContentAlignment.MiddleLeft,
    });
    var done = new PillButton
    {
      Dock = DockStyle.Right,
      Width = 92,
      Text = "완료",
      BackColor = Accent,
      ForeColor = Color.White,
      CornerRadius = 18,
      Font = new Font("Segoe UI", 10.5F, FontStyle.Bold),
      AccessibleName = "설정 저장",
    };
    done.Click += (_, _) => SaveAndClose();
    header.Controls.Add(done);
    root.Controls.Add(header, 0, 0);

    _content.Dock = DockStyle.Fill;
    _content.AutoScroll = true;
    _content.FlowDirection = FlowDirection.TopDown;
    _content.WrapContents = false;
    _content.BackColor = Background;
    _content.Padding = new Padding(28, 8, 28, 30);
    root.Controls.Add(_content, 0, 1);

    AddSection("일반",
        ChoiceRow("◉", "시작 동작", "마지막 일반 탭 복원", () => _working.RestoreTabs ? "복원" : "새 탭", () =>
        {
          _working.RestoreTabs = !_working.RestoreTabs;
          return _working.RestoreTabs ? "복원" : "새 탭";
        }),
        ToggleRow("⌁", "마지막 탭 복원", "브라우저를 다시 열어도 탭과 미리보기를 유지합니다.",
            () => _working.RestoreTabs, value => _working.RestoreTabs = value));

    AddSection("검색",
        ChoiceRow("⌕", "검색 엔진", "주소창에서 검색할 엔진", () => _working.SearchEngine, CycleSearchEngine));

    AddSection("모양",
        ActionRow("☷", "주소창 아래 메뉴 버튼 편집", "자주 쓰는 메뉴를 바로가기 버튼으로 추가합니다.", () =>
        {
          using var picker = new Form { Text = "도구 모음 버튼", Size = new Size(380, 450), StartPosition = FormStartPosition.CenterParent, BackColor = Background, ForeColor = TextColor };
          var choices = new CheckedListBox { Dock = DockStyle.Fill, CheckOnClick = true, BackColor = Card, ForeColor = TextColor, BorderStyle = BorderStyle.None };
          var selected = _working.ToolbarShortcuts.Split(',').ToHashSet();
          foreach (var label in DesktopToolbarShortcuts.Labels) choices.Items.Add(label, selected.Contains(label));
          var accept = new Button { Text = "저장", Dock = DockStyle.Bottom, Height = 44, DialogResult = DialogResult.OK };
          picker.Controls.Add(choices); picker.Controls.Add(accept); picker.AcceptButton = accept;
          if (picker.ShowDialog(this) == DialogResult.OK)
            _working.ToolbarShortcuts = string.Join(",", choices.CheckedItems.Cast<string>());
          return Task.CompletedTask;
        }),
        ToggleRow("▧", "캡처 화면 양옆 여백", "영역 선택 중 페이지 좌우에 여백을 표시합니다.",
            () => _working.CaptureSideMargins, value => _working.CaptureSideMargins = value),
        ToggleRow("★", "북마크 막대", "주소창 아래에 빠른 북마크 막대를 표시합니다.",
            () => _working.ShowFavoritesBar, value => _working.ShowFavoritesBar = value),
        ToggleRow("◉", "북마크 아이콘만 표시", "북마크 막대에서 이름을 숨기고 사이트 아이콘만 표시합니다.",
            () => _working.BookmarkIconsOnly, value => _working.BookmarkIconsOnly = value),
        ToggleRow("▱", "상태 막대", "프로필과 동기화 상태를 창 아래에 표시합니다.",
            () => _working.ShowStatusBar, value => _working.ShowStatusBar = value),
        ToggleRow("↕", "컴팩트 도구 모음", "탐색 영역의 세로 공간을 줄입니다.",
            () => _working.CompactToolbar, value => _working.CompactToolbar = value));

    AddSection("탭",
        ToggleRow("▣", "링크를 새 탭에서 열기", "새 창 요청을 현재 프로필의 새 탭으로 엽니다.",
            () => _working.OpenLinksInNewTab, value => _working.OpenLinksInNewTab = value),
        ToggleRow("▤", "탭 미리보기", "탭 목록과 동기화 파일에 썸네일을 유지합니다.",
            () => _working.ShowTabThumbnails, value => _working.ShowTabThumbnails = value));

    AddSection("브라우징",
        ToggleRow("⚿", "자동 HTTPS 업그레이드", "주소 입력 시 가능한 경우 HTTPS를 먼저 사용합니다.",
            () => _working.AutomaticHttps, value => _working.AutomaticHttps = value),
        ToggleRow("↗", "전체 URL 표시", "주소창에 도메인 대신 전체 주소를 표시합니다.",
            () => _working.ShowFullUrls, value => _working.ShowFullUrls = value));

    AddSection("소리",
        ActionRow("♫", "출력 장치",
            string.IsNullOrWhiteSpace(_working.AudioOutputLabel) ? "시스템 기본 장치" : _working.AudioOutputLabel,
            async () => await _chooseAudioOutput(_working)));

    AddSection("동기화",
        ActionRow("⇄", "Android/Windows 동기화", "프로필·그룹·탭·순서·미리보기를 암호화해 동기화합니다.", async () =>
        {
          SavePreferences();
          await _openSync();
        }));

    AddSection("개인정보 보호",
        ToggleRow("◈", "추적 방지", "분석·광고 추적 요청을 차단하는 보호 계층입니다.",
            () => _working.BlockTrackers, value => _working.BlockTrackers = value),
        ToggleRow("∅", "진단 데이터 수집", "끄면 앱 사용 통계나 진단 데이터를 전송하지 않습니다.",
            () => _working.CollectDiagnostics, value => _working.CollectDiagnostics = value));

    AddSection("도움말 및 정보",
        InfoRow("ⓘ", "Omyaru", "Windows 0.5.6 · macOS Safari 스타일 GPU 데스크톱"));

    void ResizeCards()
    {
      _content.SuspendLayout();
      var width = Math.Max(560, _content.ClientSize.Width - _content.Padding.Horizontal - 18);
      foreach (Control control in _content.Controls) control.Width = width;
      _content.ResumeLayout(true);
    }
    _content.SizeChanged += (_, _) => ResizeCards();
    Load += (_, _) => ResizeCards();
  }

  private void AddSection(string title, params Control[] rows)
  {
    _content.Controls.Add(new Label
    {
      Height = 40,
      Text = title,
      ForeColor = Muted,
      Font = new Font("Segoe UI", 11F, FontStyle.Bold),
      TextAlign = ContentAlignment.BottomLeft,
      Padding = new Padding(10, 0, 0, 6),
      Margin = new Padding(0, 10, 0, 0),
    });
    var card = new RoundedPanel
    {
      Height = rows.Sum(row => row.Height) + 16,
      BackColor = Card,
      BorderColor = Outline,
      BorderWidth = 1F,
      CornerRadius = 20,
      Padding = new Padding(8),
      Margin = new Padding(0, 0, 0, 8),
    };
    var stack = new FlowLayoutPanel
    {
      Dock = DockStyle.Fill,
      FlowDirection = FlowDirection.TopDown,
      WrapContents = false,
      BackColor = Card,
      Margin = Padding.Empty,
      Padding = Padding.Empty,
    };
    foreach (var row in rows) stack.Controls.Add(row);
    stack.SizeChanged += (_, _) =>
    {
      foreach (Control row in stack.Controls) row.Width = Math.Max(100, stack.ClientSize.Width);
    };
    card.Controls.Add(stack);
    _content.Controls.Add(card);
  }

  private static Control ToggleRow(
      string glyph,
      string title,
      string subtitle,
      Func<bool> read,
      Action<bool> write)
  {
    var row = BaseRow(glyph, title, subtitle, 76);
    var toggle = new SettingsToggleSwitch
    {
      Width = 52,
      Height = 30,
      Checked = read(),
      Anchor = AnchorStyles.Right,
      Margin = new Padding(10, 20, 14, 0),
    };
    toggle.CheckedChanged += (_, _) => write(toggle.Checked);
    row.Controls.Add(toggle, 2, 0);
    return row;
  }

  private static Control ChoiceRow(
      string glyph,
      string title,
      string subtitle,
      Func<string> read,
      Func<string> advance)
  {
    var row = BaseRow(glyph, title, subtitle, 72);
    var choice = new PillButton
    {
      Width = 140,
      Height = 38,
      Text = read(),
      BackColor = CardHover,
      ForeColor = TextColor,
      CornerRadius = 18,
      BorderColor = Outline,
      BorderWidth = 1F,
      Anchor = AnchorStyles.Right,
      Margin = new Padding(10, 16, 12, 0),
    };
    choice.Click += (_, _) => choice.Text = advance();
    row.Controls.Add(choice, 2, 0);
    return row;
  }

  private static Control ActionRow(string glyph, string title, string subtitle, Func<Task> action)
  {
    var row = BaseRow(glyph, title, subtitle, 76);
    var button = new PillButton
    {
      Width = 48,
      Height = 38,
      Text = "›",
      BackColor = CardHover,
      ForeColor = TextColor,
      CornerRadius = 18,
      Font = new Font("Segoe UI", 17F),
      Anchor = AnchorStyles.Right,
      Margin = new Padding(10, 18, 12, 0),
    };
    button.Click += async (_, _) => await action();
    row.Controls.Add(button, 2, 0);
    return row;
  }

  private static Control InfoRow(string glyph, string title, string subtitle) => BaseRow(glyph, title, subtitle, 72);

  private static TableLayoutPanel BaseRow(string glyph, string title, string subtitle, int height)
  {
    var row = new TableLayoutPanel
    {
      Height = height,
      ColumnCount = 3,
      RowCount = 1,
      BackColor = Card,
      Margin = Padding.Empty,
      Padding = Padding.Empty,
    };
    row.ColumnStyles.Add(new ColumnStyle(SizeType.Absolute, 56));
    row.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 100));
    row.ColumnStyles.Add(new ColumnStyle(SizeType.Absolute, 172));
    row.Controls.Add(new Label
    {
      Dock = DockStyle.Fill,
      Text = glyph,
      ForeColor = Accent,
      Font = new Font("Segoe UI Symbol", 16F),
      TextAlign = ContentAlignment.MiddleCenter,
    }, 0, 0);
    row.Controls.Add(new Label
    {
      Dock = DockStyle.Fill,
      Text = title + "\n" + subtitle,
      ForeColor = TextColor,
      Font = new Font("Segoe UI", 10.5F, FontStyle.Bold),
      Padding = new Padding(4, 14, 8, 0),
    }, 1, 0);
    return row;
  }

  private string CycleSearchEngine()
  {
    string[] engines = ["DuckDuckGo", "Google", "Bing"];
    var index = Array.IndexOf(engines, _working.SearchEngine);
    _working.SearchEngine = engines[(index + 1 + engines.Length) % engines.Length];
    return _working.SearchEngine;
  }

  private void SavePreferences()
  {
    DesktopPreferencesStore.Save(_working);
    _apply(_working.Copy());
  }

  private void SaveAndClose()
  {
    SavePreferences();
    DialogResult = DialogResult.OK;
  }
}

internal static class DesktopToolbarShortcuts
{
  internal static readonly string[] Labels = ["새 탭", "탭 목록", "동기화", "다운로드", "북마크", "화면 캡처", "책장", "인쇄", "현재 링크 복사", "설정"];
}

internal sealed class SettingsToggleSwitch : Control
{
  private bool _checked;
  internal event EventHandler? CheckedChanged;

  internal bool Checked
  {
    get => _checked;
    set
    {
      if (_checked == value) return;
      _checked = value;
      Invalidate();
      CheckedChanged?.Invoke(this, EventArgs.Empty);
    }
  }

  internal SettingsToggleSwitch()
  {
    SetStyle(ControlStyles.UserPaint | ControlStyles.AllPaintingInWmPaint |
        ControlStyles.OptimizedDoubleBuffer | ControlStyles.ResizeRedraw, true);
    Cursor = Cursors.Hand;
    AccessibleRole = AccessibleRole.CheckButton;
  }

  protected override void OnClick(EventArgs eventArgs)
  {
    Checked = !Checked;
    base.OnClick(eventArgs);
  }

  protected override void OnPaint(PaintEventArgs eventArgs)
  {
    var graphics = eventArgs.Graphics;
    graphics.SmoothingMode = System.Drawing.Drawing2D.SmoothingMode.AntiAlias;
    var track = Rectangle.Inflate(ClientRectangle, -1, -3);
    using var trackPath = UiGeometry.RoundedRectangle(track, track.Height / 2);
    using var trackBrush = new SolidBrush(Checked ? Color.FromArgb(10, 132, 255) : Color.FromArgb(72, 72, 78));
    graphics.FillPath(trackBrush, trackPath);
    var diameter = track.Height - 6;
    var left = Checked ? track.Right - diameter - 3 : track.Left + 3;
    using var thumbBrush = new SolidBrush(Color.White);
    graphics.FillEllipse(thumbBrush, left, track.Top + 3, diameter, diameter);
  }
}
