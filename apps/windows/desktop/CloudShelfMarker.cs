using System.Drawing.Imaging;
using System.Security.Cryptography;
using System.Text.Json;
using System.Text.Json.Nodes;

namespace PrivacyBrowser.Desktop;

// Tiny on-disk reference to an encrypted Drive original. No account token or key is stored here.
internal static class CloudShelfMarker
{
  private const string Kind = "omyaru-cloud-image-v1";
  private const long ThumbnailLimit = 96L * 1024 * 1024;
#if !SYNC_BRIDGE
  internal static bool CheckCurrentAccountReadOnly()
  {
    if (GoogleDriveAuthorization.Load()?.AccountKeys != true) return false;
    var catalog = BookshelfStore.Load();
    var syncRoot = Path.Combine(BookshelfStore.StorageDirectory, ".sync");
    if (!Directory.Exists(syncRoot)) return false;
    foreach (var state in Directory.EnumerateDirectories(syncRoot))
    {
      var cachePath = Path.Combine(state, "images.json");
      if (!File.Exists(cachePath)) continue;
      var cache = JsonNode.Parse(File.ReadAllText(cachePath))?.AsObject();
      if (cache is null) continue;
      if (catalog.Items.Count == 0 || catalog.Items.Any(item => cache[item.Id]?["manifest"] is not JsonObject)) continue;
      var checkedImages = 0;
      foreach (var item in catalog.Items)
      {
        if (!File.Exists(item.FilePath) || cache[item.Id]?["manifest"] is not JsonObject manifest) return false;
        var expected = manifest["sha256"]?.GetValue<string>();
        if (expected is null) return false;
        using (var original = File.OpenRead(item.FilePath))
          if (!Convert.ToHexString(SHA256.HashData(original)).Equals(expected, StringComparison.OrdinalIgnoreCase)) return false;
        var markerPath = Path.Combine(Path.GetTempPath(), "omyaru-cloud-check-" + Guid.NewGuid().ToString("N") + ".cloud");
        try
        {
          Write(markerPath, item.Id, manifest);
          using var image = LoadImage(item with { FilePath = markerPath });
          if (image is null || image.Width != item.Width || image.Height != item.Height) return false;
          checkedImages++;
        }
        finally { if (File.Exists(markerPath)) File.Delete(markerPath); }
      }
      return checkedImages == catalog.Items.Count;
    }
    return false;
  }
#endif
  internal static JsonObject? Read(string path)
  {
    try
    {
      var info = new FileInfo(path);
      if (!info.Exists || info.Length is < 32 or > 1_048_576) return null;
      var marker = JsonNode.Parse(File.ReadAllText(path))?.AsObject();
      var id = marker?["id"]?.GetValue<string>();
      var hash = marker?["manifest"]?["sha256"]?.GetValue<string>();
      return marker?["kind"]?.GetValue<string>() == Kind && Guid.TryParseExact(id, "N", out _) &&
          hash is { Length: 64 } && hash.All(Uri.IsHexDigit) ? marker : null;
    }
    catch { return null; }
  }

  internal static void Write(string path, string id, JsonObject manifest)
  {
    if (!Guid.TryParseExact(id, "N", out _) || manifest["sha256"]?.GetValue<string>() is not { Length: 64 } hash ||
        !hash.All(Uri.IsHexDigit)) throw new InvalidDataException("책장 클라우드 참조가 올바르지 않습니다.");
    Directory.CreateDirectory(Path.GetDirectoryName(path)!);
    var marker = new JsonObject { ["kind"] = Kind, ["id"] = id, ["manifest"] = manifest.DeepClone() };
    if (File.Exists(path))
    {
      if (!JsonNode.DeepEquals(Read(path)?["manifest"], manifest)) throw new InvalidDataException("책장 클라우드 참조가 충돌합니다.");
      return;
    }
    var temporary = path + "." + Guid.NewGuid().ToString("N") + ".partial";
    try
    {
      using (var output = new FileStream(temporary, FileMode.CreateNew, FileAccess.Write, FileShare.None))
      using (var writer = new StreamWriter(output)) { writer.Write(marker.ToJsonString()); writer.Flush(); output.Flush(true); }
      File.Move(temporary, path);
    }
    finally { if (File.Exists(temporary)) File.Delete(temporary); }
  }

#if !SYNC_BRIDGE
  internal static Image? LoadImage(BookshelfItem item)
  {
    var marker = Read(item.FilePath);
    if (marker is null) return null;
    var temporary = Path.Combine(Path.GetTempPath(), "omyaru-shelf-" + Guid.NewGuid().ToString("N") + ".image");
    try
    {
      using var stream = new FileStream(temporary, FileMode.CreateNew, FileAccess.ReadWrite, FileShare.Read,
          65536, FileOptions.DeleteOnClose);
      Download(marker, stream);
      stream.Position = 0;
      using var source = Image.FromStream(stream);
      return new Bitmap(source);
    }
    catch { return null; }
    finally { if (File.Exists(temporary)) File.Delete(temporary); }
  }

  internal static Image? LoadThumbnail(BookshelfItem item, int width, int height)
  {
    var marker = Read(item.FilePath);
    if (marker is null) return null;
    var hash = marker["manifest"]!["sha256"]!.GetValue<string>();
    var cache = Path.Combine(BrowserStateStore.StateDirectory, "bookshelf-thumbnails");
    Directory.CreateDirectory(cache);
    var targetWidth = Math.Clamp(width, 1, 4096);
    var cached = Path.Combine(cache, $"{item.Id}-{hash}-{targetWidth}.png");
    if (File.Exists(cached))
    {
      try { using var image = Image.FromFile(cached); File.SetLastWriteTimeUtc(cached, DateTime.UtcNow); return new Bitmap(image); }
      catch { try { File.Delete(cached); } catch { } }
    }
    using var source = LoadImage(item);
    if (source is null) return null;
    var targetHeight = Math.Max(1, (int)Math.Round(targetWidth * source.Height / (double)Math.Max(1, source.Width)));
    var thumbnail = new Bitmap(targetWidth, targetHeight, PixelFormat.Format32bppPArgb);
    using (var graphics = Graphics.FromImage(thumbnail))
    {
      graphics.InterpolationMode = System.Drawing.Drawing2D.InterpolationMode.HighQualityBicubic;
      graphics.DrawImage(source, 0, 0, targetWidth, targetHeight);
    }
    var temporary = cached + "." + Guid.NewGuid().ToString("N") + ".partial";
    try { thumbnail.Save(temporary, ImageFormat.Png); if (!File.Exists(cached)) File.Move(temporary, cached); Trim(cache); }
    catch { }
    finally { if (File.Exists(temporary)) File.Delete(temporary); }
    return thumbnail;
  }

  internal static int RetireVerifiedOriginals(IReadOnlyList<DriveBookshelfSync.ImageSource> previous,
      BookshelfCatalog current, JsonObject snapshot, string storageDirectory)
  {
    var currentPaths = current.Items.ToDictionary(item => item.Id, item => Path.GetFullPath(item.FilePath), StringComparer.Ordinal);
    var retained = currentPaths.Values.ToHashSet(StringComparer.OrdinalIgnoreCase);
    var hashes = snapshot["images"]!.AsArray().ToDictionary(row => row!["id"]!.GetValue<string>(),
        row => row!["manifest"]!["sha256"]!.GetValue<string>(), StringComparer.Ordinal);
    var root = Path.GetFullPath(storageDirectory).TrimEnd(Path.DirectorySeparatorChar) + Path.DirectorySeparatorChar;
    var removed = 0;
    foreach (var source in previous)
    {
      if (!currentPaths.TryGetValue(source.Id, out var replacement) || !hashes.TryGetValue(source.Id, out var hash)) continue;
      var old = Path.GetFullPath(source.Path);
      if (retained.Contains(old) || !old.StartsWith(root, StringComparison.OrdinalIgnoreCase) || !File.Exists(old) || Read(old) is not null) continue;
      var marker = Read(replacement);
      if (marker?["id"]?.GetValue<string>() != source.Id ||
          !string.Equals(marker["manifest"]?["sha256"]?.GetValue<string>(), hash, StringComparison.OrdinalIgnoreCase)) continue;
      using var input = new FileStream(old, FileMode.Open, FileAccess.Read, FileShare.Read);
      if (!Convert.ToHexString(SHA256.HashData(input)).Equals(hash, StringComparison.OrdinalIgnoreCase)) continue;
      try { File.Delete(old); removed++; } catch { }
    }
    return removed;
  }

  private static void Download(JsonObject marker, Stream destination)
  {
    var session = GoogleDriveAuthorization.Load();
    if (session?.AccountKeys != true) throw new InvalidOperationException("Google 계정을 연결해 주세요.");
    using var http = new HttpClient(new HttpClientHandler { AllowAutoRedirect = false }) { Timeout = TimeSpan.FromSeconds(40) };
    var client = new DriveAppDataClient(http, token => GoogleDriveAuthorization.AccessTokenAsync(token)) { AccountMode = true };
    var keys = DriveAccountKeys.LoadAsync(client, CancellationToken.None).GetAwaiter().GetResult();
    var descriptor = marker["manifest"]!.AsObject();
    var manifest = descriptor.Deserialize<BookshelfImageChunks.Manifest>(BookshelfSyncReplica.Json)!;
    var files = descriptor["files"]!.AsArray();
    if (files.Count != manifest.Chunks.Count) throw new InvalidDataException("책장 원본 참조가 올바르지 않습니다.");
    BookshelfImageChunks.Decode(manifest, index =>
    {
      var reference = files[index]!;
      var name = reference["name"]!.GetValue<string>();
      if (!name.StartsWith(DriveAppDataClient.ShelfChunkPrefix, StringComparison.Ordinal) || !DriveAppDataClient.ValidShelfName(name))
        throw new InvalidDataException("책장 원본 참조가 올바르지 않습니다.");
      return keys.Decrypt(client.ReadAsync(new(reference["id"]!.GetValue<string>(), name, null), CancellationToken.None).GetAwaiter().GetResult());
    }, destination);
  }

  private static void Trim(string directory)
  {
    var files = Directory.GetFiles(directory).Select(path => new FileInfo(path)).OrderBy(file => file.LastWriteTimeUtc).ToArray();
    var total = files.Sum(file => file.Length);
    foreach (var file in files)
    {
      if (total <= ThumbnailLimit) break;
      try { total -= file.Length; file.Delete(); } catch { }
    }
  }
#endif
}
