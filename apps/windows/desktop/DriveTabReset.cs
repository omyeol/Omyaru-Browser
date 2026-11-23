using System.Text.Json;

namespace PrivacyBrowser.Desktop;
internal static class DriveTabReset
{
  internal const string Confirmation = "동기화 탭 초기화";
  internal static void ValidateConfirmation(string value)
  {
    if (!string.Equals(value, Confirmation, StringComparison.Ordinal))
      throw new InvalidOperationException("‘동기화 탭 초기화’를 정확히 입력해야 합니다.");
  }
  internal static async Task<HashSet<string>> ReadAsync(DriveAppDataClient client, DriveAccountKeys keys, CancellationToken token)
  {
    var entries = await client.ListResetsAsync(token);
    if (entries.Count > 1000) throw new InvalidDataException("탭 초기화 기록이 너무 많습니다.");
    var ids = new HashSet<string>(StringComparer.Ordinal);
    foreach (var entry in entries)
    {
      var id = entry.Name[DriveAppDataClient.ResetPrefix.Length..^5];
      using var doc = JsonDocument.Parse(keys.Decrypt(await client.ReadAsync(entry, token)));
      var root = doc.RootElement;
      if (root.GetProperty("format").GetString() != "omyaru-tab-reset" || root.GetProperty("version").GetInt32() != 1 ||
          root.GetProperty("id").GetString() != id) throw new InvalidDataException("탭 초기화 기록이 올바르지 않습니다.");
      ids.Add(id);
    }
    return ids;
  }
  internal static async Task PublishAsync(string confirmation, DriveAppDataClient client, DriveAccountKeys keys, CancellationToken token)
  {
    ValidateConfirmation(confirmation);
    var id = Guid.NewGuid();
    var plain = JsonSerializer.Serialize(new { format = "omyaru-tab-reset", version = 1, id = id.ToString("N") });
    var file = await client.AppendResetAsync(id, keys.Encrypt(plain), token);
    var stored = keys.Decrypt(await client.ReadAsync(new(file, DriveAppDataClient.ResetPrefix + id.ToString("N") + ".json", null), token));
    if (stored != plain) throw new InvalidDataException("초기화 기록 확인에 실패했습니다. 연결을 복구한 뒤 상태를 확인해 주세요.");
  }
}
