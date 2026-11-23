using System.Net;
using System.Net.Http.Headers;
using System.Text;
using System.Text.Json;

namespace PrivacyBrowser.Desktop;

// Transport only: callers supply a short-lived OAuth token and already encrypted data.
// There is deliberately no update/delete operation for a shared mutable snapshot.
internal sealed class DriveAppDataClient(HttpClient http, Func<CancellationToken, Task<string>> accessToken)
{
  internal const string Scope = "https://www.googleapis.com/auth/drive.appdata";
  internal const string Prefix = "omyaru-tabs-v1-";
  internal const string AccountPrefix = "omyaru-tabs-v2-";
  internal const string KeyPrefix = "omyaru-account-key-v1-";
  internal const string ResetPrefix = "omyaru-tab-reset-v1-";
  internal const string ShelfPrefix = "omyaru-shelf-v1-";
  internal const string ShelfChunkPrefix = "omyaru-shelf-chunk-v1-";
  internal const string UserDataPrefix = "omyaru-userdata-v1-";
  internal bool AccountMode { get; init; }
  internal const int MaximumBytes = 4 * 1024 * 1024;
  private const string Api = "https://www.googleapis.com/drive/v3/files";
  internal sealed record Entry(string Id, string Name, string? Md5Checksum);

  internal Task<IReadOnlyList<Entry>> ListAsync(CancellationToken cancellation) =>
      ListNamedAsync(AccountMode ? "omyaru-tabs-v" : Prefix, false, cancellation);
  internal Task<IReadOnlyList<Entry>> ListKeysAsync(CancellationToken cancellation) =>
      ListNamedAsync(KeyPrefix, true, cancellation);
  internal Task<IReadOnlyList<Entry>> ListResetsAsync(CancellationToken cancellation) =>
      ListNamedAsync(ResetPrefix, false, cancellation);
  internal Task<IReadOnlyList<Entry>> ListShelfAsync(CancellationToken cancellation) => ListNamedAsync(ShelfPrefix, false, cancellation);
  internal Task<IReadOnlyList<Entry>> ListUserDataAsync(CancellationToken cancellation) => ListNamedAsync(UserDataPrefix, false, cancellation);
  private async Task<IReadOnlyList<Entry>> ListNamedAsync(string prefix, bool keys, CancellationToken cancellation)
  {
    var entries = new List<Entry>();
    var seenPages = new HashSet<string>();
    string? page = null;
    do
    {
      var query = Uri.EscapeDataString($"trashed = false and name contains '{prefix}'");
      var url = $"{Api}?spaces=appDataFolder&pageSize=1000&q={query}&fields=nextPageToken,files(id,name,md5Checksum)";
      if (page is not null) url += "&pageToken=" + Uri.EscapeDataString(page);
      using var request = new HttpRequestMessage(HttpMethod.Get, url);
      using var json = JsonDocument.Parse(await SendAsync(request, cancellation));
      foreach (var file in json.RootElement.GetProperty("files").EnumerateArray())
      {
        var name = file.GetProperty("name").GetString()!;
        if (!(prefix == ShelfPrefix ? ValidPrefixed(name, ShelfPrefix) : prefix == UserDataPrefix ? ValidUserDataName(name) : prefix == ResetPrefix ? ValidResetName(name) : keys ? ValidKeyName(name) : ValidName(name) &&
            (AccountMode || name.StartsWith(Prefix, StringComparison.Ordinal)))) continue;
        entries.Add(new Entry(file.GetProperty("id").GetString()!, name,
            file.TryGetProperty("md5Checksum", out var checksum) ? checksum.GetString() : null));
      }
      page = json.RootElement.TryGetProperty("nextPageToken", out var next) ? next.GetString() : null;
      if (page is not null && !seenPages.Add(page)) throw new InvalidDataException("Drive 페이지가 반복됩니다.");
    } while (!string.IsNullOrEmpty(page));
    return entries;
  }

  internal async Task<string> ReadAsync(Entry entry, CancellationToken cancellation)
  {
    if (!ValidName(entry.Name) && !ValidKeyName(entry.Name) && !ValidResetName(entry.Name) && !ValidShelfName(entry.Name) && !ValidUserDataName(entry.Name)) throw new ArgumentException("Omyaru 동기화 파일이 아닙니다.");
    using var request = new HttpRequestMessage(HttpMethod.Get, Api + "/" + Uri.EscapeDataString(entry.Id) + "?alt=media");
    return await SendAsync(request, cancellation);
  }

  internal async Task DeleteCoveredAsync(Entry entry, CancellationToken cancellation)
  {
    if (!ValidName(entry.Name)) throw new ArgumentException("Omyaru 동기화 파일이 아닙니다.");
    using var request = new HttpRequestMessage(HttpMethod.Delete, Api + "/" + Uri.EscapeDataString(entry.Id));
    try { await SendAsync(request, cancellation); }
    catch (HttpRequestException error) when (error.StatusCode == HttpStatusCode.NotFound) { }
  }

  // Retry with the SAME operation id. Drive can contain duplicate names after an
  // uncertain response; the merge layer must deduplicate by encrypted operation id.
  internal async Task<string> AppendAsync(Guid operationId, string encryptedPayload, CancellationToken cancellation)
    => await AppendNamedAsync((AccountMode ? AccountPrefix : Prefix) + operationId.ToString("N") + ".json", encryptedPayload, cancellation);

  internal Task<string> AppendKeyAsync(Guid keyId, string payload, CancellationToken cancellation) =>
      AppendNamedAsync(KeyPrefix + keyId.ToString("N") + ".json", payload, cancellation);
  internal Task<string> AppendResetAsync(Guid id, string payload, CancellationToken cancellation) =>
      AppendNamedAsync(ResetPrefix + id.ToString("N") + ".json", payload, cancellation);
  internal Task<string> AppendShelfAsync(Guid id, string payload, CancellationToken cancellation) =>
      AppendNamedAsync(ShelfPrefix + id.ToString("N") + ".json", payload, cancellation);
  internal Task<string> AppendShelfChunkAsync(Guid id, string payload, CancellationToken cancellation) =>
      AppendNamedAsync(ShelfChunkPrefix + id.ToString("N") + ".json", payload, cancellation);
  internal Task<string> AppendUserDataAsync(Guid id, string payload, CancellationToken cancellation) =>
      AppendNamedAsync(UserDataPrefix + id.ToString("N") + ".json", payload, cancellation);

  private async Task<string> AppendNamedAsync(string name, string encryptedPayload, CancellationToken cancellation)
  {
    if (!ValidName(name) && !ValidKeyName(name) && !ValidResetName(name) && !ValidShelfName(name) && !ValidUserDataName(name)) throw new ArgumentException("변경 기록 ID가 필요합니다.");
    if (Encoding.UTF8.GetByteCount(encryptedPayload) > MaximumBytes) throw new InvalidDataException("변경 기록이 너무 큽니다.");
    using var body = new MultipartContent("related");
    body.Add(new StringContent(JsonSerializer.Serialize(new
    {
      name,
      parents = new[] { "appDataFolder" },
      mimeType = "application/json",
    }), Encoding.UTF8, "application/json"));
    body.Add(new StringContent(encryptedPayload, Encoding.UTF8, "application/json"));
    using var request = new HttpRequestMessage(HttpMethod.Post,
        "https://www.googleapis.com/upload/drive/v3/files?uploadType=multipart&fields=id") { Content = body };
    using var json = JsonDocument.Parse(await SendAsync(request, cancellation));
    return json.RootElement.GetProperty("id").GetString()!;
  }

  internal static bool ValidName(string name) => ValidPrefixed(name, Prefix) || ValidPrefixed(name, AccountPrefix);
  internal static bool ValidKeyName(string name) => ValidPrefixed(name, KeyPrefix);
  internal static bool ValidResetName(string name) => ValidPrefixed(name, ResetPrefix);
  internal static bool ValidShelfName(string name) => ValidPrefixed(name, ShelfPrefix) || ValidPrefixed(name, ShelfChunkPrefix);
  internal static bool ValidUserDataName(string name) => ValidPrefixed(name, UserDataPrefix);
  private static bool ValidPrefixed(string name, string prefix) => name.StartsWith(prefix, StringComparison.Ordinal) &&
      name.EndsWith(".json", StringComparison.Ordinal) &&
      Guid.TryParseExact(name[prefix.Length..^5], "N", out var id) && id != Guid.Empty;

  private async Task<string> SendAsync(HttpRequestMessage request, CancellationToken cancellation)
  {
    request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", await accessToken(cancellation));
    using var response = await http.SendAsync(request, HttpCompletionOption.ResponseHeadersRead, cancellation);
    if (!response.IsSuccessStatusCode)
    {
      if (response.StatusCode == HttpStatusCode.Unauthorized)
        GoogleDriveAuthorization.InvalidateAccessToken();
      // Never display Google's response body: it may contain account/token details.
      throw new HttpRequestException(response.StatusCode switch
      {
        HttpStatusCode.Unauthorized => "Google 연결을 다시 인증해야 합니다.",
        HttpStatusCode.Forbidden => "Google Drive 권한 또는 사용 한도를 확인해 주세요.",
        HttpStatusCode.TooManyRequests => "Google Drive 요청 한도에 도달했습니다. 잠시 후 재시도합니다.",
        _ => $"Google Drive 요청 실패 ({(int)response.StatusCode}).",
      }, null, response.StatusCode);
    }
    if (response.Content.Headers.ContentLength > MaximumBytes) throw new InvalidDataException("Drive 응답이 너무 큽니다.");
    using var stream = await response.Content.ReadAsStreamAsync(cancellation);
    using var output = new MemoryStream();
    var buffer = new byte[8192];
    int read;
    while ((read = await stream.ReadAsync(buffer, cancellation)) != 0)
    {
      if (output.Length + read > MaximumBytes) throw new InvalidDataException("Drive 응답이 너무 큽니다.");
      output.Write(buffer, 0, read);
    }
    return Encoding.UTF8.GetString(output.ToArray());
  }
}
