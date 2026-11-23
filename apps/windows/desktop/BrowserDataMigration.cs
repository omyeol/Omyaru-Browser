using System.Text.Json;

namespace PrivacyBrowser.Desktop;

internal static class BrowserDataMigration
{
  internal static void MoveLegacyState()
  {
    var oldRoot = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "PrivacyBrowser");
    var newRoot = BrowserStateStore.StateDirectory;
    Move(oldRoot, newRoot);
  }

  internal static void Move(string oldRoot, string newRoot)
  {
    if (Directory.Exists(oldRoot))
    {
      if (!File.Exists(Path.Combine(oldRoot, "desktop-state.json")))
        throw new InvalidDataException("기존 브라우저 상태 파일을 찾지 못했습니다. 자동 이동을 중단했습니다.");
      string? archived = null;
      if (Directory.Exists(newRoot))
      {
        if (Directory.EnumerateFileSystemEntries(newRoot).Any())
        {
          archived = newRoot + ".previous-" + DateTime.UtcNow.ToString("yyyyMMddHHmmss") + "-" + Guid.NewGuid().ToString("N") + ".backup";
          Directory.Move(newRoot, archived);
        }
        else Directory.Delete(newRoot);
      }
      try { Directory.Move(oldRoot, newRoot); }
      catch
      {
        if (archived is not null && !Directory.Exists(newRoot)) Directory.Move(archived, newRoot);
        throw;
      }
    }
    if (!Directory.Exists(newRoot)) return;
    // Same-volume rename keeps profiles, protected OAuth state, and files together.
    var oldShelf = Path.Combine(oldRoot, "bookshelf");
    var newShelf = Path.Combine(newRoot, "bookshelf");
    var location = Path.Combine(newRoot, "bookshelf-location.json");
    if (File.Exists(location))
    {
      using var doc = JsonDocument.Parse(File.ReadAllText(location));
      var configured = doc.RootElement.TryGetProperty("storageDirectory", out var element) ? element.GetString() : null;
      if (!string.IsNullOrWhiteSpace(configured) && Path.GetFullPath(configured).Equals(oldShelf, StringComparison.OrdinalIgnoreCase))
        File.WriteAllText(location, JsonSerializer.Serialize(new { storageDirectory = newShelf }));
    }
    var catalogPath = Path.Combine(newShelf, "catalog.json");
    if (!File.Exists(catalogPath)) return;
    var catalog = JsonSerializer.Deserialize<BookshelfCatalog>(File.ReadAllText(catalogPath), BookshelfSyncReplica.Json)
        ?? throw new InvalidDataException("기존 책장 목록을 읽지 못했습니다.");
    var changed = false;
    catalog.Items = catalog.Items.Select(item =>
    {
      if (!item.FilePath.StartsWith(oldShelf + Path.DirectorySeparatorChar, StringComparison.OrdinalIgnoreCase)) return item;
      changed = true;
      return item with { FilePath = newShelf + item.FilePath[oldShelf.Length..] };
    }).ToList();
    if (!changed) return;
    File.Copy(catalogPath, catalogPath + ".before-omyaru-move", true);
    File.WriteAllText(catalogPath + ".new", JsonSerializer.Serialize(catalog, BookshelfSyncReplica.Json));
    File.Move(catalogPath + ".new", catalogPath, true);
  }
}
