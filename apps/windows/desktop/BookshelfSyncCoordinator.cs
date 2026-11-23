using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;

namespace PrivacyBrowser.Desktop;

internal sealed record BookshelfTransferProgress(string Phase, int Done, int Total)
{
  internal int Percent => Total == 0 ? 100 : Math.Clamp((int)Math.Round(Done * 100.0 / Total), 0, 100);
}

internal sealed class BookshelfSyncCoordinator : IDisposable
{
  private readonly CancellationTokenSource _stop = new();
  private bool _busy;
  private BookshelfTransferProgress? _progress;
  internal BookshelfTransferProgress? Progress => Volatile.Read(ref _progress);
  private static readonly SemaphoreSlim TransferGate = new(1, 1);
  private static readonly HttpClient Http = new(new HttpClientHandler { AllowAutoRedirect = false }) { Timeout = TimeSpan.FromSeconds(40) };
  private static string SettingsPath => Path.Combine(BrowserStateStore.StateDirectory, "bookshelf-sync.json");
  private static string UnifiedAccountMarker => Path.Combine(BrowserStateStore.StateDirectory, "bookshelf-unified-account-v1.marker");
  internal static void EnableForConnectedAccountOnce()
  {
    if (GoogleDriveAuthorization.Load()?.AccountKeys != true || File.Exists(UnifiedAccountMarker)) return;
    Enabled = true;
    File.WriteAllText(UnifiedAccountMarker, "1");
  }
  private static JsonObject ReadSettings()
  {
    try { return JsonNode.Parse(File.ReadAllText(SettingsPath))?.AsObject() ?? new JsonObject(); }
    catch { return new JsonObject(); }
  }
  private static void SaveSetting(string name, JsonNode value)
  {
    var settings = ReadSettings(); settings[name] = value;
    Directory.CreateDirectory(BrowserStateStore.StateDirectory);
    File.WriteAllText(SettingsPath + ".new", settings.ToJsonString()); File.Move(SettingsPath + ".new", SettingsPath, true);
  }
  internal static bool Enabled {
    get => ReadSettings()["enabled"]?.GetValue<bool>() == true;
    set => SaveSetting("enabled", JsonValue.Create(value)!);
  }
  internal static bool OnDemand {
    get => ReadSettings()["imageMode"]?.GetValue<string>() == "on_demand";
    set => SaveSetting("imageMode", JsonValue.Create(value ? "on_demand" : "local")!);
  }
  internal string Status { get; private set; } = "책장 동기화 꺼짐";
  internal static string Fingerprint(BookshelfCatalog catalog) => JsonSerializer.Serialize(catalog, BookshelfSyncReplica.Json);
  internal static JsonObject Snapshot(BookshelfCatalog catalog)
  {
    var folders = new JsonArray(); var images = new JsonArray();
    for (var folderIndex = 0; folderIndex < catalog.Folders.Count; folderIndex++)
    {
      var folder = catalog.Folders[folderIndex];
      folders.Add(new JsonObject { ["id"] = catalog.FolderIds[folder], ["name"] = folder,
        ["note"] = catalog.FolderNotes.GetValueOrDefault(folder, ""), ["columns"] = BookshelfStore.GetColumnCount(catalog, folder), ["order"] = folderIndex });
    }
    for (var index = 0; index < catalog.Items.Count; index++)
    {
      var image = catalog.Items[index];
      images.Add(new JsonObject { ["id"] = image.Id, ["folderId"] = catalog.FolderIds[image.Folder], ["manifest"] = new JsonObject(),
        ["sourceUrl"] = image.SourceUrl, ["width"] = image.Width, ["height"] = image.Height, ["createdAt"] = image.CreatedAtEpochMs, ["order"] = index });
    }
    return new JsonObject { ["folders"] = folders, ["images"] = images };
  }
  internal async Task<bool> TickAsync(BookshelfCatalog catalog, Action? commitPendingEdits = null)
  {
    if (_busy || !Enabled || _stop.IsCancellationRequested) return false;
    if (BookshelfStore.ConfiguredCatalogUnavailable)
    {
      Status = "선택한 책장 저장 위치에 접근할 수 없습니다. 저장 매체를 다시 연결해 주세요.";
      return false;
    }
    var missingOriginals = BookshelfStore.MissingOriginalCount(catalog);
    if (missingOriginals > 0)
    {
      Status = $"원본 사진 {missingOriginals}개를 찾지 못해 동기화를 보류했습니다. 목록은 유지됩니다.";
      return false;
    }
    var session = GoogleDriveAuthorization.Load();
    if (session?.AccountKeys != true) { Status = "Google 계정 연결 필요"; return false; }
    if (!TransferGate.Wait(0)) return false;
    _busy = true;
    try
    {
      // A corrupt on-disk catalog must never become a cloud-wide empty snapshot.
      var path = Path.Combine(BookshelfStore.StorageDirectory, "catalog.json");
      if (File.Exists(path)) _ = JsonSerializer.Deserialize<BookshelfCatalog>(File.ReadAllText(path), BookshelfSyncReplica.Json)
          ?? throw new InvalidDataException("책장 목록을 읽지 못했습니다.");
      var scope = Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(session.RefreshToken))).ToLowerInvariant();
      var directory = Path.Combine(BookshelfStore.StorageDirectory, ".sync", scope);
      var replicaPath = Path.Combine(directory, "replica.json");
      var restoreStaleEmptyCatalog = catalog.Items.Count == 0 && File.Exists(replicaPath) &&
          !BookshelfStore.ExplicitEmptyCatalog;
      BookshelfStore.Save(catalog); // Persist identity migration before network IO.
      var before = Fingerprint(catalog);
      var snapshot = Snapshot(catalog);
      var sources = catalog.Items.Select(i => new DriveBookshelfSync.ImageSource(i.Id, i.FilePath)).ToList();
      var client = new DriveAppDataClient(Http, async token => {
        if (GoogleDriveAuthorization.Load()?.RefreshToken != session.RefreshToken) throw new OperationCanceledException();
        var accessToken = await GoogleDriveAuthorization.AccessTokenAsync(token);
        if (GoogleDriveAuthorization.Load()?.RefreshToken != session.RefreshToken) throw new OperationCanceledException();
        return accessToken;
      }) { AccountMode = true };
      Status = "책장 동기화 준비 중…";
      Volatile.Write(ref _progress, new BookshelfTransferProgress("사진 확인", 0, sources.Count));
      var result = await Task.Run(async () => {
        var keys = await DriveAccountKeys.LoadAsync(client, _stop.Token);
        return await DriveBookshelfSync.RunAsync(directory, snapshot, sources, client, keys, _stop.Token,
            (done, total) =>
            {
              Volatile.Write(ref _progress, new BookshelfTransferProgress("다운로드", done, total));
              Status = $"책장 다운로드 {done}/{total}";
            }, restoreStaleEmptyCatalog,
            (done, total) =>
            {
              var progress = new BookshelfTransferProgress("업로드", done, total);
              Volatile.Write(ref _progress, progress);
              Status = $"책장 업로드 {progress.Percent}% ({done}/{total})";
            }, (done, total) =>
            {
              var progress = new BookshelfTransferProgress("목록 받기", done, total);
              Volatile.Write(ref _progress, progress);
              Status = $"책장 목록 받기 {progress.Percent}% ({done}/{total})";
            }, (done, total) =>
            {
              var progress = new BookshelfTransferProgress("사진 확인", done, total);
              Volatile.Write(ref _progress, progress);
              Status = $"책장 사진 확인 {progress.Percent}% ({done}/{total})";
            }, OnDemand);
      }, _stop.Token);
      if (!_stop.IsCancellationRequested) commitPendingEdits?.Invoke();
      if (_stop.IsCancellationRequested || !Enabled || GoogleDriveAuthorization.Load()?.RefreshToken != session.RefreshToken || before != Fingerprint(catalog))
      { Status = "새 변경사항을 다음 동기화에 반영합니다."; return false; }
      var activeId = catalog.FolderIds.GetValueOrDefault(catalog.ActiveFolder);
      var replacement = new BookshelfCatalog { Folders = [], FolderIds = [], FolderNotes = [], FolderColumnCounts = [], Items = [] };
      var names = new Dictionary<string, string>();
      foreach (var node in result.Snapshot["folders"]!.AsArray().OrderBy(node => node?["order"]?.GetValue<int>() ?? int.MaxValue).ThenBy(node => node?["id"]?.GetValue<string>(), StringComparer.Ordinal))
      {
        var id = node!["id"]!.GetValue<string>(); var originalName = node["name"]!.GetValue<string>(); var name = originalName;
        if (replacement.Folders.Contains(name, StringComparer.OrdinalIgnoreCase)) name = name[..Math.Min(name.Length, 501)] + " (" + id[..8] + ")";
        replacement.Folders.Add(name); replacement.FolderIds[name] = id; names[id] = name;
        replacement.FolderNotes[name] = node["note"]!.GetValue<string>(); replacement.FolderColumnCounts[name] = node["columns"]!.GetValue<int>();
      }
      foreach (var node in result.Snapshot["images"]!.AsArray().OrderBy(n => n!["order"]!.GetValue<int>()).ThenBy(n => n!["id"]!.GetValue<string>(), StringComparer.Ordinal))
      {
        var id = node!["id"]!.GetValue<string>();
        if (!names.TryGetValue(node["folderId"]!.GetValue<string>(), out var folder)) continue;
        replacement.Items.Add(new(id, folder, result.Images[id], node["sourceUrl"]!.GetValue<string>(), node["width"]!.GetValue<int>(), node["height"]!.GetValue<int>(), node["createdAt"]!.GetValue<long>()));
      }
      replacement.ActiveFolder = activeId is not null && names.TryGetValue(activeId, out var active) ? active : replacement.Folders.FirstOrDefault() ?? BookshelfStore.DefaultFolder;
      var changed = Fingerprint(replacement) != before;
      if (changed)
      {
        // Keep one local recovery catalog; remote deletions do not delete originals.
        if (File.Exists(path)) File.Copy(path, path + ".before-sync", true);
        BookshelfStore.Save(replacement);
        catalog.Folders = replacement.Folders; catalog.FolderIds = replacement.FolderIds; catalog.FolderNotes = replacement.FolderNotes;
        catalog.FolderColumnCounts = replacement.FolderColumnCounts; catalog.Items = replacement.Items; catalog.ActiveFolder = replacement.ActiveFolder;
      }
      DriveBookshelfSync.Acknowledge(directory, result.Snapshot);
      // Keep local recovery originals until the cloud-reference viewer has been
      // confirmed on the user's live catalog across both Windows entry points.
      BookshelfStore.ClearExplicitEmptyMarker();
      Status = "책장 동기화 완료";
      Volatile.Write(ref _progress, new BookshelfTransferProgress("완료", result.Images.Count, result.Images.Count));
      return changed;
    }
    catch (OperationCanceledException) { Status = "책장 동기화 중지됨"; Volatile.Write(ref _progress, null); return false; }
    catch (GoogleDriveReauthenticationRequiredException) { Status = "Google 로그인 만료됨 · 동기화 버튼에서 다시 로그인하세요."; Volatile.Write(ref _progress, null); return false; }
    catch { Status = "책장 동기화 실패 · 연결 또는 원본 파일을 확인하세요."; Volatile.Write(ref _progress, null); return false; }
    finally { _busy = false; TransferGate.Release(); }
  }
  internal static void ShowSettings(IWin32Window owner)
  {
    using var dialog = new Form
    {
      Text = "책장 Google Drive 동기화",
      ClientSize = new Size(560, 390),
      StartPosition = FormStartPosition.CenterParent,
      BackColor = Color.FromArgb(24, 26, 32),
      ForeColor = Color.FromArgb(245, 245, 247),
      Font = new Font("Segoe UI", 9.5F),
    };
    WinFormsSurface.Attach(dialog);
    using var stop = new CancellationTokenSource();
    var help = new Label { Dock = DockStyle.Top, Height = 100, Padding = new Padding(14), Text = "브라우저의 Google 계정과 같은 계정으로 책장·원본 사진을 연동합니다.\n이름 변경과 삭제도 다른 연결 기기에 반영됩니다.\n사진은 암호화하여 계정의 앱 전용 공간에 저장합니다.\n책장 창이 열려 있는 동안 약 15초마다 확인합니다." };
    var enabled = new CheckBox { Dock = DockStyle.Top, Height = 35, Padding = new Padding(14, 0, 14, 0), Text = "책장 자동 동기화 사용", Checked = Enabled };
    var mode = new CheckBox { Dock = DockStyle.Top, Height = 45, Padding = new Padding(14, 0, 14, 0), Text = "사진을 열 때 클라우드에서 불러오기 (선택 해제: 로컬에 저장)", Checked = OnDemand };
    var modeHelp = new Label { Dock = DockStyle.Top, Height = 48, Padding = new Padding(14, 3, 14, 0), ForeColor = Color.FromArgb(162, 167, 180), Text = "두 방식 모두 동기화됩니다. 변경해도 이미 저장한 사진은 삭제하지 않습니다." };
    var password = new TextBox { Dock = DockStyle.Top, UseSystemPasswordChar = true, PlaceholderText = "예전 동기화 암호 (기존 기록이 요청할 때만)", Visible = false, BackColor = Color.FromArgb(34, 37, 46), ForeColor = Color.White, BorderStyle = BorderStyle.FixedSingle };
    var status = new Label { Dock = DockStyle.Top, Height = 55, Padding = new Padding(14, 4, 14, 0), ForeColor = GoogleDriveAuthorization.NeedsReauthentication ? Color.FromArgb(255, 192, 110) : Color.FromArgb(215, 224, 236), Text = BookshelfStore.ConfiguredCatalogUnavailable
        ? "선택한 책장 저장 위치에 접근할 수 없습니다. 저장 매체를 다시 연결해 주세요."
        : GoogleDriveAuthorization.NeedsReauthentication ? "Google 로그인 만료됨 · 아래 버튼으로 다시 로그인해 주세요."
        : GoogleDriveAuthorization.Load()?.AccountKeys == true ? "Google 계정 연결됨"
        : "Google 계정 연결 필요" };
    var connect = new Button
    {
      Dock = DockStyle.Bottom,
      Height = 42,
      Text = GoogleDriveAuthorization.NeedsReauthentication
          ? "Google 계정 다시 로그인"
          : "브라우저와 같은 Google 계정 연결 / 변경",
    };
    WinFormsSurface.StyleDarkButton(connect, primary: true);
    enabled.CheckedChanged += (_, _) => Enabled = enabled.Checked;
    mode.CheckedChanged += (_, _) => OnDemand = mode.Checked;
    connect.Click += async (_, _) => {
      connect.Enabled = false;
      try {
        var previousKeyId = GoogleDriveAuthorization.Load()?.PrimaryKeyId;
        await GoogleDriveAuthorization.ConnectAsync(password.Text, stop.Token, stage =>
        {
          if (!dialog.IsDisposed) dialog.BeginInvoke(new Action(() => status.Text = stage));
        });
        var currentKeyId = GoogleDriveAuthorization.Load()?.PrimaryKeyId;
        if (!string.IsNullOrEmpty(previousKeyId) && !string.IsNullOrEmpty(currentKeyId) &&
            !string.Equals(previousKeyId, currentKeyId, StringComparison.Ordinal))
        {
#if BOOKSHELF_STANDALONE
          foreach (var name in new[] { "drive-tab-replica.json", "drive-userdata-state.json" })
          {
            var path = Path.Combine(GoogleDriveAuthorization.SharedBrowserDirectory, name);
            if (File.Exists(path)) File.Move(path, path + "." + Guid.NewGuid().ToString("N") + ".backup");
          }
#else
          DesktopDriveSync.ResetConnection();
          DesktopUserDataSync.ResetConnection();
#endif
        }
        if (!dialog.IsDisposed) {
          password.Clear();
          status.Text = BookshelfStore.ConfiguredCatalogUnavailable
              ? "Google 계정 연결됨 · 책장 저장 매체를 다시 연결해야 동기화됩니다."
              : "Google 계정 연결됨";
          connect.Text = "브라우저와 같은 Google 계정 연결 / 변경";
          enabled.Checked = true;
        }
      }
      catch (Exception error) { if (!dialog.IsDisposed) { status.Text = error.Message; password.Visible = error.Message.Contains("암호"); } }
      finally { if (!dialog.IsDisposed) connect.Enabled = true; }
    };
    dialog.FormClosing += (_, _) => stop.Cancel();
    dialog.Controls.AddRange([status, password, modeHelp, mode, enabled, help, connect]); dialog.ShowDialog(owner);
  }
  public void Dispose() { _stop.Cancel(); }
}
