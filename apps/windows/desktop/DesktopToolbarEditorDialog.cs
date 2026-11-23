using System;
using System.Collections.Generic;
using System.Drawing;
using System.Linq;
using System.Windows.Forms;

namespace PrivacyBrowser.Desktop;

internal sealed class DesktopToolbarItemDefinition
{
  public string Id { get; }
  public string Name { get; }
  public string Description { get; }
  public PillGlyph Glyph { get; }

  public DesktopToolbarItemDefinition(string id, string name, string description, PillGlyph glyph)
  {
    Id = id;
    Name = name;
    Description = description;
    Glyph = glyph;
  }
}

internal sealed class DesktopToolbarRegistry
{
  public static readonly DesktopToolbarItemDefinition[] AllItems = new[]
  {
    new DesktopToolbarItemDefinition("menu", "메뉴", "브라우저 전체 메뉴 열기", PillGlyph.More),
    new DesktopToolbarItemDefinition("sync", "동기화", "Android 및 Google Drive 동기화", PillGlyph.Sync),
    new DesktopToolbarItemDefinition("watermark", "워터마크 삭제", "현재 화면 캡처 후 워터마크 삭제", PillGlyph.Watermark),
    new DesktopToolbarItemDefinition("capture", "화면 캡처", "웹 화면 영역 캡처", PillGlyph.Capture),
    new DesktopToolbarItemDefinition("audio", "소리 출력", "오디오 출력 장치 선택", PillGlyph.Music),
    new DesktopToolbarItemDefinition("newtab", "새 탭", "새 브라우저 탭 열기", PillGlyph.Add),
    new DesktopToolbarItemDefinition("tabs", "탭 목록", "열려 있는 탭 목록 오버뷰", PillGlyph.Tabs),
    new DesktopToolbarItemDefinition("bookmarks", "북마크", "북마크 관리자 열기", PillGlyph.Bookmarks),
    new DesktopToolbarItemDefinition("extensions", "확장 프로그램", "확장 프로그램 설치 및 관리", PillGlyph.Extension),
    new DesktopToolbarItemDefinition("bookshelf", "책장", "책장 뷰어 열기", PillGlyph.Bookshelf),
    new DesktopToolbarItemDefinition("downloads", "다운로드", "다운로드 폴더 열기", PillGlyph.Downloads),
    new DesktopToolbarItemDefinition("print", "인쇄", "웹페이지 인쇄", PillGlyph.Print),
    new DesktopToolbarItemDefinition("settings", "설정", "브라우저 설정 열기", PillGlyph.Settings),
  };

  public static DesktopToolbarItemDefinition? Find(string id) =>
      AllItems.FirstOrDefault(item => string.Equals(item.Id, id, StringComparison.OrdinalIgnoreCase));
}

internal sealed class DesktopToolbarEditorDialog : Form
{
  private static readonly Color Background = Color.FromArgb(28, 28, 30);
  private static readonly Color Card = Color.FromArgb(44, 44, 48);
  private static readonly Color CardBorder = Color.FromArgb(58, 58, 64);
  private static readonly Color TextColor = Color.FromArgb(245, 245, 247);
  private static readonly Color MutedText = Color.FromArgb(152, 152, 159);
  private static readonly Color Accent = Color.FromArgb(10, 132, 255);

  private readonly List<string> _activeItems = new();
  private readonly ListBox _activeListBox = new();
  private readonly ListBox _availableListBox = new();
  private readonly Action<string> _onSaved;

  public DesktopToolbarEditorDialog(string currentButtonIds, Action<string> onSaved)
  {
    _onSaved = onSaved;
    Text = "도구모음 편집 — Omyaru";
    StartPosition = FormStartPosition.CenterParent;
    Size = new Size(580, 520);
    MinimumSize = new Size(500, 450);
    BackColor = Background;
    ForeColor = TextColor;
    Font = new Font("Segoe UI", 9.5F);
    FormBorderStyle = FormBorderStyle.FixedDialog;
    MaximizeBox = false;
    MinimizeBox = false;

    var tokens = currentButtonIds.Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
    foreach (var token in tokens)
    {
      if (DesktopToolbarRegistry.Find(token) != null && !_activeItems.Contains(token))
      {
        _activeItems.Add(token);
      }
    }
    if (_activeItems.Count == 0)
    {
      _activeItems.AddRange(new[] { "sync", "watermark", "capture", "audio", "bookshelf", "menu" });
    }

    BuildInterface();
  }

  private void BuildInterface()
  {
    var root = new TableLayoutPanel
    {
      Dock = DockStyle.Fill,
      Padding = new Padding(20),
      RowCount = 4,
      ColumnCount = 1,
    };
    root.RowStyles.Add(new RowStyle(SizeType.Absolute, 45)); // Header
    root.RowStyles.Add(new RowStyle(SizeType.Percent, 100)); // Main editor lists
    root.RowStyles.Add(new RowStyle(SizeType.Absolute, 15)); // Spacer
    root.RowStyles.Add(new RowStyle(SizeType.Absolute, 46)); // Buttons

    var header = new Label
    {
      Dock = DockStyle.Fill,
      Text = "우측 상단 도구모음 편집\n원하는 도구를 추가하고 순서를 변경할 수 있습니다.",
      ForeColor = TextColor,
      Font = new Font("Segoe UI", 10.5F, FontStyle.Bold),
    };
    root.Controls.Add(header, 0, 0);

    var listsGrid = new TableLayoutPanel
    {
      Dock = DockStyle.Fill,
      ColumnCount = 3,
      RowCount = 1,
    };
    listsGrid.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 46));
    listsGrid.ColumnStyles.Add(new ColumnStyle(SizeType.Absolute, 50));
    listsGrid.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 54));

    // Left: Active items list
    var activePanel = new Panel { Dock = DockStyle.Fill };
    var activeLabel = new Label
    {
      Dock = DockStyle.Top,
      Text = "현재 도구모음 (표시 순서)",
      Height = 24,
      ForeColor = MutedText,
      Font = new Font("Segoe UI", 9F, FontStyle.Bold),
    };
    _activeListBox.Dock = DockStyle.Fill;
    _activeListBox.BackColor = Card;
    _activeListBox.ForeColor = TextColor;
    _activeListBox.BorderStyle = BorderStyle.FixedSingle;
    _activeListBox.Font = new Font("Segoe UI", 10F);
    _activeListBox.ItemHeight = 28;
    activePanel.Controls.Add(_activeListBox);
    activePanel.Controls.Add(activeLabel);
    listsGrid.Controls.Add(activePanel, 0, 0);

    // Center: Order buttons (▲, ▼, Remove)
    var centerActions = new FlowLayoutPanel
    {
      Dock = DockStyle.Fill,
      FlowDirection = FlowDirection.TopDown,
      WrapContents = false,
      Padding = new Padding(4, 28, 4, 0),
    };
    var upBtn = CreateActionButton("▲", "위로 이동", () => MoveActiveItem(-1));
    var downBtn = CreateActionButton("▼", "아래로 이동", () => MoveActiveItem(1));
    var removeBtn = CreateActionButton("✕", "도구모음에서 제거", RemoveSelectedActiveItem);
    centerActions.Controls.Add(upBtn);
    centerActions.Controls.Add(downBtn);
    centerActions.Controls.Add(removeBtn);
    listsGrid.Controls.Add(centerActions, 1, 0);

    // Right: Available items list
    var availablePanel = new Panel { Dock = DockStyle.Fill };
    var availableLabel = new Label
    {
      Dock = DockStyle.Top,
      Text = "추가 가능한 도구",
      Height = 24,
      ForeColor = MutedText,
      Font = new Font("Segoe UI", 9F, FontStyle.Bold),
    };
    _availableListBox.Dock = DockStyle.Fill;
    _availableListBox.BackColor = Card;
    _availableListBox.ForeColor = TextColor;
    _availableListBox.BorderStyle = BorderStyle.FixedSingle;
    _availableListBox.Font = new Font("Segoe UI", 10F);
    _availableListBox.ItemHeight = 28;

    var addBtnPanel = new Panel { Dock = DockStyle.Bottom, Height = 36, Padding = new Padding(0, 4, 0, 0) };
    var addBtn = new Button
    {
      Dock = DockStyle.Fill,
      Text = "＋ 선택한 도구 추가",
      FlatStyle = FlatStyle.Flat,
      BackColor = Color.FromArgb(40, 52, 68),
      ForeColor = Accent,
      Font = new Font("Segoe UI", 9F, FontStyle.Bold),
      Cursor = Cursors.Hand,
    };
    addBtn.FlatAppearance.BorderColor = Accent;
    addBtn.Click += (_, _) => AddSelectedAvailableItem();
    addBtnPanel.Controls.Add(addBtn);

    availablePanel.Controls.Add(_availableListBox);
    availablePanel.Controls.Add(addBtnPanel);
    availablePanel.Controls.Add(availableLabel);
    listsGrid.Controls.Add(availablePanel, 2, 0);

    root.Controls.Add(listsGrid, 0, 1);

    // Bottom action buttons
    var bottomBar = new TableLayoutPanel
    {
      Dock = DockStyle.Fill,
      ColumnCount = 4,
      RowCount = 1,
    };
    bottomBar.ColumnStyles.Add(new ColumnStyle(SizeType.Absolute, 120)); // Reset
    bottomBar.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 100)); // Gap
    bottomBar.ColumnStyles.Add(new ColumnStyle(SizeType.Absolute, 90));  // Cancel
    bottomBar.ColumnStyles.Add(new ColumnStyle(SizeType.Absolute, 90));  // Save

    var resetBtn = new Button
    {
      Dock = DockStyle.Fill,
      Text = "기본값으로 복원",
      FlatStyle = FlatStyle.Flat,
      BackColor = Card,
      ForeColor = MutedText,
      Font = new Font("Segoe UI", 9F),
      Cursor = Cursors.Hand,
    };
    resetBtn.FlatAppearance.BorderColor = CardBorder;
    resetBtn.Click += (_, _) =>
    {
      _activeItems.Clear();
      _activeItems.AddRange(new[] { "sync", "watermark", "capture", "audio", "bookshelf", "menu" });
      RefreshLists();
    };

    var cancelBtn = new Button
    {
      Dock = DockStyle.Fill,
      Text = "취소",
      FlatStyle = FlatStyle.Flat,
      BackColor = Card,
      ForeColor = TextColor,
      Font = new Font("Segoe UI", 9.5F),
      Cursor = Cursors.Hand,
    };
    cancelBtn.FlatAppearance.BorderColor = CardBorder;
    cancelBtn.Click += (_, _) => Close();

    var saveBtn = new Button
    {
      Dock = DockStyle.Fill,
      Text = "저장",
      FlatStyle = FlatStyle.Flat,
      BackColor = Accent,
      ForeColor = Color.White,
      Font = new Font("Segoe UI", 9.5F, FontStyle.Bold),
      Cursor = Cursors.Hand,
    };
    saveBtn.FlatAppearance.BorderSize = 0;
    saveBtn.Click += (_, _) =>
    {
      var result = string.Join(",", _activeItems);
      _onSaved(result);
      Close();
    };

    bottomBar.Controls.Add(resetBtn, 0, 0);
    bottomBar.Controls.Add(cancelBtn, 2, 0);
    bottomBar.Controls.Add(saveBtn, 3, 0);

    root.Controls.Add(bottomBar, 0, 3);
    Controls.Add(root);

    RefreshLists();
  }

  private Button CreateActionButton(string text, string tooltip, Action onClick)
  {
    var btn = new Button
    {
      Text = text,
      Width = 40,
      Height = 36,
      FlatStyle = FlatStyle.Flat,
      BackColor = Card,
      ForeColor = TextColor,
      Font = new Font("Segoe UI", 11F, FontStyle.Bold),
      Cursor = Cursors.Hand,
      Margin = new Padding(0, 0, 0, 8),
    };
    btn.FlatAppearance.BorderColor = CardBorder;
    var tip = new ToolTip();
    tip.SetToolTip(btn, tooltip);
    btn.Click += (_, _) => onClick();
    return btn;
  }

  private void RefreshLists()
  {
    var selActive = _activeListBox.SelectedIndex;
    var selAvail = _availableListBox.SelectedIndex;

    _activeListBox.Items.Clear();
    foreach (var id in _activeItems)
    {
      var def = DesktopToolbarRegistry.Find(id);
      _activeListBox.Items.Add(def != null ? $"{def.Name} ({def.Description})" : id);
    }
    if (selActive >= 0 && selActive < _activeListBox.Items.Count)
      _activeListBox.SelectedIndex = selActive;
    else if (_activeListBox.Items.Count > 0)
      _activeListBox.SelectedIndex = 0;

    _availableListBox.Items.Clear();
    foreach (var def in DesktopToolbarRegistry.AllItems)
    {
      if (!_activeItems.Contains(def.Id))
      {
        _availableListBox.Items.Add($"{def.Name} ({def.Description})");
      }
    }
    if (selAvail >= 0 && selAvail < _availableListBox.Items.Count)
      _availableListBox.SelectedIndex = selAvail;
    else if (_availableListBox.Items.Count > 0)
      _availableListBox.SelectedIndex = 0;
  }

  private void MoveActiveItem(int direction)
  {
    var idx = _activeListBox.SelectedIndex;
    if (idx < 0) return;
    var target = idx + direction;
    if (target < 0 || target >= _activeItems.Count) return;

    var item = _activeItems[idx];
    _activeItems.RemoveAt(idx);
    _activeItems.Insert(target, item);
    RefreshLists();
    _activeListBox.SelectedIndex = target;
  }

  private void RemoveSelectedActiveItem()
  {
    var idx = _activeListBox.SelectedIndex;
    if (idx < 0) return;
    if (_activeItems.Count <= 1)
    {
      MessageBox.Show("최소 한 개의 도구는 도구모음에 유지되어야 합니다.", "도구모음 편집", MessageBoxButtons.OK, MessageBoxIcon.Information);
      return;
    }
    _activeItems.RemoveAt(idx);
    RefreshLists();
  }

  private void AddSelectedAvailableItem()
  {
    var idx = _availableListBox.SelectedIndex;
    if (idx < 0) return;

    var availableDefs = DesktopToolbarRegistry.AllItems.Where(d => !_activeItems.Contains(d.Id)).ToList();
    if (idx < availableDefs.Count)
    {
      _activeItems.Add(availableDefs[idx].Id);
      RefreshLists();
      _activeListBox.SelectedIndex = _activeItems.Count - 1;
    }
  }
}
