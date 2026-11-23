using System.Security.Cryptography;
using System.Text.Json;

namespace PrivacyBrowser.Desktop;

// Platform-neutral wire format. Encrypt each chunk with DriveAccountKeys before upload.
// Decode only into a staging file; commit it after this method verifies the full hash.
internal static class BookshelfImageChunks
{
  internal const int ChunkSize = 1024 * 1024;
  internal const long MaximumBytes = 512L * ChunkSize;
  internal sealed record Manifest(int Version, long Length, string Sha256, List<string> Chunks);
  private sealed record Chunk(int Version, int Index, string Sha256, string Data);
  private static readonly JsonSerializerOptions Json = new() { PropertyNamingPolicy = JsonNamingPolicy.CamelCase };
  internal static Manifest Encode(Stream input, Action<int, string> emit)
  {
    using var fullHash = IncrementalHash.CreateHash(HashAlgorithmName.SHA256);
    var hashes = new List<string>();
    var buffer = new byte[ChunkSize];
    long length = 0;
    for (;;)
    {
      var count = 0;
      while (count < buffer.Length)
      {
        var read = input.Read(buffer, count, buffer.Length - count);
        if (read == 0) break;
        count += read;
      }
      if (count == 0) break;
      length += count;
      if (length > MaximumBytes) throw new InvalidDataException("동기화 이미지가 512MB를 초과했습니다.");
      var bytes = buffer.AsSpan(0, count);
      fullHash.AppendData(bytes);
      var hash = Convert.ToHexString(SHA256.HashData(bytes)).ToLowerInvariant();
      emit(hashes.Count, JsonSerializer.Serialize(new Chunk(1, hashes.Count, hash, Convert.ToBase64String(bytes)), Json));
      hashes.Add(hash);
    }
    if (length == 0) throw new InvalidDataException("빈 이미지 파일입니다.");
    return new Manifest(1, length, Convert.ToHexString(fullHash.GetHashAndReset()).ToLowerInvariant(), hashes);
  }
  internal static void Decode(Manifest manifest, Func<int, string> read, Stream staging)
  {
    static bool HashValid(string? value) => value is { Length: 64 } && value.All(c => c is >= '0' and <= '9' or >= 'a' and <= 'f');
    if (manifest.Version != 1 || manifest.Length <= 0 || manifest.Length > MaximumBytes ||
        !HashValid(manifest.Sha256) || manifest.Chunks is null ||
        manifest.Chunks.Count != (manifest.Length + ChunkSize - 1) / ChunkSize || manifest.Chunks.Any(h => !HashValid(h)))
      throw new InvalidDataException("이미지 동기화 목록이 올바르지 않습니다.");
    using var fullHash = IncrementalHash.CreateHash(HashAlgorithmName.SHA256);
    for (var index = 0; index < manifest.Chunks.Count; index++)
    {
      var json = read(index);
      if (json.Length > 2 * ChunkSize) throw new InvalidDataException("이미지 조각이 너무 큽니다.");
      var chunk = JsonSerializer.Deserialize<Chunk>(json, Json) ?? throw new InvalidDataException("이미지 조각이 비어 있습니다.");
      if (chunk.Version != 1 || chunk.Index != index || chunk.Sha256 != manifest.Chunks[index] || chunk.Data is null)
        throw new InvalidDataException("이미지 조각 순서가 올바르지 않습니다.");
      var bytes = Convert.FromBase64String(chunk.Data);
      var expected = Math.Min(ChunkSize, manifest.Length - (long)index * ChunkSize);
      if (bytes.Length != expected || Convert.ToHexString(SHA256.HashData(bytes)).ToLowerInvariant() != chunk.Sha256)
        throw new InvalidDataException("이미지 조각이 손상되었습니다.");
      fullHash.AppendData(bytes);
      staging.Write(bytes);
    }
    if (Convert.ToHexString(fullHash.GetHashAndReset()).ToLowerInvariant() != manifest.Sha256)
      throw new InvalidDataException("원본 이미지 검증에 실패했습니다.");
  }
}
