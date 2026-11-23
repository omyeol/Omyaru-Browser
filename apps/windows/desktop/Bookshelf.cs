using System.Drawing.Imaging;
using System.Drawing.Drawing2D;
using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;

namespace PrivacyBrowser.Desktop;

internal sealed record BookshelfItem(
    string Id,
    string Folder,
    string FilePath,
    string SourceUrl,
    int Width,
    int Height,
    long CreatedAtEpochMs);

internal sealed class BookshelfCatalog
{
  public Dictionary<string, string> FolderIds { get; set; } = new(StringComparer.OrdinalIgnoreCase);
  public List<string> Folders { get; set; } = [BookshelfStore.DefaultFolder];
  public string ActiveFolder { get; set; } = BookshelfStore.DefaultFolder;
  public Dictionary<string, int> FolderColumnCounts { get; set; } = new(StringComparer.CurrentCultureIgnoreCase)
  {
    [BookshelfStore.DefaultFolder] = 2,
  };
  public Dictionary<string, string> FolderNotes { get; set; } = new(StringComparer.CurrentCultureIgnoreCase)
  {
    [BookshelfStore.DefaultFolder] = string.Empty,
  };
  public List<BookshelfItem> Items { get; set; } = [];
}

internal static class BookshelfStore
{
  internal const string DefaultFolder = "기본 책장";
  private sealed class BookshelfLocationSettings
  {
    public string StorageDirectory { get; set; } = string.Empty;
  }

  private static readonly JsonSerializerOptions JsonOptions = new()
  {
    PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
    WriteIndented = true,
  };
  private static readonly object LocationGate = new();
  private static string _cachedStateDirectory = string.Empty;
  private static string _cachedRootDirectory = string.Empty;

  private static string DefaultRootDirectory => Path.GetFullPath(Path.Combine(BrowserStateStore.StateDirectory, "bookshelf"));
  private static string LocationSettingsPath => Path.Combine(BrowserStateStore.StateDirectory, "bookshelf-location.json");
  private static string RootDirectory
  {
    get
    {
      lock (LocationGate)
      {
        var stateDirectory = Path.GetFullPath(BrowserStateStore.StateDirectory);
        if (_cachedStateDirectory.Equals(stateDirectory, StringComparison.OrdinalIgnoreCase) &&
            !string.IsNullOrWhiteSpace(_cachedRootDirectory)) return _cachedRootDirectory;
        _cachedStateDirectory = stateDirectory;
        _cachedRootDirectory = LoadConfiguredRootDirectory();
        return _cachedRootDirectory;
      }
    }
  }
  private static string CatalogPath => Path.Combine(RootDirectory, "catalog.json");
  private static string ExplicitEmptyMarker => Path.Combine(RootDirectory, ".sync", "explicit-empty.marker");
  internal static bool ExplicitEmptyCatalog => File.Exists(ExplicitEmptyMarker);
  internal static void ClearExplicitEmptyMarker() { if (File.Exists(ExplicitEmptyMarker)) File.Delete(ExplicitEmptyMarker); }

  internal static string StorageDirectory => RootDirectory;
  internal static string DefaultStorageDirectory => DefaultRootDirectory;
  internal static int MissingOriginalCount(BookshelfCatalog catalog) =>
      catalog.Items.Count(item => !File.Exists(item.FilePath));
  internal static bool ConfiguredCatalogUnavailable =>
      !RootDirectory.Equals(DefaultRootDirectory, StringComparison.OrdinalIgnoreCase) && !File.Exists(CatalogPath);
  private static void RequireConfiguredCatalog()
  {
    if (ConfiguredCatalogUnavailable)
      throw new IOException("선택한 책장 저장 위치 또는 목록에 접근할 수 없습니다. 저장 매체를 다시 연결해 주세요.");
  }

  internal static BookshelfCatalog Load()
  {
    try
    {
      SyncWithRedirectedSandboxCatalog();
      if (!File.Exists(CatalogPath)) return Normalize(new BookshelfCatalog());
      var catalog = Normalize(JsonSerializer.Deserialize<BookshelfCatalog>(File.ReadAllText(CatalogPath), JsonOptions)
          ?? new BookshelfCatalog());
      if (SelfHealItemPaths(catalog))
      {
        try { SaveToDirectory(catalog, RootDirectory); } catch { }
      }
      return catalog;
    }
    catch
    {
      return Normalize(new BookshelfCatalog());
    }
  }

  internal static void SyncWithRedirectedSandboxCatalog()
  {
    if (BrowserStateStore.IsTestOverride) return;
    try
    {
      var localAppData = Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData);
      var packagesDir = Path.Combine(localAppData, "Packages");
      if (!Directory.Exists(packagesDir)) return;
      var shelfNames = new[] { "Omyaru Browser", "OmyaruBookshelf" };
      foreach (var pkg in Directory.EnumerateDirectories(packagesDir, "*Codex*"))
      {
        foreach (var shelfName in shelfNames)
        {
          var candidateCatalog = Path.Combine(pkg, "LocalCache", "Local", shelfName, "bookshelf", "catalog.json");
          if (!File.Exists(candidateCatalog)) continue;
          if (!File.Exists(CatalogPath))
          {
            Directory.CreateDirectory(Path.GetDirectoryName(CatalogPath)!);
            File.Copy(candidateCatalog, CatalogPath, true);
            return;
          }
          var realBytes = File.ReadAllBytes(CatalogPath);
          var candidateBytes = File.ReadAllBytes(candidateCatalog);
          if (realBytes.AsSpan().SequenceEqual(candidateBytes)) continue;
          var candidateCat = JsonSerializer.Deserialize<BookshelfCatalog>(candidateBytes, JsonOptions);
          if (candidateCat is null || candidateCat.Items.Count == 0) continue;
          var candidateMissing = candidateCat.Items.Count(item => !File.Exists(item.FilePath));
          var realCat = JsonSerializer.Deserialize<BookshelfCatalog>(realBytes, JsonOptions);
          var realMissing = realCat is null ? int.MaxValue : realCat.Items.Count(item => !File.Exists(item.FilePath));
          if (candidateMissing < realMissing || (candidateMissing == realMissing && File.GetLastWriteTimeUtc(candidateCatalog) > File.GetLastWriteTimeUtc(CatalogPath)))
          {
            var backup = CatalogPath + ".before-sandbox-sync-" + DateTimeOffset.UtcNow.ToString("yyyyMMddHHmmss") + ".bak";
            File.Copy(CatalogPath, backup, true);
            File.Copy(candidateCatalog, CatalogPath, true);
            return;
          }
        }
      }
    }
    catch { }
  }

  internal static bool TryResolveItemPath(BookshelfItem item, string rootDirectory, out string resolvedPath)
  {
    if (File.Exists(item.FilePath))
    {
      resolvedPath = item.FilePath;
      return true;
    }
    try
    {
      var folderDirectory = ShelfDirectory(rootDirectory, item.Folder);
      if (!Directory.Exists(folderDirectory))
      {
        var leaf = BookshelfPath.Leaf(item.Folder);
        var alt = Path.Combine(rootDirectory, leaf);
        if (Directory.Exists(alt)) folderDirectory = alt;
      }
      if (Directory.Exists(folderDirectory))
      {
        var fileName = Path.GetFileName(item.FilePath);
        if (!string.IsNullOrWhiteSpace(fileName))
        {
          var direct = Path.Combine(folderDirectory, fileName);
          if (File.Exists(direct)) { resolvedPath = direct; return true; }
        }
        var guidPng = Path.Combine(folderDirectory, item.Id + ".png");
        if (File.Exists(guidPng)) { resolvedPath = guidPng; return true; }
        var guidCloud = Path.Combine(folderDirectory, item.Id + ".cloud");
        if (File.Exists(guidCloud)) { resolvedPath = guidCloud; return true; }

        if (!string.IsNullOrWhiteSpace(fileName))
        {
          var baseName = Path.GetFileNameWithoutExtension(fileName);
          var altPng = Path.Combine(folderDirectory, baseName + ".png");
          if (File.Exists(altPng)) { resolvedPath = altPng; return true; }
          var altCloud = Path.Combine(folderDirectory, baseName + ".cloud");
          if (File.Exists(altCloud)) { resolvedPath = altCloud; return true; }
        }
      }

      var localAppData = Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData);
      var defaultBrowserShelf = Path.Combine(localAppData, "Omyaru Browser", "bookshelf");
      if (!rootDirectory.Equals(defaultBrowserShelf, StringComparison.OrdinalIgnoreCase))
      {
        var altFolderDir = ShelfDirectory(defaultBrowserShelf, item.Folder);
        if (Directory.Exists(altFolderDir))
        {
          var fileName = Path.GetFileName(item.FilePath);
          if (!string.IsNullOrWhiteSpace(fileName))
          {
            var altDirect = Path.Combine(altFolderDir, fileName);
            if (File.Exists(altDirect)) { resolvedPath = altDirect; return true; }
          }
          var altGuid = Path.Combine(altFolderDir, item.Id + ".png");
          if (File.Exists(altGuid)) { resolvedPath = altGuid; return true; }
        }
      }
    }
    catch { }
    resolvedPath = item.FilePath;
    return false;
  }

  internal static bool SelfHealItemPaths(BookshelfCatalog catalog)
  {
    var changed = false;
    for (var i = 0; i < catalog.Items.Count; i++)
    {
      var item = catalog.Items[i];
      if (File.Exists(item.FilePath)) continue;
      if (TryResolveItemPath(item, RootDirectory, out var resolved) && !resolved.Equals(item.FilePath, StringComparison.OrdinalIgnoreCase))
      {
        catalog.Items[i] = item with { FilePath = resolved };
        changed = true;
      }
    }
    foreach (var folder in catalog.Folders)
    {
      var folderItems = catalog.Items.Where(it => string.Equals(it.Folder, folder, StringComparison.OrdinalIgnoreCase)).ToList();
      if (folderItems.Count == 0 || folderItems.All(it => File.Exists(it.FilePath))) continue;
      try
      {
        var folderDir = ShelfDirectory(RootDirectory, folder);
        if (!Directory.Exists(folderDir))
        {
          var leaf = BookshelfPath.Leaf(folder);
          var alt = Path.Combine(RootDirectory, leaf);
          if (Directory.Exists(alt)) folderDir = alt;
        }
        if (!Directory.Exists(folderDir)) continue;
        for (var idx = 0; idx < folderItems.Count; idx++)
        {
          var it = folderItems[idx];
          if (File.Exists(it.FilePath)) continue;
          var numCandidate = Path.Combine(folderDir, $"image{idx + 1}.png");
          if (File.Exists(numCandidate))
          {
            var itemIdx = catalog.Items.FindIndex(x => x.Id == it.Id);
            if (itemIdx >= 0)
            {
              catalog.Items[itemIdx] = it with { FilePath = numCandidate };
              changed = true;
            }
            continue;
          }
          var cloudCandidate = Path.Combine(folderDir, $"image{idx + 1}.cloud");
          if (File.Exists(cloudCandidate))
          {
            var itemIdx = catalog.Items.FindIndex(x => x.Id == it.Id);
            if (itemIdx >= 0)
            {
              catalog.Items[itemIdx] = it with { FilePath = cloudCandidate };
              changed = true;
            }
          }
        }
      }
      catch { }
    }
    return changed;
  }

  internal static void Save(BookshelfCatalog catalog)
  {
    RequireConfiguredCatalog();
    if (catalog.Items.Count == 0 && File.Exists(CatalogPath))
    {
      var previous = JsonSerializer.Deserialize<BookshelfCatalog>(File.ReadAllText(CatalogPath), JsonOptions);
      if (previous?.Items.Count > 0)
      {
        Directory.CreateDirectory(Path.GetDirectoryName(ExplicitEmptyMarker)!);
        File.WriteAllText(ExplicitEmptyMarker, "1");
      }
    }
    SaveToDirectory(catalog, RootDirectory);
  }

  internal static bool TryMigrateImageNames(BookshelfCatalog catalog)
  {
    if (catalog.Items.Count == 0 || MissingOriginalCount(catalog) > 0 || ConfiguredCatalogUnavailable)
      return false;
    if (HasCurrentImageNames(catalog))
      return false;
    try { Save(catalog); return true; }
    catch (IOException) { return false; }
    catch (UnauthorizedAccessException) { return false; }
  }

  internal static bool HasCurrentImageNames(BookshelfCatalog catalog) =>
      catalog.Items.All(item => IsNumberedFile(item.FilePath, ShelfDirectory(RootDirectory, item.Folder)));

  internal static void RecoverUnavailableStorage(string requestedDirectory)
  {
    if (!ConfiguredCatalogUnavailable)
      throw new IOException("현재 책장 저장 위치에 접근할 수 있습니다. 저장 위치 변경을 사용해 주세요.");
    var target = NormalizeDirectoryPath(requestedDirectory);
    if (target.Equals(RootDirectory, StringComparison.OrdinalIgnoreCase))
      throw new IOException("선택한 책장 저장 위치가 아직 연결되지 않았습니다.");
    if (target.Equals(DefaultRootDirectory, StringComparison.OrdinalIgnoreCase))
      Directory.CreateDirectory(target);
    else if (!File.Exists(Path.Combine(target, "catalog.json")))
      throw new IOException("선택한 폴더에 기존 책장 목록(catalog.json)이 없습니다.");
    var targetCatalog = Path.Combine(target, "catalog.json");
    if (File.Exists(targetCatalog) &&
        JsonSerializer.Deserialize<BookshelfCatalog>(File.ReadAllText(targetCatalog), JsonOptions) is null)
      throw new IOException("선택한 책장 목록을 읽지 못했습니다.");
    var backup = LocationSettingsPath + ".unavailable-" + Guid.NewGuid().ToString("N") + ".bak";
    File.Copy(LocationSettingsPath, backup);
    SetConfiguredRootDirectory(target);
  }

  internal static void MoveStorage(BookshelfCatalog catalog, string requestedDirectory)
  {
    RequireConfiguredCatalog();
    if (string.IsNullOrWhiteSpace(requestedDirectory)) throw new ArgumentException("새 저장 위치가 비어 있습니다.");
    var source = NormalizeDirectoryPath(RootDirectory);
    var destination = NormalizeDirectoryPath(requestedDirectory.Trim());
    if (source.Equals(destination, StringComparison.OrdinalIgnoreCase)) return;
    if (IsSubdirectory(destination, source) || IsSubdirectory(source, destination))
      throw new IOException("현재 책장 폴더의 내부 또는 상위 폴더는 새 위치로 사용할 수 없습니다.");

    var destinationExisted = Directory.Exists(destination);
    if (destinationExisted && Directory.EnumerateFileSystemEntries(destination).Any())
      throw new IOException("새 저장 위치로는 비어 있는 폴더를 선택해 주세요.");

    var originalItems = catalog.Items.ToList();
    try
    {
      Directory.CreateDirectory(destination);
      if (Directory.Exists(source)) CopyDirectoryVerified(source, destination);
      catalog.Items = catalog.Items.Select(item => item with
      {
        FilePath = RelocateItemPath(item.FilePath, source, destination),
      }).ToList();
      SaveToDirectory(catalog, destination);
      VerifyMigratedCatalog(catalog, destination);
      SetConfiguredRootDirectory(destination);

      try
      {
        if (Directory.Exists(source)) Directory.Delete(source, true);
      }
      catch
      {
        SetConfiguredRootDirectory(source);
        catalog.Items = originalItems;
        ResetDestination(destination, destinationExisted);
        throw;
      }
    }
    catch
    {
      catalog.Items = originalItems;
      if (RootDirectory.Equals(destination, StringComparison.OrdinalIgnoreCase))
        SetConfiguredRootDirectory(source);
      ResetDestination(destination, destinationExisted);
      throw;
    }
  }

  internal static string AddFolder(BookshelfCatalog catalog, string requestedName)
  {
    var normalized = BookshelfPath.Normalize(string.IsNullOrWhiteSpace(requestedName) ? DefaultFolder : requestedName) ?? throw new ArgumentException("사용할 수 없는 폴더 이름입니다.");
    if (!catalog.Folders.Contains(normalized, StringComparer.CurrentCultureIgnoreCase)) catalog.Folders.Add(normalized);
    catalog.ActiveFolder = catalog.Folders.First(folder => folder.Equals(normalized, StringComparison.CurrentCultureIgnoreCase));
    if (!catalog.FolderColumnCounts.ContainsKey(catalog.ActiveFolder)) catalog.FolderColumnCounts[catalog.ActiveFolder] = 2;
    if (!catalog.FolderNotes.ContainsKey(catalog.ActiveFolder)) catalog.FolderNotes[catalog.ActiveFolder] = string.Empty;
    Save(catalog);
    return catalog.ActiveFolder;
  }

  internal static bool MoveFolder(BookshelfCatalog catalog, string folder, int direction)
  {
    var reordered = BookshelfPath.MoveSibling(catalog.Folders, folder, direction);
    if (reordered is null) return false;
    catalog.Folders = reordered;
    Save(catalog);
    return true;
  }

  internal static List<BookshelfItem> VisibleItems(BookshelfCatalog catalog, string folder)
  {
    var result = new List<BookshelfItem>();
    void Visit(string path)
    {
      result.AddRange(catalog.Items.Where(item => item.Folder.Equals(path, StringComparison.OrdinalIgnoreCase)));
      foreach (var child in BookshelfPath.Children(catalog.Folders, path)) Visit(child);
    }
    Visit(folder);
    return result;
  }

  internal static bool RenameFolder(BookshelfCatalog catalog, string folder, string requestedName)
  {
    var moves = BookshelfPath.Relocation(catalog.Folders, folder, requestedName);
    if (moves is null) return false;
    var ids = moves.Keys.ToDictionary(p => p, p => catalog.FolderIds.GetValueOrDefault(p, Guid.NewGuid().ToString("N")));
    var notes = moves.Keys.ToDictionary(p => p, p => catalog.FolderNotes.GetValueOrDefault(p, ""));
    var columns = moves.Keys.ToDictionary(p => p, p => GetColumnCount(catalog, p));
    foreach (var (old, renamed) in moves)
    {
      catalog.Folders[catalog.Folders.IndexOf(old)] = renamed;
      catalog.FolderIds.Remove(old); catalog.FolderIds[renamed] = ids[old];
      catalog.FolderNotes.Remove(old); catalog.FolderNotes[renamed] = notes[old];
      catalog.FolderColumnCounts.Remove(old); catalog.FolderColumnCounts[renamed] = columns[old];
    }
    for (var i = 0; i < catalog.Items.Count; i++)
      if (moves.TryGetValue(catalog.Items[i].Folder, out var target)) catalog.Items[i] = catalog.Items[i] with { Folder = target };
    if (moves.TryGetValue(catalog.ActiveFolder, out var active)) catalog.ActiveFolder = active;
    Save(catalog);
    return true;
  }

  internal static bool DeleteFolder(BookshelfCatalog catalog, string folder)
  {
    if (catalog.Folders.Any(p => p.StartsWith(folder + "/", StringComparison.OrdinalIgnoreCase))) return false;
    if (!catalog.Folders.Contains(folder, StringComparer.CurrentCultureIgnoreCase)) return false;
    var removedItems = catalog.Items
        .Where(item => item.Folder.Equals(folder, StringComparison.CurrentCultureIgnoreCase))
        .ToList();
    catalog.Items.RemoveAll(item => item.Folder.Equals(folder, StringComparison.CurrentCultureIgnoreCase));
    catalog.Folders.RemoveAll(candidate => candidate.Equals(folder, StringComparison.CurrentCultureIgnoreCase));
    catalog.FolderColumnCounts.Remove(folder);
    catalog.FolderNotes.Remove(folder);
    if (catalog.Folders.Count == 0)
    {
      catalog.Folders.Add(DefaultFolder);
      catalog.FolderColumnCounts[DefaultFolder] = 2;
      catalog.FolderNotes[DefaultFolder] = string.Empty;
    }
    if (catalog.ActiveFolder.Equals(folder, StringComparison.CurrentCultureIgnoreCase))
      catalog.ActiveFolder = catalog.Folders[0];
    Save(catalog);
    foreach (var item in removedItems)
    {
      try { if (!catalog.Items.Any(other => string.Equals(other.FilePath, item.FilePath, StringComparison.OrdinalIgnoreCase)) && File.Exists(item.FilePath)) File.Delete(item.FilePath); } catch { }
    }
    return true;
  }

  internal static int GetColumnCount(BookshelfCatalog catalog, string folder)
      => catalog.FolderColumnCounts.TryGetValue(folder, out var count) ? Math.Clamp(count, 1, 6) : 2;

  internal static void SetColumnCount(BookshelfCatalog catalog, string folder, int count)
  {
    var key = BookshelfPath.Normalize(folder) ?? folder;
    if (string.IsNullOrWhiteSpace(key)) return;
    catalog.FolderColumnCounts[key] = Math.Clamp(count, 1, 6);
    Save(catalog);
  }

  internal static BookshelfItem AddImage(BookshelfCatalog catalog, Image image, string sourceUrl)
  {
    var folder = AddFolder(catalog, catalog.ActiveFolder);
    var directory = ShelfDirectory(RootDirectory, folder);
    Directory.CreateDirectory(directory);
    var id = Guid.NewGuid().ToString("N");
    var filePath = Path.Combine(directory, id + ".png");
    image.Save(filePath, ImageFormat.Png);
    var item = new BookshelfItem(
        id,
        folder,
        filePath,
        sourceUrl,
        image.Width,
        image.Height,
        DateTimeOffset.UtcNow.ToUnixTimeMilliseconds());
    catalog.Items.Add(item);
    Save(catalog);
    return catalog.Items.Single(saved => saved.Id == id);
  }

  internal static Image? LoadImage(BookshelfItem item)
  {
    try
    {
      if (!File.Exists(item.FilePath)) return null;
      if (CloudShelfMarker.Read(item.FilePath) is not null) return CloudShelfMarker.LoadImage(item);
      using var stream = new MemoryStream(File.ReadAllBytes(item.FilePath));
      using var source = Image.FromStream(stream);
      return new Bitmap(source);
    }
    catch
    {
      return null;
    }
  }

  internal static Image? LoadThumbnail(BookshelfItem item, int width, int height)
  {
    if (CloudShelfMarker.Read(item.FilePath) is not null) return CloudShelfMarker.LoadThumbnail(item, width, height);
    using var source = LoadImage(item);
    if (source is null) return null;
    var targetWidth = Math.Max(1, Math.Min(width, source.Width));
    var targetHeight = Math.Max(1, (int)Math.Round(targetWidth * source.Height / (double)Math.Max(1, source.Width)));
    var thumbnail = new Bitmap(targetWidth, targetHeight, PixelFormat.Format32bppPArgb);
    using var graphics = Graphics.FromImage(thumbnail);
    graphics.CompositingMode = CompositingMode.SourceCopy;
    graphics.CompositingQuality = CompositingQuality.HighQuality;
    graphics.InterpolationMode = InterpolationMode.HighQualityBicubic;
    graphics.PixelOffsetMode = PixelOffsetMode.HighQuality;
    graphics.SmoothingMode = SmoothingMode.HighQuality;
    using var attributes = new ImageAttributes();
    attributes.SetWrapMode(WrapMode.TileFlipXY);
    graphics.DrawImage(
        source,
        new Rectangle(0, 0, thumbnail.Width, thumbnail.Height),
        0,
        0,
        source.Width,
        source.Height,
        GraphicsUnit.Pixel,
        attributes);
    return thumbnail;
  }

  internal static bool RemoveImage(BookshelfCatalog catalog, BookshelfItem item)
  {
    var removed = catalog.Items.RemoveAll(candidate => candidate.Id == item.Id) > 0;
    if (!removed) return false;
    Save(catalog);
    try
    {
      if (!catalog.Items.Any(other => string.Equals(other.FilePath, item.FilePath, StringComparison.OrdinalIgnoreCase)) && File.Exists(item.FilePath)) File.Delete(item.FilePath);
    }
    catch
    {
      // The catalog removal is authoritative; an orphaned file is harmless and
      // can be cleaned up after any external image viewer releases it.
    }
    return true;
  }

  private static BookshelfCatalog Normalize(BookshelfCatalog catalog)
  {
    catalog.FolderColumnCounts ??= new Dictionary<string, int>(StringComparer.CurrentCultureIgnoreCase);
    catalog.FolderNotes ??= new Dictionary<string, string>(StringComparer.CurrentCultureIgnoreCase);
    catalog.Folders = catalog.Folders
        .Where(folder => !string.IsNullOrWhiteSpace(folder))
        .Distinct(StringComparer.CurrentCultureIgnoreCase)
        .ToList();
    if (catalog.Folders.Count == 0) catalog.Folders.Add(DefaultFolder);
    var isActiveKnown = catalog.Folders.Contains(catalog.ActiveFolder, StringComparer.CurrentCultureIgnoreCase) ||
        (BookshelfPath.Normalize(catalog.ActiveFolder) is { } activeNorm &&
         catalog.Folders.Any(f => f.StartsWith(activeNorm + "/", StringComparison.OrdinalIgnoreCase)));
    if (!isActiveKnown)
      catalog.ActiveFolder = catalog.Folders[0];
    var normalizedColumns = new Dictionary<string, int>(StringComparer.CurrentCultureIgnoreCase);
    foreach (var (key, value) in catalog.FolderColumnCounts)
    {
      if (BookshelfPath.Normalize(key) is { } validKey)
        normalizedColumns[validKey] = Math.Clamp(value, 1, 6);
    }
    foreach (var folder in catalog.Folders)
    {
      if (!normalizedColumns.ContainsKey(folder))
        normalizedColumns[folder] = 2;
    }
    catalog.FolderColumnCounts = normalizedColumns;
    var normalizedNotes = new Dictionary<string, string>(StringComparer.CurrentCultureIgnoreCase);
    foreach (var (key, value) in catalog.FolderNotes)
    {
      if (BookshelfPath.Normalize(key) is { } validKey)
      {
        var note = value ?? string.Empty;
        normalizedNotes[validKey] = note.Length <= 4000 ? note : note[..4000];
      }
    }
    foreach (var folder in catalog.Folders)
    {
      if (!normalizedNotes.ContainsKey(folder))
        normalizedNotes[folder] = string.Empty;
    }
    catalog.FolderNotes = normalizedNotes;
    var normalizedIds = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
    var usedIds = new HashSet<string>(StringComparer.Ordinal);
    foreach (var folder in catalog.Folders)
    {
      var id = catalog.FolderIds?.GetValueOrDefault(folder)?.ToLowerInvariant();
      if (id is null || !Guid.TryParseExact(id, "N", out var parsed) || parsed == Guid.Empty || !usedIds.Add(id))
      {
        id = Guid.NewGuid().ToString("N");
        usedIds.Add(id);
      }
      normalizedIds[folder] = id;
    }
    catalog.FolderIds = normalizedIds;
    // An unavailable disk/OneDrive original is not a user deletion.
    // Keep the catalog entry so sync cannot propagate a transient read failure.
    return catalog;
  }

  private static string LoadConfiguredRootDirectory()
  {
    try
    {
      if (!File.Exists(LocationSettingsPath)) return DefaultRootDirectory;
      var settings = JsonSerializer.Deserialize<BookshelfLocationSettings>(
          File.ReadAllText(LocationSettingsPath), JsonOptions);
      return string.IsNullOrWhiteSpace(settings?.StorageDirectory)
          ? DefaultRootDirectory
          : Path.GetFullPath(settings.StorageDirectory);
    }
    catch
    {
      return DefaultRootDirectory;
    }
  }

  private static void SetConfiguredRootDirectory(string directory)
  {
    var normalized = Path.GetFullPath(directory);
    Directory.CreateDirectory(BrowserStateStore.StateDirectory);
    var settings = new BookshelfLocationSettings
    {
      StorageDirectory = normalized.Equals(DefaultRootDirectory, StringComparison.OrdinalIgnoreCase)
          ? string.Empty
          : normalized,
    };
    var temporary = LocationSettingsPath + ".new";
    File.WriteAllText(temporary, JsonSerializer.Serialize(settings, JsonOptions));
    File.Move(temporary, LocationSettingsPath, true);
    lock (LocationGate)
    {
      _cachedStateDirectory = Path.GetFullPath(BrowserStateStore.StateDirectory);
      _cachedRootDirectory = normalized;
    }
  }

  private static string ShelfDirectory(string root, string folder)
  {
    if (BookshelfPath.Normalize(folder) is null) throw new IOException("사용할 수 없는 책장 경로입니다.");
    var directory = Path.GetFullPath(Path.Combine(root, folder.Replace('/', Path.DirectorySeparatorChar)));
    if (!IsSubdirectory(directory, Path.GetFullPath(root))) throw new IOException("책장 밖의 경로는 사용할 수 없습니다.");
    return directory;
  }

  private static void SaveToDirectory(BookshelfCatalog catalog, string directory)
  {
    catalog = Normalize(catalog);
    Directory.CreateDirectory(directory);
    foreach (var folder in catalog.Folders) Directory.CreateDirectory(ShelfDirectory(directory, folder));
    var before = catalog.Items.ToList();
    var copies = new List<(string Source, string Target)>();
    var createdTargets = new List<string>();
    var nextNumbers = new Dictionary<string, int>(StringComparer.OrdinalIgnoreCase);
    try
    {
      for (var i = 0; i < catalog.Items.Count; i++)
      {
        var item = catalog.Items[i];
        if (!File.Exists(item.FilePath))
        {
          if (TryResolveItemPath(item, directory, out var resolved) && File.Exists(resolved))
          {
            item = item with { FilePath = resolved };
            catalog.Items[i] = item;
          }
          else
          {
            // Do not crash or abort saving if a photo is temporarily unavailable
            continue;
          }
        }
        if (!Guid.TryParseExact(item.Id, "N", out _)) throw new IOException("사진 ID를 확인해 주세요.");
        var extension = Path.GetExtension(item.FilePath);
        if (extension.Length is < 2 or > 8 || extension.Skip(1).Any(c => !char.IsAsciiLetterOrDigit(c))) extension = ".img";
        var folderDirectory = ShelfDirectory(directory, item.Folder);
        string target;
        if (IsNumberedFile(item.FilePath, folderDirectory))
          target = item.FilePath;
        else
        {
          if (!nextNumbers.TryGetValue(item.Folder, out var next))
          {
            next = Directory.EnumerateFiles(folderDirectory)
                .Select(path => NumberFromFileName(path)).DefaultIfEmpty(0).Max() + 1;
          }
          while (File.Exists(Path.Combine(folderDirectory, $"image{next}{extension.ToLowerInvariant()}")))
            next++;
          target = Path.Combine(folderDirectory, $"image{next}{extension.ToLowerInvariant()}");
          nextNumbers[item.Folder] = checked(next + 1);
        }
        if (!Path.GetFullPath(item.FilePath).Equals(target, StringComparison.OrdinalIgnoreCase))
        {
          if (!File.Exists(target))
          {
            var partial = target + "." + Guid.NewGuid().ToString("N") + ".partial";
            try { File.Copy(item.FilePath, partial); if (!EqualOriginals(item.FilePath, partial)) throw new IOException("원본 복사 검증 실패"); File.Move(partial, target); createdTargets.Add(target); }
            finally { if (File.Exists(partial)) File.Delete(partial); }
          }
          if (!EqualOriginals(item.FilePath, target)) throw new IOException("같은 사진 ID의 원본이 다릅니다.");
          copies.Add((item.FilePath, target));
          catalog.Items[i] = item with { FilePath = target };
        }
      }
      var catalogPath = Path.Combine(directory, "catalog.json");
      var temporary = catalogPath + ".new";
      using (var output = new FileStream(temporary, FileMode.Create, FileAccess.Write, FileShare.None))
      { JsonSerializer.Serialize(output, catalog, JsonOptions); output.Flush(true); }
      File.Move(temporary, catalogPath, true);
    }
    catch
    {
      catalog.Items = before;
      foreach (var target in createdTargets)
        try { File.Delete(target); } catch { }
      throw;
    }
    foreach (var (source, target) in copies)
      if (IsSubdirectory(Path.GetFullPath(source), Path.GetFullPath(directory)) && EqualOriginals(source, target))
        try { File.Delete(source); } catch { }
  }

  private static int NumberFromFileName(string path)
  {
    var match = Regex.Match(Path.GetFileNameWithoutExtension(path), "^image([1-9][0-9]*)$", RegexOptions.IgnoreCase | RegexOptions.CultureInvariant);
    return match.Success && int.TryParse(match.Groups[1].Value, out var number) ? number : 0;
  }

  private static bool IsNumberedFile(string path, string folderDirectory) =>
      NumberFromFileName(path) > 0 &&
      Path.GetFullPath(Path.GetDirectoryName(path)!).Equals(folderDirectory, StringComparison.OrdinalIgnoreCase);

  private static bool EqualOriginals(string first, string second)
  {
    if (!File.Exists(first) || !File.Exists(second) || new FileInfo(first).Length != new FileInfo(second).Length) return false;
    using var a = File.OpenRead(first); using var b = File.OpenRead(second);
    return System.Security.Cryptography.SHA256.HashData(a).AsSpan().SequenceEqual(System.Security.Cryptography.SHA256.HashData(b));
  }

  private static void CopyDirectoryVerified(string source, string destination)
  {
    foreach (var sourceDirectory in Directory.EnumerateDirectories(source, "*", SearchOption.AllDirectories))
    {
      var relative = Path.GetRelativePath(source, sourceDirectory);
      Directory.CreateDirectory(Path.Combine(destination, relative));
    }
    foreach (var sourceFile in Directory.EnumerateFiles(source, "*", SearchOption.AllDirectories))
    {
      var relative = Path.GetRelativePath(source, sourceFile);
      var destinationFile = Path.Combine(destination, relative);
      Directory.CreateDirectory(Path.GetDirectoryName(destinationFile)!);
      File.Copy(sourceFile, destinationFile, true);
      if (new FileInfo(sourceFile).Length != new FileInfo(destinationFile).Length)
        throw new IOException($"파일 복사 검증에 실패했습니다: {relative}");
    }
  }

  private static string RelocateItemPath(string filePath, string source, string destination)
  {
    var fullPath = Path.GetFullPath(filePath);
    var relative = Path.GetRelativePath(source, fullPath);
    if (Path.IsPathRooted(relative) || relative.Equals("..", StringComparison.Ordinal) ||
        relative.StartsWith(".." + Path.DirectorySeparatorChar, StringComparison.Ordinal))
      throw new IOException("책장 외부에 있는 이미지 경로가 발견되어 안전하게 이동할 수 없습니다.");
    return Path.GetFullPath(Path.Combine(destination, relative));
  }

  private static void VerifyMigratedCatalog(BookshelfCatalog catalog, string destination)
  {
    var catalogPath = Path.Combine(destination, "catalog.json");
    if (!File.Exists(catalogPath)) throw new IOException("새 위치에서 책장 목록을 확인할 수 없습니다.");
    var restored = JsonSerializer.Deserialize<BookshelfCatalog>(File.ReadAllText(catalogPath), JsonOptions)
        ?? throw new IOException("새 위치의 책장 목록을 읽을 수 없습니다.");
    if (restored.Items.Count != catalog.Items.Count || restored.Items.Any(item => !File.Exists(item.FilePath)))
      throw new IOException("새 위치의 이미지 파일 검증에 실패했습니다.");
  }

  private static bool IsSubdirectory(string candidate, string parent)
  {
    var relative = Path.GetRelativePath(parent, candidate);
    return !relative.Equals(".", StringComparison.Ordinal) && !Path.IsPathRooted(relative) &&
           !relative.Equals("..", StringComparison.Ordinal) &&
           !relative.StartsWith(".." + Path.DirectorySeparatorChar, StringComparison.Ordinal);
  }

  private static string NormalizeDirectoryPath(string directory)
  {
    var fullPath = Path.GetFullPath(directory);
    var root = Path.GetPathRoot(fullPath);
    return !string.IsNullOrWhiteSpace(root) && fullPath.Equals(root, StringComparison.OrdinalIgnoreCase)
        ? fullPath
        : fullPath.TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar);
  }

  private static void ResetDestination(string directory, bool preserveRoot)
  {
    try
    {
      if (Directory.Exists(directory)) Directory.Delete(directory, true);
      if (preserveRoot) Directory.CreateDirectory(directory);
    }
    catch { }
  }

  private static string SafeSegment(string value)
  {
    var invalid = Path.GetInvalidFileNameChars();
    var safe = new string(value.Select(character => invalid.Contains(character) ? '_' : character).ToArray()).Trim();
    return safe.Length == 0 ? "shelf" : safe;
  }
}

internal enum BookshelfPdfOrientation
{
  Portrait,
  Landscape,
}

internal static class BookshelfPdfExporter
{
  private const int PageShortEdge = 1080;
  private const int PageLongEdge = 1920;
  private const int PageMargin = 0;
  private const int ContentTop = 0;
  private const int ContentBottom = 0;
  private const int Gap = 0;

  private sealed record Placement(BookshelfItem Item, int X, int Y, int Width, int Height);

  internal static void Export(
      BookshelfCatalog catalog,
      string folder,
      string outputPath,
      BookshelfPdfOrientation orientation = BookshelfPdfOrientation.Portrait)
  {
    var pageWidth = orientation == BookshelfPdfOrientation.Portrait ? PageShortEdge : PageLongEdge;
    var pageHeight = orientation == BookshelfPdfOrientation.Portrait ? PageLongEdge : PageShortEdge;
    var items = BookshelfStore.VisibleItems(catalog, folder)
        .Where(item => File.Exists(item.FilePath))
        .ToList();
    var columns = BookshelfStore.GetColumnCount(catalog, folder);
    var contentWidth = pageWidth - PageMargin * 2;
    var columnWidth = (contentWidth - Gap * (columns - 1)) / columns;
    var columnHeights = new int[columns];
    var placements = new List<Placement>(items.Count);
    foreach (var item in items)
    {
      var targetColumn = 0;
      for (var column = 1; column < columns; column++)
        if (columnHeights[column] < columnHeights[targetColumn]) targetColumn = column;
      var height = Math.Max(1, (int)Math.Round(columnWidth * item.Height / (double)Math.Max(1, item.Width)));
      placements.Add(new Placement(
          item,
          PageMargin + targetColumn * (columnWidth + Gap),
          columnHeights[targetColumn],
          columnWidth,
          height));
      columnHeights[targetColumn] += height + Gap;
    }

    var pageContentHeight = pageHeight - ContentTop - ContentBottom;
    var galleryHeight = Math.Max(0, columnHeights.DefaultIfEmpty(0).Max() - Gap);
    var pageCount = Math.Max(1, (int)Math.Ceiling(galleryHeight / (double)pageContentHeight));
    var pages = new List<byte[]>(pageCount);
    try
    {
      for (var pageIndex = 0; pageIndex < pageCount; pageIndex++)
      {
        using var page = new Bitmap(pageWidth, pageHeight, PixelFormat.Format24bppRgb);
        using var graphics = Graphics.FromImage(page);
        graphics.Clear(Color.White);
        graphics.InterpolationMode = InterpolationMode.HighQualityBicubic;
        graphics.PixelOffsetMode = PixelOffsetMode.HighQuality;

        var pageStart = pageIndex * pageContentHeight;
        var pageEnd = pageStart + pageContentHeight;
        var state = graphics.Save();
        graphics.SetClip(new Rectangle(PageMargin, ContentTop, contentWidth, pageContentHeight));
        foreach (var placement in placements)
        {
          if (placement.Y + placement.Height <= pageStart || placement.Y >= pageEnd) continue;
          using var image = BookshelfStore.LoadImage(placement.Item);
          if (image is null) continue;
          var target = new RectangleF(
              placement.X,
              ContentTop + placement.Y - pageStart,
              placement.Width,
              placement.Height);
          graphics.DrawImage(image, target);
        }
        graphics.Restore(state);

        using var encoded = new MemoryStream();
        var jpegCodec = ImageCodecInfo.GetImageEncoders().First(codec => codec.FormatID == ImageFormat.Jpeg.Guid);
        using var encoderParameters = new EncoderParameters(1);
        encoderParameters.Param[0] = new EncoderParameter(System.Drawing.Imaging.Encoder.Quality, 92L);
        page.Save(encoded, jpegCodec, encoderParameters);
        pages.Add(encoded.ToArray());
      }
      WritePdf(outputPath, pages, pageWidth, pageHeight);
    }
    finally
    {
      pages.Clear();
    }
  }

  private static void WritePdf(
      string outputPath,
      IReadOnlyList<byte[]> pages,
      int pageWidth,
      int pageHeight)
  {
    var pdfWidth = pageWidth > pageHeight ? 960 : 540;
    var pdfHeight = pageWidth > pageHeight ? 540 : 960;
    Directory.CreateDirectory(Path.GetDirectoryName(Path.GetFullPath(outputPath))!);
    using var stream = new FileStream(outputPath, FileMode.Create, FileAccess.Write, FileShare.None);
    var offsets = new long[3 + pages.Count * 3];
    WriteAscii(stream, "%PDF-1.4\n%Omyaru\n");

    void BeginObject(int id)
    {
      offsets[id] = stream.Position;
      WriteAscii(stream, $"{id} 0 obj\n");
    }
    void EndObject() => WriteAscii(stream, "endobj\n");

    BeginObject(1);
    WriteAscii(stream, "<< /Type /Catalog /Pages 2 0 R >>\n");
    EndObject();

    BeginObject(2);
    var kids = string.Join(" ", Enumerable.Range(0, pages.Count).Select(index => $"{3 + index * 3} 0 R"));
    WriteAscii(stream, $"<< /Type /Pages /Count {pages.Count} /Kids [{kids}] >>\n");
    EndObject();

    for (var index = 0; index < pages.Count; index++)
    {
      var pageObject = 3 + index * 3;
      var contentObject = pageObject + 1;
      var imageObject = pageObject + 2;
      BeginObject(pageObject);
      WriteAscii(stream, $"<< /Type /Page /Parent 2 0 R /MediaBox [0 0 {pdfWidth} {pdfHeight}] /Resources << /XObject << /Im0 {imageObject} 0 R >> >> /Contents {contentObject} 0 R >>\n");
      EndObject();

      var content = Encoding.ASCII.GetBytes($"q\n{pdfWidth} 0 0 {pdfHeight} 0 0 cm\n/Im0 Do\nQ\n");
      BeginObject(contentObject);
      WriteAscii(stream, $"<< /Length {content.Length} >>\nstream\n");
      stream.Write(content);
      WriteAscii(stream, "endstream\n");
      EndObject();

      var image = pages[index];
      BeginObject(imageObject);
      WriteAscii(stream, $"<< /Type /XObject /Subtype /Image /Width {pageWidth} /Height {pageHeight} /ColorSpace /DeviceRGB /BitsPerComponent 8 /Filter /DCTDecode /Length {image.Length} >>\nstream\n");
      stream.Write(image);
      WriteAscii(stream, "\nendstream\n");
      EndObject();
    }

    var xref = stream.Position;
    WriteAscii(stream, $"xref\n0 {offsets.Length}\n0000000000 65535 f \n");
    for (var id = 1; id < offsets.Length; id++) WriteAscii(stream, $"{offsets[id]:D10} 00000 n \n");
    WriteAscii(stream, $"trailer\n<< /Size {offsets.Length} /Root 1 0 R >>\nstartxref\n{xref}\n%%EOF\n");
  }

  private static void WriteAscii(Stream stream, string value)
  {
    var bytes = Encoding.ASCII.GetBytes(value);
    stream.Write(bytes);
  }
}

internal enum CaptureAction
{
  Cancel,
  SavePng,
  Copy,
  AddToBookshelf,
  OpenBookshelf,
}

internal sealed class CaptureActionsForm : Form
{
  internal CaptureAction SelectedAction { get; private set; }
  protected override CreateParams CreateParams => WinFormsSurface.WithComposited(base.CreateParams);

  internal CaptureActionsForm(string lastDirectory, string activeShelf)
  {
    SuspendLayout();
    Text = "화면 캡처 — Omyaru";
    StartPosition = FormStartPosition.CenterParent;
    FormBorderStyle = FormBorderStyle.FixedDialog;
    MinimizeBox = false;
    MaximizeBox = false;
    ClientSize = new Size(520, 356);
    BackColor = Color.FromArgb(28, 28, 30);
    ForeColor = Color.FromArgb(245, 245, 247);
    Font = new Font("Segoe UI", 10F);

    var root = new FlowLayoutPanel
    {
      Dock = DockStyle.Fill,
      FlowDirection = FlowDirection.TopDown,
      WrapContents = false,
      Padding = new Padding(22, 18, 22, 18),
      BackColor = BackColor,
    };
    root.Controls.Add(new Label
    {
      Width = 470,
      Height = 56,
      Text = "실시간 페이지에서 캡처 영역 선택",
      Font = new Font("Segoe UI", 17F, FontStyle.Bold),
      ForeColor = ForeColor,
      TextAlign = ContentAlignment.MiddleLeft,
    });
    AddAction("영역 지정 후 PNG 저장", string.IsNullOrWhiteSpace(lastDirectory) ? "처음 한 번 저장 폴더를 선택합니다." : $"마지막 경로 · {lastDirectory}", CaptureAction.SavePng);
    AddAction("영역 지정 후 복사", "PNG 이미지를 바로 붙여넣을 수 있습니다.", CaptureAction.Copy);
    AddAction("영역 지정 후 책장에 추가", $"‘{activeShelf}’ 폴더에 원본 비율로 보관합니다.", CaptureAction.AddToBookshelf);
    AddAction("책장 열기", "폴더별 2~6열 이미지 책장을 엽니다.", CaptureAction.OpenBookshelf);
    Controls.Add(root);
    ResumeLayout(true);
    WinFormsSurface.Attach(this);

    void AddAction(string title, string subtitle, CaptureAction action)
    {
      var button = new PillButton
      {
        Width = 470,
        Height = 52,
        Text = title + "   ·   " + subtitle,
        TextAlign = ContentAlignment.MiddleLeft,
        Padding = new Padding(18, 0, 12, 0),
        BackColor = Color.FromArgb(44, 44, 48),
        HoverBackColor = Color.FromArgb(56, 56, 62),
        ForeColor = ForeColor,
        BorderColor = Color.FromArgb(58, 58, 64),
        BorderWidth = 1F,
        CornerRadius = 12,
        Margin = new Padding(0, 3, 0, 3),
      };
      button.Click += (_, _) =>
      {
        SelectedAction = action;
        DialogResult = DialogResult.OK;
      };
      root.Controls.Add(button);
    }
  }
}

internal sealed class CaptureRegionForm : Form
{
  private static readonly Color Background = Color.FromArgb(18, 18, 20);
  private static readonly Color Surface = Color.FromArgb(44, 44, 48);
  private static readonly Color Outline = Color.FromArgb(58, 58, 64);
  private static readonly Color TextColor = Color.FromArgb(245, 245, 247);
  private static readonly Color MutedText = Color.FromArgb(152, 152, 157);
  private static readonly Color Accent = Color.FromArgb(10, 132, 255);
  private readonly Bitmap _source;
  private readonly CaptureRegionCanvas _canvas;
  private readonly Label _selectionStatus;
  private readonly PillButton _complete;

  internal CaptureRegionForm(Bitmap source)
  {
    _source = source;
    Text = "영역 선택 — Omyaru";
    StartPosition = FormStartPosition.CenterParent;
    WindowState = FormWindowState.Maximized;
    MinimumSize = new Size(900, 640);
    BackColor = Background;
    ForeColor = TextColor;
    Font = new Font("Segoe UI", 10F);
    KeyPreview = true;

    var root = new TableLayoutPanel
    {
      Dock = DockStyle.Fill,
      BackColor = Background,
      ColumnCount = 1,
      RowCount = 3,
      Margin = Padding.Empty,
      Padding = Padding.Empty,
    };
    root.RowStyles.Add(new RowStyle(SizeType.Absolute, 76));
    root.RowStyles.Add(new RowStyle(SizeType.Percent, 100));
    root.RowStyles.Add(new RowStyle(SizeType.Absolute, 68));
    Controls.Add(root);

    var header = new TableLayoutPanel
    {
      Dock = DockStyle.Fill,
      BackColor = Background,
      ColumnCount = 2,
      RowCount = 1,
      Padding = new Padding(24, 8, 20, 8),
      Margin = Padding.Empty,
    };
    header.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 100));
    header.ColumnStyles.Add(new ColumnStyle(SizeType.Absolute, 180));
    var heading = new Label
    {
      Dock = DockStyle.Fill,
      Text = "캡처할 영역을 드래그하세요\n마우스 휠로 긴 페이지를 이동하고, 드래그 중 화면 가장자리에서는 자동으로 스크롤됩니다.",
      Font = new Font("Segoe UI", 12F, FontStyle.Bold),
      ForeColor = TextColor,
      TextAlign = ContentAlignment.MiddleLeft,
    };
    var documentSize = new Label
    {
      Dock = DockStyle.Fill,
      Text = $"전체 문서  {_source.Width:N0} × {_source.Height:N0}",
      ForeColor = MutedText,
      TextAlign = ContentAlignment.MiddleRight,
    };
    header.Controls.Add(heading, 0, 0);
    header.Controls.Add(documentSize, 1, 0);
    root.Controls.Add(header, 0, 0);

    _canvas = new CaptureRegionCanvas(_source)
    {
      Dock = DockStyle.Fill,
      BackColor = Color.FromArgb(18, 21, 25),
      Margin = new Padding(12, 0, 12, 0),
    };
    root.Controls.Add(_canvas, 0, 1);

    var footer = new TableLayoutPanel
    {
      Dock = DockStyle.Fill,
      BackColor = Background,
      ColumnCount = 2,
      RowCount = 1,
      Padding = new Padding(20, 8, 20, 8),
      Margin = Padding.Empty,
    };
    footer.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 100));
    footer.ColumnStyles.Add(new ColumnStyle(SizeType.Absolute, 220));
    _selectionStatus = new Label
    {
      Dock = DockStyle.Fill,
      Text = "선택 영역 없음",
      ForeColor = MutedText,
      TextAlign = ContentAlignment.MiddleLeft,
    };
    var actions = new FlowLayoutPanel
    {
      Dock = DockStyle.Fill,
      BackColor = Background,
      FlowDirection = FlowDirection.RightToLeft,
      WrapContents = false,
      Margin = Padding.Empty,
      Padding = Padding.Empty,
    };
    _complete = ActionButton("선택 완료", Accent, Color.White);
    _complete.Enabled = false;
    _complete.Click += (_, _) => DialogResult = DialogResult.OK;
    var cancel = ActionButton("취소", Surface, TextColor);
    cancel.Click += (_, _) => DialogResult = DialogResult.Cancel;
    actions.Controls.Add(_complete);
    actions.Controls.Add(cancel);
    footer.Controls.Add(_selectionStatus, 0, 0);
    footer.Controls.Add(actions, 1, 0);
    root.Controls.Add(footer, 0, 2);

    _canvas.SelectionChanged += (_, _) =>
    {
      var selection = _canvas.SelectedImageRectangle;
      _complete.Enabled = selection.Width > 1 && selection.Height > 1;
      _selectionStatus.Text = _complete.Enabled
          ? $"선택 영역  {selection.Width:N0} × {selection.Height:N0}"
          : "선택 영역 없음";
    };
    KeyDown += (_, eventArgs) =>
    {
      if (eventArgs.KeyCode != Keys.Escape) return;
      eventArgs.SuppressKeyPress = true;
      DialogResult = DialogResult.Cancel;
    };
    Shown += (_, _) => _canvas.Focus();
  }

  internal Bitmap? CreateSelectionBitmap()
  {
    var selection = _canvas.SelectedImageRectangle;
    if (selection.Width < 2 || selection.Height < 2) return null;
    var bitmap = new Bitmap(selection.Width, selection.Height, PixelFormat.Format32bppPArgb);
    using var graphics = Graphics.FromImage(bitmap);
    graphics.DrawImage(_source, new Rectangle(0, 0, bitmap.Width, bitmap.Height), selection, GraphicsUnit.Pixel);
    return bitmap;
  }

  private static PillButton ActionButton(string text, Color background, Color foreground) => new()
  {
    Width = 102,
    Height = 42,
    Text = text,
    BackColor = background,
    HoverBackColor = ControlPaint.Light(background, 0.08F),
    ForeColor = foreground,
    BorderColor = background == Accent ? Accent : Outline,
    BorderWidth = 1F,
    CornerRadius = 19,
    Margin = new Padding(4, 2, 0, 2),
  };
}

internal sealed class CaptureRegionCanvas : ScrollableControl
{
  private const int ContentPadding = 18;
  private readonly Bitmap _image;
  private readonly System.Windows.Forms.Timer _edgeScrollTimer;
  private readonly Font _dimensionFont = new("Segoe UI", 10F, FontStyle.Bold);
  private Point _selectionStart;
  private Point _selectionEnd;
  private float _zoom = 1F;
  private bool _pointerInside;

  internal CaptureRegionCanvas(Bitmap image)
  {
    _image = image;
    AutoScroll = true;
    TabStop = true;
    Cursor = Cursors.Cross;
    SetStyle(ControlStyles.AllPaintingInWmPaint | ControlStyles.OptimizedDoubleBuffer |
             ControlStyles.ResizeRedraw | ControlStyles.UserPaint | ControlStyles.Selectable, true);
    _edgeScrollTimer = new System.Windows.Forms.Timer { Interval = 28 };
    _edgeScrollTimer.Tick += (_, _) => ScrollWhileSelecting();
  }

  internal event EventHandler? SelectionChanged;
  internal bool IsSelecting { get; private set; }

  internal Rectangle SelectedImageRectangle
  {
    get
    {
      var left = Math.Clamp(Math.Min(_selectionStart.X, _selectionEnd.X), 0, _image.Width);
      var top = Math.Clamp(Math.Min(_selectionStart.Y, _selectionEnd.Y), 0, _image.Height);
      var right = Math.Clamp(Math.Max(_selectionStart.X, _selectionEnd.X), 0, _image.Width);
      var bottom = Math.Clamp(Math.Max(_selectionStart.Y, _selectionEnd.Y), 0, _image.Height);
      return Rectangle.FromLTRB(left, top, right, bottom);
    }
  }

  protected override void OnResize(EventArgs eventArgs)
  {
    base.OnResize(eventArgs);
    var availableWidth = Math.Max(1, ClientSize.Width - ContentPadding * 2 - SystemInformation.VerticalScrollBarWidth);
    _zoom = Math.Min(1F, availableWidth / (float)Math.Max(1, _image.Width));
    AutoScrollMinSize = new Size(
        Math.Max(1, (int)Math.Ceiling(_image.Width * _zoom) + ContentPadding * 2),
        Math.Max(1, (int)Math.Ceiling(_image.Height * _zoom) + ContentPadding * 2));
    Invalidate();
  }

  protected override void OnPaint(PaintEventArgs eventArgs)
  {
    base.OnPaint(eventArgs);
    var graphics = eventArgs.Graphics;
    graphics.SmoothingMode = System.Drawing.Drawing2D.SmoothingMode.AntiAlias;
    graphics.InterpolationMode = System.Drawing.Drawing2D.InterpolationMode.HighQualityBilinear;
    graphics.PixelOffsetMode = System.Drawing.Drawing2D.PixelOffsetMode.HighQuality;
    var scroll = AutoScrollPosition;
    graphics.TranslateTransform(scroll.X, scroll.Y);
    var imageBounds = new RectangleF(
        ContentPadding,
        ContentPadding,
        _image.Width * _zoom,
        _image.Height * _zoom);
    graphics.DrawImage(_image, imageBounds);

    var selection = SelectedImageRectangle;
    if (selection.Width > 1 && selection.Height > 1)
    {
      var displaySelection = new RectangleF(
          ContentPadding + selection.X * _zoom,
          ContentPadding + selection.Y * _zoom,
          selection.Width * _zoom,
          selection.Height * _zoom);
      using (var shadeRegion = new Region(imageBounds))
      using (var shade = new SolidBrush(Color.FromArgb(118, 4, 7, 12)))
      {
        shadeRegion.Exclude(displaySelection);
        graphics.FillRegion(shade, shadeRegion);
      }
      using var border = new Pen(Color.FromArgb(65, 206, 255), 1F);
      graphics.DrawRectangle(border, displaySelection.X, displaySelection.Y, displaySelection.Width, displaySelection.Height);
    }

    if (IsSelecting) DrawPointerGuides(graphics, imageBounds);
    if (_pointerInside) DrawMagnifier(graphics);
    if (selection.Width > 1 && selection.Height > 1) DrawDimensionBadge(graphics, selection);
  }

  private void DrawPointerGuides(Graphics graphics, RectangleF imageBounds)
  {
    var pointerX = ContentPadding + _selectionEnd.X * _zoom;
    var pointerY = ContentPadding + _selectionEnd.Y * _zoom;
    using var guide = new Pen(Color.FromArgb(190, 42, 210, 255), 1F);
    graphics.DrawLine(guide, imageBounds.Left, pointerY, imageBounds.Right, pointerY);
    graphics.DrawLine(guide, pointerX, imageBounds.Top, pointerX, imageBounds.Bottom);
  }

  private void DrawMagnifier(Graphics graphics)
  {
    const float diameter = 118F;
    const float gap = 28F;
    var scroll = AutoScrollPosition;
    var visible = new RectangleF(-scroll.X, -scroll.Y, ClientSize.Width, ClientSize.Height);
    var pointer = new PointF(
        ContentPadding + _selectionEnd.X * _zoom,
        ContentPadding + _selectionEnd.Y * _zoom);
    var lens = MagnifierBounds(pointer, visible, diameter, gap);

    using (var shadow = new SolidBrush(Color.FromArgb(95, 0, 0, 0)))
      graphics.FillEllipse(shadow, lens.X + 3, lens.Y + 5, lens.Width, lens.Height);

    var sourceDiameter = Math.Max(8F, diameter / 3.5F / Math.Max(0.001F, _zoom));
    var source = new RectangleF(
        Math.Clamp(_selectionEnd.X - sourceDiameter / 2F, 0F, Math.Max(0F, _image.Width - sourceDiameter)),
        Math.Clamp(_selectionEnd.Y - sourceDiameter / 2F, 0F, Math.Max(0F, _image.Height - sourceDiameter)),
        Math.Min(sourceDiameter, _image.Width),
        Math.Min(sourceDiameter, _image.Height));
    var saved = graphics.Save();
    using (var clip = new System.Drawing.Drawing2D.GraphicsPath())
    {
      clip.AddEllipse(lens);
      graphics.SetClip(clip);
      using var lensBackground = new SolidBrush(Color.FromArgb(20, 24, 29));
      graphics.FillRectangle(lensBackground, lens);
      graphics.InterpolationMode = System.Drawing.Drawing2D.InterpolationMode.NearestNeighbor;
      graphics.PixelOffsetMode = System.Drawing.Drawing2D.PixelOffsetMode.Half;
      graphics.DrawImage(_image, lens, source, GraphicsUnit.Pixel);
    }
    graphics.Restore(saved);

    using (var outline = new Pen(Color.White, 3F)) graphics.DrawEllipse(outline, lens);
    var center = new PointF(lens.X + lens.Width / 2F, lens.Y + lens.Height / 2F);
    using var crosshair = new Pen(Color.FromArgb(65, 206, 255), 1.4F);
    graphics.DrawLine(crosshair, lens.Left, center.Y, lens.Right, center.Y);
    graphics.DrawLine(crosshair, center.X, lens.Top, center.X, lens.Bottom);
    using var centerBrush = new SolidBrush(Color.FromArgb(65, 206, 255));
    graphics.FillEllipse(centerBrush, center.X - 2.5F, center.Y - 2.5F, 5F, 5F);
  }

  private void DrawDimensionBadge(Graphics graphics, Rectangle selection)
  {
    var text = $"▣  {selection.Width:N0} × {selection.Height:N0}";
    var measured = graphics.MeasureString(text, _dimensionFont);
    var width = Math.Max(112F, measured.Width + 24F);
    const float height = 32F;
    var scroll = AutoScrollPosition;
    var visible = new RectangleF(-scroll.X, -scroll.Y, ClientSize.Width, ClientSize.Height);
    var pointer = new PointF(
        ContentPadding + _selectionEnd.X * _zoom,
        ContentPadding + _selectionEnd.Y * _zoom);
    var left = pointer.X + 14F;
    var top = pointer.Y + 14F;
    if (IsSelecting)
    {
      var lens = MagnifierBounds(pointer, visible, 118F, 28F);
      left = lens.X + (lens.Width - width) / 2F;
      top = lens.Bottom + 8F;
      if (top + height > visible.Bottom - 10) top = lens.Top - height - 8F;
    }
    else
    {
      if (left + width > visible.Right - 10) left = pointer.X - width - 14F;
      if (top + height > visible.Bottom - 10) top = pointer.Y - height - 14F;
    }
    left = Math.Clamp(left, visible.Left + 10, Math.Max(visible.Left + 10, visible.Right - width - 10));
    top = Math.Clamp(top, visible.Top + 10, Math.Max(visible.Top + 10, visible.Bottom - height - 10));
    var bounds = new RectangleF(left, top, width, height);
    using (var shadow = new SolidBrush(Color.FromArgb(70, 0, 0, 0)))
    using (var shadowPath = UiGeometry.RoundedRectangle(new RectangleF(bounds.X + 2, bounds.Y + 3, bounds.Width, bounds.Height), 16F))
      graphics.FillPath(shadow, shadowPath);
    using (var background = new SolidBrush(Color.FromArgb(50, 143, 255)))
    using (var path = UiGeometry.RoundedRectangle(bounds, 16F))
      graphics.FillPath(background, path);
    using var textBrush = new SolidBrush(Color.White);
    using var format = new StringFormat { Alignment = StringAlignment.Center, LineAlignment = StringAlignment.Center };
    graphics.DrawString(text, _dimensionFont, textBrush, bounds, format);
  }

  private static RectangleF MagnifierBounds(PointF pointer, RectangleF visible, float diameter, float gap)
  {
    var left = pointer.X + gap;
    var top = pointer.Y + gap;
    if (left + diameter > visible.Right - 12) left = pointer.X - diameter - gap;
    if (top + diameter > visible.Bottom - 12) top = pointer.Y - diameter - gap;
    left = Math.Clamp(left, visible.Left + 12, Math.Max(visible.Left + 12, visible.Right - diameter - 12));
    top = Math.Clamp(top, visible.Top + 12, Math.Max(visible.Top + 12, visible.Bottom - diameter - 12));
    return new RectangleF(left, top, diameter, diameter);
  }

  protected override void OnMouseDown(MouseEventArgs eventArgs)
  {
    base.OnMouseDown(eventArgs);
    if (eventArgs.Button != MouseButtons.Left) return;
    Focus();
    Capture = true;
    IsSelecting = true;
    _selectionStart = ImagePointFromClient(eventArgs.Location);
    _selectionEnd = _selectionStart;
    _edgeScrollTimer.Start();
    SelectionChanged?.Invoke(this, EventArgs.Empty);
    Invalidate();
  }

  protected override void OnMouseMove(MouseEventArgs eventArgs)
  {
    base.OnMouseMove(eventArgs);
    _pointerInside = true;
    if (IsSelecting) UpdateSelectionFromPointer();
    else
    {
      _selectionEnd = ImagePointFromClient(eventArgs.Location);
      Invalidate();
    }
  }

  protected override void OnMouseEnter(EventArgs eventArgs)
  {
    base.OnMouseEnter(eventArgs);
    _pointerInside = true;
    _selectionEnd = ImagePointFromClient(PointToClient(Cursor.Position));
    Invalidate();
  }

  protected override void OnMouseLeave(EventArgs eventArgs)
  {
    base.OnMouseLeave(eventArgs);
    if (IsSelecting) return;
    _pointerInside = false;
    Invalidate();
  }

  protected override void OnMouseUp(MouseEventArgs eventArgs)
  {
    base.OnMouseUp(eventArgs);
    if (eventArgs.Button != MouseButtons.Left || !IsSelecting) return;
    UpdateSelectionFromPointer();
    IsSelecting = false;
    Capture = false;
    _edgeScrollTimer.Stop();
    Invalidate();
  }

  protected override void Dispose(bool disposing)
  {
    if (disposing)
    {
      _edgeScrollTimer.Dispose();
      _dimensionFont.Dispose();
    }
    base.Dispose(disposing);
  }

  private void ScrollWhileSelecting()
  {
    if (!IsSelecting) return;
    var pointer = PointToClient(Cursor.Position);
    var currentX = -AutoScrollPosition.X;
    var currentY = -AutoScrollPosition.Y;
    var nextY = currentY;
    const int edge = 52;
    const int step = 30;
    if (pointer.Y < edge) nextY = Math.Max(0, currentY - step);
    else if (pointer.Y > ClientSize.Height - edge)
      nextY = Math.Min(Math.Max(0, AutoScrollMinSize.Height - ClientSize.Height), currentY + step);
    if (nextY == currentY) return;
    AutoScrollPosition = new Point(currentX, nextY);
    UpdateSelectionFromPointer();
  }

  private void UpdateSelectionFromPointer()
  {
    _selectionEnd = ImagePointFromClient(PointToClient(Cursor.Position));
    SelectionChanged?.Invoke(this, EventArgs.Empty);
    Invalidate();
  }

  private Point ImagePointFromClient(Point clientPoint)
  {
    var scroll = AutoScrollPosition;
    var contentX = clientPoint.X - scroll.X - ContentPadding;
    var contentY = clientPoint.Y - scroll.Y - ContentPadding;
    return new Point(
        Math.Clamp((int)Math.Round(contentX / Math.Max(0.001F, _zoom)), 0, _image.Width),
        Math.Clamp((int)Math.Round(contentY / Math.Max(0.001F, _zoom)), 0, _image.Height));
  }
}

internal sealed class PdfOrientationDialog : Form
{
  internal BookshelfPdfOrientation SelectedOrientation { get; private set; } = BookshelfPdfOrientation.Portrait;
  protected override CreateParams CreateParams => WinFormsSurface.WithComposited(base.CreateParams);

  internal PdfOrientationDialog()
  {
    SuspendLayout();
    Text = "PDF 페이지 비율 — Omyaru";
    StartPosition = FormStartPosition.CenterParent;
    FormBorderStyle = FormBorderStyle.FixedDialog;
    MinimizeBox = false;
    MaximizeBox = false;
    ShowInTaskbar = false;
    ClientSize = new Size(470, 248);
    BackColor = Color.FromArgb(28, 28, 30);
    ForeColor = Color.FromArgb(245, 245, 247);
    Font = new Font("Segoe UI", 10F);

    var root = new TableLayoutPanel
    {
      Dock = DockStyle.Fill,
      Padding = new Padding(22, 18, 22, 18),
      ColumnCount = 2,
      RowCount = 3,
      BackColor = BackColor,
    };
    root.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 50));
    root.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 50));
    root.RowStyles.Add(new RowStyle(SizeType.Absolute, 54));
    root.RowStyles.Add(new RowStyle(SizeType.Percent, 100));
    root.RowStyles.Add(new RowStyle(SizeType.Absolute, 44));
    var title = new Label
    {
      Dock = DockStyle.Fill,
      Text = "PDF 페이지 비율",
      Font = new Font("Segoe UI", 17F, FontStyle.Bold),
      ForeColor = ForeColor,
      TextAlign = ContentAlignment.MiddleLeft,
    };
    root.Controls.Add(title, 0, 0);
    root.SetColumnSpan(title, 2);

    var portrait = ChoiceButton("세로 9:16", "모바일·세로 감상", BookshelfPdfOrientation.Portrait);
    var landscape = ChoiceButton("가로 16:9", "모니터·가로 감상", BookshelfPdfOrientation.Landscape);
    root.Controls.Add(portrait, 0, 1);
    root.Controls.Add(landscape, 1, 1);
    var cancel = new PillButton
    {
      Dock = DockStyle.Right,
      Width = 96,
      Text = "취소",
      BackColor = Color.FromArgb(44, 44, 48),
      HoverBackColor = Color.FromArgb(56, 56, 62),
      ForeColor = ForeColor,
      BorderColor = Color.FromArgb(58, 58, 64),
      BorderWidth = 1F,
      CornerRadius = 12,
    };
    cancel.Click += (_, _) =>
    {
      DialogResult = DialogResult.Cancel;
      Close();
    };
    root.Controls.Add(cancel, 1, 2);
    Controls.Add(root);
    ResumeLayout(true);
    WinFormsSurface.Attach(this);

    PillButton ChoiceButton(string heading, string detail, BookshelfPdfOrientation orientation)
    {
      var button = new PillButton
      {
        Dock = DockStyle.Fill,
        Margin = orientation == BookshelfPdfOrientation.Portrait
            ? new Padding(0, 6, 7, 10)
            : new Padding(7, 6, 0, 10),
        Text = heading + "\n" + detail,
        BackColor = Color.FromArgb(44, 44, 48),
        HoverBackColor = Color.FromArgb(56, 56, 62),
        ForeColor = ForeColor,
        BorderColor = Color.FromArgb(58, 58, 64),
        BorderWidth = 1F,
        CornerRadius = 14,
      };
      button.Click += (_, _) =>
      {
        SelectedOrientation = orientation;
        DialogResult = DialogResult.OK;
        Close();
      };
      return button;
    }
  }

  protected override bool ProcessCmdKey(ref Message message, Keys keyData)
  {
    if (keyData == Keys.Escape)
    {
      DialogResult = DialogResult.Cancel;
      Close();
      return true;
    }
    return base.ProcessCmdKey(ref message, keyData);
  }
}

internal sealed class BookshelfForm : Form
{
  private const float HeaderHeight = 104F;
  private const float SidebarWidth = 272F;
  private static readonly Color Background = Color.FromArgb(18, 18, 20);
  private static readonly Color SidebarBg = Color.FromArgb(26, 26, 30);
  private static readonly Color Chrome = Color.FromArgb(28, 28, 32);
  private static readonly Color Surface = Color.FromArgb(44, 44, 48);
  private static readonly Color SurfaceHover = Color.FromArgb(56, 56, 62);
  private static readonly Color Outline = Color.FromArgb(58, 58, 64);
  private static readonly Color TextColor = Color.FromArgb(245, 245, 247);
  private static readonly Color MutedText = Color.FromArgb(152, 152, 157);
  private static readonly Color Accent = Color.FromArgb(10, 132, 255);
  private readonly BookshelfCatalog _catalog = BookshelfStore.Load();
  private readonly BookshelfSyncCoordinator _shelfSync = new();
  private readonly System.Windows.Forms.Timer _syncTimer = new() { Interval = 15000 };
  private readonly System.Windows.Forms.Timer _syncProgressTimer = new() { Interval = 250 };
  private PillButton? _syncButton;
  private Panel? _syncProgressPanel;
  private Panel? _syncProgressFill;
  private Label? _syncProgressLabel;
  private TableLayoutPanel _sidebar = null!;
  private readonly FlowLayoutPanel _folders = new();
  private readonly TreeView _folderTree = new();
  private readonly Dictionary<TreeNode, ContextMenuStrip> _folderNodeMenus = [];
  private bool _renderingFolderTree;
  private bool _folderTreeInitialized;
  private string? _lastRenderedActiveFolder;
  private TreeNode? _hoveredNode;
  private bool _suppressSelectionChange;
  private sealed class PreserveScrollPanel : Panel
  {
    protected override Point ScrollToControl(Control activeControl) => DisplayRectangle.Location;
  }

  private readonly PreserveScrollPanel _galleryHost = new();
  private readonly List<Image> _loadedImages = [];
  private readonly Label _columnValue = new();
  private readonly System.Windows.Forms.Timer _renderDebounce = new() { Interval = 24 };
  private readonly System.Windows.Forms.Timer _noteSaveTimer = new() { Interval = 350 };
  private readonly Label _shelfTitle = new();
  private readonly TextBox _shelfNote = new();
  private readonly ToolTip _storageToolTip = new();
  private readonly List<GalleryEntry> _galleryEntries = [];
  private static readonly SemaphoreSlim GalleryTransfers = new(3, 3);
  private static readonly HashSet<string> SupportedImageExtensions = new(StringComparer.OrdinalIgnoreCase)
  {
    ".png", ".jpg", ".jpeg", ".webp", ".bmp", ".gif",
  };
  private TableLayoutPanel _layout = null!;
  private TableLayoutPanel _header = null!;
  private TableLayoutPanel? _galleryCanvas;
  private VirtualBookshelfGallery? _virtualGallery;
  private List<FlowLayoutPanel> _galleryColumns = [];
  private PillButton _decreaseColumns = null!;
  private PillButton _increaseColumns = null!;
  private PillButton _fullscreenButton = null!;
  private bool _isFullscreen;
  private bool _importing;
  private bool _updatingShelfMetadata;
  private int _renderGeneration;
  private int _lastRenderedGalleryWidth;
  private Rectangle _windowedBounds;
  private FormWindowState _windowedState;

  protected override CreateParams CreateParams
  {
    get
    {
      const int WsClipChildren = 0x02000000;
      var parameters = base.CreateParams;
      parameters.Style |= WsClipChildren;
      return parameters;
    }
  }

  internal BookshelfForm()
  {
    SuspendLayout();
    BookshelfSyncCoordinator.EnableForConnectedAccountOnce();
    BookshelfStore.TryMigrateImageNames(_catalog);
    Text = "책장 — Omyaru";
    StartPosition = FormStartPosition.CenterScreen;
    ShowInTaskbar = true;
    MinimumSize = new Size(920, 640);
    Size = new Size(1080, 820);
    BackColor = Background;
    ForeColor = TextColor;
    Font = new Font("Segoe UI", 10F);
    KeyPreview = true;
    AllowDrop = true;
    _renderDebounce.Tick += (_, _) =>
    {
      _renderDebounce.Stop();
      ResizeGalleryWithoutReload();
    };
    _noteSaveTimer.Tick += (_, _) =>
    {
      _noteSaveTimer.Stop();
      CommitShelfNote();
    };
    BuildInterface();
    _syncTimer.Tick += async (_, _) => await SyncBookshelfAsync();
    _syncProgressTimer.Tick += (_, _) => UpdateSyncProgressUi();
    Shown += async (_, _) => { _syncProgressTimer.Start(); _syncTimer.Start(); await SyncBookshelfAsync(); };
    Deactivate += (_, _) => CommitShelfNote();
    ResizeEnd += (_, _) => ResizeGalleryWithoutReload();
    ConfigureDropTarget(this);
    RenderFolders();
    RenderGallery();
    ResumeLayout(true);
    WinFormsSurface.Attach(this);
    FormClosed += (_, _) =>
    {
      _renderDebounce.Dispose();
      _syncTimer.Dispose();
      _syncProgressTimer.Dispose();
      _shelfSync.Dispose();
      _noteSaveTimer.Dispose();
      _storageToolTip.Dispose();
      DisposeFolderNodeMenus();
      DisposeLoadedImages();
    };
    FormClosing += (_, _) => CommitShelfNote();
    KeyDown += (_, eventArgs) =>
    {
      if (eventArgs.Control && eventArgs.KeyCode == Keys.V)
      {
        if (_shelfNote.Focused && !Clipboard.ContainsImage() && !Clipboard.ContainsFileDropList()) return;
        eventArgs.Handled = true;
        eventArgs.SuppressKeyPress = true;
        _ = PasteFromClipboardAsync();
        return;
      }
      if (eventArgs.Control && eventArgs.KeyCode == Keys.O)
      {
        eventArgs.Handled = true;
        eventArgs.SuppressKeyPress = true;
        AddImagesFromFiles();
        return;
      }
      if (!_isFullscreen || eventArgs.KeyCode != Keys.Escape) return;
      eventArgs.SuppressKeyPress = true;
      ToggleFullscreen();
    };
  }

  internal void ReloadCatalogFromExternalChange()
  {
    if (IsDisposed) return;
    CommitShelfNote();
    var saved = BookshelfStore.Load();
    var active = _catalog.ActiveFolder;
    _catalog.Folders = saved.Folders;
    _catalog.FolderIds = saved.FolderIds;
    _catalog.FolderNotes = saved.FolderNotes;
    _catalog.FolderColumnCounts = saved.FolderColumnCounts;
    _catalog.Items = saved.Items;
    _catalog.ActiveFolder = !string.IsNullOrWhiteSpace(saved.ActiveFolder)
        ? saved.ActiveFolder
        : active;
    RenderFolders();
    UpdateColumnControls();
    RenderGallery(CurrentGalleryScrollOffset());
  }

  protected override bool ProcessCmdKey(ref Message message, Keys keyData)
  {
    if (keyData == (Keys.Control | Keys.O))
    {
      AddImagesFromFiles();
      return true;
    }
    if (keyData == (Keys.Control | Keys.V))
    {
      if (_shelfNote.Focused && !Clipboard.ContainsImage() && !Clipboard.ContainsFileDropList())
        return base.ProcessCmdKey(ref message, keyData);
      _ = PasteFromClipboardAsync();
      return true;
    }
    if (_isFullscreen && keyData == Keys.Escape)
    {
      ToggleFullscreen();
      return true;
    }
    return base.ProcessCmdKey(ref message, keyData);
  }

  private async Task SyncBookshelfAsync()
  {
    if (_importing || IsDisposed) return;
    var restored = RefreshMissingOriginalPathsFromCatalog();
    if (!restored) CommitShelfNote();
    var changed = await _shelfSync.TickAsync(_catalog, restored ? null : CommitShelfNote);
    if (IsDisposed) return;
    if (changed || restored)
    {
      var scroll = CurrentGalleryScrollOffset();
      RenderFolders();
      UpdateColumnControls();
      RenderGallery(scroll);
    }
    UpdateSyncProgressUi();
  }

  private void UpdateSyncProgressUi()
  {
    if (_syncButton is null || _syncProgressPanel is null || _syncProgressFill is null || _syncProgressLabel is null) return;
    var progress = BookshelfSyncCoordinator.Enabled ? _shelfSync.Progress : null;
    _syncProgressPanel.Visible = progress is not null;
    if (progress is not null)
    {
      _syncProgressLabel.Text = $"{progress.Phase} {progress.Percent}%  ·  {progress.Done}/{progress.Total}";
      _syncProgressFill.Width = Math.Clamp((int)Math.Round(240 * progress.Percent / 100.0), 0, 240);
      _syncButton.Text = progress.Phase == "완료" ? "☁ 동기화 완료" : $"☁ {progress.Phase} {progress.Percent}%";
    }
    else _syncButton.Text = BookshelfSyncCoordinator.Enabled ? "☁ 동기화" : "☁ 동기화 꺼짐";
    _storageToolTip.SetToolTip(_syncButton, _shelfSync.Status);
  }

  private void BuildInterface()
  {
    var root = _layout = new TableLayoutPanel
    {
      Dock = DockStyle.Fill,
      ColumnCount = 2,
      RowCount = 2,
      BackColor = Background,
      Padding = Padding.Empty,
      Margin = Padding.Empty,
    };
    root.ColumnStyles.Add(new ColumnStyle(SizeType.Absolute, SidebarWidth));
    root.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 100));
    root.RowStyles.Add(new RowStyle(SizeType.Absolute, HeaderHeight));
    root.RowStyles.Add(new RowStyle(SizeType.Percent, 100));
    Controls.Add(root);
    ConfigureDropTarget(root);

    var sidebar = _sidebar = new TableLayoutPanel
    {
      Dock = DockStyle.Fill,
      ColumnCount = 1,
      RowCount = 3,
      BackColor = SidebarBg,
      Padding = new Padding(10, 10, 10, 10),
      Margin = Padding.Empty,
    };
    sidebar.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 100));
    sidebar.RowStyles.Add(new RowStyle(SizeType.Absolute, 42));
    sidebar.RowStyles.Add(new RowStyle(SizeType.Percent, 100));
    sidebar.RowStyles.Add(new RowStyle(SizeType.AutoSize));
    sidebar.Paint += (_, e) =>
    {
      using var borderPen = new Pen(Outline, 1F);
      e.Graphics.DrawLine(borderPen, sidebar.ClientSize.Width - 1, 0, sidebar.ClientSize.Width - 1, sidebar.ClientSize.Height);
    };

    var sidebarHeader = new TableLayoutPanel
    {
      Dock = DockStyle.Fill,
      ColumnCount = 2,
      RowCount = 1,
      BackColor = SidebarBg,
      Margin = Padding.Empty,
      Padding = new Padding(4, 2, 2, 4),
    };
    sidebarHeader.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 100));
    sidebarHeader.ColumnStyles.Add(new ColumnStyle(SizeType.Absolute, 84));
    var sidebarTitle = new Label
    {
      Dock = DockStyle.Fill,
      Text = "책장 폴더",
      Font = new Font("Segoe UI", 10.5F, FontStyle.Bold),
      ForeColor = MutedText,
      TextAlign = ContentAlignment.MiddleLeft,
    };
    var addFolder = new PillButton
    {
      Dock = DockStyle.Fill,
      Height = 30,
      Text = "＋ 폴더",
      Font = new Font("Segoe UI", 9F, FontStyle.Bold),
      BackColor = Surface,
      HoverBackColor = SurfaceHover,
      ForeColor = TextColor,
      BorderColor = Outline,
      BorderWidth = 1F,
      CornerRadius = 8,
      Margin = new Padding(0, 2, 0, 2),
    };
    addFolder.Click += (_, _) => CreateFolder();
    sidebarHeader.Controls.Add(sidebarTitle, 0, 0);
    sidebarHeader.Controls.Add(addFolder, 1, 0);
    sidebar.Controls.Add(sidebarHeader, 0, 0);

    _folderTree.Dock = DockStyle.Fill;
    _folderTree.Name = "BookshelfFolderTree";
    _folderTree.BackColor = SidebarBg;
    _folderTree.ForeColor = TextColor;
    _folderTree.BorderStyle = BorderStyle.None;
    _folderTree.Font = new Font("Segoe UI", 9.5F);
    _folderTree.ItemHeight = 34;
    _folderTree.Indent = 18;
    _folderTree.HideSelection = false;
    _folderTree.FullRowSelect = true;
    _folderTree.ShowLines = false;
    _folderTree.ShowRootLines = false;
    _folderTree.ShowPlusMinus = false;
    _folderTree.ShowNodeToolTips = true;
    _folderTree.DrawMode = TreeViewDrawMode.OwnerDrawAll;
    _folderTree.DrawNode += DrawFolderNode;
    try
    {
      typeof(TreeView).GetProperty("DoubleBuffered", System.Reflection.BindingFlags.Instance | System.Reflection.BindingFlags.NonPublic)
          ?.SetValue(_folderTree, true, null);
    }
    catch { }
    _folderTree.MouseMove += (_, e) =>
    {
      var hit = _folderTree.HitTest(e.Location);
      var node = hit.Node;
      if (node != _hoveredNode)
      {
        var old = _hoveredNode;
        _hoveredNode = node;
        if (old is not null)
          _folderTree.Invalidate(new Rectangle(0, old.Bounds.Top, _folderTree.ClientSize.Width, old.Bounds.Height));
        if (_hoveredNode is not null)
          _folderTree.Invalidate(new Rectangle(0, _hoveredNode.Bounds.Top, _folderTree.ClientSize.Width, _hoveredNode.Bounds.Height));
      }
    };
    _folderTree.MouseLeave += (_, _) =>
    {
      if (_hoveredNode is not null)
      {
        var old = _hoveredNode;
        _hoveredNode = null;
        _folderTree.Invalidate(new Rectangle(0, old.Bounds.Top, _folderTree.ClientSize.Width, old.Bounds.Height));
      }
    };
    _folderTree.MouseDown += (_, e) =>
    {
      if (e.Button == MouseButtons.Right)
      {
        _suppressSelectionChange = true;
        return;
      }
      if (e.Button != MouseButtons.Left) return;
      var hit = _folderTree.HitTest(e.Location);
      if (hit.Node is not null)
      {
        var node = hit.Node;
        int indentX = 10 + node.Level * 18;
        if (node.Nodes.Count > 0 && e.X >= indentX - 6 && e.X <= indentX + 22)
        {
          _suppressSelectionChange = true;
          if (node.IsExpanded) node.Collapse();
          else node.Expand();
          return;
        }
      }
    };
    _folderTree.BeforeSelect += (_, e) =>
    {
      if (_suppressSelectionChange)
      {
        e.Cancel = true;
      }
    };
    _folderTree.MouseUp += (_, e) =>
    {
      if (e.Button == MouseButtons.Right)
      {
        var hit = _folderTree.HitTest(e.Location);
        if (hit.Node is not null && _folderNodeMenus.TryGetValue(hit.Node, out var menu))
          menu.Show(_folderTree, e.Location);
      }
      _suppressSelectionChange = false;
    };
    _folderTree.NodeMouseDoubleClick += (_, e) =>
    {
      if (e.Node is not null && e.Node.Nodes.Count > 0)
        e.Node.Toggle();
    };
    _folderTree.AfterSelect += (_, e) =>
    {
      if (_renderingFolderTree || e.Node?.Tag is not string folder ||
          folder.Equals(_catalog.ActiveFolder, StringComparison.OrdinalIgnoreCase)) return;
      CommitShelfNote();
      _catalog.ActiveFolder = folder;
      if (_catalog.Folders.Contains(folder, StringComparer.OrdinalIgnoreCase) &&
          BookshelfStore.MissingOriginalCount(_catalog) == 0)
      {
        try { BookshelfStore.Save(_catalog); } catch { }
      }
      UpdateShelfMetadataUi();
      UpdateColumnControls();
      RenderGallery(Point.Empty);
      _folderTree.Invalidate();
    };
    ConfigureDropTarget(_folderTree);
    sidebar.Controls.Add(_folderTree, 0, 1);

    _folders.Dock = DockStyle.Fill;
    _folders.AutoSize = true;
    _folders.AutoSizeMode = AutoSizeMode.GrowAndShrink;
    _folders.FlowDirection = FlowDirection.TopDown;
    _folders.WrapContents = false;
    _folders.Padding = new Padding(2, 8, 2, 2);
    _folders.Margin = Padding.Empty;
    _folders.BackColor = SidebarBg;
    _folders.Paint += (_, e) =>
    {
      using var sepPen = new Pen(Outline, 1F);
      e.Graphics.DrawLine(sepPen, 4, 0, _folders.ClientSize.Width - 4, 0);
    };
    ConfigureDropTarget(_folders);
    sidebar.Controls.Add(_folders, 0, 2);

    root.Controls.Add(sidebar, 0, 0);
    root.SetRowSpan(sidebar, 2);

    var header = _header = new TableLayoutPanel
    {
      Dock = DockStyle.Fill,
      BackColor = Chrome,
      Padding = new Padding(20, 8, 20, 8),
      ColumnCount = 5,
      RowCount = 2,
      Margin = Padding.Empty,
    };
    header.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 100));
    header.ColumnStyles.Add(new ColumnStyle(SizeType.Absolute, 152));
    header.ColumnStyles.Add(new ColumnStyle(SizeType.Absolute, 104));
    header.ColumnStyles.Add(new ColumnStyle(SizeType.Absolute, 110));
    header.ColumnStyles.Add(new ColumnStyle(SizeType.Absolute, 104));
    header.RowStyles.Add(new RowStyle(SizeType.Absolute, 46));
    header.RowStyles.Add(new RowStyle(SizeType.Absolute, 38));
    header.Paint += (_, e) =>
    {
      using var borderPen = new Pen(Outline, 1F);
      e.Graphics.DrawLine(borderPen, 0, header.ClientSize.Height - 1, header.ClientSize.Width, header.ClientSize.Height - 1);
    };

    _shelfTitle.Dock = DockStyle.Fill;
    _shelfTitle.Font = new Font("Segoe UI", 17F, FontStyle.Bold);
    _shelfTitle.ForeColor = TextColor;
    _shelfTitle.TextAlign = ContentAlignment.MiddleLeft;
    _shelfTitle.AutoEllipsis = true;

    var columnCapsule = new RoundedPanel
    {
      Anchor = AnchorStyles.Left | AnchorStyles.Right,
      Height = 36,
      BackColor = Surface,
      BorderColor = Outline,
      BorderWidth = 1F,
      CornerRadius = 10,
      Padding = new Padding(3, 2, 3, 2),
      Margin = new Padding(4, 0, 4, 0),
    };
    var columnInner = new TableLayoutPanel
    {
      Dock = DockStyle.Fill,
      ColumnCount = 3,
      RowCount = 1,
      BackColor = Color.Transparent,
      Margin = Padding.Empty,
      Padding = Padding.Empty,
    };
    columnInner.ColumnStyles.Add(new ColumnStyle(SizeType.Absolute, 36));
    columnInner.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 100));
    columnInner.ColumnStyles.Add(new ColumnStyle(SizeType.Absolute, 36));
    _decreaseColumns = ColumnButton("−", (_, _) => ChangeColumnCount(-1));
    _columnValue.Dock = DockStyle.Fill;
    _columnValue.ForeColor = TextColor;
    _columnValue.Font = new Font("Segoe UI", 9.5F, FontStyle.Bold);
    _columnValue.TextAlign = ContentAlignment.MiddleCenter;
    _columnValue.Margin = Padding.Empty;
    _increaseColumns = ColumnButton("＋", (_, _) => ChangeColumnCount(1));
    columnInner.Controls.Add(_decreaseColumns, 0, 0);
    columnInner.Controls.Add(_columnValue, 1, 0);
    columnInner.Controls.Add(_increaseColumns, 2, 0);
    columnCapsule.Controls.Add(columnInner);

    var addImages = new PillButton
    {
      Anchor = AnchorStyles.Left | AnchorStyles.Right,
      Height = 36,
      Text = "＋ 사진 추가",
      Font = new Font("Segoe UI", 9.5F, FontStyle.Bold),
      BackColor = Accent,
      HoverBackColor = Color.FromArgb(40, 150, 255),
      ForeColor = Color.White,
      BorderColor = Color.Transparent,
      BorderWidth = 0F,
      CornerRadius = 10,
      Margin = new Padding(6, 0, 0, 0),
    };
    addImages.Click += (_, _) => AddImagesFromFiles();

    var exportPdf = HeaderButton("PDF 내보내기");
    exportPdf.Click += (_, _) => ExportCurrentFolderPdf();

    _fullscreenButton = HeaderButton("⛶ 전체 화면");
    _fullscreenButton.Click += (_, _) => ToggleFullscreen();

    header.Controls.Add(_shelfTitle, 0, 0);
    header.Controls.Add(columnCapsule, 1, 0);
    header.Controls.Add(addImages, 2, 0);
    header.Controls.Add(exportPdf, 3, 0);
    header.Controls.Add(_fullscreenButton, 4, 0);

    var noteHost = new RoundedPanel
    {
      Dock = DockStyle.Fill,
      BackColor = Color.FromArgb(20, 20, 23),
      BorderColor = Outline,
      BorderWidth = 1F,
      CornerRadius = 10,
      Padding = new Padding(10, 6, 10, 4),
      Margin = new Padding(0, 2, 0, 0),
    };
    var noteLayout = new TableLayoutPanel
    {
      Dock = DockStyle.Fill,
      ColumnCount = 2,
      RowCount = 1,
      BackColor = Color.Transparent,
      Margin = Padding.Empty,
      Padding = Padding.Empty,
    };
    noteLayout.ColumnStyles.Add(new ColumnStyle(SizeType.Absolute, 42));
    noteLayout.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 100));
    var noteBadge = new Label
    {
      Dock = DockStyle.Fill,
      Text = "메모",
      Font = new Font("Segoe UI", 8.5F, FontStyle.Bold),
      ForeColor = MutedText,
      TextAlign = ContentAlignment.MiddleLeft,
    };
    _shelfNote.Dock = DockStyle.Fill;
    _shelfNote.BorderStyle = BorderStyle.None;
    _shelfNote.BackColor = Color.FromArgb(20, 20, 23);
    _shelfNote.ForeColor = TextColor;
    _shelfNote.Font = new Font("Segoe UI", 9.5F);
    _shelfNote.PlaceholderText = "이 책장에 메모 추가…";
    _shelfNote.MaxLength = 4000;
    _shelfNote.Multiline = true;
    _shelfNote.AcceptsReturn = true;
    _shelfNote.TextChanged += (_, _) =>
    {
      if (_updatingShelfMetadata) return;
      _noteSaveTimer.Stop();
      _noteSaveTimer.Start();
    };
    noteLayout.Controls.Add(noteBadge, 0, 0);
    noteLayout.Controls.Add(_shelfNote, 1, 0);
    noteHost.Controls.Add(noteLayout);
    header.Controls.Add(noteHost, 0, 1);
    header.SetColumnSpan(noteHost, 5);

    root.Controls.Add(header, 1, 0);

    _galleryHost.Dock = DockStyle.Fill;
    _galleryHost.AutoScroll = true;
    _galleryHost.BackColor = Background;
    _galleryHost.Padding = new Padding(12, 12, 20, 24);
    _galleryHost.SizeChanged += (_, _) => ScheduleGalleryRender();
    ConfigureDropTarget(_galleryHost);
    root.Controls.Add(_galleryHost, 1, 1);

    PillButton ColumnButton(string text, EventHandler click)
    {
      var button = new PillButton
      {
        Dock = DockStyle.Fill,
        Text = text,
        Font = new Font("Segoe UI", 10F, FontStyle.Bold),
        BackColor = Surface,
        HoverBackColor = SurfaceHover,
        ForeColor = TextColor,
        BorderColor = Color.Transparent,
        BorderWidth = 0F,
        CornerRadius = 8,
        Margin = Padding.Empty,
      };
      button.Click += click;
      return button;
    }

    PillButton HeaderButton(string text) => new()
    {
      Anchor = AnchorStyles.Left | AnchorStyles.Right,
      Height = 36,
      Text = text,
      Font = new Font("Segoe UI", 9F, FontStyle.Bold),
      BackColor = Surface,
      HoverBackColor = SurfaceHover,
      ForeColor = TextColor,
      BorderColor = Outline,
      BorderWidth = 1F,
      CornerRadius = 10,
      Margin = new Padding(6, 0, 0, 0),
    };
  }

  private void ToggleFullscreen()
  {
    _layout.SuspendLayout();
    if (!_isFullscreen)
    {
      _windowedBounds = Bounds;
      _windowedState = WindowState;
      WindowState = FormWindowState.Normal;
      FormBorderStyle = FormBorderStyle.None;
      Bounds = Screen.FromControl(this).Bounds;
      _isFullscreen = true;
      _fullscreenButton.Text = "⛶ 종료";
      _header.Visible = false;
      _sidebar.Visible = false;
      _layout.RowStyles[0].Height = 0;
      _layout.ColumnStyles[0].Width = 0;
      _galleryHost.Padding = Padding.Empty;
      TopMost = false;
    }
    else
    {
      _header.Visible = true;
      _sidebar.Visible = true;
      _layout.RowStyles[0].Height = HeaderHeight;
      _layout.ColumnStyles[0].Width = SidebarWidth;
      _galleryHost.Padding = new Padding(12, 12, 20, 24);
      FormBorderStyle = FormBorderStyle.Sizable;
      WindowState = FormWindowState.Normal;
      Bounds = _windowedBounds;
      WindowState = _windowedState;
      _isFullscreen = false;
      _fullscreenButton.Text = "⛶ 전체 화면";
    }
    _layout.ResumeLayout(true);
    _renderDebounce.Stop();
    PerformLayout();
    _galleryHost.PerformLayout();
    BeginInvoke(new Action(ResizeGalleryWithoutReload));
  }

  private void ScheduleGalleryRender(bool force = false)
  {
    if (IsDisposed || Disposing) return;
    if (!force && Math.Abs(_galleryHost.ClientSize.Width - _lastRenderedGalleryWidth) <= 24) return;
    if (!_renderDebounce.Enabled) _renderDebounce.Start();
  }

  private void ExportCurrentFolderPdf()
  {
    using var formatDialog = new PdfOrientationDialog();
    if (formatDialog.ShowDialog(this) != DialogResult.OK) return;
    var orientation = formatDialog.SelectedOrientation;
    var safeName = new string(_catalog.ActiveFolder
        .Select(character => Path.GetInvalidFileNameChars().Contains(character) ? '_' : character)
        .ToArray()).Trim();
    if (safeName.Length == 0) safeName = "bookshelf";
    using var dialog = new SaveFileDialog
    {
      Title = "책장을 PDF로 내보내기",
      Filter = "PDF 문서 (*.pdf)|*.pdf",
      AddExtension = true,
      DefaultExt = "pdf",
      FileName = $"Omyaru-{safeName}.pdf",
    };
    if (dialog.ShowDialog(this) != DialogResult.OK) return;
    try
    {
      UseWaitCursor = true;
      BookshelfPdfExporter.Export(_catalog, _catalog.ActiveFolder, dialog.FileName, orientation);
      MessageBox.Show(this, "현재 책장을 PDF로 저장했습니다.", "책장", MessageBoxButtons.OK, MessageBoxIcon.Information);
    }
    catch (Exception error)
    {
      MessageBox.Show(this, "PDF를 만들지 못했습니다.\n" + error.Message, "책장", MessageBoxButtons.OK, MessageBoxIcon.Warning);
    }
    finally
    {
      UseWaitCursor = false;
    }
  }

  private void RenderFolders()
  {
    RefreshMissingOriginalPathsFromCatalog();
    UpdateShelfMetadataUi();
    _folders.Controls.Clear();

    if (BookshelfStore.ConfiguredCatalogUnavailable)
    {
      _folders.Controls.Add(new Label
      {
        Width = 244,
        Height = 56,
        ForeColor = Color.FromArgb(255, 192, 130),
        Font = new Font("Segoe UI", 8.75F),
        Text = "이전 책장 저장 위치를 찾지 못했습니다. 기본 위치를 사용하거나 기존 책장을 다시 연결하세요.",
        Margin = new Padding(0, 4, 0, 4),
      });
      var useDefault = new PillButton
      {
        Text = "기본 위치 사용",
        Width = 244,
        Height = 34,
        BackColor = Surface,
        HoverBackColor = SurfaceHover,
        ForeColor = TextColor,
        BorderColor = Outline,
        BorderWidth = 1F,
        CornerRadius = 10,
        Margin = new Padding(0, 2, 0, 6),
      };
      useDefault.Click += (_, _) => RecoverUnavailableStorage(BookshelfStore.DefaultStorageDirectory);
      _folders.Controls.Add(useDefault);
    }

    var missing = BookshelfStore.MissingOriginalCount(_catalog);
    _shelfNote.ReadOnly = missing > 0;
    if (missing > 0)
    {
      var missingNotice = new Label
      {
        Width = 244,
        Height = 58,
        AutoEllipsis = true,
        ForeColor = Color.FromArgb(255, 192, 130),
        Font = new Font("Segoe UI", 8.75F),
        Text = $"원본 사진 {missing}개를 찾지 못했습니다. 저장 매체 확인 후 클릭하여 다시 확인",
        Margin = new Padding(0, 4, 0, 6),
        Cursor = Cursors.Hand,
      };
      missingNotice.Click += (_, _) => { RenderFolders(); RenderGallery(CurrentGalleryScrollOffset()); };
      _folders.Controls.Add(missingNotice);
    }

    _syncProgressPanel = new Panel
    {
      Width = 244,
      Height = 34,
      BackColor = SidebarBg,
      Margin = new Padding(0, 2, 0, 6),
      Visible = false,
    };
    _syncProgressLabel = new Label
    {
      Left = 2,
      Top = 0,
      Width = 240,
      Height = 20,
      ForeColor = MutedText,
      Font = new Font("Segoe UI", 8.5F),
    };
    var progressTrack = new Panel { Left = 2, Top = 23, Width = 240, Height = 5, BackColor = Outline };
    _syncProgressFill = new Panel { Left = 0, Top = 0, Width = 0, Height = 5, BackColor = Accent };
    progressTrack.Controls.Add(_syncProgressFill);
    _syncProgressPanel.Controls.Add(_syncProgressLabel);
    _syncProgressPanel.Controls.Add(progressTrack);
    _folders.Controls.Add(_syncProgressPanel);

    var footerRow = new TableLayoutPanel
    {
      Width = 244,
      Height = 36,
      ColumnCount = 2,
      RowCount = 1,
      BackColor = SidebarBg,
      Margin = new Padding(0, 2, 0, 0),
      Padding = Padding.Empty,
    };
    footerRow.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 50));
    footerRow.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 50));

    _syncButton = new PillButton
    {
      Dock = DockStyle.Fill,
      Text = "☁ 동기화",
      Font = new Font("Segoe UI", 8.75F, FontStyle.Bold),
      BackColor = Surface,
      HoverBackColor = SurfaceHover,
      ForeColor = TextColor,
      BorderColor = Outline,
      BorderWidth = 1F,
      CornerRadius = 9,
      Margin = new Padding(0, 1, 3, 1),
    };
    _syncButton.Click += async (_, _) => { BookshelfSyncCoordinator.ShowSettings(this); await SyncBookshelfAsync(); };
    _storageToolTip.SetToolTip(_syncButton,
        BookshelfSyncCoordinator.Enabled ? _shelfSync.Status : "책장 자동 동기화 꺼짐");

    var storageLocation = new PillButton
    {
      Dock = DockStyle.Fill,
      Text = BookshelfStore.ConfiguredCatalogUnavailable ? "⌂ 다시 연결" : "⌂ 저장 위치",
      Font = new Font("Segoe UI", 8.75F),
      BackColor = Surface,
      HoverBackColor = SurfaceHover,
      ForeColor = MutedText,
      BorderColor = Outline,
      BorderWidth = 1F,
      CornerRadius = 9,
      Margin = new Padding(3, 1, 0, 1),
      AccessibleName = "책장 저장 위치 변경",
    };
    storageLocation.Click += async (_, _) => await ChangeStorageDirectoryAsync();
    _storageToolTip.SetToolTip(storageLocation, BookshelfStore.StorageDirectory);

    footerRow.Controls.Add(_syncButton, 0, 0);
    footerRow.Controls.Add(storageLocation, 1, 0);
    _folders.Controls.Add(footerRow);

    UpdateSyncProgressUi();
    RenderFolderTree(missing);
    UpdateColumnControls();
  }

  private bool RefreshMissingOriginalPathsFromCatalog()
  {
    if (BookshelfStore.MissingOriginalCount(_catalog) == 0) return false;
    if (BookshelfStore.SelfHealItemPaths(_catalog) && BookshelfStore.MissingOriginalCount(_catalog) == 0)
    {
      try { BookshelfStore.Save(_catalog); } catch { }
      return true;
    }
    var saved = BookshelfStore.Load();
    if (BookshelfStore.MissingOriginalCount(saved) == 0 &&
        saved.Items.Select(item => item.Id).Distinct(StringComparer.Ordinal).Count() == saved.Items.Count &&
        (saved.Items.Count != _catalog.Items.Count ||
         !_catalog.Items.Select(item => item.Id).SequenceEqual(saved.Items.Select(item => item.Id))))
    {
      var active = _catalog.ActiveFolder;
      _catalog.Folders = saved.Folders; _catalog.FolderIds = saved.FolderIds;
      _catalog.FolderNotes = saved.FolderNotes; _catalog.FolderColumnCounts = saved.FolderColumnCounts;
      _catalog.Items = saved.Items;
      _catalog.ActiveFolder = saved.Folders.Contains(active, StringComparer.OrdinalIgnoreCase)
          ? active : saved.ActiveFolder;
      return true;
    }
    if (saved.Items.Count != _catalog.Items.Count ||
        saved.Items.Select(item => item.Id).Distinct(StringComparer.Ordinal).Count() != saved.Items.Count) return false;
    var byId = saved.Items.ToDictionary(item => item.Id, StringComparer.Ordinal);
    if (_catalog.Items.Any(item => !byId.ContainsKey(item.Id))) return false;
    var repaired = false;
    for (var index = 0; index < _catalog.Items.Count; index++)
    {
      var current = _catalog.Items[index];
      if (File.Exists(current.FilePath)) continue;
      var replacement = byId[current.Id];
      if (current.Folder != replacement.Folder || current.Width != replacement.Width ||
          current.Height != replacement.Height || current.CreatedAtEpochMs != replacement.CreatedAtEpochMs ||
          !File.Exists(replacement.FilePath)) continue;
      _catalog.Items[index] = current with { FilePath = replacement.FilePath };
      repaired = true;
    }
    return repaired;
  }

  private void DisposeFolderNodeMenus()
  {
    foreach (var menu in _folderNodeMenus.Values)
      try { menu.Dispose(); } catch { }
    _folderNodeMenus.Clear();
  }

  private void RenderFolderTree(int missingOriginals)
  {
    var expanded = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
    CollectExpandedFolders(_folderTree.Nodes, expanded);
    var previousTop = _folderTree.TopNode?.Name;
    var initialRender = !_folderTreeInitialized;
    var activeFolderChanged = !string.Equals(_lastRenderedActiveFolder, _catalog.ActiveFolder, StringComparison.OrdinalIgnoreCase);
    _renderingFolderTree = true;
    _folderTree.BeginUpdate();
    try
    {
      DisposeFolderNodeMenus();
      _folderTree.Nodes.Clear();
      var nodes = new Dictionary<string, TreeNode>(StringComparer.OrdinalIgnoreCase);
      var actualFolders = new HashSet<string>(_catalog.Folders, StringComparer.OrdinalIgnoreCase);
      var displayFolders = new List<string>();
      var seenFolders = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
      foreach (var folder in _catalog.Folders)
      {
        var ancestors = new List<string>();
        for (var path = folder; path.Length > 0; path = BookshelfPath.Parent(path)) ancestors.Add(path);
        ancestors.Reverse();
        foreach (var path in ancestors) if (seenFolders.Add(path)) displayFolders.Add(path);
      }
      foreach (var folder in displayFolders)
      {
        var isActualFolder = actualFolders.Contains(folder);
        var count = _catalog.Items.Count(item => BookshelfPath.Contains(folder, item.Folder));
        var node = new TreeNode(BookshelfPath.Leaf(folder))
        {
          Name = folder,
          Tag = folder,
          ToolTipText = isActualFolder ? $"{folder} ({count})" : $"{folder} · 상위 폴더 ({count})",
        };
        var folderMenu = new ContextMenuStrip
        {
          Renderer = new BookshelfMenuRenderer(),
          BackColor = Color.FromArgb(38, 38, 42),
          ForeColor = TextColor,
          ShowImageMargin = false,
          Font = new Font("Segoe UI", 9.5F),
        };
        folderMenu.Items.Add("하위 폴더 만들기", null, (_, _) => BeginInvoke(new Action(() => CreateChildFolder(folder))));
        folderMenu.Items.Add("위로 이동", null, (_, _) => BeginInvoke(new Action(() => MoveFolderInTree(folder, -1))));
        folderMenu.Items.Add("아래로 이동", null, (_, _) => BeginInvoke(new Action(() => MoveFolderInTree(folder, 1))));
        if (isActualFolder)
        {
          folderMenu.Items.Add(new ToolStripSeparator());
          folderMenu.Items.Add("다른 폴더로 이동", null, (_, _) => BeginInvoke(new Action(() => ChooseFolderParent(folder))));
          folderMenu.Items.Add("이름 변경", null, (_, _) => BeginInvoke(new Action(() => RenameFolder(folder))));
          var deleteItem = folderMenu.Items.Add("삭제", null, (_, _) => BeginInvoke(new Action(() => DeleteFolder(folder))));
          deleteItem.ForeColor = Color.FromArgb(255, 69, 58);
        }
        foreach (ToolStripItem action in folderMenu.Items)
        {
          action.Enabled = missingOriginals == 0;
          if (action.ForeColor != Color.FromArgb(255, 69, 58)) action.ForeColor = TextColor;
        }
        _folderNodeMenus[node] = folderMenu;
        var parent = BookshelfPath.Parent(folder);
        if (nodes.TryGetValue(parent, out var parentNode)) parentNode.Nodes.Add(node);
        else _folderTree.Nodes.Add(node);
        nodes[folder] = node;
      }
      foreach (var path in expanded)
        if (nodes.TryGetValue(path, out var node)) node.Expand();
      if (!_folderTreeInitialized)
      {
        foreach (TreeNode root in _folderTree.Nodes) root.ExpandAll();
        _folderTreeInitialized = true;
      }
      if (nodes.TryGetValue(_catalog.ActiveFolder, out var active))
      {
        if (initialRender || activeFolderChanged)
          active.EnsureVisible();
        _folderTree.SelectedNode = active;
      }
      if (!initialRender && !activeFolderChanged &&
          previousTop is not null && nodes.TryGetValue(previousTop, out var top))
        _folderTree.TopNode = top;
      _lastRenderedActiveFolder = _catalog.ActiveFolder;
    }
    finally
    {
      _folderTree.EndUpdate();
      _renderingFolderTree = false;
    }
  }

  private static void CollectExpandedFolders(TreeNodeCollection nodes, HashSet<string> expanded)
  {
    foreach (TreeNode node in nodes)
    {
      if (node.IsExpanded) expanded.Add(node.Name);
      CollectExpandedFolders(node.Nodes, expanded);
    }
  }

  private void DrawFolderNode(object? sender, DrawTreeNodeEventArgs e)
  {
    if (e.Node is null) return;
    var g = e.Graphics;
    g.SmoothingMode = SmoothingMode.AntiAlias;

    var rowRect = new Rectangle(0, e.Bounds.Top, _folderTree.ClientSize.Width, e.Bounds.Height);
    var selected = (e.State & TreeNodeStates.Selected) != 0 ||
                   string.Equals(e.Node.Tag as string, _catalog.ActiveFolder, StringComparison.OrdinalIgnoreCase);
    var hovered = !_folderTree.Capture && e.Node == _hoveredNode && !selected;

    using (var bgBrush = new SolidBrush(_folderTree.BackColor))
    {
      g.FillRectangle(bgBrush, rowRect);
    }

    var pillRect = new Rectangle(4, e.Bounds.Top + 2, Math.Max(0, _folderTree.ClientSize.Width - 8), e.Bounds.Height - 4);
    if (selected)
    {
      using var selBrush = new SolidBrush(Color.FromArgb(30, 68, 128));
      using var path = CreateRoundedRectanglePath(pillRect, 8);
      g.FillPath(selBrush, path);
      using var selPen = new Pen(Color.FromArgb(95, 10, 132, 255), 1F);
      g.DrawPath(selPen, path);
    }
    else if (hovered)
    {
      using var hovBrush = new SolidBrush(Color.FromArgb(40, 40, 46));
      using var path = CreateRoundedRectanglePath(pillRect, 8);
      g.FillPath(hovBrush, path);
    }

    var level = e.Node.Level;
    var indentX = 10 + level * 18;
    var centerY = e.Bounds.Top + e.Bounds.Height / 2;

    if (e.Node.Nodes.Count > 0)
    {
      using var chevronPen = new Pen(selected ? Color.FromArgb(220, 235, 255) : Color.FromArgb(152, 152, 157), 1.7f)
      {
        StartCap = LineCap.Round,
        EndCap = LineCap.Round,
        LineJoin = LineJoin.Round,
      };

      var chevronCenterX = indentX + 7;
      if (e.Node.IsExpanded)
      {
        var p1 = new PointF(chevronCenterX - 4f, centerY - 2f);
        var p2 = new PointF(chevronCenterX, centerY + 2.5f);
        var p3 = new PointF(chevronCenterX + 4f, centerY - 2f);
        g.DrawLines(chevronPen, new[] { p1, p2, p3 });
      }
      else
      {
        var p1 = new PointF(chevronCenterX - 2f, centerY - 4f);
        var p2 = new PointF(chevronCenterX + 2.5f, centerY);
        var p3 = new PointF(chevronCenterX - 2f, centerY + 4f);
        g.DrawLines(chevronPen, new[] { p1, p2, p3 });
      }
    }

    var iconX = indentX + 18;
    var iconY = centerY - 8;
    DrawFolderIcon(g, iconX, iconY, selected);

    var textX = iconX + 23;
    var folder = e.Node.Tag as string ?? string.Empty;
    var leafName = BookshelfPath.Leaf(folder);
    if (string.IsNullOrEmpty(leafName)) leafName = e.Node.Text;

    var count = _catalog.Items.Count(item => BookshelfPath.Contains(folder, item.Folder));
    var textColor = selected ? Color.White : TextColor;

    var countText = count.ToString();
    using var countFont = new Font("Segoe UI", 8.25F, FontStyle.Bold);
    var countMeasured = TextRenderer.MeasureText(g, countText, countFont, Size.Empty, TextFormatFlags.NoPadding);
    var badgeWidth = Math.Max(22, countMeasured.Width + 10);
    var badgeRight = pillRect.Right - 8;
    var badgeRect = new Rectangle(badgeRight - badgeWidth, centerY - 9, badgeWidth, 18);

    using var nodeFont = selected ? new Font(_folderTree.Font, FontStyle.Bold) : new Font(_folderTree.Font, FontStyle.Regular);
    var textSize = TextRenderer.MeasureText(g, leafName, nodeFont, Size.Empty,
        TextFormatFlags.NoPadding | TextFormatFlags.SingleLine);

    var textY = centerY - textSize.Height / 2;
    var maxTextWidth = Math.Max(20, badgeRect.Left - textX - 8);

    var nameRect = new Rectangle(textX, textY, Math.Min(textSize.Width, maxTextWidth), textSize.Height);
    TextRenderer.DrawText(g, leafName, nodeFont, nameRect, textColor,
        TextFormatFlags.NoPadding | TextFormatFlags.SingleLine | TextFormatFlags.EndEllipsis);

    if (badgeRect.Left > textX + 16)
    {
      using var badgePath = CreateRoundedRectanglePath(badgeRect, 8);
      using var badgeBrush = new SolidBrush(selected ? Color.FromArgb(10, 132, 255) : Color.FromArgb(44, 44, 50));
      g.FillPath(badgeBrush, badgePath);
      TextRenderer.DrawText(g, countText, countFont, badgeRect,
          selected ? Color.White : MutedText,
          TextFormatFlags.HorizontalCenter | TextFormatFlags.VerticalCenter | TextFormatFlags.NoPadding);
    }
  }

  private static void DrawFolderIcon(Graphics g, int x, int y, bool selected = false)
  {
    using var tabBrush = new SolidBrush(selected ? Color.FromArgb(84, 178, 255) : Color.FromArgb(10, 132, 255));
    using var folderBrush = new SolidBrush(selected ? Color.FromArgb(116, 196, 255) : Color.FromArgb(56, 156, 255));

    using var tabPath = new GraphicsPath();
    tabPath.AddLine(x + 1, y + 2, x + 7, y + 2);
    tabPath.AddLine(x + 9, y + 5, x + 1, y + 5);
    tabPath.CloseFigure();
    g.FillPath(tabBrush, tabPath);

    var bodyRect = new Rectangle(x, y + 4, 17, 11);
    using var bodyPath = CreateRoundedRectanglePath(bodyRect, 3);
    g.FillPath(folderBrush, bodyPath);
    using var highlightPen = new Pen(Color.FromArgb(80, 255, 255, 255), 1F);
    g.DrawLine(highlightPen, x + 2, y + 5, x + 14, y + 5);
  }

  private static GraphicsPath CreateRoundedRectanglePath(Rectangle rect, int radius)
  {
    var path = new GraphicsPath();
    if (rect.Width <= 0 || rect.Height <= 0) return path;
    var diameter = radius * 2;
    if (diameter > rect.Width) diameter = rect.Width;
    if (diameter > rect.Height) diameter = rect.Height;
    var arc = new Rectangle(rect.Location, new Size(diameter, diameter));

    path.AddArc(arc, 180, 90);
    arc.X = rect.Right - diameter;
    path.AddArc(arc, 270, 90);
    arc.Y = rect.Bottom - diameter;
    path.AddArc(arc, 0, 90);
    arc.X = rect.Left;
    path.AddArc(arc, 90, 90);
    path.CloseFigure();
    return path;
  }

  private void RecoverUnavailableStorage(string directory)
  {
    var syncWasEnabled = BookshelfSyncCoordinator.Enabled;
    try
    {
      BookshelfSyncCoordinator.Enabled = false;
      BookshelfStore.RecoverUnavailableStorage(directory);
      var restored = BookshelfStore.Load();
      _catalog.FolderIds = restored.FolderIds;
      _catalog.Folders = restored.Folders;
      _catalog.ActiveFolder = restored.ActiveFolder;
      _catalog.FolderColumnCounts = restored.FolderColumnCounts;
      _catalog.FolderNotes = restored.FolderNotes;
      _catalog.Items = restored.Items;
      RenderFolders();
      RenderGallery();
      MessageBox.Show(this,
          "책장 저장 위치를 연결했습니다. 이전 저장 위치 설정은 백업했습니다. " +
          "책장 내용 확인 후 Google Drive 자동 동기화를 다시 켜 주세요.",
          "책장", MessageBoxButtons.OK, MessageBoxIcon.Information);
    }
    catch (Exception error)
    {
      if (BookshelfStore.ConfiguredCatalogUnavailable) BookshelfSyncCoordinator.Enabled = syncWasEnabled;
      MessageBox.Show(this, "책장 저장 위치를 연결하지 못했습니다.\n\n" + error.Message,
          "책장", MessageBoxButtons.OK, MessageBoxIcon.Warning);
    }
  }

  private async Task ChangeStorageDirectoryAsync()
  {
    if (BookshelfStore.ConfiguredCatalogUnavailable)
    {
      using var reconnectDialog = new FolderBrowserDialog
      {
        Description = "catalog.json이 있는 기존 책장 폴더를 선택하세요.",
        UseDescriptionForTitle = true,
        InitialDirectory = BrowserStateStore.StateDirectory,
      };
      if (reconnectDialog.ShowDialog(this) == DialogResult.OK)
        RecoverUnavailableStorage(reconnectDialog.SelectedPath);
      return;
    }
    CommitShelfNote();
    var currentDirectory = BookshelfStore.StorageDirectory;
    using var dialog = new FolderBrowserDialog
    {
      Description = "책장 전체를 옮길 새 빈 폴더를 선택하세요.",
      UseDescriptionForTitle = true,
      InitialDirectory = Directory.Exists(currentDirectory)
          ? currentDirectory
          : BrowserStateStore.StateDirectory,
      ShowNewFolderButton = true,
    };
    if (dialog.ShowDialog(this) != DialogResult.OK) return;
    var destination = Path.GetFullPath(dialog.SelectedPath);
    if (destination.Equals(currentDirectory, StringComparison.OrdinalIgnoreCase)) return;
    var confirmation = "책장 목록, 메모, 이미지와 폴더를 모두 새 위치로 옮길까요?\n\n" +
                       $"현재: {currentDirectory}\n새 위치: {destination}\n\n" +
                       "복사와 검증이 끝난 뒤에만 기존 위치를 삭제합니다.";
    if (MessageBox.Show(this, confirmation, "책장 저장 위치 변경", MessageBoxButtons.YesNo,
            MessageBoxIcon.Question, MessageBoxDefaultButton.Button2) != DialogResult.Yes) return;

    UseWaitCursor = true;
    Enabled = false;
    _renderGeneration++;
    try
    {
      await Task.Run(() => BookshelfStore.MoveStorage(_catalog, destination));
    }
    catch (Exception error)
    {
      MessageBox.Show(this, "저장 위치를 변경하지 못했습니다. 기존 책장은 그대로 유지됩니다.\n\n" + error.Message,
          "책장", MessageBoxButtons.OK, MessageBoxIcon.Warning);
      return;
    }
    finally
    {
      Enabled = true;
      UseWaitCursor = false;
    }

    try
    {
      RenderFolders();
      RenderGallery();
      MessageBox.Show(this, "책장 전체를 새 저장 위치로 옮겼습니다.", "책장",
          MessageBoxButtons.OK, MessageBoxIcon.Information);
    }
    catch (Exception error)
    {
      MessageBox.Show(this,
          "책장 이동은 완료됐지만 화면을 새로 그리지 못했습니다. 앱을 다시 열면 새 위치에서 정상적으로 불러옵니다.\n\n" +
          error.Message,
          "책장", MessageBoxButtons.OK, MessageBoxIcon.Warning);
    }
  }

  private void RenderGallery(Point? scrollOffset = null)
  {
    if (_galleryHost.ClientSize.Width < 200) return;
    _lastRenderedGalleryWidth = _galleryHost.ClientSize.Width;
    var previousOffset = scrollOffset ?? CurrentGalleryScrollOffset();
    VirtualBookshelfGallery? createdVirtual = null;
    TableLayoutPanel? createdCanvas = null;
    List<GalleryEntry>? pendingImages = null;
    var generation = 0;
    var loadColumnWidth = 0;

    _galleryHost.SuspendLayout();
    try
    {
      DisposeLoadedImages();
      generation = ++_renderGeneration;
      var previousCanvas = _galleryCanvas;
      var previousVirtual = _virtualGallery;
      _galleryHost.Controls.Clear();
      previousCanvas?.Dispose();
      previousVirtual?.Dispose();
      _virtualGallery = null;
      _galleryCanvas = null;
      _galleryEntries.Clear();
      var columnCount = BookshelfStore.GetColumnCount(_catalog, _catalog.ActiveFolder);
      var folderItems = BookshelfStore.VisibleItems(_catalog, _catalog.ActiveFolder).ToArray();
      var availableWidth = Math.Max(360, _galleryHost.ClientSize.Width - _galleryHost.Padding.Horizontal);
      var prelimColumnWidth = Math.Max(72, availableWidth / columnCount);
      var estimatedHeights = new int[columnCount];
      foreach (var item in folderItems)
      {
        var index = Array.IndexOf(estimatedHeights, estimatedHeights.Min());
        estimatedHeights[index] += Math.Max(1, (int)Math.Round(prelimColumnWidth *
            (double)item.Height / Math.Max(1, item.Width)));
      }
      var maxEstimatedHeight = estimatedHeights.Length > 0 ? estimatedHeights.Max() : 0;
      if (maxEstimatedHeight > 28000)
      {
        _galleryHost.AutoScroll = false;
        var virtualGallery = new VirtualBookshelfGallery(folderItems, columnCount,
            item => _ = OpenVirtualGalleryImageAsync(item), DeleteImage,
            canDelete: BookshelfStore.MissingOriginalCount(_catalog) == 0) { Dock = DockStyle.Fill };
        _virtualGallery = virtualGallery;
        createdVirtual = virtualGallery;
        ConfigureDropTarget(virtualGallery);
        _galleryHost.Controls.Add(virtualGallery);
        virtualGallery.ScrollTo(previousOffset.Y);
        return;
      }
      var needsScroll = maxEstimatedHeight > Math.Max(1, _galleryHost.ClientSize.Height - _galleryHost.Padding.Vertical);
      var scrollAllowance = needsScroll ? SystemInformation.VerticalScrollBarWidth + 2 : 0;
      var canvasWidth = Math.Max(360, availableWidth - scrollAllowance);
      _galleryHost.AutoScroll = true;
      _galleryHost.AutoScrollPosition = Point.Empty;
      var canvas = _galleryCanvas = createdCanvas = new TableLayoutPanel
      {
        AutoSize = false,
        ColumnCount = columnCount,
        RowCount = 1,
        BackColor = Background,
        Padding = Padding.Empty,
        Margin = Padding.Empty,
        Location = Point.Empty,
        Width = canvasWidth,
        Height = 1,
      };
      canvas.RowStyles.Add(new RowStyle(SizeType.Absolute, 1));
      ConfigureDropTarget(canvas);
      var columns = _galleryColumns = new List<FlowLayoutPanel>(columnCount);
      var colWidths = ComputeColumnWidths(canvasWidth, columnCount);
      for (var column = 0; column < columnCount; column++)
      {
        canvas.ColumnStyles.Add(new ColumnStyle(SizeType.Absolute, colWidths[column]));
        var panel = CreateColumn();
        panel.Width = colWidths[column];
        ConfigureDropTarget(panel);
        columns.Add(panel);
        canvas.Controls.Add(panel, column, 0);
      }
      _galleryHost.Controls.Add(canvas);

      var columnWidth = loadColumnWidth = colWidths[0];
      var columnHeights = new int[columnCount];
      pendingImages = new List<GalleryEntry>();
      foreach (var item in folderItems)
      {
        var targetColumn = 0;
        for (var column = 1; column < columnHeights.Length; column++)
          if (columnHeights[column] < columnHeights[targetColumn]) targetColumn = column;
        var colW = colWidths[targetColumn];
        var imageHeight = Math.Max(1, (int)Math.Round(colW * (double)item.Height / Math.Max(1, item.Width)));
        var picture = new PictureBox
        {
          Width = colW,
          Height = imageHeight,
          SizeMode = PictureBoxSizeMode.Zoom,
          BackColor = Surface,
          Margin = Padding.Empty,
          Cursor = Cursors.Hand,
        };
        ConfigureDropTarget(picture);
        var itemMenu = new ContextMenuStrip
        {
          BackColor = Surface,
          ForeColor = TextColor,
          ShowImageMargin = false,
          Renderer = new BookshelfMenuRenderer(),
        };
        itemMenu.Items.Add("사진 열기", null, async (_, _) => await OpenBookshelfImageAsync(item, picture));
        var deleteItem = itemMenu.Items.Add("사진 삭제", null, (_, _) => DeleteImage(item));
        deleteItem.ForeColor = Color.FromArgb(255, 69, 58);
        deleteItem.Enabled = BookshelfStore.MissingOriginalCount(_catalog) == 0;
        picture.ContextMenuStrip = itemMenu;
        picture.DoubleClick += async (_, _) => await OpenBookshelfImageAsync(item, picture);
        columns[targetColumn].Controls.Add(picture);
        columnHeights[targetColumn] += imageHeight;
        var galleryEntry = new GalleryEntry(picture, item);
        _galleryEntries.Add(galleryEntry);
        if (File.Exists(item.FilePath)) pendingImages.Add(galleryEntry);
        else ShowUnavailableImage(picture, "원본 사진 없음\n저장 위치 확인");
      }
      ApplyGalleryHeight(canvas, columns, columnHeights);
    }
    finally
    {
      _galleryHost.ResumeLayout(true);
    }

    // Restore scroll position AFTER ResumeLayout(true) so WinForms computes the real scroll range first.
    if (createdVirtual is { IsDisposed: false })
    {
      createdVirtual.Relayout();
      createdVirtual.ScrollTo(previousOffset.Y);
      return;
    }
    if (createdCanvas is { IsDisposed: false })
    {
      RestoreGalleryScrollPosition(previousOffset);
      if (pendingImages is not null)
        _ = LoadGalleryImagesAsync(pendingImages, generation, loadColumnWidth);
    }
  }

  private void RestoreGalleryScrollPosition(Point offset)
  {
    var targetX = Math.Max(0, offset.X);
    var targetY = Math.Max(0, offset.Y);
    _galleryHost.PerformLayout();
    var verticalMax = _galleryHost.VerticalScroll.Visible
        ? Math.Max(0, _galleryHost.VerticalScroll.Maximum - _galleryHost.VerticalScroll.LargeChange + 1)
        : 0;
    var canvasMax = _galleryCanvas is { IsDisposed: false } canvas
        ? Math.Max(0, canvas.Height - _galleryHost.ClientSize.Height)
        : 0;
    var maxScroll = Math.Max(verticalMax, canvasMax);
    var clampedY = Math.Clamp(targetY, 0, maxScroll);
    _galleryHost.AutoScrollPosition = new Point(targetX, clampedY);
    if (_galleryHost.VerticalScroll.Visible)
    {
      var updatedMax = Math.Max(0, _galleryHost.VerticalScroll.Maximum - _galleryHost.VerticalScroll.LargeChange + 1);
      var finalY = Math.Clamp(targetY, 0, Math.Max(updatedMax, maxScroll));
      if (-_galleryHost.AutoScrollPosition.Y != finalY)
        _galleryHost.AutoScrollPosition = new Point(targetX, finalY);
    }
  }

  private async Task OpenBookshelfImageAsync(BookshelfItem item, Control? anchor = null)
  {
    if (!File.Exists(item.FilePath))
    {
      MessageBox.Show(this, "원본 사진을 찾지 못했습니다. 저장 매체나 원본 폴더를 확인해 주세요.",
          "책장", MessageBoxButtons.OK, MessageBoxIcon.Warning);
      return;
    }
    if (CloudShelfMarker.Read(item.FilePath) is not null)
    {
      if (anchor is not null) anchor.Cursor = Cursors.WaitCursor;
      Image? full = null;
      try { full = await Task.Run(() => BookshelfStore.LoadImage(item)); }
      finally { if (anchor is not null && !anchor.IsDisposed) anchor.Cursor = Cursors.Hand; }
      if (full is null)
      {
        MessageBox.Show(this, "클라우드 사진을 가져오지 못했습니다. Google 연결과 인터넷을 확인해 주세요.",
            "책장", MessageBoxButtons.OK, MessageBoxIcon.Warning);
        return;
      }
      using (full)
      using (var viewer = new Form { Text = "책장 사진", WindowState = FormWindowState.Maximized, BackColor = Color.Black })
      {
        WinFormsSurface.Attach(viewer);
        viewer.Controls.Add(new PictureBox { Dock = DockStyle.Fill, Image = full, SizeMode = PictureBoxSizeMode.Zoom });
        viewer.ShowDialog(this);
      }
      return;
    }
    System.Diagnostics.Process.Start(new System.Diagnostics.ProcessStartInfo { FileName = item.FilePath, UseShellExecute = true });
  }

  private async Task OpenVirtualGalleryImageAsync(BookshelfItem item)
  {
    await OpenBookshelfImageAsync(item, _virtualGallery);
  }

  private static void ShowUnavailableImage(PictureBox picture, string message)
  {
    picture.Controls.Clear();
    picture.Controls.Add(new Label
    {
      Dock = DockStyle.Fill,
      Text = message,
      TextAlign = ContentAlignment.MiddleCenter,
      ForeColor = MutedText,
      BackColor = Surface,
      Font = new Font("Segoe UI", 9F),
      ContextMenuStrip = picture.ContextMenuStrip,
    });
  }

  private static int[] ComputeColumnWidths(int totalWidth, int columnCount)
  {
    columnCount = Math.Max(1, columnCount);
    var baseWidth = Math.Max(72, totalWidth / columnCount);
    var remainder = Math.Max(0, totalWidth - baseWidth * columnCount);
    var widths = new int[columnCount];
    for (var i = 0; i < columnCount; i++)
      widths[i] = baseWidth + (i < remainder ? 1 : 0);
    return widths;
  }

  private void ResizeGalleryWithoutReload()
  {
    if (_galleryHost.ClientSize.Width < 200) return;
    var expectedColumns = BookshelfStore.GetColumnCount(_catalog, _catalog.ActiveFolder);
    if (_virtualGallery is { IsDisposed: false })
    {
      _lastRenderedGalleryWidth = _galleryHost.ClientSize.Width;
      _virtualGallery.Relayout();
      return;
    }
    if (_galleryCanvas is null || _galleryCanvas.IsDisposed || _galleryColumns.Count != expectedColumns)
    {
      RenderGallery();
      return;
    }

    var previousOffset = CurrentGalleryScrollOffset();
    _lastRenderedGalleryWidth = _galleryHost.ClientSize.Width;
    var availableWidth = Math.Max(360, _galleryHost.ClientSize.Width - _galleryHost.Padding.Horizontal);

    // Pre-estimate heights at the new width (e.g. entering fullscreen after lowering column count).
    // If the taller scaled layout exceeds Win32's 32,767px child HWND limit, promote to VirtualBookshelfGallery!
    var prelimColW = Math.Max(72, availableWidth / _galleryColumns.Count);
    var estimatedHeights = new int[_galleryColumns.Count];
    foreach (var entry in _galleryEntries)
    {
      if (entry.Picture.IsDisposed) continue;
      var target = 0;
      for (var c = 1; c < estimatedHeights.Length; c++)
        if (estimatedHeights[c] < estimatedHeights[target]) target = c;
      estimatedHeights[target] += Math.Max(1, (int)Math.Round(prelimColW * (double)entry.Item.Height / Math.Max(1, entry.Item.Width)));
    }
    var maxEstimatedHeight = estimatedHeights.Length > 0 ? estimatedHeights.Max() : 0;
    if (maxEstimatedHeight > 28000)
    {
      RenderGallery(previousOffset);
      return;
    }

    var needsScroll = maxEstimatedHeight > Math.Max(1, _galleryHost.ClientSize.Height - _galleryHost.Padding.Vertical);
    var scrollAllowance = needsScroll ? SystemInformation.VerticalScrollBarWidth + 2 : 0;
    var canvasWidth = Math.Max(360, availableWidth - scrollAllowance);
    var colWidths = ComputeColumnWidths(canvasWidth, _galleryColumns.Count);
    var columnHeights = new int[_galleryColumns.Count];

    _galleryHost.SuspendLayout();
    _galleryHost.AutoScrollPosition = Point.Empty;
    _galleryCanvas.SuspendLayout();
    _galleryCanvas.Location = Point.Empty;
    _galleryCanvas.Width = canvasWidth;
    for (var i = 0; i < _galleryColumns.Count; i++)
    {
      if (i < _galleryCanvas.ColumnStyles.Count)
      {
        _galleryCanvas.ColumnStyles[i].SizeType = SizeType.Absolute;
        _galleryCanvas.ColumnStyles[i].Width = colWidths[i];
      }
      _galleryColumns[i].SuspendLayout();
      _galleryColumns[i].Controls.Clear();
      _galleryColumns[i].Width = colWidths[i];
    }
    foreach (var entry in _galleryEntries)
    {
      if (entry.Picture.IsDisposed) continue;
      var targetColumn = 0;
      for (var column = 1; column < columnHeights.Length; column++)
        if (columnHeights[column] < columnHeights[targetColumn]) targetColumn = column;
      var colW = colWidths[targetColumn];
      var height = Math.Max(1, (int)Math.Round(colW * (double)entry.Item.Height / Math.Max(1, entry.Item.Width)));
      entry.Picture.Size = new Size(colW, height);
      _galleryColumns[targetColumn].Controls.Add(entry.Picture);
      columnHeights[targetColumn] += height;
    }
    ApplyGalleryHeight(_galleryCanvas, _galleryColumns, columnHeights);
    // performLayout: true is critical so every FlowLayoutPanel re-flows PictureBoxes to their new
    // Y coordinates and TableLayoutPanel applies the new column widths (eliminating fullscreen overlap!).
    foreach (var column in _galleryColumns) column.ResumeLayout(true);
    _galleryCanvas.ResumeLayout(true);
    _galleryCanvas.PerformLayout();
    _galleryHost.ResumeLayout(true);
    _galleryHost.PerformLayout();
    RestoreGalleryScrollPosition(previousOffset);
    var generation = ++_renderGeneration;
    _ = LoadGalleryImagesAsync(_galleryEntries.ToArray(), generation, colWidths[0]);
  }

  private async Task LoadGalleryImagesAsync(
      IReadOnlyList<GalleryEntry> pending,
      int generation,
      int width)
  {
    var viewportTop = -_galleryHost.AutoScrollPosition.Y;
    var ordered = pending
        .Where(entry => !entry.Picture.IsDisposed)
        .OrderBy(entry => Math.Abs(entry.Picture.Top - viewportTop))
        .ToArray();
    await Task.WhenAll(ordered.Select(LoadEntryAsync));

    async Task LoadEntryAsync(GalleryEntry entry)
    {
      if (generation != _renderGeneration || IsDisposed) return;
      if (!File.Exists(entry.Item.FilePath)) return;
      var targetWidth = Math.Max(1, Math.Min(width, entry.Item.Width));
      if (entry.Picture.Image is not null && entry.LoadedWidth >= targetWidth) return;
      var cloud = CloudShelfMarker.Read(entry.Item.FilePath) is not null;
      if (cloud && entry.Picture.Image is null && entry.Picture.Controls.Count == 0)
        ShowUnavailableImage(entry.Picture, "클라우드 사진 가져오는 중…");
      await GalleryTransfers.WaitAsync();
      try
      {
        if (generation != _renderGeneration || IsDisposed || entry.Picture.IsDisposed) return;
        var targetHeight = Math.Max(1, (int)Math.Round(targetWidth * entry.Item.Height /
            (double)Math.Max(1, entry.Item.Width)));
        Image? image;
        try { image = await Task.Run(() => BookshelfStore.LoadThumbnail(entry.Item, targetWidth, targetHeight)); }
        catch { image = null; }
        if (image is null)
        {
          if (generation == _renderGeneration && !IsDisposed && !entry.Picture.IsDisposed)
            ShowUnavailableImage(entry.Picture, cloud ? "클라우드 사진을 열 수 없음\n연결 확인" : "사진을 열 수 없음\n원본 확인");
          return;
        }
        if (generation != _renderGeneration || IsDisposed || entry.Picture.IsDisposed)
        {
          image.Dispose();
          return;
        }
        var previous = entry.Picture.Image;
        _loadedImages.Add(image);
        entry.Picture.Controls.Clear();
        entry.Picture.Image = image;
        entry.LoadedWidth = image.Width;
        if (previous is not null && !ReferenceEquals(previous, image))
        {
          _loadedImages.Remove(previous);
          previous.Dispose();
        }
      }
      finally { GalleryTransfers.Release(); }
    }
  }

  private void ChangeColumnCount(int delta)
  {
    var current = BookshelfStore.GetColumnCount(_catalog, _catalog.ActiveFolder);
    var updated = Math.Clamp(current + delta, 1, 6);
    if (updated == current) return;
    BookshelfStore.SetColumnCount(_catalog, _catalog.ActiveFolder, updated);
    UpdateColumnControls();
    RenderGallery();
  }

  private void UpdateColumnControls()
  {
    if (_decreaseColumns is null || _increaseColumns is null) return;
    var columns = BookshelfStore.GetColumnCount(_catalog, _catalog.ActiveFolder);
    _columnValue.Text = $"{columns}열";
    var originalsAvailable = BookshelfStore.MissingOriginalCount(_catalog) == 0;
    _decreaseColumns.Enabled = originalsAvailable && columns > 1;
    _increaseColumns.Enabled = originalsAvailable && columns < 6;
  }

  private void UpdateShelfMetadataUi()
  {
    _updatingShelfMetadata = true;
    try
    {
      var folder = _catalog.ActiveFolder;
      _shelfTitle.Text = folder;
      _shelfNote.Text = _catalog.FolderNotes.TryGetValue(folder, out var note) ? note : string.Empty;
      Text = $"{folder} — Omyaru 책장";
    }
    finally
    {
      _updatingShelfMetadata = false;
    }
  }

  private void CommitShelfNote()
  {
    _noteSaveTimer.Stop();
    if (_updatingShelfMetadata || string.IsNullOrWhiteSpace(_catalog.ActiveFolder)) return;
    var folder = _catalog.ActiveFolder;
    if (BookshelfPath.Normalize(folder) is null) return;
    var note = _shelfNote.Text.Length <= 4000 ? _shelfNote.Text : _shelfNote.Text[..4000];
    if (_catalog.FolderNotes.TryGetValue(folder, out var stored) && stored == note) return;
    _catalog.FolderNotes[folder] = note;
    try { BookshelfStore.Save(_catalog); } catch { }
  }

  private static void ApplyGalleryHeight(
      TableLayoutPanel canvas,
      IReadOnlyList<FlowLayoutPanel> columns,
      IReadOnlyList<int> columnHeights)
  {
    var height = Math.Max(1, columnHeights.Count == 0 ? 1 : columnHeights.Max());
    canvas.Height = height;
    if (canvas.RowStyles.Count == 0)
      canvas.RowStyles.Add(new RowStyle(SizeType.Absolute, height));
    else
      canvas.RowStyles[0].Height = height;
    foreach (var column in columns) column.Height = height;
  }

  private static FlowLayoutPanel CreateColumn() => new()
  {
    AutoSize = false,
    Dock = DockStyle.Fill,
    FlowDirection = FlowDirection.TopDown,
    WrapContents = false,
    BackColor = Background,
    Margin = Padding.Empty,
    Padding = Padding.Empty,
  };

  private void CreateFolder() => ChooseFolderParent(null);

  private void MoveFolderInTree(string folder, int direction)
  {
    CommitShelfNote();
    if (!_catalog.Folders.Contains(folder, StringComparer.OrdinalIgnoreCase)) return;
    if (BookshelfStore.MoveFolder(_catalog, folder, direction))
    {
      RenderFolders();
      RenderGallery();
    }
  }

  private void CreateChildFolder(string parent)
  {
    CommitShelfNote();
    var leaf = InputDialog.Show(this, "책장/" + parent, "새 폴더 이름");
    if (string.IsNullOrWhiteSpace(leaf)) return;
    var path = parent.Length == 0 ? leaf : parent + "/" + leaf;
    if (leaf.Contains('/') || BookshelfPath.Normalize(path) is null) { MessageBox.Show(this, "사용할 수 없는 폴더 이름입니다."); return; }
    BookshelfStore.AddFolder(_catalog, path);
    RenderFolders(); RenderGallery();
  }

  private void ChooseFolderParent(string? moving)
  {
    using var dialog = new Form
    {
      Text = moving is null ? "책장 폴더" : "이동할 폴더 선택",
      Width = 500,
      Height = 580,
      StartPosition = FormStartPosition.CenterParent,
      MinimizeBox = false,
      MaximizeBox = false,
      BackColor = Color.FromArgb(28, 28, 30),
      ForeColor = TextColor,
      Font = new Font("Segoe UI", 9.5F),
    };
    WinFormsSurface.Attach(dialog);
    var hint = new Label
    {
      Dock = DockStyle.Top,
      Height = moving is null ? 36 : 48,
      Padding = new Padding(14, 8, 14, 4),
      BackColor = Color.FromArgb(28, 28, 30),
      ForeColor = MutedText,
      Font = new Font("Segoe UI", 9F),
      Text = moving is null
          ? "열고 싶은 폴더를 선택한 뒤 [확인]을 누르거나 더블클릭하세요."
          : $"‘{BookshelfPath.Leaf(moving)}’ 폴더를 넣을 대상 폴더를 선택한 뒤 [확인 (여기로 이동)]을 누르세요.\n최상위로 이동하려면 맨 위의 ‘책장 (최상위)’를 선택하세요.",
    };
    var tree = new TreeView
    {
      Dock = DockStyle.Fill,
      HideSelection = false,
      BackColor = Color.FromArgb(20, 20, 22),
      ForeColor = TextColor,
      BorderStyle = BorderStyle.None,
      ItemHeight = 28,
      Indent = 18,
    };
    var root = tree.Nodes.Add(moving is null ? "책장" : "책장 (최상위)"); root.Tag = "";
    foreach (var path in _catalog.Folders)
    {
      if (moving is not null && BookshelfPath.Contains(moving, path)) continue;
      var node = root; var accumulated = "";
      foreach (var segment in path.Split('/'))
      {
        accumulated = accumulated.Length == 0 ? segment : accumulated + "/" + segment;
        var child = node.Nodes.Cast<TreeNode>().FirstOrDefault(n => n.Text.Equals(segment, StringComparison.OrdinalIgnoreCase));
        if (child is null) { child = node.Nodes.Add(segment); child.Tag = accumulated; }
        node = child;
      }
    }
    root.ExpandAll(); tree.SelectedNode = root;
    tree.NodeMouseClick += (_, args) => {
      tree.SelectedNode = args.Node;
      var path = (string?)args.Node.Tag ?? "";
      if (args.Button != MouseButtons.Right || path.Length == 0) return;
      var menu = new ContextMenuStrip
      {
        BackColor = Surface,
        ForeColor = TextColor,
        ShowImageMargin = false,
        Renderer = new BookshelfMenuRenderer(),
      };
      menu.Items.Add("하위 폴더 만들기", null, (_, _) => BeginInvoke(new Action(() => { dialog.Close(); CreateChildFolder(path); })));
      menu.Items.Add("이름 변경", null, (_, _) => BeginInvoke(new Action(() => { dialog.Close(); RenameFolder(path); })));
      menu.Items.Add("다른 폴더로 이동", null, (_, _) => BeginInvoke(new Action(() => { dialog.Close(); ChooseFolderParent(path); })));
      menu.Closed += (_, _) => BeginInvoke(new Action(menu.Dispose));
      menu.Show(tree, args.Location);
    };
    var bar = new FlowLayoutPanel
    {
      Dock = DockStyle.Bottom,
      Height = 56,
      Padding = new Padding(12, 10, 12, 10),
      BackColor = Color.FromArgb(28, 28, 30),
      FlowDirection = FlowDirection.RightToLeft,
      WrapContents = false,
    };
    var cancel = new PillButton
    {
      Text = "취소",
      Width = 76,
      Height = 36,
      Margin = new Padding(8, 0, 0, 0),
      BackColor = Surface,
      HoverBackColor = Color.FromArgb(58, 58, 64),
      PressedBackColor = Color.FromArgb(72, 72, 78),
      BorderColor = Outline,
      BorderWidth = 1F,
      ForeColor = TextColor,
    };
    var create = new PillButton
    {
      Text = "＋ 새 폴더",
      Width = 104,
      Height = 36,
      Margin = new Padding(8, 0, 0, 0),
      BackColor = Surface,
      HoverBackColor = Color.FromArgb(58, 58, 64),
      PressedBackColor = Color.FromArgb(72, 72, 78),
      BorderColor = Outline,
      BorderWidth = 1F,
      ForeColor = TextColor,
    };
    var select = new PillButton
    {
      Text = moving is null ? "확인 (폴더 열기)" : "확인 (여기로 이동)",
      Width = 142,
      Height = 36,
      Margin = new Padding(8, 0, 0, 0),
      BackColor = Accent,
      HoverBackColor = Color.FromArgb(30, 148, 255),
      PressedBackColor = Color.FromArgb(0, 112, 224),
      BorderColor = Color.Transparent,
      ForeColor = Color.White,
      Font = new Font("Segoe UI Semibold", 9.5F),
    };
    void ConfirmSelection()
    {
      var parent = (string?)tree.SelectedNode?.Tag ?? "";
      if (moving is null)
      {
        if (parent.Length == 0) return;
        _catalog.ActiveFolder = parent;
        try { BookshelfStore.Save(_catalog); } catch { }
      }
      else
      {
        var target = parent.Length == 0 ? BookshelfPath.Leaf(moving) : parent + "/" + BookshelfPath.Leaf(moving);
        if (target.Equals(moving, StringComparison.OrdinalIgnoreCase))
        {
          dialog.Close();
          return;
        }
        if (!BookshelfStore.RenameFolder(_catalog, moving, target))
        {
          MessageBox.Show(dialog, "같은 이름의 폴더가 있거나 이동할 수 없는 위치입니다.", "폴더 이동");
          return;
        }
      }
      dialog.Close();
      RenderFolders();
      RenderGallery(moving is null ? Point.Empty : null);
    }
    cancel.Click += (_, _) => dialog.Close();
    create.Click += (_, _) => { var parent = (string?)tree.SelectedNode?.Tag ?? ""; dialog.Close(); CreateChildFolder(parent); };
    select.Click += (_, _) => ConfirmSelection();
    tree.NodeMouseDoubleClick += (_, args) =>
    {
      tree.SelectedNode = args.Node;
      if (moving is not null || ((string?)args.Node.Tag ?? "").Length > 0)
        ConfirmSelection();
    };
    tree.KeyDown += (_, args) =>
    {
      if (args.KeyCode == Keys.Enter)
      {
        args.SuppressKeyPress = true;
        ConfirmSelection();
      }
      else if (args.KeyCode == Keys.Escape)
      {
        args.SuppressKeyPress = true;
        dialog.Close();
      }
    };
    bar.Controls.AddRange(new Control[] { select, create, cancel });
    dialog.Controls.Add(tree);
    dialog.Controls.Add(hint);
    dialog.Controls.Add(bar);
    dialog.ShowDialog(this);
  }

  private void RenameFolder(string folder)
  {
    CommitShelfNote();
    var leaf = InputDialog.Show(this, "책장 이름 변경", "새 책장 이름", BookshelfPath.Leaf(folder));
    var parent = BookshelfPath.Parent(folder);
    var name = string.IsNullOrWhiteSpace(leaf) ? "" : parent.Length == 0 ? leaf : parent + "/" + leaf;
    if (string.IsNullOrWhiteSpace(name) || name.Trim().Equals(folder, StringComparison.CurrentCulture)) return;
    if (!BookshelfStore.RenameFolder(_catalog, folder, name))
    {
      MessageBox.Show(this, "같은 이름의 책장이 있거나 사용할 수 없는 이름입니다.", "책장");
      return;
    }
    RenderFolders();
    RenderGallery();
  }

  private void DeleteFolder(string folder)
  {
    if (_catalog.Folders.Any(p => p.StartsWith(folder + "/", StringComparison.OrdinalIgnoreCase)))
    { MessageBox.Show(this, "하위 폴더를 먼저 이동하거나 삭제해 주세요.", "책장"); return; }
    CommitShelfNote();
    var count = _catalog.Items.Count(item => item.Folder.Equals(folder, StringComparison.CurrentCultureIgnoreCase));
    var message = $"‘{folder}’ 책장을 삭제할까요?\n포함된 사진 {count}개도 함께 삭제되며 되돌릴 수 없습니다.";
    if (MessageBox.Show(this, message, "책장 삭제 확인", MessageBoxButtons.YesNo,
            MessageBoxIcon.Warning, MessageBoxDefaultButton.Button2) != DialogResult.Yes) return;
    DisposeLoadedImages();
    BookshelfStore.DeleteFolder(_catalog, folder);
    RenderFolders();
    RenderGallery();
  }

  private void AddImagesFromFiles()
  {
    using var dialog = new OpenFileDialog
    {
      Title = $"‘{_catalog.ActiveFolder}’ 책장에 사진 추가",
      Filter = "이미지 파일|*.png;*.jpg;*.jpeg;*.webp;*.bmp;*.gif|모든 파일|*.*",
      Multiselect = true,
      CheckFileExists = true,
    };
    if (dialog.ShowDialog(this) != DialogResult.OK) return;
    _ = ImportImageFilesAsync(dialog.FileNames, showPartialFailure: true);
  }

  private void ConfigureDropTarget(Control target)
  {
    target.AllowDrop = true;
    target.DragEnter += (_, eventArgs) =>
    {
      eventArgs.Effect = HasImportableDrop(eventArgs.Data) ? DragDropEffects.Copy : DragDropEffects.None;
    };
    target.DragDrop += async (_, eventArgs) => await ImportDropDataAsync(eventArgs.Data);
  }

  private static bool HasImportableDrop(IDataObject? data)
  {
    if (data is null) return false;
    if (data.GetDataPresent(DataFormats.Bitmap)) return true;
    if (!data.GetDataPresent(DataFormats.FileDrop) || data.GetData(DataFormats.FileDrop) is not string[] files) return false;
    return files.Any(IsSupportedImageFile);
  }

  private async Task ImportDropDataAsync(IDataObject? data)
  {
    if (_importing || data is null) return;
    if (data.GetDataPresent(DataFormats.FileDrop) && data.GetData(DataFormats.FileDrop) is string[] files)
    {
      await ImportImageFilesAsync(files, showPartialFailure: true);
      return;
    }
    if (data.GetDataPresent(DataFormats.Bitmap) && data.GetData(DataFormats.Bitmap) is Image image)
    {
      using var copy = new Bitmap(image);
      await ImportClipboardImageAsync(copy);
    }
  }

  private async Task PasteFromClipboardAsync()
  {
    if (_importing) return;
    try
    {
      if (Clipboard.ContainsImage())
      {
        using var source = Clipboard.GetImage();
        if (source is null) return;
        using var copy = new Bitmap(source);
        await ImportClipboardImageAsync(copy);
        return;
      }
      if (Clipboard.ContainsFileDropList())
      {
        var files = Clipboard.GetFileDropList().Cast<string>().ToArray();
        await ImportImageFilesAsync(files, showPartialFailure: true);
      }
    }
    catch (Exception error)
    {
      MessageBox.Show(this, "클립보드 이미지를 추가하지 못했습니다.\n" + error.Message,
          "책장", MessageBoxButtons.OK, MessageBoxIcon.Warning);
    }
  }

  private void RunOnUi(Action action)
  {
    if (IsDisposed) return;
    if (InvokeRequired) Invoke(action);
    else action();
  }

  private async Task ImportClipboardImageAsync(Bitmap image)
  {
    if (_importing) return;
    CommitShelfNote();
    _importing = true;
    UseWaitCursor = true;
    try
    {
      using var owned = new Bitmap(image);
      await Task.Run(() => BookshelfStore.AddImage(_catalog, owned, "clipboard"));
      RunOnUi(() =>
      {
        RenderFolders();
        RenderGallery(new Point(0, int.MaxValue));
      });
    }
    finally
    {
      RunOnUi(() =>
      {
        UseWaitCursor = false;
        _importing = false;
      });
    }
  }

  private async Task ImportImageFilesAsync(IEnumerable<string> candidates, bool showPartialFailure)
  {
    if (_importing) return;
    CommitShelfNote();
    var files = candidates
        .Where(IsSupportedImageFile)
        .Distinct(StringComparer.OrdinalIgnoreCase)
        .ToArray();
    if (files.Length == 0) return;
    _importing = true;
    UseWaitCursor = true;
    var added = 0;
    try
    {
      added = await Task.Run(() =>
      {
        var imported = 0;
        foreach (var filePath in files)
        {
          try
          {
            using var source = Image.FromFile(filePath);
            using var copy = new Bitmap(source);
            BookshelfStore.AddImage(_catalog, copy, filePath);
            imported++;
          }
          catch
          {
            // Keep the successful files when one item is unreadable.
          }
        }
        return imported;
      });
      RunOnUi(() =>
      {
        var scrollOffset = added > 0 ? new Point(0, int.MaxValue) : CurrentGalleryScrollOffset();
        RenderFolders();
        RenderGallery(scrollOffset);
        if (showPartialFailure && added != files.Length)
          MessageBox.Show(this, $"{files.Length}개 중 {added}개를 추가했습니다.", "책장");
      });
    }
    finally
    {
      RunOnUi(() =>
      {
        UseWaitCursor = false;
        _importing = false;
      });
    }
  }

  private static bool IsSupportedImageFile(string filePath) =>
      File.Exists(filePath) && SupportedImageExtensions.Contains(Path.GetExtension(filePath));

  private void DeleteImage(BookshelfItem item)
  {
    CommitShelfNote();
    if ((ModifierKeys & Keys.Shift) == 0 &&
        MessageBox.Show(this, "이 사진을 책장에서 삭제할까요?\n\nShift를 누른 상태에서 삭제하면 이 확인창이 나타나지 않습니다.", "책장",
            MessageBoxButtons.YesNo, MessageBoxIcon.Question) != DialogResult.Yes) return;
    var scrollOffset = CurrentGalleryScrollOffset();
    DisposeLoadedImages();
    BookshelfStore.RemoveImage(_catalog, item);
    RenderFolders();
    RenderGallery(scrollOffset);
  }

  internal Point GalleryScrollOffsetForTest => CurrentGalleryScrollOffset();
  internal int? GalleryCanvasTopForTest => _galleryCanvas is { IsDisposed: false } canvas ? canvas.Top : null;
  internal bool IsVirtualGalleryActiveForTest => _virtualGallery is { IsDisposed: false };
  internal bool VerifyColumnChangeAndFullscreenLayoutForTest(int delta, Size fullscreenSize)
  {
    ChangeColumnCount(delta);
    Size = fullscreenSize;
    _header.Visible = false;
    _sidebar.Visible = false;
    _layout.RowStyles[0].Height = 0;
    _layout.ColumnStyles[0].Width = 0;
    _galleryHost.Padding = Padding.Empty;
    _layout.PerformLayout();
    _galleryHost.PerformLayout();
    ResizeGalleryWithoutReload();
    if (_virtualGallery is { IsDisposed: false }) return true;
    if (_galleryCanvas is null || _galleryCanvas.IsDisposed || _galleryColumns.Count == 0) return false;
    var expectedMinWidth = _galleryHost.ClientSize.Width - SystemInformation.VerticalScrollBarWidth - 8;
    if (_galleryCanvas.Width < expectedMinWidth) return false;
    foreach (var col in _galleryColumns)
    {
      var prevBottom = 0;
      foreach (Control child in col.Controls)
      {
        if (child.Top < prevBottom) return false; // overlap!
        if (Math.Abs(child.Width - col.Width) > 2) return false; // didn't fill column!
        prevBottom = child.Bottom;
      }
    }
    return true;
  }
  internal void ScrollGalleryForTest(int y)
  {
    if (_virtualGallery is { IsDisposed: false } virtualGallery)
    {
      virtualGallery.Relayout();
      virtualGallery.ScrollTo(y);
      return;
    }
    RestoreGalleryScrollPosition(new Point(0, y));
  }
  internal Task PasteBitmapForTestAsync(Bitmap image) => ImportClipboardImageAsync(image);

  private Point CurrentGalleryScrollOffset() => _virtualGallery is { IsDisposed: false } virtualGallery
      ? new Point(0, virtualGallery.ScrollOffset)
      : new Point(Math.Max(0, -_galleryHost.AutoScrollPosition.X), Math.Max(0, -_galleryHost.AutoScrollPosition.Y));

  private void DisposeLoadedImages()
  {
    _renderGeneration++;
    foreach (var entry in _galleryEntries)
    {
      if (entry.Picture.IsDisposed) continue;
      entry.Picture.Image = null;
      entry.LoadedWidth = 0;
    }
    foreach (var image in _loadedImages)
    {
      try { image.Dispose(); } catch { }
    }
    _loadedImages.Clear();
  }

  private sealed class GalleryEntry(PictureBox picture, BookshelfItem item)
  {
    internal PictureBox Picture { get; } = picture;
    internal BookshelfItem Item { get; } = item;
    internal int LoadedWidth { get; set; }
  }
}

internal sealed class BookshelfMenuRenderer() : ToolStripProfessionalRenderer(new BookshelfMenuColorTable())
{
  protected override void OnRenderItemText(ToolStripItemTextRenderEventArgs eventArgs)
  {
    eventArgs.TextColor = !eventArgs.Item.Enabled
        ? Color.FromArgb(110, 110, 118)
        : eventArgs.Item.ForeColor != SystemColors.ControlText && eventArgs.Item.ForeColor != Color.Empty && !eventArgs.Item.Selected
            ? eventArgs.Item.ForeColor
            : Color.FromArgb(245, 245, 247);
    base.OnRenderItemText(eventArgs);
  }
}

internal sealed class BookshelfMenuColorTable : ProfessionalColorTable
{
  public override Color ToolStripDropDownBackground => Color.FromArgb(36, 36, 40);
  public override Color MenuBorder => Color.FromArgb(62, 62, 68);
  public override Color MenuItemBorder => Color.FromArgb(10, 132, 255);
  public override Color MenuItemSelected => Color.FromArgb(10, 132, 255);
  public override Color MenuItemSelectedGradientBegin => Color.FromArgb(10, 132, 255);
  public override Color MenuItemSelectedGradientEnd => Color.FromArgb(10, 132, 255);
  public override Color ImageMarginGradientBegin => Color.FromArgb(36, 36, 40);
  public override Color ImageMarginGradientMiddle => Color.FromArgb(36, 36, 40);
  public override Color ImageMarginGradientEnd => Color.FromArgb(36, 36, 40);
  public override Color SeparatorDark => Color.FromArgb(58, 58, 64);
  public override Color SeparatorLight => Color.FromArgb(58, 58, 64);
}
