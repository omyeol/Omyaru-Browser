using System.Security.Cryptography;
using System.Text;
using System.Text.Json;

namespace PrivacyBrowser.Desktop;

internal static class GoogleDriveOAuthConfiguration
{
#if BOOKSHELF_STANDALONE
  private static string StandaloneClientPath => Path.Combine(
      Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "OmyaruBookshelf", "google-drive-client.dat");
  private static string BrowserClientPath => Path.Combine(
      Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "Omyaru Browser", "google-drive-client.dat");
  private static string LegacyBrowserClientPath => Path.Combine(
      Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "PrivacyBrowser", "google-drive-client.dat");
#endif
  // Public Desktop OAuth client identifier, not a credential or user token.
  // The Android client must be registered separately in the same Cloud project.
  internal const string ClientId =
      "136563471424-6phesors0pqgjnpsk3lnb8tr49dh9kvo.apps.googleusercontent.com";
  internal const string Scope = DriveAppDataClient.Scope;
  internal static string ClientSecret()
  {
    using var bundled = typeof(GoogleDriveOAuthConfiguration).Assembly.GetManifestResourceStream("Omyaru.GoogleDriveClient.json");
    if (bundled is not null)
    {
      using var document = JsonDocument.Parse(bundled);
      var client = document.RootElement.GetProperty("installed");
      if (client.GetProperty("client_id").GetString() != ClientId)
        throw new InvalidDataException("배포용 Google 클라이언트 설정이 일치하지 않습니다.");
      var secret = client.GetProperty("client_secret").GetString();
      if (string.IsNullOrWhiteSpace(secret)) throw new InvalidDataException("배포용 Google 클라이언트 설정이 비어 있습니다.");
      return secret;
    }
    var paths = new List<string>();
#if BOOKSHELF_STANDALONE
    paths.Add(StandaloneClientPath);
#endif
    paths.Add(Path.Combine(BrowserStateStore.StateDirectory, "google-drive-client.dat"));
#if BOOKSHELF_STANDALONE
    paths.Add(BrowserClientPath);
    paths.Add(LegacyBrowserClientPath);
#endif
    var found = false;
    foreach (var path in paths.Distinct(StringComparer.OrdinalIgnoreCase))
    {
      if (!File.Exists(path)) continue;
      found = true;
      byte[]? plain = null;
      try
      {
        plain = ProtectedData.Unprotect(File.ReadAllBytes(path), Encoding.UTF8.GetBytes("omyaru-google-client-v1"), DataProtectionScope.CurrentUser);
        var secret = Encoding.UTF8.GetString(plain);
        if (!string.IsNullOrWhiteSpace(secret)) return secret;
      }
      catch (Exception error) when (error is CryptographicException or IOException or UnauthorizedAccessException) { }
      finally { if (plain is not null) CryptographicOperations.ZeroMemory(plain); }
    }
    throw new InvalidOperationException(found
        ? "Google 연결 정보를 읽지 못했습니다. 최신 버전의 앱을 다시 설치해 주세요."
        : "Google 연결 정보가 앱에 포함되지 않았습니다. 최신 버전의 앱을 다시 설치해 주세요.");
  }
  internal static bool HasBundledClient => typeof(GoogleDriveOAuthConfiguration).Assembly.GetManifestResourceNames().Contains("Omyaru.GoogleDriveClient.json");
}
