using System.Security.Cryptography;
using System.Text;
using System.Text.Json;

namespace PrivacyBrowser.Desktop;

internal static class SyncCodec
{
  private const string Format = "privacy-browser-sync";
  private const int Version = 1;
  private const int Iterations = 210_000;
  private const int MaximumEncryptedBytes = 32 * 1024 * 1024;
  private static readonly byte[] Aad = Encoding.UTF8.GetBytes($"{Format}-v{Version}");
  private static readonly JsonSerializerOptions JsonOptions = new()
  {
    PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
    WriteIndented = false,
  };

  internal static void Write(string path, SyncSnapshot snapshot, string passphrase)
  {
    var temporaryPath = path + ".new";
    File.WriteAllText(temporaryPath, Encrypt(snapshot, passphrase), new UTF8Encoding(false));
    File.Move(temporaryPath, path, true);
  }

  internal static string Encrypt(SyncSnapshot snapshot, string passphrase)
  {
    if (passphrase.Length < 6) throw new ArgumentException("동기화 암호는 6자 이상이어야 합니다.");
    var normalized = snapshot.Copy();
    normalized.Normalize();
    normalized.SchemaVersion = 2;
    return EncryptJson(JsonSerializer.Serialize(normalized, JsonOptions), passphrase);
  }

  internal static string EncryptJson(string json, string passphrase)
  {
    if (passphrase.Length < 6) throw new ArgumentException("동기화 암호는 6자 이상이어야 합니다.");
    var plaintext = Encoding.UTF8.GetBytes(json);
    if (plaintext.Length > MaximumEncryptedBytes - 16) throw new InvalidDataException("동기화 데이터가 너무 큽니다.");
    var salt = RandomNumberGenerator.GetBytes(16);
    var nonce = RandomNumberGenerator.GetBytes(12);
    var key = Rfc2898DeriveBytes.Pbkdf2(passphrase, salt, Iterations, HashAlgorithmName.SHA256, 32);
    var ciphertext = new byte[plaintext.Length];
    var tag = new byte[16];
    try
    {
      using var aes = new AesGcm(key, tag.Length);
      aes.Encrypt(nonce, plaintext, ciphertext, tag, Aad);
    }
    finally
    {
      CryptographicOperations.ZeroMemory(key);
      CryptographicOperations.ZeroMemory(plaintext);
    }
    var combined = new byte[ciphertext.Length + tag.Length];
    Buffer.BlockCopy(ciphertext, 0, combined, 0, ciphertext.Length);
    Buffer.BlockCopy(tag, 0, combined, ciphertext.Length, tag.Length);
    var envelope = new
    {
      format = Format,
      version = Version,
      kdf = new { name = "pbkdf2-sha256", iterations = Iterations, salt = Convert.ToBase64String(salt) },
      cipher = new { name = "aes-256-gcm", nonce = Convert.ToBase64String(nonce), ciphertext = Convert.ToBase64String(combined) },
    };
    return JsonSerializer.Serialize(envelope, JsonOptions);
  }

  internal static SyncSnapshot Read(string path, string passphrase)
  {
    if (passphrase.Length < 6) throw new ArgumentException("동기화 암호는 6자 이상이어야 합니다.");
    var file = new FileInfo(path);
    if (!file.Exists || file.Length > 48L * 1024 * 1024) throw new InvalidDataException("올바른 동기화 파일이 아닙니다.");
    return Decrypt(File.ReadAllText(path), passphrase);
  }

  internal static SyncSnapshot Decrypt(string envelope, string passphrase)
  {
    var snapshot = JsonSerializer.Deserialize<SyncSnapshot>(DecryptJson(envelope, passphrase), JsonOptions)
        ?? throw new InvalidDataException("동기화 데이터가 비어 있습니다.");
    if (snapshot.SchemaVersion is < 1 or > 2) throw new InvalidDataException("지원하지 않는 데이터 버전입니다.");
    snapshot.Normalize();
    return snapshot;
  }

  internal static string DecryptJson(string envelope, string passphrase)
  {
    if (passphrase.Length < 6) throw new ArgumentException("동기화 암호는 6자 이상이어야 합니다.");
    if (Encoding.UTF8.GetByteCount(envelope) > 48L * 1024 * 1024)
      throw new InvalidDataException("동기화 데이터가 너무 큽니다.");
    using var document = JsonDocument.Parse(envelope);
    var root = document.RootElement;
    if (root.GetProperty("format").GetString() != Format || root.GetProperty("version").GetInt32() != Version)
      throw new InvalidDataException("지원하지 않는 동기화 파일입니다.");
    var kdf = root.GetProperty("kdf");
    var cipherDocument = root.GetProperty("cipher");
    if (kdf.GetProperty("name").GetString() != "pbkdf2-sha256" ||
        cipherDocument.GetProperty("name").GetString() != "aes-256-gcm")
      throw new InvalidDataException("지원하지 않는 암호화 방식입니다.");
    var iterations = kdf.GetProperty("iterations").GetInt32();
    if (iterations is < 100_000 or > 1_000_000) throw new InvalidDataException("안전하지 않은 키 설정입니다.");
    var salt = Convert.FromBase64String(kdf.GetProperty("salt").GetString()!);
    var nonce = Convert.FromBase64String(cipherDocument.GetProperty("nonce").GetString()!);
    var combined = Convert.FromBase64String(cipherDocument.GetProperty("ciphertext").GetString()!);
    if (salt.Length != 16 || nonce.Length != 12 || combined.Length is < 16 or > MaximumEncryptedBytes)
      throw new InvalidDataException("손상된 동기화 파일입니다.");
    var ciphertextLength = combined.Length - 16;
    var ciphertext = combined.AsSpan(0, ciphertextLength);
    var tag = combined.AsSpan(ciphertextLength, 16);
    var plaintext = new byte[ciphertextLength];
    var key = Rfc2898DeriveBytes.Pbkdf2(passphrase, salt, iterations, HashAlgorithmName.SHA256, 32);
    try
    {
      using var aes = new AesGcm(key, tag.Length);
      aes.Decrypt(nonce, ciphertext, tag, plaintext, Aad);
      return Encoding.UTF8.GetString(plaintext);
    }
    finally
    {
      CryptographicOperations.ZeroMemory(key);
      CryptographicOperations.ZeroMemory(plaintext);
    }
  }
}
