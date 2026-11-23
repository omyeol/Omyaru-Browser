using System.Text.Json;
using System.Text.Json.Serialization;

namespace PrivacyBrowser.Desktop;

internal sealed record ProfileEntry(
    string Id,
    string Name,
    string Icon,
    uint Color)
{
  public override string ToString() => $"{ProfileGlyph(Icon)}  {Name}";

  private static string ProfileGlyph(string icon) => icon.ToUpperInvariant() switch
  {
    "WORK" => "▣",
    "STUDY" => "▤",
    "TRAVEL" => "✈",
    "GAMING" => "◆",
    "STAR" => "★",
    _ => "●",
  };
}

internal sealed record TabGroupEntry(string Id, string ProfileId, string Name)
{
  public override string ToString() => Name;
}

internal sealed record TabEntry(
    string Id,
    string Title,
    string Url,
    string ProfileId,
    string? GroupId,
    int Order = int.MaxValue,
    string? ThumbnailBase64 = null)
{
  public override string ToString() => Title;
}

internal sealed class SyncSnapshot
{
  internal const int MaxProfiles = 32;
  internal const int MaxGroupsPerProfile = 100;
  internal const int MaxTabs = 500;
  public int SchemaVersion { get; set; } = 2;
  public long ModifiedAtEpochMs { get; set; } = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds();
  public string DeviceId { get; set; } = "windows-" + Guid.NewGuid();
  public List<ProfileEntry> Profiles { get; set; } = [];
  public List<TabGroupEntry> Groups { get; set; } = [];
  public List<TabEntry> Tabs { get; set; } = [];
  public string ActiveProfileId { get; set; } = "default";
  public string? ActiveGroupId { get; set; }
  public string? ActiveTabId { get; set; }

  public static SyncSnapshot CreateDefault() => new()
  {
    Profiles = [new ProfileEntry("default", "개인", "PERSON", 0xFF6D8BFF)],
    Tabs = [new TabEntry(Guid.NewGuid().ToString("N"), "새 탭", "about:blank", "default", null, 0)],
  };

  public SyncSnapshot Copy(bool includeThumbnails = true) => new()
  {
    SchemaVersion = SchemaVersion,
    ModifiedAtEpochMs = ModifiedAtEpochMs,
    DeviceId = DeviceId,
    Profiles = [.. Profiles],
    Groups = [.. Groups],
    Tabs = Tabs.Select(tab => includeThumbnails ? tab : tab with { ThumbnailBase64 = null }).ToList(),
    ActiveProfileId = ActiveProfileId,
    ActiveGroupId = ActiveGroupId,
    ActiveTabId = ActiveTabId,
  };

  public void Normalize()
  {
    var sourceSchemaVersion = SchemaVersion;
    Profiles = Profiles
        .Where(profile => !string.IsNullOrWhiteSpace(profile.Id))
        .DistinctBy(profile => profile.Id)
        .Take(MaxProfiles)
        .Select(profile => profile with { Name = NormalizeName(profile.Name, "Profile") })
        .ToList();
    if (Profiles.Count == 0) Profiles.Add(new ProfileEntry("default", "개인", "PERSON", 0xFF6D8BFF));

    var profileIds = Profiles.Select(profile => profile.Id).ToHashSet(StringComparer.Ordinal);
    Groups = Groups
        .Where(group => profileIds.Contains(group.ProfileId) && !string.IsNullOrWhiteSpace(group.Id))
        .DistinctBy(group => group.Id)
        .GroupBy(group => group.ProfileId)
        .SelectMany(group => group.Take(MaxGroupsPerProfile))
        .Select(group => group with { Name = NormalizeName(group.Name, "Tab group") })
        .ToList();
    var groups = Groups.ToDictionary(group => group.Id, StringComparer.Ordinal);

    ActiveProfileId = profileIds.Contains(ActiveProfileId) ? ActiveProfileId : Profiles[0].Id;
    ActiveGroupId = ActiveGroupId is not null &&
        groups.TryGetValue(ActiveGroupId, out var activeGroup) &&
        activeGroup.ProfileId == ActiveProfileId
            ? ActiveGroupId
            : null;

    Tabs = Tabs
        .Select((tab, index) => (Tab: tab, SourceIndex: index))
        .Where(item => !string.IsNullOrWhiteSpace(item.Tab.Id))
        .DistinctBy(item => item.Tab.Id)
        .Take(MaxTabs)
        .OrderBy(item => sourceSchemaVersion >= 2 ? Math.Max(0, item.Tab.Order) : item.SourceIndex)
        .ThenBy(item => item.SourceIndex)
        .Select((item, order) =>
        {
          var tab = item.Tab;
          var profileId = profileIds.Contains(tab.ProfileId) ? tab.ProfileId : ActiveProfileId;
          var groupId = tab.GroupId is not null && groups.TryGetValue(tab.GroupId, out var group) &&
                  group.ProfileId == profileId ? tab.GroupId : null;
          return tab with
          {
            Title = string.IsNullOrWhiteSpace(tab.Title) ? "새 탭" : tab.Title[..Math.Min(tab.Title.Length, 200)],
            Url = NormalizeUrl(tab.Url),
            ProfileId = profileId,
            GroupId = groupId,
            Order = order,
            ThumbnailBase64 = tab.ThumbnailBase64 is { Length: > 0 and <= 716_800 }
                ? tab.ThumbnailBase64
                : null,
          };
        })
        .ToList();
    ActiveTabId = Tabs.Any(tab => tab.Id == ActiveTabId) ? ActiveTabId : null;
    SchemaVersion = 2;
  }

  internal static string NormalizeUrl(string? candidate)
  {
    var value = (candidate ?? string.Empty).Trim();
    if (value.Equals("about:blank", StringComparison.OrdinalIgnoreCase)) return "about:blank";
    if (Uri.TryCreate(value, UriKind.Absolute, out var uri) &&
        (uri.Scheme == Uri.UriSchemeHttps || uri.Scheme == Uri.UriSchemeHttp)) return value[..Math.Min(value.Length, 8192)];
    return "about:blank";
  }

  private static string NormalizeName(string? value, string fallback)
  {
    var normalized = string.Join(' ', (value ?? string.Empty).Split((char[]?)null, StringSplitOptions.RemoveEmptyEntries));
    if (string.IsNullOrWhiteSpace(normalized)) normalized = fallback;
    return normalized[..Math.Min(normalized.Length, 40)];
  }
}

#if !BOOKSHELF_STANDALONE
internal static class BrowserStateStore
{
  private static string? _stateDirectoryOverride;
  private static readonly JsonSerializerOptions JsonOptions = new()
  {
    PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
    WriteIndented = true,
  };

  internal static string StateDirectory => _stateDirectoryOverride ?? Path.Combine(
      Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
      "Omyaru Browser");
  internal static bool IsTestOverride => _stateDirectoryOverride is not null;

  internal static void UseStateDirectoryForTests(string path) => _stateDirectoryOverride = path;

  private static string StatePath => Path.Combine(StateDirectory, "desktop-state.json");

  internal static SyncSnapshot Load()
  {
    try
    {
      if (!File.Exists(StatePath)) return SyncSnapshot.CreateDefault();
      var state = JsonSerializer.Deserialize<SyncSnapshot>(File.ReadAllText(StatePath), JsonOptions)
          ?? SyncSnapshot.CreateDefault();
      state.Normalize();
      return state;
    }
    catch
    {
      return SyncSnapshot.CreateDefault();
    }
  }

  internal static void SaveChanged(SyncSnapshot state)
  {
    state.ModifiedAtEpochMs = Math.Max(
        DateTimeOffset.UtcNow.ToUnixTimeMilliseconds(),
        state.ModifiedAtEpochMs + 1);
    state.DeviceId = DesktopIdentity.DeviceId;
    WriteState(state);
  }

  internal static void SaveImported(SyncSnapshot state)
  {
    WriteState(state);
  }

  private static void WriteState(SyncSnapshot state)
  {
    state.Normalize();
    var persisted = state.Copy(includeThumbnails: false);
    Directory.CreateDirectory(StateDirectory);
    var temporaryPath = StatePath + ".new";
    File.WriteAllText(temporaryPath, JsonSerializer.Serialize(persisted, JsonOptions));
    File.Move(temporaryPath, StatePath, true);
  }
}
#endif
