using System.Security.Cryptography;
using System.Text.Json;
using System.Text.Json.Nodes;

namespace PrivacyBrowser.Desktop;

// Account-backed encryption, NOT zero-knowledge encryption: access to this
// Google app-data space grants access to these keys. Records are immutable.
// Concurrent first connections may create distinct keys; envelopes identify
// their key and readers retain every key rather than overwriting a singleton.
internal sealed class DriveAccountKeys
{
  internal const string MigrationRequired = "이전 동기화 암호를 한 번 입력해 기존 데이터를 연결해 주세요.";
  private readonly SortedDictionary<string, string> _keys = new(StringComparer.Ordinal);
  private readonly HashSet<string> _legacy = new(StringComparer.Ordinal);
  internal string? PrimaryKeyId => _keys.Keys.FirstOrDefault();
  internal void Add(string expectedId, string json)
  {
    using var doc = JsonDocument.Parse(json);
    var root = doc.RootElement;
    if (root.GetProperty("format").GetString() != "omyaru-account-key" ||
        root.GetProperty("version").GetInt32() != 1 ||
        root.GetProperty("id").GetString() != expectedId ||
        !Guid.TryParseExact(expectedId, "N", out var id) || id == Guid.Empty)
      throw new InvalidDataException("계정 동기화 키 형식이 올바르지 않습니다.");
    var secret = root.GetProperty("secret").GetString()!;
    if (Convert.FromBase64String(secret).Length != 32)
      throw new InvalidDataException("계정 동기화 키 길이가 올바르지 않습니다.");
    if (_keys.TryGetValue(expectedId, out var previous) && previous != secret)
      throw new InvalidDataException("계정 동기화 키가 충돌합니다. 기존 데이터는 유지됩니다.");
    string? legacy = null;
    if (root.TryGetProperty("legacyEnvelope", out var wrapped) && wrapped.ValueKind == JsonValueKind.String)
    {
      using var plain = JsonDocument.Parse(SyncCodec.DecryptJson(wrapped.GetString()!, secret));
      legacy = plain.RootElement.GetProperty("passphrase").GetString();
      if (legacy is null || legacy.Length < 6) throw new InvalidDataException("기존 암호 이관 정보가 올바르지 않습니다.");
    }
    _keys[expectedId] = secret;
    if (legacy is not null) _legacy.Add(legacy);
  }
  internal static string Record(string id, string secret, string? legacy) => JsonSerializer.Serialize(new
  {
    format = "omyaru-account-key", version = 1, id, secret,
    legacyEnvelope = legacy is null ? null : SyncCodec.EncryptJson(JsonSerializer.Serialize(new { passphrase = legacy }), secret),
  });
  internal string Encrypt(string plain)
  {
    var key = _keys.First();
    var envelope = JsonNode.Parse(SyncCodec.EncryptJson(plain, key.Value))!.AsObject();
    envelope["accountKeyId"] = key.Key;
    return envelope.ToJsonString();
  }
  internal string Decrypt(string envelope)
  {
    using var doc = JsonDocument.Parse(envelope);
    if (doc.RootElement.TryGetProperty("accountKeyId", out var id))
    {
      if (!_keys.TryGetValue(id.GetString()!, out var secret))
        throw new InvalidDataException("이 데이터의 계정 키를 찾지 못했습니다. 다시 연결해 주세요. 기존 데이터는 유지됩니다.");
      return SyncCodec.DecryptJson(envelope, secret);
    }
    foreach (var legacy in _legacy)
    {
      try { return SyncCodec.DecryptJson(envelope, legacy); }
      catch (CryptographicException) { }
    }
    throw new InvalidOperationException(MigrationRequired);
  }
  internal static async Task<DriveAccountKeys> LoadAsync(DriveAppDataClient client, CancellationToken cancellation)
  {
    var ring = new DriveAccountKeys();
    var records = await client.ListKeysAsync(cancellation);
    if (records.Count > 128) throw new InvalidDataException("계정 키 기록이 너무 많습니다. 기존 데이터는 유지됩니다.");
    foreach (var record in records)
      ring.Add(record.Name[DriveAppDataClient.KeyPrefix.Length..^5], await client.ReadAsync(record, cancellation));
    return ring;
  }
  internal static async Task<DriveAccountKeys> ConnectAsync(
      DriveAppDataClient client,
      string legacyPassword,
      CancellationToken cancellation,
      Action<string>? onProgress = null)
  {
    onProgress?.Invoke("계정 동기화 키 확인 중…");
    var ring = await LoadAsync(client, cancellation);
    // Re-authenticating or adding a new device on an account that already has account keys
    // does not need to re-download and PBKDF2-decrypt every historical tab batch during login.
    if (ring._keys.Count > 0 && string.IsNullOrEmpty(legacyPassword))
      return ring;

    var files = await client.ListAsync(cancellation);
    var legacyFiles = files.Where(f => f.Name.StartsWith(DriveAppDataClient.Prefix, StringComparison.Ordinal)).ToList();
    if (legacyFiles.Count > 5000) throw new InvalidDataException("기존 동기화 기록이 너무 많습니다. 기존 데이터는 유지됩니다.");
    var needsLegacy = false;
    // Validate existing legacy passphrase-encrypted records before publishing any migration key.
    for (var i = 0; i < legacyFiles.Count; i++)
    {
      if (i == 0 || (i + 1) % 10 == 0 || i + 1 == legacyFiles.Count)
        onProgress?.Invoke($"기존 동기화 기록 확인 중 ({i + 1}/{legacyFiles.Count})…");
      var envelope = await client.ReadAsync(legacyFiles[i], cancellation);
      try { ring.Decrypt(envelope); }
      catch (InvalidOperationException error) when (error.Message == MigrationRequired)
      {
        if (legacyPassword.Length < 6) throw;
        SyncCodec.DecryptJson(envelope, legacyPassword);
        needsLegacy = true;
      }
    }
    if (ring._keys.Count == 0 || needsLegacy)
    {
      onProgress?.Invoke("계정 동기화 키 생성 중…");
      var id = Guid.NewGuid();
      var secret = Convert.ToBase64String(RandomNumberGenerator.GetBytes(32));
      var record = Record(id.ToString("N"), secret, needsLegacy ? legacyPassword : null);
      var fileId = await client.AppendKeyAsync(id, record, cancellation);
      var stored = await client.ReadAsync(new(fileId, DriveAppDataClient.KeyPrefix + id.ToString("N") + ".json", null), cancellation);
      // Exact read-back verification before saving a connected session.
      if (stored != record) throw new InvalidDataException("계정 키 저장 검증에 실패했습니다. 기존 데이터는 유지됩니다.");
      ring.Add(id.ToString("N"), stored);
    }
    return ring;
  }
}
