using System.IO.Compression;
using System.Net.Http;
using System.Runtime.InteropServices;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using System.Text.RegularExpressions;
using Microsoft.Web.WebView2.Core;

namespace PrivacyBrowser.Desktop;

internal sealed class ExtensionManagerForm : Form
{
  private static readonly Color Background = Color.FromArgb(18, 18, 20);
  private static readonly Color Surface = Color.FromArgb(38, 38, 42);
  private static readonly Color Field = Color.FromArgb(24, 24, 28);
  private static readonly Color Accent = Color.FromArgb(10, 132, 255);
  private static readonly Color TextColor = Color.FromArgb(245, 245, 247);
  private static readonly Color Muted = Color.FromArgb(165, 165, 170);

  private static readonly HttpClient StoreHttp = CreateStoreHttpClient();

  private readonly CoreWebView2Profile _profile;
  private readonly string _profileId;
  private readonly string _storageRoot;
  private readonly string _sourceMapPath;
  private readonly Func<string, Task>? _openUrlInTab;
  private readonly Func<string, string, string?, Task>? _openPopup;
  private readonly ListView _list = new();
  private readonly Label _status = new();
  private readonly TextBox _urlInput = new();
  private readonly Button _installUrlButton;
  private readonly Button _chromeStoreButton;
  private readonly Button _firefoxStoreButton;
  private readonly Button _packageButton;
  private readonly Button _folderButton;
  private readonly Button _openPopupButton;
  private readonly Button _openOptionsButton;
  private readonly Button _toggleButton;
  private readonly Button _removeButton;
  private bool _busy;

  internal ExtensionManagerForm(
      CoreWebView2Profile profile,
      string profileId,
      string profileName,
      Func<string, Task>? openUrlInTab = null,
      string? initialStoreUrl = null,
      Func<string, string, string?, Task>? openPopup = null)
  {
    _profile = profile;
    _profileId = profileId;
    _openUrlInTab = openUrlInTab;
    _openPopup = openPopup;
    _storageRoot = GetStorageRoot(profileId);
    _sourceMapPath = GetSourceMapPath(profileId);
    SuspendLayout();
    Text = "확장 프로그램 — " + profileName;
    Size = new Size(860, 600);
    MinimumSize = new Size(700, 480);
    StartPosition = FormStartPosition.CenterParent;
    BackColor = Background;
    ForeColor = TextColor;
    Font = new Font("Segoe UI", 9.5F);

    var layout = new TableLayoutPanel
    {
      Dock = DockStyle.Fill,
      Padding = new Padding(20),
      ColumnCount = 1,
      RowCount = 7,
    };
    layout.RowStyles.Add(new RowStyle(SizeType.Absolute, 38));
    layout.RowStyles.Add(new RowStyle(SizeType.Absolute, 42));
    layout.RowStyles.Add(new RowStyle(SizeType.Absolute, 46));
    layout.RowStyles.Add(new RowStyle(SizeType.Absolute, 44));
    layout.RowStyles.Add(new RowStyle(SizeType.Percent, 100));
    layout.RowStyles.Add(new RowStyle(SizeType.Absolute, 52));
    layout.RowStyles.Add(new RowStyle(SizeType.Absolute, 26));
    Controls.Add(layout);

    layout.Controls.Add(new Label
    {
      Dock = DockStyle.Fill,
      Text = "확장 프로그램",
      Font = new Font("Segoe UI", 17F, FontStyle.Bold),
      ForeColor = TextColor,
      TextAlign = ContentAlignment.MiddleLeft,
    }, 0, 0);

    layout.Controls.Add(new Label
    {
      Dock = DockStyle.Fill,
      Text = "Chrome 웹 스토어, Firefox 부가기능(addons.mozilla.org), Edge 애드온 페이지에서 바로 설치하거나 스토어 URL·CRX·XPI·ZIP·폴더를 추가할 수 있습니다.",
      ForeColor = Muted,
      TextAlign = ContentAlignment.MiddleLeft,
    }, 0, 1);

    var urlBar = new TableLayoutPanel
    {
      Dock = DockStyle.Fill,
      ColumnCount = 2,
      RowCount = 1,
      Margin = Padding.Empty,
      Padding = new Padding(0, 4, 0, 4),
    };
    urlBar.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 100));
    urlBar.ColumnStyles.Add(new ColumnStyle(SizeType.Absolute, 132));
    _urlInput.Dock = DockStyle.Fill;
    _urlInput.BackColor = Field;
    _urlInput.ForeColor = TextColor;
    _urlInput.BorderStyle = BorderStyle.FixedSingle;
    _urlInput.Font = new Font("Segoe UI", 10F);
    _urlInput.PlaceholderText = "Chrome 웹 스토어 / Firefox 부가기능 / Edge 애드온 URL 또는 32자 ID 붙여넣기";
    if (!string.IsNullOrWhiteSpace(initialStoreUrl) && IsSupportedStoreOrPackageUrl(initialStoreUrl))
      _urlInput.Text = initialStoreUrl;
    _installUrlButton = ActionButton("URL로 바로 설치", 124, primary: true);
    _installUrlButton.Dock = DockStyle.Fill;
    _installUrlButton.Margin = new Padding(8, 0, 0, 0);
    urlBar.Controls.Add(_urlInput, 0, 0);
    urlBar.Controls.Add(_installUrlButton, 1, 0);
    layout.Controls.Add(urlBar, 0, 2);

    var storeBar = new FlowLayoutPanel
    {
      Dock = DockStyle.Fill,
      FlowDirection = FlowDirection.LeftToRight,
      WrapContents = false,
      Padding = new Padding(0, 3, 0, 3),
    };
    _chromeStoreButton = ActionButton("Chrome 웹 스토어 열기", 156);
    _firefoxStoreButton = ActionButton("Firefox 부가기능 열기", 156);
    _packageButton = ActionButton("파일 추가 (.crx / .xpi / .zip)", 186);
    _folderButton = ActionButton("폴더 추가 (manifest.json)", 168);
    storeBar.Controls.AddRange([_chromeStoreButton, _firefoxStoreButton, _packageButton, _folderButton]);
    layout.Controls.Add(storeBar, 0, 3);

    _list.Dock = DockStyle.Fill;
    _list.View = View.Details;
    _list.FullRowSelect = true;
    _list.MultiSelect = false;
    _list.HideSelection = false;
    _list.BackColor = Surface;
    _list.ForeColor = TextColor;
    _list.BorderStyle = BorderStyle.None;
    _list.Columns.Add("이름", 280);
    _list.Columns.Add("상태", 85);
    _list.Columns.Add("실행/옵션", 140);
    _list.Columns.Add("ID", 260);
    _list.SelectedIndexChanged += (_, _) => UpdateButtons();
    _list.DoubleClick += async (_, _) => await LaunchSelectedExtensionAsync(preferOptions: false);
    layout.Controls.Add(_list, 0, 4);

    var actions = new FlowLayoutPanel
    {
      Dock = DockStyle.Fill,
      FlowDirection = FlowDirection.LeftToRight,
      WrapContents = false,
      Padding = new Padding(0, 8, 0, 0),
    };
    _openPopupButton = ActionButton("팝업/실행 열기", 122, primary: true);
    _openOptionsButton = ActionButton("옵션 설정 열기", 122);
    _toggleButton = ActionButton("사용 중지", 104);
    _removeButton = ActionButton("제거", 84);
    actions.Controls.AddRange([_openPopupButton, _openOptionsButton, _toggleButton, _removeButton]);
    layout.Controls.Add(actions, 0, 5);

    _status.Dock = DockStyle.Fill;
    _status.ForeColor = Muted;
    _status.TextAlign = ContentAlignment.MiddleLeft;
    layout.Controls.Add(_status, 0, 6);

    _installUrlButton.Click += async (_, _) => await InstallFromInputAsync();
    _urlInput.KeyDown += async (_, e) =>
    {
      if (e.KeyCode == Keys.Enter)
      {
        e.SuppressKeyPress = true;
        await InstallFromInputAsync();
      }
    };
    _chromeStoreButton.Click += async (_, _) =>
    {
      if (_openUrlInTab is not null)
      {
        Close();
        await _openUrlInTab("https://chromewebstore.google.com/");
      }
    };
    _firefoxStoreButton.Click += async (_, _) =>
    {
      if (_openUrlInTab is not null)
      {
        Close();
        await _openUrlInTab("https://addons.mozilla.org/ko/firefox/extensions/");
      }
    };
    _folderButton.Click += async (_, _) =>
    {
      using var picker = new FolderBrowserDialog { Description = "manifest.json이 있는 확장 폴더를 선택하세요." };
      if (picker.ShowDialog(this) == DialogResult.OK)
        await InstallLocalAsync(picker.SelectedPath, isPackageFile: false);
    };
    _packageButton.Click += async (_, _) =>
    {
      using var picker = new OpenFileDialog
      {
        Title = "Chrome(.crx), Firefox(.xpi), 또는 ZIP 확장 패키지 선택",
        Filter = "확장 패키지 (*.crx;*.xpi;*.zip)|*.crx;*.xpi;*.zip|모든 파일 (*.*)|*.*",
        CheckFileExists = true,
      };
      if (picker.ShowDialog(this) == DialogResult.OK)
        await InstallLocalAsync(picker.FileName, isPackageFile: true);
    };
    _openPopupButton.Click += async (_, _) => await LaunchSelectedExtensionAsync(preferOptions: false);
    _openOptionsButton.Click += async (_, _) => await LaunchSelectedExtensionAsync(preferOptions: true);
    _toggleButton.Click += async (_, _) =>
    {
      if (SelectedExtension() is not { } extension) return;
      await RunAsync(async () =>
      {
        await extension.EnableAsync(!extension.IsEnabled);
        await RefreshAsync();
      });
    };
    _removeButton.Click += async (_, _) =>
    {
      if (SelectedExtension() is not { } extension) return;
      if (MessageBox.Show(this, $"'{extension.Name}' 확장 프로그램을 제거할까요?", "확장 프로그램 제거",
              MessageBoxButtons.YesNo, MessageBoxIcon.Question) != DialogResult.Yes) return;
      await RunAsync(async () =>
      {
        await extension.RemoveAsync();
        DeleteManagedSource(_profileId, extension.Id);
        await RefreshAsync();
      });
    };
    Shown += async (_, _) => await RunAsync(RefreshAsync);
    UpdateButtons();
    ResumeLayout(true);
    WinFormsSurface.Attach(this);
  }

  private static HttpClient CreateStoreHttpClient()
  {
    var handler = new HttpClientHandler
    {
      AllowAutoRedirect = true,
      MaxAutomaticRedirections = 10,
      AutomaticDecompression = System.Net.DecompressionMethods.All,
    };
    var client = new HttpClient(handler)
    {
      Timeout = TimeSpan.FromSeconds(45),
    };
    client.DefaultRequestHeaders.UserAgent.ParseAdd(
        "Mozilla/5.0 (Windows NT 10.0; Win64; x64) AppleWebKit/537.36 (KHTML, like Gecko) Chrome/131.0.0.0 Safari/537.36");
    return client;
  }

  private static Button ActionButton(string caption, int width, bool primary = false)
  {
    var button = new Button
    {
      Text = caption,
      Width = width,
      Height = 34,
      Margin = new Padding(0, 0, 8, 0),
      BackColor = primary ? Accent : Surface,
      ForeColor = Color.White,
      FlatStyle = FlatStyle.Flat,
      Cursor = Cursors.Hand,
      Font = new Font("Segoe UI Semibold", 9F),
    };
    button.FlatAppearance.BorderSize = primary ? 0 : 1;
    button.FlatAppearance.BorderColor = Color.FromArgb(62, 62, 68);
    return button;
  }

  private CoreWebView2BrowserExtension? SelectedExtension() =>
      _list.SelectedItems.Count == 0 ? null : _list.SelectedItems[0].Tag as CoreWebView2BrowserExtension;

  private void UpdateButtons()
  {
    var selected = SelectedExtension();
    _installUrlButton.Enabled = !_busy;
    _chromeStoreButton.Enabled = !_busy && _openUrlInTab is not null;
    _firefoxStoreButton.Enabled = !_busy && _openUrlInTab is not null;
    _folderButton.Enabled = !_busy;
    _packageButton.Enabled = !_busy;
    _toggleButton.Enabled = !_busy && selected is not null;
    _removeButton.Enabled = !_busy && selected is not null;
    if (selected is not null)
    {
      _toggleButton.Text = selected.IsEnabled ? "사용 중지" : "사용";
      var endpoints = GetExtensionEndpoints(_profileId, selected.Id);
      _openPopupButton.Enabled = !_busy && selected.IsEnabled && !string.IsNullOrWhiteSpace(endpoints.PopupUrl);
      _openOptionsButton.Enabled = !_busy && selected.IsEnabled && !string.IsNullOrWhiteSpace(endpoints.OptionsUrl);
    }
    else
    {
      _openPopupButton.Enabled = false;
      _openOptionsButton.Enabled = false;
    }
  }

  private async Task LaunchSelectedExtensionAsync(bool preferOptions)
  {
    if (SelectedExtension() is not { } selected || (_openUrlInTab is null && _openPopup is null)) return;
    var endpoints = GetExtensionEndpoints(_profileId, selected.Id);
    if (!preferOptions && !string.IsNullOrWhiteSpace(endpoints.PopupUrl) && _openPopup is not null)
    {
      Close();
      await _openPopup(selected.Name, endpoints.PopupUrl, endpoints.OptionsUrl);
      return;
    }
    var target = preferOptions
        ? (endpoints.OptionsUrl ?? endpoints.PopupUrl)
        : (endpoints.PopupUrl ?? endpoints.OptionsUrl);
    if (string.IsNullOrWhiteSpace(target) || _openUrlInTab is null)
    {
      _status.Text = $"'{selected.Name}' 확장 프로그램은 별도 팝업/옵션 페이지가 없는 자동 실행형 확장입니다.";
      return;
    }
    Close();
    await _openUrlInTab(target);
  }

  private async Task InstallFromInputAsync()
  {
    var text = _urlInput.Text.Trim();
    if (string.IsNullOrWhiteSpace(text))
    {
      _status.Text = "설치할 스토어 URL이나 확장 프로그램 ID를 입력해 주세요.";
      return;
    }
    await RunAsync(async () =>
    {
      var installed = await InstallFromStoreOrUrlAsync(
          _profile,
          _profileId,
          text,
          progress => _status.Text = progress);
      _urlInput.Clear();
      await RefreshAsync();
      _status.Text = $"'{installed.Name}' 설치 완료";
    });
  }

  private async Task InstallLocalAsync(string source, bool isPackageFile)
  {
    await RunAsync(async () =>
    {
      var installed = await InstallFromLocalPathAsync(_profile, _profileId, source, isPackageFile);
      await RefreshAsync();
      _status.Text = $"'{installed.Name}' 설치 완료";
    });
  }

  private async Task RunAsync(Func<Task> operation)
  {
    if (_busy) return;
    _busy = true;
    UpdateButtons();
    try { await operation(); }
    catch (Exception error)
    {
      _status.Text = "작업 실패: " + error.Message;
    }
    finally
    {
      _busy = false;
      UpdateButtons();
    }
  }

  private async Task RefreshAsync()
  {
    var extensions = await _profile.GetBrowserExtensionsAsync();
    _list.BeginUpdate();
    try
    {
      _list.Items.Clear();
      foreach (var extension in extensions.OrderBy(item => item.Name, StringComparer.CurrentCultureIgnoreCase))
      {
        var endpoints = GetExtensionEndpoints(_profileId, extension.Id);
        var capability = (!string.IsNullOrWhiteSpace(endpoints.PopupUrl), !string.IsNullOrWhiteSpace(endpoints.OptionsUrl)) switch
        {
          (true, true) => "팝업 · 옵션",
          (true, false) => "팝업 실행 가능",
          (false, true) => "옵션 설정 가능",
          _ => "자동 실행 (콘텐츠/배경)",
        };
        var row = new ListViewItem(extension.Name) { Tag = extension };
        row.SubItems.Add(extension.IsEnabled ? "사용 중" : "중지됨");
        row.SubItems.Add(capability);
        row.SubItems.Add(extension.Id);
        _list.Items.Add(row);
      }
      _status.Text = $"{extensions.Count}개 설치됨 · 더블클릭하거나 하단 버튼으로 팝업/옵션 실행";
    }
    finally { _list.EndUpdate(); }
  }

  internal static bool IsSupportedStoreOrPackageUrl(string? url)
  {
    if (string.IsNullOrWhiteSpace(url)) return false;
    var trimmed = url.Trim();
    if (Regex.IsMatch(trimmed, "^[a-p]{32}$", RegexOptions.IgnoreCase)) return true;
    if (!Uri.TryCreate(trimmed, UriKind.Absolute, out var uri)) return false;
    var host = uri.Host.ToLowerInvariant();
    var path = uri.AbsolutePath.ToLowerInvariant();
    return host.Contains("chromewebstore.google.com") ||
           (host.Contains("chrome.google.com") && path.Contains("/webstore")) ||
           host.Contains("addons.mozilla.org") ||
           (host.Contains("microsoftedge.microsoft.com") && path.Contains("/addons")) ||
           path.EndsWith(".crx", StringComparison.OrdinalIgnoreCase) ||
           path.EndsWith(".xpi", StringComparison.OrdinalIgnoreCase);
  }

  internal static async Task<CoreWebView2BrowserExtension> InstallFromStoreOrUrlAsync(
      CoreWebView2Profile profile,
      string profileId,
      string inputUrlOrId,
      Action<string>? onProgress = null)
  {
    var trimmed = inputUrlOrId.Trim();
    if (File.Exists(trimmed))
      return await InstallFromLocalPathAsync(profile, profileId, trimmed, isPackageFile: true);
    if (Directory.Exists(trimmed))
      return await InstallFromLocalPathAsync(profile, profileId, trimmed, isPackageFile: false);

    var downloadUrl = await ResolvePackageDownloadUrlAsync(trimmed, onProgress);
    onProgress?.Invoke("확장 프로그램 패키지 다운로드 중...");

    using var response = await StoreHttp.GetAsync(downloadUrl, HttpCompletionOption.ResponseHeadersRead);
    if (!response.IsSuccessStatusCode)
      throw new InvalidOperationException($"스토어에서 확장 패키지를 내려받지 못했습니다 (HTTP {(int)response.StatusCode}).");

    var bytes = await response.Content.ReadAsByteArrayAsync();
    if (bytes.Length < 32)
      throw new InvalidDataException("내려받은 확장 패키지 데이터가 비어 있거나 손상되었습니다.");

    var tempFile = Path.Combine(Path.GetTempPath(), "omyaru-ext-" + Guid.NewGuid().ToString("N") + ".pkg");
    try
    {
      await File.WriteAllBytesAsync(tempFile, bytes);
      onProgress?.Invoke("확장 프로그램 압축 해제 및 호환성 변환 중...");
      return await InstallFromLocalPathAsync(profile, profileId, tempFile, isPackageFile: true);
    }
    finally
    {
      try { if (File.Exists(tempFile)) File.Delete(tempFile); } catch { }
    }
  }

  private static async Task<string> ResolvePackageDownloadUrlAsync(string input, Action<string>? onProgress)
  {
    if (Regex.IsMatch(input, "^[a-p]{32}$", RegexOptions.IgnoreCase))
    {
      var id = input.ToLowerInvariant();
      return $"https://clients2.google.com/service/update2/crx?response=redirect&prodversion=145.0.0.0&acceptformat=crx2,crx3&x=id%3D{id}%26installsource%3Dondemand%26uc";
    }

    if (!Uri.TryCreate(input, UriKind.Absolute, out var uri))
      throw new InvalidDataException("유효한 스토어 URL 또는 32자 확장 ID가 아닙니다.");

    var host = uri.Host.ToLowerInvariant();
    var path = uri.AbsolutePath;

    if (path.EndsWith(".crx", StringComparison.OrdinalIgnoreCase) ||
        path.EndsWith(".xpi", StringComparison.OrdinalIgnoreCase) ||
        path.EndsWith(".zip", StringComparison.OrdinalIgnoreCase))
    {
      return uri.ToString();
    }

    // 1. Chrome Web Store: https://chromewebstore.google.com/detail/<name>/<32-char-id>
    if (host.Contains("chromewebstore.google.com") || host.Contains("chrome.google.com"))
    {
      var match = Regex.Match(path, "([a-p]{32})", RegexOptions.IgnoreCase);
      if (!match.Success)
        throw new InvalidDataException("Chrome 웹 스토어 상세 페이지 URL에서 32자 확장 ID를 찾지 못했습니다. 확장 프로그램 상세 페이지에서 설치해 주세요.");
      var id = match.Groups[1].Value.ToLowerInvariant();
      return $"https://clients2.google.com/service/update2/crx?response=redirect&prodversion=145.0.0.0&acceptformat=crx2,crx3&x=id%3D{id}%26installsource%3Dondemand%26uc";
    }

    // 2. Microsoft Edge Add-ons: https://microsoftedge.microsoft.com/addons/detail/<name>/<32-char-id>
    if (host.Contains("microsoftedge.microsoft.com"))
    {
      var match = Regex.Match(path, "([a-p]{32})", RegexOptions.IgnoreCase);
      if (!match.Success)
        throw new InvalidDataException("Edge 애드온 상세 페이지 URL에서 32자 확장 ID를 찾지 못했습니다.");
      var id = match.Groups[1].Value.ToLowerInvariant();
      return $"https://edge.microsoft.com/extensionwebstorebase/v1/crx?response=redirect&x=id%3D{id}%26installsource%3Dondemand%26uc";
    }

    // 3. Firefox Add-ons (AMO): https://addons.mozilla.org/ko/firefox/addon/<slug>/
    if (host.Contains("addons.mozilla.org"))
    {
      var match = Regex.Match(path, "/addon/([^/?#]+)", RegexOptions.IgnoreCase);
      if (!match.Success)
        throw new InvalidDataException("Firefox 부가기능 상세 페이지(/firefox/addon/...)에서 설치해 주세요.");
      var slug = Uri.UnescapeDataString(match.Groups[1].Value);
      onProgress?.Invoke($"Firefox 부가기능 정보 조회 중 ({slug})...");
      var apiUrl = $"https://addons.mozilla.org/api/v5/addons/addon/{Uri.EscapeDataString(slug)}/";
      var json = await StoreHttp.GetStringAsync(apiUrl);
      using var doc = JsonDocument.Parse(json);
      if (doc.RootElement.TryGetProperty("current_version", out var currentVersion) &&
          currentVersion.TryGetProperty("file", out var fileObj) &&
          fileObj.TryGetProperty("url", out var urlProp) &&
          !string.IsNullOrWhiteSpace(urlProp.GetString()))
      {
        return urlProp.GetString()!;
      }
      throw new InvalidDataException("Firefox 부가기능 API에서 XPI 다운로드 주소를 찾지 못했습니다.");
    }

    throw new InvalidDataException("지원되는 스토어 주소(Chrome 웹 스토어, Firefox 부가기능, Edge 애드온) 또는 .crx/.xpi URL을 입력해 주세요.");
  }

  internal static async Task<CoreWebView2BrowserExtension> InstallFromLocalPathAsync(
      CoreWebView2Profile profile,
      string profileId,
      string source,
      bool isPackageFile)
  {
    var storageRoot = GetStorageRoot(profileId);
    Directory.CreateDirectory(storageRoot);
    var staged = Path.Combine(storageRoot, Guid.NewGuid().ToString("N"));
    Directory.CreateDirectory(staged);
    CoreWebView2BrowserExtension? installed = null;
    try
    {
      if (isPackageFile) ExtractPackageFile(source, staged);
      else CopyExtensionFolder(source, staged);
      var extensionRoot = FindManifestRoot(staged);
      SanitizeUnpackedExtensionForWebView2(extensionRoot);
      NormalizeManifestForWebView2(extensionRoot);
      ValidateManifest(extensionRoot);
      try
      {
        installed = await profile.AddBrowserExtensionAsync(extensionRoot);
      }
      catch (Exception error) when (error is UnauthorizedAccessException or COMException)
      {
        var hr = $"0x{error.HResult:X8}";
        if (error.HResult == unchecked((int)0x80070005))
          throw new InvalidOperationException(
              $"WebView2 확장 로더가 이 확장 프로그램을 거부했습니다 ({hr}). Chromium 미지원 매니페스트 항목이나 예약된 파일명(_)이 포함되었을 수 있습니다.",
              error);
        if (error.HResult == unchecked((int)0x80070032))
          throw new InvalidOperationException(
              $"현재 프로필에서 확장 프로그램 기능이 비활성화되어 있습니다 ({hr}). Omyaru를 완전히 재시작한 뒤 다시 시도해 주세요.",
              error);
        throw new InvalidOperationException(
            $"WebView2 확장 프로그램 로드에 실패했습니다 ({hr}).",
            error);
      }
      var sources = ReadSources(profileId);
      sources[installed.Id] = extensionRoot;
      SaveSources(profileId, sources);
      return installed;
    }
    catch
    {
      if (installed is null)
      {
        try { Directory.Delete(staged, true); } catch { }
      }
      throw;
    }
  }

  private static void ExtractPackageFile(string sourceFile, string destination)
  {
    var info = new FileInfo(sourceFile);
    if (!info.Exists || info.Length < 16)
      throw new InvalidDataException("확장 패키지 파일이 비어 있거나 존재하지 않습니다.");
    if (info.Length > 100L * 1024 * 1024)
      throw new InvalidDataException("확장 패키지는 100MB를 넘을 수 없습니다.");

    var bytes = File.ReadAllBytes(sourceFile);
    var zipOffset = FindZipPayloadOffset(bytes);
    if (zipOffset < 0 || zipOffset >= bytes.Length - 4)
      throw new InvalidDataException("유효한 CRX / XPI / ZIP 확장 패키지가 아닙니다.");

    using var memory = new MemoryStream(bytes, zipOffset, bytes.Length - zipOffset, writable: false);
    using var archive = new ZipArchive(memory, ZipArchiveMode.Read);
    if (archive.Entries.Count > 6_000 || archive.Entries.Sum(entry => entry.Length) > 150L * 1024 * 1024)
      throw new InvalidDataException("확장 패키지는 6,000개 파일과 150MB를 넘을 수 없습니다.");

    var destFull = Path.GetFullPath(destination) + Path.DirectorySeparatorChar;
    foreach (var entry in archive.Entries)
    {
      if (string.IsNullOrEmpty(entry.Name) && entry.FullName.EndsWith('/')) continue;
      var normalizedEntry = entry.FullName.TrimStart('/');
      if (normalizedEntry.StartsWith("__MACOSX/", StringComparison.OrdinalIgnoreCase) ||
          normalizedEntry.StartsWith("META-INF/", StringComparison.OrdinalIgnoreCase) ||
          normalizedEntry.StartsWith("_metadata/", StringComparison.OrdinalIgnoreCase))
        continue;
      var relative = normalizedEntry.Replace('/', Path.DirectorySeparatorChar);
      var targetPath = Path.GetFullPath(Path.Combine(destination, relative));
      if (!targetPath.StartsWith(destFull, StringComparison.OrdinalIgnoreCase)) continue;
      Directory.CreateDirectory(Path.GetDirectoryName(targetPath)!);
      entry.ExtractToFile(targetPath, overwrite: true);
    }
  }

  private static int FindZipPayloadOffset(byte[] bytes)
  {
    // Standard ZIP / Firefox XPI starts with PK\x03\x04
    if (bytes.Length >= 4 && bytes[0] == 0x50 && bytes[1] == 0x4B && bytes[2] == 0x03 && bytes[3] == 0x04)
      return 0;

    // Chrome / Edge CRX2 or CRX3 starts with "Cr24" (0x43, 0x72, 0x32, 0x34)
    if (bytes.Length >= 16 && bytes[0] == 0x43 && bytes[1] == 0x72 && bytes[2] == 0x32 && bytes[3] == 0x34)
    {
      var version = BitConverter.ToUInt32(bytes, 4);
      if (version == 3)
      {
        var headerSize = (int)BitConverter.ToUInt32(bytes, 8);
        var offset = 12 + headerSize;
        if (offset > 0 && offset < bytes.Length - 4) return offset;
      }
      else if (version == 2)
      {
        var pubKeySize = (int)BitConverter.ToUInt32(bytes, 8);
        var sigSize = (int)BitConverter.ToUInt32(bytes, 12);
        var offset = 16 + pubKeySize + sigSize;
        if (offset > 0 && offset < bytes.Length - 4) return offset;
      }
    }

    // Fallback scan for PK\x03\x04 within the first 64 KB
    var limit = Math.Min(bytes.Length - 4, 65536);
    for (var i = 0; i < limit; i++)
    {
      if (bytes[i] == 0x50 && bytes[i + 1] == 0x4B && bytes[i + 2] == 0x03 && bytes[i + 3] == 0x04)
        return i;
    }
    return -1;
  }

  private static void CopyExtensionFolder(string source, string destination)
  {
    if (!File.Exists(Path.Combine(source, "manifest.json")))
      throw new InvalidDataException("선택한 폴더에 manifest.json이 없습니다.");
    var options = new EnumerationOptions { RecurseSubdirectories = true, AttributesToSkip = FileAttributes.ReparsePoint };
    long bytes = 0;
    var count = 0;
    foreach (var file in Directory.EnumerateFiles(source, "*", options))
    {
      if (++count > 6_000 || (bytes += new FileInfo(file).Length) > 150L * 1024 * 1024)
        throw new InvalidDataException("확장 폴더는 6,000개 파일과 150MB를 넘을 수 없습니다.");
      var relative = Path.GetRelativePath(source, file);
      var target = Path.Combine(destination, relative);
      Directory.CreateDirectory(Path.GetDirectoryName(target)!);
      File.Copy(file, target, overwrite: true);
    }
  }

  private static string FindManifestRoot(string directory)
  {
    if (File.Exists(Path.Combine(directory, "manifest.json"))) return directory;
    var children = Directory.GetDirectories(directory);
    if (children.Length == 1 && File.Exists(Path.Combine(children[0], "manifest.json"))) return children[0];
    var found = Directory.EnumerateFiles(directory, "manifest.json", SearchOption.AllDirectories).FirstOrDefault();
    if (found is not null) return Path.GetDirectoryName(found)!;
    throw new InvalidDataException("확장 패키지 내부에서 manifest.json을 찾지 못했습니다.");
  }

  private static void SanitizeUnpackedExtensionForWebView2(string extensionRoot)
  {
    // Chromium rejects unpacked extensions containing non-reserved top-level names starting with "_".
    // Keep only Chromium-whitelisted "_locales" and "_platform_specific".
    foreach (var junkDir in new[] { "__MACOSX", "META-INF", "_metadata" })
    {
      var dirPath = Path.Combine(extensionRoot, junkDir);
      if (Directory.Exists(dirPath))
        try { Directory.Delete(dirPath, recursive: true); } catch { }
    }
    foreach (var junkFile in new[] { ".DS_Store", "Thumbs.db" })
    {
      var filePath = Path.Combine(extensionRoot, junkFile);
      if (File.Exists(filePath))
        try { File.Delete(filePath); } catch { }
    }
    var manifestText = File.Exists(Path.Combine(extensionRoot, "manifest.json"))
        ? File.ReadAllText(Path.Combine(extensionRoot, "manifest.json"))
        : string.Empty;
    foreach (var entry in Directory.EnumerateFileSystemEntries(extensionRoot))
    {
      var name = Path.GetFileName(entry);
      if (!name.StartsWith('_')) continue;
      if (name.Equals("_locales", StringComparison.OrdinalIgnoreCase) ||
          name.Equals("_platform_specific", StringComparison.OrdinalIgnoreCase))
        continue;
      if (!manifestText.Contains(name, StringComparison.OrdinalIgnoreCase))
      {
        try
        {
          if (Directory.Exists(entry)) Directory.Delete(entry, recursive: true);
          else File.Delete(entry);
        }
        catch { }
      }
    }
  }

  private static void NormalizeManifestForWebView2(string extensionRoot)
  {
    var utf8NoBom = new UTF8Encoding(false);
    var manifestPath = Path.Combine(extensionRoot, "manifest.json");
    var raw = File.ReadAllText(manifestPath);
    var node = JsonNode.Parse(raw, documentOptions: new JsonDocumentOptions
    {
      CommentHandling = JsonCommentHandling.Skip,
      AllowTrailingCommas = true,
    }) as JsonObject;
    if (node is null) return;

    var hadFirefoxMetadata = node.Remove("applications") | node.Remove("browser_specific_settings");
    var manifestVersion = node["manifest_version"]?.GetValue<int>() ?? 2;

    // IMPORTANT: Never start generated filenames with "_" — Chromium rejects any unreserved
    // top-level "_" filename in unpacked WebView2 extensions with E_ACCESSDENIED (0x80070005).
    const string polyfillFileName = "omyaru-browser-shim.js";
    const string polyfillCode = """
        if (typeof globalThis.browser === 'undefined' && typeof globalThis.chrome !== 'undefined') {
          globalThis.browser = globalThis.chrome;
        }
        """;

    if (manifestVersion == 3 && node["background"] is JsonObject bgObj)
    {
      if (bgObj["service_worker"] is null && bgObj["scripts"] is JsonArray scriptsArr && scriptsArr.Count > 0)
      {
        var scripts = scriptsArr
            .Select(s => s?.GetValue<string>())
            .Where(s => !string.IsNullOrWhiteSpace(s))
            .Select(s => s!.TrimStart('/'))
            .ToArray();
        var swFileName = "omyaru-sw.js";
        var isModule = string.Equals(bgObj["type"]?.GetValue<string>(), "module", StringComparison.OrdinalIgnoreCase);
        var swContent = isModule
            ? polyfillCode + "\n" + string.Join("\n", scripts.Select(s => $"import {JsonSerializer.Serialize("./" + s)};")) + "\n"
            : polyfillCode + "\nimportScripts(" + string.Join(", ", scripts.Select(s => JsonSerializer.Serialize(s))) + ");\n";
        File.WriteAllText(Path.Combine(extensionRoot, swFileName), swContent, utf8NoBom);
        bgObj["service_worker"] = swFileName;
        bgObj.Remove("scripts");
        bgObj.Remove("persistent");
        bgObj.Remove("page");
        if (!isModule) bgObj.Remove("type");
      }
      else
      {
        bgObj.Remove("scripts");
        bgObj.Remove("persistent");
        bgObj.Remove("page");
      }
    }
    else if (manifestVersion == 2 && hadFirefoxMetadata)
    {
      File.WriteAllText(Path.Combine(extensionRoot, polyfillFileName), polyfillCode, utf8NoBom);
      if (node["background"] is JsonObject bgMv2 && bgMv2["scripts"] is JsonArray bgScripts)
      {
        if (!bgScripts.Any(s => string.Equals(s?.GetValue<string>(), polyfillFileName, StringComparison.OrdinalIgnoreCase)))
          bgScripts.Insert(0, polyfillFileName);
      }
    }

    if (hadFirefoxMetadata && node["content_scripts"] is JsonArray contentScripts)
    {
      var shimPath = Path.Combine(extensionRoot, polyfillFileName);
      if (!File.Exists(shimPath))
        File.WriteAllText(shimPath, polyfillCode, utf8NoBom);
      foreach (var item in contentScripts.OfType<JsonObject>())
      {
        if (item["js"] is JsonArray jsArray &&
            !jsArray.Any(s => string.Equals(s?.GetValue<string>(), polyfillFileName, StringComparison.OrdinalIgnoreCase)))
          jsArray.Insert(0, polyfillFileName);
      }
    }

    File.WriteAllText(
        manifestPath,
        node.ToJsonString(new JsonSerializerOptions { WriteIndented = true }),
        utf8NoBom);
  }

  private static void ValidateManifest(string directory)
  {
    using var document = JsonDocument.Parse(
        File.ReadAllText(Path.Combine(directory, "manifest.json")),
        new JsonDocumentOptions { CommentHandling = JsonCommentHandling.Skip, AllowTrailingCommas = true });
    if (!document.RootElement.TryGetProperty("name", out var name) || name.ValueKind != JsonValueKind.String ||
        string.IsNullOrWhiteSpace(name.GetString()) ||
        !document.RootElement.TryGetProperty("manifest_version", out var version) ||
        version.ValueKind != JsonValueKind.Number || version.GetInt32() is not (2 or 3))
      throw new InvalidDataException("유효한 WebExtension manifest.json(v2/v3)이 아닙니다.");
  }

  internal readonly record struct ExtensionEndpoints(string? PopupUrl, string? OptionsUrl);

  internal static ExtensionEndpoints GetExtensionEndpoints(string profileId, string extensionId)
  {
    try
    {
      var sources = ReadSources(profileId);
      if (!sources.TryGetValue(extensionId, out var root) || !Directory.Exists(root))
        return default;
      var manifestPath = Path.Combine(root, "manifest.json");
      if (!File.Exists(manifestPath))
        manifestPath = FindManifestRoot(root) is { } found ? Path.Combine(found, "manifest.json") : manifestPath;
      if (!File.Exists(manifestPath)) return default;

      using var doc = JsonDocument.Parse(
          File.ReadAllText(manifestPath),
          new JsonDocumentOptions { CommentHandling = JsonCommentHandling.Skip, AllowTrailingCommas = true });
      var rootEl = doc.RootElement;

      string? popupRel = null;
      if (rootEl.TryGetProperty("action", out var action) && action.ValueKind == JsonValueKind.Object &&
          action.TryGetProperty("default_popup", out var dp1) && dp1.ValueKind == JsonValueKind.String)
        popupRel = dp1.GetString();
      else if (rootEl.TryGetProperty("browser_action", out var bAction) && bAction.ValueKind == JsonValueKind.Object &&
               bAction.TryGetProperty("default_popup", out var dp2) && dp2.ValueKind == JsonValueKind.String)
        popupRel = dp2.GetString();
      else if (rootEl.TryGetProperty("page_action", out var pAction) && pAction.ValueKind == JsonValueKind.Object &&
               pAction.TryGetProperty("default_popup", out var dp3) && dp3.ValueKind == JsonValueKind.String)
        popupRel = dp3.GetString();

      string? optionsRel = null;
      if (rootEl.TryGetProperty("options_page", out var op1) && op1.ValueKind == JsonValueKind.String)
        optionsRel = op1.GetString();
      else if (rootEl.TryGetProperty("options_ui", out var opUi) && opUi.ValueKind == JsonValueKind.Object &&
               opUi.TryGetProperty("page", out var op2) && op2.ValueKind == JsonValueKind.String)
        optionsRel = op2.GetString();

      var popupUrl = !string.IsNullOrWhiteSpace(popupRel)
          ? $"chrome-extension://{extensionId}/{popupRel.TrimStart('/')}"
          : null;
      var optionsUrl = !string.IsNullOrWhiteSpace(optionsRel)
          ? $"chrome-extension://{extensionId}/{optionsRel.TrimStart('/')}"
          : null;
      return new ExtensionEndpoints(popupUrl, optionsUrl);
    }
    catch
    {
      return default;
    }
  }

  private static string GetStorageRoot(string profileId)
  {
    var digest = Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(profileId))).ToLowerInvariant();
    return Path.Combine(BrowserStateStore.StateDirectory, "extensions", digest);
  }

  private static string GetSourceMapPath(string profileId) =>
      Path.Combine(GetStorageRoot(profileId), "sources.json");

  private static Dictionary<string, string> ReadSources(string profileId)
  {
    var path = GetSourceMapPath(profileId);
    if (!File.Exists(path)) return new Dictionary<string, string>(StringComparer.Ordinal);
    try
    {
      return JsonSerializer.Deserialize<Dictionary<string, string>>(File.ReadAllText(path))
          ?? new Dictionary<string, string>(StringComparer.Ordinal);
    }
    catch
    {
      return new Dictionary<string, string>(StringComparer.Ordinal);
    }
  }

  private static void SaveSources(string profileId, Dictionary<string, string> sources)
  {
    var root = GetStorageRoot(profileId);
    var mapPath = GetSourceMapPath(profileId);
    Directory.CreateDirectory(root);
    var temporary = mapPath + ".new";
    File.WriteAllText(temporary, JsonSerializer.Serialize(sources));
    File.Move(temporary, mapPath, true);
  }

  private static void DeleteManagedSource(string profileId, string extensionId)
  {
    var sources = ReadSources(profileId);
    if (!sources.Remove(extensionId, out var path)) return;
    SaveSources(profileId, sources);
    var root = Path.GetFullPath(GetStorageRoot(profileId)) + Path.DirectorySeparatorChar;
    var fullPath = Path.GetFullPath(path);
    if (fullPath.StartsWith(root, StringComparison.OrdinalIgnoreCase))
    {
      var dirToDelete = fullPath;
      while (Path.GetDirectoryName(dirToDelete) is { } parent &&
             !string.Equals(parent + Path.DirectorySeparatorChar, root, StringComparison.OrdinalIgnoreCase) &&
             parent.StartsWith(root, StringComparison.OrdinalIgnoreCase))
      {
        dirToDelete = parent;
      }
      if (Directory.Exists(dirToDelete))
        Directory.Delete(dirToDelete, true);
    }
  }
}
