using System.Security.Cryptography;
using System.Text;
using System.Text.Json;

namespace PrivacyBrowser.Desktop;

// Android and Windows use the same append-only appDataFolder record format.
internal static class DesktopUserDataSync
{
  private sealed record Folder(string Id, string Name, string? ParentId, int Order, long CreatedAtEpochMs);
  private sealed record Item(string Id, string Title, string Url, string? FolderId, int Order, long CreatedAtEpochMs);
  private sealed class State
  {
    public HashSet<string> SeenFiles { get; set; } = [];
    public List<Folder> Folders { get; set; } = [];
    public List<Item> Items { get; set; } = [];
    public HashSet<string> DeletedIds { get; set; } = new(StringComparer.Ordinal);
    public HashSet<string> DeletedUrlKeys { get; set; } = new(StringComparer.OrdinalIgnoreCase);
    public HashSet<string> DeletedFolderIds { get; set; } = new(StringComparer.Ordinal);
    public string LastUploadFingerprint { get; set; } = string.Empty;
  }
  private sealed record BookmarkPayload(List<Folder> Folders, List<Item> Items);
  private sealed record Payload(string Id, string DeviceId, long Timestamp, BookmarkPayload Bookmarks, List<BrowserHistoryEntry> History);
  private static readonly JsonSerializerOptions Json = new() { PropertyNamingPolicy = JsonNamingPolicy.CamelCase, PropertyNameCaseInsensitive = true };
  private static string StatePath => Path.Combine(BrowserStateStore.StateDirectory, "drive-userdata-state.json");
  private static readonly HttpClient Http = new(new HttpClientHandler { AllowAutoRedirect = false }) { Timeout = TimeSpan.FromSeconds(35) };

  internal static async Task<bool> RunAsync(CancellationToken cancellation)
  {
    var session = GoogleDriveAuthorization.Load() ?? throw new InvalidOperationException("Google 계정 연결이 필요합니다.");
    var client = new DriveAppDataClient(Http, GoogleDriveAuthorization.AccessTokenAsync) { AccountMode = session.AccountKeys };
    var keys = session.AccountKeys ? await DriveAccountKeys.LoadAsync(client, cancellation) : null;
    string Decrypt(string value) => keys is null ? SyncCodec.DecryptJson(value, session.Passphrase) : keys.Decrypt(value);
    string Encrypt(string value) => keys is null ? SyncCodec.EncryptJson(value, session.Passphrase) : keys.Encrypt(value);
    var state = File.Exists(StatePath) ? JsonSerializer.Deserialize<State>(File.ReadAllText(StatePath), Json) ?? new State() : new State();
    state.DeletedIds = new HashSet<string>(state.DeletedIds ?? [], StringComparer.Ordinal);
    state.DeletedUrlKeys = new HashSet<string>(state.DeletedUrlKeys ?? [], StringComparer.OrdinalIgnoreCase);
    state.DeletedFolderIds = new HashSet<string>(state.DeletedFolderIds ?? [], StringComparer.Ordinal);

    // Sync local bookmark & folder deletions into tombstones BEFORE applying remote files,
    // so items/folders deleted by the user NEVER resurrect on subsequent syncs or app upgrades.
    ReconcileLocalDeletions(state);

    var files = await client.ListUserDataAsync(cancellation);
    var changed = false;
    foreach (var file in files.Where(x => !state.SeenFiles.Contains(x.Id)))
    {
      var payload = JsonSerializer.Deserialize<Payload>(Decrypt(await client.ReadAsync(file, cancellation)), Json)
          ?? throw new InvalidDataException("방문 기록 동기화 파일을 읽지 못했습니다.");

      var foldersById = state.Folders.ToDictionary(x => x.Id, StringComparer.Ordinal);
      foreach (var folder in payload.Bookmarks.Folders)
      {
        if (string.IsNullOrWhiteSpace(folder.Id) || string.IsNullOrWhiteSpace(folder.Name)) continue;
        if (state.DeletedFolderIds.Contains(folder.Id)) continue;
        foldersById.TryAdd(folder.Id, folder);
      }
      state.Folders = foldersById.Values.ToList();

      var itemsById = state.Items.ToDictionary(x => x.Id, StringComparer.Ordinal);
      var knownUrlKeys = state.Items
          .Select(x => (x.FolderId ?? string.Empty) + "\n" + BookmarkStore.CanonicalUrlKey(x.Url))
          .ToHashSet(StringComparer.OrdinalIgnoreCase);
      foreach (var item in payload.Bookmarks.Items)
      {
        if (!BookmarkStore.TryNormalizeUrl(item.Url, out _)) continue;
        var urlKey = BookmarkStore.CanonicalUrlKey(item.Url);
        if (state.DeletedIds.Contains(item.Id) || (!string.IsNullOrEmpty(urlKey) && state.DeletedUrlKeys.Contains(urlKey)))
          continue;
        var folderId = item.FolderId is not null && state.DeletedFolderIds.Contains(item.FolderId) ? null : item.FolderId;
        var scopeKey = (folderId ?? string.Empty) + "\n" + urlKey;
        if (!string.IsNullOrEmpty(urlKey) && !knownUrlKeys.Add(scopeKey))
          continue;
        itemsById.TryAdd(item.Id, item with { FolderId = folderId });
      }
      state.Items = itemsById.Values.ToList();
      BrowserHistory.Merge(payload.History);
      if (MergeBookmarks(state.Folders, state.Items, state.DeletedIds, state.DeletedUrlKeys, state.DeletedFolderIds))
        changed = true;
      state.SeenFiles.Add(file.Id);
      Save(state); // Crash-safe progress before the next remote file.
    }

    // Reflect authoritative local folder tree & bookmarks into state for cross-device upload
    var catalog = BookmarkStore.Load();
    foreach (var id in catalog.DeletedIds) state.DeletedIds.Add(id);
    foreach (var key in catalog.DeletedUrlKeys) state.DeletedUrlKeys.Add(key);
    foreach (var fid in catalog.DeletedFolderIds) state.DeletedFolderIds.Add(fid);

    var oldFoldersById = state.Folders.ToDictionary(x => x.Id, StringComparer.Ordinal);
    var mergedFolders = new List<Folder>();
    foreach (var (folder, idx) in catalog.Folders.Select((f, i) => (f, i)))
    {
      state.DeletedFolderIds.Remove(folder.Id);
      if (oldFoldersById.TryGetValue(folder.Id, out var oldF))
        mergedFolders.Add(oldF with { Name = folder.Name, ParentId = folder.ParentId, Order = idx });
      else
        mergedFolders.Add(new Folder(folder.Id, folder.Name, folder.ParentId, idx,
            folder.CreatedAtEpochMs > 0 ? folder.CreatedAtEpochMs : DateTimeOffset.UtcNow.ToUnixTimeMilliseconds()));
    }
    state.Folders = mergedFolders;

    var mergedItems = new List<Item>();
    var seenKeys = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
    var oldById = state.Items.ToDictionary(x => x.Id, StringComparer.Ordinal);
    foreach (var (entry, index) in catalog.Items.Select((entry, index) => (entry, index)))
    {
      var key = BookmarkStore.CanonicalUrlKey(entry.Url);
      var scopeKey = (entry.FolderId ?? string.Empty) + "\n" + key;
      if (!string.IsNullOrEmpty(key))
      {
        state.DeletedUrlKeys.Remove(key);
        if (!seenKeys.Add(scopeKey)) continue;
      }
      state.DeletedIds.Remove(entry.Id);
      if (oldById.TryGetValue(entry.Id, out var old))
        mergedItems.Add(old with { Title = entry.Name, Url = entry.Url, FolderId = entry.FolderId, Order = index });
      else
        mergedItems.Add(new Item(entry.Id, entry.Name, entry.Url, entry.FolderId, index, DateTimeOffset.UtcNow.ToUnixTimeMilliseconds()));
    }
    state.Items = mergedItems;

    var history = BrowserHistory.Load().Take(300).ToList();
    var bookmarks = new BookmarkPayload(state.Folders, state.Items);
    var content = JsonSerializer.Serialize(new { bookmarks, history }, Json);
    var fingerprint = Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(content)));
    if ((state.Items.Count > 0 || state.Folders.Count > 0 || history.Count > 0) && fingerprint != state.LastUploadFingerprint)
    {
      var id = Guid.NewGuid();
      var payload = new Payload(id.ToString("N"), DesktopIdentity.DeviceId, DateTimeOffset.UtcNow.ToUnixTimeMilliseconds(), bookmarks, history);
      var uploaded = await client.AppendUserDataAsync(id, Encrypt(JsonSerializer.Serialize(payload, Json)), cancellation);
      state.SeenFiles.Add(uploaded);
      state.LastUploadFingerprint = fingerprint;
      Save(state);
    }
    return changed;
  }

  private static void ReconcileLocalDeletions(State state)
  {
    if (!BookmarkStore.ExistsOnDisk) return;
    var catalog = BookmarkStore.Load();
    foreach (var id in catalog.DeletedIds) state.DeletedIds.Add(id);
    foreach (var key in catalog.DeletedUrlKeys) state.DeletedUrlKeys.Add(key);
    foreach (var fid in catalog.DeletedFolderIds) state.DeletedFolderIds.Add(fid);

    // If catalog has folders already synced before, record locally removed folder IDs.
    var localFolderIds = catalog.Folders.Select(f => f.Id).ToHashSet(StringComparer.Ordinal);
    if (catalog.Folders.Count > 0 || catalog.DeletedFolderIds.Count > 0)
    {
      state.Folders = state.Folders.Where(f => !state.DeletedFolderIds.Contains(f.Id)).ToList();
    }

    var localIds = catalog.Items.Select(x => x.Id).ToHashSet(StringComparer.Ordinal);
    var localKeys = catalog.Items
        .Select(x => BookmarkStore.CanonicalUrlKey(x.Url))
        .Where(k => !string.IsNullOrEmpty(k))
        .ToHashSet(StringComparer.OrdinalIgnoreCase);

    var kept = new List<Item>();
    var seenKeys = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
    foreach (var prev in state.Items)
    {
      var key = BookmarkStore.CanonicalUrlKey(prev.Url);
      var presentLocally = localIds.Contains(prev.Id) || (!string.IsNullOrEmpty(key) && localKeys.Contains(key));
      if (!presentLocally)
      {
        state.DeletedIds.Add(prev.Id);
        if (!string.IsNullOrEmpty(key)) state.DeletedUrlKeys.Add(key);
        continue;
      }
      var scopeKey = (prev.FolderId ?? string.Empty) + "\n" + key;
      if (!string.IsNullOrEmpty(key) && !seenKeys.Add(scopeKey)) continue;
      kept.Add(prev);
    }
    state.Items = kept;
  }

  private static bool MergeBookmarks(
      IEnumerable<Folder> remoteFolders,
      IEnumerable<Item> remoteItems,
      HashSet<string> deletedIds,
      HashSet<string> deletedUrlKeys,
      HashSet<string> deletedFolderIds)
  {
    var catalog = BookmarkStore.Load();
    foreach (var id in catalog.DeletedIds) deletedIds.Add(id);
    foreach (var key in catalog.DeletedUrlKeys) deletedUrlKeys.Add(key);
    foreach (var fid in catalog.DeletedFolderIds) deletedFolderIds.Add(fid);

    var changed = false;
    var folderIdMap = new Dictionary<string, string>(StringComparer.Ordinal);
    var existingFoldersById = catalog.Folders.ToDictionary(f => f.Id, StringComparer.Ordinal);
    var existingFolderKeyToId = catalog.Folders.ToDictionary(
        f => (f.ParentId ?? string.Empty) + "\n" + f.Name,
        f => f.Id,
        StringComparer.OrdinalIgnoreCase);

    foreach (var rf in remoteFolders)
    {
      if (string.IsNullOrWhiteSpace(rf.Id) || string.IsNullOrWhiteSpace(rf.Name)) continue;
      if (deletedFolderIds.Contains(rf.Id)) continue;
      var cleanName = string.Join(' ', rf.Name.Split((char[]?)null, StringSplitOptions.RemoveEmptyEntries));
      if (string.IsNullOrWhiteSpace(cleanName)) continue;
      var mappedParent = rf.ParentId is not null && folderIdMap.TryGetValue(rf.ParentId, out var mp) ? mp : rf.ParentId;
      if (mappedParent is not null && !existingFoldersById.ContainsKey(mappedParent)) mappedParent = null;
      if (existingFoldersById.ContainsKey(rf.Id))
      {
        folderIdMap[rf.Id] = rf.Id;
        continue;
      }
      var key = (mappedParent ?? string.Empty) + "\n" + cleanName;
      if (existingFolderKeyToId.TryGetValue(key, out var canonicalId))
      {
        folderIdMap[rf.Id] = canonicalId;
        continue;
      }
      var created = new BookmarkFolderEntry(rf.Id, cleanName, mappedParent, rf.Order, rf.CreatedAtEpochMs);
      catalog.Folders.Add(created);
      existingFoldersById[created.Id] = created;
      existingFolderKeyToId[key] = created.Id;
      folderIdMap[rf.Id] = created.Id;
      changed = true;
    }

    var knownIds = catalog.Items.Select(x => x.Id).ToHashSet(StringComparer.Ordinal);
    var knownKeys = catalog.Items
        .Select(x => (x.FolderId ?? string.Empty) + "\n" + BookmarkStore.CanonicalUrlKey(x.Url))
        .ToHashSet(StringComparer.OrdinalIgnoreCase);
    var knownGlobalKeys = catalog.Items
        .Select(x => BookmarkStore.CanonicalUrlKey(x.Url))
        .Where(k => !string.IsNullOrEmpty(k))
        .ToHashSet(StringComparer.OrdinalIgnoreCase);

    foreach (var item in remoteItems)
    {
      if (!BookmarkStore.TryNormalizeUrl(item.Url, out var url)) continue;
      var urlKey = BookmarkStore.CanonicalUrlKey(url);
      if (deletedIds.Contains(item.Id) || (!string.IsNullOrEmpty(urlKey) && deletedUrlKeys.Contains(urlKey)))
        continue;
      string? targetFolderId = null;
      if (!string.IsNullOrWhiteSpace(item.FolderId) && !deletedFolderIds.Contains(item.FolderId))
      {
        if (folderIdMap.TryGetValue(item.FolderId, out var mappedFolder)) targetFolderId = mappedFolder;
        else if (existingFoldersById.ContainsKey(item.FolderId)) targetFolderId = item.FolderId;
      }
      if (!knownIds.Add(item.Id)) continue;
      var scopeKey = (targetFolderId ?? string.Empty) + "\n" + urlKey;
      if (!string.IsNullOrEmpty(urlKey) && (!knownKeys.Add(scopeKey) || (targetFolderId is null && knownGlobalKeys.Contains(urlKey))))
        continue;
      if (!string.IsNullOrEmpty(urlKey)) knownGlobalKeys.Add(urlKey);
      catalog.Items.Add(new BookmarkEntry(item.Id, item.Title, url, BookmarkStore.GlyphFor(item.Title), BookmarkStore.ColorFor(catalog.Items.Count), targetFolderId));
      changed = true;
    }

    if (changed) BookmarkStore.Save(catalog);
    return changed;
  }
  private static void Save(State state)
  {
    Directory.CreateDirectory(BrowserStateStore.StateDirectory);
    File.WriteAllText(StatePath + ".new", JsonSerializer.Serialize(state, Json));
    File.Move(StatePath + ".new", StatePath, true);
  }

  internal static void ResetConnection()
  {
    if (File.Exists(StatePath)) File.Move(StatePath, StatePath + "." + Guid.NewGuid().ToString("N") + ".backup");
  }
}
