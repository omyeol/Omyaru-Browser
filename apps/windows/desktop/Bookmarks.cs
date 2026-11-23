using System.Drawing.Drawing2D;
using System.Drawing.Imaging;
using System.Net;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;

namespace PrivacyBrowser.Desktop;

internal sealed record BookmarkFolderEntry(
    string Id,
    string Name,
    string? ParentId = null,
    int Order = 0,
    long CreatedAtEpochMs = 0);

internal sealed record BookmarkEntry(
    string Id,
    string Name,
    string Url,
    string Glyph,
    int ColorArgb,
    string? FolderId = null);

internal sealed class BookmarkCatalog
{
  public List<BookmarkFolderEntry> Folders { get; set; } = [];
  public List<BookmarkEntry> Items { get; set; } = [];
  public HashSet<string> DeletedIds { get; set; } = new(StringComparer.Ordinal);
  public HashSet<string> DeletedUrlKeys { get; set; } = new(StringComparer.OrdinalIgnoreCase);
  public HashSet<string> DeletedFolderIds { get; set; } = new(StringComparer.Ordinal);

  internal BookmarkCatalog Copy() => new()
  {
    Folders = [.. Folders],
    Items = [.. Items],
    DeletedIds = new HashSet<string>(DeletedIds, StringComparer.Ordinal),
    DeletedUrlKeys = new HashSet<string>(DeletedUrlKeys, StringComparer.OrdinalIgnoreCase),
    DeletedFolderIds = new HashSet<string>(DeletedFolderIds, StringComparer.Ordinal),
  };
}

internal static class BookmarkStore
{
  private static readonly JsonSerializerOptions JsonOptions = new()
  {
    PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
    WriteIndented = true,
  };

  internal static string FilePath => Path.Combine(BrowserStateStore.StateDirectory, "bookmarks.json");

  internal static bool ExistsOnDisk => File.Exists(FilePath);

  internal static BookmarkCatalog Load()
  {
    try
    {
      if (!File.Exists(FilePath)) return DefaultCatalog();
      var catalog = JsonSerializer.Deserialize<BookmarkCatalog>(File.ReadAllText(FilePath), JsonOptions)
          ?? DefaultCatalog();
      var beforeItems = catalog.Items.Count;
      var beforeFolders = catalog.Folders.Count;
      Normalize(catalog);
      // Persist automatic duplicate cleanup once so duplicate rows never linger on disk.
      if (catalog.Items.Count != beforeItems || catalog.Folders.Count != beforeFolders)
      {
        try { Save(catalog); } catch { }
      }
      return catalog;
    }
    catch
    {
      return DefaultCatalog();
    }
  }

  internal static void Save(BookmarkCatalog catalog)
  {
    Normalize(catalog);
    Directory.CreateDirectory(BrowserStateStore.StateDirectory);
    var temporaryPath = FilePath + ".new";
    File.WriteAllText(temporaryPath, JsonSerializer.Serialize(catalog, JsonOptions));
    File.Move(temporaryPath, FilePath, true);
  }

  internal static bool TryNormalizeUrl(string? value, out string normalized)
  {
    normalized = (value ?? string.Empty).Trim();
    if (!normalized.Contains("://", StringComparison.Ordinal)) normalized = "https://" + normalized;
    if (!Uri.TryCreate(normalized, UriKind.Absolute, out var uri) ||
        (uri.Scheme != Uri.UriSchemeHttps && uri.Scheme != Uri.UriSchemeHttp))
    {
      normalized = string.Empty;
      return false;
    }
    normalized = normalized[..Math.Min(normalized.Length, 8192)];
    return true;
  }

  /// <summary>
  /// Canonical deduplication/matching key for a bookmark URL:
  /// lowercases host, strips redundant www./m. prefixes, removes trailing root slash,
  /// and preserves distinct paths and query strings verbatim.
  /// </summary>
  internal static string CanonicalUrlKey(string? value)
  {
    if (!TryNormalizeUrl(value, out var normalized) || !Uri.TryCreate(normalized, UriKind.Absolute, out var uri))
      return string.Empty;
    var host = uri.IdnHost.ToLowerInvariant();
    if (host.StartsWith("www.", StringComparison.Ordinal)) host = host[4..];
    else if (host.StartsWith("m.", StringComparison.Ordinal)) host = host[2..];
    var port = uri.IsDefaultPort ? string.Empty : ":" + uri.Port;
    var path = uri.AbsolutePath;
    if (path == "/") path = string.Empty;
    else path = path.TrimEnd('/');
    return host + port + path + uri.Query;
  }

  internal static BookmarkEntry AddOrUpdate(BookmarkCatalog catalog, string name, string rawUrl, string? folderId = null)
  {
    if (!TryNormalizeUrl(rawUrl, out var url))
      throw new ArgumentException("Invalid bookmark URL.", nameof(rawUrl));
    var validFolderId = !string.IsNullOrWhiteSpace(folderId) && catalog.Folders.Any(f => f.Id == folderId)
        ? folderId
        : null;
    var key = CanonicalUrlKey(url);
    if (!string.IsNullOrEmpty(key)) catalog.DeletedUrlKeys.Remove(key);
    var trimmedName = string.IsNullOrWhiteSpace(name) ? url : name.Trim();
    var existingIdx = catalog.Items.FindIndex(item =>
        string.Equals(item.FolderId ?? string.Empty, validFolderId ?? string.Empty, StringComparison.Ordinal) &&
        string.Equals(CanonicalUrlKey(item.Url), key, StringComparison.OrdinalIgnoreCase));
    if (existingIdx >= 0)
    {
      var updated = catalog.Items[existingIdx] with { Name = trimmedName, Url = url, Glyph = GlyphFor(trimmedName), FolderId = validFolderId };
      catalog.DeletedIds.Remove(updated.Id);
      catalog.Items[existingIdx] = updated;
      return updated;
    }
    var created = new BookmarkEntry(Guid.NewGuid().ToString("N"), trimmedName, url, GlyphFor(trimmedName), ColorFor(catalog.Items.Count), validFolderId);
    catalog.Items.Add(created);
    return created;
  }

  internal static bool Remove(BookmarkCatalog catalog, BookmarkEntry entry)
  {
    var key = CanonicalUrlKey(entry.Url);
    if (!string.IsNullOrWhiteSpace(entry.Id)) catalog.DeletedIds.Add(entry.Id);
    if (!string.IsNullOrWhiteSpace(key)) catalog.DeletedUrlKeys.Add(key);
    var removed = catalog.Items.RemoveAll(item =>
        string.Equals(item.Id, entry.Id, StringComparison.Ordinal) ||
        (!string.IsNullOrWhiteSpace(key) && string.Equals(CanonicalUrlKey(item.Url), key, StringComparison.OrdinalIgnoreCase)));
    return removed > 0;
  }

  internal static BookmarkFolderEntry CreateFolder(BookmarkCatalog catalog, string name, string? parentId = null)
  {
    var cleanName = string.Join(' ', (name ?? string.Empty).Split((char[]?)null, StringSplitOptions.RemoveEmptyEntries));
    if (string.IsNullOrWhiteSpace(cleanName)) cleanName = "새 폴더";
    cleanName = cleanName[..Math.Min(cleanName.Length, 80)];
    var validParent = !string.IsNullOrWhiteSpace(parentId) && catalog.Folders.Any(f => f.Id == parentId) ? parentId : null;
    var existing = catalog.Folders.FirstOrDefault(f =>
        string.Equals(f.ParentId ?? string.Empty, validParent ?? string.Empty, StringComparison.Ordinal) &&
        string.Equals(f.Name, cleanName, StringComparison.OrdinalIgnoreCase));
    if (existing is not null)
    {
      catalog.DeletedFolderIds.Remove(existing.Id);
      return existing;
    }
    var siblings = catalog.Folders.Count(f => string.Equals(f.ParentId ?? string.Empty, validParent ?? string.Empty, StringComparison.Ordinal));
    var created = new BookmarkFolderEntry(Guid.NewGuid().ToString("N"), cleanName, validParent, siblings, DateTimeOffset.UtcNow.ToUnixTimeMilliseconds());
    catalog.Folders.Add(created);
    return created;
  }

  internal static bool RenameFolder(BookmarkCatalog catalog, string folderId, string newName)
  {
    var cleanName = string.Join(' ', (newName ?? string.Empty).Split((char[]?)null, StringSplitOptions.RemoveEmptyEntries));
    if (string.IsNullOrWhiteSpace(cleanName)) return false;
    cleanName = cleanName[..Math.Min(cleanName.Length, 80)];
    var idx = catalog.Folders.FindIndex(f => f.Id == folderId);
    if (idx < 0) return false;
    catalog.Folders[idx] = catalog.Folders[idx] with { Name = cleanName };
    return true;
  }

  internal static bool IsDescendantOrSelf(BookmarkCatalog catalog, string? candidateId, string ancestorId)
  {
    var current = candidateId;
    var guard = 0;
    while (!string.IsNullOrWhiteSpace(current) && guard++ < 64)
    {
      if (string.Equals(current, ancestorId, StringComparison.Ordinal)) return true;
      current = catalog.Folders.FirstOrDefault(f => f.Id == current)?.ParentId;
    }
    return false;
  }

  internal static bool MoveFolder(BookmarkCatalog catalog, string folderId, string? newParentId)
  {
    var idx = catalog.Folders.FindIndex(f => f.Id == folderId);
    if (idx < 0) return false;
    var validParent = !string.IsNullOrWhiteSpace(newParentId) && catalog.Folders.Any(f => f.Id == newParentId) ? newParentId : null;
    if (validParent is not null && IsDescendantOrSelf(catalog, validParent, folderId)) return false;
    catalog.Folders[idx] = catalog.Folders[idx] with { ParentId = validParent };
    return true;
  }

  internal static bool MoveBookmarkToFolder(BookmarkCatalog catalog, string bookmarkId, string? targetFolderId)
  {
    var idx = catalog.Items.FindIndex(b => b.Id == bookmarkId);
    if (idx < 0) return false;
    var validFolder = !string.IsNullOrWhiteSpace(targetFolderId) && catalog.Folders.Any(f => f.Id == targetFolderId) ? targetFolderId : null;
    if (string.Equals(catalog.Items[idx].FolderId ?? string.Empty, validFolder ?? string.Empty, StringComparison.Ordinal)) return false;
    catalog.Items[idx] = catalog.Items[idx] with { FolderId = validFolder };
    return true;
  }

  internal static int DeleteFolder(BookmarkCatalog catalog, string folderId)
  {
    var toRemove = new HashSet<string>(StringComparer.Ordinal);
    void Collect(string id)
    {
      if (!toRemove.Add(id)) return;
      foreach (var child in catalog.Folders.Where(f => f.ParentId == id).ToArray())
        Collect(child.Id);
    }
    Collect(folderId);
    foreach (var id in toRemove) catalog.DeletedFolderIds.Add(id);
    catalog.Folders.RemoveAll(f => toRemove.Contains(f.Id));
    var removedItems = catalog.Items.Where(b => b.FolderId is not null && toRemove.Contains(b.FolderId)).ToArray();
    foreach (var item in removedItems)
    {
      catalog.DeletedIds.Add(item.Id);
      var key = CanonicalUrlKey(item.Url);
      if (!string.IsNullOrEmpty(key)) catalog.DeletedUrlKeys.Add(key);
    }
    catalog.Items.RemoveAll(b => b.FolderId is not null && toRemove.Contains(b.FolderId));
    return removedItems.Length;
  }

  internal static int RecursiveBookmarkCount(BookmarkCatalog catalog, string? folderId)
  {
    if (string.IsNullOrWhiteSpace(folderId)) return catalog.Items.Count;
    var ids = new HashSet<string>(StringComparer.Ordinal);
    void Collect(string id)
    {
      if (!ids.Add(id)) return;
      foreach (var child in catalog.Folders.Where(f => f.ParentId == id)) Collect(child.Id);
    }
    Collect(folderId);
    return catalog.Items.Count(b => b.FolderId is not null && ids.Contains(b.FolderId));
  }

  internal static int CleanDuplicates(BookmarkCatalog catalog)
  {
    var beforeItems = catalog.Items.Count;
    var beforeFolders = catalog.Folders.Count;
    Normalize(catalog);
    // Global URL deduplication across folders (keep first occurrence in tree order)
    var seenGlobal = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
    var kept = new List<BookmarkEntry>();
    foreach (var item in catalog.Items)
    {
      var key = CanonicalUrlKey(item.Url);
      if (!string.IsNullOrEmpty(key) && !seenGlobal.Add(key))
      {
        catalog.DeletedIds.Add(item.Id);
        continue;
      }
      kept.Add(item);
    }
    catalog.Items = kept;
    return (beforeItems - catalog.Items.Count) + Math.Max(0, beforeFolders - catalog.Folders.Count);
  }

  internal static void ExportHtml(BookmarkCatalog catalog, string path)
  {
    var sb = new StringBuilder();
    sb.AppendLine("<!DOCTYPE NETSCAPE-Bookmark-file-1>");
    sb.AppendLine("<META HTTP-EQUIV=\"Content-Type\" CONTENT=\"text/html; charset=UTF-8\">");
    sb.AppendLine("<TITLE>Omyaru Bookmarks</TITLE>");
    sb.AppendLine("<H1>Omyaru Bookmarks</H1>");
    sb.AppendLine("<DL><p>");

    void WriteFolderContents(string? folderId, int depth)
    {
      var indent = new string(' ', depth * 2);
      foreach (var folder in catalog.Folders
          .Where(f => string.Equals(f.ParentId ?? string.Empty, folderId ?? string.Empty, StringComparison.Ordinal))
          .OrderBy(f => f.Order))
      {
        sb.AppendLine($"{indent}<DT><H3>{WebUtility.HtmlEncode(folder.Name)}</H3>");
        sb.AppendLine($"{indent}<DL><p>");
        WriteFolderContents(folder.Id, depth + 1);
        sb.AppendLine($"{indent}</DL><p>");
      }
      foreach (var item in catalog.Items.Where(b => string.Equals(b.FolderId ?? string.Empty, folderId ?? string.Empty, StringComparison.Ordinal)))
      {
        sb.AppendLine($"{indent}<DT><A HREF=\"{WebUtility.HtmlEncode(item.Url)}\">{WebUtility.HtmlEncode(item.Name)}</A>");
      }
    }

    WriteFolderContents(null, 1);
    sb.AppendLine("</DL><p>");
    File.WriteAllText(path, sb.ToString(), new UTF8Encoding(false));
  }

  internal static (int AddedBookmarks, int AddedFolders) ImportHtml(BookmarkCatalog catalog, string path, string? targetFolderId = null)
  {
    var html = File.ReadAllText(path, Encoding.UTF8);
    var tokenRegex = new Regex(
        @"<(?<close>/)?(?<tag>h3|a|dl)\b(?<attrs>[^>]*)>(?:(?<text>[^<]*)</(?:h3|a)>)?",
        RegexOptions.IgnoreCase | RegexOptions.Singleline);
    var hrefRegex = new Regex(@"href\s*=\s*(?:""(?<u>[^""]+)""|'(?<u>[^']+)'|(?<u>[^\s>]+))", RegexOptions.IgnoreCase);

    var folderStack = new Stack<string?>();
    folderStack.Push(targetFolderId);
    string? pendingFolderId = null;
    var addedBookmarks = 0;
    var addedFolders = 0;

    foreach (Match m in tokenRegex.Matches(html))
    {
      var isClose = m.Groups["close"].Success;
      var tag = m.Groups["tag"].Value.ToLowerInvariant();
      if (tag == "h3" && !isClose)
      {
        var title = WebUtility.HtmlDecode(m.Groups["text"].Value).Trim();
        if (!string.IsNullOrWhiteSpace(title))
        {
          var before = catalog.Folders.Count;
          var folder = CreateFolder(catalog, title, folderStack.Peek());
          if (catalog.Folders.Count > before) addedFolders++;
          pendingFolderId = folder.Id;
        }
      }
      else if (tag == "dl")
      {
        if (!isClose)
        {
          folderStack.Push(pendingFolderId ?? folderStack.Peek());
          pendingFolderId = null;
        }
        else if (folderStack.Count > 1)
        {
          folderStack.Pop();
        }
      }
      else if (tag == "a" && !isClose)
      {
        var attrs = m.Groups["attrs"].Value;
        var hm = hrefRegex.Match(attrs);
        if (!hm.Success) continue;
        var rawUrl = WebUtility.HtmlDecode(hm.Groups["u"].Value).Trim();
        if (!TryNormalizeUrl(rawUrl, out var url)) continue;
        var title = WebUtility.HtmlDecode(m.Groups["text"].Value).Trim();
        if (string.IsNullOrWhiteSpace(title)) title = url;
        var before = catalog.Items.Count;
        AddOrUpdate(catalog, title, url, folderStack.Peek());
        if (catalog.Items.Count > before) addedBookmarks++;
      }
    }
    Normalize(catalog);
    return (addedBookmarks, addedFolders);
  }

  private static void Normalize(BookmarkCatalog catalog)
  {
    catalog.DeletedIds = new HashSet<string>(
        (catalog.DeletedIds ?? []).Where(id => !string.IsNullOrWhiteSpace(id)).Take(2000),
        StringComparer.Ordinal);
    catalog.DeletedUrlKeys = new HashSet<string>(
        (catalog.DeletedUrlKeys ?? []).Where(k => !string.IsNullOrWhiteSpace(k)).Take(2000),
        StringComparer.OrdinalIgnoreCase);
    catalog.DeletedFolderIds = new HashSet<string>(
        (catalog.DeletedFolderIds ?? []).Where(id => !string.IsNullOrWhiteSpace(id)).Take(1000),
        StringComparer.Ordinal);

    // 1. Normalize & deduplicate folders by (ParentId, Name), remapping collapsed IDs cleanly.
    var idRemap = new Dictionary<string, string>(StringComparer.Ordinal);
    var cleanedFolders = new List<BookmarkFolderEntry>();
    var seenFolderIds = new HashSet<string>(StringComparer.Ordinal);
    var seenFolderKeyToId = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
    foreach (var raw in catalog.Folders ?? [])
    {
      if (string.IsNullOrWhiteSpace(raw.Id) || string.IsNullOrWhiteSpace(raw.Name)) continue;
      if (!seenFolderIds.Add(raw.Id)) continue;
      var cleanName = string.Join(' ', raw.Name.Split((char[]?)null, StringSplitOptions.RemoveEmptyEntries));
      if (string.IsNullOrWhiteSpace(cleanName)) continue;
      cleanName = cleanName[..Math.Min(cleanName.Length, 80)];
      var parentId = string.IsNullOrWhiteSpace(raw.ParentId) ? null : raw.ParentId;
      if (parentId is not null && idRemap.TryGetValue(parentId, out var mappedParent)) parentId = mappedParent;
      var dedupeKey = (parentId ?? string.Empty) + "\n" + cleanName;
      if (seenFolderKeyToId.TryGetValue(dedupeKey, out var existingId))
      {
        idRemap[raw.Id] = existingId;
        continue;
      }
      seenFolderKeyToId[dedupeKey] = raw.Id;
      cleanedFolders.Add(raw with { Name = cleanName, ParentId = parentId });
      if (cleanedFolders.Count >= 400) break;
    }

    // Remove broken/cyclic ParentId references
    var validFolderIds = cleanedFolders.Select(f => f.Id).ToHashSet(StringComparer.Ordinal);
    for (var i = 0; i < cleanedFolders.Count; i++)
    {
      var f = cleanedFolders[i];
      var parent = f.ParentId;
      if (parent is not null && idRemap.TryGetValue(parent, out var mapped)) parent = mapped;
      if (parent is null || !validFolderIds.Contains(parent) || string.Equals(parent, f.Id, StringComparison.Ordinal))
        parent = null;
      cleanedFolders[i] = f with { ParentId = parent };
    }
    catalog.Folders = cleanedFolders;

    // 2. Normalize & deduplicate bookmark items by Id and (FolderId, CanonicalUrlKey).
    var seenIds = new HashSet<string>(StringComparer.Ordinal);
    var seenScopeKeys = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
    var cleaned = new List<BookmarkEntry>();
    foreach (var item in catalog.Items ?? [])
    {
      if (string.IsNullOrWhiteSpace(item.Id) || string.IsNullOrWhiteSpace(item.Name) || !TryNormalizeUrl(item.Url, out var url))
        continue;
      if (!seenIds.Add(item.Id)) continue;
      var folderId = string.IsNullOrWhiteSpace(item.FolderId) ? null : item.FolderId;
      if (folderId is not null && idRemap.TryGetValue(folderId, out var mappedFolder)) folderId = mappedFolder;
      if (folderId is not null && !validFolderIds.Contains(folderId)) folderId = null;

      var urlKey = CanonicalUrlKey(url);
      var scopeKey = (folderId ?? string.Empty) + "\n" + urlKey;
      if (!string.IsNullOrEmpty(urlKey) && !seenScopeKeys.Add(scopeKey)) continue;
      var name = string.Join(' ', item.Name.Split((char[]?)null, StringSplitOptions.RemoveEmptyEntries));
      cleaned.Add(item with
      {
        Name = name[..Math.Min(name.Length, 80)],
        Url = url,
        Glyph = string.IsNullOrWhiteSpace(item.Glyph) ? GlyphFor(name) : item.Glyph[..Math.Min(item.Glyph.Length, 2)],
        FolderId = folderId,
      });
      if (cleaned.Count >= 2000) break;
    }
    catalog.Items = cleaned;
  }

  internal static string GlyphFor(string name) => string.IsNullOrWhiteSpace(name)
      ? "◆"
      : name.Trim()[0].ToString().ToUpperInvariant();

  internal static int ColorFor(int index) => new[]
  {
    Color.FromArgb(222, 88, 44).ToArgb(),
    Color.FromArgb(3, 199, 90).ToArgb(),
    Color.FromArgb(66, 133, 244).ToArgb(),
    Color.FromArgb(111, 116, 126).ToArgb(),
    Color.FromArgb(230, 33, 23).ToArgb(),
    Color.FromArgb(92, 97, 107).ToArgb(),
    Color.FromArgb(80, 105, 220).ToArgb(),
    Color.FromArgb(99, 102, 241).ToArgb(),
  }[Math.Abs(index) % 8];

  private static BookmarkCatalog DefaultCatalog() => new()
  {
    Items =
    [
      new("default-duckduckgo", "DuckDuckGo", "https://duckduckgo.com", "D", ColorFor(0)),
      new("default-naver", "네이버", "https://www.naver.com", "N", ColorFor(1)),
      new("default-google", "Google", "https://www.google.com", "G", ColorFor(2)),
      new("default-wikipedia", "Wikipedia", "https://www.wikipedia.org", "W", ColorFor(3)),
      new("default-youtube", "YouTube", "https://www.youtube.com", "▶", ColorFor(4)),
      new("default-github", "GitHub", "https://github.com", "◉", ColorFor(5)),
      new("default-daum", "Daum", "https://www.daum.net", "D", ColorFor(6)),
      new("default-news", "뉴스", "https://news.google.com", "▤", ColorFor(7)),
    ],
  };
}

internal static class BookmarkFaviconStore
{
  private const int StoredSize = 32;
  private static string DirectoryPath => Path.Combine(BrowserStateStore.StateDirectory, "bookmark-favicons");

  internal static bool SameSite(string firstUrl, string secondUrl) =>
      CacheKey(firstUrl) is { } first && CacheKey(secondUrl) is { } second &&
      first.Equals(second, StringComparison.Ordinal);

  internal static Image LoadOrCreate(BookmarkEntry bookmark, int size = StoredSize)
  {
    var cached = Load(bookmark.Url);
    if (cached is not null)
    {
      if (cached.Width == size && cached.Height == size) return cached;
      var resized = Resize(cached, size);
      cached.Dispose();
      return resized;
    }

    var fallback = new Bitmap(size, size, PixelFormat.Format32bppArgb);
    using var graphics = Graphics.FromImage(fallback);
    graphics.SmoothingMode = SmoothingMode.AntiAlias;
    graphics.TextRenderingHint = System.Drawing.Text.TextRenderingHint.ClearTypeGridFit;
    using var background = new SolidBrush(Color.FromArgb(bookmark.ColorArgb));
    graphics.FillEllipse(background, 1, 1, size - 2, size - 2);
    using var font = new Font("Segoe UI", Math.Max(8F, size * 0.38F), FontStyle.Bold, GraphicsUnit.Pixel);
    TextRenderer.DrawText(graphics, bookmark.Glyph, font, new Rectangle(0, 0, size, size), Color.White,
        TextFormatFlags.HorizontalCenter | TextFormatFlags.VerticalCenter | TextFormatFlags.NoPadding);
    return fallback;
  }

  internal static async Task<bool> SaveAsync(string pageUrl, Stream faviconStream)
  {
    var path = FilePath(pageUrl);
    if (path is null) return false;
    var temporaryPath = path + "." + Guid.NewGuid().ToString("N") + ".new";
    try
    {
      if (faviconStream.CanSeek) faviconStream.Position = 0;
      using var payload = new MemoryStream();
      await faviconStream.CopyToAsync(payload);
      if (payload.Length is <= 0 or > 2_000_000) return false;
      payload.Position = 0;
      using var source = Image.FromStream(payload, useEmbeddedColorManagement: true, validateImageData: true);
      using var normalized = Resize(source, StoredSize);
      Directory.CreateDirectory(DirectoryPath);
      normalized.Save(temporaryPath, ImageFormat.Png);
      File.Move(temporaryPath, path, true);
      return true;
    }
    catch
    {
      return false;
    }
    finally
    {
      try { if (File.Exists(temporaryPath)) File.Delete(temporaryPath); } catch { }
    }
  }

  private static Image? Load(string url)
  {
    var path = FilePath(url);
    if (path is null || !File.Exists(path)) return null;
    try
    {
      using var source = Image.FromFile(path);
      return new Bitmap(source);
    }
    catch
    {
      return null;
    }
  }

  private static Bitmap Resize(Image source, int size)
  {
    var result = new Bitmap(size, size, PixelFormat.Format32bppArgb);
    using var graphics = Graphics.FromImage(result);
    graphics.Clear(Color.Transparent);
    graphics.CompositingQuality = CompositingQuality.HighQuality;
    graphics.InterpolationMode = InterpolationMode.HighQualityBicubic;
    graphics.PixelOffsetMode = PixelOffsetMode.HighQuality;
    var scale = Math.Min((double)size / source.Width, (double)size / source.Height);
    var width = Math.Max(1, (int)Math.Round(source.Width * scale));
    var height = Math.Max(1, (int)Math.Round(source.Height * scale));
    graphics.DrawImage(source, new Rectangle((size - width) / 2, (size - height) / 2, width, height));
    return result;
  }

  private static string? FilePath(string url)
  {
    var key = CacheKey(url);
    if (key is null) return null;
    var digest = Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(key))).ToLowerInvariant();
    return Path.Combine(DirectoryPath, digest + ".png");
  }

  private static string? CacheKey(string url)
  {
    if (!Uri.TryCreate(url, UriKind.Absolute, out var uri) ||
        (uri.Scheme != Uri.UriSchemeHttp && uri.Scheme != Uri.UriSchemeHttps)) return null;
    var host = uri.IdnHost.ToLowerInvariant();
    return host.StartsWith("www.", StringComparison.Ordinal) ? host[4..] : host;
  }
}

internal sealed class BookmarkManagerForm : Form
{
  private static readonly Color Background = Color.FromArgb(18, 18, 20);
  private static readonly Color Card = Color.FromArgb(28, 28, 30);
  private static readonly Color CardHover = Color.FromArgb(44, 44, 48);
  private static readonly Color Outline = Color.FromArgb(58, 58, 64);
  private static readonly Color TextColor = Color.FromArgb(245, 245, 247);
  private static readonly Color Muted = Color.FromArgb(152, 152, 159);
  private static readonly Color Accent = Color.FromArgb(10, 132, 255);
  private static readonly Color Danger = Color.FromArgb(255, 69, 58);

  private readonly BookmarkCatalog _working;
  private readonly FlowLayoutPanel _list = new();

  internal BookmarkCatalog Result => _working.Copy();
  protected override CreateParams CreateParams => WinFormsSurface.WithComposited(base.CreateParams);

  internal BookmarkManagerForm(BookmarkCatalog catalog)
  {
    _working = catalog.Copy();
    SuspendLayout();
    Text = "Omyaru · 북마크 관리자";
    StartPosition = FormStartPosition.CenterParent;
    MinimumSize = new Size(760, 560);
    Size = new Size(900, 680);
    BackColor = Background;
    ForeColor = TextColor;
    Font = new Font("Segoe UI", 10F);
    AutoScaleMode = AutoScaleMode.Dpi;
    DoubleBuffered = true;
    WinFormsSurface.Attach(this);

    var root = new TableLayoutPanel
    {
      Dock = DockStyle.Fill,
      BackColor = Background,
      ColumnCount = 1,
      RowCount = 3,
      Padding = new Padding(24),
    };
    root.RowStyles.Add(new RowStyle(SizeType.Absolute, 76));
    root.RowStyles.Add(new RowStyle(SizeType.Percent, 100));
    root.RowStyles.Add(new RowStyle(SizeType.Absolute, 68));

    var header = new TableLayoutPanel
    {
      Dock = DockStyle.Fill,
      BackColor = Color.Transparent,
      ColumnCount = 2,
      Margin = Padding.Empty,
    };
    header.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 100));
    header.ColumnStyles.Add(new ColumnStyle(SizeType.Absolute, 156));
    var title = new Label
    {
      Dock = DockStyle.Fill,
      Text = "북마크 관리자\n이름·주소·순서를 한곳에서 관리합니다",
      ForeColor = TextColor,
      Font = new Font("Segoe UI", 15F, FontStyle.Bold),
      TextAlign = ContentAlignment.MiddleLeft,
    };
    var add = ActionButton("＋  북마크 추가", Accent, Color.White);
    add.Dock = DockStyle.Fill;
    add.Margin = new Padding(6, 12, 0, 12);
    add.Click += (_, _) => AddBookmark();
    header.Controls.Add(title, 0, 0);
    header.Controls.Add(add, 1, 0);

    var listHost = new RoundedPanel
    {
      Dock = DockStyle.Fill,
      BackColor = Card,
      BorderColor = Outline,
      BorderWidth = 1F,
      CornerRadius = 22,
      ClipContent = true,
      Padding = new Padding(8),
    };
    _list.Dock = DockStyle.Fill;
    _list.FlowDirection = FlowDirection.TopDown;
    _list.WrapContents = false;
    _list.AutoScroll = true;
    _list.BackColor = Card;
    _list.Padding = new Padding(4);
    _list.SizeChanged += (_, _) => ResizeRows();
    listHost.Controls.Add(_list);

    var footer = new FlowLayoutPanel
    {
      Dock = DockStyle.Fill,
      BackColor = Color.Transparent,
      FlowDirection = FlowDirection.RightToLeft,
      WrapContents = false,
      Padding = new Padding(0, 12, 0, 0),
    };
    var done = ActionButton("완료", Accent, Color.White);
    done.Width = 116;
    done.Click += (_, _) => { DialogResult = DialogResult.OK; Close(); };
    var cancel = ActionButton("취소", Card, TextColor);
    cancel.Width = 116;
    cancel.BorderColor = Outline;
    cancel.BorderWidth = 1F;
    cancel.Click += (_, _) => { DialogResult = DialogResult.Cancel; Close(); };
    footer.Controls.Add(done);
    footer.Controls.Add(cancel);

    root.Controls.Add(header, 0, 0);
    root.Controls.Add(listHost, 0, 1);
    root.Controls.Add(footer, 0, 2);
    Controls.Add(root);
    RefreshRows();
    ResumeLayout(true);
  }

  private void RefreshRows()
  {
    _list.SuspendLayout();
    foreach (Control control in _list.Controls.Cast<Control>().ToArray()) control.Dispose();
    _list.Controls.Clear();
    for (var index = 0; index < _working.Items.Count; index++)
      _list.Controls.Add(CreateRow(_working.Items[index], index));
    if (_working.Items.Count == 0)
    {
      _list.Controls.Add(new Label
      {
        Height = 90,
        Text = "저장된 북마크가 없습니다.\n위의 ‘북마크 추가’를 눌러 시작하세요.",
        ForeColor = Muted,
        TextAlign = ContentAlignment.MiddleCenter,
      });
    }
    ResizeRows();
    _list.ResumeLayout(true);
  }

  private Control CreateRow(BookmarkEntry entry, int index)
  {
    var row = new RoundedPanel
    {
      Height = 68,
      BackColor = Background,
      BorderColor = Outline,
      BorderWidth = 1F,
      CornerRadius = 17,
      ClipContent = true,
      Margin = new Padding(3, 3, 3, 7),
      Padding = new Padding(12, 5, 8, 5),
      Tag = "bookmark-row",
    };
    var layout = new TableLayoutPanel
    {
      Dock = DockStyle.Fill,
      BackColor = Color.Transparent,
      ColumnCount = 6,
      RowCount = 1,
      Margin = Padding.Empty,
      Padding = Padding.Empty,
    };
    layout.ColumnStyles.Add(new ColumnStyle(SizeType.Absolute, 46));
    layout.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 100));
    for (var column = 0; column < 4; column++) layout.ColumnStyles.Add(new ColumnStyle(SizeType.Absolute, 48));
    var iconImage = BookmarkFaviconStore.LoadOrCreate(entry, 32);
    var badge = new PictureBox
    {
      Dock = DockStyle.Fill,
      Image = iconImage,
      SizeMode = PictureBoxSizeMode.CenterImage,
      BackColor = Color.Transparent,
      Margin = new Padding(4, 7, 8, 7),
    };
    badge.Disposed += (_, _) => iconImage.Dispose();
    var text = new Label
    {
      Dock = DockStyle.Fill,
      Text = entry.Name + "\n" + entry.Url,
      ForeColor = TextColor,
      Font = new Font("Segoe UI", 10F, FontStyle.Bold),
      TextAlign = ContentAlignment.MiddleLeft,
      AutoEllipsis = true,
      Margin = new Padding(7, 2, 8, 2),
    };
    layout.Controls.Add(badge, 0, 0);
    layout.Controls.Add(text, 1, 0);
    layout.Controls.Add(RowButton("↑", "위로", () => MoveBookmark(index, -1), index > 0), 2, 0);
    layout.Controls.Add(RowButton("↓", "아래로", () => MoveBookmark(index, 1), index < _working.Items.Count - 1), 3, 0);
    layout.Controls.Add(RowButton("✎", "수정", () => Edit(index), true), 4, 0);
    layout.Controls.Add(RowButton("×", "삭제", () => Delete(index), true, Danger), 5, 0);
    row.Controls.Add(layout);
    return row;
  }

  private PillButton RowButton(string text, string name, Action action, bool enabled, Color? foreground = null)
  {
    var button = new PillButton
    {
      Dock = DockStyle.Fill,
      Text = text,
      BackColor = Background,
      HoverBackColor = CardHover,
      ForeColor = foreground ?? TextColor,
      CornerRadius = 15,
      Margin = new Padding(4),
      Enabled = enabled,
      AccessibleName = name,
    };
    button.Click += (_, _) => action();
    return button;
  }

  private void ResizeRows()
  {
    var width = Math.Max(320, _list.ClientSize.Width - (_list.VerticalScroll.Visible ? SystemInformation.VerticalScrollBarWidth : 0) - 10);
    foreach (Control control in _list.Controls) control.Width = width;
  }

  private void AddBookmark()
  {
    var name = InputDialog.Show(this, "북마크 추가", "이름");
    if (string.IsNullOrWhiteSpace(name)) return;
    var address = InputDialog.Show(this, "북마크 추가", "웹 주소");
    if (!BookmarkStore.TryNormalizeUrl(address, out var url))
    {
      MessageBox.Show(this, "올바른 HTTP 또는 HTTPS 주소를 입력하세요.", "북마크 추가");
      return;
    }
    BookmarkStore.AddOrUpdate(_working, name.Trim(), url);
    RefreshRows();
  }

  private void Edit(int index)
  {
    if (index < 0 || index >= _working.Items.Count) return;
    var current = _working.Items[index];
    var name = InputDialog.Show(this, "북마크 수정", "이름", current.Name);
    if (string.IsNullOrWhiteSpace(name)) return;
    var address = InputDialog.Show(this, "북마크 수정", "웹 주소", current.Url);
    if (!BookmarkStore.TryNormalizeUrl(address, out var url))
    {
      MessageBox.Show(this, "올바른 HTTP 또는 HTTPS 주소를 입력하세요.", "북마크 수정");
      return;
    }
    var newKey = BookmarkStore.CanonicalUrlKey(url);
    if (!string.IsNullOrEmpty(newKey)) _working.DeletedUrlKeys.Remove(newKey);
    _working.Items[index] = current with { Name = name.Trim(), Url = url, Glyph = BookmarkStore.GlyphFor(name) };
    RefreshRows();
  }

  private void Delete(int index)
  {
    if (index < 0 || index >= _working.Items.Count) return;
    var current = _working.Items[index];
    if (MessageBox.Show(this, $"‘{current.Name}’ 북마크를 삭제할까요?", "북마크 삭제",
        MessageBoxButtons.YesNo, MessageBoxIcon.Warning) != DialogResult.Yes) return;
    BookmarkStore.Remove(_working, current);
    RefreshRows();
  }

  private void MoveBookmark(int index, int delta)
  {
    var target = index + delta;
    if (index < 0 || index >= _working.Items.Count || target < 0 || target >= _working.Items.Count) return;
    (_working.Items[index], _working.Items[target]) = (_working.Items[target], _working.Items[index]);
    RefreshRows();
  }

  private static PillButton ActionButton(string text, Color background, Color foreground) => new()
  {
    Height = 44,
    Text = text,
    BackColor = background,
    HoverBackColor = ControlPaint.Light(background, 0.08F),
    ForeColor = foreground,
    CornerRadius = 20,
    Font = new Font("Segoe UI", 10F, FontStyle.Bold),
    Margin = new Padding(6, 0, 0, 0),
  };
}
