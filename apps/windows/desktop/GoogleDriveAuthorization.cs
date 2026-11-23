using System.Net;
using System.Net.Sockets;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;

namespace PrivacyBrowser.Desktop;

internal sealed class GoogleDriveReauthenticationRequiredException(string message) : InvalidOperationException(message);

internal sealed class GoogleDriveAuthorization
{
  private static readonly HttpClient Http = new(new HttpClientHandler { AllowAutoRedirect = false })
      { Timeout = TimeSpan.FromSeconds(25) };
  private static readonly SemaphoreSlim TokenGate = new(1, 1);
  private static readonly byte[] Entropy = Encoding.UTF8.GetBytes("omyaru-google-drive-v1");
  internal static volatile bool NeedsReauthentication;
  internal static event Action? SessionChanged;
#if BOOKSHELF_STANDALONE
  internal static string SharedBrowserDirectory
  {
    get
    {
      if (BrowserStateStore.IsTestOverride) return BrowserStateStore.StateDirectory;
      var local = Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData);
      var current = Path.Combine(local, "Omyaru Browser");
      var previous = Path.Combine(local, "PrivacyBrowser");
      return Directory.Exists(current) || !Directory.Exists(previous) ? current : previous;
    }
  }
#endif
  private static string StateDirectory =>
#if BOOKSHELF_STANDALONE
      SharedBrowserDirectory;
#else
      BrowserStateStore.StateDirectory;
#endif
  private static string PathName => Path.Combine(StateDirectory, "google-drive-session.dat");
  private static string DiagnosticLogPath => Path.Combine(StateDirectory, "drive-auth-last.log");
  private static string? _accessToken;
  private static DateTimeOffset _expires;
  internal sealed record Session(string RefreshToken, string Passphrase, bool AccountKeys = false, string? PrimaryKeyId = null);

  private static void WriteDiagnostic(string stage, string summary)
  {
    // Never write tokens, secrets, authorization codes, emails, or raw response bodies.
    try
    {
      Directory.CreateDirectory(StateDirectory);
      var line = $"{DateTimeOffset.Now:yyyy-MM-dd HH:mm:ss zzz} | {stage} | {summary}{Environment.NewLine}";
      File.AppendAllText(DiagnosticLogPath, line, new UTF8Encoding(false));
      var info = new FileInfo(DiagnosticLogPath);
      if (info.Length > 32768)
      {
        var lines = File.ReadAllLines(DiagnosticLogPath);
        File.WriteAllLines(DiagnosticLogPath, lines.TakeLast(40), new UTF8Encoding(false));
      }
    }
    catch { }
  }

  internal static Session? Load()
  {
#if BOOKSHELF_STANDALONE
    // The independent bookshelf and browser use the same Windows-user account.
    var portable = Path.Combine(BrowserStateStore.StateDirectory, "google-drive-session.dat");
    if (!BrowserStateStore.IsTestOverride && !File.Exists(PathName) && File.Exists(portable))
    {
      Directory.CreateDirectory(Path.GetDirectoryName(PathName)!);
      File.Copy(portable, PathName, overwrite: false);
    }
#endif
    if (!File.Exists(PathName)) return null;
    byte[]? plain = null;
    try
    {
      plain = ProtectedData.Unprotect(File.ReadAllBytes(PathName), Entropy, DataProtectionScope.CurrentUser);
      return JsonSerializer.Deserialize<Session>(plain);
    }
    catch { return null; }
    finally { if (plain is not null) CryptographicOperations.ZeroMemory(plain); }
  }

  internal static void Save(Session session)
  {
    var plain = JsonSerializer.SerializeToUtf8Bytes(session);
    try
    {
      Directory.CreateDirectory(Path.GetDirectoryName(PathName)!);
      File.WriteAllBytes(PathName + ".new", ProtectedData.Protect(plain, Entropy, DataProtectionScope.CurrentUser));
      File.Move(PathName + ".new", PathName, true);
    }
    finally { CryptographicOperations.ZeroMemory(plain); }
  }

  internal static string Base64Url(byte[] data) => Convert.ToBase64String(data).TrimEnd('=').Replace('+', '-').Replace('/', '_');
  internal static string Challenge(string verifier) => Base64Url(SHA256.HashData(Encoding.ASCII.GetBytes(verifier)));
  internal static string AuthorizationUrl(string redirect, string state, string verifier) =>
      "https://accounts.google.com/o/oauth2/v2/auth?" + string.Join("&", new Dictionary<string, string>
      {
        ["client_id"] = GoogleDriveOAuthConfiguration.ClientId,
        ["redirect_uri"] = redirect,
        ["response_type"] = "code",
        ["scope"] = GoogleDriveOAuthConfiguration.Scope,
        ["state"] = state,
        ["code_challenge"] = Challenge(verifier),
        ["code_challenge_method"] = "S256",
        ["access_type"] = "offline",
        ["prompt"] = "consent select_account",
      }.Select(pair => Uri.EscapeDataString(pair.Key) + "=" + Uri.EscapeDataString(pair.Value)));

  internal static Task ConnectAsync(string passphrase, CancellationToken cancellation) =>
      ConnectAsync(passphrase, cancellation, null);

  internal static async Task ConnectAsync(string passphrase, CancellationToken cancellation, Action<string>? onStage)
  {
    DesktopDriveSync.EnsureExclusiveInstance();
    _ = GoogleDriveOAuthConfiguration.ClientSecret(); // The distributed desktop app supplies this configuration.
    await TokenGate.WaitAsync(cancellation);
    var stage = "로그인 창 준비";
    var notifyChanged = false;
    try
    {
      onStage?.Invoke("Google 로그인 창 준비 중…");
      // Listen on loopback only. Never embed Google's account login in WebView2.
      using var reservation = new TcpListener(IPAddress.Loopback, 0);
      reservation.Start();
      var port = ((IPEndPoint)reservation.LocalEndpoint).Port;
      reservation.Stop();
      var redirect = $"http://127.0.0.1:{port}/";
      using var listener = new HttpListener();
      listener.Prefixes.Add(redirect);
      listener.Start();
      using var timeout = CancellationTokenSource.CreateLinkedTokenSource(cancellation);
      timeout.CancelAfter(TimeSpan.FromMinutes(3));
      using var stop = timeout.Token.Register(listener.Close);
      var verifier = Base64Url(RandomNumberGenerator.GetBytes(32));
      var state = Base64Url(RandomNumberGenerator.GetBytes(32));
      // Open a tabless standalone Edge app window, even if Omyaru is default.
      GoogleDriveLoginBrowser.Open(AuthorizationUrl(redirect, state, verifier));
      onStage?.Invoke("별도의 로그인 창에서 Google 계정을 승인해 주세요.\n승인을 마치면 자동으로 다음 단계로 넘어갑니다.");
      string? code = null;
      while (code is null)
      {
        HttpListenerContext context;
        try { context = await listener.GetContextAsync().WaitAsync(timeout.Token); }
        catch (Exception) when (timeout.IsCancellationRequested) { throw new OperationCanceledException(timeout.Token); }
        var valid = context.Request.HttpMethod == "GET" && context.Request.Url?.AbsolutePath == "/" &&
            context.Request.QueryString.GetValues("state") is { Length: 1 } states &&
            CryptographicOperations.FixedTimeEquals(Encoding.UTF8.GetBytes(states[0]), Encoding.UTF8.GetBytes(state));
        var oauthError = valid ? context.Request.QueryString["error"] : null;
        var html = !valid
            ? "Invalid authorization response."
            : oauthError is not null
            ? "<!doctype html><meta charset=\"utf-8\"><title>Omyaru</title><body style=\"font-family:Segoe UI,sans-serif;background:#181d23;color:#f5f5f7;display:flex;align-items:center;justify-content:center;height:100vh;margin:0\"><div style=\"text-align:center\"><h2>Google 권한 요청이 취소되었습니다</h2><p>이 창을 닫고 Omyaru로 돌아가세요.</p></div></body>"
            : "<!doctype html><meta charset=\"utf-8\"><title>Omyaru</title><body style=\"font-family:Segoe UI,sans-serif;background:#181d23;color:#f5f5f7;display:flex;align-items:center;justify-content:center;height:100vh;margin:0\"><div style=\"text-align:center\"><h2>Google 로그인 승인 완료</h2><p>Omyaru 앱으로 돌아가세요. 이 창은 잠시 후 닫힙니다.</p></div><script>setTimeout(function(){window.close();},1200);</script></body>";
        var response = Encoding.UTF8.GetBytes(html);
        context.Response.StatusCode = valid ? 200 : 400;
        context.Response.ContentType = valid ? "text/html; charset=utf-8" : "text/plain; charset=utf-8";
        context.Response.Headers["Cache-Control"] = "no-store";
        await context.Response.OutputStream.WriteAsync(response, timeout.Token);
        context.Response.Close();
        if (!valid) continue;
        if (oauthError is not null)
        {
          WriteDiagnostic(stage, "oauth_callback_error");
          throw new InvalidOperationException("Google 권한 요청이 취소되었습니다.");
        }
        if (context.Request.QueryString.GetValues("code") is not { Length: 1 } codes || string.IsNullOrWhiteSpace(codes[0]))
          throw new InvalidOperationException("Google 인증 응답이 올바르지 않습니다.");
        code = codes[0];
      }
      stage = "Google 인증 토큰 발급";
      onStage?.Invoke("Google 인증 승인 확인됨 · 로그인 토큰 발급 중…");
      using var result = await TokenRequest(new Dictionary<string, string>
      {
        ["client_id"] = GoogleDriveOAuthConfiguration.ClientId,
        ["grant_type"] = "authorization_code", ["code"] = code,
        ["code_verifier"] = verifier, ["redirect_uri"] = redirect,
      }, cancellation);
      var root = result.RootElement;
      var token = root.GetProperty("access_token").GetString()!;
      if (!root.TryGetProperty("refresh_token", out var refresh) || string.IsNullOrWhiteSpace(refresh.GetString()))
      {
        WriteDiagnostic(stage, "missing_refresh_token");
        throw new InvalidOperationException("자동 연결 권한을 받지 못했습니다. 다시 연결해 주세요.");
      }
      var drive = new DriveAppDataClient(Http, _ => Task.FromResult(token)) { AccountMode = true };
      stage = "기존 동기화 암호 확인";
      onStage?.Invoke("Google Drive 동기화 키 확인 중…");
      var ring = await DriveAccountKeys.ConnectAsync(drive, passphrase, cancellation, onStage);
      cancellation.ThrowIfCancellationRequested();
      stage = "PC 로그인 정보 보관";
      onStage?.Invoke("PC 로그인 정보 저장 중…");
      Save(new Session(refresh.GetString()!, "", true, ring.PrimaryKeyId));
      var expiresIn = root.TryGetProperty("expires_in", out var expEl) && expEl.TryGetInt32(out var secs) ? secs : 3600;
      _accessToken = token;
      _expires = DateTimeOffset.UtcNow.AddSeconds(Math.Max(60, expiresIn - 60));
      NeedsReauthentication = false;
      WriteDiagnostic("Connect", "OK");
      notifyChanged = true;
    }
    catch (HttpRequestException error)
    {
      WriteDiagnostic(stage, $"HttpRequestException({(int?)error.StatusCode ?? 0})");
      throw new InvalidOperationException(AuthorizationFailure(stage, error), error);
    }
    catch (CryptographicException error)
    {
      WriteDiagnostic(stage, "CryptographicException");
      throw new InvalidOperationException(AuthorizationFailure(stage, error), error);
    }
    catch (OperationCanceledException error) when (!cancellation.IsCancellationRequested && stage != "로그인 창 준비")
    {
      WriteDiagnostic(stage, "Timeout");
      throw new InvalidOperationException(stage + ": 응답 시간이 초과되었습니다. 다시 연결해 주세요.", error);
    }
    catch (Exception error) when (error is IOException or UnauthorizedAccessException or JsonException or KeyNotFoundException)
    {
      WriteDiagnostic(stage, error.GetType().Name);
      throw new InvalidOperationException(AuthorizationFailure(stage, error), error);
    }
    finally { TokenGate.Release(); }
    if (notifyChanged) SessionChanged?.Invoke();
  }

  internal static string AuthorizationFailure(string stage, Exception error)
  {
    // Only fixed text, a local stage label and numeric HTTP status reach the UI.
    // Never expose exception messages containing response bodies, tokens or URLs.
    var detail = error switch
    {
      HttpRequestException { StatusCode: HttpStatusCode.Forbidden } =>
        "Google Drive 접근이 거부되었습니다 (403). Drive API 활성화 여부와 앱의 Drive 권한을 확인해 주세요.",
      HttpRequestException { StatusCode: HttpStatusCode.Unauthorized } =>
        "Google 인증이 거부되었습니다 (401). 같은 계정으로 다시 연결해 주세요.",
      HttpRequestException { StatusCode: { } status } => $"Google 요청이 실패했습니다 (HTTP {(int)status}).",
      HttpRequestException => "Google 서버에 연결하지 못했습니다. 네트워크·프록시 연결을 확인해 주세요.",
      CryptographicException when stage == "기존 동기화 암호 확인" =>
        "기존 데이터를 열지 못했습니다. 모바일과 같은 동기화 암호인지 확인해 주세요. 데이터 손상 가능성도 있습니다.",
      CryptographicException => "Windows에서 로그인 정보를 안전하게 저장하지 못했습니다.",
      UnauthorizedAccessException or IOException => "파일에 접근하지 못했습니다. 시험판 프로필 폴더의 쓰기 권한을 확인해 주세요.",
      JsonException or KeyNotFoundException => "서버 응답 또는 기존 동기화 데이터의 형식이 올바르지 않습니다.",
      _ => "요청을 완료하지 못했습니다.",
    };
    return stage + ": " + detail;
  }

  internal static void InvalidateAccessToken()
  {
    _accessToken = null;
    _expires = default;
  }

  internal static async Task<string> AccessTokenAsync(CancellationToken cancellation)
  {
    await TokenGate.WaitAsync(cancellation);
    try
    {
      var session = Load() ?? throw new InvalidOperationException("Google Drive 연결이 필요합니다.");
      if (_accessToken is not null && _expires > DateTimeOffset.UtcNow) return _accessToken;
      using var result = await TokenRequest(new Dictionary<string, string>
      {
        ["client_id"] = GoogleDriveOAuthConfiguration.ClientId,
        ["grant_type"] = "refresh_token", ["refresh_token"] = session.RefreshToken,
      }, cancellation);
      var root = result.RootElement;
      if (root.TryGetProperty("refresh_token", out var rotated) && !string.IsNullOrWhiteSpace(rotated.GetString()))
        Save(session with { RefreshToken = rotated.GetString()! });
      var expiresIn = root.TryGetProperty("expires_in", out var expEl) && expEl.TryGetInt32(out var secs) ? secs : 3600;
      _accessToken = root.GetProperty("access_token").GetString()!;
      _expires = DateTimeOffset.UtcNow.AddSeconds(Math.Max(60, expiresIn - 60));
      NeedsReauthentication = false;
      return _accessToken;
    }
    finally { TokenGate.Release(); }
  }

  internal static async Task DisconnectAsync(CancellationToken cancellation)
  {
    DesktopDriveSync.EnsureExclusiveInstance();
    await TokenGate.WaitAsync(cancellation);
    try
    {
      File.Delete(PathName);
      _accessToken = null;
      _expires = default;
      NeedsReauthentication = false;
      WriteDiagnostic("Disconnect", "OK");
      // Token revocation can revoke the project's grant on other devices too.
      // Disconnect locally; users can revoke all access in their Google account.
    }
    finally { TokenGate.Release(); }
    SessionChanged?.Invoke();
  }

  private static async Task<JsonDocument> TokenRequest(Dictionary<string, string> values, CancellationToken cancellation)
  {
    values["client_secret"] = GoogleDriveOAuthConfiguration.ClientSecret();
    using var content = new FormUrlEncodedContent(values);
    using var response = await Http.PostAsync("https://oauth2.googleapis.com/token", content, cancellation);
    var json = await response.Content.ReadAsStringAsync(cancellation);
    if (!response.IsSuccessStatusCode)
    {
      var errorCode = ExtractOAuthErrorCode(json);
      var grantType = values.TryGetValue("grant_type", out var gt) ? gt : "unknown";
      WriteDiagnostic("TokenRequest:" + grantType, $"HTTP {(int)response.StatusCode} {(string.IsNullOrEmpty(errorCode) ? "no_error_code" : errorCode)}");
      if (grantType == "refresh_token" &&
          errorCode is "invalid_grant" or "unauthorized_client")
      {
        _accessToken = null;
        _expires = default;
        NeedsReauthentication = true;
        throw new GoogleDriveReauthenticationRequiredException(
            "Google 로그인 만료됨 · 다시 로그인해 주세요.");
      }
      if (errorCode == "invalid_grant")
        throw new InvalidOperationException("Google 인증 코드가 만료되었습니다. 다시 연결해 주세요.");
      if (errorCode == "invalid_client")
        throw new InvalidOperationException("Google 앱 클라이언트 인증 오류 (invalid_client). 최신 빌드를 확인해 주세요.");
      var suffix = string.IsNullOrEmpty(errorCode) ? string.Empty : $", {errorCode}";
      throw new InvalidOperationException($"Google 인증 실패 ({(int)response.StatusCode}{suffix}). 클라이언트 유형·테스트 사용자·앱 권한을 확인해 주세요.");
    }
    if (json.Length > 65536) throw new InvalidDataException("Google 인증 응답이 너무 큽니다.");
    return JsonDocument.Parse(json);
  }

  private static string ExtractOAuthErrorCode(string body)
  {
    if (string.IsNullOrWhiteSpace(body) || body.Length > 8192) return string.Empty;
    try
    {
      using var doc = JsonDocument.Parse(body);
      if (doc.RootElement.TryGetProperty("error", out var errorElement) &&
          errorElement.ValueKind == JsonValueKind.String)
      {
        var raw = errorElement.GetString() ?? string.Empty;
        return raw.All(c => char.IsAsciiLetterOrDigit(c) || c == '_') && raw.Length <= 48 ? raw : string.Empty;
      }
    }
    catch { }
    return string.Empty;
  }
}
