using System.Security.Cryptography;
using System.Text;
using System.Text.Json;

namespace PrivacyBrowser.Desktop;

internal enum AutoSyncDecision
{
  Idle,
  Export,
  Import,
  Conflict,
  NotReady,
}

internal static class AutoSyncDecisionEngine
{
  internal static AutoSyncDecision Decide(
      string? baselineFingerprint,
      string? currentFingerprint,
      long baselineLocalModifiedAt,
      long localModifiedAt)
  {
    if (string.IsNullOrWhiteSpace(baselineFingerprint)) return AutoSyncDecision.NotReady;
    if (string.IsNullOrWhiteSpace(currentFingerprint)) return AutoSyncDecision.Conflict;
    var remoteChanged = !string.Equals(currentFingerprint, baselineFingerprint, StringComparison.Ordinal);
    var localChanged = localModifiedAt > baselineLocalModifiedAt;
    if (remoteChanged && localChanged) return AutoSyncDecision.Conflict;
    if (remoteChanged) return AutoSyncDecision.Import;
    if (localChanged) return AutoSyncDecision.Export;
    return AutoSyncDecision.Idle;
  }
}

internal sealed class AutoSyncConfiguration
{
  public bool Enabled { get; set; }
  public string SyncPath { get; set; } = string.Empty;
  public string ProtectedPassphrase { get; set; } = string.Empty;
  public string? BaselineFingerprint { get; set; }
  public string? PendingRemoteFingerprint { get; set; }
  public long BaselineLocalModifiedAt { get; set; }
  public bool HasConflict { get; set; }
}

internal static class DesktopIdentity
{
  private static readonly object Gate = new();
  private static string? _deviceId;
  private static string IdentityPath => Path.Combine(BrowserStateStore.StateDirectory, "device-id");

  internal static string DeviceId
  {
    get
    {
      lock (Gate)
      {
        if (_deviceId is not null) return _deviceId;
        try
        {
          if (File.Exists(IdentityPath))
          {
            var stored = File.ReadAllText(IdentityPath).Trim();
            if (stored.StartsWith("windows-", StringComparison.Ordinal) && stored.Length <= 128)
              return _deviceId = stored;
          }
        }
        catch
        {
          // A new anonymous identifier is safer than falling back to a machine name.
        }
        _deviceId = "windows-" + Guid.NewGuid();
        Directory.CreateDirectory(BrowserStateStore.StateDirectory);
        var temporaryPath = IdentityPath + ".new";
        File.WriteAllText(temporaryPath, _deviceId, new UTF8Encoding(false));
        File.Move(temporaryPath, IdentityPath, true);
        return _deviceId;
      }
    }
  }
}

internal static class AutoSyncSettingsStore
{
  private static readonly object Gate = new();
  private static readonly byte[] Entropy = Encoding.UTF8.GetBytes("privacy-browser-desktop-auto-sync-v1");
  private static readonly JsonSerializerOptions JsonOptions = new()
  {
    PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
    WriteIndented = true,
  };
  private static string SettingsPath => Path.Combine(BrowserStateStore.StateDirectory, "auto-sync.json");

  internal static AutoSyncConfiguration Load()
  {
    lock (Gate)
    {
      try
      {
        if (!File.Exists(SettingsPath)) return new AutoSyncConfiguration();
        return JsonSerializer.Deserialize<AutoSyncConfiguration>(File.ReadAllText(SettingsPath), JsonOptions)
            ?? new AutoSyncConfiguration();
      }
      catch
      {
        return new AutoSyncConfiguration();
      }
    }
  }

  internal static string? UnprotectPassphrase(AutoSyncConfiguration configuration)
  {
    if (string.IsNullOrWhiteSpace(configuration.ProtectedPassphrase)) return null;
    try
    {
      var protectedBytes = Convert.FromBase64String(configuration.ProtectedPassphrase);
      var plaintext = ProtectedData.Unprotect(protectedBytes, Entropy, DataProtectionScope.CurrentUser);
      try
      {
        return Encoding.UTF8.GetString(plaintext);
      }
      finally
      {
        CryptographicOperations.ZeroMemory(plaintext);
      }
    }
    catch
    {
      return null;
    }
  }

  internal static void Configure(
      string syncPath,
      string passphrase,
      string baselineFingerprint,
      long baselineLocalModifiedAt)
  {
    var plaintext = Encoding.UTF8.GetBytes(passphrase);
    try
    {
      var protectedBytes = ProtectedData.Protect(plaintext, Entropy, DataProtectionScope.CurrentUser);
      Save(new AutoSyncConfiguration
      {
        Enabled = true,
        SyncPath = Path.GetFullPath(syncPath),
        ProtectedPassphrase = Convert.ToBase64String(protectedBytes),
        BaselineFingerprint = baselineFingerprint,
        BaselineLocalModifiedAt = baselineLocalModifiedAt,
      });
    }
    finally
    {
      CryptographicOperations.ZeroMemory(plaintext);
    }
  }

  internal static void Disable()
  {
    lock (Gate)
    {
      try { File.Delete(SettingsPath); } catch { }
    }
  }

  internal static void UpdateBaseline(AutoSyncConfiguration configuration, string fingerprint, long localModifiedAt)
  {
    configuration.BaselineFingerprint = fingerprint;
    configuration.PendingRemoteFingerprint = null;
    configuration.BaselineLocalModifiedAt = localModifiedAt;
    configuration.HasConflict = false;
    Save(configuration);
  }

  internal static void MarkConflict(AutoSyncConfiguration configuration)
  {
    if (configuration.HasConflict) return;
    configuration.HasConflict = true;
    Save(configuration);
  }

  internal static void ObserveRemoteFingerprint(AutoSyncConfiguration configuration, string fingerprint)
  {
    configuration.PendingRemoteFingerprint = fingerprint;
    Save(configuration);
  }

  private static void Save(AutoSyncConfiguration configuration)
  {
    lock (Gate)
    {
      Directory.CreateDirectory(BrowserStateStore.StateDirectory);
      var temporaryPath = SettingsPath + ".new";
      File.WriteAllText(temporaryPath, JsonSerializer.Serialize(configuration, JsonOptions), new UTF8Encoding(false));
      File.Move(temporaryPath, SettingsPath, true);
    }
  }
}

internal sealed record AutoSyncOutcome(
    AutoSyncDecision Decision,
    SyncSnapshot? Imported = null,
    string? Fingerprint = null,
    string? Error = null);

internal static class DesktopAutoSync
{
  internal static AutoSyncOutcome Run(SyncSnapshot localSnapshot)
  {
    var configuration = AutoSyncSettingsStore.Load();
    if (!configuration.Enabled || string.IsNullOrWhiteSpace(configuration.SyncPath))
      return new AutoSyncOutcome(AutoSyncDecision.NotReady);
    if (configuration.HasConflict) return new AutoSyncOutcome(AutoSyncDecision.Conflict);
    var passphrase = AutoSyncSettingsStore.UnprotectPassphrase(configuration);
    if (string.IsNullOrEmpty(passphrase))
      return new AutoSyncOutcome(AutoSyncDecision.NotReady, Error: "자동 동기화 암호를 열 수 없습니다.");
    try
    {
      var fingerprint = SyncFileFingerprint.TryCompute(configuration.SyncPath);
      var decision = AutoSyncDecisionEngine.Decide(
          configuration.BaselineFingerprint,
          fingerprint,
          configuration.BaselineLocalModifiedAt,
          localSnapshot.ModifiedAtEpochMs);
      switch (decision)
      {
        case AutoSyncDecision.Export:
          SyncCodec.Write(configuration.SyncPath, localSnapshot.Copy(), passphrase);
          fingerprint = SyncFileFingerprint.Compute(configuration.SyncPath);
          AutoSyncSettingsStore.UpdateBaseline(configuration, fingerprint, localSnapshot.ModifiedAtEpochMs);
          break;
        case AutoSyncDecision.Import:
          if (!string.Equals(configuration.PendingRemoteFingerprint, fingerprint, StringComparison.Ordinal))
          {
            AutoSyncSettingsStore.ObserveRemoteFingerprint(configuration, fingerprint!);
            return new AutoSyncOutcome(AutoSyncDecision.NotReady);
          }
          var imported = SyncCodec.Read(configuration.SyncPath, passphrase);
          return new AutoSyncOutcome(decision, imported, fingerprint);
        case AutoSyncDecision.Conflict:
          AutoSyncSettingsStore.MarkConflict(configuration);
          break;
      }
      return new AutoSyncOutcome(decision);
    }
    catch (Exception error)
    {
      AutoSyncSettingsStore.MarkConflict(configuration);
      return new AutoSyncOutcome(AutoSyncDecision.Conflict, Error: error.Message);
    }
  }
}

internal static class SyncFileFingerprint
{
  private const long MaximumFileBytes = 48L * 1024 * 1024;

  internal static string? TryCompute(string path)
  {
    try { return Compute(path); } catch { return null; }
  }

  internal static string Compute(string path)
  {
    var file = new FileInfo(path);
    if (!file.Exists || file.Length > MaximumFileBytes) throw new InvalidDataException("올바른 동기화 파일이 아닙니다.");
    using var stream = File.OpenRead(path);
    return Convert.ToHexString(SHA256.HashData(stream)).ToLowerInvariant();
  }
}
