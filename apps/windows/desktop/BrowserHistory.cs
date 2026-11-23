using System.Text.Json;

namespace PrivacyBrowser.Desktop;

internal sealed record BrowserHistoryEntry(string Id, string Url, string Title, long VisitedAtEpochMs, int VisitCount);

internal static class BrowserHistory
{
  private static readonly object Gate = new();
  private static string PathName => Path.Combine(BrowserStateStore.StateDirectory, "browser-history.json");
  private static readonly JsonSerializerOptions Json = new() { PropertyNamingPolicy = JsonNamingPolicy.CamelCase };
  internal static List<BrowserHistoryEntry> Load()
  {
    lock (Gate)
    {
      if (!File.Exists(PathName)) return [];
      return JsonSerializer.Deserialize<List<BrowserHistoryEntry>>(File.ReadAllText(PathName), Json) ?? [];
    }
  }
  private static void Save(List<BrowserHistoryEntry> entries)
  {
    Directory.CreateDirectory(BrowserStateStore.StateDirectory);
    File.WriteAllText(PathName + ".new", JsonSerializer.Serialize(entries.Take(2000), Json));
    File.Move(PathName + ".new", PathName, true);
  }
  internal static void Record(string url, string title)
  {
    if (!Uri.TryCreate(url, UriKind.Absolute, out var uri) || uri.Scheme is not ("http" or "https")) return;
    lock (Gate)
    {
      var entries = Load();
      var index = entries.FindIndex(x => x.Url.Equals(url, StringComparison.OrdinalIgnoreCase));
      var previous = index >= 0 ? entries[index] : null;
      if (index >= 0) entries.RemoveAt(index);
      entries.Insert(0, new(previous?.Id ?? Guid.NewGuid().ToString(), url,
          string.IsNullOrWhiteSpace(title) ? url : title, DateTimeOffset.UtcNow.ToUnixTimeMilliseconds(),
          (previous?.VisitCount ?? 0) + 1));
      Save(entries);
    }
  }
  internal static void Merge(IEnumerable<BrowserHistoryEntry> incoming)
  {
    lock (Gate)
    {
      var byUrl = Load().ToDictionary(x => x.Url, StringComparer.OrdinalIgnoreCase);
      foreach (var entry in incoming)
      {
        if (!Uri.TryCreate(entry.Url, UriKind.Absolute, out var uri) || uri.Scheme is not ("http" or "https")) continue;
        if (!byUrl.TryGetValue(entry.Url, out var old)) { byUrl[entry.Url] = entry; continue; }
        byUrl[entry.Url] = old with {
          Title = entry.VisitedAtEpochMs > old.VisitedAtEpochMs ? entry.Title : old.Title,
          VisitedAtEpochMs = Math.Max(entry.VisitedAtEpochMs, old.VisitedAtEpochMs),
          VisitCount = Math.Max(entry.VisitCount, old.VisitCount),
        };
      }
      Save(byUrl.Values.OrderByDescending(x => x.VisitedAtEpochMs).Take(2000).ToList());
    }
  }
}
