using System.Text.Json;

namespace PrivacyBrowser.Desktop;

internal static class DesktopDriveSync
{
  private static readonly HttpClient Http = new(new HttpClientHandler { AllowAutoRedirect = false })
      { Timeout = TimeSpan.FromSeconds(25) };
  private static string StatePath => Path.Combine(BrowserStateStore.StateDirectory, "drive-tab-replica.json");
  private static readonly object InstanceGate = new();
  private static FileStream? _instanceLease;
  internal static void EnsureExclusiveInstance()
  {
    lock (InstanceGate)
    {
      if (_instanceLease is not null) return;
      Directory.CreateDirectory(BrowserStateStore.StateDirectory);
      try
      {
        _instanceLease = new FileStream(Path.Combine(BrowserStateStore.StateDirectory, "drive-sync.lock"),
            FileMode.OpenOrCreate, FileAccess.ReadWrite, FileShare.None);
      }
      catch (IOException) { throw new InvalidOperationException("다른 Omyaru 프로그램이 Drive 연동 중입니다. 이전 실행 창을 종료해 주세요."); }
    }
  }
  private static DriveTabReplica Load() => new(File.Exists(StatePath)
      ? JsonSerializer.Deserialize<DriveTabReplica.State>(File.ReadAllText(StatePath), DriveTabReplica.Json)
        ?? throw new InvalidDataException("Drive 로컬 상태가 손상되었습니다.") : null);
  private static void Save(DriveTabReplica replica)
  {
    Directory.CreateDirectory(BrowserStateStore.StateDirectory);
    File.WriteAllText(StatePath + ".new", JsonSerializer.Serialize(replica.Data, DriveTabReplica.Json));
    File.Move(StatePath + ".new", StatePath, true);
  }

  // Called only by the shell's single sync worker. Capture is persisted BEFORE IO,
  // so edits during network IO can be diffed against this exact local observation.
  internal static async Task<SyncSnapshot> RunAsync(SyncSnapshot local, CancellationToken cancellation)
  {
    EnsureExclusiveInstance();
    var session = GoogleDriveAuthorization.Load() ?? throw new InvalidOperationException("Google Drive 연결이 필요합니다.");
    var hadReplica = File.Exists(StatePath);
    var replica = Load();
    replica.Capture(local);
    Save(replica);
    var client = new DriveAppDataClient(Http, GoogleDriveAuthorization.AccessTokenAsync) { AccountMode = session.AccountKeys };
    var keys = session.AccountKeys ? await DriveAccountKeys.LoadAsync(client, cancellation) : null;
    if (keys is not null)
    {
      var resets = await DriveTabReset.ReadAsync(client, keys, cancellation);
      if (hadReplica) replica.ObserveResets(resets);
      else replica.Data.TabResetIds.UnionWith(resets);
      if (replica.Data.NeedsTabClear)
      {
        local = local.Copy(false);
        local.Tabs.RemoveAll(t => t.GroupId != "__private_workspace__");
        replica.Capture(local);
      }
      Save(replica);
    }
    string Encrypt(string json) => keys is null ? SyncCodec.EncryptJson(json, session.Passphrase) : keys.Encrypt(json);
    string Decrypt(string envelope) => keys is null ? SyncCodec.DecryptJson(envelope, session.Passphrase) : keys.Decrypt(envelope);
    // Publish local edits before downloading history so receivers can see them immediately.
    foreach (var batch in replica.Data.Pending.ToArray())
    {
      var envelope = Encrypt(replica.Stamp(JsonSerializer.Serialize(batch, DriveTabReplica.Json)));
      var id = await client.AppendAsync(Guid.ParseExact(batch.Id, "N"), envelope, cancellation);
      replica.Data.SeenFiles.Add(id);
      replica.Data.Pending.Remove(batch);
      Save(replica);
    }
    var entries = await client.ListAsync(cancellation);
    if (entries.Count > 5000) throw new InvalidOperationException("Drive 변경 기록이 5,000개를 초과했습니다. 기록 압축이 필요합니다.");
    foreach (var entry in entries.Where(e => !replica.Data.SeenFiles.Contains(e.Id)))
    {
      var json = Decrypt(await client.ReadAsync(entry, cancellation));
      replica.ApplyDocument(json, entry.Name[DriveAppDataClient.Prefix.Length..^5]);
      replica.Data.SeenFiles.Add(entry.Id);
    }
    Save(replica);
    var projected = replica.Project(local);
    var compactable = entries.Where(e => !session.AccountKeys || e.Name.StartsWith(DriveAppDataClient.AccountPrefix, StringComparison.Ordinal)).ToArray();
    if (compactable.Length >= 128)
    {
      var checkpoint = replica.Compact();
      var envelope = Encrypt(replica.Stamp(JsonSerializer.Serialize(checkpoint, DriveTabReplica.Json)));
      var id = await client.AppendAsync(Guid.ParseExact(checkpoint.Id, "N"), envelope, cancellation);
      var uploaded = new DriveAppDataClient.Entry(id, (session.AccountKeys ? DriveAppDataClient.AccountPrefix : DriveAppDataClient.Prefix) + checkpoint.Id + ".json", null);
      var verification = new DriveTabReplica();
      verification.ApplyDocument(Decrypt(await client.ReadAsync(uploaded, cancellation)), checkpoint.Id);
      replica.Data.SeenFiles.Add(id);
      Save(replica);
      // Only remove exact files already represented by the verified checkpoint.
      // Concurrent uploads not in this listing are never removed.
      foreach (var entry in compactable) await client.DeleteCoveredAsync(entry, cancellation);
      replica.Data.SeenFiles.ExceptWith(compactable.Select(e => e.Id));
      Save(replica);
    }
    return projected;
  }

  internal static void Acknowledge(SyncSnapshot applied)
  {
    var replica = Load();
    replica.Acknowledge(applied);
    Save(replica);
  }
  internal static async Task ResetTabsAsync(string confirmation, CancellationToken cancellation)
  {
    DriveTabReset.ValidateConfirmation(confirmation);
    EnsureExclusiveInstance();
    var session = GoogleDriveAuthorization.Load();
    if (session?.AccountKeys != true) throw new InvalidOperationException("Google 계정 방식으로 먼저 연결해 주세요.");
    var client = new DriveAppDataClient(Http, GoogleDriveAuthorization.AccessTokenAsync) { AccountMode = true };
    var keys = await DriveAccountKeys.LoadAsync(client, cancellation);
    await DriveTabReset.PublishAsync(confirmation, client, keys, cancellation);
  }

  // Archive rather than erase unsent offline changes when changing accounts.
  internal static void ResetConnection()
  {
    if (File.Exists(StatePath)) File.Move(StatePath, StatePath + "." + Guid.NewGuid().ToString("N") + ".backup");
  }
}
