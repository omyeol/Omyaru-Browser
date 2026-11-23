namespace PrivacyBrowser.Desktop;

internal sealed class GoogleDriveSyncDialog : Form
{
  private readonly CancellationTokenSource _cancellation = new();
  private CancellationTokenSource? _attempt;
  protected override CreateParams CreateParams => WinFormsSurface.WithComposited(base.CreateParams);
  internal static bool CanConnect(bool connected, string password) => true;
  internal GoogleDriveSyncDialog(Func<string, CancellationToken, Task>? authorize = null)
  {
    SuspendLayout();
    Text = "Omyaru Browser · Google Drive 동기화";
    ClientSize = new Size(840, 390);
    MinimumSize = Size;
    StartPosition = FormStartPosition.CenterParent;
    BackColor = Color.FromArgb(24, 29, 35);
    ForeColor = Color.White;
    Font = new Font("Segoe UI", 10);
    var layout = new TableLayoutPanel { Dock = DockStyle.Fill, Padding = new Padding(20), ColumnCount = 1, RowCount = 4 };
    layout.RowStyles.Add(new RowStyle(SizeType.Absolute, 105));
    layout.RowStyles.Add(new RowStyle(SizeType.Absolute, 36));
    layout.RowStyles.Add(new RowStyle(SizeType.Percent, 100));
    layout.RowStyles.Add(new RowStyle(SizeType.Absolute, 52));
    var connected = GoogleDriveAuthorization.Load() is not null;
    var expired = connected && GoogleDriveAuthorization.NeedsReauthentication;
    var notice = new Label { Dock = DockStyle.Fill, Text = expired
        ? "Google 로그인 인증이 만료되었습니다.\n[Google 계정 다시 로그인]을 누르면 기존 탭·북마크·책장 데이터를 유지한 채 바로 동기화가 재개됩니다.\n(Google Cloud 테스트 앱 상태에서는 7일마다 재인증이 필요하며, 프로덕션 게시 시 만료되지 않습니다.)"
        : connected
        ? "Google Drive에 연결되어 있습니다.\n탭·북마크·방문 기록과 책장이 같은 계정을 사용합니다.\n책장 자동 동기화는 책장 설정에서 켤 수 있습니다.\n연결 해제는 이 기기만 중지하며 Drive 기록은 남습니다."
        : "PC·Android·iOS에서 같은 Google 계정으로 로그인하세요.\n탭·북마크·방문 기록과 책장이 같은 계정을 사용합니다.\n암호화 키는 Google 계정의 앱 전용 공간에 보관합니다." };
    var password = new TextBox { Dock = DockStyle.Fill, UseSystemPasswordChar = true, PlaceholderText = "이전 동기화 암호 (이번 한 번만)", Enabled = !connected || expired, Visible = false, BackColor = Color.FromArgb(34, 39, 48), ForeColor = Color.White, BorderStyle = BorderStyle.FixedSingle };
    var status = new Label
    {
      Dock = DockStyle.Fill,
      ForeColor = expired ? Color.FromArgb(255, 192, 110) : Color.FromArgb(215, 224, 236),
      Text = expired ? "Google 로그인 만료됨 · [Google 계정 다시 로그인]을 눌러 주세요." : connected ? "자동 연동 연결됨" : "Google 계정 연결을 눌러 주세요.",
    };
    var buttons = new FlowLayoutPanel { Dock = DockStyle.Fill, FlowDirection = FlowDirection.LeftToRight, WrapContents = true, AutoSize = false };
    var connect = new Button { AutoSize = true, Text = connected ? "Google 연결 해제" : "Google 계정 연결", Height = 36, Padding = new Padding(8, 0, 8, 0) };
    WinFormsSurface.StyleDarkButton(connect, primary: !connected);
    var reconnect = new Button { AutoSize = true, Text = "Google 계정 다시 로그인", Height = 36, Visible = connected, Padding = new Padding(10, 0, 10, 0) };
    WinFormsSurface.StyleDarkButton(reconnect, primary: expired);
    if (expired) AcceptButton = reconnect;
    var legacy = new Button { AutoSize = true, Text = "기존 파일 동기화", Height = 36, Padding = new Padding(8, 0, 8, 0) };
    WinFormsSurface.StyleDarkButton(legacy);
    var close = new Button { AutoSize = true, Text = "닫기", Height = 36, Padding = new Padding(8, 0, 8, 0) };
    WinFormsSurface.StyleDarkButton(close);
    var reset = new Button { AutoSize = true, Text = DriveTabReset.Confirmation, Height = 36, Visible = connected, Padding = new Padding(8, 0, 8, 0) };
    WinFormsSurface.StyleDarkButton(reset);
    if (expired)
      buttons.Controls.AddRange([reconnect, connect, legacy, reset, close]);
    else
      buttons.Controls.AddRange([connect, reconnect, legacy, reset, close]);
    void SetStatus(string text, Color? color = null)
    {
      if (IsDisposed) return;
      if (InvokeRequired)
      {
        BeginInvoke(new Action(() => SetStatus(text, color)));
        return;
      }
      status.Text = text;
      status.ForeColor = color ?? Color.FromArgb(215, 224, 236);
    }
    reset.Click += async (_, _) =>
    {
      using var confirm = new Form { Text = DriveTabReset.Confirmation, ClientSize = new Size(560, 210), StartPosition = FormStartPosition.CenterParent, MinimizeBox = false, MaximizeBox = false, BackColor = Color.FromArgb(24, 29, 35), ForeColor = Color.White };
      WinFormsSurface.Attach(confirm);
      var explanation = new Label { Text = "모든 연결 기기의 동기화 탭을 비웁니다. 꺼진 기기는 다시 연결하면 반영됩니다. 프로필·책장·비공개 탭은 유지됩니다.\n\n‘동기화 탭 초기화’를 정확히 입력하세요.", Dock = DockStyle.Top, Height = 110, Padding = new Padding(12) };
      var input = new TextBox { Dock = DockStyle.Top, BackColor = Color.FromArgb(34, 39, 48), ForeColor = Color.White, BorderStyle = BorderStyle.FixedSingle };
      var execute = new Button { Text = "초기화", AutoSize = true, Enabled = false, DialogResult = DialogResult.OK };
      WinFormsSurface.StyleDarkButton(execute, primary: true);
      var cancel = new Button { Text = "취소", AutoSize = true, DialogResult = DialogResult.Cancel };
      WinFormsSurface.StyleDarkButton(cancel);
      var row = new FlowLayoutPanel { Dock = DockStyle.Bottom, Height = 45 };
      row.Controls.AddRange([execute, cancel]);
      confirm.Controls.AddRange([input, explanation, row]); confirm.CancelButton = cancel;
      input.TextChanged += (_, _) => execute.Enabled = input.Text == DriveTabReset.Confirmation;
      if (confirm.ShowDialog(this) != DialogResult.OK || input.Text != DriveTabReset.Confirmation) return;
      reset.Enabled = connect.Enabled = false;
      try {
        await DesktopDriveSync.ResetTabsAsync(input.Text, _cancellation.Token);
        if (!IsDisposed) status.Text = "초기화 요청이 저장되었습니다. 연결 기기에 순차 반영됩니다.";
      } catch (Exception error) { if (!IsDisposed) status.Text = error.Message; }
      finally { if (!IsDisposed) reset.Enabled = connect.Enabled = true; }
    };
    layout.Controls.Add(notice, 0, 0);
    layout.Controls.Add(password, 0, 1);
    layout.Controls.Add(status, 0, 2);
    layout.Controls.Add(buttons, 0, 3);
    Controls.Add(layout);
    ResumeLayout(true);
    WinFormsSurface.Attach(this);
    connect.Enabled = CanConnect(connected, password.Text);
    password.TextChanged += (_, _) =>
    {
      if (_attempt is not null) return;
      connect.Enabled = CanConnect(connected, password.Text);
      status.Text = connect.Enabled
          ? "연결 준비됨 · 탭 없는 별도의 Edge 로그인 창이 열립니다."
          : "동기화 암호를 6자 이상 입력하면 연결 버튼이 활성화됩니다.";
    };
    reconnect.Click += async (_, _) =>
    {
      if (_attempt is not null)
      {
        _attempt.Cancel();
        reconnect.Enabled = false;
        SetStatus("로그인 취소 중…");
        return;
      }
      using var attempt = CancellationTokenSource.CreateLinkedTokenSource(_cancellation.Token);
      _attempt = attempt;
      connect.Enabled = reset.Enabled = password.Enabled = false;
      reconnect.Text = "로그인 취소";
      SetStatus("별도의 Edge 로그인 창에서 Google 계정을 다시 승인해 주세요.\n기존 탭·북마크·책장 데이터는 그대로 유지됩니다.");
      try
      {
        var previousKeyId = GoogleDriveAuthorization.Load()?.PrimaryKeyId;
        if (authorize is not null) await authorize(password.Text, attempt.Token);
        else await GoogleDriveAuthorization.ConnectAsync(password.Text, attempt.Token, stage => SetStatus(stage));
        var currentKeyId = GoogleDriveAuthorization.Load()?.PrimaryKeyId;
        if (!string.IsNullOrEmpty(previousKeyId) && !string.IsNullOrEmpty(currentKeyId) &&
            !string.Equals(previousKeyId, currentKeyId, StringComparison.Ordinal))
        {
          DesktopDriveSync.ResetConnection();
          DesktopUserDataSync.ResetConnection();
        }
        AutoSyncSettingsStore.Disable();
        if (IsDisposed) return;
        SetStatus("Google 로그인 완료! 동기화를 시작합니다…", Color.FromArgb(124, 224, 152));
        DialogResult = DialogResult.OK;
        Close();
      }
      catch (OperationCanceledException) { SetStatus("로그인을 취소했거나 제한 시간이 지났습니다.", Color.FromArgb(255, 192, 110)); }
      catch (Exception error)
      {
        if (!IsDisposed)
        {
          SetStatus(error.Message, Color.FromArgb(255, 140, 140));
          if (error.Message.Contains("이전 동기화 암호") || error.Message.Contains("기존 동기화 암호 확인"))
            password.Visible = password.Enabled = true;
        }
      }
      finally
      {
        _attempt = null;
        if (!IsDisposed)
        {
          reconnect.Text = "Google 계정 다시 로그인";
          reconnect.Enabled = connect.Enabled = reset.Enabled = true;
        }
      }
    };
    close.Click += (_, _) => Close();
    legacy.Enabled = !connected;
    legacy.Click += (_, _) => { DialogResult = DialogResult.Retry; Close(); };
    connect.Click += async (_, _) =>
    {
      if (_attempt is not null)
      {
        _attempt.Cancel();
        connect.Enabled = false;
        SetStatus("연결 취소 중…");
        return;
      }
      if (!CanConnect(connected, password.Text)) return;
      using var attempt = CancellationTokenSource.CreateLinkedTokenSource(_cancellation.Token);
      _attempt = attempt;
      password.Enabled = legacy.Enabled = false;
      connect.Enabled = !connected;
      connect.Text = connected ? "연결 해제 중…" : "연결 취소";
      SetStatus(connected ? "연결 해제 중…" : "별도의 Edge 로그인 창에서 Google 연결을 승인해 주세요.\n로그인 창을 닫았다면 ‘연결 취소’를 누르면 여기서 다시 시도할 수 있습니다.");
      try
      {
        if (connected) await GoogleDriveAuthorization.DisconnectAsync(attempt.Token);
        else
        {
          if (authorize is not null) await authorize(password.Text, attempt.Token);
          else await GoogleDriveAuthorization.ConnectAsync(password.Text, attempt.Token, stage => SetStatus(stage));
          AutoSyncSettingsStore.Disable();
        }
        DesktopDriveSync.ResetConnection();
        DesktopUserDataSync.ResetConnection();
        if (IsDisposed) return;
        DialogResult = DialogResult.OK;
        Close();
      }
      catch (OperationCanceledException) { SetStatus("연결을 취소했거나 제한 시간이 지났습니다.", Color.FromArgb(255, 192, 110)); }
      catch (Exception error) {
        if (!IsDisposed) {
          SetStatus(error.Message, Color.FromArgb(255, 140, 140));
          if (error.Message.Contains("이전 동기화 암호") || error.Message.Contains("기존 동기화 암호 확인"))
            password.Visible = true;
        }
      }
      finally
      {
        _attempt = null;
        if (!IsDisposed)
        {
          connect.Text = connected ? "Google 연결 해제" : "Google 계정 연결";
          connect.Enabled = CanConnect(connected, password.Text);
          password.Enabled = legacy.Enabled = !connected;
        }
      }
    };
    FormClosing += (_, _) => _cancellation.Cancel();
  }
}
