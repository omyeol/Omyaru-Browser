using System.Security.Cryptography;
using System.Text.Json;
using System.Text.Json.Nodes;

namespace PrivacyBrowser.Desktop;

// Network/merge engine only. Caller applies the result on its UI thread after checking
// the local snapshot has not changed. Tabs and reset markers are outside this namespace.
internal static class DriveBookshelfSync
{
  private static readonly JsonSerializerOptions Json = BookshelfSyncReplica.Json;
  internal sealed record ImageSource(string Id, string Path);
  internal sealed record Result(JsonObject Snapshot, Dictionary<string, string> Images);
  internal static bool VerifiedOriginal(string path, BookshelfImageChunks.Manifest manifest)
  {
    if (!File.Exists(path)) return false;
    using var stream = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.Read);
    return stream.Length == manifest.Length &&
        Convert.ToHexString(SHA256.HashData(stream)).Equals(manifest.Sha256, StringComparison.OrdinalIgnoreCase);
  }
  internal static bool ShouldRestoreStaleEmptyCatalog(BookshelfSyncReplica replica, JsonObject snapshot, bool requested)
  {
    if (!requested || snapshot["images"]!.AsArray().Count != 0 ||
        !replica.Data.Baseline.Keys.Any(key => key.StartsWith("images/", StringComparison.Ordinal))) return false;
    if (replica.Data.Pending.Count > 0 || replica.Project()["images"]!.AsArray().Count == 0)
      throw new InvalidDataException("PC 책장 기록이 일치하지 않아 자동 복구를 보류했습니다.");
    return true;
  }
  internal static async Task<Result> RunAsync(string stateDirectory, JsonObject local,
      IReadOnlyList<ImageSource> sources, DriveAppDataClient client, DriveAccountKeys keys, CancellationToken token,
      Action<int, int>? downloadProgress = null, bool restoreStaleEmptyCatalog = false,
      Action<int, int>? uploadProgress = null, Action<int, int>? metadataProgress = null,
      Action<int, int>? scanProgress = null, bool onDemand = false)
  {
    Directory.CreateDirectory(stateDirectory);
    var statePath = Path.Combine(stateDirectory, "replica.json");
    var cachePath = Path.Combine(stateDirectory, "images.json");
    var replica = new BookshelfSyncReplica(File.Exists(statePath)
        ? JsonSerializer.Deserialize<BookshelfSyncReplica.State>(await File.ReadAllTextAsync(statePath, token), Json)
          ?? throw new InvalidDataException("책장 동기화 상태가 손상되었습니다.") : null);
    var cache = File.Exists(cachePath) ? JsonNode.Parse(await File.ReadAllTextAsync(cachePath, token))!.AsObject() : new JsonObject();
    async Task Save(string path, string data) { await File.WriteAllTextAsync(path + ".new", data, token); File.Move(path + ".new", path, true); }
    async Task SaveReplica() => await Save(statePath, JsonSerializer.Serialize(replica.Data, Json));
    var snapshot = local.DeepClone().AsObject();
    var localPaths = sources.ToDictionary(s => s.Id, s => s.Path, StringComparer.Ordinal);
    var localImages = snapshot["images"]!.AsArray();
    var uploadTotal = localImages.Count;
    var uploaded = 0;
    var scanned = 0;
    scanProgress?.Invoke(0, uploadTotal);
    var uploadJobs = new List<(JsonNode Image, string Id, string Path, string Stamp)>();
    foreach (var image in localImages)
    {
      token.ThrowIfCancellationRequested();
      var id = image!["id"]!.GetValue<string>();
      if (!localPaths.TryGetValue(id, out var path) || !File.Exists(path))
        throw new IOException("책장 원본 이미지에 접근할 수 없어 동기화를 보류했습니다. 삭제로 처리하지 않습니다.");
      if (CloudShelfMarker.Read(path) is JsonObject cloudMarker)
      {
        if (cloudMarker["id"]!.GetValue<string>() != id) throw new InvalidDataException("책장 클라우드 사진 ID가 다릅니다.");
        image["manifest"] = cloudMarker["manifest"]!.DeepClone();
        scanProgress?.Invoke(++scanned, uploadTotal);
        continue;
      }
      var file = new FileInfo(path);
      var stamp = $"{Path.GetFullPath(path)}|{file.Length}|{file.LastWriteTimeUtc.Ticks}";
      var saved = cache[id]?.AsObject();
      var baselineKey = $"images/{id}/manifest";
      if (replica.Data.Baseline.TryGetValue(baselineKey, out var baselineNode) && baselineNode is JsonObject baseline)
      {
        var sameStampAndManifest = saved?["stamp"]?.GetValue<string>() == stamp &&
            JsonNode.DeepEquals(saved["manifest"], baseline);
        var sameContent = sameStampAndManifest || VerifiedOriginal(path, baseline.Deserialize<BookshelfImageChunks.Manifest>(Json)!);
        if (sameContent)
        {
          image["manifest"] = baseline.DeepClone();
          if (!sameStampAndManifest)
          {
            cache[id] = new JsonObject { ["stamp"] = stamp, ["manifest"] = baseline.DeepClone() };
            await Save(cachePath, cache.ToJsonString());
          }
          scanProgress?.Invoke(++scanned, uploadTotal);
          continue;
        }
      }
      if (saved?["stamp"]?.GetValue<string>() == stamp)
      { image["manifest"] = saved["manifest"]!.DeepClone(); scanProgress?.Invoke(++scanned, uploadTotal); continue; }
      if (saved?["manifest"] is JsonObject savedManifest &&
          VerifiedOriginal(path, savedManifest.Deserialize<BookshelfImageChunks.Manifest>(Json)!))
      {
        image["manifest"] = savedManifest.DeepClone();
        saved["stamp"] = stamp;
        await Save(cachePath, cache.ToJsonString());
        scanProgress?.Invoke(++scanned, uploadTotal);
        continue;
      }
      uploadJobs.Add((image, id, path, stamp));
      scanProgress?.Invoke(++scanned, uploadTotal);
    }
    if (uploadJobs.Count > 0) uploadProgress?.Invoke(0, uploadJobs.Count);
    // Keep full Drive read-back verification, but overlap independent photos.
    // Each successful image is checkpointed immediately so interrupted transfers resume.
    using var cacheGate = new SemaphoreSlim(1, 1);
    await Parallel.ForEachAsync(uploadJobs,
        new ParallelOptions { MaxDegreeOfParallelism = 3, CancellationToken = token },
        async (job, cancellation) =>
    {
      var references = new JsonArray();
      // Encode reads at most one chunk at a time; no full-resolution bitmap allocation.
      var manifest = await Task.Run(() =>
      {
        using var input = new FileStream(job.Path, FileMode.Open, FileAccess.Read, FileShare.Read);
        return BookshelfImageChunks.Encode(input, (_, plain) =>
        {
          cancellation.ThrowIfCancellationRequested();
          var operation = Guid.NewGuid();
          var encrypted = keys.Encrypt(plain);
          var cloudId = client.AppendShelfChunkAsync(operation, encrypted, cancellation).GetAwaiter().GetResult();
          var name = DriveAppDataClient.ShelfChunkPrefix + operation.ToString("N") + ".json";
          var verified = keys.Decrypt(client.ReadAsync(new(cloudId, name, null), cancellation).GetAwaiter().GetResult());
          if (verified != plain) throw new InvalidDataException("책장 이미지 업로드 확인에 실패했습니다.");
          references.Add(new JsonObject { ["id"] = cloudId, ["name"] = name });
        });
      }, cancellation);
      var file = new FileInfo(job.Path);
      if ($"{Path.GetFullPath(job.Path)}|{file.Length}|{file.LastWriteTimeUtc.Ticks}" != job.Stamp)
        throw new IOException("전송 중 이미지가 변경되어 다음 동기화에서 다시 확인합니다.");
      var descriptor = JsonSerializer.SerializeToNode(manifest, Json)!.AsObject();
      descriptor["files"] = references;
      await cacheGate.WaitAsync(cancellation);
      try
      {
        job.Image["manifest"] = descriptor.DeepClone();
        cache[job.Id] = new JsonObject { ["stamp"] = job.Stamp, ["manifest"] = descriptor };
        await Save(cachePath, cache.ToJsonString());
        uploadProgress?.Invoke(Interlocked.Increment(ref uploaded), uploadJobs.Count);
      }
      finally { cacheGate.Release(); }
    });
    var restore = ShouldRestoreStaleEmptyCatalog(replica, snapshot, restoreStaleEmptyCatalog);
    if (!restore) { replica.Capture(snapshot); await SaveReplica(); }
    foreach (var batch in replica.Data.Pending.ToArray())
    {
      var id = await client.AppendShelfAsync(Guid.ParseExact(batch.Id, "N"), keys.Encrypt(JsonSerializer.Serialize(batch, Json)), token);
      replica.Data.SeenFiles.Add(id); replica.Data.Pending.Remove(batch); await SaveReplica();
    }
    var entries = await client.ListShelfAsync(token);
    if (entries.Count > 10000) throw new InvalidDataException("책장 변경 기록의 안전 한도에 도달했습니다.");
    var unread = entries.Where(e => !replica.Data.SeenFiles.Contains(e.Id)).ToList();
    metadataProgress?.Invoke(0, unread.Count);
    var metadataDone = 0;
    foreach (var entry in unread)
    {
      var batch = JsonSerializer.Deserialize<BookshelfSyncReplica.Batch>(keys.Decrypt(await client.ReadAsync(entry, token)), Json)
          ?? throw new InvalidDataException("책장 변경 기록이 비어 있습니다.");
      if (entry.Name != DriveAppDataClient.ShelfPrefix + batch.Id + ".json") throw new InvalidDataException("책장 변경 ID가 다릅니다.");
      replica.Apply(batch); replica.Data.SeenFiles.Add(entry.Id);
      metadataProgress?.Invoke(++metadataDone, unread.Count);
    }
    await SaveReplica();
    var projected = replica.Project();
    var paths = new Dictionary<string, string>();
    var imageDirectory = Path.Combine(stateDirectory, "originals"); Directory.CreateDirectory(imageDirectory);
    var remoteImages = projected["images"]!.AsArray();
    var completed = 0;
    downloadProgress?.Invoke(0, remoteImages.Count);
    foreach (var image in remoteImages)
    {
      var id = image!["id"]!.GetValue<string>(); var descriptor = image["manifest"]!.AsObject();
      localPaths.TryGetValue(id, out var original);
      var manifest = descriptor.Deserialize<BookshelfImageChunks.Manifest>(Json)!;
      var files = descriptor["files"]!.AsArray();
      if (files.Count != manifest.Chunks.Count || manifest.Sha256.Length != 64 || manifest.Sha256.Any(c => !Uri.IsHexDigit(c)))
        throw new InvalidDataException("책장 이미지 참조가 올바르지 않습니다.");
      if (original is not null && File.Exists(original) && CloudShelfMarker.Read(original) is null)
      {
        if (!VerifiedOriginal(original, manifest)) throw new InvalidDataException("로컬 원본과 클라우드 사진이 다릅니다.");
        // A verified local original is already the best download cache. Keep its
        // catalog path instead of replacing it with a network-only marker.
        paths[id] = original;
        downloadProgress?.Invoke(++completed, remoteImages.Count);
        continue;
      }
      var destination = Path.Combine(imageDirectory, id + "-" + manifest.Sha256 + ".png");
      if (onDemand && !VerifiedOriginal(destination, manifest))
      {
        var marker = Path.Combine(imageDirectory, id + "-" + manifest.Sha256 + ".cloud");
        CloudShelfMarker.Write(marker, id, descriptor);
        paths[id] = marker;
        downloadProgress?.Invoke(++completed, remoteImages.Count);
        continue;
      }
      if (!VerifiedOriginal(destination, manifest))
      {
        var temporary = destination + "." + Guid.NewGuid().ToString("N") + ".partial";
        try
        {
          await using (var output = new FileStream(temporary, FileMode.CreateNew, FileAccess.Write, FileShare.None))
            BookshelfImageChunks.Decode(manifest, index =>
            {
              token.ThrowIfCancellationRequested();
              var reference = files[index]!;
              var name = reference["name"]!.GetValue<string>();
              if (!name.StartsWith(DriveAppDataClient.ShelfChunkPrefix, StringComparison.Ordinal) || !DriveAppDataClient.ValidShelfName(name))
                throw new InvalidDataException("책장 이미지 파일명이 올바르지 않습니다.");
              return keys.Decrypt(client.ReadAsync(new(reference["id"]!.GetValue<string>(), name, null), token).GetAwaiter().GetResult());
            }, output);
          File.Move(temporary, destination, true);
        }
        finally { if (File.Exists(temporary)) File.Delete(temporary); }
      }
      paths[id] = destination;
      var info = new FileInfo(destination);
      cache[id] = new JsonObject { ["stamp"] = $"{Path.GetFullPath(destination)}|{info.Length}|{info.LastWriteTimeUtc.Ticks}", ["manifest"] = descriptor.DeepClone() };
      await Save(cachePath, cache.ToJsonString());
      downloadProgress?.Invoke(++completed, remoteImages.Count);
    }
    return new(projected, paths);
  }
  internal static void Acknowledge(string directory, JsonObject applied)
  {
    var path = Path.Combine(directory, "replica.json");
    var replica = new BookshelfSyncReplica(JsonSerializer.Deserialize<BookshelfSyncReplica.State>(File.ReadAllText(path), Json)!);
    replica.Acknowledge(applied);
    File.WriteAllText(path + ".new", JsonSerializer.Serialize(replica.Data, Json)); File.Move(path + ".new", path, true);
  }
}
